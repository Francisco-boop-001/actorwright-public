#!/usr/bin/env python3
"""Independent raw TES4 and copied-NIF audit for the SKY-GUI-021 Briar corpus."""

from __future__ import annotations

import argparse
import json
from pathlib import Path

import verify_sky_gui_018_armor as base
import verify_sky_gui_020_armor_addon as gate020


SOURCE = "Briar.esp"
MASTERS = [SOURCE, "Skyrim.esm"]
SOURCE_SHA256 = "40dc31b288cb3fbb6510060df1671d34b308a4102f9347df09c8bef561b4b62c"
ARMA_PATH = r"armor\Briar\Briar_1.nif"
ARMO_PATH = r"armor\Briar\Briar_GND.nif"
ARMA_NIF_SHA256 = "19dcfe2dc1545f823e2dc5df2a44571568fe27ff8ffff2899215c34c77af4c88"
ARMO_NIF_SHA256 = "5c5fe6d84ba76f8bd82df7ac1dedec07190e62eedb5606f8a8ff47854d56ad9d"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--armor-addon-plugin", required=True)
    parser.add_argument("--armor-addon-proposal", required=True)
    parser.add_argument("--armor-plugin", required=True)
    parser.add_argument("--armor-proposal", required=True)
    parser.add_argument("--armor-addon-nif", required=True)
    parser.add_argument("--armor-world-nif", required=True)
    parser.add_argument("--report", required=True)
    parser.add_argument("--expect-forward-slashes", action="store_true")
    args = parser.parse_args()
    paths = {
        key: Path(value).resolve()
        for key, value in vars(args).items()
        if key != "expect_forward_slashes"
    }
    for path in paths.values():
        base.ensure_under_project(path)
    for key, path in paths.items():
        if key != "report" and not path.is_file():
            raise FileNotFoundError(f"Required artifact is missing: {path}")

    expected_arma = ARMA_PATH.replace("\\", "/") if args.expect_forward_slashes else ARMA_PATH
    expected_armo = ARMO_PATH.replace("\\", "/") if args.expect_forward_slashes else ARMO_PATH
    failures: list[str] = []
    arma = inspect_arma(
        paths["armor_addon_plugin"],
        paths["armor_addon_proposal"],
        expected_arma,
        failures,
    )
    armo = inspect_armo(
        paths["armor_plugin"],
        paths["armor_proposal"],
        expected_armo,
        failures,
    )
    verify_hash(
        paths["armor_addon_nif"], ARMA_NIF_SHA256, "ARMA selected NIF", failures
    )
    verify_hash(
        paths["armor_world_nif"], ARMO_NIF_SHA256, "ARMO selected NIF", failures
    )

    payload = {
        "schema": "npcmanager.sky-gui-021.real-briar-raw-audit.v1",
        "passed": not failures,
        "expectationProfile": (
            "forward-slash-negative-control"
            if args.expect_forward_slashes
            else "real-installed-briar-paths"
        ),
        "sourcePluginSha256": SOURCE_SHA256,
        "hashes": {
            key: base.sha256(path)
            for key, path in paths.items()
            if key != "report"
        },
        "observed": {
            "armorAddon": arma,
            "armor": armo,
        },
        "failures": failures,
        "realCopiedPluginCompatibility": not failures,
        "realCopiedMeshPathPersistence": not failures,
        "equipmentAuthority": False,
        "runtimeAuthority": False,
        "visualAuthority": False,
    }
    paths["report"].parent.mkdir(parents=True, exist_ok=True)
    paths["report"].write_text(
        json.dumps(payload, indent=2) + "\n", encoding="utf-8"
    )
    print(json.dumps(payload, indent=2))
    return 0 if not failures else 1


def inspect_arma(
    plugin: Path,
    proposal_path: Path,
    expected_path: str,
    failures: list[str],
) -> dict[str, object]:
    data = plugin.read_bytes()
    masters = base.masters(data)
    check_equal("ARMA masters", masters, MASTERS, failures)
    counts, records = base.inventory(data, plugin.name, masters, "ARMA")
    check_equal("ARMA record surface", counts, {"ARMA": 1}, failures)
    base.forbid(counts, "ARMA", failures)
    actual: dict[str, object] = {}
    if len(records) != 1:
        failures.append("ARMA output did not expose exactly one record")
    else:
        owner, local_form, major_flags, body = records[0]
        fields = base.field_map(body)
        actual = {
            "owner": owner,
            "provider": plugin.name,
            "localFormId": f"0x{local_form:08X}",
            "majorFlags": major_flags,
            "editorId": base.decode_zstring(
                base.one(fields, "EDID", failures, "ARMA")
            ),
            "maleModel": gate020.optional_zstring(fields, "MOD2"),
            "femaleModel": gate020.optional_zstring(fields, "MOD3"),
            "maleFirstPersonModel": gate020.optional_zstring(fields, "MOD4"),
            "femaleFirstPersonModel": gate020.optional_zstring(fields, "MOD5"),
            "subrecordSequence": [
                signature for signature, _ in base.subrecords(body)
            ],
        }
    check_equal("ARMA owner", actual.get("owner"), SOURCE, failures)
    check_equal("ARMA local FormID", actual.get("localFormId"), "0x00000D63", failures)
    check_equal("ARMA EditorID", actual.get("editorId"), "BriarCuriassAA", failures)
    for key in (
        "maleModel", "femaleModel", "maleFirstPersonModel", "femaleFirstPersonModel"
    ):
        check_equal(f"ARMA {key}", actual.get(key), expected_path, failures)
    inspect_proposal(
        proposal_path,
        "armor-addon-record-proposal",
        "0x00000D63",
        "BriarCuriassAA",
        "maleModel",
        expected_path,
        failures,
    )
    return {"recordCounts": counts, "masters": masters, **actual}


def inspect_armo(
    plugin: Path,
    proposal_path: Path,
    expected_path: str,
    failures: list[str],
) -> dict[str, object]:
    data = plugin.read_bytes()
    masters = base.masters(data)
    check_equal("ARMO masters", masters, MASTERS, failures)
    counts, records = base.inventory(data, plugin.name, masters, "ARMO")
    check_equal("ARMO record surface", counts, {"ARMO": 1}, failures)
    base.forbid(counts, "ARMO", failures)
    actual: dict[str, object] = {}
    if len(records) != 1:
        failures.append("ARMO output did not expose exactly one record")
    else:
        owner, local_form, major_flags, body = records[0]
        fields = base.field_map(body)
        actual = {
            "owner": owner,
            "provider": plugin.name,
            "localFormId": f"0x{local_form:08X}",
            "majorFlags": major_flags,
            "editorId": base.decode_zstring(
                base.one(fields, "EDID", failures, "ARMO")
            ),
            "maleWorldModel": gate020.optional_zstring(fields, "MOD2"),
            "femaleWorldModel": gate020.optional_zstring(fields, "MOD4"),
            "subrecordSequence": [
                signature for signature, _ in base.subrecords(body)
            ],
        }
    check_equal("ARMO owner", actual.get("owner"), SOURCE, failures)
    check_equal("ARMO local FormID", actual.get("localFormId"), "0x00000D62", failures)
    check_equal("ARMO EditorID", actual.get("editorId"), "BriarCuriass", failures)
    check_equal("ARMO male world model", actual.get("maleWorldModel"), expected_path, failures)
    check_equal("ARMO female world model", actual.get("femaleWorldModel"), expected_path, failures)
    inspect_proposal(
        proposal_path,
        "armor-record-proposal",
        "0x00000D62",
        "BriarCuriass",
        "maleWorldModel",
        expected_path,
        failures,
    )
    return {"recordCounts": counts, "masters": masters, **actual}


def inspect_proposal(
    path: Path,
    artifact_kind: str,
    form_id: str,
    editor_id: str,
    field: str,
    expected_path: str,
    failures: list[str],
) -> None:
    proposal = base.load_json(path)
    expected = {
        "schemaVersion": "1",
        "artifactKind": artifact_kind,
        "edition": "skyrimse",
        "mode": "override",
        "sourceFormId": form_id,
        "editorId": editor_id,
        "inputSha256": SOURCE_SHA256,
        field: expected_path,
        "changedFields": [field],
        "noUnrelatedRecords": True,
    }
    for key, value in expected.items():
        check_equal(f"{artifact_kind} proposal {key}", proposal.get(key), value, failures)
    if Path(str(proposal.get("sourcePlugin", ""))).name != SOURCE:
        failures.append(
            f"{artifact_kind} proposal sourcePlugin does not end in {SOURCE!r}"
        )


def verify_hash(
    path: Path, expected: str, role: str, failures: list[str]
) -> None:
    actual = base.sha256(path)
    if actual.lower() != expected.lower():
        failures.append(f"{role} hash expected={expected} actual={actual}")


def check_equal(
    role: str, actual: object, expected: object, failures: list[str]
) -> None:
    if actual != expected:
        failures.append(f"{role} expected={expected!r} actual={actual!r}")


if __name__ == "__main__":
    raise SystemExit(main())
