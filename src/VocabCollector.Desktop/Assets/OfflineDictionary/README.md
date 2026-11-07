# Bundled ECDICT word details

Source: [skywind3000/ECDICT at 39cfb9672c0906b63569e0453c2e6abfb9ab182a](https://raw.githubusercontent.com/skywind3000/ECDICT/39cfb9672c0906b63569e0453c2e6abfb9ab182a/ecdict.csv).
The full basic CSV is filtered only by TUCI's single-English-word input rules.
All eligible entries are retained, including entries whose Chinese translation is empty.
The small upstream `ecdict.mini.csv` is a sample, not the bundled dictionary.

Each UTF-8 JSONL line is `[word, phonetic, translation, exchange]`.
Keys are lowercase and ordinally sorted in `a.jsonl` through `z.jsonl`.
Original Chinese meanings, POS abbreviations and nonempty source pronunciations are retained.
Source literal `\n` separators become LF characters inside the JSON string.
Inflected forms retain their own entries; exchange metadata is not a spelling correction.
No machine translation was used to generate dictionary definitions or POS labels.

The upstream MIT license and copyright notice are included as `LICENSE.MIT.txt`.
The separately licensed `lemma.en.txt` file is not included.
All sources, filtering counts, sizes and SHA256 values are in `dictionary.manifest.json`.

Rebuild from the fixed official sources with `python scripts/build-offline-dictionary.py`.
A verified local CSV can be supplied with `--source-csv`; use `--license-source` for its pinned license.
Python is needed only to rebuild these development assets, never by installed TUCI.
Verify the bundled assets without network or Python using `./scripts/verify-offline-dictionary.ps1`.
