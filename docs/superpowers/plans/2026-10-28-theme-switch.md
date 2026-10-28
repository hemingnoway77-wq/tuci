# Theme Switch Implementation Plan

> **For agentic workers:** Use superpowers:subagent-driven-development or superpowers:executing-plans.

**Goal:** Offer a restrained warm neutral default with an opt-in original pink theme and local preference persistence.

**Architecture:** Dynamic WPF palette resources, an independent appearance store and ThemeManager. MainWindow exposes a toggle; App loads preference before showing UI. No API or vault coupling.

**Tech Stack:** Existing .NET 10 WPF, System.Text.Json, no new dependencies.

**Spec:** docs/superpowers/specs/2026-10-28-theme-switch-design.md

## Constraints and review focus

- Warm neutral default, no blue or green accents; original pink remains available.
- Application-wide live switch without losing input, current note, settings draft or reading state.
- appearance.json stores only theme and never rewrites encrypted API settings.
- Unconfigured API, bad/missing preference and unwritable preference folder remain usable.
- UI stays usable at 900×640 / reading 800×600; contrast and focus/hover states follow theme.

## Task 1: Palette, controller and preference

Files: ThemePalette.cs, ThemeManager.cs, AppearanceSettingsStore.cs, theme tests.

Contract: enum AppTheme { WarmNeutral, Pink }; ThemePalette.Create(AppTheme) returns ResourceDictionary. Color keys ThemeColorHEX and brush keys ThemeBrushHEX preserve original hex colors for pink; existing aliases Ink/Muted/Rose/RoseSoft/Line/Paper/ShellBackground remain supported. ThemeManager(ResourceDictionary, AppearanceSettingsStore? = null) exposes CurrentTheme, LoadAsync(), Apply(AppTheme), SetThemeAsync(AppTheme) returning save success. Store LoadAsync returns AppTheme, SaveAsync writes only appearance.json.

- [x] Observe failing tests for default, round trip, corruption, canceled/failed writes and palette validity.
- [x] Implement isolated atomic store and live palette application; run focused tests.

## Task 2: Dynamic visual surfaces

Files: Theme.xaml, MainWindow.xaml, SettingsWindow.xaml, DailyReadingWindow.xaml, Assets/AppIcon.xaml, Preview/MarkdownDocumentRenderer.cs.

- [x] Convert brush literals and color resources into dynamic palette lookups; retain exact original pink values.
- [x] Add compact ThemeToggleButton with ThemeToggle_Click and ThemeToggle style, off = neutral/on = pink.
- [x] Make FlowDocument colors update for already-rendered notes, preserve text/link behavior.
- [x] Coordinate with palette owner, build and report unsupported resource cases.

## Task 3: Integration, real controls and package

Files: App.xaml.cs, MainWindow.xaml.cs, Smoke theme checks, README and verification record.

- [x] Load appearance before displaying production window; use optional injected ThemeManager in smoke.
- [x] Switch/persist from main toggle with error reporting and no API dependence.
- [x] Verify both themes across already-open windows and note preview, preserve content, prove restart persistence and zero AI requests, inspect normal/minimum screenshots.
- [x] Run complete verification, review and publish self-contained exe/zip to dist/兔词-双主题版.
