"""Render icon_uninstall.svg -> icon_uninstall.png (512) + icon_uninstall.ico.

The repo toolchain has no SVG rasterizer (no ImageMagick/Inkscape/cairo), so
this script replicates icon_uninstall.svg's layer stack exactly with
PIL + numpy: same geometry, gradients and stack order as the SVG, rendered at
1024 px (2x supersample) and downscaled. Run from the repo root:

    python tools/render-uninstall-icon.py

Layers mirror icon_v2.svg's "liquid glass" build (see that file's comments);
only the palette is inverted (blue disc, silver glyph) and the glyph is the
MDI close-thick cross.
"""
from __future__ import annotations

import numpy as np
from PIL import Image, ImageDraw

S = 2          # supersample factor over the 512 design space
N = 512 * S    # render canvas

# Coordinate grids in DESIGN units (512-space), pixel centers.
yy, xx = np.mgrid[0:N, 0:N].astype(np.float64)
xx = (xx + 0.5) / S
yy = (yy + 0.5) / S

def hexrgb(h: str) -> np.ndarray:
    return np.array([int(h[i:i + 2], 16) for i in (1, 3, 5)], dtype=np.float64)

def over(base_rgb, base_a, rgb, a):
    """Source-over composite (premultiplied math on straight buffers)."""
    out_a = a + base_a * (1 - a)
    safe = np.where(out_a == 0, 1, out_a)
    out_rgb = (rgb * a[..., None] + base_rgb * (base_a * (1 - a))[..., None]) / safe[..., None]
    return out_rgb, out_a

# --- disc geometry -----------------------------------------------------------
CX, CY, R = 256.0, 256.0, 248.0
dist = np.hypot(xx - CX, yy - CY)
disc_cov = np.clip((R - dist) * S + 0.5, 0.0, 1.0)      # antialiased silhouette
disc_mask = disc_cov > 0

# --- 1. body: radial light blue -> brand blue --------------------------------
# objectBoundingBox radial on the r=248 circle: cx 50%, cy 35%, r 65% of the
# 496-px bbox -> center (256, 181.6), radius 322.4 in design units.
body_c = (256.0, 8.0 + 0.35 * 496.0)
body_r = 0.65 * 496.0
t = np.clip(np.hypot(xx - body_c[0], yy - body_c[1]) / body_r, 0.0, 1.0)
c0, c1 = hexrgb("#4f86ff"), hexrgb("#1652e2")
rgb = c0[None, None, :] * (1 - t)[..., None] + c1[None, None, :] * t[..., None]
alpha = disc_cov.copy()

# --- 2. glyph: MDI close-thick, translate(64 64) scale(16), silver -----------
GLYPH = [(20, 6.91), (17.09, 4), (12, 9.09), (6.91, 4), (4, 6.91), (9.09, 12),
         (4, 17.09), (6.91, 20), (12, 14.91), (17.09, 20), (20, 17.09), (14.91, 12)]
pts = [((x * 16 + 64) * S, (y * 16 + 64) * S) for x, y in GLYPH]
gimg = Image.new("L", (N, N), 0)
ImageDraw.Draw(gimg).polygon(pts, fill=255)
ga = np.asarray(gimg, dtype=np.float64) / 255.0
rgb, alpha = over(rgb, alpha, hexrgb("#e9edf5")[None, None, :], ga * disc_cov)

# --- gloss layers (all clipped to the disc) ----------------------------------
def ellipse_norm(cx, cy, rx, ry):
    return np.hypot((xx - cx) / rx, (yy - cy) / ry)

# 3. lower darken band: rect y 281..504, vertical #001e3c 0 -> 0.31
in_rect = (yy >= 281) & (yy <= 504)
a = np.where(in_rect, 0.31 * np.clip((yy - 281) / 223.0, 0, 1), 0.0) * disc_cov
rgb, alpha = over(rgb, alpha, hexrgb("#001e3c")[None, None, :], a)

# 4. upper sheen ellipse c(256,161) r(213,136): vertical white 0.55 -> 0
d = ellipse_norm(256, 161, 213, 136)
tv = np.clip((yy - 25.0) / 272.0, 0, 1)                  # ellipse bbox y 25..297
a = np.where(d <= 1, 0.55 * (1 - tv), 0.0) * disc_cov
rgb, alpha = over(rgb, alpha, np.full((1, 1, 3), 255.0), a)

# 5. specular catchlight ellipse c(194,84) r(42,22): radial white 1 -> 0
d = ellipse_norm(194, 84, 42, 22)
a = np.clip(1 - d, 0, 1) * disc_cov
rgb, alpha = over(rgb, alpha, np.full((1, 1, 3), 255.0), a)

# 6. bottom bounce ellipse c(256,430) r(124,37): radial white 0.4 -> 0
d = ellipse_norm(256, 430, 124, 37)
a = 0.4 * np.clip(1 - d, 0, 1) * disc_cov
rgb, alpha = over(rgb, alpha, np.full((1, 1, 3), 255.0), a)

# 7. inner rim refraction: radial #0c1f4a, 0 @ offset .86 -> 0.5 @ 1.0
tr = dist / R
a = np.where(tr >= 0.86, 0.5 * np.clip((tr - 0.86) / 0.14, 0, 1), 0.0) * disc_cov
rgb, alpha = over(rgb, alpha, hexrgb("#0c1f4a")[None, None, :], a)

# 8. outer crisp silhouette: r=248 stroke, width 2, black 22%
a = np.clip((1.0 - np.abs(dist - R)) * S + 0.5, 0, 1) * 0.22
rgb, alpha = over(rgb, alpha, np.zeros((1, 1, 3)), a)

# --- write outputs ------------------------------------------------------------
out = np.dstack([np.clip(rgb, 0, 255).astype(np.uint8),
                 (np.clip(alpha, 0, 1) * 255).astype(np.uint8)[..., None]])
img = Image.fromarray(out, "RGBA").resize((512, 512), Image.LANCZOS)
img.save("icon_uninstall.png")

sizes = [256, 128, 64, 48, 32, 24, 20, 16]
frames = [img.resize((s, s), Image.LANCZOS) for s in sizes]
frames[0].save("icon_uninstall.ico", format="ICO",
               append_images=frames[1:], sizes=[(s, s) for s in sizes])
print("wrote icon_uninstall.png (512) + icon_uninstall.ico", sizes)
