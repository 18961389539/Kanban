#!/usr/bin/env python3
"""Compatibility entry point for Web localization generation.

The source of truth is MainAPP/Resources/Localization.csv. Use
``python ci/generate_localization.py --web`` for the explicit command.
"""

from __future__ import annotations

import runpy
import sys
from pathlib import Path


if __name__ == "__main__":
    script = Path(__file__).with_name("generate_localization.py")
    sys.argv = [str(script), "--web", *sys.argv[1:]]
    runpy.run_path(str(script), run_name="__main__")
