# Bundled offline dictionary and English-to-Chinese translation

## Result

The Windows reading popup now shows a local dictionary mini entry with phonetic
text and source-labelled Chinese meanings for each available part of speech.
`book` displays noun and verb meanings; `run` preserves its separate noun,
intransitive, transitive and adjective entries. Source-provided lemma metadata
can be shown without changing the selected word used for capture. Selecting a
different word clears all preceding details and rejects late responses.

ECDICT is queried first. Dictionary hits do not initialize a browser or WASM.
An unknown word falls back to the bundled Firefox/Bergamot model locally, with
no invented dictionary fields or online translation request. The existing
capture action still uses the separate user-configured AI only when saving a
new card, as before.

## Bundled data and provenance

- ECDICT full base CSV at `39cfb9672c0906b63569e0453c2e6abfb9ab182a`:
  770,611 original rows, 400,181 normalized word entries compatible with
  `WordInput`, 26 JSONL shards / 27,939,101 bytes. The 201 source entries without
  Chinese text are retained in the data and treated as dictionary misses.
  No independent `lemma.en.txt` data is shipped. MIT notice is included.
- Mozilla en-to-Simplified-Chinese Release/base-memory model: four original
  inference files / 49,913,927 bytes, including both vocabularies. Main model
  SHA-256 matches Mozilla's registered `uncompressedHash`.
- Official Bergamot WASM 3.0 / 4,960,506 bytes, the matching Firefox loader and
  local worker source. Loader/configuration references are pinned to Firefox
  commit `d9bd6be9a67efb1c2667ef91a89856f35eb7cf5e`.
- Model/runtime and dictionary manifests record sizes, SHA-256 and original
  sources. Full MPL-2.0/MIT and runtime component notices accompany the files.
  Git attributes preserve exact hashed bytes across checkouts.
- Both offline directories together contain 47 files / 83,018,328 bytes in this
  release. No first-use language download, Firefox or Node installation is
  required. The model fallback uses the existing system WebView2 Runtime;
  missing-runtime errors explain that prerequisite. Dictionary-only lookup,
  native Markdown and roaming do not require WebView2.

## Verification

`./scripts/publish.ps1 -OutputDirectory 'dist/兔词-离线词典版'` completed with exit 0:

- Locked restore and model/dictionary SHA-256 checks.
- **929/929 unit tests**; Release build **0 warnings / 0 errors**.
- Full existing WPF smoke: native reading, pointer-route word actions,
  in-page capture, library, roaming, themes, real local-HTTP WebView2 articles,
  settings, save/error/cancellation flows.
- Real offline-model smoke, including actual `hello` and sentence translations,
  successful-result cache, apostrophes, stale IDs, cancellation/retry,
  `Page.crash` renderer recovery, disposal, and missing assets.
- A reachable loopback HTTP probe is first confirmed using HttpClient, then an
  engine-document fetch must reject without incrementing its server count.
  This avoids treating DNS failure as proof of blocking.
- Real dictionary data: `book`, `run`, `introduced`; popup screenshot shows real
  `book` phonetic and noun/verb meanings. A dictionary hit leaves the runtime
  host empty. A verified dictionary miss starts the actual local model and
  returns no fabricated parts of speech.
- Published-layout smoke is repeated using the actual release model directory
  and its sibling dictionary directory, with fresh isolated browser profiles.

Evidence from the published-layout run:
`.artifacts/offline-translation-smoke/8cc0ad62cd0845de930196a143b1a79e/`.
The real cold model took 1,202 ms; the test sentence took 35 ms warm; the native
dictionary popup completed in 232 ms on this machine. These are observations,
not minimum hardware or speed guarantees.

The earlier full test run exposed unrelated fixture interference: concurrent
test windows could steal initial keyboard focus, and WPF's default application
icon decoder could belong to a different STA. `BlankAreaFocusTests` now runs in
a nonparallel collection, loads before focusing, verifies its setup, and uses
its own frozen icon. Production blank-click behavior was not changed. The
subsequent full run passed.

Two independent read-only reviews covered runtime lifecycle/network restrictions
and then dictionary word shapes, POS provenance, cache, stale UI results,
licenses and publish layout. The identified release-JSON depth issue was fixed
using `ConvertTo-Json -Depth 8`; no material findings remain.

## Delivered artifact

- Folder: `C:\Private Files\Projects\TUCI\dist\兔词-离线词典版`
- ZIP: `兔词-离线词典版-win-x64.zip`, **110,416,679 bytes**, 53 entries.
- EXE: **143,584,968 bytes**, SHA-256
  `5C0AAE5340965571C9FAC80009CF6404E21BE27F6CEEE8C779C909FB6E4B805A`.
- ZIP SHA-256:
  `E9B798D2CC440EADA252951153699BFD88F1A88A040174591DB9FF28D01D6F67`.
- Extracted folder: **227,424,200 bytes**. The executable and all 47 offline
  files were checked against release metadata after copying. Every required
  archive entry is present. Unzip the whole folder and keep its `Assets`
  directory alongside `兔词.exe`.

User settings, existing notes and previous releases were preserved. Test and
translation-model browser instances were isolated and disposed.
