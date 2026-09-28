"""Render one K-local preview scene through Blender and the staged PyNifly addon.

The script is intentionally a narrow import/render adapter. It does not export NIFs,
write Blender configuration, or inspect a live game root. The caller supplies a
hash-bound JSON request and receives a small status JSON beside the PNG.
"""

from __future__ import annotations

import argparse
import binascii
import hashlib
import json
import struct
import sys
import zlib
from pathlib import Path

import bpy
from mathutils import Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from hair_zap import apply_face_cull, apply_hair_zap


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True, type=Path)
    parser.add_argument("--status", required=True, type=Path)
    argv = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else sys.argv[1:]
    return parser.parse_args(argv)


def fail(status_path: Path, message: str) -> int:
    status_path.write_text(json.dumps({"rendered": False, "error": message}, indent=2), encoding="utf-8")
    return 1


def reset_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for datablocks in (bpy.data.meshes, bpy.data.curves, bpy.data.materials, bpy.data.cameras, bpy.data.lights):
        for datablock in list(datablocks):
            if datablock.users == 0:
                datablocks.remove(datablock)


def import_meshes(paths: list[Path], morphs: list[dict]) -> tuple[list[object], list[str], bool, int]:
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    meshes: list[object] = []
    armature_count = 0
    remaining = {str(item["name"]).casefold(): item for item in morphs}
    base_digest = hashlib.sha256()
    for path in paths:
        before = set(bpy.data.objects)
        bpy.ops.import_scene.pynifly(
            filepath=str(path),
            # Keep the source armature/skin path in the preview scene. Blender
            # then evaluates the same modifier chain that the upstream exporter
            # bakes before writing its review NIF. We still render only meshes.
            mesh_only=False,
            import_tris=False,
            import_collisions=False,
            import_animations=False,
            create_bones=True,
            create_collection=False,
        )
        imported_objects = set(bpy.data.objects) - before
        imported = [obj for obj in imported_objects if obj.type == "MESH"]
        for obj in imported:
            obj["NPCM_ASSET_PATH"] = str(path.resolve())
        armature_count += sum(1 for obj in imported_objects if obj.type == "ARMATURE")
        meshes.extend(imported)
        if not remaining:
            continue
        tri_paths = [path.with_suffix(".tri"), path.with_name(path.stem + "chargen.tri")]
        tri_paths = [tri for tri in tri_paths if tri.is_file()]
        if not tri_paths:
            raise ValueError(f"morphs were requested but no adjacent TRI file exists for {path.name}")
        if len(imported) != 1:
            raise ValueError(f"morph rendering requires one mesh per NIF asset, got {len(imported)} for {path.name}")
        target = imported[0]
        bpy.ops.object.select_all(action="DESELECT")
        target.select_set(True)
        bpy.context.view_layer.objects.active = target
        for tri in tri_paths:
            result = bpy.ops.import_scene.pyniflytri(filepath=str(tri), do_apply_active=True)
            if "CANCELLED" in result:
                raise ValueError(f"PyNifly cancelled TRI import for {tri.name}")
        if target.data.shape_keys is None:
            raise ValueError(f"TRI import did not create shape keys for {path.name}")
        keys = {key.name.casefold(): key for key in target.data.shape_keys.key_blocks}
        for name, item in list(remaining.items()):
            key = keys.get(name)
            if key is None:
                continue
            key.value = float(item["value"])
            remaining.pop(name)
    if remaining:
        raise ValueError("requested morphs were not present in the imported TRI shape keys: " + ", ".join(sorted(remaining)))
    deformed = False
    if morphs:
        for mesh in meshes:
            for vertex in mesh.data.vertices:
                base_digest.update(struct.pack("<fff", float(vertex.co.x), float(vertex.co.y), float(vertex.co.z)))
        before = base_digest.digest()
        depsgraph = bpy.context.evaluated_depsgraph_get()
        evaluated_digest = hashlib.sha256()
        for mesh in meshes:
            evaluated = mesh.evaluated_get(depsgraph)
            for vertex in evaluated.data.vertices:
                evaluated_digest.update(struct.pack("<fff", float(vertex.co.x), float(vertex.co.y), float(vertex.co.z)))
        deformed = before != evaluated_digest.digest()
        if not deformed:
            raise ValueError("requested morphs did not deform the imported mesh")
    return meshes, [str(item["name"]) for item in morphs], deformed, armature_count


def evaluated_vertex_digest(meshes: list[object]) -> str:
    digest = hashlib.sha256()
    depsgraph = bpy.context.evaluated_depsgraph_get()
    for mesh in meshes:
        evaluated = mesh.evaluated_get(depsgraph)
        digest.update(struct.pack("<I", len(evaluated.data.vertices)))
        for vertex in evaluated.data.vertices:
            digest.update(struct.pack("<fff", float(vertex.co.x), float(vertex.co.y), float(vertex.co.z)))
    return digest.hexdigest()


def apply_animation(animation: dict | None, meshes: list[object], scene: object) -> dict:
    if animation is None:
        return {"applied": False, "id": None, "frame": None, "poseDeformed": False}
    armatures = [obj for obj in bpy.data.objects if obj.type == "ARMATURE"]
    if len(armatures) != 1:
        raise ValueError("requested animation requires exactly one imported NIF armature")
    if bool(animation.get("additive", False)):
        raise ValueError("additive animation semantics are not supported by the copied renderer")
    path = Path(animation["path"]).resolve()
    skeleton = Path(animation["skeleton"]).resolve()
    if path.suffix.casefold() not in {".hkx", ".kf"} or skeleton.suffix.casefold() != ".hkx":
        raise ValueError("animation and skeleton files must use HKX (or KF animation) extensions")
    if not path.is_file() or not skeleton.is_file():
        raise ValueError("animation and skeleton files must exist")
    target = armatures[0]
    bpy.ops.object.select_all(action="DESELECT")
    target.select_set(True)
    bpy.context.view_layer.objects.active = target
    if path.suffix.casefold() == ".hkx":
        result = bpy.ops.import_scene.pynifly_hkx(
            filepath=str(path), reference_skel=str(skeleton), create_collection=False
        )
    else:
        result = bpy.ops.import_scene.pynifly_kf(filepath=str(path))
    if "CANCELLED" in result or not target.animation_data or not target.animation_data.action:
        raise ValueError(f"PyNifly did not apply animation {path.name}")
    action = target.animation_data.action
    requested_frame = int(animation["frame"])
    scene.frame_start = 1
    scene.frame_end = max(scene.frame_end, requested_frame + 1)
    scene.frame_set(1)
    bpy.context.view_layer.update()
    neutral_digest = evaluated_vertex_digest(meshes)
    scene.frame_set(requested_frame + 1)
    bpy.context.view_layer.update()
    pose_digest = evaluated_vertex_digest(meshes)
    return {
        "applied": True,
        "id": str(animation["id"]),
        "frame": requested_frame,
        "poseDeformed": neutral_digest != pose_digest,
        "action": action.name,
    }


def configure_materials(meshes: list[object], edition: str) -> None:
    material = bpy.data.materials.new("npcm_preview_material")
    material.diffuse_color = (0.34, 0.42, 0.52, 1.0) if edition == "fallout4" else (0.52, 0.36, 0.24, 1.0)
    for mesh in meshes:
        mesh.data.materials.clear()
        mesh.data.materials.append(material)
        mesh.hide_render = False
        mesh.hide_viewport = False


def bounds(meshes: list[object]) -> tuple[Vector, float] | None:
    points = [obj.matrix_world @ Vector(corner) for obj in meshes for corner in obj.bound_box]
    if not points:
        return None
    minimum = Vector((min(point.x for point in points), min(point.y for point in points), min(point.z for point in points)))
    maximum = Vector((max(point.x for point in points), max(point.y for point in points), max(point.z for point in points)))
    return (minimum + maximum) / 2, max((maximum - minimum).x, (maximum - minimum).y, (maximum - minimum).z, 1.0)


def canonicalize_png(path: Path) -> None:
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("Blender render is not a PNG")
    offset = 8
    header = None
    compressed = bytearray()
    while offset < len(data):
        length = struct.unpack_from(">I", data, offset)[0]
        kind = data[offset + 4:offset + 8]
        payload = data[offset + 8:offset + 8 + length]
        offset += length + 12
        if kind == b"IHDR":
            header = payload
        elif kind == b"IDAT":
            compressed.extend(payload)
        elif kind == b"IEND":
            break
    if header is None or len(header) != 13:
        raise ValueError("Blender PNG is missing a valid IHDR")
    width, height, bit_depth, color_type, _, _, interlace = struct.unpack(">IIBBBBB", header)
    if bit_depth != 8 or color_type != 6 or interlace != 0:
        raise ValueError("Blender PNG must be non-interlaced 8-bit RGBA")
    stride = width * 4
    raw = zlib.decompress(compressed)
    if len(raw) != height * (stride + 1):
        raise ValueError("Blender PNG scanline payload has an invalid size")
    pixels = bytearray(height * stride)
    previous = bytearray(stride)
    cursor = 0
    for row in range(height):
        filter_type = raw[cursor]
        cursor += 1
        current = bytearray(raw[cursor:cursor + stride])
        cursor += stride
        for index in range(stride):
            left = current[index - 4] if index >= 4 else 0
            above = previous[index]
            upper_left = previous[index - 4] if index >= 4 else 0
            if filter_type == 1:
                current[index] = (current[index] + left) & 0xFF
            elif filter_type == 2:
                current[index] = (current[index] + above) & 0xFF
            elif filter_type == 3:
                current[index] = (current[index] + ((left + above) // 2)) & 0xFF
            elif filter_type == 4:
                estimate = left + above - upper_left
                distances = (abs(estimate - left), abs(estimate - above), abs(estimate - upper_left))
                predictor = (left, above, upper_left)[distances.index(min(distances))]
                current[index] = (current[index] + predictor) & 0xFF
            elif filter_type != 0:
                raise ValueError(f"unsupported Blender PNG filter {filter_type}")
        pixels[row * stride:(row + 1) * stride] = current
        previous = current

    def chunk(kind: bytes, payload: bytes) -> bytes:
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", binascii.crc32(kind + payload) & 0xFFFFFFFF)

    canonical = bytearray(b"\x89PNG\r\n\x1a\n")
    canonical.extend(chunk(b"IHDR", header))
    scanlines = b"".join(b"\x00" + pixels[row * stride:(row + 1) * stride] for row in range(height))
    canonical.extend(chunk(b"IDAT", zlib.compress(scanlines, 9)))
    canonical.extend(chunk(b"IEND", b""))
    path.write_bytes(canonical)


def look_at(camera: object, target: Vector) -> None:
    camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()


def render(request: dict) -> dict:
    edition = request.get("edition")
    if edition not in {"fallout4", "skyrimse"}:
        raise ValueError("edition must be fallout4 or skyrimse")
    root = Path(request["root"]).resolve()
    output = Path(request["output"]).resolve()
    assets = [Path(path).resolve() for path in request["assets"]]
    if not output.is_relative_to(root):
        raise ValueError("image output escapes the workspace root")
    if output.exists():
        raise ValueError("image output already exists")
    if not assets or any(not path.is_relative_to(root) or not path.is_file() for path in assets):
        raise ValueError("every preview asset must be an existing workspace-local file")
    width, height = int(request["width"]), int(request["height"])
    if width < 64 or width > 2048 or height < 64 or height > 2048:
        raise ValueError("preview dimensions must be between 64 and 2048 pixels")

    output.parent.mkdir(parents=True, exist_ok=True)
    reset_scene()
    morphs = request.get("morphs", [])
    if not isinstance(morphs, list) or len(morphs) > 2048:
        raise ValueError("morphs must be an array with at most 2048 entries")
    meshes, morph_names, morph_deformed, armature_count = import_meshes(assets, morphs)
    if not meshes:
        raise ValueError("PyNifly imported no mesh objects")
    animation = request.get("animation")
    if animation is not None and not isinstance(animation, dict):
        raise ValueError("animation must be an object or null")
    scene = bpy.context.scene
    animation_status = apply_animation(animation, meshes, scene)
    hair_zap_status = apply_hair_zap(
        meshes, edition, request.get("hairZap"), request.get("hairAssets", [])
    )
    configure_materials(meshes, edition)
    face_cull_status = apply_face_cull(
        meshes, request.get("hairZap"), request.get("faceAssets", [])
    )
    scene_bounds = bounds(meshes)
    if scene_bounds is None:
        raise ValueError("imported preview meshes have no bounds")
    center, size = scene_bounds

    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "MATERIAL"
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.film_transparent = False

    camera_data = bpy.data.cameras.new("npcm_preview_camera")
    camera = bpy.data.objects.new("npcm_preview_camera", camera_data)
    scene.collection.objects.link(camera)
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = size * 1.25
    camera.location = center + Vector((0, -size * 2.8, size * 0.05))
    look_at(camera, center)
    scene.camera = camera

    light_data = bpy.data.lights.new("npcm_preview_key", type="AREA")
    light = bpy.data.objects.new("npcm_preview_key", light_data)
    scene.collection.objects.link(light)
    light.location = center + Vector((-size, -size * 2, size * 2))
    light_data.energy = 450
    light_data.size = size

    scene.render.filepath = str(output)
    bpy.ops.render.render(write_still=True)
    canonicalize_png(output)
    if not output.is_file() or output.stat().st_size <= 32:
        raise ValueError("Blender did not produce a non-empty PNG")
    data = output.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("render output is not a PNG")
    return {"rendered": True, "edition": edition, "output": str(output), "sha256": hashlib.sha256(data).hexdigest(),
            "width": width, "height": height, "meshCount": len(meshes),
            "armatureCount": armature_count,
            "deformationMode": "nif-skinned-evaluated" if armature_count else "nif-mesh-evaluated",
            "animationId": animation_status["id"],
            "animationApplied": animation_status["applied"],
            "animationFrame": animation_status["frame"],
            "animationPoseDeformed": animation_status["poseDeformed"],
            "hairZapApplied": hair_zap_status["applied"],
            "hairZapTop": hair_zap_status["top"],
            "hairZapLong": hair_zap_status["long"],
            "hairZapAffectedMeshCount": hair_zap_status["affectedMeshCount"],
            "hairZapRemovedFaceCount": hair_zap_status["removedFaceCount"],
            "faceCullApplied": face_cull_status["applied"],
            "faceCullAffectedMeshCount": face_cull_status["affectedMeshCount"],
            "morphNames": morph_names, "morphDeformed": morph_deformed}


def main() -> int:
    args = parse_args()
    try:
        request = json.loads(args.request.read_text(encoding="utf-8-sig"))
        status = render(request)
        args.status.write_text(json.dumps(status, indent=2), encoding="utf-8")
        return 0
    except Exception as exc:  # Blender must always return structured refusal evidence.
        return fail(args.status, str(exc))


if __name__ == "__main__":
    raise SystemExit(main())
