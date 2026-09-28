#!/usr/bin/env python3
"""Independent raw-byte verifier for FaceGeom HairTint transactions.

This release-evidence tool deliberately has no imports from NPC Manager's
writer, C# NIF parser, or renderer.  It parses the bounded Skyrim SE NIF
header and the relevant block graph directly from bytes, derives HairTint
ownership independently, accounts for every changed byte, and emits a
deterministic JSON verdict.  It never edits a NIF.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import math
import os
import re
import struct
import sys
from ctypes import wintypes
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, NoReturn, Sequence


SCHEMA = "npcmanager-facegeom-hair-regions-raw-nif-oracle/1"
ANALYSIS_SCHEMA = "npcmanager-facegeom-hair-regions-analysis/1"
REQUEST_SCHEMA = "npcmanager-facegeom-hair-regions-request/1"
PROPOSAL_SCHEMA = "npcmanager-facegeom-hair-regions-proposal/1"
MANIFEST_SCHEMA = "npcmanager-facegeom-hair-regions-manifest/1"
NIF_HEADER = b"Gamebryo File Format, Version 20.2.0.7"
NIF_VERSION = 0x14020007
BETHESDA_STREAM_VERSION = 100
MAXIMUM_FILE_BYTES = 128 * 1024 * 1024
MAXIMUM_REGIONS = 64
MAXIMUM_COUNT = 100_000
MAXIMUM_JSON_DEPTH = 32
MAXIMUM_JSON_NODES = 100_000
FILE_ATTRIBUTE_REPARSE_POINT = 0x0400
FILE_ATTRIBUTE_DIRECTORY = 0x0010
FILE_ATTRIBUTE_NORMAL = 0x0080
GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_READ_ATTRIBUTES = 0x0080
FILE_SHARE_READ = 0x00000001
FILE_SHARE_WRITE = 0x00000002
FILE_SHARE_DELETE = 0x00000004
OPEN_EXISTING = 3
CREATE_NEW = 1
FILE_FLAG_SEQUENTIAL_SCAN = 0x08000000
FILE_FLAG_BACKUP_SEMANTICS = 0x02000000
FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000
FILE_ATTRIBUTE_TAG_INFO_CLASS = 9
FILE_ID_INFO_CLASS = 18
INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
WORKSPACE_ROOT = Path(os.environ.get(
    "ACTORWRIGHT_WORKSPACE_ROOT",
    str(Path(__file__).resolve().parents[2]),
))
SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
COLOR_RE = re.compile(r"^#[0-9A-F]{6}$")
STRUCTURAL_ID_RE = re.compile(r"^shape:([0-9]+):shader:([0-9]+)$")
SHADER_GROUP_RE = re.compile(r"^shader:([0-9]+)$")
ROLES = frozenset(("preserve", "primary", "accent"))
SHAPE_TYPES = frozenset(
    ("BSTriShape", "BSDynamicTriShape", "BSSubIndexTriShape")
)
NODE_TYPES = frozenset(("NiNode", "BSFadeNode"))
SKIN_INSTANCE_TYPES = frozenset(("NiSkinInstance", "BSDismemberSkinInstance"))
SKIN_BLOCK_TYPES = frozenset(
    (
        "NiSkinInstance",
        "BSDismemberSkinInstance",
        "NiSkinData",
        "NiSkinPartition",
    )
)
LEAF_TYPES = frozenset(("NiSkinData", "NiSkinPartition"))


class FileAttributeTagInfo(ctypes.Structure):
    _fields_ = [
        ("FileAttributes", wintypes.DWORD),
        ("ReparseTag", wintypes.DWORD),
    ]


class FileId128(ctypes.Structure):
    _fields_ = [("Identifier", ctypes.c_ubyte * 16)]


class FileIdInfo(ctypes.Structure):
    _fields_ = [
        ("VolumeSerialNumber", ctypes.c_ulonglong),
        ("FileId", FileId128),
    ]


class ByHandleFileInformation(ctypes.Structure):
    _fields_ = [
        ("FileAttributes", wintypes.DWORD),
        ("CreationTime", wintypes.FILETIME),
        ("LastAccessTime", wintypes.FILETIME),
        ("LastWriteTime", wintypes.FILETIME),
        ("VolumeSerialNumber", wintypes.DWORD),
        ("FileSizeHigh", wintypes.DWORD),
        ("FileSizeLow", wintypes.DWORD),
        ("NumberOfLinks", wintypes.DWORD),
        ("FileIndexHigh", wintypes.DWORD),
        ("FileIndexLow", wintypes.DWORD),
    ]


_KERNEL32 = ctypes.WinDLL("kernel32", use_last_error=True)
_CREATE_FILE = _KERNEL32.CreateFileW
_CREATE_FILE.argtypes = (
    wintypes.LPCWSTR,
    wintypes.DWORD,
    wintypes.DWORD,
    wintypes.LPVOID,
    wintypes.DWORD,
    wintypes.DWORD,
    wintypes.HANDLE,
)
_CREATE_FILE.restype = wintypes.HANDLE
_CLOSE_HANDLE = _KERNEL32.CloseHandle
_CLOSE_HANDLE.argtypes = (wintypes.HANDLE,)
_CLOSE_HANDLE.restype = wintypes.BOOL
_GET_FILE_INFORMATION_EX = _KERNEL32.GetFileInformationByHandleEx
_GET_FILE_INFORMATION_EX.argtypes = (
    wintypes.HANDLE,
    ctypes.c_int,
    wintypes.LPVOID,
    wintypes.DWORD,
)
_GET_FILE_INFORMATION_EX.restype = wintypes.BOOL
_GET_FILE_SIZE_EX = _KERNEL32.GetFileSizeEx
_GET_FILE_SIZE_EX.argtypes = (
    wintypes.HANDLE,
    ctypes.POINTER(ctypes.c_longlong),
)
_GET_FILE_SIZE_EX.restype = wintypes.BOOL
_GET_FILE_INFORMATION = _KERNEL32.GetFileInformationByHandle
_GET_FILE_INFORMATION.argtypes = (
    wintypes.HANDLE,
    ctypes.POINTER(ByHandleFileInformation),
)
_GET_FILE_INFORMATION.restype = wintypes.BOOL
_GET_FINAL_PATH = _KERNEL32.GetFinalPathNameByHandleW
_GET_FINAL_PATH.argtypes = (
    wintypes.HANDLE,
    wintypes.LPWSTR,
    wintypes.DWORD,
    wintypes.DWORD,
)
_GET_FINAL_PATH.restype = wintypes.DWORD
_READ_FILE = _KERNEL32.ReadFile
_READ_FILE.argtypes = (
    wintypes.HANDLE,
    wintypes.LPVOID,
    wintypes.DWORD,
    ctypes.POINTER(wintypes.DWORD),
    wintypes.LPVOID,
)
_READ_FILE.restype = wintypes.BOOL
_WRITE_FILE = _KERNEL32.WriteFile
_WRITE_FILE.argtypes = (
    wintypes.HANDLE,
    wintypes.LPCVOID,
    wintypes.DWORD,
    ctypes.POINTER(wintypes.DWORD),
    wintypes.LPVOID,
)
_WRITE_FILE.restype = wintypes.BOOL
_FLUSH_FILE_BUFFERS = _KERNEL32.FlushFileBuffers
_FLUSH_FILE_BUFFERS.argtypes = (wintypes.HANDLE,)
_FLUSH_FILE_BUFFERS.restype = wintypes.BOOL


class OracleError(Exception):
    """A closed, user-actionable oracle refusal."""

    def __init__(self, code: str, message: str) -> None:
        super().__init__(message)
        self.code = code
        self.message = message


class DuplicatePropertyError(ValueError):
    pass


@dataclass(frozen=True)
class Diagnostic:
    code: str
    severity: str
    message: str

    def to_json(self) -> dict[str, str]:
        return {
            "code": self.code,
            "severity": self.severity,
            "message": self.message,
        }


@dataclass(frozen=True)
class FileIdentity:
    volume_serial_number: int
    file_id: bytes


@dataclass(frozen=True)
class PinnedFileRead:
    requested_path: Path
    final_path: Path
    identity: FileIdentity
    data: bytes
    handle: int


@dataclass(frozen=True)
class NifReference:
    kind: str
    byte_offset: int
    target: int
    slot: int | None = None


@dataclass(frozen=True)
class NifBlock:
    block_id: int
    block_type: str
    offset: int
    size: int

    @property
    def end(self) -> int:
        return self.offset + self.size


@dataclass(frozen=True)
class ParsedBlock:
    block: NifBlock
    name: str | None
    references: tuple[NifReference, ...]


@dataclass(frozen=True)
class ShapeBlock:
    block: NifBlock
    name: str
    skin_block_id: int
    shader_block_id: int
    shader_reference_byte_offset: int
    alpha_block_id: int
    geometry_offset: int


@dataclass(frozen=True)
class ShaderBlock:
    block: NifBlock
    shader_type: int
    flags1: int
    flags2: int
    texture_set_block_id: int
    tint_byte_offset: int | None
    tint_bits: tuple[int, int, int] | None


@dataclass(frozen=True)
class TextureSetBlock:
    block: NifBlock
    routes: tuple[str, ...]


@dataclass(frozen=True)
class HairRegion:
    structural_id: str
    name: str
    duplicate_name_ordinal: int
    shared_shader_group_id: str
    shape_block_id: int
    shape_block_type: str
    shape_shader_reference_byte_offset: int
    shader_block_id: int
    tint_byte_offset: int
    tint_bits: tuple[int, int, int]
    texture_set_block_id: int
    texture_routes: tuple[str, ...]
    shader_owner_structural_ids: tuple[str, ...]


@dataclass(frozen=True)
class NifDocument:
    data: bytes
    blocks: tuple[NifBlock, ...]
    parsed_blocks: tuple[ParsedBlock, ...]
    shapes: tuple[ShapeBlock, ...]
    shaders: tuple[ShaderBlock, ...]
    texture_sets: tuple[TextureSetBlock, ...]
    hair_regions: tuple[HairRegion, ...]
    roots: tuple[int, ...]
    fingerprints: dict[str, str]
    product_fingerprints: dict[str, str]


class BinaryReader:
    def __init__(self, data: bytes, position: int = 0, end: int | None = None):
        self.data = data
        self.position = position
        self.end = len(data) if end is None else end

    def require(self, count: int) -> None:
        if count < 0 or self.position < 0 or self.position > self.end - count:
            raise OracleError(
                "facegeom-hair-regions-oracle-nif-invalid",
                "The NIF is truncated or declares an out-of-bounds field.",
            )

    def take(self, count: int) -> bytes:
        self.require(count)
        value = self.data[self.position : self.position + count]
        self.position += count
        return value

    def skip(self, count: int) -> None:
        self.require(count)
        self.position += count

    def u8(self) -> int:
        return self.take(1)[0]

    def u16(self) -> int:
        return struct.unpack("<H", self.take(2))[0]

    def u32(self) -> int:
        return struct.unpack("<I", self.take(4))[0]

    def i32(self) -> int:
        return struct.unpack("<i", self.take(4))[0]

    def string(self, maximum: int) -> str:
        length = self.u32()
        if length > maximum:
            raise OracleError(
                "facegeom-hair-regions-oracle-nif-invalid",
                "A NIF string exceeds the bounded parser limit.",
            )
        try:
            return self.take(length).decode("latin-1")
        except UnicodeDecodeError as exception:
            raise OracleError(
                "facegeom-hair-regions-oracle-nif-invalid",
                f"A NIF string is not Latin-1: {exception}",
            ) from exception


def _count(value: int, role: str, maximum: int = MAXIMUM_COUNT) -> int:
    if isinstance(value, bool) or value < 0 or value > maximum:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            f"The NIF {role} count is outside the bounded parser limit.",
        )
    return value


def _read_reference(
    reader: BinaryReader,
    block_count: int,
    kind: str,
    slot: int | None = None,
) -> NifReference:
    offset = reader.position
    target = reader.i32()
    if target != -1 and not 0 <= target < block_count:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            f"A NIF {kind} reference targets block {target}, outside the block table.",
        )
    return NifReference(kind, offset, target, slot)


def _read_name(reader: BinaryReader, strings: Sequence[str]) -> str | None:
    index = reader.u32()
    if index == 0xFFFFFFFF:
        return None
    if index >= len(strings):
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "A NIF name index exceeds the string table.",
        )
    return strings[index]


def _read_object_net(
    reader: BinaryReader,
    strings: Sequence[str],
    block_count: int,
) -> tuple[str | None, list[NifReference]]:
    name = _read_name(reader, strings)
    extra_count = _count(reader.u32(), "extra-data")
    references = [
        _read_reference(reader, block_count, "extra", index)
        for index in range(extra_count)
    ]
    references.append(_read_reference(reader, block_count, "controller"))
    return name, references


def _read_av_object(
    reader: BinaryReader,
    strings: Sequence[str],
    block_count: int,
) -> tuple[str | None, list[NifReference]]:
    name, references = _read_object_net(reader, strings, block_count)
    reader.skip(4)  # flags
    reader.skip(12 + 36 + 4)  # translation, rotation, scale
    references.append(_read_reference(reader, block_count, "collision"))
    return name, references


def _validate_reference_type(
    reference: NifReference,
    blocks: Sequence[NifBlock],
    expected: Iterable[str],
) -> None:
    if reference.target == -1:
        return
    expected_set = frozenset(expected)
    actual = blocks[reference.target].block_type
    if actual not in expected_set:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            f"NIF {reference.kind} reference targets {actual}, expected one of "
            f"{sorted(expected_set)}.",
        )


def _raw_sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _canonical_sha256(value: Any) -> str:
    encoded = json.dumps(
        value,
        ensure_ascii=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return _raw_sha256(encoded)


def _product_fingerprints(
    data: bytes,
    blocks: Sequence[NifBlock],
    parsed_blocks: Sequence[ParsedBlock],
    shapes: Sequence[ShapeBlock],
    shaders: Sequence[ShaderBlock],
    texture_sets: Sequence[TextureSetBlock],
    roots: Sequence[int],
) -> dict[str, str]:
    """Independently reproduce the normative schema-1 fingerprint contract."""

    topology = "roots:" + "".join(f"{root}," for root in roots) + "\r\n"
    for parsed in parsed_blocks:
        topology += (
            f"{parsed.block.block_id}|{parsed.block.block_type}|"
            f"{parsed.name if parsed.name is not None else '<null>'}"
        )
        for reference in parsed.references:
            topology += f"|{reference.kind}:{reference.target}"
        topology += "\r\n"

    def identity(block: NifBlock, name: str | None) -> bytes:
        return (
            f"{block.block_id}|{block.block_type}|"
            f"{name if name is not None else '<null>'}\n"
        ).encode("utf-8")

    parsed_by_id = {
        item.block.block_id: item
        for item in parsed_blocks
    }
    geometry = hashlib.sha256()
    for shape in shapes:
        geometry.update(
            identity(
                shape.block,
                parsed_by_id[shape.block.block_id].name,
            )
        )
        geometry.update(data[shape.geometry_offset : shape.block.end])

    skinning = hashlib.sha256()
    for block in blocks:
        if block.block_type not in SKIN_BLOCK_TYPES:
            continue
        skinning.update(
            identity(block, parsed_by_id[block.block_id].name)
        )
        skinning.update(data[block.offset : block.end])

    textures = hashlib.sha256()
    for texture in texture_sets:
        textures.update(
            identity(
                texture.block,
                parsed_by_id[texture.block.block_id].name,
            )
        )
        for route in texture.routes:
            textures.update((route + "\n").encode("utf-8"))

    shader_hash = hashlib.sha256()
    for shader in shaders:
        shader_hash.update(
            identity(
                shader.block,
                parsed_by_id[shader.block.block_id].name,
            )
        )
        raw = bytearray(data[shader.block.offset : shader.block.end])
        if shader.shader_type == 6:
            raw[-12:] = b"\x00" * 12
        shader_hash.update(raw)

    return {
        "topology": hashlib.sha256(topology.encode("utf-8")).hexdigest(),
        "geometry": geometry.hexdigest(),
        "skinning": skinning.hexdigest(),
        "textures": textures.hexdigest(),
        "shaders": shader_hash.hexdigest(),
    }


def parse_nif(data: bytes) -> NifDocument:
    if not isinstance(data, bytes) or not 0 < len(data) <= MAXIMUM_FILE_BYTES:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "The NIF byte length must be between 1 byte and 128 MiB.",
        )
    line_end = data.find(b"\n", 0, min(len(data), 256))
    if line_end < 0 or data[:line_end].rstrip(b"\r") != NIF_HEADER:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "Only Skyrim SE Gamebryo 20.2.0.7 NIFs are supported.",
        )
    reader = BinaryReader(data, line_end + 1)
    if reader.u32() != NIF_VERSION:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "The NIF binary version is not Skyrim SE 20.2.0.7.",
        )
    if reader.u8() != 1:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "Only little-endian Skyrim SE NIFs are supported.",
        )
    if reader.u32() != 12:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "The NIF user version is not the required Skyrim value 12.",
        )
    block_count = _count(reader.u32(), "block", maximum=100_000)
    if block_count < 1:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "A NIF must contain at least one block.",
        )
    if reader.u32() != BETHESDA_STREAM_VERSION:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "The NIF Bethesda stream version is not 100.",
        )
    for _ in range(3):
        reader.skip(reader.u8())

    type_count = reader.u16()
    if not 1 <= type_count <= 4096:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "The NIF block-type count is outside bounds.",
        )
    type_names = tuple(reader.string(4096) for _ in range(type_count))
    type_indexes = tuple(reader.u16() for _ in range(block_count))
    if any(index >= type_count for index in type_indexes):
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "A NIF block-type index exceeds its table.",
        )
    sizes = tuple(reader.u32() for _ in range(block_count))
    if any(size > MAXIMUM_FILE_BYTES for size in sizes):
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "A NIF block exceeds the 128 MiB file budget.",
        )

    string_count = _count(reader.u32(), "string")
    maximum_string_length = reader.u32()
    strings = tuple(reader.string(1024 * 1024) for _ in range(string_count))
    if maximum_string_length and any(
        len(value.encode("latin-1")) > maximum_string_length for value in strings
    ):
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "A NIF string exceeds its declared maximum length.",
        )
    group_count = _count(reader.u32(), "group")
    reader.skip(group_count * 4)

    blocks: list[NifBlock] = []
    for block_id, (type_index, size) in enumerate(zip(type_indexes, sizes, strict=True)):
        blocks.append(
            NifBlock(
                block_id,
                type_names[type_index],
                reader.position,
                size,
            )
        )
        reader.skip(size)

    root_count = _count(reader.u32(), "root")
    roots = tuple(reader.i32() for _ in range(root_count))
    if reader.position != len(data):
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "The NIF footer does not end at the declared file boundary.",
        )
    if any(root != -1 and not 0 <= root < block_count for root in roots):
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "A NIF root reference exceeds the block table.",
        )

    parsed_blocks: list[ParsedBlock] = []
    shapes: list[ShapeBlock] = []
    shaders: list[ShaderBlock] = []
    texture_sets: list[TextureSetBlock] = []
    block_tuple = tuple(blocks)
    for block in blocks:
        block_reader = BinaryReader(data, block.offset, block.end)
        name: str | None = None
        references: list[NifReference] = []
        block_type = block.block_type

        if block_type in NODE_TYPES:
            name, references = _read_av_object(block_reader, strings, block_count)
            child_count = _count(block_reader.u32(), "node-child")
            references.extend(
                _read_reference(block_reader, block_count, "child", index)
                for index in range(child_count)
            )
            effect_count = _count(block_reader.u32(), "node-effect")
            references.extend(
                _read_reference(block_reader, block_count, "effect", index)
                for index in range(effect_count)
            )
            if block_reader.position != block.end:
                raise OracleError(
                    "facegeom-hair-regions-oracle-nif-invalid",
                    f"Node block {block.block_id} does not match the stream-100 layout.",
                )

        elif block_type in SHAPE_TYPES:
            name, references = _read_av_object(block_reader, strings, block_count)
            block_reader.skip(16)  # bounding sphere
            skin_reference = _read_reference(block_reader, block_count, "skin")
            shader_reference = _read_reference(block_reader, block_count, "shader")
            alpha_reference = _read_reference(block_reader, block_count, "alpha")
            references.extend((skin_reference, shader_reference, alpha_reference))
            if block_reader.position + 16 > block.end:
                raise OracleError(
                    "facegeom-hair-regions-oracle-nif-invalid",
                    f"Shape block {block.block_id} lacks its geometry header.",
                )
            _validate_reference_type(
                skin_reference,
                block_tuple,
                SKIN_INSTANCE_TYPES,
            )
            _validate_reference_type(
                shader_reference,
                block_tuple,
                ("BSLightingShaderProperty",),
            )
            _validate_reference_type(
                alpha_reference,
                block_tuple,
                ("NiAlphaProperty",),
            )
            shapes.append(
                ShapeBlock(
                    block,
                    name or "",
                    skin_reference.target,
                    shader_reference.target,
                    shader_reference.byte_offset,
                    alpha_reference.target,
                    block_reader.position,
                )
            )

        elif block_type in SKIN_INSTANCE_TYPES:
            skin_data = _read_reference(block_reader, block_count, "skindata")
            skin_partition = _read_reference(
                block_reader, block_count, "skinpartition"
            )
            skeleton_root = _read_reference(
                block_reader, block_count, "skeletonroot"
            )
            references.extend((skin_data, skin_partition, skeleton_root))
            bone_count = _count(block_reader.u32(), "bone")
            references.extend(
                _read_reference(block_reader, block_count, "bone", index)
                for index in range(bone_count)
            )
            if block_type == "BSDismemberSkinInstance":
                partition_count = _count(
                    block_reader.u32(),
                    "dismember-partition",
                )
                block_reader.skip(partition_count * 4)
            if block_reader.position != block.end:
                raise OracleError(
                    "facegeom-hair-regions-oracle-nif-invalid",
                    f"Skin instance block {block.block_id} does not match the bounded layout.",
                )
            _validate_reference_type(skin_data, block_tuple, ("NiSkinData",))
            _validate_reference_type(
                skin_partition,
                block_tuple,
                ("NiSkinPartition",),
            )
            _validate_reference_type(
                skeleton_root,
                block_tuple,
                NODE_TYPES,
            )
            for reference in references[3:]:
                _validate_reference_type(reference, block_tuple, NODE_TYPES)

        elif block_type == "BSLightingShaderProperty":
            shader_type = block_reader.u32()
            name, references = _read_object_net(
                block_reader,
                strings,
                block_count,
            )
            flags1 = block_reader.u32()
            flags2 = block_reader.u32()
            block_reader.skip(16)  # UV offset and scale
            texture_reference = _read_reference(
                block_reader,
                block_count,
                "textureset",
            )
            references.append(texture_reference)
            _validate_reference_type(
                texture_reference,
                block_tuple,
                ("BSShaderTextureSet",),
            )
            tint_offset: int | None = None
            tint_bits: tuple[int, int, int] | None = None
            if shader_type == 6:
                if block.size < 12 or block.end - 12 < block_reader.position:
                    raise OracleError(
                        "facegeom-hair-regions-oracle-nif-invalid",
                        f"HairTint shader block {block.block_id} has no disjoint 12-byte tint field.",
                    )
                tint_offset = block.end - 12
                tint_bits = struct.unpack_from("<III", data, tint_offset)
                for bits in tint_bits:
                    value = struct.unpack("<f", struct.pack("<I", bits))[0]
                    if not math.isfinite(value) or not 0.0 <= value <= 1.0:
                        raise OracleError(
                            "facegeom-hair-regions-oracle-nif-invalid",
                            f"HairTint shader block {block.block_id} contains a non-finite or out-of-range color.",
                        )
            shaders.append(
                ShaderBlock(
                    block,
                    shader_type,
                    flags1,
                    flags2,
                    texture_reference.target,
                    tint_offset,
                    tint_bits,
                )
            )

        elif block_type == "BSShaderTextureSet":
            route_count = _count(block_reader.u32(), "texture-route", maximum=32)
            if route_count < 1:
                raise OracleError(
                    "facegeom-hair-regions-oracle-nif-invalid",
                    "A shader texture set must contain at least one route.",
                )
            routes = tuple(block_reader.string(4096) for _ in range(route_count))
            if block_reader.position != block.end:
                raise OracleError(
                    "facegeom-hair-regions-oracle-nif-invalid",
                    f"Texture-set block {block.block_id} has trailing or truncated bytes.",
                )
            texture_sets.append(TextureSetBlock(block, routes))

        elif block_type == "NiAlphaProperty":
            name, references = _read_object_net(
                block_reader,
                strings,
                block_count,
            )
            block_reader.skip(2 + 1)
            if block_reader.position != block.end:
                raise OracleError(
                    "facegeom-hair-regions-oracle-nif-invalid",
                    f"Alpha-property block {block.block_id} does not match the bounded layout.",
                )

        elif block_type in LEAF_TYPES:
            pass

        else:
            raise OracleError(
                "facegeom-hair-regions-oracle-nif-invalid",
                f"Unsupported NIF block type {block_type!r} at block {block.block_id}; "
                "the raw oracle refuses to guess.",
            )
        parsed_blocks.append(
            ParsedBlock(
                block,
                name,
                tuple(references),
            )
        )

    shader_by_id = {item.block.block_id: item for item in shaders}
    texture_by_id = {item.block.block_id: item for item in texture_sets}
    hair_shape_rows: list[
        tuple[ShapeBlock, ShaderBlock, TextureSetBlock | None]
    ] = []
    for shape in shapes:
        shader = shader_by_id.get(shape.shader_block_id)
        if shader is None or shader.shader_type != 6:
            continue
        texture = texture_by_id.get(shader.texture_set_block_id)
        if texture is None:
            raise OracleError(
                "facegeom-hair-regions-oracle-nif-invalid",
                f"HairTint shader block {shader.block.block_id} lacks one exact texture-set route.",
            )
        hair_shape_rows.append((shape, shader, texture))
    if len(hair_shape_rows) > MAXIMUM_REGIONS:
        raise OracleError(
            "facegeom-hair-regions-oracle-nif-invalid",
            "The NIF contains more than 64 HairTint shapes.",
        )

    ownership: dict[int, tuple[str, ...]] = {}
    for _, shader, _ in hair_shape_rows:
        ownership[shader.block.block_id] = tuple(
            sorted(
                f"shape:{candidate.block.block_id}:shader:{candidate_shader.block.block_id}"
                for candidate, candidate_shader, _ in hair_shape_rows
                if candidate_shader.block.block_id == shader.block.block_id
            )
        )
    duplicate_names: dict[str, int] = {}
    hair_region_values: list[HairRegion] = []
    for shape, shader, texture in sorted(
        hair_shape_rows,
        key=lambda item: item[0].block.block_id,
    ):
        duplicate_ordinal = duplicate_names.get(shape.name, 0)
        duplicate_names[shape.name] = duplicate_ordinal + 1
        hair_region_values.append(HairRegion(
            structural_id=f"shape:{shape.block.block_id}:shader:{shader.block.block_id}",
            name=shape.name,
            duplicate_name_ordinal=duplicate_ordinal,
            shared_shader_group_id=f"shader:{shader.block.block_id}",
            shape_block_id=shape.block.block_id,
            shape_block_type=shape.block.block_type,
            shape_shader_reference_byte_offset=(
                shape.shader_reference_byte_offset
            ),
            shader_block_id=shader.block.block_id,
            tint_byte_offset=(
                shader.tint_byte_offset
                if shader.tint_byte_offset is not None
                else _unreachable("HairTint shader lost tint offset")
            ),
            tint_bits=(
                shader.tint_bits
                if shader.tint_bits is not None
                else _unreachable("HairTint shader lost tint bits")
            ),
            texture_set_block_id=shader.texture_set_block_id,
            texture_routes=texture.routes if texture is not None else (),
            shader_owner_structural_ids=ownership[shader.block.block_id],
        ))
    hair_regions = tuple(hair_region_values)

    topology_rows = {
        "blocks": [
            {
                "id": block.block_id,
                "type": block.block_type,
                "size": block.size,
                "references": [
                    {
                        "kind": reference.kind,
                        "slot": reference.slot,
                        "target": reference.target,
                    }
                    for reference in parsed_blocks[block.block_id].references
                ],
            }
            for block in blocks
        ],
        "roots": list(roots),
    }
    geometry_rows = [
        {
            "id": shape.block.block_id,
            "type": shape.block.block_type,
            "sha256": _raw_sha256(data[shape.block.offset : shape.block.end]),
        }
        for shape in shapes
    ]
    skinning_rows = [
        {
            "id": block.block_id,
            "type": block.block_type,
            "sha256": _raw_sha256(data[block.offset : block.end]),
        }
        for block in blocks
        if block.block_type in SKIN_BLOCK_TYPES
    ]
    skinning_rows.extend(
        {
            "shapeId": shape.block.block_id,
            "skinId": shape.skin_block_id,
        }
        for shape in shapes
    )
    texture_rows = {
        "sets": [
            {
                "id": texture.block.block_id,
                "routes": list(texture.routes),
                "sha256": _raw_sha256(
                    data[texture.block.offset : texture.block.end]
                ),
            }
            for texture in texture_sets
        ],
        "ownership": [
            {
                "shapeId": shape.block.block_id,
                "shaderId": shape.shader_block_id,
                "textureSetId": (
                    shader_by_id[shape.shader_block_id].texture_set_block_id
                    if shape.shader_block_id in shader_by_id
                    else -1
                ),
            }
            for shape in shapes
        ],
    }
    owners_by_shader: dict[int, list[int]] = {}
    for shape in shapes:
        owners_by_shader.setdefault(shape.shader_block_id, []).append(
            shape.block.block_id
        )
    shader_rows: list[dict[str, Any]] = []
    for shader in shaders:
        raw = bytearray(data[shader.block.offset : shader.block.end])
        if shader.tint_byte_offset is not None:
            relative = shader.tint_byte_offset - shader.block.offset
            raw[relative : relative + 12] = b"\x00" * 12
        shader_rows.append(
            {
                "id": shader.block.block_id,
                "type": shader.shader_type,
                "flags1": shader.flags1,
                "flags2": shader.flags2,
                "textureSetId": shader.texture_set_block_id,
                "owners": sorted(owners_by_shader.get(shader.block.block_id, [])),
                "maskedSha256": _raw_sha256(bytes(raw)),
            }
        )
    fingerprints = {
        "topology": _canonical_sha256(topology_rows),
        "geometry": _canonical_sha256(geometry_rows),
        "skinning": _canonical_sha256(skinning_rows),
        "textures": _canonical_sha256(texture_rows),
        "shaderOwnership": _canonical_sha256(shader_rows),
    }
    product_fingerprints = _product_fingerprints(
        data,
        blocks,
        parsed_blocks,
        shapes,
        shaders,
        texture_sets,
        roots,
    )
    return NifDocument(
        data,
        tuple(blocks),
        tuple(parsed_blocks),
        tuple(shapes),
        tuple(shaders),
        tuple(texture_sets),
        hair_regions,
        roots,
        fingerprints,
        product_fingerprints,
    )


def _unreachable(message: str) -> NoReturn:
    raise AssertionError(message)


def _duplicate_rejecting_object(
    pairs: list[tuple[str, Any]],
) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for name, value in pairs:
        if name in result:
            raise DuplicatePropertyError(f"Duplicate JSON property {name!r}.")
        result[name] = value
    return result


def _reject_json_constant(value: str) -> NoReturn:
    raise ValueError(f"Non-finite JSON number {value!r} is forbidden.")


def _json_budget(value: Any, depth: int = 0) -> int:
    if depth > MAXIMUM_JSON_DEPTH:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            "A JSON document exceeds the maximum nesting depth.",
        )
    total = 1
    if isinstance(value, dict):
        for name, child in value.items():
            if not isinstance(name, str):
                raise OracleError(
                    "facegeom-hair-regions-oracle-json-invalid",
                    "JSON object property names must be strings.",
                )
            total += _json_budget(child, depth + 1)
    elif isinstance(value, list):
        for child in value:
            total += _json_budget(child, depth + 1)
    if total > MAXIMUM_JSON_NODES:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            "A JSON document exceeds the maximum node budget.",
        )
    return total


def _parse_strict_json(data: bytes, role: str) -> dict[str, Any]:
    try:
        text = data.decode("utf-8")
        value = json.loads(
            text,
            object_pairs_hook=_duplicate_rejecting_object,
            parse_constant=_reject_json_constant,
        )
    except (UnicodeDecodeError, json.JSONDecodeError, DuplicatePropertyError, ValueError) as exception:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"The {role} JSON document is not strict UTF-8 JSON: {exception}",
        ) from exception
    _json_budget(value)
    if not isinstance(value, dict):
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"The {role} JSON root must be an object.",
        )
    return value


def _require_exact_keys(
    value: Any,
    required: Iterable[str],
    role: str,
    optional: Iterable[str] = (),
) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must be a JSON object.",
        )
    required_set = frozenset(required)
    allowed = required_set | frozenset(optional)
    actual = frozenset(value)
    missing = sorted(required_set - actual)
    unknown = sorted(actual - allowed)
    if missing or unknown:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} has missing properties {missing} or unknown properties {unknown}.",
        )
    return value


def _require_string(value: Any, role: str, allow_empty: bool = False) -> str:
    if (
        not isinstance(value, str)
        or "\x00" in value
        or (not allow_empty and not value.strip())
    ):
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must be a non-null JSON string.",
        )
    return value


def _require_bool(value: Any, role: str) -> bool:
    if type(value) is not bool:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must be a JSON boolean.",
        )
    return value


def _require_int(
    value: Any,
    role: str,
    minimum: int = 0,
    maximum: int = (1 << 63) - 1,
) -> int:
    if type(value) is not int or not minimum <= value <= maximum:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must be an integer in [{minimum}, {maximum}].",
        )
    return value


def _require_sha256(value: Any, role: str) -> str:
    text = _require_string(value, role)
    if SHA256_RE.fullmatch(text) is None:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must be canonical lowercase SHA-256.",
        )
    return text


def _require_color(value: Any, role: str) -> str:
    text = _require_string(value, role)
    if COLOR_RE.fullmatch(text) is None:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must be canonical uppercase #RRGGBB.",
        )
    return text


def _require_k_authority_path(value: Any, role: str) -> str:
    text = _require_string(value, role)
    canonical = _require_k_path(text, role)
    if text != str(canonical):
        raise OracleError(
            "facegeom-hair-regions-oracle-path-invalid",
            f"{role} must use the canonical ordinary absolute K-local spelling.",
        )
    return text


def _validate_file_authority(value: Any, role: str) -> dict[str, Any]:
    document = _require_exact_keys(
        value,
        ("path", "byteLength", "sha256"),
        role,
    )
    _require_k_authority_path(document["path"], f"{role}.path")
    _require_int(
        document["byteLength"],
        f"{role}.byteLength",
        1,
        MAXIMUM_FILE_BYTES,
    )
    _require_sha256(document["sha256"], f"{role}.sha256")
    return document


def _validate_structural_ids(
    value: Any,
    role: str,
    *,
    allow_empty: bool,
) -> list[str]:
    if (
        not isinstance(value, list)
        or len(value) > MAXIMUM_REGIONS
        or (not allow_empty and not value)
        or len(set(value)) != len(value)
        or any(
            not isinstance(item, str)
            or STRUCTURAL_ID_RE.fullmatch(item) is None
            for item in value
        )
    ):
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must be a bounded unique structural-ID array.",
        )
    return value


def _validate_texture_routes(value: Any, role: str) -> list[str]:
    if (
        not isinstance(value, list)
        or not 1 <= len(value) <= 32
        or any(
            not isinstance(item, str)
            or "\x00" in item
            or len(item) > 4096
            for item in value
        )
    ):
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must be a bounded non-null texture-route array.",
        )
    return value


def _validate_regions(value: Any, role: str) -> list[dict[str, Any]]:
    if not isinstance(value, list) or not 1 <= len(value) <= MAXIMUM_REGIONS:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must contain between 1 and 64 regions.",
        )
    result: list[dict[str, Any]] = []
    seen: set[str] = set()
    for index, item in enumerate(value):
        region_role = f"{role}[{index}]"
        region = _require_exact_keys(
            item,
            (
                "structuralId",
                "name",
                "duplicateNameOrdinal",
                "shapeBlockType",
                "shapeBlockId",
                "shapeShaderReferenceByteOffset",
                "shaderBlockType",
                "shaderBlockId",
                "sharedShaderGroupId",
                "shaderOwnerStructuralIds",
                "sharedStructuralIds",
                "textureSetBlockId",
                "textureRoutes",
                "currentColor",
                "colorFloatBits",
                "tintByteOffset",
                "tintByteLength",
                "defaultRole",
            ),
            region_role,
        )
        structural_id = _require_string(
            region["structuralId"],
            f"{region_role}.structuralId",
        )
        match = STRUCTURAL_ID_RE.fullmatch(structural_id)
        if match is None or structural_id in seen:
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{role} contains an invalid or duplicate structural ID.",
            )
        seen.add(structural_id)
        _require_string(
            region["name"],
            f"{region_role}.name",
            allow_empty=True,
        )
        _require_int(
            region["duplicateNameOrdinal"],
            f"{region_role}.duplicateNameOrdinal",
            0,
            MAXIMUM_REGIONS - 1,
        )
        shape_type = _require_string(
            region["shapeBlockType"],
            f"{region_role}.shapeBlockType",
        )
        if shape_type not in SHAPE_TYPES:
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{region_role}.shapeBlockType is unsupported.",
            )
        shape_id = _require_int(
            region["shapeBlockId"],
            f"{region_role}.shapeBlockId",
            0,
            MAXIMUM_COUNT,
        )
        _require_int(
            region["shapeShaderReferenceByteOffset"],
            f"{region_role}.shapeShaderReferenceByteOffset",
            0,
            MAXIMUM_FILE_BYTES - 4,
        )
        if region["shaderBlockType"] != "BSLightingShaderProperty":
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{region_role}.shaderBlockType is unsupported.",
            )
        shader_id = _require_int(
            region["shaderBlockId"],
            f"{region_role}.shaderBlockId",
            0,
            MAXIMUM_COUNT,
        )
        shared_group = _require_string(
            region["sharedShaderGroupId"],
            f"{region_role}.sharedShaderGroupId",
        )
        if (
            structural_id != f"shape:{shape_id}:shader:{shader_id}"
            or shared_group != f"shader:{shader_id}"
        ):
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{region_role} structural ownership fields disagree.",
            )
        owners = _validate_structural_ids(
            region["shaderOwnerStructuralIds"],
            f"{region_role}.shaderOwnerStructuralIds",
            allow_empty=False,
        )
        shared = _validate_structural_ids(
            region["sharedStructuralIds"],
            f"{region_role}.sharedStructuralIds",
            allow_empty=True,
        )
        if structural_id not in owners or sorted(shared) != sorted(
            item for item in owners if item != structural_id
        ):
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{region_role} shared ownership arrays disagree.",
            )
        _require_int(
            region["textureSetBlockId"],
            f"{region_role}.textureSetBlockId",
            0,
            MAXIMUM_COUNT,
        )
        _validate_texture_routes(
            region["textureRoutes"],
            f"{region_role}.textureRoutes",
        )
        _require_color(
            region["currentColor"],
            f"{region_role}.currentColor",
        )
        _validate_bits(
            region["colorFloatBits"],
            f"{region_role}.colorFloatBits",
        )
        _require_int(
            region["tintByteOffset"],
            f"{region_role}.tintByteOffset",
            0,
            MAXIMUM_FILE_BYTES - 12,
        )
        if _require_int(
            region["tintByteLength"],
            f"{region_role}.tintByteLength",
            0,
            12,
        ) != 12:
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{region_role}.tintByteLength must be 12.",
            )
        if region["defaultRole"] != "preserve":
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{region_role}.defaultRole must be preserve.",
            )
        result.append(region)
    return result


def _validate_assignments(value: Any, role: str) -> list[dict[str, Any]]:
    if not isinstance(value, list) or not 1 <= len(value) <= MAXIMUM_REGIONS:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must contain between 1 and 64 assignments.",
        )
    assignments: list[dict[str, Any]] = []
    seen: set[str] = set()
    for index, item in enumerate(value):
        assignment = _require_exact_keys(
            item,
            ("structuralId", "role"),
            f"{role}[{index}]",
        )
        structural_id = _require_string(
            assignment["structuralId"],
            f"{role}[{index}].structuralId",
        )
        if STRUCTURAL_ID_RE.fullmatch(structural_id) is None or structural_id in seen:
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{role} contains an invalid or duplicate structural ID.",
            )
        seen.add(structural_id)
        region_role = _require_string(
            assignment["role"],
            f"{role}[{index}].role",
        )
        if region_role not in ROLES:
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{role}[{index}].role is outside the closed role enum.",
            )
        assignments.append(assignment)
    return assignments


def _validate_bits(value: Any, role: str) -> list[int]:
    if not isinstance(value, list) or len(value) != 3:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must contain exactly three IEEE-754 bit patterns.",
        )
    return [
        _require_int(item, f"{role}[{index}]", 0, 0xFFFFFFFF)
        for index, item in enumerate(value)
    ]


def _validate_envelopes(value: Any, role: str) -> list[dict[str, Any]]:
    if not isinstance(value, list) or not 1 <= len(value) <= MAXIMUM_REGIONS:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must contain between 1 and 64 selected envelopes.",
        )
    envelopes: list[dict[str, Any]] = []
    for index, item in enumerate(value):
        envelope = _require_exact_keys(
            item,
            (
                "sharedShaderGroupId",
                "structuralIds",
                "role",
                "byteOffset",
                "byteLength",
                "oldFloatBits",
                "newFloatBits",
            ),
            f"{role}[{index}]",
        )
        group = _require_string(
            envelope["sharedShaderGroupId"],
            f"{role}[{index}].sharedShaderGroupId",
        )
        if SHADER_GROUP_RE.fullmatch(group) is None:
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{role}[{index}] has an invalid shader group ID.",
            )
        structural_ids = envelope["structuralIds"]
        if (
            not isinstance(structural_ids, list)
            or not 1 <= len(structural_ids) <= MAXIMUM_REGIONS
            or len(set(structural_ids)) != len(structural_ids)
            or any(
                not isinstance(item, str)
                or STRUCTURAL_ID_RE.fullmatch(item) is None
                for item in structural_ids
            )
        ):
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{role}[{index}].structuralIds is invalid or duplicated.",
            )
        selected_role = _require_string(
            envelope["role"],
            f"{role}[{index}].role",
        )
        if selected_role not in ("primary", "accent"):
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{role}[{index}] may only authorize Primary or Accent.",
            )
        _require_int(
            envelope["byteOffset"],
            f"{role}[{index}].byteOffset",
            0,
            MAXIMUM_FILE_BYTES - 12,
        )
        if _require_int(
            envelope["byteLength"],
            f"{role}[{index}].byteLength",
            0,
            12,
        ) != 12:
            raise OracleError(
                "facegeom-hair-regions-oracle-json-invalid",
                f"{role}[{index}] must authorize exactly 12 bytes.",
            )
        _validate_bits(
            envelope["oldFloatBits"],
            f"{role}[{index}].oldFloatBits",
        )
        _validate_bits(
            envelope["newFloatBits"],
            f"{role}[{index}].newFloatBits",
        )
        envelopes.append(envelope)
    return envelopes


def _validate_offsets(value: Any, role: str) -> list[int]:
    if not isinstance(value, list) or len(value) > MAXIMUM_REGIONS * 12:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} exceeds the bounded changed-byte budget.",
        )
    offsets = [
        _require_int(item, f"{role}[{index}]", 0, MAXIMUM_FILE_BYTES - 1)
        for index, item in enumerate(value)
    ]
    if offsets != sorted(set(offsets)):
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            f"{role} must be unique and strictly ascending.",
        )
    return offsets


def _validate_fingerprints(value: Any, role: str) -> dict[str, Any]:
    document = _require_exact_keys(
        value,
        ("topology", "geometry", "skinning", "textures", "shaders"),
        role,
    )
    for name in ("topology", "geometry", "skinning", "textures", "shaders"):
        _require_sha256(document[name], f"{role}.{name}")
    return document


def validate_analysis(value: Any) -> dict[str, Any]:
    document = _require_exact_keys(
        value,
        (
            "schema",
            "source",
            "regions",
            "fingerprints",
            "pluginColorContext",
        ),
        "analysis",
    )
    if document["schema"] != ANALYSIS_SCHEMA:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            "The analysis schema is unknown.",
        )
    _validate_file_authority(document["source"], "analysis.source")
    _validate_regions(document["regions"], "analysis.regions")
    _validate_fingerprints(
        document["fingerprints"],
        "analysis.fingerprints",
    )
    _validate_plugin_context(document["pluginColorContext"])
    return document


def validate_request(value: Any) -> dict[str, Any]:
    document = _require_exact_keys(
        value,
        (
            "schema",
            "analysisSha256",
            "source",
            "primaryColor",
            "accentColor",
            "assignments",
            "output",
            "manifest",
        ),
        "request",
    )
    if document["schema"] != REQUEST_SCHEMA:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            "The request schema is unknown.",
        )
    _require_sha256(document["analysisSha256"], "request.analysisSha256")
    _validate_file_authority(document["source"], "request.source")
    primary = _require_color(document["primaryColor"], "request.primaryColor")
    accent = _require_color(document["accentColor"], "request.accentColor")
    if primary == accent:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            "Primary and Accent colors must be distinct.",
        )
    _validate_assignments(document["assignments"], "request.assignments")
    _require_k_authority_path(document["output"], "request.output")
    _require_k_authority_path(document["manifest"], "request.manifest")
    return document


def _validate_plugin_context(value: Any) -> None:
    if value is None:
        return
    document = _require_exact_keys(
        value,
        ("plugin", "hairColor", "hairColorHex"),
        "proposal.pluginColorContext",
    )
    _require_string(document["plugin"], "proposal.pluginColorContext.plugin")
    if document["hairColor"] is not None:
        _require_string(
            document["hairColor"],
            "proposal.pluginColorContext.hairColor",
        )
    if document["hairColorHex"] is not None:
        _require_color(
            document["hairColorHex"],
            "proposal.pluginColorContext.hairColorHex",
        )


def validate_proposal(value: Any) -> dict[str, Any]:
    document = _require_exact_keys(
        value,
        (
            "schema",
            "analysisSha256",
            "requestSha256",
            "source",
            "output",
            "manifest",
            "primaryColor",
            "accentColor",
            "assignments",
            "authorizedEnvelopes",
            "predictedChangedByteOffsets",
            "sourceFingerprints",
            "expectedOutputFingerprints",
            "expectedOutput",
            "pluginColorContext",
        ),
        "proposal",
    )
    if document["schema"] != PROPOSAL_SCHEMA:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            "The proposal schema is unknown.",
        )
    _require_sha256(document["analysisSha256"], "proposal.analysisSha256")
    _require_sha256(document["requestSha256"], "proposal.requestSha256")
    _validate_file_authority(document["source"], "proposal.source")
    _require_k_authority_path(document["output"], "proposal.output")
    _require_k_authority_path(document["manifest"], "proposal.manifest")
    _require_color(document["primaryColor"], "proposal.primaryColor")
    _require_color(document["accentColor"], "proposal.accentColor")
    _validate_assignments(document["assignments"], "proposal.assignments")
    _validate_envelopes(
        document["authorizedEnvelopes"],
        "proposal.authorizedEnvelopes",
    )
    _validate_offsets(
        document["predictedChangedByteOffsets"],
        "proposal.predictedChangedByteOffsets",
    )
    _validate_fingerprints(
        document["sourceFingerprints"],
        "proposal.sourceFingerprints",
    )
    _validate_fingerprints(
        document["expectedOutputFingerprints"],
        "proposal.expectedOutputFingerprints",
    )
    _validate_file_authority(
        document["expectedOutput"],
        "proposal.expectedOutput",
    )
    _validate_plugin_context(document["pluginColorContext"])
    return document


def validate_manifest(value: Any) -> dict[str, Any]:
    document = _require_exact_keys(
        value,
        (
            "schema",
            "proposalSha256",
            "source",
            "output",
            "authorizedEnvelopes",
            "changedByteOffsets",
            "fingerprints",
            "survivingArtifacts",
            "runtimeAuthority",
        ),
        "manifest",
    )
    if document["schema"] != MANIFEST_SCHEMA:
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            "The manifest schema is unknown.",
        )
    _require_sha256(document["proposalSha256"], "manifest.proposalSha256")
    _validate_file_authority(document["source"], "manifest.source")
    _validate_file_authority(document["output"], "manifest.output")
    _validate_envelopes(
        document["authorizedEnvelopes"],
        "manifest.authorizedEnvelopes",
    )
    _validate_offsets(
        document["changedByteOffsets"],
        "manifest.changedByteOffsets",
    )
    _validate_fingerprints(document["fingerprints"], "manifest.fingerprints")
    surviving = document["survivingArtifacts"]
    if (
        not isinstance(surviving, list)
        or len(surviving) > MAXIMUM_REGIONS
        or any(not isinstance(item, str) or not item.strip() for item in surviving)
    ):
        raise OracleError(
            "facegeom-hair-regions-oracle-json-invalid",
            "manifest.survivingArtifacts must be a bounded string array.",
        )
    _require_bool(document["runtimeAuthority"], "manifest.runtimeAuthority")
    return document


def _path_text(path: str | Path) -> str:
    return str(path)


def _require_k_path(path: str | Path, role: str) -> Path:
    text = _path_text(path)
    normalized_separators = text.replace("/", "\\")
    if (
        not text
        or "\x00" in text
        or normalized_separators.startswith("\\\\")
        or normalized_separators.startswith("\\?\\")
        or normalized_separators.startswith("\\.\\")
        or len(normalized_separators) < 3
        or normalized_separators[1:3] != ":\\"
        or normalized_separators[0].casefold() != "k"
        or ":" in normalized_separators[2:]
        or any(part == ".." for part in Path(text).parts)
    ):
        raise OracleError(
            "facegeom-hair-regions-oracle-path-invalid",
            f"{role} must be an ordinary absolute K-local path without UNC, device, ADS, or traversal syntax.",
        )
    candidate = Path(os.path.abspath(text))
    root_text = os.path.normcase(str(WORKSPACE_ROOT.resolve(strict=True)))
    candidate_text = os.path.normcase(str(candidate))
    try:
        common = os.path.commonpath((root_text, candidate_text))
    except ValueError as exception:
        raise OracleError(
            "facegeom-hair-regions-oracle-path-invalid",
            f"{role} is outside the K-local workspace.",
        ) from exception
    if common != root_text or candidate_text == root_text:
        raise OracleError(
            "facegeom-hair-regions-oracle-path-invalid",
            f"{role} is outside the K-local workspace or names the workspace root.",
        )
    return candidate


def _windows_failure(role: str, action: str) -> OracleError:
    code = ctypes.get_last_error()
    return OracleError(
        "facegeom-hair-regions-oracle-path-invalid",
        f"{role} could not be {action}: "
        f"{ctypes.FormatError(code).strip()} (Win32 {code}).",
    )


def _open_windows_handle(
    path: Path,
    *,
    access: int,
    share: int,
    creation: int,
    flags: int,
    role: str,
) -> int:
    handle = _CREATE_FILE(
        str(path),
        access,
        share,
        None,
        creation,
        flags,
        None,
    )
    if handle in (None, INVALID_HANDLE_VALUE):
        raise _windows_failure(role, "opened")
    return int(handle)


def _close_windows_handle(handle: int) -> None:
    if not _CLOSE_HANDLE(handle):
        raise _windows_failure("Windows file handle", "closed")


def _query_basic_file_information(
    handle: int,
    role: str,
) -> ByHandleFileInformation:
    value = ByHandleFileInformation()
    if not _GET_FILE_INFORMATION(handle, ctypes.byref(value)):
        raise _windows_failure(role, "inspected by its pinned handle")
    return value


def _query_attribute_tag(
    handle: int,
    role: str,
) -> FileAttributeTagInfo:
    value = FileAttributeTagInfo()
    if not _GET_FILE_INFORMATION_EX(
        handle,
        FILE_ATTRIBUTE_TAG_INFO_CLASS,
        ctypes.byref(value),
        ctypes.sizeof(value),
    ):
        code = ctypes.get_last_error()
        if code not in (1, 50, 87):
            raise _windows_failure(
                role,
                "inspected for reparse identity",
            )
        basic = _query_basic_file_information(handle, role)
        value.FileAttributes = basic.FileAttributes
        value.ReparseTag = 0
    return value


def _query_file_identity(
    handle: int,
    role: str,
) -> FileIdentity:
    value = FileIdInfo()
    if not _GET_FILE_INFORMATION_EX(
        handle,
        FILE_ID_INFO_CLASS,
        ctypes.byref(value),
        ctypes.sizeof(value),
    ):
        code = ctypes.get_last_error()
        if code not in (1, 50, 87):
            raise _windows_failure(
                role,
                "inspected for stable file identity",
            )
        basic = _query_basic_file_information(handle, role)
        file_index = (
            int(basic.FileIndexHigh) << 32
        ) | int(basic.FileIndexLow)
        return FileIdentity(
            int(basic.VolumeSerialNumber),
            struct.pack("<Q", file_index) + b"\x00" * 8,
        )
    return FileIdentity(
        int(value.VolumeSerialNumber),
        bytes(value.FileId.Identifier),
    )


def _query_file_size(handle: int, role: str) -> int:
    value = ctypes.c_longlong()
    if not _GET_FILE_SIZE_EX(handle, ctypes.byref(value)):
        raise _windows_failure(role, "inspected for exact byte length")
    return int(value.value)


def _final_path_for_handle(handle: int, role: str) -> Path:
    required = _GET_FINAL_PATH(handle, None, 0, 0)
    if required == 0:
        raise _windows_failure(role, "resolved to its final path")
    buffer = ctypes.create_unicode_buffer(required + 1)
    written = _GET_FINAL_PATH(handle, buffer, len(buffer), 0)
    if written == 0 or written >= len(buffer):
        raise _windows_failure(role, "resolved to its final path")
    value = buffer.value
    if value.startswith("\\\\?\\UNC\\"):
        value = "\\\\" + value[8:]
    elif value.startswith("\\\\?\\"):
        value = value[4:]
    return Path(value)


def _same_canonical_path(left: Path, right: Path) -> bool:
    return os.path.normcase(os.path.abspath(str(left))) == os.path.normcase(
        os.path.abspath(str(right))
    )


def _require_handle_path(
    handle: int,
    expected: Path,
    role: str,
    *,
    allow_workspace_root: bool = False,
) -> Path:
    final_path = _final_path_for_handle(handle, role)
    if not (
        allow_workspace_root
        and _same_canonical_path(
            final_path,
            WORKSPACE_ROOT,
        )
    ):
        _require_k_path(final_path, role)
    if not _same_canonical_path(final_path, expected):
        raise OracleError(
            "facegeom-hair-regions-oracle-path-invalid",
            f"{role} final handle path differs from the requested ordinary K-local path.",
        )
    return final_path


def _inspect_directory_no_reparse(path: Path, role: str) -> None:
    handle = _open_windows_handle(
        path,
        access=FILE_READ_ATTRIBUTES,
        share=FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        creation=OPEN_EXISTING,
        flags=(
            FILE_FLAG_BACKUP_SEMANTICS
            | FILE_FLAG_OPEN_REPARSE_POINT
        ),
        role=role,
    )
    try:
        tag = _query_attribute_tag(handle, role)
        if tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT:
            raise OracleError(
                "facegeom-hair-regions-oracle-path-invalid",
                f"{role} traverses a reparse point: {path}.",
            )
        if not tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY:
            raise OracleError(
                "facegeom-hair-regions-oracle-path-invalid",
                f"{role} ancestor is not an ordinary directory: {path}.",
            )
        _require_handle_path(
            handle,
            path,
            role,
            allow_workspace_root=True,
        )
    finally:
        _close_windows_handle(handle)


def _require_no_reparse_ancestry(
    path: Path,
    role: str,
    include_leaf: bool,
) -> None:
    relative = path.relative_to(WORKSPACE_ROOT)
    current = WORKSPACE_ROOT
    parts = relative.parts if include_leaf else relative.parts[:-1]
    _inspect_directory_no_reparse(WORKSPACE_ROOT, role)
    for part in parts:
        current = current / part
        if include_leaf and current == path:
            break
        _inspect_directory_no_reparse(current, role)


def _read_pinned_file(
    path: str | Path,
    role: str,
    *,
    maximum_bytes: int = MAXIMUM_FILE_BYTES,
    opened_handles: list[int] | None = None,
) -> PinnedFileRead:
    candidate = _require_k_path(path, role)
    _require_no_reparse_ancestry(
        candidate,
        role,
        include_leaf=False,
    )
    handle = _open_windows_handle(
        candidate,
        access=GENERIC_READ | FILE_READ_ATTRIBUTES,
        share=FILE_SHARE_READ,
        creation=OPEN_EXISTING,
        flags=(
            FILE_ATTRIBUTE_NORMAL
            | FILE_FLAG_SEQUENTIAL_SCAN
            | FILE_FLAG_OPEN_REPARSE_POINT
        ),
        role=role,
    )
    retain_handle = False
    try:
        tag = _query_attribute_tag(handle, role)
        if (
            tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT
            or tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY
        ):
            raise OracleError(
                "facegeom-hair-regions-oracle-path-invalid",
                f"{role} must be an ordinary non-reparse file.",
            )
        final_path = _require_handle_path(handle, candidate, role)
        identity_before = _query_file_identity(handle, role)
        size_before = _query_file_size(handle, role)
        if not 0 < size_before <= maximum_bytes:
            raise OracleError(
                "facegeom-hair-regions-oracle-path-invalid",
                f"{role} must be non-empty and no larger than {maximum_bytes} bytes.",
            )
        chunks: list[bytes] = []
        total = 0
        while total < size_before:
            requested = min(64 * 1024, size_before - total)
            buffer = ctypes.create_string_buffer(requested)
            observed = wintypes.DWORD()
            if not _READ_FILE(
                handle,
                buffer,
                requested,
                ctypes.byref(observed),
                None,
            ):
                raise _windows_failure(role, "read from its pinned handle")
            if observed.value == 0:
                break
            chunks.append(buffer.raw[: observed.value])
            total += observed.value
        extra = ctypes.create_string_buffer(1)
        observed = wintypes.DWORD()
        if not _READ_FILE(
            handle,
            extra,
            1,
            ctypes.byref(observed),
            None,
        ):
            raise _windows_failure(role, "checked for overlong bytes")
        identity_after = _query_file_identity(handle, role)
        size_after = _query_file_size(handle, role)
        if (
            total != size_before
            or observed.value != 0
            or size_after != size_before
            or identity_after != identity_before
        ):
            raise OracleError(
                "facegeom-hair-regions-oracle-path-invalid",
                f"{role} changed while its pinned handle was read.",
            )
        result = PinnedFileRead(
            candidate,
            final_path,
            identity_before,
            b"".join(chunks),
            handle,
        )
        if opened_handles is not None:
            opened_handles.append(handle)
            retain_handle = True
        return result
    finally:
        if not retain_handle:
            _close_windows_handle(handle)


def require_new_report_path(path: str | Path) -> Path:
    candidate = _require_k_path(path, "oracle report")
    _require_no_reparse_ancestry(candidate, "oracle report", include_leaf=False)
    if os.path.lexists(candidate):
        raise OracleError(
            "facegeom-hair-regions-oracle-report-exists",
            f"The oracle report already exists and will not be overwritten: {candidate}.",
        )
    _inspect_directory_no_reparse(
        candidate.parent,
        "oracle report parent",
    )
    return candidate


def _read_document(
    path: str | Path,
    expected_sha256: str,
    role: str,
    validator: Any,
    opened_handles: list[int],
) -> tuple[dict[str, Any], bytes, PinnedFileRead]:
    _require_sha256(expected_sha256, f"expected {role} SHA-256")
    document_file = _read_pinned_file(
        path,
        f"{role} document",
        opened_handles=opened_handles,
    )
    data = document_file.data
    parsed = _parse_strict_json(data, role)
    value = validator(parsed)
    observed = _raw_sha256(data)
    if observed != expected_sha256:
        raise OracleError(
            "facegeom-hair-regions-oracle-document-authority-mismatch",
            f"The exact {role} JSON bytes hash to {observed}, not {expected_sha256}.",
        )
    return value, data, document_file


def _bits_for_color(color: str) -> tuple[int, int, int]:
    return tuple(
        struct.unpack(
            "<I",
            struct.pack("<f", int(color[index : index + 2], 16) / 255.0),
        )[0]
        for index in (1, 3, 5)
    )  # type: ignore[return-value]


def _color_for_bits(bits: Sequence[int]) -> str:
    channels: list[int] = []
    for bit_pattern in bits:
        channel = struct.unpack(
            "<f",
            struct.pack("<I", bit_pattern),
        )[0]
        scaled = struct.unpack(
            "<f",
            struct.pack("<f", channel * 255.0),
        )[0]
        value = math.floor(scaled + 0.5)
        if not 0 <= value <= 255:
            raise OracleError(
                "facegeom-hair-regions-oracle-nif-invalid",
                "A HairTint channel cannot be represented as a canonical byte color.",
            )
        channels.append(value)
    return "#" + "".join(f"{value:02X}" for value in channels)


def _analysis_regions_from_nif(
    document: NifDocument,
) -> list[dict[str, Any]]:
    return [
        {
            "structuralId": region.structural_id,
            "name": region.name,
            "duplicateNameOrdinal": region.duplicate_name_ordinal,
            "shapeBlockType": region.shape_block_type,
            "shapeBlockId": region.shape_block_id,
            "shapeShaderReferenceByteOffset": (
                region.shape_shader_reference_byte_offset
            ),
            "shaderBlockType": "BSLightingShaderProperty",
            "shaderBlockId": region.shader_block_id,
            "sharedShaderGroupId": region.shared_shader_group_id,
            "shaderOwnerStructuralIds": list(
                region.shader_owner_structural_ids
            ),
            "sharedStructuralIds": [
                value
                for value in region.shader_owner_structural_ids
                if value != region.structural_id
            ],
            "textureSetBlockId": region.texture_set_block_id,
            "textureRoutes": list(region.texture_routes),
            "currentColor": _color_for_bits(region.tint_bits),
            "colorFloatBits": list(region.tint_bits),
            "tintByteOffset": region.tint_byte_offset,
            "tintByteLength": 12,
            "defaultRole": "preserve",
        }
        for region in document.hair_regions
    ]


def _observed_file(path: Path, data: bytes) -> dict[str, Any]:
    return {
        "path": str(path),
        "byteLength": len(data),
        "sha256": _raw_sha256(data),
    }


def _add_diagnostic(
    diagnostics: list[Diagnostic],
    code: str,
    message: str,
    severity: str = "error",
) -> None:
    candidate = Diagnostic(code, severity, message)
    if candidate not in diagnostics:
        diagnostics.append(candidate)


def _empty_result(diagnostics: Sequence[Diagnostic]) -> dict[str, Any]:
    return {
        "schema": SCHEMA,
        "verdict": "FAIL",
        "visualAuthority": False,
        "runtimeAuthority": False,
        "documents": {},
        "observed": {
            "source": None,
            "output": None,
            "changedByteOffsets": [],
        },
        "hairTintRegionCount": 0,
        "rawNifFingerprints": {
            "source": {},
            "output": {},
        },
        "productFingerprints": {
            "source": {},
            "output": {},
        },
        "diagnostics": [item.to_json() for item in diagnostics],
    }


def _verify_transaction_with_open_handles(
    *,
    opened_handles: list[int],
    analysis_document: str | Path,
    expected_analysis_sha256: str,
    request_document: str | Path,
    expected_request_sha256: str,
    proposal_document: str | Path,
    expected_proposal_sha256: str,
    manifest_document: str | Path,
    expected_manifest_sha256: str,
    source_path: str | Path,
    output_path: str | Path,
) -> dict[str, Any]:
    diagnostics: list[Diagnostic] = []
    try:
        analysis, analysis_bytes, analysis_file = _read_document(
            analysis_document,
            expected_analysis_sha256,
            "analysis",
            validate_analysis,
            opened_handles,
        )
        request, request_bytes, request_file = _read_document(
            request_document,
            expected_request_sha256,
            "request",
            validate_request,
            opened_handles,
        )
        proposal, proposal_bytes, proposal_file = _read_document(
            proposal_document,
            expected_proposal_sha256,
            "proposal",
            validate_proposal,
            opened_handles,
        )
        manifest, manifest_bytes, manifest_file = _read_document(
            manifest_document,
            expected_manifest_sha256,
            "manifest",
            validate_manifest,
            opened_handles,
        )
        source_file = _read_pinned_file(
            source_path,
            "FaceGeom source",
            opened_handles=opened_handles,
        )
        output_file = _read_pinned_file(
            output_path,
            "FaceGeom output",
            opened_handles=opened_handles,
        )
    except OracleError as exception:
        _add_diagnostic(diagnostics, exception.code, exception.message)
        return _empty_result(diagnostics)

    documents = {
        "analysis": {
            "path": str(analysis_file.final_path),
            "byteLength": len(analysis_bytes),
            "sha256": _raw_sha256(analysis_bytes),
        },
        "request": {
            "path": str(request_file.final_path),
            "byteLength": len(request_bytes),
            "sha256": _raw_sha256(request_bytes),
        },
        "proposal": {
            "path": str(proposal_file.final_path),
            "byteLength": len(proposal_bytes),
            "sha256": _raw_sha256(proposal_bytes),
        },
        "manifest": {
            "path": str(manifest_file.final_path),
            "byteLength": len(manifest_bytes),
            "sha256": _raw_sha256(manifest_bytes),
        },
    }

    if (
        request["analysisSha256"] != _raw_sha256(analysis_bytes)
        or proposal["analysisSha256"] !=
        _raw_sha256(analysis_bytes)
    ):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-analysis-authority-mismatch",
            "The request and proposal do not bind the exact analysis JSON bytes.",
        )
    if proposal["requestSha256"] != _raw_sha256(request_bytes):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-request-proposal-mismatch",
            "The proposal does not bind the exact request JSON bytes.",
        )
    if manifest["proposalSha256"] != _raw_sha256(proposal_bytes):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-proposal-manifest-mismatch",
            "The manifest does not bind the exact proposal JSON bytes.",
        )
    if (
        request["source"] != analysis["source"]
        or proposal["source"] != analysis["source"]
        or manifest["source"] != request["source"]
        or proposal["output"] != request["output"]
        or proposal["manifest"] != request["manifest"]
        or proposal["expectedOutput"]["path"] != request["output"]
        or manifest["output"] != proposal["expectedOutput"]
        or proposal["assignments"] != request["assignments"]
        or proposal["primaryColor"] != request["primaryColor"]
        or proposal["accentColor"] != request["accentColor"]
        or proposal["pluginColorContext"] !=
        analysis["pluginColorContext"]
    ):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-authority-chain-mismatch",
            "The request, proposal, and manifest disagree on an exact transaction authority.",
        )
    if (
        proposal["authorizedEnvelopes"] != manifest["authorizedEnvelopes"]
        or proposal["predictedChangedByteOffsets"] != manifest["changedByteOffsets"]
        or proposal["expectedOutputFingerprints"] != manifest["fingerprints"]
        or proposal["sourceFingerprints"]
        != proposal["expectedOutputFingerprints"]
    ):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-manifest-mismatch",
            "The manifest or proposal invariant surface differs from the promised transaction.",
        )
    if manifest["survivingArtifacts"]:
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-surviving-artifact",
            "The manifest records surviving transaction artifacts.",
        )
    if manifest["runtimeAuthority"]:
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-runtime-authority-invalid",
            "A raw-NIF verification manifest may not claim Skyrim runtime authority.",
        )

    if (
        not _same_canonical_path(
            source_file.final_path,
            Path(request["source"]["path"]),
        )
        or not _same_canonical_path(
            output_file.final_path,
            Path(request["output"]),
        )
        or not _same_canonical_path(
            manifest_file.final_path,
            Path(request["manifest"]),
        )
    ):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-path-authority-mismatch",
            "CLI paths do not exactly identify the request-bound source, output, and manifest.",
        )

    identities = (
        analysis_file.identity,
        request_file.identity,
        proposal_file.identity,
        manifest_file.identity,
        source_file.identity,
        output_file.identity,
    )
    if len(set(identities)) != len(identities):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-same-file",
            "Two transaction authorities resolve to the same stable Windows file identity or hardlink.",
        )
    source_bytes = source_file.data
    output_bytes = output_file.data

    source_observed = _observed_file(
        source_file.final_path,
        source_bytes,
    )
    output_observed = _observed_file(
        output_file.final_path,
        output_bytes,
    )
    if (
        source_observed["byteLength"] != request["source"]["byteLength"]
        or source_observed["sha256"] != request["source"]["sha256"]
    ):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-source-authority-mismatch",
            "The source NIF length or SHA-256 differs from its request authority.",
        )
    if (
        output_observed["byteLength"]
        != proposal["expectedOutput"]["byteLength"]
        or output_observed["sha256"] != proposal["expectedOutput"]["sha256"]
    ):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-output-authority-mismatch",
            "The output NIF length or SHA-256 differs from the proposal and manifest.",
        )
    if len(source_bytes) != len(output_bytes):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-length-changed",
            "The output NIF length differs from the source.",
        )

    try:
        source_nif = parse_nif(source_bytes)
        output_nif = parse_nif(output_bytes)
    except OracleError as exception:
        _add_diagnostic(diagnostics, exception.code, exception.message)
        result = _empty_result(diagnostics)
        result["documents"] = documents
        result["observed"]["source"] = source_observed
        result["observed"]["output"] = output_observed
        return result

    expected_analysis_regions = _analysis_regions_from_nif(
        source_nif
    )
    if analysis["regions"] != expected_analysis_regions:
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-analysis-mismatch",
            "The exact analysis region inventory differs from independently parsed raw-NIF semantics.",
        )
    if (
        analysis["fingerprints"] !=
            source_nif.product_fingerprints
        or proposal["sourceFingerprints"] !=
            source_nif.product_fingerprints
        or proposal["expectedOutputFingerprints"] !=
            output_nif.product_fingerprints
        or manifest["fingerprints"] !=
            output_nif.product_fingerprints
    ):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-product-fingerprint-mismatch",
            "A promised schema-1 fingerprint differs from the independently recomputed normative raw-NIF fingerprint.",
        )

    for name, code in (
        ("topology", "facegeom-hair-regions-oracle-topology-changed"),
        ("geometry", "facegeom-hair-regions-oracle-geometry-changed"),
        ("skinning", "facegeom-hair-regions-oracle-skinning-changed"),
        ("textures", "facegeom-hair-regions-oracle-textures-changed"),
        (
            "shaderOwnership",
            "facegeom-hair-regions-oracle-shader-ownership-changed",
        ),
    ):
        if source_nif.fingerprints[name] != output_nif.fingerprints[name]:
            _add_diagnostic(
                diagnostics,
                code,
                f"The independent raw-NIF {name} fingerprint changed.",
            )

    source_regions = {
        item.structural_id: item for item in source_nif.hair_regions
    }
    output_regions = {
        item.structural_id: item for item in output_nif.hair_regions
    }
    assignments = {
        item["structuralId"]: item["role"] for item in request["assignments"]
    }
    if (
        len(assignments) != len(request["assignments"])
        or set(assignments) != set(source_regions)
        or set(output_regions) != set(source_regions)
    ):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-region-inventory-mismatch",
            "Assignments and independently derived HairTint region inventories differ.",
        )

    roles_by_group: dict[str, set[str]] = {}
    for structural_id, role in assignments.items():
        region = source_regions.get(structural_id)
        if region is not None:
            roles_by_group.setdefault(
                region.shared_shader_group_id,
                set(),
            ).add(role)
    if any(len(roles) != 1 for roles in roles_by_group.values()):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-shared-shader-conflict",
            "Shapes sharing a physical HairTint shader have conflicting roles.",
        )
    physical_roles = {
        next(iter(roles))
        for roles in roles_by_group.values()
        if len(roles) == 1
    }
    if not {"primary", "accent"}.issubset(physical_roles):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-role-coverage-invalid",
            "At least one physical shader group must be Primary and one Accent.",
        )

    envelopes_by_group: dict[str, list[dict[str, Any]]] = {}
    allowed_offsets: set[int] = set()
    for envelope in proposal["authorizedEnvelopes"]:
        envelopes_by_group.setdefault(
            envelope["sharedShaderGroupId"],
            [],
        ).append(envelope)
        start = envelope["byteOffset"]
        if start > len(source_bytes) - 12:
            _add_diagnostic(
                diagnostics,
                "facegeom-hair-regions-oracle-envelope-invalid",
                "An authorized envelope is outside the source NIF.",
            )
            continue
        for offset in range(start, start + 12):
            if offset in allowed_offsets:
                _add_diagnostic(
                    diagnostics,
                    "facegeom-hair-regions-oracle-envelope-invalid",
                    "Authorized 12-byte envelopes overlap.",
                )
            allowed_offsets.add(offset)

    for group, roles in roles_by_group.items():
        if len(roles) != 1:
            continue
        role = next(iter(roles))
        group_regions = sorted(
            (
                region
                for region in source_regions.values()
                if region.shared_shader_group_id == group
            ),
            key=lambda item: item.structural_id,
        )
        group_envelopes = envelopes_by_group.get(group, [])
        if role == "preserve":
            if group_envelopes:
                _add_diagnostic(
                    diagnostics,
                    "facegeom-hair-regions-oracle-envelope-invalid",
                    f"Preserve group {group} has an authorized envelope.",
                )
            for region in group_regions:
                output_region = output_regions.get(region.structural_id)
                if output_region is not None and output_region.tint_bits != region.tint_bits:
                    _add_diagnostic(
                        diagnostics,
                        "facegeom-hair-regions-oracle-preserve-tint-changed",
                        f"Preserve region {region.structural_id} changed its exact tint bits.",
                    )
            continue

        if len(group_envelopes) != 1:
            _add_diagnostic(
                diagnostics,
                "facegeom-hair-regions-oracle-envelope-invalid",
                f"Selected group {group} does not have exactly one envelope.",
            )
            continue
        envelope = group_envelopes[0]
        expected_ids = [item.structural_id for item in group_regions]
        target_bits = _bits_for_color(
            request["primaryColor"] if role == "primary" else request["accentColor"]
        )
        if (
            envelope["role"] != role
            or sorted(envelope["structuralIds"]) != expected_ids
            or not group_regions
            or envelope["byteOffset"] != group_regions[0].tint_byte_offset
            or envelope["oldFloatBits"] != list(group_regions[0].tint_bits)
            or envelope["newFloatBits"] != list(target_bits)
        ):
            _add_diagnostic(
                diagnostics,
                "facegeom-hair-regions-oracle-envelope-invalid",
                f"Selected group {group} envelope differs from raw-NIF ownership or canonical target bits.",
            )
        for region in group_regions:
            output_region = output_regions.get(region.structural_id)
            if output_region is None or output_region.tint_bits != target_bits:
                _add_diagnostic(
                    diagnostics,
                    "facegeom-hair-regions-oracle-selected-tint-mismatch",
                    f"Selected region {region.structural_id} does not contain its canonical target bits.",
                )

    unknown_groups = sorted(set(envelopes_by_group) - set(roles_by_group))
    if unknown_groups:
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-envelope-invalid",
            f"Authorized envelopes name unknown physical shader groups: {unknown_groups}.",
        )

    changed_offsets = [
        index
        for index, (before, after) in enumerate(
            zip(source_bytes, output_bytes, strict=False)
        )
        if before != after
    ]
    if (
        not changed_offsets
        or not proposal["predictedChangedByteOffsets"]
        or not manifest["changedByteOffsets"]
    ):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-no-op",
            "A complete no-op is forbidden; at least one selected physical tint field must change.",
        )
    if changed_offsets != proposal["predictedChangedByteOffsets"]:
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-changed-offsets-mismatch",
            "The actual changed-byte offsets differ from the proposal and manifest.",
        )
    if any(offset not in allowed_offsets for offset in changed_offsets):
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-outside-envelope-changed",
            "At least one output byte outside the authorized tint envelopes changed.",
        )

    for role, pinned in (
        ("analysis document", analysis_file),
        ("request document", request_file),
        ("proposal document", proposal_file),
        ("manifest document", manifest_file),
        ("FaceGeom source", source_file),
        ("FaceGeom output", output_file),
    ):
        try:
            tag = _query_attribute_tag(pinned.handle, role)
            if (
                tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT
                or tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY
                or _query_file_identity(
                    pinned.handle,
                    role,
                ) != pinned.identity
                or _query_file_size(
                    pinned.handle,
                    role,
                ) != len(pinned.data)
                or not _same_canonical_path(
                    _final_path_for_handle(
                        pinned.handle,
                        role,
                    ),
                    pinned.final_path,
                )
            ):
                raise OracleError(
                    "facegeom-hair-regions-oracle-path-invalid",
                    f"{role} changed identity while verification retained its pinned handle.",
                )
        except OracleError as exception:
            _add_diagnostic(
                diagnostics,
                exception.code,
                exception.message,
            )

    if not diagnostics:
        diagnostics.append(
            Diagnostic(
                "facegeom-hair-regions-oracle-pass",
                "info",
                "Independent raw-NIF parsing and whole-file byte accounting passed.",
            )
        )
    verdict = (
        "PASS"
        if all(item.severity != "error" for item in diagnostics)
        else "FAIL"
    )
    return {
        "schema": SCHEMA,
        "verdict": verdict,
        "visualAuthority": False,
        "runtimeAuthority": False,
        "documents": documents,
        "observed": {
            "source": source_observed,
            "output": output_observed,
            "changedByteOffsets": changed_offsets,
        },
        "hairTintRegionCount": len(source_nif.hair_regions),
        "rawNifFingerprints": {
            "source": source_nif.fingerprints,
            "output": output_nif.fingerprints,
        },
        "productFingerprints": {
            "source": source_nif.product_fingerprints,
            "output": output_nif.product_fingerprints,
        },
        "diagnostics": [item.to_json() for item in diagnostics],
    }


def verify_transaction(
    *,
    analysis_document: str | Path,
    expected_analysis_sha256: str,
    request_document: str | Path,
    expected_request_sha256: str,
    proposal_document: str | Path,
    expected_proposal_sha256: str,
    manifest_document: str | Path,
    expected_manifest_sha256: str,
    source_path: str | Path,
    output_path: str | Path,
) -> dict[str, Any]:
    opened_handles: list[int] = []
    result: dict[str, Any] | None = None
    close_failures: list[int] = []
    try:
        result = _verify_transaction_with_open_handles(
            opened_handles=opened_handles,
            analysis_document=analysis_document,
            expected_analysis_sha256=expected_analysis_sha256,
            request_document=request_document,
            expected_request_sha256=expected_request_sha256,
            proposal_document=proposal_document,
            expected_proposal_sha256=expected_proposal_sha256,
            manifest_document=manifest_document,
            expected_manifest_sha256=expected_manifest_sha256,
            source_path=source_path,
            output_path=output_path,
        )
    finally:
        for handle in reversed(opened_handles):
            if not _CLOSE_HANDLE(handle):
                close_failures.append(ctypes.get_last_error())
    if result is None:
        raise AssertionError(
            "The raw-NIF oracle ended without a deterministic result."
        )
    if close_failures:
        diagnostics = [
            Diagnostic(
                item["code"],
                item["severity"],
                item["message"],
            )
            for item in result["diagnostics"]
        ]
        _add_diagnostic(
            diagnostics,
            "facegeom-hair-regions-oracle-handle-close-failed",
            f"Pinned input handles could not all be closed: {close_failures}.",
        )
        result = {
            **result,
            "verdict": "FAIL",
            "diagnostics": [
                item.to_json()
                for item in diagnostics
            ],
        }
    return result


def render_result(result: dict[str, Any]) -> str:
    return (
        json.dumps(
            result,
            ensure_ascii=False,
            sort_keys=True,
            indent=2,
        )
        + "\n"
    )


def write_new_report(path: str | Path, text: str) -> None:
    report = require_new_report_path(path)
    encoded = text.encode("utf-8")
    handle = _CREATE_FILE(
        str(report),
        GENERIC_WRITE | FILE_READ_ATTRIBUTES,
        0,
        None,
        CREATE_NEW,
        FILE_ATTRIBUTE_NORMAL | FILE_FLAG_OPEN_REPARSE_POINT,
        None,
    )
    if handle in (None, INVALID_HANDLE_VALUE):
        code = ctypes.get_last_error()
        if code in (80, 183):
            raise OracleError(
                "facegeom-hair-regions-oracle-report-exists",
                f"The oracle report already exists and will not be overwritten: {report}.",
            )
        raise OracleError(
            "facegeom-hair-regions-oracle-report-write-failed",
            f"The oracle report could not be created: "
            f"{ctypes.FormatError(code).strip()} (Win32 {code}).",
        )
    created = True
    try:
        tag = _query_attribute_tag(int(handle), "oracle report")
        if (
            tag.FileAttributes & FILE_ATTRIBUTE_REPARSE_POINT
            or tag.FileAttributes & FILE_ATTRIBUTE_DIRECTORY
        ):
            raise OracleError(
                "facegeom-hair-regions-oracle-report-write-failed",
                "The newly created oracle report is not an ordinary file.",
            )
        _require_handle_path(int(handle), report, "oracle report")
        offset = 0
        while offset < len(encoded):
            chunk = encoded[offset : offset + 64 * 1024]
            buffer = ctypes.create_string_buffer(chunk)
            written = wintypes.DWORD()
            if not _WRITE_FILE(
                handle,
                buffer,
                len(chunk),
                ctypes.byref(written),
                None,
            ) or written.value != len(chunk):
                raise _windows_failure(
                    "oracle report",
                    "written completely",
                )
            offset += written.value
        if not _FLUSH_FILE_BUFFERS(handle):
            raise _windows_failure(
                "oracle report",
                "flushed durably",
            )
        if _query_file_size(int(handle), "oracle report") != len(encoded):
            raise OracleError(
                "facegeom-hair-regions-oracle-report-write-failed",
                "The created oracle report length differs from its deterministic JSON bytes.",
            )
        _query_file_identity(int(handle), "oracle report")
        created = False
    except OracleError:
        raise
    finally:
        try:
            _close_windows_handle(int(handle))
        finally:
            if created:
                try:
                    os.unlink(report)
                except OSError:
                    pass
    if created:
        raise OracleError(
            "facegeom-hair-regions-oracle-report-write-failed",
            f"The oracle report could not be committed: {report}.",
        )


def parse_arguments(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Independently verify an NPC Manager FaceGeom HairTint transaction."
    )
    parser.add_argument("--analysis", required=True)
    parser.add_argument("--expected-analysis-sha256", required=True)
    parser.add_argument("--request", required=True)
    parser.add_argument("--expected-request-sha256", required=True)
    parser.add_argument("--proposal", required=True)
    parser.add_argument("--expected-proposal-sha256", required=True)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--expected-manifest-sha256", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--output", required=True)
    parser.add_argument("--report")
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    arguments = parse_arguments(argv)
    result = verify_transaction(
        analysis_document=arguments.analysis,
        expected_analysis_sha256=arguments.expected_analysis_sha256,
        request_document=arguments.request,
        expected_request_sha256=arguments.expected_request_sha256,
        proposal_document=arguments.proposal,
        expected_proposal_sha256=arguments.expected_proposal_sha256,
        manifest_document=arguments.manifest,
        expected_manifest_sha256=arguments.expected_manifest_sha256,
        source_path=arguments.source,
        output_path=arguments.output,
    )
    if arguments.report:
        try:
            text = render_result(result)
            write_new_report(arguments.report, text)
        except OracleError as exception:
            diagnostics = [
                Diagnostic(
                    item["code"],
                    item["severity"],
                    item["message"],
                )
                for item in result["diagnostics"]
            ]
            _add_diagnostic(
                diagnostics,
                exception.code,
                exception.message,
            )
            result = {
                **result,
                "verdict": "FAIL",
                "diagnostics": [item.to_json() for item in diagnostics],
            }
    sys.stdout.write(render_result(result))
    return 0 if result["verdict"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
