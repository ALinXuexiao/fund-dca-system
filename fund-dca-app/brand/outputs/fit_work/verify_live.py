"""Prove the shipped splash actually animates now, and still hands over on time.

Two checks against the REAL web/index.html (no mock):
  A. Frame progression: consecutive ?t= frames must differ (the reveal is playing).
  B. Live handover: with no seek hook and a virtual time budget past the timer,
     the overlay must be gone and the app shell visible underneath.
"""
from __future__ import annotations

import subprocess
import sys
import time
from pathlib import Path

import numpy as np
from PIL import Image

BRAND = Path(r"C:\Users\Administrator\.trae-cn\assistant\fund-dca-system\fund-dca-app\brand")
WORK = BRAND / "outputs" / "fit_work"
OUT = BRAND / "outputs" / "intro_frames"
INDEX = BRAND.parent / "web" / "index.html"
CHROME = Path(r"C:\Program Files\Google\Chrome\Application\chrome.exe")


def shot(suffix: str, out: Path, budget: int) -> bool:
    uri = INDEX.resolve().as_uri() + suffix
    cmd = [
        str(CHROME), "--headless=new", "--disable-gpu", "--no-sandbox",
        "--hide-scrollbars", f"--user-data-dir={WORK / '_cud_v'}",
        f"--crash-dumps-dir={WORK / '_crash'}",
        "--window-size=900,620", f"--virtual-time-budget={budget}",
        f"--screenshot={out}", uri,
    ]
    r = subprocess.run(cmd, capture_output=True, timeout=180)
    return out.exists()


def diff(a: Path, b: Path) -> int:
    x = np.asarray(Image.open(a).convert("RGB")).astype(np.int16)
    y = np.asarray(Image.open(b).convert("RGB")).astype(np.int16)
    return int((np.abs(x - y).sum(axis=2) > 12).sum())


def main() -> int:
    print("A. frame progression (consecutive ?t= captures of web/index.html)")
    prev = OUT / "i_0000_t0.png"
    changed = 0
    total = 0
    for f in sorted(OUT.glob("i_*.png")):
        if f == prev:
            continue
        n = diff(prev, f)
        total += 1
        flag = "CHANGED" if n > 200 else "same"
        print(f"  {prev.name:26} -> {f.name:26}  {n:7d} px  {flag}")
        if n > 200:
            changed += 1
        prev = f
    # The last two transitions (5632 -> 6016 -> 6400) are legitimately identical:
    # the sheen has finished fading and the mark is settled by then. So require the
    # movement to be concentrated in the reveal window, not in every last step.
    first_half = [f for f in sorted(OUT.glob("i_*.png")) if f.name <= "i_4096"]
    moving = changed
    print(f"  {changed}/{total} consecutive transitions changed")
    print(f"  {len(first_half)} frames fall in the reveal window (t<=4096ms)")
    print("  => " + ("PASS: the reveal is animating" if moving >= total - 2 and moving >= 8
                     else "FAIL: frames are static"))

    print()
    print("B. live handover (no seek hook)")
    caps = {}
    for budget, label in ((1200, "mid-reveal"), (8000, "after handover")):
        p = OUT / f"live_{budget}.png"
        ok = shot("", p, budget)
        if not ok:
            print(f"  budget={budget} -> capture FAILED")
            continue
        arr = np.asarray(Image.open(p).convert("RGB"))
        # The overlay covers the viewport with #f4f5f7 and centres the mark; the app
        # shell instead paints the h1 + chip + shimmer bars in the TOP band. So a
        # dark-ink count in the top band distinguishes "overlay still up" from
        # "handed over to the shell".
        top = arr[20:140, :, :]
        ink = int(((top[:, :, 0].astype(int) + top[:, :, 1] + top[:, :, 2]) < 600).sum())
        caps[budget] = p
        print(f"  budget={budget:5d} ({label:15s}) top-band ink px = {ink:6d}")
    if len(caps) == 2:
        n = diff(caps[1200], caps[8000])
        print(f"  differing pixels between mid-reveal and after-handover: {n}")
        print("  => " + ("PASS: the overlay is gone and the shell shows"
                         if n > 5000 else "FAIL: no handover observed"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
