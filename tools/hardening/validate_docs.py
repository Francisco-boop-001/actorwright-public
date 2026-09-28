#!/usr/bin/env python3
"""Validate the release documentation surface without network access."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


LINK_PATTERN = re.compile(r"!?(?:\[[^\]]*\])\(([^)]+)\)")


def validate(project_root: Path) -> dict[str, object]:
    required = [
        project_root / "README.md",
        project_root / "docs" / "build.md",
        project_root / "docs" / "cli-contract.md",
        project_root / "docs" / "licenses.md",
        project_root / "docs" / "threat-model.md",
        project_root / "docs" / "runtime-smoke-kit.md",
        project_root / "tasks" / "todo.md",
    ]
    errors: list[str] = []
    checked = 0
    for path in required:
        if not path.is_file() or not path.read_text(encoding="utf-8").strip():
            errors.append(f"missing or empty required document: {path.relative_to(project_root)}")
            continue
        checked += 1

    for template in ("runtime-smoke-fo4.txt", "runtime-smoke-sse.txt"):
        path = project_root / "tools" / "templates" / template
        if not path.is_file() or not path.read_text(encoding="utf-8").strip():
            errors.append(f"missing or empty runtime template: tools/templates/{template}")

    markdown = sorted(project_root.glob("README.md")) + sorted((project_root / "docs").glob("*.md"))
    for path in markdown:
        text = path.read_text(encoding="utf-8")
        relative = path.relative_to(project_root).as_posix()
        if re.search(r"(?i)(?<![/\\])\b(?:TODO|FIXME)\b", text):
            errors.append(f"placeholder marker in {relative}")
        for target in LINK_PATTERN.findall(text):
            target = target.strip().split("#", 1)[0]
            if not target or target.startswith(("http://", "https://", "mailto:")):
                continue
            candidate = (path.parent / target).resolve()
            if not candidate.is_file():
                errors.append(f"broken local link in {relative}: {target}")

    readme = (project_root / "README.md").read_text(encoding="utf-8")
    cli = (project_root / "docs" / "cli-contract.md").read_text(encoding="utf-8")
    required_markers = {
        "README overall state": "M9_IN_PROGRESS",
        "README strict GUI coverage": "26/26",
        "README canonical journeys": "2/4",
        "README registered package": "1.0.0-preview.227",
        "README runtime boundary": "runtime_release_claim=false",
        "README internal version": "0.1.0-m1",
        "README registration report": "npc-manager-preview227-provisional-registration-20260803.json",
        "CLI pipeline": "pipeline preset-to-npc",
        "README GUI": "gui --launch",
    }
    forbidden_markers = {
        "README stale GUI baseline": "0/26",
        "README false runtime authority": "runtime_release_claim=true",
    }
    for label, marker in required_markers.items():
        if marker not in (readme if label.startswith("README") else cli):
            errors.append(f"missing documentation marker: {label}")
    for label, marker in forbidden_markers.items():
        if marker in readme:
            errors.append(f"forbidden documentation marker: {label}")

    return {"status": "PASS" if not errors else "FAIL", "documents_checked": checked, "errors": errors}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    project_root = Path(__file__).resolve().parents[2]
    result = validate(project_root)
    output = args.output if args.output.is_absolute() else project_root / args.output
    if output.exists():
        raise SystemExit(f"refusing to overwrite existing output: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"result": result["status"], "documents_checked": result["documents_checked"]}))
    return 0 if result["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
