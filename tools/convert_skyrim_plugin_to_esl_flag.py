#!/usr/bin/env python3
"""Set only the TES4 ESL flag on one exact K-local Skyrim plugin."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import struct
import zipfile
from pathlib import Path


ESL_FLAG = 0x200
WORKSPACE = Path(r"K:\ExampleWorkspace")


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def require_workspace_path(path: Path, *, must_exist: bool) -> Path:
    resolved = path.resolve(strict=must_exist)
    if os.path.commonpath((str(WORKSPACE), str(resolved))).casefold() != str(WORKSPACE).casefold():
        raise ValueError(f"Path is outside K workspace: {resolved}")
    for parent in (resolved, *resolved.parents):
        if parent == WORKSPACE.parent:
            break
        if parent.exists() and parent.is_symlink():
            raise ValueError(f"Reparse/symlink path refused: {parent}")
    return resolved


def subrecords(body: bytes):
    position = 0
    extended_size = None
    while position < len(body):
        if position + 6 > len(body):
            raise ValueError("Truncated subrecord header")
        signature = body[position : position + 4]
        size = struct.unpack_from("<H", body, position + 4)[0]
        position += 6
        if signature == b"XXXX":
            if size != 4 or position + 4 > len(body):
                raise ValueError("Malformed XXXX subrecord")
            extended_size = struct.unpack_from("<I", body, position)[0]
            position += 4
            continue
        if extended_size is not None:
            size = extended_size
            extended_size = None
        if position + size > len(body):
            raise ValueError("Subrecord overruns record body")
        yield signature, body[position : position + size]
        position += size


def walk_record_headers(data: bytes):
    header_size = struct.unpack_from("<I", data, 4)[0]

    def walk(position: int, end: int):
        while position < end:
            signature = data[position : position + 4]
            if signature == b"GRUP":
                group_size = struct.unpack_from("<I", data, position + 4)[0]
                if group_size < 24 or position + group_size > end:
                    raise ValueError("Malformed GRUP")
                yield from walk(position + 24, position + group_size)
                position += group_size
                continue
            record_size = struct.unpack_from("<I", data, position + 4)[0]
            record_end = position + 24 + record_size
            if record_end > end:
                raise ValueError("Record overruns container")
            form_id = struct.unpack_from("<I", data, position + 12)[0]
            yield signature, form_id
            position = record_end
        if position != end:
            raise ValueError("Container size mismatch")

    yield from walk(24 + header_size, len(data))


def inspect_source(data: bytes) -> dict:
    if len(data) < 24 or data[:4] != b"TES4":
        raise ValueError("Input is not a TES4 plugin")
    tes4_size = struct.unpack_from("<I", data, 4)[0]
    flags = struct.unpack_from("<I", data, 8)[0]
    if flags & ESL_FLAG:
        raise ValueError("Source plugin is already ESL flagged")
    tes4_body = data[24 : 24 + tes4_size]
    masters = [
        value.rstrip(b"\x00").decode("cp1252")
        for signature, value in subrecords(tes4_body)
        if signature == b"MAST"
    ]
    self_index = len(masters)
    self_records = []
    for signature, form_id in walk_record_headers(data):
        if form_id >> 24 != self_index:
            continue
        local_id = form_id & 0x00FFFFFF
        if not 0x800 <= local_id <= 0xFFF:
            raise ValueError(
                f"Self record {signature.decode('ascii', 'replace')} "
                f"0x{local_id:06X} is outside admitted light range"
            )
        self_records.append(
            {
                "signature": signature.decode("ascii", "replace"),
                "localFormId": f"0x{local_id:06X}",
            }
        )
    if not self_records:
        raise ValueError("No self-owned records found")
    return {
        "flags": flags,
        "masters": masters,
        "selfRecords": self_records,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-zip", required=True, type=Path)
    parser.add_argument("--plugin-entry", required=True)
    parser.add_argument("--expected-zip-sha256", required=True)
    parser.add_argument("--expected-plugin-sha256", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--result", required=True, type=Path)
    args = parser.parse_args()

    source_zip = require_workspace_path(args.source_zip, must_exist=True)
    output = require_workspace_path(args.output, must_exist=False)
    result = require_workspace_path(args.result, must_exist=False)
    if output.exists() or result.exists():
        raise FileExistsError("Output/result must not already exist")
    if not output.parent.is_dir() or not result.parent.is_dir():
        raise FileNotFoundError("Output/result parent must already exist")

    zip_bytes = source_zip.read_bytes()
    if sha256(zip_bytes).casefold() != args.expected_zip_sha256.casefold():
        raise ValueError("Source ZIP SHA-256 mismatch")
    with zipfile.ZipFile(source_zip) as archive:
        source = archive.read(args.plugin_entry)
    if sha256(source).casefold() != args.expected_plugin_sha256.casefold():
        raise ValueError("Source plugin SHA-256 mismatch")

    inspection = inspect_source(source)
    written = bytearray(source)
    struct.pack_into("<I", written, 8, inspection["flags"] | ESL_FLAG)
    written_bytes = bytes(written)
    changed_offsets = [
        index
        for index, (before, after) in enumerate(zip(source, written_bytes))
        if before != after
    ]
    if changed_offsets != [9] or source[9] != 0 or written_bytes[9] != 2:
        raise ValueError(f"Unexpected binary change surface: {changed_offsets}")

    with output.open("xb") as stream:
        stream.write(written_bytes)

    artifact = {
        "schema": "skyrim-esl-flag-write/1",
        "sourceZip": str(source_zip),
        "sourceZipSha256": sha256(zip_bytes),
        "pluginEntry": args.plugin_entry,
        "sourcePluginSha256": sha256(source),
        "outputPlugin": str(output),
        "outputPluginSha256": sha256(written_bytes),
        "byteLength": len(written_bytes),
        "oldTes4Flags": f"0x{inspection['flags']:08X}",
        "newTes4Flags": f"0x{inspection['flags'] | ESL_FLAG:08X}",
        "changedOffsets": changed_offsets,
        "masters": inspection["masters"],
        "selfRecords": inspection["selfRecords"],
        "compacted": False,
    }
    with result.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(artifact, stream, indent=2)
        stream.write("\n")
    print(json.dumps(artifact, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
