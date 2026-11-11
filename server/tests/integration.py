"""Run the actual HTTP service against an isolated SQLite file (Python 3.9+, .NET 10)."""
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing, contextmanager
from pathlib import Path
import hashlib
import json
import os
import shutil
import socket
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
DOTNET = os.environ.get("DOTNET") or shutil.which("dotnet") or str(Path.home() / ".dotnet/dotnet")
DLL = ROOT / "bin/Release/net10.0/Tuci.Sync.dll"
HTTP = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def command(database, *args):
    env = dict(os.environ, TUCI_DB=str(database))
    return json.loads(subprocess.check_output([DOTNET, str(DLL), *args], env=env))


@contextmanager
def service(database, trust_local_proxy=False):
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        url = "http://127.0.0.1:" + str(sock.getsockname()[1])
    with tempfile.TemporaryFile() as log:
        process = subprocess.Popen([DOTNET, str(DLL), "--urls", url], env=dict(os.environ, TUCI_DB=str(database),
                                            TUCI_TRUST_LOOPBACK_PROXY='1' if trust_local_proxy else '0'),
                                   stdout=log, stderr=subprocess.STDOUT)
        def call(path, body=None, token=None, forwarded=None):
            headers = {"Content-Type": "application/json"}
            if forwarded:
                headers["X-Forwarded-For"] = forwarded
            if token:
                headers["Authorization"] = "Bearer " + token
            req = urllib.request.Request(url + path, data=None if body is None else json.dumps(body).encode(), headers=headers)
            try:
                with HTTP.open(req, timeout=15) as response:
                    return response.status, json.loads(response.read())
            except urllib.error.HTTPError as e:
                return e.code, e.read().decode()
        try:
            for _ in range(150):
                if process.poll() is not None:
                    log.seek(0)
                    raise RuntimeError(log.read().decode())
                try:
                    if call("/health")[0] == 200:
                        break
                except OSError:
                    time.sleep(.05)
            else:
                raise TimeoutError("server did not start")
            yield call
        finally:
            process.terminate()
            process.wait(timeout=10)


def run(temp):
    database = temp / "source.db"
    admin = lambda *args: command(database, *args)
    a = admin("invite")
    b = admin("invite")
    with service(database) as http:
        def ok(path, body=None, token=None):
            status, data = http(path, body, token)
            assert status == 200, (path, status, data)
            return data
        def redeem(code, name):
            return ok("/v1/redeem", {"code": code, "deviceName": name})
        def op(note, version, path, markdown, deleted=False, regions=None):
            value = dict(operationId=str(uuid.uuid4()), noteId=note, baseVersion=version, path=path,
                         markdown=markdown, deleted=deleted)
            if regions is not None:
                value["regions"] = regions
            return value
        assert http("/v1/changes")[0] == 401
        # A one-time code cannot be redeemed by two racing devices.
        with ThreadPoolExecutor(2) as pool:
            results = list(pool.map(lambda _: http("/v1/redeem", {"code": a["code"], "deviceName":"Mac"}), range(2)))
        assert sorted(x[0] for x in results) == [200, 401]
        first = next(x[1] for x in results if x[0] == 200)
        assert first["vaultId"] == a["vaultId"]
        device = first["token"]
        other = redeem(b["code"], "Bob")["token"]
        code = ok("/v1/pair", {}, device)["code"]
        phone = redeem(code, "iPhone")["token"]
        assert http("/v1/redeem", {"code":code,"deviceName":"Second"})[0] == 401
        assert http("/v1/changes?after=1", token=phone)[0] == 409

        post = lambda item, token=phone: ok("/v1/operations", item, token)
        pull = lambda after=0, token=device: ok("/v1/changes?after="+str(after), token=token)
        for path in ("../secret.md", "/etc/secret.md", ".vocabcollector/notes.md", "a//b.md",
                     "NUL.txt.md", "x\\y.md", "dev/file.txt", "a:stream.md", "con .md"):
            assert http("/v1/operations", op("bad",0,path,"x"),device)[0] == 400, path
        assert http("/v1/operations", op("bad",0,"valid.md","x"*(1024*1024+1)),device)[0] == 400
        regions = {"examples": hashlib.sha256(b"protected").hexdigest()}
        save = op("word",0,"名词/example.md","content",regions=regions)
        with ThreadPoolExecutor(2) as pool:
            receipts = list(pool.map(post,[save,save]))
        assert receipts[0] == receipts[1] and receipts[0]["version"] == 1
        assert len(pull()["changes"]) == 1
        assert http("/v1/operations",dict(save,markdown="altered"),phone)[0] == 409
        assert pull()["changes"][0]["regions"] == regions
        assert pull(token=other)["changes"] == []
        assert post(op("word",0,"名词/example.md","Bob only"),other)["version"] == 1

        # First link of an existing folder merges identical contents. Filename case differs.
        merged = post(op("legacy-id",0,"名词/Example.MD","content"))
        assert merged["noteId"] == "word" and not merged["conflict"]
        old = pull()["cursor"]
        post(op("word",1,"名词/example.md","edited on PC",regions=regions),device)
        conflicted = post(op("word",1,"名词/example.md","edited offline",regions=regions))
        recovered = {r["noteId"]:r for r in pull(old)["changes"]}
        assert conflicted["conflict"] and recovered["word"]["markdown"] == "edited on PC"
        assert recovered[conflicted["noteId"]]["markdown"] == "edited offline" and recovered[conflicted["noteId"]]["regions"] == regions
        assert conflicted["path"] != "名词/example.md"
        old = pull()["cursor"]
        stale_delete = post(op("word",1,"名词/example.md","",True))
        assert stale_delete["conflict"] and pull(old)["changes"][-1]["markdown"] == "edited on PC"
        # A stale editor after deletion retains its text in a new conflict note.
        post(op("word",2,"名词/example.md","",True))
        result = post(op("word",2,"名词/example.md","after deletion"))
        assert result["conflict"] and result["noteId"] != "word"
        post(op("prose",0,"杂记/我的见闻.md","entry"))
        assert post(op("prose",1,"阅读/见闻.md","entry"))["version"] == 2
        hierarchy = post(op("nested",0,"阅读/见闻.md/other.md","child"))
        assert hierarchy["conflict"] and hierarchy["path"].startswith("同步冲突/")
        # Two writers concurrently editing the same revision must both survive.
        with ThreadPoolExecutor(2) as pool:
            both=list(pool.map(post,[op("prose",2,"阅读/见闻.md",v) for v in ["v1","v2"]]))
        assert sum(x["conflict"] for x in both)==1
        # More than one actual page and no cursor gaps.
        for i in range(70):
            post(op("page"+str(i),0,f"分页/{i}.md",str(i)))
        cursor = 0
        history = []
        while True:
            page=pull(cursor)
            history.extend(page["changes"])
            cursor=page["cursor"]
            if not page["hasMore"]:
                break
        assert [r["seq"] for r in history] == list(range(1,cursor+1))
        assert pull(cursor)["changes"] == []
        assert [r["markdown"] for r in pull(token=other)["changes"]] == ["Bob only"]
        outstanding=ok("/v1/pair", {}, phone)["code"]
        lost=next(d for d in admin("devices",a["vaultId"]) if d["name"]=="iPhone")
        admin("revoke",lost["deviceId"])
        assert http("/v1/pair",{},phone)[0] == 401
        assert http("/v1/redeem",{"code":outstanding,"deviceName":"Lost"})[0] == 401
        assert redeem(admin("invite",a["vaultId"])["code"],"Recovered")["vaultId"] == a["vaultId"]
        for _ in range(31):
            last=http("/v1/redeem",{"code":"wrong","deviceName":"x"})[0]
        assert last == 429
        assert http("/v1/redeem", {"code":"bad","deviceName":"x"}, forwarded="192.0.2.1")[0] == 429
        backup=temp/"backup.db"
        admin("backup",str(backup))
    with closing(sqlite3.connect(backup)) as con:
        assert con.execute("pragma integrity_check").fetchone()[0]=="ok"
        assert con.execute("select tokenhash from devices where name='Mac'").fetchone()[0] == hashlib.sha256(device.encode()).hexdigest().upper()
    with service(backup, trust_local_proxy=True) as restored:
        reply=restored("/v1/changes?after="+str(cursor),token=device)
        assert reply[0]==200 and reply[1]["cursor"]==cursor
        reply=restored("/v1/operations",save,device)
        assert reply==(200,receipts[0])
        for _ in range(31):
            blocked = restored("/v1/redeem", {"code":"bad","deviceName":"proxy-test"}, forwarded="192.0.2.8")[0]
        assert blocked == 429
        assert restored("/v1/redeem", {"code":"bad","deviceName":"other-peer"}, forwarded="192.0.2.9")[0] == 401
    print("PASS: auth and revocation, user isolation, retries, paths, conflict copies, paging and live backup/restore")


if __name__ == "__main__":
    subprocess.run([DOTNET,"build",str(ROOT / "Tuci.Sync.csproj"),"-c","Release","--no-restore"], check=True)
    with tempfile.TemporaryDirectory() as tmp:
        run(Path(tmp))
