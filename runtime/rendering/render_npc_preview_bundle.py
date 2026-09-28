"""Render a schema-2 NPC preview bundle from K-local, hash-bound assets.

The final FaceGeom NIF is imported exactly once and remains the authoritative
head graph. PyNifly materials are retained; adjacent preview-only PNG files are
preferred over DDS by the addon. This script never exports a NIF and never
touches a live game tree.
"""

from __future__ import annotations

import argparse
import binascii
import hashlib
import json
import math
import struct
import sys
import traceback
import zlib
from pathlib import Path

import bpy
from mathutils import Vector


VIEW_IDS = (
    "face-front",
    "face-left",
    "face-right",
    "face-alternate-light",
    "body-front",
    "body-back",
)
ROLE_COLORS = {
    "FaceGeom": (1.0, 0.0, 0.0, 1.0),
    "Body": (0.0, 1.0, 0.0, 1.0),
    "Hands": (1.0, 1.0, 0.0, 1.0),
    "Feet": (1.0, 0.0, 1.0, 1.0),
    "Outfit": (0.0, 0.25, 1.0, 1.0),
    "Skeleton": (0.0, 1.0, 1.0, 1.0),
}
MAX_PIRT_BYTES = 128 * 1024 * 1024


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", required=True, type=Path)
    parser.add_argument("--status", required=True, type=Path)
    argv = (
        sys.argv[sys.argv.index("--") + 1 :]
        if "--" in sys.argv
        else sys.argv[1:]
    )
    return parser.parse_args(argv)


def fail(path: Path, message: str) -> int:
    payload = {
        "rendered": False,
        "error": message,
        "traceback": traceback.format_exc(limit=12),
    }
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    return 1


def reset_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for blocks in (
        bpy.data.meshes,
        bpy.data.curves,
        bpy.data.materials,
        bpy.data.cameras,
        bpy.data.lights,
        bpy.data.armatures,
    ):
        for block in list(blocks):
            if block.users == 0:
                blocks.remove(block)


def configure_pynifly(data_root: Path) -> None:
    bpy.ops.preferences.addon_enable(module="io_scene_nifly")
    addon = bpy.context.preferences.addons.get("io_scene_nifly")
    if addon is None:
        raise ValueError("PyNifly did not become available")
    addon.preferences.sky_texture_path_1 = str(data_root)
    addon.preferences.sky_texture_path_2 = ""
    addon.preferences.sky_texture_path_3 = ""
    addon.preferences.sky_texture_path_4 = ""
    bpy.context.preferences.filepaths.texture_directory = str(data_root)


def apply_weight_companion(
    row: dict,
    high_meshes: list[object],
    weight_percent: float,
) -> tuple[int, int]:
    low_text = row.get("lowWeightPath")
    if not low_text or weight_percent >= 100.0:
        return (0, 0)
    low_path = Path(str(low_text)).resolve()
    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = None
    before = set(bpy.data.objects)
    result = bpy.ops.import_scene.pynifly(
        filepath=str(low_path),
        mesh_only=True,
        import_shapekeys=False,
        import_tris=False,
        import_collisions=False,
        import_animations=False,
        create_bones=False,
        create_collection=False,
    )
    if "CANCELLED" in result:
        raise ValueError(
            f"PyNifly cancelled low-weight import of {low_path.name}"
        )
    imported = list(set(bpy.data.objects) - before)
    low_meshes = [item for item in imported if item.type == "MESH"]
    if not low_meshes:
        raise ValueError(
            f"PyNifly imported no low-weight mesh from {low_path.name}"
        )

    def canonical_name(value: str) -> str:
        name = value.casefold()
        while True:
            stem, separator, suffix = name.rpartition(".")
            if not separator or not suffix.isdigit():
                return name
            name = stem

    factor = max(0.0, min(100.0, weight_percent)) / 100.0
    morphed = 0
    pairs: list[tuple[object, object]] = []
    unused_low = list(low_meshes)
    for high in high_meshes:
        candidates = [
            low
            for low in unused_low
            if canonical_name(low.name) ==
                canonical_name(high.name)
            and len(low.data.vertices) ==
                len(high.data.vertices)
        ]
        if len(candidates) != 1:
            candidates = [
                low
                for low in unused_low
                if len(low.data.vertices) ==
                    len(high.data.vertices)
            ]
        if len(candidates) != 1:
            available = ", ".join(
                f"{low.name}:{len(low.data.vertices)}/"
                f"{len(low.data.polygons)}"
                for low in unused_low
            )
            raise ValueError(
                f"weight-pair mesh match for {high.name} from "
                f"{low_path.name} resolved {len(candidates)} "
                f"candidate(s); available=[{available}]"
            )
        low = candidates[0]
        unused_low.remove(low)
        pairs.append((high, low))
    try:
        for high, low in pairs:
            bpy.ops.object.select_all(action="DESELECT")
            low.select_set(True)
            high.select_set(True)
            bpy.context.view_layer.objects.active = high
            keys = high.data.shape_keys
            prior_count = (
                len(keys.key_blocks)
                if keys is not None
                else 0
            )
            joined = bpy.ops.object.join_shapes()
            keys = high.data.shape_keys
            expected_count = (
                prior_count + 1
                if prior_count > 0
                else 2
            )
            if (
                "FINISHED" not in joined
                or keys is None
                or len(keys.key_blocks) != expected_count
            ):
                raise ValueError(
                    f"weight-pair shape key mismatch for {high.name}: "
                    f"join={sorted(joined)}, before={prior_count}, "
                    f"after={0 if keys is None else len(keys.key_blocks)}"
                )
            added = keys.key_blocks[-1]
            added.name = "NPCM_LOW_WEIGHT"
            added.value = 1.0 - factor
            high["NPCM_WEIGHT_PERCENT"] = weight_percent
            high["NPCM_LOW_WEIGHT_PATH"] = str(low_path)
            morphed += 1
    finally:
        for obj in imported:
            if obj.name in bpy.data.objects:
                bpy.data.objects.remove(obj, do_unlink=True)
    return (1, morphed)


def parse_pirt(path: Path) -> dict[str, dict]:
    if path.stat().st_size <= 4 or path.stat().st_size > MAX_PIRT_BYTES:
        raise ValueError(f"PIRT {path.name} is outside the bounded size")
    data = path.read_bytes()
    if len(data) > MAX_PIRT_BYTES or data[:4] != b"PIRT":
        raise ValueError(f"{path.name} is not a bounded PIRT BodySlide file")
    cursor = 4

    def take(size: int) -> bytes:
        nonlocal cursor
        if size < 0 or cursor + size > len(data):
            raise ValueError(f"PIRT {path.name} ended before a declared field")
        value = data[cursor:cursor + size]
        cursor += size
        return value

    def read_u8() -> int:
        return take(1)[0]

    def read_u16() -> int:
        return struct.unpack("<H", take(2))[0]

    def read_i16() -> int:
        return struct.unpack("<h", take(2))[0]

    def read_f32() -> float:
        return struct.unpack("<f", take(4))[0]

    def read_ascii(kind: str) -> str:
        raw = take(read_u8())
        if not raw or any(value > 0x7F for value in raw):
            raise ValueError(
                f"PIRT {path.name} has an invalid {kind} name"
            )
        return raw.decode("ascii")

    shapes: dict[str, dict] = {}
    total_offsets = 0
    for morph_type, component_count in (("position", 3), ("uv", 2)):
        shape_count = read_u16()
        for _ in range(shape_count):
            shape_name = read_ascii("shape")
            shape_key = shape_name.casefold()
            shape = shapes.setdefault(
                shape_key,
                {"name": shape_name, "morphs": {}},
            )
            if shape["name"] != shape_name:
                raise ValueError(
                    f"PIRT {path.name} has case-ambiguous shape names"
                )
            morph_count = read_u16()
            for _ in range(morph_count):
                morph_name = read_ascii("morph")
                morph_key = morph_name.casefold()
                multiplier = read_f32()
                if not math.isfinite(multiplier):
                    raise ValueError(
                        f"PIRT {path.name} morph {morph_name} "
                        "has a non-finite multiplier"
                    )
                offset_count = read_u16()
                total_offsets += offset_count
                if total_offsets > 8_000_000:
                    raise ValueError(
                        f"PIRT {path.name} exceeds the offset limit"
                    )
                offsets = []
                for _ in range(offset_count):
                    vertex_index = read_u16()
                    values = tuple(
                        read_i16() * multiplier
                        for _ in range(component_count)
                    )
                    if any(value != 0.0 for value in values):
                        offsets.append((vertex_index, values))
                morph = shape["morphs"].setdefault(
                    morph_key,
                    {"name": morph_name},
                )
                if morph.get(morph_type) is not None:
                    raise ValueError(
                        f"PIRT {path.name} repeats "
                        f"{shape_name}/{morph_name}/{morph_type}"
                    )
                morph[morph_type] = offsets
    if cursor != len(data):
        raise ValueError(f"PIRT {path.name} has trailing bytes")
    return shapes


def bodygen_asset_key(path: str, tri: bool) -> str:
    normalized = path.replace("\\", "/").casefold()
    directory, separator, filename = normalized.rpartition("/")
    if tri:
        if not filename.endswith(".tri"):
            return ""
        stem = filename[:-4]
    else:
        if not filename.endswith(".nif"):
            return ""
        stem = filename[:-4]
        if stem.endswith("_0") or stem.endswith("_1"):
            stem = stem[:-2]
    return f"{directory}{separator}{stem}"


def canonical_object_name(value: str) -> str:
    name = value.casefold()
    while True:
        stem, separator, suffix = name.rpartition(".")
        if not separator or not suffix.isdigit():
            return name
        name = stem


def apply_bodygen_morphs(
    tri_rows: list[dict],
    morph_rows: list[dict],
    meshes: list[object],
) -> dict:
    active: dict[str, tuple[str, float]] = {}
    for row in morph_rows:
        name = str(row.get("name", "")).strip()
        value = float(row.get("value", 0.0))
        if not name or not math.isfinite(value):
            raise ValueError("BodyGen morph rows require finite named values")
        if abs(value) < 0.001:
            continue
        key = name.casefold()
        if key in active:
            raise ValueError(
                f"BodyGen morph {name} is selected more than once"
            )
        active[key] = (name, value)

    available: set[str] = set()
    resolved: set[str] = set()
    morphed_meshes: set[str] = set()
    position_offsets = 0
    uv_offsets = 0
    for row in tri_rows:
        tri_path = Path(str(row["path"])).resolve()
        tri_key = bodygen_asset_key(str(row["assetPath"]), True)
        candidates = [
            obj
            for obj in meshes
            if bodygen_asset_key(
                str(obj.get("NPCM_ASSET_PATH", "")),
                False,
            ) == tri_key
        ]
        if not candidates:
            raise ValueError(
                f"PIRT {tri_path.name} has no matching imported NIF"
            )
        catalog = parse_pirt(tri_path)
        for shape in catalog.values():
            available.update(shape["morphs"])
            objects = [
                obj
                for obj in candidates
                if canonical_object_name(obj.name) ==
                shape["name"].casefold()
            ]
            if len(objects) != 1:
                raise ValueError(
                    f"PIRT shape {shape['name']} from {tri_path.name} "
                    f"matched {len(objects)} imported meshes"
                )
            obj = objects[0]
            for morph_key, morph in shape["morphs"].items():
                selected = active.get(morph_key)
                if selected is None:
                    continue
                _, weight = selected
                applied = False
                for vertex_index, values in morph.get("position", []):
                    if vertex_index >= len(obj.data.vertices):
                        raise ValueError(
                            f"PIRT {tri_path.name} vertex "
                            f"{vertex_index} exceeds {obj.name}"
                        )
                    delta = Vector(values) * weight
                    keys = obj.data.shape_keys
                    if keys is None:
                        obj.data.vertices[vertex_index].co += delta
                    else:
                        for key_block in keys.key_blocks:
                            key_block.data[vertex_index].co += delta
                    position_offsets += 1
                    applied = True
                uv_layer = obj.data.uv_layers.active
                uv_morph = morph.get("uv", [])
                if uv_morph and uv_layer is None:
                    raise ValueError(
                        f"PIRT {tri_path.name} has UV offsets for "
                        f"{obj.name}, but the imported mesh has no UV layer"
                    )
                if uv_morph:
                    loops_by_vertex: dict[int, list[int]] = {}
                    for loop in obj.data.loops:
                        loops_by_vertex.setdefault(
                            loop.vertex_index, []
                        ).append(loop.index)
                    for vertex_index, values in uv_morph:
                        if vertex_index >= len(obj.data.vertices):
                            raise ValueError(
                                f"PIRT {tri_path.name} UV vertex "
                                f"{vertex_index} exceeds {obj.name}"
                            )
                        for loop_index in loops_by_vertex.get(
                            vertex_index, []
                        ):
                            uv_layer.data[loop_index].uv.x += (
                                values[0] * weight
                            )
                            uv_layer.data[loop_index].uv.y += (
                                values[1] * weight
                            )
                            uv_offsets += 1
                            applied = True
                if applied:
                    resolved.add(morph_key)
                    morphed_meshes.add(obj.name)
                    obj["NPCM_BODYGEN_TRI"] = str(tri_path)
    return {
        "bodyGenTriAssetCount": len(tri_rows),
        "bodyGenRequestedMorphCount": len(active),
        "bodyGenResolvedMorphCount": len(resolved),
        "bodyGenUnresolvedMorphCount": len(set(active) - available),
        "bodyGenMorphedMeshCount": len(morphed_meshes),
        "bodyGenPositionOffsetCount": position_offsets,
        "bodyGenUvOffsetCount": uv_offsets,
    }


def import_assets(
    rows: list[dict], weight_percent: float
) -> tuple[list[object], list[object], dict]:
    meshes: list[object] = []
    armatures: list[object] = []
    face_imports = 0
    weight_companions = 0
    weight_morphed_meshes = 0
    for row in rows:
        role = str(row["role"])
        path = Path(row["path"]).resolve()
        before = set(bpy.data.objects)
        result = bpy.ops.import_scene.pynifly(
            filepath=str(path),
            mesh_only=False,
            import_tris=False,
            import_collisions=False,
            import_animations=False,
            create_bones=True,
            create_collection=False,
        )
        if "CANCELLED" in result:
            raise ValueError(f"PyNifly cancelled import of {path.name}")
        imported = list(set(bpy.data.objects) - before)
        imported_meshes = [item for item in imported if item.type == "MESH"]
        if not imported_meshes:
            raise ValueError(f"PyNifly imported no mesh from {path.name}")
        if role == "FaceGeom":
            face_imports += 1
        for obj in imported_meshes:
            obj["NPCM_ROLE"] = role
            obj["NPCM_ASSET_PATH"] = str(row["assetPath"])
            obj["NPCM_SOURCE_PATH"] = str(path)
            obj["NPCM_SOURCE_SHA256"] = str(row["sha256"])
            obj.hide_render = False
            obj.hide_viewport = False
        for obj in imported:
            if obj.type == "ARMATURE":
                obj["NPCM_SOURCE_PATH"] = str(path)
                armatures.append(obj)
        companion_count, morphed_count = apply_weight_companion(
            row, imported_meshes, weight_percent
        )
        weight_companions += companion_count
        weight_morphed_meshes += morphed_count
        meshes.extend(imported_meshes)
    if face_imports != 1:
        raise ValueError(
            f"the authoritative FaceGeom was imported {face_imports} times"
        )
    return meshes, armatures, {
        "weightCompanionAssetCount": weight_companions,
        "weightMorphedMeshCount": weight_morphed_meshes,
        "appliedWeightPercent": weight_percent,
    }


def omit_materialless_helpers(meshes: list[object]) -> tuple[list[object], list[object]]:
    retained, omitted = [], []
    for obj in meshes:
        materialless = not any(material is not None for material in obj.data.materials)
        direct_marker = any(
            child.type == "EMPTY"
            and child.get("NiStringExtraData_Name") == "HDT Skinned Mesh Physics Object"
            and str(child.get("NiStringExtraData_Value", "")).strip()
            for child in obj.children
        )
        if materialless and (
            blender_base_name(obj.name).casefold().startswith("virtual")
            or direct_marker or len(obj.data.polygons) == 0
        ):
            obj.hide_render = True
            obj.hide_viewport = True
            omitted.append(obj)
        else:
            retained.append(obj)
    return retained, omitted


def hex_rgb(value: str | None, default: tuple[float, float, float]) -> tuple[float, float, float]:
    if not value:
        return default
    text = value.lstrip("#")
    if len(text) != 6:
        return default
    try:
        return tuple(int(text[index:index + 2], 16) / 255.0 for index in (0, 2, 4))
    except ValueError:
        return default


def custom_texture_bindings(material: object) -> dict[str, str]:
    return {
        str(key): str(value)
        for key, value in material.items()
        if str(key).startswith("BSShaderTextureSet_") and value
    }


def resolve_preview_texture(data_root: Path, skyrim_path: str) -> Path | None:
    normalized = skyrim_path.replace("\\", "/").lstrip("/")
    candidate = (data_root / normalized).resolve()
    try:
        candidate.relative_to(data_root)
    except ValueError:
        return None
    png = candidate.with_suffix(".png")
    return png if png.is_file() else None


def shader_group(material: object) -> object | None:
    if material is None or not material.use_nodes:
        return None
    for node in material.node_tree.nodes:
        if node.type == "GROUP" and "Diffuse" in node.inputs:
            return node
    return None


def blender_base_name(value: str) -> str:
    if len(value) > 4 and value[-4] == "." and value[-3:].isdigit():
        return value[:-4]
    return value


def resolved_material_descriptor(
    request: dict, obj: object
) -> dict | None:
    asset_path = str(obj.get("NPCM_ASSET_PATH", "")).replace("\\", "/").casefold()
    shape = blender_base_name(obj.name).casefold()
    for asset in request.get("assets", []):
        if str(asset.get("assetPath", "")).replace("\\", "/").casefold() != asset_path:
            continue
        materials = asset.get("materials", [])
        exact = [
            item
            for item in materials
            if str(item.get("shape", "")).casefold() == shape
        ]
        if len(exact) == 1:
            return exact[0]
        if len(materials) == 1:
            return materials[0]
    return None


def binding_for_slot(material: object, slot: int) -> tuple[str, str | None, str]:
    if slot == 0:
        return "Diffuse", "Diffuse_Texture", "sRGB"
    if slot == 1:
        return "Normal", "Normal_Texture", "Non-Color"
    if slot == 2:
        if (
            "Subsurface_Texture" in material.node_tree.nodes
            or "BSShaderTextureSet_SoftLighting" in material
        ):
            return "SoftLighting", "Subsurface_Texture", "Non-Color"
        return "Glow", "Glow_Map_Texture", "Non-Color"
    if slot == 3:
        return "HeightMap", None, "Non-Color"
    if slot == 4:
        return "EnvMap", None, "Non-Color"
    if slot == 5:
        return "EnvMask", None, "Non-Color"
    if slot == 6:
        return "FacegenDetail", None, "sRGB"
    if slot == 7:
        return "Specular", "Specular_Texture", "Non-Color"
    return f"Slot{slot}", None, "Non-Color"


def ensure_routed_image_node(
    material: object, node_name: str, binding: str
) -> object | None:
    node = material.node_tree.nodes.get(node_name)
    if node is not None:
        return node
    group = shader_group(material)
    if group is None:
        return None
    input_names = {
        "Diffuse": ("Diffuse",),
        "Normal": ("Normal",),
        "SoftLighting": ("Subsurface",),
        "Glow": ("Emission Color",),
        "Specular": ("Specular", "Specular Color"),
    }.get(binding, ())
    target = next(
        (group.inputs[name] for name in input_names if name in group.inputs),
        None,
    )
    if target is None:
        return None
    node = material.node_tree.nodes.new("ShaderNodeTexImage")
    node.name = node_name
    node.label = f"NPCM resolved {binding}"
    diffuse = material.node_tree.nodes.get("Diffuse_Texture")
    if (
        diffuse is not None
        and diffuse.inputs["Vector"].links
    ):
        material.node_tree.links.new(
            diffuse.inputs["Vector"].links[0].from_socket,
            node.inputs["Vector"],
        )
    for link in list(target.links):
        material.node_tree.links.remove(link)
    material.node_tree.links.new(node.outputs["Color"], target)
    return node


def apply_resolved_material_routes(
    meshes: list[object], data_root: Path, request: dict
) -> dict:
    matched_materials = 0
    applied_bindings = 0
    applied_images = 0
    missing = 0
    seen: set[int] = set()
    for obj in meshes:
        descriptor = resolved_material_descriptor(request, obj)
        if descriptor is None:
            missing += len(obj.data.materials)
            continue
        for material in obj.data.materials:
            if material is None or material.as_pointer() in seen:
                continue
            seen.add(material.as_pointer())
            matched_materials += 1
            for slot in descriptor.get("textureSlots", []):
                index = int(slot["slot"])
                asset_path = str(slot["assetPath"])
                image_path = resolve_preview_texture(data_root, asset_path)
                if image_path is None:
                    missing += 1
                    continue
                binding, node_name, color_space = binding_for_slot(
                    material, index
                )
                material[f"BSShaderTextureSet_{binding}"] = asset_path
                applied_bindings += 1
                if node_name is None:
                    continue
                node = ensure_routed_image_node(
                    material, node_name, binding
                )
                if node is None:
                    missing += 1
                    continue
                image = bpy.data.images.load(
                    str(image_path), check_existing=True
                )
                image.colorspace_settings.name = color_space
                node.image = image
                applied_images += 1
    return {
        "resolvedMaterialCount": matched_materials,
        "resolvedTextureBindingCount": applied_bindings,
        "resolvedTextureImageCount": applied_images,
        "unresolvedTextureBindingCount": missing,
    }


def multiply_diffuse_by_image(
    material: object, group: object, image_path: Path, label: str
) -> bool:
    tree = material.node_tree
    socket = group.inputs["Diffuse"]
    incoming = list(socket.links)
    if not incoming:
        return False
    diffuse_socket = incoming[0].from_socket
    for link in incoming:
        tree.links.remove(link)
    node = tree.nodes.new("ShaderNodeTexImage")
    node.name = label
    node.label = label
    node.image = bpy.data.images.load(str(image_path), check_existing=True)
    # Skyrim's FaceTint texture-set slot 6 is sampled as linear data and
    # multiplied onto the sRGB-decoded slot-0 face diffuse. It is not a
    # replacement diffuse.
    node.image.colorspace_settings.name = "Non-Color"
    diffuse = tree.nodes.get("Diffuse_Texture")
    if diffuse is not None and diffuse.inputs["Vector"].links:
        tree.links.new(
            diffuse.inputs["Vector"].links[0].from_socket,
            node.inputs["Vector"],
        )
    # The verified Skyrim FaceTint shader maps the raw tint sample to a
    # multiplier with neutral bytes (63, 64, 63):
    #   fgTint = (sample + (1/255, 0, 1/255)) * (255/64)
    # and then multiplies that value by the slot-0 albedo.
    offset = tree.nodes.new("ShaderNodeVectorMath")
    offset.name = "NPCM FaceTint channel offset"
    offset.label = "NPCM FaceTint channel offset"
    offset.operation = "ADD"
    offset.inputs[1].default_value = (1.0 / 255.0, 0.0, 1.0 / 255.0)
    tree.links.new(node.outputs["Color"], offset.inputs[0])
    amplify = tree.nodes.new("ShaderNodeVectorMath")
    amplify.name = "NPCM FaceTint ×255÷64"
    amplify.label = "NPCM FaceTint ×255÷64"
    amplify.operation = "SCALE"
    amplify.inputs[3].default_value = 255.0 / 64.0
    tree.links.new(offset.outputs["Vector"], amplify.inputs[0])
    multiply = tree.nodes.new("ShaderNodeVectorMath")
    multiply.name = "NPCM slot0 diffuse × slot6 FaceTint"
    multiply.label = "NPCM slot0 diffuse × slot6 FaceTint"
    multiply.operation = "MULTIPLY"
    tree.links.new(diffuse_socket, multiply.inputs[0])
    tree.links.new(amplify.outputs["Vector"], multiply.inputs[1])
    tree.links.new(multiply.outputs["Vector"], socket)
    material["NPCM_COMPOSITION"] = "slot0-diffuse-times-slot6-facetint"
    material["NPCM_FACETINT_IMAGE"] = str(image_path)
    return True


def multiply_diffuse(
    material: object,
    group: object,
    color: tuple[float, float, float],
    factor: float,
    label: str,
) -> None:
    tree = material.node_tree
    socket = group.inputs["Diffuse"]
    incoming = list(socket.links)
    if not incoming:
        return
    source_socket = incoming[0].from_socket
    for link in incoming:
        tree.links.remove(link)
    mix = tree.nodes.new("ShaderNodeMixRGB")
    mix.name = label
    mix.label = label
    mix.blend_type = "MULTIPLY"
    mix.inputs[0].default_value = factor
    mix.inputs[2].default_value = (*color, 1.0)
    tree.links.new(source_socket, mix.inputs[1])
    tree.links.new(mix.outputs["Color"], socket)


def softlight_diffuse_with_color(
    material: object,
    group: object,
    color: tuple[float, float, float],
    factor: float,
    label: str,
) -> None:
    tree = material.node_tree
    socket = group.inputs["Diffuse"]
    incoming = list(socket.links)
    if not incoming:
        return
    source_socket = incoming[0].from_socket
    for link in incoming:
        tree.links.remove(link)
    mix = tree.nodes.new("ShaderNodeMixRGB")
    mix.name = label
    mix.label = label
    mix.blend_type = "SOFT_LIGHT"
    mix.inputs[0].default_value = factor
    mix.inputs[2].default_value = (*color, 1.0)
    tree.links.new(source_socket, mix.inputs[1])
    tree.links.new(mix.outputs["Color"], socket)


def apply_package_tints(meshes: list[object], data_root: Path, request: dict) -> dict:
    skin = hex_rgb(request.get("skinTint"), (0.78, 0.58, 0.48))
    skin_alpha = max(
        0.0, min(1.0, float(request.get("skinTintAlpha", 1.0)))
    )
    baked_hair_tints: dict[str, tuple[float, float, float]] = {}
    for asset in request.get("assets", []):
        if str(asset.get("role", "")) != "FaceGeom":
            continue
        for material in asset.get("materials", []):
            tint = material.get("tintHex")
            shape = str(material.get("shape", "")).strip()
            if tint and shape:
                baked_hair_tints[shape.casefold()] = hex_rgb(
                    tint, (0.22, 0.16, 0.10)
                )
    face_tint_applied = 0
    face_diffuse_times_tint = 0
    hair_tint_applied = 0
    skin_tint_applied = 0
    seen: set[int] = set()
    for obj in meshes:
        role = str(obj.get("NPCM_ROLE", ""))
        descriptor = resolved_material_descriptor(request, obj)
        shader_type = (
            int(descriptor.get("shaderType", 0))
            if descriptor is not None
            else 0
        )
        for material in obj.data.materials:
            if material is None or material.as_pointer() in seen:
                continue
            seen.add(material.as_pointer())
            group = shader_group(material)
            if group is None:
                continue
            bindings = custom_texture_bindings(material)
            face_path = next(
                (
                    resolve_preview_texture(data_root, value)
                    for key, value in bindings.items()
                    if "facetint" in key.casefold()
                    or "/facetint/" in value.replace("\\", "/").casefold()
                ),
                None,
            )
            if role == "FaceGeom" and face_path is not None:
                if multiply_diffuse_by_image(
                    material,
                    group,
                    face_path,
                    "NPCM canonical slot-6 FaceTint",
                ):
                    face_tint_applied += 1
                    face_diffuse_times_tint += 1
                continue
            tokens = " ".join(
                [obj.name, material.name, *bindings.values()]
            ).casefold()
            if role == "FaceGeom" and "hair" in tokens:
                object_key = obj.name.casefold()
                baked = baked_hair_tints.get(object_key)
                if baked is None and "." in object_key:
                    baked = baked_hair_tints.get(object_key.rsplit(".", 1)[0])
                if baked is not None:
                    multiply_diffuse(
                        material,
                        group,
                        baked,
                        1.0,
                        "NPCM authoritative FaceGeom HairTint",
                    )
                    hair_tint_applied += 1
            elif (
                role in {"Body", "Hands", "Feet", "Outfit"}
                and shader_type == 5
                and skin_alpha > 0.001
            ):
                softlight_diffuse_with_color(
                    material,
                    group,
                    skin,
                    skin_alpha,
                    "NPCM authoritative QNAM SkinTint soft-light",
                )
                material["NPCM_COMPOSITION"] = (
                    "diffuse-softlight-qnam-skintint"
                )
                skin_tint_applied += 1
    return {
        "faceTintMaterialCount": face_tint_applied,
        "faceDiffuseTimesFaceTintCount": face_diffuse_times_tint,
        "hairTintMaterialCount": hair_tint_applied,
        "skinTintMaterialCount": skin_tint_applied,
    }


def make_fallback_material() -> object:
    material = bpy.data.materials.new("NPCM_MISSING_MATERIAL")
    material.use_nodes = True
    bsdf = material.node_tree.nodes.get("Principled BSDF")
    if bsdf is not None:
        bsdf.inputs["Base Color"].default_value = (1.0, 0.0, 0.8, 1.0)
        bsdf.inputs["Roughness"].default_value = 0.8
    return material


def ensure_materials(meshes: list[object]) -> int:
    fallback = make_fallback_material()
    count = 0
    for obj in meshes:
        if len(obj.data.materials) == 0:
            obj.data.materials.append(fallback)
            count += 1
    return count


def evaluated_points(objects: list[object]) -> list[Vector]:
    graph = bpy.context.evaluated_depsgraph_get()
    points: list[Vector] = []
    for obj in objects:
        evaluated = obj.evaluated_get(graph)
        for vertex in evaluated.data.vertices:
            points.append(evaluated.matrix_world @ vertex.co)
    return points


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    if not ordered:
        raise ValueError("cannot compute an empty percentile")
    index = max(0, min(len(ordered) - 1, round((len(ordered) - 1) * fraction)))
    return ordered[index]


def robust_bounds(objects: list[object], trim: float = 0.0) -> tuple[Vector, Vector]:
    points = evaluated_points(objects)
    if not points:
        raise ValueError("imported meshes have no evaluated vertices")
    minimum = Vector(
        tuple(percentile([p[index] for p in points], trim) for index in range(3))
    )
    maximum = Vector(
        tuple(percentile([p[index] for p in points], 1.0 - trim) for index in range(3))
    )
    return minimum, maximum


def align_facegeom_to_body(meshes: list[object]) -> dict:
    """Reconcile head-node-relative FaceGeom with body-world preview coordinates.

    High Poly Head FaceGeom can contain a complete, internally coherent face
    assembly whose imported world frame is still the Skyrim head-node frame.
    The game supplies the missing skeleton placement at runtime; the standalone
    preview must instead align the anatomical head neck seam to the body neck.
    """
    face_meshes = [
        obj for obj in meshes
        if str(obj.get("NPCM_ROLE", "")) == "FaceGeom"
    ]
    body_meshes = [
        obj for obj in meshes
        if str(obj.get("NPCM_ROLE", "")) == "Body"
    ]
    head_surfaces = [
        obj for obj in face_meshes
        if "privatefacehead" in obj.name.casefold()
    ]
    if not face_meshes or not body_meshes or not head_surfaces:
        return {
            "applied": False,
            "reason": "required FaceGeom, Body, or private face-head surface absent",
            "offsetZ": 0.0,
            "headSurfaceCount": len(head_surfaces),
        }

    head_min, _ = robust_bounds(head_surfaces, 0.0)
    body_min, body_max = robust_bounds(body_meshes, 0.0)
    offset_z = float(body_max.z - head_min.z)
    body_height = float(body_max.z - body_min.z)
    threshold = max(2.0, body_height * 0.05)
    if abs(offset_z) <= threshold:
        return {
            "applied": False,
            "reason": "FaceGeom and body already share a compatible world frame",
            "offsetZ": offset_z,
            "headSurfaceCount": len(head_surfaces),
            "bodyHeight": body_height,
            "threshold": threshold,
        }

    face_set = set(face_meshes)
    safe_parents = set()
    for obj in face_meshes:
        parent = obj.parent
        if parent is None:
            continue
        descendant_meshes = {
            child for child in parent.children_recursive
            if child.type == "MESH"
        }
        if descendant_meshes and descendant_meshes.issubset(face_set):
            safe_parents.add(parent)

    moved_meshes = set()
    for parent in safe_parents:
        parent.location.z += offset_z
        moved_meshes.update(
            child for child in parent.children_recursive
            if child in face_set
        )
    for obj in face_meshes:
        if obj not in moved_meshes:
            obj.location.z += offset_z
    bpy.context.view_layer.update()

    aligned_head_min, _ = robust_bounds(head_surfaces, 0.0)
    return {
        "applied": True,
        "reason": "aligned private face-head neck seam to body neck seam",
        "offsetZ": offset_z,
        "headSurfaceCount": len(head_surfaces),
        "bodyHeight": body_height,
        "threshold": threshold,
        "parentNodeCount": len(safe_parents),
        "directMeshCount": len(face_set - moved_meshes),
        "headNeckZBefore": float(head_min.z),
        "headNeckZAfter": float(aligned_head_min.z),
        "bodyNeckZ": float(body_max.z),
    }


def face_camera_bounds(face_meshes: list[object]) -> tuple[Vector, Vector, bool]:
    facial = []
    for obj in face_meshes:
        token = obj.name.casefold()
        if any(part in token for part in ("head", "face", "eye", "mouth", "brow")) and not any(
            part in token for part in ("hair", "scalp", "beard")
        ):
            facial.append(obj)
    if facial:
        return (*robust_bounds(facial, 0.01), True)
    # The fallback still uses only the authoritative FaceGeom, with outlier
    # trimming so long hair cannot define the face frame.
    return (*robust_bounds(face_meshes, 0.08), True)


def look_at(camera: object, target: Vector) -> None:
    camera.rotation_euler = (
        target - camera.location
    ).to_track_quat("-Z", "Y").to_euler()


def configure_scene(scene: object, width: int, height: int) -> None:
    scene.render.engine = "BLENDER_EEVEE_NEXT"
    scene.render.resolution_x = width
    scene.render.resolution_y = height
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.render.film_transparent = False
    scene.render.image_settings.color_depth = "8"
    scene.render.image_settings.compression = 100
    scene.render.use_file_extension = True
    scene.render.dither_intensity = 0.0
    scene.view_settings.look = "AgX - Medium High Contrast"
    scene.view_settings.view_transform = "AgX"
    scene.view_settings.exposure = 0.0
    scene.view_settings.gamma = 1.0
    scene.world.color = (0.035, 0.045, 0.06)


def clear_lights() -> None:
    for obj in list(bpy.data.objects):
        if obj.type == "LIGHT":
            bpy.data.objects.remove(obj, do_unlink=True)


def add_area_light(
    name: str,
    location: Vector,
    energy: float,
    size: float,
    color: tuple[float, float, float],
    target: Vector,
) -> None:
    data = bpy.data.lights.new(name, type="AREA")
    data.energy = energy
    data.shape = "DISK"
    data.size = size
    data.color = color
    obj = bpy.data.objects.new(name, data)
    bpy.context.scene.collection.objects.link(obj)
    obj.location = location
    obj.rotation_euler = (target - obj.location).to_track_quat("-Z", "Y").to_euler()


def configure_lighting(
    center: Vector,
    size: float,
    alternate: bool,
    inspection_gain: float = 1.0,
) -> None:
    clear_lights()
    # Blender's area-light energy is total emitted power. The light surface
    # and its distance both scale with the framed subject, so constant
    # irradiance requires a fourth-power scale rather than distance alone.
    scale = max((size / 50.0) ** 4, 1.0)
    if alternate:
        add_area_light(
            "NPCM alternate key",
            center + Vector((size * 1.7, size * 2.1, size * 1.1)),
            6500 * scale * inspection_gain,
            size * 1.2,
            (0.55, 0.68, 1.0),
            center,
        )
        add_area_light(
            "NPCM alternate fill",
            center + Vector((-size * 1.1, size * 1.2, size * 0.2)),
            3000 * scale * inspection_gain,
            size,
            (1.0, 0.48, 0.25),
            center,
        )
    else:
        add_area_light(
            "NPCM key",
            center + Vector((-size * 1.4, size * 2.2, size * 1.7)),
            7200 * scale * inspection_gain,
            size * 1.15,
            (1.0, 0.86, 0.72),
            center,
        )
        add_area_light(
            "NPCM fill",
            center + Vector((size * 1.35, size * 1.45, size * 0.45)),
            3600 * scale * inspection_gain,
            size,
            (0.62, 0.75, 1.0),
            center,
        )
        add_area_light(
            "NPCM rim",
            center + Vector((0, -size * 1.6, size * 1.4)),
            4200 * scale * inspection_gain,
            size * 0.8,
            (1.0, 0.93, 0.82),
            center,
        )


def configure_camera(
    scene: object,
    center: Vector,
    extent: Vector,
    view_id: str,
) -> None:
    if scene.camera is None:
        camera_data = bpy.data.cameras.new("NPCM preview camera")
        camera = bpy.data.objects.new("NPCM preview camera", camera_data)
        scene.collection.objects.link(camera)
        scene.camera = camera
    camera = scene.camera
    camera.data.type = "ORTHO"
    span = max(extent.x, extent.z, 1.0)
    camera.data.ortho_scale = span * (
        1.42 if view_id.startswith("face-") else 1.18
    )
    distance = max(span * 3.0, 100.0)
    if view_id == "body-back":
        camera.location = center + Vector((0, -distance, extent.z * 0.02))
    elif view_id == "face-left":
        camera.location = center + Vector((-distance * 0.78, distance * 0.78, extent.z * 0.03))
    elif view_id == "face-right":
        camera.location = center + Vector((distance * 0.78, distance * 0.78, extent.z * 0.03))
    else:
        camera.location = center + Vector((0, distance, extent.z * 0.03))
    look_at(camera, center)


def set_visibility(meshes: list[object], face_view: bool) -> list[object]:
    visible: list[object] = []
    for obj in meshes:
        role = str(obj.get("NPCM_ROLE", ""))
        show = True
        if face_view:
            show = role in {"FaceGeom", "Body", "Hands", "Outfit"}
        obj.hide_render = not show
        obj.hide_viewport = not show
        if show:
            visible.append(obj)
    return visible


def canonicalize_png(path: Path) -> bytes:
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
        raise ValueError("PNG is missing a valid IHDR")
    width, height, bit_depth, color_type, _, _, interlace = struct.unpack(
        ">IIBBBBB", header
    )
    if bit_depth != 8 or color_type != 6 or interlace != 0:
        raise ValueError("PNG must be non-interlaced 8-bit RGBA")
    stride = width * 4
    raw = zlib.decompress(compressed)
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
                distances = (
                    abs(estimate - left),
                    abs(estimate - above),
                    abs(estimate - upper_left),
                )
                predictor = (left, above, upper_left)[
                    distances.index(min(distances))
                ]
                current[index] = (current[index] + predictor) & 0xFF
            elif filter_type != 0:
                raise ValueError(f"unsupported PNG filter {filter_type}")
        pixels[row * stride:(row + 1) * stride] = current
        previous = current

    def chunk(kind: bytes, payload: bytes) -> bytes:
        return (
            struct.pack(">I", len(payload))
            + kind
            + payload
            + struct.pack(">I", binascii.crc32(kind + payload) & 0xFFFFFFFF)
        )

    canonical = bytearray(b"\x89PNG\r\n\x1a\n")
    canonical.extend(chunk(b"IHDR", header))
    scanlines = b"".join(
        b"\x00" + pixels[row * stride:(row + 1) * stride]
        for row in range(height)
    )
    canonical.extend(chunk(b"IDAT", zlib.compress(scanlines, 9)))
    canonical.extend(chunk(b"IEND", b""))
    path.write_bytes(canonical)
    return bytes(pixels)


def role_pixel_counts(pixels: bytes) -> dict[str, int]:
    counts = {role: 0 for role in ROLE_COLORS}
    for index in range(0, len(pixels), 4):
        red, green, blue, alpha = pixels[index:index + 4]
        if alpha < 16 or max(red, green, blue) < 20:
            continue
        if red > 180 and green > 180 and blue < 120:
            counts["Hands"] += 1
        elif red > 180 and blue > 180 and green < 120:
            counts["Feet"] += 1
        elif red > green * 1.5 and red > blue * 1.5:
            counts["FaceGeom"] += 1
        elif green > red * 1.5 and green > blue * 1.5:
            counts["Body"] += 1
        elif blue > red * 1.35 and blue > green * 1.15:
            counts["Outfit"] += 1
    return counts


def render_role_mask(
    scene: object, meshes: list[object], output: Path
) -> tuple[dict[str, int], bytes]:
    saved: dict[object, list[object]] = {}
    role_materials: dict[str, object] = {}
    for role, color in ROLE_COLORS.items():
        material = bpy.data.materials.new(f"NPCM mask {role}")
        material.use_nodes = True
        nodes = material.node_tree.nodes
        bsdf = nodes.get("Principled BSDF")
        if bsdf is not None:
            bsdf.inputs["Base Color"].default_value = color
            bsdf.inputs["Emission Color"].default_value = color
            bsdf.inputs["Emission Strength"].default_value = 1.0
            bsdf.inputs["Roughness"].default_value = 1.0
        role_materials[role] = material
    prior_transform = scene.view_settings.view_transform
    prior_look = scene.view_settings.look
    prior_world = tuple(scene.world.color)
    for obj in meshes:
        saved[obj] = list(obj.data.materials)
        obj.data.materials.clear()
        obj.data.materials.append(
            role_materials.get(
                str(obj.get("NPCM_ROLE", "")),
                role_materials["Skeleton"],
            )
        )
    scene.view_settings.view_transform = "Standard"
    scene.view_settings.look = "Medium High Contrast"
    scene.world.color = (0.0, 0.0, 0.0)
    scene.render.filepath = str(output)
    bpy.ops.render.render(write_still=True)
    pixels = canonicalize_png(output)
    for obj, materials in saved.items():
        obj.data.materials.clear()
        for material in materials:
            obj.data.materials.append(material)
    scene.view_settings.view_transform = prior_transform
    scene.view_settings.look = prior_look
    scene.world.color = prior_world
    return role_pixel_counts(pixels), pixels


def role_mean_luminance(
    image_pixels: bytes, mask_pixels: bytes, role: str
) -> float:
    total = 0.0
    count = 0
    for index in range(0, len(image_pixels), 4):
        red, green, blue, _ = image_pixels[index:index + 4]
        mr, mg, mb, ma = mask_pixels[index:index + 4]
        if ma < 16:
            continue
        selected = (
            role == "Body"
            and mg > mr * 1.5
            and mg > mb * 1.5
        ) or (
            role == "Hands"
            and mr > 180
            and mg > 180
            and mb < 120
        ) or (
            role == "FaceGeom"
            and mr > mg * 1.5
            and mr > mb * 1.5
        )
        if not selected:
            continue
        total += 0.2126 * red + 0.7152 * green + 0.0722 * blue
        count += 1
    return total / count if count else 0.0


def mesh_evidence(meshes: list[object]) -> list[dict]:
    evidence: list[dict] = []
    for obj in sorted(meshes, key=lambda item: (str(item.get("NPCM_ROLE", "")), item.name)):
        evidence.append(
            {
                "role": str(obj.get("NPCM_ROLE", "")),
                "assetPath": str(obj.get("NPCM_ASSET_PATH", "")),
                "objectName": obj.name,
                "vertexCount": len(obj.data.vertices),
                "materials": [
                    material.name
                    for material in obj.data.materials
                    if material is not None
                ],
                "worldTransform": [
                    float(obj.matrix_world[row][column])
                    for row in range(4)
                    for column in range(4)
                ],
            }
        )
    return evidence


def material_evidence(meshes: list[object]) -> list[dict]:
    result: list[dict] = []
    for obj in sorted(meshes, key=lambda item: item.name):
        for material in obj.data.materials:
            if material is None:
                continue
            images = []
            if material.use_nodes:
                images = sorted(
                    {
                        str(Path(node.image.filepath).resolve())
                        for node in material.node_tree.nodes
                        if node.type == "TEX_IMAGE"
                        and node.image is not None
                        and node.image.filepath
                    }
                )
            method = str(
                getattr(
                    material,
                    "surface_render_method",
                    getattr(material, "blend_method", "DITHERED"),
                )
            )
            result.append(
                {
                    "objectName": obj.name,
                    "materialName": material.name,
                    "textureBindings": custom_texture_bindings(material),
                    "loadedImages": images,
                    "blendMethod": method,
                    "hasAlpha": method.upper() not in {"DITHERED", "OPAQUE"},
                }
            )
    return result


def validate_request(request: dict) -> tuple[Path, Path, list[dict], int, int]:
    if request.get("schemaVersion") != "npc-preview-scene/2":
        raise ValueError("only npc-preview-scene/2 is supported")
    root = Path(request["root"]).resolve()
    data_root = Path(request["dataRoot"]).resolve()
    output_root = Path(request["outputRoot"]).resolve()
    for path in (data_root, output_root):
        path.relative_to(root)
    if not data_root.is_dir() or not output_root.is_dir():
        raise ValueError("dataRoot and outputRoot must be existing K-local directories")
    width = int(request["width"])
    height = int(request["height"])
    if (width, height) != (900, 900):
        raise ValueError("schema-2 review renders are fixed at 900x900")
    assets = request.get("assets")
    if not isinstance(assets, list) or not 1 <= len(assets) <= 128:
        raise ValueError("assets must contain between 1 and 128 mesh rows")
    if sum(1 for row in assets if row.get("role") == "FaceGeom") != 1:
        raise ValueError("exactly one authoritative FaceGeom asset is required")
    for row in assets:
        path = Path(row["path"]).resolve()
        path.relative_to(output_root)
        if not path.is_file() or path.suffix.casefold() != ".nif":
            raise ValueError("every renderer asset must be a materialized NIF")
        low_weight_path = row.get("lowWeightPath")
        if low_weight_path:
            low_path = Path(str(low_weight_path)).resolve()
            low_path.relative_to(output_root)
            if not low_path.is_file() or low_path.suffix.casefold() != ".nif":
                raise ValueError(
                    "every low-weight companion must be a materialized NIF"
                )
    tri_assets = request.get("triAssets", [])
    if not isinstance(tri_assets, list) or len(tri_assets) > 128:
        raise ValueError("triAssets must be a bounded list")
    for row in tri_assets:
        path = Path(str(row["path"])).resolve()
        path.relative_to(output_root)
        if not path.is_file() or path.suffix.casefold() != ".tri":
            raise ValueError(
                "every BodyGen TRI must be a materialized PIRT file"
            )
    morphs = request.get("morphs", [])
    if not isinstance(morphs, list) or len(morphs) > 4096:
        raise ValueError("morphs must be a bounded list")
    for view_id in VIEW_IDS:
        for suffix in (".png", ".roles.png"):
            if (output_root / f"{view_id}{suffix}").exists():
                raise ValueError("renderer outputs never overwrite existing files")
    return data_root, output_root, assets, width, height


def render(request: dict) -> dict:
    data_root, output_root, assets, width, height = validate_request(request)
    reset_scene()
    configure_pynifly(data_root)
    weight_percent = float(request.get("weight", 100.0))
    if not math.isfinite(weight_percent) or not 0.0 <= weight_percent <= 100.0:
        raise ValueError("weight must be finite and between 0 and 100")
    meshes, armatures, weight_status = import_assets(
        assets, weight_percent
    )
    meshes, omitted = omit_materialless_helpers(meshes)
    alignment_status = align_facegeom_to_body(meshes)
    bodygen_status = apply_bodygen_morphs(
        request.get("triAssets", []),
        request.get("morphs", []),
        meshes,
    )
    fallback_count = ensure_materials(meshes)
    material_route_status = apply_resolved_material_routes(
        meshes, data_root, request
    )
    tint_status = apply_package_tints(meshes, data_root, request)
    tint_status.update(material_route_status)
    tint_status.update(weight_status)
    tint_status.update(bodygen_status)
    scene = bpy.context.scene
    configure_scene(scene, width, height)

    face_meshes = [
        obj for obj in meshes
        if str(obj.get("NPCM_ROLE", "")) == "FaceGeom"
    ]
    face_min, face_max, authoritative_face_frame = face_camera_bounds(face_meshes)
    body_min, body_max = robust_bounds(meshes, 0.002)
    views = []
    aggregate_counts: dict[str, int] = {}
    role_luminance: dict[str, float] = {}
    for view_id in VIEW_IDS:
        face_view = view_id.startswith("face-")
        visible = set_visibility(meshes, face_view)
        minimum, maximum = (
            (face_min, face_max) if face_view else (body_min, body_max)
        )
        center = (minimum + maximum) / 2
        extent = maximum - minimum
        configure_camera(scene, center, extent, view_id)
        configure_lighting(
            center,
            max(extent.x, extent.y, extent.z, 1.0),
            view_id == "face-alternate-light",
            3.0 if view_id.startswith("body-") else 1.0,
        )
        image = output_root / f"{view_id}.png"
        scene.render.filepath = str(image)
        bpy.ops.render.render(write_still=True)
        image_pixels = canonicalize_png(image)
        mask = output_root / f"{view_id}.roles.png"
        counts, mask_pixels = render_role_mask(scene, visible, mask)
        for role, count in counts.items():
            aggregate_counts[f"{view_id}:{role}"] = count
        for role in ("FaceGeom", "Body", "Hands"):
            role_luminance[f"{view_id}:{role}"] = role_mean_luminance(
                image_pixels, mask_pixels, role
            )
        views.append(
            {
                "id": view_id,
                "image": str(image),
                "imageSha256": hashlib.sha256(image.read_bytes()).hexdigest(),
                "roleMask": str(mask),
                "roleMaskSha256": hashlib.sha256(mask.read_bytes()).hexdigest(),
                "width": width,
                "height": height,
            }
        )

    return {
        "rendered": True,
        "blenderVersion": bpy.app.version_string,
        "renderEngine": "BLENDER_EEVEE_NEXT",
        "faceGeomImportCount": 1,
        "faceCameraUsedAuthoritativeGeometry": authoritative_face_frame,
        "faceBodyAlignment": alignment_status,
        "armatureCount": len(armatures),
        "skeletons": sorted(obj.name for obj in armatures),
        "roleMaskPixelCounts": aggregate_counts,
        "roleMeanLuminance": role_luminance,
        "fallbackMaterialCount": fallback_count,
        "omittedMaterialRoutes": mesh_evidence(omitted),
        "tints": tint_status,
        "meshes": mesh_evidence(meshes),
        "materials": material_evidence(meshes),
        "views": views,
    }


def main() -> int:
    args = parse_args()
    try:
        request = json.loads(args.request.read_text(encoding="utf-8-sig"))
        status = render(request)
        args.status.write_text(
            json.dumps(status, indent=2, sort_keys=True),
            encoding="utf-8",
        )
        return 0
    except Exception as exc:
        return fail(args.status, str(exc))


if __name__ == "__main__":
    raise SystemExit(main())
