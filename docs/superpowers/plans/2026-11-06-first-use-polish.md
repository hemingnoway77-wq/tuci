# First-use Polish Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans. Track completed steps with checkboxes.

**Goal:** Fix the five reported first-use and interaction issues.

**Architecture:** Keep native controls and current data preparation. Pre-realize
library UI under the existing startup overlay, scope tree feedback to its row,
constrain the settings path column and tune presentation-only page animation.

**Tech Stack:** .NET 10 / WPF.

**Spec:** `docs/superpowers/specs/2026-11-06-first-use-polish-design.md`

## Constraints and review focus

- Preserve current 24-DIP mask and all prior changes in this shared worktree.
- Root owns startup and startup smoke; note_reader owns tree/settings;
  article_reader owns PageMotion/ReadingPage. Serialize all builds.
- Startup must not change selected note, review history, feed or translation.
- Closing or rapid navigation cannot leave opacity, input or layout clocks stuck.
- Folder keyboard navigation and file selection remain native.
- Long paths, minimum size, keyboard entry and disabled animation remain usable.

## Tasks

- [x] Reproduce first library UI creation after startup; record cold/warm timing.
- [x] Prepare real library layout under overlay and verify zero side effects.
- [x] Fix ancestor hover/folder selection; verify actual nested row states.
- [x] Fix settings browse action clipping at initial/minimum sizes and long paths.
- [x] Tune module/article motion and add article-title hover feedback.
- [x] Review lifecycle and regression risks; run full verification and publish.
- [x] Inspect real UI frames; copy/hash complete release and ZIP to original dist.
