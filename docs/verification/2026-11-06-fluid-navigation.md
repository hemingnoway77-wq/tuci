# Responsive loading and restrained motion — 2026-11-06

## Delivered behavior

- Preserve the existing 24-DIP horizontal wood-grain meaning mask, as requested
  in the user's final correction. No shader or reveal behavior was changed.
- Prepare local data behind a quiet startup surface. Library preparation shares
  one in-flight task and keeps a 30-second fresh snapshot; watcher changes and
  explicit refresh force a fresh scan. Changes during a scan trigger another
  pass inside the same completion task. Window activation checks freshness.
- Keep the four native navigation controls and animate one shared underline.
  Leading/trailing edges stretch and settle over 250 ms, including reversals.
  Modules use a 140 ms fade and 3-DIP presentation offset.
- Switch notes without moving or fading the entire reader. History still
  restores the requested position, including an explicit same-note zero offset.
- Sidebar collapse/expand uses a frozen previous frame fading for 130 ms and
  one final layout. Rapid reversal, resize/DPI change, leaving the page and
  disposal release the bitmap, stop clocks and restore local input.
- Articles enter with an 8-DIP shallow side motion and fade over 140 ms. Their
  scrollbar follows the viewport edge, with a separate 24-DIP text gutter and
  a maximum 950-DIP text column.
- Shared scrollbars use a quiet 2-DIP visual thumb within an 8-DIP hit area,
  with short appearance/disappearance fades. Wheel input eases over 90 ms;
  direct dragging, keyboard commands, selection and programmatic positioning
  take precedence. Logical item scrolling remains native.
- System-disabled animation settles immediately. Markdown replacement and
  article cancellation stop any previous wheel motion.

## Evidence

Fresh serialized Release rebuild: **0 warnings, 0 errors**. The earlier isolated
BAML initialization anomaly did not recur after the serialized rebuild.

Independent read-only review found no material remaining issue in shared
loading/dirty follow-up, startup failure/close recovery, animation/bitmap cleanup,
wheel event lifetimes or retained sentence semantics.

`scripts/publish.ps1 -OutputDirectory 'dist/兔词-流畅体验版'` passed:

- **976/976** unit/WPF regression tests, no skips or failures.
- Full real-control WPF smoke: capture, corrected spelling, cancellation,
  encrypted settings, themes, minimum window, native Markdown, tree/search/
  import/refresh, history, roaming counts and independent note preview.
- Real clean-article pointer/translation/capture actions, retained reading
  positions, cancellation, retry and optional source-browser checks.
- Dedicated production startup preparation smoke and shared underline checks.
- Offline dictionary/model hash and license checks; real bundled Bergamot
  translation, dictionary details, native popup, cancellation/cache and blocked
  remote resources. Repeated against the actual published Assets layout.

Focused additions include shared-task/TTL/dirty/retry/disposal cases, underline
reversal/reduced motion, stable repeated note switches, sidebar cleanup and
actual RichTextBox Run wheel routing. Wheel tests cover accumulation, reversal,
nested boundaries and exact direct scroll operations. Related note-reader
regressions passed **33/33**; related reading/scroll/theme regressions **47/47**.

Local timing evidence (synthetic fixtures on this machine; not a hardware-wide
performance guarantee):

| Measurement | Result |
| --- | --- |
| Before: 400-note fixture, ten ordinary preparations | 3,009 ms |
| Startup smoke: 240 notes, each with 12 paragraphs, local preparation | 504 ms |
| Same startup fixture: ten fresh cached preparations | 0.0062 ms |
| Same startup fixture: first roaming content after preparation | 112 ms |
| Sidebar collapse: real reader width changes, before → after | 4 → 1 |
| Sidebar expand: real reader width changes, before → after | 4 → 1 |

The 400- and 240-note fixtures differ and are not a direct speedup ratio.
Cached calls do no Markdown reading or snapshot replacement. Startup checks
explicitly verify zero feed requests, AI/network requests, translation calls,
translation browser views and review encounters. First intentional roaming
creates one encounter; rapid warm tab changes keep that same card and count.

Artifacts:

- `.artifacts/prepared-startup/6d6de7f2e4464796b4243ae7c793b4d7/`: preparation
  frame, warm roaming frame and timing JSON.
- `.artifacts/motion-tests/library-reader-motion.trx`: note/sidebar test output.
- `.artifacts/smoke/library-reader-collapsed.png`, `library-reader-mono.png`,
  `pure-reading-mono.png`, `pure-reading-capture-minimum.png`: real final UI.
- `.artifacts/smoke/roaming-meaning-masked.png`, `roaming-meaning-held.png`:
  existing horizontal mask retained in the integrated application.
- `.artifacts/offline-translation-smoke/2f8a80f475c342a6ab230acec3305b0d/`:
  published offline bundle checks.

## Release

Copied and verified in `C:\Private Files\Projects\TUCI\dist\兔词-流畅体验版`:

- EXE: **143,621,832 bytes**; SHA-256
  `C8DBD28DA19A71112E2754DEC6267927F35136F02C9282C6154D7410F73C030E`.
- Complete ZIP: `兔词-流畅体验版-win-x64.zip`, **110,434,052 bytes**, 53 entries.
- ZIP SHA-256:
  `DAC588AC1156086C1F79D758BD1019DF8819E20852F6CB695CDAE4C43C5E4AD8`.
- Extracted directory: **227,461,064 bytes**. The copied executable and all
  **47 offline files** match `release.json`; all required ZIP entries exist.

Earlier releases, settings, library notes and learning history were preserved.
Open this new folder's executable, or fully extract the ZIP with its Assets
directory. No source commit or GitHub push was performed for this change.
