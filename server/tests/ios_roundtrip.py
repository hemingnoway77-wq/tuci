"""Run the Swift client against a real, temporary ASP.NET/SQLite instance; no external account."""
from pathlib import Path
import json
import os
import socket
import subprocess
import tempfile
import time

from integration import DOTNET, DLL, HTTP, ROOT, command

DRIVER = ROOT.parent / "tests/VocabCollector.Sync.RoundTrip"
DRIVER_DLL = DRIVER / "bin/Release/net10.0/VocabCollector.Sync.RoundTrip.dll"


def test(tmp):
    database = tmp / "roundtrip.db"
    invite = command(database, "invite")
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        url = "http://127.0.0.1:" + str(sock.getsockname()[1])
    env = dict(os.environ, TUCI_DB=str(database))
    with tempfile.TemporaryFile() as log:
        process = subprocess.Popen([DOTNET, str(DLL), "--urls", url], env=env,
                                   stdout=log, stderr=subprocess.STDOUT)
        try:
            for _ in range(200):
                if process.poll() is not None:
                    raise RuntimeError("local server failed to start")
                try:
                    with HTTP.open(url+"/health", timeout=1) as response:
                        if response.status == 200:
                            break
                except OSError:
                    time.sleep(.05)
            else:
                raise TimeoutError("local server did not become ready")

            sandbox = (tmp / "windows-engine").resolve()
            sandbox.mkdir()
            example = ROOT.parent / "docs/examples/introduced.md"
            client_env = dict(os.environ, TUCI_TEST_SERVER=url, TUCI_TEST_NOTE=str(example))
            def windows(mode, env):
                subprocess.run([DOTNET, str(DRIVER_DLL), mode, str(sandbox)], env=env, check=True)
            windows("seed", dict(client_env, TUCI_TEST_CODE=invite["code"]))
            code = (sandbox / "pair-code.txt").read_text().strip()
            body = example.read_bytes().decode("utf-8-sig")
            fixture = tmp / "fixture.json"
            fixture.write_text(json.dumps({"server":url,"code":code,"vaultId":invite["vaultId"],
                                            "remoteMarkdown":body}), encoding="utf-8")
            subprocess.run([str(ROOT.parent / "ios/scripts/test-core.sh"),
                            "--filter", "ServerRoundTripTests"],
                           env=dict(os.environ, TUCI_IOS_ROUNDTRIP=str(fixture)), check=True)
            windows("verify", client_env)
            # Confirm the filtered test actually ran, even if a different SwiftPM reports no matching suite.
            labels={device["name"] for device in command(database,"devices",invite["vaultId"])}
            if labels != {"Windows fixture", "Swift round-trip", "Swift paired"}:
                raise RuntimeError("Swift client did not complete device exchange")
        finally:
            process.terminate()
            process.wait(timeout=10)


if __name__ == "__main__":
    subprocess.run([DOTNET, "build", str(ROOT / "Tuci.Sync.csproj"), "-c", "Release", "--no-restore"], check=True)
    subprocess.run([DOTNET, "build", str(DRIVER / "VocabCollector.Sync.RoundTrip.csproj"),
                    "-c", "Release"], check=True)
    with tempfile.TemporaryDirectory() as directory:
        test(Path(directory))
    print("PASS: Windows sync engine -> server -> Swift Notebook -> server -> Windows sync engine, with conflict and retry recovery")
