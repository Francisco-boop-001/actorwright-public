"""Bounded Bethesda NIF geometry readback shared by the real writers.

The Blender mesh digest remains a local deformation check.  This module is the
writer-side byte boundary: it reopens the exact source/output NIF and derives
the same aggregate and vertex-count evidence used by the independent C# reader.
"""

from __future__ import annotations

import hashlib
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


def _descriptor_stride(descriptor: int, skyrim: bool = False,
                       separate_positions: bool = False) -> int:
    flags = (descriptor >> 44) & 0xFFF
    supported = 0x001 | 0x002 | 0x004 | 0x008 | 0x010 | 0x020 | 0x040 | 0x080 | 0x100 | 0x400
    _require(flags & ~supported == 0 and (separate_positions or flags & 0x001)
             and not (flags & 0x010 and not flags & 0x008),
             "unsupported Bethesda vertex descriptor attributes")
    stride = (descriptor & 0xF) * 4
    computed = (16 if skyrim or flags & 0x400 else 8) if flags & 0x001 else 0

    def offset(shift):
        return ((descriptor >> shift) & 0xF) * 4

    def attribute(flag, shift, size):
        nonlocal computed
        if flags & flag:
            _require(offset(shift) == computed and size <= stride - computed,
                     "Bethesda vertex attribute offset overlaps or escapes the vertex stride")
            computed += size

    for flag, shift, size in ((0x002, 8, 4), (0x004, 12, 4), (0x008, 16, 4),
                             (0x010, 20, 4), (0x020, 24, 4), (0x040, 28, 12)):
        attribute(flag, shift, size)
    if flags & 0x080:
        # LAND bytes stay opaque, bounded between known attributes.
        end = offset(36) if flags & 0x100 else stride
        _require(offset(32) == computed and computed < end <= stride,
                 "Bethesda LAND attribute interval overlaps or escapes the vertex stride")
        computed = end
    attribute(0x100, 36, 4)
    _require(stride > 0 and stride == computed,
             "Bethesda vertex descriptor stride is inconsistent")
    return stride


def _shape(data: bytes, reader: _Reader, block_index: int, block_type: str,
           name: str | None, stream_version: int) -> dict:
    descriptor = reader.u64()
    if stream_version == 100 and block_type == "BSDynamicTriShape":
        _descriptor_stride(descriptor, skyrim=True, separate_positions=True)
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
        stride = _descriptor_stride(descriptor, skyrim=stream_version == 100)
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
        if stream_version == 100 and block_type == "BSTriShape" and reader.end - reader.pos == 4:
            _require(reader.u32() == 0, "only empty SSE BSTriShape particle data is admitted")
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


def read_geometry(path: Path, target_game: str) -> dict:
    edition = {"SKYRIMSE": "skyrimse", "FO4": "fallout4"}.get(
        target_game.upper(), target_game.lower())
    return parse_nif(path, edition)
