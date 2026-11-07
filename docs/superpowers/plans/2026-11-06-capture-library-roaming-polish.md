# Capture, Library and Roaming Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Fix stale article context and refresh layout/focus, replace navigation labels with icons, and soften roaming reveal motion.

**Architecture:** Make context provenance explicit in MainViewModel; keep focus decoration outside Button measurement; reuse vector navigation controls; nest static displacement and animated Gaussian blur in HoldToReveal. Existing capture, navigation and offline dictionary contracts remain intact.

**Tech Stack:** .NET 10/WPF, native vector paths, WPF ps_2_0 ShaderEffect and BlurEffect, Windows SDK fxc.

**Spec:** `docs/superpowers/specs/2026-11-06-capture-library-roaming-polish-design.md`

## Global Constraints

- Preserve previous uncommitted offline dictionary/translation work in this worktree.
- No remote translation fallback, new service/account, user-note migration or settings reset.
- Keep shared capture behavior, pointer and keyboard accessibility and current theme.
- No persistent reveal on a new card, navigation or focus loss; respect disabled animation.
- Root controls full builds, smoke and publishing to avoid simultaneous STA tests/locked assemblies.

## Review Focus

- Changing only word casing/outer spaces and AI spelling correction retains a relevant source sentence.
- A user-edited sentence is distinguished from an untouched automatic prefill.
- Mouse focus does not change Button/footer dimensions; keyboard focus stays visible.
- Icon controls retain accessible names, history state and hit targets in all relevant toolbars.
- A replacement meaning cannot render while an old reveal animation is still clear.

### Task 1: Capture context ownership

**Files:** `src/VocabCollector.Desktop/MainViewModel.cs`, `tests/VocabCollector.Tests/MainViewModelTests.cs`.

- [x] Trace shared article prefill and both WordBox bindings, reproduce stale sentence in a failing test.
- [x] Track automatic sentence ownership; clear only an unrelated automatic prefill on a genuinely new word and preserve manual edits/internal correction.
- [x] Verify focused capture tests and review state/race behavior.

### Task 2: Refresh layout/focus and navigation icons

**Files:** `Theme.xaml`, `Learning/LibraryBrowser.xaml(.cs)`, `Learning/NoteReader.cs`, `Learning/NotebookIcon.cs`, native navigation control and focused tests.

- [x] Reproduce focus-border-induced measurement changes and persistent mouse-focus decoration.
- [x] Move keyboard focus decoration into a stable overlay; keep hover/press and keyboard operation.
- [x] Replace visible back/forward/file-list/collapse labels using reusable native vector icons, tooltip and automation names.
- [x] Apply the same icons to roaming preview toolbar and verify measured control/reader heights and navigation.

### Task 3: Frosted ink reveal

**Files:** `Learning/HoldToReveal.cs`, `Learning/Effects/**`, shader rebuild script, `LearningPage.xaml` halo margins, Desktop resource item, focused tests.

- [x] Add failing tests for stronger masking, interpolated press, fast reversal and immediate hard concealment.
- [x] Compile a static low-amplitude displacement shader with fxc ps_2_0 and retain its source/rebuild command.
- [x] Animate blur/distortion for ordinary holds/releases; hard-reset on replacement/focus/navigation; keep blur fallback and reduced motion.
- [x] Verify representative blurred, intermediate and clear frames without clipped halos or layout changes.

User refinement while implementing: the masking should resemble fine irregular
wood grain, with ink spreading left/right into horizontal streaks. Adjust the
shader to directional horizontal diffusion with static row variation; tune the
outer Gaussian to retain grain while keeping meanings unreadable. Show a real
preview and retain the existing 120/100 ms and hard-conceal protections. Validate
directional spread using an isolated shader probe and the production control,
including the strong Gaussian-only fallback.

### Task 4: Integrated verification and release

**Files:** WPF smoke checks, README, verification record and the complete release folder/ZIP.

- [x] Add actual shared capture/page and refresh/navigation evidence to smoke; update reveal checks for transition endpoints.
- [x] Independently review all changes and address material findings.
- [x] Run unit tests, Release build, complete WPF smoke and actual bundled offline dictionary/model checks.
- [x] Publish and validate a new complete release; copy to the original project's dist, archive all required assets, verify checksums and deliver.

## Execution

The user explicitly requested all four changes. Independent context, library and
reveal work is delegated; root owns integration, review, full verification and
delivery. Existing offline release files and user data are retained.
