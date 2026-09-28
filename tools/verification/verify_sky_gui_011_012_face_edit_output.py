#!/usr/bin/env python3
"""Independent raw semantic comparison for the SKY-GUI-011/012 output ESP."""

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


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--proposal", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    source_path = Path(args.source).resolve()
    output_path = Path(args.output).resolve()
    proposal_path = Path(args.proposal).resolve()
    report_path = Path(args.report).resolve()
    for path in (source_path, output_path, proposal_path, report_path):
        ensure_under_project(path)
    for path in (source_path, output_path, proposal_path):
        if not path.is_file():
            raise FileNotFoundError(f"Required transaction artifact is missing: {path}")

    source_data = source_path.read_bytes()
    output_data = output_path.read_bytes()
    proposal = json.loads(proposal_path.read_text(encoding="utf-8-sig"))
    failures: list[str] = []
    source_masters = masters(source_data)
    output_masters = masters(output_data)
    if output_masters != [source_path.name]:
        failures.append(
            f"output-masters expected={[source_path.name]!r} actual={output_masters!r}"
        )

    source_counts, source_npc = inventory(source_data, 0x800)
    output_counts, output_npc = inventory(output_data, 0x800)
    if source_npc is None:
        failures.append("source NPC 0x00000800 missing or duplicated")
    if output_npc is None:
        failures.append("output NPC 0x00000800 missing or duplicated")
    if output_counts != {"NPC_": 1}:
        failures.append(f"output-record-surface expected={{'NPC_': 1}} actual={output_counts!r}")
    if any(signature in output_counts for signature in ("WRLD", "CELL")):
        failures.append("output contains forbidden WRLD or CELL records")

    observed: dict[str, object] = {
        "source_masters": source_masters,
        "output_masters": output_masters,
        "source_record_counts": source_counts,
        "output_record_counts": output_counts,
    }
    if source_npc is not None and output_npc is not None:
        source_fields = field_map(source_npc)
        output_fields = field_map(output_npc)
        source_name = source_path.name
        output_name = output_path.name
        for signature in ("RNAM", "HCLF", "FTST"):
            compare_reference_list(
                failures,
                signature,
                source_fields,
                source_name,
                source_masters,
                output_fields,
                output_name,
                output_masters,
            )
        compare_reference_list(
            failures,
            "PNAM",
            source_fields,
            source_name,
            source_masters,
            output_fields,
            output_name,
            output_masters,
        )
        for signature in ("NAM7", "NAMA", "TINI", "TINC", "TINV", "TIAS", "QNAM"):
            compare_payloads(failures, signature, source_fields, output_fields)
        if output_fields.get("VMAD"):
            failures.append("output VMAD expected=absent actual=present")
        if source_fields.get("VMAD"):
            failures.append("source VMAD expected=absent actual=present")

        source_nam9 = one(source_fields, "NAM9", 76, failures, "source")
        output_nam9 = one(output_fields, "NAM9", 76, failures, "output")
        if source_nam9 is not None and output_nam9 is not None:
            before = list(struct.unpack("<19f", source_nam9))
            after = list(struct.unpack("<19f", output_nam9))
            changed = [index for index, pair in enumerate(zip(before, after, strict=True))
                       if not math.isclose(pair[0], pair[1], rel_tol=0.0, abs_tol=1e-7)]
            observed["nam9_before"] = before
            observed["nam9_after"] = after
            observed["nam9_changed_indices"] = changed
            if changed != [0] or before[0] != 0.0 or after[0] != 0.25:
                failures.append(
                    f"NAM9 semantic delta expected=index0 0.0->0.25 actual={changed!r} "
                    f"values={before[0]!r}->{after[0]!r}"
                )
            if before[18] != 0.125 or after[18] != 0.125:
                failures.append("NAM9 trailing value 0.125 was not preserved")
        observed["source_references"] = references(
            source_fields, source_name, source_masters
        )
        observed["output_references"] = references(
            output_fields, output_name, output_masters
        )

    artifact_kind = proposal.get("artifactKind")
    if artifact_kind != "skyrim-npc-appearance-override-proposal":
        failures.append(f"proposal artifactKind unexpected: {artifact_kind!r}")
    if proposal.get("sourceSha256", "").lower() != hashlib.sha256(source_data).hexdigest():
        failures.append("proposal sourceSha256 does not bind the exact source")
    if proposal.get("targetFormId") != "0x00000800":
        failures.append(f"proposal targetFormId unexpected: {proposal.get('targetFormId')!r}")
    if Path(proposal.get("outputPlugin", "")).resolve() != output_path:
        failures.append("proposal outputPlugin does not bind the exact output")

    payload = {
        "schema": "npcmanager.sky-gui-011-012.face-edit-output-raw-audit.v1",
        "source": str(source_path),
        "source_sha256": hashlib.sha256(source_data).hexdigest(),
        "output": str(output_path),
        "output_sha256": hashlib.sha256(output_data).hexdigest(),
        "output_length": len(output_data),
        "proposal": str(proposal_path),
        "proposal_sha256": hashlib.sha256(proposal_path.read_bytes()).hexdigest(),
        "verifier": "workspace esp_tools.py raw TES4 semantic comparison",
        "observed": observed,
        "failures": failures,
        "passed": not failures,
        "runtime_authority": False,
        "facegen_authority": False,
        "texture_render_authority": False,
        "visual_authority": False,
    }
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report_path.write_text(json.dumps(payload, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(payload, indent=2))
    return 0 if not failures else 1


def ensure_under_project(path: Path) -> None:
    try:
        path.relative_to(PROJECT_ROOT)
    except ValueError as error:
        raise ValueError(f"Path escaped project root: {path}") from error


def masters(data: bytes) -> list[str]:
    if len(data) < 24 or data[:4] != b"TES4":
        raise ValueError("Input is not a TES4 plugin")
    size = struct.unpack_from("<I", data, 4)[0]
    return [payload.rstrip(b"\0").decode("cp1252")
            for signature, payload in subrecords(data[24:24 + size])
            if signature == "MAST"]


def inventory(data: bytes, target: int) -> tuple[dict[str, int], bytes | None]:
    counts: dict[str, int] = {}
    matches: list[bytes] = []

    def capture(signature: str, form_id: int, flags: int, body: bytes, depth: int) -> None:
        del flags, depth
        counts[signature] = counts.get(signature, 0) + 1
        if signature == "NPC_" and (form_id & 0x00FFFFFF) == target:
            matches.append(body)

    walk(data, capture)
    return counts, matches[0] if len(matches) == 1 else None


def field_map(body: bytes) -> dict[str, list[bytes]]:
    result: dict[str, list[bytes]] = {}
    for signature, payload in subrecords(body):
        result.setdefault(signature, []).append(payload)
    return result


def resolve_reference(raw: int, plugin: str, plugin_masters: list[str]) -> str:
    index = (raw >> 24) & 0xFF
    local = raw & 0x00FFFFFF
    # A raw FormID index addresses the zero-based TES4 MAST array first; the
    # file itself follows all masters. Therefore index zero is self only when
    # the plugin has no masters. Treating index zero as unconditionally self
    # mislabels every reference to the first master in an override plugin.
    if index < len(plugin_masters):
        owner = plugin_masters[index]
    elif index == len(plugin_masters):
        owner = plugin
    else:
        owner = f"invalid-master-{index}"
    return f"{owner}|0x{local:08X}"


def references(
    fields: dict[str, list[bytes]], plugin: str, plugin_masters: list[str]
) -> dict[str, list[str]]:
    result: dict[str, list[str]] = {}
    for signature in ("RNAM", "PNAM", "HCLF", "FTST"):
        values = fields.get(signature, [])
        result[signature] = [resolve_reference(struct.unpack("<I", payload)[0],
                                                plugin, plugin_masters)
                             for payload in values if len(payload) == 4]
    return result


def compare_reference_list(
    failures: list[str],
    signature: str,
    source_fields: dict[str, list[bytes]],
    source_plugin: str,
    source_masters: list[str],
    output_fields: dict[str, list[bytes]],
    output_plugin: str,
    output_masters: list[str],
) -> None:
    source_values = references(source_fields, source_plugin, source_masters)[signature]
    output_values = references(output_fields, output_plugin, output_masters)[signature]
    if source_values != output_values:
        failures.append(
            f"{signature} references expected={source_values!r} actual={output_values!r}"
        )


def compare_payloads(
    failures: list[str],
    signature: str,
    source_fields: dict[str, list[bytes]],
    output_fields: dict[str, list[bytes]],
) -> None:
    if source_fields.get(signature, []) != output_fields.get(signature, []):
        failures.append(f"{signature} payloads were not preserved exactly")


def one(
    fields: dict[str, list[bytes]],
    signature: str,
    size: int,
    failures: list[str],
    role: str,
) -> bytes | None:
    values = fields.get(signature, [])
    if len(values) != 1 or len(values[0]) != size:
        failures.append(
            f"{role} {signature} expected=one {size}-byte payload "
            f"actual-count={len(values)} sizes={[len(value) for value in values]!r}"
        )
        return None
    return values[0]


if __name__ == "__main__":
    raise SystemExit(main())
