#!/usr/bin/env python3
"""Independently verify the Chel MDNR + Hourglass install candidate."""

from __future__ import annotations

import hashlib
import importlib.util
import json
import struct
import zipfile
from collections import Counter
from pathlib import Path
from typing import Any
from xml.etree import ElementTree


ROOT = Path(r"K:\ExampleWorkspace")
PROJECT = ROOT / "projects" / "NpcManagerReimplementation"
PACKAGE_ROOT = (
    PROJECT
    / "03-builds"
    / "work"
    / "chel-ube-mdnr-hourglass-20260726-3"
    / "manager-package"
)
DATA = PACKAGE_ROOT / "Data"
ARCHIVE = (
    PROJECT
    / "04-packages"
    / "NpcManagerReimplementation-Chel-v0.9-mdnr-hourglass-runtime-test.zip"
)
REPORT = (
    PROJECT
    / "05-reports"
    / "chel-mdnr-hourglass-independent-verification-20260726.json"
)
ESP_TOOLS = ROOT / "tools" / "gates" / "lib" / "esp_tools.py"

EXPECTED = {
    "archive": "ac4309c4fc2528cea8bd33217b2da9c51ef4a33c681e8dfe17777e18a4c21734",
    "manifest": "358e95e63f69bf4f50fb9068dfe690e420950395d341a45ef7a3c5a12a11dd77",
    "plugin": "5e92e21aa25c9167f3121948f9fa64d8f6bac96d0835abaec5331d079c4c735f",
    "faceGeom": "83952d0fbe7c441cb227ea8c3801791efba14e183da8d88bde405af6b5f9e464",
    "faceTint": "dfdb072049c5a04ca277c219d71bbca32f3353828ba1e3fceb6e80292d8b5d9c",
    "preset": "4f312633973b70df356c03b96bc85b4b32345ec178c13761c3131836b751e0ce",
    "hourglass": "95f68cf553191a8fdcfbbc748848efe71f829dc261027f50397bdef78a898147",
    "mdnrConfig": "fc4510d0ea55b2d400416eedd381070e0804635d58d46bfbf04e58dac6864f9b",
}


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def load_esp_tools() -> Any:
    spec = importlib.util.spec_from_file_location("npcm_esp_tools", ESP_TOOLS)
    require(spec is not None and spec.loader is not None, "Cannot load esp_tools")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def decode_zstring(value: bytes) -> str:
    return value.rstrip(b"\0").decode("cp1252")


def verify_manifest() -> dict[str, Any]:
    manifest_path = PACKAGE_ROOT / "npcmanager-package.json"
    require(sha256(manifest_path) == EXPECTED["manifest"], "Manifest hash drift")
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    rows = manifest["artifacts"]
    declared = {row["relativePath"].replace("\\", "/"): row for row in rows}
    require(len(declared) == len(rows), "Duplicate manifest artifact path")
    actual = {
        path.relative_to(PACKAGE_ROOT).as_posix(): path
        for path in PACKAGE_ROOT.rglob("*")
        if path.is_file() and path != manifest_path
    }
    require(set(actual) == set(declared), "Declared/actual package inventory drift")
    for relative, path in actual.items():
        row = declared[relative]
        require(path.stat().st_size == row["byteLength"], f"Size drift: {relative}")
        require(sha256(path) == row["sha256"].lower(), f"Hash drift: {relative}")
    return {
        "sha256": EXPECTED["manifest"],
        "declaredFileCount": len(declared),
        "noUndeclaredFiles": True,
        "allDeclaredHashesReopened": True,
    }


def verify_plugin() -> dict[str, Any]:
    plugin = DATA / "ChelNpcManager.esp"
    require(sha256(plugin) == EXPECTED["plugin"], "Plugin hash drift")
    esp_tools = load_esp_tools()
    data = plugin.read_bytes()
    records: list[dict[str, Any]] = []

    def collect(
        signature: str,
        form_id: int,
        flags: int,
        body: bytes,
        depth: int,
    ) -> None:
        records.append(
            {
                "signature": signature,
                "formId": f"0x{form_id:08X}",
                "localFormId": f"0x{form_id & 0x00FFFFFF:08X}",
                "flags": f"0x{flags:08X}",
                "subrecords": [
                    name for name, _ in esp_tools.subrecords(body)
                ],
                "body": body,
            }
        )

    esp_tools.walk(data, collect)
    counts = Counter(row["signature"] for row in records)
    require(
        counts == Counter({"TXST": 1, "HDPT": 1, "NPC_": 1, "CLFM": 1}),
        f"Unexpected plugin record surface: {dict(counts)}",
    )
    npc = next(row for row in records if row["signature"] == "NPC_")
    require(npc["localFormId"] == "0x00000800", "NPC local FormID drift")
    npc_subrecords = list(esp_tools.subrecords(npc["body"]))
    editor_ids = [
        decode_zstring(value)
        for name, value in npc_subrecords
        if name == "EDID"
    ]
    require(editor_ids == ["ChelNpcManager"], "NPC EditorID drift")
    require(
        all(name != "WNAM" for name, _ in npc_subrecords),
        "NPC unexpectedly owns a private WNAM route",
    )

    tes4_size = struct.unpack_from("<I", data, 4)[0]
    tes4_body = data[24 : 24 + tes4_size]
    masters = [
        decode_zstring(value)
        for name, value in esp_tools.subrecords(tes4_body)
        if name == "MAST"
    ]
    require("UBE_AllRace.esp" in masters, "UBE race master is absent")
    return {
        "sha256": EXPECTED["plugin"],
        "byteLength": plugin.stat().st_size,
        "recordCounts": dict(sorted(counts.items())),
        "npcLocalFormId": "0x00000800",
        "npcEditorId": "ChelNpcManager",
        "masters": masters,
        "npcWnamPresent": False,
        "privateArmaCount": 0,
        "privateArmoCount": 0,
        "privateOutfitCount": 0,
        "independentParser": str(ESP_TOOLS),
    }


def verify_static_assets() -> dict[str, Any]:
    face_geom = (
        DATA
        / "meshes"
        / "actors"
        / "character"
        / "FaceGenData"
        / "FaceGeom"
        / "ChelNpcManager.esp"
        / "00000800.nif"
    )
    face_tint = (
        DATA
        / "textures"
        / "actors"
        / "character"
        / "FaceGenData"
        / "FaceTint"
        / "ChelNpcManager.esp"
        / "00000800.dds"
    )
    runtime_tint = (
        DATA / "SKSE" / "Plugins" / "CharGen" / "ChelNpcManager.dds"
    )
    preset = (
        DATA
        / "SKSE"
        / "Plugins"
        / "CharGen"
        / "Presets"
        / "UBE_Chel.jslot"
    )
    hourglass = (
        DATA
        / "CalienteTools"
        / "BodySlide"
        / "SliderPresets"
        / "Hourglass Body UBE.xml"
    )
    require(sha256(face_geom) == EXPECTED["faceGeom"], "FaceGeom hash drift")
    require(sha256(face_tint) == EXPECTED["faceTint"], "FaceTint hash drift")
    require(sha256(runtime_tint) == EXPECTED["faceTint"], "Runtime tint drift")
    require(sha256(preset) == EXPECTED["preset"], "Chel JSlot hash drift")
    require(sha256(hourglass) == EXPECTED["hourglass"], "Hourglass XML drift")

    dds = face_tint.read_bytes()
    require(dds[:4] == b"DDS " and len(dds) >= 128, "Malformed FaceTint DDS")
    height, width = struct.unpack_from("<II", dds, 12)
    require((width, height) == (512, 512), "FaceTint dimensions drift")

    root = ElementTree.parse(hourglass).getroot()
    presets = root.findall("Preset")
    require(len(presets) == 1, "Hourglass XML must contain one preset")
    body = presets[0]
    groups = [row.attrib.get("name") for row in body.findall("Group")]
    sliders = body.findall("SetSlider")
    require(body.attrib.get("name") == "Hourglass Body UBE", "Preset name drift")
    require(
        body.attrib.get("set") == "UBE SE 2.0 Release Body",
        "Hourglass target set drift",
    )
    require(groups == ["UBE"], "Hourglass group drift")
    require(len(sliders) == 58, "Hourglass slider count drift")
    pairs = [(row.attrib.get("name"), row.attrib.get("size")) for row in sliders]
    require(len(pairs) == len(set(pairs)), "Duplicate Hourglass slider row")
    return {
        "faceGeomSha256": EXPECTED["faceGeom"],
        "faceTintSha256": EXPECTED["faceTint"],
        "faceTintDimensions": {"width": width, "height": height},
        "runtimeTintMatchesStaticFaceTint": True,
        "presetSha256": EXPECTED["preset"],
        "hourglassSha256": EXPECTED["hourglass"],
        "hourglassPresetName": body.attrib["name"],
        "hourglassTargetSet": body.attrib["set"],
        "hourglassGroups": groups,
        "hourglassSliderCount": len(sliders),
    }


def verify_authorities() -> dict[str, Any]:
    evidence = DATA / "NPCManager" / "Evidence"
    standalone = json.loads(
        (evidence / "standalone-assets.json").read_text(encoding="utf-8")
    )
    require(standalone.get("schemaVersion") == 6, "Host is not schema 6")
    forbidden_authority = {
        "bodySlidePresetAuthority",
        "bodyMeshAuthority",
        "externalCharGenExportAuthority",
    }
    require(
        not forbidden_authority.intersection(standalone),
        "Schema 7 authority survived",
    )

    whole_skin = json.loads(
        (evidence / "whole-skin-authority.json").read_text(encoding="utf-8")
    )
    require(whole_skin.get("route") == "inherited-race", "Skin is not inherited")
    require(whole_skin.get("race") == "UBE_AllRace.esp|0x0005A18E", "Race drift")
    require(whole_skin.get("runtimeAuthority") is False, "False runtime claim")
    regions = whole_skin.get("regions", [])
    require(
        [row.get("region") for row in regions] == ["body", "hands", "feet"],
        "Whole-skin regions drift",
    )
    require(
        all(row.get("textureRoute") == "mesh-embedded" for row in regions),
        "Whole-skin texture route drift",
    )
    require(
        all(len(row.get("textureAssets", [])) == 3 for row in regions),
        "Whole-skin texture provider closure drift",
    )
    return {
        "standaloneSchemaVersion": 6,
        "schema7AuthoritiesPresent": False,
        "wholeSkinRoute": whole_skin["route"],
        "wholeSkinRace": whole_skin["race"],
        "wholeSkinRegions": [
            {
                "region": row["region"],
                "textureRoute": row["textureRoute"],
                "textureAssetCount": len(row["textureAssets"]),
            }
            for row in regions
        ],
        "runtimeAuthority": False,
    }


def verify_mdnr() -> dict[str, Any]:
    config_path = (
        DATA
        / "SKSE"
        / "Plugins"
        / "MuDynamicNPCReplacer"
        / "ChelNpcManager.json"
    )
    require(sha256(config_path) == EXPECTED["mdnrConfig"], "MDNR config drift")
    config = json.loads(config_path.read_text(encoding="utf-8"))
    actor = config["actors"][0]
    preset = actor["presets"][0]
    expected_apply = {
        "overrides": False,
        "bodymorphs": False,
        "transforms": False,
        "skinoverrides": False,
    }
    require(
        actor["condition"] == "IsActorBase(ChelNpcManager.esp|0x800)",
        "MDNR actor condition drift",
    )
    require(preset["applytype"] == expected_apply, "MDNR applytype drift")
    require(
        preset["presetfile"] == "CharGen\\Presets\\UBE_Chel.jslot",
        "MDNR JSlot route drift",
    )
    require(
        preset["tintfile"] == "CharGen\\ChelNpcManager.dds",
        "MDNR tint route drift",
    )
    require(
        preset["bodypresetfile"].endswith("Hourglass Body UBE.xml"),
        "MDNR BodySlide route drift",
    )
    return {
        "configSha256": EXPECTED["mdnrConfig"],
        "condition": actor["condition"],
        "applytype": expected_apply,
        "presetfile": preset["presetfile"],
        "tintfile": preset["tintfile"],
        "bodypresetfile": preset["bodypresetfile"],
        "externalDependencyBundled": False,
    }


def verify_forbidden_paths() -> dict[str, Any]:
    relative_files = [
        path.relative_to(DATA).as_posix()
        for path in DATA.rglob("*")
        if path.is_file()
    ]
    lowered = [path.lower() for path in relative_files]
    forbidden = [
        path
        for path in lowered
        if "zenithar" in path
        or "bodygendata/" in path
        or path.startswith("meshes/actors/character/chel/body/")
    ]
    require(not forbidden, f"Forbidden carryover: {forbidden}")
    mdnr_files = [
        path
        for path in lowered
        if path.startswith("skse/plugins/mudynamicnpcreplacer/")
    ]
    require(
        mdnr_files == [
            "skse/plugins/mudynamicnpcreplacer/chelnpcmanager.json"
        ],
        "Unexpected bundled MDNR files",
    )
    return {
        "noZenithar": True,
        "noBodyGen": True,
        "noPrivateChelBodyMeshes": True,
        "noMdnrBinary": True,
    }


def verify_archive() -> dict[str, Any]:
    require(sha256(ARCHIVE) == EXPECTED["archive"], "Archive hash drift")
    required = {
        "ChelNpcManager.esp": DATA / "ChelNpcManager.esp",
        (
            "meshes/actors/character/FaceGenData/FaceGeom/"
            "ChelNpcManager.esp/00000800.nif"
        ): (
            DATA
            / "meshes"
            / "actors"
            / "character"
            / "FaceGenData"
            / "FaceGeom"
            / "ChelNpcManager.esp"
            / "00000800.nif"
        ),
        (
            "textures/actors/character/FaceGenData/FaceTint/"
            "ChelNpcManager.esp/00000800.dds"
        ): (
            DATA
            / "textures"
            / "actors"
            / "character"
            / "FaceGenData"
            / "FaceTint"
            / "ChelNpcManager.esp"
            / "00000800.dds"
        ),
        (
            "SKSE/Plugins/CharGen/Presets/UBE_Chel.jslot"
        ): (
            DATA
            / "SKSE"
            / "Plugins"
            / "CharGen"
            / "Presets"
            / "UBE_Chel.jslot"
        ),
        "SKSE/Plugins/CharGen/ChelNpcManager.dds": (
            DATA / "SKSE" / "Plugins" / "CharGen" / "ChelNpcManager.dds"
        ),
        (
            "SKSE/Plugins/MuDynamicNPCReplacer/ChelNpcManager.json"
        ): (
            DATA
            / "SKSE"
            / "Plugins"
            / "MuDynamicNPCReplacer"
            / "ChelNpcManager.json"
        ),
        (
            "CalienteTools/BodySlide/SliderPresets/Hourglass Body UBE.xml"
        ): (
            DATA
            / "CalienteTools"
            / "BodySlide"
            / "SliderPresets"
            / "Hourglass Body UBE.xml"
        ),
    }
    with zipfile.ZipFile(ARCHIVE, "r") as archive:
        infos = [row for row in archive.infolist() if not row.is_dir()]
        names = [row.filename for row in infos]
        require(len(names) == len(set(names)), "Duplicate ZIP entry")
        require(all("\\" not in name for name in names), "Backslash ZIP entry")
        require(
            all(not name.lower().startswith("data/") for name in names),
            "ZIP has a Data wrapper",
        )
        lowered = [name.lower() for name in names]
        require(
            not any(
                "zenithar" in name
                or "bodygendata/" in name
                or name.startswith("meshes/actors/character/chel/body/")
                for name in lowered
            ),
            "ZIP contains forbidden body carryover",
        )
        require(
            set(required).issubset(names),
            "ZIP is missing a required runtime file",
        )
        for member, source in required.items():
            actual = hashlib.sha256(archive.read(member)).hexdigest()
            require(actual == sha256(source), f"ZIP member drift: {member}")
    return {
        "sha256": EXPECTED["archive"],
        "entryCount": len(names),
        "forwardSlashEntries": True,
        "noWrapperDirectory": True,
        "requiredMembersRehashed": len(required),
        "independentlyReopened": True,
    }


def main() -> int:
    require(not REPORT.exists(), f"Report already exists: {REPORT}")
    result = {
        "schema": "npcmanager-chel-mdnr-hourglass-independent-verification/1",
        "date": "2026-07-26",
        "verdict": "STATIC_PASS_RUNTIME_REQUIRED",
        "manifest": verify_manifest(),
        "plugin": verify_plugin(),
        "assets": verify_static_assets(),
        "authorities": verify_authorities(),
        "mdnr": verify_mdnr(),
        "forbiddenCarryover": verify_forbidden_paths(),
        "archive": verify_archive(),
        "packageGates": {
            "report": "05-reports/gates-package-2026-07-26.json",
            "overall": "PASS",
            "tier0Fail": 0,
            "fail": 0,
            "advisories": [
                "white HairTint on UBE lash shape",
                "white HairTint on UBE brow shape",
            ],
        },
        "mdnrRequiredVersion": "2.1.4",
        "mdnrInstalledAtBuildIntake": False,
        "runtimeAuthority": False,
    }
    REPORT.write_text(
        json.dumps(result, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    print(json.dumps(result, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
