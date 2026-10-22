# Application artwork

The icon is original vector artwork: a vocabulary page, a bookmark, a folded corner, and a geometric lowercase **a**. It uses the supplied reference only for its warm pink, cream, and pale yellow color family.

| Role | Color |
| --- | --- |
| Pink tile | `#FBD1DF` → `#F3ABC5` |
| Cream paper | `#FFF9EB` |
| Pale yellow folded corner | `#FFE28A` |
| Plum lettering | `#563244` |
| Rose bookmark | `#DE6F98` |
| Quiet page lines | `#E9BAC8` |

`AppIcon.xaml` supplies the scalable WPF `DrawingImage` resource `BrandMark`. `app-icon.svg` is the corresponding vector master for other uses. Both have a transparent 512 × 512 canvas.

`app.ico` contains 32-bit PNG representations at 16, 24, 32, 48, 64, 128, and 256 pixels. The standalone PNG previews are 256 and 512 pixels.

Regenerate the raster assets from the XAML source:

```powershell
powershell.exe -NoProfile -STA -File scripts/create-icon.ps1
```

The script supersamples the drawing, writes the ICO directory, and verifies each embedded PNG's dimensions. When changing the artwork, update the matching SVG geometry as well.
