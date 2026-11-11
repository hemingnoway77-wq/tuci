# Roaming Practice Implementation Plan

> **For agentic workers:** Use subagent-driven development; Root serializes builds.

**Goal:** Add local Windows spelling and meaning self-assessment to roaming.

**Architecture:** A Core question bank and durable per-sense deck feed a new
LearningPage practice partial. Existing RoamingSession retains word weighting,
with a neutral completion path and interleaving for practice only.

**Tech Stack:** .NET 10 / WPF / local JSON / existing offline dictionary.

**Spec:** `docs/superpowers/specs/2026-11-10-roaming-practice-design.md`

## Ownership and constraints

- learning_core: new Core PracticeQuestion/Bank/Session and their tests.
- note_reader: RoamingSession practice advancement and weight/sync tests.
- Root: Desktop XAML, practice partial, integration, documentation and package.
- article_reader: isolated practice WPF smoke and page regressions.
- No coordinate UI, server changes, web feature, source commit or upload.
- Invalid/unknown senses or lemmas must not be guessed. No answer leaks.
- Retries, close, account/surface changes and failed writes must not duplicate
  grades, lose the submitted answer, advance stale cards or expose old account state.

## Tasks

- [x] Red/green Core sense parsing, trustworthy morphology and interleaved durable deck.
- [x] Red/green neutral completion, weighting, immediate-repeat avoidance and sync replay.
- [x] Implement simple practice card, timer, local-state recovery and manual progression.
- [x] Verify real UI results, self-assessment, pause/timeout, themes and minimum size.
- [x] Independent review, complete checks and self-contained local package.
- [x] Copy/hash local trial ZIP and deliver without committing or uploading.
