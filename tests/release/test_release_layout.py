import base64
import hashlib
import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile
from collections.abc import Callable
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import ANY

import pytest


ROOT = Path(__file__).resolve().parents[2]
VERIFY = ROOT / "tools" / "release" / "verify_release.py"
SOURCE_COMMIT = "df154c82772b9ea9ede1c82ad558c524a4716b1f"
SOURCE_TAG = "v1.0.0-preview.243"
EXPECTED_STANDALONE_PYTHON_CASES = 37
EXPECTED_SBOM_PACKAGES = 36
CURRENT_PREVIEW260_PROBE_REPAIR_PATHS = frozenset({
    "catalog.json",
    "npc-create-preflight/Skyrim.esm.base64",
    "npc-create-preflight/preset-bundle.json",
    "npc-create-preflight/record-authority.json",
    "npc-create-preflight/request.json",
    "npc-create-preflight/standalone-assets.json",
})
HISTORICAL_FINISH_FIXTURE_SOURCES = frozenset({
    "finish-verify/diag.txt.base64",
    "finish-verify/finish-core-manifest.json.base64",
    "finish-verify/finish-core-proposal.json.base64",
    "finish-verify/finish-core-request.json.base64",
    "finish-verify/finish-core-verification.json.base64",
    "finish-verify/npc-creation-proposal.json.base64",
    "finish-verify/npcmanager-package.json.base64",
    "finish-verify/output-plugin.esp.base64",
    "finish-verify/output.zip.base64",
    "finish-verify/runtime-identities.json.base64",
    "finish-verify/Skyrim.esm.base64",
    "finish-verify/source-plugin.esp.base64",
})


def _plugin_records(plugin: bytes) -> list[dict[str, object]]:
    records: list[dict[str, object]] = []

    def visit(start: int, end: int) -> None:
        offset = start
        while offset < end:
            assert offset + 24 <= end
            signature = plugin[offset:offset + 4].decode("ascii")
            size = int.from_bytes(plugin[offset + 4:offset + 8], "little")
            form_id = int.from_bytes(plugin[offset + 12:offset + 16], "little")
            data_start = offset + 24
            if signature == "GRUP":
                data_end = offset + size
                assert data_start <= data_end <= end
                visit(data_start, data_end)
            else:
                data_end = data_start + size
                assert data_end <= end
                subrecords: list[tuple[str, int]] = []
                sub_offset = data_start
                while sub_offset < data_end:
                    assert sub_offset + 6 <= data_end
                    sub_signature = plugin[sub_offset:sub_offset + 4].decode("ascii")
                    sub_size = int.from_bytes(
                        plugin[sub_offset + 4:sub_offset + 6], "little")
                    sub_offset += 6 + sub_size
                    assert sub_offset <= data_end
                    subrecords.append((sub_signature, sub_size))
                records.append({
                    "signature": signature,
                    "formId": form_id,
                    "size": size,
                    "subrecords": subrecords,
                })
            offset = data_end

    visit(0, len(plugin))
    return records


def _assert_current_probe_change_is_scoped(
    current_paths: dict[str, Path],
    snapshot_paths: dict[str, Path],
) -> None:
    assert set(current_paths) == set(snapshot_paths)
    differences = {
        relative for relative in current_paths
        if current_paths[relative].read_bytes()
        != snapshot_paths[relative].read_bytes()
    }
    assert differences == CURRENT_PREVIEW260_PROBE_REPAIR_PATHS
    assert all(
        current_paths[relative].read_bytes()
        == snapshot_paths[relative].read_bytes()
        for relative in current_paths
        if relative not in CURRENT_PREVIEW260_PROBE_REPAIR_PATHS
    )


def test_preview281_current_source_preserves_preview280_and_prior_history() -> None:
    build_info = (
        ROOT / "src" / "NpcManager.Application" / "BuildInfo.cs"
    ).read_text(encoding="utf-8-sig")
    assert 'ProductVersion = "1.0.0-preview.281"' in build_info
    assert 'SourceLine = "preview.281-public"' in build_info
    assert (ROOT / "docs" / "alpha-consumer-handoff.md").is_file()
    release280 = (ROOT / "docs" / "releases" / "1.0.0-preview.280.md").read_text(
        encoding="utf-8-sig")
    release280_normalized = " ".join(release280.split())
    assert "compiled selector inventories matched 221 explicit routes and 12 defaults" in release280_normalized
    assert "all 156 runnable routes passed" in release280_normalized
    assert "77 fixture-bound routes remain unexecuted" in release280_normalized
    assert (
        "The user explicitly deferred NTFS-positive verification"
        in release280_normalized
    )
    assert "remote CI" in release280_normalized
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.272.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.274.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.275.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.276.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.277.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.278.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.279.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.280.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.281.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.273.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.271.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.270.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.269.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.260.md").is_file()
    failed_preview259 = (ROOT / "docs" / "releases" / "1.0.0-preview.259.md").read_text(
        encoding="utf-8-sig")
    assert "failed pre-release" in failed_preview259
    assert "never packaged or promoted" in failed_preview259
    assert "exactly 220 Python cases" in failed_preview259
    assert "exactly 224 Python cases" not in failed_preview259
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.258.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.257.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.256.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.255.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.254.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.253.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.252.md").is_file()
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.250.md").is_file()
    readme = (ROOT / "README.md").read_text(encoding="utf-8-sig")
    assert "derived from Preview.280" in readme
    assert "Historical private release results do not certify this modified source snapshot." in readme
    release_278 = (ROOT / "docs" / "releases" / "1.0.0-preview.278.md").read_text(
        encoding="utf-8-sig")
    assert "Phase 3 workspace-policy shadow comparison" in release_278
    assert "existing `KOnlyWorkspacePolicy` remains authoritative" in release_278
    assert "PRIVATE_ONLY" in release_278
    assert "Current private source line: `1.0.0-preview.260`" not in readme
    release_note = (ROOT / "docs" / "releases" / "1.0.0-preview.260.md").read_text(
        encoding="utf-8-sig")
    assert "# Actorwright 1.0.0-preview.260" in release_note
    assert "evidence-only" in release_note
    assert "Preview.258" in release_note
    assert (
        "no runtime, visual, pathing, recruitment, consumer-install, "
        "public-release, or promotion authority"
    ) in " ".join(release_note.split())
    capabilities_doc = (ROOT / "docs" / "product-capabilities.md").read_text(
        encoding="utf-8-sig")
    assert "Frozen `1.0.0-preview.266` baseline" in capabilities_doc
    assert "`1.0.0-preview.269` source/tag" in capabilities_doc
    assert "Historical eight-command protocol-v2-ready contract" in capabilities_doc
    assert "eleven-command\nprotocol-2-ready set" in capabilities_doc
    assert "Consumer installation\nis governed by separate current consumer receipts" in capabilities_doc
    assert (
        "| `1.0.0-preview.277` package | Tagged, independently verified, and "
        "candidate-published private release; approval and installation status require receipts |"
    ) in capabilities_doc
    assert (
        "| `1.0.0-preview.265` source/tag | Failed unpublished final-verifier "
        "checkpoint; never final-rooted, zipped, candidate-published, or "
        "promoted |"
    ) in capabilities_doc
    assert "`1.0.0-preview.262` source/tag" in capabilities_doc
    assert "`1.0.0-preview.261` source/tag" in capabilities_doc
    assert "`1.0.0-preview.260` source/package contract" in capabilities_doc
    assert "`1.0.0-preview.259` source/package contract" not in capabilities_doc
    assert "`1.0.0-preview.258` source/tag" in capabilities_doc
    assert "`1.0.0-preview.257` source/tag" in capabilities_doc
    assert "`1.0.0-preview.256` source/tag" in capabilities_doc
    assert "`1.0.0-preview.255` source/tag" in capabilities_doc
    assert "`1.0.0-preview.254` source/tag" in capabilities_doc
    assert "`1.0.0-preview.253` source/tag" in capabilities_doc
    assert "`1.0.0-preview.252` source/package contract" in capabilities_doc
    assert "`1.0.0-preview.249` source tag | Failed pre-release checkpoint" in capabilities_doc

    verifier = _load_release_verifier()
    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.260"] == {
        "version": "1.0.0-preview.260",
        "sourceLine": "preview.260-private",
        "sourceTag": "v1.0.0-preview.260",
    }
    assert "1.0.0-preview.259" not in verifier.BINARY_RELEASE_IDENTITIES
    assert "1.0.0-preview.259" not in verifier.BINARY_RELEASE_WORKFLOW_COMMANDS
    assert "1.0.0-preview.259" not in verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS

    release_source = (
        ROOT / "tools" / "release" / "build_release.ps1"
    ).read_text(encoding="utf-8-sig")
    package_source = (
        ROOT / "tools" / "build" / "package.ps1"
    ).read_text(encoding="utf-8-sig")
    assert "protocol-v2-schema-exports.json" in release_source
    assert "alpha-consumer-handoff.md" in release_source
    assert "$expectedProtocolV2ReadyCommands" in release_source
    assert "$readyCommands.Count -lt 3" not in release_source
    assert "$expectedProtocolV2WorkflowCommands" in package_source
    assert "@($verification.protocolV2WorkflowCommands).Count -ne 5" not in package_source

    catalog = json.loads((
        ROOT / "tools" / "release" / "protocol-v2-workflow-probes" /
        "catalog.json"
    ).read_text(encoding="utf-8"))
    preview = next(
        row for row in catalog["probes"] if row["command"] == "preview npc")
    assert preview["validator"] == "preview-consumer-required-v1"
    assert preview["fixtures"] == []
    assert [row["command"] for row in catalog["probes"]] == [
        "preview npc",
        "npc finish verify",
        "npc create-from-jslot",
        "preset inspect",
        "workspace preflight",
        "npc assembly preflight",
    ]
    assembly_probe = next(
        row for row in catalog["probes"]
        if row["command"] == "npc assembly preflight")
    assert assembly_probe["validator"] == "npc-assembly-preflight-v1"
    assert assembly_probe["resultSchemaIds"] == [
        "urn:actorwright:protocol-v2:npc-assembly-preflight-result:v1"]
    assert assembly_probe["fixtures"]
    workspace_probe = next(
        row for row in catalog["probes"]
        if row["command"] == "workspace preflight")
    load_order_row = next(
        row for row in workspace_probe["fixtures"]
        if row["destination"] == "load-order.json")
    load_order = json.loads((
        ROOT / "tools" / "release" / "protocol-v2-workflow-probes" /
        load_order_row["source"]
    ).read_text(encoding="utf-8"))
    assert load_order == {
        "schemaVersion": 1,
        "edition": "skyrimse",
        "plugins": [{
            "name": "Probe.esp", "order": 0, "enabled": True,
        }],
    }
    preflight_probe = next(
        row for row in catalog["probes"]
        if row["validator"] == "npc-create-preflight-v1")
    verifier._validate_npc_create_preflight_catalog_probe(preflight_probe)
    plugin_row = next(
        row for row in preflight_probe["fixtures"]
        if row["destination"] == "Data/Skyrim.esm")
    plugin = base64.b64decode(
        (ROOT / "tools" / "release" / "protocol-v2-workflow-probes" /
         "npc-create-preflight" / "Skyrim.esm.base64").read_text(
             encoding="utf-8-sig").strip())
    assert plugin_row["size"] == len(plugin)
    assert plugin_row["sha256"] == hashlib.sha256(plugin).hexdigest().upper()
    records = _plugin_records(plugin)
    assert sum(record["signature"] == "RACE" and record["formId"] == 0x13746
               for record in records) == 1
    npc_records = [
        record for record in records
        if record["signature"] == "NPC_" and record["formId"] == 0x800
    ]
    assert len(npc_records) == 1
    subrecords = npc_records[0]["subrecords"]
    assert subrecords.count(("NAM9", 76)) == 1
    assert subrecords.count(("NAMA", 16)) == 1


def test_preview281_source_identity_preserves_preview280_and_prior_pins() -> None:
    verifier = _load_release_verifier()
    probe_root = (
        ROOT / "tools" / "release" / "protocol-v2-workflow-probes")
    public_current_probe_root = probe_root / "public-current-staging"
    current_probe_root = probe_root / "current-staging"
    preview274_probe_root = probe_root / "preview274"
    preview272_probe_root = probe_root / "preview272"
    expected_current_workflows = (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    )
    expected_frozen_workflows = (
        "npc assembly preflight",
        "npc create-from-jslot",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    )

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.281"] == {
        "version": "1.0.0-preview.281",
        "sourceLine": "preview.281-public",
        "sourceTag": "v1.0.0-preview.281",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.281"] == expected_current_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.281"] == public_current_probe_root
    assert verifier.EXPECTED_COMMAND_COUNTS_BY_VERSION[
        "1.0.0-preview.281"] == 142
    assert verifier._select_binary_release_identity(
        "1.0.0-preview.281", "preview.281-public"
    )["sourceTag"] == "v1.0.0-preview.281"
    assert verifier.REQUIRED_RELEASE_DOCUMENTS_BY_VERSION[
        "1.0.0-preview.281"] == frozenset({
            "docs/external-rendering-prerequisites.md",
            "docs/licenses.md",
            "docs/third-party-licenses.md",
        })
    assert "1.0.0-preview.281" not in verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION
    assert verifier.requires_public_advisory_evidence({
        "version": "1.0.0-preview.281", "privateOnly": False})

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.280"] == {
        "version": "1.0.0-preview.280",
        "sourceLine": "preview.280-private",
        "sourceTag": "v1.0.0-preview.280",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.280"] == expected_current_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.280"] == current_probe_root
    assert verifier.EXPECTED_COMMAND_COUNTS_BY_VERSION[
        "1.0.0-preview.280"] == 142
    assert verifier._select_binary_release_identity(
        "1.0.0-preview.280", "preview.280-private"
    )["sourceTag"] == "v1.0.0-preview.280"
    assert verifier.REQUIRED_RELEASE_DOCUMENTS_BY_VERSION[
        "1.0.0-preview.280"] == frozenset({
            "docs/external-rendering-prerequisites.md",
        })
    assert "1.0.0-preview.280" not in verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.279"] == {
        "version": "1.0.0-preview.279",
        "sourceLine": "preview.279-private",
        "sourceTag": "v1.0.0-preview.279",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.279"] == expected_current_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.279"] == current_probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.279"] == 893
    assert verifier.EXPECTED_COMMAND_COUNTS_BY_VERSION[
        "1.0.0-preview.279"] == 142
    assert verifier._select_binary_release_identity(
        "1.0.0-preview.279", "preview.279-private"
    )["sourceTag"] == "v1.0.0-preview.279"
    assert verifier.REQUIRED_RELEASE_DOCUMENTS_BY_VERSION[
        "1.0.0-preview.279"] == frozenset({
            "docs/external-rendering-prerequisites.md",
        })
    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.278"] == {
        "version": "1.0.0-preview.278",
        "sourceLine": "preview.278-private",
        "sourceTag": "v1.0.0-preview.278",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.278"] == expected_current_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.278"] == current_probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.278"] == 893
    assert verifier.EXPECTED_COMMAND_COUNTS_BY_VERSION[
        "1.0.0-preview.278"] == 142
    assert verifier._select_binary_release_identity(
        "1.0.0-preview.278", "preview.278-private"
    )["sourceTag"] == "v1.0.0-preview.278"
    assert verifier.REQUIRED_RELEASE_DOCUMENTS_BY_VERSION[
        "1.0.0-preview.278"] == frozenset({
            "docs/external-rendering-prerequisites.md",
        })
    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.277"] == {
        "version": "1.0.0-preview.277",
        "sourceLine": "preview.277-private",
        "sourceTag": "v1.0.0-preview.277",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.277"] == expected_current_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.277"] == current_probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.277"] == 893
    assert verifier.EXPECTED_COMMAND_COUNTS_BY_VERSION[
        "1.0.0-preview.277"] == 142
    assert verifier._select_binary_release_identity(
        "1.0.0-preview.277", "preview.277-private"
    )["sourceTag"] == "v1.0.0-preview.277"
    assert verifier.REQUIRED_RELEASE_DOCUMENTS_BY_VERSION[
        "1.0.0-preview.277"] == frozenset({
            "docs/external-rendering-prerequisites.md",
        })
    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.276"] == {
        "version": "1.0.0-preview.276",
        "sourceLine": "preview.276-private",
        "sourceTag": "v1.0.0-preview.276",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.276"] == expected_current_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.276"] == current_probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.276"] == 880
    assert verifier.EXPECTED_COMMAND_COUNTS_BY_VERSION[
        "1.0.0-preview.276"] == 142
    assert verifier._select_binary_release_identity(
        "1.0.0-preview.276", "preview.276-private"
    )["sourceTag"] == "v1.0.0-preview.276"
    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.275"] == {
        "version": "1.0.0-preview.275",
        "sourceLine": "preview.275-private",
        "sourceTag": "v1.0.0-preview.275",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.275"] == expected_current_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.275"] == current_probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.275"] == 317
    assert verifier.EXPECTED_COMMAND_COUNTS_BY_VERSION[
        "1.0.0-preview.275"] == 142
    assert verifier._select_binary_release_identity(
        "1.0.0-preview.275", "preview.275-private"
    )["sourceTag"] == "v1.0.0-preview.275"
    current_schemas = {
        command: _ready_result_schema_ids(command)
        for command in expected_current_workflows
    }
    current_probes = verifier._load_protocol_v2_workflow_probe_catalog(
        set(expected_current_workflows),
        current_schemas,
        release_version="1.0.0-preview.280",
        root=current_probe_root,
    )
    assert {probe["command"] for probe in current_probes} == set(
        expected_current_workflows)

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.274"] == {
        "version": "1.0.0-preview.274",
        "sourceLine": "preview.274-private",
        "sourceTag": "v1.0.0-preview.274",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.274"] == expected_current_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.274"] == preview274_probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.274"] == 317
    assert verifier.EXPECTED_COMMAND_COUNTS_BY_VERSION[
        "1.0.0-preview.274"] == 142
    assert verifier.PREVIEW274_STAGING_SCHEMA_INVENTORY_SHA256 == (
        "AA1E590C2EFA6F5595BFADC4056BDAF01AE150B6ECDEFBA6F4D4E36BD973E0AA")
    assert verifier.CURRENT_STAGING_SCHEMA_INVENTORY_SHA256 == (
        "A77D5A555EF4234B22CEAECAC9D103555D5835FB0BD4F481FD0ADF6D6BFB5C09")

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.272"] == {
        "version": "1.0.0-preview.272",
        "sourceLine": "preview.272-private",
        "sourceTag": "v1.0.0-preview.272",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.272"] == expected_current_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.272"] == preview272_probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.272"] == 315
    assert verifier.EXPECTED_COMMAND_COUNTS_BY_VERSION[
        "1.0.0-preview.272"] == 136
    assert verifier._select_binary_release_identity(
        "1.0.0-preview.272", "preview.272-private"
    )["sourceTag"] == "v1.0.0-preview.272"
    preview272_schemas = {
        command: _ready_result_schema_ids(command)
        for command in expected_current_workflows
    }
    preview272_probes = verifier._load_protocol_v2_workflow_probe_catalog(
        set(expected_current_workflows),
        preview272_schemas,
        release_version="1.0.0-preview.272",
        root=preview272_probe_root,
    )
    assert {probe["command"] for probe in preview272_probes} == set(
        expected_current_workflows)

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.271"] == {
        "version": "1.0.0-preview.271",
        "sourceLine": "preview.271-private",
        "sourceTag": "v1.0.0-preview.271",
    }
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.271"] == 314

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.270"] == {
        "version": "1.0.0-preview.270",
        "sourceLine": "preview.270-private",
        "sourceTag": "v1.0.0-preview.270",
    }
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.270"] == 287

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.269"] == {
        "version": "1.0.0-preview.269",
        "sourceLine": "preview.269-private",
        "sourceTag": "v1.0.0-preview.269",
    }
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.269"] == 286

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.268"] == {
        "version": "1.0.0-preview.268",
        "sourceLine": "preview.268-private",
        "sourceTag": "v1.0.0-preview.268",
    }
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.268"] == 286

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.267"] == {
        "version": "1.0.0-preview.267",
        "sourceLine": "preview.267-private",
        "sourceTag": "v1.0.0-preview.267",
    }
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.267"] == 284

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.266"] == {
        "version": "1.0.0-preview.266",
        "sourceLine": "preview.266-private",
        "sourceTag": "v1.0.0-preview.266",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.266"] == expected_frozen_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.266"] == probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.266"] == 231
    assert verifier._select_binary_release_identity(
        "1.0.0-preview.266", "preview.266-private"
    )["sourceTag"] == "v1.0.0-preview.266"
    frozen_schemas = {
        command: _ready_result_schema_ids(command)
        for command in expected_frozen_workflows
    }
    frozen_probes = verifier._load_protocol_v2_workflow_probe_catalog(
        set(expected_frozen_workflows),
        frozen_schemas,
        release_version="1.0.0-preview.266",
        root=probe_root,
    )
    assert {probe["command"] for probe in frozen_probes} == set(
        expected_frozen_workflows)

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.265"] == {
        "version": "1.0.0-preview.265",
        "sourceLine": "preview.265-private",
        "sourceTag": "v1.0.0-preview.265",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.265"] == expected_frozen_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.265"] == probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.265"] == 234

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.264"] == {
        "version": "1.0.0-preview.264",
        "sourceLine": "preview.264-private",
        "sourceTag": "v1.0.0-preview.264",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.264"] == expected_frozen_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.264"] == probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.264"] == 233

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.263"] == {
        "version": "1.0.0-preview.263",
        "sourceLine": "preview.263-private",
        "sourceTag": "v1.0.0-preview.263",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.263"] == expected_frozen_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.263"] == probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.263"] == 225

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.262"] == {
        "version": "1.0.0-preview.262",
        "sourceLine": "preview.262-private",
        "sourceTag": "v1.0.0-preview.262",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.262"] == expected_frozen_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.262"] == probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.262"] == 225

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.261"] == {
        "version": "1.0.0-preview.261",
        "sourceLine": "preview.261-private",
        "sourceTag": "v1.0.0-preview.261",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.261"] == expected_frozen_workflows
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.261"] == probe_root
    assert verifier.EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION[
        "1.0.0-preview.261"] == 225
    preview261_probes = verifier._load_protocol_v2_workflow_probe_catalog(
        set(expected_frozen_workflows),
        frozen_schemas,
        release_version="1.0.0-preview.261",
        root=probe_root,
    )
    assert {probe["command"] for probe in preview261_probes} == set(
        expected_frozen_workflows)

    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.260"] == {
        "version": "1.0.0-preview.260",
        "sourceLine": "preview.260-private",
        "sourceTag": "v1.0.0-preview.260",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.260"] == (
            "npc assembly preflight",
            "npc create-from-jslot",
            "npc finish verify",
            "preset inspect",
            "preview npc",
            "workspace preflight",
        )
    rollback_workflows = verifier.BINARY_RELEASE_WORKFLOW_COMMANDS[
        "1.0.0-preview.260"]
    rollback_probes = verifier._load_protocol_v2_workflow_probe_catalog(
        set(rollback_workflows),
        {
            command: _ready_result_schema_ids(command)
            for command in rollback_workflows
        },
        release_version="1.0.0-preview.260",
        root=probe_root,
    )
    assert {probe["command"] for probe in rollback_probes} == set(
        rollback_workflows)

    release_script = (
        ROOT / "tools" / "release" / "build_release.ps1"
    ).read_text(encoding="utf-8-sig")
    package_script = (
        ROOT / "tools" / "build" / "package.ps1"
    ).read_text(encoding="utf-8-sig")
    assert "$version = '1.0.0-preview.281'" in release_script
    assert "$tag = 'v1.0.0-preview.281'" in release_script
    assert "docs\\releases\\1.0.0-preview.281.md" in release_script
    assert "sourceLine = 'preview.281-public'" in release_script
    assert "'-SelectorTimeoutSeconds', '600'" in release_script
    assert "exactCommandNames = 142" in release_script
    ready_match = re.search(
        r"\$expectedProtocolV2ReadyCommands\s*=\s*@\((?P<body>.*?)\n\)",
        release_script,
        flags=re.DOTALL,
    )
    assert ready_match is not None
    assert re.findall(r"'([^']+)'", ready_match.group("body")) == [
        "capabilities",
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "schema export",
        "version",
        "workspace preflight",
    ]
    assert "actorwright-1.0.0-preview.281-win-x64" in package_script
    assert package_script.count("'1.0.0-preview.281'") >= 3
    assert package_script.count("'preview.281-public'") >= 3
    workflow_match = re.search(
        r"\$expectedProtocolV2WorkflowCommands\s*=\s*@\((?P<body>.*?)\n\)",
        package_script,
        flags=re.DOTALL,
    )
    assert workflow_match is not None
    assert re.findall(r"'([^']+)'", workflow_match.group("body")) == [
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ]
    assert (ROOT / "docs" / "releases" / "1.0.0-preview.266.md").is_file()


def test_preview252_rollback_identity_remains_closed() -> None:
    verifier = _load_release_verifier()
    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.252"] == {
        "version": "1.0.0-preview.252",
        "sourceLine": "preview.252-private",
        "sourceTag": "v1.0.0-preview.252",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS["1.0.0-preview.252"] == (
        "npc assembly preflight", "npc create-from-jslot", "npc finish verify",
        "preset inspect", "preview npc", "workspace preflight")
    probe_root = (
        ROOT / "tools" / "release" / "protocol-v2-workflow-probes" /
        "preview252")
    assert (probe_root / "catalog.json").is_file()


def test_preview256_rollback_identity_remains_closed() -> None:
    verifier = _load_release_verifier()
    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.256"] == {
        "version": "1.0.0-preview.256",
        "sourceLine": "preview.256-private",
        "sourceTag": "v1.0.0-preview.256",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS["1.0.0-preview.256"] == (
        "npc assembly preflight", "npc create-from-jslot", "npc finish verify",
        "preset inspect", "preview npc", "workspace preflight")
    probe_root = (
        ROOT / "tools" / "release" / "protocol-v2-workflow-probes" /
        "preview256")
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.256"] == probe_root
    assert (probe_root / "catalog.json").is_file()


def test_preview257_rollback_identity_remains_closed() -> None:
    verifier = _load_release_verifier()
    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.257"] == {
        "version": "1.0.0-preview.257",
        "sourceLine": "preview.257-private",
        "sourceTag": "v1.0.0-preview.257",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS["1.0.0-preview.257"] == (
        "npc assembly preflight", "npc create-from-jslot", "npc finish verify",
        "preset inspect", "preview npc", "workspace preflight")
    probe_root = (
        ROOT / "tools" / "release" / "protocol-v2-workflow-probes" /
        "preview257")
    assert (probe_root / "catalog.json").is_file()


def test_preview258_rollback_identity_remains_closed() -> None:
    verifier = _load_release_verifier()
    assert verifier.BINARY_RELEASE_IDENTITIES["1.0.0-preview.258"] == {
        "version": "1.0.0-preview.258",
        "sourceLine": "preview.258-private",
        "sourceTag": "v1.0.0-preview.258",
    }
    assert verifier.BINARY_RELEASE_WORKFLOW_COMMANDS["1.0.0-preview.258"] == (
        "npc assembly preflight", "npc create-from-jslot", "npc finish verify",
        "preset inspect", "preview npc", "workspace preflight")
    probe_root = (
        ROOT / "tools" / "release" / "protocol-v2-workflow-probes" /
        "preview258")
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.258"] == probe_root
    assert (probe_root / "catalog.json").is_file()


def test_external_rendering_prerequisites_are_documented_and_packaged() -> None:
    path = ROOT / "docs" / "external-rendering-prerequisites.md"
    assert path.is_file()
    document = path.read_text(encoding="utf-8-sig")
    required = [
        "https://www.blender.org/download/previous-versions/",
        "https://github.com/BadDogSkyrim/PyNifly",
        "Blender 4.5.1",
        "PyNifly 27.4.0",
        "GNU General Public License",
        "blender-4.5.1-windows-x64/blender-4.5.1-windows-x64/blender.exe",
        "blender-4.5.1-pynifly-profile/scripts/addons/io_scene_nifly",
        "BLENDER_USER_CONFIG",
        "BLENDER_USER_SCRIPTS",
        "BLENDER_USER_DATA",
        "absent",
        "admitted",
        "executed",
        "not bundled",
    ]
    assert all(value in document for value in required)
    handoff = (ROOT / "docs" / "alpha-consumer-handoff.md").read_text(
        encoding="utf-8-sig")
    assert "[external-rendering-prerequisites.md](external-rendering-prerequisites.md)" in handoff
    release_source = (
        ROOT / "tools" / "release" / "build_release.ps1"
    ).read_text(encoding="utf-8-sig")
    assert "docs\\external-rendering-prerequisites.md" in release_source


def test_product_capabilities_distinguishes_source_gate_from_tagged_package() -> None:
    document = (ROOT / "docs" / "product-capabilities.md").read_text(
        encoding="utf-8-sig")
    assert (
        "records preview.266 as packaged, approved on 2026-08-26, and installed by the\nconsumer"
    ) in document
    assert "Its test results must not be attributed to a package." in " ".join(
        document.split())
    assert (
        "`1.0.0-preview.280` source | Current private source identity; release, promotion, "
        "and installation gates pending"
    ) in document
    assert "Preview.276 is the tagged,\nindependently verified, candidate-published" in document
    assert "Preview.277 has a published candidate; promotion and current\nconsumer installation status require separate receipts." in document
    assert "Preview.275 remains an immutable tagged" in document
    assert "Preview.274 remains a historical tagged rollback package" in document
    assert (
        "Preview.272 remains a\ntagged, independently verified private release "
        "directory and deterministic ZIP\nat 136 commands"
    ) in document
    assert "`1.0.0-preview.272` package | Tagged and independently verified" in document
    assert "The current freshly built package inventory is obtained" not in document


COMMAND_SNAPSHOT = (
    ROOT / "tests" / "NpcManager.Cli.Tests" / "Preview231CommandSnapshot.cs")
JSON_SCHEMA_DRAFT = "https://json-schema.org/draft/2020-12/schema"
READY_RESULT_SCHEMAS = {
    "capabilities": "urn:actorwright:protocol-v2:capabilities-result:v1",
    "preview npc": "urn:actorwright:protocol-v2:npc-visual-preview-result:v1",
    "schema export": "urn:actorwright:protocol-v2:schema-export-result:v1",
    "version": "urn:actorwright:protocol-v2:version-result:v1",
    "npc create-from-jslot": (
        "urn:actorwright:protocol-v2:npc-create-preflight-result:v1"),
    "npc finish analyze": (
        "urn:actorwright:protocol-v2:finish-analyze-result:v1"),
    "npc finish apply": (
        "urn:actorwright:protocol-v2:finish-apply-result:v1"),
    "npc finish verify": (
        "urn:actorwright:protocol-v2:finish-verify-result:v1"),
    "preset inspect": (
        "urn:actorwright:protocol-v2:preset-inspect-result:v1"),
    "workspace preflight": (
        "urn:actorwright:protocol-v2:workspace-preflight-result:v1"),
    "npc assembly preflight": (
        "urn:actorwright:protocol-v2:npc-assembly-preflight-result:v1"),
}
NPC_CREATE_BUILD_RESULT_SCHEMA = (
    "urn:actorwright:protocol-v2:npc-create-from-jslot-build-result:v1")
HISTORICAL_PREVIEW250_SOURCE_COMMIT = (
    "fef5ac9b78a4c9b348199b4e4ddc9f3862bade60")
HISTORICAL_PREVIEW250_WORKFLOWS = (
    "npc create-from-jslot", "npc finish verify", "preset inspect",
    "preview npc", "workspace preflight")
HISTORICAL_PREVIEW255_WORKFLOWS = (
    "npc assembly preflight", "npc create-from-jslot", "npc finish verify",
    "preset inspect", "preview npc", "workspace preflight")
CURRENT_PREVIEW260_WORKFLOWS = HISTORICAL_PREVIEW255_WORKFLOWS
PREVIEW258_ROLLBACK_WORKFLOWS = HISTORICAL_PREVIEW255_WORKFLOWS
CURRENT_PREVIEW256_WORKFLOWS = HISTORICAL_PREVIEW255_WORKFLOWS
PREVIEW252_ROLLBACK_WORKFLOWS = HISTORICAL_PREVIEW255_WORKFLOWS
PREVIEW251_ROLLBACK_WORKFLOWS = PREVIEW252_ROLLBACK_WORKFLOWS


def _ready_result_schema_ids(command: str) -> list[str]:
    primary = READY_RESULT_SCHEMAS[command]
    if command == "npc create-from-jslot":
        return [primary, NPC_CREATE_BUILD_RESULT_SCHEMA]
    return [primary]


def _authentic_command_names() -> list[str]:
    names = re.findall(
        r'^\s*"([a-z0-9 -]+)",?\s*$',
        COMMAND_SNAPSHOT.read_text(encoding="utf-8"),
        flags=re.MULTILINE,
    )
    assert len(names) == 142 and len(set(names)) == 142
    return names


AUTHENTIC_COMMAND_NAMES = _authentic_command_names()
VOICE_COMMAND_NAMES = {
    "npc dialogue analyze", "npc dialogue apply", "npc dialogue verify",
    "npc voice discover", "npc voice import", "npc voice synthesize",
}
HISTORICAL_136_COMMAND_NAMES = [
    name for name in AUTHENTIC_COMMAND_NAMES if name not in VOICE_COMMAND_NAMES
]
assert len(HISTORICAL_136_COMMAND_NAMES) == 136


def _bytes(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)


def _json(path: Path, value: object) -> None:
    _bytes(path, (json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n").encode())


def _write_release_hashes(root: Path) -> None:
    lines = []
    for path in sorted(
        (candidate for candidate in root.rglob("*") if candidate.is_file()
         and candidate.name != "SHA256SUMS"),
        key=lambda candidate: candidate.relative_to(root).as_posix(),
    ):
        relative = path.relative_to(root).as_posix()
        lines.append(
            f"{hashlib.sha256(path.read_bytes()).hexdigest().upper()}  {relative}")
    _bytes(root / "SHA256SUMS", ("\n".join(lines) + "\n").encode())


def _replace_release_evidence(
    root: Path,
    relative: str,
    value: object,
) -> None:
    evidence_path = root / relative
    _json(evidence_path, value)
    release_path = root / "actorwright-release.json"
    release = json.loads(release_path.read_text(encoding="utf-8-sig"))
    binding = {
        "evidence/capabilities.json": "capabilitiesSha256",
        "evidence/sbom.spdx.json": "sbomSha256",
        "evidence/test-summary.json": "testSummarySha256",
    }[relative]
    release[binding] = hashlib.sha256(evidence_path.read_bytes()).hexdigest().upper()
    _json(release_path, release)
    _write_release_hashes(root)


def _replace_release_metadata(root: Path, release: object) -> None:
    _json(root / "actorwright-release.json", release)
    _write_release_hashes(root)


def _release(tmp_path: Path) -> Path:
    root = tmp_path / "actorwright-1.0.0-preview.243"
    _bytes(root / "cli" / "actorwright.exe", b"MZ-test-cli")
    _bytes(root / "cli" / "npcm.cmd", b"@echo off\r\n\"%~dp0actorwright.exe\" %*\r\n")
    _bytes(root / "cli" / "actorwright.ps1", (
        b"$exe = Join-Path $PSScriptRoot 'actorwright.exe'; "
        b"if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { "
        b'[Console]::Error.WriteLine("Actorwright executable is missing: $exe"); exit 1 }; '
        b"& $exe @args; exit $LASTEXITCODE\r\n"
    ))
    _bytes(root / "desktop" / "Actorwright.Desktop.exe", b"MZ-test-desktop")
    _json(root / "schemas" / "issue.schema.json", {"$id": "urn:actorwright:exchange:v1:issue"})
    _bytes(root / "docs" / "README.md", b"Actorwright\n")
    _bytes(root / "licenses" / "LICENSE", b"GPL-3.0-only\n")
    capabilities = root / "evidence" / "capabilities.json"
    sbom = root / "evidence" / "sbom.spdx.json"
    tests = root / "evidence" / "test-summary.json"
    renderers = root / "evidence" / "renderer-scripts.json"
    _json(capabilities, {
        "version": "1.0.0-preview.243",
        "sourceLine": "preview.243-private",
        "commands": [{"name": name} for name in HISTORICAL_136_COMMAND_NAMES],
    })
    packages = [
        {
            "SPDXID": f"SPDXRef-Package-Package.{index}-1.0.{index}",
            "name": f"Package.{index}",
            "versionInfo": f"1.0.{index}",
            "downloadLocation": (
                f"https://www.nuget.org/packages/Package.{index}/1.0.{index}"
            ),
            "filesAnalyzed": False,
            "licenseConcluded": "NOASSERTION",
            "licenseDeclared": "NOASSERTION",
            "copyrightText": "NOASSERTION",
            "externalRefs": [{
                "referenceCategory": "PACKAGE-MANAGER",
                "referenceType": "purl",
                "referenceLocator": f"pkg:nuget/Package.{index}@1.0.{index}",
            }],
        }
        for index in range(EXPECTED_SBOM_PACKAGES)
    ]
    _json(sbom, {
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "SPDXID": "SPDXRef-DOCUMENT",
        "name": "Actorwright-1.0.0-preview.243",
        "documentNamespace": (
            "https://actorwright.invalid/spdx/1.0.0-preview.243/"
            f"{SOURCE_COMMIT}"
        ),
        "creationInfo": {
            "created": "2026-08-11T00:00:00Z",
            "creators": ["Tool: Actorwright-generate-sbom"],
        },
        "packages": packages,
        "documentDescribes": [package["SPDXID"] for package in packages],
    })
    _json(tests, {
        "schemaVersion": 1,
        "status": "PASS",
        "sourceCommit": SOURCE_COMMIT,
        "sourceTag": SOURCE_TAG,
        "configuration": "Release",
        "projectsCompiled": 25,
        "warnings": 0,
        "errors": 0,
        "exactCommandNames": 136,
        "standalonePythonCases": EXPECTED_STANDALONE_PYTHON_CASES,
        "legacyWorkspaceBoundSuites": "COMPILE_ONLY",
        "runtimeAuthority": False,
        "visualAuthority": False,
    })
    _json(renderers, {"embedded": 6, "loosePython": 0})
    closure_entries = [
        {"role": role, "path": path, "length": index + 1, "sha256": f"{index + 1:064X}", "storage": storage}
        for index, (role, path, storage) in enumerate(
            [
                ("reference-runtime-manifest", "runtime/reference-preset/runtime-asset-manifest.json", "content"),
                ("native-c-api", "runtime/reference-preset/libmediapipe.dll", "content"),
                ("opencv-runtime", "runtime/reference-preset/opencv_world3410.dll", "content"),
                ("vc-runtime-concurrency", "runtime/reference-preset/concrt140.dll", "content"),
                ("vc-runtime-cpp", "runtime/reference-preset/msvcp140.dll", "content"),
                ("vc-runtime-core", "runtime/reference-preset/vcruntime140.dll", "content"),
                ("vc-runtime-core-1", "runtime/reference-preset/vcruntime140_1.dll", "content"),
                ("face-detector-model", "runtime/reference-preset/blaze_face_short_range.tflite", "content"),
                ("face-landmarker-model", "runtime/reference-preset/face_landmarker.task", "content"),
                ("npc-preview-profile-manifest", "runtime/rendering/npc-preview-profile-manifest.json", "content"),
                ("npc-preview-render-script", "assembly://NpcManager.Rendering/render_npc_preview_bundle.py", "managed-resource"),
                ("product-provider-registry", "runtime/product-fixtures/blank-npc-v1/registry.json", "content"),
                ("product-provider-provenance", "runtime/product-fixtures/blank-npc-v1/provenance.json", "content"),
                ("product-provider-manifest", "runtime/product-fixtures/blank-npc-v1/provider-manifest.json", "content"),
                ("product-provider-template-plugin", "runtime/product-fixtures/blank-npc-v1/Data/ActorwrightBlankNpcProvider.esp", "content"),
                ("product-provider-facegeom-carrier", "runtime/product-fixtures/blank-npc-v1/Data/meshes/actors/character/FaceGenData/FaceGeom/ActorwrightBlankNpcProvider.esp/00000800.nif", "content"),
                ("product-provider-facetint-manifest", "runtime/product-fixtures/blank-npc-v1/facetint-manifest.json", "content"),
                ("product-provider-facetint-source", "runtime/product-fixtures/blank-npc-v1/Data/textures/actors/character/FaceGenData/FaceTint/ActorwrightBlankNpcProvider.esp/00000800.dds", "content"),
                ("product-provider-dependency-manifest", "runtime/product-fixtures/blank-npc-v1/dependency-manifest.json", "content"),
            ]
        )
    ]
    closures = []
    for name, entrypoint in (
        ("cli-resource-closure.json", "cli/actorwright.exe"),
        ("desktop-resource-closure.json", "desktop/Actorwright.Desktop.exe"),
    ):
        probe_path = root / "evidence" / name
        _json(probe_path, {
            "schemaVersion": 1,
            "accepted": True,
            "resourceBase": str(root / "extracted"),
            "runtimeManifestSha256": "B" * 64,
            "entries": closure_entries,
        })
        closures.append({
            "entrypoint": entrypoint,
            "probeSha256": hashlib.sha256(
                probe_path.read_bytes()).hexdigest().upper(),
            "runtimeManifestSha256": "B" * 64,
            "entries": closure_entries,
        })
    release = {
        "schemaVersion": 1,
        "product": "Actorwright",
        "version": "1.0.0-preview.243",
        "sourceCommit": SOURCE_COMMIT,
        "sourceTag": SOURCE_TAG,
        "privateOnly": True,
        "runtimeAuthority": False,
        "visualAuthority": False,
        "capabilitiesSha256": hashlib.sha256(capabilities.read_bytes()).hexdigest().upper(),
        "sbomSha256": hashlib.sha256(sbom.read_bytes()).hexdigest().upper(),
        "testSummarySha256": hashlib.sha256(tests.read_bytes()).hexdigest().upper(),
        "embeddedResourceClosures": closures,
    }
    _json(root / "actorwright-release.json", release)
    _write_release_hashes(root)
    return root


def _binary_metadata_release(
    tmp_path: Path,
    *,
    version: str,
    source_line: str,
    source_tag: str,
    source_commit: str,
    workflow_commands: tuple[str, ...],
) -> Path:
    """Construct a full release evidence root for a closed binary identity."""
    root = _release(tmp_path / "seed")
    release_path = root / "actorwright-release.json"
    release = json.loads(release_path.read_text(encoding="utf-8-sig"))
    release["version"] = version
    release["sourceTag"] = source_tag
    release["sourceCommit"] = source_commit

    capabilities_path = root / "evidence" / "capabilities.json"
    capabilities = json.loads(
        capabilities_path.read_text(encoding="utf-8-sig"))
    capabilities["version"] = version
    capabilities["sourceLine"] = source_line
    _json(capabilities_path, capabilities)

    sbom_path = root / "evidence" / "sbom.spdx.json"
    sbom = json.loads(sbom_path.read_text(encoding="utf-8-sig"))
    sbom["name"] = f"Actorwright-{version}"
    sbom["documentNamespace"] = (
        f"https://actorwright.invalid/spdx/{version}/{source_commit}")
    _json(sbom_path, sbom)

    summary_path = root / "evidence" / "test-summary.json"
    summary = json.loads(summary_path.read_text(encoding="utf-8-sig"))
    summary["sourceCommit"] = source_commit
    summary["sourceTag"] = source_tag
    _json(summary_path, summary)

    kernel_commands = ("capabilities", "schema export", "version")
    ready_commands = set(kernel_commands) | set(workflow_commands)
    protocol_commands = []
    for name in HISTORICAL_136_COMMAND_NAMES:
        protocol_commands.append({
            "name": name,
            "readiness": "v2" if name in ready_commands else "legacy",
            "resultSchemaIds": _ready_result_schema_ids(name)
                if name in ready_commands else [],
        })
    _json(root / "evidence" / "protocol-v2-capabilities.json", {
        "outcome": "succeeded",
        "result": {
            "schemaId": READY_RESULT_SCHEMAS["capabilities"],
            "protocolVersion": "2",
            "commands": protocol_commands,
        },
    })

    def schema_definition(schema_identifier: str) -> dict[str, object]:
        return _result_schema_definition(schema_identifier, [])

    schema_rows = []
    for name in sorted(ready_commands):
        result_schema_ids = _ready_result_schema_ids(name)
        schema_rows.append({
            "command": name,
            "resultSchemaIds": result_schema_ids,
            "export": {
                "schemaId": READY_RESULT_SCHEMAS["schema export"],
                "protocolVersion": "2",
                "scopedHelpResultSchema": schema_definition(
                    "urn:actorwright:protocol-v2:scoped-help-result:v1"),
                "contract": {
                    "name": name,
                    "readiness": "v2",
                    "resultSchemaIds": result_schema_ids,
                },
                "resultSchemas": [
                    schema_definition(identifier)
                    for identifier in result_schema_ids
                ],
                "documentSchemas": [],
            },
        })
    _json(root / "evidence" / "protocol-v2-schema-exports.json", {
        "schemaVersion": 1,
        "productVersion": version,
        "sourceLine": source_line,
        "protocolVersion": "2",
        "commands": schema_rows,
    })

    release["capabilitiesSha256"] = hashlib.sha256(
        capabilities_path.read_bytes()).hexdigest().upper()
    release["sbomSha256"] = hashlib.sha256(
        sbom_path.read_bytes()).hexdigest().upper()
    release["testSummarySha256"] = hashlib.sha256(
        summary_path.read_bytes()).hexdigest().upper()
    _json(release_path, release)
    _write_release_hashes(root)
    return root


def _run(root: Path, *extra: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run([sys.executable, str(VERIFY), str(root), "--metadata-only", *extra], cwd=ROOT, text=True, capture_output=True, check=False)


def _package(tmp_path: Path) -> Path:
    release_root = _release(tmp_path / "release-seed")
    release = json.loads(
        (release_root / "actorwright-release.json").read_text(
            encoding="utf-8-sig"))
    root = tmp_path / "binary-package"
    _bytes(root / "cli" / "actorwright.exe", b"MZ-package-cli")
    _bytes(root / "desktop" / "Actorwright.Desktop.exe", b"MZ-package-desktop")
    for exchange_version in ("v1", "v2"):
        source_root = ROOT / "contracts" / "exchange" / exchange_version
        for schema in sorted(source_root.glob("*.schema.json")):
            raw = schema.read_bytes().replace(b"\r\n", b"\n").replace(b"\r", b"\n")
            _bytes(root / "schemas" / "exchange" / exchange_version / schema.name, raw)
    capabilities = {
        "version": "1.0.0-preview.260",
        "sourceLine": "preview.260-private",
        "commands": [{"name": name} for name in HISTORICAL_136_COMMAND_NAMES],
    }
    _json(root / "capabilities.json", capabilities)
    closures = []
    for name, declaration in zip(
        ("cli-resource-closure.json", "desktop-resource-closure.json"),
        release["embeddedResourceClosures"],
        strict=True,
    ):
        probe = {
            "schemaVersion": 1,
            "accepted": True,
            "resourceBase": str(root / "extracted"),
            "runtimeManifestSha256": declaration["runtimeManifestSha256"],
            "entries": declaration["entries"],
        }
        _json(root / name, probe)
        closures.append({
            **declaration,
            "probeSha256": hashlib.sha256(
                (root / name).read_bytes()).hexdigest(),
        })
    files = []
    for path in sorted(
        (candidate for candidate in root.rglob("*") if candidate.is_file()),
        key=lambda candidate: candidate.relative_to(root).as_posix(),
    ):
        files.append({
            "path": path.relative_to(root).as_posix(),
            "size": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        })
    _json(root / "manifest.json", {
        "schemaVersion": 1,
        "product": "Actorwright",
        "version": "1.0.0-preview.260",
        "sourceLine": "preview.260-private",
        "runtime": "win-x64",
        "privateOnly": True,
        "runtimeAuthority": False,
        "commandCount": 136,
        "embeddedResourceClosures": closures,
        "files": files,
    })
    return root


def _run_package(root: Path, *extra: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(VERIFY), str(root),
         "--package-staging", "--metadata-only", *extra],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )


def _load_release_verifier() -> object:
    spec = importlib.util.spec_from_file_location(
        "actorwright_verify_release_protocol_kernel", VERIFY)
    assert spec is not None and spec.loader is not None
    verifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(verifier)
    return verifier


def _assert_historical_finish_payloads_unavailable(
    verifier: object,
    catalog_root: Path,
) -> None:
    catalog = json.loads(
        (catalog_root / "catalog.json").read_text(encoding="utf-8-sig"))
    missing = {
        fixture["source"]
        for probe in catalog["probes"]
        for fixture in probe["fixtures"]
        if not (catalog_root / Path(*fixture["source"].split("/"))).is_file()
    }
    assert missing == HISTORICAL_FINISH_FIXTURE_SOURCES
    with pytest.raises(
        verifier.ReleaseError, match="historical-positive-unavailable"):
        verifier._require_workflow_fixture_sources(
            catalog["probes"], catalog_root)


def _assert_historical_package_probe_unavailable(
    verifier: object,
    package_root: Path,
    harness: SimpleNamespace,
    monkeypatch: pytest.MonkeyPatch,
    release_version: str,
) -> Path:
    selected: list[tuple[str, Path]] = []
    select_root = verifier._select_protocol_v2_probe_root

    def capture(version: str) -> Path:
        root = select_root(version)
        selected.append((version, root))
        return root

    monkeypatch.setattr(verifier, "_select_protocol_v2_probe_root", capture)
    monkeypatch.setattr(verifier.subprocess, "run", harness.fake_run)
    with pytest.raises(
        verifier.ReleaseError, match="historical-positive-unavailable"):
        verifier.verify_package_staging(package_root, metadata_only=False)
    expected = (
        release_version,
        verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[release_version],
    )
    assert selected and all(selection == expected for selection in selected)
    return selected[0][1]


def _use_test_owned_legacy_probe_fixtures(
    tmp_path: Path,
    verifier: object,
    monkeypatch: pytest.MonkeyPatch,
) -> Path:
    """Supply opaque test-only bytes where excluded legacy Finish inputs were."""
    source_root = verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOT
    catalog_root = tmp_path / "synthetic-legacy-workflow-probes"
    shutil.copytree(source_root, catalog_root)
    catalog_path = catalog_root / "catalog.json"
    catalog = json.loads(catalog_path.read_text(encoding="utf-8-sig"))
    missing: set[str] = set()
    for probe in catalog["probes"]:
        for fixture in probe["fixtures"]:
            relative = fixture["source"]
            source = catalog_root / Path(*relative.split("/"))
            if source.is_file() and relative not in missing:
                continue
            assert probe["command"] == "npc finish verify"
            assert relative in HISTORICAL_FINISH_FIXTURE_SOURCES
            assert fixture["encoding"] == "base64"
            missing.add(relative)
            content = (
                "Actorwright test-only synthetic legacy fixture: "
                + relative + "\n"
            ).encode("ascii")
            if not source.is_file():
                _bytes(source, base64.b64encode(content) + b"\n")
            fixture["size"] = len(content)
            fixture["sha256"] = hashlib.sha256(content).hexdigest().upper()
    assert missing == HISTORICAL_FINISH_FIXTURE_SOURCES
    _json(catalog_path, catalog)
    monkeypatch.setattr(verifier, "PROTOCOL_V2_WORKFLOW_PROBE_ROOT", catalog_root)
    monkeypatch.setattr(
        verifier, "PROTOCOL_V2_WORKFLOW_PROBE_CATALOG", catalog_path)
    return catalog_root


def _result_schema_definition(
    schema_identifier: str,
    required: list[str],
) -> dict[str, object]:
    return {
        "schemaIdentifier": schema_identifier,
        "jsonSchema": {
            "$schema": JSON_SCHEMA_DRAFT,
            "$id": schema_identifier,
            "type": "object",
            "additionalProperties": False,
            "required": required,
            "properties": {
                name: {"type": "object"}
                for name in required
            },
        },
    }


def _document_schema_definition(
    name: str,
    direction: str,
    schema_identifier: str,
) -> dict[str, object]:
    return {
        "name": name,
        "direction": direction,
        "schemaIdentifier": schema_identifier,
        "jsonSchema": {
            "$schema": JSON_SCHEMA_DRAFT,
            "$id": f"urn:actorwright:schema:{schema_identifier}",
            "type": "object",
            "additionalProperties": False,
            "required": ["schema"],
            "properties": {
                "schema": {"const": schema_identifier},
            },
        },
    }


def _finish_analyze_schema_envelope(
    tmp_path: Path,
) -> dict[str, object]:
    harness = _protocol_v2_harness(_package(tmp_path))
    return json.loads(json.dumps(harness.responses[(
        "schema", "export", "--protocol", "2", "--json",
        "--command", "npc finish analyze",
    )][1]))


def _reviewed_intake_schema_definition() -> dict[str, object]:
    return {
        "name": "reviewed-intake",
        "direction": "output",
        "schemaIdentifier": "npcmanager-reviewed-game-intake/2",
        "jsonSchema": json.loads((ROOT / "tests/release/fixtures/preview266/reviewed-game-intake.schema.json").read_text(encoding="utf-8")),
    }


def _npc_create_request_schema_definition() -> dict[str, object]:
    schema = json.loads((ROOT / "tests/release/fixtures/preview266/npc-create-request.schema.json").read_text(encoding="utf-8"))
    return {
        "name": "request",
        "direction": "input",
        "schemaIdentifier": "npc.create-from-jslot.request.v1",
        "jsonSchema": schema,
    }


def _safe_kernel_authority() -> list[dict[str, str]]:
    kinds = [
        "inputAdmission",
        "sourceProviderIdentity",
        "deterministicMaterialization",
        "independentStaticVerification",
        "offEnginePreview",
        "humanVisualAcceptance",
        "gameRuntimeVerification",
        "promotionApproval",
    ]
    return [
        {
            "kind": kind,
            "state": "established" if kind == "inputAdmission" else "notApplicable",
            "reason": f"Fixture authority for {kind}.",
        }
        for kind in kinds
    ]


def _journal_effects(effects: list[dict[str, object]]) -> list[dict[str, object]]:
    return [
        {
            **effect,
            "scope": str(effect["scope"]),
        }
        for effect in effects
    ]


def _kernel_envelope(
    command: str,
    *,
    result: object | None,
    exit_code: int = 0,
    outcome: str = "succeeded",
    diagnostics: list[object] | None = None,
) -> dict[str, object]:
    value: dict[str, object] = {
        "protocolVersion": "2",
        "schemaVersion": "1",
        "command": command,
        "outcome": outcome,
        "exitCode": exit_code,
        "requestDigest": "A" * 64,
        "effects": [
            {
                "kind": "appendLocalOperationJournal",
                "status": "attempted",
                "scope": "workspace-local-journal",
            },
            {
                "kind": "appendLocalOperationJournal",
                "status": "completed",
                "scope": "workspace-local-journal",
            },
        ],
        "diagnostics": diagnostics or [],
        "artifacts": [],
        "authority": _safe_kernel_authority() if exit_code == 0 else [],
        "nextActions": [],
    }
    if result is not None:
        value["result"] = result
    return value


def _workflow_authority() -> list[dict[str, str]]:
    states = {
        "inputAdmission": "established",
        "sourceProviderIdentity": "established",
        "deterministicMaterialization": "established",
        "independentStaticVerification": "established",
        "offEnginePreview": "notApplicable",
        "humanVisualAcceptance": "notApplicable",
        "gameRuntimeVerification": "required",
        "promotionApproval": "notApplicable",
    }
    return [
        {"kind": kind, "state": state, "reason": f"Fixture {kind}."}
        for kind, state in states.items()
    ]


def _bundle_authority(
    artifact_rows: list[dict[str, object]],
) -> list[dict[str, object]]:
    artifacts = {str(row["kind"]): row for row in artifact_rows}

    def evidence(
        kind: str,
        state: str,
        reason: str,
        *artifact_kinds: str,
    ) -> dict[str, object]:
        return {
            "kind": kind, "state": state, "reason": reason,
            "artifactHashes": sorted(
                str(artifacts[name]["sha256"])
                for name in artifact_kinds if name in artifacts),
        }

    def state(kind: str) -> str:
        return "established" if kind in artifacts else "required"

    return [
        evidence(
            "inputAdmission",
            "established" if (
                "reviewed-workspace-intake" in artifacts
                or "npc-package-manifest" in artifacts) else "required",
            "Reviewed intake or its exact verified package lineage establishes input admission.",
            "reviewed-workspace-intake", "npc-package-manifest"),
        evidence(
            "sourceProviderIdentity",
            "established" if (
                "npc-build-preflight" in artifacts
                or "npc-package-manifest" in artifacts) else "required",
            "Reviewed preflight or its exact verified package lineage binds source-provider identity.",
            "npc-build-preflight", "npc-package-manifest"),
        evidence(
            "deterministicMaterialization",
            "established" if (
                "npc-package-manifest" in artifacts
                or "npc-finish-core-manifest" in artifacts) else "required",
            "Only hash-bound package or Finish Core manifests establish deterministic materialization.",
            "npc-package-manifest", "npc-finish-core-manifest"),
        evidence(
            "independentStaticVerification",
            "established" if (
                "npc-package-manifest" in artifacts
                or "npc-finish-core-verification" in artifacts) else "required",
            "A retained-read package or Finish verification artifact establishes independent static verification.",
            "npc-package-manifest", "npc-finish-core-verification"),
        evidence("offEnginePreview", state("npc-preview-manifest"),
                 "Preview production is off-engine and is not human visual acceptance.",
                 "npc-preview-manifest"),
        evidence("humanVisualAcceptance", "required",
                 "Operator-attested review does not establish human visual acceptance."),
        evidence("gameRuntimeVerification", "required",
                 "Generic runtime bytes do not establish structurally verified game runtime evidence.",
                 "runtime-evidence"),
        evidence("promotionApproval", "required",
                 "Workflow artifacts and review receipts never grant promotion approval."),
    ]


def _bundle_actions(
    artifact_rows: list[dict[str, object]],
) -> list[dict[str, object]]:
    artifacts = {str(row["kind"]): row for row in artifact_rows}
    signature = tuple(sorted(artifacts))
    if signature == ("reviewed-workspace-intake",):
        return [{
            "command": "preset inspect",
            "reason": "Inspect an explicitly supplied RaceMenu JSlot.",
            "requiredBindings": [
                {"option": "--format", "value": "racemenu-jslot",
                 "artifactSha256": None},
                {"option": "--edition", "value": "skyrimse",
                 "artifactSha256": None}],
            "missingPrerequisites": [
                "--input", "--input-sha256", "--inspection-output",
                "--workflow-bundle", "--workflow-bundle-sha256",
                "--workflow-output"],
            "requiresHumanAction": False,
        }]
    if signature == ("racemenu-jslot", "reviewed-workspace-intake"):
        preset = artifacts["racemenu-jslot"]
        return [{
            "command": "npc create-from-jslot",
            "reason": "Preflight the exact admitted JSlot through the real NPC build gate.",
            "requiredBindings": [
                {"option": "--preset", "value": preset["path"],
                 "artifactSha256": preset["sha256"]},
                {"option": "--preset-sha256", "value": preset["sha256"],
                 "artifactSha256": preset["sha256"]}],
            "missingPrerequisites": [
                "--request", "--request-sha256", "--data-root", "--plugins",
                "--companion-root", "--preflight-output",
                "--workflow-bundle", "--workflow-bundle-sha256",
                "--workflow-output"],
            "requiresHumanAction": False,
        }]
    if signature == (
        "npc-build-preflight", "racemenu-jslot",
        "reviewed-workspace-intake"):
        preset = artifacts["racemenu-jslot"]
        preflight = artifacts["npc-build-preflight"]
        return [{
            "command": "npc create-from-jslot",
            "reason": "Build the exact reviewed JSlot preflight through the existing static NPC pipeline.",
            "requiredBindings": [
                {"option": "--preset", "value": preset["path"],
                 "artifactSha256": preset["sha256"]},
                {"option": "--preset-sha256", "value": preset["sha256"],
                 "artifactSha256": preset["sha256"]},
                {"option": "--reviewed-preflight", "value": preflight["path"],
                 "artifactSha256": preflight["sha256"]},
                {"option": "--reviewed-preflight-sha256",
                 "value": preflight["sha256"],
                 "artifactSha256": preflight["sha256"]}],
            "missingPrerequisites": [
                "--request", "--request-sha256", "--data-root", "--plugins",
                "--companion-root", "--workflow-bundle",
                "--workflow-bundle-sha256", "--workflow-output"],
            "requiresHumanAction": False,
        }]
    if signature == ("npc-finish-core-verification", "package-archive"):
        return [{
            "command": "runtime smoke verify",
            "reason": "Runtime authority requires a typed runtime report and package acceptance.",
            "requiredBindings": [{
                "option": "--edition", "value": "skyrimse",
                "artifactSha256": None}],
            "missingPrerequisites": [
                "--runtime-report", "--package-acceptance"],
            "requiresHumanAction": True,
        }]
    raise AssertionError(f"unexpected fixture workflow signature: {signature}")


def _workflow_binding(artifact: dict[str, object]) -> dict[str, object]:
    return {
        "kind": artifact["kind"],
        "schemaOrMediaType": artifact["schemaOrMediaType"],
        "path": artifact["path"],
        "size": artifact["size"],
        "sha256": artifact["sha256"],
        "producerCommand": artifact["producerCommand"],
        "requestDigest": artifact["requestDigest"],
        "inputArtifactHashes": artifact["inputBindings"],
        "semanticSha256": None,
    }


def _write_fixture_workflow(
    output: Path,
    request_digest: str,
    npc: dict[str, object],
    phase: str,
    bindings: list[dict[str, object]],
    producer_command: str,
) -> tuple[dict[str, object], dict[str, object]]:
    document = {
        "schema": "actorwright.agent-workflow-bundle.v1",
        "workflowKind": "skyrim-jslot-follower-v1",
        "game": "skyrimSpecialEdition",
        "npc": npc,
        "phase": phase,
        "requestDigest": request_digest,
        "artifacts": bindings,
        "authority": _bundle_authority(bindings),
        "nextActions": _bundle_actions(bindings),
    }
    content = json.dumps(
        document, ensure_ascii=False, indent=2,
        separators=(",", ": ")).encode("utf-8")
    output.write_bytes(content)
    sha256 = hashlib.sha256(content).hexdigest().upper()
    artifact = {
        "kind": "workflow-bundle",
        "schemaOrMediaType": "actorwright.agent-workflow-bundle.v1",
        "path": str(output), "size": len(content), "sha256": sha256,
        "producerCommand": producer_command,
        "requestDigest": request_digest,
        "inputBindings": sorted(str(row["sha256"]) for row in bindings),
        "state": "independentlyVerified",
    }
    return document, artifact


def _rewrite_fixture_workflow(
    artifact: dict[str, object],
    mutate: Callable[[dict[str, object]], None],
) -> dict[str, object]:
    path = Path(str(artifact["path"]))
    document = json.loads(path.read_text(encoding="utf-8"))
    mutate(document)
    content = json.dumps(
        document, ensure_ascii=False, indent=2,
        separators=(",", ": ")).encode("utf-8")
    path.write_bytes(content)
    artifact["size"] = len(content)
    artifact["sha256"] = hashlib.sha256(content).hexdigest().upper()
    return document


def _project_bundle_action(
    document: dict[str, object],
    workflow_artifact: dict[str, object],
) -> list[dict[str, object]]:
    actions = json.loads(json.dumps(document["nextActions"]))
    for action in actions:
        missing = action["missingPrerequisites"]
        if "--workflow-bundle" in missing:
            missing.remove("--workflow-bundle")
            missing.remove("--workflow-bundle-sha256")
            action["requiredBindings"].extend([
                {"option": "--workflow-bundle",
                 "value": workflow_artifact["path"],
                 "artifactSha256": workflow_artifact["sha256"]},
                {"option": "--workflow-bundle-sha256",
                 "value": workflow_artifact["sha256"],
                 "artifactSha256": workflow_artifact["sha256"]},
            ])
        for binding in action["requiredBindings"]:
            if binding.get("artifactSha256") is None:
                binding.pop("artifactSha256")
    return actions


def _workflow_schema_envelope(
    command: str = "workspace preflight",
    preset_document_schema: str = "actorwright-preset-inspection/2",
    release_version: str = "1.0.0-preview.260",
) -> dict[str, object]:
    result_schema = READY_RESULT_SCHEMAS[command]
    result_schema_ids = _ready_result_schema_ids(command)
    probe_root = (
        ROOT / "tools" / "release" / "protocol-v2-workflow-probes")
    if command == "preview npc":
        json_schema = {
            "$schema": JSON_SCHEMA_DRAFT,
            "$id": result_schema,
            "title": "Actorwright protocol v2 NPC visual preview result",
            "type": "object",
            "additionalProperties": False,
            "required": [
                "composed", "status", "runtimeAuthority", "bundlePath",
                "bundleSize", "bundleSha256", "hashManifestPath",
                "hashManifestSize", "hashManifestSha256",
                "contactSheetPath", "contactSheetSha256", "views",
                "diagnostics"],
            "properties": {
                "runtimeAuthority": {"const": False},
            },
            "oneOf": [{"type": "object"}, {"type": "object"}],
            "$defs": {},
        }
        document_schemas = []
    elif command == "preset inspect":
        json_schema = json.loads((
            probe_root / "preset-inspect-result.schema.json"
        ).read_text(encoding="utf-8"))
        if preset_document_schema == "actorwright-preset-inspection/2":
            document_schemas = [
                {
                    "name": "inspection-current",
                    "direction": "output",
                    "schemaIdentifier": "actorwright-preset-inspection/2",
                    "jsonSchema": json.loads((
                        probe_root /
                        "preset-inspection-current-receipt.schema.json"
                    ).read_text(encoding="utf-8")),
                },
                {
                    "name": "inspection-legacy-read",
                    "direction": "input",
                    "schemaIdentifier": "actorwright-preset-inspection/1",
                    "jsonSchema": json.loads((
                        probe_root /
                        "preset-inspection-receipt.schema.json"
                    ).read_text(encoding="utf-8")),
                },
            ]
        else:
            document_schemas = [{
                "name": "inspection",
                "direction": "output",
                "schemaIdentifier": "actorwright-preset-inspection/1",
                "jsonSchema": json.loads((
                    probe_root / "preset-inspection-receipt.schema.json"
                ).read_text(encoding="utf-8")),
            }]
    elif command == "npc create-from-jslot":
        json_schema = json.loads((
            probe_root / "npc-create-preflight-result.schema.json"
        ).read_text(encoding="utf-8"))
        preflight_document = {
            "name": "preflight",
            "direction": "output",
            "schemaIdentifier": "actorwright-npc-build-preflight/1",
            "jsonSchema": json.loads((
                probe_root / "npc-build-preflight-artifact.schema.json"
            ).read_text(encoding="utf-8")),
        }
        if release_version in {
            "1.0.0-preview.252", "1.0.0-preview.253",
            "1.0.0-preview.254", "1.0.0-preview.255",
            "1.0.0-preview.256", "1.0.0-preview.257",
            "1.0.0-preview.258", "1.0.0-preview.260",
        }:
            document_schemas = [
                _npc_create_request_schema_definition(), preflight_document,
            ]
        elif release_version in {
            "1.0.0-preview.251", "1.0.0-preview.250",
        }:
            document_schemas = [preflight_document]
        else:
            raise AssertionError(
                f"unsupported synthetic release version: {release_version}")
    elif command == "npc assembly preflight":
        json_schema = json.loads((
            probe_root / "actor-assembly-preflight-result.schema.json"
        ).read_text(encoding="utf-8"))
        document_schemas = [
            {
                "name": "contract",
                "direction": "input",
                "schemaIdentifier": "npc.actor-assembly-preflight.contract.v1",
                "jsonSchema": json.loads((
                    probe_root / "actor-assembly-contract.schema.json"
                ).read_text(encoding="utf-8")),
            },
            {
                "name": "result",
                "direction": "output",
                "schemaIdentifier": "npc.actor-assembly-preflight.result.v1",
                "jsonSchema": json.loads((
                    probe_root / "actor-assembly-document-result.schema.json"
                ).read_text(encoding="utf-8")),
            },
            {
                "name": "error",
                "direction": "output",
                "schemaIdentifier": "npc.actor-assembly-preflight.error.v1",
                "jsonSchema": json.loads((
                    probe_root / "actor-assembly-error.schema.json"
                ).read_text(encoding="utf-8")),
            },
        ]
    elif command == "npc finish verify":
        json_schema = json.loads((
            probe_root / "finish-verify-result.schema.json"
        ).read_text(encoding="utf-8"))
        document_schemas = ([
            _document_schema_definition(
                "manifest-legacy", "input", "npc.finish-core.manifest.v1"),
            _document_schema_definition(
                "manifest-external", "input", "npc.finish-core.manifest.v2"),
            _document_schema_definition(
                "verification-legacy", "output",
                "npc.finish-core.verification.v1"),
            _document_schema_definition(
                "verification-external", "output",
                "npc.finish-core.verification.v2"),
        ] if release_version in {
            "1.0.0-preview.255", "1.0.0-preview.256",
            "1.0.0-preview.257", "1.0.0-preview.258",
            "1.0.0-preview.260",
        } else [])
    else:
        json_schema = json.loads((
            probe_root / "workspace-preflight-result.schema.json"
        ).read_text(encoding="utf-8"))
        document_schemas = (
            [_reviewed_intake_schema_definition()]
            if release_version in {
                "1.0.0-preview.261", "1.0.0-preview.262",
                "1.0.0-preview.263", "1.0.0-preview.264",
                "1.0.0-preview.265", "1.0.0-preview.266",
                "1.0.0-preview.269", "1.0.0-preview.270",
                "1.0.0-preview.271", "1.0.0-preview.272",
            }
            else [])
    result_schemas = [{
        "schemaIdentifier": result_schema,
        "jsonSchema": json_schema,
    }]
    if command == "npc create-from-jslot":
        result_schemas.append(_result_schema_definition(
            NPC_CREATE_BUILD_RESULT_SCHEMA,
            ["schemaId"],
        ))
    return _kernel_envelope(
        "schema export",
        result={
            "schemaId": READY_RESULT_SCHEMAS["schema export"],
            "protocolVersion": "2",
            "scopedHelpResultSchema": _result_schema_definition(
                "urn:actorwright:protocol-v2:scoped-help-result:v1",
                ["schemaId", "contract"],
            ),
            "contract": {
                "name": command,
                "readiness": "v2",
                "resultSchemaIds": result_schema_ids,
            },
            "resultSchemas": result_schemas,
            "documentSchemas": document_schemas,
        },
    )


def _assembly_preflight_envelope(
    arguments: tuple[str, ...],
) -> dict[str, object]:
    options = {
        arguments[index]: arguments[index + 1]
        for index in range(6, len(arguments), 2)
    }
    contract = Path(options["--contract"])
    assert contract.read_bytes() == b"{}\n"
    assert options["--contract-sha256"] == (
        "CA3D163BAB055381827226140568F3BEF7EAAC187CEBD76878E0B63E9E442356")
    envelope = _kernel_envelope(
        "npc assembly preflight",
        result={
            "schemaVersion": 1,
            "artifactKind": "actor-assembly-preflight-error",
            "contractAdmitted": False,
            "diagnostics": [{
                "code": "npc-build-preflight-validation-failed",
                "severity": "error",
                "message": "actor-assembly-contract-invalid: contract fields are missing.",
            }],
        },
        exit_code=4,
        outcome="failed",
        diagnostics=[{
            "code": "npc-build-preflight-validation-failed",
            "severity": "error",
            "message": "actor-assembly-contract-invalid: contract fields are missing.",
            "class": "validation",
            "recovery": {
                "action": "correctInput",
                "constraint": "contract",
                "artifactKind": "actor-assembly-preflight-result",
                "reason": "Correct the exact Actor Assembly contract binding and retry.",
                "retryUnchangedSafe": False,
            },
        }],
    )
    envelope["effects"] = [
        {"kind": "readWorkspace", "status": "completed", "scope": "workspace"},
        {"kind": "appendLocalOperationJournal", "status": "attempted",
         "scope": "workspace-local-journal"},
        {"kind": "appendLocalOperationJournal", "status": "completed",
         "scope": "workspace-local-journal"},
    ]
    envelope["authority"] = [
        {"kind": "inputAdmission", "state": "blocked",
         "reason": "Input admission is blocked after refusal."},
        {"kind": "sourceProviderIdentity", "state": "established",
         "reason": "The hash-bound package and actor identity evidence were evaluated."},
        {"kind": "deterministicMaterialization", "state": "required",
         "reason": "Result materialization is required after refusal."},
        {"kind": "independentStaticVerification", "state": "required",
         "reason": "Result verification is required after refusal."},
        {"kind": "offEnginePreview", "state": "notApplicable",
         "reason": "Actor Assembly preflight does not render an off-engine preview."},
        {"kind": "humanVisualAcceptance", "state": "required",
         "reason": "Static preflight evidence does not establish human visual acceptance."},
        {"kind": "gameRuntimeVerification", "state": "required",
         "reason": "Static preflight evidence does not establish Skyrim runtime behavior."},
        {"kind": "promotionApproval", "state": "required",
         "reason": "Static preflight does not grant promotion approval."},
    ]
    return envelope


def _workspace_preflight_envelope(
    arguments: tuple[str, ...],
    workspace: Path,
    mutations: set[str],
) -> dict[str, object]:
    options = {
        arguments[index]: arguments[index + 1]
        for index in range(5, len(arguments), 2)
    }
    data_root = Path(options["--data-root"])
    output_root = Path(options["--output-root"])
    load_order = Path(options["--load-order"])
    intake_output = Path(options["--intake-output"])
    plugin = data_root / "Probe.esp"
    load_hash = hashlib.sha256(load_order.read_bytes()).hexdigest().upper()
    plugin_hash = hashlib.sha256(plugin.read_bytes()).hexdigest().upper()
    asset_hash = hashlib.sha256(b"").hexdigest().upper()
    document = {
        "schemaVersion": "2",
        "edition": "skyrimse",
        "isAccepted": True,
        "workspaceRoot": str(workspace),
        "dataRoot": str(data_root),
        "loadOrderPath": str(load_order),
        "outputRoot": str(output_root),
        "loadOrderHash": load_hash.lower(),
        "assetIndexFingerprint": asset_hash.lower(),
        "intakeFingerprint": "",
        "plugins": [{
            "plugin": "Probe.esp",
            "order": 0,
            "active": True,
            "requested": True,
            "requiredMaster": False,
            "sourceHash": plugin_hash.lower(),
            "masters": [],
        }],
        "bodySidecarCount": 0,
        "generatedPluginCount": 0,
        "generatedSidecarCount": 0,
        "bodySidecars": [],
        "generatedPlugins": [],
        "generatedSidecars": [],
        "assetProviderCount": 0,
        "runtimeAuthority": False,
        "diagnostics": [],
    }
    intake_parts = [
        document["edition"],
        document["workspaceRoot"],
        document["dataRoot"],
        document["loadOrderPath"],
        document["outputRoot"],
        document["loadOrderHash"],
        document["plugins"][0]["plugin"],
        str(document["plugins"][0]["order"]),
        document["plugins"][0]["sourceHash"],
        document["assetIndexFingerprint"],
    ]
    intake_bytes = b"".join(
        str(value).encode("utf-8") + b"\0" for value in intake_parts)
    document["intakeFingerprint"] = hashlib.sha256(
        intake_bytes).hexdigest()
    if "workflow-nested-type" in mutations:
        document["bodySidecars"] = 0
    if "workflow-nested-count" in mutations:
        document["bodySidecarCount"] = 1
    if "workflow-plugin-hash" in mutations:
        document["plugins"][0]["sourceHash"] = "F" * 64
    if "workflow-fingerprint-asset" in mutations:
        document["assetIndexFingerprint"] = "F" * 64
    if "workflow-fingerprint-intake" in mutations:
        document["intakeFingerprint"] = "F" * 64
    if "workflow-integer-bool" in mutations:
        document["plugins"][0]["order"] = False
        document["bodySidecarCount"] = False
        document["generatedPluginCount"] = False
        document["generatedSidecarCount"] = False
    content = json.dumps(
        document, ensure_ascii=False, indent=2,
        separators=(",", ": ")).replace("\n", "\r\n").encode("utf-8")
    if "workflow-line-ending-mix" in mutations:
        content = content.replace(b"\r\n", b"\n", 1)
    intake_output.write_bytes(content)
    artifact_sha = hashlib.sha256(content).hexdigest().upper()
    result = json.loads(json.dumps(document))
    request_digest = "E" * 64
    intake_artifact: dict[str, object] = {
        "kind": "reviewed-workspace-intake",
        "schemaOrMediaType": "npcmanager-reviewed-game-intake/2",
        "path": str(intake_output),
        "size": len(content),
        "sha256": artifact_sha,
        "producerCommand": "workspace preflight",
        "requestDigest": request_digest,
        "inputBindings": sorted([
            load_hash, plugin_hash,
            str(document["assetIndexFingerprint"]).upper()]),
        "state": "independentlyVerified",
    }
    workflow_document, workflow_artifact = _write_fixture_workflow(
        Path(options["--workflow-output"]),
        request_digest,
        {"editorId": options["--npc-editor-id"], "displayName": None,
         "plugin": None, "localFormId": None},
        "analyze",
        [_workflow_binding(intake_artifact)],
        "workspace preflight")
    envelope: dict[str, object] = {
        "protocolVersion": "2",
        "schemaVersion": "1",
        "command": "workspace preflight",
        "outcome": "succeeded",
        "exitCode": 0,
        "requestDigest": request_digest,
        "effects": [
            {"kind": "readWorkspace", "status": "completed", "scope": "workspace"},
            {"kind": "writeNewArtifact", "status": "completed", "scope": "k-local-output"},
            {"kind": "appendLocalOperationJournal", "status": "attempted", "scope": "workspace-local-journal"},
            {"kind": "appendLocalOperationJournal", "status": "completed", "scope": "workspace-local-journal"},
        ],
        "diagnostics": [],
        "artifacts": [intake_artifact, workflow_artifact],
        "authority": _workflow_authority(),
        "nextActions": _project_bundle_action(
            workflow_document, workflow_artifact),
        "result": result,
    }
    effects = envelope["effects"]
    authority = envelope["authority"]
    actions = envelope["nextActions"]
    artifacts = envelope["artifacts"]
    assert isinstance(effects, list) and isinstance(authority, list)
    assert isinstance(actions, list) and isinstance(artifacts, list)
    if "workflow-effect" in mutations:
        effects[0]["scope"] = str(workspace)
    if "workflow-authority" in mutations:
        authority[0]["state"] = "blocked"
    if "workflow-action" in mutations:
        actions[0]["missingPrerequisites"] = ["--contract"]
    if "workflow-artifact" in mutations:
        artifacts[0]["state"] = "generated"
    if "workflow-result-extra" in mutations:
        result["unexpected"] = True
    if "workflow-result-mismatch" in mutations:
        result["assetIndexFingerprint"] = "F" * 64
    if "workflow-binding-missing" in mutations:
        artifacts[0]["inputBindings"].remove(asset_hash)
    if "workflow-binding-extra" in mutations:
        artifacts[0]["inputBindings"] = sorted(
            [*artifacts[0]["inputBindings"], "F" * 64])
    if "workflow-bundle-signature" in mutations:
        _rewrite_fixture_workflow(
            workflow_artifact,
            lambda bundle: bundle["artifacts"][0].__setitem__(
                "kind", "npc-finish-core-manifest"))
    if "workflow-bundle-hash" in mutations:
        workflow_artifact["sha256"] = "F" * 64

    operations = workspace / ".actorwright" / "operations"
    operations.mkdir(parents=True, exist_ok=True)
    journal = {
        "command": "workspace preflight",
        "requestDigest": envelope["requestDigest"],
        "effects": _journal_effects(effects[:3]),
        "diagnosticCodes": [],
        "diagnosticClasses": [],
        "artifactHashes": sorted([artifact_sha, workflow_artifact["sha256"]]),
        "durationMilliseconds": 1,
        "outcome": "succeeded",
        "exitCode": 0,
    }
    journal_name = f"{envelope['requestDigest']}-{'1':0>20}.json"
    (operations / journal_name).write_text(
        json.dumps(journal, separators=(",", ":")), encoding="utf-8")
    return envelope


def _preset_inspect_envelope(
    arguments: tuple[str, ...],
    workspace: Path,
    document_schema: str = "actorwright-preset-inspection/2",
) -> dict[str, object]:
    options = {
        arguments[index]: arguments[index + 1]
        for index in range(5, len(arguments), 2)
    }
    source = Path(options["--input"])
    output = Path(options["--inspection-output"])
    source_bytes = source.read_bytes()
    source_sha = hashlib.sha256(source_bytes).hexdigest().upper()
    appearance = {
        "gender": None, "headParts": [], "hairColor": None,
        "weight": {"value": 50, "thin": None, "muscular": None, "fat": None},
        "morphs": {}, "bodyMorphs": {}, "customMorphs": {},
        "sliderMorphs": [], "tints": [], "overlays": [], "skin": None,
        "presence": {
            "gender": False, "headParts": False, "hairColor": False,
            "weight": True, "morphs": False, "bodyMorphs": False,
            "tints": False, "overlays": False, "skin": False,
            "fallout4BodyMorphs": False, "chargenFaceMorphs": False,
            "faceBoneRegions": False, "facialMorphIntensity": False,
        },
        "unknownFields": [], "fallout4BodyMorphs": None,
        "chargenFaceMorphs": None, "faceBoneRegions": None,
        "facialMorphIntensity": 1,
        "raceMenu": {
            "headTexture": None, "faceMorphPresets": [],
            "sculptDivisor": 10000, "sculptParts": [],
            "bodyMorphsKeyed": {}, "bodyOverlays": [],
            "nodeTransforms": [], "skinOverrides": [],
            "version": {
                "formatVersion": 4, "runtimeVersion": 17039360,
                "signature": 1397442898, "skseVersion": 131840},
            "faceTextures": [], "modNames": [], "mods": [],
        },
        "orderedCustomMorphs": [],
    }
    receipt = {
        "schema": document_schema,
        "sourcePath": str(source), "sourceSha256": source_sha,
        "format": "racemenu-jslot", "edition": "skyrimse",
        "isValid": True, "appearance": appearance, "diagnostics": [],
    }
    receipt_bytes = json.dumps(receipt, separators=(",", ":")).encode()
    output.write_bytes(receipt_bytes)
    receipt_sha = hashlib.sha256(receipt_bytes).hexdigest().upper()
    request_digest = "D" * 64
    effects = [
        {"kind": "readWorkspace", "status": "completed", "scope": "workspace"},
        {"kind": "writeNewArtifact", "status": "completed", "scope": "k-local-output"},
    ]
    if document_schema == "actorwright-preset-inspection/2":
        effects.append(
            {"kind": "writeNewArtifact", "status": "completed", "scope": "k-local-output"})
    effects.extend([
        {"kind": "appendLocalOperationJournal", "status": "attempted", "scope": "workspace-local-journal"},
        {"kind": "appendLocalOperationJournal", "status": "completed", "scope": "workspace-local-journal"},
    ])
    artifacts = [
        {
            "kind": "racemenu-jslot", "schemaOrMediaType": "application/json",
            "path": str(source), "size": len(source_bytes), "sha256": source_sha,
            "producerCommand": "preset inspect", "requestDigest": request_digest,
            "inputBindings": [], "state": "independentlyVerified",
        },
        {
            "kind": "preset-inspection",
            "schemaOrMediaType": document_schema,
            "path": str(output), "size": len(receipt_bytes), "sha256": receipt_sha,
            "producerCommand": "preset inspect", "requestDigest": request_digest,
            "inputBindings": [source_sha], "state": "independentlyVerified",
        },
    ]
    input_workflow = json.loads(
        Path(options["--workflow-bundle"]).read_text(encoding="utf-8"))
    workflow_document, workflow_artifact = _write_fixture_workflow(
        Path(options["--workflow-output"]),
        request_digest,
        input_workflow["npc"],
        "analyze",
        [*input_workflow["artifacts"], _workflow_binding(artifacts[0])],
        "preset inspect")
    artifacts.append(workflow_artifact)
    authority_states = {
        "inputAdmission": "established", "sourceProviderIdentity": "required",
        "deterministicMaterialization": "established",
        "independentStaticVerification": "established",
        "offEnginePreview": "notApplicable",
        "humanVisualAcceptance": "notApplicable",
        "gameRuntimeVerification": "required",
        "promotionApproval": "notApplicable",
    }
    envelope: dict[str, object] = {
        "protocolVersion": "2", "schemaVersion": "1",
        "command": "preset inspect", "outcome": "succeeded", "exitCode": 0,
        "requestDigest": request_digest, "effects": effects, "diagnostics": [],
        "artifacts": artifacts,
        "authority": [{"kind": kind, "state": state, "reason": f"Fixture {kind}."}
                      for kind, state in authority_states.items()],
        "nextActions": _project_bundle_action(
            workflow_document, workflow_artifact),
        "result": {
            "schemaVersion": "1", "format": "racemenu-jslot",
            "edition": "skyrimse", "sourcePath": str(source),
            "sourceSha256": source_sha, "isValid": True,
            "appearance": appearance, "diagnostics": [],
            "inspectionPath": str(output), "inspectionSize": len(receipt_bytes),
            "inspectionSha256": receipt_sha,
        },
    }
    operations = workspace / ".actorwright" / "operations"
    operations.mkdir(parents=True, exist_ok=True)
    journal = {
        "command": "preset inspect", "requestDigest": request_digest,
        "effects": _journal_effects(effects[:-1]),
        "diagnosticCodes": [], "diagnosticClasses": [],
        "artifactHashes": sorted(
            [source_sha, receipt_sha, workflow_artifact["sha256"]]),
        "durationMilliseconds": 1, "outcome": "succeeded", "exitCode": 0,
    }
    (operations / f"{request_digest}-{'2':0>20}.json").write_text(
        json.dumps(journal, separators=(",", ":")), encoding="utf-8")
    return envelope


def _npc_create_preflight_envelope(
    arguments: tuple[str, ...],
    workspace: Path,
    mutations: set[str],
) -> dict[str, object]:
    options = {
        arguments[index]: arguments[index + 1]
        for index in range(5, len(arguments), 2)
    }
    request = Path(options["--request"])
    preset = Path(options["--preset"])
    output = Path(options["--preflight-output"])
    request_sha = hashlib.sha256(request.read_bytes()).hexdigest().upper()
    preset_sha = hashlib.sha256(preset.read_bytes()).hexdigest().upper()
    gates = [
        {"id": name, "required": True, "passed": True, "detail": "passed"}
        for name in (
            "request-authority", "preset-authority", "winning-records",
            "appearance-plan", "asset-authority", "facegeom-codec",
            "final-dependency-closure", "output-policy")
    ]
    optional = [{
        "id": "preview:dependency-preflight", "required": False,
        "passed": False, "detail": "not exercised by static preflight"
    }]
    document = {
        "schema": "actorwright-npc-build-preflight/1",
        "product": "Actorwright", "productVersion": "1.0.0-preview.243",
        "sourceLine": "preview.243-private", "executableSha256": None,
        "derivationVersion": 1, "sourceRequest": str(request),
        "sourceRequestSha256": request_sha.lower(),
        "presetSha256": preset_sha.lower(),
        "race": "Skyrim.esm|0x00013746", "sex": "Female",
        "winningPluginOrder": ["Skyrim.esm"],
        "authorities": [{
            "role": "execution-request", "origin": "workspace",
            "identity": str(request), "sha256": request_sha.lower(),
        }],
        "headParts": [],
        "appearance": [{
            "field": "provider", "providerValue": "actorwright-blank-npc-v1",
            "effectiveValue": "actorwright-blank-npc-v1",
            "authority": "D52E5BCC5070C8F41D814F0FBAAF843A1256B7B402C90F44319ECCFA43BC9FFB",
        }],
        "finalDependencyClosure": [{
            "role": "final-CharGen-NIF", "origin": "workspace",
            "identity": str(workspace / "npc-preflight" / "face.nif"),
            "sha256": "89478637C5F31E673CB955622DA9A30ECFDC39E90EB41B97017C7E9F1EFB3692".lower(),
        }],
        "requiredGates": gates, "optionalPreview": optional,
        "plannedOutputs": [{
            "role": "plugin", "path": str(workspace / "planned-npc-output" /
                                               "Data" / "PackagedNpc.esp")
        }],
        "readyForBuild": True, "previewReady": False,
        "runtimeAuthority": False,
    }
    if "npc-artifact-document" in mutations:
        document["readyForBuild"] = False
    content = json.dumps(document, separators=(",", ":")).encode()
    output.write_bytes(content)
    artifact_sha = hashlib.sha256(content).hexdigest().upper()
    binding_hashes = sorted({
        value.upper() for value in (
            request_sha, preset_sha,
            *(row["sha256"] for row in document["authorities"]),
            *(row["sha256"] for row in document["finalDependencyClosure"]),
        )
    })
    request_digest = "C" * 64
    effects = [
        {"kind": "readWorkspace", "status": "completed", "scope": "workspace"},
        {"kind": "writeNewArtifact", "status": "completed", "scope": "k-local-output"},
        {"kind": "appendLocalOperationJournal", "status": "attempted", "scope": "workspace-local-journal"},
        {"kind": "appendLocalOperationJournal", "status": "completed", "scope": "workspace-local-journal"},
    ]
    preflight_artifact: dict[str, object] = {
        "kind": "npc-build-preflight",
        "schemaOrMediaType": "actorwright-npc-build-preflight/1",
        "path": str(output), "size": len(content), "sha256": artifact_sha,
        "producerCommand": "npc create-from-jslot",
        "requestDigest": request_digest, "inputBindings": binding_hashes,
        "state": "independentlyVerified",
    }
    input_workflow = json.loads(
        Path(options["--workflow-bundle"]).read_text(encoding="utf-8"))
    npc = dict(input_workflow["npc"])
    npc["displayName"] = npc.get("displayName") or "Probe NPC"
    npc["plugin"] = npc.get("plugin") or "PackagedNpc.esp"
    workflow_document, workflow_artifact = _write_fixture_workflow(
        Path(options["--workflow-output"]), request_digest, npc, "apply",
        [*input_workflow["artifacts"],
         _workflow_binding(preflight_artifact)],
        "npc create-from-jslot")
    envelope = {
        "protocolVersion": "2", "schemaVersion": "1",
        "command": "npc create-from-jslot", "outcome": "succeeded",
        "exitCode": 0, "requestDigest": request_digest, "effects": effects,
        "diagnostics": [{
            "code": "npc-build-preflight-info", "severity": "info",
            "message": "The request was admitted.", "class": "validation",
            "recovery": {"action": "correctInput", "option": None,
                         "artifactKind": "npc-build-preflight",
                         "constraint": "Correct inputs if the authority drifts.",
                         "retryUnchangedSafe": False},
        }],
        "artifacts": [preflight_artifact, workflow_artifact],
        "authority": _workflow_authority(),
        "nextActions": _project_bundle_action(
            workflow_document, workflow_artifact),
        "result": {
            "created": True, "readyForBuild": True, "previewReady": False,
            "path": str(output), "sha256": artifact_sha,
            "requiredGates": gates, "optionalPreview": optional,
            "diagnostics": [{"code": "preset-npc-request-loaded",
                             "severity": 0,
                             "message": "The request was admitted."}],
        },
    }
    operations = workspace / ".actorwright" / "operations"
    operations.mkdir(parents=True, exist_ok=True)
    journal = {
        "command": "npc create-from-jslot", "requestDigest": request_digest,
        "effects": _journal_effects(effects[:3]),
        "diagnosticCodes": ["npc-build-preflight-info"],
        "diagnosticClasses": ["validation"],
        "artifactHashes": sorted(
            [artifact_sha, workflow_artifact["sha256"]]),
        "durationMilliseconds": 1,
        "outcome": "succeeded", "exitCode": 0,
    }
    if "npc-result" in mutations:
        envelope["result"]["readyForBuild"] = False
    if "npc-binding" in mutations:
        envelope["artifacts"][0]["inputBindings"] = binding_hashes[1:]
    if "workflow-preflight-action" in mutations:
        _rewrite_fixture_workflow(
            workflow_artifact,
            lambda bundle: bundle.__setitem__("nextActions", [{
                "command": "npc create-from-jslot",
                "reason": "False fixture action.",
                "requiredBindings": [],
                "missingPrerequisites": [],
                "requiresHumanAction": False,
            }]))
        for binding in envelope["nextActions"][0]["requiredBindings"]:
            if binding["option"] == "--workflow-bundle-sha256":
                binding["value"] = workflow_artifact["sha256"]
                binding["artifactSha256"] = workflow_artifact["sha256"]
            elif binding["option"] == "--workflow-bundle":
                binding["artifactSha256"] = workflow_artifact["sha256"]
        journal["artifactHashes"] = sorted(
            [artifact_sha, workflow_artifact["sha256"]])
    if "npc-journal" in mutations:
        journal["artifactHashes"] = ["F" * 64]
    (operations / f"{request_digest}-{'3':0>20}.json").write_text(
        json.dumps(journal, separators=(",", ":")), encoding="utf-8")
    return envelope


def _finish_verify_envelope(
    arguments: tuple[str, ...],
    workspace: Path,
) -> dict[str, object]:
    options = {
        arguments[index]: arguments[index + 1]
        for index in range(6, len(arguments), 2)
    }
    manifest = Path(options["--manifest"])
    output = Path(options["--verification-output"])
    manifest_sha = hashlib.sha256(manifest.read_bytes()).hexdigest().upper()
    archive_sha = hashlib.sha256(
        (workspace / "output.zip").read_bytes()).hexdigest().upper()
    verification = {
        "schema": "npc.finish-core.verification.v1",
        "status": "staticPassRuntimeRequired",
        "verified": True,
        "placementIncluded": False,
        "runtimeAuthority": False,
        "visualAuthority": False,
        "typedForbiddenCounts": {
            name: 0 for name in (
                "ACHR", "CELL", "CLMT", "IMGS", "LAND", "LCTN", "LTEX",
                "MUSC", "NAVI", "NAVM", "REFR", "REGN", "WATR", "WRLD")},
        "rawForbiddenCounts": {
            name: 0 for name in (
                "ACHR", "CELL", "CLMT", "IMGS", "LAND", "LCTN", "LTEX",
                "MUSC", "NAVI", "NAVM", "REFR", "REGN", "WATR", "WRLD")},
        "pluginSha256": {"value": "dacd392164d879bf74f013a17981b7b1c2f2483df4ac69be9fb9287068cfbaf5"},
        "packageTreeSha256": {"value": "53598665274afd4706af4df0d01cf86da8cd0f050faa66b1040e72287fc517b3"},
        "sourcePackageTreeSha256": {"value": "53598665274afd4706af4df0d01cf86da8cd0f050faa66b1040e72287fc517b3"},
        "archiveSha256": {"value": archive_sha.lower()},
        "runtimeIdentity": {
            "baseNpc": {
                "plugin": {"value": "BrigitteBardotNpcManager.esp"},
                "formId": {"value": 0x800},
            },
            "placedReference": None,
            "placementIncluded": False,
        },
    }
    persisted_verification = {
        "archiveSha256": archive_sha.lower(),
        "packageTreeSha256": verification["packageTreeSha256"]["value"],
        "placementIncluded": False,
        "pluginSha256": verification["pluginSha256"]["value"],
        "rawForbiddenCounts": verification["rawForbiddenCounts"],
        "runtimeAuthority": False,
        "runtimeIdentity": {
            "baseNpc": "BrigitteBardotNpcManager.esp|0x00000800",
            "placedReference": None,
            "placementIncluded": False,
        },
        "schema": "npc.finish-core.verification.v1",
        "sourcePackageTreeSha256":
            verification["sourcePackageTreeSha256"]["value"],
        "status": "StaticPassRuntimeRequired",
        "typedForbiddenCounts": verification["typedForbiddenCounts"],
        "verified": True,
        "visualAuthority": False,
    }
    content = json.dumps(
        persisted_verification, separators=(",", ":"), sort_keys=True).encode()
    output.write_bytes(content)
    output_sha = hashlib.sha256(content).hexdigest().upper()
    request_digest = "D" * 64
    effects = [
        {"kind": "readWorkspace", "status": "completed", "scope": "workspace"},
        {"kind": "writeNewArtifact", "status": "completed", "scope": "k-local-output"},
        {"kind": "appendLocalOperationJournal", "status": "attempted", "scope": "workspace-local-journal"},
        {"kind": "appendLocalOperationJournal", "status": "completed", "scope": "workspace-local-journal"},
    ]
    authority_states = {
        "inputAdmission": "established",
        "sourceProviderIdentity": "established",
        "deterministicMaterialization": "established",
        "independentStaticVerification": "established",
        "offEnginePreview": "notApplicable",
        "humanVisualAcceptance": "required",
        "gameRuntimeVerification": "required",
        "promotionApproval": "required",
    }
    envelope: dict[str, object] = {
        "protocolVersion": "2", "schemaVersion": "1",
        "command": "npc finish verify", "outcome": "succeeded", "exitCode": 0,
        "requestDigest": request_digest, "effects": effects, "diagnostics": [],
        "artifacts": [{
            "kind": "npc-finish-core-verification",
            "schemaOrMediaType": "npc.finish-core.verification.v1",
            "path": str(output), "size": len(content), "sha256": output_sha,
            "producerCommand": "npc finish verify", "requestDigest": request_digest,
            "inputBindings": sorted([manifest_sha, archive_sha]),
            "state": "independentlyVerified",
        }],
        "authority": [
            {"kind": kind, "state": state, "reason": f"Fixture {kind}."}
            for kind, state in authority_states.items()],
        "nextActions": [{
            "command": "runtime smoke verify",
            "reason": "Runtime authority requires operator evidence.",
            "requiredBindings": [{"option": "--edition", "value": "skyrimse"}],
            "missingPrerequisites": ["--runtime-report", "--package-acceptance"],
            "requiresHumanAction": True,
        }],
        "result": {
            "schemaVersion": "1", "verified": True,
            "status": "staticPassRuntimeRequired", "manifestPath": str(manifest),
            "manifestSha256": manifest_sha, "verificationPath": str(output),
            "verificationSize": len(content), "verificationSha256": output_sha,
            "verification": verification, "diagnostics": [],
        },
    }
    operations = workspace / ".actorwright" / "operations"
    operations.mkdir(parents=True, exist_ok=True)
    journal = {
        "command": "npc finish verify", "requestDigest": request_digest,
        "effects": _journal_effects(effects[:3]),
        "diagnosticCodes": [], "diagnosticClasses": [],
        "artifactHashes": [output_sha], "durationMilliseconds": 1,
        "outcome": "succeeded", "exitCode": 0,
    }
    (operations / f"{request_digest}-{'4':0>20}.json").write_text(
        json.dumps(journal, separators=(",", ":")), encoding="utf-8")
    return envelope


def _attach_finish_workflow(
    envelope: dict[str, object],
    arguments: tuple[str, ...],
    workspace: Path,
) -> dict[str, object]:
    options = {
        arguments[index]: arguments[index + 1]
        for index in range(6, len(arguments), 2)
    }
    artifacts = envelope["artifacts"]
    assert isinstance(artifacts, list) and len(artifacts) == 1
    verification_artifact = artifacts[0]
    assert isinstance(verification_artifact, dict)
    input_workflow = json.loads(
        Path(options["--workflow-bundle"]).read_text(encoding="utf-8"))
    manifest_binding = input_workflow["artifacts"][0]
    archive = workspace / "output.zip"
    archive_bytes = archive.read_bytes()
    archive_sha = hashlib.sha256(archive_bytes).hexdigest().upper()
    archive_binding: dict[str, object] = {
        "kind": "package-archive",
        "schemaOrMediaType": "application/zip",
        "path": str(archive),
        "size": len(archive_bytes),
        "sha256": archive_sha,
        "producerCommand": manifest_binding["producerCommand"],
        "requestDigest": manifest_binding["requestDigest"],
        "inputArtifactHashes": [manifest_binding["sha256"]],
        "semanticSha256": None,
    }
    workflow_document, workflow_artifact = _write_fixture_workflow(
        Path(options["--workflow-output"]),
        str(envelope["requestDigest"]),
        input_workflow["npc"],
        "runtimeAcceptance",
        [_workflow_binding(verification_artifact), archive_binding],
        "npc finish verify")
    artifacts.append(workflow_artifact)
    envelope["nextActions"] = _project_bundle_action(
        workflow_document, workflow_artifact)
    operations = workspace / ".actorwright" / "operations"
    matches = [
        path for path in operations.glob("*.json")
        if json.loads(path.read_text(encoding="utf-8")).get("command")
            == "npc finish verify"]
    assert len(matches) == 1
    journal = json.loads(matches[0].read_text(encoding="utf-8"))
    journal["artifactHashes"] = sorted([
        verification_artifact["sha256"], workflow_artifact["sha256"]])
    matches[0].write_text(
        json.dumps(journal, separators=(",", ":")), encoding="utf-8")
    return envelope


def _protocol_v2_harness(
    root: Path,
    *,
    workflow_commands: tuple[str, ...] = CURRENT_PREVIEW260_WORKFLOWS,
) -> SimpleNamespace:
    preset_document_schema = (
        "actorwright-preset-inspection/1"
        if workflow_commands == HISTORICAL_PREVIEW250_WORKFLOWS
        else "actorwright-preset-inspection/2")
    manifest = json.loads(
        (root / "manifest.json").read_text(encoding="utf-8-sig"))
    release_version = manifest["version"]
    declarations = {
        item["entrypoint"]: item
        for item in manifest["embeddedResourceClosures"]
    }
    capabilities = json.loads(
        (root / "capabilities.json").read_text(encoding="utf-8-sig"))
    command_names = [row["name"] for row in capabilities["commands"]]
    ready_commands = {"capabilities", "schema export", "version"} | set(
        workflow_commands)
    commands = [
        {
            "name": name,
            "readiness": "v2" if name in ready_commands else "legacy",
            "resultSchemaIds": _ready_result_schema_ids(name)
                if name in ready_commands else [],
        }
        for name in command_names
    ]
    responses = {
        ("capabilities", "--protocol", "2", "--json"): (0, _kernel_envelope(
            "capabilities",
            result={
                "schemaId": READY_RESULT_SCHEMAS["capabilities"],
                "protocolVersion": "2",
                "commands": commands,
            },
        )),
        ("version", "--protocol", "2", "--json"): (0, _kernel_envelope(
            "version",
            result={
                "schemaId": READY_RESULT_SCHEMAS["version"],
                "productName": "Actorwright",
                "productVersion": manifest["version"],
                "sourceLine": manifest["sourceLine"],
                "targetFramework": "net10.0-windows",
                "protocolVersion": "2",
                "supportedProtocolVersions": ["1", "2"],
            },
        )),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc finish analyze",
        ): (0, _kernel_envelope(
            "schema export",
            result={
                "schemaId": READY_RESULT_SCHEMAS["schema export"],
                "protocolVersion": "2",
                "scopedHelpResultSchema": _result_schema_definition(
                    "urn:actorwright:protocol-v2:scoped-help-result:v1",
                    ["schemaId", "contract"],
                ),
                "contract": {
                    "name": "npc finish analyze",
                    "readiness": "legacy",
                    "resultSchemaIds": [],
                },
                "resultSchemas": [],
                "documentSchemas": ([
                    _document_schema_definition(
                        "request-legacy", "input", "npc.finish-core.request.v1"),
                    _document_schema_definition(
                        "request", "input", "npc.finish-core.request.v2"),
                    _document_schema_definition(
                        "request-external", "input", "npc.finish-core.request.v3"),
                    _document_schema_definition(
                        "proposal-legacy", "output", "npc.finish-core.proposal.v1"),
                    _document_schema_definition(
                        "proposal", "output", "npc.finish-core.proposal.v2"),
                    _document_schema_definition(
                        "proposal-external", "output", "npc.finish-core.proposal.v3"),
                    _document_schema_definition(
                        "validation", "output", "npc.finish-core.validation.v1"),
                ] if release_version in {
                    "1.0.0-preview.255", "1.0.0-preview.256",
                    "1.0.0-preview.257", "1.0.0-preview.258",
                    "1.0.0-preview.260",
                } else [
                    _document_schema_definition(
                        "request", "input", "npc.finish-core.request.v2"),
                    _document_schema_definition(
                        "proposal", "output", "npc.finish-core.proposal.v2"),
                    _document_schema_definition(
                        "validation", "output", "npc.finish-core.validation.v1"),
                ]),
            },
        )),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "workspace preflight",
        ): (0, _workflow_schema_envelope(
            "workspace preflight", release_version=release_version)),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc assembly preflight",
        ): (0, _workflow_schema_envelope(
            "npc assembly preflight", release_version=release_version)),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "preset inspect",
        ): (0, _workflow_schema_envelope(
            "preset inspect", preset_document_schema, release_version)),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc create-from-jslot",
        ): (0, _workflow_schema_envelope(
            "npc create-from-jslot", release_version=release_version)),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc finish verify",
        ): (0, _workflow_schema_envelope(
            "npc finish verify", release_version=release_version)),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "preview npc",
        ): (0, _workflow_schema_envelope(
            "preview npc", release_version=release_version)),
        ("npc", "create", "--protocol", "2", "--json"): (2, _kernel_envelope(
            "npc create",
            result=None,
            exit_code=2,
            outcome="failed",
            diagnostics=[{
                "code": "protocol-command-legacy",
                "severity": "error",
                "message": "Command 'npc create' is not ready for protocol 2.",
                "class": "usage",
                "recovery": {
                    "action": "correctInput",
                    "constraint": "Command 'npc create' is not ready for protocol 2.",
                    "retryUnchangedSafe": False,
                },
            }],
        )),
    }
    calls: list[tuple[str, ...]] = []
    protocol_temporary_roots: list[Path] = []
    timeout_arguments: set[tuple[str, ...]] = set()
    workflow_mutations: set[str] = set()

    def fake_run(
        command: list[str],
        **kwargs: object,
    ) -> SimpleNamespace:
        arguments = tuple(command[1:])
        calls.append(arguments)
        if arguments in timeout_arguments:
            raise subprocess.TimeoutExpired(command, timeout=90)
        if arguments == ("--resource-extraction-probe",):
            relative = (
                "desktop/Actorwright.Desktop.exe"
                if command[0].endswith("Actorwright.Desktop.exe")
                else "cli/actorwright.exe"
            )
            declaration = declarations[relative]
            environment = kwargs["env"]
            assert isinstance(environment, dict)
            return SimpleNamespace(
                returncode=0,
                stderr="",
                stdout=json.dumps({
                    "schemaVersion": 1,
                    "accepted": True,
                    "resourceBase": environment[
                        "DOTNET_BUNDLE_EXTRACT_BASE_DIR"],
                    "runtimeManifestSha256": declaration[
                        "runtimeManifestSha256"],
                    "entries": declaration["entries"],
                }),
            )
        environment = kwargs["env"]
        assert isinstance(environment, dict)
        protocol_temporary_roots.append(
            Path(environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"]).parent)
        if arguments[:6] == (
            "npc", "assembly", "preflight", "--protocol", "2", "--json"
        ):
            envelope = _assembly_preflight_envelope(arguments)
            return SimpleNamespace(
                returncode=4,
                stderr="",
                stdout=json.dumps(envelope, separators=(",", ":")) + "\n",
            )
        if arguments[:5] == (
            "npc", "create-from-jslot", "--protocol", "2", "--json"
        ):
            envelope = _npc_create_preflight_envelope(
                arguments,
                Path(environment["ACTORWRIGHT_WORKSPACE_ROOT"]),
                workflow_mutations,
            )
            return SimpleNamespace(
                returncode=0,
                stderr="",
                stdout=json.dumps(envelope, separators=(",", ":")) + "\n",
            )
        if arguments[:6] == (
            "npc", "finish", "verify", "--protocol", "2", "--json"
        ):
            envelope = _attach_finish_workflow(_finish_verify_envelope(
                arguments,
                Path(environment["ACTORWRIGHT_WORKSPACE_ROOT"]),
            ), arguments, Path(environment["ACTORWRIGHT_WORKSPACE_ROOT"]))
            return SimpleNamespace(
                returncode=0,
                stderr="",
                stdout=json.dumps(envelope, separators=(",", ":")) + "\n",
            )
        if arguments[:5] == (
            "workspace", "preflight", "--protocol", "2", "--json"
        ):
            envelope = _workspace_preflight_envelope(
                arguments,
                Path(environment["ACTORWRIGHT_WORKSPACE_ROOT"]),
                workflow_mutations,
            )
            return SimpleNamespace(
                returncode=0,
                stderr="",
                stdout=json.dumps(envelope, separators=(",", ":")) + "\n",
            )
        if arguments[:5] == (
            "preset", "inspect", "--protocol", "2", "--json"
        ):
            if "workflow-input-hash" in workflow_mutations:
                options = {
                    arguments[index]: arguments[index + 1]
                    for index in range(5, len(arguments), 2)
                }
                workflow = Path(options["--workflow-bundle"])
                workflow.write_bytes(workflow.read_bytes() + b"\n")
                return SimpleNamespace(
                    returncode=3,
                    stderr="",
                    stdout=json.dumps(_kernel_envelope(
                        "preset inspect", result=None, exit_code=3,
                        outcome="failed", diagnostics=[{
                            "code": "workflow-bundle-validation-failed",
                            "severity": "error",
                            "message": "The workflow bundle hash changed.",
                            "class": "validation",
                        }]), separators=(",", ":")) + "\n",
                )
            envelope = _preset_inspect_envelope(
                arguments,
                Path(environment["ACTORWRIGHT_WORKSPACE_ROOT"]),
                preset_document_schema,
            )
            return SimpleNamespace(
                returncode=0,
                stderr="",
                stdout=json.dumps(envelope, separators=(",", ":")) + "\n",
            )
        returncode, envelope = responses[arguments]
        return SimpleNamespace(
            returncode=returncode,
            stderr="",
            stdout=json.dumps(envelope, separators=(",", ":")) + "\n",
        )

    return SimpleNamespace(
        root=root,
        responses=responses,
        calls=calls,
        protocol_temporary_roots=protocol_temporary_roots,
        timeout_arguments=timeout_arguments,
        workflow_mutations=workflow_mutations,
        fake_run=fake_run,
    )


def test_package_protocol_v2_kernel_gate_and_metadata_only_skip(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    _use_test_owned_legacy_probe_fixtures(tmp_path, verifier, monkeypatch)
    harness = _protocol_v2_harness(
        _package(tmp_path / "deep" / "caller" / "chosen" / "output"))
    monkeypatch.setattr(verifier.subprocess, "run", harness.fake_run)

    metadata = verifier.verify_package_staging(
        harness.root, metadata_only=True)
    assert metadata["status"] == "PASS"
    assert harness.calls == []

    result = verifier.verify_package_staging(
        harness.root, metadata_only=False)
    assert result["protocolV2Kernel"] is True
    npc_schema = harness.responses[(
        "schema", "export", "--protocol", "2", "--json",
        "--command", "npc create-from-jslot",
    )][1]
    assert [
        row["name"] for row in npc_schema["result"]["documentSchemas"]
    ] == ["request", "preflight"]
    assert result["protocolV2WorkflowCommands"] == [
        "npc assembly preflight", "npc create-from-jslot", "npc finish verify",
        "preset inspect", "preview npc", "workspace preflight"]
    assert result["protocolV2ConsumerRequiredCommands"] == ["preview npc"]
    assert len(set(harness.protocol_temporary_roots)) == 1
    protocol_root = harness.protocol_temporary_roots[0]
    workspace = protocol_root / "w"
    assert harness.calls == [
        ("--resource-extraction-probe",),
        ("--resource-extraction-probe",),
        ("capabilities", "--protocol", "2", "--json"),
        ("version", "--protocol", "2", "--json"),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc finish analyze",
        ),
        ("npc", "create", "--protocol", "2", "--json"),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc assembly preflight",
        ),
        (
            "npc", "assembly", "preflight", "--protocol", "2", "--json",
            "--contract", str(workspace / "assembly-preflight" / "invalid-contract.json"),
            "--contract-sha256",
            "CA3D163BAB055381827226140568F3BEF7EAAC187CEBD76878E0B63E9E442356",
            "--output", str(workspace / "evidence" / "assembly-preflight.json"),
        ),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "workspace preflight",
        ),
        (
            "workspace", "preflight", "--protocol", "2", "--json",
            "--game", "skyrimse",
            "--workspace-root", str(workspace),
            "--data-root", str(workspace / "Data"),
            "--output-root", str(workspace / "reserved-output"),
            "--load-order", str(workspace / "load-order.json"),
            "--intake-output", str(workspace / "evidence" / "reviewed-intake.json"),
            "--npc-editor-id", "ActorwrightPackagedNpc",
            "--workflow-output", str(workspace / "evidence" / "workflow-workspace.json"),
        ),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "preset inspect",
        ),
        (
            "preset", "inspect", "--protocol", "2", "--json",
            "--format", "racemenu-jslot", "--edition", "skyrimse",
            "--input", str(workspace / "npc-preflight" / "fixture.jslot"),
            "--input-sha256", "D7B7274263FA120B1C92D896F4AB8ED2582F562A7C5B39224F86B3645AAAFE58",
            "--inspection-output", str(workspace / "evidence" / "preset-inspection.json"),
            "--workflow-bundle", str(workspace / "evidence" / "workflow-workspace.json"),
            "--workflow-bundle-sha256", ANY,
            "--workflow-output", str(workspace / "evidence" / "workflow-preset.json"),
        ),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc create-from-jslot",
        ),
        (
            "npc", "create-from-jslot", "--protocol", "2", "--json",
            "--request", str(workspace / "npc-preflight" / "request.json"),
            "--request-sha256", "4BC427D2252C30F7A4A5BEFCD47EFC9049104D15ABC62D9F5FC04E93A4300041",
            "--preset", str(workspace / "npc-preflight" / "fixture.jslot"),
            "--preset-sha256", "D7B7274263FA120B1C92D896F4AB8ED2582F562A7C5B39224F86B3645AAAFE58",
            "--data-root", str(workspace / "Data"),
            "--plugins", "Skyrim.esm",
            "--companion-root", str(workspace / "planned-companion"),
            "--preflight-output", str(workspace / "evidence" / "npc-build-preflight.json"),
            "--workflow-bundle", str(workspace / "evidence" / "workflow-preset.json"),
            "--workflow-bundle-sha256", ANY,
            "--workflow-output", str(workspace / "evidence" / "workflow-preflight.json"),
        ),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc finish verify",
        ),
        (
            "npc", "finish", "verify", "--protocol", "2", "--json",
            "--manifest", str(workspace / "output" / "NPCManager" /
                              "Evidence" / "finish-core-manifest.json"),
            "--manifest-sha256", ANY,
            "--verification-output", str(workspace / "evidence" /
                                         "finish-verification.json"),
            "--workflow-bundle", str(workspace / "evidence" / "workflow-finish-manifest.json"),
            "--workflow-bundle-sha256", ANY,
            "--workflow-output", str(workspace / "evidence" / "workflow-finish-runtime.json"),
        ),
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "preview npc",
        ),
    ]
    assert protocol_root.parent == ROOT / "artifacts" / "x"
    assert protocol_root.name.startswith("p-")
    assert not protocol_root.exists()


def test_preset_inspect_schema_export_accepts_current_and_legacy_documents(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    _use_test_owned_legacy_probe_fixtures(tmp_path, verifier, monkeypatch)
    root = _package(tmp_path / "preset-schema")
    harness = _protocol_v2_harness(root)
    monkeypatch.setattr(verifier.subprocess, "run", harness.fake_run)

    result = verifier.verify_package_staging(root, metadata_only=False)

    assert result["status"] == "PASS"


def test_preview272_finish_schema_requires_policy_documents(
    tmp_path: Path,
) -> None:
    verifier = _load_release_verifier()
    envelope = _finish_analyze_schema_envelope(tmp_path)
    documents = envelope["result"]["documentSchemas"]
    documents.extend([
        _document_schema_definition(
            "request-policy", "input", "npc.finish-core.request.v4"),
        _document_schema_definition(
            "proposal-policy", "output", "npc.finish-core.proposal.v4"),
    ])

    verifier._validate_protocol_v2_schema_export(
        envelope, "1.0.0-preview.272")

    envelope["result"]["documentSchemas"] = [
        row for row in documents if row["name"] != "request-policy"]
    with pytest.raises(verifier.ReleaseError, match="bind Finish Core"):
        verifier._validate_protocol_v2_schema_export(
            envelope, "1.0.0-preview.272")


def test_preview266_finish_schema_rejects_policy_documents(
    tmp_path: Path,
) -> None:
    verifier = _load_release_verifier()
    envelope = _finish_analyze_schema_envelope(tmp_path)
    envelope["result"]["documentSchemas"].extend([
        _document_schema_definition(
            "request-policy", "input", "npc.finish-core.request.v4"),
        _document_schema_definition(
            "proposal-policy", "output", "npc.finish-core.proposal.v4"),
    ])

    with pytest.raises(verifier.ReleaseError, match="bind Finish Core"):
        verifier._validate_protocol_v2_schema_export(
            envelope, "1.0.0-preview.266")


def test_workspace_preflight_schema_export_accepts_reviewed_intake_document(
) -> None:
    verifier = _load_release_verifier()
    envelope = _workflow_schema_envelope(
        "workspace preflight", release_version="1.0.0-preview.266")

    verifier._validate_protocol_v2_workflow_schema_export(
        envelope,
        "workspace preflight",
        _ready_result_schema_ids("workspace preflight"),
        READY_RESULT_SCHEMAS["workspace preflight"],
        release_version="1.0.0-preview.266",
    )


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("name", "intake"),
        ("direction", "input"),
        ("schemaIdentifier", "npcmanager-reviewed-game-intake/3"),
    ],
)
def test_workspace_preflight_schema_export_rejects_document_contract_drift(
    field: str,
    value: str,
) -> None:
    verifier = _load_release_verifier()
    envelope = _workflow_schema_envelope(
        "workspace preflight", release_version="1.0.0-preview.266")
    envelope["result"]["documentSchemas"][0][field] = value

    with pytest.raises(verifier.ReleaseError, match=(
        "workspace preflight.*document schema")):
        verifier._validate_protocol_v2_workflow_schema_export(
            envelope,
            "workspace preflight",
            _ready_result_schema_ids("workspace preflight"),
            READY_RESULT_SCHEMAS["workspace preflight"],
            release_version="1.0.0-preview.266",
        )


def test_workspace_preflight_schema_export_rejects_malformed_document_schema(
) -> None:
    verifier = _load_release_verifier()
    envelope = _workflow_schema_envelope(
        "workspace preflight", release_version="1.0.0-preview.266")
    document = envelope["result"]["documentSchemas"][0]
    document["jsonSchema"]["required"] = []

    with pytest.raises(verifier.ReleaseError, match=(
        "workspace preflight.*document schema")):
        verifier._validate_protocol_v2_workflow_schema_export(
            envelope,
            "workspace preflight",
            _ready_result_schema_ids("workspace preflight"),
            READY_RESULT_SCHEMAS["workspace preflight"],
            release_version="1.0.0-preview.266",
        )


def test_workspace_preflight_probe_dispatch_receives_release_identity(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    observed: list[str | None] = []

    def capture(
        executable: Path,
        probe: dict[str, object],
        workspace_root: Path,
        extraction_root: Path,
        catalog_root: Path,
        release_version: str | None = None,
    ) -> None:
        observed.append(release_version)

    monkeypatch.setattr(
        verifier, "_verify_workspace_preflight_probe", capture)
    verifier._verify_protocol_v2_workflow_probes(
        ROOT / "actorwright.exe",
        [{"validator": "workspace-preflight-v1"}],
        ROOT / "workspace",
        ROOT / "extraction",
        ROOT / "catalog",
        "1.0.0-preview.266",
    )

    assert observed == ["1.0.0-preview.266"]


@pytest.mark.parametrize(
    ("release_version", "envelope_version", "accepted"),
    [
        ("1.0.0-preview.260", "1.0.0-preview.260", True),
        ("1.0.0-preview.258", "1.0.0-preview.258", True),
        ("1.0.0-preview.257", "1.0.0-preview.257", True),
        ("1.0.0-preview.256", "1.0.0-preview.256", True),
        ("1.0.0-preview.255", "1.0.0-preview.255", True),
        ("1.0.0-preview.254", "1.0.0-preview.254", True),
        ("1.0.0-preview.252", "1.0.0-preview.252", True),
        ("1.0.0-preview.251", "1.0.0-preview.251", True),
        ("1.0.0-preview.250", "1.0.0-preview.250", True),
        ("1.0.0-preview.259", "1.0.0-preview.260", False),
        ("1.0.0-preview.251", "1.0.0-preview.252", False),
        ("1.0.0-preview.250", "1.0.0-preview.252", False),
    ],
)
def test_npc_create_schema_export_shape_is_bound_to_release_version(
    release_version: str,
    envelope_version: str,
    accepted: bool,
) -> None:
    verifier = _load_release_verifier()
    envelope = _workflow_schema_envelope(
        "npc create-from-jslot", release_version=envelope_version)
    validate = lambda: verifier._validate_protocol_v2_workflow_schema_export(
        envelope,
        "npc create-from-jslot",
        _ready_result_schema_ids("npc create-from-jslot"),
        READY_RESULT_SCHEMAS["npc create-from-jslot"],
        release_version=release_version,
    )
    if accepted:
        validate()
    else:
        with pytest.raises(verifier.ReleaseError, match="NPC preflight schema"):
            validate()


def test_npc_create_schema_export_rejects_unresolved_request_refs() -> None:
    verifier = _load_release_verifier()
    envelope = _workflow_schema_envelope(
        "npc create-from-jslot", release_version="1.0.0-preview.256")
    request_schema = envelope["result"]["documentSchemas"][0]["jsonSchema"]
    request_schema["$defs"].pop("path")

    with pytest.raises(verifier.ReleaseError, match="unresolved \\$ref"):
        verifier._validate_protocol_v2_workflow_schema_export(
            envelope,
            "npc create-from-jslot",
            _ready_result_schema_ids("npc create-from-jslot"),
            READY_RESULT_SCHEMAS["npc create-from-jslot"],
            release_version="1.0.0-preview.256",
        )


def test_npc_create_request_schema_pin_rejects_provider_arm_drift() -> None:
    verifier = _load_release_verifier()
    envelope = _workflow_schema_envelope(
        "npc create-from-jslot", release_version="1.0.0-preview.256")
    request_schema = envelope["result"]["documentSchemas"][0]["jsonSchema"]
    canonical_sha256 = hashlib.sha256(json.dumps(
        request_schema, sort_keys=True, separators=(",", ":"),
        ensure_ascii=False).encode("utf-8")).hexdigest().upper()
    assert canonical_sha256 == (
        verifier.PREVIEW272_NPC_CREATE_REQUEST_JSON_SCHEMA_CANONICAL_SHA256)

    provider_context = request_schema["$defs"]["providerContext"]
    provider_context["oneOf"][0]["required"].pop()
    with pytest.raises(verifier.ReleaseError, match="canonical bytes changed"):
        verifier._validate_protocol_v2_workflow_schema_export(
            envelope,
            "npc create-from-jslot",
            _ready_result_schema_ids("npc create-from-jslot"),
            READY_RESULT_SCHEMAS["npc create-from-jslot"],
            release_version="1.0.0-preview.256",
        )


def test_preview256_preset_inspect_effects_are_distinct_from_preview250() -> None:
    current_catalog = json.loads((
        ROOT / "tools" / "release" / "protocol-v2-workflow-probes" /
        "catalog.json"
    ).read_text(encoding="utf-8"))
    historical_catalog = json.loads((
        ROOT / "tools" / "release" / "protocol-v2-workflow-probes" /
        "preview250" / "catalog.json"
    ).read_text(encoding="utf-8"))

    current = next(
        row for row in current_catalog["probes"]
        if row["command"] == "preset inspect")
    historical = next(
        row for row in historical_catalog["probes"]
        if row["command"] == "preset inspect")

    assert current["expectedEffects"] == [
        "readWorkspace|completed|workspace",
        "writeNewArtifact|completed|k-local-output",
        "writeNewArtifact|completed|k-local-output",
        "appendLocalOperationJournal|attempted|workspace-local-journal",
        "appendLocalOperationJournal|completed|workspace-local-journal",
    ]
    assert historical["expectedEffects"] == [
        "readWorkspace|completed|workspace",
        "writeNewArtifact|completed|k-local-output",
        "appendLocalOperationJournal|attempted|workspace-local-journal",
        "appendLocalOperationJournal|completed|workspace-local-journal",
    ]


def test_preview255_probe_snapshot_preserves_previous_current_bytes() -> None:
    verifier = _load_release_verifier()
    probe_root = ROOT / "tools" / "release" / "protocol-v2-workflow-probes"
    snapshot_root = probe_root / "preview255"
    _assert_historical_finish_payloads_unavailable(verifier, probe_root)
    _assert_historical_finish_payloads_unavailable(verifier, snapshot_root)
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.255"] == snapshot_root
    assert (
        verifier.CURRENT_PREVIEW255_FINISH_DESTINATIONS
        == verifier.CURRENT_PREVIEW256_FINISH_DESTINATIONS
    )
    assert (
        verifier.CURRENT_PREVIEW255_FINISH_DESTINATIONS
        is not verifier.CURRENT_PREVIEW256_FINISH_DESTINATIONS
    )
    snapshot_paths = {
        path.relative_to(snapshot_root).as_posix(): path
        for path in snapshot_root.rglob("*")
        if path.is_file()
    }
    current_paths = {
        path.relative_to(probe_root).as_posix(): path
        for path in probe_root.rglob("*")
        if path.is_file() and not any(
            part.startswith("preview")
            or part in {"current-staging", "public-current-staging"}
            for part in path.relative_to(probe_root).parts)
    }
    assert snapshot_paths
    _assert_current_probe_change_is_scoped(current_paths, snapshot_paths)


def test_preview255_validation_is_bound_to_its_frozen_probe_root(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    snapshot_root = verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.255"]
    catalog = json.loads(
        (snapshot_root / "catalog.json").read_text(encoding="utf-8-sig"))
    finish_probe = next(
        probe for probe in catalog["probes"]
        if probe["validator"] == "finish-verify-v1")
    monkeypatch.setattr(
        verifier, "CURRENT_PREVIEW256_FINISH_DESTINATIONS", frozenset())
    verifier._validate_finish_verify_catalog_probe(
        finish_probe, release_version="1.0.0-preview.255")

    observed: dict[str, object] = {}

    def capture(path: Path, expected_sha256: str, label: str) -> dict:
        observed.update(path=path, sha256=expected_sha256, label=label)
        return {}

    monkeypatch.setattr(verifier, "_pinned_json_schema", capture)
    verifier._pinned_workflow_json_schema(
        "1.0.0-preview.255",
        "finish-verify-result.schema.json",
        "FUTURE_PREVIEW256_HASH",
        "Finish Verify result schema")
    assert observed == {
        "path": snapshot_root / "finish-verify-result.schema.json",
        "sha256": verifier.PREVIEW255_WORKFLOW_SCHEMA_CANONICAL_SHA256[
            "finish-verify-result.schema.json"],
        "label": "Finish Verify result schema",
    }


def test_preview256_probe_snapshot_and_hashes_are_independent_from_preview257(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    probe_root = ROOT / "tools" / "release" / "protocol-v2-workflow-probes"
    snapshot_root = probe_root / "preview256"
    _assert_historical_finish_payloads_unavailable(verifier, probe_root)
    _assert_historical_finish_payloads_unavailable(verifier, snapshot_root)
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.260"] == probe_root
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.258"] == probe_root / "preview258"
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.257"] == probe_root / "preview257"
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.256"] == snapshot_root
    current_paths = {
        path.relative_to(probe_root).as_posix(): path
        for path in probe_root.rglob("*")
        if path.is_file() and not any(
            part.startswith("preview")
            or part in {"current-staging", "public-current-staging"}
            for part in path.relative_to(probe_root).parts)
    }
    snapshot_paths = {
        path.relative_to(snapshot_root).as_posix(): path
        for path in snapshot_root.rglob("*")
        if path.is_file()
    }
    _assert_current_probe_change_is_scoped(current_paths, snapshot_paths)

    observed: dict[str, object] = {}

    def capture(path: Path, expected_sha256: str, label: str) -> dict:
        observed.update(path=path, sha256=expected_sha256, label=label)
        return {}

    monkeypatch.setattr(verifier, "_pinned_json_schema", capture)
    verifier._pinned_workflow_json_schema(
        "1.0.0-preview.256",
        "finish-verify-result.schema.json",
        "FUTURE_PREVIEW260_HASH",
        "Finish Verify result schema")
    assert observed == {
        "path": snapshot_root / "finish-verify-result.schema.json",
        "sha256": verifier.PREVIEW256_WORKFLOW_SCHEMA_CANONICAL_SHA256[
            "finish-verify-result.schema.json"],
        "label": "Finish Verify result schema",
    }


def test_preview257_probe_snapshot_and_hashes_are_independent_from_preview258(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    probe_root = ROOT / "tools" / "release" / "protocol-v2-workflow-probes"
    snapshot_root = probe_root / "preview257"
    _assert_historical_finish_payloads_unavailable(verifier, probe_root)
    _assert_historical_finish_payloads_unavailable(verifier, snapshot_root)
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.260"] == probe_root
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.258"] == probe_root / "preview258"
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.257"] == snapshot_root
    current_paths = {
        path.relative_to(probe_root).as_posix(): path
        for path in probe_root.rglob("*")
        if path.is_file() and not any(
            part.startswith("preview")
            or part in {"current-staging", "public-current-staging"}
            for part in path.relative_to(probe_root).parts)
    }
    snapshot_paths = {
        path.relative_to(snapshot_root).as_posix(): path
        for path in snapshot_root.rglob("*")
        if path.is_file()
    }
    _assert_current_probe_change_is_scoped(current_paths, snapshot_paths)

    observed: dict[str, object] = {}

    def capture(path: Path, expected_sha256: str, label: str) -> dict:
        observed.update(path=path, sha256=expected_sha256, label=label)
        return {}

    monkeypatch.setattr(verifier, "_pinned_json_schema", capture)
    verifier._pinned_workflow_json_schema(
        "1.0.0-preview.257",
        "finish-verify-result.schema.json",
        "FUTURE_PREVIEW260_HASH",
        "Finish Verify result schema")
    assert observed == {
        "path": snapshot_root / "finish-verify-result.schema.json",
        "sha256": verifier.PREVIEW257_WORKFLOW_SCHEMA_CANONICAL_SHA256[
            "finish-verify-result.schema.json"],
        "label": "Finish Verify result schema",
    }


def test_preview258_probe_snapshot_and_hashes_are_independent_from_preview260(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    probe_root = ROOT / "tools" / "release" / "protocol-v2-workflow-probes"
    snapshot_root = probe_root / "preview258"
    _assert_historical_finish_payloads_unavailable(verifier, probe_root)
    _assert_historical_finish_payloads_unavailable(verifier, snapshot_root)
    plugin = base64.b64decode(
        (probe_root / "npc-create-preflight" / "Skyrim.esm.base64").read_text(
            encoding="utf-8-sig").strip())
    records = _plugin_records(plugin)
    assert sum(record["signature"] == "RACE" and record["formId"] == 0x13746
               for record in records) == 1
    npc_records = [
        record for record in records
        if record["signature"] == "NPC_" and record["formId"] == 0x800
    ]
    assert len(npc_records) == 1
    subrecords = npc_records[0]["subrecords"]
    assert subrecords.count(("NAM9", 76)) == 1
    assert subrecords.count(("NAMA", 16)) == 1
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.260"] == probe_root
    assert verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.258"] == snapshot_root
    current_paths = {
        path.relative_to(probe_root).as_posix(): path
        for path in probe_root.rglob("*")
        if path.is_file() and not any(
            part.startswith("preview")
            or part in {"current-staging", "public-current-staging"}
            for part in path.relative_to(probe_root).parts)
    }
    snapshot_paths = {
        path.relative_to(snapshot_root).as_posix(): path
        for path in snapshot_root.rglob("*")
        if path.is_file()
    }
    _assert_current_probe_change_is_scoped(current_paths, snapshot_paths)
    snapshot_plugin = base64.b64decode(
        (snapshot_root / "npc-create-preflight" / "Skyrim.esm.base64").read_text(
            encoding="utf-8-sig").strip())
    assert len(snapshot_plugin) == 66
    assert hashlib.sha256(snapshot_plugin).hexdigest().upper() == (
        "5F871B7220E6F3894B3148C04F70B0416AFE86D39DB4C529795C54352D95388F")

    observed: dict[str, object] = {}

    def capture(path: Path, expected_sha256: str, label: str) -> dict:
        observed.update(path=path, sha256=expected_sha256, label=label)
        return {}

    monkeypatch.setattr(verifier, "_pinned_json_schema", capture)
    verifier._pinned_workflow_json_schema(
        "1.0.0-preview.258",
        "finish-verify-result.schema.json",
        "FUTURE_PREVIEW260_HASH",
        "Finish Verify result schema")
    assert observed == {
        "path": snapshot_root / "finish-verify-result.schema.json",
        "sha256": verifier.PREVIEW258_WORKFLOW_SCHEMA_CANONICAL_SHA256[
            "finish-verify-result.schema.json"],
        "label": "Finish Verify result schema",
    }


def test_preview258_rollback_hashes_ignore_current_hash_changes() -> None:
    source = VERIFY.read_text(encoding="utf-8-sig")
    current_hash_names = (
        "WORKSPACE_PREFLIGHT_RESULT_SCHEMA_CANONICAL_SHA256",
        "PRESET_INSPECT_RESULT_SCHEMA_CANONICAL_SHA256",
        "PRESET_INSPECTION_RECEIPT_SCHEMA_CANONICAL_SHA256",
        "PRESET_INSPECTION_CURRENT_RECEIPT_SCHEMA_CANONICAL_SHA256",
        "NPC_CREATE_PREFLIGHT_RESULT_SCHEMA_CANONICAL_SHA256",
        "NPC_BUILD_PREFLIGHT_ARTIFACT_SCHEMA_CANONICAL_SHA256",
        "FINISH_VERIFY_RESULT_SCHEMA_CANONICAL_SHA256",
        "ACTOR_ASSEMBLY_PROTOCOL_RESULT_SCHEMA_CANONICAL_SHA256",
        "ACTOR_ASSEMBLY_CONTRACT_SCHEMA_CANONICAL_SHA256",
        "ACTOR_ASSEMBLY_DOCUMENT_RESULT_SCHEMA_CANONICAL_SHA256",
        "ACTOR_ASSEMBLY_ERROR_SCHEMA_CANONICAL_SHA256",
    )
    for name in current_hash_names:
        source, replacements = re.subn(
            rf"(?m)^({name}\s*=\s*\(\s*)\"[0-9A-F]+\"(\s*\))",
            r'\1"MUTATED_PREVIEW257_HASH"\2',
            source,
        )
        assert replacements == 1

    module = importlib.util.module_from_spec(
        importlib.util.spec_from_file_location(
            "actorwright_verify_release_preview256_mutated_current", VERIFY))
    module.__file__ = str(VERIFY)
    exec(compile(source, str(VERIFY), "exec"), module.__dict__)

    for release_version, hashes in (
        ("1.0.0-preview.256", module.PREVIEW256_WORKFLOW_SCHEMA_CANONICAL_SHA256),
        ("1.0.0-preview.257", module.PREVIEW257_WORKFLOW_SCHEMA_CANONICAL_SHA256),
        ("1.0.0-preview.258", module.PREVIEW258_WORKFLOW_SCHEMA_CANONICAL_SHA256),
    ):
        for filename in hashes:
            module._pinned_workflow_json_schema(
                release_version,
                filename,
                "MUTATED_CURRENT_HASH",
                f"{release_version} {filename}",
            )


def test_preview256_package_verification_selects_frozen_probe_root(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    root = _package(tmp_path / "rollback-preview256")
    manifest_path = root / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    manifest["version"] = "1.0.0-preview.256"
    manifest["sourceLine"] = "preview.256-private"
    _json(manifest_path, manifest)
    capabilities_path = root / "capabilities.json"
    capabilities = json.loads(
        capabilities_path.read_text(encoding="utf-8-sig"))
    capabilities["version"] = "1.0.0-preview.256"
    capabilities["sourceLine"] = "preview.256-private"
    _json(capabilities_path, capabilities)
    capabilities_row = next(
        row for row in manifest["files"] if row["path"] == "capabilities.json")
    capabilities_row["size"] = capabilities_path.stat().st_size
    capabilities_row["sha256"] = hashlib.sha256(
        capabilities_path.read_bytes()).hexdigest()
    _json(manifest_path, manifest)

    harness = _protocol_v2_harness(root)
    selected_root = _assert_historical_package_probe_unavailable(
        verifier, root, harness, monkeypatch, "1.0.0-preview.256")
    assert selected_root != verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.257"]


def test_preview252_and_preview251_probe_snapshots_preserve_shared_legacy_bytes() -> None:
    probe_root = ROOT / "tools" / "release" / "protocol-v2-workflow-probes"
    legacy_root = probe_root / "preview252"
    verifier = _load_release_verifier()
    _assert_historical_finish_payloads_unavailable(verifier, probe_root)
    _assert_historical_finish_payloads_unavailable(verifier, legacy_root)
    legacy_paths = {
        path.relative_to(legacy_root).as_posix(): path
        for path in legacy_root.rglob("*")
        if path.is_file()
    }
    preview251_root = probe_root / "preview251"
    _assert_historical_finish_payloads_unavailable(verifier, preview251_root)
    preview251_paths = {
        path.relative_to(preview251_root).as_posix(): path
        for path in preview251_root.rglob("*")
        if path.is_file()
    }
    assert set(legacy_paths) == set(preview251_paths)
    assert all(
        legacy_paths[relative].read_bytes()
        == preview251_paths[relative].read_bytes()
        for relative in legacy_paths
    )

    current_paths = {
        path.relative_to(probe_root).as_posix(): path
        for path in probe_root.rglob("*")
        if path.is_file() and not ({"preview250", "preview251", "preview252", "preview255", "preview256", "preview257", "preview258", "preview272", "preview273", "preview274", "current-staging", "public-current-staging"} &
                                   set(path.relative_to(probe_root).parts))
    }
    assert set(current_paths) == set(legacy_paths)
    changed = {
        relative for relative in current_paths
        if current_paths[relative].read_bytes()
        != legacy_paths[relative].read_bytes()
    }
    assert changed == {
        "catalog.json",
        "npc-create-preflight/Skyrim.esm.base64",
        "npc-create-preflight/preset-bundle.json",
        "npc-create-preflight/record-authority.json",
        "npc-create-preflight/request.json",
        "npc-create-preflight/standalone-assets.json",
    }


def test_preview250_rollback_package_uses_its_closed_binary_identity(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    root = _package(tmp_path / "rollback")
    manifest_path = root / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    manifest["version"] = "1.0.0-preview.250"
    manifest["sourceLine"] = "preview.250-private"
    _json(manifest_path, manifest)
    capabilities_path = root / "capabilities.json"
    capabilities = json.loads(
        capabilities_path.read_text(encoding="utf-8-sig"))
    capabilities["version"] = "1.0.0-preview.250"
    capabilities["sourceLine"] = "preview.250-private"
    _json(capabilities_path, capabilities)
    capabilities_row = next(
        row for row in manifest["files"] if row["path"] == "capabilities.json")
    capabilities_row["size"] = capabilities_path.stat().st_size
    capabilities_row["sha256"] = hashlib.sha256(
        capabilities_path.read_bytes()).hexdigest()
    _json(manifest_path, manifest)
    harness = _protocol_v2_harness(
        root, workflow_commands=HISTORICAL_PREVIEW250_WORKFLOWS)
    npc_schema = harness.responses[(
        "schema", "export", "--protocol", "2", "--json",
        "--command", "npc create-from-jslot",
    )][1]
    assert [
        row["name"] for row in npc_schema["result"]["documentSchemas"]
    ] == ["preflight"]
    version_response = harness.responses[(
        "version", "--protocol", "2", "--json")][1]
    version_response["result"]["productVersion"] = "1.0.0-preview.250"
    version_response["result"]["sourceLine"] = "preview.250-private"
    selected_root = _assert_historical_package_probe_unavailable(
        verifier, root, harness, monkeypatch, "1.0.0-preview.250")
    assert selected_root == verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.250"]


def test_preview251_rollback_package_uses_its_closed_binary_identity(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    root = _package(tmp_path / "rollback-preview251")
    manifest_path = root / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    manifest["version"] = "1.0.0-preview.251"
    manifest["sourceLine"] = "preview.251-private"
    _json(manifest_path, manifest)
    capabilities_path = root / "capabilities.json"
    capabilities = json.loads(
        capabilities_path.read_text(encoding="utf-8-sig"))
    capabilities["version"] = "1.0.0-preview.251"
    capabilities["sourceLine"] = "preview.251-private"
    _json(capabilities_path, capabilities)
    capabilities_row = next(
        row for row in manifest["files"] if row["path"] == "capabilities.json")
    capabilities_row["size"] = capabilities_path.stat().st_size
    capabilities_row["sha256"] = hashlib.sha256(
        capabilities_path.read_bytes()).hexdigest()
    _json(manifest_path, manifest)
    harness = _protocol_v2_harness(root)
    npc_schema = harness.responses[(
        "schema", "export", "--protocol", "2", "--json",
        "--command", "npc create-from-jslot",
    )][1]
    assert [
        row["name"] for row in npc_schema["result"]["documentSchemas"]
    ] == ["preflight"]
    version_response = harness.responses[(
        "version", "--protocol", "2", "--json")][1]
    version_response["result"]["productVersion"] = "1.0.0-preview.251"
    version_response["result"]["sourceLine"] = "preview.251-private"
    selected_root = _assert_historical_package_probe_unavailable(
        verifier, root, harness, monkeypatch, "1.0.0-preview.251")
    assert selected_root == verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.251"]


def test_preview255_rollback_package_uses_its_closed_binary_identity(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verifier = _load_release_verifier()
    root = _package(tmp_path / "rollback-preview255")
    manifest_path = root / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    manifest["version"] = "1.0.0-preview.255"
    manifest["sourceLine"] = "preview.255-private"
    _json(manifest_path, manifest)
    capabilities_path = root / "capabilities.json"
    capabilities = json.loads(
        capabilities_path.read_text(encoding="utf-8-sig"))
    capabilities["version"] = "1.0.0-preview.255"
    capabilities["sourceLine"] = "preview.255-private"
    _json(capabilities_path, capabilities)
    capabilities_row = next(
        row for row in manifest["files"] if row["path"] == "capabilities.json")
    capabilities_row["size"] = capabilities_path.stat().st_size
    capabilities_row["sha256"] = hashlib.sha256(
        capabilities_path.read_bytes()).hexdigest()
    _json(manifest_path, manifest)
    harness = _protocol_v2_harness(
        root, workflow_commands=HISTORICAL_PREVIEW255_WORKFLOWS)
    selected_root = _assert_historical_package_probe_unavailable(
        verifier, root, harness, monkeypatch, "1.0.0-preview.255")
    assert selected_root == verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOTS[
        "1.0.0-preview.255"]


def test_preview252_metadata_rejects_historical_five_workflow_evidence(
    tmp_path: Path,
) -> None:
    root = _binary_metadata_release(
        tmp_path,
        version="1.0.0-preview.252",
        source_line="preview.252-private",
        source_tag="v1.0.0-preview.252",
        source_commit="A" * 40,
        workflow_commands=HISTORICAL_PREVIEW250_WORKFLOWS,
    )

    result = _run(root, "--json")

    assert result.returncode != 0
    assert "workflow command set" in (
        result.stdout + result.stderr).lower()


def test_preview252_metadata_accepts_current_six_workflow_evidence(
    tmp_path: Path,
) -> None:
    root = _binary_metadata_release(
        tmp_path,
        version="1.0.0-preview.252",
        source_line="preview.252-private",
        source_tag="v1.0.0-preview.252",
        source_commit="A" * 40,
        workflow_commands=PREVIEW252_ROLLBACK_WORKFLOWS,
    )
    _bytes(
        root / "docs" / "external-rendering-prerequisites.md",
        b"External rendering prerequisites\n",
    )
    _write_release_hashes(root)

    result = _run(root, "--json")

    assert result.returncode == 0, result.stdout + result.stderr
    assert json.loads(result.stdout)["version"] == "1.0.0-preview.252"


def test_preview272_schema_snapshot_accepts_exported_path_conventions(
    tmp_path: Path,
) -> None:
    verifier = _load_release_verifier()
    identity = verifier._select_binary_release_identity(
        "1.0.0-preview.272", "preview.272-private")
    assert identity is not None
    root = _binary_metadata_release(
        tmp_path,
        version="1.0.0-preview.272",
        source_line="preview.272-private",
        source_tag="v1.0.0-preview.272",
        source_commit="A" * 40,
        workflow_commands=tuple(
            verifier.BINARY_RELEASE_WORKFLOW_COMMANDS["1.0.0-preview.272"]),
    )
    capabilities = json.loads(
        (root / "evidence" / "protocol-v2-capabilities.json").read_text(
            encoding="utf-8-sig"))
    snapshot_path = root / "evidence" / "protocol-v2-schema-exports.json"
    snapshot = json.loads(snapshot_path.read_text(encoding="utf-8-sig"))
    for row in snapshot["commands"]:
        row["export"]["pathConventions"] = {
            "cli": "absolute K-local paths",
            "documents": "workspace-relative paths",
            "assets": "Data-relative paths",
        }
    _json(snapshot_path, snapshot)

    verifier.verify_protocol_v2_schema_snapshot(
        snapshot_path, capabilities["result"], identity)


def test_preview251_metadata_accepts_current_six_workflow_evidence(
    tmp_path: Path,
) -> None:
    root = _binary_metadata_release(
        tmp_path,
        version="1.0.0-preview.251",
        source_line="preview.251-private",
        source_tag="v1.0.0-preview.251",
        source_commit="B" * 40,
        workflow_commands=PREVIEW251_ROLLBACK_WORKFLOWS,
    )
    _bytes(
        root / "docs" / "external-rendering-prerequisites.md",
        b"External rendering prerequisites\n",
    )
    _write_release_hashes(root)

    result = _run(root, "--json")

    assert result.returncode == 0, result.stdout + result.stderr
    assert json.loads(result.stdout)["version"] == "1.0.0-preview.251"


def test_preview250_metadata_accepts_historical_five_workflow_evidence(
    tmp_path: Path,
) -> None:
    root = _binary_metadata_release(
        tmp_path,
        version="1.0.0-preview.250",
        source_line="preview.250-private",
        source_tag="v1.0.0-preview.250",
        source_commit=HISTORICAL_PREVIEW250_SOURCE_COMMIT,
        workflow_commands=HISTORICAL_PREVIEW250_WORKFLOWS,
    )
    assert not (root / "docs" / "external-rendering-prerequisites.md").exists()

    result = _run(root, "--json")

    assert result.returncode == 0, result.stdout + result.stderr
    assert json.loads(result.stdout)["version"] == "1.0.0-preview.250"


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        ("registry-name-set", "command name set"),
        ("effect-shape", "effect"),
        ("artifact-shape", "artifact"),
        ("authority-shape", "authority"),
        ("dangerous-authority", "authority"),
        ("next-action-shape", "next action"),
        ("duplicate-document-schema", "document schema"),
        ("document-json-schema", "document schema"),
        ("scoped-help-schema", "scoped help"),
        ("result-schema-projection", "result schema"),
        ("short-root-reparse", "temporary root"),
        ("catalog-missing", "catalog membership"),
        ("catalog-stale", "catalog membership"),
        ("workflow-schema", "workflow result schema"),
        ("workflow-schema-required", "workflow result schema"),
        ("workflow-schema-property", "workflow result schema"),
        ("workflow-schema-branch", "workflow result schema"),
        ("workflow-secondary-schema", "workflow schema contract"),
        ("workflow-effect", "effect"),
        ("workflow-authority", "authority"),
        ("workflow-action", "next action"),
        ("workflow-artifact", "artifact"),
        ("workflow-nested-type", "reviewed intake shape"),
        ("workflow-nested-count", "reviewed intake shape"),
        ("workflow-plugin-hash", "reviewed intake shape"),
        ("workflow-fingerprint-asset", "reviewed intake shape"),
        ("workflow-fingerprint-intake", "reviewed intake shape"),
        ("workflow-integer-bool", "reviewed intake shape"),
        ("workflow-line-ending-mix", "strict canonical UTF-8 JSON"),
        ("workflow-result-extra", "result mismatch"),
        ("workflow-result-mismatch", "result mismatch"),
        ("workflow-binding-missing", "input bindings"),
        ("workflow-binding-extra", "input bindings"),
        ("workflow-input-hash", "preset inspect"),
        ("workflow-bundle-signature", "workflow derived state"),
        ("workflow-bundle-hash", "workflow bundle mismatch"),
        ("workflow-preflight-action", "workflow derived state"),
        ("npc-artifact-document", "artifact root"),
        ("npc-result", "result mismatch"),
        ("npc-binding", "artifact binding"),
        ("npc-journal", "journal record"),
        ("fixture-hash", "fixture size or hash"),
    ],
)
def test_package_protocol_v2_kernel_rejects_untruthful_contracts(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    mutation: str,
    message: str,
) -> None:
    verifier = _load_release_verifier()
    _use_test_owned_legacy_probe_fixtures(tmp_path, verifier, monkeypatch)
    harness = _protocol_v2_harness(_package(tmp_path))
    capabilities = harness.responses[
        ("capabilities", "--protocol", "2", "--json")][1]
    schema = harness.responses[
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc finish analyze",
        )
    ][1]
    refusal = harness.responses[
        ("npc", "create", "--protocol", "2", "--json")][1]
    workflow_schema = harness.responses[
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "workspace preflight",
        )
    ][1]
    npc_schema = harness.responses[
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc create-from-jslot",
        )
    ][1]

    if mutation == "registry-name-set":
        capabilities["result"]["commands"][-1]["name"] = "invented command"
    elif mutation == "effect-shape":
        capabilities["effects"][0].pop("scope")
    elif mutation == "artifact-shape":
        capabilities["artifacts"].append({"kind": "schema"})
    elif mutation == "authority-shape":
        capabilities["authority"][0].pop("reason")
    elif mutation == "dangerous-authority":
        refusal["authority"].append({
            "kind": "gameRuntimeVerification",
            "state": "established",
            "reason": "Invented runtime authority.",
        })
    elif mutation == "next-action-shape":
        capabilities["nextActions"].append({"command": "npc create"})
    elif mutation == "duplicate-document-schema":
        schema["result"]["documentSchemas"].append(
            schema["result"]["documentSchemas"][0].copy())
    elif mutation == "document-json-schema":
        schema["result"]["documentSchemas"][0].pop("jsonSchema")
    elif mutation == "scoped-help-schema":
        schema["result"]["scopedHelpResultSchema"]["jsonSchema"] = []
    elif mutation == "result-schema-projection":
        schema["result"]["resultSchemas"] = [{
            "schemaIdentifier": "urn:actorwright:protocol-v2:invented:v1",
        }]
    elif mutation == "short-root-reparse":
        monkeypatch.setattr(
            verifier,
            "_is_reparse_point",
            lambda path: Path(path) == ROOT / "artifacts" / "x",
            raising=False,
        )
    elif mutation == "catalog-missing":
        catalog_root = tmp_path / "workflow-probe-catalog"
        shutil.copytree(
            ROOT / "tools" / "release" / "protocol-v2-workflow-probes",
            catalog_root,
        )
        catalog_path = catalog_root / "catalog.json"
        catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
        catalog["probes"] = []
        _json(catalog_path, catalog)
        monkeypatch.setattr(
            verifier, "PROTOCOL_V2_WORKFLOW_PROBE_ROOT", catalog_root)
        monkeypatch.setattr(
            verifier, "PROTOCOL_V2_WORKFLOW_PROBE_CATALOG", catalog_path)
    elif mutation == "catalog-stale":
        row = next(item for item in capabilities["result"]["commands"]
                   if item["name"] == "workspace preflight")
        row["readiness"] = "legacy"
        row["resultSchemaIds"] = []
    elif mutation == "workflow-schema":
        workflow_schema["result"]["resultSchemas"][0][
            "schemaIdentifier"] = "urn:actorwright:protocol-v2:invented:v1"
    elif mutation == "workflow-schema-required":
        workflow_schema["result"]["resultSchemas"][0]["jsonSchema"][
            "oneOf"][1]["required"].remove("assetIndexFingerprint")
    elif mutation == "workflow-schema-property":
        workflow_schema["result"]["resultSchemas"][0]["jsonSchema"][
            "oneOf"][1]["properties"]["edition"] = {"const": "fallout4"}
    elif mutation == "workflow-schema-branch":
        workflow_schema["result"]["resultSchemas"][0]["jsonSchema"][
            "oneOf"][0]["additionalProperties"] = True
    elif mutation == "workflow-secondary-schema":
        npc_schema["result"]["resultSchemas"].pop()
    elif mutation in {
        "workflow-effect", "workflow-authority", "workflow-action",
        "workflow-artifact", "workflow-nested-type",
        "workflow-nested-count", "workflow-plugin-hash",
        "workflow-fingerprint-asset", "workflow-fingerprint-intake",
        "workflow-integer-bool", "workflow-line-ending-mix",
            "workflow-result-extra", "workflow-result-mismatch",
            "workflow-binding-missing", "workflow-binding-extra",
            "workflow-input-hash", "workflow-bundle-signature",
            "workflow-bundle-hash", "workflow-preflight-action",
            "npc-artifact-document", "npc-result", "npc-binding", "npc-journal",
    }:
        harness.workflow_mutations.add(mutation)
    elif mutation == "fixture-hash":
        catalog_root = tmp_path / "workflow-probe-catalog"
        shutil.copytree(
            verifier.PROTOCOL_V2_WORKFLOW_PROBE_ROOT, catalog_root,
        )
        catalog_path = catalog_root / "catalog.json"
        catalog = json.loads(catalog_path.read_text(encoding="utf-8"))
        fixture_probe = next(
            row for row in catalog["probes"] if row["fixtures"])
        fixture_probe["fixtures"][0]["sha256"] = "F" * 64
        _json(catalog_path, catalog)
        monkeypatch.setattr(
            verifier, "PROTOCOL_V2_WORKFLOW_PROBE_ROOT", catalog_root)
        monkeypatch.setattr(
            verifier, "PROTOCOL_V2_WORKFLOW_PROBE_CATALOG", catalog_path)
    else:
        raise AssertionError(f"unknown mutation: {mutation}")

    monkeypatch.setattr(verifier.subprocess, "run", harness.fake_run)
    with pytest.raises(verifier.ReleaseError, match=message):
        verifier.verify_package_staging(harness.root, metadata_only=False)


def test_workspace_preflight_journal_accepts_persisted_wire_scopes(
    tmp_path: Path,
) -> None:
    verifier = _load_release_verifier()
    request_digest = "A" * 64
    operations = tmp_path / ".actorwright" / "operations"
    operations.mkdir(parents=True)
    record = {
        "command": "workspace preflight",
        "requestDigest": request_digest,
        "effects": [
            {
                "kind": "readWorkspace",
                "status": "completed",
                "scope": "workspace",
            },
            {
                "kind": "writeNewArtifact",
                "status": "completed",
                "scope": "k-local-output",
            },
            {
                "kind": "appendLocalOperationJournal",
                "status": "attempted",
                "scope": "workspace-local-journal",
            },
        ],
        "diagnosticCodes": [],
        "diagnosticClasses": [],
        "artifactHashes": [],
        "durationMilliseconds": 1,
        "outcome": "succeeded",
        "exitCode": 0,
    }
    (operations / f"{request_digest}-{'0':0>20}.json").write_text(
        json.dumps(record, separators=(",", ":")), encoding="utf-8")

    verifier._validate_workspace_preflight_journal(
        {"requestDigest": request_digest}, tmp_path, [])


def test_package_protocol_v2_kernel_timeout_is_stable_cli_failure(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    verifier = _load_release_verifier()
    harness = _protocol_v2_harness(_package(tmp_path))
    harness.timeout_arguments.add(
        ("capabilities", "--protocol", "2", "--json"))
    monkeypatch.setattr(verifier.subprocess, "run", harness.fake_run)
    monkeypatch.setattr(
        sys,
        "argv",
        ["verify_release.py", str(harness.root), "--package-staging", "--json"],
    )

    exit_code = verifier.main()
    captured = capsys.readouterr()

    assert exit_code == 2
    assert "FAIL: protocol 2 probe timed out: capabilities" in captured.err
    assert "Traceback" not in captured.err


def test_valid_binary_only_release(tmp_path: Path) -> None:
    root = _release(tmp_path)
    assert not (root / "docs" / "external-rendering-prerequisites.md").exists()
    result = _run(root, "--json")
    assert result.returncode == 0, result.stdout + result.stderr
    assert json.loads(result.stdout)["status"] == "PASS"


def test_release_rejects_missing_external_rendering_prerequisites_document(
    tmp_path: Path,
) -> None:
    root = _binary_metadata_release(
        tmp_path,
        version="1.0.0-preview.252",
        source_line="preview.252-private",
        source_tag="v1.0.0-preview.252",
        source_commit="A" * 40,
        workflow_commands=PREVIEW252_ROLLBACK_WORKFLOWS,
    )
    assert not (root / "docs" / "external-rendering-prerequisites.md").exists()
    result = _run(root)
    assert result.returncode != 0
    assert "external-rendering-prerequisites.md" in (
        result.stdout + result.stderr)


def test_valid_binary_package_staging(tmp_path: Path) -> None:
    result = _run_package(_package(tmp_path), "--json")
    assert result.returncode == 0, result.stdout + result.stderr
    response = json.loads(result.stdout)
    assert response["status"] == "PASS"
    assert response["artifactKind"] == "binary-package-staging"


def test_package_staging_rejects_wrong_provider_manifest_role(
    tmp_path: Path,
) -> None:
    root = _package(tmp_path)
    manifest_path = root / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    for closure in manifest["embeddedResourceClosures"]:
        for row in closure["entries"]:
            if row["role"] == "product-provider-manifest":
                row["role"] = "product-provider-provider-manifest"
    _json(manifest_path, manifest)
    result = _run_package(root)
    assert result.returncode != 0
    assert "role set mismatch" in (result.stdout + result.stderr).lower()


def test_package_staging_rejects_mismatched_closure_hashes(
    tmp_path: Path,
) -> None:
    for field in ("probeSha256", "runtimeManifestSha256"):
        root = _package(tmp_path / field)
        manifest_path = root / "manifest.json"
        manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        manifest["embeddedResourceClosures"][0][field] = "C" * 64
        _json(manifest_path, manifest)

        result = _run_package(root)

        assert result.returncode != 0, (
            f"package accepted mismatched {field}: "
            + result.stdout + result.stderr
        )
        assert "closure" in (result.stdout + result.stderr).lower() or (
            "probe hash" in (result.stdout + result.stderr).lower()
        )


def test_release_rejects_mismatched_closure_hashes(tmp_path: Path) -> None:
    for field in ("probeSha256", "runtimeManifestSha256"):
        root = _release(tmp_path / field)
        release_path = root / "actorwright-release.json"
        release = json.loads(release_path.read_text(encoding="utf-8-sig"))
        release["embeddedResourceClosures"][0][field] = "C" * 64
        _replace_release_metadata(root, release)

        result = _run(root)

        assert result.returncode != 0, (
            f"release accepted mismatched {field}: "
            + result.stdout + result.stderr
        )
        assert "closure" in (result.stdout + result.stderr).lower() or (
            "probe hash" in (result.stdout + result.stderr).lower()
        )


def test_release_rejects_incomplete_or_misbound_sbom(tmp_path: Path) -> None:
    for mutation in ("missing-package", "document-binding", "source-commit"):
        root = _release(tmp_path / mutation)
        sbom_path = root / "evidence" / "sbom.spdx.json"
        sbom = json.loads(sbom_path.read_text(encoding="utf-8-sig"))
        if mutation == "missing-package":
            sbom["packages"].pop()
            sbom["documentDescribes"].pop()
        elif mutation == "document-binding":
            sbom["documentDescribes"][-1] = "SPDXRef-Package-undeclared"
        else:
            sbom["documentNamespace"] = (
                "https://actorwright.invalid/spdx/1.0.0-preview.243/"
                + "C" * 40
            )
        _replace_release_evidence(
            root, "evidence/sbom.spdx.json", sbom)

        result = _run(root)

        assert result.returncode != 0, (
            f"release accepted {mutation} SBOM: "
            + result.stdout + result.stderr
        )
        assert "sbom" in (result.stdout + result.stderr).lower() or (
            "spdx" in (result.stdout + result.stderr).lower()
        )


def test_release_rejects_incomplete_or_misbound_test_summary(
    tmp_path: Path,
) -> None:
    mutations = {
        "missing-visual-authority": ("visualAuthority", None),
        "source-commit": ("sourceCommit", "C" * 40),
        "source-tag": ("sourceTag", "v1.0.0-preview.238"),
        "configuration": ("configuration", "Debug"),
        "project-count": ("projectsCompiled", 24),
        "warnings": ("warnings", 1),
        "errors": ("errors", 1),
        "command-count": ("exactCommandNames", 135),
        "python-count": (
            "standalonePythonCases", EXPECTED_STANDALONE_PYTHON_CASES - 1),
        "legacy-suites": ("legacyWorkspaceBoundSuites", "PASS"),
        "runtime-authority": ("runtimeAuthority", True),
        "visual-authority": ("visualAuthority", True),
    }
    for mutation, (field, value) in mutations.items():
        root = _release(tmp_path / mutation)
        summary_path = root / "evidence" / "test-summary.json"
        summary = json.loads(summary_path.read_text(encoding="utf-8-sig"))
        if value is None:
            summary.pop(field)
        else:
            summary[field] = value
        _replace_release_evidence(
            root, "evidence/test-summary.json", summary)

        result = _run(root)

        assert result.returncode != 0, (
            f"release accepted {mutation} test summary: "
            + result.stdout + result.stderr
        )
        assert "test summary" in (result.stdout + result.stderr).lower()


def test_rejects_source_and_undeclared_file(tmp_path: Path) -> None:
    root = _release(tmp_path)
    _bytes(root / "src" / "Program.cs", b"class Program {}")
    result = _run(root)
    assert result.returncode != 0
    assert "source" in (result.stdout + result.stderr).lower() or "undeclared" in (result.stdout + result.stderr).lower()


def test_rejects_hash_drift(tmp_path: Path) -> None:
    root = _release(tmp_path)
    _bytes(root / "docs" / "README.md", b"changed\n")
    result = _run(root)
    assert result.returncode != 0
    assert "hash" in (result.stdout + result.stderr).lower()


def test_rejects_zip_traversal(tmp_path: Path) -> None:
    root = _release(tmp_path)
    archive = tmp_path / "bad.zip"
    with zipfile.ZipFile(archive, "w") as handle:
        handle.writestr("../escape.txt", "bad")
    result = _run(root, "--zip", str(archive))
    assert result.returncode != 0
    assert "traversal" in (result.stdout + result.stderr).lower()


def test_release_builder_orders_hashes_and_verified_tag_creation(
    tmp_path: Path,
) -> None:
    script = (ROOT / "tools" / "release" / "build_release.ps1").read_text(
        encoding="utf-8")
    assert "[Array]::Sort($relativePaths, [StringComparer]::Ordinal)" in script
    helper_source = ROOT / "tools" / "release" / "verify_and_tag.ps1"
    assert helper_source.is_file(), "fixed release verify-and-tag helper is missing"
    helper_dir = tmp_path / "tools" / "release"
    helper_dir.mkdir(parents=True)
    helper = helper_dir / helper_source.name
    shutil.copy2(helper_source, helper)
    fake_verifier = helper_dir / "verify_release.py"
    verifier_args = helper_dir / "verifier-args.json"
    repository = tmp_path / "repository"
    repository.mkdir()
    tracked = repository / "tracked.txt"
    tracked.write_text("verified source\n", encoding="utf-8")
    git_context = (
        "git", "-c", f"safe.directory={repository.as_posix()}",
        "-C", str(repository),
    )
    for command in (
        (*git_context, "init"),
        (*git_context, "config", "user.email", "release-test@example.invalid"),
        (*git_context, "config", "user.name", "Actorwright Release Test"),
        (*git_context, "add", "tracked.txt"),
        (*git_context, "commit", "-m", "verified source"),
    ):
        result = subprocess.run(
            command, cwd=repository, text=True, capture_output=True, check=False)
        assert result.returncode == 0, result.stdout + result.stderr
    captured_commit = subprocess.run(
        (*git_context, "rev-parse", "HEAD"), cwd=repository, text=True,
        capture_output=True, check=True).stdout.strip()

    output_root = tmp_path / "release-root"
    zip_path = tmp_path / "release.zip"
    powershell = shutil.which("powershell.exe")
    git = shutil.which("git")
    assert powershell is not None
    assert git is not None
    helper_environment = os.environ.copy()
    for key in tuple(helper_environment):
        if key.startswith(("GIT_CONFIG_KEY_", "GIT_CONFIG_VALUE_")):
            helper_environment.pop(key)
    helper_environment.update({
        "GIT_CONFIG_COUNT": "1",
        "GIT_CONFIG_KEY_0": "safe.directory",
        "GIT_CONFIG_VALUE_0": repository.as_posix(),
    })

    def invoke(tag: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
             str(helper), "-ProjectRoot", str(repository),
             "-PythonPath", sys.executable, "-GitPath", git,
             "-OutputRoot", str(output_root), "-ZipPath", str(zip_path),
             "-Tag", tag, "-Commit", captured_commit,
             "-Version", "9.9.9-test"],
            cwd=repository, env=helper_environment, text=True,
            capture_output=True, check=False)

    fake_verifier.write_text(
        "import json, pathlib, sys\n"
        "pathlib.Path(__file__).with_name('verifier-args.json').write_text(json.dumps(sys.argv[1:]))\n"
        "raise SystemExit(23)\n",
        encoding="utf-8",
    )
    failed_tag = "v9.9.9-test-failed"
    failed = invoke(failed_tag)
    assert failed.returncode != 0
    assert "Release verification failed with exit code 23" in (
        failed.stdout + failed.stderr)
    missing = subprocess.run(
        (*git_context, "show-ref", "--verify", "--quiet", f"refs/tags/{failed_tag}"),
        cwd=repository, text=True, capture_output=True, check=False)
    assert missing.returncode == 1

    tracked.write_text("later source\n", encoding="utf-8")
    for command in ((*git_context, "add", "tracked.txt"),
                    (*git_context, "commit", "-m", "later source")):
        result = subprocess.run(
            command, cwd=repository, text=True, capture_output=True, check=False)
        assert result.returncode == 0, result.stdout + result.stderr
    fake_verifier.write_text(
        "import json, pathlib, sys\n"
        "pathlib.Path(__file__).with_name('verifier-args.json').write_text(json.dumps(sys.argv[1:]))\n"
        f"print('{{\"status\":\"PASS\",\"zipVerified\":true,"
        f"\"compatibilityVerdict\":\"FULL_COMPATIBLE\","
        f"\"sourceCommit\":\"{captured_commit}\",\"version\":\"9.9.9-test\"}}')\n",
        encoding="utf-8",
    )
    firewall_dir = repository / "tools" / "verification"
    firewall_dir.mkdir(parents=True)
    firewall_report_path = repository / "firewall-report-path.txt"
    (firewall_dir / "compatibility-firewall.ps1").write_text(
        "param([string]$Mode, [Parameter(ValueFromRemainingArguments=$true)][string[]]$ForwardedArguments)\n"
        "$index = [array]::IndexOf($ForwardedArguments, '-Output')\n"
        "$report = $ForwardedArguments[$index + 1]\n"
        "Set-Content -LiteralPath '" + str(firewall_report_path).replace("'", "''")
        + "' -Value $report\n"
        "@{tier='Full'; baselineId='publicSynthetic'; compatible=$true; verdict='FULL_COMPATIBLE'; "
        "fixtureBacked=$false; realInstallation=$false} | ConvertTo-Json -Compress | "
        "Set-Content -LiteralPath $report\nexit 0\n",
        encoding="utf-8",
    )
    successful_tag = "v9.9.9-test"
    succeeded = invoke(successful_tag)
    assert succeeded.returncode == 0, succeeded.stdout + succeeded.stderr
    Path(firewall_report_path.read_text(encoding="utf-8").strip()).unlink(missing_ok=True)
    assert json.loads(verifier_args.read_text(encoding="utf-8")) == [
        str(output_root), "--create-zip", str(zip_path), "--json"]
    tag_type = subprocess.run(
        (*git_context, "cat-file", "-t", f"refs/tags/{successful_tag}"),
        cwd=repository, text=True, capture_output=True, check=True).stdout.strip()
    peeled = subprocess.run(
        (*git_context, "rev-parse", f"refs/tags/{successful_tag}^{{}}"),
        cwd=repository, text=True, capture_output=True, check=True).stdout.strip()
    assert tag_type == "tag"
    assert peeled == captured_commit

    helper_reference = "tools\\release\\verify_and_tag.ps1"
    assert helper_reference in script
    assert script.index("SHA256SUMS") < script.index(helper_reference)
    assert script.index(helper_reference) < script.index(
        "Actorwright binary-only release: PASS")


def test_release_builder_derives_executed_python_case_count(tmp_path: Path) -> None:
    script = (ROOT / "tools" / "release" / "build_release.ps1").read_text(encoding="utf-8")
    verifier = _load_release_verifier()
    assert "$standalonePythonCases = [int]$pytestMatch.Groups['count'].Value" in script
    assert "standalonePythonCases = $standalonePythonCases" in script
    assert "-m tools.release.derive_build_pins" in script
    for field in (
        "standalonePythonSkipped", "orderedHelpSha256", "protocolReadinessSha256",
        "selectorInventorySha256", "selectorResultsSha256",
    ):
        assert f"{field} = $derivedPins.{field}" in script
    release = {
        "version": "1.0.0-preview.276", "sourceCommit": SOURCE_COMMIT,
        "sourceTag": "v1.0.0-preview.276",
        "dependencyVulnerabilityReportSha256": "A" * 64,
    }
    summary = {
        "schemaVersion": 1, "status": "PASS", "sourceCommit": SOURCE_COMMIT,
        "sourceTag": "v1.0.0-preview.276", "configuration": "Release",
        "projectsCompiled": 25, "warnings": 0, "errors": 0,
        "exactCommandNames": 142, "standalonePythonCases": 880,
        "standalonePythonSkipped": 4, "orderedHelpSha256": "A" * 64,
        "protocolReadinessSha256": "B" * 64,
        "selectorInventorySha256": "C" * 64,
        "selectorResultsSha256": "D" * 64,
        "legacyWorkspaceBoundSuites": "COMPILE_ONLY",
        "runtimeAuthority": False, "visualAuthority": False,
    }
    verifier.verify_test_summary(summary, release)
    verifier.verify_test_summary(
        {
            **summary,
            "sourceTag": "v1.0.0-preview.279",
            "standalonePythonCases": 893,
        },
        {
            **release,
            "version": "1.0.0-preview.279",
            "sourceTag": "v1.0.0-preview.279",
        },
    )
    evidence_root = tmp_path / "preview280-evidence"
    evidence = evidence_root / "evidence"
    evidence.mkdir(parents=True)
    commit = "A" * 40
    tree = "B" * 40
    config_hash = "C" * 64
    canonical_log = evidence / "canonical-build.log"
    canonical_log.write_text(
        f"EVIDENCE_SOURCE_COMMIT={commit}\n"
        f"EVIDENCE_SOURCE_TREE={tree}\n"
        "EVIDENCE_WORKTREE_STATUS=CLEAN\n"
        "EVIDENCE_RESTORE_CONFIG=K:\\Actorwright\\nuget.config\n"
        "EVIDENCE_RESTORE_CONFIG_MODE=explicit-override\n"
        f"EVIDENCE_RESTORE_CONFIG_SHA256={config_hash}\n"
        f"EVIDENCE_TRACKED_NUGET_CONFIG_SHA256={config_hash}\n"
        "EVIDENCE_NUGET_AUDIT=true\n"
        "EVIDENCE_NUGET_HTTP_CACHE=BYPASS\n"
        f"EVIDENCE_FINAL_SOURCE_COMMIT={commit}\n"
        f"EVIDENCE_FINAL_SOURCE_TREE={tree}\n"
        "EVIDENCE_FINAL_WORKTREE_STATUS=CLEAN\n"
        f"EVIDENCE_FINAL_RESTORE_CONFIG_SHA256={config_hash}\n"
        "9 passed, 2 skipped, 0 warnings in 0.10s\n",
        encoding="utf-8",
    )
    summary280 = {
        **summary,
        "sourceCommit": commit,
        "sourceTag": "v1.0.0-preview.280",
        "standalonePythonCases": 8,
        "standalonePythonSkipped": 2,
    }
    summary_path = evidence / "test-summary.json"
    summary_path.write_text(json.dumps(summary280), encoding="utf-8")
    release280 = {
        "version": "1.0.0-preview.280",
        "sourceCommit": commit,
        "sourceTag": "v1.0.0-preview.280",
        "sourceTree": tree,
        "dependencyVulnerabilityReportSha256": "D" * 64,
        "canonicalBuildLogSha256": verifier.digest(canonical_log),
        "testSummarySha256": verifier.digest(summary_path),
    }
    with pytest.raises(verifier.ReleaseError, match="canonical build"):
        verifier.verify_test_summary(summary280, release280, root=evidence_root)
    summary280["standalonePythonCases"] = 9
    summary_path.write_text(json.dumps(summary280), encoding="utf-8")
    release280["testSummarySha256"] = verifier.digest(summary_path)
    with pytest.raises(verifier.ReleaseError, match="evidence root"):
        verifier.verify_test_summary(summary280, release280)
    verifier.verify_test_summary(summary280, release280, root=evidence_root)
    with pytest.raises(verifier.ReleaseError, match="truthful static PASS"):
        verifier.verify_test_summary(
            {**summary, "standalonePythonCases": 881}, release
        )
    with pytest.raises(verifier.ReleaseError, match="field set mismatch"):
        verifier.verify_test_summary(
            {key: value for key, value in summary.items() if key != "selectorResultsSha256"},
            release,
        )
    with pytest.raises(verifier.ReleaseError, match="overlap pins are malformed"):
        verifier.verify_test_summary({**summary, "orderedHelpSha256": "bad"}, release)
    assert "independenceAndExchangeReleaseTests = 18" not in script
    assert "-AdvisoryCoverage public" in script
    assert "$vulnerabilityReport --public-release" in script
    assert "-RequireCleanTree" in script
    assert "-NoHttpCache" in script
    assert "evidence\\canonical-build.log" in script
    assert "canonicalBuildLogSha256" in script
    assert "sourceTree = $buildSourceTree" in script
    verifier_source = (ROOT / "tools" / "release" / "verify_release.py").read_text(
        encoding="utf-8")
    assert "def verify_canonical_build_evidence" in verifier_source
    assert "EVIDENCE_FINAL_RESTORE_CONFIG_SHA256" in verifier_source
    assert "Release source identity or cleanliness changed during package assembly" in script
    canonical_paths = (
        "tests/test_permission_profile.py",
        "tests/independence",
        "tests/exchange",
        "tests/release",
        "tests/architecture/test_validate_architecture.py",
        "tests/architecture/test_standalone_test_registry.py",
    )
    build_script = (ROOT / "tools" / "build" / "build.ps1").read_text(
        encoding="utf-8")
    assert all(path.replace("/", "\\") in build_script for path in canonical_paths)
    assert verifier.EXPECTED_PREVIEW275_STANDALONE_PYTHON_CASES == 317
    assert verifier.EXPECTED_PREVIEW272_STANDALONE_PYTHON_CASES == 315

    commit = "A" * 40
    tree = "B" * 40
    config_hash = "C" * 64
    log = tmp_path / "evidence" / "canonical-build.log"
    _bytes(log, (
        f"EVIDENCE_SOURCE_COMMIT={commit}\n"
        f"EVIDENCE_SOURCE_TREE={tree}\n"
        "EVIDENCE_WORKTREE_STATUS=CLEAN\n"
        "EVIDENCE_RESTORE_CONFIG=K:\\Actorwright\\nuget.config\n"
        "EVIDENCE_RESTORE_CONFIG_MODE=explicit-override\n"
        f"EVIDENCE_RESTORE_CONFIG_SHA256={config_hash}\n"
        f"EVIDENCE_TRACKED_NUGET_CONFIG_SHA256={config_hash}\n"
        "EVIDENCE_NUGET_AUDIT=true\n"
        "EVIDENCE_NUGET_HTTP_CACHE=BYPASS\n"
        f"EVIDENCE_FINAL_SOURCE_COMMIT={commit}\n"
        f"EVIDENCE_FINAL_SOURCE_TREE={tree}\n"
        "EVIDENCE_FINAL_WORKTREE_STATUS=CLEAN\n"
        f"EVIDENCE_FINAL_RESTORE_CONFIG_SHA256={config_hash}\n"
    ).encode("utf-8"))
    release = {
        "version": "1.0.0-preview.271",
        "sourceCommit": commit,
        "sourceTree": tree,
        "canonicalBuildLogSha256": hashlib.sha256(log.read_bytes()).hexdigest().upper(),
    }
    verifier.verify_canonical_build_evidence(tmp_path, release)
    report = tmp_path / "evidence" / "dependency-vulnerability-report.json"
    _bytes(report, b"{}")
    with pytest.raises(verifier.ReleaseError, match=(
        "explicit restore config lacks bound audit transport")):
        verifier.verify_canonical_build_evidence(tmp_path, release)
    _bytes(report, json.dumps({
        "auditTransport": {"restoreConfigSha256": config_hash},
    }).encode("utf-8"))
    verifier.verify_canonical_build_evidence(tmp_path, release)
    _bytes(report, json.dumps({
        "auditTransport": {"restoreConfigSha256": "E" * 64},
    }).encode("utf-8"))
    with pytest.raises(verifier.ReleaseError, match=(
        "audit transport restore config does not match canonical build")):
        verifier.verify_canonical_build_evidence(tmp_path, release)
    log.write_text(log.read_text(encoding="utf-8").replace(
        "EVIDENCE_WORKTREE_STATUS=CLEAN",
        "EVIDENCE_WORKTREE_STATUS=DIRTY"), encoding="utf-8")
    release["canonicalBuildLogSha256"] = hashlib.sha256(
        log.read_bytes()).hexdigest().upper()
    with pytest.raises(verifier.ReleaseError, match="not publication-grade"):
        verifier.verify_canonical_build_evidence(tmp_path, release)
def test_release_builder_binds_staged_cli_before_protocol_evidence() -> None:
    script = (ROOT / "tools" / "release" / "build_release.ps1").read_text(
        encoding="utf-8")
    binding = "$cli = Join-Path $staging 'cli\\actorwright.exe'"
    first_use = "$protocolCapabilitiesOutput = & $cli"
    assert binding in script
    assert script.index(binding) < script.index(first_use)
    assert "Test-Path -LiteralPath $cli -PathType Leaf" in script
    match = re.search(
        r"\$buildText,\s*'(?P<pattern>[^']+)'\)",
        script,
    )
    assert match is not None
    pattern = match.group("pattern").replace("(?<count>", "(?P<count>")
    summary = re.search(
        pattern,
        "148 passed, 2 skipped, 2 warnings in 49.88s\n",
    )
    assert summary is not None
    assert summary.group("count") == "148"


def test_release_builder_ready_catalog_matches_packaged_sort_order() -> None:
    script = (ROOT / "tools" / "release" / "build_release.ps1").read_text(
        encoding="utf-8")
    match = re.search(
        r"\$expectedProtocolV2ReadyCommands\s*=\s*@\((?P<body>.*?)\n\)",
        script,
        flags=re.DOTALL,
    )
    assert match is not None
    expected = re.findall(r"'([^']+)'", match.group("body"))
    assert expected == sorted(expected)
    assert expected == [
        "capabilities",
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "schema export",
        "version",
        "workspace preflight",
    ]


def test_rejects_noncanonical_schema_line_endings(tmp_path: Path) -> None:
    root = _release(tmp_path)
    schema = root / "schemas" / "issue.schema.json"
    schema.write_bytes(schema.read_bytes().replace(b"\n", b"\r\n"))
    replacement = hashlib.sha256(schema.read_bytes()).hexdigest().upper()
    sums = root / "SHA256SUMS"
    lines = sums.read_text(encoding="ascii").splitlines()
    sums.write_text(
        "\n".join(replacement + "  schemas/issue.schema.json" if line.endswith("  schemas/issue.schema.json") else line for line in lines) + "\n",
        encoding="ascii",
        newline="\n",
    )
    result = _run(root)
    assert result.returncode != 0
    assert "canonical utf-8 lf" in (result.stdout + result.stderr).lower()


def test_release_builder_normalizes_public_schema_bytes() -> None:
    script = (ROOT / "tools" / "release" / "build_release.ps1").read_text(encoding="utf-8")
    assert '$schemaText.Replace("`r`n", "`n").Replace("`r", "`n")' in script
    assert "[IO.File]::WriteAllText" in script
    assert "contracts\\exchange\\$exchangeVersion" in script
    assert "schemas\\exchange\\$exchangeVersion" in script
    assert "@('v1', 'v2')" in script


def test_packager_exercises_consumer_style_bundle_extraction() -> None:
    script = (ROOT / "tools" / "build" / "package.ps1").read_text(encoding="utf-8")
    assert "consumer-smoke-workspace" in script
    assert "consumer-smoke-bundle-extract" in script
    assert "packaged CLI consumer-style startup failed" in script
    assert "--resource-extraction-probe" in script
    assert "desktop resource probe failed" in script
    assert "contracts\\exchange\\$exchangeVersion" in script
    assert "schemas\\exchange\\$exchangeVersion" in script
    assert "@('v1', 'v2')" in script


def test_preview281_package_staging_requires_declared_canonical_launcher(
    tmp_path: Path,
) -> None:
    verifier = _load_release_verifier()
    legacy = _package(tmp_path / "legacy")
    assert verifier.verify_package_staging(
        legacy, metadata_only=True)["status"] == "PASS"

    legacy_launcher = legacy / "cli" / "actorwright.ps1"
    legacy_launcher.write_bytes(verifier.POWERSHELL_WRAPPER)
    with pytest.raises(verifier.ReleaseError, match="PowerShell launcher"):
        verifier.verify_package_staging(legacy, metadata_only=True)

    package_root = _package(tmp_path / "preview281")
    manifest_path = package_root / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["version"] = "1.0.0-preview.281"
    manifest["sourceLine"] = "preview.281-public"
    manifest["commandCount"] = 142
    capabilities_path = package_root / "capabilities.json"
    capabilities = json.loads(capabilities_path.read_text(encoding="utf-8"))
    capabilities["version"] = "1.0.0-preview.281"
    capabilities["sourceLine"] = "preview.281-public"
    capabilities["commands"] = [{"name": f"command-{index}"} for index in range(142)]
    _json(capabilities_path, capabilities)
    launcher = package_root / "cli" / "actorwright.ps1"
    launcher.write_bytes(verifier.POWERSHELL_WRAPPER)
    manifest["files"] = [
        {
            "path": path.relative_to(package_root).as_posix(),
            "size": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
        }
        for path in sorted(package_root.rglob("*"))
        if path.is_file() and path != manifest_path
    ]
    _json(manifest_path, manifest)

    assert verifier.verify_package_staging(
        package_root, metadata_only=True)["status"] == "PASS"

    undeclared = tmp_path / "undeclared"
    shutil.copytree(package_root, undeclared)
    undeclared_manifest_path = undeclared / "manifest.json"
    undeclared_manifest = json.loads(
        undeclared_manifest_path.read_text(encoding="utf-8"))
    undeclared_manifest["files"] = [
        row for row in undeclared_manifest["files"]
        if row["path"] != "cli/actorwright.ps1"
    ]
    _json(undeclared_manifest_path, undeclared_manifest)
    with pytest.raises(verifier.ReleaseError, match="inventory mismatch"):
        verifier.verify_package_staging(undeclared, metadata_only=True)

    missing = tmp_path / "missing"
    shutil.copytree(package_root, missing)
    (missing / "cli" / "actorwright.ps1").unlink()
    with pytest.raises(verifier.ReleaseError, match="PowerShell launcher"):
        verifier.verify_package_staging(missing, metadata_only=True)

    tampered = tmp_path / "tampered"
    shutil.copytree(package_root, tampered)
    (tampered / "cli" / "actorwright.ps1").write_bytes(b"changed wrapper")
    with pytest.raises(verifier.ReleaseError, match="PowerShell wrapper content differs"):
        verifier.verify_package_staging(tampered, metadata_only=True)


def test_packagers_copy_the_shared_hash_bound_launcher_source() -> None:
    verifier = _load_release_verifier()
    source = ROOT / "tools" / "build" / "actorwright.ps1"
    package_script = (ROOT / "tools" / "build" / "package.ps1").read_text(
        encoding="utf-8")
    release_script = (ROOT / "tools" / "release" / "build_release.ps1").read_text(
        encoding="utf-8-sig")

    assert source.read_bytes() == verifier.POWERSHELL_WRAPPER
    assert "$launcherSource = Join-Path $PSScriptRoot 'actorwright.ps1'" in package_script
    assert "Copy-Item -LiteralPath $launcherSource" in package_script
    assert package_script.index("Copy-Item -LiteralPath $launcherSource") < package_script.index(
        "$files = Get-ChildItem -LiteralPath $OutputRoot")
    assert (
        "$launcherSource = Join-Path $projectRoot 'tools\\build\\actorwright.ps1'"
        in release_script
    )
    assert "Copy-Item -LiteralPath $launcherSource" in release_script
    assert "Set-Content -LiteralPath (Join-Path $OutputRoot 'cli\\actorwright.ps1')" not in release_script


def test_packager_bounds_resource_probe_extraction_paths() -> None:
    script = (ROOT / "tools" / "build" / "package.ps1").read_text(
        encoding="utf-8")
    assert "$probeExtractRoot = Join-Path $artifacts 'x'" in script
    assert ".Substring(0, 12)" in script
    assert '"c-$probeId"' in script
    assert '"d-$probeId"' in script
    assert '"resource-probe-desktop-' not in script


def test_release_verifier_binds_fresh_probe_runtime_manifest(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    spec = importlib.util.spec_from_file_location(
        "actorwright_verify_release_under_test", VERIFY)
    assert spec is not None and spec.loader is not None
    verifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(verifier)
    root = _release(tmp_path)
    release = json.loads(
        (root / "actorwright-release.json").read_text(encoding="utf-8-sig"))
    entries = release["embeddedResourceClosures"][0]["entries"]

    def fake_probe(
        command: list[str],
        **kwargs: object,
    ) -> SimpleNamespace:
        environment = kwargs["env"]
        assert isinstance(environment, dict)
        runtime_hash = (
            "C" * 64
            if command[0].endswith("Actorwright.Desktop.exe")
            else "B" * 64
        )
        return SimpleNamespace(
            returncode=0,
            stderr="",
            stdout=json.dumps({
                "schemaVersion": 1,
                "accepted": True,
                "resourceBase": environment[
                    "DOTNET_BUNDLE_EXTRACT_BASE_DIR"],
                "runtimeManifestSha256": runtime_hash,
                "entries": entries,
            }),
        )

    monkeypatch.setattr(verifier.subprocess, "run", fake_probe)

    with pytest.raises(
        verifier.ReleaseError,
        match="extracted runtime manifest closure drift",
    ):
        verifier.verify_embedded_resource_closures(root, release)


def test_resource_closure_allows_absent_optional_provider_but_rejects_partial_bundle(
    tmp_path: Path,
) -> None:
    spec = importlib.util.spec_from_file_location(
        "actorwright_verify_release_optional_provider", VERIFY)
    assert spec is not None and spec.loader is not None
    verifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(verifier)
    root = _release(tmp_path)
    release = json.loads(
        (root / "actorwright-release.json").read_text(encoding="utf-8-sig"))
    closure = release["embeddedResourceClosures"][0]
    provider_roles = {
        "product-provider-registry", "product-provider-provenance",
        "product-provider-manifest", "product-provider-template-plugin",
        "product-provider-facegeom-carrier", "product-provider-facetint-manifest",
        "product-provider-facetint-source", "product-provider-dependency-manifest",
    }
    base_closure = {
        **closure,
        "entries": [row for row in closure["entries"] if row["role"] not in provider_roles],
    }
    verifier._normalized_closure(base_closure)

    partial_closure = {
        **base_closure,
        "entries": [
            *base_closure["entries"],
            next(row for row in closure["entries"]
                 if row["role"] == "product-provider-registry"),
        ],
    }
    with pytest.raises(verifier.ReleaseError, match="complete product-provider bundle"):
        verifier._normalized_closure(partial_closure)


def test_ci_installs_pinned_pytest_before_canonical_build() -> None:
    workflow = (ROOT / ".github" / "workflows" / "build.yml").read_text(encoding="utf-8")
    install = "python -m pip install pytest==8.4.2"
    build = "pwsh -NoProfile -File ./tools/build/build.ps1 -Configuration Release"
    assert install in workflow
    assert workflow.index(install) < workflow.index(build)
