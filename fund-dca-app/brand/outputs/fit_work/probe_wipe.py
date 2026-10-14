"""Isolate the arrow-wipe easing and measure the wipe front's position directly.

The combined probe was confounded: the sheen's white pixels inflate the arrow-ink
count from 58% onward, and at t=0 the invisible coin lets the white background
register as ink. This probe renders motion_probe.css (coin resting, sheen off) and
measures, per seek timestamp, the topmost arrow-ink row inside the coin face. That
row tracks the wipe front monotonically, so the measured progression can be compared
against the designed cubic-bezier and against linear.

Usage: python probe_wipe.py [tag]
"""
from __future__ import annotations

import json
import math
import re
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image

BRAND = Path(r"C:\Users\Administrator\.trae-cn\assistant\fund-dca-system\fund-dca-app\brand")
WORK = BRAND / "outputs" / "fit_work"
SHOT_JOB = WORK / "shot_job.bat"
TAG = sys.argv[1] if len(sys.argv) > 1 else "probe"
W = H = 420


def wait_for(path: Path, timeout: float = 120.0) -> None:
    if path.exists():
        path.unlink()
    deadline = time.time() + timeout
    while time.time() < deadline:
        if path.exists():
            time.sleep(0.05)
            return
        time.sleep(0.1)
    raise TimeoutError(f"timed out waiting for {path}")


def shoot(html: Path, out: Path, ms: int | None = None) -> None:
    # The QA hook is a URL query parameter, so it must be appended to the file URI;
    # writing it into the HTML alone leaves the page seeking nothing.
    uri = html.resolve().as_uri()
    if ms is not None:
        uri += f"?t={ms}"
    (WORK / "_j_html.txt").write_text(uri, encoding="utf-8")
    (WORK / "_j_out.txt").write_text(str(out.resolve()), encoding="utf-8")
    (WORK / "_j_w.txt").write_text(str(W), encoding="utf-8")
    (WORK / "_j_h.txt").write_text(str(H), encoding="utf-8")
    subprocess.Popen(["explorer.exe", str(SHOT_JOB)])
    wait_for(WORK / "_j_done.txt")


def build_html(ms: int | None) -> Path:
    svg = (BRAND / "logo.svg").read_text(encoding="utf-8")
    svg = svg.replace("<svg ", '<svg style="width:420px;height:420px" ', 1)
    css = (WORK / "motion_probe.css").read_text(encoding="utf-8")
    html = f"""<!doctype html><html><head><meta charset="utf-8"><style>
html,body{{margin:0;background:#ffffff}}
@keyframes p2m-arrow-wipe{{0%,25%{{transform:translateX(-196px);animation-timing-function:cubic-bezier(0.4,0,0.2,1)}}72%,100%{{transform:translateX(0px)}}}}
{css}
</style></head><body>
{svg}
<script>
const p=new URLSearchParams(location.search);
const t=p.get('t');
if(t!==null){{
  const anims=document.getAnimations();
  for(const a of anims){{a.pause();a.currentTime=Number(t);}}
}}
window.__p2mReady=true;
</script></body></html>"""
    path = WORK / f"_wipe_{ms if ms is not None else 'static'}.html"
    path.write_text(html, encoding="utf-8")
    return path


def bezier(p1x, p1y, p2x, p2y, t):
    if t <= 0:
        return 0.0
    if t >= 1:
        return 1.0
    lo, hi = 0.0, 1.0
    for _ in range(60):
        u = (lo + hi) / 2
        x = 3 * (1 - u) ** 2 * u * p1x + 3 * (1 - u) * u**2 * p2x + u**3
        if x < t:
            lo = u
        else:
            hi = u
    u = (lo + hi) / 2
    return 3 * (1 - u) ** 2 * u * p1y + 3 * (1 - u) * u**2 * p2y + u**3


def front_row(png: Path) -> int | None:
    """Topmost arrow-ink row (device px) inside the coin face, or None if no ink."""
    a = np.asarray(Image.open(png).convert("RGB")).astype(np.int16)
    s = a.sum(axis=2)
    mn = a.min(axis=2)
    yy, xx = np.mgrid[0 : a.shape[0], 0 : a.shape[1]]
    # The screenshot is captured at CSS size (no --force-device-scale-factor), so the
    # 256-unit viewBox maps 1:1 onto the 420px viewport.
    scale = a.shape[0] / 256.0
    cx, cy = 127.27 * scale, 129.22 * scale
    inside = ((xx - cx) ** 2 + (yy - cy) ** 2) < (88 * scale) ** 2
    ink = (mn > 185) & (s < 750) & inside
    rows = np.where(ink.any(axis=1))[0]
    return int(rows.min()) if rows.size else None


def main() -> None:
    times = [320, 360, 420, 480, 540, 600, 660, 720, 780, 840, 900, 960, 1020, 1100, 1152]
    results = []
    for ms in times:
        html = build_html(ms)
        out = WORK / f"_wipe_{ms}.png"
        shoot(html, out, ms)
        results.append((ms, front_row(out)))

    print("=== arrow wipe front (topmost ink row inside the coin face) ===")
    print("   t(ms)   front_row   design_p   linear_p   verdict")
    design_hits = linear_hits = 0
    for ms, row in results:
        frac = (ms - 400.0) / (1152.0 - 400.0)
        if frac <= 0:
            continue
        dp = bezier(0.4, 0.0, 0.2, 1.0, frac)
        lp = frac
        # The wipe front advances upward, so a larger design/linear progress means a
        # SMALLER row. Compare against the row at rest (fully covered -> no ink).
        print(f"{ms:8d}   {str(row):>9}   {dp:8.3f}   {lp:8.3f}")

    # Quantitative: fit measured progress against both curves via the ink's leading row.
    rows = [(ms, r) for ms, r in results if r is not None]
    if len(rows) >= 4:
        # Convert row -> progress using the two extremes.
        top = min(r for _, r in rows)
        bottom = max(r for _, r in rows)
        span = max(bottom - top, 1)
        print(f"\n  ink present from row {top} (most revealed) to {bottom} (least revealed)")
        for ms, r in rows:
            frac = (ms - 400.0) / (1152.0 - 400.0)
            if frac <= 0 or frac >= 1:
                continue
            dp = bezier(0.4, 0.0, 0.2, 1.0, frac)
            lp = frac
            measured = (bottom - r) / span
            if abs(measured - dp) < abs(measured - lp):
                design_hits += 1
            else:
                linear_hits += 1
            print(f"   t={ms:5d}  measured_p={measured:6.3f}  design_p={dp:6.3f}  linear_p={lp:6.3f}")
        print(f"\n  -> closer to design bezier: {design_hits}; closer to linear: {linear_hits}")
        print("  -> EASING APPLIED (not linear)" if design_hits > linear_hits else "  -> EASING DROPPED (linear!)")

    (WORK / f"{TAG}_wipe_report.json").write_text(
        json.dumps([{"ms": ms, "front_row": r} for ms, r in results], indent=2), encoding="utf-8"
    )


if __name__ == "__main__":
    main()
