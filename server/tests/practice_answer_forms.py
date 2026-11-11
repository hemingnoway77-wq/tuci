"""Isolated schema-v2 migration and plural answer-form HTTP regression; no production access."""
from contextlib import closing
from pathlib import Path
import sqlite3
import tempfile
import uuid

from integration import command, service
from practice import sense_id


OLD_COLUMNS = "vault,note,word,sense,meaning,english,chinese,created"
OLD_TABLE = """
    CREATE TABLE practice_contexts(vault TEXT NOT NULL REFERENCES vaults, note TEXT NOT NULL,
        word TEXT NOT NULL, sense TEXT NOT NULL, meaning TEXT NOT NULL, english TEXT NOT NULL,
        chinese TEXT NOT NULL, created INTEGER NOT NULL, PRIMARY KEY(vault,note,word,sense),
        FOREIGN KEY(vault,note) REFERENCES heads(vault,note))
"""


def ok(call, path, body=None, token=None):
    status, result = call(path, body, token)
    assert status == 200, (path, status, result)
    return result


def put_note(call, token, identifier, word, meanings, version=0, deleted=False):
    markdown = "" if deleted else "# " + word + "\n\n**释义**：\n\n" + "\n".join("> " + meaning for meaning in meanings)
    return ok(call, "/v1/operations", dict(operationId=str(uuid.uuid4()), noteId=identifier,
        baseVersion=version, path=word + ".md", markdown=markdown, deleted=deleted), token)


def legacy_migration(temp):
    database = temp / "legacy-817.db"
    invite = command(database, "invite")
    ids = []
    with service(database) as call:
        grant = ok(call, "/v1/redeem", dict(code=invite["code"], deviceName="Old schema fixture"))
        for index, word in enumerate(("book", "apple", "run", "cat", "dog", "home", "table")):
            identifier = "legacy-" + word
            ids.append(identifier)
            meanings = ["n. 隔离旧义项" + str(number) for number in range(121 if index == 0 else 116)]
            put_note(call, grant["token"], identifier, word, meanings)
            with closing(sqlite3.connect(database)) as db, db:
                db.executemany("INSERT INTO practice_contexts(" + OLD_COLUMNS + ") VALUES(?,?,?,?,?,?,?,?)", [
                    (grant["vaultId"], identifier, word, sense_id(meaning), meaning,
                     "The teacher wrote " + word + " on the board today.", "老师今天在黑板上写下了这个单词。", 1200 + number)
                    for number, meaning in enumerate(meanings)])
    with closing(sqlite3.connect(database)) as db, db:
        original = db.execute("SELECT " + OLD_COLUMNS + " FROM practice_contexts ORDER BY vault,note,word,sense").fetchall()
        assert len(original) == 817
        # Recreate the actual old eight-column schema after stopping the HTTP process.
        db.execute("CREATE TABLE legacy_rows AS SELECT " + OLD_COLUMNS + " FROM practice_contexts")
        db.execute("DROP TABLE practice_contexts")
        db.execute(OLD_TABLE)
        db.execute("INSERT INTO practice_contexts SELECT " + OLD_COLUMNS + " FROM legacy_rows")
        db.execute("DROP TABLE legacy_rows")
        assert db.execute("PRAGMA user_version").fetchone()[0] == 2
        assert "answer_form" not in [column[1] for column in db.execute("PRAGMA table_info(practice_contexts)")]
    plan = command(database, "practice-plan")
    assert plan["senses"] == 817 and plan["validSenses"] == 817 and plan["missingSenses"] == 0, plan
    with closing(sqlite3.connect(database)) as db, db:
        assert "answer_form" in [column[1] for column in db.execute("PRAGMA table_info(practice_contexts)")]
        assert db.execute("SELECT count(*) FROM practice_contexts WHERE answer_form IS NULL").fetchone()[0] == 817
        assert db.execute("SELECT " + OLD_COLUMNS + " FROM practice_contexts ORDER BY vault,note,word,sense").fetchall() == original
        assert db.execute("PRAGMA user_version").fetchone()[0] == 2
        assert db.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
    # Repeated constructor entry remains safe and never upgrades old null values or timestamps.
    assert command(database, "practice-plan")["validSenses"] == 817
    with service(database) as call:
        banks = ok(call, "/v1/practice/banks", {"noteIds": ids}, grant["token"])["banks"]
        assert len(banks) == 7 and sum(len(bank["contexts"]) for bank in banks) == 817
        assert all("answerForm" not in context for bank in banks for context in bank["contexts"])
    with closing(sqlite3.connect(database)) as db, db:
        assert db.execute("SELECT " + OLD_COLUMNS + " FROM practice_contexts ORDER BY vault,note,word,sense").fetchall() == original


def plural_lookup(temp):
    database = temp / "plural-banks.db"
    invite = command(database, "invite")
    other_invite = command(database, "invite")
    cases = [
        ("remain", "remains", "n. (复数 remains) 剩余物；遗骸", "She collected the remains after the long ceremony.", "她在漫长的仪式后收集了遗留物。"),
        ("supply", "supplies", "n. (复数 supplies) 日用物资；用品", "They packed medical supplies for the long journey.", "他们为长途旅行打包了医疗用品。"),
        ("specification", "specifications", "n. (复数 specifications) 规格；技术参数", "The engineer reviewed the specifications before production began.", "工程师在开始生产前审查了技术规格。"),
    ]
    with service(database) as call:
        grant = ok(call, "/v1/redeem", dict(code=invite["code"], deviceName="Plural fixture"))
        other = ok(call, "/v1/redeem", dict(code=other_invite["code"], deviceName="Other vault fixture"))
        for word, form, meaning, english, chinese in cases:
            put_note(call, grant["token"], "plural-" + word, word, [meaning])
            with closing(sqlite3.connect(database)) as db, db:
                db.execute("INSERT INTO practice_contexts(" + OLD_COLUMNS + ",answer_form) VALUES(?,?,?,?,?,?,?,?,?)",
                    (grant["vaultId"], "plural-" + word, word, sense_id(meaning), meaning, english, chinese, 123, form))
        ids = ["plural-" + case[0] for case in cases]
        before = None
        with closing(sqlite3.connect(database)) as db, db:
            before = db.execute("SELECT * FROM practice_contexts ORDER BY word").fetchall()
        banks = ok(call, "/v1/practice/banks", {"noteIds": ids}, grant["token"])["banks"]
        assert [bank["word"] for bank in banks] == [case[0] for case in cases]
        for bank, (word, form, meaning, english, chinese) in zip(banks, cases):
            assert bank["contexts"] == [dict(senseId=sense_id(meaning), english=english, chinese=chinese, answerForm=form)], bank
        assert ok(call, "/v1/practice/banks", {"noteIds": ids}, other["token"])["banks"] == []
        with closing(sqlite3.connect(database)) as db, db:
            assert db.execute("SELECT * FROM practice_contexts ORDER BY word").fetchall() == before
        # A trustworthy family is still insufficient after the source sense changes.
        put_note(call, grant["token"], "plural-supply", "supply", ["v. 提供；供应"], version=1)
        put_note(call, grant["token"], "plural-remain", "remain", [], version=1, deleted=True)
        current = ok(call, "/v1/practice/banks", {"noteIds": ids}, grant["token"])["banks"]
        assert [bank["word"] for bank in current] == ["specification"], current
    print("PASS: isolated answer-form migration preserves 817 legacy rows, plural bank lookup, stale and vault guards")


if __name__ == "__main__":
    # Build the server separately in Root's serialized slot.
    with tempfile.TemporaryDirectory(prefix="tuci-answer-form-") as directory:
        legacy_migration(Path(directory))
        plural_lookup(Path(directory))
