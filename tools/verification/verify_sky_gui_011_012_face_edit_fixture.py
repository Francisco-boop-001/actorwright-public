#!/usr/bin/env python3
"""Independent raw TES4 audit for the SKY-GUI-011/012 face-edit fixture."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
from pathlib import Path
import struct
import sys

PROJECT_ROOT = Path(__file__).resolve().parents[2]
WORKSPACE_ROOT = PROJECT_ROOT.parents[1]
sys.path.insert(0, str(WORKSPACE_ROOT / "tools" / "gates" / "lib"))

from esp_tools import subrecords, walk  # noqa: E402


EXPECTED_COUNTS = {
    "NPC_": 1,
    "RACE": 1,
    "CLFM": 1,
    "FLST": 1,
    "TXST": 1,
    "HDPT": 10,
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--plugin", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    plugin = Path(args.plugin).resolve()
    report = Path(args.report).resolve()
    ensure_under_project(plugin)
    ensure_under_project(report)
    if not plugin.is_file():
        raise FileNotFoundError(f"Fixture plugin is missing: {plugin}")

    data = plugin.read_bytes()
    if len(data) < 24 or data[:4] != b"TES4":
        raise ValueError("Fixture is not a TES4 plugin.")
    failures: list[str] = []
    counts: dict[str, int] = {}
    npc_rows: list[tuple[int, list[tuple[str, bytes]]]] = []

    def capture(signature: str, form_id: int, flags: int, body: bytes, depth: int) -> None:
        del flags, depth
        counts[signature] = counts.get(signature, 0) + 1
        if signature == "NPC_" and form_id == 0x800:
            npc_rows.append((form_id, list(subrecords(body))))

    walk(data, capture)
    if counts != EXPECTED_COUNTS:
        failures.append(f"record-counts expected={EXPECTED_COUNTS!r} actual={counts!r}")
    if len(npc_rows) != 1:
        failures.append(f"npc-00000800 expected=1 actual={len(npc_rows)}")

    observed: dict[str, object] = {
        "record_counts": counts,
        "tes4_form_version": struct.unpack_from("<H", data, 20)[0],
    }
    if observed["tes4_form_version"] != 44:
        failures.append(
            f"tes4-form-version expected=44 actual={observed['tes4_form_version']}"
        )

    if npc_rows:
        rows = npc_rows[0][1]
        by_signature: dict[str, list[bytes]] = {}
        for signature, payload in rows:
            by_signature.setdefault(signature, []).append(payload)
        pnam = [struct.unpack("<I", value)[0] for value in by_signature.get("PNAM", [])]
        observed.update(
            {
                "npc_subrecord_signatures": [signature for signature, _ in rows],
                "pnam": [f"0x{value:08X}" for value in pnam],
                "vmad_count": len(by_signature.get("VMAD", [])),
            }
        )
        expect_equal(failures, "pnam", pnam, list(range(0x811, 0x81A)))
        expect_single_u32(failures, by_signature, "RNAM", 0x801)
        expect_single_u32(failures, by_signature, "HCLF", 0x802)
        expect_single_u32(failures, by_signature, "FTST", 0x804)
        if by_signature.get("VMAD"):
            failures.append("vmad expected=absent actual=present")

        nam7 = expect_single(failures, by_signature, "NAM7", 4)
        nam9 = expect_single(failures, by_signature, "NAM9", 76)
        nama = expect_single(failures, by_signature, "NAMA", 16)
        qnam = expect_single(failures, by_signature, "QNAM", 12)
        tini = expect_single(failures, by_signature, "TINI", 2)
        tinc = expect_single(failures, by_signature, "TINC", 4)
        tinv = expect_single(failures, by_signature, "TINV", 4)
        tias = expect_single(failures, by_signature, "TIAS", 2)

        if nam7 is not None:
            weight = struct.unpack("<f", nam7)[0]
            observed["weight"] = weight
            expect_close(failures, "weight", weight, 55.0)
        if nam9 is not None:
            values = list(struct.unpack("<19f", nam9))
            observed["nam9"] = values
            if any(value != 0.0 for value in values[:18]) or values[18] != 0.125:
                failures.append(f"nam9 expected=18x0+0.125 actual={values!r}")
        if nama is not None:
            values = list(struct.unpack("<4I", nama))
            observed["nama"] = values
            expect_equal(failures, "nama", values, [0xFFFFFFFF] * 4)
        if qnam is not None:
            values = list(struct.unpack("<3f", qnam))
            observed["qnam"] = values
            for index, (actual, expected) in enumerate(
                zip(values, (32 / 255, 96 / 255, 160 / 255), strict=True)
            ):
                expect_close(failures, f"qnam[{index}]", actual, expected)
        if tini is not None:
            expect_equal(failures, "tini", struct.unpack("<H", tini)[0], 1)
        if tinc is not None:
            expect_equal(failures, "tinc", list(tinc), [10, 20, 30, 255])
        if tinv is not None:
            expect_equal(failures, "tinv", struct.unpack("<I", tinv)[0], 75)
        if tias is not None:
            expect_equal(failures, "tias", struct.unpack("<h", tias)[0], 2)

    payload = {
        "schema": "npcmanager.sky-gui-011-012.face-edit-fixture-raw-audit.v1",
        "plugin": str(plugin),
        "plugin_sha256": hashlib.sha256(data).hexdigest(),
        "plugin_length": len(data),
        "verifier": "workspace esp_tools.py raw TES4 walk",
        "observed": observed,
        "failures": failures,
        "passed": not failures,
        "runtime_authority": False,
        "visual_authority": False,
    }
    report.parent.mkdir(parents=True, exist_ok=True)
    report.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(payload, indent=2))
    return 0 if not failures else 1


def ensure_under_project(path: Path) -> None:
    try:
        path.relative_to(PROJECT_ROOT)
    except ValueError as error:
        raise ValueError(f"Path escaped project root: {path}") from error


def expect_single(
    failures: list[str],
    values: dict[str, list[bytes]],
    signature: str,
    size: int,
) -> bytes | None:
    rows = values.get(signature, [])
    if len(rows) != 1:
        failures.append(f"{signature} expected-count=1 actual={len(rows)}")
        return None
    if len(rows[0]) != size:
        failures.append(f"{signature} expected-size={size} actual={len(rows[0])}")
        return None
    return rows[0]


def expect_single_u32(
    failures: list[str],
    values: dict[str, list[bytes]],
    signature: str,
    expected: int,
) -> None:
    payload = expect_single(failures, values, signature, 4)
    if payload is not None:
        expect_equal(failures, signature, struct.unpack("<I", payload)[0], expected)


def expect_equal(failures: list[str], label: str, actual: object, expected: object) -> None:
    if actual != expected:
        failures.append(f"{label} expected={expected!r} actual={actual!r}")


def expect_close(
    failures: list[str], label: str, actual: float, expected: float
) -> None:
    if not math.isclose(actual, expected, rel_tol=0.0, abs_tol=1e-6):
        failures.append(f"{label} expected={expected!r} actual={actual!r}")


if __name__ == "__main__":
    raise SystemExit(main())
