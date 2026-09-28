#!/usr/bin/env python3
"""Independently verify the fixed M2 fixture facts without using Mutagen."""

from __future__ import annotations

import hashlib
import json
import struct
import sys
from pathlib import Path


def read_ba2_members(path: Path) -> set[str]:
    raw = path.read_bytes()
    if len(raw) < 24:
        raise ValueError("BA2 header is truncated")
    magic, version, kind, count, names_offset = struct.unpack_from("<4sI4sIQ", raw, 0)
    if magic != b"BTDX" or version != 1 or kind != b"GNRL":
        raise ValueError("expected BA2 v1 GNRL archive")
    record_size = 36
    records_end = 24 + count * record_size
    if records_end > len(raw) or names_offset < records_end or names_offset > len(raw):
        raise ValueError("BA2 record or name-table bounds are invalid")
    records = [struct.unpack_from("<I4sIIQIII", raw, 24 + index * record_size)
               for index in range(count)]
    members: list[str] = []
    offset = names_offset
    for _ in range(count):
        if offset + 2 > len(raw):
            raise ValueError("BA2 name length is truncated")
        length = struct.unpack_from("<H", raw, offset)[0]
        offset += 2
        end = offset + length
        if end > len(raw):
            raise ValueError("BA2 name is truncated")
        members.append(raw[offset:end].decode("utf-8").replace("\\", "/"))
        offset = end
    for _, _, _, _, data_offset, packed_size, unpacked_size, _ in records:
        stored_size = packed_size or unpacked_size
        if data_offset + stored_size > names_offset:
            raise ValueError("BA2 payload escapes the name table")
    return {member.casefold() for member in members}


def read_bsa_members(path: Path) -> set[str]:
    raw = path.read_bytes()
    if len(raw) < 36:
        raise ValueError("BSA header is truncated")
    magic, version, directory_offset, flags, folder_count, file_count, folder_names_length, file_names_length, _ = struct.unpack_from("<4s8I", raw, 0)
    if magic != b"BSA\x00" or version != 105 or not (flags & 0x003):
        raise ValueError("expected named BSA v105 archive")
    record_size = 24
    records_end = directory_offset + folder_count * record_size
    if records_end > len(raw):
        raise ValueError("BSA folder records are truncated")
    folder_records = [struct.unpack_from("<QI4xQ", raw, directory_offset + index * record_size)
                      for index in range(folder_count)]
    offset = records_end
    folders: list[tuple[str, int]] = []
    for _, count, _ in folder_records:
        if offset >= len(raw):
            raise ValueError("BSA folder name is truncated")
        length = raw[offset]
        offset += 1
        end = offset + length
        if end > len(raw):
            raise ValueError("BSA folder name is truncated")
        name = raw[offset:end].rstrip(b"\x00").decode("cp1252")
        offset = end
        folders.append((name, count))
    if offset - records_end - folder_count != folder_names_length:
        raise ValueError("BSA folder-name length does not match its indexes")
    file_records: list[tuple[int, int, int]] = []
    for _, count in folders:
        end = offset + count * 16
        if end > len(raw):
            raise ValueError("BSA file records are truncated")
        file_records.extend(struct.unpack_from("<QII", raw, offset + index * 16)
                            for index in range(count))
        offset = end
    names_end = offset + file_names_length
    if names_end > len(raw):
        raise ValueError("BSA file-name table is truncated")
    names = raw[offset:names_end].split(b"\x00")
    if names and names[-1] == b"":
        names.pop()
    if len(names) != file_count or len(file_records) != file_count:
        raise ValueError("BSA file count does not match its indexes")
    members: set[str] = set()
    cursor = 0
    for folder, count in folders:
        for _ in range(count):
            _, size, data_offset = file_records[cursor]
            cursor += 1
            if data_offset + (size & 0x3FFFFFFF) > len(raw):
                raise ValueError("BSA payload escapes the archive")
            file_name = names[cursor - 1].decode("cp1252")
            members.add(f"{folder}/{file_name}".replace("\\", "/").casefold())
    return members


def verify_archives(data: Path, expected: list[dict[str, object]]) -> None:
    expected_by_name = {str(item["file"]): item for item in expected}
    actual = {path.name: path for path in data.iterdir() if path.suffix.lower() in {".ba2", ".bsa"}}
    if set(actual) != set(expected_by_name):
        raise ValueError(f"archive set mismatch: actual={sorted(actual)} expected={sorted(expected_by_name)}")
    for name, item in expected_by_name.items():
        path = actual[name]
        archive_format = str(item["format"])
        if archive_format == "ba2-gnrl":
            members = read_ba2_members(path)
        elif archive_format == "bsa-v105":
            members = read_bsa_members(path)
        else:
            raise ValueError(f"{name}: unsupported expected archive format {archive_format!r}")
        expected_members = {str(member).replace("\\", "/").casefold() for member in item["members"]}
        if members != expected_members:
            raise ValueError(f"{name}: members differ: actual={sorted(members)} expected={sorted(expected_members)}")


def main() -> int:
    root = Path(__file__).resolve().parents[1]
    manifest = json.loads((root / "01-source-copies" / "m2-fixtures" / "fixture-expected.json").read_text(encoding="utf-8"))
    failures: list[str] = []
    checked = 0
    for fixture in manifest["fixtures"]:
        data = root / "01-source-copies" / "m2-fixtures" / ("sse" if fixture["edition"] == "skyrimse" else "fo4") / "Data"
        plugin = data / fixture["plugin"]
        if not plugin.is_file():
            failures.append(f"missing plugin: {plugin}")
            continue
        raw = plugin.read_bytes()
        npc = fixture["expectedNpc"]
        form_id = int(npc["formId"], 16).to_bytes(4, "little")
        for label, needle in (("form id", form_id), ("EditorID", npc["editorId"].encode()), ("name", npc["name"].encode())):
            if needle not in raw:
                failures.append(f"{fixture['id']}: expected {label} marker absent from plugin bytes")
        for relative in fixture["expectedLooseAssets"]:
            asset = data / Path(*relative.split("/"))
            if not asset.is_file():
                failures.append(f"{fixture['id']}: missing loose asset {relative}")
        try:
            verify_archives(data, fixture["expectedArchives"])
        except (OSError, UnicodeError, ValueError, KeyError, struct.error) as error:
            failures.append(f"{fixture['id']}: archive verification failed: {error}")
        checked += 1
        print(f"FIXTURE PASS {fixture['id']} pluginSha256={hashlib.sha256(raw).hexdigest()}")
    if failures:
        print("RESULT FAIL")
        for failure in failures:
            print(f"  - {failure}")
        return 1
    print(f"RESULT PASS fixtures={checked} parser=independent-byte-scan")
    return 0


if __name__ == "__main__":
    sys.exit(main())
