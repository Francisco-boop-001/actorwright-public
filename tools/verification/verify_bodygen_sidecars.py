#!/usr/bin/env python3
"""Independently verify BodyGen sidecars without importing production code."""

from __future__ import annotations

import argparse
import hashlib
import math
from pathlib import Path


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def read_single_line(path: Path) -> str:
    if not path.is_file():
        fail(f"missing={path}")
    raw = path.read_bytes()
    if raw.startswith(b"\xef\xbb\xbf"):
        fail(f"utf8-bom={path}")
    text = raw.decode("utf-8")
    lines = text.splitlines()
    if len(lines) != 1 or not lines[0].strip():
        fail(f"line-shape={path}")
    return lines[0]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    parser.add_argument("--plugin", required=True)
    parser.add_argument("--form-id", required=True)
    parser.add_argument("--mod-name", required=True)
    args = parser.parse_args()

    root = args.root.resolve()
    if not str(root).lower().startswith("k:\\exampleworkspace"):
        fail(f"outside-k={root}")
    prefix = ("F4SE/Plugins/F4EE/BodyGen" if args.edition == "fallout4"
              else "meshes/actors/character/BodyGenData")
    package = root.joinpath(*prefix.split("/"), args.mod_name)
    template_path = package / "templates.ini"
    morph_path = package / "morphs.ini"
    template_line = read_single_line(template_path)
    morph_line = read_single_line(morph_path)

    expected_template = f"NpcManager_{args.form_id.removeprefix('0x').upper().zfill(8)}"
    name, separator, body = template_line.partition("=")
    if separator != "=" or name != expected_template:
        fail(f"template-name={name!r}")
    entries = []
    for entry in body.split(","):
        morph_name, at, value_text = entry.partition("@")
        if at != "@" or not morph_name or any(char in morph_name for char in ",|=@:#/\\"):
            fail(f"morph-entry={entry!r}")
        try:
            value = float(value_text)
        except ValueError:
            fail(f"morph-value={value_text!r}")
        if not math.isfinite(value) or not -1.0 <= value <= 1.0:
            fail(f"morph-range={value!r}")
        entries.append((morph_name, value))
    if not entries or [name for name, _ in entries] != sorted((name for name, _ in entries)):
        fail("morph-order-or-empty")
    if len({name.casefold() for name, _ in entries}) != len(entries):
        fail("morph-duplicate")

    target, separator, template = morph_line.partition("=")
    expected_target = f"{args.plugin}|{args.form_id.removeprefix('0x').upper().zfill(8)}"
    if separator != "=" or target != expected_target or template != expected_template:
        fail(f"target={morph_line!r}")

    hashes = [hashlib.sha256(path.read_bytes()).hexdigest() for path in (template_path, morph_path)]
    print(f"RESULT PASS edition={args.edition} files=2 template={expected_template} sha256={','.join(hashes)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
