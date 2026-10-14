#!/usr/bin/env python
"""Decisive easing probe.

The skill requires proving that the keyframe timing functions are actually APPLIED,
because Chromium silently drops `animation-timing-function: var(--token)` inside
@keyframes and falls back to the animation's base timing (usually linear). This script
measures the rendered geometry over time and compares it against the DESIGNED cubic-bezier
and against a LINEAR reference. If the measurement tracks linear, the easing was dropped.

Two independent probes:
  1. coin scale  — opaque bounding-box width, over the action window 320..768 ms
                   designed: cubic-bezier(0,0,0.2,1) between scale 0.845 and 1.015
  2. arrow wipe  — count of light pixels inside the coin face, over 400..1152 ms
                   designed: cubic-bezier(0.4,0,0.2,1) translateX -196 -> 0
"""
from __future__ import annotations

import subprocess
import time
from pathlib import Path

import numpy as np
from PIL import Image

BRAND = Path(r"C:\Users\Administrator\.trae-cn\assistant\fund-dca-system\fund-dca-app\brand")
WORK = BRAND / "outputs" / "fit_work"
HTML = BRAND / "logo_motion.html"
DONE = WORK / "_j_done.txt"
W = H = 420


def shoot(query: str, out_png: Path) -> None:
    if DONE.exists():
        DONE.unlink()
    (WORK / "_j_html.txt").write_text(HTML.as_uri() + query, encoding="utf-8")
    (WORK / "_j_out.txt").write_text(str(out_png), encoding="utf-8")
    (WORK / "_j_w.txt").write_text(str(W), encoding="utf-8")
    (WORK / "_j_h.txt").write_text(str(H), encoding="utf-8")
    subprocess.Popen(["explorer.exe", str(WORK / "shot_v.bat")])
    deadline = time.time() + 120
    while time.time() < deadline and not DONE.exists():
        time.sleep(0.25)
    time.sleep(0.3)
    if not out_png.exists():
        raise RuntimeError(f"no PNG for {query}")


def bezier(p1x, p1y, p2x, p2y, t: float) -> float:
    """CSS cubic-bezier easing: solve x(u)=t for u, then return y(u)."""
    lo, hi = 0.0, 1.0
    u = t
    for _ in range(60):
        x = 3 * u * (1 - u) ** 2 * p1x + 3 * u * u * (1 - u) * p2x + u ** 3
        if x < t:
            lo = u
        else:
            hi = u
        u = (lo + hi) / 2
    return 3 * u * (1 - u) ** 2 * p1y + 3 * u * u * (1 - u) * p2y + u ** 3


def measure(png: Path):
    a = np.asarray(Image.open(png).convert("RGB")).astype(np.int16)
    s = a.sum(axis=2)
    # Coin: opaque extent (everything that is not near-white background).
    nonwhite = s < 720
    cols = np.where(nonwhite.any(axis=0))[0]
    width = int(cols.max() - cols.min() + 1) if cols.size else 0
    # Arrow ink, measured strictly inside the coin FACE (r < 88 of 91.6) so the bright
    # ring/bevel highlight cannot be mistaken for the arrow. The background is excluded
    # by the sum<750 test (pure white is 765, the composited arrow is ~717).
    mn = a.min(axis=2)
    yy, xx = np.mgrid[0:a.shape[0], 0:a.shape[1]]
    scale = (W * 2) / 256.0
    cx, cy = 127.27 * scale, 129.22 * scale
    inside = ((xx - cx) ** 2 + (yy - cy) ** 2) < (88 * scale) ** 2
    ink = int(((mn > 185) & (s < 750) & inside).sum())
    return width, ink


def report(name, times, measured, lo_t, hi_t, v0, v1, ease, invert=False):
    print(f"\n=== {name} ===")
    print(f"  window {lo_t}..{hi_t} ms   value {v0} -> {v1}   ease {ease}")
    print(f"  {'t(ms)':>6} {'measured':>10} {'design_p':>9} {'linear_p':>9} {'verdict':>8}")
    lin_hits = 0
    bez_hits = 0
    for t in times:
        m = measured[t]
        raw = (m - v0) / (v1 - v0) if v1 != v0 else 0.0
        p = 1 - raw if invert else raw
        p = max(0.0, min(1.0, p))
        frac = (t - lo_t) / (hi_t - lo_t)
        frac = max(0.0, min(1.0, frac))
        d = bezier(*ease, frac)
        dl = abs(p - d)
        ll = abs(p - frac)
        verdict = "bezier" if dl < ll else "LINEAR"
        bez_hits += dl < ll
        lin_hits += ll < dl
        print(f"  {t:>6} {m:>10} {d:>9.3f} {frac:>9.3f} {verdict:>8}")
    print(f"  -> closer to design bezier: {bez_hits}/{len(times)}; closer to linear: {lin_hits}/{len(times)}")
    print(f"  -> {'EASING APPLIED (not linear)' if bez_hits > lin_hits else 'EASING DROPPED (linear!)'}")


def main() -> int:
    cache = {}
    times = sorted(set(list(range(300, 800, 40)) + list(range(400, 1200, 60))
                       + [0, 320, 400, 768, 1152, 1600]))
    for t in times:
        p = WORK / f"_e_{t}.png"
        shoot(f"?t={t}", p)
        cache[t] = p
    widths = {}
    inks = {}
    for t in times:
        w, ink = measure(cache[t])
        widths[t] = w
        inks[t] = ink
    print("measured coin widths:", {t: widths[t] for t in times if t <= 800})

    coin_times = [t for t in times if 320 <= t <= 768]
    report("coin scale easing (action)", coin_times, widths,
           320, 768, widths[320], widths[768], (0.0, 0.0, 0.2, 1.0))

    wipe_times = [t for t in times if 400 <= t <= 1152]
    report("arrow wipe easing (secondary)", wipe_times, inks,
           400, 1152, inks[400], inks[1152], (0.4, 0.0, 0.2, 1.0), invert=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
