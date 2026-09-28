#!/usr/bin/env python3
"""Generate owned, test-only packed-normal NIF fixtures.

The planar mesh exercises two packed vertex layouts and the 16-byte
no-normal control. It is synthetic parser input, not a Skyrim head or runtime
provider.
"""
import argparse
import hashlib
import json
import struct
from pathlib import Path

FIXTURE_DIR = Path(__file__).resolve().parent
VERTEX_COUNT = 3832
GRID_ROWS = 19
GRID_COLUMNS = 200
EXTRA_VERTICES = VERTEX_COUNT - GRID_ROWS * GRID_COLUMNS
TRIANGLE_COUNT = 18 * 199 * 2
SHAPE_NAME = "SyntheticPackedNormalShape"
TEXTURE_PATH = "textures/actorwright/tests/synthetic/neutral-placeholder.dds"
FEMALE_TEXTURE_PATH = r"textures\actors\character\female\FemaleHead.dds"
NORMALS = (
    bytes((0x7F, 0x7F, 0xFF, 0x00)),
    bytes((0x7F, 0xFF, 0x7F, 0x00)),
    bytes((0xFF, 0x7F, 0x7F, 0x00)),
)
TANGENTS = (
    bytes((0xFF, 0x7F, 0x7F, 0x00)),
    bytes((0x7F, 0x7F, 0xFF, 0x00)),
    bytes((0x7F, 0xFF, 0x7F, 0x00)),
)
LAYOUTS = (
    ("dynamic-16", 0x0051A03030210044, 16, True, True, FEMALE_TEXTURE_PATH),
    ("dynamic-24", 0x0045A00030210046, 24, True, True, TEXTURE_PATH),
    ("dynamic-16-no-normals", 0x0044200010000044, 16, False, False, TEXTURE_PATH),
)


def packed(fmt, *values):
    return struct.pack("<" + fmt, *values)


def text(value):
    encoded = value.encode("ascii")
    return packed("i", len(encoded)) + encoded


def rotation():
    return packed("9f", 1.0, 0.0, 0.0, 0.0, 1.0, 0.0, 0.0, 0.0, 1.0)


def transform():
    return rotation() + packed("4f", 0.0, 0.0, 0.0, 1.0)


def av(name_index):
    return (
        packed("iIiI", name_index, 0, -1, 0)
        + packed("3f", 0.0, 0.0, 0.0)
        + rotation()
        + packed("fi", 1.0, -1)
    )


def positions():
    result = [
        (column / (GRID_COLUMNS - 1), row / (GRID_ROWS - 1), 0.0)
        for row in range(GRID_ROWS)
        for column in range(GRID_COLUMNS)
    ]
    result.extend(
        (index / max(1, EXTRA_VERTICES - 1), 1.25, 0.0)
        for index in range(EXTRA_VERTICES)
    )
    return result


def triangles():
    result = []
    for row in range(18):
        for column in range(199):
            top_left = row * GRID_COLUMNS + column
            top_right = top_left + 1
            bottom_left = top_left + GRID_COLUMNS
            bottom_right = bottom_left + 1
            result.extend(
                (top_left, top_right, bottom_left,
                 top_right, bottom_right, bottom_left)
            )
    assert len(result) == TRIANGLE_COUNT * 3
    return result


def partition_rows(stride, has_normals, has_tangents):
    rows = bytearray()
    normal_bytes = bytearray()
    tangent_bytes = bytearray()
    for vertex in range(VERTEX_COUNT):
        if vertex < GRID_ROWS * GRID_COLUMNS:
            grid_row, column = divmod(vertex, GRID_COLUMNS)
            u = column / (GRID_COLUMNS - 1)
            v = grid_row / (GRID_ROWS - 1)
        else:
            u = (vertex - GRID_ROWS * GRID_COLUMNS) / max(1, EXTRA_VERTICES - 1)
            v = 1.0
        row_bytes = bytearray(struct.pack("<ee", u, v))
        if has_normals:
            normal = NORMALS[vertex % len(NORMALS)]
            row_bytes.extend(normal)
            normal_bytes.extend(normal)
        if has_tangents:
            tangent = TANGENTS[vertex % len(TANGENTS)]
            row_bytes.extend(tangent)
            tangent_bytes.extend(tangent)
        if stride == 16 and has_normals:
            # This opaque row attribute is not a structured string-table value.
            row_bytes.extend(b"Hl\x00\xff")
        else:
            row_bytes.extend(bytes(stride - len(row_bytes)))
        assert len(row_bytes) == stride
        rows.extend(row_bytes)
    return bytes(rows), bytes(normal_bytes), bytes(tangent_bytes)


def make_nif(descriptor, stride, has_normals, has_tangents, texture_path):
    vertex_positions = positions()
    face_indices = triangles()
    row_bytes, normal_bytes, tangent_bytes = partition_rows(
        stride, has_normals, has_tangents
    )

    root = bytearray(av(0))
    root.extend(packed("IiiiI", 3, 1, 2, 8, 0))
    head = av(1) + packed("II", 0, 0)

    shape = bytearray(av(2))
    shape.extend(bytes(16))
    shape.extend(packed("iii", 3, 6, -1))
    shape.extend(packed("QHHII", descriptor, 0, VERTEX_COUNT, 0, 0))
    shape.extend(packed("I", VERTEX_COUNT * 16))
    for x, y, z in vertex_positions:
        shape.extend(packed("4f", x, y, z, 0.0))

    skin_instance = packed("iiiIii", 4, 5, 0, 2, 1, 8)

    skin_data = bytearray(transform())
    skin_data.extend(packed("I", 2))
    skin_data.extend(b"\x01")
    for bone in range(2):
        skin_data.extend(transform())
        skin_data.extend(bytes(16))
        bone_vertices = range(bone, VERTEX_COUNT, 2)
        skin_data.extend(packed("H", len(bone_vertices)))
        for vertex in bone_vertices:
            skin_data.extend(packed("Hf", vertex, 1.0))

    partition = bytearray()
    partition.extend(packed("IIIQ", 1, len(row_bytes), stride, descriptor))
    partition.extend(row_bytes)
    partition.extend(packed("5H", VERTEX_COUNT, TRIANGLE_COUNT, 2, 0, 1))
    partition.extend(packed("2H", 0, 1))
    partition.extend(b"\x01")
    for vertex in range(VERTEX_COUNT):
        partition.extend(packed("H", vertex))
    partition.extend(b"\x01")
    partition.extend(packed(f"{VERTEX_COUNT}f", *([1.0] * VERTEX_COUNT)))
    partition.extend(b"\x01")
    for index in face_indices:
        partition.extend(packed("H", index))
    partition.extend(b"\x01")
    partition.extend(bytes(vertex % 2 for vertex in range(VERTEX_COUNT)))
    partition.extend(packed("HQ", 0, descriptor))
    for index in face_indices:
        partition.extend(packed("H", index))

    shader = (
        packed("IIi", 5, 0xFFFFFFFF, 0)
        + packed("i", -1)
        + bytes(24)
        + packed("i", 7)
        + bytes(60)
    )
    texture_set = packed("I", 9) + text(texture_path)
    texture_set += b"".join(text("") for _ in range(8))
    spine = av(3) + packed("II", 0, 0)

    blocks = [
        bytes(root),
        bytes(head),
        bytes(shape),
        skin_instance,
        bytes(skin_data),
        bytes(partition),
        shader,
        texture_set,
        bytes(spine),
    ]
    types = (
        "NiNode",
        "BSDynamicTriShape",
        "NiSkinInstance",
        "NiSkinData",
        "NiSkinPartition",
        "BSLightingShaderProperty",
        "BSShaderTextureSet",
    )
    block_types = (0, 0, 1, 2, 3, 4, 5, 6, 0)
    names = ("FixtureRoot", "NPC Head [Head]", SHAPE_NAME, "NPC Spine2 [Spn2]")

    document = bytearray(b"Gamebryo File Format, Version 20.2.0.7\n")
    document.extend(packed("IBIII", 0x14020007, 1, 12, len(blocks), 100))
    document.extend(b"\x00\x00\x00")
    document.extend(packed("H", len(types)))
    for value in types:
        document.extend(text(value))
    for index in block_types:
        document.extend(packed("H", index))
    for block in blocks:
        document.extend(packed("i", len(block)))
    document.extend(packed("II", len(names), max(len(name) for name in names)))
    for name in names:
        document.extend(text(name))
    document.extend(packed("I", 0))
    for block in blocks:
        document.extend(block)
    document.extend(packed("Ii", 1, 0))
    return bytes(document), normal_bytes, tangent_bytes


def manifest_entry(fixture_id, descriptor, stride, has_normals, has_tangents,
                   nif, normal_bytes, tangent_bytes):
    return {
        "id": fixture_id,
        "path": "tests/fixtures/sse-packed-normals/" + fixture_id + ".nif",
        "sourceSha256": hashlib.sha256(nif).hexdigest().upper(),
        "shapeName": SHAPE_NAME,
        "vertexDescription": f"{descriptor:016X}",
        "vertexSize": stride,
        "hasPackedNormals": has_normals,
        "hasTangents": has_tangents,
        "normalOffset": 4 if has_normals else -1,
        "tangentOffset": 8 if has_tangents else -1,
        "vertexCount": VERTEX_COUNT,
        "triangleCount": TRIANGLE_COUNT,
        "packedNormalSha256": hashlib.sha256(normal_bytes).hexdigest().upper(),
        "packedTangentSha256": hashlib.sha256(tangent_bytes).hexdigest().upper(),
        "firstEightBytes": normal_bytes[:8].hex().upper(),
        "lastEightBytes": normal_bytes[-8:].hex().upper(),
        "firstTangentBytes": tangent_bytes[:8].hex().upper(),
        "lastTangentBytes": tangent_bytes[-8:].hex().upper(),
    }


def generated_files():
    entries = []
    outputs = {}
    for (fixture_id, descriptor, stride, has_normals, has_tangents,
         texture_path) in LAYOUTS:
        nif, normal_bytes, tangent_bytes = make_nif(
            descriptor, stride, has_normals, has_tangents, texture_path
        )
        outputs[fixture_id + ".nif"] = nif
        entries.append(manifest_entry(
            fixture_id, descriptor, stride, has_normals, has_tangents,
            nif, normal_bytes, tangent_bytes
        ))
    generator_hash = hashlib.sha256(Path(__file__).read_bytes()).hexdigest().upper()
    manifest = {
        "schemaVersion": 2,
        "fixtureAuthority": "synthetic-test-input-only",
        "runtimeAuthority": False,
        "geometry": {
            "vertexCount": VERTEX_COUNT,
            "triangleCount": TRIANGLE_COUNT,
            "description": (
                "A planar synthetic grid with deterministic positions, UVs, "
                "topology, and two-bone skin weights; it is not head geometry."
            ),
        },
        "fixtures": entries,
        "provenance": {
            "kind": "owned-synthetic-fixture",
            "generator": "tests/fixtures/sse-packed-normals/generate.py",
            "generatorSha256": generator_hash,
            "source": "Deterministic numeric inputs only; no game or third-party NIF bytes are used.",
            "runtimeAuthority": False,
        },
    }
    outputs["manifest.json"] = (
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n"
    ).encode("utf-8")
    return outputs


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--check",
        action="store_true",
        help="fail unless checked-in fixtures exactly match deterministic output",
    )
    args = parser.parse_args()
    expected = generated_files()
    mismatches = []
    for name, content in expected.items():
        path = FIXTURE_DIR / name
        if args.check:
            if not path.is_file() or path.read_bytes() != content:
                mismatches.append(name)
        else:
            path.write_bytes(content)
    if mismatches:
        print("Fixture generation mismatch: " + ", ".join(mismatches))
        return 1
    if args.check:
        print("PASS synthetic packed-normal fixtures reproduce byte-for-byte")
    else:
        print("Wrote synthetic packed-normal fixtures: " + ", ".join(expected))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())