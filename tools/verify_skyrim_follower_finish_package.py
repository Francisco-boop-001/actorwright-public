#!/usr/bin/env python3
"""Independent raw verifier for a staged simple-follower finish package."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import os
import re
import stat
import struct
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any, Iterable


ROOT_DOCUMENTS = frozenset(
    {
        "BUILD_INFO.txt",
        "README-NPCMANAGER-RUNTIME-TEST.txt",
        "RUNTIME-TEST-INSTRUCTIONS.md",
    }
)
EVIDENCE_FILES = frozenset(
    {
        "evidence/follower-finish-request.json",
        "evidence/follower-finish-proposal.json",
        "evidence/follower-finish-verification.json",
        "evidence/source-package-binding.json",
        "evidence/world-conflict-audit.json",
        "evidence/runtime-test-instructions.json",
    }
)
FORBIDDEN_SIGNATURES = frozenset(
    {
        "WRLD",
        "LAND",
        "WATR",
        "LTEX",
        "NAVM",
        "NAVI",
        "LCTN",
        "REGN",
        "CLMT",
        "MUSC",
        "IMGS",
    }
)
MAX_JSON_BYTES = 4 * 1024 * 1024
MAX_ENTRY_BYTES = 512 * 1024 * 1024
MAX_ENTRIES = 10_000
MAX_TARGET_RECORD_GROUP_DEPTH = 32
WORKSPACE_ROOT = Path(__file__).resolve().parents[1]
PROTECTED_ROOT = Path(r"F:\ExampleGame")
SHA256_TEXT = re.compile(r"^[0-9A-Fa-f]{64}$")


class VerificationError(Exception):
    """An input could not be safely inspected."""


class DuplicateKeyError(VerificationError):
    """A JSON object contained a duplicate key."""


def is_exact_exterior_cell_body(
    data: bytes,
    cell_grid_x: int,
    cell_grid_y: int,
) -> bool:
    payload = struct.pack("<iiI", cell_grid_x, cell_grid_y, 0)
    return data == b"XCLC" + struct.pack("<H", len(payload)) + payload


def normalize_bound_json(value: Any) -> Any:
    """Normalize only case-insensitive SHA-256 text in typed JSON."""
    if isinstance(value, dict):
        return {
            key: normalize_bound_json(item)
            for key, item in value.items()
        }
    if isinstance(value, list):
        return [normalize_bound_json(item) for item in value]
    if isinstance(value, str) and SHA256_TEXT.fullmatch(value):
        return value.lower()
    return value


@dataclass(frozen=True)
class RawRecord:
    signature: str
    form_id: int
    flags: int
    data: bytes
    raw: bytes
    groups: tuple[tuple[int, bytes], ...]


@dataclass(frozen=True)
class RawPlugin:
    records: tuple[RawRecord, ...]

    def by_form(self) -> dict[int, RawRecord]:
        result: dict[int, RawRecord] = {}
        for record in self.records:
            if record.form_id in result:
                raise VerificationError(
                    f"duplicate FormID 0x{record.form_id:08X}"
                )
            result[record.form_id] = record
        return result

    def records_of(self, signature: str) -> list[RawRecord]:
        return [
            record
            for record in self.records
            if record.signature == signature
        ]


class Verifier:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.diagnostics: list[dict[str, str]] = []
        self.plugin_report: dict[str, Any] = {
            "recordCounts": {"PACK": 0, "REFR": 0, "ACHR": 0}
        }

    def error(self, code: str, message: str) -> None:
        self.diagnostics.append({"code": code, "message": message})

    def verify(self) -> dict[str, Any]:
        try:
            request_bytes = read_bounded(self.args.request, MAX_JSON_BYTES)
            proposal_bytes = read_bounded(self.args.proposal, MAX_JSON_BYTES)
            request = load_json_bytes(request_bytes)
            proposal = load_json_bytes(proposal_bytes)
            require_object(request, "request")
            require_object(proposal, "proposal")
            self._verify_declared_path_authority(request)
            if self.diagnostics:
                raise VerificationError(
                    "Declared path authority was rejected before source inspection."
                )
            self._verify_request_proposal(
                request, request_bytes, proposal, proposal_bytes
            )

            source_entries, source_manifest = self._verify_source(request)
            package_entries, manifest = self._verify_package(request)
            self._verify_source_preservation(
                request, source_entries, package_entries
            )
            self._verify_evidence(
                request, request_bytes, proposal, proposal_bytes, package_entries
            )
            self._verify_manifest(
                request, request_bytes, manifest, package_entries
            )
            self._verify_archive(request, package_entries)
            self._verify_plugin(request, source_entries, package_entries)

            # Retain the parsed value so the source-manifest read cannot be
            # optimized into a mere path/hash check.
            if source_manifest.get("outputPlugin") != request["source"]["plugin"]:
                self.error(
                    "source-manifest-plugin",
                    "The source manifest output plugin differs from the request.",
                )
        except (
            VerificationError,
            OSError,
            ValueError,
            KeyError,
            TypeError,
            struct.error,
            zipfile.BadZipFile,
        ) as exception:
            self.error("verification-input", str(exception))

        passed = not self.diagnostics
        return {
            "schemaVersion": 1,
            "artifactKind": "skyrim-follower-finish-independent-verification",
            "verdict": (
                "STATIC_PASS_RUNTIME_REQUIRED"
                if passed
                else "FAIL"
            ),
            "runtimeAuthority": False,
            "plugin": self.plugin_report,
            "diagnostics": self.diagnostics,
        }

    def _verify_declared_path_authority(
        self, request: dict[str, Any]
    ) -> None:
        source = require_object(request.get("source"), "request.source")
        declared = [
            Path(require_string(source, "zip")),
            Path(require_string(source, "packageManifest")),
            Path(require_string(request, "outputRoot")),
            Path(require_string(request, "outputZip")),
        ]
        external = request.get("externalAuthorities")
        if isinstance(external, dict):
            placement = external.get("placementEvidence")
            if isinstance(placement, dict) and isinstance(
                placement.get("path"), str
            ):
                declared.append(Path(placement["path"]))
            providers = external.get("providers")
            if isinstance(providers, list):
                for provider in providers:
                    if isinstance(provider, dict) and isinstance(
                        provider.get("path"), str
                    ):
                        declared.append(Path(provider["path"]))
        for path in declared:
            if not is_under_path(
                path, WORKSPACE_ROOT
            ) or is_under_path(path, PROTECTED_ROOT):
                self.error(
                    "path-authority",
                    f"Declared path {str(path)!r} is outside K-only workspace authority.",
                )
                continue
            reparse = first_reparse_component(path)
            if reparse is not None:
                self.error(
                    "path-reparse",
                    f"Declared path {str(path)!r} crosses reparse point {str(reparse)!r}.",
                )

    def _verify_request_proposal(
        self,
        request: dict[str, Any],
        request_bytes: bytes,
        proposal: dict[str, Any],
        proposal_bytes: bytes,
    ) -> None:
        del proposal_bytes
        if proposal.get("requestSha256", "").upper() != sha256(request_bytes):
            self.error(
                "proposal-request-hash",
                "The proposal request SHA-256 does not bind the supplied request bytes.",
            )
        if normalize_bound_json(proposal.get("request")) != (
            normalize_bound_json(request)
        ):
            self.error(
                "proposal-request-binding",
                "The proposal embeds a different request.",
            )
        mappings = (
            ("existingRecordChanges", "allowedExistingRecordChanges"),
            ("newRecords", "allowedNewRecords"),
            ("allowedPackageFiles", "allowedPackageFiles"),
        )
        for proposal_key, request_key in mappings:
            if proposal.get(proposal_key) != request.get(request_key):
                self.error(
                    "proposal-surface",
                    f"The proposal {proposal_key} differs from the request.",
                )
        if proposal.get("nextFormId") != request["allocation"]["nextFormId"]:
            self.error(
                "proposal-next-form-id",
                "The proposal next FormID differs from the request.",
            )
        if proposal.get("runtimeAuthority") is not False:
            self.error(
                "runtime-authority",
                "Static follower-finish evidence may not claim runtime authority.",
            )

    def _verify_source(
        self, request: dict[str, Any]
    ) -> tuple[dict[str, bytes], dict[str, Any]]:
        source = require_object(request.get("source"), "request.source")
        requested_zip = Path(str(source["zip"])).resolve()
        if requested_zip != self.args.source_zip.resolve():
            self.error(
                "source-zip-binding",
                "The supplied source ZIP path differs from the request.",
            )
        zip_bytes = read_bounded(self.args.source_zip, 2 * 1024**3)
        if len(zip_bytes) != require_int(source, "zipByteLength"):
            self.error("source-zip-length", "The source ZIP length changed.")
        if sha256(zip_bytes) != str(source["zipSha256"]).upper():
            self.error("source-zip-hash", "The source ZIP hash changed.")

        manifest_path = Path(str(source["packageManifest"]))
        manifest_bytes = read_bounded(manifest_path, MAX_JSON_BYTES)
        if sha256(manifest_bytes) != str(
            source["packageManifestSha256"]
        ).upper():
            self.error(
                "source-manifest-hash",
                "The source package manifest hash changed.",
            )
        source_manifest = load_json_bytes(manifest_bytes)
        require_object(source_manifest, "source manifest")
        rows = require_list(source_manifest, "artifacts")
        declared: dict[str, dict[str, Any]] = {}
        seen_manifest_paths: set[str] = set()
        for row_value in rows:
            row = require_object(row_value, "source artifact")
            relative = require_string(row, "relativePath")
            if not safe_posix_path(relative):
                self.error(
                    "source-manifest-path",
                    f"Source artifact {relative!r} is unsafe.",
                )
                continue
            relative_folded = relative.casefold()
            if relative_folded in seen_manifest_paths:
                self.error(
                    "source-manifest-path",
                    f"Source artifact {relative!r} is duplicated or case-aliased.",
                )
                continue
            seen_manifest_paths.add(relative_folded)
            if relative.startswith("evidence/"):
                # Manager manifests retain build evidence outside the
                # install archive. It remains hash-bound by the manifest but
                # is not a runtime ZIP member.
                continue
            if not relative.startswith("Data/"):
                self.error(
                    "source-manifest-path",
                    f"Source artifact {relative!r} is outside Data/ or evidence/.",
                )
                continue
            zipped = relative[5:]
            folded = zipped.casefold()
            declared[folded] = row

        entries = read_zip_bytes(
            zip_bytes, self, "source-zip-path"
        )
        expected = set(declared) | {
            path.casefold() for path in ROOT_DOCUMENTS
        }
        if set(entries) != expected:
            self.error(
                "source-zip-closure",
                "The source ZIP differs from manifest payload plus required root documents.",
            )
        for folded, row in declared.items():
            if folded not in entries:
                continue
            name, data = entries[folded]
            if len(data) != require_int(row, "byteLength") or sha256(
                data
            ) != require_string(row, "sha256").upper():
                self.error(
                    "source-zip-artifact",
                    f"Source ZIP artifact {name!r} failed its manifest binding.",
                )

        plugin_name = require_string(source, "plugin")
        plugin = entries.get(plugin_name.casefold())
        if plugin is None or sha256(plugin[1]) != str(
            source["pluginSha256"]
        ).upper():
            self.error(
                "source-plugin-hash",
                "The source plugin failed its request hash binding.",
            )
        self._verify_source_sidecar_bindings(
            request, source, declared, entries
        )
        return {name: data for name, data in entries.values()}, source_manifest

    def _verify_source_sidecar_bindings(
        self,
        request: dict[str, Any],
        source: dict[str, Any],
        declared: dict[str, dict[str, Any]],
        entries: dict[str, tuple[str, bytes]],
    ) -> None:
        for kind, request_key, code in (
            ("facegeom", "faceGeomSha256", "source-facegeom-binding"),
            ("facetint", "faceTintSha256", "source-facetint-binding"),
        ):
            rows = [
                row
                for row in declared.values()
                if str(row.get("kind", "")).casefold() == kind
            ]
            expected_hash = str(source.get(request_key, "")).upper()
            plugin = require_string(source, "plugin")
            local_form = parse_form_id(
                request["npcFormId"]
            )
            branch = "meshes" if kind == "facegeom" else "textures"
            leaf = "FaceGeom" if kind == "facegeom" else "FaceTint"
            extension = "nif" if kind == "facegeom" else "dds"
            canonical_path = (
                f"Data/{branch}/actors/character/FaceGenData/"
                f"{leaf}/{plugin}/{local_form:08X}.{extension}"
            )
            if (
                len(rows) != 1
                or require_string(rows[0], "sha256").upper()
                != expected_hash
                or require_string(
                    rows[0], "relativePath"
                ).casefold()
                != canonical_path.casefold()
            ):
                self.error(
                    code,
                    f"The source {kind} manifest row does not bind the request hash.",
                )
                continue
            relative = require_string(rows[0], "relativePath")[5:]
            entry = entries.get(relative.casefold())
            if entry is None or sha256(entry[1]) != expected_hash:
                self.error(
                    code,
                    f"The source {kind} ZIP payload does not bind the request hash.",
                )

    def _verify_package(
        self, request: dict[str, Any]
    ) -> tuple[dict[str, bytes], dict[str, Any]]:
        requested_root = Path(str(request["outputRoot"])).resolve()
        if requested_root != self.args.package_root.resolve():
            self.error(
                "package-root-binding",
                "The supplied package root differs from the request.",
            )
        package_entries = read_tree(self.args.package_root)
        manifest_data = package_entries.get("npcmanager-package.json")
        if manifest_data is None:
            raise VerificationError("The package manifest is missing.")
        manifest = load_json_bytes(manifest_data)
        require_object(manifest, "package manifest")
        return package_entries, manifest

    def _verify_source_preservation(
        self,
        request: dict[str, Any],
        source_entries: dict[str, bytes],
        package_entries: dict[str, bytes],
    ) -> None:
        plugin_name = str(request["source"]["plugin"])
        diagnostic_name = (
            "diag-" + Path(plugin_name).stem.lower() + ".txt"
        )
        expected = {
            (
                name
                if name in ROOT_DOCUMENTS
                else "Data/" + name
            )
            for name in source_entries
        }
        expected |= EVIDENCE_FILES | {"npcmanager-package.json"}
        expected.add("Data/" + diagnostic_name)
        if {path.casefold() for path in expected} != {
            path.casefold() for path in package_entries
        }:
            self.error(
                "package-closure",
                "The package file closure widened or dropped an admitted path.",
            )
        for name, data in source_entries.items():
            if name.casefold() in {
                plugin_name.casefold(),
                diagnostic_name.casefold(),
            }:
                continue
            mapped = name if name in ROOT_DOCUMENTS else "Data/" + name
            actual = find_casefold(package_entries, mapped)
            if actual is None or actual != data:
                self.error(
                    "source-preservation",
                    f"Source-preserved payload {name!r} changed.",
                )
        diagnostic = find_casefold(
            package_entries, "Data/" + diagnostic_name
        )
        if diagnostic != expected_diagnostic_bytes(request):
            self.error(
                "diagnostic-binding",
                "The runtime diagnostic is not the exact request-derived generic batch.",
            )

    def _verify_evidence(
        self,
        request: dict[str, Any],
        request_bytes: bytes,
        proposal: dict[str, Any],
        proposal_bytes: bytes,
        package_entries: dict[str, bytes],
    ) -> None:
        request_evidence = package_entries.get(
            "evidence/follower-finish-request.json"
        )
        proposal_evidence = package_entries.get(
            "evidence/follower-finish-proposal.json"
        )
        request_evidence_value = (
            None
            if request_evidence is None
            else load_json_bytes(request_evidence)
        )
        if (
            request_evidence_value is None
            or normalize_bound_json(request_evidence_value)
            != normalize_bound_json(request)
        ):
            self.error(
                "request-evidence",
                "The canonical request evidence is not structurally identical.",
            )
        if proposal_evidence != proposal_bytes:
            self.error(
                "proposal-evidence",
                "The proposal evidence is not byte-identical.",
            )

        for path in EVIDENCE_FILES:
            data = package_entries.get(path)
            if data is None:
                self.error(
                    "evidence-missing", f"Evidence file {path!r} is missing."
                )
                continue
            value = load_json_bytes(data)
            require_object(value, path)
            if value.get("runtimeAuthority") is True:
                self.error(
                    "runtime-authority",
                    f"Evidence file {path!r} claims runtime authority.",
                )

        verification = load_json_bytes(
            package_entries["evidence/follower-finish-verification.json"]
        )
        output_hash = verification.get(
            "outputPluginSha256", verification.get("outputSha256")
        )
        plugin_path = "Data/" + str(request["source"]["plugin"])
        if output_hash is None or str(output_hash).upper() != sha256(
            package_entries[plugin_path]
        ):
            self.error(
                "verification-plugin-hash",
                "Plugin verification evidence does not bind the output plugin.",
            )
        if proposal.get("runtimeAuthority") is not False:
            self.error(
                "runtime-authority",
                "The proposal claims runtime authority.",
            )
        source_binding = load_json_bytes(
            package_entries["evidence/source-package-binding.json"]
        )
        expected_changed = [
            "Data/" + str(request["source"]["plugin"]),
            "Data/diag-"
            + Path(str(request["source"]["plugin"])).stem.lower()
            + ".txt",
        ]
        if source_binding.get("allowedChangedPaths") != expected_changed:
            self.error(
                "source-binding-change-surface",
                "Source binding evidence does not declare the exact plugin and diagnostic change surface.",
            )

    def _verify_manifest(
        self,
        request: dict[str, Any],
        request_bytes: bytes,
        manifest: dict[str, Any],
        package_entries: dict[str, bytes],
    ) -> None:
        if manifest.get("schemaVersion") != 1:
            self.error(
                "manifest-schema", "The package manifest schema is unsupported."
            )
        if manifest.get("outputPlugin") != request["source"]["plugin"]:
            self.error(
                "manifest-plugin",
                "The package manifest output plugin differs from the request.",
            )
        if manifest.get("targetFormId") != request["npcFormId"]:
            self.error(
                "manifest-target",
                "The package manifest target FormID differs from the request.",
            )
        request_evidence = package_entries.get(
            "evidence/follower-finish-request.json"
        )
        if (
            request_evidence is None
            or str(manifest.get("sourcePresetSha256", "")).upper()
            != sha256(request_evidence)
        ):
            self.error(
                "manifest-request-hash",
                "The package manifest does not bind request evidence.",
            )
        rows = require_list(manifest, "artifacts")
        declared: set[str] = {"npcmanager-package.json"}
        for row_value in rows:
            row = require_object(row_value, "package artifact")
            relative = require_string(row, "relativePath")
            if not safe_posix_path(relative):
                self.error(
                    "manifest-artifact-path",
                    f"Manifest artifact {relative!r} is unsafe.",
                )
                continue
            folded = relative.casefold()
            if folded in {path.casefold() for path in declared}:
                self.error(
                    "manifest-artifact-path",
                    f"Manifest artifact {relative!r} is duplicated.",
                )
                continue
            declared.add(relative)
            data = find_casefold(package_entries, relative)
            if data is None or len(data) != require_int(
                row, "byteLength"
            ) or sha256(data) != require_string(row, "sha256").upper():
                self.error(
                    "manifest-artifact-hash",
                    f"Manifest artifact {relative!r} failed size/hash verification.",
                )
        if {path.casefold() for path in declared} != {
            path.casefold() for path in package_entries
        }:
            self.error(
                "manifest-closure",
                "The package contains undeclared or missing files.",
            )

    def _verify_archive(
        self, request: dict[str, Any], package_entries: dict[str, bytes]
    ) -> None:
        requested_archive = Path(str(request["outputZip"])).resolve()
        if requested_archive != self.args.archive.resolve():
            self.error(
                "archive-binding",
                "The supplied archive differs from the request.",
            )
        archive_bytes = read_bounded(
            self.args.archive, 2 * 1024**3
        )
        archive = read_zip_bytes(
            archive_bytes, self, "archive-path"
        )
        expected: dict[str, bytes] = {}
        for relative, data in package_entries.items():
            if relative.startswith("Data/"):
                expected[relative[5:]] = data
            elif relative in ROOT_DOCUMENTS:
                expected[relative] = data
        if set(archive) != {path.casefold() for path in expected}:
            self.error(
                "archive-closure",
                "The no-wrapper archive path closure differs from the package payload.",
            )
        for name, data in expected.items():
            actual = archive.get(name.casefold())
            if actual is None or actual[1] != data:
                self.error(
                    "archive-hash",
                    f"Archive entry {name!r} differs from its package file.",
                )

    def _verify_plugin(
        self,
        request: dict[str, Any],
        source_entries: dict[str, bytes],
        package_entries: dict[str, bytes],
    ) -> None:
        plugin_name = str(request["source"]["plugin"])
        source_data = find_casefold(source_entries, plugin_name)
        output_data = find_casefold(package_entries, "Data/" + plugin_name)
        if source_data is None or output_data is None:
            self.error("plugin-missing", "Source or output plugin is missing.")
            return
        try:
            source = parse_plugin(source_data)
            output = parse_plugin(output_data)
            source_by_form = source.by_form()
            output_by_form = output.by_form()
        except VerificationError as exception:
            self.error("plugin-raw-parse", str(exception))
            return

        counts = {
            signature: len(output.records_of(signature))
            for signature in ("PACK", "REFR", "ACHR")
        }
        self.plugin_report["recordCounts"] = counts
        if counts != {"PACK": 1, "REFR": 1, "ACHR": 1}:
            self.error(
                "plugin-record-count",
                "The plugin must add exactly one PACK, REFR, and ACHR.",
            )

        tes4_records = output.records_of("TES4")
        if len(tes4_records) != 1:
            self.error("plugin-tes4", "The plugin must contain one TES4 record.")
            return
        tes4 = tes4_records[0]
        masters = [
            data.rstrip(b"\0").decode("utf-8", "strict")
            for data in all_subrecords(tes4, "MAST")
        ]
        master_count = len(masters)
        target_local = parse_form_id(request["npcFormId"])
        self_prefix = (
            0
            if target_local in output_by_form
            else master_count << 24
        )

        def self_form(value: Any) -> int:
            local = value if isinstance(value, int) else parse_form_id(value)
            if local > 0x00FFFFFF or master_count > 0xFF:
                raise VerificationError(
                    "A local FormID cannot be encoded through the master table."
                )
            return self_prefix | local

        def external_form(value: Any) -> int:
            plugin, local = parse_reference(value)
            matches = [
                index
                for index, master in enumerate(masters)
                if master.casefold() == plugin.casefold()
            ]
            if len(matches) != 1 or local > 0x00FFFFFF:
                raise VerificationError(
                    f"FormReference {value!r} is absent or ambiguous in the master table."
                )
            return (matches[0] << 24) | local

        allocation = require_object(request["allocation"], "allocation")
        expected_new = {
            self_form(allocation["package"]): "PACK",
            self_form(allocation["anchor"]): "REFR",
            self_form(allocation["actor"]): "ACHR",
        }
        placement = require_object(request["placement"], "placement")
        actor_transform = require_object(
            placement["actor"], "placement.actor"
        )
        anchor_transform = require_object(
            placement["anchor"], "placement.anchor"
        )
        actor_grid = (
            math.floor(float(actor_transform["x"]) / 4096.0),
            math.floor(float(actor_transform["y"]) / 4096.0),
        )
        anchor_grid = (
            math.floor(float(anchor_transform["x"]) / 4096.0),
            math.floor(float(anchor_transform["y"]) / 4096.0),
        )
        if actor_grid != anchor_grid:
            self.error(
                "plugin-placement-cell",
                "Actor and anchor transforms do not resolve to one exterior cell.",
            )
        cell_grid_x, cell_grid_y = actor_grid
        block_label = struct.pack(
            "<hh", cell_grid_y // 32, cell_grid_x // 32
        )
        subblock_label = struct.pack(
            "<hh", cell_grid_y // 8, cell_grid_x // 8
        )
        world_id = external_form(placement["worldspace"])
        cell_id = external_form(placement["cell"])
        expected_cell_groups = (
            (0, b"WRLD"),
            (1, struct.pack("<I", world_id)),
            (4, block_label),
            (5, subblock_label),
        )
        structural_parents = {cell_id: "CELL"}
        source_ids = set(source_by_form)
        output_ids = set(output_by_form)
        if output_ids - source_ids != (
            set(expected_new) | set(structural_parents)
        ) or source_ids - output_ids:
            self.error(
                "plugin-record-count",
                "The output record inventory differs from fixed allocation.",
            )
        for form_id, signature in expected_new.items():
            record = output_by_form.get(form_id)
            if record is None or record.signature != signature:
                self.error(
                    "plugin-record-count",
                    f"Allocated {signature} 0x{form_id:08X} is missing.",
                )
        for form_id, signature in structural_parents.items():
            record = output_by_form.get(form_id)
            if record is None or record.signature != signature:
                self.error(
                    "plugin-record-count",
                    f"Structural {signature} 0x{form_id:08X} is missing.",
                )
                continue
            if (
                not is_exact_exterior_cell_body(
                    record.data,
                    cell_grid_x,
                    cell_grid_y,
                )
                or record.flags != 0
                or len(record.raw) < 24
                or struct.unpack_from("<H", record.raw, 20)[0] != 44
            ):
                self.error(
                    "plugin-cell-payload",
                    "Structural CELL is not the exact flags-0, FormVersion-44 XCLC-only record.",
                )
            if record.groups != expected_cell_groups:
                self.error(
                    "plugin-group-tree",
                    "Structural CELL is outside the exact WRLD/world-child/block/subblock path.",
                )

        if tes4.flags & 0x200 == 0:
            self.error("plugin-esl-flag", "The output plugin is not ESL flagged.")
        hedr = first_subrecord(tes4, "HEDR")
        if hedr is None or len(hedr) < 12:
            self.error("plugin-next-form-id", "TES4 HEDR is missing.")
        elif struct.unpack_from("<I", hedr, 8)[0] != parse_form_id(
            allocation["nextFormId"]
        ):
            self.error(
                "plugin-next-form-id",
                "TES4 next FormID differs from the allocation.",
            )
        if any("lux" in master.casefold() for master in masters):
            self.error(
                "plugin-lux-master",
                "The output plugin introduced a Lux-family master.",
            )

        observed_forbidden = {
            record.signature
            for record in output.records
            if record.signature in FORBIDDEN_SIGNATURES
        }
        if observed_forbidden:
            self.error(
                "plugin-forbidden-signature",
                "The output plugin contains forbidden broad world records: "
                + ", ".join(sorted(observed_forbidden)),
            )

        npc_id = self_form(request["npcFormId"])
        hair = require_object(request["hair"], "hair")
        color_id = self_form(hair["colorFormId"])
        npc = output_by_form.get(npc_id)
        color = output_by_form.get(color_id)
        if npc is None or npc.signature != "NPC_":
            self.error("plugin-npc", "The target NPC_ record is missing.")
        else:
            if all_subrecords(npc, "DOFT") or all_subrecords(npc, "OTFT"):
                self.error(
                    "plugin-outfit",
                    "The output NPC has a non-null default outfit.",
                )
            hclf = first_u32(npc, "HCLF")
            if hclf != color_id:
                self.error(
                    "plugin-hair-link",
                    "The output NPC hair color link changed.",
                )
            pkids = [
                struct.unpack("<I", data)[0]
                for data in all_subrecords(npc, "PKID")
                if len(data) == 4
            ]
            if pkids != [self_form(allocation["package"])]:
                self.error(
                    "plugin-package-link",
                    "The output NPC does not bind exactly the allocated package.",
                )
        if color is None or color.signature != "CLFM":
            self.error("plugin-hair-rgb", "The hair color record is missing.")
        else:
            cnam = first_subrecord(color, "CNAM")
            actual_rgb = (
                None
                if cnam is None or len(cnam) < 3
                else (cnam[0] << 16) | (cnam[1] << 8) | cnam[2]
            )
            if actual_rgb != require_int(hair, "newPackedRgb"):
                self.error(
                    "plugin-hair-rgb",
                    "The output hair packed RGB differs from the request.",
                )

        self._verify_fixed_existing_records(
            request, source_by_form, output_by_form, self_form
        )
        self._verify_package_record(
            request, output_by_form, self_form
        )
        self._verify_placed_records(
            request,
            output_by_form,
            self_form,
            external_form,
        )

    def _verify_fixed_existing_records(
        self,
        request: dict[str, Any],
        source: dict[int, RawRecord],
        output: dict[int, RawRecord],
        self_form: Any,
    ) -> None:
        npc_id = self_form(request["npcFormId"])
        color_id = self_form(request["hair"]["colorFormId"])
        self._verify_tes4_delta(
            request, source.get(0), output.get(0)
        )
        self._verify_npc_delta(
            request,
            source.get(npc_id),
            output.get(npc_id),
            self_form,
        )
        self._verify_color_delta(
            request,
            source.get(color_id),
            output.get(color_id),
        )
        for form_id, source_record in source.items():
            output_record = output.get(form_id)
            if output_record is None:
                continue
            if form_id not in {0, npc_id, color_id} and (
                source_record.raw != output_record.raw
                or source_record.groups != output_record.groups
            ):
                self.error(
                    "plugin-existing-record-drift",
                    f"Existing record 0x{form_id:08X} changed outside approval.",
                )

    def _verify_tes4_delta(
        self,
        request: dict[str, Any],
        source: RawRecord | None,
        output: RawRecord | None,
    ) -> None:
        if source is None or output is None:
            return
        source_rows = parse_subrecords(source.data)
        output_rows = parse_subrecords(output.data)
        valid = (
            source.signature == output.signature == "TES4"
            and output.flags == (source.flags | 0x200)
            and source.groups == output.groups == ()
            and len(source_rows) == len(output_rows)
        )
        if valid:
            for source_row, output_row in zip(
                source_rows, output_rows
            ):
                if source_row[0] != output_row[0]:
                    valid = False
                    break
                if source_row[0] == "HEDR":
                    if (
                        len(source_row[1]) != len(output_row[1])
                        or source_row[1][:4] != output_row[1][:4]
                        or source_row[1][12:] != output_row[1][12:]
                    ):
                        valid = False
                        break
                elif source_row[1] != output_row[1]:
                    valid = False
                    break
        source_hedr = first_subrecord(source, "HEDR")
        output_hedr = first_subrecord(output, "HEDR")
        expected_next = parse_form_id(
            request["allocation"]["nextFormId"]
        )
        counts_valid = (
            source_hedr is not None
            and output_hedr is not None
            and len(source_hedr) >= 12
            and len(output_hedr) >= 12
            and struct.unpack_from("<I", output_hedr, 4)[0]
            == struct.unpack_from("<I", source_hedr, 4)[0] + 11
            and struct.unpack_from("<I", output_hedr, 8)[0]
            == expected_next
        )
        if not valid or not counts_valid:
            self.error(
                "plugin-tes4-preservation",
                "TES4 changed outside the exact ESL/NextFormID mechanical delta.",
            )

    def _verify_npc_delta(
        self,
        request: dict[str, Any],
        source: RawRecord | None,
        output: RawRecord | None,
        self_form: Any,
    ) -> None:
        if source is None or output is None:
            return
        expected_package = struct.pack(
            "<I", self_form(request["allocation"]["package"])
        )
        source_rows = parse_subrecords(source.data)
        output_rows = parse_subrecords(output.data)
        matching = [
            index
            for index, row in enumerate(output_rows)
            if row == ("PKID", expected_package)
        ]
        remaining = [
            row
            for index, row in enumerate(output_rows)
            if index not in matching
        ]
        if (
            len(matching) != 1
            or remaining != source_rows
            or source.flags != output.flags
            or source.groups != output.groups
        ):
            self.error(
                "plugin-npc-preservation",
                "NPC_ changed outside one exact allocated PKID addition.",
            )

    def _verify_color_delta(
        self,
        request: dict[str, Any],
        source: RawRecord | None,
        output: RawRecord | None,
    ) -> None:
        if source is None or output is None:
            return
        source_rows = parse_subrecords(source.data)
        output_rows = parse_subrecords(output.data)
        valid = (
            len(source_rows) == len(output_rows)
            and source.flags == output.flags
            and source.groups == output.groups
        )
        cnam_count = 0
        if valid:
            for source_row, output_row in zip(
                source_rows, output_rows
            ):
                if source_row[0] != output_row[0]:
                    valid = False
                    break
                if source_row[0] != "CNAM":
                    if source_row[1] != output_row[1]:
                        valid = False
                        break
                    continue
                cnam_count += 1
                old_rgb = require_int(request["hair"], "oldPackedRgb")
                new_rgb = require_int(request["hair"], "newPackedRgb")
                expected_old = old_rgb.to_bytes(3, "big")
                expected_new = new_rgb.to_bytes(3, "big")
                if (
                    len(source_row[1]) < 3
                    or len(source_row[1]) != len(output_row[1])
                    or source_row[1][:3] != expected_old
                    or output_row[1][:3] != expected_new
                    or source_row[1][3:] != output_row[1][3:]
                ):
                    valid = False
                    break
        if not valid or cnam_count != 1:
            self.error(
                "plugin-color-preservation",
                "CLFM changed outside the exact RGB-only delta.",
            )

    def _verify_package_record(
        self,
        request: dict[str, Any],
        records: dict[int, RawRecord],
        self_form: Any,
    ) -> None:
        allocation = request["allocation"]
        package = records.get(self_form(allocation["package"]))
        if package is None:
            return
        sandbox = request["sandbox"]
        if require_string(sandbox, "procedure") != "Sandbox":
            self.error(
                "plugin-procedure",
                "The admitted package procedure is not exact Sandbox.",
            )
        if package.groups != ((0, b"PACK"),):
            self.error(
                "plugin-group-tree",
                "The PACK record is outside its exact top-level group.",
            )
        package_rows = parse_subrecords(package.data)
        signatures = [signature for signature, _ in package_rows]
        simple_signatures = ["EDID", "PKDT", "PSDT", "PLDT", "CTDA"]
        production_signatures = [
            "EDID", "PKDT", "PSDT", "CTDA", "PKCU", "ANAM", "PLDT",
            "ANAM", "CNAM", "ANAM", "CNAM", "ANAM", "CNAM",
            "ANAM", "CNAM", "ANAM", "CNAM", "ANAM", "CNAM",
            "ANAM", "CNAM", "ANAM", "CNAM", "ANAM", "CNAM",
            "ANAM", "CNAM", "ANAM", "CNAM", "UNAM", "UNAM",
            "UNAM", "UNAM", "UNAM", "UNAM", "UNAM", "UNAM",
            "UNAM", "UNAM", "UNAM", "UNAM", "XNAM", "POBA",
            "INAM", "PDTO", "POEA", "INAM", "PDTO", "POCA",
            "INAM", "PDTO",
        ]
        condition_text = require_string(sandbox, "condition")
        condition_match = re.fullmatch(
            r"GetFactionRank\(([^|()]+)\|(0x[0-9A-Fa-f]{8})\) < 0",
            condition_text,
        )
        requested_faction = (
            0
            if condition_match is None
            else parse_form_id(condition_match.group(2))
        )
        production_rows = [
            (
                "EDID",
                (
                    require_string(request, "npcEditorId")
                    + "_FollowerSandbox\0"
                ).encode("utf-8"),
            ),
            ("PKDT", bytes.fromhex("0000000012000200fffe0000")),
            ("PSDT", bytes.fromhex("ffff00ffff00000000000000")),
            (
                "CTDA",
                struct.pack(
                    "<IfIIIIII",
                    0x80,
                    0.0,
                    0x49,
                    requested_faction,
                    0,
                    0,
                    0,
                    0xFFFFFFFF,
                ),
            ),
            ("PKCU", bytes.fromhex("0c00000054c201000a000000")),
            ("ANAM", b"Location\0"),
            (
                "PLDT",
                struct.pack(
                    "<iIi",
                    0,
                    self_form(
                        parse_reference(sandbox["target"])[1]
                    ),
                    int(sandbox["radius"]),
                ),
            ),
        ]
        for value in ([1] * 7 + [0, 0]):
            production_rows.extend(
                [("ANAM", b"Bool\0"), ("CNAM", bytes([value]))]
            )
        production_rows.extend(
            [
                ("ANAM", b"Float\0"),
                ("CNAM", struct.pack("<f", 50.0)),
                ("ANAM", b"Bool\0"),
                ("CNAM", b"\x01"),
            ]
        )
        production_rows.extend(
            ("UNAM", bytes([value]))
            for value in (
                0x00, 0x01, 0x03, 0x04, 0x05, 0x06,
                0x07, 0x0E, 0x19, 0x1B, 0x1D, 0x1F,
            )
        )
        production_rows.extend(
            [
                ("XNAM", b"\x0A"),
                ("POBA", b""),
                ("INAM", bytes(4)),
                ("PDTO", bytes(8)),
                ("POEA", b""),
                ("INAM", bytes(4)),
                ("PDTO", bytes(8)),
                ("POCA", b""),
                ("INAM", bytes(4)),
                ("PDTO", bytes(8)),
            ]
        )
        if signatures not in (simple_signatures, production_signatures):
            self.error(
                "plugin-procedure",
                "The PACK subrecord surface differs from the fixed Sandbox shape.",
            )
        elif (
            signatures == production_signatures
            and package_rows != production_rows
        ):
            self.error(
                "plugin-procedure",
                "The PACK differs from the exact frozen production Sandbox template.",
            )
        psdt = first_subrecord(package, "PSDT")
        continuous = (
            require_string(sandbox, "schedule").casefold()
            == "continuous"
            and psdt is not None
            and (
                psdt == bytes.fromhex("ffff00ffff00000000000000")
                or (
                    len(psdt) >= 4
                    and struct.unpack_from("<b", psdt, 0)[0] == -1
                    and struct.unpack_from("<b", psdt, 2)[0] in {0, -1}
                    and struct.unpack_from("<b", psdt, 3)[0] in {0, 24}
                )
            )
        )
        if not continuous:
            self.error(
                "plugin-schedule",
                "The PACK schedule is not the exact continuous shape.",
            )
        ctda = first_subrecord(package, "CTDA")
        condition_valid = (
            condition_match is not None
            and ctda is not None
            and len(ctda) >= 12
            and struct.unpack_from("<f", ctda, 4)[0] == 0.0
        )
        if condition_valid:
            try:
                actual_faction = struct.unpack_from(
                    "<I",
                    ctda,
                    12 if len(ctda) == 32 else 8,
                )[0]
                condition_valid = actual_faction == requested_faction
            except (IndexError, struct.error):
                condition_valid = False
        if not condition_valid:
            self.error(
                "plugin-condition",
                "The PACK does not contain the exact follower-faction rank condition.",
            )
        pldt = first_subrecord(package, "PLDT")
        if pldt is None or len(pldt) < 12:
            self.error(
                "plugin-package-target", "The PACK PLDT target is missing."
            )
            return
        _, target, radius = struct.unpack_from("<iIi", pldt)
        if target != self_form(
            parse_reference(sandbox["target"])[1]
        ) or radius != int(
            sandbox["radius"]
        ):
            self.error(
                "plugin-package-target",
                "The PACK target/radius differs from the request.",
            )

    def _verify_placed_records(
        self,
        request: dict[str, Any],
        records: dict[int, RawRecord],
        self_form: Any,
        external_form: Any,
    ) -> None:
        allocation = request["allocation"]
        placement = request["placement"]
        anchor = records.get(self_form(allocation["anchor"]))
        actor = records.get(self_form(allocation["actor"]))
        expected = (
            (
                anchor,
                external_form(placement["markerBase"]),
                placement["anchor"],
                "REFR",
            ),
            (
                actor,
                self_form(request["npcFormId"]),
                placement["actor"],
                "ACHR",
            ),
        )
        world_id = external_form(placement["worldspace"])
        cell_id = external_form(placement["cell"])
        actor_grid = (
            math.floor(float(placement["actor"]["x"]) / 4096.0),
            math.floor(float(placement["actor"]["y"]) / 4096.0),
        )
        anchor_grid = (
            math.floor(float(placement["anchor"]["x"]) / 4096.0),
            math.floor(float(placement["anchor"]["y"]) / 4096.0),
        )
        production_groups = (
            (0, b"WRLD"),
            (1, struct.pack("<I", world_id)),
            (
                4,
                struct.pack(
                    "<hh",
                    actor_grid[1] // 32,
                    actor_grid[0] // 32,
                ),
            ),
            (
                5,
                struct.pack(
                    "<hh",
                    actor_grid[1] // 8,
                    actor_grid[0] // 8,
                ),
            ),
            (6, struct.pack("<I", cell_id)),
            (8, struct.pack("<I", cell_id)),
        )
        if actor_grid != anchor_grid:
            self.error(
                "plugin-placement-cell",
                "Actor and anchor transforms resolve to different exterior cells.",
            )
        for record, base_id, transform, signature in expected:
            if record is None:
                continue
            if record.flags & 0x400 == 0:
                self.error(
                    "plugin-placement-persistent",
                    f"The placed {signature} is not persistent.",
                )
            if first_u32(record, "NAME") != base_id:
                self.error(
                    "plugin-placement-base",
                    f"The placed {signature} base differs from the request.",
                )
            data = first_subrecord(record, "DATA")
            values = (
                ()
                if data is None or len(data) < 24
                else struct.unpack_from("<6f", data)
            )
            target = tuple(
                struct.unpack(
                    "<f",
                    struct.pack("<f", float(transform[key])),
                )[0]
                for key in (
                    "x",
                    "y",
                    "z",
                    "rotationX",
                    "rotationY",
                    "rotationZ",
                )
            )
            if len(values) != 6 or any(
                not math.isclose(
                    actual, wanted, rel_tol=0.0, abs_tol=1e-5
                )
                for actual, wanted in zip(values, target)
            ):
                self.error(
                    "plugin-placement-transform",
                    f"The placed {signature} transform differs from the request.",
                )
            if not group_path_contains(record.groups, 1, world_id):
                self.error(
                    "plugin-placement-world",
                    f"The placed {signature} is outside the requested worldspace.",
                )
            if not (
                group_path_contains(record.groups, 6, cell_id)
                and group_path_contains(record.groups, 8, cell_id)
            ):
                self.error(
                    "plugin-placement-cell",
                    f"The placed {signature} is outside the requested cell groups.",
                )
            if record.groups != production_groups:
                self.error(
                    "plugin-group-tree",
                    f"The placed {signature} group tree is not the exact persistent-cell surface.",
                )


def parse_plugin(
    data: bytes,
    *,
    target_record: tuple[str, int] | None = None,
) -> RawPlugin:
    records: list[RawRecord] = []

    def walk(
        start: int,
        end: int,
        groups: tuple[tuple[int, bytes], ...],
    ) -> None:
        position = start
        while position < end:
            if end - position < 24:
                raise VerificationError(
                    f"truncated record/group header at offset {position}"
                )
            signature_bytes = data[position : position + 4]
            try:
                signature = signature_bytes.decode("ascii")
            except UnicodeDecodeError as exception:
                raise VerificationError(
                    f"non-ASCII signature at offset {position}"
                ) from exception
            if signature == "GRUP":
                size = struct.unpack_from("<I", data, position + 4)[0]
                if size < 24 or position + size > end:
                    raise VerificationError(
                        f"invalid GRUP size at offset {position}"
                    )
                if (
                    target_record is not None
                    and len(groups) >= MAX_TARGET_RECORD_GROUP_DEPTH
                ):
                    raise VerificationError(
                        "target record group nesting exceeds the bound"
                    )
                label = data[position + 8 : position + 12]
                group_type = struct.unpack_from("<i", data, position + 12)[0]
                walk(
                    position + 24,
                    position + size,
                    groups + ((group_type, label),),
                )
                position += size
                continue
            size, flags, form_id = struct.unpack_from(
                "<III", data, position + 4
            )
            record_end = position + 24 + size
            if record_end > end:
                raise VerificationError(
                    f"invalid {signature} size at offset {position}"
                )
            wanted = (
                target_record is None
                or (
                    signature == target_record[0]
                    and (form_id & 0x00FFFFFF)
                    == (target_record[1] & 0x00FFFFFF)
                )
            )
            if flags & 0x00040000:
                if target_record is None or wanted:
                    raise VerificationError(
                        f"compressed record {signature} is outside this oracle"
                    )
                position = record_end
                continue
            if wanted:
                raw = data[position:record_end]
                records.append(
                    RawRecord(
                        signature,
                        form_id,
                        flags,
                        data[position + 24 : record_end],
                        raw,
                        groups,
                    )
                )
            position = record_end
        if position != end:
            raise VerificationError("raw plugin object bounds did not close")

    walk(0, len(data), ())
    return RawPlugin(tuple(records))


def parse_subrecords(data: bytes) -> list[tuple[str, bytes]]:
    rows: list[tuple[str, bytes]] = []
    position = 0
    extended_size: int | None = None
    while position < len(data):
        if len(data) - position < 6:
            raise VerificationError("truncated subrecord header")
        signature = data[position : position + 4].decode("ascii")
        size = struct.unpack_from("<H", data, position + 4)[0]
        position += 6
        if signature == "XXXX":
            if size != 4 or position + 4 > len(data):
                raise VerificationError("invalid XXXX subrecord")
            extended_size = struct.unpack_from("<I", data, position)[0]
            position += 4
            continue
        actual_size = extended_size if extended_size is not None else size
        extended_size = None
        end = position + actual_size
        if end > len(data):
            raise VerificationError(f"truncated {signature} subrecord")
        rows.append((signature, data[position:end]))
        position = end
    if extended_size is not None:
        raise VerificationError("dangling XXXX subrecord")
    return rows


def all_subrecords(record: RawRecord, signature: str) -> list[bytes]:
    return [
        data
        for found_signature, data in parse_subrecords(record.data)
        if found_signature == signature
    ]


def first_subrecord(record: RawRecord, signature: str) -> bytes | None:
    rows = all_subrecords(record, signature)
    return rows[0] if rows else None


def first_u32(record: RawRecord, signature: str) -> int | None:
    data = first_subrecord(record, signature)
    return (
        None
        if data is None or len(data) != 4
        else struct.unpack("<I", data)[0]
    )


def group_path_contains(
    groups: Iterable[tuple[int, bytes]], group_type: int, label: int
) -> bool:
    expected = struct.pack("<I", label)
    return any(
        found_type == group_type and found_label == expected
        for found_type, found_label in groups
    )


def read_zip_bytes(
    data: bytes, verifier: Verifier, unsafe_code: str
) -> dict[str, tuple[str, bytes]]:
    result: dict[str, tuple[str, bytes]] = {}
    with zipfile.ZipFile(io.BytesIO(data), "r") as archive:
        if len(archive.infolist()) > MAX_ENTRIES:
            raise VerificationError("ZIP entry count exceeds the bound")
        for info in archive.infolist():
            name = info.filename
            unix_mode = info.external_attr >> 16
            unsafe = (
                not safe_posix_path(name)
                or name.endswith("/")
                or stat.S_IFMT(unix_mode) == stat.S_IFLNK
                or info.file_size <= 0
                or info.file_size > MAX_ENTRY_BYTES
            )
            if unsafe:
                verifier.error(
                    unsafe_code, f"ZIP entry {name!r} is unsafe."
                )
                continue
            folded = name.casefold()
            if folded in result:
                verifier.error(
                    unsafe_code,
                    f"ZIP entry {name!r} is duplicated or case-aliased.",
                )
                continue
            result[folded] = (name, archive.read(info))
    return result


def read_tree(root: Path) -> dict[str, bytes]:
    if not root.is_dir() or is_reparse_point(root):
        raise VerificationError("The package root is missing or a link.")
    result: dict[str, bytes] = {}
    folded: set[str] = set()
    for current, directories, files in os.walk(root, followlinks=False):
        current_path = Path(current)
        for directory in list(directories):
            if is_reparse_point(current_path / directory):
                raise VerificationError(
                    f"Package directory {directory!r} is a reparse point."
                )
        for filename in files:
            path = current_path / filename
            if is_reparse_point(path):
                raise VerificationError(
                    f"Package file {str(path)!r} is a reparse point."
                )
            relative = path.relative_to(root).as_posix()
            if not safe_posix_path(relative):
                raise VerificationError(
                    f"Package path {relative!r} is unsafe."
                )
            key = relative.casefold()
            if key in folded:
                raise VerificationError(
                    f"Package path {relative!r} is case-aliased."
                )
            folded.add(key)
            result[relative] = read_bounded(path, MAX_ENTRY_BYTES)
    return result


def safe_posix_path(value: str) -> bool:
    if (
        not value
        or "\\" in value
        or ":" in value
        or value.startswith("/")
        or value.endswith("/")
        or "//" in value
    ):
        return False
    path = PurePosixPath(value)
    return all(part not in {"", ".", ".."} for part in path.parts)


def read_bounded(path: Path, maximum: int) -> bytes:
    size = path.stat().st_size
    if size <= 0 or size > maximum:
        raise VerificationError(
            f"File {str(path)!r} is empty or exceeds its size bound."
        )
    return path.read_bytes()


def load_json_bytes(data: bytes) -> Any:
    def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise DuplicateKeyError(f"duplicate JSON key {key!r}")
            result[key] = value
        return result

    try:
        return json.loads(data.decode("utf-8"), object_pairs_hook=unique_object)
    except (UnicodeDecodeError, json.JSONDecodeError) as exception:
        raise VerificationError(f"invalid strict UTF-8 JSON: {exception}") from exception


def require_object(value: Any, role: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise VerificationError(f"{role} must be a JSON object")
    return value


def require_list(value: dict[str, Any], key: str) -> list[Any]:
    result = value.get(key)
    if not isinstance(result, list):
        raise VerificationError(f"{key!r} must be a JSON array")
    return result


def require_string(value: dict[str, Any], key: str) -> str:
    result = value.get(key)
    if not isinstance(result, str) or not result.strip():
        raise VerificationError(f"{key!r} must be a nonblank string")
    return result


def require_int(value: dict[str, Any], key: str) -> int:
    result = value.get(key)
    if isinstance(result, bool) or not isinstance(result, int):
        raise VerificationError(f"{key!r} must be an integer")
    return result


def parse_form_id(value: Any) -> int:
    if not isinstance(value, str) or not re.fullmatch(
        r"0x[0-9A-Fa-f]{1,8}", value
    ):
        raise VerificationError(f"invalid FormID {value!r}")
    return int(value, 16)


def parse_reference(value: Any) -> tuple[str, int]:
    if not isinstance(value, str) or "|" not in value:
        raise VerificationError(f"invalid FormReference {value!r}")
    plugin, form_id = value.rsplit("|", 1)
    if not plugin:
        raise VerificationError(f"invalid FormReference {value!r}")
    return plugin, parse_form_id(form_id)


def find_casefold(mapping: dict[str, bytes], path: str) -> bytes | None:
    folded = path.casefold()
    for key, value in mapping.items():
        if key.casefold() == folded:
            return value
    return None


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def expected_diagnostic_bytes(request: dict[str, Any]) -> bytes:
    source = require_object(request.get("source"), "request.source")
    plugin = require_string(source, "plugin")
    editor_id = require_string(request, "npcEditorId")
    form_id = require_string(request, "npcFormId")
    return (
        "; NPC Manager follower-finish diagnostic\n"
        "; status=STATIC_PASS_RUNTIME_REQUIRED\n"
        f"; plugin={plugin}\n"
        f"; editorId={editor_id}\n"
        f"; formId={form_id}\n"
        f'help "{editor_id}" 4\n'
    ).encode("utf-8")


def path_key(path: Path) -> str:
    return str(path.absolute()).replace("/", "\\").casefold()


def is_under_path(path: Path, root: Path) -> bool:
    candidate = path_key(path)
    authority = path_key(root).rstrip("\\")
    return candidate == authority or candidate.startswith(
        authority + "\\"
    )


def is_reparse_point(path: Path) -> bool:
    try:
        details = path.lstat()
    except FileNotFoundError:
        return False
    attributes = getattr(details, "st_file_attributes", 0)
    reparse_flag = getattr(
        stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400
    )
    junction = getattr(os.path, "isjunction", None)
    return (
        path.is_symlink()
        or bool(attributes & reparse_flag)
        or bool(junction is not None and junction(path))
    )


def first_reparse_component(path: Path) -> Path | None:
    absolute = path.absolute()
    if not is_under_path(absolute, WORKSPACE_ROOT):
        return None
    relative = absolute.relative_to(WORKSPACE_ROOT)
    current = WORKSPACE_ROOT
    if is_reparse_point(current):
        return current
    for part in relative.parts:
        current = current / part
        if is_reparse_point(current):
            return current
    return None


def validate_report_path(report: Path) -> list[dict[str, str]]:
    diagnostics: list[dict[str, str]] = []
    if not is_under_path(report, WORKSPACE_ROOT) or is_under_path(
        report, PROTECTED_ROOT
    ):
        diagnostics.append(
            {
                "code": "path-authority",
                "message": "The report path is outside K-only workspace authority.",
            }
        )
        return diagnostics
    if report.exists():
        diagnostics.append(
            {
                "code": "report-exists",
                "message": "The verifier never overwrites an existing report.",
            }
        )
    if not report.parent.is_dir():
        diagnostics.append(
            {
                "code": "report-parent",
                "message": "The report parent must already exist.",
            }
        )
    reparse = first_reparse_component(report.parent)
    if reparse is not None:
        diagnostics.append(
            {
                "code": "path-reparse",
                "message": f"Report path crosses reparse point {str(reparse)!r}.",
            }
        )
    return diagnostics


def validate_cli_inputs(
    args: argparse.Namespace,
) -> list[dict[str, str]]:
    diagnostics: list[dict[str, str]] = []
    roles = (
        ("request", args.request, "file"),
        ("proposal", args.proposal, "file"),
        ("source ZIP", args.source_zip, "file"),
        ("package root", args.package_root, "directory"),
        ("archive", args.archive, "file"),
    )
    for role, path, kind in roles:
        if not is_under_path(path, WORKSPACE_ROOT) or is_under_path(
            path, PROTECTED_ROOT
        ):
            diagnostics.append(
                {
                    "code": "path-authority",
                    "message": f"The {role} path is outside K-only workspace authority.",
                }
            )
            continue
        reparse = first_reparse_component(path)
        if reparse is not None:
            diagnostics.append(
                {
                    "code": "path-reparse",
                    "message": f"The {role} path crosses reparse point {str(reparse)!r}.",
                }
            )
        exists_as_kind = (
            path.is_file() if kind == "file" else path.is_dir()
        )
        if not exists_as_kind:
            diagnostics.append(
                {
                    "code": "path-type",
                    "message": f"The {role} is not an existing ordinary {kind}.",
                }
            )

    paths = [
        args.request,
        args.proposal,
        args.source_zip,
        args.package_root,
        args.archive,
        args.report,
    ]
    for first in range(len(paths)):
        for second in range(first + 1, len(paths)):
            if (
                path_key(paths[first]) == path_key(paths[second])
                or is_under_path(paths[first], paths[second])
                or is_under_path(paths[second], paths[first])
            ):
                diagnostics.append(
                    {
                        "code": "path-collision",
                        "message": "Verifier input/output paths must be pairwise disjoint, including case aliases.",
                    }
                )
    return diagnostics


def report_collides_with_input(
    args: argparse.Namespace,
) -> bool:
    for path in (
        args.request,
        args.proposal,
        args.source_zip,
        args.package_root,
        args.archive,
    ):
        if (
            path_key(args.report) == path_key(path)
            or is_under_path(args.report, path)
            or is_under_path(path, args.report)
        ):
            return True
    return False


def boundary_report(
    diagnostics: list[dict[str, str]],
) -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "artifactKind": "skyrim-follower-finish-independent-verification",
        "verdict": "FAIL",
        "runtimeAuthority": False,
        "plugin": {
            "recordCounts": {"PACK": 0, "REFR": 0, "ACHR": 0}
        },
        "diagnostics": diagnostics,
    }


def write_new_report(path: Path, report: dict[str, Any]) -> None:
    with path.open(
        "x", encoding="utf-8", newline="\n"
    ) as stream:
        json.dump(
            report,
            stream,
            indent=2,
            ensure_ascii=False,
        )
        stream.write("\n")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Independently verify a simple-follower finish package, "
            "raw plugin surface, and no-wrapper archive."
        )
    )
    parser.add_argument("--request", type=Path, required=True)
    parser.add_argument("--proposal", type=Path, required=True)
    parser.add_argument("--source-zip", type=Path, required=True)
    parser.add_argument("--package-root", type=Path, required=True)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    report_path_diagnostics = validate_report_path(args.report)
    if report_path_diagnostics:
        for diagnostic in report_path_diagnostics:
            print(
                f"{diagnostic['code']}: {diagnostic['message']}",
                file=sys.stderr,
            )
        return 2
    if report_collides_with_input(args):
        print(
            "path-collision: The report path overlaps verifier input.",
            file=sys.stderr,
        )
        return 2

    input_diagnostics = validate_cli_inputs(args)
    report: dict[str, Any]
    if input_diagnostics:
        report = boundary_report(input_diagnostics)
    else:
        try:
            report = Verifier(args).verify()
        except Exception as exception:  # last-resort report guarantee
            report = boundary_report(
                [
                    {
                        "code": "verification-internal",
                        "message": f"{type(exception).__name__}: {exception}",
                    }
                ]
            )
    try:
        write_new_report(args.report, report)
    except FileExistsError:
        return 2
    except OSError as exception:
        print(
            f"report-write: {type(exception).__name__}: {exception}",
            file=sys.stderr,
        )
        return 2
    return 0 if report["verdict"] == "STATIC_PASS_RUNTIME_REQUIRED" else 1


if __name__ == "__main__":
    raise SystemExit(main())
