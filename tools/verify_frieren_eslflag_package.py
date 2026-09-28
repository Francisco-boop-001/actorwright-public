#!/usr/bin/env python3
"""Independent exact-diff and archive verifier for Frieren v0.1.1."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import struct
import sys
import zipfile
from pathlib import Path


WORKSPACE = Path(r"K:\ExampleWorkspace")
ESL_FLAG = 0x200
PLUGIN = "FrierenNpcManager.esp"
ALLOWED_NEW_ENTRY = "FRIEREN-LIGHT-CONVERSION.txt"

sys.path.insert(0, str(WORKSPACE / "tools" / "gates" / "lib"))
from esp_tools import subrecords, walk  # noqa: E402


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def normalized_entries(archive: zipfile.ZipFile) -> dict[str, bytes]:
    result = {}
    for info in archive.infolist():
        name = info.filename
        if "\\" in name or name.startswith("/") or ".." in Path(name).parts:
            raise ValueError(f"Unsafe archive entry: {name}")
        key = name.casefold()
        if key in result:
            raise ValueError(f"Duplicate archive entry: {name}")
        result[key] = archive.read(info)
    return result


def records(data: bytes) -> list[dict]:
    result = []

    def collect(signature, form_id, flags, body, depth):
        result.append(
            {
                "signature": signature,
                "formId": f"0x{form_id:08X}",
                "localFormId": f"0x{form_id & 0x00FFFFFF:06X}",
                "flags": f"0x{flags:08X}",
                "bodySha256": sha256(body),
                "subrecords": [kind for kind, _ in subrecords(body)],
            }
        )

    walk(data, collect)
    return result


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-zip", required=True, type=Path)
    parser.add_argument("--output-zip", required=True, type=Path)
    parser.add_argument("--report", required=True, type=Path)
    args = parser.parse_args()

    for path in (args.source_zip, args.output_zip):
        resolved = path.resolve(strict=True)
        if os.path.commonpath((str(WORKSPACE), str(resolved))).casefold() != str(WORKSPACE).casefold():
            raise ValueError(f"Path outside K workspace: {resolved}")
    if args.report.exists():
        raise FileExistsError("Report must not already exist")

    with zipfile.ZipFile(args.source_zip) as source_archive:
        source = normalized_entries(source_archive)
    with zipfile.ZipFile(args.output_zip) as output_archive:
        output = normalized_entries(output_archive)

    source_keys = set(source)
    output_keys = set(output)
    expected_output_keys = source_keys | {ALLOWED_NEW_ENTRY.casefold()}
    failures = []
    if output_keys != expected_output_keys:
        failures.append(
            {
                "code": "archive-entry-set",
                "missing": sorted(expected_output_keys - output_keys),
                "unexpected": sorted(output_keys - expected_output_keys),
            }
        )

    plugin_key = PLUGIN.casefold()
    changed_non_plugin = [
        key
        for key in sorted(source_keys - {plugin_key})
        if output.get(key) != source[key]
    ]
    if changed_non_plugin:
        failures.append(
            {"code": "non-plugin-drift", "entries": changed_non_plugin}
        )

    before = source[plugin_key]
    after = output[plugin_key]
    changed_offsets = [
        index
        for index, (old, new) in enumerate(zip(before, after))
        if old != new
    ]
    if len(before) != len(after):
        failures.append(
            {
                "code": "plugin-size",
                "before": len(before),
                "after": len(after),
            }
        )
    if changed_offsets != [9] or before[9] != 0 or after[9] != 2:
        failures.append(
            {
                "code": "plugin-byte-surface",
                "changedOffsets": changed_offsets,
            }
        )
    before_flags = struct.unpack_from("<I", before, 8)[0]
    after_flags = struct.unpack_from("<I", after, 8)[0]
    if after_flags != (before_flags | ESL_FLAG):
        failures.append(
            {
                "code": "tes4-esl-flag",
                "before": f"0x{before_flags:08X}",
                "after": f"0x{after_flags:08X}",
            }
        )

    before_records = records(before)
    after_records = records(after)
    if before_records != after_records:
        failures.append({"code": "record-drift"})
    expected_records = {
        ("NPC_", "0x000800"),
        ("CLFM", "0x000801"),
        ("TXST", "0x000802"),
        ("HDPT", "0x000803"),
    }
    actual_records = {
        (row["signature"], row["localFormId"]) for row in after_records
    }
    if actual_records != expected_records:
        failures.append(
            {
                "code": "record-set",
                "actual": sorted(actual_records),
            }
        )

    artifact = {
        "schema": "frieren-eslflag-package-verification/1",
        "sourceZip": str(args.source_zip.resolve()),
        "sourceZipSha256": sha256(args.source_zip.read_bytes()),
        "outputZip": str(args.output_zip.resolve()),
        "outputZipSha256": sha256(args.output_zip.read_bytes()),
        "sourcePluginSha256": sha256(before),
        "outputPluginSha256": sha256(after),
        "pluginByteLength": len(after),
        "oldTes4Flags": f"0x{before_flags:08X}",
        "newTes4Flags": f"0x{after_flags:08X}",
        "changedOffsets": changed_offsets,
        "recordBodiesIdentical": before_records == after_records,
        "preExistingNonPluginEntriesIdentical": not changed_non_plugin,
        "sourceEntryCount": len(source_keys),
        "outputEntryCount": len(output_keys),
        "addedEntries": [ALLOWED_NEW_ENTRY],
        "failures": failures,
        "verdict": "PASS" if not failures else "FAIL",
    }
    with args.report.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(artifact, stream, indent=2)
        stream.write("\n")
    print(json.dumps(artifact, indent=2))
    return 0 if not failures else 1


if __name__ == "__main__":
    raise SystemExit(main())
