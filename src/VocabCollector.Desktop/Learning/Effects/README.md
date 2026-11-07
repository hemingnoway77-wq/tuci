`InkDistortion.hlsl` is a static PS 2.0 horizontal diffusion field. Nine samples
spread the live text left and right with subtly varying lengths for each fine
row. It has no vertical refraction, time/noise animation, external texture,
font raster asset, package dependency or network resource.

Rebuild the committed `InkDistortion.ps` with the Windows SDK HLSL compiler:

```powershell
./Build-InkDistortion.ps1 -FxcPath 'C:/Program Files (x86)/Windows Kits/10/bin/10.0.26100.0/x64/fxc.exe'
```

The shader target is `ps_2_0`, entry point `Main`, optimized with `/O3`.
The WPF project embeds the bytecode as a Resource, including single-file
publications; no shader compiler or external `.ps` file is needed at runtime.
`.gitattributes` preserves bytecode without line-ending conversion.

HoldToReveal uses Gaussian radius 3.6, normalized Amount 1, SmearWidth 24 DIPs
and Grain .30 on a device that advertises PS 2.0 support and a nonzero rendering
tier. Otherwise a Gaussian radius 12 mask remains in place. Amount 0 restores
the original sample position without moving text or leaving grain behind.
Press/release animate only blur radius and horizontal diffusion strength; exposure
changes and every safety dismissal immediately restore the full mask.
