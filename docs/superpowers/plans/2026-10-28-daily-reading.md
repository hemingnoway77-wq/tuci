# Daily Reading Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox syntax for tracking.

**Goal:** Add daily English article recommendations without AI calls or local vocabulary matching.

**Architecture:** Independent Core RSS/Atom service and LocalAppData cache; a pink WPF window consumes IDailyReadingService. MainWindow opens a single reading window without requiring API settings. Use the existing feature checkout and preserve all capture behavior.

**Tech Stack:** .NET 10, WPF, HttpClient, System.Xml; no new dependencies.

**Spec:** docs/superpowers/specs/2026-10-28-daily-reading-design.md

## Global Constraints

- No AI requests, credentials, vocabulary reads or Obsidian writes in reading code.
- At most two general articles and one technology article; browser opens original HTTP/HTTPS pages.
- Same-day cache, daily rotation, partial-feed success, offline cache and request cancellation.
- Untrusted XML and HTML stay bounded and are never rendered as executable content.

## Review Focus

- First run with no API or vault configuration still opens reading.
- Closing during refresh cancels requests without unhandled async errors.
- Empty, failed or malformed feeds do not overwrite useful cached content.
- Unsafe links, XML entities, large feeds and HTML do not enter the renderer/browser.
- Midnight rollover and a failed source preserve useful recommendations.

## Task 1: Feed service and daily cache

Files: src/VocabCollector.Core/Reading/*; tests/VocabCollector.Tests/DailyReadingServiceTests.cs.

Interface: IDailyReadingService.GetAsync(bool forceRefresh, CancellationToken) returns DailyReadingResult(Day, Articles, IsFromCache, IsOffline, Message). ReadingArticle contains Title, Summary, Url, SourceName, Category and optional PublishedAt.

- [x] Add failing fake-HTTP tests for parsing, cache hits, daily rotation, failures, cancellation and unsafe content.
- [x] Implement independent bounded concurrent feeds, local selection and atomic cache persistence.
- [x] Run targeted tests and record results.

## Task 2: Reading window and integration

Files: DailyReadingViewModel.cs, DailyReadingWindow.xaml(.cs), MainWindow.xaml(.cs), DailyReadingViewModelTests.cs.

Interface: DailyReadingViewModel(IDailyReadingService), LoadAsync(bool forceRefresh = false); DailyReadingWindow accepts the view model and optional original-URL opener for tests.

- [x] Add failing VM tests for loading, refresh, error retention and cancellation.
- [x] Implement article cards, refresh/status/empty states, default browser action and window-close cancellation.
- [x] Add MainWindow entry and lazy independent reading HttpClient; reuse a visible reading window.
- [x] Verify existing capture input and preview stay intact.

## Task 3: End-to-end verification and distribution

Files: tests/VocabCollector.Desktop.Smoke/Program.cs; README.md; docs/verification/2026-10-28-daily-reading.md.

- [x] Add real-control checks using an isolated fake reading service; assert zero model requests and no settings requirement.
- [x] Verify live default feeds separately from deterministic tests, inspect normal/minimum-size screenshots.
- [x] Run scripts/verify.ps1 and inspect all results; resolve review findings.
- [x] Publish self-contained exe to dist/兔词-每日阅读版, zip the distribution and record size/SHA256.
