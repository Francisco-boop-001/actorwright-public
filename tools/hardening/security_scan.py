#!/usr/bin/env python3
"""Run the bounded, deterministic source security scan for M8 evidence."""

from __future__ import annotations

import argparse
import json
import re
from pathlib import Path


SECRET_PATTERNS = {
    "private_key": re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    "credential_assignment": re.compile(r"(?i)\b(?:password|secret|api[_-]?key|access[_-]?token)\s*[:=]\s*['\"]\S+"),
}

CSHARP_NON_CODE = re.compile(
    r'''(?://[^\r\n]*|/\*.*?\*/|\$*"{3,}.*?"{3,}|\$?@"(?:[^"]|"")*"|\$?"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')''',
    re.DOTALL,
)


def _csharp_code_only(text: str) -> str:
    """Mask comments and literals before scanning C# language tokens."""
    return CSHARP_NON_CODE.sub(lambda match: " " * len(match.group(0)), text)


def scan(project_root: Path) -> dict[str, object]:
    source_files = sorted(
        path
        for root in (project_root / "src", project_root / "tests", project_root / "tools")
        for path in root.rglob("*.cs")
        if "\\bin\\" not in str(path) and "\\obj\\" not in str(path)
    )
    secret_hits: list[str] = []
    dynamic_hits: list[str] = []
    placeholder_hits: list[str] = []
    process_files: list[str] = []
    for path in source_files:
        text = path.read_text(encoding="utf-8")
        relative = str(path.relative_to(project_root)).replace("\\", "/")
        for name, pattern in SECRET_PATTERNS.items():
            if pattern.search(text):
                secret_hits.append(f"{relative}:{name}")
        if re.search(r"\bdynamic\b", _csharp_code_only(text)):
            dynamic_hits.append(relative)
        if re.search(r"\b(?:TODO|FIXME|NotImplementedException)\b", text):
            placeholder_hits.append(relative)
        if "Process.Start" in text:
            process_files.append(relative)

    forbidden_process = [
        path for path in process_files
        if "UseShellExecute = false" not in (project_root / path).read_text(encoding="utf-8")
    ]
    return {
        "status": "PASS" if not (secret_hits or dynamic_hits or placeholder_hits or forbidden_process) else "FAIL",
        "files_scanned": len(source_files),
        "secret_hits": secret_hits,
        "dynamic_hits": dynamic_hits,
        "placeholder_hits": placeholder_hits,
        "process_start_files": process_files,
        "process_start_without_no_shell": forbidden_process,
        "protected_root_policy": "K-only policy and refusal tests are release-gate inputs",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    project_root = Path(__file__).resolve().parents[2]
    report = scan(project_root)
    output = args.output if args.output.is_absolute() else project_root / args.output
    if output.exists():
        raise SystemExit(f"refusing to overwrite existing output: {output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps({"result": report["status"], "files_scanned": report["files_scanned"]}))
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
