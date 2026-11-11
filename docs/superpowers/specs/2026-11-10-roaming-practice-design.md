# Roaming answer practice — local Windows trial

Approved in conversation: implement spelling and Chinese meaning recall now;
defer the semantic coordinate feature. Keep the card-based interaction simple.
Deliver a local Windows package; do not commit, push, deploy or publish updates.

- Add a single practice toggle alongside ordinary roaming. Practice interleaves
  spelling and meaning cards across words and senses. Each definition line is
  a sense; synonyms inside a definition remain together.
- Spelling shows the current meaning and a contextual blank where the existing
  note supplies a reliable sentence for that sense. Without a reliable binding,
  show that meaning and a first-letter/length clue instead of inventing context.
  Cover every available sense. Hide the word, phonetics, answer preview and all
  other answer-bearing text until submission.

## Correction after the first local trial

The user rejected the definition-only fallback: the agreed feature is recall in
context. This supersedes the fallback above. Every new spelling and meaning
question must have an English sentence bound to that exact sense plus a natural
Chinese sentence translation. Spelling blanks the target in English; meaning
recall shows the word in English context and reveals Chinese only on submission.
Missing/partial context banks never become definition-only questions.

Existing note sentences may be used when their sense binding and translation are
reliable. Otherwise the card offers an explicit Prepare context questions action.
It uses the currently configured AI service only after that click, tells the
learner it uses their API quota, prepares one sentence for every sense together,
validates the structure/target/IDs, then caches the bank in this account's local
library state. Cached exercises require no per-answer request. Unconfigured,
offline, invalid or cancelled generation leaves the card in a retryable
preparation state, with no countdown or false fallback. The user can choose
another word without changing remembered/skipped counters.
- Exact saved form passes and uses ordinary remembered weighting. A confirmed
  lemma also passes, shows the correct saved form, and changes neither
  remembered nor skipped counters. Other inflections and misspellings do not
  automatically pass. Normalize case, outer whitespace and equivalent apostrophes.
- Meaning cards show the word and available context. Submission reveals that
  sense's reference meaning alongside the submitted answer. The learner selects
  matches/does not match; no Chinese string grading or AI request.
- Use a 30-second active-view timer for this trial. Timeout reveals the reference
  and never auto-advances; spelling timeout is unremembered, Chinese still uses
  self-assessment. Pause on leaving the surface or window deactivation.
- Every result remains visible until a manual next or self-assessment action.
  Persist local per-sense/per-kind practice results under the current account's
  library state. Preserve pending questions and completion idempotency.
- Preserve ordinary roaming, blur, statistics, note preview, account sync and
  existing user notes. Practice-only detail stays local in this first trial;
  normal remembered/skipped exposures keep the existing sync format. Neutral
  lemma success must never become a false skipped completion during replay.

Acceptance: deterministic parser/word-form/deck/persistence and neutral-weight
tests; real WPF spelling/lemma/meaning/timeout/pause/manual-next/navigation tests;
themes/minimum size/screenshots, full verification and complete offline bundle.

## Contextual answer forms after production backfill

Some existing noun meanings explicitly require a plural under a base-title note
(remain/remains, supply/supplies, specification/specifications). Requiring the
unchanged note title in those sentences produces ungrammatical practice. An
optional per-context answer form may therefore select a noun plural only when
the source meaning explicitly declares it and trusted forward family rules
confirm it. The default remains the saved word; the generator receives its exact
allowlist and cannot invent another inflection or unrelated answer.

The actual contextual form becomes the blank, letter hint and revealed answer.
Its base is accepted as LemmaCorrect with neutral roaming weighting, followed
by manual next. Neither source Markdown nor stable note/sense identity changes.
Nullable fields are omitted from JSON to preserve old cache fingerprints and
three-field contexts/checkpoints. Historical completed practice receipts retain
their original answers, and valid cloud rows are not regenerated.
