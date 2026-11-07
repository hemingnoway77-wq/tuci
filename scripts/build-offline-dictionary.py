#!/usr/bin/env python3
"""Rebuild TUCI's bundled dictionary from one pinned, verified official CSV."""

import argparse
import collections
import csv
import hashlib
import json
import os
from pathlib import Path
import re
import string
import tempfile
import time
import urllib.request


COMMIT = "39cfb9672c0906b63569e0453c2e6abfb9ab182a"
SOURCE_ROOT = f"https://raw.githubusercontent.com/skywind3000/ECDICT/{COMMIT}"
CSV_BYTES = 66_009_548
CSV_SHA256 = "d1ecbdf9ee2b920afb6f430ca6b6bb9f559588cf0dff86e04bd255f9390843cc"
LICENSE_BYTES = 1063
LICENSE_SHA256 = "f8552dd246f61a4e064569eae6194a01c6b3d63b03bf27c6ca863593c549ed0f"
WORD = re.compile(r"[a-z]+(?:[-'][a-z]+)*", re.ASCII)
FIELDS = ["word", "phonetic", "definition", "translation", "pos", "collins", "oxford", "tag", "bnc", "frq", "exchange", "detail", "audio"]
REPOSITORY = Path(__file__).resolve().parents[1]


def plain_path(path):
    path = Path(os.path.abspath(path))
    for candidate in (path, *path.parents):
        if candidate.is_symlink() or candidate.is_junction():
            raise ValueError(f"Linked paths are not supported: {candidate}")
    return path


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify(path, size, checksum):
    path = plain_path(path)
    if not path.is_file() or path.stat().st_size != size or sha256(path) != checksum:
        raise ValueError(f"Pinned source size or SHA256 mismatch: {path}")
    return path


def source_file(cache, name, size, checksum, supplied=None):
    if supplied:
        return verify(Path(supplied), size, checksum)
    target = cache / name
    if target.exists():
        return verify(target, size, checksum)
    cache.mkdir(parents=True, exist_ok=True)
    for attempt in range(3):
        descriptor, temporary_name = tempfile.mkstemp(prefix="ecdict-download-", dir=cache)
        temporary = Path(temporary_name)
        try:
            request = urllib.request.Request(f"{SOURCE_ROOT}/{name}", headers={"User-Agent": "TUCI-dictionary-assets/1"})
            with os.fdopen(descriptor, "wb") as output, urllib.request.urlopen(request, timeout=180) as response:
                while block := response.read(1024 * 1024):
                    output.write(block)
            verify(temporary, size, checksum)
            os.replace(temporary, target)
            return target
        except Exception:
            if attempt == 2:
                raise
            time.sleep(attempt + 1)
        finally:
            if temporary.exists():
                temporary.unlink()


def read_entries(source):
    counts = collections.Counter(sourceRows=0, filteredRows=0, eligibleRows=0, duplicateRows=0,
                                 duplicateKeys=0, malformedRows=0, emptyTranslationRows=0)
    entries = {}
    duplicate_keys = set()
    with source.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(stream)
        if reader.fieldnames != FIELDS:
            raise ValueError("Unexpected ECDICT CSV columns")
        for row_number, row in enumerate(reader, 2):
            counts["sourceRows"] += 1
            if None in row or any(value is None for value in row.values()):
                raise ValueError(f"Malformed ECDICT CSV row: {row_number}")
            raw = row["word"].strip()
            word = raw.replace("‘", "'").replace("’", "'").lower()
            if len(word) > 64 or not WORD.fullmatch(word):
                counts["filteredRows"] += 1
                continue
            counts["eligibleRows"] += 1
            translation = row["translation"].replace("\\n", "\n")
            if not translation.strip():
                counts["emptyTranslationRows"] += 1
            candidate = [raw, row["phonetic"], translation, row["exchange"]]
            if word not in entries:
                entries[word] = [candidate]
            else:
                counts["duplicateRows"] += 1
                duplicate_keys.add(word)
                entries[word].append(candidate)
    counts["duplicateKeys"] = len(duplicate_keys)
    counts["entryCount"] = len(entries)
    result = {}
    for word, candidates in entries.items():
        # Prefer the original lowercase headword, retaining any other complete
        # source meanings, pronunciations and exchange components if duplicates occur.
        candidates.sort(key=lambda value: value[0] != word)
        phonetics = list(dict.fromkeys(value[1] for value in candidates if value[1]))
        translations = list(dict.fromkeys(value[2] for value in candidates if value[2]))
        exchanges = list(dict.fromkeys(part for value in candidates for part in value[3].split("/") if part))
        result[word] = [word, " / ".join(phonetics), "\n".join(translations), "/".join(exchanges)]
    counts["entriesWithoutTranslation"] = sum(not value[2].strip() for value in result.values())
    return result, dict(counts)


def build(source, license_source, output):
    entries, counts = read_entries(source)
    output = plain_path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    staging = Path(tempfile.mkdtemp(prefix=".offline-dictionary-", dir=output.parent))
    if staging.resolve().parent != output.parent.resolve() or not staging.name.startswith(".offline-dictionary-"):
        raise ValueError("Unexpected dictionary staging directory")
    try:
        paths = {letter: staging / f"{letter}.jsonl" for letter in string.ascii_lowercase}
        handles = {letter: path.open("wb") for letter, path in paths.items()}
        shard_counts = collections.Counter()
        try:
            for word, entry in sorted(entries.items()):
                handles[word[0]].write((json.dumps(entry, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
                shard_counts[word[0]] += 1
        finally:
            for handle in handles.values():
                handle.close()
        files = []
        for letter, path in paths.items():
            files.append({"role": "entries", "path": path.name, "bytes": path.stat().st_size,
                          "sha256": sha256(path), "entryCount": shard_counts[letter], "sourceUrl": f"{SOURCE_ROOT}/ecdict.csv"})
        license_path = staging / "LICENSE.MIT.txt"
        license_path.write_bytes(license_source.read_bytes())
        files.append({"role": "license", "path": license_path.name, "bytes": license_path.stat().st_size,
                      "sha256": sha256(license_path), "sourceUrl": f"{SOURCE_ROOT}/LICENSE"})
        manifest = {
            "formatVersion": 1,
            "name": "ECDICT English-Chinese word details",
            "entryFormat": ["word", "phonetic", "translation", "exchange"],
            "encoding": "UTF-8 JSON Lines, lowercase key, ordinal order, no BOM",
            "source": {"repository": "https://github.com/skywind3000/ECDICT", "commit": COMMIT,
                       "csvUrl": f"{SOURCE_ROOT}/ecdict.csv", "csvBytes": CSV_BYTES, "csvSha256": CSV_SHA256},
            "license": {"spdxId": "MIT", "path": "LICENSE.MIT.txt", "sourceUrl": f"{SOURCE_ROOT}/LICENSE"},
            "generation": {"script": "scripts/build-offline-dictionary.py", "version": 1,
                           "normalization": "WordInput-compatible lowercase ASCII, internal hyphen/apostrophe, at most 64 characters",
                           "translation": "Source literal backslash-n is decoded to LF; original meanings are retained",
                           "duplicates": "Prefer the original lowercase headword; retain distinct complete translations, phonetics and exchange components"},
            "statistics": counts,
            "totals": {"entryBytes": sum(file["bytes"] for file in files if file["role"] == "entries")},
            "files": files,
        }
        (staging / "dictionary.manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        (staging / ".gitattributes").write_text("*.jsonl -text\nLICENSE.MIT.txt -text\ndictionary.manifest.json text eol=lf\nREADME.md text eol=lf\n", encoding="utf-8", newline="\n")
        (staging / "README.md").write_text(
            "# Bundled ECDICT word details\n\n"
            f"Source: [skywind3000/ECDICT at {COMMIT}]({SOURCE_ROOT}/ecdict.csv).\n"
            "The full basic CSV is filtered only by TUCI's single-English-word input rules.\n"
            "All eligible entries are retained, including entries whose Chinese translation is empty.\n"
            "The small upstream `ecdict.mini.csv` is a sample, not the bundled dictionary.\n\n"
            "Each UTF-8 JSONL line is `[word, phonetic, translation, exchange]`.\n"
            "Keys are lowercase and ordinally sorted in `a.jsonl` through `z.jsonl`.\n"
            "Original Chinese meanings, POS abbreviations and nonempty source pronunciations are retained.\n"
            "Source literal `\\n` separators become LF characters inside the JSON string.\n"
            "Inflected forms retain their own entries; exchange metadata is not a spelling correction.\n"
            "No machine translation was used to generate dictionary definitions or POS labels.\n\n"
            "The upstream MIT license and copyright notice are included as `LICENSE.MIT.txt`.\n"
            "The separately licensed `lemma.en.txt` file is not included.\n"
            "All sources, filtering counts, sizes and SHA256 values are in `dictionary.manifest.json`.\n\n"
            "Rebuild from the fixed official sources with `python scripts/build-offline-dictionary.py`.\n"
            "A verified local CSV can be supplied with `--source-csv`; use `--license-source` for its pinned license.\n"
            "Python is needed only to rebuild these development assets, never by installed TUCI.\n"
            "Verify the bundled assets without network or Python using `./scripts/verify-offline-dictionary.ps1`.\n",
            encoding="utf-8", newline="\n")
        # Sources and all generated files are complete before any destination changes.
        # Per-file replacement is atomic and the manifest is published last.
        output.mkdir(parents=True, exist_ok=True)
        generated = sorted(staging.iterdir(), key=lambda path: path.name == "dictionary.manifest.json")
        for path in generated:
            target = plain_path(output / path.name)
            if target.exists() and target.read_bytes() == path.read_bytes():
                path.unlink()
            else:
                os.replace(path, target)
        return manifest
    finally:
        # Never recursively remove a computed directory. Only exact files in the
        # freshly created, checked staging directory are eligible for cleanup.
        if staging.resolve().parent != output.parent.resolve():
            raise ValueError("Unexpected staging cleanup path")
        for path in staging.iterdir():
            if path.is_file() and not path.is_symlink():
                path.unlink()
        staging.rmdir()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-csv", type=Path)
    parser.add_argument("--license-source", type=Path)
    parser.add_argument("--cache-directory", type=Path, default=REPOSITORY / ".artifacts/ecdict-sources")
    parser.add_argument("--output-directory", type=Path, default=REPOSITORY / "src/VocabCollector.Desktop/Assets/OfflineDictionary")
    args = parser.parse_args()
    cache = plain_path(args.cache_directory)
    source = source_file(cache, "ecdict.csv", CSV_BYTES, CSV_SHA256, args.source_csv)
    license_source = source_file(cache, "LICENSE", LICENSE_BYTES, LICENSE_SHA256, args.license_source)
    manifest = build(source, license_source, args.output_directory)
    print(json.dumps({"output": str(args.output_directory.resolve()), "statistics": manifest["statistics"], "totals": manifest["totals"]}, ensure_ascii=True))


if __name__ == "__main__":
    main()
