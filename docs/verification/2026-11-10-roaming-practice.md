# Roaming answer practice — local Windows trial, 2026-11-10

## Scope and use

The user approved spelling and Chinese self-assessment, interleaved by word and
sense. The semantic coordinate feature is deferred. This delivery is a local
Windows trial; no commit, Git push, server deployment or online update publish
is part of this task. Web and server source remain unchanged.

Open the trial EXE, log into the existing account, select 漫游 and enable
练习作答. Ordinary frosted browsing remains the default. The new card shares the
existing page and has one input, a 30-second active-view countdown and explicit
submit/next actions. Hidden/deactivated views pause the countdown.

- Spelling: each saved definition line is a sense, with same-line synonyms kept
  together. The current meaning and first-letter/character-count cue are shown.
  An original sentence is used only for a single, reliably associated sense and
  an actual whole target occurrence; multiple unbound senses use the meaning cue
  without invented sentences or arbitrary example-index mapping.
- Exact spelling uses the normal remembered result. A trusted saved/dictionary
  lemma also passes but does not increase remembered or skipped counts; the
  saved form and available form note are revealed. No suffix chopping or
  approximate-spelling acceptance is used. Case/outer whitespace/apostrophes
  normalize consistently with existing word input.
- Chinese: the word and available context appear, the learner submits an answer,
  and only then sees the current reference meaning. 符合/不符合 are manual
  assessment-and-next actions. No Chinese string matching or model request.
- Timeout/想不起来 reveal the reference and wait for a manual next/assessment.
  Submission does not advance or mutate the ordinary roaming grade. New practice
  cards close any previous revealed full-note preview and reset their viewport;
  result layout brings the saved form/reference into view at minimum size.
- Question selection and outcomes live in account/library-local practice.json.
  Per-sense/per-kind counters rotate material, with weaker ties prioritized.
  Durable completion receipts restore the original submitted answer/result if
  roaming advancement fails after the practice write. Duplicate actions do not
  write another completion. Corrupt or externally changed state is preserved.
- Existing roaming weighting/statistics remain. Practice advancement avoids the
  just-finished word if another word is available. Neutral lemma completion adds
  no bool completed sync event and has a bounded local 4,096-exposure replay
  guard. Standard browse NextAsync behavior and existing sync schemas remain.

The practice question/outcome history currently retains complete local attempt
receipts. Bounding/compacting older completed receipts is deferred for this short
local trial; the per-sense aggregate and pending receipt must be retained in any
future compaction. This feature does not persist practice detail to the server.

## Verification

- Core bank/deck red: **21 failed, 7 passed**, then **28/28 passed**.
- Practice weighting red: stub failures, then new **22/22** plus existing
  roaming/session/statistics/sync tests for **54/54** combined.
- WPF entry red: default browse passed, entering practice failed with the stub;
  completed Core/page combination **38/38 passed** (28 Core plus 10 page cases).
- Real UI automation caught and fixed a disabled-first-toggle bug and a minimum
  window result clipping bug. Pointer/keyboard submission, exact/lemma feedback,
  both senses and both kinds, retained drafts, self-assessment, skip, preview
  concealment, themes, minimum size, no-meaning fallback and no-lemma material
  are checked using isolated notes/settings and zero model requests.
- Independent review identified a revealed-preview answer leak into the next
  question and dictionary-disposal race on close/account switch. Both were fixed;
  the former is covered by the real single-word preview→next regression. Pending
  loading invalidation was also made explicit.
- A complete verification run passed **1,180/1,180**, zero skips/failures, build
  with zero warnings/errors, full WPF, practice, pure reading, startup, 160 rapid
  module switches, signed-update mock checks and real offline model/dictionary.

All fixtures use temporary directories or .artifacts. They preserve original
Markdown bytes and do not read real account credentials or invoke production
startup/login. The real published offline assets are verified again during
packaging.

Final repeated publisher verification passed after all review fixes:
**1,180/1,180**, zero warnings/errors, full WPF/practice/reading/startup/
navigation/update/offline checks. The final strict practice run is
`.artifacts/practice-smoke/dcbaf744e5374b6cb4a48b884b81c5cb` (four sense/kind
combinations, zero model requests, zero binding errors), including the
revealed-preview→next concealment regression. Published real-model verification:
`.artifacts/offline-translation-smoke/448e41fdad904cac83f9fe2318150d31`.

## Local package

Copied to `C:\Private Files\Projects\TUCI\dist\兔词-作答复习试用版`:

- EXE **143,958,216 bytes**, SHA-256
  `D6070CDFAE45726452B19ABDC7FD9DEC6EF62DEE90312E6A6060BAEC705D712C`.
- ZIP `兔词-作答复习试用版-win-x64.zip`, **144,276,414 bytes**, 54 file entries.
- ZIP SHA-256
  `8A6B35499CDBD6F70E3B0D4B0DFD6536291B3FD94FAB64B793182C9FBB3D3DD2`.
- Full directory **302,993,233 bytes**; all 54 copied files match the source
  release, all 47 offline assets match release metadata, and every source file
  is present in the ZIP. The existing signed-update helper is included.

Earlier releases and user data were preserved. Repository HEAD remains
`88ac7184451be81c0d664934756d6a58a5621d1d`; feature source and documentation
remain uncommitted locally, with no staged files and no upload/deployment.
