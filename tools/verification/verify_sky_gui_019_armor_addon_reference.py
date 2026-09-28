#!/usr/bin/env python3
"""SKY-GUI-019 raw audit profile for the shared ARMO/OTFT verifier."""

from __future__ import annotations

import sys

from verify_sky_gui_018_armor import main


if __name__ == "__main__":
    if "--expected-armor-addon" not in sys.argv:
        sys.argv.extend(
            ["--expected-armor-addon", "Source.esp|0x00000908"]
        )
    if "--report-schema" not in sys.argv:
        sys.argv.extend(
            [
                "--report-schema",
                "npcmanager.sky-gui-019.armor-addon-reference-raw-audit.v1",
            ]
        )
    raise SystemExit(main())
