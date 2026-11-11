"""Build a self-contained Safari home-screen app with an atomic offline cache."""
import argparse
from base64 import b64encode
import hashlib
import json
import re
from pathlib import Path
from shutil import copyfile

SOURCE = Path(__file__).resolve().parent
FILES = (
    "index.html", "update.html", "update-recovery.js", "manifest.json", "app.css", "app.js", "storage.js",
    "account.js", "login-transition.js", "tab-indicator.js", "practice.js", "reading-state.js", "legacy-roaming.js", "dictionary.js", "offline-translation.js",
    "dictionary/dictionary.manifest.json", "dictionary/LICENSE.MIT.txt", "library.js", "library.css", "managed-links.js", "learning.js", "learning-data.js", "learning.css", "reader-words.js", "word-lookup.js",
    "icons/notebook.svg", "icons/login-paper.svg", "icons/brand.png", "icons/apple-touch.png", "icons/icon-192.png", "icons/icon-512.png",
    "fonts/serif.css", "fonts/OFL.txt", "fonts/NOTICE.txt",
)

TRANSLATION_SOURCE = SOURCE.parent / "src/VocabCollector.Desktop/Assets/OfflineTranslation"
TRANSLATION_FILES = {
    "translation-worker.js": "text/javascript",
    "bergamot-translator.js": "text/javascript",
    "bergamot-translator.wasm": "application/wasm",
    "en-zh/model.enzh.intgemm.alphas.bin": "application/octet-stream",
    "en-zh/lex.50.50.enzh.s2t.bin": "application/octet-stream",
    "en-zh/srcvocab.enzh.spm": "application/octet-stream",
    "en-zh/trgvocab.enzh.spm": "application/octet-stream",
}


def package_translation(target, assets):
    runtime = json.loads((TRANSLATION_SOURCE / "runtime.manifest.json").read_text(encoding="utf-8"))
    model = json.loads((TRANSLATION_SOURCE / "models.manifest.json").read_text(encoding="utf-8"))
    declared = {file["path"]: file for file in runtime["files"] + model["files"] + [model["license"]]}
    files = {path: {"bytes": declared[path]["bytes"], "sha256": declared[path]["sha256"], "type": content_type}
             for path, content_type in TRANSLATION_FILES.items()}
    version = hashlib.sha256(json.dumps(files, sort_keys=True).encode()).hexdigest()[:16]
    directory = f"offline-translation/{version}"
    notices = {"LICENSE-MPL-2.0.txt": "LICENSE-MPL-2.0.txt", "LICENSE.MPL-2.0.txt": "LICENSE.MPL-2.0.txt",
               "LICENSES-THIRD-PARTY.txt": "LICENSES-THIRD-PARTY.txt", "RUNTIME-SOURCES.txt": "RUNTIME-SOURCES.md"}
    for destination, path in {**{path: path for path in files}, **notices}.items():
        data = (TRANSLATION_SOURCE / path).read_bytes()
        asset = declared[path]
        if len(data) != asset["bytes"] or hashlib.sha256(data).hexdigest() != asset["sha256"]:
            raise ValueError("Translation asset is missing or corrupt: " + path)
        output = target / directory / destination
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(data)
    for name in ("runtime.manifest.json", "models.manifest.json"):
        copyfile(TRANSLATION_SOURCE / name, target / directory / name)
    manifest_path = "offline-translation/manifest.json"
    manifest = json.dumps({"formatVersion": 1, "version": version, "files": files}, sort_keys=True).encode()
    (target / manifest_path).write_bytes(manifest)
    assets[manifest_path] = "sha256-" + b64encode(hashlib.sha256(manifest).digest()).decode("ascii")
    return version, {f"{directory}/{path}": info for path, info in files.items()}


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
    # Ship the entire official SC web-font distribution, while only the small
    # core UI subset participates in installation. Other unicode-range shards
    # are verified and cached by the service worker on first use.
    fonts = json.loads((SOURCE / "fonts/manifest.json").read_text(encoding="utf-8"))
    font_assets = {}
    if not isinstance(fonts.get("files"), list) or not 1 <= len(fonts["files"]) <= 128:
        raise ValueError("Invalid font manifest")
    for font in fonts["files"]:
        name = font.get("path", "")
        if not re.fullmatch(r"noto-serif-sc-v\d+-\d{3}-[a-f0-9]{12}\.woff2", name):
            raise ValueError("Invalid font shard path")
        path = "fonts/" + name
        if path in font_assets:
            raise ValueError("Duplicate font shard")
        data = (SOURCE / path).read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if (data[:4] != b"wOF2" or len(data) != font["bytes"] or
                digest != font["sha256"] or digest[:12] not in name):
            raise ValueError("Font shard is missing or corrupt: " + name)
        copyfile(SOURCE / path, target / path)
        font_assets[path] = {"bytes": len(data), "sha256": digest}
        if font.get("precache") is True:
            assets[path] = "sha256-" + b64encode(bytes.fromhex(digest)).decode("ascii")
    copyfile(SOURCE / "fonts/manifest.json", target / "fonts/manifest.json")
    # Public, immutable dictionary shards are deployed but never downloaded by shell installation.
    dictionary = json.loads((SOURCE / "dictionary/dictionary.manifest.json").read_text(encoding="utf-8"))
    declared = {file["path"]: file for file in dictionary["files"]}
    for letter in "abcdefghijklmnopqrstuvwxyz":
        name = letter + ".jsonl"
        asset = declared[name]
        src = SOURCE / "dictionary" / name
        data = src.read_bytes()
        if len(data) != asset["bytes"] or hashlib.sha256(data).hexdigest() != asset["sha256"]:
            raise ValueError("Dictionary shard is missing or corrupt: " + name)
        copyfile(src, target / "dictionary" / name)
    translation_version, translation_files = package_translation(target, assets)
    template = (SOURCE / "sw.template.js").read_text()
    manifest = json.dumps(assets, sort_keys=True)
    font_manifest = json.dumps(font_assets, sort_keys=True)
    font_release = hashlib.sha256(font_manifest.encode()).hexdigest()[:16]
    release = hashlib.sha256((template + manifest + font_manifest).encode()).hexdigest()[:16]
    # The recovery entry must reach the network even under an older worker.
    offline_assets = {name: digest for name, digest in assets.items() if name not in ("update.html", "update-recovery.js")}
    worker = (template.replace('"__VERSION__"', json.dumps(release))
              .replace('"__ASSETS__"', json.dumps(offline_assets, sort_keys=True))
              .replace('"__FONT_VERSION__"', json.dumps(font_release))
              .replace('"__FONT_ASSETS__"', font_manifest)
              .replace('"__TRANSLATION_VERSION__"', json.dumps(translation_version))
              .replace('"__TRANSLATION_ASSETS__"', json.dumps(translation_files, sort_keys=True)))
    (target / "sw.js").write_text(worker)
    print(release)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", nargs="?", type=Path, default=SOURCE.parent / ".artifacts" / "web")
    build(parser.parse_args().directory)
