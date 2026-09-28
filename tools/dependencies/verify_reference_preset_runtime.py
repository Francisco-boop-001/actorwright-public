"""Independently verify the admitted P12-009 native/model runtime closure."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import struct
import zipfile
from pathlib import Path


WORKSPACE = Path(__file__).resolve().parents[2]
PROJECT = WORKSPACE
EXPECTED_ASSETS = (
    (
        "libmediapipe.dll",
        "native-c-api",
        8_825_344,
        "6B4C7503B1324CA5D1D2579DAC8D5E842DE6538A7483E778729E113063ACEC31",
    ),
    (
        "opencv_world3410.dll",
        "opencv-runtime",
        8_406_528,
        "98EAF023A55C1A50904C9EBB6AD20348A1654C394C8359ED95EE878D58C31291",
    ),
    (
        "concrt140.dll",
        "vc-runtime-concurrency",
        374_200,
        "54716F0738AF891F283D213B5C8D11B25896BB8EE3097D301EAE718560CF974E",
    ),
    (
        "msvcp140.dll",
        "vc-runtime-cpp",
        643_512,
        "7C26614E1D733892C2DEAC7E245CE115504B1D80592DD0A01B08E3E5A55F89CA",
    ),
    (
        "vcruntime140.dll",
        "vc-runtime-core",
        178_616,
        "D1F4225DF2CD877DBF130D5668A021DCE3F94118455FF5EC952061C30AFC9CE7",
    ),
    (
        "vcruntime140_1.dll",
        "vc-runtime-core-1",
        50_112,
        "A7146C08F89FE5B04541AB507CDB59FF7B44534D4BA3C668A426C6450A03434E",
    ),
    (
        "blaze_face_short_range.tflite",
        "face-detector-model",
        229_746,
        "B4578F35940BF5A1A655214A1CCE5CAB13EBA73C1297CD78E1A04C2380B0152F",
    ),
    (
        "face_landmarker.task",
        "face-landmarker-model",
        3_758_596,
        "64184E229B263107BC2B804C6625DB1341FF2BB731874B0BCC2FE6544E0BC9FF",
    ),
)
EXPECTED_EXPORTS = (
    "MpErrorFree",
    "MpFaceDetectorClose",
    "MpFaceDetectorCloseResult",
    "MpFaceDetectorCreate",
    "MpFaceDetectorDetectImage",
    "MpFaceLandmarkerClose",
    "MpFaceLandmarkerCloseResult",
    "MpFaceLandmarkerCreate",
    "MpFaceLandmarkerDetectImage",
    "MpImageCreateFromUint8Data",
    "MpImageFree",
)
EXPECTED_NON_OS_IMPORTS = {
    "libmediapipe.dll": {
        "opencv_world3410.dll",
        "msvcp140.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll",
    },
    "opencv_world3410.dll": {
        "concrt140.dll",
        "msvcp140.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll",
    },
    "concrt140.dll": {
        "msvcp140.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll",
    },
    "msvcp140.dll": {"vcruntime140.dll", "vcruntime140_1.dll"},
    "vcruntime140.dll": set(),
    "vcruntime140_1.dll": {"vcruntime140.dll"},
}
EXPECTED_TASK_MEMBERS = (
    "face_detector.tflite",
    "face_landmarks_detector.tflite",
    "geometry_pipeline_metadata_landmarks.binarypb",
    "face_blendshapes.tflite",
)
WINDOWS_IMPORTS = {
    "advapi32.dll",
    "comdlg32.dll",
    "d3d11.dll",
    "dbghelp.dll",
    "gdi32.dll",
    "kernel32.dll",
    "mf.dll",
    "mfplat.dll",
    "mfreadwrite.dll",
    "ole32.dll",
    "oleaut32.dll",
    "shlwapi.dll",
    "user32.dll",
}
UPPER_SHA256 = re.compile(r"^[0-9A-F]{64}$")


def fail(message: str) -> None:
    raise SystemExit(f"RESULT FAIL {message}")


def is_under(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def is_reparse(path: Path) -> bool:
    try:
        attributes = getattr(path.lstat(), "st_file_attributes", 0)
        return path.is_symlink() or bool(attributes & 0x400)
    except OSError:
        return True


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def read_c_string(data: bytes, offset: int, context: str) -> str:
    if offset < 0 or offset >= len(data):
        fail(f"{context} string offset")
    end = data.find(b"\0", offset, min(len(data), offset + 4096))
    if end < 0:
        fail(f"{context} unterminated string")
    try:
        return data[offset:end].decode("ascii")
    except UnicodeDecodeError:
        fail(f"{context} non-ASCII string")


class PeImage:
    def __init__(self, path: Path) -> None:
        self.path = path
        self.data = path.read_bytes()
        if len(self.data) < 0x100 or self.data[:2] != b"MZ":
            fail(f"{path.name} DOS header")
        pe_offset = struct.unpack_from("<I", self.data, 0x3C)[0]
        if (
            pe_offset > len(self.data) - 24
            or self.data[pe_offset : pe_offset + 4] != b"PE\0\0"
        ):
            fail(f"{path.name} PE signature")
        coff = pe_offset + 4
        machine, section_count, _, _, _, optional_size, characteristics = (
            struct.unpack_from("<HHIIIHH", self.data, coff)
        )
        if machine != 0x8664:
            fail(f"{path.name} machine")
        if not characteristics & 0x2000:
            fail(f"{path.name} not DLL")
        optional = coff + 20
        if optional_size < 128 or optional + optional_size > len(self.data):
            fail(f"{path.name} optional header")
        if struct.unpack_from("<H", self.data, optional)[0] != 0x20B:
            fail(f"{path.name} not PE32+")
        directory_count = struct.unpack_from("<I", self.data, optional + 108)[0]
        directories = optional + 112
        if directory_count < 2 or directories + 16 > optional + optional_size:
            fail(f"{path.name} data directories")
        self.export_rva, self.export_size = struct.unpack_from(
            "<II", self.data, directories
        )
        self.import_rva, self.import_size = struct.unpack_from(
            "<II", self.data, directories + 8
        )
        section_table = optional + optional_size
        if section_table + section_count * 40 > len(self.data):
            fail(f"{path.name} section table")
        self.sections: list[tuple[int, int, int, int]] = []
        for index in range(section_count):
            row = section_table + index * 40
            virtual_size, virtual_address, raw_size, raw_offset = (
                struct.unpack_from("<IIII", self.data, row + 8)
            )
            if raw_offset + raw_size > len(self.data):
                fail(f"{path.name} section bounds")
            self.sections.append(
                (virtual_address, max(virtual_size, raw_size), raw_offset, raw_size)
            )

    def rva_offset(self, rva: int, context: str) -> int:
        for virtual, span, raw, raw_size in self.sections:
            if virtual <= rva < virtual + span:
                delta = rva - virtual
                if delta >= raw_size:
                    fail(f"{self.path.name} {context} virtual-only RVA")
                return raw + delta
        fail(f"{self.path.name} {context} unmapped RVA")

    def exports(self) -> tuple[int, tuple[str, ...]]:
        if not self.export_rva or self.export_size < 40:
            return 0, ()
        offset = self.rva_offset(self.export_rva, "export")
        if offset + 40 > len(self.data):
            fail(f"{self.path.name} export directory")
        function_count = struct.unpack_from("<I", self.data, offset + 20)[0]
        name_count = struct.unpack_from("<I", self.data, offset + 24)[0]
        names_rva = struct.unpack_from("<I", self.data, offset + 32)[0]
        names_offset = self.rva_offset(names_rva, "export names")
        if name_count > 65_536 or names_offset + name_count * 4 > len(self.data):
            fail(f"{self.path.name} export names bounds")
        names = tuple(
            read_c_string(
                self.data,
                self.rva_offset(
                    struct.unpack_from("<I", self.data, names_offset + index * 4)[0],
                    "export name",
                ),
                "export",
            )
            for index in range(name_count)
        )
        return function_count, names

    def imports(self) -> tuple[str, ...]:
        if not self.import_rva:
            return ()
        offset = self.rva_offset(self.import_rva, "import")
        imports: list[str] = []
        for index in range(4096):
            row = offset + index * 20
            if row + 20 > len(self.data):
                fail(f"{self.path.name} import bounds")
            descriptor = struct.unpack_from("<IIIII", self.data, row)
            if descriptor == (0, 0, 0, 0, 0):
                return tuple(imports)
            imports.append(
                read_c_string(
                    self.data,
                    self.rva_offset(descriptor[3], "import name"),
                    "import",
                )
            )
        fail(f"{self.path.name} import count")


def is_windows_import(name: str) -> bool:
    lowered = name.casefold()
    return (
        lowered in WINDOWS_IMPORTS
        or lowered.startswith("api-ms-win-")
        or lowered.startswith("ext-ms-win-")
    )


def resolve_runtime_root(runtime_root: Path) -> Path:
    requested = Path(runtime_root)
    if ".." in requested.parts:
        fail("runtime root outside, absent, or reparse")
    candidate = Path(os.path.abspath(requested))
    if not is_under(candidate, WORKSPACE):
        fail("runtime root outside, absent, or reparse")
    current = candidate
    while True:
        if is_reparse(current):
            fail("runtime root outside, absent, or reparse")
        if current == WORKSPACE:
            break
        parent = current.parent
        if parent == current or not is_under(parent, WORKSPACE):
            fail("runtime root outside, absent, or reparse")
        current = parent
    try:
        root = candidate.resolve(strict=True)
    except (OSError, RuntimeError):
        fail("runtime root outside, absent, or reparse")
    if not is_under(root, WORKSPACE) or not root.is_dir():
        fail("runtime root outside, absent, or reparse")
    return root


def verify(runtime_root: Path) -> None:
    root = resolve_runtime_root(runtime_root)
    manifest_path = root / "runtime-asset-manifest.json"
    if not manifest_path.is_file() or is_reparse(manifest_path):
        fail("runtime manifest")
    try:
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        fail(f"runtime manifest JSON: {error}")
    expected_root_keys = {
        "schemaVersion",
        "runtimeArchitecture",
        "offline",
        "cpuOnly",
        "mediaPipeVersion",
        "mediaPipeCommit",
        "mediaPipeBuildTarget",
        "openCvVersion",
        "vcRuntimeVersion",
        "skiaSharpVersion",
        "assets",
    }
    if not isinstance(manifest, dict) or set(manifest) != expected_root_keys:
        fail("runtime manifest fields")
    if (
        manifest["schemaVersion"] != 1
        or manifest["runtimeArchitecture"] != "windows-x64"
        or manifest["offline"] is not True
        or manifest["cpuOnly"] is not True
        or manifest["mediaPipeVersion"] != "0.10.35"
        or manifest["mediaPipeCommit"]
        != "f8ef212d5c962c0e853db7e59d217056b187084b"
        or manifest["mediaPipeBuildTarget"]
        != "//mediapipe/tasks/c:npc_manager_reference_source"
        or manifest["openCvVersion"] != "3.4.10"
        or manifest["vcRuntimeVersion"] != "14.51.36247.0"
        or manifest["skiaSharpVersion"] != "3.119.4"
    ):
        fail("runtime manifest identity")
    assets = manifest["assets"]
    if not isinstance(assets, list) or len(assets) != len(EXPECTED_ASSETS):
        fail("runtime asset count")
    for index, expected in enumerate(EXPECTED_ASSETS):
        row = assets[index]
        path_name, role, length, digest = expected
        if (
            not isinstance(row, dict)
            or set(row) != {"path", "role", "length", "sha256"}
            or (row["path"], row["role"], row["length"], row["sha256"])
            != expected
            or not UPPER_SHA256.fullmatch(row["sha256"])
            or Path(row["path"]).name != row["path"]
        ):
            fail(f"runtime asset row {index}")
        path = root / path_name
        if (
            not path.is_file()
            or is_reparse(path)
            or path.stat().st_size != length
            or sha256(path) != digest
        ):
            fail(f"runtime asset bytes {path_name}")
    actual_files = sorted(
        path.name for path in root.iterdir() if path.is_file()
    )
    expected_files = sorted(
        ["runtime-asset-manifest.json"]
        + [item[0] for item in EXPECTED_ASSETS]
    )
    if actual_files != expected_files:
        fail("runtime undeclared file")

    detector = (root / "blaze_face_short_range.tflite").read_bytes()
    if detector[4:8] != b"TFL3":
        fail("detector model signature")
    try:
        with zipfile.ZipFile(root / "face_landmarker.task", "r") as archive:
            members = tuple(info.filename for info in archive.infolist())
            if (
                members != EXPECTED_TASK_MEMBERS
                or any(
                    info.is_dir()
                    or info.file_size <= 0
                    or info.flag_bits & 0x1
                    or Path(info.filename).name != info.filename
                    for info in archive.infolist()
                )
                or archive.testzip() is not None
            ):
                fail("landmarker task members")
    except (OSError, zipfile.BadZipFile, RuntimeError) as error:
        fail(f"landmarker task archive: {error}")

    media_pipe = PeImage(root / "libmediapipe.dll")
    function_count, exports = media_pipe.exports()
    if function_count != len(EXPECTED_EXPORTS) or exports != EXPECTED_EXPORTS:
        fail("MediaPipe exact export surface")

    import_edges = 0
    for filename, expected_imports in EXPECTED_NON_OS_IMPORTS.items():
        imports = PeImage(root / filename).imports()
        non_os = {
            name.casefold() for name in imports if not is_windows_import(name)
        }
        if non_os != expected_imports:
            fail(
                f"{filename} non-OS imports "
                f"actual={sorted(non_os)} expected={sorted(expected_imports)}"
            )
        import_edges += len(non_os)
    print(
        "RESULT PASS "
        f"assets={len(EXPECTED_ASSETS)} "
        f"exports={len(exports)} "
        f"nonOsImportEdges={import_edges}"
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--runtime-root",
        type=Path,
        default=PROJECT / "runtime" / "reference-preset",
    )
    args = parser.parse_args()
    verify(args.runtime_root)


if __name__ == "__main__":
    main()
