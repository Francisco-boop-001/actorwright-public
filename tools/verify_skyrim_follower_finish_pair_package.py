#!/usr/bin/env python3
"""Independent raw/package verifier for schema-2/3 paired follower finish."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import struct
import sys
import zipfile
from pathlib import Path, PurePosixPath
from typing import Any

WORKSPACE_ROOT = Path(r"K:\ExampleWorkspace")
sys.path.insert(
    0,
    str(WORKSPACE_ROOT / "tools" / "gates" / "lib"),
)
from nif_graph_tool import parse_blocks, parse_header  # noqa: E402

from verify_skyrim_follower_finish_package import (
    MAX_JSON_BYTES,
    VerificationError,
    all_subrecords,
    first_subrecord,
    load_json_bytes,
    normalize_bound_json,
    parse_plugin,
    parse_subrecords,
    read_bounded,
    sha256,
)


FORBIDDEN_SIGNATURES = {
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


def required_exterior_placement_roles(
    request: dict[str, Any],
) -> tuple[str, ...]:
    if request.get("companionFinish") is None:
        return ("subject",)
    return ("subject", "companion")


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", type=Path, required=True)
    parser.add_argument("--proposal", type=Path, required=True)
    parser.add_argument("--package-root", type=Path, required=True)
    parser.add_argument("--archive", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    return parser.parse_args(argv)


class PairVerifier:
    def __init__(self, args: argparse.Namespace) -> None:
        self.args = args
        self.errors: list[dict[str, str]] = []
        self.plugin: dict[str, Any] = {}

    def fail(self, code: str, message: str) -> None:
        self.errors.append({"code": code, "message": message})

    def verify(self) -> dict[str, Any]:
        try:
            self._verify_paths()
            request_bytes = read_bounded(
                self.args.request, MAX_JSON_BYTES
            )
            proposal_bytes = read_bounded(
                self.args.proposal, MAX_JSON_BYTES
            )
            request = load_json_bytes(request_bytes)
            proposal = load_json_bytes(proposal_bytes)
            self._verify_documents(
                request, request_bytes, proposal, proposal_bytes
            )
            self._verify_bound_files(request)
            entries = self._verify_tree(request)
            self._verify_archive(request, entries)
            self._verify_plugin(request, proposal, entries)
        except (
            AssertionError,
            VerificationError,
            OSError,
            ValueError,
            KeyError,
            TypeError,
            struct.error,
            zipfile.BadZipFile,
        ) as exception:
            self.fail("verification-input", str(exception))
        passed = not self.errors
        return {
            "schemaVersion": 1,
            "artifactKind":
                "skyrim-paired-follower-finish-independent-verification",
            "verdict":
                "STATIC_PASS_RUNTIME_REQUIRED" if passed else "FAIL",
            "runtimeAuthority": False,
            "plugin": self.plugin,
            "diagnostics": self.errors,
        }

    def _verify_paths(self) -> None:
        for path in (
            self.args.request,
            self.args.proposal,
            self.args.package_root,
            self.args.archive,
            self.args.report,
        ):
            resolved = path.resolve()
            if (
                not is_under(resolved, WORKSPACE_ROOT)
                or ":" in str(resolved)[2:]
            ):
                self.fail(
                    "path-authority",
                    f"Path is outside ordinary K-local authority: {path}",
                )
        if self.args.report.exists():
            self.fail(
                "report-exists",
                "Independent reports are create-new.",
            )

    def _verify_documents(
        self,
        request: dict[str, Any],
        request_bytes: bytes,
        proposal: dict[str, Any],
        proposal_bytes: bytes,
    ) -> None:
        del proposal_bytes
        if (
            request.get("schemaVersion") not in {2, 3}
            or request.get("operation")
            != "skyrim-paired-follower-finish"
            or proposal.get("schemaVersion")
            != request.get("schemaVersion")
            or proposal.get("operation")
            != "skyrim-paired-follower-finish"
        ):
            self.fail(
                "document-schema",
                "Request/proposal is not the closed schema-2/3 operation.",
            )
        if (
            str(proposal.get("requestSha256", "")).upper()
            != sha256(request_bytes)
            or normalize_bound_json(proposal.get("request"))
            != normalize_bound_json(request)
        ):
            self.fail(
                "proposal-binding",
                "Proposal does not bind the exact request bytes/content.",
            )
        if proposal.get("runtimeAuthority") is not False:
            self.fail(
                "runtime-authority",
                "Static proposal claimed runtime authority.",
            )
        expected_masters = list(
            proposal["subjectSnapshot"]["masters"]
        ) + [
            request["companion"]["plugin"],
            request["outfit"]["plugin"],
        ]
        if proposal.get("outputMasters") != expected_masters:
            self.fail(
                "proposal-masters",
                "Proposal master append plan is not exact.",
            )
        companion_finish = request.get("companionFinish")
        expected_companion_masters = (
            list(proposal["companionSnapshot"]["masters"])
            + [request["outfit"]["plugin"]]
            if companion_finish is not None
            else None
        )
        if proposal.get("companionOutputMasters") != (
            expected_companion_masters
        ):
            self.fail(
                "proposal-companion-masters",
                "Proposal companion master plan is not exact.",
            )

    def _verify_bound_files(self, request: dict[str, Any]) -> None:
        for row in bound_file_rows(request):
            path = Path(row["path"])
            if (
                not path.is_file()
                or path.stat().st_size != row["byteLength"]
                or file_sha(path).lower() != row["sha256"].lower()
            ):
                self.fail(
                    "bound-file-drift",
                    f"Bound authority changed: {path}",
                )
        if (
            request["companion"]["runtimeScript"]["sha256"].lower()
            != request["subject"]["runtimeScript"]["sha256"].lower()
        ):
            self.fail(
                "script-deduplication",
                "The supposedly shared runtime script differs.",
            )

    def _verify_tree(
        self, request: dict[str, Any]
    ) -> dict[str, bytes]:
        root = self.args.package_root
        manifest_path = root / "npcmanager-paired-package.json"
        manifest = load_json_bytes(
            read_bounded(manifest_path, MAX_JSON_BYTES)
        )
        if (
            manifest.get("schemaVersion")
            != request.get("schemaVersion")
            or manifest.get("artifactKind")
            != "skyrim-paired-follower-finish-package"
            or manifest.get("status")
            != "STATIC_PASS_RUNTIME_REQUIRED"
            or manifest.get("runtimeAuthority") is not False
        ):
            self.fail(
                "manifest-envelope",
                "Paired manifest envelope is invalid.",
            )
        entries = {
            relative(root, path): path.read_bytes()
            for path in root.rglob("*")
            if path.is_file()
        }
        declared = {
            row["relativePath"]: (
                row["byteLength"],
                row["sha256"].lower(),
            )
            for row in manifest["artifacts"]
        }
        actual_without_manifest = {
            name: (len(data), hashlib.sha256(data).hexdigest())
            for name, data in entries.items()
            if name != "npcmanager-paired-package.json"
        }
        if (
            declared != actual_without_manifest
            or len(declared) != len(manifest["artifacts"])
            or any(unsafe_member(name) for name in entries)
        ):
            self.fail(
                "manifest-inventory",
                "Manifest inventory is missing, extra, unsafe, duplicate, or mismatched.",
            )
        immutable = (
            (
                "Data/textures/actors/character/FaceGenData/FaceTint/"
                f"{request['companion']['plugin']}/"
                f"{local_id(request['companion']['actorFormId']):08X}.dds",
                request["companion"]["faceTint"]["sha256"],
            ),
            (
                "Data/meshes/actors/character/FaceGenData/FaceGeom/"
                f"{request['subject']['plugin']}/"
                f"{local_id(request['subject']['actorFormId']):08X}.nif",
                request["subject"]["faceGeom"]["sha256"],
            ),
            (
                "Data/textures/actors/character/FaceGenData/FaceTint/"
                f"{request['subject']['plugin']}/"
                f"{local_id(request['subject']['actorFormId']):08X}.dds",
                request["subject"]["faceTint"]["sha256"],
            ),
            (
                "Data/Scripts/NPCM_Manolov_ApplySSE.pex",
                request["subject"]["runtimeScript"]["sha256"],
            ),
        )
        if request.get("companionFinish") is None:
            immutable += (
                (
                    f"Data/{request['companion']['plugin']}",
                    request["companion"]["pluginFile"]["sha256"],
                ),
                (
                    "Data/meshes/actors/character/FaceGenData/FaceGeom/"
                    f"{request['companion']['plugin']}/"
                    f"{local_id(request['companion']['actorFormId']):08X}.nif",
                    request["companion"]["faceGeom"]["sha256"],
                ),
            )
        for name, expected in immutable:
            data = entries.get(name)
            if (
                data is None
                or hashlib.sha256(data).hexdigest()
                != expected.lower()
            ):
                self.fail(
                    "immutable-payload",
                    f"Immutable payload differs: {name}",
                )
        return entries

    def _verify_archive(
        self,
        request: dict[str, Any],
        entries: dict[str, bytes],
    ) -> None:
        expected = {
            name.removeprefix("Data/"): data
            for name, data in entries.items()
            if name.startswith("Data/")
        }
        readme = "README-NPCMANAGER-RUNTIME-TEST.txt"
        if readme not in entries:
            self.fail(
                "archive-readme",
                "The transaction tree lacks its runtime README.",
            )
        else:
            expected[readme] = entries[readme]
        for plugin in (
            request["companion"]["plugin"],
            request["subject"]["plugin"],
        ):
            if plugin not in expected:
                self.fail(
                    "archive-root-plugin",
                    f"The install projection lacks root plugin: {plugin}",
                )
        with zipfile.ZipFile(self.args.archive, "r") as archive:
            infos = archive.infolist()
            names = [info.filename for info in infos]
            if (
                len(names) != len(set(name.casefold() for name in names))
                or set(names) != set(expected)
                or any(unsafe_member(name) for name in names)
            ):
                self.fail(
                    "archive-inventory",
                    "Archive inventory differs from Data-at-root plus the runtime README.",
                )
            for info in infos:
                data = archive.read(info)
                if data != expected.get(info.filename):
                    self.fail(
                        "archive-member",
                        f"Archive member differs: {info.filename}",
                    )

    def _verify_plugin(
        self,
        request: dict[str, Any],
        proposal: dict[str, Any],
        entries: dict[str, bytes],
    ) -> None:
        source_bytes = Path(
            request["subject"]["pluginFile"]["path"]
        ).read_bytes()
        output_name = f"Data/{request['subject']['plugin']}"
        output_bytes = entries[output_name]
        source = parse_plugin(source_bytes)
        output = parse_plugin(output_bytes)
        companion_output = parse_plugin(
            entries[f"Data/{request['companion']['plugin']}"]
        )
        plugins_by_role = {
            "subject": output,
            "companion": companion_output,
        }
        for role in required_exterior_placement_roles(request):
            self._verify_exact_exterior_placement(
                plugins_by_role[role],
                request,
                role,
            )
        forbidden_by_role: dict[str, list[str]] = {}
        for role, plugin in plugins_by_role.items():
            forbidden_by_role[role] = sorted(
                signature
                for signature in FORBIDDEN_SIGNATURES
                if plugin.records_of(signature)
            )
            if forbidden_by_role[role]:
                code = (
                    "forbidden-world-surface"
                    if role == "subject"
                    else "companion-forbidden-world-surface"
                )
                self.fail(
                    code,
                    f"{role.capitalize()} output contains forbidden "
                    "master-world, terrain, water, or navigation records: "
                    + ", ".join(forbidden_by_role[role]),
                )
        forbidden = forbidden_by_role["subject"]
        source_tes4 = only(source.records_of("TES4"), "source TES4")
        output_tes4 = only(output.records_of("TES4"), "output TES4")
        source_masters = masters(source_tes4)
        output_masters = masters(output_tes4)
        if output_masters != proposal["outputMasters"]:
            self.fail(
                "plugin-masters",
                "Raw TES4 masters differ from the proposal.",
            )
        if output_tes4.flags & 0x200 == 0:
            self.fail(
                "plugin-esl",
                "Subject plugin does not carry the ESL flag.",
            )
        hedr = first_subrecord(output_tes4, "HEDR")
        allocation = request["allocation"]
        next_form_id = local_id(allocation["nextFormId"])
        if (
            hedr is None
            or len(hedr) < 12
            or struct.unpack_from("<I", hedr, 8)[0] != next_form_id
        ):
            self.fail(
                "plugin-next-form-id",
                "Subject plugin NextFormID differs from the request.",
            )
        if any(
            record.signature in FORBIDDEN_SIGNATURES
            for record in output.records
        ):
            self.fail(
                "plugin-forbidden-world",
                "Subject plugin contains a forbidden broad world record.",
            )

        source_self = len(source_masters)
        output_self = len(output_masters)
        source_by_local = local_records(source, source_self)
        output_by_local = local_records(output, output_self)
        subject_local = local_id(
            request["subject"]["actorFormId"]
        )
        first_new = local_id(allocation["privateArmorAddon"])
        if (
            subject_local not in source_by_local
            or not source_by_local
            or any(local >= first_new for local in source_by_local)
        ):
            self.fail(
                "source-inventory",
                "Accepted subject source inventory omits the actor or collides with the allocation.",
            )
        for local, source_record in source_by_local.items():
            output_record = output_by_local.get(local)
            if (
                output_record is None
                or output_record.signature != source_record.signature
            ):
                self.fail(
                    "existing-record",
                    f"Existing record 0x{local:03X} is missing or changed type.",
                )
                continue
            source_data = normalize_self_ids(
                source_record.data, source_self, source_by_local
            )
            output_rows = parse_subrecords(output_record.data)
            if local == subject_local:
                output_rows = [
                    row
                    for row in output_rows
                    if row[0] not in {"PKID", "DOFT"}
                ]
                output_data = encode_subrecords(output_rows)
            else:
                output_data = output_record.data
            output_data = normalize_self_ids(
                output_data, output_self, source_by_local
            )
            if (
                source_data != output_data
                or source_record.flags != output_record.flags
            ):
                self.fail(
                    "appearance-preservation",
                    f"Existing {source_record.signature} 0x{local:03X} changed outside owner relocation/PKID/DOFT.",
                )

        expected_new = expected_new_records(request)
        for local, signature in expected_new.items():
            record = output_by_local.get(local)
            if record is None or record.signature != signature:
                self.fail(
                    "new-record-inventory",
                    f"Expected {signature} 0x{local:03X} is missing.",
                )
        unexpected = set(output_by_local) - (
            set(source_by_local) | set(expected_new)
        )
        if unexpected:
            self.fail(
                "new-record-inventory",
                f"Unexpected self-owned local IDs: {sorted(unexpected)}",
            )

        companion_index = output_masters.index(
            request["companion"]["plugin"]
        )
        outfit_index = output_masters.index(
            request["outfit"]["plugin"]
        )
        companion_form = raw_form(
            companion_index,
            local_id(request["companion"]["actorFormId"]),
        )
        anchor_plugin, anchor_local = split_form_reference(
            request["companionAnchor"]
        )
        if anchor_plugin != request["companion"]["plugin"]:
            self.fail(
                "companion-anchor",
                "Companion anchor is not companion-owned.",
            )
        anchor_form = raw_form(companion_index, anchor_local)
        subject_form = raw_form(output_self, subject_local)
        target_txst = raw_reference(
            output_masters,
            output_self,
            request["subject"]["plugin"],
            request["outfit"]["targetFemaleSkinTextureSet"],
        )
        outfit_boots = raw_reference(
            output_masters,
            output_self,
            request["subject"]["plugin"],
            request["outfit"]["boots"],
        )
        outfit_gloves = raw_reference(
            output_masters,
            output_self,
            request["subject"]["plugin"],
            request["outfit"]["gauntlets"],
        )
        private_addon = local_id(allocation["privateArmorAddon"])
        private_armor = local_id(allocation["privateArmor"])
        outfit_local = local_id(allocation["outfit"])
        package_local = local_id(allocation["package"])
        placed_local = local_id(allocation["placedActor"])
        subject_rela = local_id(
            allocation["subjectToCompanionRelationship"]
        )
        companion_rela = local_id(
            allocation["companionToSubjectRelationship"]
        )

        npc = output_by_local[subject_local]
        if (
            unpack_u32(only(all_subrecords(npc, "PKID"), "NPC PKID"))
            != raw_form(output_self, package_local)
            or unpack_u32(
                only(all_subrecords(npc, "DOFT"), "NPC DOFT")
            )
            != raw_form(output_self, outfit_local)
        ):
            self.fail(
                "npc-links",
                "Subject NPC package/outfit links differ.",
            )
        arma = output_by_local[private_addon]
        if unpack_u32(first_subrecord(arma, "NAM1")) != target_txst:
            self.fail(
                "private-skin",
                "Private torso ARMA does not bind the COtR target TXST.",
            )
        armo = output_by_local[private_armor]
        armo_links = [
            unpack_u32(data)
            for signature, data in parse_subrecords(armo.data)
            if signature == "MODL" and len(data) == 4
        ]
        if armo_links != [raw_form(output_self, private_addon)]:
            self.fail(
                "private-armor",
                "Private torso ARMO does not contain exactly one private ARMA.",
            )
        otft = output_by_local[outfit_local]
        inam = first_subrecord(otft, "INAM")
        if (
            inam is None
            or len(inam) != 12
            or struct.unpack("<III", inam)
            != (
                raw_form(output_self, private_armor),
                outfit_boots,
                outfit_gloves,
            )
        ):
            self.fail(
                "outfit-items",
                "Private OTFT does not contain exact torso/boots/gauntlets.",
            )
        package = output_by_local[package_local]
        pldt = first_subrecord(package, "PLDT")
        if (
            pldt is None
            or len(pldt) != 12
            or struct.unpack("<iIi", pldt) != (0, anchor_form, 768)
            or len(all_subrecords(package, "CTDA")) != 1
        ):
            self.fail(
                "package-target",
                "Subject Sandbox target/radius/condition surface differs.",
            )
        achr = output_by_local[placed_local]
        data = first_subrecord(achr, "DATA")
        expected = request["placement"]["subject"]
        if (
            unpack_u32(first_subrecord(achr, "NAME")) != subject_form
            or data is None
            or len(data) != 24
            or not all(
                math.isclose(left, right, abs_tol=0.01)
                for left, right in zip(
                    struct.unpack("<ffffff", data),
                    (
                        expected["x"],
                        expected["y"],
                        expected["z"],
                        expected["rotationX"],
                        expected["rotationY"],
                        expected["rotationZ"],
                    ),
                )
            )
        ):
            self.fail(
                "placement",
                "Subject ACHR base or transform differs.",
            )
        relationship_pairs = (
            (subject_rela, subject_form, companion_form),
            (companion_rela, companion_form, subject_form),
        )
        for local, parent, child in relationship_pairs:
            rela = output_by_local[local]
            rela_data = first_subrecord(rela, "DATA")
            if (
                rela_data is None
                or len(rela_data) != 16
                or struct.unpack_from("<II", rela_data) != (parent, child)
                or rela_data[8] != 1
            ):
                self.fail(
                    "pair-relationships",
                    f"Directional Ally RELA 0x{local:03X} differs.",
                )
        self.plugin = {
            "sourceSha256": hashlib.sha256(source_bytes).hexdigest(),
            "outputSha256": hashlib.sha256(output_bytes).hexdigest(),
            "masters": output_masters,
            "tes4Esl": bool(output_tes4.flags & 0x200),
            "existingLocalIds": [
                f"0x{value:08X}" for value in sorted(source_by_local)
            ],
            "newRecords": [
                f"{expected_new[value]} 0x{value:08X}"
                for value in sorted(expected_new)
            ],
            "forbiddenSignaturesPresent": forbidden,
        }
        if request.get("companionFinish") is not None:
            self.plugin["companionFinish"] = (
                self._verify_companion_finish(
                    request,
                    proposal,
                    entries,
                )
            )

    def _verify_exact_exterior_placement(
        self,
        plugin: Any,
        request: dict[str, Any],
        role: str,
    ) -> None:
        tes4 = only(plugin.records_of("TES4"), f"{role} TES4")
        output_masters = masters(tes4)
        placement = request["placement"]

        def external_form(value: str) -> int:
            plugin_name, local = split_form_reference(value)
            matches = [
                index
                for index, master in enumerate(output_masters)
                if master.casefold() == plugin_name.casefold()
            ]
            if len(matches) != 1:
                raise VerificationError(
                    f"{role} placement master is absent or ambiguous: {value}"
                )
            return raw_form(matches[0], local)

        world_id = external_form(placement["worldspace"])
        cell_id = external_form(placement["cell"])
        grid_x = int(placement["cellGridX"])
        grid_y = int(placement["cellGridY"])
        cell_groups = (
            (0, b"WRLD"),
            (1, struct.pack("<I", world_id)),
            (
                4,
                struct.pack(
                    "<hh",
                    grid_y // 32,
                    grid_x // 32,
                ),
            ),
            (
                5,
                struct.pack(
                    "<hh",
                    grid_y // 8,
                    grid_x // 8,
                ),
            ),
        )
        cells = plugin.records_of("CELL")
        expected_body = encode_subrecords(
            [
                (
                    "XCLC",
                    struct.pack("<iiI", grid_x, grid_y, 0),
                )
            ]
        )
        if (
            len(cells) != 1
            or cells[0].form_id != cell_id
            or cells[0].flags != 0
            or len(cells[0].raw) < 24
            or struct.unpack_from("<H", cells[0].raw, 20)[0] != 44
            or cells[0].data != expected_body
            or cells[0].groups != cell_groups
        ):
            self.fail(
                f"{role}-cell-surface",
                f"{role.capitalize()} plugin does not contain one exact "
                "flags-0, FormVersion-44 XCLC-only CELL on the reviewed "
                "WRLD/world-child/block/subblock path.",
            )

        persistent_groups = cell_groups + (
            (6, struct.pack("<I", cell_id)),
            (8, struct.pack("<I", cell_id)),
        )
        placed = (
            plugin.records_of("REFR") +
            plugin.records_of("ACHR")
        )
        if not placed or any(
            record.groups != persistent_groups
            for record in placed
        ):
            self.fail(
                f"{role}-placement-group-path",
                f"{role.capitalize()} placed references do not all retain "
                "the exact persistent CELL group path.",
            )

    def _verify_companion_finish(
        self,
        request: dict[str, Any],
        proposal: dict[str, Any],
        entries: dict[str, bytes],
    ) -> dict[str, Any]:
        finish = request["companionFinish"]
        actor = request["companion"]
        source_bytes = Path(actor["pluginFile"]["path"]).read_bytes()
        output_bytes = entries[f"Data/{actor['plugin']}"]
        source = parse_plugin(source_bytes)
        output = parse_plugin(output_bytes)
        companion_forbidden = sorted(
            signature
            for signature in FORBIDDEN_SIGNATURES
            if output.records_of(signature)
        )
        source_tes4 = only(source.records_of("TES4"), "companion source TES4")
        output_tes4 = only(output.records_of("TES4"), "companion output TES4")
        source_masters = masters(source_tes4)
        output_masters = masters(output_tes4)
        if output_masters != proposal["companionOutputMasters"]:
            self.fail(
                "companion-plugin-masters",
                "Companion raw TES4 masters differ from the proposal.",
            )
        if output_tes4.flags & 0x200 == 0:
            self.fail(
                "companion-plugin-esl",
                "Companion output lost its ESL flag.",
            )
        hedr = first_subrecord(output_tes4, "HEDR")
        allocation = finish["allocation"]
        if (
            hedr is None
            or len(hedr) < 12
            or struct.unpack_from("<I", hedr, 8)[0]
            != local_id(allocation["nextFormId"])
        ):
            self.fail(
                "companion-plugin-next-form-id",
                "Companion NextFormID differs from the request.",
            )

        source_self = len(source_masters)
        output_self = len(output_masters)
        source_by_local = local_records(source, source_self)
        output_by_local = local_records(output, output_self)
        actor_local = local_id(actor["actorFormId"])
        color_local = local_id(finish["hair"]["colorFormId"])
        first_new = local_id(allocation["privateArmorAddon"])
        for local, source_record in source_by_local.items():
            output_record = output_by_local.get(local)
            if (
                output_record is None
                or output_record.signature != source_record.signature
            ):
                self.fail(
                    "companion-existing-record",
                    f"Existing companion record 0x{local:03X} is missing or changed type.",
                )
                continue
            source_rows = parse_subrecords(source_record.data)
            output_rows = parse_subrecords(output_record.data)
            if local == actor_local:
                output_rows = [
                    row for row in output_rows if row[0] != "DOFT"
                ]
            elif local == color_local:
                source_rows = [
                    row for row in source_rows if row[0] != "CNAM"
                ]
                output_rows = [
                    row for row in output_rows if row[0] != "CNAM"
                ]
            source_data = normalize_self_ids(
                encode_subrecords(source_rows),
                source_self,
                source_by_local,
            )
            output_data = normalize_self_ids(
                encode_subrecords(output_rows),
                output_self,
                source_by_local,
            )
            if (
                source_data != output_data
                or source_record.flags != output_record.flags
            ):
                self.fail(
                    "companion-preservation",
                    f"Companion {source_record.signature} 0x{local:03X} changed outside CLFM/DOFT.",
                )

        expected_new = {
            local_id(allocation["privateArmorAddon"]): "ARMA",
            local_id(allocation["privateArmor"]): "ARMO",
            local_id(allocation["outfit"]): "OTFT",
        }
        if (
            any(local >= first_new for local in source_by_local)
            or set(output_by_local)
            != set(source_by_local) | set(expected_new)
        ):
            self.fail(
                "companion-new-record-inventory",
                "Companion source/output local-ID inventory is not closed.",
            )
        for local, signature in expected_new.items():
            if output_by_local[local].signature != signature:
                self.fail(
                    "companion-new-record-inventory",
                    f"Companion {signature} 0x{local:03X} is missing.",
                )

        cnam = first_subrecord(output_by_local[color_local], "CNAM")
        new_rgb = finish["hair"]["newPackedRgb"]
        if cnam is None or len(cnam) != 4 or tuple(cnam[:3]) != (
            (new_rgb >> 16) & 0xFF,
            (new_rgb >> 8) & 0xFF,
            new_rgb & 0xFF,
        ):
            self.fail(
                "companion-color",
                "Companion CLFM does not carry the requested RGB.",
            )
        doft = first_subrecord(output_by_local[actor_local], "DOFT")
        if unpack_u32(doft) != raw_form(
            output_self,
            local_id(allocation["outfit"]),
        ):
            self.fail(
                "companion-outfit-link",
                "Companion NPC does not point at the private outfit.",
            )

        outfit_masters = output_masters
        target_txst = raw_reference(
            outfit_masters,
            output_self,
            actor["plugin"],
            request["outfit"]["targetFemaleSkinTextureSet"],
        )
        boots = raw_reference(
            outfit_masters,
            output_self,
            actor["plugin"],
            request["outfit"]["boots"],
        )
        gloves = raw_reference(
            outfit_masters,
            output_self,
            actor["plugin"],
            request["outfit"]["gauntlets"],
        )
        addon_local = local_id(allocation["privateArmorAddon"])
        armor_local = local_id(allocation["privateArmor"])
        outfit_local = local_id(allocation["outfit"])
        if (
            unpack_u32(
                first_subrecord(output_by_local[addon_local], "NAM1")
            )
            != target_txst
        ):
            self.fail(
                "companion-private-skin",
                "Companion private ARMA does not bind the COtR TXST.",
            )
        armo_links = [
            unpack_u32(data)
            for signature, data in parse_subrecords(
                output_by_local[armor_local].data
            )
            if signature == "MODL" and len(data) == 4
        ]
        if armo_links != [raw_form(output_self, addon_local)]:
            self.fail(
                "companion-private-armor",
                "Companion private ARMO does not bind exactly one private ARMA.",
            )
        inam = first_subrecord(output_by_local[outfit_local], "INAM")
        if (
            inam is None
            or len(inam) != 12
            or struct.unpack("<III", inam)
            != (
                raw_form(output_self, armor_local),
                boots,
                gloves,
            )
        ):
            self.fail(
                "companion-outfit-items",
                "Companion OTFT does not contain torso/boots/gauntlets.",
            )

        source_nif = Path(actor["faceGeom"]["path"]).read_bytes()
        nif_name = (
            "Data/meshes/actors/character/FaceGenData/FaceGeom/"
            f"{actor['plugin']}/"
            f"{local_id(actor['actorFormId']):08X}.nif"
        )
        output_nif = entries[nif_name]
        nif_result = verify_hair_tint_nif(
            source_nif,
            output_nif,
            finish["hair"],
        )
        for code, message in nif_result["errors"]:
            self.fail(code, message)
        return {
            "sourcePluginSha256":
                hashlib.sha256(source_bytes).hexdigest(),
            "outputPluginSha256":
                hashlib.sha256(output_bytes).hexdigest(),
            "sourceFaceGeomSha256":
                hashlib.sha256(source_nif).hexdigest(),
            "outputFaceGeomSha256":
                hashlib.sha256(output_nif).hexdigest(),
            "faceGeomChangedByteCount":
                nif_result["changedByteCount"],
            "faceGeomAuthorizedEnvelopeByteCount":
                nif_result["authorizedEnvelopeByteCount"],
            "masters": output_masters,
            "forbiddenSignaturesPresent": companion_forbidden,
        }


def verify_hair_tint_nif(
    source: bytes,
    output: bytes,
    hair: dict[str, Any],
) -> dict[str, Any]:
    errors: list[tuple[str, str]] = []
    if len(source) != len(output):
        errors.append(
            (
                "companion-facegeom-length",
                "Companion FaceGeom rewrite changed file length.",
            )
        )
        return {
            "errors": errors,
            "changedByteCount": -1,
            "authorizedEnvelopeByteCount": 0,
        }
    source_header = parse_header(source)
    output_header = parse_header(output)
    source_blocks = parse_blocks(source, source_header)
    output_blocks = parse_blocks(output, output_header)
    if (
        source_header["types"] != output_header["types"]
        or source_header["offs"] != output_header["offs"]
        or source_header["sizes"] != output_header["sizes"]
    ):
        errors.append(
            (
                "companion-facegeom-topology",
                "Companion FaceGeom block table changed.",
            )
        )
    requested_names = hair["faceGeomShapeNames"]
    requested_set = set(requested_names)
    if len(requested_set) != len(requested_names):
        errors.append(
            (
                "companion-facegeom-shapes",
                "Companion FaceGeom request repeats a shape.",
            )
        )
    allowed: set[int] = set()
    shader_indexes: set[int] = set()
    old_rgb = hair["oldFaceGeomRgb"]
    new_rgb = hair["newFaceGeomRgb"]
    for name in requested_names:
        matches = [
            block
            for block in source_blocks
            if block["name"] == name
        ]
        if len(matches) != 1:
            errors.append(
                (
                    "companion-facegeom-shapes",
                    f"Requested hair shape cardinality is not one: {name}",
                )
            )
            continue
        shape = matches[0]
        shader_refs = [
            ref
            for ref in shape["refs"]
            if ref["kind"] == "shader" and ref["target"] >= 0
        ]
        if len(shader_refs) != 1:
            errors.append(
                (
                    "companion-facegeom-shader",
                    f"Requested hair shape does not have one shader: {name}",
                )
            )
            continue
        shader_index = shader_refs[0]["target"]
        if shader_index in shader_indexes:
            errors.append(
                (
                    "companion-facegeom-shader",
                    f"Requested shapes share a shader: {name}",
                )
            )
            continue
        shader_indexes.add(shader_index)
        if any(
            block["name"] not in requested_set
            and any(
                ref["kind"] == "shader"
                and ref["target"] == shader_index
                for ref in block["refs"]
            )
            for block in source_blocks
        ):
            errors.append(
                (
                    "companion-facegeom-shader",
                    f"Hair shader is shared by a non-target shape: {name}",
                )
            )
        shader_offset = source_header["offs"][shader_index]
        shader_size = source_header["sizes"][shader_index]
        if (
            source_header["types"][shader_index]
            != "BSLightingShaderProperty"
            or struct.unpack_from("<I", source, shader_offset)[0] != 6
        ):
            errors.append(
                (
                    "companion-facegeom-shader",
                    f"Requested shape is not HairTint: {name}",
                )
            )
            continue
        tint_offset = shader_offset + shader_size - 12
        for channel in range(3):
            source_bits = struct.unpack_from(
                "<I", source, tint_offset + channel * 4
            )[0]
            output_bits = struct.unpack_from(
                "<I", output, tint_offset + channel * 4
            )[0]
            old_bits = struct.unpack(
                "<I", struct.pack("<f", old_rgb[channel] / 255.0)
            )[0]
            new_bits = struct.unpack(
                "<I", struct.pack("<f", new_rgb[channel] / 255.0)
            )[0]
            if source_bits != old_bits or output_bits != new_bits:
                errors.append(
                    (
                        "companion-facegeom-rgb",
                        f"HairTint old/new value differs: {name} channel {channel}",
                    )
                )
        allowed.update(range(tint_offset, tint_offset + 12))
    changed = {
        index
        for index, (left, right) in enumerate(zip(source, output))
        if left != right
    }
    if not changed or not changed.issubset(allowed):
        errors.append(
            (
                "companion-facegeom-delta",
                "Companion FaceGeom changed outside the exact named HairTint envelope.",
            )
        )
    return {
        "errors": errors,
        "changedByteCount": len(changed),
        "authorizedEnvelopeByteCount": len(allowed),
    }


def bound_file_rows(request: dict[str, Any]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for actor in (request["companion"], request["subject"]):
        rows.extend(
            actor[key]
            for key in (
                "pluginFile",
                "faceGeom",
                "faceTint",
                "bodyGenTemplates",
                "bodyGenMorphs",
                "runtimeScript",
            )
        )
    outfit = request["outfit"]
    rows.extend(
        outfit[key]
        for key in (
            "pluginFile",
            "baseMeshArchive",
            "winningFemaleMeshArchive",
            "textureArchive",
        )
    )
    return rows


def masters(tes4: Any) -> list[str]:
    return [
        data.rstrip(b"\0").decode("latin1")
        for data in all_subrecords(tes4, "MAST")
    ]


def raw_form(index: int, local: int) -> int:
    return (index << 24) | local


def local_id(value: str) -> int:
    parsed = int(value, 0)
    if parsed < 0 or parsed > 0x00FFFFFF:
        raise VerificationError(f"Local FormID is out of range: {value}")
    return parsed


def split_form_reference(value: str) -> tuple[str, int]:
    plugin, separator, local = value.rpartition("|")
    if not separator or not plugin:
        raise VerificationError(f"Form reference is malformed: {value}")
    return plugin, local_id(local)


def raw_reference(
    masters_: list[str],
    self_index: int,
    self_plugin: str,
    value: str,
) -> int:
    plugin, local = split_form_reference(value)
    index = (
        self_index
        if plugin == self_plugin
        else masters_.index(plugin)
    )
    return raw_form(index, local)


def expected_new_records(
    request: dict[str, Any],
) -> dict[int, str]:
    allocation = request["allocation"]
    return {
        local_id(allocation["privateArmorAddon"]): "ARMA",
        local_id(allocation["privateArmor"]): "ARMO",
        local_id(allocation["outfit"]): "OTFT",
        local_id(allocation["package"]): "PACK",
        local_id(allocation["placedActor"]): "ACHR",
        local_id(
            allocation["subjectToCompanionRelationship"]
        ): "RELA",
        local_id(
            allocation["companionToSubjectRelationship"]
        ): "RELA",
    }


def local_records(plugin: Any, self_index: int) -> dict[int, Any]:
    result: dict[int, Any] = {}
    for record in plugin.records:
        if record.signature == "TES4":
            continue
        if record.form_id >> 24 != self_index:
            continue
        local = record.form_id & 0x00FFFFFF
        if local in result:
            raise VerificationError(
                f"Duplicate self local ID 0x{local:08X}"
            )
        result[local] = record
    return result


def normalize_self_ids(
    data: bytes, self_index: int, locals_: Any
) -> bytes:
    normalized = data
    for local in locals_:
        normalized = normalized.replace(
            struct.pack("<I", raw_form(self_index, local)),
            struct.pack("<I", 0xFE000000 | local),
        )
    return normalized


def encode_subrecords(rows: list[tuple[str, bytes]]) -> bytes:
    output = bytearray()
    for signature, data in rows:
        if len(data) > 0xFFFF:
            output.extend(b"XXXX")
            output.extend(struct.pack("<H", 4))
            output.extend(struct.pack("<I", len(data)))
            output.extend(signature.encode("ascii"))
            output.extend(b"\0\0")
        else:
            output.extend(signature.encode("ascii"))
            output.extend(struct.pack("<H", len(data)))
        output.extend(data)
    return bytes(output)


def unpack_u32(data: bytes | None) -> int:
    if data is None or len(data) != 4:
        raise VerificationError("Expected one four-byte FormID subrecord.")
    return struct.unpack("<I", data)[0]


def only(values: list[Any], label: str) -> Any:
    if len(values) != 1:
        raise VerificationError(f"{label} cardinality is not one.")
    return values[0]


def relative(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def unsafe_member(name: str) -> bool:
    pure = PurePosixPath(name)
    return (
        not name
        or "\\" in name
        or ":" in name
        or pure.is_absolute()
        or any(part in {"", ".", ".."} for part in pure.parts)
    )


def is_under(path: Path, root: Path) -> bool:
    try:
        path.resolve().relative_to(root.resolve())
        return True
    except ValueError:
        return False


def file_sha(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest()


def write_new(path: Path, document: dict[str, Any]) -> None:
    path.parent.mkdir(parents=False, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        json.dump(document, stream, indent=2)
        stream.write("\n")


def main(argv: list[str] | None = None) -> int:
    args = parse_args(sys.argv[1:] if argv is None else argv)
    report = PairVerifier(args).verify()
    try:
        write_new(args.report, report)
    except (FileExistsError, OSError) as exception:
        print(f"report-write: {exception}", file=sys.stderr)
        return 2
    return (
        0
        if report["verdict"] == "STATIC_PASS_RUNTIME_REQUIRED"
        else 1
    )


if __name__ == "__main__":
    raise SystemExit(main())
