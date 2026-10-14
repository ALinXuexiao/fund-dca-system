"""Inject the brand intro (logo reveal splash) into web/index.html.

Why a generator: the splash must use the exact geometry-QA'd mark and the same
choreography numbers as brand/motion.css. Re-running gen_svg.py would otherwise
silently desync the copy that ships in index.html. Run this after gen_svg.py.

Usage: python gen_intro.py
"""
from __future__ import annotations

import re
from pathlib import Path

# Both paths are resolved from this file's own location, never from the current working
# directory, so the generator runs the same from anywhere (and after a fresh clone):
# this file lives in brand/outputs/fit_work/, so three parents up is brand/.
BRAND = Path(__file__).resolve().parents[2]
INDEX = BRAND.parent / "web" / "index.html"

START = "<!-- brand-intro:start -->"
END = "<!-- brand-intro:end -->"

# ===========================================================================
# THE TUNING KNOBS. Retime the reveal by editing these three numbers only --
# the six `animation:` shorthands, the fade, and the handover timer are all
# derived from them.
#
# Only the DURATION is centralised. Every keyframe keeps a LITERAL
# cubic-bezier: `animation-timing-function: var(--token)` is silently dropped
# inside @keyframes by Chromium, which turns each handoff into a linear cliff.
#
# DURATION_MS also mirrors --p2m-duration in brand/motion.css; the phase
# percentages in the keyframes below mirror that file's keyframes too, so keep
# the two in sync when you retime the choreography.
# ===========================================================================
DURATION_MS = 6400  # the single choreography clock (golden ratio 20 / 50 / 30)
HOLD_MS = 600  # how long the finished mark rests before the handover
FADE_MS = 640  # overlay fade-out duration
# ===========================================================================

# Derived from the knobs above -- do not edit these directly.
HANDOVER_MS = DURATION_MS + HOLD_MS  # the overlay starts fading out at this point
DROP_AFTER_MS = 400  # remove the overlay from the DOM once it has fully faded
REDUCED_MS = 900  # prefers-reduced-motion: nothing to watch, so hand over at once


def _expand(template: str) -> str:
    """Fill in the derived numbers.

    Plain placeholder substitution rather than str.format(), because these
    templates are full of literal CSS braces.
    """
    for token, value in (
        ("@DURATION@", DURATION_MS),
        ("@FADE@", FADE_MS),
        ("@HANDOVER@", HANDOVER_MS),
        ("@DROP@", DROP_AFTER_MS),
        ("@REDUCED@", REDUCED_MS),
    ):
        template = template.replace(token, str(value))
    return template


INTRO_CSS = """    /* ---- brand intro (logo reveal) ----------------------------------------
       Played once on top of the app shell, then faded away. Geometry is the
       same vector that passed the Phase 2 overlay QA; choreography numbers
       mirror brand/motion.css. Everything is inline, so the reveal paints
       before any bundle is fetched. */
    #brand-intro {
      position: fixed;
      inset: 0;
      z-index: 9999;
      display: grid;
      place-items: center;
      background: #f4f5f7;
      transition: opacity @FADE@ms cubic-bezier(0.4, 0, 0.2, 1);
    }
    #brand-intro.bi-hide {
      opacity: 0;
      pointer-events: none;
    }
    #brand-intro .bi-stage {
      display: flex;
      flex-direction: column;
      align-items: center;
      gap: 14px;
    }
    #brand-intro .bi-logo {
      width: 132px;
      height: 132px;
      display: block;
    }
    #brand-intro .bi-word {
      margin: 0;
      color: #1a2332;
      font-size: 15px;
      font-weight: 600;
      letter-spacing: 0.02em;
    }
    #brand-intro .bi-sub {
      margin: 0;
      color: #6b7280;
      font-size: 12px;
      letter-spacing: 0.04em;
    }

    /* Scale/rotate pivots sit on each part's own geometry, never on the canvas. */
    #brand-intro #coin {
      transform-box: fill-box;
      transform-origin: center;
    }

    #brand-intro #coin {
      animation: bi-coin-in @DURATION@ms both;
    }
    #brand-intro #arrowWipeBand {
      animation: bi-arrow-wipe @DURATION@ms both;
    }
    #brand-intro #coinSheen {
      animation: bi-sheen-fade @DURATION@ms both;
    }
    #brand-intro #coinSheenBand {
      animation: bi-sheen-sweep @DURATION@ms both;
    }
    #brand-intro .bi-word {
      animation: bi-word-in @DURATION@ms both;
    }
    #brand-intro .bi-sub {
      animation: bi-sub-in @DURATION@ms both;
    }

    @keyframes bi-coin-in {
      0% {
        transform: scale(0.88) rotate(-3.4deg);
        opacity: 0;
        animation-timing-function: cubic-bezier(0.4, 0, 0.6, 1);
      }
      9% {
        opacity: 1;
        animation-timing-function: cubic-bezier(0.4, 0, 0.6, 1);
      }
      20% {
        transform: scale(0.845) rotate(-4.2deg);
        animation-timing-function: cubic-bezier(0, 0, 0.2, 1);
      }
      48% {
        transform: scale(1.015) rotate(0.85deg);
        animation-timing-function: cubic-bezier(0.4, 0, 0.2, 1);
      }
      70% {
        transform: scale(0.998) rotate(-0.18deg);
        animation-timing-function: cubic-bezier(0.4, 0, 0.2, 1);
      }
      88%,
      100% {
        transform: scale(1) rotate(0deg);
        opacity: 1;
      }
    }
    @keyframes bi-arrow-wipe {
      0%,
      25% {
        transform: translateX(-196px);
        animation-timing-function: cubic-bezier(0.4, 0, 0.2, 1);
      }
      72%,
      100% {
        transform: translateX(0px);
      }
    }
    @keyframes bi-sheen-fade {
      0%,
      58% {
        opacity: 0;
        animation-timing-function: cubic-bezier(0.4, 0, 0.2, 1);
      }
      70% {
        opacity: 0.85;
        animation-timing-function: cubic-bezier(0.4, 0, 0.2, 1);
      }
      94%,
      100% {
        opacity: 0;
      }
    }
    @keyframes bi-sheen-sweep {
      0%,
      58% {
        transform: translateX(-190px);
        animation-timing-function: cubic-bezier(0.4, 0, 0.2, 1);
      }
      94%,
      100% {
        transform: translateX(190px);
      }
    }
    /* Follow-through: the wordmark trails the mark by 2-4 frames, never in lockstep. */
    @keyframes bi-word-in {
      0%,
      64% {
        opacity: 0;
        transform: translateY(8px);
        animation-timing-function: cubic-bezier(0.4, 0, 0.2, 1);
      }
      80%,
      100% {
        opacity: 1;
        transform: translateY(0);
      }
    }
    @keyframes bi-sub-in {
      0%,
      72% {
        opacity: 0;
        transform: translateY(6px);
        animation-timing-function: cubic-bezier(0.4, 0, 0.2, 1);
      }
      88%,
      100% {
        opacity: 1;
        transform: translateY(0);
      }
    }

    /* Reduced motion: show the finished mark at once, no choreography. */
    @media (prefers-reduced-motion: reduce) {
      #brand-intro * {
        animation: none !important;
      }
    }
"""

INTRO_SCRIPT = """    <script>
      // Brand intro: play the reveal, then fade the overlay out and drop it.
      // Deliberately a timer, not a data callback: the app underneath may still be
      // fetching, and the splash must never wait on it.
      (function () {
        var intro = document.getElementById('brand-intro');
        if (!intro) return;
        var params = new URLSearchParams(location.search);
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        // QA hooks, mirroring the pixel2motion contract: ?t=<ms> freezes the clock,
        // ?static=1 jumps to the finished frame. Both keep the overlay in place.
        if (params.has('t') || params.has('static')) {
          var anims = document.getAnimations();
          for (var i = 0; i < anims.length; i++) {
            if (params.has('static')) {
              anims[i].finish();
            } else {
              anims[i].pause();
              anims[i].currentTime = Number(params.get('t'));
            }
          }
          intro.classList.add('bi-hide');
          intro.style.opacity = '1';
          intro.style.transition = 'none';
          return;
        }

        function drop() {
          intro.classList.add('bi-hide');
          window.setTimeout(function () {
            if (intro.parentNode) intro.parentNode.removeChild(intro);
          }, @DROP@);
        }
        // Handover is a TIMER derived from the choreography, never a data callback.
        // Reduced motion: no reveal to watch, so hand over almost immediately.
        window.setTimeout(drop, reduced ? @REDUCED@ : @HANDOVER@);
      })();
    </script>
"""


def scoped_svg() -> str:
    svg = (BRAND / "logo.svg").read_text(encoding="utf-8")
    # Strip the XML prolog and the accessible-image role: this copy is decorative
    # (the app's own <h1> carries the title), so it must not announce a second image.
    svg = re.sub(r"<\?xml[^>]*\?>\s*", "", svg)
    svg = svg.replace('role="img" aria-labelledby="brandTitle brandDesc"', 'aria-hidden="true"')
    svg = svg.replace(
        '<svg xmlns="http://www.w3.org/2000/svg" ',
        '<svg class="bi-logo" xmlns="http://www.w3.org/2000/svg" ',
        1,
    )
    # Drop the standalone width/height so the CSS size wins.
    svg = re.sub(r'\s+width="256"\s+height="256"', "", svg, count=1)
    return "\n".join("      " + line if line.strip() else line for line in svg.splitlines())


def intro_block() -> str:
    svg = scoped_svg()
    return f"""{START}
    <!-- Logo reveal / brand intro. Generated by brand/outputs/fit_work/gen_intro.py
         from brand/logo.svg + brand/motion.css - regenerate, do not hand-edit. -->
    <style>
{_expand(INTRO_CSS)}    </style>
    <div id="brand-intro" aria-hidden="true">
      <div class="bi-stage">
{svg}
        <p class="bi-word">基金持仓看板与定投决策系统</p>
        <p class="bi-sub">估值采集（蛋卷基金、中证指数） · 真实净值采集（天天基金）</p>
      </div>
    </div>
{_expand(INTRO_SCRIPT)}    {END}"""


def main() -> None:
    html = INDEX.read_text(encoding="utf-8")
    block = intro_block()
    if START in html and END in html:
        html = re.sub(
            re.escape(START) + r".*?" + re.escape(END), lambda _: block, html, count=1, flags=re.S
        )
    else:
        # Insert right after <body> so the overlay exists before the module script runs.
        html = html.replace("<body>", "<body>\n" + block, 1)
    INDEX.write_text(html, encoding="utf-8")
    print(f"wrote {INDEX} ({len(html.encode('utf-8'))} bytes)")
    print(
        f"  clock {DURATION_MS}ms, hold {HOLD_MS}ms -> handover at {HANDOVER_MS}ms, "
        f"fade {FADE_MS}ms, drop at {HANDOVER_MS + DROP_AFTER_MS}ms"
    )


if __name__ == "__main__":
    main()
