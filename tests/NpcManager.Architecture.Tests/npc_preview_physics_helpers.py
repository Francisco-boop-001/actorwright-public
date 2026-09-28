"""Actual pinned Blender/PyNifly import and preview helper regression."""
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import struct
import uuid

import bpy

workspace = Path(sys.argv[sys.argv.index("--") + 1]).resolve()
root = workspace / "artifacts/task34" / ("actual-" + uuid.uuid4().hex)
root.mkdir(parents=True)
data = root / "Data"
(data / "textures").mkdir(parents=True)
spec = importlib.util.spec_from_file_location("preview", workspace / "runtime/rendering/render_npc_preview_bundle.py")
preview = importlib.util.module_from_spec(spec)
spec.loader.exec_module(preview)
preview.configure_pynifly(data)
from io_scene_nifly.pyn.pynifly import NifFile, NiStringExtraData

image = bpy.data.images.new("FixtureDiffuse", width=8, height=8)
image.generated_color = (0.75, 0.55, 0.45, 1)
image.filepath_raw = str(data / "textures/diffuse.png")
image.file_format = "PNG"
image.save()
locator = "HDT Skinned Mesh Physics Object"


def remove_shader(path, shape_name):
    # Patch only the shader link on the actual serialized fixture; retain native payloads.
    module_spec = importlib.util.spec_from_file_location("readback", workspace / "runtime/rendering/nif_geometry_readback.py")
    readback = importlib.util.module_from_spec(module_spec)
    module_spec.loader.exec_module(readback)
    raw = bytearray(path.read_bytes())
    reader = readback._Reader(raw)
    reader.take(raw.index(b"\n") + 1 + 9)
    count = reader.u32()
    assert reader.u32() == 100
    for _ in range(3): reader.byte_string()
    for _ in range(reader.u16()): reader.sized_string()
    reader.take(count * 2)
    sizes = [reader.u32() for _ in range(count)]
    string_count = reader.u32()
    reader.u32()
    strings = [reader.sized_string() for _ in range(string_count)]
    reader.take(reader.u32() * 4)
    shape = next(s for s in NifFile(str(path)).shapes if s.name == shape_name)
    offset = reader.pos + sum(sizes[:shape.id])
    block = readback._Reader(raw, offset, offset + sizes[shape.id])
    name, _ = readback._av_object(block, strings, count)
    assert name == shape_name
    block.take(16 + 4)  # sphere and skin link precede the shader link
    struct.pack_into("<i", raw, block.pos, -1)
    path.write_bytes(raw)


def fixture(name, helper=None, material=False, marker=None, triangles=True):
    path = data / (name + ".nif")
    nif = NifFile()
    nif.initialize("SKYRIMSE", str(path))
    names = ["Head" if name == "face" else "OutfitSurface"]
    if helper:
        names.append(helper)
    for shape_name in names:
        is_helper = shape_name == helper
        shift = 10000 if is_helper else 0
        shape = nif.createShapeFromData(shape_name,
            [(-1 + shift, 0, 0), (1 + shift, 0, 0), (shift, 0, 2)],
            [] if is_helper and not triangles else [(0, 1, 2)],
            [(0, 0), (1, 0), (0.5, 1)], [(0, -1, 0)] * 3)
        if not is_helper or material:
            shape.set_texture("Diffuse", "textures/diffuse.dds")
            shape.save_shader_attributes()
        if is_helper and marker == "mesh":
            NiStringExtraData.New(nif, name=locator, string_value="fixture.xml", parent=shape)
    if marker == "root":
        NiStringExtraData.New(nif, name=locator, string_value="fixture.xml", parent=nif.root)
    nif.save()
    if helper and not material:
        remove_shader(path, helper)
    reopened = NifFile(str(path))
    if helper and not material:
        assert any(s.name == helper and s.properties.shaderPropertyID == 0xFFFFFFFF for s in reopened.shapes), [(s.name, s.properties.shaderPropertyID) for s in reopened.shapes]
    materials = [{"shape": n, "shaderType": 0,
                  "textureSlots": [{"slot": 0, "assetPath": "textures/diffuse.dds"}]} for n in names if n != helper or material]
    return {"role": "FaceGeom" if name == "face" else "Outfit", "assetPath": name + ".nif",
            "path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(), "materials": materials}


face = fixture("face")
outfit = fixture("outfit", "VirtualGround")
request = {"schemaVersion": "npc-preview-scene/2", "root": str(workspace), "dataRoot": str(data),
           "outputRoot": str(root), "width": 900, "height": 900, "weight": 100, "assets": [face, outfit]}
status = preview.render(request)
(root / "status.json").write_text(json.dumps(status, indent=2), encoding="utf-8")
assert status["tints"]["unresolvedTextureBindingCount"] == 0, "VirtualGround still causes material-route-incomplete"
assert status["fallbackMaterialCount"] == 0
assert any(m["objectName"] == "VirtualGround" and m["assetPath"] == "outfit.nif" for m in status["omittedMaterialRoutes"])
assert all(m["objectName"] != "VirtualGround" for m in status["meshes"])
assert bpy.data.objects["VirtualGround"].hide_render
assert len(status["views"]) == 6 and all(Path(v["image"]).is_file() for v in status["views"])

controls = []
for name, helper, material, marker, triangles, omitted in (
    ("ordinary", "OrdinarySurface", False, None, True, False),
    ("root-marker", "OrdinarySurface", False, "root", True, False),
    ("direct-marker", "PhysicsProxy", False, "mesh", True, True),
    ("empty", "EmptyHelper", False, None, False, True),
    ("materialed", "VirtualBody", True, None, True, False),
):
    asset = fixture(name, helper, material, marker, triangles)
    preview.reset_scene()
    meshes, _, _ = preview.import_assets([face, asset], 100)
    obj = next(o for o in meshes if o.name == helper)
    assert bool(obj.data.materials) == material
    kept, removed = preview.omit_materialless_helpers(meshes)
    assert (obj in removed) == omitted
    preview.ensure_materials(kept)
    routes = preview.apply_resolved_material_routes(kept, data, {"assets": [face, asset]})
    assert (routes["unresolvedTextureBindingCount"] == 0) == (omitted or material)
    preview.set_visibility(kept, False)
    assert obj.hide_render == omitted
    controls.append({"case": name, "omitted": omitted, "unresolved": routes["unresolvedTextureBindingCount"]})
for asset in [face, outfit]:
    assert hashlib.sha256(Path(asset["path"]).read_bytes()).hexdigest() == asset["sha256"]
(root / "controls.json").write_text(json.dumps(controls, indent=2), encoding="utf-8")
assert (workspace / "runtime/rendering/render_npc_preview_bundle.py").read_bytes() == (workspace / "tools/rendering/render_npc_preview_bundle.py").read_bytes()
print("PASS actual imported helper routing, six preview views, bounded controls and unchanged NIFs:", root)
