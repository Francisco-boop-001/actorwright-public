#!/usr/bin/env python3
"""Independent raw TES4 verifier for NPC KWDA/APPR list mutations."""
from __future__ import annotations
import argparse, hashlib, json, os, struct
from pathlib import Path

ROOT = Path(r"K:\ExampleWorkspace").resolve()

class Failure(Exception): pass
def fail(message: str) -> None: raise Failure(message)

def safe(value: str) -> Path:
    path = Path(value).resolve()
    try: path.relative_to(ROOT)
    except ValueError: fail("path outside workspace")
    if not path.is_file() or path.is_symlink(): fail("missing or linked path")
    return path

def subrecords(data: bytes, start: int, end: int) -> dict[str, bytes]:
    out: dict[str, bytes] = {}; pos = start
    while pos < end:
        if pos + 6 > end: fail("truncated subrecord")
        sig = data[pos:pos+4].decode("ascii"); size = struct.unpack_from("<H", data, pos+4)[0]; pos += 6
        if pos + size > end or sig in out: fail("invalid or duplicate subrecord")
        out[sig] = data[pos:pos+size]; pos += size
    return out

def find(data: bytes, start: int, end: int, form_id: int, found: list[dict[str, bytes]]) -> None:
    pos = start
    while pos < end:
        if pos + 8 > end: fail("truncated record")
        sig = data[pos:pos+4].decode("ascii"); size = struct.unpack_from("<I", data, pos+4)[0]
        record_end = pos + (size if sig == "GRUP" else 24 + size)
        if record_end > end: fail("record boundary")
        if sig == "GRUP": find(data, pos+24, record_end, form_id, found)
        elif sig == "NPC_" and struct.unpack_from("<I", data, pos+12)[0] == form_id: found.append(subrecords(data, pos+24, record_end))
        pos = record_end

def npc(path: Path, form_id: int) -> dict[str, bytes]:
    found: list[dict[str, bytes]] = []; data = path.read_bytes(); find(data, 0, len(data), form_id, found)
    if len(found) != 1: fail(f"expected one NPC, found {len(found)}")
    return found[0]

def list_value(record: dict[str, bytes], signature: str, plugin: str) -> str:
    raw = record.get(signature, b"")
    if len(raw) % 4: fail(f"{signature} is not FormID-aligned")
    return ",".join(f"{plugin}|0x{struct.unpack_from('<I', raw, i)[0]:08X}" for i in range(0, len(raw), 4))

def main() -> int:
    p = argparse.ArgumentParser(); p.add_argument("--source", required=True); p.add_argument("--output", required=True); p.add_argument("--json", required=True); p.add_argument("--form-id", required=True); p.add_argument("--plugin", required=True); p.add_argument("--keywords", required=True); p.add_argument("--appr", default="")
    a = p.parse_args()
    try:
        source, output, evidence = safe(a.source), safe(a.output), safe(a.json); doc = json.loads(evidence.read_text(encoding="utf-8-sig"))
        if doc.get("applied") is not True or hashlib.sha256(output.read_bytes()).hexdigest() != doc.get("outputSha256", "").casefold(): fail("CLI application/hash evidence")
        before, after = npc(source, int(a.form_id, 0)), npc(output, int(a.form_id, 0)); plugin = Path(a.plugin).name
        if list_value(after, "KWDA", plugin) != a.keywords: fail("KWDA output mismatch")
        if list_value(after, "APPR", plugin) != a.appr: fail("APPR output mismatch")
        for signature in set(before) | set(after):
            if signature not in {"KWDA", "APPR", "KSIZ"} and before.get(signature) != after.get(signature): fail(f"non-target drift: {signature}")
        print("RESULT PASS KWDA/APPR raw lists and non-target preservation"); return 0
    except (Failure, OSError, ValueError, struct.error, json.JSONDecodeError) as error:
        print(f"RESULT FAIL {error}"); return 1

if __name__ == "__main__": raise SystemExit(main())
