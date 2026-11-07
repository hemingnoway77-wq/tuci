# Fluid Navigation Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans. Track completed steps with checkboxes.

**Goal:** Remove redundant loading/layout work and add consistent, restrained navigation and scrolling motion.

**Architecture:** Coalesced local load preparation and dirty/fresh snapshots; one shared nav underline; short presentation-only page motion; stationary document swaps; one-layout sidebar snapshots; exact direct scrolling with short eased wheel motion.

**Tech Stack:** .NET 10/WPF native controls and rendering, existing local catalog and article/dictionary services.

**Spec:** `docs/superpowers/specs/2026-11-06-fluid-navigation-design.md`

## Constraints and review focus

- Existing 24-DIP horizontal meaning mask remains unchanged at the user's request.
- Root owns MainWindow/App/navigation helpers; load agent owns LearningPage/catalog;
  note agent owns LibraryBrowser/NoteReader; reading agent owns scrollbar/scroll
  behavior and ReadingPage/ArticleTextReader. Theme edits stay in disjoint blocks.
- Full verification and publishing are serialized; focused builds use coordinated slots.
- Watcher changes arriving during a scan must not be lost. Manual refresh stays fresh.
- Hidden/startup preparation must not advance reviews or start feed/model requests.
- Rapid switching, cancellation, resize, unload, reduced motion and closing must
  settle presentation and release bitmap/animation/task resources.

### Local load and startup

- [x] Trace and measure repeated tab/activation scans and UI work.
- [x] Red/green tests for `EnsureLoadedAsync(bool forceRefresh=false)`, shared
  in-flight load, short freshness, dirty/manual requests, recovery and disposal.
- [x] Integrate production quiet startup preparation and ordinary cached access;
  activate with a freshness check rather than unconditional forced scanning.
- [x] Verify no review/network/translation startup side effects and cold/warm timing.

### Navigation and page motion

- [x] Add a shared animated underline while retaining native RadioButtons.
- [x] Test rapid reversal/final alignment and immediate reduced-motion settlement.
- [x] Add short module motion with cancellation and retained selected-page state.

### Frequent note and sidebar changes

- [x] Reproduce repeated reader translation/fades and per-frame width reflows.
- [x] Remove whole-reader note-switch jumps and redundant zero-offset restore.
- [x] Implement single-layout static-snapshot sidebar motion and cleanup; verify
  frequent switching, layout counts, reversal, resize and leaving the page.

### Article and scrolling

- [x] Add subtle article entrance with retained return/selection/cancellation behavior.
- [x] Move article scrollbar to the viewport edge and soften shared scrollbar styles.
- [x] Add short wheel smoothing; test accumulation, reversal, nested boundaries,
  exact drag/key/programmatic operations and disabled animation.

### Integrated delivery

- [x] Independently review new lifecycle, performance and UI behavior.
- [x] Run complete unit/WPF/offline dictionary/model verification and publish.
- [x] Inspect representative UI frames, record measured evidence, copy/check the
  new full release and ZIP in the original project's dist and deliver links.
