"""Locate where SPI_SETCLIENTAREAANIMATION persists, by diffing the registry
around an OFF->ON cycle. Restores the ON state at the end.

This answers two user-facing questions:
  - Which GUI toggle corresponds to this flag?
  - Does it survive a reboot (i.e. is it written to the user profile)?
"""
from __future__ import annotations

import ctypes
import subprocess
import sys
import time

SPI_GET = 0x1042
SPI_SET = 0x1043
SPIF = 0x01 | 0x02
u32 = ctypes.windll.user32

# Candidate locations for the "Animation effects" / client-area animation toggle.
KEYS = [
    r"HKCU\Control Panel\Desktop",
    r"HKCU\Control Panel\Desktop\WindowMetrics",
    r"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects",
    r"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects\ClientAreaAnimation",
    r"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects\AnimateControlsInWindows",
    r"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
    r"HKCU\Software\Microsoft\Accessibility",
    r"HKCU\Control Panel\Accessibility",
]


def get() -> int:
    v = ctypes.c_int(0)
    u32.SystemParametersInfoW(SPI_GET, 0, ctypes.byref(v), 0)
    return v.value


def setv(on: int) -> bool:
    return bool(u32.SystemParametersInfoW(SPI_SET, 0, ctypes.c_void_p(on), SPIF))


def snap() -> dict[str, str]:
    out: dict[str, str] = {}
    for k in KEYS:
        r = subprocess.run(
            ["reg", "query", k], capture_output=True, text=True,
            encoding="utf-8", errors="replace",
        )
        if r.returncode != 0:
            continue
        for line in r.stdout.splitlines():
            line = line.strip()
            if not line or line.startswith("HKEY"):
                continue
            parts = line.split("    ")
            name = parts[0].strip()
            val = parts[-1].strip()
            if name:
                out[f"{k}\\{name}"] = val
    return out


def main() -> int:
    before = snap()
    print(f"flag before      = {get()}")
    print(f"registry values snapshotted: {len(before)}")

    setv(0)
    time.sleep(1.0)
    off_flag = get()
    off = snap()
    setv(1)
    time.sleep(1.0)
    on_flag = get()
    on = snap()
    # end state must be ON
    assert on_flag == 1, f"failed to restore ON (got {on_flag})"

    print(f"flag after OFF   = {off_flag}")
    print(f"flag after ON    = {on_flag}  (restored)")
    print()

    changed = sorted(
        set(before) | set(off) | set(on),
    )
    print("values that moved during the OFF->ON cycle:")
    found = 0
    for k in changed:
        b, o, n = before.get(k, "(absent)"), off.get(k, "(absent)"), on.get(k, "(absent)")
        if not (b == o == n):
            found += 1
            print(f"  {k}")
            print(f"      before={b}   OFF={o}   ON={n}")
    if not found:
        print("  (none) -- the flag is held in memory, not mirrored to these keys")
    return 0


if __name__ == "__main__":
    sys.exit(main())
