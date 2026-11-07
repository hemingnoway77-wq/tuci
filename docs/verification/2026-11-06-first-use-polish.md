# First-use and interaction fixes — 2026-11-06

## First library entry

The previous startup prepared note data while the learning page was Collapsed.
The tree therefore had no real TreeViewItem containers or first layout when
navigation was enabled. Its first visible layout still blocked the first click;
later entries reused that work. The prior smoke measured first roaming rather
than first library layout, so it missed this path.

The new startup prepares actual library and reading templates/layout underneath
the existing opaque preparation surface. Pages temporarily receive visible
layout with pointer input disabled, then restore their original visibility and
input. No page tab, note selection or review is changed.

Measured in the same 240-note flat synthetic fixture before/after the fix:

| Measurement | Before | After |
| --- | --- | --- |
| Realized file rows before enabling navigation | 0 | 240 |
| First library layout after clicking | 622.9 ms | 27.4 ms |
| Second library entry | 4.8 ms | 8.6 ms |
| Startup preparation including local data | 473.9 ms | 1,481.7 ms |

This deliberately moves one-time UI work into the visible startup preparation;
there is no artificial delay. Times describe this machine and fixture rather
than a universal performance guarantee. The final smoke also includes expanded
empty nested folders, valid tree measure/arrange, an empty unselected reader,
and interrupted/failed startup followed by retry. Preparation must make zero
feed, article-content, translation and AI requests or review encounters.

## Other requested corrections

- Folder selection no longer has a persistent selected fill. Hover belongs to
  the header row rather than the entire nested TreeViewItem subtree; only files
  receive the persistent selection style. Native keyboard-only focus visuals
  use a thin solid line at the header, so folder keyboard navigation remains
  visible without restoring a persistent mouse selection fill.
- Modules use a clearer 16-DIP side motion and opacity .76→1 over 200 ms.
  Articles use 24 DIPs and .74→1 over 230 ms, with a quadratic ease-out.
  Both affect presentation, retain cancellation, and respect disabled animation.
  The article starts its clock after first layout at Dispatcher Loaded priority,
  so body layout cannot consume the entrance's visible motion. A generation
  guard rejects old pending starts/completions and all exits restore the base.
- Directory article titles retain the hand cursor and gain theme-accent ink
  plus an underline on hover. The existing keyboard full-article action remains.
- Settings keep the folder action fully inside its layout when the current
  vault path is long, at initial and minimum window sizes. The confirmed case
  was a 1-DIP rounding overflow at 900×700 with long fake credentials:
  content actual width 777 versus viewport 776, so the button's right border
  was clipped. A 2-DIP content safety margin gives right edge 774/776 at 900,
  and 637/639 at 780. No credentials were read from the user's settings.

The current 24-DIP wood-grain meaning mask remains unchanged.

## Verification evidence

- Tree red: two real pointer/selection regressions fail against the prior
  template, including descendant-to-ancestor IsMouseOver propagation.
- Settings red: initial blank-credential fixtures pass; long fake URL/model and
  64-/128-character fake keys reproduce the right-edge clip in both 900 cases.
  The final four cases cover initial/minimum sizes and blank/long vault paths.
- Combined tree/settings green: **6/6**, with real viewport/row checks and PNGs
  in `.artifacts/ui-fixes`. Native folder selection/expansion semantics remain.
- Real animation frame tests observe module travel ≥8 DIPs and article travel
  ≥12 DIPs, then assert clean clocks/transforms and retained article document,
  selection and scroll offset. Frames/metrics are in
  `.artifacts/visible-navigation-motion`.
- Complete Release verification: **984/984** tests, no skips/failures; build
  with zero warnings/errors; full native WPF and optional local source-browser
  smoke; startup/interrupted/failure/retry smoke; offline dictionary/model hashes
  and actual Bergamot/dictionary UI checks repeated against published Assets.
- Final startup fixture has 240 notes plus three nested empty folder rows,
  valid actual 240×426 tree layout, no note selection and no review encounter.
  One full run measured startup preparation 1,084 ms, first library 7.8 ms,
  second entry 2.7 ms. Timings include that machine's current cache/JIT state;
  they are evidence that the first layout moved, not a universal latency limit.
- Independent review covered pending animation cancellation, preparation and
  folder/settings interactions. The keyboard-position feedback regression it
  identified was fixed using native keyboard-only focus styling.

One repeated full run exposed an existing test-only secondary window inheriting
an unfrozen application icon from another STA. That capture-link fixture now
explicitly shares its own frozen owner icon, matching the other isolated test
windows. This changes test resource isolation, not production icon behavior.

Representative inspected real frames: `library-nested-selection.png`,
`settings-900x700-long-path.png`, `settings-780x600-long-path.png` in
`.artifacts/ui-fixes`, and `article-middle.png` in the motion directory.

Final repeated verification passed after the keyboard-focus and test-isolation
fixes: **984/984**, zero warnings/errors, complete WPF/startup/offline checks.
The final startup run is `.artifacts/prepared-startup/debf965709a04200974b53befcf58c99`
(1,237 ms preparation; 7.1 ms first library layout; 3.4 ms second entry).
Published offline checks are in
`.artifacts/offline-translation-smoke/8787e88477184ecb949673c9334c4362`.

## Release

Copied and verified in `C:\Private Files\Projects\TUCI\dist\兔词-细节修正版`:

- EXE: **143,625,928 bytes**; SHA-256
  `550713EEFF813201D79F071F3A39EA6863BE5298D6332D245C09D702092E0727`.
- ZIP: `兔词-细节修正版-win-x64.zip`, **110,436,544 bytes**, 53 entries.
- ZIP SHA-256:
  `1AB09B6CF5BE4DB9EC32E088A79E62FDC2DDF4919F4FB3DEEAA9542BD89AF580`.
- Full extracted directory: **227,465,160 bytes**. Executable and all **47**
  offline assets match release metadata; all required ZIP entries exist.

Earlier release folders, user settings, notes and learning history were
preserved. Run the new folder's EXE, or fully extract its ZIP with Assets.
This task did not commit or push source to GitHub.
