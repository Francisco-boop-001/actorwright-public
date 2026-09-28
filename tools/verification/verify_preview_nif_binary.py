"""Independent parser for the bounded sandbox preview-NIF binary export."""

import argparse
import hashlib
import json
import struct
from pathlib import Path


SHAPE_TYPES = {
    "NiTriShape",
    "NiTriStrips",
    "BSTriShape",
    "BSSubIndexTriShape",
    "BSDynamicTriShape",
    "BSMeshLODTriShape",
    "BSLODTriShape",
    "NiTriBasedGeom",
    "BSSegmentedTriShape",
}


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


class Reader:
    def __init__(self, data: bytes):
        self.data = data
        self.pos = 0

    def take(self, size: int) -> bytes:
        require(size >= 0 and self.pos + size <= len(self.data), "NIF table exceeds file bounds")
        value = self.data[self.pos : self.pos + size]
        self.pos += size
        return value

    def u8(self) -> int:
        return self.take(1)[0]

    def u16(self) -> int:
        return struct.unpack("<H", self.take(2))[0]

    def u32(self) -> int:
        return struct.unpack("<I", self.take(4))[0]

    def sized_string(self) -> str:
        length = self.u32()
        require(length <= 10000, "NIF string length exceeds bound")
        return self.take(length).decode("utf-8", errors="replace")

    def byte_string(self) -> bytes:
        return self.take(self.u8())


def parse_nif(path: Path) -> dict:
    data = path.read_bytes()
    require(len(data) > 64, "NIF is unexpectedly small")
    reader = Reader(data)
    header = reader.take(data.find(b"\n") + 1).decode("utf-8", errors="replace").rstrip("\n")
    require(header == "Gamebryo File Format, Version 20.2.0.7", "unexpected NIF header")
    require(reader.u32() == 0x14020007, "unexpected NIF version")
    require(reader.u8() == 1, "unexpected NIF endian marker")
    user_version = reader.u32()
    block_count = reader.u32()
    user_version_2 = reader.u32()
    require(block_count > 0 and block_count <= 100000, "invalid NIF block count")
    require(user_version_2 >= 3, "NIF stream metadata is missing")
    reader.byte_string()  # author
    reader.byte_string()  # process
    reader.byte_string()  # export
    if user_version_2 >= 130:
        reader.byte_string()  # max file length

    type_count = reader.u16()
    require(0 < type_count <= 4096, "invalid NIF block type count")
    types = [reader.sized_string() for _ in range(type_count)]
    indices = [reader.u16() & 0x7FFF for _ in range(block_count)]
    sizes = [reader.u32() for _ in range(block_count)]
    string_count = reader.u32()
    require(string_count <= 100000, "invalid NIF string count")
    reader.u32()  # maximum string length
    for _ in range(string_count):
        reader.sized_string()
    group_count = reader.u32()
    require(group_count <= block_count, "invalid NIF group count")
    reader.take(group_count * 4)

    block_start = reader.pos
    require(all(index < len(types) for index in indices), "NIF block type index is out of range")
    require(sum(sizes) <= len(data) - block_start, "NIF block sizes exceed file bounds")
    block_end = block_start + sum(sizes)
    reader.pos = block_end
    root_count = reader.u32()
    require(0 < root_count <= block_count, "invalid NIF root count")
    roots = [struct.unpack("<i", reader.take(4))[0] for _ in range(root_count)]
    require(all(0 <= root < block_count for root in roots), "NIF root index is out of range")
    require(reader.pos == len(data), "NIF contains an unexpected trailing region")
    shape_count = sum(types[index] in SHAPE_TYPES for index in indices)
    require(shape_count > 0, "NIF contains no admitted triangle shape block")
    return {
        "bytes": len(data),
        "version": "20.2.0.7",
        "userVersion": user_version,
        "userVersion2": user_version_2,
        "blockCount": block_count,
        "typeCount": type_count,
        "shapeCount": shape_count,
        "blockEnd": block_end,
        "rootCount": root_count,
    }


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--nif", required=True, type=Path)
    parser.add_argument("--scene", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--edition", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()

    response = load(args.response)
    scene = load(args.scene)
    nif = parse_nif(args.nif)
    nif_hash = hashlib.sha256(args.nif.read_bytes()).hexdigest()
    included = [asset for asset in scene["assets"] if asset.get("included", True)]

    require(response["written"] is True, "binary export response is not successful")
    require(response["artifactKind"] == "preview-nif-binary-export", "unexpected binary artifact kind")
    require(response["outputPath"].casefold() == str(args.nif.resolve()).casefold(), "output path is not bound")
    require(response["outputSha256"].casefold() == nif_hash, "output hash is not bound")
    require(response["byteLength"] == nif["bytes"], "output byte length is not bound")
    require(response["meshCount"] == nif["shapeCount"], "mesh count is not bound to parsed shape count")
    require(response["assetCount"] == len(included), "source asset count is not bound")
    require(response["sourceAssets"] == [
        {
            "category": item["category"],
            "path": item["path"],
            "provider": item["provider"],
            "sha256": item["sha256"],
        }
        for item in included
    ], "source asset hashes/providers are not bound")
    require(response["inputSceneSha256"].casefold() == hashlib.sha256(args.scene.read_bytes()).hexdigest(),
            "scene hash is not bound")
    expected_morphs = [
        {"name": item["name"], "value": item["value"]}
        for item in scene.get("morphs", [])
        if item.get("applied", True) and item.get("included", True)
    ]
    require(response.get("morphs", []) == expected_morphs,
            "preview morph selection is not bound")
    dependencies = response.get("morphDependencies", [])
    require(all(item.get("path", "").lower().endswith(".tri") for item in dependencies),
            "morph dependency paths are not TRI files")
    require(len({item.get("path", "").casefold() for item in dependencies}) == len(dependencies),
            "morph dependencies contain duplicates")
    for item in dependencies:
        dependency = Path(item["path"])
        require(dependency.is_file(), "morph dependency is missing")
        require(hashlib.sha256(dependency.read_bytes()).hexdigest().casefold() == item["sha256"].casefold(),
                "morph dependency hash is not bound")
    if expected_morphs:
        require(dependencies, "morphed export did not report TRI dependencies")
        require(response.get("morphDeformed") is True, "morph export did not prove deformation")
        require(response.get("baseVertexSha256") and response.get("bakedVertexSha256"),
                "morph vertex digests are missing")
        require(response["baseVertexSha256"].casefold() != response["bakedVertexSha256"].casefold(),
                "morph vertex digests are identical")
        require(response["importMode"] == "mesh-plus-tri-bake", "unexpected morph import mode")
    else:
        require(response.get("morphDeformed") is False, "neutral export reported deformation")
        require(response.get("baseVertexSha256") is None and response.get("bakedVertexSha256") is None,
                "neutral export unexpectedly reported vertex digests")
        require(response["importMode"] == "mesh-only-import", "unexpected neutral import mode")
    require(all(item["severity"] != "error" for item in response.get("diagnostics", [])),
            "binary export returned an error diagnostic")
    require(response["exporter"] == "Blender/PyNifly 27.4.0", "unexpected exporter identity")
    require(response.get("deformationMode") in {"nif-skinned-evaluated", "nif-mesh-evaluated"},
            "binary export deformation mode is not admitted")
    require(isinstance(response.get("armatureCount", 0), int) and response.get("armatureCount", 0) >= 0,
            "binary export armature count is invalid")
    if expected_morphs:
        require(response["deformationMode"] == "nif-skinned-evaluated" and response["armatureCount"] > 0,
                "morphed FaceGeom export did not preserve the source armature path")
    print(f"PREVIEW NIF BINARY INDEPENDENT PASS nif={args.nif} blocks={nif['blockCount']} shapes={nif['shapeCount']}")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        raise SystemExit(f"PREVIEW NIF BINARY INDEPENDENT FAIL: {error}")
