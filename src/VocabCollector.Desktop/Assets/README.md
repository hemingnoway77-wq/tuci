# Application artwork

The icon is the supplied rabbit-and-magnifier mark: black strokes forming two ears, a ring head, and a diagonal handle on a transparent canvas. The raster master is `app-icon-source.png` (1254 × 1254), including transparency inside the ring.

| Role | Color |
| --- | --- |
| Background and ring interior | Transparent |
| Rabbit strokes | `#000000` |

`scripts/create-icon.ps1` scales the master with WPF high-quality resampling and writes:

- `app.ico` — 32-bit PNG representations at 16, 24, 32, 48, 64, 128, and 256 pixels; embedded in the executable and used for the window, taskbar and title-bar icon.
- `app-icon-256.png` — embedded as the WPF resource used by the header mark.
- `app-icon.png` — 512-pixel preview.

Regenerate the raster assets from the master:

```powershell
powershell.exe -NoProfile -STA -File scripts/create-icon.ps1
```

The script preserves alpha transparency, writes the ICO directory, produces the previews, and verifies each embedded PNG's dimensions.
