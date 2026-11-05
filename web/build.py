"""Build a self-contained Safari home-screen app with an atomic offline cache."""
import argparse
from base64 import b64encode
import hashlib
import json
from pathlib import Path
from shutil import copyfile

SOURCE = Path(__file__).resolve().parent
FILES = (
    "index.html", "update.html", "manifest.json", "app.css", "app.js", "storage.js",
    "library.js", "library.css", "managed-links.js", "learning.js", "learning-data.js", "learning.css",
    "icons/notebook.svg", "icons/brand.png", "icons/apple-touch.png", "icons/icon-192.png", "icons/icon-512.png",
)


def build(target):
    target = target.resolve()
    if target == SOURCE or target in SOURCE.parents:
        raise ValueError("Choose a separate build directory")
    target.mkdir(parents=True, exist_ok=True)
    assets = {}
    for name in FILES:
        dst = target / name
        dst.parent.mkdir(parents=True, exist_ok=True)
        copyfile(SOURCE / name, dst)
        digest = hashlib.sha256(dst.read_bytes()).digest()
        assets[name] = "sha256-" + b64encode(digest).decode("ascii")
    template = (SOURCE / "sw.template.js").read_text()
    manifest = json.dumps(assets, sort_keys=True)
    release = hashlib.sha256((template + manifest).encode()).hexdigest()[:16]
    # The recovery entry must reach the network even under an older worker.
    offline_assets = {name: digest for name, digest in assets.items() if name != "update.html"}
    worker = template.replace('"__VERSION__"', json.dumps(release)).replace('"__ASSETS__"', json.dumps(offline_assets, sort_keys=True))
    (target / "sw.js").write_text(worker)
    print(release)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", nargs="?", type=Path, default=SOURCE.parent / ".artifacts" / "web")
    build(parser.parse_args().directory)
