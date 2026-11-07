# Responsive loading and restrained motion

The user requested faster first roaming/module access, smooth shared navigation
underlines, subtle module transitions, stable frequent note changes, smoother
sidebar motion, a light page-turn entrance for daily articles, quieter scrollbars
at the far edge, and short eased wheel scrolling. The user then explicitly
withdrew shortening the horizontal meaning mask: preserve the existing 24-DIP
wood-grain appearance and all reveal protections unchanged.

## Decisions

- Fix loading work before decorating it: coalesce local scans, retain a short
  fresh snapshot, mark actual watcher/manual changes dirty, and avoid full scans
  for every tab switch or window activation. Keep manual refresh authoritative.
- Production startup shows a quiet preparation surface while existing local
  initialization and reusable module construction/scan happen. Do not fetch
  reading feeds, initialize the translation browser/model, or count a review.
- The four native navigation RadioButtons keep their names/keyboard behavior;
  one shared thin line stretches and settles between targets, like the period
  selector. Rapid reversals use current presentation values; reduced motion and
  resize settle immediately.
- Module transitions use a short, restrained fade/very small horizontal offset.
  Frequent note changes keep the entire reader stationary. Sidebar transitions
  capture one static frame and perform final real layout once, avoiding repeated
  document reflow for each animation tick. Release snapshots on every exit.
- Daily articles enter with a shallow horizontal page-like motion, without a
  physical 3D curl or decoration. Keep article selection, return position,
  cancellation, clean text and word actions intact.
- Put the article scrollbar at the outer viewport edge, preserve a comfortable
  gap from the text, and make all application scrollbar thumbs quieter. Wheel
  smoothing is short and follows accumulated input; direct dragging, keys,
  selection, programmatic navigation and nested/boundary scrolling remain exact.
- Respect system-disabled animations; cancel superseded motion and async work.
  Preserve all prior uncommitted offline dictionary and interaction fixes.

## Acceptance

Evidence must include shared-scan/activation behavior and cold/warm timings,
no startup network/model/review side effects, navigation reversal and final
alignment, stationary repeated note changes, sidebar layout counts and cleanup,
wheel accumulation/reversal/boundaries, article transitions/scrollbar spacing,
and retained history/selection. Run the complete unit/WPF/offline-model release
checks and deliver a new complete Windows ZIP preserving earlier releases.
