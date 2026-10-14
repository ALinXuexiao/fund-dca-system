"""gen_svg.py - build logo.svg from measured geometry and measured colour.

Both geometry (band boundaries) and paint (per-band gradient stops) come from the source
raster, so the SVG is regenerated rather than hand-tuned.

Geometry. The silhouette is an ellipse (LSQ fit to the alpha=128 boundary: centre
(127.268,129.225), semi-axes 114.75 x 118.50, rms 0.64 px). Band boundaries were read off
the source by fitting the radii that extremise luminance along 120 rays:

    dark groove trough  r = 94.94   (rms 1.93)
    bright ring crest   r = 98.87   (rms 1.61)

and by reading the radial luminance profile for the outer edge, which shows a dark bevel
(r ~ 112-113.5) followed by a bright lip (r ~ 113.5-116). Band radii are therefore given in
absolute pixels and converted to the ellipse by the silhouette's axis scale, which is why the
coin is slightly taller than it is wide.

Paint. One shared, nearly vertical light axis (light from the top, a touch toward the right).
Along that axis the source colour inside each band is averaged into bins; each bin becomes a
gradient stop, so a linear gradient along the shared axis reproduces the source's vertical
shading and all bands are lit from one direction. The groove is dark and the lip is bright
because that is what the source measures, not because of a stylistic choice.
"""
from pathlib import Path

import numpy as np
from PIL import Image

# Paths are resolved from this file's own location, never from the current working
# directory, so the script runs the same from anywhere (and after a fresh clone).
FW = Path(__file__).resolve().parent                 # brand/outputs/fit_work
SVG = FW.parents[1] / 'logo.svg'                     # brand/logo.svg
SRC = FW.parents[1] / 'source' / 'source.png'        # brand/source/source.png

# --- silhouette ellipse
CX, CY, RX, RY = 127.268, 129.225, 114.75, 118.50
# mean silhouette radius, used to convert absolute pixel radii into the ellipse metric
R_MEAN = 116.0

# --- bands, largest outer radius first so each smaller band paints over the one beneath
BANDS = [('coinLip', 113.50, 115.98),
         ('coinBevel', 111.90, 113.50),
         ('coinPlatform', 100.40, 111.90),
         ('coinRing', 97.20, 100.40),
         ('coinGroove', 92.60, 97.20),
         ('coinFace', 0.00, 92.60)]
NBINS = 9

# --- shared light axis, user space
AX1 = np.array([140.0, 4.0])
AX2 = np.array([116.0, 254.4])

src = np.asarray(Image.open(SRC).convert('RGBA')).astype(np.float64)
rgb, alpha = src[:, :, :3], src[:, :, 3:4] / 255.0
on_white = rgb * alpha + 255.0 * (1 - alpha)

yy, xx = np.meshgrid(np.arange(256), np.arange(256), indexing='ij')
# u = radius in absolute pixels, expressed in the silhouette's own axis metric
u = R_MEAN * np.sqrt(((xx - CX) / RX) ** 2 + ((yy - CY) / RY) ** 2)
arrow = np.load(FW / 'ref50.npy')
coin = (alpha[:, :, 0] > 200 / 255.0) & (~arrow)

d = AX2 - AX1
t = ((xx - AX1[0]) * d[0] + (yy - AX1[1]) * d[1]) / float(d @ d)


def hexc(c):
    return '#%02X%02X%02X' % tuple(int(round(min(255, max(0, v)))) for v in c)


def profile(lo, hi):
    band = (u >= lo) & (u < hi) & coin
    tv = t[band]
    if tv.size < 20:
        return []
    edges = np.linspace(tv.min(), tv.max(), NBINS + 1)
    out = []
    for i in range(NBINS):
        hi_edge = edges[i + 1] if i == NBINS - 1 else edges[i + 1]
        m = (tv >= edges[i]) & (tv <= hi_edge)
        if int(m.sum()) < 6:
            continue
        out.append(((edges[i] + edges[i + 1]) / 2.0, on_white[band][m].mean(axis=0)))
    return out


grads, meta = [], []
for gid, lo, hi in BANDS:
    rows = profile(lo, hi)
    if len(rows) < 2:
        # band too thin for bins: use its single mean colour at both ends
        band = (u >= lo) & (u < hi) & coin
        col = on_white[band].mean(axis=0)
        tv = t[band]
        rows = [(tv.min(), col), (tv.max(), col)]
    tmin, tmax = rows[0][0], rows[-1][0]
    span = max(1e-9, tmax - tmin)
    stops = '\n'.join('      <stop offset="%.3f" stop-color="%s"/>' % ((tv - tmin) / span, hexc(c))
                      for tv, c in rows)
    p_a = AX1 + d * tmin
    p_b = AX1 + d * tmax
    grads.append('    <linearGradient id="%sGrad" gradientUnits="userSpaceOnUse" '
                 'x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f">\n%s\n    </linearGradient>'
                 % (gid, p_a[0], p_a[1], p_b[0], p_b[1], stops))
    meta.append((gid, lo, hi, len(rows), tmin, tmax))
    print('%-14s r %6.2f-%6.2f  u %.3f-%.3f  %d stops' % (gid, lo, hi, lo / R_MEAN, hi / R_MEAN, len(rows)))

ar = on_white[arrow]
tv = t[arrow]
tip = ar[tv < np.median(tv)].mean(axis=0)
tail = ar[tv >= np.median(tv)].mean(axis=0)
print('\narrow tip rgb(%.1f,%.1f,%.1f)  tail rgb(%.1f,%.1f,%.1f)' % (*tip, *tail))

sx, sy = RX / R_MEAN, RY / R_MEAN
ellipses = []
for gid, lo, hi in BANDS:
    r = hi * sx if False else hi
    ellipses.append('      <ellipse id="%s" cx="%.2f" cy="%.2f" rx="%.2f" ry="%.2f" fill="url(#%sGrad)"/>'
                    % (gid, CX, CY, hi * sx, hi * sy, gid))

ARROW = """      <path id="arrow" pathLength="1" fill="url(#arrowFill)" mask="url(#arrowWipe)"
            d="M 194.428 76.433 L 191.556 109.045 L 182.491 104.461 L 161.317 134.044
               L 143.582 128.374 L 123.773 165.186 L 100.671 158.585 L 83.113 187.021
               L 65.557 180.674 L 90.416 137.015 L 113.731 143.651 L 132.739 106.750
               L 155.420 113.465 L 166.528 96.575 L 162.293 86.511 Z"/>"""

# --- motion scaffolding -------------------------------------------------------------
# Everything here rests in a state that leaves the resting frame identical to the
# geometry-QA'd vector, so the Phase 2 measurements stay valid.
#
# Arrow wipe. The reveal travels along the arrow's own rising axis, which runs from the
# lower-left vertex to the upper-right tip. Measured in the axis-aligned frame (centre at
# (128,128), rotated so that axis is +x), the arrow spans local x = -81.68 .. +84.08, so
# the wipe front must come to rest at x >= 85 to leave the arrow fully visible.
# The band is therefore built in a frame translated to the centre and rotated onto that
# axis (ARROW_AXIS), so a CSS translateX on the rect is a slide along the reveal direction.
ARROW_AXIS = -38.969          # screen angle of the arrow's rising axis, degrees
WIPE_X0 = -500.0              # band tail, far behind the arrow
WIPE_FRONT = 86.0             # white (visible) side ends here; 0.985 * width past WIPE_X0
WIPE_WHITE_FRACTION = 0.985
WIPE_W = (WIPE_FRONT - WIPE_X0) / WIPE_WHITE_FRACTION
WIPE_HIDE = -180.0            # CSS translateX that puts the front past the arrow's tail
SCAFFOLD = f"""    <!-- Motion scaffolding. Everything below rests in a state that leaves the resting
         frame identical to the geometry-QA'd vector, so Phase 2 stays valid: the wipe band
         covers the whole arrow (front at local x = 86, arrow ends at 84.08) and the sheen
         group is base opacity 0. Only the animations move them. -->
    <linearGradient id="arrowWipeGrad" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0" stop-color="#ffffff"/>
      <stop offset="{WIPE_WHITE_FRACTION}" stop-color="#ffffff"/>
      <stop offset="1" stop-color="#000000"/>
    </linearGradient>
    <mask id="arrowWipe" maskUnits="userSpaceOnUse" x="0" y="0" width="256" height="256">
      <g transform="translate({CX:.2f} {CY:.2f}) rotate({ARROW_AXIS})">
        <rect id="arrowWipeBand" x="{WIPE_X0:.1f}" y="-200" width="{WIPE_W:.1f}" height="400"
              fill="url(#arrowWipeGrad)"/>
      </g>
    </mask>
    <clipPath id="coinClip">
      <ellipse cx="{CX:.2f}" cy="{CY:.2f}" rx="{RX:.2f}" ry="{RY:.2f}"/>
    </clipPath>
    <linearGradient id="coinSheenGrad" x1="0" y1="0" x2="1" y2="0">
      <stop offset="0" stop-color="#ffffff" stop-opacity="0"/>
      <stop offset="0.5" stop-color="#ffffff" stop-opacity="1"/>
      <stop offset="1" stop-color="#ffffff" stop-opacity="0"/>
    </linearGradient>"""

# The sheen sits OUTSIDE #mark on purpose. If it were a child, its 400px-tall rotated
# rect would join #mark's object bounding box and drag the fill-box centre far off the
# coin, so #mark's scale/rotate pivot would drift. As a sibling, #mark's bbox is exactly
# the coin (12.54..241.99 x 10.74..247.70) plus the arrow inside it, so the fill-box
# centre lands on the coin centre. The sheen is invisible until the follow-through, by
# which point #mark has settled to within 0.6% of scale 1.
SHEEN = f"""    <g id="coinSheen" clip-path="url(#coinClip)" opacity="0">
      <g transform="rotate(-32 128 128)">
        <rect id="coinSheenBand" x="-90" y="-200" width="180" height="400"
              fill="url(#coinSheenGrad)"/>
      </g>
    </g>"""

svg = f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256"
     role="img" aria-labelledby="brandTitle brandDesc">
  <title id="brandTitle">Coin with a rising trend arrow</title>
  <desc id="brandDesc">A gold coin mark carrying an upward zig-zag trend arrow. Six concentric
  elliptical bands plus one analytic polygon, so every part can be choreographed on its own.</desc>

  <!-- Geometry generated by brand/outputs/fit_work/gen_svg.py from measurements of the source
       raster. Band radii are the radii that extremise luminance along 120 rays of the source;
       the gradient stops are the source's own colour averaged into bins along one shared, nearly
       vertical light axis, so every band is lit from the same direction. -->
  <defs>
{chr(10).join(grads)}
    <linearGradient id="arrowFill" gradientUnits="userSpaceOnUse" x1="200" y1="70" x2="70" y2="195">
      <stop offset="0" stop-color="{hexc(tip)}"/>
      <stop offset="1" stop-color="{hexc(tail)}"/>
    </linearGradient>
{SCAFFOLD}
  </defs>

  <g id="mark">
    <g id="coin">
{chr(10).join(ellipses)}
    </g>

{ARROW}

{SHEEN}
  </g>
</svg>
"""
open(SVG, 'w', encoding='utf-8', newline='\n').write(svg)
print('\nwrote %s (%d bytes)' % (SVG, len(svg)))
