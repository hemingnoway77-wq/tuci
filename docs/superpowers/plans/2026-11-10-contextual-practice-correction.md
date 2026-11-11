# Contextual Practice Correction

**Goal:** Correct the first local trial so every exercise recalls a word's
meaning in its own sentence, as the user originally requested.

**Architecture:** Map complete sentence/translation banks by stable sense ID;
pass them into the existing local deck. Add explicit AI preparation and local
cache, with no silent definition-only fallback or per-answer API request.

**Spec:** correction section in
`docs/superpowers/specs/2026-11-10-roaming-practice-design.md`.

- [x] Red/green complete sense-ID context binding, blanking, translations and
  legacy pending-question migration (learning_core).
- [x] Red/green bounded configured Chat Completions generation and validated
  account-local cache, including invalid data, failures and cancellation (note_reader).
- [x] Integrate preparation action, live API settings, cancellation, contextual
  card rendering and retry into Desktop (Root).
- [x] Adapt page/smoke fixtures to genuine contextual sentences; verify no API
  call before the explicit preparation action, complete per-sense bank and reuse
  after preparation, and both question types in actual sentence context (article_reader).
- [x] Independent review; complete checks; inspect screenshots; build a new
  complete local package, preserving the first trial and all user data.
  Review identified a same-exposure refresh bug: preserve preparation during
  unchanged background sync, but invalidate it after the note content changes.
  Four red/green regression branches now cover the guard. Final publisher:
  1,234/1,234 unit tests, all WPF/smoke/offline checks passed; zero warnings.
- [x] Prepare local delivery links, with no source commit, upload or deployment.
  Full package copied and ZIP verified at the original project's dist directory;
  evidence in `docs/verification/2026-11-10-contextual-practice-correction.md`.
