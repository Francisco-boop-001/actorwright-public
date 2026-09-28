"""Copied-asset hair partition masking for the preview adapters.

The reference application zaps only the FO4 Hair Top (30) and Hair Long (31)
partitions when headwear is rendered. Skyrim uses its BSDismember hair groups
instead. This module deliberately operates on imported Blender meshes only;
it never edits source NIF files or claims game/runtime loadability.
"""

from __future__ import annotations

import bmesh
import bpy


FO4_GROUPS = {
    "top": ("Hair Top", "SBP_30_HAIR"),
    "long": ("Hair Long", "SBP_31_LONGHAIR"),
}
SKYRIM_GROUPS = {
    "top": ("SBP_31_HAIR", "SBP_131_HAIR"),
    "long": ("SBP_41_LONGHAIR", "SBP_141_LONGHAIR"),
}
MIN_PARTITION_WEIGHT = 0.5


def _vertex_indices(obj: object, group_names: tuple[str, ...]) -> set[int]:
    group_indices = {
        group.index
        for name in group_names
        if (group := obj.vertex_groups.get(name)) is not None
    }
    if not group_indices:
        return set()
    indices: set[int] = set()
    for vertex in obj.data.vertices:
        if any(
            assignment.group in group_indices and assignment.weight >= MIN_PARTITION_WEIGHT
            for assignment in vertex.groups
        ):
            indices.add(vertex.index)
    return indices


def _remove_partition_faces(obj: object, target_vertices: set[int]) -> int:
    if not target_vertices:
        return 0
    mesh = obj.data
    bm = bmesh.new()
    try:
        bm.from_mesh(mesh)
        bm.verts.index_update()
        victims = [
            face
            for face in bm.faces
            if face.verts and all(vertex.index in target_vertices for vertex in face.verts)
        ]
        if victims:
            bmesh.ops.delete(bm, geom=victims, context="FACES")
            bm.to_mesh(mesh)
            mesh.update()
        return len(victims)
    finally:
        bm.free()


def apply_hair_zap(meshes: list[object], edition: str, request: dict | None,
                   hair_assets: list[str]) -> dict:
    """Apply the requested partition mask and return independently checkable status."""
    if request is None:
        return {
            "applied": False,
            "top": False,
            "long": False,
            "affectedMeshCount": 0,
            "removedFaceCount": 0,
        }
    if not isinstance(request, dict):
        raise ValueError("hairZap must be an object or null")
    render_headwear = request.get("renderHeadwear")
    top = request.get("top")
    long = request.get("long")
    if not isinstance(render_headwear, bool) or not isinstance(top, bool) or not isinstance(long, bool):
        raise ValueError("hairZap requires boolean renderHeadwear, top, and long fields")
    if not render_headwear:
        top = long = False
    hair_paths = {str(path).casefold() for path in hair_assets}
    groups = FO4_GROUPS if edition == "fallout4" else SKYRIM_GROUPS
    affected_meshes = 0
    removed_faces = 0
    requested_parts = set()
    if top:
        requested_parts.add("top")
    if long:
        requested_parts.add("long")
    recognized_parts: set[str] = set()
    for obj in meshes:
        source = str(obj.get("NPCM_ASSET_PATH", "")).casefold()
        if source not in hair_paths or not requested_parts:
            continue
        target_vertices: set[int] = set()
        for part in requested_parts:
            if any(obj.vertex_groups.get(name) is not None for name in groups[part]):
                recognized_parts.add(part)
            target_vertices.update(_vertex_indices(obj, groups[part]))
        removed = _remove_partition_faces(obj, target_vertices)
        if removed:
            affected_meshes += 1
            removed_faces += removed
    missing_parts = requested_parts - recognized_parts
    if missing_parts:
        missing = ", ".join(sorted(missing_parts))
        raise ValueError(f"hair asset has no {edition} partition group for requested part(s): {missing}")
    return {
        "applied": removed_faces > 0,
        "top": top,
        "long": long,
        "affectedMeshCount": affected_meshes,
        "removedFaceCount": removed_faces,
    }


def apply_face_cull(meshes: list[object], request: dict | None,
                    face_assets: list[str], remove: bool = False) -> dict:
    """Cull copied FaceGen head meshes for FO4 headwear slot 32.

    The upstream rule hides the head partition when headwear covers slot 32.
    Render requests hide those objects; export requests remove them from the
    selected export set so the resulting NIF carries the same cull. Source NIFs
    are never modified.
    """
    face_cull = False if request is None else request.get("faceCull")
    if not isinstance(face_cull, bool):
        raise ValueError("hairZap requires boolean faceCull field")
    if not face_cull:
        return {"applied": False, "affectedMeshCount": 0}
    face_paths = {str(path).casefold() for path in face_assets}
    candidates = [
        obj for obj in meshes
        if str(obj.get("NPCM_ASSET_PATH", "")).casefold() in face_paths
    ]
    if not candidates:
        raise ValueError("FaceGen head cull requested but no face asset was supplied")
    for obj in candidates:
        if remove:
            bpy.data.objects.remove(obj, do_unlink=True)
        else:
            obj.hide_render = True
            obj.hide_viewport = True
    if remove:
        meshes[:] = [obj for obj in meshes if obj not in candidates]
        if not meshes:
            raise ValueError("FaceGen head cull removed every imported mesh")
    return {"applied": True, "affectedMeshCount": len(candidates)}
