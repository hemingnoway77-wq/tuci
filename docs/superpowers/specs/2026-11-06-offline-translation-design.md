# Bundled offline English-to-Chinese translation

The user approved replacing quota-limited reading word translation with the Firefox/Bergamot local translation engine and bundling the English-to-Simplified-Chinese language files with every Windows release. Users install the complete application and translate without another model download, API key, subscription, or translation-server request. The current native word popup, capture action and theme remain the product UI.

The user then clarified that a single Chinese machine translation is insufficient: the popup must show a small dictionary entry, including the meanings for each part of speech. Bundle an offline ECDICT English-to-Chinese dictionary as the primary source for word details. Show phonetics and source-labelled parts of speech when present, without inventing missing dictionary data. Retain the bundled Bergamot engine for words absent from the dictionary.

## Approaches and decision

1. Bundled local engine and English-to-Chinese model: selected by the user. The current released model assets are about 37 MB compressed / 50 MB extracted, plus the engine. Windows compatibility and translation quality must be verified with the actual engine before shipping.
2. Download model at first use: rejected in favor of installation-ready offline use.
3. An online quota API or a browser translation website: does not satisfy the selected offline/native experience.

## Implementation decisions

- Keep `IWordTranslationService` and the WPF popup. Add a Core offline service for input validation, successful-result caching and error normalization.
- Add optional dictionary details to `WordTranslation` while preserving its existing three constructor parameters. A dictionary-first service looks up the selected exact word before the local machine-translation fallback. Preserve the selected word for capture. Dictionary `exchange` metadata may display a source-provided lemma; do not guess lemmas by trimming suffixes or replace an existing inflected entry's own meanings.
- Bundle fixed ECDICT CSV-derived data in 26 first-letter JSONL shards, retaining only word/phonetic/translation/exchange. Validate the bundled manifests/hashes and load only requested shards, caching at most three. Retain original source/commit/license, actual translation labels and ordering.
- Display phonetic text and a compact scrollable list of labelled meanings in the current themed popup. A fallback machine translation has no fabricated part-of-speech labels. Selecting a new word clears all previous detail fields; late requests cannot repopulate them.
- Run the official Firefox Bergamot WebAssembly engine with bundled JS and local English-to-Chinese files in an isolated, noninteractive WebView2 host. Use the project's existing WebView2 dependency; do not require Firefox or Node on the user's machine.
- No automatic online fallback and no runtime model fetching. All engine/model resources are local; block nonlocal navigation and resources. Disable browser UI and permissions.
- Lazy initialization on the first translation; serialize native runtime operations; preserve cancellation and ignore results for dismissed/changed selections. Dispose the engine with its reading page. Recover from a failed browser process or timeout instead of leaving a permanently hung translate button.
- Pin upstream versions/URLs, sizes and SHA-256 hashes. Include upstream licenses, notices and source locations. Commit the model files so rebuilding/releasing does not download models.
- Copy the entire offline directory into build and publish outputs. Preserve that directory in the user-facing release archive. A lone executable is not a complete offline release.

## Acceptance

- The real bundled model returns Chinese for an English word and sentence with no nonlocal resource allowed and no translation account configured.
- The existing reading popup calls the offline implementation by default; capture remains independent.
- Real `book`/`run` entries show multiple source-labelled meanings, phonetics when present and no WASM startup for dictionary hits. Verify unknown-word fallback and one real inflected entry; test missing dictionary assets and clearing/stale details.
- Cancellation, retry, cache bounds, malformed/missing local files, runtime failure and disposal do not strand requests or replace a later word's popup text.
- Unit tests and the existing WPF smoke suite pass. Add a real local-model smoke gate to release verification and test the published layout.
- Document measured cold/warm timings, final asset size and the existing WebView2 Runtime prerequisite. Do not promise general sentence translation controls, dictionary-style multi-sense entries or parity with a particular cloud translator.
