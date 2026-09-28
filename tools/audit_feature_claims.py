#!/usr/bin/env python3
"""Find feature-ledger PASS claims whose acceptance text discloses a bounded slice.

The feature-ledger validator checks provenance and evidence structure.  This
small, deterministic audit complements it by flagging wording that commonly
means a requirement is still semantic, proposal-only, runtime-only, or
explicitly incomplete.  It is intentionally a review signal, not a parser
that silently changes ledger status.
"""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path
from typing import Any


BASELINE_COMMIT = "2f765c5dc09277f48321eb8cf80ad7925d4699a4"
MARKERS: tuple[tuple[str, re.Pattern[str]], ...] = (
    ("planned", re.compile(r"\bplanned\b", re.IGNORECASE)),
    ("remaining", re.compile(r"\bremain(?:s|ing)?\b", re.IGNORECASE)),
    ("proposal", re.compile(r"\bproposal(?:s)?\b", re.IGNORECASE)),
    ("semantic", re.compile(r"\bsemantic\b", re.IGNORECASE)),
    ("bounded", re.compile(r"\bbounded\b", re.IGNORECASE)),
    ("without-binary", re.compile(r"\bwithout\s+binary\b", re.IGNORECASE)),
    ("runtime-boundary", re.compile(r"\bruntime\b", re.IGNORECASE)),
)


def _snippet(text: str, match: re.Match[str]) -> str:
    start = max(0, match.start() - 42)
    end = min(len(text), match.end() + 58)
    return " ".join(text[start:end].split())


def audit(data: dict[str, Any]) -> dict[str, Any]:
    findings: list[dict[str, Any]] = []
    for item in data.get("items", []):
        if item.get("implementation_status") != "PASS":
            continue
        acceptance = item.get("acceptance_rule")
        if not isinstance(acceptance, str):
            continue
        markers: list[dict[str, str]] = []
        for name, pattern in MARKERS:
            match = pattern.search(acceptance)
            if match:
                markers.append({"marker": name, "snippet": _snippet(acceptance, match)})
        if markers:
            findings.append(
                {
                    "id": item.get("id"),
                    "family": item.get("family"),
                    "name": item.get("name"),
                    "implementationStatus": item.get("implementation_status"),
                    "markers": markers,
                    "acceptanceRule": acceptance,
                }
            )
    return {
        "schemaVersion": 1,
        "status": "OPEN_CLAIMS" if findings else "PASS",
        "baselineCommit": data.get("upstream_commit"),
        "expectedBaselineCommit": BASELINE_COMMIT,
        "ledgerItems": len(data.get("items", [])),
        "passItemsAudited": sum(item.get("implementation_status") == "PASS" for item in data.get("items", [])),
        "flaggedPassClaims": len(findings),
        "interpretation": (
            "Markers are review signals. They do not change ledger status; each flagged claim "
            "requires explicit evidence that the disclosed boundary is actually closed."
        ),
        "findings": findings,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("ledger", nargs="?", type=Path,
                        default=Path("05-reports/feature-ledger.json"))
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    try:
        data = json.loads(args.ledger.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as exc:
        print(json.dumps({"result": "FAIL", "errors": [str(exc)]}, indent=2))
        return 1
    result = audit(data)
    result["result"] = "PASS" if result["status"] == "PASS" else "OPEN_CLAIMS"
    output = json.dumps(result, indent=2) + "\n"
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(output, encoding="utf-8", newline="\n")
    print(output, end="")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
