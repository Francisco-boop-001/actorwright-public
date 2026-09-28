from __future__ import annotations

import ast
import copy
import hashlib
import importlib.util
import json
import os
import struct
import subprocess
import sys
import tempfile
import unittest
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable


HARDENING_ROOT = Path(__file__).resolve().parents[1]
PROJECT_ROOT = Path(__file__).resolve().parents[3]
WORKSPACE_ROOT = PROJECT_ROOT
MODULE_PATH = HARDENING_ROOT / "verify_facegeom_hair_regions_raw_nif.py"
FIXTURE = (
    PROJECT_ROOT
    / "tests"
    / "fixtures"
    / "synthetic-facegeom-hair-regions"
    / "facegeom.nif"
)
EXPECTED_SOURCE_SHA256 = (
    "4b67591a7cf6c2af712305d4e27412eed79c6285b21c9686cd0cb700954ee248"
)
EXPECTED_UNIFORM_SHA256 = (
    "1cadc94ffd7be40c417a6183971f8cf40439a9fcea0844e418972c79b2b83b02"
)
EXPECTED_TWO_TONE_SHA256 = (
    "2f4e12f5ad947040e46e7d1ff729da708ef1b24bbcac52743cbe064dfba85dc8"
)
PINNED_DOTNET = Path(
    os.environ.get(
        "ACTORWRIGHT_TEST_DOTNET",
        str(
            PROJECT_ROOT
            / "artifacts"
            / "tools"
            / "dotnet-sdk-10.0.301"
            / "dotnet.exe",
        ),
    ),
)
CLI_DLL = (
    PROJECT_ROOT
    / "src"
    / "NpcManager.Cli"
    / "bin"
    / "Release"
    / "net10.0"
    / "actorwright.dll"
)

WORK_ROOT = PROJECT_ROOT / ".actorwright" / "work"

def scratch_work_root() -> Path:
    WORK_ROOT.mkdir(parents=True, exist_ok=True)
    return WORK_ROOT

SPEC = importlib.util.spec_from_file_location(
    "verify_facegeom_hair_regions_raw_nif",
    MODULE_PATH,
)
assert SPEC is not None and SPEC.loader is not None, (
    "raw-NIF oracle implementation is missing"
)
ORACLE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = ORACLE
SPEC.loader.exec_module(ORACLE)


REGIONS = (
    ("shape:10:shader:14", "primary", 518_968),
    ("shape:16:shader:20", "primary", 777_960),
    ("shape:22:shader:26", "preserve", 1_036_952),
    ("shape:28:shader:32", "accent", 1_295_944),
    ("shape:34:shader:38", "preserve", 1_554_936),
)

PRODUCT_FINGERPRINTS = {
    "topology": "2a50009951d75d935aa92ef410789494dec965382931849b647e5bcc7ba9bb2e",
    "geometry": "3796ddd5264769c7d97c8993fa138049e3335fd76df51127d03310dda27ab3af",
    "skinning": "ff9d7f37945041f965f75cd6aa211c023301bd1c8e0f60c204b05722879835e6",
    "textures": "410103097623f8091f6a99d2700dbae9df894e692fc1452fd24988629713690b",
    "shaders": "c19dfaf3d64e1dd9f4204abb7fefb72705267b91f53adc0f15c30345ac7b56af",
}


def canonical_json(value: Any) -> bytes:
    return (
        json.dumps(
            value,
            ensure_ascii=False,
            indent=2,
            separators=(",", ": "),
        )
        + "\n"
    ).encode("utf-8")


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def color_bits(color: str) -> list[int]:
    return [
        struct.unpack("<I", struct.pack("<f", int(color[index : index + 2], 16) / 255.0))[0]
        for index in (1, 3, 5)
    ]


def read_bits(value: bytes | bytearray, offset: int) -> list[int]:
    return list(struct.unpack_from("<III", value, offset))


def write_bits(value: bytearray, offset: int, bits: list[int]) -> None:
    struct.pack_into("<III", value, offset, *bits)


@dataclass
class Transaction:
    root: Path
    source_path: Path
    analysis_path: Path
    request_path: Path
    proposal_path: Path
    manifest_path: Path
    output_path: Path
    analysis_sha256: str
    request_sha256: str
    proposal_sha256: str
    manifest_sha256: str
    source_bytes: bytes
    output_bytes: bytes

    def verify(self) -> dict[str, Any]:
        return ORACLE.verify_transaction(
            analysis_document=self.analysis_path,
            expected_analysis_sha256=self.analysis_sha256,
            request_document=self.request_path,
            expected_request_sha256=self.request_sha256,
            proposal_document=self.proposal_path,
            expected_proposal_sha256=self.proposal_sha256,
            manifest_document=self.manifest_path,
            expected_manifest_sha256=self.manifest_sha256,
            source_path=self.source_path,
            output_path=self.output_path,
        )

    def rewrite_output(self, mutate: Callable[[bytearray], None]) -> None:
        changed = bytearray(self.output_bytes)
        mutate(changed)
        self.output_path.write_bytes(changed)


def canonical_color(bits: list[int] | tuple[int, int, int]) -> str:
    channels = []
    for bit_pattern in bits:
        value = struct.unpack("<f", struct.pack("<I", bit_pattern))[0]
        channels.append(int(value * 255.0 + 0.5))
    return "#" + "".join(f"{channel:02X}" for channel in channels)


def analysis_region(region: Any, duplicate_ordinal: int) -> dict[str, Any]:
    return {
        "structuralId": region.structural_id,
        "name": region.name,
        "duplicateNameOrdinal": duplicate_ordinal,
        "shapeBlockType": region.shape_block_type,
        "shapeBlockId": region.shape_block_id,
        "shapeShaderReferenceByteOffset": region.shape_shader_reference_byte_offset,
        "shaderBlockType": "BSLightingShaderProperty",
        "shaderBlockId": region.shader_block_id,
        "sharedShaderGroupId": region.shared_shader_group_id,
        "shaderOwnerStructuralIds": list(region.shader_owner_structural_ids),
        "sharedStructuralIds": [
            value
            for value in region.shader_owner_structural_ids
            if value != region.structural_id
        ],
        "textureSetBlockId": region.texture_set_block_id,
        "textureRoutes": list(region.texture_routes),
        "currentColor": canonical_color(region.tint_bits),
        "colorFloatBits": list(region.tint_bits),
        "tintByteOffset": region.tint_byte_offset,
        "tintByteLength": 12,
        "defaultRole": "preserve",
    }


def build_transaction(
    root: Path,
    *,
    source_path: Path = FIXTURE,
    roles: dict[str, str] | None = None,
    primary: str = "#D6BE83",
    accent: str = "#F4E3B2",
) -> Transaction:
    source_path = source_path.resolve()
    source = source_path.read_bytes()
    if source_path == FIXTURE.resolve() and sha256_bytes(source) != EXPECTED_SOURCE_SHA256:
        raise AssertionError("synthetic FaceGeom fixture drifted")
    parsed_source = ORACLE.parse_nif(source)
    if roles is None:
        roles = {
            structural_id: role
            for structural_id, role, _ in REGIONS
        }
    duplicate_counts: dict[str, int] = {}
    analysis_regions: list[dict[str, Any]] = []
    for region in parsed_source.hair_regions:
        ordinal = duplicate_counts.get(region.name, 0)
        duplicate_counts[region.name] = ordinal + 1
        analysis_regions.append(analysis_region(region, ordinal))

    analysis_path = root / "analysis.json"
    request_path = root / "request.json"
    proposal_path = root / "proposal.json"
    manifest_path = root / "result.manifest.json"
    output_path = root / "result.nif"
    assignments = [
        {
            "structuralId": region.structural_id,
            "role": roles[region.structural_id],
        }
        for region in parsed_source.hair_regions
    ]
    source_authority = {
        "path": str(source_path),
        "byteLength": len(source),
        "sha256": sha256_bytes(source),
    }
    analysis: dict[str, Any] = {
        "schema": "npcmanager-facegeom-hair-regions-analysis/1",
        "source": source_authority,
        "regions": analysis_regions,
        "fingerprints": parsed_source.product_fingerprints,
        "pluginColorContext": None,
    }
    analysis_bytes = canonical_json(analysis)
    analysis_path.write_bytes(analysis_bytes)
    request: dict[str, Any] = {
        "schema": "npcmanager-facegeom-hair-regions-request/1",
        "analysisSha256": sha256_bytes(analysis_bytes),
        "source": source_authority,
        "primaryColor": primary,
        "accentColor": accent,
        "assignments": assignments,
        "output": str(output_path.resolve()),
        "manifest": str(manifest_path.resolve()),
    }
    request_bytes = canonical_json(request)
    request_path.write_bytes(request_bytes)

    output = bytearray(source)
    envelopes: list[dict[str, Any]] = []
    regions_by_group: dict[str, list[Any]] = {}
    for region in parsed_source.hair_regions:
        regions_by_group.setdefault(
            region.shared_shader_group_id,
            [],
        ).append(region)
    for shared_group, group_regions in sorted(regions_by_group.items()):
        group_roles = {
            roles[region.structural_id]
            for region in group_regions
        }
        if len(group_roles) != 1:
            continue
        role = next(iter(group_roles))
        if role == "preserve":
            continue
        target = color_bits(primary if role == "primary" else accent)
        offset = group_regions[0].tint_byte_offset
        old = read_bits(source, offset)
        write_bits(output, offset, target)
        envelopes.append(
            {
                "sharedShaderGroupId": shared_group,
                "structuralIds": sorted(
                    region.structural_id
                    for region in group_regions
                ),
                "role": role,
                "byteOffset": offset,
                "byteLength": 12,
                "oldFloatBits": old,
                "newFloatBits": target,
            }
        )
    output_bytes = bytes(output)
    output_path.write_bytes(output_bytes)
    changed = [
        index
        for index, (before, after) in enumerate(zip(source, output_bytes, strict=True))
        if before != after
    ]
    parsed_output = ORACLE.parse_nif(output_bytes)
    expected_output = {
        "path": str(output_path.resolve()),
        "byteLength": len(output_bytes),
        "sha256": sha256_bytes(output_bytes),
    }
    proposal: dict[str, Any] = {
        "schema": "npcmanager-facegeom-hair-regions-proposal/1",
        "analysisSha256": request["analysisSha256"],
        "requestSha256": sha256_bytes(request_bytes),
        "source": source_authority,
        "output": str(output_path.resolve()),
        "manifest": str(manifest_path.resolve()),
        "primaryColor": primary,
        "accentColor": accent,
        "assignments": assignments,
        "authorizedEnvelopes": envelopes,
        "predictedChangedByteOffsets": changed,
        "sourceFingerprints": parsed_source.product_fingerprints,
        "expectedOutputFingerprints": parsed_output.product_fingerprints,
        "expectedOutput": expected_output,
        "pluginColorContext": None,
    }
    proposal_bytes = canonical_json(proposal)
    proposal_path.write_bytes(proposal_bytes)
    manifest: dict[str, Any] = {
        "schema": "npcmanager-facegeom-hair-regions-manifest/1",
        "proposalSha256": sha256_bytes(proposal_bytes),
        "source": source_authority,
        "output": expected_output,
        "authorizedEnvelopes": envelopes,
        "changedByteOffsets": changed,
        "fingerprints": parsed_output.product_fingerprints,
        "survivingArtifacts": [],
        "runtimeAuthority": False,
    }
    manifest_bytes = canonical_json(manifest)
    manifest_path.write_bytes(manifest_bytes)
    return Transaction(
        root,
        source_path,
        analysis_path,
        request_path,
        proposal_path,
        manifest_path,
        output_path,
        sha256_bytes(analysis_bytes),
        sha256_bytes(request_bytes),
        sha256_bytes(proposal_bytes),
        sha256_bytes(manifest_bytes),
        source,
        output_bytes,
    )


def diagnostic_codes(result: dict[str, Any]) -> set[str]:
    return {item["code"] for item in result["diagnostics"]}


def rewrite_document_chain(
    transaction: Transaction,
    *,
    mutate_analysis: Callable[[dict[str, Any]], None] | None = None,
    mutate_request: Callable[[dict[str, Any]], None] | None = None,
    mutate_proposal: Callable[[dict[str, Any]], None] | None = None,
    mutate_manifest: Callable[[dict[str, Any]], None] | None = None,
    ) -> None:
    analysis = json.loads(transaction.analysis_path.read_bytes())
    request = json.loads(transaction.request_path.read_bytes())
    proposal = json.loads(transaction.proposal_path.read_bytes())
    manifest = json.loads(transaction.manifest_path.read_bytes())
    if mutate_analysis is not None:
        mutate_analysis(analysis)
    analysis_bytes = canonical_json(analysis)
    transaction.analysis_path.write_bytes(analysis_bytes)
    transaction.analysis_sha256 = sha256_bytes(analysis_bytes)
    request["analysisSha256"] = transaction.analysis_sha256
    proposal["analysisSha256"] = transaction.analysis_sha256
    if mutate_request is not None:
        mutate_request(request)
    request_bytes = canonical_json(request)
    transaction.request_path.write_bytes(request_bytes)
    transaction.request_sha256 = sha256_bytes(request_bytes)
    proposal["requestSha256"] = transaction.request_sha256
    if mutate_proposal is not None:
        mutate_proposal(proposal)
    proposal_bytes = canonical_json(proposal)
    transaction.proposal_path.write_bytes(proposal_bytes)
    transaction.proposal_sha256 = sha256_bytes(proposal_bytes)
    manifest["proposalSha256"] = transaction.proposal_sha256
    if mutate_manifest is not None:
        mutate_manifest(manifest)
    manifest_bytes = canonical_json(manifest)
    transaction.manifest_path.write_bytes(manifest_bytes)
    transaction.manifest_sha256 = sha256_bytes(manifest_bytes)


class SyntheticTransactionTests(unittest.TestCase):
    def test_synthetic_product_fingerprints_are_independently_recomputed(
        self,
    ) -> None:
        parsed = ORACLE.parse_nif(FIXTURE.read_bytes())
        self.assertEqual(parsed.product_fingerprints, PRODUCT_FINGERPRINTS)

    def test_synthetic_dual_tone_passes_raw_oracle(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            result = transaction.verify()

            self.assertEqual(result["verdict"], "PASS", result["diagnostics"])
            self.assertEqual(result["hairTintRegionCount"], 5)
            self.assertFalse(result["visualAuthority"])
            self.assertFalse(result["runtimeAuthority"])
            self.assertEqual(
                result["observed"]["source"]["sha256"],
                EXPECTED_SOURCE_SHA256,
            )
            self.assertEqual(
                result["observed"]["output"]["sha256"],
                EXPECTED_TWO_TONE_SHA256,
            )
            self.assertEqual(
                result["documents"]["analysis"]["sha256"],
                transaction.analysis_sha256,
            )
            self.assertEqual(
                result["observed"]["changedByteOffsets"],
                json.loads(transaction.proposal_path.read_text(encoding="utf-8"))[
                    "predictedChangedByteOffsets"
                ],
            )
            for name in (
                "topology",
                "geometry",
                "skinning",
                "textures",
                "shaderOwnership",
            ):
                self.assertEqual(
                    result["rawNifFingerprints"]["source"][name],
                    result["rawNifFingerprints"]["output"][name],
                    name,
                )
            self.assertEqual(FIXTURE.read_bytes(), transaction.source_bytes)

    def test_rejects_product_fingerprint_claim_not_derived_from_raw_nif(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-product-fingerprint-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            rewrite_document_chain(
                transaction,
                mutate_analysis=lambda value: value[
                    "fingerprints"
                ].__setitem__("geometry", "0" * 64),
                mutate_proposal=lambda value: value[
                    "sourceFingerprints"
                ].__setitem__("geometry", "0" * 64),
            )
            result = transaction.verify()
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(
                "facegeom-hair-regions-oracle-product-fingerprint-mismatch",
                diagnostic_codes(result),
            )

    def test_rejects_rehashed_analysis_with_false_region_semantics(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-analysis-semantic-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            rewrite_document_chain(
                transaction,
                mutate_analysis=lambda value: value["regions"][0].__setitem__(
                    "tintByteOffset",
                    value["regions"][0]["tintByteOffset"] + 4,
                ),
            )
            result = transaction.verify()
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(
                "facegeom-hair-regions-oracle-analysis-mismatch",
                diagnostic_codes(result),
            )

    def test_rejects_complete_no_op_but_allows_one_selected_group_already_target(
        self,
    ) -> None:
        roles = {
            structural_id: role
            for structural_id, role, _ in REGIONS
        }
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-noop-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(
                Path(scratch_text).resolve(),
                roles=roles,
                primary="#222222",
                accent="#303030",
            )
            result = transaction.verify()
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(
                "facegeom-hair-regions-oracle-no-op",
                diagnostic_codes(result),
            )
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-partial-noop-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(
                Path(scratch_text).resolve(),
                roles=roles,
                primary="#222222",
                accent="#F4E3B2",
            )
            result = transaction.verify()
            self.assertEqual(result["verdict"], "PASS", result["diagnostics"])

    def test_existing_uniform_synthetic_regression_hash_and_inventory_remain_valid(
        self,
    ) -> None:
        uniform = bytearray(FIXTURE.read_bytes())
        target = color_bits("#D6BE83")
        for offset in (518_968, 777_960, 1_295_944):
            write_bits(uniform, offset, target)
        self.assertEqual(sha256_bytes(uniform), EXPECTED_UNIFORM_SHA256)

        parsed = ORACLE.parse_nif(bytes(uniform))
        by_id = {region.structural_id: region for region in parsed.hair_regions}
        self.assertEqual(len(by_id), 5)
        for structural_id in (
            "shape:10:shader:14",
            "shape:16:shader:20",
            "shape:28:shader:32",
        ):
            self.assertEqual(list(by_id[structural_id].tint_bits), target)
        for structural_id, offset in (
            ("shape:22:shader:26", 1_036_952),
            ("shape:34:shader:38", 1_554_936),
        ):
            self.assertEqual(
                list(by_id[structural_id].tint_bits),
                read_bits(FIXTURE.read_bytes(), offset),
            )


class MutationTests(unittest.TestCase):
    def run_mutation(
        self,
        mutate: Callable[[bytearray], None],
        expected_code: str,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-mutation-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            transaction.rewrite_output(mutate)
            result = transaction.verify()
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(expected_code, diagnostic_codes(result), result["diagnostics"])

    def test_rejects_vertex_mutation(self) -> None:
        self.run_mutation(
            lambda value: value.__setitem__(2_000, value[2_000] ^ 0x01),
            "facegeom-hair-regions-oracle-geometry-changed",
        )

    def test_rejects_skinning_mutation(self) -> None:
        self.run_mutation(
            lambda value: value.__setitem__(62_480, value[62_480] ^ 0x01),
            "facegeom-hair-regions-oracle-skinning-changed",
        )

    def test_rejects_texture_route_mutation(self) -> None:
        def mutate(value: bytearray) -> None:
            offset = value.index(b"FemaleHead.dds")
            value[offset] = ord("x")

        self.run_mutation(
            mutate,
            "facegeom-hair-regions-oracle-textures-changed",
        )

    def test_rejects_topology_mutation(self) -> None:
        def mutate(value: bytearray) -> None:
            struct.pack_into("<i", value, 971, 2)

        self.run_mutation(
            mutate,
            "facegeom-hair-regions-oracle-topology-changed",
        )

    def test_rejects_preserve_tint_mutation(self) -> None:
        self.run_mutation(
            lambda value: value.__setitem__(
                1_554_936,
                value[1_554_936] ^ 0x01,
            ),
            "facegeom-hair-regions-oracle-preserve-tint-changed",
        )

    def test_rejects_selected_tint_mutation(self) -> None:
        self.run_mutation(
            lambda value: value.__setitem__(
                1_295_944,
                value[1_295_944] ^ 0x01,
            ),
            "facegeom-hair-regions-oracle-selected-tint-mismatch",
        )

    def test_rejects_shader_owner_reference_mutation(self) -> None:
        parsed = ORACLE.parse_nif(FIXTURE.read_bytes())
        shader_reference = next(
            reference
            for reference in parsed.parsed_blocks[16].references
            if reference.kind == "shader"
        )
        self.run_mutation(
            lambda value: struct.pack_into(
                "<i",
                value,
                shader_reference.byte_offset,
                14,
            ),
            "facegeom-hair-regions-oracle-shader-ownership-changed",
        )

    def test_rejects_hairtint_shader_changed_to_non_tint(self) -> None:
        parsed = ORACLE.parse_nif(FIXTURE.read_bytes())
        shader = next(
            value for value in parsed.shaders
            if value.block.block_id == 14
        )
        self.run_mutation(
            lambda value: struct.pack_into(
                "<I",
                value,
                shader.block.offset,
                1,
            ),
            "facegeom-hair-regions-oracle-shader-ownership-changed",
        )

    def test_rejects_malformed_and_truncated_nif(self) -> None:
        self.run_mutation(
            lambda value: value.__setitem__(0, ord("X")),
            "facegeom-hair-regions-oracle-nif-invalid",
        )
        self.run_mutation(
            lambda value: value.__delitem__(slice(-17, None)),
            "facegeom-hair-regions-oracle-nif-invalid",
        )

    def test_rejects_nonfinite_hairtint(self) -> None:
        self.run_mutation(
            lambda value: struct.pack_into(
                "<I",
                value,
                1_554_936,
                0x7FC00000,
            ),
            "facegeom-hair-regions-oracle-nif-invalid",
        )

    def test_rejects_wrong_user_version_and_unbounded_block_count(self) -> None:
        source = FIXTURE.read_bytes()
        line_end = source.index(b"\n") + 1
        user_version_offset = line_end + 4 + 1
        block_count_offset = user_version_offset + 4
        self.run_mutation(
            lambda value: struct.pack_into(
                "<I",
                value,
                user_version_offset,
                11,
            ),
            "facegeom-hair-regions-oracle-nif-invalid",
        )
        self.run_mutation(
            lambda value: struct.pack_into(
                "<I",
                value,
                block_count_offset,
                100_001,
            ),
            "facegeom-hair-regions-oracle-nif-invalid",
        )


def replace_block_type_name(
    source: bytes,
    original: str,
    replacement: str,
) -> bytes:
    needle = struct.pack("<I", len(original)) + original.encode("latin-1")
    replacement_bytes = (
        struct.pack("<I", len(replacement))
        + replacement.encode("latin-1")
    )
    if source.count(needle) != 1:
        raise AssertionError(
            f"expected one block-type row for {original}"
        )
    return source.replace(needle, replacement_bytes, 1)


class StructuralVariantTests(unittest.TestCase):
    def test_all_three_supported_shape_types_complete_transactions(self) -> None:
        fixture = FIXTURE.read_bytes()
        for shape_type in (
            "BSDynamicTriShape",
            "BSTriShape",
            "BSSubIndexTriShape",
        ):
            with self.subTest(shape_type=shape_type), tempfile.TemporaryDirectory(
                prefix="raw-hair-oracle-shape-type-",
                dir=scratch_work_root(),
            ) as scratch_text:
                root = Path(scratch_text).resolve()
                source_path = root / "shape-variant.nif"
                source_path.write_bytes(
                    fixture
                    if shape_type == "BSDynamicTriShape"
                    else replace_block_type_name(
                        fixture,
                        "BSDynamicTriShape",
                        shape_type,
                    )
                )
                transaction = build_transaction(
                    root,
                    source_path=source_path,
                )
                result = transaction.verify()
                self.assertEqual(
                    result["verdict"],
                    "PASS",
                    result["diagnostics"],
                )
                parsed = ORACLE.parse_nif(source_path.read_bytes())
                self.assertEqual(
                    {shape.block.block_type for shape in parsed.shapes},
                    {shape_type},
                )

    def test_shared_shader_same_role_uses_one_physical_envelope_and_conflict_fails(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-shared-shader-",
            dir=scratch_work_root(),
        ) as scratch_text:
            root = Path(scratch_text).resolve()
            source = bytearray(FIXTURE.read_bytes())
            original = ORACLE.parse_nif(bytes(source))
            shader_reference = next(
                reference
                for reference in original.parsed_blocks[16].references
                if reference.kind == "shader"
            )
            struct.pack_into(
                "<i",
                source,
                shader_reference.byte_offset,
                14,
            )
            source_path = root / "shared-shader.nif"
            source_path.write_bytes(source)
            roles = {
                "shape:10:shader:14": "primary",
                "shape:22:shader:26": "preserve",
                "shape:34:shader:38": "preserve",
                "shape:16:shader:14": "primary",
                "shape:28:shader:32": "accent",
            }
            transaction = build_transaction(
                root,
                source_path=source_path,
                roles=roles,
            )
            result = transaction.verify()
            self.assertEqual(
                result["verdict"],
                "PASS",
                result["diagnostics"],
            )
            proposal = json.loads(transaction.proposal_path.read_bytes())
            primary_envelopes = [
                value
                for value in proposal["authorizedEnvelopes"]
                if value["sharedShaderGroupId"] == "shader:14"
            ]
            self.assertEqual(len(primary_envelopes), 1)
            self.assertEqual(
                primary_envelopes[0]["structuralIds"],
                [
                    "shape:10:shader:14",
                    "shape:16:shader:14",
                ],
            )

            def conflict(value: dict[str, Any]) -> None:
                next(
                    assignment
                    for assignment in value["assignments"]
                    if assignment["structuralId"]
                    == "shape:16:shader:14"
                )["role"] = "accent"

            rewrite_document_chain(
                transaction,
                mutate_request=conflict,
                mutate_proposal=conflict,
            )
            result = transaction.verify()
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(
                "facegeom-hair-regions-oracle-shared-shader-conflict",
                diagnostic_codes(result),
            )


class StrictDocumentTests(unittest.TestCase):
    def test_oracle_has_no_product_parser_or_writer_dependency(self) -> None:
        source = MODULE_PATH.read_text(encoding="utf-8")
        tree = ast.parse(source, filename=str(MODULE_PATH))
        imported_roots = {
            alias.name.partition(".")[0]
            for node in ast.walk(tree)
            if isinstance(node, ast.Import)
            for alias in node.names
        } | {
            (node.module or "").partition(".")[0]
            for node in ast.walk(tree)
            if isinstance(node, ast.ImportFrom)
        }
        imported_roots.discard("")
        self.assertLessEqual(imported_roots, sys.stdlib_module_names)
        for forbidden in (
            "NpcManager.",
            "FaceGeomHairRegionsSupport",
            "BethesdaFaceGeomHairRegionsVerifier",
            "SseNif",
            "pythonnet",
            "clr.AddReference",
        ):
            with self.subTest(forbidden=forbidden):
                self.assertNotIn(forbidden, source)

    def test_rejects_duplicate_and_unknown_properties_in_all_documents(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-json-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            cases = (
                (transaction.analysis_path, '"schema":', '"schema": "duplicate",\n  "schema":'),
                (transaction.request_path, '"schema":', '"schema": "duplicate",\n  "schema":'),
                (transaction.proposal_path, '"schema":', '"schema": "duplicate",\n  "schema":'),
                (transaction.manifest_path, '"schema":', '"schema": "duplicate",\n  "schema":'),
            )
            for path, needle, replacement in cases:
                with self.subTest(path=path.name, kind="duplicate"):
                    original = path.read_text(encoding="utf-8")
                    changed = original.replace(needle, replacement, 1)
                    path.write_text(changed, encoding="utf-8", newline="\n")
                    result = transaction.verify()
                    self.assertEqual(result["verdict"], "FAIL")
                    self.assertIn(
                        "facegeom-hair-regions-oracle-json-invalid",
                        diagnostic_codes(result),
                    )
                    path.write_text(original, encoding="utf-8", newline="\n")

                with self.subTest(path=path.name, kind="unknown"):
                    value = json.loads(path.read_text(encoding="utf-8"))
                    value["unexpected"] = True
                    path.write_bytes(canonical_json(value))
                    result = transaction.verify()
                    self.assertEqual(result["verdict"], "FAIL")
                    self.assertIn(
                        "facegeom-hair-regions-oracle-json-invalid",
                        diagnostic_codes(result),
                    )
                    path.write_text(original, encoding="utf-8", newline="\n")

    def test_rejects_closed_enum_and_more_than_64_regions(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-budget-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            request = json.loads(transaction.request_path.read_text(encoding="utf-8"))
            request["assignments"][0]["role"] = "PRIMARY"
            transaction.request_path.write_bytes(canonical_json(request))
            result = transaction.verify()
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(
                "facegeom-hair-regions-oracle-json-invalid",
                diagnostic_codes(result),
            )

            request = json.loads(canonical_json({
                **request,
                "assignments": [
                    {"structuralId": f"shape:{index}:shader:{index}", "role": "preserve"}
                    for index in range(65)
                ],
            }))
            transaction.request_path.write_bytes(canonical_json(request))
            result = transaction.verify()
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(
                "facegeom-hair-regions-oracle-json-invalid",
                diagnostic_codes(result),
            )

    def test_rejects_null_required_values_in_all_documents(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-null-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            cases = (
                (transaction.analysis_path, "regions"),
                (transaction.request_path, "source"),
                (transaction.proposal_path, "authorizedEnvelopes"),
                (transaction.manifest_path, "fingerprints"),
            )
            for path, property_name in cases:
                with self.subTest(path=path.name, property=property_name):
                    original = path.read_bytes()
                    value = json.loads(original)
                    value[property_name] = None
                    path.write_bytes(canonical_json(value))
                    result = transaction.verify()
                    self.assertEqual(result["verdict"], "FAIL")
                    self.assertIn(
                        "facegeom-hair-regions-oracle-json-invalid",
                        diagnostic_codes(result),
                    )
                    path.write_bytes(original)


class CliAndPathTests(unittest.TestCase):
    def command(self, transaction: Transaction, report: Path | None = None) -> list[str]:
        command = [
            sys.executable,
            str(MODULE_PATH),
            "--analysis",
            str(transaction.analysis_path),
            "--expected-analysis-sha256",
            transaction.analysis_sha256,
            "--request",
            str(transaction.request_path),
            "--expected-request-sha256",
            transaction.request_sha256,
            "--proposal",
            str(transaction.proposal_path),
            "--expected-proposal-sha256",
            transaction.proposal_sha256,
            "--manifest",
            str(transaction.manifest_path),
            "--expected-manifest-sha256",
            transaction.manifest_sha256,
            "--source",
            str(FIXTURE),
            "--output",
            str(transaction.output_path),
        ]
        if report is not None:
            command.extend(("--report", str(report)))
        return command

    def test_cli_is_deterministic_and_report_is_no_overwrite(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-cli-",
            dir=scratch_work_root(),
        ) as scratch_text:
            root = Path(scratch_text).resolve()
            transaction = build_transaction(root)
            report = root / "oracle-report.json"
            first = subprocess.run(
                self.command(transaction, report),
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(first.returncode, 0, first.stderr)
            self.assertEqual(json.loads(first.stdout)["verdict"], "PASS")
            first_report = report.read_bytes()
            self.assertEqual(first_report, first.stdout.encode("utf-8"))

            deterministic = subprocess.run(
                self.command(transaction),
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(deterministic.returncode, 0, deterministic.stderr)
            self.assertEqual(deterministic.stdout, first.stdout)

            second = subprocess.run(
                self.command(transaction, report),
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(second.returncode, 1)
            self.assertEqual(report.read_bytes(), first_report)
            self.assertIn(
                "facegeom-hair-regions-oracle-report-exists",
                diagnostic_codes(json.loads(second.stdout)),
            )

    def test_cli_rejects_non_k_source_before_read(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-path-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            command = self.command(transaction)
            source_index = command.index("--source") + 1
            command[source_index] = r"F:\ExampleGame\Data\forbidden.nif"
            result = subprocess.run(
                command,
                check=False,
                capture_output=True,
                text=True,
            )
            self.assertEqual(result.returncode, 1)
            self.assertIn(
                "facegeom-hair-regions-oracle-path-invalid",
                diagnostic_codes(json.loads(result.stdout)),
            )

    def test_rejects_source_output_hardlink_identity(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-hardlink-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            transaction.output_path.unlink()
            try:
                os.link(
                    transaction.source_path,
                    transaction.output_path,
                )
            except OSError as exception:
                self.skipTest(
                    f"K-local filesystem hardlinks are unavailable: {exception}"
                )
            result = transaction.verify()
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(
                "facegeom-hair-regions-oracle-same-file",
                diagnostic_codes(result),
            )

    def test_rejects_exact_same_source_output_file_identity(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-same-file-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            result = ORACLE.verify_transaction(
                analysis_document=transaction.analysis_path,
                expected_analysis_sha256=transaction.analysis_sha256,
                request_document=transaction.request_path,
                expected_request_sha256=transaction.request_sha256,
                proposal_document=transaction.proposal_path,
                expected_proposal_sha256=transaction.proposal_sha256,
                manifest_document=transaction.manifest_path,
                expected_manifest_sha256=transaction.manifest_sha256,
                source_path=transaction.source_path,
                output_path=transaction.source_path,
            )
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(
                "facegeom-hair-regions-oracle-same-file",
                diagnostic_codes(result),
            )

    def test_pinned_output_handle_blocks_leaf_replacement_during_verification(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-substitution-race-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            replacement = transaction.root / "replacement.nif"
            replacement.write_bytes(transaction.output_bytes)
            original_parse_nif = ORACLE.parse_nif
            attempted = False
            replacement_failure: OSError | None = None

            def parse_while_replacement_is_attempted(value: bytes) -> Any:
                nonlocal attempted, replacement_failure
                if not attempted:
                    attempted = True
                    try:
                        os.replace(replacement, transaction.output_path)
                    except OSError as exception:
                        replacement_failure = exception
                return original_parse_nif(value)

            ORACLE.parse_nif = parse_while_replacement_is_attempted
            try:
                result = transaction.verify()
            finally:
                ORACLE.parse_nif = original_parse_nif

            self.assertTrue(attempted)
            self.assertIsNotNone(
                replacement_failure,
                "The output leaf was replaceable while the oracle was verifying it.",
            )
            self.assertEqual(result["verdict"], "PASS", result["diagnostics"])
            self.assertEqual(
                sha256_bytes(transaction.output_path.read_bytes()),
                EXPECTED_TWO_TONE_SHA256,
            )

    def test_rejects_real_reparse_leaf(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-reparse-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            transaction.output_path.unlink()
            try:
                os.symlink(
                    transaction.source_path,
                    transaction.output_path,
                )
            except OSError as exception:
                self.skipTest(
                    f"Windows symlink creation is unavailable: {exception}"
                )
            result = transaction.verify()
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(
                "facegeom-hair-regions-oracle-path-invalid",
                diagnostic_codes(result),
            )

    def test_rejects_ads_unc_device_and_traversal_syntax(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-path-syntax-",
            dir=scratch_work_root(),
        ) as scratch_text:
            transaction = build_transaction(Path(scratch_text).resolve())
            values = (
                f"{transaction.source_path}:stream",
                r"\\localhost\K$\ExampleWorkspace\forbidden.nif",
                r"\\?\K:\ExampleWorkspace\forbidden.nif",
                r"K:\ExampleWorkspace\projects\..\forbidden.nif",
            )
            for value in values:
                with self.subTest(value=value):
                    result = ORACLE.verify_transaction(
                        analysis_document=transaction.analysis_path,
                        expected_analysis_sha256=transaction.analysis_sha256,
                        request_document=transaction.request_path,
                        expected_request_sha256=transaction.request_sha256,
                        proposal_document=transaction.proposal_path,
                        expected_proposal_sha256=transaction.proposal_sha256,
                        manifest_document=transaction.manifest_path,
                        expected_manifest_sha256=transaction.manifest_sha256,
                        source_path=value,
                        output_path=transaction.output_path,
                    )
                    self.assertEqual(result["verdict"], "FAIL")
                    self.assertIn(
                        "facegeom-hair-regions-oracle-path-invalid",
                        diagnostic_codes(result),
                    )

    def test_rejects_rehashed_chain_with_embedded_traversal_paths(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-embedded-path-",
            dir=scratch_work_root(),
        ) as scratch_text:
            root = Path(scratch_text).resolve()

            for authority in ("source", "output", "manifest"):
                with self.subTest(authority=authority):
                    transaction_root = root / authority
                    transaction_root.mkdir()
                    transaction = build_transaction(transaction_root)
                    if authority == "source":
                        traversal = (
                            f"{transaction.source_path.parent}"
                            f"\\unused\\..\\{transaction.source_path.name}"
                        )
                        rewrite_document_chain(
                            transaction,
                            mutate_analysis=lambda value: value["source"].update(
                                path=traversal
                            ),
                            mutate_request=lambda value: value["source"].update(
                                path=traversal
                            ),
                            mutate_proposal=lambda value: value["source"].update(
                                path=traversal
                            ),
                            mutate_manifest=lambda value: value["source"].update(
                                path=traversal
                            ),
                        )
                    elif authority == "output":
                        traversal = (
                            f"{transaction.output_path.parent}"
                            f"\\unused\\..\\{transaction.output_path.name}"
                        )

                        def mutate_request(value: dict[str, Any]) -> None:
                            value["output"] = traversal

                        def mutate_proposal(value: dict[str, Any]) -> None:
                            value["output"] = traversal
                            value["expectedOutput"]["path"] = traversal

                        rewrite_document_chain(
                            transaction,
                            mutate_request=mutate_request,
                            mutate_proposal=mutate_proposal,
                            mutate_manifest=lambda value: value["output"].update(
                                path=traversal
                            ),
                        )
                    else:
                        traversal = (
                            f"{transaction.manifest_path.parent}"
                            f"\\unused\\..\\{transaction.manifest_path.name}"
                        )

                        def mutate_request(value: dict[str, Any]) -> None:
                            value["manifest"] = traversal

                        def mutate_proposal(value: dict[str, Any]) -> None:
                            value["manifest"] = traversal

                        rewrite_document_chain(
                            transaction,
                            mutate_request=mutate_request,
                            mutate_proposal=mutate_proposal,
                        )

                    result = transaction.verify()
                    self.assertEqual(result["verdict"], "FAIL")
                    self.assertIn(
                        "facegeom-hair-regions-oracle-path-invalid",
                        diagnostic_codes(result),
                    )

    def test_rejects_file_larger_than_128_mib_before_read(self) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-size-",
            dir=scratch_work_root(),
        ) as scratch_text:
            root = Path(scratch_text).resolve()
            transaction = build_transaction(root)
            oversized = root / "oversized.nif"
            with oversized.open("wb") as stream:
                stream.seek(128 * 1024 * 1024)
                stream.write(b"\0")
            result = ORACLE.verify_transaction(
                analysis_document=transaction.analysis_path,
                expected_analysis_sha256=transaction.analysis_sha256,
                request_document=transaction.request_path,
                expected_request_sha256=transaction.request_sha256,
                proposal_document=transaction.proposal_path,
                expected_proposal_sha256=transaction.proposal_sha256,
                manifest_document=transaction.manifest_path,
                expected_manifest_sha256=transaction.manifest_sha256,
                source_path=oversized,
                output_path=transaction.output_path,
            )
            self.assertEqual(result["verdict"], "FAIL")
            self.assertIn(
                "facegeom-hair-regions-oracle-path-invalid",
                diagnostic_codes(result),
            )


def product_canonical_json(value: Any) -> bytes:
    return json.dumps(
        value,
        ensure_ascii=False,
        indent=2,
        separators=(",", ": "),
    ).replace("\n", "\r\n").encode("utf-8")


class ProductCompatibilityTests(unittest.TestCase):
    def run_product(self, arguments: list[str]) -> dict[str, Any]:
        self.assertTrue(PINNED_DOTNET.is_file(), PINNED_DOTNET)
        self.assertTrue(
            CLI_DLL.is_file(),
            f"Build the Release CLI before this compatibility test: {CLI_DLL}",
        )
        environment = os.environ.copy()
        environment["ACTORWRIGHT_WORKSPACE_ROOT"] = str(PROJECT_ROOT)
        result = subprocess.run(
            [str(PINNED_DOTNET), str(CLI_DLL), *arguments, "--json"],
            cwd=WORKSPACE_ROOT,
            env=environment,
            check=False,
            capture_output=True,
            text=True,
        )
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        payload = json.loads(result.stdout)
        self.assertEqual(payload["phaseVerdict"], "PASS", payload)
        return payload

    def test_product_emitted_analysis_request_proposal_manifest_and_output_pass(
        self,
    ) -> None:
        with tempfile.TemporaryDirectory(
            prefix="raw-hair-oracle-product-",
            dir=scratch_work_root(),
        ) as scratch_text:
            root = Path(scratch_text).resolve()
            analysis_path = root / "analysis.json"
            request_path = root / "request.json"
            proposal_path = root / "proposal.json"
            analyze = self.run_product(
                [
                    "facegen",
                    "hair-regions",
                    "analyze",
                    "--source",
                    str(FIXTURE),
                    "--expected-source-sha256",
                    EXPECTED_SOURCE_SHA256,
                    "--analysis",
                    str(analysis_path),
                    "--assignment-template",
                    str(request_path),
                ]
            )
            artifacts = {
                value["role"]: value
                for value in analyze["artifacts"]
            }
            request = json.loads(request_path.read_bytes())
            request["primaryColor"] = "#D6BE83"
            request["accentColor"] = "#F4E3B2"
            roles = {
                structural_id: role
                for structural_id, role, _ in REGIONS
            }
            for assignment in request["assignments"]:
                assignment["role"] = roles[
                    assignment["structuralId"]
                ]
            request_path.write_bytes(product_canonical_json(request))
            request_sha256 = sha256_bytes(request_path.read_bytes())
            proposal = self.run_product(
                [
                    "facegen",
                    "hair-regions",
                    "propose",
                    "--analysis",
                    str(analysis_path),
                    "--analysis-sha256",
                    artifacts["analysis"]["sha256"],
                    "--request",
                    str(request_path),
                    "--request-sha256",
                    request_sha256,
                    "--proposal",
                    str(proposal_path),
                ]
            )
            proposal_artifact = next(
                value
                for value in proposal["artifacts"]
                if value["role"] == "proposal"
            )
            output_path = Path(request["output"])
            manifest_path = Path(request["manifest"])
            apply_result = self.run_product(
                [
                    "facegen",
                    "hair-regions",
                    "apply",
                    "--request",
                    str(request_path),
                    "--request-sha256",
                    request_sha256,
                    "--proposal",
                    str(proposal_path),
                    "--proposal-sha256",
                    proposal_artifact["sha256"],
                    "--output",
                    str(output_path),
                    "--manifest",
                    str(manifest_path),
                ]
            )
            apply_artifacts = {
                value["role"]: value
                for value in apply_result["artifacts"]
            }
            result = ORACLE.verify_transaction(
                analysis_document=analysis_path,
                expected_analysis_sha256=artifacts["analysis"]["sha256"],
                request_document=request_path,
                expected_request_sha256=request_sha256,
                proposal_document=proposal_path,
                expected_proposal_sha256=proposal_artifact["sha256"],
                manifest_document=manifest_path,
                expected_manifest_sha256=apply_artifacts["manifest"][
                    "sha256"
                ],
                source_path=FIXTURE,
                output_path=output_path,
            )
            self.assertEqual(
                result["verdict"],
                "PASS",
                result["diagnostics"],
            )
            self.assertEqual(
                result["observed"]["output"]["sha256"],
                EXPECTED_TWO_TONE_SHA256,
            )


if __name__ == "__main__":
    unittest.main(verbosity=2)
