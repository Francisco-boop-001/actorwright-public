"""Independent raw-plugin oracle for Fallout 4 NPC.WNAM skin routing."""

from __future__ import annotations

import argparse
import struct
from pathlib import Path


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


def wnam(payload: bytes) -> int | None:
    position = 0
    value = None
    while position + 6 <= len(payload):
        signature = payload[position : position + 4]
        size = struct.unpack_from("<H", payload, position + 4)[0]
        end = position + 6 + size
        if end > len(payload):
            raise AssertionError("NPC subrecord exceeds record boundary")
        if signature == b"WNAM":
            if size != 4:
                raise AssertionError("WNAM is not a four-byte FormID")
            value = struct.unpack_from("<I", payload, position + 6)[0]
        position = end
    if position != len(payload):
        raise AssertionError("NPC payload has trailing bytes")
    return value


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--plugin", required=True, type=Path)
    parser.add_argument("--npc", required=True, type=lambda value: int(value, 0))
    parser.add_argument("--expected", required=True)
    args = parser.parse_args()
    data = args.plugin.read_bytes()
    matches = list(records(data, 0, len(data), args.npc))
    assert len(matches) == 1, f"expected one NPC record, found {len(matches)}"
    actual = wnam(matches[0])
    if args.expected.lower() == "none":
        assert actual in (None, 0), f"expected race-default WNAM, found {actual:#x}"
    else:
        form_id = int(args.expected.rsplit("|", 1)[-1], 0)
        assert actual == form_id, f"expected WNAM {form_id:#x}, found {actual!r}"
    print(f"SKIN WNAM INDEPENDENT PASS npc=0x{args.npc:08X} value={actual!r}")


if __name__ == "__main__":
    main()
