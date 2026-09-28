#!/usr/bin/env python3
"""Independent raw TES4 audit for SKY-GUI-015 new and override OTFT outputs."""

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

FORBIDDEN = {"WRLD", "CELL", "LAND", "WATR", "NAVM", "NAVI", "VMAD"}
EXPECTED_MASTERS = ["OutfitProvider.esp", "OutfitBase.esm"]
NEW_ITEMS = [
    "OutfitBase.esm|0x00000800",
    "OutfitBase.esm|0x00000810",
]
OVERRIDE_ITEMS = [
    "OutfitBase.esm|0x00000810",
    "OutfitBase.esm|0x00000800",
]


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-provider", required=True)
    parser.add_argument("--new-plugin", required=True)
    parser.add_argument("--new-proposal", required=True)
    parser.add_argument("--override-plugin", required=True)
    parser.add_argument("--override-proposal", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    paths = {name: Path(value).resolve() for name, value in vars(args).items()}
    for path in paths.values():
        ensure_under_project(path)
    for name, path in paths.items():
        if name != "report" and not path.is_file():
            raise FileNotFoundError(f"Required artifact is missing: {path}")

    source_hash = sha256(paths["source_provider"])
    failures: list[str] = []
    observed: dict[str, object] = {}
    observed["new"] = inspect_output(
        paths["new_plugin"],
        paths["new_proposal"],
        expected_owner=paths["new_plugin"].name,
        expected_form=0xA00,
        expected_editor="NpcManager_NewOutfit",
        expected_mode="new",
        expected_items=NEW_ITEMS,
        source_provider=paths["source_provider"],
        source_hash=source_hash,
        failures=failures,
    )
    observed["override"] = inspect_output(
        paths["override_plugin"],
        paths["override_proposal"],
        expected_owner="OutfitBase.esm",
        expected_form=0x900,
        expected_editor="ProductionBaseOutfit",
        expected_mode="override",
        expected_items=OVERRIDE_ITEMS,
        source_provider=paths["source_provider"],
        source_hash=source_hash,
        failures=failures,
    )
    payload = {
        "schema": "npcmanager.sky-gui-015.outfit-raw-audit.v1",
        "passed": not failures,
        "source_provider": str(paths["source_provider"]),
        "source_provider_sha256": source_hash,
        "new_plugin_sha256": sha256(paths["new_plugin"]),
        "new_proposal_sha256": sha256(paths["new_proposal"]),
        "override_plugin_sha256": sha256(paths["override_plugin"]),
        "override_proposal_sha256": sha256(paths["override_proposal"]),
        "observed": observed,
        "failures": failures,
        "preview_authority": False,
        "equipment_authority": False,
        "runtime_authority": False,
        "visual_authority": False,
    }
    paths["report"].parent.mkdir(parents=True, exist_ok=True)
    paths["report"].write_text(
        json.dumps(payload, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(payload, indent=2))
    return 0 if not failures else 1


def inspect_output(
    plugin: Path,
    proposal_path: Path,
    *,
    expected_owner: str,
    expected_form: int,
    expected_editor: str,
    expected_mode: str,
    expected_items: list[str],
    source_provider: Path,
    source_hash: str,
    failures: list[str],
) -> dict[str, object]:
    label = expected_mode
    data = plugin.read_bytes()
    output_masters = masters(data)
    if output_masters != EXPECTED_MASTERS:
        failures.append(
            f"{label} masters expected={EXPECTED_MASTERS!r} actual={output_masters!r}"
        )
    counts, outfits = inventory(data, plugin.name, output_masters)
    if counts != {"OTFT": 1}:
        failures.append(f"{label} record surface expected={{'OTFT': 1}} actual={counts!r}")
    for signature in sorted(FORBIDDEN & set(counts)):
        failures.append(f"{label} contains forbidden {signature} record surface")
    record = outfits[0] if len(outfits) == 1 else None
    if record is None:
        failures.append(f"{label} output did not expose exactly one OTFT")
        owner = None
        local_form = None
        editor = None
        items: list[str] = []
    else:
        owner, local_form, body = record
        fields = field_map(body)
        editor = decode_zstring(one(fields, "EDID", failures, label))
        items = [
            resolve_raw_reference(raw, plugin.name, output_masters)
            for payload in fields.get("INAM", [])
            for raw in unpack_form_ids(payload)
        ]
        if owner.lower() != expected_owner.lower() or local_form != expected_form:
            failures.append(
                f"{label} identity expected={expected_owner}|0x{expected_form:08X} "
                f"actual={owner}|0x{(local_form or 0):08X}"
            )
        if editor != expected_editor:
            failures.append(
                f"{label} EditorID expected={expected_editor!r} actual={editor!r}"
            )
        if items != expected_items:
            failures.append(
                f"{label} ordered INAM expected={expected_items!r} actual={items!r}"
            )

    proposal = load_json(proposal_path)
    expected_target = f"0x{expected_form:08X}"
    exact = {
        "schemaVersion": "1",
        "artifactKind": "outfit-record-proposal",
        "edition": "skyrimse",
        "mode": expected_mode,
        "sourceFormId": "0x00000900",
        "editorId": expected_editor,
        "inputSha256": source_hash.lower(),
        "items": expected_items,
        "noUnrelatedRecords": True,
        "targetFormId": expected_target,
        "sourceOwnerPlugin": "OutfitBase.esm",
    }
    for key, expected in exact.items():
        actual = proposal.get(key)
        if key == "inputSha256" and isinstance(actual, str):
            actual = actual.lower()
        if actual != expected:
            failures.append(
                f"{label} proposal {key} expected={expected!r} actual={actual!r}"
            )
    source_plugin = Path(str(proposal.get("sourcePlugin", ""))).resolve()
    if source_plugin != source_provider:
        failures.append(f"{label} proposal sourcePlugin is not the copied winning provider")
    return {
        "record_counts": counts,
        "masters": output_masters,
        "owner": owner,
        "local_form_id": None if local_form is None else f"0x{local_form:08X}",
        "editor_id": editor,
        "ordered_items": items,
        "proposal_mode": proposal.get("mode"),
        "proposal_target": proposal.get("targetFormId"),
    }
def ensure_under_project(path: Path) -> None:
    try:
        path.relative_to(PROJECT_ROOT)
    except ValueError as error:
        raise ValueError(f"Path escaped project root: {path}") from error


def load_json(path: Path) -> dict[str, object]:
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError(f"Expected JSON object: {path}")
    return value


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def masters(data: bytes) -> list[str]:
    if len(data) < 24 or data[:4] != b"TES4":
        raise ValueError("Input is not a TES4 plugin")
    size = struct.unpack_from("<I", data, 4)[0]
    return [payload.rstrip(b"\0").decode("cp1252")
            for signature, payload in subrecords(data[24:24 + size])
            if signature == "MAST"]


def inventory(
    data: bytes, self_plugin: str, plugin_masters: list[str]
) -> tuple[dict[str, int], list[tuple[str, int, bytes]]]:
    counts: dict[str, int] = {}
    outfits: list[tuple[str, int, bytes]] = []

    def capture(signature: str, form_id: int, flags: int, body: bytes, depth: int) -> None:
        del flags, depth
        counts[signature] = counts.get(signature, 0) + 1
        if signature == "OTFT":
            owner, local = resolve_form_id(form_id, self_plugin, plugin_masters)
            outfits.append((owner, local, body))

    walk(data, capture)
    return counts, outfits


def field_map(body: bytes) -> dict[str, list[bytes]]:
    result: dict[str, list[bytes]] = {}
    for signature, payload in subrecords(body):
        result.setdefault(signature, []).append(payload)
    return result


def one(
    fields: dict[str, list[bytes]], signature: str,
    failures: list[str], role: str
) -> bytes:
    values = fields.get(signature, [])
    if len(values) != 1:
        failures.append(f"{role} {signature} is missing or duplicated")
        return b""
    return values[0]


def decode_zstring(payload: bytes) -> str:
    return payload.rstrip(b"\0").decode("cp1252")


def unpack_form_ids(payload: bytes) -> list[int]:
    if not payload or len(payload) % 4:
        raise ValueError("INAM payload is not a nonempty packed FormID sequence")
    return [struct.unpack_from("<I", payload, offset)[0]
            for offset in range(0, len(payload), 4)]


def resolve_raw_reference(
    raw: int, self_plugin: str, plugin_masters: list[str]
) -> str:
    owner, local = resolve_form_id(raw, self_plugin, plugin_masters)
    return f"{owner}|0x{local:08X}"


def resolve_form_id(
    raw: int, self_plugin: str, plugin_masters: list[str]
) -> tuple[str, int]:
    index = raw >> 24
    if index < len(plugin_masters):
        owner = plugin_masters[index]
    elif index == len(plugin_masters):
        owner = self_plugin
    else:
        raise ValueError(f"Raw FormID 0x{raw:08X} exceeds the ordinary master table")
    return owner, raw & 0x00FFFFFF


if __name__ == "__main__":
    raise SystemExit(main())
