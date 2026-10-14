"""Decisive A/B: does the Windows client-area-animation flag actually control
`prefers-reduced-motion` in Chromium, and does it gate the intro's animations?

Reads SPI_GETCLIENTAREAANIMATION (0x1042), then for each value runs headless
Chrome over a probe copy of web/index.html and reports:
  - matchMedia('(prefers-reduced-motion: reduce)').matches
  - document.getAnimations().length and their names

Always restores the original value. Also reports whether this is an RDP session,
because Windows disables client-area animation in remote sessions.
"""
from __future__ import annotations

import ctypes
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

BRAND = Path(r"C:\Users\Administrator\.trae-cn\assistant\fund-dca-system\fund-dca-app\brand")
WORK = BRAND / "outputs" / "fit_work"
INDEX = BRAND.parent / "web" / "index.html"
CHROME = Path(r"C:\Program Files\Google\Chrome\Application\chrome.exe")

SPI_GET = 0x1042
SPI_SET = 0x1043
SPIF = 0x01 | 0x02  # UPDATEINIFILE | SENDCHANGE
SM_REMOTESESSION = 0x1000

u32 = ctypes.windll.user32

PROBE = """
<script>
(function(){
  var out = {
    reduce: matchMedia('(prefers-reduced-motion: reduce)').matches,
    noPref: matchMedia('(prefers-reduced-motion: no-preference)').matches,
    anims: document.getAnimations().map(function(a){
      var t = a.effect && a.effect.target;
      return (a.animationName || '?') + '@' + (t ? (t.id || t.tagName) : '?');
    })
  };
  document.documentElement.setAttribute('data-media', JSON.stringify(out));
})();
</script>
</body>"""


def get_anim() -> int:
    v = ctypes.c_int(0)
    u32.SystemParametersInfoW(SPI_GET, 0, ctypes.byref(v), 0)
    return v.value


def set_anim(on: int) -> bool:
    return bool(u32.SystemParametersInfoW(SPI_SET, 0, ctypes.c_void_p(on), SPIF))


def probe() -> dict | None:
    src = INDEX.read_text(encoding="utf-8")
    tmp = WORK / "_ab_probe.html"
    tmp.write_text(src.replace("</body>", PROBE, 1), encoding="utf-8")
    out = WORK / "_ab_dom.txt"
    cmd = [
        str(CHROME), "--headless=new", "--disable-gpu", "--no-sandbox",
        "--hide-scrollbars", f"--user-data-dir={WORK / '_cud_ab'}",
        f"--crash-dumps-dir={WORK / '_crash'}",
        "--window-size=900,620", "--virtual-time-budget=500",
        "--dump-dom", tmp.resolve().as_uri(),
    ]
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=90)
    except Exception as exc:  # noqa: BLE001
        print(f"  chrome failed: {exc}")
        return None
    dom = r.stdout.decode("utf-8", "replace")
    out.write_text(dom, encoding="utf-8", errors="replace")
    got = None
    for m in re.finditer(r'data-media="([^"]*)"', dom):
        got = json.loads(m.group(1).replace("&quot;", '"').replace("&amp;", "&"))
    return got


def main() -> int:
    print(f"SESSIONNAME        = {os.environ.get('SESSIONNAME', '(unset)')}")
    print(f"SM_REMOTESESSION   = {u32.GetSystemMetrics(SM_REMOTESESSION)}  (1 = this is an RDP session)")
    original = get_anim()
    print(f"SPI client-area-animation, original = {original}")
    print()

    results = []
    try:
        for value in (original, 1 - original, original):
            set_anim(value)
            time.sleep(1.2)
            g = probe()
            tag = "ON " if value else "OFF"
            if not g:
                print(f"flag={value} ({tag})  -> NO DATA")
                results.append((value, None))
                continue
            print(f"flag={value} ({tag})  reduce={g['reduce']}  "
                  f"no-preference={g['noPref']}  animations={len(g['anims'])}")
            for a in g["anims"]:
                print(f"      {a}")
            results.append((value, g))
            print()
    finally:
        set_anim(original)
        time.sleep(0.6)
        print(f"restored SPI = {get_anim()} (original was {original})")

    print()
    print("VERDICT")
    off = next((g for v, g in results if v == 0 and g), None)
    on = next((g for v, g in results if v == 1 and g), None)
    if off and on:
        print(f"  flag OFF: reduce={off['reduce']} animations={len(off['anims'])}")
        print(f"  flag ON : reduce={on['reduce']} animations={len(on['anims'])}")
        if off["reduce"] and not on["reduce"]:
            print("  -> CONFIRMED: the OS flag is the cause of prefers-reduced-motion: reduce,")
            print("     and it is what suppresses the intro's animations.")
        else:
            print("  -> NOT confirmed; something else drives the media query.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
