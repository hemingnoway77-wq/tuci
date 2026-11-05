# Reading word actions implementation plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans task by task.

**Goal:** Tapping an article word highlights that occurrence and opens nearby 收录 / 翻译 actions; temporary translation and pronunciation do not create notes.

**Architecture:** Keep the existing static application and capture flow. Isolate optional public word lookups from reader selection state. Tokenize literal article text with occurrence offsets; one anchored popup owns abortable requests and audio playback.

**Tech stack:** Native browser DOM, fetch, HTML audio / SpeechSynthesis, Node test runner, Playwright Chromium and WebKit.

**Spec:** Current user request: selected word background, actions below the word, temporary free translation and pronunciation.

## Constraints

- Preserve the serif typography, theme palettes, vocabulary data, capture draft semantics, and offline upgrade repair.
- Only 翻译 initiates public lookup requests, with the selected word only. Success caches remain bounded in memory.
- MyMemory anonymous English → Chinese lookup; Free Dictionary API optional phonetics/audio; system English speech fallback.
- No account setup, paid service, API key, backend storage change, or automatic note creation.
- Abort and ignore stale results on selection changes, close, body replacement, or leaving reading.
- Validate third-party responses and restrict audio URLs/CSP to dictionary audio hosts.

## Review focus

Repeated occurrences and punctuation must select exactly one original token; stale responses must not overwrite a later selection; API failure must leave 收录 usable; 320px layouts must retain accessible actions; temporary lookup must leave notes/preferences/drafts unchanged.

## Tasks

### 1. Word lookup service

- [x] Write failing mocked-fetch tests, then implement `translateWord(word,{signal}) -> Promise<string>` and `lookupPronunciation(word,{signal}) -> Promise<{phonetic,audio,sourceUrl}>` in `web/word-lookup.js`.
- [x] Verify normalization, request boundaries, abort/timeout, quota and malformed response handling, safe audio sources, and bounded success caching.

### 2. Reader selection and popup

- [x] Test `wordSegments(text)` and occurrence-based `wordSentence(text,start,end)` before implementation.
- [x] Add `web/reader-words.js`, integrate cleanup/collection into `web/learning.js`, and style token selection and anchored actions in `web/learning.css`.
- [x] Render asynchronous lookup results safely; handle audio failure with a user-triggered system speech fallback.

### 3. Packaging and verification

- [x] Include both modules in `web/build.py`, allow only expected audio origins in CSP, and document the free service limits.
- [x] Run Node tests and build; verify real reading interaction at 320px/402px/desktop in Chromium and WebKit with mocked network success/failure/races and storage snapshots.
- [x] Check actual anonymous providers separately and review changes. Live translation and phonetics returned successfully; current-network dictionary audio returned 502, so real iPhone sound playback still needs device confirmation. Audio failure/system speech invocation is covered in the browser tests.
- [x] Push `codex/web`, atomically publish static release, and verify live assets and reader behavior.

## Verification

- Node tests: 35 passed. Reading browser checks passed in Chromium and WebKit with storage/IndexedDB snapshots, repeated occurrences, stale responses, quota/retry, pronunciation fallback, correct collection context, and narrow/landscape layouts.
- Existing oldest-worker upgrade and offline reload regression passed; general 15-layout/theme/serif checks passed in both engines. Windows WebKit offline worker installation remains unavailable in this runner.
- Published code commit `768d1242c35668eb0c1c490046a5adbc57eb2261`, static release `b495c24ae2fbd38e`. All 20 live files matched the local build; sync health returned `ok:true`; live reading interaction checks passed in both engines with isolated mocked lookup data.
