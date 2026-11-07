# Capture, library controls and horizontal frosted reveal

## Delivered behavior

- Article-prefilled source sentences are associated with their word in the
  shared capture view model. Replacing the word in either capture input clears
  the old automatic sentence before announcing the new word. Equivalent casing,
  outer whitespace and apostrophes, and successful internal spelling correction,
  retain the corresponding sentence. Manual sentence edits are respected.
- Mouse focus no longer adds a layout-changing border to buttons. The previous
  template increased the refresh button by 2 DIP, affecting the auto-sized
  footer and leaving a persistent Accent/black rectangle. Keyboard focus now
  uses a lightweight adorner; the footer action row remains 32 DIP.
- Library navigation, file-list visibility, shared note-reader back navigation,
  roaming preview and source-webpage back/forward use native vector icons.
  Hit targets are 32 DIP, glyphs 16 DIP; tooltips, automation names, disabled
  state, history and ordinary Button keyboard input remain available.
- The user clarified that the desired masking resembles horizontal wood grain,
  then reviewed a real preview and requested a finer, softer texture. The final
  local shader samples nine horizontal positions with static row variation,
  using 24 DIP width, grain 0.30 and supporting Gaussian radius 3.6. It does not
  move the text vertically or animate noise. Unsupported hardware retains a
  radius-12 Gaussian mask. Amount zero restores the original pixels/position.
- Ordinary holds reveal over 120 ms and releases conceal over 100 ms, reversing
  from the current interpolated value. Text/exposure changes, focus loss,
  navigation, unload, deactivation and lost capture cancel interpolation and
  immediately apply full masking. System-disabled motion uses final values.

## Verification

`./scripts/publish.ps1 -OutputDirectory 'dist/兔词-交互优化版'` completed with exit 0:

- **954/954 unit tests**, Release build **0 warnings / 0 errors**.
- Actual WPF smoke verifies article prefill followed by replacing `WordBox` in
  the main capture page; the shared `ContextBox` and view model both become
  empty, with no model request.
- Actual focused library refresh records footer height throughout layout
  updates and verifies unchanged dimensions and a zero-thickness button
  surface. File reading, history, selection, themes and sidebar restoration
  continue to pass.
- Production roaming smoke checks the horizontal shader or fallback, animated
  reveal, clear endpoint, release endpoint and immediate hard concealment,
  plus the existing statistics, independent preview and minimum window checks.
- A dedicated 8-by-8-pixel shader probe verifies that horizontal spread is at
  least twice the source width with unchanged vertical spread; zero-strength
  output matches original bounds and pixels within one channel level.
- Mouse, Space, fast reversal, stale key-repeat suppression, replacement during
  reveal, reduced motion, static idle pixels, halo and theme tests pass.
- Existing full WPF checks and real bundled dictionary/Bergamot checks pass.
  The latter are repeated using the actual published layout and fresh isolated
  profiles; offline assets are still validated by size and SHA-256.
- Independent read-only reviews found no material remaining issue, including a
  separate review of the final horizontal shader and its sampling/padding.

Visual evidence:

- `.artifacts/roaming-frost-horizontal/soft-horizontal.png` and
  `soft-horizontal-clear.png`: actual final production control.
- `soft-production.json`: rendering tier 2, PS 2.0 supported, production shader
  present, blur 3.6 / amount 1 / width 24 / grain 0.30.
- `.artifacts/smoke/roaming-meaning-masked.png`,
  `roaming-meaning-transition.png`, `roaming-meaning-held.png`:
  integrated application frames, not a replacement image or mockup.
- `.artifacts/smoke/library-reader-mono.png`: line-icon toolbar and refresh.

The HLSL, fxc rebuild script and compiled PS 2.0 bytecode are versioned together.
The bytecode is embedded as a WPF resource, with binary Git attributes. Users
do not need a shader compiler or an additional shader file.

## Release

Copied into `C:\Private Files\Projects\TUCI\dist\兔词-交互优化版`:

- EXE: **143,593,160 bytes**, SHA-256
  `7CAEBCA3BDBC6AF05286FB9EF644AB446E1C06E77F13546EC1F7996932F6FC62`.
- Complete ZIP: `兔词-交互优化版-win-x64.zip`, **110,430,023 bytes**, 53 entries.
- ZIP SHA-256:
  `9DAB005545CA54F5A7AD23084EE117BF6049D98F78F0AE8D6AECC08DC67E492E`.
- Extracted directory: **227,432,392 bytes**. The executable and all 47 offline
  assets match release metadata; all required ZIP entries are present.

Existing releases, settings, library notes and learning history were preserved.
Run the new folder's executable, or fully extract the ZIP while retaining its
`Assets` directory.
