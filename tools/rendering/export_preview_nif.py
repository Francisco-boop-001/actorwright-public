"""Export a hash-bound preview scene to a new K-local NIF through PyNifly.

This is a narrow two-pass sandbox adapter. It never chooses a live game path,
does not mutate source NIFs, and returns structured status for the C# caller.
"""

from __future__ import annotations

import hashlib
import json
import math
import struct
import sys
from pathlib import Path

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hair_zap import apply_face_cull, apply_hair_zap
from nif_geometry_readback import read_geometry


LAB_ROOT = Path(r"K:\ExampleWorkspace").resolve()
MAX_ASSETS = 64
MAX_ASSET_BYTES = 256 * 1024 * 1024
MAX_OUTPUT_BYTES = 512 * 1024 * 1024
MAX_MORPHS = 256


def parse_args() -> tuple[Path, Path]:
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else sys.argv[1:]
    values = dict(zip(argv[::2], argv[1::2], strict=True))
    if "--request" not in values or "--status" not in values:
        raise ValueError("--request and --status are required")
    return Path(values["--request"]), Path(values["--status"])


def write_status(path: Path, status: dict) -> int:
    path.write_text(json.dumps(status, indent=2), encoding="utf-8")
    return 0 if status.get("exported") else 1


def reset_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for datablocks in (bpy.data.meshes, bpy.data.curves, bpy.data.materials):
        for datablock in list(datablocks):
            if datablock.users == 0:
                datablocks.remove(datablock)


def resolve_request(request: dict) -> tuple[Path, Path, str, list[dict], list[dict]]:
    root = Path(request["root"]).resolve()
    output = Path(request["output"]).resolve()
    target_game = str(request["targetGame"])
    if target_game not in {"SKYRIMSE", "FO4"}:
        raise ValueError("targetGame must be SKYRIMSE or FO4")
    if not root.is_relative_to(LAB_ROOT):
        raise ValueError("NIF export root must remain under K:\\ExampleWorkspace")
    if not output.is_relative_to(root):
        raise ValueError("NIF output escapes the workspace root")
    if output.exists():
        raise ValueError("NIF output already exists")
    assets = request.get("assets")
    if not isinstance(assets, list) or not assets:
        raise ValueError("at least one NIF asset is required")
    if len(assets) > MAX_ASSETS:
        raise ValueError(f"at most {MAX_ASSETS} NIF assets are accepted")
    morphs = request.get("morphs", [])
    if not isinstance(morphs, list) or len(morphs) > MAX_MORPHS:
        raise ValueError(f"morphs must be an array with at most {MAX_MORPHS} entries")
    normalized_morphs: list[dict] = []
    morph_names: set[str] = set()
    for item in morphs:
        if not isinstance(item, dict) or not isinstance(item.get("name"), str):
            raise ValueError("each morph requires a name and value")
        name = item["name"].strip()
        value = item.get("value")
        if not name or len(name) > 255 or not isinstance(value, (int, float)) or not math.isfinite(value):
            raise ValueError("each morph requires a finite value and non-empty name")
        if value < -1.0 or value > 1.0:
            raise ValueError("morph values must be within -1..1")
        if name.casefold() in morph_names:
            raise ValueError(f"duplicate morph '{name}'")
        morph_names.add(name.casefold())
        normalized_morphs.append({"name": name, "value": float(value)})
    if normalized_morphs and all(item["value"] == 0.0 for item in normalized_morphs):
        raise ValueError("at least one non-zero morph value is required")
    resolved: list[dict] = []
    for item in assets:
        path = Path(item["path"]).resolve()
        if not path.is_relative_to(root) or not path.is_file() or path.suffix.lower() != ".nif":
            raise ValueError("every source asset must be an existing workspace-local NIF")
        if path.stat().st_size <= 0 or path.stat().st_size > MAX_ASSET_BYTES:
            raise ValueError(f"NIF asset is outside the {MAX_ASSET_BYTES}-byte bound")
        actual = hashlib.sha256(path.read_bytes()).hexdigest()
        if actual.casefold() != str(item["sha256"]).casefold():
            raise ValueError(f"source hash mismatch for {path}")
        tri_files = item.get("triFiles", [])
        if not isinstance(tri_files, list):
            raise ValueError("asset triFiles must be an array")
        resolved_tri_files: list[dict] = []
        seen_tri: set[str] = set()
        for tri_item in tri_files:
            if not isinstance(tri_item, dict) or not isinstance(tri_item.get("path"), str):
                raise ValueError("each triFiles entry requires a path and hash")
            tri = Path(tri_item["path"]).resolve()
            if not tri.is_relative_to(root) or tri.suffix.lower() != ".tri" or not tri.is_file():
                raise ValueError("every TRI dependency must be a workspace-local .tri file")
            if tri.stat().st_size <= 0 or tri.stat().st_size > MAX_ASSET_BYTES:
                raise ValueError("TRI dependency is outside the bounded size limit")
            actual_tri = hashlib.sha256(tri.read_bytes()).hexdigest()
            if actual_tri.casefold() != str(tri_item.get("sha256", "")).casefold():
                raise ValueError(f"TRI dependency hash mismatch for {tri}")
            key = str(tri).casefold()
            if key not in seen_tri:
                seen_tri.add(key)
                resolved_tri_files.append({"path": str(tri), "sha256": actual_tri})
        resolved.append({
            "path": str(path), "sha256": actual,
            "category": str(item.get("category", "")),
            "triFiles": resolved_tri_files,
        })
    if normalized_morphs and not any(item["triFiles"] for item in resolved):
        raise ValueError("included morphs require at least one adjacent TRI dependency")
    return root, output, target_game, resolved, normalized_morphs


def vertex_digest(meshes: list[object]) -> str:
    digest = hashlib.sha256()
    for mesh in meshes:
        digest.update(struct.pack("<I", len(mesh.vertices)))
        for vertex in mesh.vertices:
            digest.update(struct.pack("<fff", float(vertex.co.x), float(vertex.co.y), float(vertex.co.z)))
    return digest.hexdigest()


def export(request: dict) -> dict:
    root, output, target_game, assets, morphs = resolve_request(request)
    output.parent.mkdir(parents=True, exist_ok=True)
    reset_scene()
    imported_meshes: list[object] = []
    armature_count = 0
    for asset in assets:
        before = set(bpy.data.objects)
        bpy.ops.preferences.addon_enable(module="io_scene_nifly")
        bpy.ops.import_scene.pynifly(
            filepath=asset["path"],
            # Preserve the source armature and skin modifier. The upstream
            # exporter serializes the evaluated world-pose mesh, so a mesh-only
            # import would silently discard the deformation path.
            mesh_only=False,
            import_tris=False,
            import_collisions=False,
            import_animations=False,
            create_bones=True,
            create_collection=False,
        )
        imported_objects = set(bpy.data.objects) - before
        new_meshes = [obj for obj in imported_objects if obj.type == "MESH"]
        armature_count += sum(1 for obj in imported_objects if obj.type == "ARMATURE")
        for obj in new_meshes:
            obj["PYN_PREVIEW_ASSET"] = asset["path"]
            obj["NPCM_ASSET_PATH"] = asset["path"]
        imported_meshes.extend(new_meshes)
        for obj in new_meshes:
            if not asset["triFiles"] or not morphs:
                continue
            bpy.ops.object.select_all(action="DESELECT")
            obj.select_set(True)
            bpy.context.view_layer.objects.active = obj
            for tri in asset["triFiles"]:
                tri_result = bpy.ops.import_scene.pyniflytri(filepath=tri["path"], do_apply_active=True)
                if "CANCELLED" in tri_result:
                    raise ValueError(f"PyNifly cancelled TRI import for {tri['path']}")
    if not imported_meshes:
        raise ValueError("PyNifly imported no mesh objects")
    morph_meshes = [obj for obj in imported_meshes if obj.data.shape_keys is not None]
    base_coordinate_digest: str | None = None
    baked_coordinate_digest: str | None = None
    morph_deformed = False
    if morphs:
        key_by_name = {
            key.name.casefold(): key
            for obj in morph_meshes
            for key in obj.data.shape_keys.key_blocks
        }
        for item in morphs:
            key = key_by_name.get(item["name"].casefold())
            if key is None:
                raise ValueError(f"requested morph '{item['name']}' is not present in the imported TRI set")
        for obj in morph_meshes:
            keys = {key.name.casefold(): key for key in obj.data.shape_keys.key_blocks}
            for item in morphs:
                key = keys.get(item["name"].casefold())
                if key is not None:
                    key.value = item["value"]
        base_coordinate_digest = vertex_digest([obj.data for obj in morph_meshes])

    # Always bake the evaluated mesh. With a full NIF import this applies the
    # source armature/skin modifier in the current pose; with an unskinned NIF
    # it is a deterministic mesh copy. This mirrors the upstream export
    # boundary without carrying live Blender modifiers into the output.
    depsgraph = bpy.context.evaluated_depsgraph_get()
    baked_morph_meshes: list[object] = []
    morph_mesh_ids = {id(obj) for obj in morph_meshes}
    hair_zap_status = apply_hair_zap(
        imported_meshes,
        "fallout4" if target_game == "FO4" else "skyrimse",
        request.get("hairZap"),
        [item["path"] for item in assets if item.get("category") == "hair"],
    )
    face_cull_status = apply_face_cull(
        imported_meshes,
        request.get("hairZap"),
        [item["path"] for item in assets if item.get("category") == "face"],
        remove=True,
    )
    for obj in imported_meshes:
        evaluated = obj.evaluated_get(depsgraph)
        baked_mesh = bpy.data.meshes.new_from_object(
            evaluated, depsgraph=depsgraph, preserve_all_data_layers=True
        )
        if baked_mesh is None:
            raise ValueError("Blender did not produce an evaluated preview mesh")
        obj.data = baked_mesh
        for modifier in list(obj.modifiers):
            obj.modifiers.remove(modifier)
        if id(obj) in morph_mesh_ids:
            baked_morph_meshes.append(baked_mesh)
    if morphs:
        baked_coordinate_digest = vertex_digest(baked_morph_meshes)
        morph_deformed = baked_coordinate_digest != base_coordinate_digest
        if not morph_deformed:
            raise ValueError("requested morphs did not deform the evaluated mesh")
    for obj in imported_meshes:
        obj["PYN_GAME"] = target_game
        if not obj.data.uv_layers:
            # PyNifly's exporter requires an active UV layer even for a mesh
            # whose source NIF did not carry UVs. A zeroed layer is explicit
            # sandbox fallback data, never a claim of source texture parity.
            layer = obj.data.uv_layers.new(name="UVMap")
            for uv in layer.data:
                uv.uv = (0.0, 0.0)
        obj.select_set(True)
    bpy.context.view_layer.objects.active = imported_meshes[0]
    result = bpy.ops.export_scene.pynifly(
        filepath=str(output),
        target_game=target_game,
        blender_xf=False,
        rename_bones=False,
        rotate_bones_pretty=False,
        preserve_hierarchy=False,
        write_bodytri=False,
        export_pose=False,
        export_modifiers=False,
        export_animations=False,
        export_colors=True,
        export_recenter_half_precision=False,
        chargen_ext="",
        intuit_defaults=False,
    )
    if ("CANCELLED" in result or not output.is_file() or
            output.stat().st_size <= 32 or output.stat().st_size > MAX_OUTPUT_BYTES):
        raise ValueError(f"PyNifly NIF export failed: {sorted(result)}")
    payload = output.read_bytes()
    source_geometry = [
        read_geometry(Path(item["path"]), target_game) for item in assets
    ]
    output_geometry = read_geometry(output, target_game)
    base_geometry_hash = (
        source_geometry[0]["geometrySha256"] if len(source_geometry) == 1 else None
    )
    return {
        "exported": True,
        "output": str(output),
        "sha256": hashlib.sha256(payload).hexdigest(),
        "bytes": len(payload),
        "targetGame": target_game,
        "meshCount": len(imported_meshes),
        "sourceAssets": [{"path": item["path"], "sha256": item["sha256"]} for item in assets],
        "triFiles": [tri for item in assets for tri in item["triFiles"]],
        "morphs": morphs,
        "morphDeformed": morph_deformed,
        "baseVertexSha256": base_geometry_hash if morphs else None,
        "bakedVertexSha256": output_geometry["geometrySha256"] if morphs else None,
        "vertexCount": output_geometry["vertexCount"],
        "importMode": "mesh-plus-tri-bake" if morphs else "mesh-only-import",
        "armatureCount": armature_count,
        "deformationMode": "nif-skinned-evaluated" if armature_count else "nif-mesh-evaluated",
        "hairZapApplied": hair_zap_status["applied"],
        "hairZapTop": hair_zap_status["top"],
        "hairZapLong": hair_zap_status["long"],
        "hairZapAffectedMeshCount": hair_zap_status["affectedMeshCount"],
        "hairZapRemovedFaceCount": hair_zap_status["removedFaceCount"],
        "faceCullApplied": face_cull_status["applied"],
        "faceCullAffectedMeshCount": face_cull_status["affectedMeshCount"],
        "root": str(root),
    }


def main() -> int:
    request_path, status_path = parse_args()
    try:
        request = json.loads(request_path.read_text(encoding="utf-8-sig"))
        return write_status(status_path, export(request))
    except Exception as exc:  # Blender must always return structured refusal evidence.
        return write_status(status_path, {"exported": False, "error": str(exc)})


if __name__ == "__main__":
    raise SystemExit(main())
