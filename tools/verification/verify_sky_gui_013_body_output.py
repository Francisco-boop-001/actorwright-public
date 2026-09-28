#!/usr/bin/env python3
"""Independent raw TES4 verification for the SKY-GUI-013 NAM7 output."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import struct
import sys

PROJECT_ROOT = Path(__file__).resolve().parents[2]
WORKSPACE_ROOT = PROJECT_ROOT.parents[1]
sys.path.insert(0, str(WORKSPACE_ROOT / "tools" / "gates" / "lib"))

from esp_tools import subrecords, walk  # noqa: E402

REFERENCE_FIELDS = ("RNAM", "PNAM", "HCLF", "FTST")


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

    source_counts, source_target = inventory(source_data, 0x800)
    output_counts, output_target = inventory(output_data, 0x800)
    source_npc = source_target[1] if source_target is not None else None
    output_npc = output_target[1] if output_target is not None else None
    if source_npc is None:
        failures.append("source NPC 0x00000800 missing or duplicated")
    if output_npc is None:
        failures.append("output NPC 0x00000800 missing or duplicated")
    elif output_target is not None and output_target[0] != 0x00000800:
        failures.append(
            "output target is not owned by source master index 0: "
            f"0x{output_target[0]:08X}"
        )
    if output_counts != {"NPC_": 1}:
        failures.append(f"output-record-surface expected={{'NPC_': 1}} actual={output_counts!r}")
    for forbidden in ("WRLD", "CELL", "LAND", "NAVM", "NAVI"):
        if forbidden in output_counts:
            failures.append(f"output contains forbidden {forbidden} record surface")

    observed: dict[str, object] = {
        "source_masters": source_masters,
        "output_masters": output_masters,
        "source_record_counts": source_counts,
        "output_record_counts": output_counts,
        "source_target_form_id": (
            f"0x{source_target[0]:08X}" if source_target is not None else None
        ),
        "output_target_form_id": (
            f"0x{output_target[0]:08X}" if output_target is not None else None
        ),
    }
    if source_npc is not None and output_npc is not None:
        source_fields = field_map(source_npc)
        output_fields = field_map(output_npc)
        for signature in REFERENCE_FIELDS:
            source_refs = references(
                source_fields, signature, source_path.name, source_masters
            )
            output_refs = references(
                output_fields, signature, output_path.name, output_masters
            )
            if source_refs != output_refs:
                failures.append(
                    f"{signature} references expected={source_refs!r} actual={output_refs!r}"
                )
        signatures = set(source_fields) | set(output_fields)
        for signature in sorted(signatures - set(REFERENCE_FIELDS) - {"NAM7"}):
            if source_fields.get(signature, []) != output_fields.get(signature, []):
                failures.append(f"{signature} payloads were not preserved exactly")
        source_weight = one(source_fields, "NAM7", 4, failures, "source")
        output_weight = one(output_fields, "NAM7", 4, failures, "output")
        if source_weight is not None and output_weight is not None:
            before = struct.unpack("<f", source_weight)[0]
            after = struct.unpack("<f", output_weight)[0]
            observed["nam7_before"] = before
            observed["nam7_after"] = after
            if before != 55.0 or after != 60.0:
                failures.append(
                    f"NAM7 semantic delta expected=55.0->60.0 actual={before!r}->{after!r}"
                )
        if source_fields.get("VMAD") or output_fields.get("VMAD"):
            failures.append("source and output VMAD must both be absent")
        observed["source_references"] = {
            signature: references(
                source_fields, signature, source_path.name, source_masters
            ) for signature in REFERENCE_FIELDS
        }
        observed["output_references"] = {
            signature: references(
                output_fields, signature, output_path.name, output_masters
            ) for signature in REFERENCE_FIELDS
        }

    expected_changes = [
        {"field": "SkyrimWeight", "before": "55", "after": "60"}
    ]
    if proposal.get("schemaVersion") != 1:
        failures.append(f"proposal schemaVersion unexpected: {proposal.get('schemaVersion')!r}")
    if proposal.get("edition") != "skyrimSpecialEdition":
        failures.append(f"proposal edition unexpected: {proposal.get('edition')!r}")
    if Path(value(proposal.get("sourcePlugin"), "")).resolve() != source_path:
        failures.append("proposal sourcePlugin does not bind the exact source")
    if Path(value(proposal.get("outputPlugin"), "")).resolve() != output_path:
        failures.append("proposal outputPlugin does not bind the exact output")
    if value(proposal.get("proposalPath")) != str(proposal_path):
        failures.append("proposal proposalPath does not bind its exact persisted bytes")
    if value(proposal.get("sourcePluginName")) != source_path.name:
        failures.append("proposal sourcePluginName is not the selected source")
    if value(proposal.get("outputPluginName")) != output_path.name:
        failures.append("proposal outputPluginName is not the fresh output")
    if value(proposal.get("targetFormId")) != 0x800:
        failures.append(f"proposal targetFormId unexpected: {proposal.get('targetFormId')!r}")
    if value(proposal.get("sourceSha256"), "").lower() != hashlib.sha256(source_data).hexdigest():
        failures.append("proposal sourceSha256 does not bind the exact source")
    if proposal.get("requiredMasters") != [{"value": source_path.name}]:
        failures.append(
            f"proposal requiredMasters unexpected: {proposal.get('requiredMasters')!r}"
        )
    expected_patch = {
        "weight": {"skyrimValue": 60, "isEmpty": False},
        "isEmpty": False,
    }
    if proposal.get("patch") != expected_patch:
        failures.append(f"proposal patch unexpected: {proposal.get('patch')!r}")
    if proposal.get("changes") != expected_changes:
        failures.append(f"proposal changes unexpected: {proposal.get('changes')!r}")

    payload = {
        "schema": "npcmanager.sky-gui-013.body-output-raw-audit.v1",
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
        "body_slide_build_authority": False,
        "mesh_authority": False,
        "texture_render_authority": False,
        "runtime_authority": False,
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


def inventory(data: bytes, target: int) -> tuple[dict[str, int], tuple[int, bytes] | None]:
    counts: dict[str, int] = {}
    matches: list[tuple[int, bytes]] = []

    def capture(signature: str, form_id: int, flags: int, body: bytes, depth: int) -> None:
        del flags, depth
        counts[signature] = counts.get(signature, 0) + 1
        if signature == "NPC_" and (form_id & 0x00FFFFFF) == target:
            matches.append((form_id, body))

    walk(data, capture)
    return counts, matches[0] if len(matches) == 1 else None


def value(payload: object, default: object = None) -> object:
    if isinstance(payload, dict) and set(payload) == {"value"}:
        return payload["value"]
    return default


def field_map(body: bytes) -> dict[str, list[bytes]]:
    result: dict[str, list[bytes]] = {}
    for signature, payload in subrecords(body):
        result.setdefault(signature, []).append(payload)
    return result


def references(
    fields: dict[str, list[bytes]],
    signature: str,
    plugin: str,
    plugin_masters: list[str],
) -> list[str]:
    result: list[str] = []
    for payload in fields.get(signature, []):
        if len(payload) != 4:
            result.append(f"invalid-size-{len(payload)}")
            continue
        raw = struct.unpack("<I", payload)[0]
        index = (raw >> 24) & 0xFF
        local = raw & 0x00FFFFFF
        if index < len(plugin_masters):
            owner = plugin_masters[index]
        elif index == len(plugin_masters):
            owner = plugin
        else:
            owner = f"invalid-master-{index}"
        result.append(f"{owner}|0x{local:08X}")
    return result


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
