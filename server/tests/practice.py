"""Isolated real HTTP coverage for read-only practice banks; never uses production data."""
from pathlib import Path
from contextlib import closing
import hashlib
import json
import sqlite3
import tempfile
import urllib.error
import urllib.request
import uuid
from integration import service, command, HTTP


def sense_id(meaning):
    return hashlib.sha256(meaning.encode("utf-8")).hexdigest()


def run(temp):
    database = temp / "practice.db"
    with service(database) as original:
        url = next(cell.cell_contents for cell in original.__closure__
                   if isinstance(cell.cell_contents, str) and cell.cell_contents.startswith("http://"))

        def call(path, body=None, token=None, cookie=None, vault=None):
            headers = {"Content-Type": "application/json"}
            if token:
                headers["Authorization"] = "Bearer " + token
            if cookie:
                headers["Cookie"] = cookie
            if vault is not None:
                headers["X-Tuci-Vault"] = vault
            request = urllib.request.Request(url + path, headers=headers,
                                             data=None if body is None else json.dumps(body).encode())
            try:
                with HTTP.open(request, timeout=20) as response:
                    raw = response.read()
                    return response.status, json.loads(raw), response.headers, len(raw)
            except urllib.error.HTTPError as error:
                raw = error.read()
                return error.code, raw.decode(), error.headers, len(raw)

        def ok(path, body=None, token=None, **kwargs):
            status, value, headers, size = call(path, body, token, **kwargs)
            assert status == 200, (path, status, value)
            return value, headers, size

        def account(name):
            invite = command(database, "invite-web")["code"]
            data = dict(username=name, password="isolated fixture password", deviceName="Practice fixture", inviteCode=invite)
            grant = ok("/v1/account/register", data)[0]
            return grant, data

        alice, credentials = account("practice-alice")
        bob, _ = account("practice-bob")
        _, headers, _ = ok("/v1/account/login", dict(credentials, browser=True))
        cookie = headers["Set-Cookie"].split(";", 1)[0]

        def note(note_id, word, meanings, token=alice["token"], version=0, path=None, deleted=False):
            body = "" if deleted else "# " + word + "\n\n**释义**：\n\n" + "\n".join("> " + meaning for meaning in meanings)
            return ok("/v1/operations", dict(operationId=str(uuid.uuid4()), noteId=note_id,
                      baseVersion=version, path=path or "名词/" + word + ".md", markdown=body, deleted=deleted), token)[0]

        meanings = ["n. 书", "v. 预订"]
        note("shared-id", "book", meanings)
        note("shared-id", "apple", ["n. 苹果"], bob["token"])
        note("partial", "run", ["v. 跑", "v. 经营"])
        note("root-note", "act", ["n. 行为"], path="词根/act.md")
        note("normal-prose", "article", ["n. 文章"], path="杂记/我的文章.md")
        note("no-meaning", "empty", [])
        note("deleted", "banana", ["n. 香蕉"])

        def seed(vault, note_id, word, meaning, english, chinese, identifier=None):
            with closing(sqlite3.connect(database)) as db, db:
                db.execute("INSERT OR REPLACE INTO practice_contexts(vault,note,word,sense,meaning,english,chinese,created) VALUES(?,?,?,?,?,?,?,?)",
                           (vault, note_id, word, identifier or sense_id(meaning), meaning, english, chinese, 123))

        seed(alice["vaultId"], "shared-id", "book", meanings[0], "She read a book on the train.", "她在火车上读了一本书。")
        seed(alice["vaultId"], "shared-id", "book", meanings[1], "We book a table for dinner tonight.", "我们预订一张今晚用餐的餐桌。")
        seed(bob["vaultId"], "shared-id", "apple", "n. 苹果", "An apple fell from the tree yesterday.", "昨天一个苹果从树上掉了下来。")
        seed(alice["vaultId"], "partial", "run", "v. 跑", "They run around the park every morning.", "他们每天早晨绕着公园跑步。")
        seed(alice["vaultId"], "root-note", "act", "n. 行为", "The act surprised everyone in the room.", "这个行为使屋里的人都很惊讶。")
        seed(alice["vaultId"], "normal-prose", "article", "n. 文章", "This article describes a simple learning method.", "这篇文章介绍了一种简单的学习方法。")
        seed(alice["vaultId"], "deleted", "banana", "n. 香蕉", "The banana fell from the kitchen table.", "那根香蕉从厨房的桌子上掉了下来。")
        note("deleted", "banana", [], version=1, deleted=True)
        # Existing note/state rows are the invariant, not just their row counts.
        tables = ("changes", "heads", "receipts", "state_changes", "state_heads", "state_receipts", "vaults")
        def unchanged_rows():
            with closing(sqlite3.connect(database)) as db, db:
                return {table: db.execute("SELECT * FROM " + table + " ORDER BY rowid").fetchall() for table in tables}
        before = unchanged_rows()

        ids = ["shared-id", "partial", "root-note", "normal-prose", "no-meaning", "deleted", "unknown"]
        result, _, size = ok("/v1/practice/banks", {"noteIds": ids}, alice["token"])
        assert size <= 2 * 1024 * 1024
        assert len(result["banks"]) == 1, result
        bank = result["banks"][0]
        assert bank["noteId"] == "shared-id" and bank["word"] == "book"
        assert {item["senseId"] for item in bank["contexts"]} == set(map(sense_id, meanings))
        assert all(item["english"] and item["chinese"] for item in bank["contexts"])
        other = ok("/v1/practice/banks", {"noteIds": ids}, bob["token"])[0]
        assert [item["word"] for item in other["banks"]] == ["apple"]
        duplicate = ok("/v1/practice/banks", {"noteIds": ["shared-id", "shared-id"]}, alice["token"])[0]
        assert len(duplicate["banks"]) == 1
        assert call("/v1/practice/banks", {"noteIds": ["shared-id"]})[0] == 401
        assert call("/v1/practice/banks", {"noteIds": ["shared-id"]}, cookie=cookie)[0] == 400
        assert call("/v1/practice/banks/", {"noteIds": ["shared-id"]}, cookie=cookie)[0] == 400
        assert call("/v1/practice/banks", {"noteIds": ["shared-id"]}, cookie=cookie, vault=bob["vaultId"])[0] == 403
        assert call("/v1/practice/banks", {"noteIds": ["shared-id"]}, alice["token"], vault=bob["vaultId"])[0] == 403
        assert ok("/v1/practice/banks", {"noteIds": ["shared-id"]}, cookie=cookie, vault=alice["vaultId"])[0] == duplicate
        for invalid in [None, [None], ["../secret"], ["a" * 129], ["same"] * 33]:
            assert call("/v1/practice/banks", {"noteIds": invalid}, alice["token"])[0] == 400
        assert ok("/v1/practice/banks", {"noteIds": []}, alice["token"])[0] == {"banks": []}
        assert unchanged_rows() == before

        # An incomplete or damaged sense never becomes a partial public bank.
        seed(alice["vaultId"], "partial", "run", "v. 经营", "They manage a company in the city.", "他们在城里经营一家公司。")
        assert ok("/v1/practice/banks", {"noteIds": ["partial"]}, alice["token"])[0] == {"banks": []}
        seed(alice["vaultId"], "partial", "run", "v. 经营", "They run a small company in town.", "他们在城里经营一家小公司。")
        assert len(ok("/v1/practice/banks", {"noteIds": ["partial"]}, alice["token"])[0]["banks"]) == 1
        note("partial", "run", ["v. 跑", "v. 改变后的意思"], version=1)
        assert ok("/v1/practice/banks", {"noteIds": ["partial"]}, alice["token"])[0] == {"banks": []}
        note("shared-id", "book", [], version=1, deleted=True)
        assert ok("/v1/practice/banks", {"noteIds": ["shared-id"]}, alice["token"])[0] == {"banks": []}
        # Force the serialized wire budget, including escaped Chinese text.
        large_ids = []
        for word in ("large", "small", "bright", "clear"):
            identifier = "budget-" + word
            large_ids.append(identifier)
            senses = ["n. 指定的不同义项" + str(index) for index in range(128)]
            note(identifier, word, senses)
            with closing(sqlite3.connect(database)) as db, db:
                db.executemany("INSERT INTO practice_contexts(vault,note,word,sense,meaning,english,chinese,created) VALUES(?,?,?,?,?,?,?,?)", [
                    (alice["vaultId"], identifier, word, sense_id(meaning), meaning,
                     "The teacher explained " + word + " to the class today.", "这是完整的中文译句。" * 180, 123)
                    for meaning in senses])
        before_budget = unchanged_rows()
        budget, _, size = ok("/v1/practice/banks", {"noteIds": large_ids}, alice["token"])
        assert size <= 2 * 1024 * 1024
        assert 0 < len(budget["banks"]) < len(large_ids), "wire budget must omit whole banks instead of truncating senses"
        assert all(len(bank["contexts"]) == 128 for bank in budget["banks"])
        assert unchanged_rows() == before_budget
        with closing(sqlite3.connect(database)) as db, db:
            assert db.execute("PRAGMA user_version").fetchone()[0] == 2
            assert db.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    print("PASS: isolated HTTP practice banks, complete/current validation, two-vault isolation, cookie guard, limits and unchanged note/state rows")


if __name__ == "__main__":
    # Build separately in the shared serialized verification slot.
    with tempfile.TemporaryDirectory(prefix="tuci-practice-http-") as directory:
        run(Path(directory))
