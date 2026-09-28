#!/usr/bin/env python3
"""Focused contract checks for the one-import HairTint renderer mode."""

from __future__ import annotations

import ast
import hashlib
import importlib.util
import json
import re
import sys
import tempfile
import types
from pathlib import Path


PROJECT_ROOT = Path(__file__).resolve().parents[2]
WORKSPACE_ROOT = PROJECT_ROOT.parents[1]
SCRIPT = WORKSPACE_ROOT / "tools" / "rendering" / "render_npc_preview_bundle.py"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise AssertionError(message)


def load_renderer():
    bpy = types.ModuleType("bpy")
    mathutils = types.ModuleType("mathutils")
    mathutils.Vector = object
    sys.modules["bpy"] = bpy
    sys.modules["mathutils"] = mathutils
    spec = importlib.util.spec_from_file_location("npcm_renderer", SCRIPT)
    require(spec is not None and spec.loader is not None, "renderer module spec failed")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_closed_request_and_block_id_names() -> None:
    renderer = load_renderer()
    with tempfile.TemporaryDirectory(
        prefix="hair-region-renderer-",
        dir=PROJECT_ROOT / "03-builds" / "work",
    ) as scratch_text:
        scratch = Path(scratch_text).resolve()
        data_root = scratch / "data"
        output_root = scratch / "output"
        staging_root = scratch / "staging"
        profile_root = scratch / "profile"
        cache_root = scratch / "python-cache"
        data_root.mkdir()
        output_root.mkdir()
        staging_root.mkdir()
        (
            profile_root
            / "scripts"
            / "addons"
            / "io_scene_nifly"
        ).mkdir(parents=True)
        cache_root.mkdir()
        candidate = staging_root / "candidate.nif"
        candidate.write_bytes(b"candidate")
        request = {
            "schema": "npcmanager-facegeom-hair-regions-render-request/1",
            "root": str(scratch),
            "dataRoot": str(data_root),
            "outputRoot": str(output_root),
            "candidatePath": str(candidate),
            "candidateSha256": hashlib.sha256(
                candidate.read_bytes()
            ).hexdigest(),
            "textureFingerprintSha256": hashlib.sha256(
                b""
            ).hexdigest(),
            "textures": [],
            "pyniflyProfileRoot": str(profile_root),
            "pythonCachePrefix": str(cache_root),
            "width": 900,
            "height": 900,
            "regions": [
                {
                    "structuralId": "shape:16:shader:20",
                    "name": "Duplicate",
                    "duplicateNameOrdinal": 0,
                    "shapeBlockType": "BSTriShape",
                    "shapeBlockId": 16,
                    "shaderBlockType": "BSLightingShaderProperty",
                    "shaderBlockId": 20,
                    "currentColor": "#D6BE83",
                },
                {
                    "structuralId": "shape:44:shader:48",
                    "name": "Duplicate",
                    "duplicateNameOrdinal": 1,
                    "shapeBlockType": "BSTriShape",
                    "shapeBlockId": 44,
                    "shaderBlockType": "BSLightingShaderProperty",
                    "shaderBlockId": 48,
                    "currentColor": "#D6BE83",
                },
            ],
        }
        prior_prefix = sys.pycache_prefix
        prior_dont_write = sys.dont_write_bytecode
        sys.pycache_prefix = str(cache_root)
        sys.dont_write_bytecode = True
        try:
            parsed = renderer.validate_hair_regions_request(
                request
            )
        finally:
            sys.pycache_prefix = prior_prefix
            sys.dont_write_bytecode = prior_dont_write
        require(parsed[2] == candidate, "candidate staging path changed")
        require(
            renderer.region_thumbnail_name(request["regions"][0])
            == "region-shape-000016.png",
            "thumbnail is not derived solely from the shape block ID",
        )
        require(
            renderer.region_mask_name(request["regions"][1])
            == "region-shape-000044.mask.png",
            "mask is not derived solely from the shape block ID",
        )
        texture = data_root / "textures" / "face.dds"
        texture.parent.mkdir()
        texture.write_bytes(b"authentic-texture-bytes")
        preview = texture.with_suffix(".png")
        preview.write_bytes(b"decoded-preview-bytes")
        declared_hash = "0" * 64
        asset_path = "textures/face.dds"
        request["textures"] = [
            {
                "assetPath": asset_path,
                "providerKind": "Loose",
                "provider": "fixture",
                "sha256": declared_hash,
                "bytes": texture.stat().st_size,
                "sourcePath": str(texture),
                "previewPath": str(preview),
                "previewSha256": hashlib.sha256(
                    preview.read_bytes()
                ).hexdigest(),
                "previewBytes": preview.stat().st_size,
                "decodeKind": "texconv-dds-to-png",
            }
        ]
        request["textureFingerprintSha256"] = hashlib.sha256(
            (
                f"{asset_path}\0Loose\0fixture\0"
                f"{declared_hash}\0{texture.stat().st_size}\n"
            ).encode("utf-8")
        ).hexdigest()
        sys.pycache_prefix = str(cache_root)
        sys.dont_write_bytecode = True
        try:
            renderer.validate_hair_regions_request(request)
        except ValueError as exception:
            message = str(exception)
            require(
                "expectedSha256=" in message
                and "observedSha256=" in message,
                "texture authority refusal omits exact expected and observed hashes",
            )
        else:
            raise AssertionError(
                "texture authority accepted deliberately wrong source hash"
            )
        finally:
            sys.pycache_prefix = prior_prefix
            sys.dont_write_bytecode = prior_dont_write
        request["textures"] = []
        request["textureFingerprintSha256"] = hashlib.sha256(
            b""
        ).hexdigest()
        request["unexpected"] = True
        sys.pycache_prefix = str(cache_root)
        sys.dont_write_bytecode = True
        try:
            renderer.validate_hair_regions_request(request)
        except ValueError as exception:
            require("unknown" in str(exception).casefold(), "unknown member refusal is unclear")
        else:
            raise AssertionError("closed request accepted an unknown member")
        finally:
            sys.pycache_prefix = prior_prefix
            sys.dont_write_bytecode = prior_dont_write


def test_structural_import_contract() -> None:
    source = SCRIPT.read_text(encoding="utf-8")
    tree = ast.parse(source)
    functions = {
        node.name: node
        for node in tree.body
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))
    }
    require("render_hair_regions" in functions, "hair-region render mode is absent")
    render_source = ast.get_source_segment(
        source, functions["render_hair_regions"]
    ) or ""
    require(
        render_source.count("NifImporter.do_import(") == 1,
        "hair-region mode must call NifImporter.do_import exactly once",
    )
    require(
        "collection=bpy.context.scene.collection" in render_source,
        "direct PyNifly import lacks a concrete Blender collection",
    )
    require(
        "mesh_only=False" in render_source,
        "direct import did not explicitly preserve structural nifnode mapping",
    )
    require(
        "objects_created.find_nifnode(shape)" in source,
        "region mapping does not use PyNifly nifnode authority",
    )
    require(
        "shape.properties.shaderPropertyID" in source,
        "shape-to-shader structural identity is not verified",
    )
    require(
        "bind_hair_region_auxiliary_textures" in functions,
        "hair-region renderer does not bind non-diffuse texture slots",
    )
    auxiliary_source = ast.get_source_segment(
        source,
        functions["bind_hair_region_auxiliary_textures"],
    ) or ""
    fingerprint_source = ast.get_source_segment(
        source,
        functions["loaded_texture_fingerprint"],
    ) or ""
    require(
        "multiply_diffuse_by_image(" in auxiliary_source
        and "ShaderNodeTexImage" not in auxiliary_source
        and "NPCM bound-only" not in auxiliary_source
        and "texture_binding_mode(semantic)" in fingerprint_source
        and '"bindingMode": mode' in fingerprint_source
        and "custom_texture_bindings(" in fingerprint_source
        and "bind_hair_region_auxiliary_textures(" in render_source,
        "hair-region renderer conflates shader-sampled images with "
        "truthfully bound-but-unmodeled material metadata",
    )
    require(
        '"pythonCachePrefix"' in source
        and '"pyniflyProfileRoot"' in source
        and "pynifly_runtime_authority(request)" in render_source,
        "Blender does not prove the source-only PyNifly runtime closure",
    )
    require(
        '"textures"' in source
        and "loaded_texture_fingerprint(meshes, request[\"textures\"])" in render_source,
        "renderer texture evidence is not derived from loaded declared textures",
    )
    require(
        "finally:" in render_source,
        "temporary render state is not restored in finally",
    )
    require(
        "alignment_status = align_facegeom_to_body(meshes)" in source,
        "ordinary npc-preview-scene/2 alignment invocation drifted",
    )


def test_exact_texture_binding_provenance() -> None:
    renderer = load_renderer()
    shared_asset = "textures/actors/character/hair/shared.dds"
    bindings = {
        "BSShaderTextureSet_Diffuse": shared_asset,
        "BSShaderTextureSet_EnvMap": shared_asset,
    }
    semantic, is_custom_binding = (
        renderer.resolve_image_binding_semantic(
            "Diffuse_Texture",
            None,
            bindings,
            shared_asset,
        )
    )
    require(
        semantic == "BSShaderTextureSet_Diffuse"
        and is_custom_binding,
        "a Diffuse node sharing one DDS with EnvMap was not attributed "
        "only to the exact Diffuse shader binding",
    )
    require(
        renderer.texture_binding_mode(semantic) == "SampledImage"
        and renderer.texture_binding_mode(
            "BSShaderTextureSet_EnvMap"
        )
        == "BoundUnmodeled"
        and renderer.texture_binding_mode(
            "BSShaderTextureSet_EnvMask"
        )
        == "BoundUnmodeled",
        "EnvMap or EnvMask is claimed as sampled before the renderer "
        "actually models that shader semantic",
    )
    try:
        renderer.resolve_image_binding_semantic(
            "Unknown imported image",
            None,
            bindings,
            shared_asset,
        )
    except ValueError as exception:
        require(
            "ambiguous" in str(exception).casefold(),
            "ambiguous shared-DDS refusal is unclear",
        )
    else:
        raise AssertionError(
            "an ambiguous image node was attributed to every same-DDS "
            "shader binding"
        )
    declared = {
        shared_asset.casefold(): {"assetPath": shared_asset}
    }
    try:
        renderer.declared_texture_for_binding(
            "BSShaderTextureSet_Diffuse",
            "textures/undeclared.dds",
            declared,
        )
    except ValueError as exception:
        require(
            "undeclared" in str(exception).casefold(),
            "undeclared custom texture refusal is unclear",
        )
    else:
        raise AssertionError(
            "an undeclared custom material texture binding was accepted"
        )


def test_visible_runtime_authority_boundary_pixels() -> None:
    renderer = load_renderer()
    expected_label = (
        "Off-engine HairTint preview — "
        "Skyrim runtime remains authoritative"
    )
    require(
        renderer.HAIR_REGION_BOUNDARY_LABEL == expected_label,
        "the exact visible runtime-authority boundary label drifted",
    )

    source = SCRIPT.read_text(encoding="utf-8")
    tree = ast.parse(source)
    functions = {
        node.name: node
        for node in tree.body
        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))
    }
    render_source = ast.get_source_segment(
        source, functions["render_hair_regions"]
    ) or ""
    contact_source = ast.get_source_segment(
        source, functions["write_hair_region_contact_sheet"]
    ) or ""
    require(
        "combined_pixels = finalize_hair_region_combined_render(combined)"
        in render_source,
        "combined-face pixels do not use the validate-then-stamp boundary",
    )
    require(
        "stamp_hair_region_boundary_label_png(destination)"
        in contact_source,
        "contact-sheet pixels are not stamped with the boundary label",
    )

    expected_pixel_hashes = {
        "combined-face.png":
            "113B17E3BB5F9747ED48F8B08C9B2BA6F3B9DC3537996677059B530171B97E44",
        "contact-sheet.png":
            "E91A7C2E2CC9D8C04ED9D081BCB77CD4505DEA43C1A5F619A3E03EAFCEF06069",
    }
    with tempfile.TemporaryDirectory(
        prefix="hair-region-boundary-label-",
        dir=PROJECT_ROOT / "03-builds" / "work",
    ) as scratch_text:
        scratch = Path(scratch_text).resolve()
        for filename, width, height in (
            ("combined-face.png", 900, 96),
            ("contact-sheet.png", 600, 96),
        ):
            path = scratch / filename
            original_pixels = bytes((17, 31, 47, 255)) * (width * height)
            renderer.write_canonical_rgba_png(
                path,
                width,
                height,
                original_pixels,
            )
            renderer.stamp_hair_region_boundary_label_png(path)
            decoded_width, decoded_height, pixels = (
                renderer.read_rgba_png(path)
            )
            require(
                (decoded_width, decoded_height) == (width, height),
                f"{filename} label stamping changed the image dimensions",
            )
            require(
                pixels[: width * 4]
                == original_pixels[: width * 4],
                f"{filename} label stamping changed pixels above its band",
            )
            require(
                pixels[-width * 4:]
                != original_pixels[-width * 4:],
                f"{filename} has no visibly composited label band",
            )
            pixel_hash = hashlib.sha256(pixels).hexdigest().upper()
            require(
                pixel_hash == expected_pixel_hashes[filename],
                f"{filename} visible label pixel signature drifted: "
                f"{pixel_hash}",
            )
            first_bytes = path.read_bytes()
            renderer.stamp_hair_region_boundary_label_png(path)
            require(
                path.read_bytes() == first_bytes,
                f"{filename} label compositing is not deterministic",
            )


def test_empty_combined_render_is_refused_before_labeling() -> None:
    renderer = load_renderer()
    with tempfile.TemporaryDirectory(
        prefix="hair-region-empty-combined-",
        dir=PROJECT_ROOT / "03-builds" / "work",
    ) as scratch_text:
        combined = Path(scratch_text).resolve() / "combined-face.png"
        width = 900
        height = 96
        empty_pixels = bytes((0, 0, 0, 0)) * (width * height)
        renderer.write_canonical_rgba_png(
            combined,
            width,
            height,
            empty_pixels,
        )
        try:
            renderer.finalize_hair_region_combined_render(combined)
        except ValueError as exception:
            require(
                "empty" in str(exception).casefold(),
                "empty combined-frame refusal is unclear",
            )
        else:
            raise AssertionError(
                "visible boundary label made an empty combined render pass"
            )
        _, _, retained_pixels = renderer.read_rgba_png(combined)
        require(
            retained_pixels == empty_pixels,
            "empty combined render was labeled before it was refused",
        )


def test_shared_script_authority_pins_and_packaging() -> None:
    source_hash = hashlib.sha256(SCRIPT.read_bytes()).hexdigest().upper()
    compositions = (
        (
            PROJECT_ROOT
            / "src"
            / "NpcManager.Cli"
            / "FaceGeomHairRegionsPreviewCliComposition.cs",
            "RendererScriptSha256",
        ),
        (
            PROJECT_ROOT
            / "src"
            / "NpcManager.Cli"
            / "NpcVisualPreviewCliComposition.cs",
            "RendererScriptSha256",
        ),
        (
            PROJECT_ROOT
            / "src"
            / "NpcManager.Desktop"
            / "SkyrimMainWorkspaceDesktopComposition.cs",
            "NpcVisualRendererScriptSha256",
        ),
        (
            PROJECT_ROOT
            / "src"
            / "NpcManager.Desktop"
            / "FaceGeomHairRegionsWizardDesktopComposition.cs",
            "RendererScriptSha256",
        ),
    )
    for composition, symbol in compositions:
        text = composition.read_text(encoding="utf-8")
        match = re.search(
            rf"{symbol}\s*=\s*(?:new)?\s*\(\s*"
            r'"([0-9A-F]{64})"\s*\);',
            text,
            re.DOTALL,
        )
        require(
            match is not None,
            f"{composition.name} has no exact {symbol} pin",
        )
        require(
            match.group(1) == source_hash,
            f"{composition.name} {symbol} does not bind the shared script",
        )

    packaging_markers = (
        r'Include="..\..\..\..\tools\rendering\render_npc_preview_bundle.py"',
        r'Link="runtime\rendering\render_npc_preview_bundle.py"',
        'CopyToOutputDirectory="PreserveNewest"',
        'CopyToPublishDirectory="PreserveNewest"',
    )
    for project in (
        PROJECT_ROOT / "src" / "NpcManager.Cli" / "NpcManager.Cli.csproj",
        PROJECT_ROOT
        / "src"
        / "NpcManager.Desktop"
        / "NpcManager.Desktop.csproj",
    ):
        text = project.read_text(encoding="utf-8")
        require(
            all(marker in text for marker in packaging_markers),
            f"{project.name} does not package the shared renderer exactly",
        )


def test_cli_renderer_avoids_compounded_guid_staging() -> None:
    composition = (
        PROJECT_ROOT
        / "src"
        / "NpcManager.Cli"
        / "FaceGeomHairRegionsPreviewCliComposition.cs"
    )
    source = composition.read_text(encoding="utf-8")
    renderer_start = source.index(
        "var renderer = new BlenderFaceGeomHairRegionsRenderer("
    )
    renderer_end = source.index(
        "var documents =",
        renderer_start,
    )
    renderer_block = source[renderer_start:renderer_end]
    require(
        '".hrr"' in source
        and "Directory.CreateDirectory(rendererRoot.Value)" in source
        and
        re.search(
            r"\brendererRoot,\s*BlenderSha256",
            renderer_block,
        )
        is not None,
        "CLI renderer lacks a short policy-checked sibling root and "
        "can either overlap public output or exceed Blender MAX_PATH",
    )


def main() -> int:
    tests = [
        test_closed_request_and_block_id_names,
        test_structural_import_contract,
        test_exact_texture_binding_provenance,
        test_visible_runtime_authority_boundary_pixels,
        test_empty_combined_render_is_refused_before_labeling,
        test_shared_script_authority_pins_and_packaging,
        test_cli_renderer_avoids_compounded_guid_staging,
    ]
    for test in tests:
        test()
        print(f"PASS {test.__name__}")
    print(f"RESULT PASS {len(tests)}/{len(tests)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
