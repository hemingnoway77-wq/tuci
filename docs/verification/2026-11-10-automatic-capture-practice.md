# Automatic contextual practice after capture — local Windows trial

The user requested automatic preparation of contextual spelling material when
capturing new words. This extends the local Windows contextual-practice trial.
No commit, source upload, server/web change, deployment or online update publish.

## Behavior

The successful Markdown commit emits a saved-only notification from the shared
MainViewModel. Main capture and the daily-reading capture sidebar therefore use
the same path. The immutable submitted API/vault settings, corrected word and
actual saved note drive preparation; later edits to the input do not change it.

Production startup injects the existing configured generator into MainWindow.
Saved captures enqueue background preparation of all senses in one extra model
request. The successful save returns without waiting, allows another capture,
and retains its preview/selection. Duplicate, rejected, failed and cancelled
captures do not start preparation. Startup and old-word browsing never batch
generate missing banks automatically.

The account's local cache is reused by auto and manual preparation. A shared
per-path preparation gate rechecks the bank after waiting, so a completed
concurrent preparation is reused. A separate per-path cache gate preserves
different words written by multiple instances. Invalid banks are not cached;
automatic failures are not retried silently. Existing manual preparation is
available if needed. Cached spelling and Chinese recall do not call the model.

MainWindow captures the account generation, fixed local state root and account
lifetime token before queuing. Closing, locking for login or switching accounts
cancels old work, and even a provider ignoring cancellation cannot write a late
result. Completion notices cannot replace a later capture/message. Preparation
does not create an exposure, update review counts or start a question timer.

The practice structure and semantic-quality limitations remain those documented
in `2026-11-10-contextual-practice-correction.md`: HTTP tests use fakes, never
real user API credentials, and structural validation cannot prove AI semantics.

## Focused verification

- Shared preparation/cache red: **8 failed / 8 passed**; green **16/16**.
  Includes same-word single preparation, cache hit without API configuration,
  active/waiting cancellation, failure/invalid-data preservation, and 12 distinct
  words saved concurrently through different cache instances.
- Saved notification red: **2 failed / 45 passed**. The failure confirms the
  saved hook was absent; negative statuses did not accidentally trigger it.
  Implemented notification and all MainViewModel cases then passed **47/47**,
  with no warnings.
- Independent review corrected automatic note paths to be relative to WordRoot,
  matching LibraryCatalog even when the output directory is named 词根.
- Combined preparation/cache, saved-event, main-window automatic preparation,
  contextual-practice page and reading-sidebar tests passed **101/101**, with no
  warnings. Automatic capture has 11 WPF cases including words/词根 output
  directories, the real reading-sidebar button, full sense banks, nonblocking
  save, corrected input, failure/duplicates, FIFO captures and close followed by
  a cancellation-ignoring provider's late result. No real network/model call.
- The first combined run stopped on 11 automatic-fixture initialization failures:
  a shared Application icon decoder was owned by another STA. Each test window
  now owns a frozen fixture icon, matching the established practice-page tests;
  product icon loading and all feature assertions remain unchanged.
- Final independent review verified the shared preparation cancellation checks,
  per-path cache write gate, production injection, fixed account scope and
  stale-message guards, and reported no remaining material finding.

## Complete verification

The complete publisher passed **1,262/1,262** unit tests, with zero skips,
failures, build warnings or errors. Native WPF, contextual practice, pure reading,
prepared startup, 160 rapid module switches, signed-update mocks and the real
offline translation/dictionary checks all passed. Published offline assets were
verified again after self-contained publication. No paid production request was
used to test generated sentences.

- Practice: `.artifacts/practice-smoke/1ba8474bc9c1485ca2832a26e08e8dbb`.
- Startup: `.artifacts/prepared-startup/4818b1282fdb451c8a1fadc380d49f27`.
- Navigation: `.artifacts/navigation-switch/e893b95df06240c89da0d240b9f260af`.
- Update checks: `.artifacts/updates/80418de6b32544d29cf470d8af7a369f`.
- Published offline assets:
  `.artifacts/offline-translation-smoke/fd4976a0efdf4ee897140146a46fbe3a`.
- Inspected new minimum capture and settings screenshots in `.artifacts/smoke`;
  the automatic-practice caption and updated AI-purpose copy fit the layout.

## Local package

Open the new Windows EXE for future captures. It uses the configured AI service
for one extra all-sense preparation request per newly saved word when the bank
is missing; cached practice has no per-answer model call. Old words can still
use explicit preparation. Failure preserves the save and exposes manual retry.

Directory: `C:\Private Files\Projects\TUCI\dist\兔词-自动出题试用版`.

- EXE: **144,001,224 bytes**, SHA-256
  `A21382A6CEC16737D2713CE85161BFBD949AB043B8EFB30B14F71059FCAFB597`.
- ZIP: `兔词-自动出题试用版-win-x64.zip`, **144,300,099 bytes**, SHA-256
  `6890A450D0F8769B18C8630A32EBA0EA1A974618F083D270F462FBD5D370FA2E`.
- Complete directory: **303,062,865 bytes**, **54 files**. Every copied file
  and decompressed ZIP entry matches the source SHA-256; all **47** offline
  assets match release metadata. The update helper remains bundled.
- The previous contextual trial remains unchanged, EXE SHA-256
  `E643C503094A1BD84AF2555FC1C30FD68ED069E8A3F2D7E743FB40544FE56163`.

Machine-readable evidence: `.artifacts/automatic-capture-practice-package.json`.
Original user notes and prior releases were preserved. Source/documentation
remain uncommitted and unstaged; HEAD is
`88ac7184451be81c0d664934756d6a58a5621d1d`. No upload or deployment occurred.
