# Bundled Offline Translation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Ship TUCI Windows with installation-ready, free offline English-to-Chinese word translation in its current native popup.

**Architecture:** Keep the Core word-translation contract. A bounded offline service calls an isolated local Bergamot WASM worker hosted by the existing WebView2 dependency. Pin and ship models, engine, JS and notices as local content; no remote translation fallback.

**Tech Stack:** .NET 10, WPF, WebView2 1.0.4314.35, Firefox Bergamot WASM, released Mozilla en-zh model.

**Spec:** `docs/superpowers/specs/2026-11-06-offline-translation-design.md`

## Global Constraints

- User explicitly selected bundled language assets; never ask users to obtain a translation key or download models at first use.
- Keep native popup/capture/theme behavior; no unrelated UI expansion.
- Existing Windows feature worktree and branch; no user settings, notes or existing application processes touched by verification.
- Only local runtime resources; preserve cancellation and normalize errors to `WordTranslationException`.
- Release must include the offline asset directory and existing third-party notices.

## Review Focus

- First translation on a computer with no model cache must use bundled assets.
- Missing/corrupt local assets and missing WebView2 Runtime must produce an actionable error and allow retry.
- Rapid word changes or leaving reading while initialization/translation runs must not show stale text.
- Browser failure, timeout and disposal must complete pending tasks and release the host.
- Test assemblies and single-file publish must preserve the complete model/worker directory.

### Task 1: Pin and bundle models and the working WASM runtime

**Files:** `src/VocabCollector.Desktop/Assets/OfflineTranslation/**`, `scripts/get-offline-translation-models.ps1`.

- [x] Pin the official Release/base-memory en-zh four-file model, record all hashes/sizes/URLs and include MPL notices.
- [x] Prove an official compatible WASM/JS runtime actually translates using those files on this Windows machine.
- [x] Implement local `index.html`/worker assets with messages `{type:'ready'}` and `{type:'result',id,text}` / `{type:'error',id,message}`. Host requests use `window.tuciTranslate({id,text})`.
- [x] Independently review asset provenance, runtime compatibility and absence of remote fetches.

### Task 2: Core offline service

**Files:** `src/VocabCollector.Core/Reading/OfflineWordTranslationService.cs`, matching tests.

**Interfaces:** `IOfflineTranslationEngine : IDisposable`, `Task<string> TranslateAsync(string text, CancellationToken ct)`; `OfflineWordTranslationService(IOfflineTranslationEngine engine)` implements the existing word service and owns its engine.

- [x] Write/run failing tests for successful Chinese output, validation, cache/cancel/disposal races and error normalization.
- [x] Implement normalization consistent with the current single-word contract and a 128-entry successful-result LRU cache without network access.
- [x] Run focused tests and review the service independently from the desktop host.

### Task 3: Desktop bridge and reading integration

**Files:** `src/VocabCollector.Desktop/Reading/WebViewOfflineTranslationEngine.cs`, `ReadingPage.xaml(.cs)`, Desktop csproj, real-engine smoke checks.

- [x] Add a smoke mode/test that fails before the runtime exists: real word/sentence, cancellation/retry and blocked nonlocal resource.
- [x] Implement lazy dispatcher-bound WebView2 host, local-only navigation/resources, structured ID-correlated responses, serialized requests, timeout/recovery and disposal.
- [x] Replace only ReadingPage's default translation service; leave injected test services and current popup version/cancel behavior intact.
- [x] Copy local assets to normal/publish/transitive test output. Verify real model operation and default popup integration.

### Task 4: Release verification and delivery

**Files:** `scripts/verify.ps1`, `scripts/publish.ps1`, `THIRD-PARTY-NOTICES.md`, `README.md`, `docs/verification/2026-11-06-offline-translation.md`.

- [x] Add actual offline-engine checks to release verification and include a full asset manifest in release metadata.
- [x] Run locked restore, all unit tests, Release build, existing WPF checks and actual local translation smoke.
- [x] Review the complete diff for correctness, licensing/provenance and lifecycle behavior, fixing material findings.
- [x] Publish a new Windows self-contained release; verify its complete offline layout and deliver the executable together with all assets as a ZIP under the user's original project's `dist` directory.

### User clarification: dictionary mini entries

The user requested source-backed part-of-speech meanings after seeing the single-translation scope. Extend the same delivery before release.

**Files:** `src/VocabCollector.Core/Reading/{TranslationContracts,OfflineDictionaryWordTranslationService,JsonLinesWordDictionary}.cs`, `src/VocabCollector.Desktop/Assets/OfflineDictionary/**`, `ReadingPage.xaml(.cs)`, matching unit and real-data smoke checks.

- [x] Pin the official full ECDICT base CSV and MIT notice; generate 26 bounded first-letter JSONL shards `[word,phonetic,translation,exchange]` with deterministic word normalization and recorded filtering/dedup statistics. Do not use the tiny demonstration CSV as a usable dictionary.
- [x] Keep the original `WordTranslation` primary constructor and add optional init-only `Phonetic`, `Meanings`, `Lemma`; introduce `WordMeaning(string PartOfSpeech,string Definition)` and `IOfflineWordDictionary.LookupAsync` returning nullable `WordTranslation`.
- [x] Write failing Core tests then implement exact-word lookup, source-label parsing, three-shard LRU, cancellation, corrupt-package errors and dictionary-first/local-model-fallback ownership. Do not infer dictionary meanings or parts of speech from model output.
- [x] Write a failing WPF test, then render the small details in the native popup and clear them on selection changes. Preserve existing full `WordTranslationText.Text` for compatibility while avoiding duplicate visible rendering.
- [x] Extend real-data smoke to verify `book` and `run` meanings, phonetics, inflected forms, zero WASM initialization on a dictionary hit, fallback and the published dictionary layout.
- [x] Re-run release checks after this addition, inspect the actual detail popup, and report measured complete bundle size.

## Execution record

User approval is the explicit instruction “可以，就按照直接自带语言包改装”. Implementation proceeds within that authorization. Existing feature worktree is reused. Independent model, runtime-spike and Core-service work is delegated; root owns Desktop integration and delivery.
