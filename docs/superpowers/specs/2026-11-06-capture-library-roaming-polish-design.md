# Capture context, library controls and roaming reveal polish

The user requested four changes after using the offline-dictionary release:

1. An article's automatically captured sentence must belong to the selected
   word. Pasting another word in the main capture page must not retain the prior
   article's sentence.
2. Library refresh must not briefly increase layout height or leave a persistent
   black focus rectangle after a mouse click.
3. Back, forward and file-list controls must use consistent native vector icons
   in library/reading and roaming preview toolbars. Keep descriptive tooltips,
   automation names, keyboard access and disabled/history behavior.
4. Roaming's bare blurred meanings should be more opaque, distorted
   like ink spreading horizontally into irregular wood-grain/frosted-glass lines, with a quick smooth transition to
   clear text on hold. Preserve mouse/Space press-and-release behavior, immediate
   concealment on a new card, focus loss or leaving the page, and reduced motion.

## Decisions

- Track automatic article-context ownership in the existing shared capture view
  model. Preserve equivalent casing/spacing and internal spelling correction,
  while a genuinely different user-entered word clears the old automatic
  sentence. Manual context edits are respected. Do not change saved notes.
- Fix the shared Button focus template at the source if its focus border alters
  measurement; use a keyboard-only focus overlay with constant layout. Keep
  normal mouse hover/press feedback. Avoid clearing global focus on every click.
- Reuse native vector icons with about 32 DIP hit areas and 16 DIP strokes;
  retain existing control names and event handlers. Do not substitute images or
  unlabelled font glyphs.
- The user clarified the material direction during implementation: horizontal
  wood-grain-like diffusion, with uneven fine horizontal streaks rather than a
  round/wavy glass distortion. Use a directional horizontal sampling shader,
  a small static row-varying displacement and enough soft blur to conceal text.
  The user reviewed a real horizontal preview and requested finer, softer
  texture. Tune the production shader accordingly: 24 DIP horizontal extent,
  0.30 static grain, 3.6 DIP supporting Gaussian blur with shader enabled;
  unsupported hardware retains the 12 DIP Gaussian mask.
  Use about 120 ms reveal and 100 ms normal release transitions. Shader source,
  compiled WPF ps_2_0 bytecode and a rebuild command belong in the repository.
  Retain a strong Gaussian-only unsupported-hardware fallback. No continuous
  noise animation, border, instruction overlay or layout shift.
- Hard concealment (new text/card, focus loss, navigation, unload) cancels all
  animation before any replacement text can be exposed. Fast repeated holds
  reverse from the current interpolated value. System-disabled animation uses
  immediate final values.

## Acceptance

Capture regression tests cover article prefill followed by replacement input,
equivalent spelling, manual edits and corrected saves. Actual library controls
keep identical measured height before and after mouse focus and refresh; native
navigation icons preserve accessible names and history. Roaming checks cover
blurred/transition/clear visual frames, release, reversal, no new-card exposure,
keyboard and focus/navigation concealment. The previous offline dictionary and
real-model release checks remain required. Deliver a new complete Windows ZIP
without replacing prior releases or touching user settings or notes.
