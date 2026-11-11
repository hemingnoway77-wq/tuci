"""Operator CLI against isolated SQLite and loopback model HTTP; requires an already built server."""
from contextlib import closing, contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import hashlib
import json
import os
import sqlite3
import subprocess
import tempfile
import threading
import uuid

from integration import command, service, DOTNET, DLL

KEY = "sk-backfill-fixture-only-secret"
MEANINGS = ["v. 介绍；使相识", "v. 引进；首次采用"]
PRIVATE = "PRIVATE-NOTE-NOT-SENT-TO-MODEL"


def markdown(meanings=MEANINGS):
    return "---\nlemma: introduce\n---\n# introduced\n\n**释义**：\n\n" + "\n".join(
        "> " + meaning for meaning in meanings) + "\n\n## 私人笔记\n" + PRIVATE


def sense_id(meaning):
    return hashlib.sha256(meaning.encode()).hexdigest()


def operate(call, token, note, body, version=0, deleted=False, word="introduced"):
    status, result = call("/v1/operations", dict(operationId=str(uuid.uuid4()), noteId=note,
        baseVersion=version, path=word + ".md", markdown=body, deleted=deleted), token)
    assert status == 200, (status, result)
    return result


@contextmanager
def library(temp, name, vaults=("vault-a",)):
    database = temp / (name + ".db")
    # Let the real Database constructor apply its schema, then create fixture identities.
    assert command(database, "practice-plan")["wordNotes"] == 0
    with closing(sqlite3.connect(database)) as con, con:
        con.executemany("INSERT INTO vaults(id) VALUES(?)", [(vault,) for vault in vaults])
    tokens = {}
    with service(database) as call:
        for vault in vaults:
            invite = command(database, "invite", vault)
            status, grant = call("/v1/redeem", {"code": invite["code"], "deviceName": "Backfill fixture"})
            assert status == 200
            tokens[vault] = grant["token"]
        yield database, call, tokens


def seed_context(database, vault, note, meaning=MEANINGS[0], english="A visitor introduced herself to the whole team."):
    with closing(sqlite3.connect(database)) as con, con:
        con.execute("INSERT OR REPLACE INTO practice_contexts(vault,note,word,sense,meaning,english,chinese,created) VALUES(?,?,?,?,?,?,?,?)",
            (vault, note, "introduced", sense_id(meaning), meaning, english, "一位来访者向整个团队介绍了自己。", 1))


class Model:
    def __init__(self):
        self.requests = []
        self.mode = "valid"
        self.before_response = None

    def __enter__(self):
        model = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_):
                pass

            def do_POST(self):
                body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                assert self.path == "/v1/chat/completions"
                assert self.headers["Authorization"] == "Bearer " + KEY
                assert body["model"] == "fixture-model" and body["stream"] is False
                assert KEY not in json.dumps(body) and PRIVATE not in json.dumps(body)
                data = json.loads(body["messages"][1]["content"])
                model.requests.append(data)
                callback, model.before_response = model.before_response, None
                if callback:
                    callback(data)
                if model.mode == "failure":
                    self.send_response(429)
                    self.end_headers()
                    self.wfile.write(("provider private body " + KEY).encode())
                    return
                contexts = [dict(sense_id=sense["sense_id"],
                    english="They introduced the new plan to everyone yesterday.",
                    chinese="他们昨天向所有人介绍了这项新计划。") for sense in data["senses"]]
                if data["word"] == "supply":
                    contexts = [dict(sense_id=sense["sense_id"],
                        english="The workers placed the supplies inside a secure storeroom.",
                        chinese="工人们把这些物资放进一间安全的储藏室。", answer_form="supplies")
                        if "supplies" in sense["meaning"] else dict(sense_id=sense["sense_id"],
                            english="The company will supply fresh water to every village.",
                            chinese="这家公司将向每个村庄供应干净的水。") for sense in data["senses"]]
                if model.mode == "incomplete":
                    contexts = contexts[:-1]
                payload = {"choices": [{"finish_reason": "stop", "message": {
                    "content": json.dumps({"contexts": contexts}, ensure_ascii=False)}}]}
                response = json.dumps(payload, ensure_ascii=False).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(response)))
                self.end_headers()
                self.wfile.write(response)

        self.http = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.http.serve_forever, daemon=True)
        self.thread.start()
        return self

    def __exit__(self, *_):
        self.http.shutdown()
        self.http.server_close()
        self.thread.join(timeout=5)

    def config(self, temp, name):
        path = temp / (name + "-api.json")
        path.write_text(json.dumps(dict(baseUrl="http://127.0.0.1:" + str(self.http.server_port) + "/v1",
            apiKey=KEY, model="fixture-model")), encoding="utf-8")
        path.chmod(0o600)
        return path


def cli_args(config, state, limit=None):
    args = ["practice-backfill", "--config", str(config), "--state", str(state)]
    if limit is not None:
        args += ["--limit", str(limit)]
    return args


def run_cli(database, config, state, limit=None, succeeds=True):
    process = subprocess.run([DOTNET, str(DLL), *cli_args(config, state, limit)],
        env=dict(os.environ, TUCI_DB=str(database)), capture_output=True, text=True, timeout=30)
    assert KEY not in process.stdout + process.stderr
    assert PRIVATE not in process.stdout + process.stderr
    if not succeeds:
        assert process.returncode != 0
        return process
    assert process.returncode == 0, (process.returncode, process.stdout, process.stderr)
    return json.loads(process.stdout)


def original_rows(database):
    with closing(sqlite3.connect(database)) as con, con:
        return (con.execute("SELECT * FROM changes ORDER BY vault,seq").fetchall(),
                con.execute("SELECT * FROM state_changes ORDER BY vault,seq").fetchall())


def dedup_resume_and_preservation(temp):
    with library(temp, "dedup", ("vault-a", "vault-b", "vault-empty")) as (database, call, tokens), Model() as model:
        operate(call, tokens["vault-a"], "note-a", markdown())
        operate(call, tokens["vault-b"], "note-b", markdown())
        before = original_rows(database)
        plan = command(database, "practice-plan")
        assert (plan["vaults"], plan["wordNotes"], plan["senses"]) == (3, 2, 4)
        assert plan["missingSenses"] == 4 and plan["estimatedRequests"] == 1
        assert not model.requests
        config, state = model.config(temp, "dedup"), temp / "dedup-job.json"
        report = run_cli(database, config, state, limit=1)
        assert report["calls"] == 1 and report["addedSenses"] == 4 and report["memoReuses"] == 1
        assert report["missingSenses"] == 0 and report["validSenses"] == 4
        assert len(model.requests) == 1
        assert KEY not in state.read_text() and PRIVATE not in state.read_text()
        if os.name != "nt":
            assert state.stat().st_mode & 0o777 == 0o600
        assert original_rows(database) == before
        # Simulate generation checkpointed before a database commit, including a
        # partial competing commit: resume must reuse each already-paid sense.
        # An old successful checkpoint has only the original three context fields.
        legacy = json.loads(state.read_text())
        for contexts in legacy["Generated"].values():
            for context in contexts:
                context.pop("AnswerForm", None)
        state.write_text(json.dumps(legacy), encoding="utf-8")
        state.chmod(0o600)
        with closing(sqlite3.connect(database)) as con, con:
            con.execute("DELETE FROM practice_contexts")
        seed_context(database, "vault-a", "note-a")
        saved = "A visitor introduced herself to the whole team."
        resumed = run_cli(database, config, state, limit=1)
        assert resumed["calls"] == 0 and resumed["addedSenses"] == 3 and resumed["missingSenses"] == 0
        assert len(model.requests) == 1
        with closing(sqlite3.connect(database)) as con, con:
            assert con.execute("SELECT english FROM practice_contexts WHERE vault=? AND note=? AND sense=?",
                ("vault-a", "note-a", sense_id(MEANINGS[0]))).fetchone()[0] == saved
            assert con.execute("SELECT count(*) FROM practice_contexts").fetchone()[0] == 4
            assert con.execute("PRAGMA integrity_check").fetchone()[0] == "ok"
        assert original_rows(database) == before


def declared_plural_survives_checkpoint_and_resumes_without_regeneration(temp):
    with library(temp, "plural") as (database, call, tokens), Model() as model:
        meanings = ["n. (复数 supplies) 日用物资；用品", "v. 提供；供应"]
        body = "---\nlemma: supply\n---\n# supply\n\n**释义**：\n\n" + "\n".join(
            "> " + meaning for meaning in meanings)
        operate(call, tokens["vault-a"], "note-supply", body, word="supply")
        before = original_rows(database)
        config, state = model.config(temp, "plural"), temp / "plural-job.json"
        first = run_cli(database, config, state)
        assert first["calls"] == 1 and first["addedSenses"] == 2 and first["missingSenses"] == 0
        contexts = [context for bank in json.loads(state.read_text())["Generated"].values() for context in bank]
        assert sum(context.get("AnswerForm") == "supplies" for context in contexts) == 1
        assert sum("AnswerForm" not in context for context in contexts) == 1
        with closing(sqlite3.connect(database)) as con, con:
            rows = con.execute("SELECT sense,answer_form,english FROM practice_contexts ORDER BY sense").fetchall()
            noun = next(row for row in rows if row[0] == sense_id(meanings[0]))
            assert noun[1] == "supplies" and " supplies " in noun[2]
            assert next(row for row in rows if row[0] == sense_id(meanings[1]))[1] is None
            con.execute("DELETE FROM practice_contexts")
        resumed = run_cli(database, config, state)
        assert resumed["calls"] == 0 and resumed["addedSenses"] == 2 and resumed["missingSenses"] == 0
        assert len(model.requests) == 1
        with closing(sqlite3.connect(database)) as con, con:
            assert con.execute("SELECT sense,answer_form,english FROM practice_contexts ORDER BY sense").fetchall() == rows
        assert original_rows(database) == before


def partial_failure_and_limit(temp):
    with library(temp, "partial") as (database, call, tokens), Model() as model:
        operate(call, tokens["vault-a"], "note-a", markdown())
        seed_context(database, "vault-a", "note-a")
        config, state = model.config(temp, "partial"), temp / "partial-job.json"
        model.mode = "failure"
        failed = run_cli(database, config, state, limit=1)
        assert failed["calls"] == 1 and failed["failedWords"] == 1 and failed["missingSenses"] == 1
        assert failed["failureCodes"] == {"generation": 1}
        assert len(model.requests) == 1 and len(model.requests[0]["senses"]) == 1
        assert not state.exists()
        model.mode = "valid"
        completed = run_cli(database, config, state, limit=1)
        assert completed["calls"] == 1 and completed["addedSenses"] == 1 and completed["missingSenses"] == 0
        assert len(model.requests) == 2


def stale_edit_delete_and_competing_context(temp):
    for action in ["edit", "delete", "competing-context"]:
        with library(temp, "race-" + action) as (database, call, tokens), Model() as model:
            operate(call, tokens["vault-a"], "note-a", markdown())
            edited = ["v. 正式介绍；使双方认识", MEANINGS[1]]
            def change(_):
                if action == "competing-context":
                    seed_context(database, "vault-a", "note-a")
                else:
                    operate(call, tokens["vault-a"], "note-a", "" if action == "delete" else markdown(edited),
                        version=1, deleted=action == "delete")
            model.before_response = change
            config, state = model.config(temp, action), temp / (action + "-job.json")
            result = run_cli(database, config, state)
            assert len(model.requests) == 1 and state.exists()
            with closing(sqlite3.connect(database)) as con, con:
                count = con.execute("SELECT count(*) FROM practice_contexts").fetchone()[0]
                if action == "competing-context":
                    assert result["staleWords"] == 0 and result["addedSenses"] == 1 and count == 2
                    assert con.execute("SELECT english FROM practice_contexts WHERE sense=?", (sense_id(MEANINGS[0]),)).fetchone()[0] == "A visitor introduced herself to the whole team."
                else:
                    assert result["staleWords"] == 1 and result["addedSenses"] == 0 and count == 0
            if action == "edit":
                resumed = run_cli(database, config, state)
                assert resumed["calls"] == 1 and resumed["missingSenses"] == 0
                assert len(model.requests) == 2 and len(model.requests[1]["senses"]) == 1
                assert model.requests[1]["senses"][0]["sense_id"] == sense_id(edited[0])
            elif action == "delete":
                assert result["wordNotes"] == 0 and result["missingSenses"] == 0
                assert command(database, "practice-plan")["wordNotes"] == 0


def invalid_response_and_state_are_safe(temp):
    with library(temp, "invalid") as (database, call, tokens), Model() as model:
        operate(call, tokens["vault-a"], "note-a", markdown())
        config, state = model.config(temp, "invalid"), temp / "invalid-job.json"
        model.mode = "incomplete"
        report = run_cli(database, config, state)
        assert report["failedWords"] == 1 and report["missingSenses"] == 2 and not state.exists()
        state.write_text("{broken", encoding="utf-8"); state.chmod(0o600)
        run_cli(database, config, state, succeeds=False)
        assert state.read_text() == "{broken" and len(model.requests) == 1
        run_cli(database, config, config, succeeds=False)
        assert KEY in config.read_text()
        if os.name != "nt":
            config.chmod(0o644)
            run_cli(database, config, temp / "new-job.json", succeeds=False)
            assert len(model.requests) == 1


def concurrent_job_uses_one_checkpoint_owner(temp):
    with library(temp, "locked") as (database, call, tokens), Model() as model:
        operate(call, tokens["vault-a"], "note-a", markdown())
        entered, release = threading.Event(), threading.Event()
        def wait(_):
            entered.set()
            assert release.wait(20)
        model.before_response = wait
        config, state = model.config(temp, "locked"), temp / "locked-job.json"
        process = subprocess.Popen([DOTNET, str(DLL), *cli_args(config, state)],
            env=dict(os.environ, TUCI_DB=str(database)), stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            assert entered.wait(10)
            blocked = run_cli(database, config, state, succeeds=False)
            assert "practice_job_busy" in blocked.stderr and len(model.requests) == 1
            release.set()
            stdout, stderr = process.communicate(timeout=20)
            assert process.returncode == 0 and KEY not in stdout + stderr
            assert json.loads(stdout)["missingSenses"] == 0
        finally:
            release.set()
            if process.poll() is None:
                process.terminate(); process.wait(timeout=10)


if __name__ == "__main__":
    if not DLL.exists():
        raise SystemExit("Build server/Tuci.Sync.csproj in Release before running this isolated test.")
    with tempfile.TemporaryDirectory(prefix="tuci-practice-backfill-") as tmp:
        temp = Path(tmp)
        dedup_resume_and_preservation(temp)
        declared_plural_survives_checkpoint_and_resumes_without_regeneration(temp)
        partial_failure_and_limit(temp)
        stale_edit_delete_and_competing_context(temp)
        invalid_response_and_state_are_safe(temp)
        concurrent_job_uses_one_checkpoint_owner(temp)
    print("PASS: operator planning, limits, per-sense resume/dedup, preserved partial/racing banks, stale edits/deletes, safe errors and checkpoint ownership")
