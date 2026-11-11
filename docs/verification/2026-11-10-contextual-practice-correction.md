# Contextual roaming practice correction — local Windows trial

This supersedes the definition-only fallback in the first local trial recorded
in `2026-11-10-roaming-practice.md`. The user explicitly requested exercises in
context. No source commit, upload, deployment or online update publication is
authorized for this trial.

## Resulting behavior

- Every new question requires an English sentence and Chinese translation for
  its exact saved sense. Banks bind by stable sense ID, never example order;
  missing, duplicate, unknown or partial sense mappings are rejected.
- English spelling blanks the saved form in the English sentence and shows its
  Chinese translation. Trusted lemma spelling still passes neutrally, reveals
  the saved form and waits for manual next. Exact spelling uses normal grade.
- Chinese recall shows the saved word in English context, conceals Chinese
  until submission, then reveals the matching definition and full translation.
  The learner decides whether their wording matches; no Chinese string grading.
- Missing contexts show an explicit 准备语境题 action. Its notice says it uses
  the currently configured AI API quota. No request is made on entry, startup,
  ordinary grading or cached questions. One preparation generates every sense
  of one word, then validates and atomically caches it locally for reuse.
- Preparation can be retried after invalid data, configuration or network
  failures. There is no timer during preparation and no definition-only fallback.
  换一个词 changes no remembered/skipped counters. The existing 30-second active
  timer, manual progression, ordinary roaming and note preview remain.
- Practice state and context banks stay under the current account/library's
  local state directory. Source Markdown is not changed. Existing practice-only
  completed receipts are retained; old contextless pending cards are not reused.

## Verification scope

- Core contextual bank/session: 16 failing/26 passing in red, then 42/42 green.
- Generator/cache: 35 failures against stubs, then 35/35 green. HTTP is mocked;
  real user API credentials are neither read nor called by these tests.
- Contextual page/core integration: 53/53 green before final review. It checks
  no automatic request, live settings, one complete all-sense preparation,
  subsequent cache reuse, concealed reference, manual grading and neutral lemma.
- The strict WPF practice smoke checks both senses/kinds, actual contextual
  prompts, mouse/keyboard operations, themes and minimum-size result visibility.

Generation validation proves structure, full sense-ID coverage, target-form
presence and safe text constraints. It cannot prove the semantic accuracy of an
AI-generated sentence or translation. The system prompt requests accurate,
natural examples for each meaning; genuine AI semantic quality was not tested
by sending paid production requests. Context preparation needs configured API
access; already cached exercises run without further model requests.

Independent review caught a same-exposure refresh bug: background sync could
cancel context generation, while an edited note could retain an old question.
Four regression branches produced 3 failures/1 pass before the fix. The Desktop
now compares the exposure and immutable note ID/title/Markdown, preserving
unchanged requests and revalidating changed notes. The combined contextual Core,
generator/cache and WPF page tests then passed **92/92**; the reviewer confirmed
both directions without further findings.

The first complete verification stopped at **1,233 passed / 1 failed**: an
existing NoteReader hover-focus assertion lost keyboard focus to null. That
fixture does not construct the new practice UI, and its production reader,
preview and theme code were unchanged. Isolated reader tests passed **21/21**.
The fixture was moved into the existing nonparallel WPF collection and now
explicitly activates the owner and asserts initial focus before opening hover;
the original final focus assertion remains. No reader production behavior was
changed to make the test pass.

## Final verification

The complete publisher rerun passed **1,234/1,234** unit tests, zero skips,
failures, build warnings or errors. Full native WPF, contextual practice,
pure reading, prepared startup, 160 rapid module switches, signed-update mocks
and real bundled offline translation/dictionary checks all passed. The actual
published offline assets were checked again after self-contained publication.

Evidence:

- Contextual practice: `.artifacts/practice-smoke/3cc1e18bf9cb473faa5112aacdc2b66f`
  (4 sense/kind combinations, zero model requests, zero binding errors).
- Visually inspected both new prereveal screenshots in that directory:
  spelling shows “She ____ me to her teacher.” with Chinese sentence translation;
  meaning shows “The company introduced a new policy.” and no Chinese reference
  before submission. The previously inspected minimum-size theme/result
  screenshots verify visible result actions and reference content.
- Prepared startup: `.artifacts/prepared-startup/b82063352f1c4a1caa2d91d224c957b1`.
- Navigation: `.artifacts/navigation-switch/0ef827e2dd984c6eb58f5e116ed45017`.
- Updates: `.artifacts/updates/35c62c6060b24b8ca874da7c9c625cb4`.
- Published real offline assets:
  `.artifacts/offline-translation-smoke/10821fa2fe1b4c178f560e4f085a93ad`.

## Local package

Open the new EXE, then 漫游 → 练习作答. Missing contexts require the explicit
准备语境题 action using the configured API; existing cached material is reused.

Directory: `C:\Private Files\Projects\TUCI\dist\兔词-语境复习试用版`.

- EXE: **143,990,472 bytes**, SHA-256
  `E643C503094A1BD84AF2555FC1C30FD68ED069E8A3F2D7E743FB40544FE56163`.
- ZIP: `兔词-语境复习试用版-win-x64.zip`, **144,293,727 bytes**, SHA-256
  `897ED63033D7DD0E252A551CFB25FD982CDAC2174F0EF761DE42FF6A99382E7F`.
- Full directory: **303,049,553 bytes**, **54 files**. Every copied file and
  every decompressed ZIP entry matches its source SHA-256; all **47** offline
  assets match release metadata. The update helper is included.
- The previous trial EXE remains unchanged at its recorded SHA-256
  `D6070CDFAE45726452B19ABDC7FD9DEC6EF62DEE90312E6A6060BAEC705D712C`.

Package machine-readable evidence: `.artifacts/contextual-practice-package.json`.
Earlier releases and user data remain intact. Source and documentation remain
uncommitted, with HEAD `88ac7184451be81c0d664934756d6a58a5621d1d` and no staged
files. Web/server source is unchanged; no upload or deployment was performed.
