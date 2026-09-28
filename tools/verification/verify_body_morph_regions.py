"""Independent raw-plugin oracle for Fallout 4 NPC.MRSV values."""

from __future__ import annotations

import argparse
import math
import struct
from pathlib import Path


REGIONS = ("head", "upperTorso", "arms", "lowerTorso", "legs")


def records(data: bytes, start: int, end: int, target: int):
    position = start
    while position + 8 <= end:
        signature = data[position : position + 4]
        size = struct.unpack_from("<I", data, position + 4)[0]
        record_end = position + (size if signature == b"GRUP" else 24 + size)
        if record_end > end:
            raise AssertionError("record exceeds plugin boundary")
        if signature == b"GRUP":
            yield from records(data, position + 24, record_end, target)
        elif signature == b"NPC_" and struct.unpack_from("<I", data, position + 12)[0] == target:
            yield data[position + 24 : record_end]
        position = record_end
    if position != end:
        raise AssertionError("plugin has a trailing partial record")


def mrsv(payload: bytes) -> tuple[float, ...]:
    position = 0
    value: bytes | None = None
    while position + 6 <= len(payload):
        signature = payload[position : position + 4]
        size = struct.unpack_from("<H", payload, position + 4)[0]
        end = position + 6 + size
        if end > len(payload):
            raise AssertionError("NPC subrecord exceeds record boundary")
        if signature == b"MRSV":
            if size != 20:
                raise AssertionError("MRSV is not five little-endian float values")
            value = payload[position + 6 : end]
        position = end
    if position != len(payload):
        raise AssertionError("NPC payload has trailing bytes")
    return struct.unpack("<5f", value) if value is not None else (0.0,) * 5


def parse_expected(raw: str) -> tuple[float, ...]:
    values = {name: 0.0 for name in REGIONS}
    for token in raw.split(","):
        name, separator, value = token.partition("=")
        if not separator or name not in values:
            raise AssertionError(f"invalid expected region token: {token}")
        values[name] = struct.unpack("<f", struct.pack("<f", float(value)))[0]
    return tuple(values[name] for name in REGIONS)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--plugin", required=True, type=Path)
    parser.add_argument("--npc", required=True, type=lambda value: int(value, 0))
    parser.add_argument("--expected", required=True)
    args = parser.parse_args()
    expected = parse_expected(args.expected)
    actual_records = list(records(args.plugin.read_bytes(), 0, args.plugin.stat().st_size, args.npc))
    assert len(actual_records) == 1, f"expected one NPC record, found {len(actual_records)}"
    actual = mrsv(actual_records[0])
    assert all(math.isfinite(value) for value in actual), "MRSV contains a non-finite value"
    assert actual == expected, f"expected MRSV {expected!r}, found {actual!r}"
    print(f"MRSV INDEPENDENT PASS npc=0x{args.npc:08X} values={actual!r}")


if __name__ == "__main__":
    main()
