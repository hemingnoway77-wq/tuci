# Automatic context preparation after capture

The user requests that future word captures automatically prepare contextual
spelling material. Extend the existing local Windows trial; do not commit,
upload, deploy or publish an online update.

- [x] Identify the common saved-word path for main capture and reading sidebar.
- [x] Emit a saved-only notification containing the immutable submitted settings
  and corrected capture result, after updating saved note/selection UI (API).
- [x] Connect production Windows startup to a MainWindow-owned background queue,
  using the account lifetime token and fixed local library scope. Preserve the
  saved note/UI regardless of generation failure. Do not draw roaming cards.
- [x] Red/green shared context preparation: cache hit reuse, one in-flight
  generation across auto/manual callers, caller cancellation, invalid/failed
  generation and cross-instance atomic cache writes (learning_core).
- [x] Red/green saved-event behavior and subscriber isolation (article_reader).
- [x] Verify real temporary-note capture through MainWindow and the reading
  sidebar contract, no startup/duplicate request, neutral background lifecycle,
  corrected form, close/account cancellation, friendly failure, cache reuse.
- [x] Independent review, focused tests, complete publisher verification and a
  new standalone local EXE/ZIP preserving prior trials (Root).
  Final publisher: 1,262/1,262 unit tests, zero warnings/errors, all native WPF,
  practice/reading/startup/navigation/update/offline checks. Complete copied
  bundle and ZIP verified against source and release asset metadata; local
  delivery links ready. See the matching verification record.

The save remains the primary transaction. An extra AI request prepares all
meanings of the new word together, then the validated local bank supports both
spelling and Chinese recall without per-answer requests. Failed preparation is
not retried automatically; the existing explicit preparation action remains.
Historical words are not batch-generated merely by opening the application.
