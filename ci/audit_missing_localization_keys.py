#!/usr/bin/env python3
"""Report Wpf localization keys referenced in code but missing from Localization.csv."""

from __future__ import annotations

import csv
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CSV_PATH = ROOT / "MainAPP" / "Resources" / "Localization.csv"
SCAN_DIRS = (
    ROOT / "MainAPP",
    ROOT / "MainAPP.Tests",
    ROOT / "MainAPP.E2E",
    ROOT / "MainAPP.UIAutomation",
)

PATTERNS = (
    re.compile(r"Strings\.([A-Za-z_][A-Za-z0-9_]*)"),
    re.compile(r"ConverterParameter=([A-Za-z_][A-Za-z0-9_]*)"),
    re.compile(r'HeaderAliases\("([A-Za-z_][A-Za-z0-9_]*)"'),
    re.compile(r'"([A-Za-z_][A-Za-z0-9_]*)"\s*:\s*"Csv_'),
    re.compile(r'LabelKey\s*=\s*"([A-Za-z_][A-Za-z0-9_]*)"'),
    re.compile(r'ToolTipKey\s*=\s*"([A-Za-z_][A-Za-z0-9_]*)"'),
    re.compile(r'x:Static resources:Strings\.([A-Za-z_][A-Za-z0-9_]*)'),
)

BOGUS = {
    "S", "cs", "en", "ja", "pt", "resx", "F20x", "Status_", "CaptureCulture",
    "GetEmbeddedValue", "IsKnownKey", "Invert",
}
PREFIXES = (
    "Nav_", "Dsm_", "Validator_", "Csv_", "Ux_", "Settings_", "Rtmon_", "Alarm_",
    "F", "K", "M", "Web_", "Common_", "Level_", "Severity_", "Defect_",
    "Badge_", "Card_", "Conn_", "Fresh_", "Lbl_", "Meta_", "Msg_", "Status_", "Val_",
    "Wo_", "Language_",
)


def load_known_keys() -> set[str]:
    with CSV_PATH.open(encoding="utf-8-sig", newline="") as handle:
        return {row["Key"] for row in csv.DictReader(handle) if row.get("Resource") == "Wpf"}


def likely_key(key: str) -> bool:
    if key in BOGUS:
        return False
    if not re.match(r"^[A-Za-z_][A-Za-z0-9_]*$", key):
        return False
    return any(key.startswith(prefix) for prefix in PREFIXES)


def collect_references() -> set[str]:
    refs: set[str] = set()
    for folder in SCAN_DIRS:
        if not folder.exists():
            continue
        for path in folder.rglob("*"):
            if path.suffix not in {".cs", ".xaml"}:
                continue
            text = path.read_text(encoding="utf-8", errors="ignore")
            for pattern in PATTERNS:
                refs.update(pattern.findall(text))

    nav_path = ROOT / "MainAPP" / "Models" / "NavigationPageCatalog.cs"
    nav_text = nav_path.read_text(encoding="utf-8")
    refs.update(re.findall(r'Page\("[^"]+",\s*\d+,\s*"([A-Za-z_][A-Za-z0-9_]*)"', nav_text))
    refs.update(re.findall(r'MaterialIconKind\.[^,]+,\s*"([A-Za-z_][A-Za-z0-9_]*)"', nav_text))
    return refs


def audit_core_getstring() -> list[str]:
    core_known = load_core_keys()
    keys: set[str] = set()
    for path in (ROOT / "Kanban.Collector.Core" / "Localization").glob("*.cs"):
        keys.update(re.findall(r'GetString\("([^"]+)"', path.read_text(encoding="utf-8")))
    return sorted(key for key in keys if key not in core_known)


def load_core_keys() -> set[str]:
    with CSV_PATH.open(encoding="utf-8-sig", newline="") as handle:
        return {row["Key"] for row in csv.DictReader(handle) if row.get("Resource") == "Core"}


def main() -> int:
    known = load_known_keys()
    refs = collect_references()
    referenced = sorted(key for key in refs if likely_key(key))
    missing = [key for key in referenced if key not in known]

    print(f"Known Wpf keys: {len(known)}")
    print(f"Referenced likely keys: {len(referenced)}")
    print(f"Missing Wpf: {len(missing)}")
    for key in missing:
        print(f"  Wpf missing: {key}")

    core_missing = audit_core_getstring()
    print(f"Missing Core GetString: {len(core_missing)}")
    for key in core_missing:
        print(f"  Core missing: {key}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
