"""Create a K-local FO4-targeted hair fixture from a copied mesh without touching its source."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

import bpy


LAB_ROOT = Path(r"K:\ExampleWorkspace").resolve()


def parse_args() -> argparse.Namespace:
    values = sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else sys.argv[1:]
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--status", required=True, type=Path)
    return parser.parse_args(values)


def reset_scene() -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for datablock in (bpy.data.meshes, bpy.data.materials, bpy.data.cameras, bpy.data.lights):
        for item in list(datablock):
            if item.users == 0:
                datablock.remove(item)


def write_status(path: Path, payload: dict, success: bool) -> int:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    return 0 if success else 1


def ensure_partition_bones(armature: object) -> None:
    """Give the synthetic FO4 partition groups explicit skin bones.

    PyNifly serializes weighted vertex groups only when they resolve to bones.
    The copied Skyrim hair uses BSDismember groups instead, so merely renaming
    those groups would make them disappear on a FO4 round trip.  Adding two
    small, named bones keeps the fixture honest without changing the source NIF.
    """
    bpy.context.view_layer.objects.active = armature
    bpy.ops.object.select_all(action="DESELECT")
    armature.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    try:
        existing = {bone.name for bone in armature.data.edit_bones}
        for name, head, tail in (
            ("Hair Top", (0.0, 0.0, 0.0), (0.0, 0.0, 0.1)),
            ("Hair Long", (0.0, 0.0, 0.1), (0.0, 0.0, 0.2)),
        ):
            if name in existing:
                continue
            bone = armature.data.edit_bones.new(name)
            bone.head = head
            bone.tail = tail
    finally:
        bpy.ops.object.mode_set(mode="OBJECT")


def main() -> int:
    args = parse_args()
    source = args.source.resolve()
    output = args.output.resolve()
    status = args.status.resolve()
    try:
        if not source.is_file() or not source.is_relative_to(LAB_ROOT):
            raise ValueError("source must be an existing K-local NIF")
        if not output.is_relative_to(LAB_ROOT) or output.exists():
            raise ValueError("output must be a new K-local NIF")
        reset_scene()
        bpy.ops.preferences.addon_enable(module="io_scene_nifly")
        before = set(bpy.data.objects)
        result = bpy.ops.import_scene.pynifly(
            filepath=str(source),
            mesh_only=False,
            import_tris=False,
            import_collisions=False,
            import_animations=False,
            create_bones=True,
            create_collection=False,
        )
        if "CANCELLED" in result:
            raise ValueError("PyNifly cancelled source import")
        imported = [item for item in set(bpy.data.objects) - before if item.type == "MESH"]
        if not imported:
            raise ValueError("source import produced no mesh")
        armatures = [item for item in set(bpy.data.objects) - before if item.type == "ARMATURE"]
        if len(armatures) != 1:
            raise ValueError(f"source import must produce one armature, got {len(armatures)}")
        ensure_partition_bones(armatures[0])
        top_groups = 0
        long_groups = 0
        for mesh in imported:
            for group in list(mesh.vertex_groups):
                if group.name in {"SBP_31_HAIR", "SBP_131_HAIR"}:
                    indices = [
                        vertex.index
                        for vertex in mesh.data.vertices
                        if any(
                            assignment.group == group.index and assignment.weight > 0.5
                            for assignment in vertex.groups
                        )
                    ]
                    group.name = "Hair Top"
                    group.add(indices, 1.0, "REPLACE")
                    top_groups += 1
                elif group.name in {"SBP_41_LONGHAIR", "SBP_141_LONGHAIR"}:
                    indices = [
                        vertex.index
                        for vertex in mesh.data.vertices
                        if any(
                            assignment.group == group.index and assignment.weight > 0.5
                            for assignment in vertex.groups
                        )
                    ]
                    group.name = "Hair Long"
                    group.add(indices, 1.0, "REPLACE")
                    long_groups += 1
            mesh["NPCM_ASSET_PATH"] = str(output)
            mesh["PYN_GAME"] = "FO4"
            mesh.select_set(True)
        if top_groups == 0 or long_groups == 0:
            raise ValueError("source mesh did not contain both Skyrim hair partitions")
        output.parent.mkdir(parents=True, exist_ok=True)
        bpy.context.view_layer.objects.active = imported[0]
        result = bpy.ops.export_scene.pynifly(
            filepath=str(output),
            target_game="FO4",
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
        if "CANCELLED" in result or not output.is_file() or output.stat().st_size <= 32:
            raise ValueError(f"PyNifly FO4 fixture export failed: {sorted(result)}")
        return write_status(
            status,
            {
                "generated": True,
                "source": str(source),
                "sourceSha256": hashlib.sha256(source.read_bytes()).hexdigest(),
                "output": str(output),
                "outputSha256": hashlib.sha256(output.read_bytes()).hexdigest(),
                "targetGame": "FO4",
                "meshCount": len(imported),
                "topGroupCount": top_groups,
                "longGroupCount": long_groups,
                "syntheticContract": True,
            },
            True,
        )
    except Exception as error:  # Blender operators expose runtime-only exceptions.
        return write_status(status, {"generated": False, "error": str(error)}, False)


if __name__ == "__main__":
    raise SystemExit(main())
