"""Bake a bounded Skyrim FaceGeom morph set into a new K-local NIF.

This adapter is deliberately narrower than a game-authoritative FaceGen writer:
it imports one copied head NIF plus its adjacent TRI files, applies explicitly
named morph values, bakes the evaluated vertices, and exports a new Gamebryo
NIF through the pinned PyNifly profile.  It never resolves a live plugin,
provider, load order, or game path.
"""

from __future__ import annotations

import hashlib
import json
import math
import os
import struct
import sys
from pathlib import Path

import bpy

sys.path.insert(0, str(Path(__file__).resolve().parent))
from nif_geometry_readback import read_geometry


MAX_ASSET_BYTES = 256 * 1024 * 1024
MAX_OUTPUT_BYTES = 512 * 1024 * 1024
MAX_MORPHS = 2048


class MorphImportError(ValueError):
    def __init__(self, code: str, message: str):
        super().__init__(message)
        self.code = code


def parse_args() -> tuple[Path, Path]:
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else sys.argv[1:]
    values = dict(zip(argv[::2], argv[1::2], strict=True))
    if "--request" not in values or "--status" not in values:
        raise ValueError("--request and --status are required")
    return Path(values["--request"]), Path(values["--status"])


def write_status(path: Path, status: dict) -> int:
    path.write_text(json.dumps(status, indent=2), encoding="utf-8")
    return 0 if status.get("exported") else 1


def hash_file(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def resolve_under(root: Path, value: str, suffix: str) -> Path:
    if not isinstance(value, str) or not value or Path(value).is_absolute():
        raise ValueError("asset paths must be non-empty relative paths")
    path = (root / value.replace("/", "\\")).resolve()
    if not path.is_relative_to(root) or path.suffix.lower() != suffix or not path.is_file():
        raise ValueError(f"asset must be an existing {suffix} under the asset root")
    if path.stat().st_size <= 0 or path.stat().st_size > MAX_ASSET_BYTES:
        raise ValueError("asset is outside the bounded size limit")
    return path


def reset_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for datablocks in (bpy.data.meshes, bpy.data.curves, bpy.data.materials):
        for datablock in list(datablocks):
            if datablock.users == 0:
                datablocks.remove(datablock)


def vertex_digest(mesh) -> str:
    digest = hashlib.sha256()
    for vertex in mesh.vertices:
        digest.update(struct.pack("<fff", float(vertex.co.x), float(vertex.co.y), float(vertex.co.z)))
    return digest.hexdigest()


def resolve_request(request: dict) -> tuple[Path, Path, str, Path, list[Path], list[dict]]:
    workspace = os.environ.get("ACTORWRIGHT_WORKSPACE_ROOT")
    if not workspace or not Path(workspace).is_absolute():
        raise ValueError("ACTORWRIGHT_WORKSPACE_ROOT must bind the admitted workspace")
    lab_root = Path(workspace).resolve(strict=True)
    root = Path(request.get("root", "")).resolve()
    output = Path(request.get("output", "")).resolve()
    if root != lab_root:
        raise ValueError("FaceGeom root must match the admitted ACTORWRIGHT_WORKSPACE_ROOT")
    if not output.is_relative_to(root) or output.suffix.lower() != ".nif":
        raise ValueError("FaceGeom output must be a new .nif under the asset root")
    if output.exists():
        raise ValueError("FaceGeom output already exists")
    target_game = str(request.get("targetGame", ""))
    if target_game not in {"FO4", "SKYRIMSE"}:
        raise ValueError("targetGame must be FO4 or SKYRIMSE")
    source = resolve_under(root, request.get("source", ""), ".nif")
    source_hash = str(request.get("sourceSha256", ""))
    if hash_file(source).casefold() != source_hash.casefold():
        raise ValueError("source NIF hash mismatch")

    tri_values = request.get("triFiles")
    if tri_values is None:
        tri_values = [
            str(path.relative_to(root)).replace("\\", "/")
            for path in (source.with_suffix(".tri"), source.with_name(source.stem + "chargen.tri"))
            if path.is_file()
        ]
    if not isinstance(tri_values, list) or not tri_values:
        raise ValueError("at least one adjacent TRI file is required")
    tris: list[Path] = []
    for value in tri_values:
        tri = resolve_under(root, value, ".tri")
        if tri not in tris:
            tris.append(tri)

    morphs = request.get("morphs", [])
    if not isinstance(morphs, list) or len(morphs) > MAX_MORPHS:
        raise ValueError(f"morphs must be an array with at most {MAX_MORPHS} entries")
    normalized: list[dict] = []
    names: set[str] = set()
    for item in morphs:
        if not isinstance(item, dict) or not isinstance(item.get("name"), str):
            raise ValueError("each morph requires a name and value")
        name = item["name"]
        value = item.get("value")
        if not name.strip() or len(name) > 255 or not isinstance(value, (int, float)) or not math.isfinite(value):
            raise ValueError("each morph requires a finite value and non-empty name")
        folded = name.casefold()
        if folded in names:
            raise ValueError(f"duplicate morph '{name}'")
        names.add(folded)
        normalized.append({"name": name, "value": float(value)})
    if not normalized or all(item["value"] == 0.0 for item in normalized):
        raise ValueError("at least one non-zero morph value is required")
    return root, output, target_game, source, tris, normalized


def bake(request: dict) -> dict:
    root, output, target_game, source, tris, morphs = resolve_request(request)
    reset_scene()
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    before = set(bpy.data.objects)
    result = bpy.ops.import_scene.pynifly(
        filepath=str(source),
        mesh_only=True,
        import_tris=False,
        import_collisions=False,
        import_animations=False,
        import_shapekeys=False,
        create_bones=False,
        create_collection=False,
    )
    if "CANCELLED" in result:
        raise ValueError("PyNifly cancelled FaceGeom import")
    meshes = [obj for obj in set(bpy.data.objects) - before if obj.type == "MESH"]
    if len(meshes) != 1:
        raise ValueError(f"FaceGeom source must import exactly one mesh, got {len(meshes)}")
    obj = meshes[0]
    # Use the importer's existing TRI API over exactly the admitted paths.
    # Automatic discovery can also follow unbound BODYTRI/TRIP/OSD references.
    from io_scene_nifly.tri.import_tri import open_tri, import_tri
    from io_scene_nifly.tri.trifile import TriFile
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    for tri in tris:
        try:
            parsed = open_tri(tri)
            if not isinstance(parsed, TriFile):
                raise ValueError("the bound file is not an admitted head TRI")
            if len(parsed.vertices) != len(obj.data.vertices):
                raise ValueError("TRI vertex count differs from the selected head")
            if import_tri(parsed, obj, allow_extra_verts=False) is not obj:
                raise ValueError("TRI import did not apply to the selected head")
        except Exception as exc:
            raise MorphImportError("facegeom-binary-tri-import", f"TRI import failed for {tri.name}: {exc}") from exc
    key_by_name = {key.name.casefold(): key for key in obj.data.shape_keys.key_blocks} if obj.data.shape_keys else {}
    for item in morphs:
        key = key_by_name.get(item["name"].casefold())
        if key is None:
            raise MorphImportError("facegeom-binary-morph-missing", f"requested morph '{item['name']}' is not present in the imported TRI set")
        key.slider_min = min(-1.0, item["value"])
        key.slider_max = max(1.0, item["value"])
        value = item["value"]
        if value < key.slider_min or value > key.slider_max:
            # Blender 4.5 bounds sliders at +/-10. Scale this relative TRI
            # delta and evaluate at one so the requested value is not clamped.
            for point, basis in zip(key.data, key.relative_key.data, strict=True):
                point.co = basis.co + (point.co - basis.co) * value
            key.value = 1.0
        else:
            key.value = value
    base_coordinate_digest = vertex_digest(obj.data)
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = obj.evaluated_get(depsgraph)
    baked_mesh = bpy.data.meshes.new_from_object(evaluated, depsgraph=depsgraph, preserve_all_data_layers=True)
    if baked_mesh is None:
        raise ValueError("Blender did not produce an evaluated FaceGeom mesh")
    obj.data = baked_mesh
    baked_coordinate_digest = vertex_digest(obj.data)
    if baked_coordinate_digest == base_coordinate_digest:
        raise ValueError("requested morphs did not deform the evaluated mesh")
    if not obj.data.uv_layers:
        layer = obj.data.uv_layers.new(name="UVMap")
        for uv in layer.data:
            uv.uv = (0.0, 0.0)
    obj["PYN_GAME"] = target_game
    obj["FACEGEOM_MORPHS_BAKED"] = json.dumps(morphs, separators=(",", ":"))
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    output.parent.mkdir(parents=True, exist_ok=True)
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
    if "CANCELLED" in result or not output.is_file() or output.stat().st_size <= 32 or output.stat().st_size > MAX_OUTPUT_BYTES:
        raise ValueError(f"PyNifly FaceGeom export failed: {sorted(result)}")
    source_geometry = read_geometry(source, target_game)
    output_geometry = read_geometry(output, target_game)
    return {
        "exported": True,
        "output": str(output),
        "sha256": hash_file(output),
        "bytes": output.stat().st_size,
        "targetGame": target_game,
        "meshCount": 1,
        "vertexCount": output_geometry["vertexCount"],
        "source": {"path": str(source), "sha256": hash_file(source)},
        "triFiles": [{"path": str(path), "sha256": hash_file(path)} for path in tris],
        "morphs": morphs,
        "baseVertexSha256": source_geometry["geometrySha256"],
        "bakedVertexSha256": output_geometry["geometrySha256"],
        "importMode": "nif-plus-tri-bake",
        "root": str(root),
    }


def main() -> int:
    request_path, status_path = parse_args()
    try:
        request = json.loads(request_path.read_text(encoding="utf-8-sig"))
        return write_status(status_path, bake(request))
    except Exception as exc:  # Blender must always return structured refusal evidence.
        return write_status(status_path, {"exported": False, "error": str(exc),
                                          "errorCode": exc.code if isinstance(exc, MorphImportError) else None})


if __name__ == "__main__":
    raise SystemExit(main())
