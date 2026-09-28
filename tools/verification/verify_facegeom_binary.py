"""Independent verifier for the hash-bound TRI-to-NIF FaceGeom sandbox route."""

from __future__ import annotations

import argparse
import hashlib
import json
import struct
from pathlib import Path

def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


class _Reader:
    def __init__(self, data: bytes, start: int = 0, end: int | None = None):
        self.data = data
        self.pos = start
        self.end = len(data) if end is None else end
        _require(0 <= start <= self.end <= len(data), "invalid NIF reader range")

    def take(self, size: int) -> bytes:
        _require(size >= 0 and self.pos + size <= self.end,
                 "NIF table exceeds file bounds")
        value = self.data[self.pos:self.pos + size]
        self.pos += size
        return value

    def u8(self) -> int:
        return self.take(1)[0]

    def u16(self) -> int:
        return struct.unpack("<H", self.take(2))[0]

    def u32(self) -> int:
        return struct.unpack("<I", self.take(4))[0]

    def i32(self) -> int:
        return struct.unpack("<i", self.take(4))[0]

    def u64(self) -> int:
        return struct.unpack("<Q", self.take(8))[0]

    def sized_string(self) -> str:
        length = self.u32()
        _require(length <= 1024 * 1024, "NIF string length exceeds bound")
        return self.take(length).decode("latin-1")

    def byte_string(self) -> bytes:
        return self.take(self.u8())


_SKYRIM_SHAPES = {"BSTriShape", "BSSubIndexTriShape", "BSDynamicTriShape"}
_FALLOUT4_SHAPES = {"BSTriShape", "BSSubIndexTriShape"}
_NODE_TYPES = {"NiNode", "BSFadeNode"}


def _reference(reader: _Reader, block_count: int, role: str) -> int:
    target = reader.i32()
    _require(-1 <= target < block_count,
             f"{role} reference is outside the NIF block table")
    return target


def _av_object(reader: _Reader, strings: list[str], block_count: int) -> tuple[str | None, list[int]]:
    name_index = reader.u32()
    if name_index == 0xFFFFFFFF:
        name = None
    else:
        _require(name_index < len(strings), "NIF name index is outside the string table")
        name = strings[name_index]
    extra_count = reader.u32()
    _require(extra_count <= block_count, "NIF extra-reference count exceeds the block bound")
    references = [_reference(reader, block_count, "extra") for _ in range(extra_count)]
    references.append(_reference(reader, block_count, "controller"))
    reader.take(56)
    references.append(_reference(reader, block_count, "collision"))
    return name, references


def _descriptor_stride(descriptor: int) -> int:
    flags = (descriptor >> 44) & 0xFFF
    supported = 0x001 | 0x002 | 0x008 | 0x010 | 0x020 | 0x040 | 0x100 | 0x400
    _require(flags & ~supported == 0 and flags & 0x001,
             "unsupported Bethesda vertex descriptor attributes")
    stride = (descriptor & 0xF) * 4
    computed = 16 if flags & 0x400 else 8
    if flags & 0x002:
        computed += 4
    if flags & 0x008:
        computed += 4
        if flags & 0x010:
            computed += 4
    if flags & 0x020:
        computed += 4
    if flags & 0x040:
        computed += 12
    if flags & 0x100:
        computed += 4
    _require(stride > 0 and stride == computed,
             "Bethesda vertex descriptor stride is inconsistent")
    return stride


def _shape(data: bytes, reader: _Reader, block_index: int, block_type: str,
           name: str | None, stream_version: int) -> dict:
    descriptor = reader.u64()
    if stream_version == 100 and block_type == "BSDynamicTriShape":
        triangle_count = reader.u16()
        vertex_count = reader.u16()
        data_size = reader.u32()
        dynamic_prefix = reader.u32()
        dynamic_size = reader.u32()
        _require(triangle_count == 0 and data_size == 0 and dynamic_prefix == 0,
                 "BSDynamicTriShape is not the admitted packed layout")
        _require(0 < vertex_count <= 1_000_000 and
                 dynamic_size == vertex_count * 16,
                 "BSDynamicTriShape vertex payload is outside the admitted bounds")
        payload_offset = reader.pos
        payload_length = dynamic_size
        payload = reader.take(payload_length)
    else:
        triangle_count = reader.u16() if stream_version == 100 else reader.u32()
        vertex_count = reader.u16()
        data_size = reader.u32()
        stride = _descriptor_stride(descriptor)
        _require(0 < vertex_count <= 1_000_000 and
                 0 < triangle_count <= 2_000_000,
                 "triangle-shape counts are outside the admitted bounds")
        payload_length = vertex_count * stride
        triangle_length = triangle_count * 6
        _require(data_size == payload_length + triangle_length,
                 "triangle-shape data size is not contiguous vertex plus topology data")
        payload_offset = reader.pos
        payload = reader.take(payload_length)
        reader.take(triangle_length)
    _require(reader.pos == reader.end, "triangle shape has trailing block data")
    _require(name is not None and name.strip(),
             "recognized triangle shape has no name")
    return {
        "name": name,
        "blockType": block_type,
        "blockIndex": block_index,
        "vertexCount": vertex_count,
        "vertexDescriptor": descriptor,
        "vertexPayloadOffset": payload_offset,
        "vertexPayloadLength": payload_length,
        "vertexPayloadSha256": hashlib.sha256(payload).hexdigest(),
        "vertexPayload": payload,
    }


def _parse_block(data: bytes, index: int, block_type: str, offset: int,
                 size: int, block_count: int, strings: list[str],
                 stream_version: int) -> tuple[list[int], dict | None]:
    reader = _Reader(data, offset, offset + size)
    if block_type in _NODE_TYPES:
        _, references = _av_object(reader, strings, block_count)
        child_count = reader.u32()
        _require(child_count <= block_count,
                 "NIF child-reference count exceeds the block bound")
        references.extend(_reference(reader, block_count, "child")
                          for _ in range(child_count))
        effect_count = reader.u32()
        _require(effect_count <= block_count,
                 "NIF effect-reference count exceeds the block bound")
        references.extend(_reference(reader, block_count, "effect")
                          for _ in range(effect_count))
        _require(reader.pos == reader.end, "NIF node block has trailing data")
        return references, None

    admitted = _SKYRIM_SHAPES if stream_version == 100 else _FALLOUT4_SHAPES
    if block_type not in admitted:
        if stream_version == 130:
            _require(False,
                     f"Fallout 4 block type '{block_type}' is outside the admitted readback layouts")
        return [], None
    name, references = _av_object(reader, strings, block_count)
    reader.take(16)
    references.append(_reference(reader, block_count, "skin"))
    references.append(_reference(reader, block_count, "shader"))
    references.append(_reference(reader, block_count, "alpha"))
    return references, _shape(data, reader, index, block_type, name, stream_version)


def _aggregate(shapes: list[dict]) -> str:
    digest = hashlib.sha256()
    for shape in sorted(shapes, key=lambda item: item["blockIndex"]):
        for value in (shape["name"], shape["blockType"]):
            encoded = value.encode("utf-8")
            digest.update(struct.pack("<I", len(encoded)))
            digest.update(encoded)
        digest.update(struct.pack("<i", shape["blockIndex"]))
        digest.update(struct.pack("<i", shape["vertexCount"]))
        digest.update(struct.pack("<Q", shape["vertexDescriptor"]))
        digest.update(struct.pack("<i", shape["vertexPayloadLength"]))
        digest.update(shape["vertexPayload"])
    return digest.hexdigest()


def parse_nif(path: Path, edition: str | None = None) -> dict:
    data = path.read_bytes()
    _require(len(data) > 64, "NIF is unexpectedly small")
    newline = data.find(b"\n", 0, min(len(data), 256))
    _require(newline > 0, "NIF header is missing")
    reader = _Reader(data)
    header = reader.take(newline + 1).decode("ascii", errors="replace").rstrip("\r\n")
    _require(header == "Gamebryo File Format, Version 20.2.0.7",
             "unexpected NIF header")
    _require(reader.u32() == 0x14020007, "unexpected NIF version")
    _require(reader.u8() == 1, "unexpected NIF endian marker")
    user_version = reader.u32()
    block_count = reader.u32()
    stream_version = reader.u32()
    _require(0 < block_count <= 10_000 and stream_version in (100, 130),
             "invalid NIF metadata")
    if edition is not None:
        expected = 100 if edition == "skyrimse" else 130 if edition == "fallout4" else None
        _require(expected is not None and stream_version == expected,
                 "NIF stream does not match the requested edition")
    for _ in range(3 if stream_version == 100 else 4):
        reader.byte_string()
    type_count = reader.u16()
    _require(0 < type_count <= 1024, "invalid NIF type count")
    types = [reader.sized_string() for _ in range(type_count)]
    indices = [reader.u16() for _ in range(block_count)]
    _require(all(index < len(types) for index in indices),
             "NIF block type index is out of range")
    sizes = [reader.u32() for _ in range(block_count)]
    string_count = reader.u32()
    _require(string_count <= 100_000, "invalid NIF string count")
    reader.u32()
    strings = [reader.sized_string() for _ in range(string_count)]
    group_count = reader.u32()
    _require(group_count <= block_count, "invalid NIF group count")
    reader.take(group_count * 4)
    offsets: list[int] = []
    for size in sizes:
        offsets.append(reader.pos)
        reader.take(size)
    block_end = reader.pos
    root_count = reader.u32()
    _require(0 < root_count <= block_count, "invalid NIF root count")
    roots = [reader.i32() for _ in range(root_count)]
    _require(all(0 <= root < block_count for root in roots),
             "NIF root index is out of range")
    _require(reader.pos == len(data), "NIF has trailing data")

    references: list[list[int]] = [[] for _ in range(block_count)]
    shapes: list[dict] = []
    for index, type_index in enumerate(indices):
        refs, shape = _parse_block(data, index, types[type_index], offsets[index],
                                   sizes[index], block_count, strings, stream_version)
        references[index] = refs
        if shape is not None:
            shapes.append(shape)
    reachable = set(roots)
    pending = list(roots)
    while pending:
        index = pending.pop()
        for target in references[index]:
            if target >= 0 and target not in reachable:
                reachable.add(target)
                pending.append(target)
    _require(shapes, "NIF contains no admitted triangle shape")
    _require(all(shape["blockIndex"] in reachable for shape in shapes),
             "NIF contains an unreachable admitted triangle shape")
    names = [shape["name"] for shape in shapes]
    _require(len(set(names)) == len(names), "NIF triangle shape names are not unique")
    ordered_by_offset = sorted(shapes, key=lambda item: item["vertexPayloadOffset"])
    for previous, current in zip(ordered_by_offset, ordered_by_offset[1:]):
        previous_end = previous["vertexPayloadOffset"] + previous["vertexPayloadLength"]
        _require(previous_end <= current["vertexPayloadOffset"],
                 "NIF admitted vertex payloads overlap")
    aggregate = _aggregate(shapes)
    return {
        "data": data,
        "bytes": len(data),
        "blocks": block_count,
        "blockCount": block_count,
        "shapes": len(shapes),
        "shapeCount": len(shapes),
        "geometryShapes": shapes,
        "vertexCount": sum(shape["vertexCount"] for shape in shapes),
        "geometrySha256": aggregate,
        "aggregateGeometrySha256": aggregate,
        "blockEnd": block_end,
        "userVersion": user_version,
        "userVersion2": stream_version,
    }




def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def load(path: Path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def response_hash(response: dict, field: str) -> str:
    value = response.get(field)
    require(isinstance(value, str) and len(value) == 64,
            f"{field} is missing or malformed")
    return value.casefold()


def carrier_path(args, asset_root: Path) -> tuple[Path, bytes]:
    require(args.carrier is not None,
            "transport requires --carrier bytes")
    carrier = args.carrier.resolve()
    require(carrier.is_relative_to(asset_root) and carrier.is_file(),
            "carrier escapes asset root or is missing")
    return carrier, carrier.read_bytes()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--nif", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--asset-root", required=True, type=Path)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--carrier", type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()

    response = load(args.response)
    output_path = args.nif.resolve()
    output_bytes = args.nif.read_bytes()
    nif = parse_nif(args.nif, args.edition)
    source = args.source.resolve()
    asset_root = args.asset_root.resolve()
    require(source.is_relative_to(asset_root), "source escapes asset root")
    source_bytes = source.read_bytes()
    source_hash = hashlib.sha256(source_bytes).hexdigest()
    nif_hash = hashlib.sha256(output_bytes).hexdigest()
    require(response["written"] is True, "FaceGeom response is not successful")
    require(response["artifactKind"] == "facegeom-binary-sandbox-build",
            "unexpected artifact kind")
    require(response["edition"] == args.edition, "edition is not bound")
    require(Path(response["outputPath"]).resolve() == output_path,
            "output path is not bound")
    require(response["outputSha256"].casefold() == nif_hash,
            "output hash is not bound")
    require(response["byteLength"] == nif["bytes"],
            "output size is not bound")
    require(response["vertexCount"] > 0,
            "vertex count is missing")
    require(response["inputSourceSha256"].casefold() == source_hash,
            "source hash is not bound")
    require(response["runtimeAuthority"] is False,
            "sandbox output was promoted to runtime authority")

    import_mode = response.get("importMode")
    output_geometry_hash = nif["geometrySha256"]
    if import_mode == "nif-plus-tri-bake":
        source_nif = parse_nif(source, args.edition)
        require(response_hash(response, "baseVertexSha256") ==
                source_nif["geometrySha256"].casefold(),
                "base geometry hash is not bound")
        require(response["vertexCount"] == nif["vertexCount"],
                "vertex count is not bound")
        require(response_hash(response, "bakedVertexSha256") ==
                output_geometry_hash.casefold(),
                "output geometry hash is not bound")
        require(output_geometry_hash.casefold() !=
                source_nif["geometrySha256"].casefold(),
                "morph bake did not change independently parsed geometry")
        require(len(response.get("morphs", [])) > 0 and
                any(item["value"] != 0 for item in response["morphs"]),
                "non-zero morph evidence is missing")
        tri_rows = response.get("triFiles", [])
        require(len(tri_rows) > 0, "TRI dependencies are missing")
        for row in tri_rows:
            tri = (asset_root / row["path"]).resolve()
            require(tri.is_relative_to(asset_root) and tri.is_file(),
                    "TRI dependency escapes asset root")
            require(hashlib.sha256(tri.read_bytes()).hexdigest().casefold() ==
                    row["sha256"].casefold(),
                    "TRI dependency hash is not bound")
    elif import_mode == "nif-transport-complete-carrier":
        source_nif = parse_nif(source, args.edition)
        base_hash = response_hash(response, "baseVertexSha256")
        baked_hash = response_hash(response, "bakedVertexSha256")
        require(base_hash == baked_hash,
                "complete-carrier base and baked hashes must match")
        require(base_hash == source_nif["geometrySha256"].casefold() ==
                output_geometry_hash.casefold(),
                "complete-carrier aggregate geometry hash is not bound")
        require(response["vertexCount"] == nif["vertexCount"] ==
                source_nif["vertexCount"],
                "complete-carrier vertex count is not bound")
        require(source_bytes == output_bytes,
                "complete-carrier output is not the exact authorized carrier")
    elif import_mode == "nif-transport-vertex-array":
        carrier, carrier_bytes = carrier_path(args, asset_root)
        carrier_nif = parse_nif(carrier, args.edition)
        source_nif = parse_nif(source, args.edition)
        base_hash = response_hash(response, "baseVertexSha256")
        baked_hash = response_hash(response, "bakedVertexSha256")
        carrier_hash = response_hash(response, "carrierVertexSha256")
        require(base_hash == baked_hash,
                "vertex-array transport base and baked hashes must match")
        source_matches = [
            shape for shape in source_nif["geometryShapes"]
            if shape["vertexPayloadSha256"].casefold() == base_hash and
            shape["vertexCount"] == response["vertexCount"]
        ]
        output_matches = [
            shape for shape in nif["geometryShapes"]
            if shape["vertexPayloadSha256"].casefold() == baked_hash and
            shape["vertexCount"] == response["vertexCount"]
        ]
        carrier_matches = [
            shape for shape in carrier_nif["geometryShapes"]
            if shape["vertexPayloadSha256"].casefold() == carrier_hash and
            shape["vertexCount"] == response["vertexCount"]
        ]
        require(len(source_matches) == 1 and len(output_matches) == 1,
                "vertex-array source/output payload and count are not unique")
        require(len(carrier_matches) == 1,
                "vertex-array carrierVertexSha256 is not uniquely bound")
        output_shape = output_matches[0]
        carrier_shape = carrier_matches[0]
        require(output_shape["vertexPayloadOffset"] ==
                carrier_shape["vertexPayloadOffset"] and
                output_shape["vertexPayloadLength"] ==
                carrier_shape["vertexPayloadLength"] and
                len(output_bytes) == len(carrier_bytes),
                "vertex-array output/carrier payload ranges are not bound")
        offset = carrier_shape["vertexPayloadOffset"]
        length = carrier_shape["vertexPayloadLength"]
        require(output_bytes[:offset] == carrier_bytes[:offset] and
                output_bytes[offset + length:] == carrier_bytes[offset + length:],
                "vertex-array output changed bytes outside the authorized carrier payload")
    else:
        require(False, "unexpected import mode")

    require(all(item["severity"] != "error"
                for item in response.get("diagnostics", [])),
            "FaceGeom response contains an error diagnostic")
    print(
        f"FACEGEOM BINARY INDEPENDENT PASS nif={args.nif} "
        f"blocks={nif['blocks']} shapes={nif['shapes']} verts={nif['vertexCount']}"
    )


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        raise SystemExit(f"FACEGEOM BINARY INDEPENDENT FAIL: {error}")
