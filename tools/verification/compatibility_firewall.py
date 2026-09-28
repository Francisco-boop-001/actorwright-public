"""Strict compatibility-firewall contract parsing and internal CLI capture.

The capture surface in this module is intentionally internal to tests and
release verification.  It observes public CLI behavior without becoming a
production dispatcher or exposing a baseline-maintenance command.
"""

from __future__ import annotations

import hashlib
import argparse
import base64
import binascii
import copy
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import stat
import unicodedata
import uuid
import zipfile
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from pathlib import Path, PureWindowsPath
from typing import Any, Literal


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
DEFAULT_BASELINE_REGISTRY = (
    REPOSITORY_ROOT / "tools" / "manifests" / "compatibility-baselines.json"
)
DEFAULT_BASELINE_ROOT = (
    REPOSITORY_ROOT / "tests" / "fixtures" / "compatibility-firewall"
)
CONSUMER_STAGE_REPORT_SCHEMA = (
    REPOSITORY_ROOT / "contracts" / "compatibility" / "v1"
    / "consumer-stage-report.schema.json"
)
EXPECTED_CONSUMER_STAGE_REPORT_SCHEMA_SHA256 = (
    "67CE223DAD7C277AE6AFB3BA19EE54E546DE654DC16C5C82774492A799EBB937"
)

SNAPSHOT_SCHEMA_ID = "urn:actorwright:compatibility:v1:snapshot"
SYNTHETIC_SNAPSHOT_SCHEMA_ID = "urn:actorwright:compatibility:v2:snapshot"
VOICE_DIALOGUE_SENTINELS = (
    "npc voice discover",
    "npc voice import",
    "npc voice synthesize",
    "npc dialogue analyze",
    "npc dialogue apply",
    "npc dialogue verify",
)
EXIT_MEANINGS = (
    (0, "Success"),
    (1, "General failure"),
    (2, "Usage or schema error"),
    (3, "Security refusal"),
    (4, "Domain validation failure"),
    (5, "Cancellation"),
)
PINNED_DOTNET = Path(os.environ.get(
    "ACTORWRIGHT_TEST_DOTNET",
    REPOSITORY_ROOT / "artifacts" / "tools" / "dotnet-sdk-10.0.301" / "dotnet.exe",
))
PINNED_DOTNET_MANIFEST = "dotnet-sdk-10.0.301-win-x64.json"
CLI_TIMEOUT_SECONDS = 20
CLI_MAX_OUTPUT_BYTES = 8 * 1024 * 1024
JOURNEY_FAILURE_OUTPUT_MAX_BYTES = 4096
EXPECTED_COMMAND_COUNT = 142
EXPECTED_PREVIEW_275_CAPABILITIES_SHA256 = "CA44D61F279B1C348F9FFBC2AA846F6EFAF5EE8F9BE75BF050BDF5C2B846E150"
EXPECTED_PREVIEW_275_COMMAND_ORDER_SHA256 = "0218342F89443C8BDBA380C89DA12B052653FF056CC4A33424D138C8264EA79E"
EXPECTED_PREVIEW_275_SELECTOR_SHA256 = "FD21FF9DB1E58A06EE4F1C656C30C2169A93333BBBA9B45A45EF4B08CEB494BF"
EXPECTED_PREVIEW_275_TEST_SUMMARY_SHA256 = "6F734914AA61C8B84B94DFB55C1288E7BEFCF32A11A0317D6659372125B415F8"
EXPECTED_PREVIEW_275_READINESS = {"v2": 11, "legacy": 131}
EXPECTED_PREVIEW_275_SELECTOR_CLASSES = {"runnable": 128, "fixture-bound": 30, "unverified": 0}
EXPECTED_PREVIEW_275_LAUNCHER_SHA256 = "637582E2F2CB6883973CE80E572D6632D0E88F06BA29A2C5DBE0CFCB4606DBA7"
CAPTURE_WORKSPACE_PARENT = REPOSITORY_ROOT / "artifacts" / "test-work"
CAPTURE_WORKSPACE_PREFIX = "compatibility-firewall-"
CAPTURE_OWNER_MARKER = ".compatibility-firewall-owner.json"
CAPTURE_OWNER_LOCK_SUFFIX = ".owner.lock"

# These probes exercise the shared public protocol-2 binder. Every projected
# option fact below is tied either to one of these observations or to a field in
# the public command contract; capture fails if a declared observation drifts.
BEHAVIOR_PROBE_CATALOGUE = (
    {"id": "unknown-option", "command": "workspace preflight", "arguments": ["workspace", "preflight", "--protocol", "2", "--json", "--compatibility-unknown", "x"], "requiredDiagnostics": ["option-unknown"], "forbiddenDiagnostics": [], "facts": []},
    {"id": "duplicate-option", "command": "workspace preflight", "arguments": ["workspace", "preflight", "--protocol", "2", "--json", "--game", "skyrimse", "--game", "skyrimse"], "requiredDiagnostics": ["option-duplicate"], "forbiddenDiagnostics": [], "facts": [{"command": "workspace preflight", "option": "game", "field": "repeatable", "value": False}]},
    {"id": "invalid-enum", "command": "workspace preflight", "arguments": ["workspace", "preflight", "--protocol", "2", "--json", "--game", "not-a-game"], "requiredDiagnostics": ["option-enum-value"], "forbiddenDiagnostics": [], "facts": [{"command": "workspace preflight", "option": "game", "field": "invalidDiagnostic", "value": "option-enum-value"}]},
    {"id": "accepted-enum-case", "command": "workspace preflight", "arguments": ["workspace", "preflight", "--protocol", "2", "--json", "--game", "SKYRIMSE"], "requiredDiagnostics": ["option-required"], "forbiddenDiagnostics": ["option-enum-value"], "facts": [{"command": "workspace preflight", "option": "game", "field": "caseSensitive", "value": False}]},
    {"id": "missing-option-value", "command": "workspace preflight", "arguments": ["workspace", "preflight", "--protocol", "2", "--json", "--game"], "requiredDiagnostics": ["option-value-required"], "forbiddenDiagnostics": [], "facts": [{"command": "workspace preflight", "option": "game", "field": "missingValueDiagnostic", "value": "option-value-required"}]},
    {"id": "required-option", "command": "npc assembly preflight", "arguments": ["npc", "assembly", "preflight", "--protocol", "2", "--json"], "requiredDiagnostics": ["option-required"], "forbiddenDiagnostics": [], "facts": []},
)
REQUIRED_BEHAVIOR_FACTS = (
    ("workspace preflight", "game", "repeatable", False, "duplicate-option"),
    ("workspace preflight", "game", "invalidDiagnostic", "option-enum-value", "invalid-enum"),
    ("workspace preflight", "game", "caseSensitive", False, "accepted-enum-case"),
    ("workspace preflight", "game", "missingValueDiagnostic", "option-value-required", "missing-option-value"),
)

_SELECTOR_SPECS = (
    (
        "selector:preview231-command-set",
        "cli",
        "--test-preview231-command-set",
        ("PASS exact 142-command set",),
    ),
    (
        "selector:preview261-v1-command-verification",
        "cli",
        "--test-preview261-v1-command-verification",
        ("PASS Preview.261 exhaustive V1 command verification",),
    ),
    (
        "selector:agent-protocol-compatibility",
        "cli",
        "--test-agent-protocol-compatibility",
        ("PASS agent protocol compatibility baseline",),
    ),
    (
        "selector:agent-protocol-registry",
        "architecture",
        "--test-agent-protocol-registry",
        ("PASS agent protocol registry",),
    ),
    (
        "selector:skyrim-npc-voice-cli",
        "cli",
        "--test-skyrim-npc-voice-cli",
        ("PASS skyrim-npc-voice-cli",),
    ),
    (
        "selector:skyrim-dialogue-command-binding",
        "cli",
        "--test-skyrim-dialogue-command-binding",
        ("PASS skyrim-dialogue-command-binding",),
    ),
    (
        "selector:skyrim-dialogue-cli",
        "cli",
        "--test-skyrim-dialogue-cli",
        (
            "PASS saved Windows Mantella setup -> CLI discovery/native synthesis/resume; discovery sends no POST",
            "PASS voice import -> full template -> fixture synthesis -> resume -> dialogue analyze/apply/verify (78 lines)",
            "PASS language override regeneration -> apply/verify; mixed or blank language refused",
        ),
    ),
)
_SELECTOR_GATE_IDS = tuple(spec[0] for spec in _SELECTOR_SPECS)
_PACKAGE_GATE_IDS = tuple(
    f"package:{command}" for command in VOICE_DIALOGUE_SENTINELS
)
_SOURCE_FOUNDATION_GATE_IDS = (
    "contract:manifest-format",
    "contract:command-surface",
    "contract:voice-dialogue-sentinels",
    "contract:metadata-help-schema-protocol-exit",
    "dispatch:no-input-no-write",
    "comparison:source-baseline",
    "verification:focused-tests",
)
_FULL_ONLY_GATE_IDS = (
    "build:pinned-release-solution",
    "selectors:complete-standalone-matrix",
    "architecture:standalone-registry",
    "architecture:repository-independence",
    "verification:python-suite",
    "verification:release",
    "comparison:source-package-baseline",
    "journey:create-to-package",
    "journey:wave-b-v1",
    "audit:source-scope",
)
_TIER_DEFINITIONS = {
    "Source": (
        _SOURCE_FOUNDATION_GATE_IDS + _SELECTOR_GATE_IDS,
        "SOURCE_COMPATIBLE",
    ),
    "Package": ((
        "verification:package-staging",
        "package:inventory-entrypoint",
        "comparison:source-package-baseline",
    ), "PACKAGE_COMPATIBLE"),
    "Full": (
        _SOURCE_FOUNDATION_GATE_IDS
        + _SELECTOR_GATE_IDS
        + _PACKAGE_GATE_IDS
        + _FULL_ONLY_GATE_IDS,
        "FULL_COMPATIBLE",
    ),
    "Candidate": (
        _SOURCE_FOUNDATION_GATE_IDS
        + _SELECTOR_GATE_IDS
        + _PACKAGE_GATE_IDS
        + _FULL_ONLY_GATE_IDS
        + ("pins:candidate",),
        "CANDIDATE_COMPATIBLE",
    ),
    "Admission": (
        _SOURCE_FOUNDATION_GATE_IDS + _SELECTOR_GATE_IDS + _PACKAGE_GATE_IDS
        + _FULL_ONLY_GATE_IDS + ("pins:candidate", "pins:promotion",
                                 "pins:consumer-admission"),
        "ADMISSION_COMPATIBLE",
    ),
    "Activation": (
        _SOURCE_FOUNDATION_GATE_IDS + _SELECTOR_GATE_IDS + _PACKAGE_GATE_IDS
        + _FULL_ONLY_GATE_IDS + ("pins:candidate", "pins:promotion",
                                 "pins:consumer-admission", "pins:consumer-activation"),
        "ACTIVATION_COMPATIBLE",
    ),
}

_VOICE_DIALOGUE_SCHEMA_CONTRACTS = {
    "npc voice discover": (("service-inventory", "output", "npc.voice-services.v1"),),
    "npc voice import": (("sample-authority", "output", "npc.voice-sample.v1"),),
    "npc voice synthesize": (
        ("dialogue-manifest", "input", "npc.dialogue.manifest.v1"),
        ("sample-authority", "input", "npc.voice-sample.v1"),
        ("synthesis", "output", "npc.voice-synthesis.v1"),
    ),
    "npc dialogue analyze": (
        ("manifest", "input-output", "npc.dialogue.manifest.v1"),
        ("sample-authority", "input", "npc.voice-sample.v1"),
        ("proposal", "output", "npc.dialogue.proposal.v1"),
    ),
    "npc dialogue apply": (
        ("proposal", "input", "npc.dialogue.proposal.v1"),
        ("synthesis", "input", "npc.voice-synthesis.v1"),
        ("output-manifest", "output", "npc.dialogue.output-manifest.v1"),
    ),
    "npc dialogue verify": (
        ("output-manifest", "input", "npc.dialogue.output-manifest.v1"),
        ("verification", "output", "npc.dialogue.verification.v1"),
    ),
}
_VOICE_DIALOGUE_REQUIRED_REFUSALS = {
    "npc voice import": "Voice command requires --sample.",
    "npc voice synthesize": "Voice command requires --manifest.",
    "npc dialogue analyze": "Analyze mode requires --manifest.",
    "npc dialogue apply": "Dialogue command requires --proposal.",
    "npc dialogue verify": "Dialogue command requires --manifest.",
}
_VOICE_DIALOGUE_SCHEMA_HASHES = {
    "npc voice discover": "7C775CE89B4F0DB52D77017A8CE2DBDD92B67D82695D86B0A8B170EA92B95347",
    "npc voice import": "03C1D7781EFF5F8675EA0FBA7FA7BF9B2A86502AA878E03C29F70D8DE4A07C2E",
    "npc voice synthesize": "F1C5AC94412C5B3E63D34A0D3C4A9E46967E0D08481B7C05BD5D1D1602CD4CD7",
    "npc dialogue analyze": "FBE3ED21DCB5A8CC3179BF79B451655B0CFA83940F7AF70805D0C74D530EAC42",
    "npc dialogue apply": "FEE7B0DD262A06731F0A88B8C01513596E0478DAABFFCB8C1BFE3C96470746EC",
    "npc dialogue verify": "B3B6D9CB91AA249CDB0970F4ACEA2903482BA45E22FA2334B6B7F452CE1DB668",
}
_VOICE_DIALOGUE_LEGACY_REQUEST_DIGESTS = {
    "npc voice discover": "F042C63AF8F2506CC77627C00E578687F4ADAB3597CC39BDC060380C51DBB2EC",
    "npc voice import": "8995BA2EC447D9D62075BB9D934F4799377C204E43DE9995D2BA44D3EE676548",
    "npc voice synthesize": "5F1561D60743ABF8E24E07DB4EB0D19662AA7DF134485BCF2862EC172D9EEF48",
    "npc dialogue analyze": "23A09B607FACBA4B00C473CEF2898E22203A78963B5C870E2B1123907CE821C7",
    "npc dialogue apply": "0F1A8AF9C3743A42DCF981D386FF4E81A22127884221F8B73B864092CF2228EB",
    "npc dialogue verify": "AD9B4F433BB902B2A3CA3BC167153956F3238C3DF415AF320646771833FFD0E1",
}

_SHA256_LENGTH = 64
_GIT_OBJECT_LENGTH = 40

CREATE_JOURNEY_COMMANDS = (
    "version",
    "capabilities",
    "schema export:npc create-from-jslot",
    "schema export:npc finish analyze",
    "schema export:npc finish apply",
    "schema export:npc finish verify",
    "npc create-from-jslot",
    "npc finish analyze",
    "npc finish apply",
    "npc finish verify",
    "package verify",
)
CREATE_JOURNEY_ARTIFACTS = {
    "planned-npc-output/npcmanager-package.json": "source-package-manifest",
    "planned-npc-output/Data/PackagedNpc.esp": "source-plugin",
    "finished/Data/PackagedNpc.esp": "finished-plugin",
    "finished/NPCManager/Evidence/finish-core-manifest.json": "finish-manifest",
    "finished.zip": "finished-archive",
}
WAVE_B_JOURNEY_COMMANDS = (
    "version", "capabilities",
    "schema export:workspace preflight", "schema export:preset inspect",
    "schema export:npc create-from-jslot", "schema export:records propose",
    "schema export:plugin write", "schema export:npc face-patch",
    "schema export:npc finish analyze", "schema export:npc finish apply",
    "schema export:npc finish verify",
    "schema export:npc placement interior analyze",
    "schema export:npc placement interior apply",
    "schema export:npc placement interior verify",
    "schema export:package verify", "schema export:plugin audit",
    "workspace preflight", "preset inspect", "npc create-from-jslot",
    "npc create-from-jslot", "records propose", "plugin write",
    "npc face-patch", "npc create-from-jslot", "npc create-from-jslot",
    "npc finish analyze", "npc finish analyze", "npc finish apply",
    "npc finish verify", "npc placement interior analyze",
    "npc placement interior apply", "npc placement interior verify",
    "package verify", "plugin audit",
)
WAVE_B_JOURNEY_ARTIFACTS = {
    **CREATE_JOURNEY_ARTIFACTS,
    "placement/PackagedNpc_InteriorPlacement.esp": "placement-plugin",
}
WAVE_B_MASTERS = [
    "Skyrim.esm", "ActorwrightBlankNpcProvider.esp", "SyntheticHair.esp",
    "SourceMaster3.esm", "SourceMaster4.esm", "SourceMaster5.esm",
    "SourceMaster6.esm", "EighthOutfit.esp",
]
WAVE_B_SOURCE_MASTERS = [
    "Skyrim.esm", "ActorwrightBlankNpcProvider.esp", "SyntheticHair.esp",
    "SourceMaster3.esm", "SourceMaster4.esm", "SourceMaster5.esm",
    "SourceMaster6.esm",
]

_COMMON_SCHEMA_IDS = {"1", "https://json-schema.org/draft/2020-12/schema", "urn:actorwright:protocol-v2:schema-export-result:v1", "urn:actorwright:protocol-v2:scoped-help-result:v1"}
EXPECTED_JOURNEY_SCHEMA_IDS = {
    "schema export:npc create-from-jslot": _COMMON_SCHEMA_IDS | {"actorwright-npc-build-preflight/1", "npc.create-from-jslot.request.v1", "skyrim-face-bake-authority/1", "urn:actorwright:protocol-v2:npc-create-from-jslot-build-result:v1", "urn:actorwright:protocol-v2:npc-create-preflight-result:v1"},
    "schema export:npc finish analyze": _COMMON_SCHEMA_IDS | {"npc.finish-core.proposal.v1", "npc.finish-core.proposal.v2", "npc.finish-core.proposal.v3", "npc.finish-core.proposal.v4", "npc.finish-core.request.v1", "npc.finish-core.request.v2", "npc.finish-core.request.v3", "npc.finish-core.request.v4", "npc.finish-core.validation.v1", "urn:actorwright:protocol-v2:finish-analyze-result:v1"},
    "schema export:npc finish apply": _COMMON_SCHEMA_IDS | {"npc.finish-core.proposal.v1", "npc.finish-core.proposal.v2", "npc.finish-core.proposal.v3", "npc.finish-core.proposal.v4", "npc.finish-core.request.v1", "npc.finish-core.request.v2", "npc.finish-core.request.v3", "npc.finish-core.request.v4", "npc.finish-core.validation.v1", "urn:actorwright:protocol-v2:finish-apply-result:v1"},
    "schema export:npc finish verify": _COMMON_SCHEMA_IDS | {"npc.finish-core.manifest.v1", "npc.finish-core.manifest.v2", "npc.finish-core.verification.v1", "npc.finish-core.verification.v2", "urn:actorwright:protocol-v2:finish-verify-result:v1"},
    "schema export:workspace preflight": _COMMON_SCHEMA_IDS | {"npcmanager-reviewed-game-intake/2", "urn:actorwright:protocol-v2:workspace-preflight-result:v1"},
    "schema export:preset inspect": _COMMON_SCHEMA_IDS | {"actorwright-preset-inspection/1", "actorwright-preset-inspection/2", "urn:actorwright:protocol-v2:preset-inspect-result:v1"},
    "schema export:records propose": _COMMON_SCHEMA_IDS | {"record-proposal.hdpt.v1"},
    "schema export:plugin write": _COMMON_SCHEMA_IDS | {"record-proposal.hdpt.v1"},
    "schema export:npc face-patch": _COMMON_SCHEMA_IDS,
    "schema export:npc placement interior analyze": _COMMON_SCHEMA_IDS | {"npc.interior-placement.proposal.v1", "npc.interior-placement.request.v1"},
    "schema export:npc placement interior apply": _COMMON_SCHEMA_IDS | {"npc.interior-placement.manifest.v1", "npc.interior-placement.proposal.v1", "npc.interior-placement.request.v1"},
    "schema export:npc placement interior verify": _COMMON_SCHEMA_IDS | {"npc.interior-placement.manifest.v1", "npc.interior-placement.verification.v1"},
    "schema export:package verify": _COMMON_SCHEMA_IDS,
    "schema export:plugin audit": _COMMON_SCHEMA_IDS,
}


class FirewallError(ValueError):
    """Raised when a compatibility contract fails closed."""


def authenticate_harness_checkout(
    repository_root: str | Path,
    *,
    expected_commit: str,
    expected_tree: str,
    expected_source_closure: dict[str, Any],
) -> dict[str, Any]:
    """Bind a journey runner to the exact clean commit containing its sources."""

    repository = Path(repository_root).resolve()
    attached = subprocess.run(
        ["git", "-c", f"safe.directory={repository.as_posix()}",
         "symbolic-ref", "--quiet", "HEAD"],
        cwd=repository, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        check=False, shell=False,
    )
    if attached.returncode == 0:
        raise FirewallError("journey harness checkout must use detached HEAD")
    if attached.returncode != 1:
        raise FirewallError("journey harness detached HEAD could not be authenticated")
    status = _git(
        repository, "status", "--porcelain=v1", "--untracked-files=all"
    ).decode("utf-8", errors="strict")
    if status:
        raise FirewallError("journey harness checkout is dirty or uncommitted")
    commit = _git(repository, "rev-parse", "HEAD").decode().strip()
    tree = _git(repository, "rev-parse", "HEAD^{tree}").decode().strip()
    source_root = "tests/NpcManager.Cli.Tests"
    relevant_roots = (source_root, "src", "tools/verification", "tests/release")
    ignored = _git(
        repository, "ls-files", "--others", "--ignored", "--exclude-standard",
        "--directory", "-z", "--", *relevant_roots,
    ).decode("utf-8", errors="strict").split("\0")
    allowed_outputs = {
        f"{source_root}/bin/", f"{source_root}/obj/",
        "tools/verification/__pycache__/", "tests/release/__pycache__/",
    }
    def generated_output(path: str) -> bool:
        return path in allowed_outputs or (
            path.startswith("src/") and path.endswith("/") and
            any(part in {"bin", "obj"} for part in path.split("/"))
        )
    if any(path and not generated_output(path) for path in ignored):
        raise FirewallError("journey harness checkout is dirty or uncommitted (ignored source)")
    source_tree = _git(
        repository, "rev-parse", f"HEAD:{source_root}"
    ).decode().strip()
    listing = _git(
        repository, "ls-tree", "-r", "--full-tree", "HEAD", "--", source_root
    )
    lines = tuple(line for line in listing.splitlines() if line)
    required = source_root + "/CompatibilityJourneyObservation.cs"
    if not any(line.decode("utf-8", errors="strict").endswith("\t" + required) for line in lines):
        raise FirewallError("committed harness source closure is missing observation source")
    identity = {
        "commit": _require_hex(commit, "harness commit", _GIT_OBJECT_LENGTH),
        "tree": _require_hex(tree, "harness tree", _GIT_OBJECT_LENGTH),
        "sourceClosure": {
            "root": source_root,
            "tree": _require_hex(source_tree, "harness source tree", _GIT_OBJECT_LENGTH),
            "fileCount": len(lines),
            "sha256": _sha256(listing).upper(),
        },
    }
    expected = {
        "commit": _require_hex(expected_commit, "expected harness commit", _GIT_OBJECT_LENGTH),
        "tree": _require_hex(expected_tree, "expected harness tree", _GIT_OBJECT_LENGTH),
        "sourceClosure": expected_source_closure,
    }
    if identity != expected:
        raise FirewallError("journey harness checkout does not match expected immutable identity")
    return identity


def create_owned_journey_capture(
    parent: str | Path = CAPTURE_WORKSPACE_PARENT,
) -> tuple[Path, str]:
    """Atomically create a short invocation-owned capture root."""

    capture_parent = Path(parent).resolve()
    if not capture_parent.is_absolute() or capture_parent.drive.casefold() != "k:":
        raise FirewallError("journey capture parent must be absolute and K-local")
    capture_parent.mkdir(parents=True, exist_ok=True)
    for _ in range(16):
        token = uuid.uuid4().hex
        root = capture_parent / ("j-" + token)
        lock_path = capture_parent / (root.name + CAPTURE_OWNER_LOCK_SUFFIX)
        try:
            with lock_path.open("xb") as stream:
                stream.write(token.encode("ascii"))
        except FileExistsError:
            continue
        try:
            root.mkdir()
        except FileExistsError:
            if lock_path.read_bytes() == token.encode("ascii"):
                lock_path.unlink()
            continue
        marker = {
            "formatVersion": 1,
            "kind": "journey-capture",
            "token": token,
        }
        marker_path = root / CAPTURE_OWNER_MARKER
        marker_created = False
        try:
            with marker_path.open("xb") as stream:
                stream.write(canonical_json_bytes(marker))
            marker_created = True
        except Exception:
            if marker_created and marker_path.is_file() and marker_path.read_bytes() == canonical_json_bytes(marker):
                marker_path.unlink()
            root.rmdir()
            if lock_path.read_bytes() == token.encode("ascii"):
                lock_path.unlink()
            raise
        return root, token
    raise FirewallError("could not atomically allocate a journey capture root")


def remove_owned_journey_capture(
    root: str | Path,
    token: str,
    parent: str | Path = CAPTURE_WORKSPACE_PARENT,
) -> bool:
    """Remove only an exact marker-matching invocation-owned capture root."""

    capture_root = Path(root).resolve()
    capture_parent = Path(parent).resolve()
    if (
        not capture_parent.is_absolute()
        or capture_parent.drive.casefold() != "k:"
        or capture_root.parent != capture_parent
        or not re.fullmatch(r"[0-9a-f]{32}", token)
        or capture_root.name != "j-" + token
    ):
        return False
    marker_path = capture_root / CAPTURE_OWNER_MARKER
    lock_path = capture_parent / (capture_root.name + CAPTURE_OWNER_LOCK_SUFFIX)
    if not marker_path.is_file() or not lock_path.is_file():
        return False
    if lock_path.read_bytes() != token.encode("ascii"):
        return False
    try:
        marker = _load_json(marker_path)
    except (OSError, json.JSONDecodeError, FirewallError):
        return False
    if marker != {
        "formatVersion": 1,
        "kind": "journey-capture",
        "token": token,
    }:
        return False
    shutil.rmtree(capture_root)
    lock_path.unlink()
    return True


@dataclass(frozen=True)
class Baseline:
    baseline_id: str
    manifest_path: Path
    snapshot: dict[str, Any]
    member_paths: tuple[Path, ...]


@dataclass(frozen=True)
class CliTarget:
    """One exact CLI entrypoint observed in an isolated K-local workspace."""

    name: str
    entrypoint: Path
    workspace_root: Path
    expected_sha256: str | None
    dotnet_path: Path = PINNED_DOTNET


@dataclass(frozen=True)
class CliObservation:
    """Bounded process evidence for one CLI invocation."""

    arguments: tuple[str, ...]
    exit_code: int
    stdout: bytes
    stderr: bytes
    json_value: Any


@dataclass(frozen=True)
class SealedPackageEvidence:
    """Authenticated release inputs that bind one extracted package CLI."""

    package_root: Path
    release_zip: Path
    expected_zip_length: int
    expected_zip_sha256: str
    source_tag: str
    source_commit: str
    source_tree: str


@dataclass(frozen=True)
class Difference:
    """One exact incompatible leaf observed by a compatibility gate."""

    layer: str
    subject: str
    pointer: str
    expected: Any
    actual: Any
    gate: str


@dataclass(frozen=True)
class SemanticRule:
    workflow_id: str
    pointer: str
    normalizer: str


@dataclass(frozen=True)
class AcceptedDifference:
    issue_id: str
    rationale: str
    regression_selector: str
    affected_command: str
    workflow_id: str
    pointer: str
    baseline_value: Any
    candidate_value: Any


@dataclass(frozen=True)
class ValidatedPolicy:
    semantic_rules: tuple[SemanticRule, ...]
    accepted_differences: tuple[AcceptedDifference, ...]

    @classmethod
    def parse(cls, value: dict[str, Any]) -> "ValidatedPolicy":
        return validate_policy(value)


@dataclass(frozen=True)
class GateResult:
    gate_id: str
    required: bool
    status: Literal["PASS", "FAIL", "BLOCKED", "NOT_APPLICABLE"]
    evidence: tuple[str, ...]
    differences: tuple[Difference, ...]


@dataclass(frozen=True)
class PinNode:
    """One producer or consumer identity available at a compatibility stage."""

    pin_id: str
    stage: str
    value: str
    evidence_path: str
    byte_path: Path | None = None
    expected_length: int | None = None
    expected_sha256: str | None = None
    resolved: bool = True


@dataclass(frozen=True)
class PinEdge:
    """One directional producer-to-consumer identity binding."""

    pin_id: str
    stage: str
    producer: str
    consumer: str
    identity_class: str


@dataclass(frozen=True)
class PinRow:
    pin_id: str
    stage: str
    producer: str
    consumer: str
    producer_value: str
    consumer_value: str
    identity_class: str
    producer_evidence_path: str
    consumer_evidence_path: str
    status: Literal["PASS", "FAIL"]


@dataclass(frozen=True)
class PinGraphEvaluation:
    stage: str
    compatible: bool
    verdict: str
    fixture_backed: bool
    real_installation: bool
    pin_rows: tuple[PinRow, ...]
    errors: tuple[str, ...]


@dataclass(frozen=True)
class TierEvaluation:
    tier: str
    compatible: bool
    verdict: str
    gates: tuple[GateResult, ...]


_PIN_STAGE_MEMBERS = {
    "Source": frozenset({"Source"}),
    "Package": frozenset({"Package"}),
    "Full": frozenset({"Source", "Package", "Full"}),
    "Candidate": frozenset({"Source", "Package", "Full", "Candidate"}),
    "Admission": frozenset(
        {"Source", "Package", "Full", "Candidate", "Admission"}
    ),
    "Activation": frozenset(
        {"Source", "Package", "Full", "Candidate", "Admission", "Activation"}
    ),
}
_PIN_STAGE_ORDER = {
    "Source": 0,
    "Package": 0,
    "Full": 1,
    "Candidate": 2,
    "Admission": 3,
    "Activation": 4,
}
_PIN_STAGE_VERDICTS = {
    "Source": "SOURCE_COMPATIBLE",
    "Package": "PACKAGE_COMPATIBLE",
    "Full": "FULL_COMPATIBLE",
    "Candidate": "CANDIDATE_COMPATIBLE",
    "Admission": "ADMISSION_COMPATIBLE",
    "Activation": "ACTIVATION_COMPATIBLE",
}

SOURCE_PIN_IDS = frozenset({
    "source-overlap:version", "source-overlap:buildSourceCommit",
    "source-overlap:sourceLine",
    "source-overlap:commandCount", "source-overlap:orderedHelpIdentity",
    "source-overlap:protocolReadinessIdentity",
    "source-overlap:selectorInventory", "source-overlap:selectorResults",
    "source-overlap:testPassCount", "source-overlap:testSkipCount",
})
SOURCE_PIN_CLASSES = frozenset({
    "source-version", "git-commit", "source-line", "command-count", "ordered-help-sha256",
    "protocol-readiness-sha256", "selector-inventory-sha256",
    "selector-results-sha256", "test-pass-count", "test-skip-count",
})
PACKAGE_ROOT_PIN_IDS = frozenset({
    "release-member-inventory",
    "release-evidence:capabilitiesSha256", "release-evidence:sbomSha256",
    "release-evidence:testSummarySha256",
    "release-evidence:dependencyVulnerabilityReportSha256",
    "release-evidence:canonicalBuildLogSha256",
    "source-overlap:schemaInventory",
})
PACKAGE_ROOT_PIN_CLASSES = frozenset({
    "release-member-inventory-sha256", "evidence-sha256",
    "schema-inventory-sha256",
})
PACKAGE_ZIP_PIN_IDS = PACKAGE_ROOT_PIN_IDS | frozenset({
    "source-overlap:releaseFileInventory",
})
PACKAGE_ZIP_PIN_CLASSES = PACKAGE_ROOT_PIN_CLASSES | frozenset({
    "release-file-inventory",
})
FULL_ONLY_PIN_IDS = frozenset({
    "source-overlap:releaseVersion", "source-overlap:sourceCommit",
    "source-overlap:sourceTag", "source-overlap:sourceTree",
    "source-overlap:sbomVersion", "source-overlap:sbomCommit",
    "source-overlap:sbomPackageIdentity",
})
FULL_ONLY_PIN_CLASSES = frozenset({
    "release-version", "source-sourceCommit", "source-sourceTag", "source-tree",
    "sbom-version", "sbom-commit", "sbom-package-identity",
})
FULL_ROOT_PIN_IDS = SOURCE_PIN_IDS | PACKAGE_ROOT_PIN_IDS | FULL_ONLY_PIN_IDS
FULL_ZIP_PIN_IDS = SOURCE_PIN_IDS | PACKAGE_ZIP_PIN_IDS | FULL_ONLY_PIN_IDS
FULL_ROOT_PIN_CLASSES = SOURCE_PIN_CLASSES | PACKAGE_ROOT_PIN_CLASSES | FULL_ONLY_PIN_CLASSES
FULL_ZIP_PIN_CLASSES = SOURCE_PIN_CLASSES | PACKAGE_ZIP_PIN_CLASSES | FULL_ONLY_PIN_CLASSES
LEGACY_UNPROVEN_PIN_IDS = frozenset({
    "source-overlap:orderedHelpIdentity", "source-overlap:selectorInventory",
    "source-overlap:selectorResults", "source-overlap:testSkipCount",
})
LEGACY_UNPROVEN_PIN_CLASSES = frozenset({
    "ordered-help-sha256", "selector-inventory-sha256",
    "selector-results-sha256", "test-skip-count",
})
LEGACY_FULL_ROOT_PIN_IDS = FULL_ROOT_PIN_IDS - LEGACY_UNPROVEN_PIN_IDS
LEGACY_FULL_ZIP_PIN_IDS = FULL_ZIP_PIN_IDS - LEGACY_UNPROVEN_PIN_IDS
LEGACY_FULL_ROOT_PIN_CLASSES = FULL_ROOT_PIN_CLASSES - LEGACY_UNPROVEN_PIN_CLASSES
LEGACY_FULL_ZIP_PIN_CLASSES = FULL_ZIP_PIN_CLASSES - LEGACY_UNPROVEN_PIN_CLASSES
_EVIDENCE_FIELDS = frozenset({
    "capabilitiesSha256", "sbomSha256", "testSummarySha256",
    "dependencyVulnerabilityReportSha256", "canonicalBuildLogSha256",
})
SOURCE_NODE_IDS = frozenset({
    f"overlap.{side}:{pin_id.removeprefix('source-overlap:')}"
    for pin_id in SOURCE_PIN_IDS for side in ("producer", "consumer")
})
PACKAGE_ROOT_NODE_IDS = frozenset({
    "release.inventory.actual", "release.inventory.declared",
    *(f"evidence.actual:{field}" for field in _EVIDENCE_FIELDS),
    *(f"release.declared:{field}" for field in _EVIDENCE_FIELDS),
    "overlap.producer:schemaInventory", "overlap.consumer:schemaInventory",
})
PACKAGE_ZIP_NODE_IDS = PACKAGE_ROOT_NODE_IDS | frozenset({
    "overlap.producer:releaseFileInventory",
    "overlap.consumer:releaseFileInventory",
})
PACKAGE_STAGE_ROOT_NODE_IDS = frozenset({
    f"package.{side}:{pin_id}" for pin_id in PACKAGE_ROOT_PIN_IDS
    for side in ("producer", "consumer")
})
PACKAGE_STAGE_ZIP_NODE_IDS = frozenset({
    f"package.{side}:{pin_id}" for pin_id in PACKAGE_ZIP_PIN_IDS
    for side in ("producer", "consumer")
})
FULL_ROOT_NODE_IDS = SOURCE_NODE_IDS | PACKAGE_ROOT_NODE_IDS
FULL_ZIP_NODE_IDS = SOURCE_NODE_IDS | PACKAGE_ZIP_NODE_IDS
FULL_ONLY_NODE_IDS = frozenset({
    f"overlap.{side}:{pin_id.removeprefix('source-overlap:')}"
    for pin_id in FULL_ONLY_PIN_IDS for side in ("producer", "consumer")
})
FULL_ROOT_NODE_IDS = FULL_ROOT_NODE_IDS | FULL_ONLY_NODE_IDS
FULL_ZIP_NODE_IDS = FULL_ZIP_NODE_IDS | FULL_ONLY_NODE_IDS
LEGACY_FULL_ROOT_NODE_IDS = FULL_ROOT_NODE_IDS - frozenset({
    f"overlap.{side}:{pin_id.removeprefix('source-overlap:')}"
    for pin_id in LEGACY_UNPROVEN_PIN_IDS for side in ("producer", "consumer")
})
LEGACY_FULL_ZIP_NODE_IDS = FULL_ZIP_NODE_IDS - frozenset({
    f"overlap.{side}:{pin_id.removeprefix('source-overlap:')}"
    for pin_id in LEGACY_UNPROVEN_PIN_IDS for side in ("producer", "consumer")
})


def resolve_tag_pin_nodes(repository: str | Path, tag: str) -> tuple[PinNode, ...]:
    """Resolve an existing annotated tag into exact name, commit, and tree pins."""

    lexical_root, reparse = _lexical_path_reparse(repository)
    if reparse is not None:
        raise FirewallError(f"repository-root-reparse:{reparse}")
    root = lexical_root.resolve()
    if not tag or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", tag):
        raise FirewallError("resolved tag name is invalid")
    reference = f"refs/tags/{tag}"
    try:
        tag_type = _git(root, "cat-file", "-t", reference).decode().strip()
        commit = _git(root, "rev-parse", f"{reference}^{{commit}}").decode().strip()
        tree = _git(root, "rev-parse", f"{reference}^{{tree}}").decode().strip()
    except (FirewallError, OSError) as exc:
        raise FirewallError(f"candidate tag is unresolved: {tag}") from exc
    if tag_type != "tag":
        raise FirewallError("candidate tag must be an existing annotated tag")
    evidence = f"{root.as_posix()}#{reference}"
    return (
        PinNode("source.tag.name", "Candidate", tag, evidence),
        PinNode(
            "source.tag.peeledCommit", "Candidate",
            _require_hex(commit, "resolved tag commit", _GIT_OBJECT_LENGTH),
            evidence + "^{commit}", resolved=True,
        ),
        PinNode(
            "source.tag.peeledTree", "Candidate",
            _require_hex(tree, "resolved tag tree", _GIT_OBJECT_LENGTH),
            evidence + "^{tree}", resolved=True,
        ),
    )


def validate_pin_graph(
    stage: str,
    nodes: list[PinNode] | tuple[PinNode, ...],
    edges: list[PinEdge] | tuple[PinEdge, ...],
    *,
    fixture_backed: bool = False,
) -> PinGraphEvaluation:
    """Validate graph structure only; artifact builders issue milestone verdicts."""

    visible_stages = _PIN_STAGE_MEMBERS.get(stage)
    if visible_stages is None:
        raise FirewallError(f"unknown pin graph stage: {stage}")
    node_list = tuple(nodes)
    edge_list = tuple(edges)
    by_id: dict[str, PinNode] = {}
    errors: list[str] = []
    for node in node_list:
        if node.stage not in _PIN_STAGE_ORDER:
            errors.append(f"unknown-node-stage:{node.pin_id}:{node.stage}")
        if not node.pin_id or node.pin_id in by_id:
            errors.append(f"duplicate-node:{node.pin_id or '<empty>'}")
        else:
            by_id[node.pin_id] = node
    edge_ids: set[str] = set()
    for edge in edge_list:
        if edge.stage not in _PIN_STAGE_ORDER:
            errors.append(f"unknown-edge-stage:{edge.pin_id}:{edge.stage}")
        if not edge.pin_id or edge.pin_id in edge_ids:
            errors.append(f"duplicate-edge:{edge.pin_id or '<empty>'}")
        else:
            edge_ids.add(edge.pin_id)

    visible_nodes = {
        pin_id: node for pin_id, node in by_id.items()
        if node.stage in visible_stages
    }

    # Phase 1: authenticate physical bytes before trusting any declared edge.
    for node in visible_nodes.values():
        if node.byte_path is None:
            if node.expected_length is not None or node.expected_sha256 is not None:
                errors.append(f"byte-path-missing:{node.pin_id}")
            continue
        path = Path(node.byte_path)
        if not path.is_file() or path.is_symlink():
            errors.append(f"byte-missing:{node.pin_id}:{path}")
            continue
        actual_length, actual_sha256 = _sha256_file_and_length(path)
        if node.expected_length is None or actual_length != node.expected_length:
            errors.append(
                f"byte-length:{node.pin_id}:expected={node.expected_length}:actual={actual_length}"
            )
        if node.expected_sha256 is None:
            errors.append(f"byte-sha256-missing:{node.pin_id}")
        elif actual_sha256.upper() != node.expected_sha256.upper():
            errors.append(
                f"byte-sha256:{node.pin_id}:expected={node.expected_sha256.upper()}:"
                f"actual={actual_sha256.upper()}"
            )
    if errors:
        return PinGraphEvaluation(
            stage, False, "INCOMPATIBLE", fixture_backed, False, (), tuple(errors)
        )

    # Phase 2: materialize diagnostic producer/consumer rows and compare values.
    rows: list[PinRow] = []
    visible_edges = tuple(edge for edge in edge_list if edge.stage in visible_stages)
    for edge in visible_edges:
        producer = visible_nodes.get(edge.producer)
        consumer = visible_nodes.get(edge.consumer)
        producer_value = producer.value if producer else "<missing>"
        consumer_value = consumer.value if consumer else "<missing>"
        producer_evidence = (
            producer.evidence_path if producer else f"<missing:{edge.producer}>"
        )
        consumer_evidence = (
            consumer.evidence_path if consumer else f"<missing:{edge.consumer}>"
        )
        hex_identity = edge.identity_class in {
            "sha256", "git-commit", "git-tree", "source-tree",
            "release-member-sha256", "evidence-sha256",
            "zip-member-sha256", "candidate-member-sha256",
            "package-sha256", "capabilities-sha256", "sbom-sha256",
            "test-summary-sha256", "contract-schema-sha256",
        }
        values_match = (
            producer_value.casefold() == consumer_value.casefold()
            if hex_identity else producer_value == consumer_value
        )
        status: Literal["PASS", "FAIL"] = (
            "PASS"
            if producer is not None
            and consumer is not None
            and bool(producer_value)
            and values_match
            else "FAIL"
        )
        rows.append(PinRow(
            edge.pin_id, edge.stage, edge.producer, edge.consumer,
            producer_value, consumer_value, edge.identity_class,
            producer_evidence, consumer_evidence, status,
        ))
        if producer is None:
            errors.append(f"missing-producer:{edge.pin_id}:{edge.producer}")
        if consumer is None:
            errors.append(f"missing-consumer:{edge.pin_id}:{edge.consumer}")
        if producer is not None and consumer is not None and status == "FAIL":
            errors.append(f"pin-mismatch:{edge.pin_id}")

    # Phase 3: reject self hashes, backward references, unresolved tags, and cycles.
    adjacency: dict[str, list[str]] = {pin_id: [] for pin_id in visible_nodes}
    for edge in visible_edges:
        if edge.producer == edge.consumer:
            errors.append(f"self-hash:{edge.pin_id}:{edge.producer}")
        producer = by_id.get(edge.producer)
        consumer = by_id.get(edge.consumer)
        if producer is not None and consumer is not None:
            if _PIN_STAGE_ORDER[producer.stage] > _PIN_STAGE_ORDER[consumer.stage]:
                errors.append(
                    f"later-node:{edge.pin_id}:{producer.pin_id}->{consumer.pin_id}"
                )
            if producer.pin_id in visible_nodes and consumer.pin_id in visible_nodes:
                adjacency[producer.pin_id].append(consumer.pin_id)
    if stage in {"Candidate", "Admission", "Activation"}:
        for node in visible_nodes.values():
            if node.pin_id.startswith("source.tag.peeled") and not node.resolved:
                errors.append(f"unresolved-tag:{node.pin_id}")

    visiting: set[str] = set()
    visited: set[str] = set()

    def visit(pin_id: str) -> bool:
        if pin_id in visiting:
            return True
        if pin_id in visited:
            return False
        visiting.add(pin_id)
        if any(visit(child) for child in adjacency.get(pin_id, ())):
            return True
        visiting.remove(pin_id)
        visited.add(pin_id)
        return False

    if any(visit(pin_id) for pin_id in adjacency if pin_id not in visited):
        errors.append("cycle:pin-graph")

    structurally_valid = not errors and all(row.status == "PASS" for row in rows)
    blocked = structurally_valid and stage in {"Admission", "Activation"}
    return PinGraphEvaluation(
        stage=stage,
        compatible=structurally_valid and not blocked,
        verdict=("BLOCKED" if blocked else "PIN_GRAPH_VALID")
        if structurally_valid else "INCOMPATIBLE",
        fixture_backed=fixture_backed,
        # A caller assertion is not authenticated consumer activation evidence.
        real_installation=False,
        pin_rows=tuple(rows),
        errors=tuple(errors),
    )


def _issue_milestone(
    stage: str,
    graph: PinGraphEvaluation,
    *,
    nodes: tuple[PinNode, ...],
    required_node_ids: frozenset[str],
    allowed_node_ids: frozenset[str],
    required_pin_ids: frozenset[str],
    allowed_pin_ids: frozenset[str],
    required_identity_classes: frozenset[str],
    fixture_backed: bool = False,
) -> PinGraphEvaluation:
    """Issue a milestone only for one closed artifact-backed inventory."""

    actual_ids = [row.pin_id for row in graph.pin_rows]
    actual_set = set(actual_ids)
    actual_node_ids = [node.pin_id for node in nodes]
    actual_node_set = set(actual_node_ids)
    errors = list(graph.errors)
    missing_nodes = sorted(required_node_ids - actual_node_set)
    unknown_nodes = sorted(actual_node_set - allowed_node_ids)
    duplicate_nodes = sorted({
        pin_id for pin_id in actual_node_ids if actual_node_ids.count(pin_id) > 1
    })
    missing = sorted(required_pin_ids - actual_set)
    unknown = sorted(actual_set - allowed_pin_ids)
    duplicate = sorted({pin_id for pin_id in actual_ids if actual_ids.count(pin_id) > 1})
    classes = {row.identity_class for row in graph.pin_rows}
    missing_classes = sorted(required_identity_classes - classes)
    if missing_nodes:
        errors.append(f"stage-node-inventory-missing:{stage}:{missing_nodes}")
    if unknown_nodes:
        errors.append(f"stage-node-inventory-unknown:{stage}:{unknown_nodes}")
    if duplicate_nodes:
        errors.append(f"stage-node-inventory-duplicate:{stage}:{duplicate_nodes}")
    if missing:
        errors.append(f"stage-inventory-missing:{stage}:{missing}")
    if unknown:
        errors.append(f"stage-inventory-unknown:{stage}:{unknown}")
    if duplicate:
        errors.append(f"stage-inventory-duplicate:{stage}:{duplicate}")
    if missing_classes:
        errors.append(f"stage-class-missing:{stage}:{missing_classes}")
    compatible = graph.compatible and not errors
    return PinGraphEvaluation(
        stage,
        compatible,
        _PIN_STAGE_VERDICTS[stage] if compatible else "INCOMPATIBLE",
        fixture_backed,
        False,
        graph.pin_rows,
        tuple(errors),
    )


def _canonical_relative_path(value: object) -> str | None:
    if not isinstance(value, str) or not value or "\\" in value or "\x00" in value:
        return None
    if value.startswith("/") or re.match(r"^[A-Za-z]:", value):
        return None
    parts = value.split("/")
    if any(part in {"", ".", ".."} for part in parts):
        return None
    if any(":" in part for part in parts):
        return None
    normalized = unicodedata.normalize("NFC", value)
    return normalized if normalized == value else None


def _path_has_reparse_component(root: Path, path: Path) -> bool:
    current = root
    try:
        relative_parts = path.relative_to(root).parts
    except ValueError:
        return True
    for part in relative_parts:
        current = current / part
        try:
            info = current.lstat()
        except FileNotFoundError:
            continue
        attributes = getattr(info, "st_file_attributes", 0)
        if stat.S_ISLNK(info.st_mode) or (
            attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
        ):
            return True
    return False


def _unresolved_path_kind(path: Path) -> tuple[bool, bool, bool]:
    """Return exists/expected ordinary directory-or-file/reparse without resolve."""

    try:
        metadata = path.lstat()
    except FileNotFoundError:
        return False, False, False
    attributes = getattr(metadata, "st_file_attributes", 0)
    reparse = stat.S_ISLNK(metadata.st_mode) or bool(
        attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    )
    return True, stat.S_ISDIR(metadata.st_mode) or stat.S_ISREG(metadata.st_mode), reparse


def _first_reparse_ancestor(path: Path) -> Path | None:
    """Inspect existing lexical ancestors without resolving/following them."""

    for ancestor in reversed(path.parents):
        exists, _ordinary, reparse = _unresolved_path_kind(ancestor)
        if exists and reparse:
            return ancestor
    return None


def _lexical_path_reparse(value: str | Path) -> tuple[Path, Path | None]:
    """Check a caller path's existing lexical components before resolution."""

    path = Path(value)
    if not path.is_absolute():
        path = Path.cwd() / path
    ancestor = _first_reparse_ancestor(path)
    if ancestor is not None:
        return path, ancestor
    exists, _ordinary, reparse = _unresolved_path_kind(path)
    return path, path if exists and reparse else None


def _safe_zip_file_infos(
    handle: zipfile.ZipFile,
) -> tuple[str | None, tuple[zipfile.ZipInfo, ...], tuple[str, ...]]:
    """Validate ZIP names/metadata completely before reading member bytes."""

    errors: list[str] = []
    files: list[zipfile.ZipInfo] = []
    aliases: set[str] = set()
    wrappers: set[str] = set()
    for info in handle.infolist():
        raw = info.filename
        if info.is_dir():
            errors.append(f"release-zip-unsafe-directory-entry:{raw}")
            continue
        candidate = raw[:-1] if info.is_dir() and raw.endswith("/") else raw
        canonical = _canonical_relative_path(candidate)
        if canonical is None:
            errors.append(f"release-zip-unsafe-name:{raw}")
            continue
        alias = unicodedata.normalize("NFC", canonical).casefold()
        if alias in aliases:
            errors.append(f"release-zip-unsafe-alias:{raw}")
            continue
        aliases.add(alias)
        unix_mode = (info.external_attr >> 16) & 0xFFFF
        dos_attributes = info.external_attr & 0xFFFF
        if stat.S_ISLNK(unix_mode) or (
            dos_attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
        ):
            errors.append(f"release-zip-unsafe-reparse:{raw}")
            continue
        parts = canonical.split("/")
        if len(parts) < 2:
            errors.append(f"release-zip-unsafe-rootless:{raw}")
            continue
        wrappers.add(parts[0])
        if not info.is_dir():
            files.append(info)
    if len(wrappers) != 1:
        errors.append(f"release-zip-unsafe-wrapper-count:{len(wrappers)}")
    wrapper = next(iter(wrappers)) if len(wrappers) == 1 else None
    return wrapper, tuple(files), tuple(errors)


def _preview275_consumer_evidence(
    *,
    release: dict[str, Any],
    archive: Path | None,
    declared: dict[str, str],
    capabilities: dict[str, Any],
    protocol_commands: list[Any],
) -> tuple[dict[str, object], dict[str, str]]:
    """Load the authenticated Task-5 consumer for the sealed legacy release.

    Preview.275 predates the declarations added to later release summaries.  Its
    repository-pinned Task-5 baseline is therefore the distinct consumer for
    those identities.  The baseline is consumed only after every retained
    product provenance/hash fact has been cross-bound to the current producer.
    """

    baseline = load_baseline("preview275")
    snapshot = baseline.snapshot
    provenance = _require_object(snapshot.get("provenance"), "preview275 provenance")
    expected_zip = _require_object(
        provenance.get("releaseZip"), "preview275 provenance/releaseZip"
    )
    errors: dict[str, str] = {}

    def require(label: str, actual: object, expected: object) -> None:
        if str(actual).casefold() != str(expected).casefold():
            errors[label] = (
                f"legacy-baseline-{label}-mismatch:producer={actual!r}:"
                f"consumer={expected!r}:baseline={baseline.manifest_path}"
            )

    require("version", release.get("version"), provenance.get("productVersion"))
    require("commit", release.get("sourceCommit"), provenance.get("sourceCommit"))
    require("tree", release.get("sourceTree"), provenance.get("sourceTree"))
    require("tag", release.get("sourceTag"), provenance.get("sourceTag"))
    require("line", capabilities.get("sourceLine"), provenance.get("sourceLine"))
    require(
        "capabilities-sha256", release.get("capabilitiesSha256"),
        provenance.get("capabilitiesSha256"),
    )
    require(
        "test-summary-sha256", release.get("testSummarySha256"),
        EXPECTED_PREVIEW_275_TEST_SUMMARY_SHA256,
    )
    if archive is None:
        errors["zip"] = "legacy-baseline-release-zip-missing"
    else:
        require("zip-length", archive.stat().st_size, expected_zip.get("length"))
        require("zip-sha256", _sha256_file(archive).upper(), expected_zip.get("sha256"))
    require(
        "launcher-sha256", declared.get("cli/actorwright.ps1", "<missing>"),
        EXPECTED_PREVIEW_275_LAUNCHER_SHA256,
    )

    baseline_commands = _require_array(snapshot.get("commands"), "preview275 commands")
    require("command-count", len(protocol_commands), snapshot.get("commandCount"))
    readiness = [
        {
            "name": _require_string(command.get("name"), "preview275 command/name"),
            "readiness": _require_string(
                command.get("protocol2Readiness"),
                "preview275 command/protocol2Readiness",
            ),
        }
        for command in (
            _require_object(value, "preview275 command") for value in baseline_commands
        )
    ]
    readiness_sha256 = _sha256(canonical_json_bytes(readiness)).upper()
    producer_readiness = [
        {"name": value.get("name"), "readiness": value.get("readiness")}
        for value in protocol_commands if isinstance(value, dict)
    ]
    require(
        "producer-readiness", _sha256(canonical_json_bytes(producer_readiness)).upper(),
        readiness_sha256,
    )

    if errors:
        raise FirewallError(";".join(errors.values()))
    evidence = str(baseline.manifest_path)
    return (
        {
            "protocolReadinessSha256": readiness_sha256,
        },
        {
            "protocolReadinessSha256": evidence + "#/commands",
        },
    )


def _verify_release_root_compatibility_impl(
    release_root: str | Path,
    release_zip: str | Path | None = None,
) -> PinGraphEvaluation:
    """Validate release bytes and source/evidence overlap without requiring a tag."""

    raw_root, root_reparse = _lexical_path_reparse(release_root)
    if root_reparse is not None:
        raise FirewallError(f"release-root-ancestor-reparse:{root_reparse}")
    raw_archive, archive_reparse = (
        _lexical_path_reparse(release_zip) if release_zip is not None else (None, None)
    )
    if archive_reparse is not None:
        raise FirewallError(f"release-zip-ancestor-reparse:{archive_reparse}")
    root = raw_root.resolve()
    archive = raw_archive.resolve() if raw_archive is not None else None
    structural_errors: list[str] = []
    if not root.is_dir() or root.is_symlink():
        structural_errors.append(f"release-root-missing:{root}")
    release_path = root / "actorwright-release.json"
    sums_path = root / "SHA256SUMS"
    if not release_path.is_file():
        structural_errors.append("release-manifest-missing:actorwright-release.json")
    if not sums_path.is_file():
        structural_errors.append("release-checksums-missing:SHA256SUMS")
    if structural_errors:
        return PinGraphEvaluation(
            "Full", False, "INCOMPATIBLE", False, False, (),
            tuple(structural_errors),
        )

    try:
        release = _require_object(_load_pin_json(release_path), "release manifest")
    except (OSError, json.JSONDecodeError, FirewallError) as exc:
        return PinGraphEvaluation(
            "Full", False, "INCOMPATIBLE", False, False, (),
            (f"release-manifest-invalid:{exc}",),
        )

    declared: dict[str, str] = {}
    for raw_line in sums_path.read_text(encoding="utf-8-sig").splitlines():
        if "  " not in raw_line:
            structural_errors.append("release-checksum-malformed")
            continue
        digest, relative = raw_line.split("  ", 1)
        relative = relative.replace("\\", "/")
        path = PureWindowsPath(relative)
        if (
            not relative or path.is_absolute() or ".." in path.parts
            or relative in declared or not re.fullmatch(r"[A-Fa-f0-9]{64}", digest)
        ):
            structural_errors.append(f"release-checksum-invalid:{relative}")
            continue
        declared[relative] = digest.upper()
    actual_members = {
        path.relative_to(root).as_posix(): path
        for path in root.rglob("*")
        if path.is_file() and path != sums_path
    }
    if set(declared) != set(actual_members):
        structural_errors.append(
            "release-inventory-mismatch:"
            f"undeclared={sorted(set(actual_members) - set(declared))}:"
            f"missing={sorted(set(declared) - set(actual_members))}"
        )

    nodes: list[PinNode] = []
    edges: list[PinEdge] = []
    actual_inventory = [
        {"path": relative, "sha256": _sha256_file(path).upper()}
        for relative, path in sorted(actual_members.items())
    ]
    declared_inventory = [
        {"path": relative, "sha256": digest}
        for relative, digest in sorted(declared.items())
    ]
    nodes.extend((
        PinNode(
            "release.inventory.actual", "Package",
            _sha256(canonical_json_bytes(actual_inventory)).upper(), str(root),
        ),
        PinNode(
            "release.inventory.declared", "Package",
            _sha256(canonical_json_bytes(declared_inventory)).upper(), str(sums_path),
        ),
    ))
    edges.append(PinEdge(
        "release-member-inventory", "Full", "release.inventory.actual",
        "release.inventory.declared", "release-member-inventory-sha256",
    ))

    evidence_fields = {
        "capabilitiesSha256": "evidence/capabilities.json",
        "sbomSha256": "evidence/sbom.spdx.json",
        "testSummarySha256": "evidence/test-summary.json",
        "dependencyVulnerabilityReportSha256": (
            "evidence/dependency-vulnerability-report.json"
        ),
        "canonicalBuildLogSha256": "evidence/canonical-build.log",
    }
    for field, relative in evidence_fields.items():
        if field not in release:
            structural_errors.append(f"release-evidence-field-missing:{field}")
            continue
        path = actual_members.get(relative)
        actual = _sha256_file(path).upper() if path is not None else "<missing>"
        nodes.extend((
            PinNode(
                f"evidence.actual:{field}", "Package", actual,
                str(path) if path is not None else f"{root}#missing:{relative}",
            ),
            PinNode(
                f"release.declared:{field}", "Package", str(release[field]).upper(),
                f"{release_path}#/{field}",
            ),
        ))
        edges.append(PinEdge(
            f"release-evidence:{field}", "Full",
            f"evidence.actual:{field}", f"release.declared:{field}",
            "evidence-sha256",
        ))

    def add_overlap(
        pin_id: str, producer_value: object, consumer_value: object,
        producer_path: str, consumer_path: str, identity_class: str,
    ) -> None:
        producer_id = f"overlap.producer:{pin_id}"
        consumer_id = f"overlap.consumer:{pin_id}"
        nodes.extend((
            PinNode(producer_id, "Source", str(producer_value), producer_path),
            PinNode(consumer_id, "Package", str(consumer_value), consumer_path),
        ))
        edges.append(PinEdge(
            f"source-overlap:{pin_id}", "Full", producer_id, consumer_id,
            identity_class,
        ))

    capabilities_path = root / "evidence" / "capabilities.json"
    summary_path = root / "evidence" / "test-summary.json"
    protocol_path = root / "evidence" / "protocol-v2-capabilities.json"
    schema_inventory_path = root / "evidence" / "protocol-v2-schema-exports.json"
    build_log_path = root / "evidence" / "canonical-build.log"
    sbom_path = root / "evidence" / "sbom.spdx.json"
    try:
        capabilities = _require_object(
            _load_pin_json(capabilities_path), "release capabilities"
        )
        summary = _require_object(_load_pin_json(summary_path), "release test summary")
        protocol = _require_object(
            _load_pin_json(protocol_path), "protocol-2 capabilities"
        )
        protocol_result = _require_object(
            protocol.get("result"), "protocol-2 capabilities result"
        )
        if not isinstance(protocol_result.get("commands"), list):
            raise FirewallError("protocol-2 capabilities commands must be an array")
        schema_inventory = _require_object(
            _load_pin_json(schema_inventory_path), "protocol-2 schema exports"
        )
        if not isinstance(schema_inventory.get("commands"), list):
            raise FirewallError("protocol-2 schema export commands must be an array")
        build_log = build_log_path.read_text(encoding="utf-8-sig", errors="strict")
        sbom = _require_object(_load_pin_json(sbom_path), "release SBOM")
    except (OSError, UnicodeError, json.JSONDecodeError, FirewallError) as exc:
        structural_errors.append(f"release-overlap-evidence-invalid:{exc}")
        capabilities = {}
        summary = {}
        protocol = {}
        protocol_result = {}
        schema_inventory = {}
        build_log = ""
        sbom = {}
    legacy_consumer: dict[str, object] | None = None
    legacy_evidence: dict[str, str] = {}
    is_preview275_legacy = (
        release.get("version") == "1.0.0-preview.275"
        and "orderedHelpSha256" not in summary
    )
    if is_preview275_legacy:
        try:
            legacy_consumer, legacy_evidence = _preview275_consumer_evidence(
                release=release,
                archive=archive,
                declared=declared,
                capabilities=capabilities,
                protocol_commands=_require_array(
                    protocol_result.get("commands"),
                    "protocol-2 capabilities commands",
                ),
            )
        except (OSError, UnicodeError, json.JSONDecodeError, FirewallError) as exc:
            structural_errors.append(f"legacy-consumer-invalid:{exc}")
            legacy_consumer = {}

    def consumer_value(field: str) -> object:
        if legacy_consumer is not None:
            return legacy_consumer.get(field, "<missing>")
        return summary.get(field, "<missing>")

    def consumer_path(field: str) -> str:
        if legacy_consumer is not None:
            return legacy_evidence.get(field, "<missing-legacy-consumer>")
        return f"{summary_path}#/{field}"
    for pin_id, field, source, identity_class in (
        ("releaseVersion", "version", capabilities, "release-version"),
        ("sourceCommit", "sourceCommit", summary, "source-sourceCommit"),
        ("sourceTag", "sourceTag", summary, "source-sourceTag"),
    ):
        if field not in release or field not in source:
            structural_errors.append(f"release-overlap-missing:{field}")
            continue
        evidence_path = capabilities_path if source is capabilities else summary_path
        add_overlap(
            pin_id, release[field], source[field], f"{release_path}#/{field}",
            f"{evidence_path}#/{field}", identity_class,
        )

    build_commit = re.search(r"(?m)^EVIDENCE_SOURCE_COMMIT=([A-Fa-f0-9]{40})$", build_log)
    build_tree = re.search(r"(?m)^EVIDENCE_SOURCE_TREE=([A-Fa-f0-9]{40})$", build_log)
    if "sourceCommit" in summary and build_commit:
        add_overlap(
            "buildSourceCommit", summary["sourceCommit"], build_commit.group(1),
            f"{summary_path}#/sourceCommit", f"{build_log_path}#EVIDENCE_SOURCE_COMMIT",
            "git-commit",
        )
    else:
        structural_errors.append("release-overlap-missing:buildSourceCommit")
    if "sourceTree" in release and build_tree:
        add_overlap(
            "sourceTree", release["sourceTree"], build_tree.group(1),
            f"{release_path}#/sourceTree",
            f"{build_log_path}#EVIDENCE_SOURCE_TREE", "source-tree",
        )
    else:
        structural_errors.append("release-overlap-missing:sourceTree")
    if "sourceLine" in capabilities and "sourceLine" in schema_inventory:
        add_overlap(
            "sourceLine", capabilities["sourceLine"], schema_inventory["sourceLine"],
            f"{capabilities_path}#/sourceLine",
            f"{schema_inventory_path}#/sourceLine", "source-line",
        )
    else:
        structural_errors.append("release-overlap-missing:sourceLine")
    if "version" in capabilities and "productVersion" in schema_inventory:
        add_overlap(
            "version", capabilities["version"], schema_inventory["productVersion"],
            f"{capabilities_path}#/version",
            f"{schema_inventory_path}#/productVersion", "source-version",
        )
    else:
        structural_errors.append("release-overlap-missing:sourceVersion")

    commands = capabilities.get("commands")
    protocol_commands = protocol_result.get("commands", [])
    if isinstance(commands, list) and isinstance(protocol_commands, list):
        add_overlap(
            "commandCount", len(commands), summary.get("exactCommandNames", "<missing>"),
            f"{capabilities_path}#/commands", f"{summary_path}#/exactCommandNames",
            "command-count",
        )
        command_names = [item.get("name") for item in commands if isinstance(item, dict)]
        protocol_names = [item.get("name") for item in protocol_commands if isinstance(item, dict)]
        if not is_preview275_legacy:
            ordered_help_digest = _sha256(canonical_json_bytes(protocol_commands)).upper()
            producer_help_path = f"{protocol_path}#/result/commands"
            add_overlap(
                "orderedHelpIdentity",
                ordered_help_digest, consumer_value("orderedHelpSha256"),
                producer_help_path, consumer_path("orderedHelpSha256"),
                "ordered-help-sha256",
            )
        readiness_inventory = [
            {"name": item.get("name"), "readiness": item.get("readiness")}
            for item in protocol_commands if isinstance(item, dict)
        ]
        add_overlap(
            "protocolReadinessIdentity",
            _sha256(canonical_json_bytes(readiness_inventory)).upper(),
            consumer_value("protocolReadinessSha256"),
            f"{protocol_path}#/result/commands",
            consumer_path("protocolReadinessSha256"),
            "protocol-readiness-sha256",
        )
    else:
        structural_errors.append("release-overlap-missing:commands")
    if schema_inventory_path.is_file():
        schema_digest = _sha256_file(schema_inventory_path).upper()
        add_overlap(
            "schemaInventory", schema_digest,
            declared.get("evidence/protocol-v2-schema-exports.json", "<missing>"),
            str(schema_inventory_path),
            f"{sums_path}#evidence/protocol-v2-schema-exports.json",
            "schema-inventory-sha256",
        )
    else:
        structural_errors.append("release-overlap-missing:schemaInventory")
    if build_log:
        selectors_producer = re.search(
            r"standalone test registry: PASS runnable=(\d+) fixture-bound=(\d+) unverified=(\d+)",
            build_log,
        )
        selector_results = [
            {"id": match.group(1), "status": match.group(2)}
            for match in re.finditer(
                r"(?m)^SELECTOR_RESULT id=([^\s]+) status=(PASS|FAIL)$", build_log
            )
        ]
        selector_ids = [item["id"] for item in selector_results]
        if not is_preview275_legacy and selectors_producer and selector_results:
            runnable, fixture_bound, unverified = map(int, selectors_producer.groups())
            if selector_results and (
                runnable != len(selector_results) or fixture_bound < 0 or unverified != 0
            ):
                structural_errors.append("selector-result-inventory-count-mismatch")
            selector_inventory_digest = _sha256(
                canonical_json_bytes(selector_ids)
            ).upper()
            selector_results_digest = _sha256(
                canonical_json_bytes(selector_results)
            ).upper()
            add_overlap(
                "selectorInventory",
                selector_inventory_digest,
                consumer_value("selectorInventorySha256"),
                f"{build_log_path}#SELECTOR_RESULT/id",
                consumer_path("selectorInventorySha256"),
                "selector-inventory-sha256",
            )
            add_overlap(
                "selectorResults",
                selector_results_digest,
                consumer_value("selectorResultsSha256"),
                f"{build_log_path}#SELECTOR_RESULT",
                consumer_path("selectorResultsSha256"),
                "selector-results-sha256",
            )
        elif not is_preview275_legacy:
            structural_errors.append("release-overlap-missing:selectorInventory")
        pytest_summary = re.search(
            r"(?m)^(\d+) passed, (\d+) skipped, \d+ warnings in ", build_log
        )
        if pytest_summary:
            add_overlap(
                "testPassCount", pytest_summary.group(1),
                summary.get("standalonePythonCases", "<missing>"),
                f"{build_log_path}#testPassCount",
                f"{summary_path}#/standalonePythonCases", "test-pass-count",
            )
            if not is_preview275_legacy:
                add_overlap(
                    "testSkipCount", pytest_summary.group(2),
                    summary.get("standalonePythonSkipped", "<missing>"),
                    f"{build_log_path}#testSkipCount",
                    f"{summary_path}#/standalonePythonSkipped", "test-skip-count",
                )
        else:
            structural_errors.append("release-overlap-missing:testPassCount")

    namespace = sbom.get("documentNamespace")
    namespace_match = re.fullmatch(
        r"https://actorwright\.invalid/spdx/([^/]+)/([A-Fa-f0-9]{40})",
        namespace if isinstance(namespace, str) else "",
    )
    if namespace_match and "version" in release and "sourceCommit" in release:
        add_overlap(
            "sbomVersion", release["version"], namespace_match.group(1),
            f"{release_path}#/version", f"{sbom_path}#/documentNamespace",
            "sbom-version",
        )
        add_overlap(
            "sbomCommit", release["sourceCommit"], namespace_match.group(2),
            f"{release_path}#/sourceCommit", f"{sbom_path}#/documentNamespace",
            "sbom-commit",
        )
        add_overlap(
            "sbomPackageIdentity", f"Actorwright-{release['version']}",
            sbom.get("name", "<missing>"), f"{release_path}#/version",
            f"{sbom_path}#/name", "sbom-package-identity",
        )
    else:
        structural_errors.append("release-overlap-missing:sbom-source-identity")

    if archive is not None:
        if not archive.is_file() or archive.is_symlink():
            structural_errors.append(f"release-zip-missing:{archive}")
        else:
            try:
                with zipfile.ZipFile(archive) as handle:
                    wrapper, infos, zip_errors = _safe_zip_file_infos(handle)
                    structural_errors.extend(zip_errors)
                    if wrapper is None or zip_errors:
                        pass
                    else:
                        archived = {}
                        for info in infos:
                            with handle.open(info) as member:
                                archived[info.filename[len(wrapper) + 1:]] = (
                                    _sha256_stream(member).upper()
                                )
                        root_with_sums = {
                            **{relative: _sha256_file(path).upper()
                               for relative, path in actual_members.items()},
                            "SHA256SUMS": _sha256_file(sums_path).upper(),
                        }
                        if set(archived) != set(root_with_sums):
                            structural_errors.append("release-zip-inventory-mismatch")
                        root_inventory = canonical_json_bytes([
                            {
                                "path": relative,
                                "length": (root / relative).stat().st_size,
                                "sha256": digest,
                            }
                            for relative, digest in sorted(root_with_sums.items())
                        ])
                        zip_lengths = {
                            info.filename[len(wrapper) + 1:]: info.file_size
                            for info in infos
                        }
                        zip_inventory = canonical_json_bytes([
                            {
                                "path": relative,
                                "length": zip_lengths[relative],
                                "sha256": digest,
                            }
                            for relative, digest in sorted(archived.items())
                        ])
                        add_overlap(
                            "releaseFileInventory", _sha256(root_inventory).upper(),
                            _sha256(zip_inventory).upper(), str(root), str(archive),
                            "release-file-inventory",
                        )
            except (OSError, zipfile.BadZipFile) as exc:
                structural_errors.append(f"release-zip-invalid:{exc}")

    if structural_errors:
        return PinGraphEvaluation(
            "Full", False, "INCOMPATIBLE", False, False, (),
            tuple(structural_errors),
        )
    graph = validate_pin_graph("Full", nodes, edges)
    if is_preview275_legacy:
        required_ids = (
            LEGACY_FULL_ZIP_PIN_IDS if archive is not None
            else LEGACY_FULL_ROOT_PIN_IDS
        )
        required_classes = (
            LEGACY_FULL_ZIP_PIN_CLASSES if archive is not None
            else LEGACY_FULL_ROOT_PIN_CLASSES
        )
        required_nodes = (
            LEGACY_FULL_ZIP_NODE_IDS if archive is not None
            else LEGACY_FULL_ROOT_NODE_IDS
        )
    else:
        required_ids = FULL_ZIP_PIN_IDS if archive is not None else FULL_ROOT_PIN_IDS
        required_classes = (
            FULL_ZIP_PIN_CLASSES if archive is not None else FULL_ROOT_PIN_CLASSES
        )
        required_nodes = FULL_ZIP_NODE_IDS if archive is not None else FULL_ROOT_NODE_IDS
    return _issue_milestone(
        "Full",
        graph,
        nodes=tuple(nodes),
        required_node_ids=required_nodes,
        allowed_node_ids=required_nodes,
        required_pin_ids=required_ids,
        allowed_pin_ids=required_ids,
        required_identity_classes=required_classes,
    )


def verify_release_root_compatibility(
    release_root: str | Path,
    release_zip: str | Path | None = None,
) -> PinGraphEvaluation:
    """Fail closed for every malformed or unreadable public release input."""

    try:
        return _verify_release_root_compatibility_impl(release_root, release_zip)
    except (
        OSError, UnicodeError, json.JSONDecodeError, FirewallError,
        AttributeError, TypeError, ValueError, KeyError,
    ) as exc:
        return PinGraphEvaluation(
            "Full", False, "INCOMPATIBLE", False, False, (),
            (f"release-compatibility-invalid:{type(exc).__name__}:{exc}",),
        )


def verify_release_source_compatibility(
    evidence_root: str | Path,
) -> PinGraphEvaluation:
    """Verify source evidence without requiring release/package artifacts."""

    try:
        supplied = Path(evidence_root).resolve()
        evidence = supplied / "evidence" if (supplied / "evidence").is_dir() else supplied
        paths = {
            "capabilities": evidence / "capabilities.json",
            "summary": evidence / "test-summary.json",
            "protocol": evidence / "protocol-v2-capabilities.json",
            "schemas": evidence / "protocol-v2-schema-exports.json",
            "build": evidence / "canonical-build.log",
        }
        capabilities = _require_object(_load_pin_json(paths["capabilities"]), "source capabilities")
        summary = _require_object(_load_pin_json(paths["summary"]), "source test summary")
        protocol = _require_object(_load_pin_json(paths["protocol"]), "source protocol capabilities")
        protocol_result = _require_object(protocol.get("result"), "source protocol result")
        protocol_commands = _require_array(protocol_result.get("commands"), "source protocol commands")
        schemas = _require_object(_load_pin_json(paths["schemas"]), "source schema inventory")
        _require_array(schemas.get("commands"), "source schema commands")
        build_log = paths["build"].read_text(encoding="utf-8-sig", errors="strict")
        commands = _require_array(capabilities.get("commands"), "source capabilities commands")

        nodes: list[PinNode] = []
        edges: list[PinEdge] = []

        def add(pin: str, producer: object, consumer: object, producer_path: str,
                consumer_path: str, identity: str) -> None:
            left, right = f"overlap.producer:{pin}", f"overlap.consumer:{pin}"
            nodes.extend((
                PinNode(left, "Source", str(producer), producer_path),
                PinNode(right, "Source", str(consumer), consumer_path),
            ))
            edges.append(PinEdge(f"source-overlap:{pin}", "Source", left, right, identity))

        add("version", capabilities.get("version", "<missing>"),
            schemas.get("productVersion", "<missing>"),
            f"{paths['capabilities']}#/version", f"{paths['schemas']}#/productVersion",
            "source-version")
        add("sourceLine", capabilities.get("sourceLine", "<missing>"),
            schemas.get("sourceLine", "<missing>"),
            f"{paths['capabilities']}#/sourceLine", f"{paths['schemas']}#/sourceLine",
            "source-line")
        commit = re.search(r"(?m)^EVIDENCE_SOURCE_COMMIT=([A-Fa-f0-9]{40})$", build_log)
        add("buildSourceCommit", summary.get("sourceCommit", "<missing>"),
            commit.group(1) if commit else "<missing>",
            f"{paths['summary']}#/sourceCommit", f"{paths['build']}#EVIDENCE_SOURCE_COMMIT",
            "git-commit")
        add("commandCount", len(commands), summary.get("exactCommandNames", "<missing>"),
            f"{paths['capabilities']}#/commands", f"{paths['summary']}#/exactCommandNames",
            "command-count")
        add("orderedHelpIdentity", _sha256(canonical_json_bytes(protocol_commands)).upper(),
            summary.get("orderedHelpSha256", "<missing>"),
            f"{paths['protocol']}#/result/commands", f"{paths['summary']}#/orderedHelpSha256",
            "ordered-help-sha256")
        readiness = [
            {"name": row.get("name"), "readiness": row.get("readiness")}
            for row in protocol_commands if isinstance(row, dict)
        ]
        add("protocolReadinessIdentity", _sha256(canonical_json_bytes(readiness)).upper(),
            summary.get("protocolReadinessSha256", "<missing>"),
            f"{paths['protocol']}#/result/commands",
            f"{paths['summary']}#/protocolReadinessSha256", "protocol-readiness-sha256")
        selector_results = [
            {"id": match.group(1), "status": match.group(2)}
            for match in re.finditer(r"(?m)^SELECTOR_RESULT id=([^\s]+) status=(PASS|FAIL)$", build_log)
        ]
        selector_ids = [row["id"] for row in selector_results]
        add("selectorInventory", _sha256(canonical_json_bytes(selector_ids)).upper(),
            summary.get("selectorInventorySha256", "<missing>"),
            f"{paths['build']}#SELECTOR_RESULT/id", f"{paths['summary']}#/selectorInventorySha256",
            "selector-inventory-sha256")
        add("selectorResults", _sha256(canonical_json_bytes(selector_results)).upper(),
            summary.get("selectorResultsSha256", "<missing>"),
            f"{paths['build']}#SELECTOR_RESULT", f"{paths['summary']}#/selectorResultsSha256",
            "selector-results-sha256")
        pytest_match = re.search(r"(?m)^(\d+) passed, (\d+) skipped, \d+ warnings in ", build_log)
        passed, skipped = pytest_match.groups() if pytest_match else ("<missing>", "<missing>")
        add("testPassCount", passed, summary.get("standalonePythonCases", "<missing>"),
            f"{paths['build']}#testPassCount", f"{paths['summary']}#/standalonePythonCases",
            "test-pass-count")
        add("testSkipCount", skipped, summary.get("standalonePythonSkipped", "<missing>"),
            f"{paths['build']}#testSkipCount", f"{paths['summary']}#/standalonePythonSkipped",
            "test-skip-count")
        graph = validate_pin_graph("Source", nodes, edges)
        return _issue_milestone(
            "Source", graph, nodes=tuple(nodes), required_node_ids=SOURCE_NODE_IDS,
            allowed_node_ids=SOURCE_NODE_IDS, required_pin_ids=SOURCE_PIN_IDS,
            allowed_pin_ids=SOURCE_PIN_IDS, required_identity_classes=SOURCE_PIN_CLASSES,
        )
    except (OSError, UnicodeError, json.JSONDecodeError, FirewallError,
            AttributeError, TypeError, ValueError, KeyError) as exc:
        return PinGraphEvaluation(
            "Source", False, "INCOMPATIBLE", False, False, (),
            (f"source-compatibility-invalid:{type(exc).__name__}:{exc}",),
        )


def verify_release_package_compatibility(
    release_root: str | Path,
    release_zip: str | Path | None = None,
) -> PinGraphEvaluation:
    """Authenticate package bytes/inventory without parsing source contracts."""

    try:
        raw_root, root_reparse = _lexical_path_reparse(release_root)
        if root_reparse is not None:
            raise FirewallError(f"release-root-ancestor-reparse:{root_reparse}")
        raw_archive, archive_reparse = (
            _lexical_path_reparse(release_zip) if release_zip is not None else (None, None)
        )
        if archive_reparse is not None:
            raise FirewallError(f"release-zip-ancestor-reparse:{archive_reparse}")
        root = raw_root.resolve()
        archive = raw_archive.resolve() if raw_archive is not None else None
        release_path, sums_path = root / "actorwright-release.json", root / "SHA256SUMS"
        release = _require_object(_load_pin_json(release_path), "package release manifest")
        declared: dict[str, str] = {}
        for line in sums_path.read_text(encoding="utf-8-sig").splitlines():
            digest, relative = line.split("  ", 1)
            relative = relative.replace("\\", "/")
            if (_canonical_relative_path(relative) is None or relative in declared
                    or not re.fullmatch(r"[A-Fa-f0-9]{64}", digest)):
                raise FirewallError(f"package checksum invalid: {relative}")
            declared[relative] = digest.upper()
        actual = {
            path.relative_to(root).as_posix(): path for path in root.rglob("*")
            if path.is_file() and path != sums_path
        }
        if set(actual) != set(declared):
            raise FirewallError("package inventory mismatch")
        nodes: list[PinNode] = []
        edges: list[PinEdge] = []

        def add(pin: str, producer: object, consumer: object, producer_path: str,
                consumer_path: str, identity: str) -> None:
            left, right = f"package.producer:{pin}", f"package.consumer:{pin}"
            nodes.extend((
                PinNode(left, "Package", str(producer), producer_path),
                PinNode(right, "Package", str(consumer), consumer_path),
            ))
            edges.append(PinEdge(pin, "Package", left, right, identity))

        actual_inventory = [
            {"path": relative, "sha256": _sha256_file(path).upper()}
            for relative, path in sorted(actual.items())
        ]
        declared_inventory = [
            {"path": relative, "sha256": digest} for relative, digest in sorted(declared.items())
        ]
        add("release-member-inventory", _sha256(canonical_json_bytes(actual_inventory)).upper(),
            _sha256(canonical_json_bytes(declared_inventory)).upper(), str(root), str(sums_path),
            "release-member-inventory-sha256")
        for field, relative in {
            "capabilitiesSha256": "evidence/capabilities.json",
            "sbomSha256": "evidence/sbom.spdx.json",
            "testSummarySha256": "evidence/test-summary.json",
            "dependencyVulnerabilityReportSha256": "evidence/dependency-vulnerability-report.json",
            "canonicalBuildLogSha256": "evidence/canonical-build.log",
        }.items():
            path = actual.get(relative)
            add(f"release-evidence:{field}",
                _sha256_file(path).upper() if path else "<missing>",
                str(release.get(field, "<missing>")).upper(), str(path),
                f"{release_path}#/{field}", "evidence-sha256")
        schema_relative = "evidence/protocol-v2-schema-exports.json"
        schema_path = actual.get(schema_relative)
        add("source-overlap:schemaInventory",
            _sha256_file(schema_path).upper() if schema_path else "<missing>",
            declared.get(schema_relative, "<missing>"), str(schema_path),
            f"{sums_path}#{schema_relative}", "schema-inventory-sha256")
        if archive is not None:
            with zipfile.ZipFile(archive) as handle:
                wrapper, infos, errors = _safe_zip_file_infos(handle)
                if errors or wrapper is None:
                    raise FirewallError(";".join(errors) or "package ZIP wrapper missing")
                archived = {}
                for info in infos:
                    relative = info.filename[len(wrapper) + 1:]
                    with handle.open(info) as member:
                        member_sha256 = _sha256_stream(member).upper()
                    archived[relative] = {
                        "path": relative,
                        "length": info.file_size,
                        "sha256": member_sha256,
                    }
            root_rows = {
                relative: {"path": relative, "length": path.stat().st_size,
                           "sha256": _sha256_file(path).upper()}
                for relative, path in actual.items()
            }
            root_rows["SHA256SUMS"] = {
                "path": "SHA256SUMS", "length": sums_path.stat().st_size,
                "sha256": _sha256_file(sums_path).upper(),
            }
            if set(archived) != set(root_rows):
                raise FirewallError("package ZIP inventory mismatch")
            add("source-overlap:releaseFileInventory",
                _sha256(canonical_json_bytes([root_rows[k] for k in sorted(root_rows)])).upper(),
                _sha256(canonical_json_bytes([archived[k] for k in sorted(archived)])).upper(),
                str(root), str(archive), "release-file-inventory")
        graph = validate_pin_graph("Package", nodes, edges)
        ids = PACKAGE_ZIP_PIN_IDS if archive is not None else PACKAGE_ROOT_PIN_IDS
        classes = PACKAGE_ZIP_PIN_CLASSES if archive is not None else PACKAGE_ROOT_PIN_CLASSES
        node_ids = (
            PACKAGE_STAGE_ZIP_NODE_IDS if archive is not None
            else PACKAGE_STAGE_ROOT_NODE_IDS
        )
        return _issue_milestone(
            "Package", graph, nodes=tuple(nodes), required_node_ids=node_ids,
            allowed_node_ids=node_ids, required_pin_ids=ids,
            allowed_pin_ids=ids, required_identity_classes=classes,
        )
    except (OSError, UnicodeError, json.JSONDecodeError, zipfile.BadZipFile,
            FirewallError, AttributeError, TypeError, ValueError, KeyError) as exc:
        return PinGraphEvaluation(
            "Package", False, "INCOMPATIBLE", False, False, (),
            (f"package-compatibility-invalid:{type(exc).__name__}:{exc}",),
        )


def _load_pin_json(path: Path) -> Any:
    """Read release/Exchange JSON while accepting the historical UTF-8 BOM."""

    def unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        result: dict[str, Any] = {}
        for key, value in pairs:
            if key in result:
                raise FirewallError(f"duplicate JSON key in {path}: {key}")
            result[key] = value
        return result

    try:
        return json.loads(
            path.read_text(encoding="utf-8-sig"),
            object_pairs_hook=unique,
            parse_constant=lambda value: (_ for _ in ()).throw(
                FirewallError(f"non-finite JSON value in {path}: {value}")
            ),
        )
    except FirewallError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise FirewallError(f"cannot parse JSON {path}: {exc}") from exc


def _verify_candidate_bundle_compatibility_impl(
    candidate_bundle: str | Path,
    release_root: str | Path,
    release_zip: str | Path,
    repository_root: str | Path,
    *,
    fixture_backed: bool = False,
) -> PinGraphEvaluation:
    """Close Exchange candidate payload hashes over sealed producer evidence."""

    bundle_root, bundle_reparse = _lexical_path_reparse(candidate_bundle)
    if bundle_reparse is not None:
        location = "root" if bundle_reparse == bundle_root else "root-ancestor"
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False, (),
            (f"candidate-{location}-reparse:{bundle_reparse}",),
        )
    exists, ordinary_kind, _root_reparse = _unresolved_path_kind(bundle_root)
    if not exists or not ordinary_kind or not bundle_root.is_dir():
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False, (),
            (f"candidate-root-invalid:{bundle_root}",),
        )
    inputs = (
        ("release-root", release_root),
        ("release-zip", release_zip),
        ("repository-root", repository_root),
    )
    checked: dict[str, Path] = {}
    for name, value in inputs:
        lexical, reparse = _lexical_path_reparse(value)
        if reparse is not None:
            return PinGraphEvaluation(
                "Candidate", False, "INCOMPATIBLE", fixture_backed, False, (),
                (f"candidate-{name}-ancestor-reparse:{reparse}",),
            )
        checked[name] = lexical
    root = checked["release-root"].resolve()
    archive = checked["release-zip"].resolve()
    repository_root = checked["repository-root"]
    manifest_path = bundle_root / "bundle-manifest.json"
    manifest_exists, manifest_ordinary, manifest_reparse = _unresolved_path_kind(
        manifest_path
    )
    if manifest_reparse:
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False, (),
            (f"candidate-manifest-reparse:{manifest_path}",),
        )
    if not manifest_exists or not manifest_ordinary or not manifest_path.is_file():
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False, (),
            (f"candidate-manifest-invalid-path:{manifest_path}",),
        )
    release_result = verify_release_root_compatibility(root, archive)
    if not release_result.compatible:
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False,
            release_result.pin_rows,
            tuple(f"release:{error}" for error in release_result.errors),
        )
    payload_path = bundle_root / "release-candidate.json"
    producer_release_path = root / "actorwright-release.json"
    candidate_release_path = bundle_root / "actorwright-release.json"
    try:
        manifest = _require_object(_load_pin_json(manifest_path), "candidate bundle manifest")
    except (OSError, json.JSONDecodeError, FirewallError) as exc:
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False,
            release_result.pin_rows, (f"candidate-document-invalid:{exc}",),
        )
    if (
        set(manifest) != {"$schema", "version", "kind", "payload", "files"}
        or manifest.get("$schema") != "urn:actorwright:exchange:v1:bundle-manifest"
        or type(manifest.get("version")) is not int
        or manifest.get("version") != 1
        or type(manifest.get("kind")) is not str
        or manifest.get("kind") != "release-candidate"
        or type(manifest.get("payload")) is not str
        or manifest.get("payload") != "release-candidate.json"
        or not isinstance(manifest.get("files"), list)
    ):
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False,
            release_result.pin_rows, ("candidate-manifest-shape",),
        )

    structural_errors: list[str] = []
    rows_by_path: dict[str, dict[str, Any]] = {}
    for raw_row in manifest["files"]:
        if not isinstance(raw_row, dict) or set(raw_row) != {"path", "size", "sha256"}:
            structural_errors.append("candidate-manifest-row-shape")
            continue
        relative = _canonical_relative_path(raw_row.get("path"))
        size = raw_row.get("size")
        digest = raw_row.get("sha256")
        if relative is None:
            structural_errors.append(f"candidate-manifest-path-unsafe:{raw_row.get('path')}")
            continue
        alias = unicodedata.normalize("NFC", relative).casefold()
        if alias in {unicodedata.normalize("NFC", item).casefold() for item in rows_by_path}:
            structural_errors.append(f"candidate-manifest-path-alias:{relative}")
            continue
        if not isinstance(size, int) or isinstance(size, bool) or size < 0:
            structural_errors.append(f"candidate-manifest-size-invalid:{relative}")
            continue
        if not isinstance(digest, str) or not re.fullmatch(r"[A-Fa-f0-9]{64}", digest):
            structural_errors.append(f"candidate-manifest-sha256-invalid:{relative}")
            continue
        candidate_path = bundle_root / relative
        if _path_has_reparse_component(bundle_root, candidate_path):
            structural_errors.append(f"candidate-manifest-path-reparse:{relative}")
            continue
        rows_by_path[relative] = raw_row

    required_members = {payload_path.name, archive.name, candidate_release_path.name}
    if set(rows_by_path) != required_members:
        structural_errors.append("candidate-manifest-required-inventory-mismatch")
    try:
        actual_entries = tuple(bundle_root.iterdir())
    except OSError as exc:
        structural_errors.append(f"candidate-bundle-inventory-invalid:{exc}")
        actual_entries = ()
    actual_members: set[str] = set()
    for path in actual_entries:
        if path == manifest_path:
            continue
        if path.is_symlink() or _path_has_reparse_component(bundle_root, path):
            structural_errors.append(f"candidate-member-reparse:{path.name}")
        elif not path.is_file():
            structural_errors.append(f"candidate-member-not-file:{path.name}")
        else:
            actual_members.add(path.name)
    if set(rows_by_path) != actual_members:
        structural_errors.append("candidate-manifest-inventory-mismatch")
    if structural_errors:
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False,
            release_result.pin_rows, tuple(structural_errors),
        )

    nodes: list[PinNode] = []
    edges: list[PinEdge] = []
    for relative, raw_row in rows_by_path.items():
        path = bundle_root / relative
        actual = _sha256_file(path).upper()
        declared = str(raw_row["sha256"]).upper()
        nodes.extend((
            PinNode(
                f"candidate.member.actual:{relative}", "Candidate", actual,
                str(path), byte_path=(path if path.is_file() else None),
                expected_length=raw_row["size"], expected_sha256=declared,
            ),
            PinNode(
                f"candidate.member.declared:{relative}", "Candidate", declared,
                f"{manifest_path}#/files/{relative}",
            ),
        ))
        edges.append(PinEdge(
            f"candidate-member:{relative}", "Candidate",
            f"candidate.member.actual:{relative}",
            f"candidate.member.declared:{relative}", "candidate-member-sha256",
        ))

    try:
        payload = _require_object(_load_pin_json(payload_path), "candidate payload")
        release = _require_object(
            _load_pin_json(candidate_release_path), "candidate release manifest"
        )
    except FirewallError as exc:
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False,
            release_result.pin_rows, (f"candidate-document-invalid:{exc}",),
        )

    def add_candidate_edge(
        pin_id: str, producer_value: object, consumer_value: object,
        producer_evidence: str, consumer_evidence: str,
        identity_class: str,
    ) -> None:
        producer_id = f"candidate.producer:{pin_id}"
        consumer_id = f"candidate.consumer:{pin_id}"
        nodes.extend((
            PinNode(producer_id, "Candidate", str(producer_value), producer_evidence),
            PinNode(consumer_id, "Candidate", str(consumer_value), consumer_evidence),
        ))
        edges.append(PinEdge(
            pin_id, "Candidate", producer_id, consumer_id, identity_class,
        ))

    schema_exists, schema_ordinary, schema_reparse = _unresolved_path_kind(
        CONSUMER_STAGE_REPORT_SCHEMA
    )
    if schema_reparse or not schema_exists or not schema_ordinary:
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False,
            release_result.pin_rows, ("consumer-stage-report-schema-invalid-path",),
        )
    add_candidate_edge(
        "consumer-stage-report-schema-sha256",
        _sha256_file(CONSUMER_STAGE_REPORT_SCHEMA).upper(),
        EXPECTED_CONSUMER_STAGE_REPORT_SCHEMA_SHA256,
        str(CONSUMER_STAGE_REPORT_SCHEMA),
        "contracts/compatibility/v1/consumer-stage-report.schema.json#approved-sha256",
        "contract-schema-sha256",
    )

    candidate_archive = bundle_root / archive.name
    candidate_archive_length, candidate_archive_sha256 = (
        _sha256_file_and_length(candidate_archive)
    )
    producer_archive_length, producer_archive_sha256 = (
        _sha256_file_and_length(archive)
    )
    candidate_release_length, candidate_release_sha256 = (
        _sha256_file_and_length(candidate_release_path)
    )
    producer_release_length, producer_release_sha256 = (
        _sha256_file_and_length(producer_release_path)
    )
    for pin_id, producer_value, consumer_value, producer_evidence, consumer_evidence, identity_class in (
        (
            "candidate-producer-package-length", producer_archive_length,
            candidate_archive_length, str(archive), str(candidate_archive), "package-length",
        ),
        (
            "candidate-producer-package-sha256", producer_archive_sha256.upper(),
            candidate_archive_sha256.upper(), str(archive), str(candidate_archive),
            "package-sha256",
        ),
        (
            "candidate-producer-release-length", producer_release_length,
            candidate_release_length, str(producer_release_path),
            str(candidate_release_path), "release-manifest-length",
        ),
        (
            "candidate-producer-release-sha256", producer_release_sha256.upper(),
            candidate_release_sha256.upper(), str(producer_release_path),
            str(candidate_release_path), "release-manifest-sha256",
        ),
    ):
        add_candidate_edge(
            pin_id, producer_value, consumer_value, producer_evidence,
            consumer_evidence, identity_class,
        )

    bindings = (
        (
            "candidate-package", candidate_archive_sha256.upper(),
            payload.get("packageSha256", ""), str(candidate_archive),
            f"{payload_path}#/packageSha256", "package-sha256",
        ),
        (
            "candidate-capabilities", release.get("capabilitiesSha256", ""),
            payload.get("capabilitiesSha256", ""),
            f"{candidate_release_path}#/capabilitiesSha256",
            f"{payload_path}#/capabilitiesSha256", "capabilities-sha256",
        ),
        (
            "candidate-sbom", release.get("sbomSha256", ""),
            payload.get("sbomSha256", ""), f"{candidate_release_path}#/sbomSha256",
            f"{payload_path}#/sbomSha256", "sbom-sha256",
        ),
        (
            "candidate-tests", release.get("testSummarySha256", ""),
            payload.get("testSummarySha256", ""),
            f"{candidate_release_path}#/testSummarySha256",
            f"{payload_path}#/testSummarySha256", "test-summary-sha256",
        ),
        (
            "candidate-source-commit", release.get("sourceCommit", ""),
            payload.get("sourceCommit", ""), f"{candidate_release_path}#/sourceCommit",
            f"{payload_path}#/sourceCommit", "git-commit",
        ),
        (
            "candidate-source-tag", release.get("sourceTag", ""),
            payload.get("sourceTag", ""), f"{candidate_release_path}#/sourceTag",
            f"{payload_path}#/sourceTag", "source-tag",
        ),
        (
            "candidate-version", release.get("version", ""),
            payload.get("productVersion", ""), f"{candidate_release_path}#/version",
            f"{payload_path}#/productVersion", "product-version",
        ),
    )
    for binding in bindings:
        add_candidate_edge(*binding)

    try:
        resolved_nodes = resolve_tag_pin_nodes(
            repository_root, str(payload.get("sourceTag", ""))
        )
        nodes.extend(resolved_nodes)
        resolved = {node.pin_id: node for node in resolved_nodes}
        add_candidate_edge(
            "tag-name", resolved["source.tag.name"].value,
            payload.get("sourceTag", ""), resolved["source.tag.name"].evidence_path,
            f"{payload_path}#/sourceTag", "git-tag-resolution",
        )
        add_candidate_edge(
            "tag-commit", resolved["source.tag.peeledCommit"].value,
            release.get("sourceCommit", ""),
            resolved["source.tag.peeledCommit"].evidence_path,
            f"{candidate_release_path}#/sourceCommit", "git-commit",
        )
        add_candidate_edge(
            "tag-tree", resolved["source.tag.peeledTree"].value,
            release.get("sourceTree", ""),
            resolved["source.tag.peeledTree"].evidence_path,
            f"{candidate_release_path}#/sourceTree", "git-tree",
        )
    except FirewallError as exc:
        structural_errors.append(f"unresolved-tag:{exc}")

    if structural_errors:
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False,
            release_result.pin_rows, tuple(structural_errors),
        )
    candidate_graph = validate_pin_graph(
        "Candidate", nodes, edges, fixture_backed=fixture_backed
    )
    required_candidate_ids = frozenset({
        *(f"candidate-member:{relative}" for relative in required_members),
        "candidate-producer-package-length",
        "candidate-producer-package-sha256",
        "candidate-producer-release-length",
        "candidate-producer-release-sha256",
        "candidate-package", "candidate-capabilities", "candidate-sbom",
        "candidate-tests", "candidate-source-commit", "candidate-source-tag",
        "candidate-version", "tag-name", "tag-commit", "tag-tree",
        "consumer-stage-report-schema-sha256",
    })
    candidate_result = _issue_milestone(
        "Candidate",
        candidate_graph,
        nodes=tuple(nodes),
        required_node_ids=frozenset({
            endpoint
            for edge in edges if edge.pin_id in required_candidate_ids
            for endpoint in (edge.producer, edge.consumer)
        }),
        allowed_node_ids=frozenset(node.pin_id for node in nodes),
        required_pin_ids=required_candidate_ids,
        allowed_pin_ids=required_candidate_ids,
        required_identity_classes=frozenset({
            "candidate-member-sha256", "package-length", "package-sha256",
            "release-manifest-length", "release-manifest-sha256",
            "capabilities-sha256", "sbom-sha256", "test-summary-sha256",
            "git-commit", "source-tag", "product-version", "git-tag-resolution",
            "git-tree", "contract-schema-sha256",
        }),
        fixture_backed=fixture_backed,
    )
    compatible = release_result.compatible and candidate_result.compatible
    return PinGraphEvaluation(
        "Candidate", compatible,
        "CANDIDATE_COMPATIBLE" if compatible else "INCOMPATIBLE",
        fixture_backed, False,
        release_result.pin_rows + candidate_result.pin_rows,
        release_result.errors + candidate_result.errors,
    )


def verify_candidate_bundle_compatibility(
    candidate_bundle: str | Path,
    release_root: str | Path,
    release_zip: str | Path,
    repository_root: str | Path,
    *,
    fixture_backed: bool = False,
) -> PinGraphEvaluation:
    """Fail closed for malformed paths, JSON, encodings, and candidate I/O."""

    try:
        return _verify_candidate_bundle_compatibility_impl(
            candidate_bundle, release_root, release_zip, repository_root,
            fixture_backed=fixture_backed,
        )
    except (
        OSError, UnicodeError, json.JSONDecodeError, FirewallError,
        AttributeError, TypeError, ValueError, KeyError,
    ) as exc:
        return PinGraphEvaluation(
            "Candidate", False, "INCOMPATIBLE", fixture_backed, False, (),
            (f"candidate-compatibility-invalid:{type(exc).__name__}:{exc}",),
        )


def required_gate_ids(tier: str) -> tuple[str, ...]:
    """Return the closed required-gate inventory for one verification tier."""

    try:
        return _TIER_DEFINITIONS[tier][0]
    except KeyError as exc:
        raise FirewallError(f"unknown compatibility tier: {tier}") from exc


def evaluate_tier(tier: str, gates: list[GateResult] | tuple[GateResult, ...]) -> TierEvaluation:
    """Pass a tier only when each required gate occurs once as explicit PASS."""

    required, success_verdict = _TIER_DEFINITIONS.get(tier, (None, None))
    if required is None or success_verdict is None:
        raise FirewallError(f"unknown compatibility tier: {tier}")
    observed: dict[str, list[GateResult]] = {gate_id: [] for gate_id in required}
    unknown = False
    for gate in gates:
        if gate.gate_id in observed:
            observed[gate.gate_id].append(gate)
        else:
            unknown = True
    compatible = all(
        len(results) == 1
        and results[0].required
        and results[0].status == "PASS"
        and bool(results[0].evidence)
        for results in observed.values()
    ) and not unknown
    return TierEvaluation(
        tier=tier,
        compatible=compatible,
        verdict=success_verdict if compatible else "INCOMPATIBLE",
        gates=tuple(gates),
    )


def prepare_capture_workspace(path: str | Path) -> Path:
    """Create one fresh repository-owned compatibility capture workspace."""

    candidate = Path(path)
    if not candidate.is_absolute():
        raise FirewallError("capture workspace must be absolute")
    resolved = candidate.resolve()
    approved_parent = CAPTURE_WORKSPACE_PARENT.resolve()
    if resolved.parent != approved_parent or not resolved.name.startswith(
        CAPTURE_WORKSPACE_PREFIX
    ):
        raise FirewallError(
            "capture workspace must be a direct compatibility-firewall-* child "
            "of the approved artifacts/test-work root"
        )
    if resolved.exists():
        raise FirewallError(f"capture workspace must be new: {resolved}")
    resolved.mkdir(parents=True)
    marker = {
        "schemaVersion": 1,
        "owner": "compatibility-firewall",
        "purpose": "cli-contract-capture",
    }
    (resolved / CAPTURE_OWNER_MARKER).write_bytes(canonical_json_bytes(marker))
    return resolved


def _require_owned_capture_workspace(path: Path) -> Path:
    resolved = path.resolve()
    approved_parent = CAPTURE_WORKSPACE_PARENT.resolve()
    if resolved.parent != approved_parent or not resolved.name.startswith(
        CAPTURE_WORKSPACE_PREFIX
    ):
        raise FirewallError(
            "CLI workspace root is not below the approved artifacts/test-work boundary"
        )
    marker_path = resolved / CAPTURE_OWNER_MARKER
    expected = canonical_json_bytes(
        {
            "schemaVersion": 1,
            "owner": "compatibility-firewall",
            "purpose": "cli-contract-capture",
        }
    )
    try:
        marker = marker_path.read_bytes()
    except OSError as exc:
        raise FirewallError("CLI workspace root is not an owned capture workspace") from exc
    if marker != expected:
        raise FirewallError("CLI workspace root has an invalid owned capture workspace marker")
    return resolved


def _object_without_duplicates(
    pairs: list[tuple[str, Any]],
) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise FirewallError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def _reject_json_numeric_constant(value: str) -> Any:
    raise FirewallError(f"invalid JSON numeric constant: {value}")


def parse_json_output(data: bytes, subject: str) -> Any:
    """Parse one strict JSON document emitted by the CLI."""

    try:
        text = data.decode("utf-8-sig")
        return json.loads(
            text,
            object_pairs_hook=_object_without_duplicates,
            parse_constant=_reject_json_numeric_constant,
        )
    except FirewallError:
        raise
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise FirewallError(f"{subject}: invalid JSON output: {exc}") from exc


def canonical_json_bytes(value: Any) -> bytes:
    """Serialize JSON deterministically as UTF-8 with one trailing newline."""

    try:
        text = json.dumps(
            value,
            ensure_ascii=False,
            allow_nan=False,
            sort_keys=True,
            separators=(",", ":"),
        )
    except (TypeError, ValueError) as exc:
        raise FirewallError(f"value is not canonical JSON: {exc}") from exc
    return (text + "\n").encode("utf-8")


def _validated_cli_target(target: CliTarget) -> tuple[Path, Path, list[str], Path]:
    entrypoint = Path(target.entrypoint)
    workspace_root = Path(target.workspace_root)
    dotnet_path = Path(target.dotnet_path)
    if not target.name:
        raise FirewallError("CLI target name must not be empty")
    if not entrypoint.is_absolute():
        raise FirewallError("CLI entrypoint must be absolute")
    if not entrypoint.is_file():
        raise FirewallError(f"CLI entrypoint missing: {entrypoint}")
    suffix = entrypoint.suffix.casefold()
    if suffix not in {".dll", ".exe"}:
        raise FirewallError("CLI entrypoint must be a .dll or .exe")
    if not workspace_root.is_absolute():
        raise FirewallError("CLI workspace root must be absolute")
    if workspace_root.drive.casefold() != "k:":
        raise FirewallError("CLI workspace root must be K-local")
    if not workspace_root.is_dir():
        raise FirewallError(f"CLI workspace root missing: {workspace_root}")
    workspace_root = _require_owned_capture_workspace(workspace_root)
    if target.expected_sha256 is not None:
        expected = target.expected_sha256
        if len(expected) != _SHA256_LENGTH or any(
            character not in "0123456789abcdefABCDEF" for character in expected
        ):
            raise FirewallError("CLI expected sha256 is malformed")
        actual = _sha256_file(entrypoint)
        if actual.casefold() != expected.casefold():
            raise FirewallError("CLI entrypoint sha256 mismatch")
    if not dotnet_path.is_absolute():
        raise FirewallError("pinned dotnet entrypoint must be absolute")
    _, reparse = _lexical_path_reparse(dotnet_path)
    if reparse is not None:
        raise FirewallError(f"pinned dotnet reparse path: {reparse}")
    if not dotnet_path.is_file():
        raise FirewallError(f"pinned dotnet missing: {dotnet_path}")
    dotnet_path = dotnet_path.resolve()
    if suffix == ".exe" and dotnet_path != PINNED_DOTNET.resolve():
        raise FirewallError(f"pinned dotnet path mismatch: {dotnet_path}")
    if suffix == ".dll":
        prefix = [str(dotnet_path), str(entrypoint)]
    else:
        prefix = [str(entrypoint)]
    return entrypoint, workspace_root, prefix, dotnet_path


def _run_bounded_process(
    arguments: list[str],
    *,
    cwd: str,
    env: dict[str, str],
    shell: bool,
    timeout: int,
    max_output_bytes: int,
) -> subprocess.CompletedProcess[bytes]:
    """Run a child with concurrent hard byte caps on stdout and stderr."""

    if shell:
        raise FirewallError("shell execution is forbidden")
    process = subprocess.Popen(
        arguments,
        cwd=cwd,
        env=env,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        shell=False,
    )
    streams = {"stdout": process.stdout, "stderr": process.stderr}
    buffers = {"stdout": bytearray(), "stderr": bytearray()}
    overflow: list[str] = []
    guard = threading.Lock()

    def drain(name: str) -> None:
        stream = streams[name]
        assert stream is not None
        try:
            while True:
                chunk = stream.read(65536)
                if not chunk:
                    return
                with guard:
                    remaining = max_output_bytes - len(buffers[name])
                    if remaining > 0:
                        buffers[name].extend(chunk[:remaining])
                    if len(chunk) > remaining:
                        if not overflow:
                            overflow.append(name)
                        try:
                            process.kill()
                        except OSError:
                            pass
                        return
        finally:
            stream.close()

    threads = [
        threading.Thread(target=drain, args=(name,), daemon=True)
        for name in ("stdout", "stderr")
    ]
    for thread in threads:
        thread.start()
    try:
        return_code = process.wait(timeout=timeout)
    except subprocess.TimeoutExpired as exc:
        process.kill()
        process.wait()
        for thread in threads:
            thread.join()
        raise FirewallError(
            f"CLI invocation timed out after {timeout}s: {' '.join(arguments)}"
        ) from exc
    for thread in threads:
        thread.join()
    if overflow:
        raise FirewallError(
            f"CLI {overflow[0]} exceeded {max_output_bytes}-byte bound"
        )
    return subprocess.CompletedProcess(
        arguments,
        return_code,
        bytes(buffers["stdout"]),
        bytes(buffers["stderr"]),
    )


def run_cli(
    target: CliTarget,
    arguments: list[str] | tuple[str, ...],
    *,
    timeout_seconds: int = CLI_TIMEOUT_SECONDS,
    max_output_bytes: int = CLI_MAX_OUTPUT_BYTES,
) -> CliObservation:
    """Invoke one exact CLI without a shell and parse its bounded JSON output."""

    if timeout_seconds <= 0:
        raise FirewallError("CLI timeout must be positive")
    if max_output_bytes <= 0:
        raise FirewallError("CLI output bound must be positive")
    _, workspace_root, prefix, dotnet_path = _validated_cli_target(target)
    if not arguments or any(not isinstance(item, str) or not item for item in arguments):
        raise FirewallError("CLI arguments must be non-empty strings")
    invocation = [*prefix, *arguments]
    environment = os.environ.copy()
    environment["ACTORWRIGHT_WORKSPACE_ROOT"] = str(workspace_root)
    environment["DOTNET_ROOT"] = str(dotnet_path.parent)
    environment["DOTNET_ROOT_X64"] = environment["DOTNET_ROOT"]
    environment["DOTNET_MULTILEVEL_LOOKUP"] = "0"
    try:
        completed = _run_bounded_process(
            invocation,
            cwd=str(workspace_root),
            env=environment,
            shell=False,
            timeout=timeout_seconds,
            max_output_bytes=max_output_bytes,
        )
    except FirewallError:
        raise
    except OSError as exc:
        raise FirewallError(f"CLI invocation failed: {exc}") from exc

    stdout = completed.stdout
    stderr = completed.stderr
    if not isinstance(stdout, bytes) or not isinstance(stderr, bytes):
        raise FirewallError("CLI runner must return byte stdout and stderr")
    json_stream = stdout if stdout.strip() else stderr
    value = parse_json_output(json_stream, f"CLI {' '.join(arguments)}")
    return CliObservation(
        arguments=tuple(invocation),
        exit_code=completed.returncode,
        stdout=stdout,
        stderr=stderr,
        json_value=value,
    )


def _explicit_file(path: str | Path, subject: str, suffix: str) -> Path:
    candidate = Path(path)
    if not candidate.is_absolute() or candidate.suffix.casefold() != suffix:
        raise FirewallError(f"{subject} must be an explicit {suffix} path")
    if not candidate.is_file():
        raise FirewallError(f"{subject} missing: {candidate}")
    return candidate.resolve()


def _selector_pass_lines(
    stdout: bytes,
    stderr: bytes,
    expected: tuple[str, ...],
) -> tuple[str, ...] | None:
    if stderr.strip():
        return None
    try:
        lines = tuple(line for line in stdout.decode("utf-8").splitlines() if line)
    except UnicodeError:
        return None
    if lines != expected:
        return None
    return lines


def _expected_sha256(value: str, subject: str) -> str:
    if len(value) != _SHA256_LENGTH or any(
        character not in "0123456789abcdefABCDEF" for character in value
    ):
        raise FirewallError(f"{subject} expected sha256 is malformed")
    return value.upper()


def _authenticate_selector_file(
    path: Path,
    expected_sha256: str,
    subject: str,
) -> tuple[int, str]:
    length, file_sha256 = _sha256_file_and_length(path)
    actual = file_sha256.upper()
    if actual != _expected_sha256(expected_sha256, subject):
        raise FirewallError(f"{subject} sha256 mismatch")
    return length, actual


def _authenticated_pinned_sdk() -> tuple[int, str]:
    """Authenticate the installed SDK against the closed tracked bootstrap pins."""

    manifest = _require_object(
        _load_json(REPOSITORY_ROOT / "tools" / "manifests" / PINNED_DOTNET_MANIFEST),
        "pinned SDK manifest",
    )
    if set(manifest) != {"archive_sha512", "source", "dotnet_length", "dotnet_sha256"}:
        raise FirewallError("pinned SDK manifest fields mismatch")
    if (
        not isinstance(manifest["archive_sha512"], str)
        or not re.fullmatch(r"[0-9a-fA-F]{128}", manifest["archive_sha512"])
        or manifest["source"] != (
            "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.301/"
            "dotnet-sdk-10.0.301-win-x64.zip"
        )
        or type(manifest["dotnet_length"]) is not int
        or manifest["dotnet_length"] <= 0
        or not isinstance(manifest["dotnet_sha256"], str)
    ):
        raise FirewallError("pinned SDK manifest pins are malformed")
    expected_sha256 = _expected_sha256(manifest["dotnet_sha256"], "pinned SDK manifest")
    sdk = _explicit_file(PINNED_DOTNET, "pinned dotnet", ".exe")
    length, sha256 = _authenticate_selector_file(sdk, expected_sha256, "pinned dotnet")
    if length != manifest["dotnet_length"]:
        raise FirewallError("pinned dotnet length mismatch")
    return length, sha256


def _build_source_selector_projects() -> tuple[str, str, str, tuple[str, ...]]:
    """Recreate both canonical selector DLLs with the authenticated SDK."""

    _sdk_length, sdk_sha256 = _authenticated_pinned_sdk()
    build_evidence: list[str] = []
    hashes: list[str] = []
    projects = tuple(
        (
            project_name,
            REPOSITORY_ROOT / "tests" / project_name / f"{project_name}.csproj",
            REPOSITORY_ROOT / "tests" / project_name / "bin" / "Release"
            / "net10.0" / f"{project_name}.dll",
        )
        for project_name in ("NpcManager.Cli.Tests", "NpcManager.Architecture.Tests")
    )
    # Preflight both lexical paths before deleting either generated output.
    for _project_name, _project, dll in projects:
        try:
            _path, reparse = _lexical_path_reparse(dll)
        except OSError as exc:
            raise FirewallError(f"cannot inspect canonical selector DLL path: {dll}: {exc}") from exc
        if reparse is not None:
            raise FirewallError(f"canonical selector DLL path contains reparse point: {reparse}")
    for project_name, project, dll in projects:
        if not project.is_file():
            raise FirewallError(f"canonical selector project missing: {project}")
        dll.unlink(missing_ok=True)
        completed = _run_bounded_process(
            [str(PINNED_DOTNET), "build", str(project), "--no-restore",
             "--configuration", "Release", "--no-incremental"],
            cwd=str(REPOSITORY_ROOT), env=os.environ.copy(), shell=False,
            timeout=600, max_output_bytes=128 * 1024,
        )
        output = (completed.stdout + b"\n" + completed.stderr).decode("utf-8", errors="replace")
        build_evidence.extend((
            f"buildProject={project_name}", f"buildExit={completed.returncode}",
            f"buildStdoutBase64={base64.b64encode(completed.stdout).decode('ascii')}",
            f"buildStderrBase64={base64.b64encode(completed.stderr).decode('ascii')}",
        ))
        summary = re.search(r"(?mi)^\s*0 Warning\(s\)\s*$.*?^\s*0 Error\(s\)\s*$", output, re.DOTALL)
        other_output = output[:summary.start()] + output[summary.end():] if summary else output
        if (completed.returncode != 0 or summary is None
                or re.search(r"(?i)\b(?:warning|error)\b", other_output)):
            raise FirewallError(f"canonical selector build failed or warned: {project_name}; "
                                + "; ".join(build_evidence))
        if not dll.is_file():
            raise FirewallError(f"canonical selector build produced no DLL: {dll}; "
                                + "; ".join(build_evidence))
        hashes.append(_sha256_file(dll).upper())
    return sdk_sha256, hashes[0], hashes[1], tuple(build_evidence)


def run_required_selectors(
    *,
    cli_project_dll: str | Path,
    architecture_project_dll: str | Path,
    dotnet_path: str | Path,
    working_directory: str | Path,
    expected_dotnet_sha256: str,
    expected_cli_project_sha256: str,
    expected_architecture_project_sha256: str,
    timeout_seconds: int = 300,
) -> tuple[GateResult, ...]:
    """Run the closed selector set through explicit DLL and SDK paths."""

    if timeout_seconds <= 0:
        raise FirewallError("selector timeout must be positive")
    if (
        Path(cli_project_dll).suffix.casefold() != ".dll"
        or Path(architecture_project_dll).suffix.casefold() != ".dll"
    ):
        raise FirewallError("selector runner requires explicit project DLL paths")
    cli_dll = _explicit_file(cli_project_dll, "CLI project", ".dll")
    architecture_dll = _explicit_file(
        architecture_project_dll, "architecture project", ".dll"
    )
    dotnet = _explicit_file(dotnet_path, "pinned dotnet", ".exe")
    cwd = Path(working_directory)
    if not cwd.is_absolute() or not cwd.is_dir():
        raise FirewallError("selector working directory must be an explicit directory")
    cwd = cwd.resolve()
    if dotnet != PINNED_DOTNET.resolve():
        raise FirewallError("pinned dotnet path mismatch")
    expected_cli_path = (
        cwd / "tests" / "NpcManager.Cli.Tests" / "bin" / "Release" /
        "net10.0" / "NpcManager.Cli.Tests.dll"
    ).resolve()
    expected_architecture_path = (
        cwd / "tests" / "NpcManager.Architecture.Tests" / "bin" / "Release" /
        "net10.0" / "NpcManager.Architecture.Tests.dll"
    ).resolve()
    if cli_dll != expected_cli_path or architecture_dll != expected_architecture_path:
        raise FirewallError("selector DLL is not the canonical project output")
    dotnet_length, dotnet_sha256 = _authenticate_selector_file(
        dotnet, expected_dotnet_sha256, "pinned dotnet"
    )
    cli_length, cli_sha256 = _authenticate_selector_file(
        cli_dll, expected_cli_project_sha256, "CLI project"
    )
    architecture_length, architecture_sha256 = _authenticate_selector_file(
        architecture_dll,
        expected_architecture_project_sha256,
        "architecture project",
    )
    version = _run_bounded_process(
        [str(dotnet), "--version"],
        cwd=str(cwd),
        env=os.environ.copy(),
        shell=False,
        timeout=timeout_seconds,
        max_output_bytes=1024,
    )
    if (
        version.returncode != 0
        or version.stderr.strip()
        or version.stdout.decode("utf-8", errors="replace").strip() != "10.0.301"
    ):
        raise FirewallError("pinned dotnet SDK version mismatch; expected 10.0.301")
    projects = {"cli": cli_dll, "architecture": architecture_dll}
    project_identities = {
        "cli": ("NpcManager.Cli.Tests", cli_length, cli_sha256),
        "architecture": (
            "NpcManager.Architecture.Tests",
            architecture_length,
            architecture_sha256,
        ),
    }
    sdk_evidence = (
        f"dotnetPath={dotnet}",
        f"dotnetLength={dotnet_length}",
        f"dotnetSha256={dotnet_sha256}",
        "sdkVersion=10.0.301",
    )
    results: list[GateResult] = []
    for gate_id, project, selector, expected_pass in _SELECTOR_SPECS:
        invocation = [str(dotnet), str(projects[project]), selector]
        assembly_name, project_length, project_sha256 = project_identities[project]
        identity_evidence = (
            *sdk_evidence,
            f"selector={selector}",
            f"projectPath={projects[project]}",
            f"assemblyName={assembly_name}",
            f"projectLength={project_length}",
            f"projectSha256={project_sha256}",
        )
        try:
            completed = _run_bounded_process(
                invocation,
                cwd=str(cwd),
                env=os.environ.copy(),
                shell=False,
                timeout=timeout_seconds,
                max_output_bytes=CLI_MAX_OUTPUT_BYTES,
            )
        except (FirewallError, OSError) as exc:
            results.append(
                GateResult(gate_id, True, "BLOCKED", (*identity_evidence, str(exc)), ())
            )
            continue
        pass_lines = _selector_pass_lines(
            completed.stdout, completed.stderr, expected_pass
        )
        if completed.returncode == 0 and pass_lines is not None:
            results.append(
                GateResult(
                    gate_id,
                    True,
                    "PASS",
                    (*identity_evidence, *pass_lines),
                    (),
                )
            )
        else:
            evidence = (
                *identity_evidence,
                f"exit={completed.returncode}",
                f"stdoutSha256={_sha256(completed.stdout).upper()}",
                f"stderrSha256={_sha256(completed.stderr).upper()}",
            )
            results.append(GateResult(gate_id, True, "FAIL", evidence, ()))
    return tuple(results)


def _validate_plugin_semantics(
    semantics: dict[str, Any],
    expected_source_masters: list[str],
    expected_final_masters: list[str],
) -> None:
    source = _require_object(semantics["source"], "journey/semantics/source")
    _require_closed(source, "journey/semantics/source", required={"plugin", "masters", "actorFormId", "beforeSha256", "afterSha256", "unchanged"})
    final = _require_object(semantics["final"], "journey/semantics/final")
    _require_closed(final, "journey/semantics/final", required={"plugin", "masters", "actorFormId", "sha256"})
    for location, value, expected_masters in (
        ("source", source, expected_source_masters),
        ("final", final, expected_final_masters),
    ):
        if value["plugin"] != "PackagedNpc.esp":
            raise FirewallError(f"journey/semantics/{location}: plugin semantics mismatch")
        if _require_string_array(value["masters"], f"journey/semantics/{location}/masters") != expected_masters:
            raise FirewallError(f"journey/semantics/{location}: master semantics mismatch")
        if value["actorFormId"] != "0x00000800":
            raise FirewallError(f"journey/semantics/{location}: form semantics mismatch")
    before = _require_hex(source["beforeSha256"], "journey/semantics/source/beforeSha256", _SHA256_LENGTH)
    after = _require_hex(source["afterSha256"], "journey/semantics/source/afterSha256", _SHA256_LENGTH)
    if source["unchanged"] is not True or before.casefold() != after.casefold():
        raise FirewallError("journey/semantics: source mutation detected")
    _require_hex(final["sha256"], "journey/semantics/final/sha256", _SHA256_LENGTH)


_JOURNEY_RELATION_DOMAINS = {
    "raw-file": {"sha256-raw-file"},
    "authority-json": {"sha256-canonical-json", "sha256-proposal-authority"},
    "tree": {"sha256-source-inventory-v1", "sha256-finish-source-tree-v1",
             "sha256-finish-output-tree-v2"},
}


def _journey_digest_specs(rows: dict[str, dict[str, Any]]) -> dict[str, list[tuple[str, str, str, str]]]:
    """Closed product-derived digest edges, keyed by exact artifact identity and pointer."""

    result: dict[str, list[tuple[str, str, str, str]]] = {}

    def add(target: str, pointer: str, kind: str, source: str, domain: str) -> None:
        if target in rows:
            result.setdefault(target, []).append((pointer, kind, source, domain))

    raw = "sha256-raw-file"
    canonical = "sha256-canonical-json"
    authority = "sha256-proposal-authority"
    source_tree = "sha256-source-inventory-v1"
    finish_source_tree = "sha256-finish-source-tree-v1"
    output_tree = "sha256-finish-output-tree-v2"
    source_manifest_names = (
        "evidence/create-before-finish-fixture/npcmanager-package.json",
        "planned-npc-output/npcmanager-package.json",
        "finished/NPCManager/finish-core-source-package-manifest.json",
        "finished/Data/NPCManager/finish-core-source-package-manifest.json",
    )
    dynamic_source_members = {
        "evidence/npc-creation-proposal.json",
        "Data/NPCManager/Evidence/racemenu-bundle.json",
        "Data/NPCManager/Evidence/record-authority.json",
        "Data/NPCManager/Evidence/runtime-routes.json",
        "Data/NPCManager/Evidence/standalone-assets.json",
    }
    for target in source_manifest_names:
        if target not in rows:
            continue
        projection = rows[target].get("projection")
        artifacts = projection.get("artifacts") if isinstance(projection, dict) else None
        if not isinstance(artifacts, list):
            raise FirewallError(f"journey: source manifest projection missing artifacts: {target}")
        matches = [(index, item["relativePath"]) for index, item in enumerate(artifacts)
                   if item["relativePath"] in dynamic_source_members]
        if {relative for _, relative in matches} != dynamic_source_members or len(matches) != 5:
            raise FirewallError(f"journey: source manifest digest identities changed: {target}")
        for index, relative in matches:
            add(target, f"/artifacts/{index}/sha256", "raw-file",
                "planned-npc-output/" + relative, raw)
        add(target, "/sourcePresetSha256", "raw-file",
            "planned-npc-output/evidence/npc-creation-proposal.json", raw)

    for target in ("finished/npcmanager-package.json",
                   "finished/Data/npcmanager-package.json"):
        if target not in rows:
            continue
        dynamic_finished_members = {
            "Data/NPCManager/Evidence/racemenu-bundle.json",
            "Data/NPCManager/Evidence/record-authority.json",
            "Data/NPCManager/Evidence/runtime-routes.json",
            "Data/NPCManager/Evidence/standalone-assets.json",
            "NPCManager/Evidence/finish-core-manifest.json",
            "NPCManager/Evidence/finish-core-proposal.json",
            "NPCManager/Evidence/finish-core-request.json",
            "NPCManager/Evidence/finish-core-verification.json",
            "NPCManager/finish-core-source-package-manifest.json",
            "evidence/npc-creation-proposal.json",
        }
        projection = rows[target].get("projection")
        artifacts = projection.get("artifacts") if isinstance(projection, dict) else None
        if not isinstance(artifacts, list):
            raise FirewallError(f"journey: finished manifest projection missing artifacts: {target}")
        matches = [(index, item["relativePath"]) for index, item in enumerate(artifacts)
                   if item["relativePath"] in dynamic_finished_members]
        if {relative for _, relative in matches} != dynamic_finished_members or len(matches) != 10:
            raise FirewallError("journey: finished manifest digest identities changed")
        for index, relative in matches:
            add(target, f"/artifacts/{index}/sha256", "raw-file",
                "finished/" + relative, raw)
        add(target, "/sourcePresetSha256", "raw-file",
            "finished/evidence/npc-creation-proposal.json", raw)

    for target, source in (
        ("planned-npc-output/Data/NPCManager/Evidence/racemenu-bundle.json",
         "planned-npc-output/Data/NPCManager/Evidence"),
        ("finished/Data/NPCManager/Evidence/racemenu-bundle.json",
         "finished/Data/NPCManager/Evidence"),
    ):
        add(target, "/recordAuthority/manifestSha256", "raw-file",
            source + "/record-authority.json", raw)
        add(target, "/runtimeRoutes/manifestSha256", "raw-file",
            source + "/runtime-routes.json", raw)

    for target, source in (
        ("evidence/finish-request.json", "planned-npc-output/npcmanager-package.json"),
        ("evidence/finish-request-canonical.json", "planned-npc-output/npcmanager-package.json"),
        ("finished/NPCManager/Evidence/finish-core-request.json",
         "planned-npc-output/npcmanager-package.json"),
    ):
        add(target, "/source/packageManifestSha256", "raw-file", source, raw)
        add(target, "/source/packageTreeSha256", "tree",
            "planned-npc-output", source_tree)

    finish_request_authority = (
        "evidence/finish-request-canonical.json"
        if "evidence/finish-request-canonical.json" in rows
        else "finished/NPCManager/Evidence/finish-core-request.json"
    )
    for target, request in (
        ("evidence/finish-proposal.json", "evidence/finish-request-canonical.json"),
        ("finished/NPCManager/Evidence/finish-core-proposal.json",
         finish_request_authority),
    ):
        add(target, "/request/source/packageManifestSha256", "raw-file",
            "planned-npc-output/npcmanager-package.json", raw)
        add(target, "/request/source/packageTreeSha256", "tree",
            "planned-npc-output", source_tree)
        add(target, "/requestSha256", "raw-file", request, raw)
        add(target, "/proposalSha256", "authority-json", target, authority)

    manifest = "finished/NPCManager/Evidence/finish-core-manifest.json"
    for index, source in enumerate((
        "finished/NPCManager/Evidence/finish-core-request.json",
        "finished/NPCManager/Evidence/finish-core-proposal.json",
    )):
        add(manifest, f"/evidence/files/{index}/sha256", "raw-file", source, raw)
    for pointer in ("/evidence/packageTreeSha256", "/packageTreeSha256"):
        add(manifest, pointer, "tree", "finished", output_tree)
    for pointer in ("/evidence/sourcePackageTreeSha256", "/sourcePackageTreeSha256"):
        add(manifest, pointer, "tree", "planned-npc-output", finish_source_tree)
    add(manifest, "/requestSha256", "raw-file",
        finish_request_authority, raw)
    add(manifest, "/proposalSha256", "authority-json",
        "finished/NPCManager/Evidence/finish-core-proposal.json", authority)

    verification = "finished/NPCManager/Evidence/finish-core-verification.json"
    add(verification, "/packageTreeSha256", "tree", "finished", output_tree)
    add(verification, "/sourcePackageTreeSha256", "tree", "planned-npc-output", finish_source_tree)

    add("evidence/placement-request.json", "/finishCore/manifestSha256", "raw-file", manifest, raw)
    add("evidence/placement-proposal.json", "/requestSha256", "authority-json",
        "evidence/placement-request.json", canonical)
    add("evidence/placement-proposal.json", "/proposalSha256", "authority-json",
        "evidence/placement-proposal.json", authority)
    placement = "placement/interior-placement.manifest.json"
    add(placement, "/archiveSha256", "raw-file", "placement.zip", raw)
    add(placement, "/finishCoreManifestSha256", "raw-file", manifest, raw)
    add(placement, "/requestSha256", "authority-json", "evidence/placement-request.json", canonical)
    add(placement, "/proposalSha256", "authority-json", "evidence/placement-proposal.json", authority)

    add("evidence/19-response.json", "/sha256", "raw-file", "evidence/initial-preflight.json", raw)
    add("evidence/24-response.json", "/sha256", "raw-file", "evidence/repaired-preflight.json", raw)
    add("evidence/25-response.json", "/manifestSha256", "raw-file",
        "evidence/create-before-finish-fixture/npcmanager-package.json", raw)
    add("evidence/27-response.json", "/proposalSha256", "authority-json",
        "evidence/finish-proposal.json", authority)
    add("evidence/29-response.json", "/verification/archiveSha256/value", "raw-file",
        "finished.zip", raw)
    add("evidence/29-response.json", "/verification/packageTreeSha256/value", "tree",
        "finished", output_tree)
    add("evidence/29-response.json", "/verification/sourcePackageTreeSha256/value", "tree",
        "planned-npc-output", finish_source_tree)
    add("evidence/30-response.json", "/proposalSha256", "authority-json",
        "evidence/placement-proposal.json", authority)
    return result


def _set_journey_pointer(document: Any, pointer: str, value: str) -> None:
    tokens = _json_pointer_tokens(pointer, "journey digest pointer")
    parent = document
    for token in tokens[:-1]:
        parent = parent[int(token)] if isinstance(parent, list) else parent[token]
    last = tokens[-1]
    if isinstance(parent, list):
        parent[int(last)] = value
    else:
        parent[last] = value


def _canonicalize_journey_digests(observation: dict[str, Any], capture_root: Path) -> None:
    """Verify product digests against raw files, then project their exact graph edges."""

    workspace = Path(observation["workspaceRoot"])
    if workspace.parent.resolve() != capture_root.resolve() or not workspace.is_dir():
        raise FirewallError("journey digest capture workspace is not the owned root")
    references: dict[str, list[dict[str, Any]]] = {}

    def collect(row: dict[str, Any], path: str) -> None:
        references.setdefault(path, []).append(row)
        if row["identityKind"] == "semantic-zip":
            prefix = path.removesuffix(".zip") + "/"
            for member in row["projection"]["members"]:
                collect(member, prefix + member["path"])

    for row in observation["artifacts"]:
        collect(row, row["path"])
    for row in observation["semantics"]["artifactInventory"]:
        collect(row, row["path"])
    rows: dict[str, dict[str, Any]] = {}
    for path, copies in references.items():
        first = copies[0]
        for duplicate in copies[1:]:
            if (duplicate["identityKind"] != first["identityKind"] or
                duplicate["length"] != first["length"] or
                duplicate.get("sha256", duplicate.get("rawSha256")) !=
                first.get("sha256", first.get("rawSha256")) or
                duplicate.get("projection") != first.get("projection")):
                raise FirewallError(f"journey raw duplicate projection differs: {path}")
        rows[path] = copy.deepcopy(first)

    raw_bytes: dict[str, bytes] = {}

    def archive_member_bytes(path: str) -> bytes:
        prefix, member = path.split("/", 1)
        archive_path = workspace / (prefix + ".zip")
        if prefix not in {"finished", "placement"} or not archive_path.is_file():
            raise FirewallError(f"journey archive member has no owned archive: {path}")
        with zipfile.ZipFile(archive_path) as archive:
            matches = [entry for entry in archive.infolist() if entry.filename == member]
            if len(matches) != 1 or matches[0].is_dir():
                raise FirewallError(f"journey archive member is absent or ambiguous: {path}")
            return archive.read(matches[0])

    def source_bytes(path: str) -> bytes:
        if path in raw_bytes:
            return raw_bytes[path]
        if path.startswith("/") or "\\" in path or any(
            part in {"", ".", ".."} for part in path.split("/")):
            raise FirewallError(f"journey digest source path is unsafe: {path}")
        candidate = workspace.joinpath(*path.split("/"))
        if candidate.is_symlink() or not candidate.resolve().is_relative_to(workspace.resolve()):
            raise FirewallError(f"journey digest source file is unsafe: {path}")
        data = candidate.read_bytes() if candidate.is_file() else archive_member_bytes(path)
        observed = rows.get(path)
        if observed is None:
            raise FirewallError(f"journey digest source is not observed: {path}")
        expected = observed.get("sha256", observed.get("rawSha256"))
        if observed["length"] != len(data) or _sha256(data).casefold() != expected.casefold():
            raise FirewallError(f"journey digest source raw identity differs: {path}")
        raw_bytes[path] = data
        return data

    def tree_members(root: str, domain: str) -> list[tuple[str, str]]:
        directory = workspace / root
        if directory.is_symlink() or not directory.is_dir():
            raise FirewallError(f"journey digest tree root is missing or unsafe: {root}")
        members: list[tuple[str, str]] = []
        for file in directory.rglob("*"):
            if file.is_symlink():
                raise FirewallError(f"journey digest tree has a reparse member: {file}")
            if not file.is_file():
                continue
            relative = file.relative_to(directory).as_posix()
            if domain == "sha256-finish-source-tree-v1" and (
                relative.casefold() == "data/packagednpc.esp"
            ):
                continue
            if domain == "sha256-finish-output-tree-v2" and (
                relative.casefold() == "data/packagednpc.esp" or
                relative.startswith("NPCManager/Evidence/") or
                relative.startswith("Data/NPCManager/Evidence/") or
                relative in {"README-Finish-Core.txt", "npcmanager-package.json"}
            ):
                continue
            members.append((root + "/" + relative, relative))
        members.sort(key=lambda item: item[1])
        if not members:
            raise FirewallError(f"journey digest tree has no members: {root}")
        for path, _ in members:
            source_bytes(path)
        return members

    def product_json_bytes(value: Any) -> bytes:
        return canonical_json_bytes(value)[:-1]

    for observed in observation["semantics"]["artifactInventory"]:
        source_bytes(observed["path"])
    for archive_name in ("finished.zip", "placement.zip"):
        archive = rows.get(archive_name)
        if archive is None:
            continue
        prefix = archive_name.removesuffix(".zip") + "/"
        for member in archive["projection"]["members"]:
            path = prefix + member["path"]
            data = archive_member_bytes(path)
            identity = rows[path]
            expected = identity.get("sha256", identity.get("rawSha256"))
            if len(data) != identity["length"] or _sha256(data).casefold() != expected.casefold():
                raise FirewallError(f"journey archive member raw identity differs: {path}")
            if path in raw_bytes and raw_bytes[path] != data:
                raise FirewallError(f"journey archive member differs from retained file: {path}")
            raw_bytes[path] = data

    specs = _journey_digest_specs(rows)
    done: set[str] = set()
    visiting: set[str] = set()

    def canonical_bytes(path: str) -> bytes:
        row = rows.get(path)
        if row is None:
            raise FirewallError(f"journey canonical source is unobserved: {path}")
        if row["identityKind"] == "raw":
            return source_bytes(path)
        transform(path)
        return canonical_json_bytes(row["projection"])

    def tree_digest(root: str, domain: str, *, canonical: bool) -> tuple[str, list[dict[str, Any]]]:
        members = tree_members(root, domain)
        closure = [{"path": path, "length": len(source_bytes(path)),
                    "rawSha256": _sha256(source_bytes(path)).upper()} for path, _ in members]
        payload = bytearray()
        if domain == "sha256-finish-output-tree-v2":
            payload.extend(b"npc.finish-core.output-tree.v2\0")
        for path, relative in members:
            data = canonical_bytes(path) if canonical else source_bytes(path)
            if domain == "sha256-source-inventory-v1":
                payload.extend(f"{relative}|{len(data)}|{_sha256(data).upper()}\n".encode("utf-8"))
            else:
                payload.extend(relative.encode("utf-8") + b"\0" + data + b"\0")
        return _sha256(bytes(payload)).upper(), closure

    def transform(path: str) -> None:
        if path in done:
            return
        if path in visiting:
            raise FirewallError(f"journey digest relationship cycle: {path}")
        row = rows[path]
        if row["identityKind"] == "raw":
            done.add(path)
            return
        visiting.add(path)
        if row["identityKind"] == "semantic-zip":
            prefix = path.removesuffix(".zip") + "/"
            for member in row["projection"]["members"]:
                member_path = prefix + member["path"]
                transform(member_path)
                member.update({key: copy.deepcopy(value) for key, value in rows[member_path].items()
                               if key in {"projection", "semanticSha256", "relationships"}})
        else:
            relations: list[dict[str, Any]] = []
            for pointer, kind, source, domain in specs.get(path, []):
                original = _value_at_pointer(row["projection"], pointer)
                if not isinstance(original, str) or re.fullmatch(r"[0-9a-fA-F]{64}", original) is None:
                    raise FirewallError(f"journey digest graph target is missing or malformed: {path}{pointer}")
                if kind == "tree":
                    computed, closure = tree_digest(source, domain, canonical=False)
                    canonical_digest, _ = tree_digest(source, domain, canonical=True)
                else:
                    source_data = source_bytes(source)
                    closure = [{"path": source, "length": len(source_data),
                                "rawSha256": _sha256(source_data).upper()}]
                    if kind == "raw-file":
                        computed = _sha256(source_data).upper()
                        canonical_digest = _sha256(canonical_bytes(source)).upper()
                    else:
                        parsed = json.loads(source_data)
                        if domain == "sha256-proposal-authority":
                            if not isinstance(parsed, dict):
                                raise FirewallError(f"journey authority source is not an object: {source}")
                            parsed.pop("proposalSha256", None)
                        computed = _sha256(product_json_bytes(parsed)).upper()
                        projected = row["projection"] if source == path else rows[source]["projection"]
                        if source != path:
                            transform(source)
                            projected = rows[source]["projection"]
                        projected = copy.deepcopy(projected)
                        if domain == "sha256-proposal-authority":
                            projected.pop("proposalSha256", None)
                        canonical_digest = _sha256(product_json_bytes(projected)).upper()
                if computed.casefold() != original.casefold():
                    raise FirewallError(f"journey product digest relation failed: {path}{pointer}")
                _set_journey_pointer(row["projection"], pointer, canonical_digest)
                relations.append({
                    "kind": kind, "sourcePath": source, "targetPointer": pointer,
                    "domain": domain, "expectedSha256": original,
                    "computedSha256": computed, "canonicalSha256": canonical_digest,
                    "closure": closure,
                })
            row["relationships"] = sorted(relations, key=lambda item: item["targetPointer"])
        row["semanticSha256"] = _sha256(canonical_json_bytes(row["projection"])).upper()
        visiting.remove(path)
        done.add(path)

    for path in sorted(rows):
        transform(path)
    nul_domains = {"sha256-finish-source-tree-v1", "sha256-finish-output-tree-v2"}
    raw_nul_paths = sorted({
        member["path"]
        for row in rows.values()
        for relation in row.get("relationships", [])
        if relation["domain"] in nul_domains
        for member in relation["closure"]
        if rows[member["path"]]["identityKind"] == "raw"
    })
    observation["semantics"]["nulRawMembers"] = [
        {"path": path, "length": len(source_bytes(path)),
         "rawSha256": _sha256(source_bytes(path)).upper(),
         "bytesBase64": base64.b64encode(source_bytes(path)).decode("ascii")}
        for path in raw_nul_paths
    ]
    for path, copies in references.items():
        for row in copies:
            canonical = rows[path]
            for key in ("projection", "semanticSha256", "relationships"):
                if key in canonical:
                    row[key] = copy.deepcopy(canonical[key])


def _validate_journey_relationships(artifact: dict[str, Any], location: str) -> None:
    relationships = _require_array(artifact["relationships"], location + "/relationships")
    seen: set[str] = set()
    for index, raw_relation in enumerate(relationships):
        relation_location = f"{location}/relationships/{index}"
        relation = _require_object(raw_relation, relation_location)
        _require_closed(relation, relation_location, required={
            "kind", "sourcePath", "targetPointer", "domain", "expectedSha256",
            "computedSha256", "canonicalSha256", "closure",
        })
        kind = _require_string(relation["kind"], relation_location + "/kind")
        domain = _require_string(relation["domain"], relation_location + "/domain")
        if domain not in _JOURNEY_RELATION_DOMAINS.get(kind, set()):
            raise FirewallError(relation_location + ": unknown relationship domain")
        source = _require_string(relation["sourcePath"], relation_location + "/sourcePath")
        if ("\\" in source or source.startswith("/") or
                any(part in {"", ".", ".."} for part in source.split("/"))):
            raise FirewallError(relation_location + ": unsafe relationship source path")
        pointer = _require_string(relation["targetPointer"], relation_location + "/targetPointer")
        _json_pointer_tokens(pointer, relation_location + "/targetPointer")
        if pointer in seen:
            raise FirewallError(relation_location + ": duplicate relationship target")
        seen.add(pointer)
        expected = _require_hex(relation["expectedSha256"], relation_location + "/expectedSha256", _SHA256_LENGTH)
        computed = _require_hex(relation["computedSha256"], relation_location + "/computedSha256", _SHA256_LENGTH)
        canonical = _require_hex(relation["canonicalSha256"], relation_location + "/canonicalSha256", _SHA256_LENGTH)
        if expected.casefold() != computed.casefold():
            raise FirewallError(relation_location + ": relationship digest mismatch")
        if _value_at_pointer(artifact["projection"], pointer) != canonical:
            raise FirewallError(relation_location + ": canonical relationship target mismatch")
        closure = _require_array(relation["closure"], relation_location + "/closure")
        if not closure:
            raise FirewallError(relation_location + ": relationship closure is empty")
        paths: list[str] = []
        for row_index, raw_row in enumerate(closure):
            row_location = f"{relation_location}/closure/{row_index}"
            row = _require_object(raw_row, row_location)
            _require_closed(row, row_location, required={"path", "length", "rawSha256"})
            path = _require_string(row["path"], row_location + "/path")
            if "\\" in path or path.startswith("/") or any(
                part in {"", ".", ".."} for part in path.split("/")):
                raise FirewallError(row_location + ": unsafe relationship closure path")
            paths.append(path)
            _require_integer(row["length"], row_location + "/length", minimum=1)
            _require_hex(row["rawSha256"], row_location + "/rawSha256", _SHA256_LENGTH)
        if paths != sorted(set(paths)) or (
            kind == "tree" and not all(path.startswith(source + "/") for path in paths)
        ) or (kind != "tree" and source not in paths):
            raise FirewallError(relation_location + ": relationship closure order or source mismatch")
        if kind == "raw-file" and (
            len(closure) != 1 or closure[0]["rawSha256"].casefold() != expected.casefold()
        ):
            raise FirewallError(relation_location + ": raw-file relationship mismatch")


def _validate_journey_shape(value: Any) -> dict[str, Any]:
    """Validate the closed observation shape and raw-identity consistency."""

    observation = _require_object(value, "journey")
    required = {
        "formatVersion", "id", "workspaceRoot", "outputRoot", "operationId",
        "generatedAtUtc", "captureTool", "steps", "artifacts", "semantics",
        "runtimeAuthority", "visualAuthority", "transientSourceIdentities",
    }
    _require_closed(observation, "journey", required=required)
    if observation["formatVersion"] != 1 or isinstance(observation["formatVersion"], bool):
        raise FirewallError("journey/formatVersion: unsupported format version")
    journey_id = _require_string(observation["id"], "journey/id")
    if journey_id not in {"journey.create-to-package", "journey.wave-b-v1"}:
        raise FirewallError("journey/id: unsupported journey")
    for key in ("workspaceRoot", "outputRoot", "operationId", "generatedAtUtc"):
        _require_string(observation[key], f"journey/{key}")
    workspace = PureWindowsPath(observation["workspaceRoot"])
    journey_output = PureWindowsPath(observation["outputRoot"])
    approved_parent = PureWindowsPath(str(CAPTURE_WORKSPACE_PARENT.resolve()))
    parent_parts = tuple(part.casefold() for part in approved_parent.parts)
    workspace_parts = tuple(part.casefold() for part in workspace.parts)
    if (
        not workspace.is_absolute()
        or workspace.drive.casefold() != "k:"
        or ".." in workspace.parts
        or workspace_parts[: len(parent_parts)] != parent_parts
        or len(workspace.parts) != len(approved_parent.parts) + 2
        or re.fullmatch(r"j-[0-9a-f]{32}", workspace.parts[-2]) is None
        or re.fullmatch(r"[cw]-[0-9a-f]{32}", workspace.name) is None
    ):
        raise FirewallError("journey/workspaceRoot: retained output outside owned root")
    if (
        not journey_output.is_absolute()
        or ".." in journey_output.parts
        or tuple(part.casefold() for part in journey_output.parts[: len(workspace.parts)])
        != workspace_parts
        or len(journey_output.parts) <= len(workspace.parts)
    ):
        raise FirewallError("journey/outputRoot: retained output outside owned root")
    if _OPERATION_ID.fullmatch(observation["operationId"]) is None:
        raise FirewallError("journey/operationId: invalid operation ID")
    if _UTC_TIMESTAMP.fullmatch(observation["generatedAtUtc"]) is None:
        raise FirewallError("journey/generatedAtUtc: invalid generated timestamp")
    capture_tool = _require_object(observation["captureTool"], "journey/captureTool")
    _require_closed(
        capture_tool,
        "journey/captureTool",
        required={"harnessCommit", "harnessTree", "harnessSourceClosure", "runner", "target"},
    )
    _require_hex(capture_tool["harnessCommit"], "journey/captureTool/harnessCommit", _GIT_OBJECT_LENGTH)
    _require_hex(capture_tool["harnessTree"], "journey/captureTool/harnessTree", _GIT_OBJECT_LENGTH)
    closure = _require_object(capture_tool["harnessSourceClosure"], "journey/captureTool/harnessSourceClosure")
    _require_closed(closure, "journey/captureTool/harnessSourceClosure", required={"root", "tree", "fileCount", "sha256"})
    if closure["root"] != "tests/NpcManager.Cli.Tests":
        raise FirewallError("journey/captureTool/harnessSourceClosure: source root mismatch")
    _require_hex(closure["tree"], "journey/captureTool/harnessSourceClosure/tree", _GIT_OBJECT_LENGTH)
    _require_integer(closure["fileCount"], "journey/captureTool/harnessSourceClosure/fileCount", minimum=1)
    _require_hex(closure["sha256"], "journey/captureTool/harnessSourceClosure/sha256", _SHA256_LENGTH)
    _validate_file_identity(capture_tool["runner"], "journey/captureTool/runner")
    _validate_file_identity(capture_tool["target"], "journey/captureTool/target")

    steps = _require_array(observation["steps"], "journey/steps")
    commands: list[str] = []
    for index, raw_step in enumerate(steps):
        location = f"journey/steps/{index}"
        step = _require_object(raw_step, location)
        _require_closed(
            step,
            location,
            required={"position", "command", "exitCode", "exitMeaning", "schemaIds"},
        )
        if _require_integer(step["position"], f"{location}/position") != index:
            raise FirewallError(f"{location}: position mismatch")
        command = _require_string(step["command"], f"{location}/command")
        commands.append(command)
        exit_code = _require_integer(step["exitCode"], f"{location}/exitCode")
        if exit_code > 5 or step["exitMeaning"] != _exit_meaning(exit_code):
            raise FirewallError(f"{location}: exit meaning mismatch")
        schema_ids = _require_string_array(step["schemaIds"], f"{location}/schemaIds")
        expected_schema_ids = sorted(EXPECTED_JOURNEY_SCHEMA_IDS.get(command, set()))
        if schema_ids != expected_schema_ids:
            raise FirewallError(f"{location}/schemaIds: exact schema identity mismatch")
    expected_commands = (
        CREATE_JOURNEY_COMMANDS
        if journey_id == "journey.create-to-package"
        else WAVE_B_JOURNEY_COMMANDS
    )
    if tuple(commands) != expected_commands:
        raise FirewallError("journey/steps: exact command sequence mismatch")
    if not commands:
        raise FirewallError("journey/steps: expected command sequence")

    artifacts = _require_array(observation["artifacts"], "journey/artifacts")
    artifact_paths: set[str] = set()
    for index, raw_artifact in enumerate(artifacts):
        location = f"journey/artifacts/{index}"
        artifact = _require_object(raw_artifact, location)
        common = {"path", "role", "identityKind", "length"}
        path = _require_string(artifact["path"], f"{location}/path")
        if path in artifact_paths:
            raise FirewallError(f"{location}/path: duplicate artifact")
        artifact_paths.add(path)
        role = _require_string(artifact["role"], f"{location}/role")
        _require_integer(artifact["length"], f"{location}/length", minimum=1)
        identity_kind = _require_string(artifact.get("identityKind"), f"{location}/identityKind")
        if identity_kind == "raw":
            _require_closed(artifact, location, required=common | {"sha256"})
            _require_hex(artifact["sha256"], f"{location}/sha256", _SHA256_LENGTH)
        elif identity_kind in {"semantic-json", "semantic-zip"}:
            _require_closed(artifact, location, required=common | {
                "rawSha256", "semanticSha256", "projection", "relationships",
            })
            _require_hex(artifact["rawSha256"], f"{location}/rawSha256", _SHA256_LENGTH)
            declared = _require_hex(artifact["semanticSha256"], f"{location}/semanticSha256", _SHA256_LENGTH)
            actual = _sha256(canonical_json_bytes(artifact["projection"]))
            if declared.casefold() != actual:
                raise FirewallError(f"{location}: semantic projection hash mismatch")
            _validate_journey_relationships(artifact, location)
        else:
            raise FirewallError(f"{location}/identityKind: unsupported identity kind")
    required_artifacts = (
        CREATE_JOURNEY_ARTIFACTS
        if journey_id == "journey.create-to-package"
        else WAVE_B_JOURNEY_ARTIFACTS
    )
    if artifact_paths != set(required_artifacts):
        raise FirewallError("journey/artifacts: exact required artifact set mismatch or required artifact missing")
    for artifact in artifacts:
        if artifact["role"] != required_artifacts[artifact["path"]]:
            raise FirewallError("journey/artifacts: artifact role mismatch")

    semantics = _require_object(observation["semantics"], "journey/semantics")
    semantics_required = {
        "readyForReviewedWrite", "applied", "verified", "source",
        "genericPackageVerification", "final",
        "packageInventoryExact", "artifactInventory", "nulRawMembers",
    }
    if journey_id == "journey.create-to-package":
        _require_closed(semantics, "journey/semantics", required=semantics_required)
        for key in (
            "readyForReviewedWrite", "applied", "verified",
            "packageInventoryExact",
        ):
            if semantics[key] is not True:
                raise FirewallError(f"journey/semantics: {key} is required")
        expected = ["Skyrim.esm", "ActorwrightBlankNpcProvider.esp"]
        _validate_plugin_semantics(semantics, expected, expected)
        package = _require_object(
            semantics["genericPackageVerification"],
            "journey/semantics/genericPackageVerification",
        )
        _require_closed(package, "journey/semantics/genericPackageVerification", required={"exitCode", "code", "verified"})
        if package != {"exitCode": 4, "code": "workflow-human-review-required", "verified": False}:
            raise FirewallError("journey/semantics: generic package verification mismatch")
    else:
        wave_required = {
            "topologyRefusal", "privateHdptRepair", "readyForReviewedWrite",
            "applied", "verified", "placementVerified", "packageVerification",
            "pluginAudit", "source", "final", "artifactInventory",
            "nulRawMembers",
        }
        _require_closed(semantics, "journey/semantics", required=wave_required)
        for key in (
            "topologyRefusal", "privateHdptRepair", "readyForReviewedWrite",
            "applied", "verified", "placementVerified", "pluginAudit",
        ):
            if semantics[key] is not True:
                raise FirewallError(f"journey/semantics: {key} is required")
        _validate_plugin_semantics(semantics, WAVE_B_SOURCE_MASTERS, WAVE_B_MASTERS)
        package = _require_object(semantics["packageVerification"], "journey/semantics/packageVerification")
        _require_closed(package, "journey/semantics/packageVerification", required={"code", "verified"})
        if package != {"code": "workflow-human-review-required", "verified": False}:
            raise FirewallError("journey/semantics: package verification mismatch")
    inventory = _require_array(semantics["artifactInventory"], "journey/semantics/artifactInventory")
    if not inventory:
        raise FirewallError("journey/semantics: artifact inventory missing")
    paths: list[str] = []
    for index, raw_row in enumerate(inventory):
        row = _require_object(raw_row, f"journey/semantics/artifactInventory/{index}")
        row_location = f"journey/semantics/artifactInventory/{index}"
        paths.append(_require_string(row["path"], row_location + "/path"))
        _require_integer(row["length"], row_location + "/length", minimum=1)
        kind = row.get("identityKind")
        if kind == "raw":
            _require_closed(row, row_location, required={"path", "role", "identityKind", "length", "sha256"})
            _require_hex(row["sha256"], row_location + "/sha256", _SHA256_LENGTH)
        elif kind in {"semantic-json", "semantic-zip"}:
            _require_closed(row, row_location, required={
                "path", "role", "identityKind", "length", "rawSha256",
                "semanticSha256", "projection", "relationships",
            })
            _require_hex(row["rawSha256"], row_location + "/rawSha256", _SHA256_LENGTH)
            declared = _require_hex(row["semanticSha256"], row_location + "/semanticSha256", _SHA256_LENGTH)
            if declared.casefold() != _sha256(canonical_json_bytes(row["projection"])):
                raise FirewallError(row_location + ": semantic projection hash mismatch")
            _validate_journey_relationships(row, row_location)
        else:
            raise FirewallError(row_location + ": unsupported identity kind")
    if paths != sorted(paths) or len(paths) != len(set(paths)):
        raise FirewallError("journey/semantics: artifact inventory must be ordered and unique")
    if observation["runtimeAuthority"] is not False:
        raise FirewallError("journey: runtime authority must remain false")
    if observation["visualAuthority"] is not False:
        raise FirewallError("journey: visual authority must remain false")

    # Every declared relationship names an observed raw source. Repeated copies
    # in the required artifacts, Wave B inventory, and sealed archive must bind
    # the same raw identity and canonical projection.
    identities: dict[str, dict[str, Any]] = {}
    semantic_rows: list[dict[str, Any]] = []

    def add_identity(row: dict[str, Any], path: str) -> None:
        if path not in artifact_paths and path not in paths:
            location = f"journey/archive-member/{path}"
            common = {"path", "identityKind", "length"}
            kind = _require_string(row.get("identityKind"), location + "/identityKind")
            _require_integer(row.get("length"), location + "/length", minimum=1)
            if kind == "raw":
                _require_closed(row, location, required=common | {"sha256"})
                _require_hex(row["sha256"], location + "/sha256", _SHA256_LENGTH)
            elif kind == "semantic-json":
                _require_closed(row, location, required=common | {
                    "rawSha256", "semanticSha256", "projection", "relationships",
                })
                _require_hex(row["rawSha256"], location + "/rawSha256", _SHA256_LENGTH)
                declared = _require_hex(row["semanticSha256"], location + "/semanticSha256", _SHA256_LENGTH)
                if declared.casefold() != _sha256(canonical_json_bytes(row["projection"])):
                    raise FirewallError(location + ": semantic projection hash mismatch")
                _validate_journey_relationships(row, location)
            else:
                raise FirewallError(location + ": unsupported archive-member identity")
        identity = {
            "length": row["length"],
            "rawSha256": row.get("rawSha256", row.get("sha256")),
            "projection": row.get("projection"),
            "identityKind": row["identityKind"],
            "relationships": row.get("relationships"),
        }
        previous = identities.setdefault(path, identity)
        if previous != identity:
            raise FirewallError(f"journey: duplicate projection or raw identity mismatch at {path}")
        if row["identityKind"] in {"semantic-json", "semantic-zip"}:
            semantic_rows.append(row)
        if row["identityKind"] == "semantic-zip":
            prefix = path.removesuffix(".zip") + "/"
            members = _require_array(row["projection"].get("members"),
                                     f"journey/{path}/projection/members")
            for member in members:
                member_path = prefix + member["path"]
                add_identity(member, member_path)

    for row in artifacts:
        add_identity(row, row["path"])
    for row in inventory:
        add_identity(row, row["path"])
    nul_domains = {"sha256-finish-source-tree-v1", "sha256-finish-output-tree-v2"}
    required_raw_nul_paths = {
        member["path"]
        for row in semantic_rows
        for relation in row["relationships"]
        if relation["domain"] in nul_domains
        for member in relation["closure"]
        if member["path"] in identities and identities[member["path"]]["identityKind"] == "raw"
    }
    retained_raw_nul: dict[str, bytes] = {}
    for index, raw_member in enumerate(_require_array(
        semantics["nulRawMembers"], "journey/semantics/nulRawMembers"
    )):
        location = f"journey/semantics/nulRawMembers/{index}"
        member = _require_object(raw_member, location)
        _require_closed(member, location, required={
            "path", "length", "rawSha256", "bytesBase64",
        })
        path = _require_string(member["path"], location + "/path")
        if path not in required_raw_nul_paths or path in retained_raw_nul:
            raise FirewallError(location + ": unknown or duplicate NUL raw member")
        encoded = _require_string(member["bytesBase64"], location + "/bytesBase64")
        try:
            data = base64.b64decode(encoded, validate=True)
        except (ValueError, base64.binascii.Error) as exc:
            raise FirewallError(location + ": malformed NUL raw member bytes") from exc
        identity = identities[path]
        length = _require_integer(member["length"], location + "/length", minimum=1)
        digest = _require_hex(member["rawSha256"], location + "/rawSha256", _SHA256_LENGTH)
        if (base64.b64encode(data).decode("ascii") != encoded or len(data) != length or
            length != identity["length"] or digest.casefold() != _sha256(data) or
            digest.casefold() != identity["rawSha256"].casefold()):
            raise FirewallError(location + ": NUL raw member identity mismatch")
        retained_raw_nul[path] = data
    if set(retained_raw_nul) != required_raw_nul_paths or list(retained_raw_nul) != sorted(retained_raw_nul):
        raise FirewallError("journey/semantics/nulRawMembers: missing or unordered NUL raw member")
    transient = _require_array(observation["transientSourceIdentities"],
                               "journey/transientSourceIdentities")
    if len(transient) != 2:
        raise FirewallError("journey/transientSourceIdentities: two selection identities required")
    transient_roles: set[str] = set()
    selection_parents: set[str] = set()
    bundle_path = "planned-npc-output/Data/NPCManager/Evidence/racemenu-bundle.json"
    bundle = identities.get(bundle_path)
    if bundle is None or not isinstance(bundle["projection"], dict):
        raise FirewallError("journey: selected RaceMenu bundle observation missing")
    for index, raw_entry in enumerate(transient):
        location = f"journey/transientSourceIdentities/{index}"
        entry = _require_object(raw_entry, location)
        _require_closed(entry, location, required={
            "role", "path", "length", "rawSha256", "captureStep",
        })
        role = _require_string(entry["role"], location + "/role")
        if role not in {"recordAuthority", "runtimeRoutes"} or role in transient_roles:
            raise FirewallError(location + ": duplicate or unknown transient role")
        transient_roles.add(role)
        filename = "record-authority.json" if role == "recordAuthority" else "runtime-routes.json"
        path = _require_string(entry["path"], location + "/path")
        match = re.fullmatch(
            r"companion/selection/racemenu-selection-[0-9a-f]{16}-[0-9a-f]{32}/" +
            re.escape(filename), path,
        )
        if match is None:
            raise FirewallError(location + ": transient source path mismatch")
        selection_parents.add(path.rsplit("/", 1)[0])
        length = _require_integer(entry["length"], location + "/length", minimum=1)
        digest = _require_hex(entry["rawSha256"], location + "/rawSha256", _SHA256_LENGTH)
        if entry["captureStep"] != "npc create-from-jslot:selection-before-promotion":
            raise FirewallError(location + ": transient capture step mismatch")
        copy_path = "planned-npc-output/Data/NPCManager/Evidence/" + filename
        copied = identities.get(copy_path)
        if copied is None or copied["length"] != length or copied["rawSha256"].casefold() != digest.casefold():
            raise FirewallError(location + ": retained selection copy diverged")
        binding = bundle["projection"].get(role)
        normalized_path = re.sub(r"-[0-9a-f]{32}/", "-<token>/", path)
        relation = next((item for item in bundle["relationships"]
                         if item["targetPointer"] == "/" + role + "/manifestSha256"), None)
        if not isinstance(binding, dict) or (
            binding.get("manifestPath") != normalized_path or
            relation is None or relation["sourcePath"] != copy_path or
            relation["expectedSha256"].casefold() != digest.casefold() or
            binding.get("manifestSha256") != relation["canonicalSha256"]
        ):
            raise FirewallError(location + ": RaceMenu bundle transient binding mismatch")
    if len(selection_parents) != 1:
        raise FirewallError("journey: transient selection identities span different transactions")
    for row in semantic_rows:
        for relation in row["relationships"]:
            source = relation["sourcePath"]
            observed = identities.get(source) if relation["kind"] != "tree" else None
            if relation["kind"] != "tree" and observed is None:
                raise FirewallError(f"journey: relationship source missing: {source}")
            for closure_source in relation["closure"]:
                closure_path = closure_source["path"]
                closure_identity = identities.get(closure_path)
                if closure_identity is None or (
                    closure_identity["length"] != closure_source["length"] or
                    closure_identity["rawSha256"].casefold() !=
                    closure_source["rawSha256"].casefold()
                ):
                    raise FirewallError(
                        f"journey: relationship closure identity mismatch: {closure_path}"
                    )
            projection = observed["projection"] if observed is not None else None
            domain = relation["domain"]
            if domain == "sha256-raw-file":
                canonical_digest = (
                    observed["rawSha256"] if projection is None
                    else _sha256(canonical_json_bytes(projection))
                )
            elif domain in {"sha256-canonical-json", "sha256-proposal-authority"}:
                if not isinstance(projection, dict):
                    raise FirewallError(f"journey: authority source is not JSON: {source}")
                authority = dict(projection)
                if domain == "sha256-proposal-authority":
                    authority.pop("proposalSha256", None)
                canonical_digest = _sha256(canonical_json_bytes(authority)[:-1])
            elif domain == "sha256-source-inventory-v1":
                raw_lines: list[str] = []
                canonical_lines: list[str] = []
                for item in relation["closure"]:
                    member_path = item["path"]
                    relative = member_path[len(source) + 1:]
                    raw_lines.append(
                        f'{relative}|{item["length"]}|{item["rawSha256"].upper()}\n'
                    )
                    member = identities[member_path]
                    if member["projection"] is None:
                        canonical_length = item["length"]
                        canonical_hash = item["rawSha256"].upper()
                    else:
                        canonical_member = canonical_json_bytes(member["projection"])
                        canonical_length = len(canonical_member)
                        canonical_hash = _sha256(canonical_member).upper()
                    canonical_lines.append(
                        f"{relative}|{canonical_length}|{canonical_hash}\n"
                    )
                raw_digest = _sha256("".join(raw_lines).encode("utf-8"))
                if raw_digest.casefold() != relation["expectedSha256"].casefold():
                    raise FirewallError(f"journey: raw source tree digest mismatch: {source}")
                canonical_digest = _sha256("".join(canonical_lines).encode("utf-8"))
            else:
                if domain not in nul_domains:
                    raise FirewallError(f"journey: unknown NUL tree domain: {domain}")
                eligible = []
                for path in paths:
                    if not path.startswith(source + "/"):
                        continue
                    relative = path[len(source) + 1:]
                    if relative.casefold() == "data/packagednpc.esp":
                        continue
                    if domain == "sha256-finish-output-tree-v2" and (
                        relative.startswith("NPCManager/Evidence/") or
                        relative.startswith("Data/NPCManager/Evidence/") or
                        relative in {"README-Finish-Core.txt", "npcmanager-package.json"}
                    ):
                        continue
                    eligible.append(path)
                if [item["path"] for item in relation["closure"]] != sorted(eligible):
                    raise FirewallError(f"journey: NUL tree closure mismatch: {source}")
                payload = bytearray(
                    b"npc.finish-core.output-tree.v2\0"
                    if domain == "sha256-finish-output-tree-v2" else b""
                )
                for item in relation["closure"]:
                    path = item["path"]
                    identity = identities[path]
                    data = (retained_raw_nul[path] if identity["identityKind"] == "raw"
                            else canonical_json_bytes(identity["projection"]))
                    payload.extend(path[len(source) + 1:].encode("utf-8") + b"\0" + data + b"\0")
                canonical_digest = _sha256(bytes(payload))
            if canonical_digest.casefold() != relation["canonicalSha256"].casefold():
                raise FirewallError(f"journey: canonical relationship digest mismatch: {source}")
    return observation


def validate_journey_observation(value: Any) -> dict[str, Any]:
    """Validate a complete capture, including every exact digest relationship."""

    observation = _validate_journey_shape(value)
    rows: dict[str, dict[str, Any]] = {}

    def collect(row: dict[str, Any], path: str) -> None:
        rows.setdefault(path, row)
        if row["identityKind"] == "semantic-zip":
            prefix = path.removesuffix(".zip") + "/"
            for member in row["projection"]["members"]:
                collect(member, prefix + member["path"])

    for row in observation["artifacts"]:
        collect(row, row["path"])
    for row in observation["semantics"]["artifactInventory"]:
        collect(row, row["path"])
    _validate_declared_digest_graph(rows)
    return observation


def _validate_declared_digest_graph(rows: dict[str, dict[str, Any]]) -> None:
    """Refuse missing, extra, reordered, or masked declared digest edges."""

    specs = _journey_digest_specs(rows)
    for path, row in rows.items():
        if row["identityKind"] not in {"semantic-json", "semantic-zip"}:
            continue
        if "<dynamic-sha256>" in json.dumps(row["projection"]):
            raise FirewallError(f"journey: masked digest remains in {path}")
        expected = sorted(specs.get(path, []), key=lambda item: item[0])
        actual = [(item["targetPointer"], item["kind"], item["sourcePath"], item["domain"])
                  for item in row["relationships"]]
        if actual != expected:
            raise FirewallError(f"journey: missing, unknown, or unordered digest relationship: {path}")
        for pointer, _, _, _ in expected:
            value_at_pointer = _value_at_pointer(row["projection"], pointer)
            if not isinstance(value_at_pointer, str) or re.fullmatch(
                r"[0-9a-fA-F]{64}", value_at_pointer) is None:
                raise FirewallError(f"journey: declared digest pointer missing: {path}{pointer}")


def journey_policy_projection(value: Any) -> dict[str, Any]:
    """Compare canonical behavior only after all raw relationship evidence passes."""

    observation = validate_journey_observation(value)
    return _journey_policy_projection_from_validated(observation)


def _journey_policy_projection_from_validated(observation: dict[str, Any]) -> dict[str, Any]:
    def artifact(row: dict[str, Any]) -> dict[str, Any]:
        common = {"path": row["path"], "identityKind": row["identityKind"]}
        if "role" in row:
            common["role"] = row["role"]
        if row["identityKind"] == "raw":
            return {**common, "length": row["length"], "sha256": row["sha256"]}
        projection = copy.deepcopy(row["projection"])
        if row["identityKind"] == "semantic-zip":
            projection["members"] = [artifact(member) for member in projection["members"]]
        return {
            **common,
            "semanticSha256": row["semanticSha256"],
            "projection": projection,
            "relationships": [{
                "kind": relation["kind"],
                "sourcePath": relation["sourcePath"],
                "targetPointer": relation["targetPointer"],
                "domain": relation["domain"],
                "canonicalSha256": relation["canonicalSha256"],
                "closurePaths": [item["path"] for item in relation["closure"]],
            } for relation in row["relationships"]],
        }

    semantics = copy.deepcopy(observation["semantics"])
    semantics["artifactInventory"] = [artifact(row) for row in semantics["artifactInventory"]]
    semantics.pop("nulRawMembers")
    return {
        "id": observation["id"],
        "formatVersion": observation["formatVersion"],
        "captureTool": observation["captureTool"],
        "steps": observation["steps"],
        "artifacts": [artifact(row) for row in observation["artifacts"]],
        "semantics": semantics,
        "transientSourceIdentities": [
            {"role": row["role"],
             "path": re.sub(r"-[0-9a-f]{32}/", "-<token>/", row["path"]),
             "captureStep": row["captureStep"]}
            for row in observation["transientSourceIdentities"]
        ],
        "runtimeAuthority": observation["runtimeAuthority"],
        "visualAuthority": observation["visualAuthority"],
    }


def _release_journey_policy_projection(
    value: dict[str, Any], *, product_version: str, source_line: str,
    executable_sha256: str | None,
) -> dict[str, Any]:
    """Compare validated behavior, not redundant or release-specific digests."""

    projected = copy.deepcopy(value)
    projected.pop("captureTool", None)

    def artifact(row: dict[str, Any]) -> None:
        if row["identityKind"] != "raw":
            # The full semantic projection remains below this digest.
            row.pop("semanticSha256", None)
        for relation in row.get("relationships", []):
            # validate_journey_observation already authenticated the graph.
            relation.pop("canonicalSha256", None)
        if row["identityKind"] == "semantic-zip":
            for member in row["projection"]["members"]:
                artifact(member)

    for row in projected["artifacts"]:
        artifact(row)
    for row in projected["semantics"]["artifactInventory"]:
        artifact(row)

    def release_identity(node: Any) -> None:
        if isinstance(node, dict):
            for key, item in node.items():
                if key == "productVersion" and item == product_version:
                    node[key] = "<release-productVersion>"
                elif key == "sourceLine" and item == source_line:
                    node[key] = "<release-sourceLine>"
                else:
                    release_identity(item)
        elif isinstance(node, list):
            for item in node:
                release_identity(item)

    release_identity(projected)
    if projected["id"] != "journey.wave-b-v1":
        return projected

    rows = {row["path"]: row for row in projected["semantics"]["artifactInventory"]}
    media_pipe_path = re.compile(
        r"MediaPipe runtime admitted at [A-Za-z]:\\(?:[^\\]+\\)*Temp\\\.net\\"
        r"actorwright\\[A-Za-z0-9_-]+\\runtime\\reference-preset\.",
        re.IGNORECASE,
    )
    linked = (
        ("evidence/19-response.json", "evidence/initial-preflight.json"),
        ("evidence/24-response.json", "evidence/repaired-preflight.json"),
    )
    for response_path, source_path in linked:
        row = rows.get(response_path)
        if row is None:
            continue
        if any(relation["targetPointer"] == "/sha256"
               and relation["sourcePath"] == source_path
               for relation in row["relationships"]):
            row["projection"]["sha256"] = "<linked-preflight-digest>"
    for path in ("evidence/19-response.json", "evidence/24-response.json",
                 "evidence/initial-preflight.json", "evidence/repaired-preflight.json"):
        row = rows.get(path)
        if row is None:
            continue
        for preview in row["projection"].get("optionalPreview", []):
            detail = preview.get("detail")
            if isinstance(detail, str) and media_pipe_path.fullmatch(detail):
                preview["detail"] = "MediaPipe runtime admitted at <runtime-extract>."

    archive = rows.get("evidence/29-response.json")
    if archive is not None and any(
        relation["targetPointer"] == "/verification/archiveSha256/value"
        and relation["sourcePath"] == "finished.zip"
        for relation in archive["relationships"]
    ):
        archive["projection"]["verification"]["archiveSha256"]["value"] = (
            "<linked-finished-archive-digest>")

    if executable_sha256 is not None:
        for path in ("evidence/initial-preflight.json", "evidence/repaired-preflight.json"):
            row = rows.get(path)
            identity = row["projection"].get("executableSha256") if row is not None else None
            if isinstance(identity, str) and identity.casefold() == executable_sha256.casefold():
                row["projection"]["executableSha256"] = "<release-cli-sha256>"
    return projected


def run_structured_journeys(
    *,
    repository_root: str | Path,
    runner_dll: str | Path,
    expected_runner_sha256: str,
    target_cli: str | Path,
    expected_target_sha256: str,
    dotnet_path: str | Path,
    expected_dotnet_sha256: str,
    output_root: str | Path,
    expected_harness: dict[str, Any],
    timeout_seconds: int = 600,
) -> tuple[dict[str, Any], ...]:
    """Run both existing selectors against one independently authenticated CLI."""

    repository = Path(repository_root).resolve()
    harness = authenticate_harness_checkout(
        repository,
        expected_commit=expected_harness["commit"],
        expected_tree=expected_harness["tree"],
        expected_source_closure=expected_harness["sourceClosure"],
    )
    runner = Path(runner_dll)
    target = Path(target_cli)
    dotnet = Path(dotnet_path)
    if not runner.is_file():
        raise FirewallError(f"runner missing: {runner}")
    if not target.is_file():
        raise FirewallError(f"target CLI missing: {target}")
    if not target.is_absolute() or target.drive.casefold() != "k:":
        raise FirewallError("target CLI must be an absolute K-local path")
    if target.drive.casefold() == "f:" or target.suffix.casefold() not in {".exe", ".dll"}:
        raise FirewallError("target CLI uses an unsupported path or suffix")
    if not dotnet.is_file():
        raise FirewallError(f"pinned dotnet missing: {dotnet}")
    runner_length, runner_hash = _authenticate_selector_file(
        runner.resolve(), expected_runner_sha256, "runner"
    )
    target_length, target_hash = _authenticate_selector_file(
        target.resolve(), expected_target_sha256, "target CLI"
    )
    _authenticate_selector_file(dotnet.resolve(), expected_dotnet_sha256, "pinned dotnet")
    output = Path(output_root)
    if not output.is_absolute() or output.exists():
        raise FirewallError("journey output root must be a new absolute path")
    output.mkdir(parents=True)
    results: list[dict[str, Any]] = []
    for selector, filename in (
        ("--test-finish-package-publication", "create-to-package.json"),
        ("--test-wave-b-v1-transcript", "wave-b-v1.json"),
    ):
        destination = output / filename
        capture_root, token = create_owned_journey_capture()
        observations = capture_root / "observations"
        observations.mkdir()
        internal_destination = observations / filename
        environment = os.environ.copy()
        environment.update({
            "ACTORWRIGHT_COMPATIBILITY_TEST_CLI": str(target.resolve()),
            "ACTORWRIGHT_COMPATIBILITY_TEST_CLI_SHA256": target_hash,
            "ACTORWRIGHT_COMPATIBILITY_CAPTURE_PARENT": str(
                CAPTURE_WORKSPACE_PARENT.resolve()
            ),
            "ACTORWRIGHT_COMPATIBILITY_CAPTURE_ROOT": str(capture_root),
            "ACTORWRIGHT_COMPATIBILITY_CAPTURE_TOKEN": token,
            "ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT": str(internal_destination),
        })
        try:
            completed = _run_bounded_process(
                [str(dotnet.resolve()), str(runner.resolve()), selector],
                cwd=str(repository),
                env=environment,
                shell=False,
                timeout=timeout_seconds,
                max_output_bytes=CLI_MAX_OUTPUT_BYTES,
            )
            if completed.returncode != 0:
                diagnostic_stream = "stderr" if completed.stderr.strip() else "stdout"
                diagnostic_bytes = (
                    completed.stderr if diagnostic_stream == "stderr" else completed.stdout
                )
                diagnostic = diagnostic_bytes[
                    :JOURNEY_FAILURE_OUTPUT_MAX_BYTES
                ].decode("utf-8", errors="replace").strip()
                truncated = len(diagnostic_bytes) > JOURNEY_FAILURE_OUTPUT_MAX_BYTES
                detail = (
                    f"; {diagnostic_stream}={diagnostic!r}"
                    + (" [truncated]" if truncated else "")
                    if diagnostic or truncated
                    else ""
                )
                raise FirewallError(
                    f"journey selector {selector} exited {completed.returncode}{detail}"
                )
            if not internal_destination.is_file():
                raise FirewallError(f"journey selector {selector} produced no structured observation")
            observation = _require_object(_load_json(internal_destination), f"journey selector {selector}")
            _canonicalize_journey_digests(observation, capture_root)
        finally:
            cleaned = remove_owned_journey_capture(capture_root, token)
            if not cleaned:
                raise FirewallError("journey capture cleanup refused an ownership mismatch")
        observation["captureTool"] = {
            "harnessCommit": harness["commit"],
            "harnessTree": harness["tree"],
            "harnessSourceClosure": harness["sourceClosure"],
            "runner": {"length": runner_length, "sha256": runner_hash},
            "target": {"length": target_length, "sha256": target_hash},
        }
        validated = validate_journey_observation(observation)
        destination.write_bytes(canonical_json_bytes(validated))
        results.append(validated)
    if authenticate_harness_checkout(
        repository,
        expected_commit=harness["commit"],
        expected_tree=harness["tree"],
        expected_source_closure=harness["sourceClosure"],
    ) != harness:
        raise FirewallError("journey harness identity changed during capture")
    return tuple(results)


def _protocol2_result(
    observation: CliObservation,
    *,
    command: str,
) -> dict[str, Any]:
    envelope = _require_object(observation.json_value, f"protocol 2 {command}")
    exit_code = envelope.get("exitCode")
    if isinstance(exit_code, bool) or not isinstance(exit_code, int):
        raise FirewallError(f"protocol 2 {command}: missing integer envelope exitCode")
    if exit_code != observation.exit_code:
        raise FirewallError(
            f"protocol 2 {command}: envelope exitCode {exit_code} "
            f"does not match process exit {observation.exit_code}"
        )
    if envelope.get("command") != command:
        raise FirewallError(f"protocol 2 {command}: envelope command mismatch")
    if exit_code != 0:
        raise FirewallError(f"protocol 2 {command}: discovery refused with exit {exit_code}")
    return _require_object(envelope.get("result"), f"protocol 2 {command}/result")


def _diagnostic_codes(value: Any, location: str) -> tuple[str, ...]:
    document = _require_object(value, location)
    diagnostics = _require_array(document.get("diagnostics"), f"{location}/diagnostics")
    return tuple(
        _require_string(
            _require_object(item, f"{location}/diagnostics/{index}").get("code"),
            f"{location}/diagnostics/{index}/code",
        )
        for index, item in enumerate(diagnostics)
    )


def _blocked_probe_results(message: str) -> tuple[GateResult, ...]:
    return tuple(
        GateResult(gate_id, True, "BLOCKED", (message,), ())
        for gate_id in _PACKAGE_GATE_IDS
    )


def _validate_sealed_package_evidence(
    target: CliTarget,
    evidence: SealedPackageEvidence,
) -> tuple[str, ...]:
    if target.expected_sha256 is None:
        raise FirewallError("package target requires expected executable sha256")
    entrypoint, _, _, _ = _validated_cli_target(target)
    package_root = Path(evidence.package_root)
    release_zip = Path(evidence.release_zip)
    if not package_root.is_absolute() or not package_root.is_dir():
        raise FirewallError("package evidence package root is missing or not absolute")
    package_root = package_root.resolve()
    expected_entrypoint = (package_root / "cli" / "actorwright.exe").resolve()
    if entrypoint.resolve() != expected_entrypoint:
        raise FirewallError("package evidence entrypoint is outside canonical package root")
    if not release_zip.is_absolute() or not release_zip.is_file():
        raise FirewallError("package evidence release ZIP is missing or not absolute")
    release_zip = release_zip.resolve()
    if (
        isinstance(evidence.expected_zip_length, bool)
        or evidence.expected_zip_length <= 0
        or release_zip.stat().st_size != evidence.expected_zip_length
    ):
        raise FirewallError("package evidence ZIP length mismatch")
    zip_sha256 = _sha256_file(release_zip).upper()
    if zip_sha256 != _expected_sha256(
        evidence.expected_zip_sha256, "package evidence ZIP"
    ):
        raise FirewallError("package evidence ZIP sha256 mismatch")
    if not evidence.source_tag:
        raise FirewallError("package evidence source tag is missing")
    for value, subject in (
        (evidence.source_commit, "source commit"),
        (evidence.source_tree, "source tree"),
    ):
        if len(value) != _GIT_OBJECT_LENGTH or any(
            character not in "0123456789abcdefABCDEF" for character in value
        ):
            raise FirewallError(f"package evidence {subject} is malformed")

    release_path = package_root / "actorwright-release.json"
    sums_path = package_root / "SHA256SUMS"
    if not release_path.is_file() or not sums_path.is_file():
        raise FirewallError("package evidence release manifest or SHA256SUMS is missing")
    release_bytes = release_path.read_bytes()
    sums_bytes = sums_path.read_bytes()
    release = _require_object(
        parse_json_output(release_bytes, "package release manifest"),
        "package release manifest",
    )
    for field, expected in (
        ("sourceTag", evidence.source_tag),
        ("sourceCommit", evidence.source_commit),
        ("sourceTree", evidence.source_tree),
    ):
        actual = release.get(field)
        if not isinstance(actual, str) or actual.casefold() != expected.casefold():
            raise FirewallError(f"package evidence {field} mismatch")
    try:
        sums_text = sums_bytes.decode("utf-8-sig")
    except UnicodeError as exc:
        raise FirewallError("package evidence SHA256SUMS is not UTF-8") from exc
    sums: dict[str, str] = {}
    for line in sums_text.splitlines():
        parts = line.strip().split(maxsplit=1)
        if len(parts) != 2:
            raise FirewallError("package evidence SHA256SUMS row is malformed")
        sums[parts[1].replace("\\", "/")] = parts[0].upper()
    executable_bytes = entrypoint.read_bytes()
    executable_sha256 = _sha256(executable_bytes).upper()
    if sums.get("cli/actorwright.exe") != executable_sha256:
        raise FirewallError("package evidence SHA256SUMS executable binding mismatch")
    prefix = package_root.name + "/"
    try:
        with zipfile.ZipFile(release_zip) as archive:
            archived_executable = archive.read(prefix + "cli/actorwright.exe")
            archived_release = archive.read(prefix + "actorwright-release.json")
            archived_sums = archive.read(prefix + "SHA256SUMS")
    except (OSError, KeyError, zipfile.BadZipFile) as exc:
        raise FirewallError("package evidence ZIP member binding is invalid") from exc
    if archived_executable != executable_bytes:
        raise FirewallError("package evidence ZIP executable bytes mismatch")
    if archived_release != release_bytes or archived_sums != sums_bytes:
        raise FirewallError("package evidence ZIP release bytes mismatch")
    return (
        f"packagePath={entrypoint.resolve()}",
        f"packageLength={len(executable_bytes)}",
        f"packageSha256={executable_sha256}",
        f"releaseZipPath={release_zip}",
        f"releaseZipLength={evidence.expected_zip_length}",
        f"releaseZipSha256={zip_sha256}",
        f"sourceTag={evidence.source_tag}",
        f"sourceCommit={evidence.source_commit}",
        f"sourceTree={evidence.source_tree}",
    )


def _validate_voice_dialogue_schema(command: str, result: dict[str, Any]) -> str:
    observed = _require_array(
        result.get("documentSchemas"), f"package schema {command}/documentSchemas"
    )
    expected = _VOICE_DIALOGUE_SCHEMA_CONTRACTS[command]
    if len(observed) != len(expected):
        raise FirewallError(f"package schema {command}: document schema count mismatch")
    for index, (schema_name, direction, identifier) in enumerate(expected):
        location = f"package schema {command}/documentSchemas/{index}"
        row = _require_object(observed[index], location)
        if (
            row.get("name") != schema_name
            or row.get("direction") != direction
            or row.get("schemaIdentifier") != identifier
        ):
            raise FirewallError(f"package schema {command}: route binding mismatch")
        document = _require_object(row.get("jsonSchema"), f"{location}/jsonSchema")
        if document.get("$id") != f"urn:actorwright:schema:{identifier}":
            raise FirewallError(f"package schema {command}: schema identifier mismatch")
        properties = _require_object(
            document.get("properties"), f"{location}/jsonSchema/properties"
        )
        schema_property = _require_object(
            properties.get("schema"), f"{location}/jsonSchema/properties/schema"
        )
        if schema_property.get("const") != identifier:
            raise FirewallError(f"package schema {command}: schema content mismatch")
    schema_sha256 = _sha256(canonical_json_bytes(observed)).upper()
    if schema_sha256 != _VOICE_DIALOGUE_SCHEMA_HASHES[command]:
        raise FirewallError(f"package schema {command}: canonical content hash mismatch")
    return schema_sha256


def _validate_legacy_protocol_envelope(
    command: str,
    observation: CliObservation,
) -> None:
    envelope = _require_object(
        observation.json_value, f"package protocol mode {command}"
    )
    expected_keys = {
        "protocolVersion",
        "schemaVersion",
        "command",
        "outcome",
        "exitCode",
        "requestDigest",
        "effects",
        "diagnostics",
        "artifacts",
        "authority",
        "nextActions",
    }
    if set(envelope) != expected_keys:
        raise FirewallError(f"package protocol mode {command}: envelope shape mismatch")
    if (
        observation.exit_code != 2
        or envelope.get("exitCode") != observation.exit_code
        or envelope.get("protocolVersion") != "2"
        or envelope.get("schemaVersion") != "1"
        or envelope.get("command") != command
        or envelope.get("outcome") != "failed"
        or envelope.get("requestDigest")
        != _VOICE_DIALOGUE_LEGACY_REQUEST_DIGESTS[command]
    ):
        raise FirewallError(f"package protocol mode {command}: envelope identity mismatch")
    expected_message = f"Command '{command}' is not ready for protocol 2."
    expected_diagnostics = [
        {
            "code": "protocol-command-legacy",
            "severity": "error",
            "class": "usage",
            "message": expected_message,
            "recovery": {
                "action": "correctInput",
                "retryUnchangedSafe": False,
                "constraint": expected_message,
            },
        }
    ]
    expected_effects = [
        {
            "kind": "appendLocalOperationJournal",
            "scope": "workspace-local-journal",
            "status": "attempted",
        },
        {
            "kind": "appendLocalOperationJournal",
            "scope": "workspace-local-journal",
            "status": "completed",
        },
    ]
    if envelope.get("diagnostics") != expected_diagnostics:
        raise FirewallError(f"package protocol mode {command}: typed error mismatch")
    if envelope.get("effects") != expected_effects:
        raise FirewallError(f"package protocol mode {command}: effects mismatch")
    for key in ("artifacts", "authority", "nextActions"):
        if envelope.get(key) != []:
            raise FirewallError(f"package protocol mode {command}: {key} must be empty")


def _validate_protocol1_refusal(
    observation: CliObservation,
    expected: dict[str, str],
    subject: str,
) -> str:
    document = _require_object(observation.json_value, subject)
    if observation.exit_code != 2 or document != expected:
        raise FirewallError(f"{subject}: exact protocol-1 refusal mismatch")
    return _sha256(canonical_json_bytes(document)).upper()


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _validate_voice_discovery_refusal(
    observation: CliObservation, invocation_start: datetime, invocation_end: datetime
) -> tuple[str, str]:
    document = _require_object(
        observation.json_value, "package voice dependency refusal"
    )
    if observation.exit_code != 4 or set(document) != {
        "schema",
        "services",
        "selectedEndpoint",
        "diagnostics",
        "probedUtc",
        "requestTextLoggedByService",
    }:
        raise FirewallError("package voice dependency probe: envelope mismatch")
    if (
        document.get("schema") != "npc.voice-services.v1"
        or document.get("selectedEndpoint") is not None
        or document.get("requestTextLoggedByService") is not True
        or _diagnostic_codes(document, "package voice dependency refusal")
        != ("voice-service-none",)
    ):
        raise FirewallError(
            "package voice dependency probe: expected voice-service-none refusal"
        )
    diagnostics = _require_array(
        document["diagnostics"], "package voice dependency diagnostics"
    )
    if len(diagnostics) != 1:
        raise FirewallError("package voice dependency probe: diagnostic count mismatch")
    diagnostic = _require_object(
        diagnostics[0], "package voice dependency diagnostic"
    )
    if not _strict_json_equal(diagnostic, {
        "code": "voice-service-none",
        "message": "No compatible storage-safe XTTS endpoint was found.",
        "severity": "error",
    }):
        raise FirewallError("package voice dependency probe: typed diagnostic mismatch")
    services = _require_array(document.get("services"), "package voice services")
    if len(services) != 1:
        raise FirewallError("package voice dependency probe: service count mismatch")
    service = _require_object(services[0], "package voice service")
    expected_service = {
        "endpoint": "http://127.0.0.1:1",
        "source": "override",
        "apiTitle": None,
        "apiVersion": None,
        "models": [],
        "languages": [],
        "speakerCount": 0,
        "speakerFolder": None,
        "outputFolder": None,
        "modelFolder": None,
        "settingsJson": None,
        "storageVerdict": "unknown",
        "storageDetail": "A task was canceled.",
        "compatible": False,
        "diagnostics": [
            {
                "code": "voice-service-unreachable",
                "message": "A task was canceled.",
                "severity": "error",
            }
        ],
        "fingerprint": None,
        "platform": "wsl",
    }
    if not _strict_json_equal(service, expected_service):
        raise FirewallError(
            "package voice dependency probe: closed service descriptor mismatch"
        )
    probed_utc = document.get("probedUtc")
    if not isinstance(probed_utc, str) or re.fullmatch(
        r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", probed_utc
    ) is None:
        raise FirewallError("package voice dependency probe: probedUtc is malformed")
    try:
        probed_at = datetime.strptime(probed_utc, "%Y-%m-%dT%H:%M:%SZ").replace(
            tzinfo=timezone.utc
        )
    except ValueError as exc:
        raise FirewallError(
            "package voice dependency probe: probedUtc is invalid"
        ) from exc
    # The CLI serializes whole seconds: its timestamp can precede the call
    # start by less than one second, but cannot validly follow call completion.
    if not invocation_start - timedelta(seconds=1) < probed_at <= invocation_end:
        raise FirewallError("package voice dependency probe: probedUtc outside invocation")
    normalized = dict(document)
    normalized["probedUtc"] = "<fresh-utc>"
    return _sha256(canonical_json_bytes(normalized)).upper(), probed_utc


def run_voice_dialogue_package_probes(
    target: CliTarget,
    *,
    package_evidence: SealedPackageEvidence,
) -> tuple[GateResult, ...]:
    """Probe every sealed-package voice/dialogue route without a success backend."""

    if target.name != "package":
        raise FirewallError("voice/dialogue package probes require a package target")
    trust_evidence = _validate_sealed_package_evidence(target, package_evidence)
    try:
        capabilities_v1 = run_cli(target, ["capabilities", "--json"])
        if capabilities_v1.exit_code != 0:
            raise FirewallError("package protocol 1 capabilities did not pass")
        v1 = _require_object(capabilities_v1.json_value, "package protocol 1 capabilities")
        v1_names = _command_names(v1, "package protocol 1 capabilities")
        capabilities_v2_observation = run_cli(
            target, ["capabilities", "--protocol", "2", "--json"]
        )
        capabilities_v2 = _protocol2_result(
            capabilities_v2_observation, command="capabilities"
        )
        v2_names = _command_names(capabilities_v2, "package protocol 2 capabilities")
        if v1_names != v2_names or len(v1_names) != EXPECTED_COMMAND_COUNT:
            raise FirewallError("package capabilities command inventory mismatch")
    except (FirewallError, OSError) as exc:
        return _blocked_probe_results(str(exc))

    results: list[GateResult] = []
    for command in VOICE_DIALOGUE_SENTINELS:
        gate_id = f"package:{command}"
        evidence = [*trust_evidence, "discovery=PASS"]
        try:
            if command not in v1_names:
                raise FirewallError(f"package command missing: {command}")
            help_observation = run_cli(
                target,
                [*command.split(), "--help", "--protocol", "2", "--json"],
            )
            help_result = _protocol2_result(help_observation, command=command)
            help_contract = _require_object(
                help_result.get("contract"), f"package help {command}/contract"
            )
            if help_contract.get("name") != command:
                raise FirewallError(f"package help {command}: contract mismatch")
            evidence.append("help=PASS")

            schema_observation = run_cli(
                target,
                [
                    "schema", "export", "--protocol", "2", "--json",
                    "--command", command,
                ],
            )
            schema_result = _protocol2_result(
                schema_observation, command="schema export"
            )
            schema_contract = _require_object(
                schema_result.get("contract"),
                f"package schema {command}/contract",
            )
            if schema_contract.get("name") != command:
                raise FirewallError(f"package schema {command}: contract mismatch")
            schema_sha256 = _validate_voice_dialogue_schema(command, schema_result)
            evidence.append("schema=PASS")
            evidence.append(f"schemaSha256={schema_sha256}")

            protocol_mode_observation = run_cli(
                target,
                [*command.split(), "--protocol", "2", "--json"],
            )
            _validate_legacy_protocol_envelope(command, protocol_mode_observation)
            evidence.append("protocol2-legacy-envelope=PASS")
            evidence.append("routeProtocol=1-legacy")

            option_observation = run_cli(
                target,
                [*command.split(), "--compatibility-unknown", "x", "--json"],
            )
            expected_option_message = (
                "Voice commands do not support --compatibility-unknown."
                if command.startswith("npc voice")
                else "Dialogue commands do not support --compatibility-unknown."
            )
            option_sha256 = _validate_protocol1_refusal(
                option_observation,
                {"code": "usage", "message": expected_option_message},
                f"package option probe {command}",
            )
            evidence.append("option-refusal=PASS")
            evidence.append(f"optionRefusalSha256={option_sha256}")

            if command == "npc voice discover":
                invocation_start = _utc_now()
                dependency_observation = run_cli(
                    target,
                    [
                        "npc", "voice", "discover", "--endpoint",
                        "http://127.0.0.1:1", "--timeout-seconds", "1", "--json",
                    ],
                    timeout_seconds=10,
                )
                invocation_end = _utc_now()
                refusal_sha256, probed_utc = _validate_voice_discovery_refusal(
                    dependency_observation, invocation_start, invocation_end
                )
                evidence.append("dependency-refusal=voice-service-none")
                evidence.append(f"dependencyRefusalSha256={refusal_sha256}")
                evidence.append(f"dependencyRefusalProbedUtc={probed_utc}")
                evidence.append(f"dependencyRefusalInvocationStartUtc={invocation_start.isoformat()}")
                evidence.append(f"dependencyRefusalInvocationEndUtc={invocation_end.isoformat()}")
            else:
                dependency_observation = run_cli(
                    target, [*command.split(), "--json"]
                )
                refusal_sha256 = _validate_protocol1_refusal(
                    dependency_observation,
                    {
                        "code": "usage",
                        "message": _VOICE_DIALOGUE_REQUIRED_REFUSALS[command],
                    },
                    f"package dependency probe {command}",
                )
                evidence.append("dependency-refusal=usage")
                evidence.append(f"dependencyRefusalSha256={refusal_sha256}")
        except (FirewallError, OSError) as exc:
            status = "BLOCKED" if "timed out" in str(exc).casefold() else "FAIL"
            results.append(
                GateResult(gate_id, True, status, (*evidence, str(exc)), ())
            )
            continue
        results.append(GateResult(gate_id, True, "PASS", tuple(evidence), ()))
    return tuple(results)


def _capture_behavior_probes(target: CliTarget) -> tuple[list[dict[str, Any]], list[dict[str, Any]]]:
    observations: list[dict[str, Any]] = []
    facts: list[dict[str, Any]] = []
    for declaration in BEHAVIOR_PROBE_CATALOGUE:
        probe_id = declaration["id"]
        observation = run_cli(target, declaration["arguments"])
        envelope = _require_object(observation.json_value, f"behavior probe {probe_id}")
        exit_code = envelope.get("exitCode")
        if exit_code != observation.exit_code or exit_code != 2:
            raise FirewallError(f"behavior probe {probe_id}: expected matching exit 2")
        if envelope.get("command") != declaration["command"]:
            raise FirewallError(f"behavior probe {probe_id}: command mismatch")
        diagnostics = _require_array(envelope.get("diagnostics"), f"behavior probe {probe_id}/diagnostics")
        codes = [
            _require_string(
                _require_object(item, f"behavior probe {probe_id}/diagnostics/{index}").get("code"),
                f"behavior probe {probe_id}/diagnostics/{index}/code",
            )
            for index, item in enumerate(diagnostics)
        ]
        missing = [code for code in declaration["requiredDiagnostics"] if code not in codes]
        forbidden = [code for code in declaration["forbiddenDiagnostics"] if code in codes]
        if missing or forbidden:
            raise FirewallError(
                f"behavior probe {probe_id}: diagnostic evidence mismatch; "
                f"missing={missing!r}; forbidden={forbidden!r}; observed={codes!r}"
            )
        observations.append(
            {
                "id": probe_id,
                "arguments": declaration["arguments"],
                "exitCode": observation.exit_code,
                "diagnosticCodes": codes,
                "stdout": _canonical_identity(observation.json_value),
            }
        )
        for raw_fact in declaration["facts"]:
            fact = dict(raw_fact)
            fact["evidenceProbe"] = probe_id
            facts.append(fact)
    observed_keys = [
        (fact["command"], fact["option"], fact["field"], fact["value"], fact["evidenceProbe"])
        for fact in facts
    ]
    for required in REQUIRED_BEHAVIOR_FACTS:
        if required not in observed_keys:
            raise FirewallError(
                f"required behavior fact lacks declared probe evidence: {required[2]}"
            )
    if len(observed_keys) != len(set(observed_keys)) or len(observed_keys) != len(REQUIRED_BEHAVIOR_FACTS):
        raise FirewallError("behavior fact catalogue contains duplicate or undeclared evidence")
    return observations, facts


def _canonical_identity(value: Any) -> dict[str, Any]:
    data = canonical_json_bytes(value)
    return {"document": value, "sha256": _sha256(data).upper()}


def _workspace_inventory(root: Path) -> list[dict[str, Any]]:
    root_stat = root.stat()
    inventory: list[dict[str, Any]] = [
        {
            "path": ".",
            "kind": "directory",
            "reparsePoint": bool(
                getattr(root_stat, "st_file_attributes", 0) & 0x400
            ),
            "fileId": root_stat.st_ino,
            "createdNs": root_stat.st_ctime_ns,
            "modifiedNs": root_stat.st_mtime_ns,
        }
    ]

    def visit(directory: Path) -> None:
        try:
            entries = sorted(os.scandir(directory), key=lambda item: item.name.casefold())
        except OSError as exc:
            raise FirewallError(f"cannot inventory capture workspace {directory}: {exc}") from exc
        for entry in entries:
            path = Path(entry.path)
            relative = path.relative_to(root).as_posix()
            stat = entry.stat(follow_symlinks=False)
            attributes = getattr(stat, "st_file_attributes", 0)
            reparse = bool(attributes & 0x400)
            is_link = entry.is_symlink() or reparse
            if entry.is_dir(follow_symlinks=False):
                record: dict[str, Any] = {
                    "path": relative,
                    "kind": "directory",
                    "reparsePoint": reparse,
                    "fileId": stat.st_ino,
                    "createdNs": stat.st_ctime_ns,
                    "modifiedNs": stat.st_mtime_ns,
                }
                if is_link:
                    try:
                        record["linkTarget"] = os.readlink(path)
                    except OSError:
                        record["linkTarget"] = None
                inventory.append(record)
                if not is_link:
                    visit(path)
                continue
            if entry.is_file(follow_symlinks=False):
                inventory.append(
                    {
                        "path": relative,
                        "kind": "file",
                        "length": stat.st_size,
                        "sha256": _sha256_file(path).upper(),
                        "reparsePoint": reparse,
                        "fileId": stat.st_ino,
                        "createdNs": stat.st_ctime_ns,
                        "modifiedNs": stat.st_mtime_ns,
                    }
                )
                continue
            inventory.append(
                {
                    "path": relative,
                    "kind": "other",
                    "reparsePoint": reparse,
                    "fileId": stat.st_ino,
                    "createdNs": stat.st_ctime_ns,
                    "modifiedNs": stat.st_mtime_ns,
                }
            )

    visit(root)
    return inventory


def _command_names(capabilities: dict[str, Any], location: str) -> list[str]:
    rows = _require_array(capabilities.get("commands"), f"{location}/commands")
    names: list[str] = []
    for index, raw_row in enumerate(rows):
        row = _require_object(raw_row, f"{location}/commands/{index}")
        name = _require_string(row.get("name"), f"{location}/commands/{index}/name")
        if name in names:
            raise FirewallError(f"{location}/commands/{index}: duplicate command {name}")
        names.append(name)
    return names


def _exit_meaning(code: int) -> str:
    meanings = dict(EXIT_MEANINGS)
    if code not in meanings:
        raise FirewallError(f"no-input probe returned unknown exit code {code}")
    return meanings[code]


def _result_class(code: int) -> str:
    if code == 0:
        return "SUCCEEDED"
    if code in {2, 3, 4}:
        return "REFUSED"
    if code == 5:
        return "CANCELLED"
    return "FAILED"


def capture_cli_contract(target: CliTarget) -> dict[str, Any]:
    """Capture the exact public command contract into an in-memory document.

    Raw public contracts are retained, while the normalized option projection
    is grounded in those contracts and the reviewed shared-binder probes.
    """

    _validated_cli_target(target)
    version_v1 = run_cli(target, ["version", "--json"])
    capabilities_v1 = run_cli(target, ["capabilities", "--json"])
    version_v2_observation = run_cli(
        target, ["version", "--protocol", "2", "--json"]
    )
    capabilities_v2_observation = run_cli(
        target, ["capabilities", "--protocol", "2", "--json"]
    )
    if version_v1.exit_code != 0 or capabilities_v1.exit_code != 0:
        raise FirewallError("protocol 1 discovery did not succeed")
    version_v1_value = _require_object(version_v1.json_value, "protocol 1 version")
    capabilities_v1_value = _require_object(
        capabilities_v1.json_value, "protocol 1 capabilities"
    )
    version_v2 = _protocol2_result(version_v2_observation, command="version")
    capabilities_v2 = _protocol2_result(
        capabilities_v2_observation, command="capabilities"
    )
    v1_names = _command_names(capabilities_v1_value, "protocol 1 capabilities")
    v2_names = _command_names(capabilities_v2, "protocol 2 capabilities")
    if v1_names != v2_names:
        raise FirewallError("protocol 1 and protocol 2 command order differs")
    if len(v1_names) != EXPECTED_COMMAND_COUNT:
        raise FirewallError(
            f"expected exact {EXPECTED_COMMAND_COUNT} commands, observed {len(v1_names)}"
        )
    missing_sentinels = [name for name in VOICE_DIALOGUE_SENTINELS if name not in v1_names]
    if missing_sentinels:
        raise FirewallError(
            "voice/dialogue sentinels missing: " + ", ".join(missing_sentinels)
        )

    behavior_probes, behavior_facts = _capture_behavior_probes(target)

    v1_rows = capabilities_v1_value["commands"]
    v2_rows = capabilities_v2["commands"]
    commands: list[dict[str, Any]] = []
    for position, name in enumerate(v1_names):
        contract = _require_object(v2_rows[position], f"protocol 2 command {name}")
        help_observation = run_cli(
            target,
            [*name.split(), "--help", "--protocol", "2", "--json"],
        )
        help_result = _protocol2_result(help_observation, command=name)
        help_contract = _require_object(
            help_result.get("contract"), f"protocol 2 help {name}/contract"
        )
        if help_contract.get("name") != name:
            raise FirewallError(f"protocol 2 help {name}: contract name mismatch")
        schema_observation = run_cli(
            target,
            [
                "schema",
                "export",
                "--protocol",
                "2",
                "--json",
                "--command",
                name,
            ],
        )
        schema_result = _protocol2_result(
            schema_observation, command="schema export"
        )
        schema_contract = _require_object(
            schema_result.get("contract"), f"schema export {name}/contract"
        )
        if schema_contract.get("name") != name:
            raise FirewallError(f"schema export {name}: contract name mismatch")
        aliases = _require_string_array(contract.get("aliases"), f"command {name}/aliases")
        commands.append(
            {
                "position": position,
                "name": name,
                "pathTokens": name.split(),
                "aliases": aliases,
                "protocol1Capability": v1_rows[position],
                "protocol2Contract": contract,
                "help": _canonical_identity(help_result),
                "schemaExport": _canonical_identity(schema_result),
            }
        )

    before = _workspace_inventory(Path(target.workspace_root))
    for command in commands:
        name = command["name"]
        observation = run_cli(
            target,
            [*name.split(), "--protocol", "1", "--json"],
        )
        command["noInputProbe"] = {
            "arguments": [*name.split(), "--protocol", "1", "--json"],
            "exitCode": observation.exit_code,
            "exitMeaning": _exit_meaning(observation.exit_code),
            "resultClass": _result_class(observation.exit_code),
            "stdout": _canonical_identity(observation.json_value),
            "stderrSha256": _sha256(observation.stderr).upper(),
        }
    after = _workspace_inventory(Path(target.workspace_root))
    if before != after:
        raise FirewallError(
            "no-input probes changed workspace; "
            f"before={before!r}; after={after!r}"
        )

    return {
        "formatVersion": 1,
        "target": target.name,
        "version": {
            "protocol1": version_v1_value,
            "protocol2": version_v2,
        },
        "capabilities": {
            "protocol1": _canonical_identity(capabilities_v1_value),
            "protocol2": _canonical_identity(capabilities_v2),
        },
        "commandCount": len(commands),
        "orderedCommandNamesSha256": _sha256("\n".join(v1_names).encode("utf-8")).upper(),
        "commands": commands,
        "behaviorProbes": behavior_probes,
        "behaviorFacts": behavior_facts,
        "exitMeanings": [
            {"code": code, "meaning": meaning} for code, meaning in EXIT_MEANINGS
        ],
        "voiceDialogueSentinels": list(VOICE_DIALOGUE_SENTINELS),
        "workspaceInventoryBeforeNoInput": before,
        "workspaceInventoryAfterNoInput": after,
    }


def _checked_process(arguments: list[str], *, cwd: Path, timeout: int) -> bytes:
    try:
        completed = _run_bounded_process(
            arguments,
            cwd=str(cwd),
            env=os.environ.copy(),
            shell=False,
            timeout=timeout,
            max_output_bytes=64 * 1024 * 1024,
        )
    except OSError as exc:
        raise FirewallError(f"authenticated capture process failed: {exc}") from exc
    if completed.returncode != 0:
        detail = (completed.stderr or completed.stdout)[-4096:].decode("utf-8", errors="replace")
        raise FirewallError(
            f"authenticated capture process exited {completed.returncode}: "
            f"{' '.join(arguments)}\n{detail}"
        )
    return completed.stdout


def _git(repository_root: Path, *arguments: str) -> bytes:
    return _checked_process(
        [
            "git", "-c", f"safe.directory={repository_root.as_posix()}",
            "-c", "core.longpaths=true", *arguments,
        ],
        cwd=repository_root,
        timeout=120,
    )


def _stable_command_contracts(snapshot: dict[str, Any]) -> list[dict[str, Any]]:
    keys = (
        "position", "name", "pathTokens", "aliases",
        "protocol1Capability", "protocol2Contract", "help", "schemaExport",
    )
    return [{key: command[key] for key in keys} for command in snapshot["commands"]]


def _remove_disposable_path(path: Path) -> None:
    try:
        if path.exists():
            shutil.rmtree(path)
    except OSError as exc:
        raise FirewallError(
            f"could not remove disposable authenticated path {path}: {exc}"
        ) from exc
    if path.exists():
        raise FirewallError(f"disposable authenticated path was not removed: {path}")


def _validate_release_inventory_closure(
    archive_members: list[str],
    wrapper: str,
    checksum_paths: set[str],
) -> None:
    prefix = f"{wrapper}/"
    relative_members: list[str] = []
    for raw_name in archive_members:
        name = raw_name.replace("\\", "/")
        if not name.startswith(prefix):
            raise FirewallError(f"sealed release member escapes wrapper: {name}")
        relative = name[len(prefix):]
        if not relative or relative.endswith("/"):
            continue
        relative_members.append(relative)
    if len(relative_members) != len(set(relative_members)):
        raise FirewallError("sealed release inventory contains duplicate member path")
    expected = set(relative_members) - {"SHA256SUMS"}
    if expected != checksum_paths:
        missing = sorted(expected - checksum_paths)
        unexpected = sorted(checksum_paths - expected)
        raise FirewallError(
            "sealed release inventory path closure mismatch; "
            f"missing checksums={missing!r}; unexpected checksums={unexpected!r}"
        )


def _cleanup_authenticated_capture(
    repository_root: Path,
    worktree: Path,
    *,
    registered: bool,
    disposable_paths: tuple[Path, ...],
) -> None:
    errors: list[str] = []
    if registered:
        try:
            _git(repository_root, "worktree", "remove", "--force", str(worktree))
        except (FirewallError, OSError) as exc:
            errors.append(str(exc))
    seen: set[Path] = set()
    for path in disposable_paths:
        if path in seen:
            continue
        seen.add(path)
        try:
            _remove_disposable_path(path)
        except (FirewallError, OSError) as exc:
            errors.append(str(exc))
    if registered:
        try:
            _git(repository_root, "worktree", "prune")
        except (FirewallError, OSError) as exc:
            errors.append(str(exc))
    if errors:
        raise FirewallError("authenticated cleanup failed: " + "; ".join(errors))


def capture_authenticated_release(
    *,
    repository_root: Path,
    source_tag: str,
    sealed_zip: Path,
    expected_zip_length: int,
    expected_zip_sha256: str,
    dotnet_path: Path = PINNED_DOTNET,
) -> dict[str, Any]:
    """Build an exact peeled tag and compare it with one authenticated ZIP.

    All checkouts, extraction, and CLI workspaces are disposable children of
    the repository test-work root. No baseline, release, or production state is
    written by this internal verification API.
    """

    repository_root = Path(repository_root).resolve()
    sealed_zip = Path(sealed_zip).resolve()
    dotnet_path = Path(dotnet_path).resolve()
    if repository_root != REPOSITORY_ROOT.resolve():
        raise FirewallError("authenticated capture repository root mismatch")
    if not re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+-preview\.[0-9]+", source_tag):
        raise FirewallError("authenticated capture requires an exact preview tag")
    if sealed_zip.stat().st_size != expected_zip_length:
        raise FirewallError("sealed ZIP length mismatch")
    zip_sha256 = _sha256_file(sealed_zip).upper()
    if zip_sha256 != expected_zip_sha256.upper():
        raise FirewallError("sealed ZIP sha256 mismatch")
    if not dotnet_path.is_file():
        raise FirewallError("pinned SDK 10.0.301 entrypoint is missing")

    commit = _git(repository_root, "rev-parse", f"refs/tags/{source_tag}^{{commit}}").decode().strip()
    tree = _git(repository_root, "rev-parse", f"refs/tags/{source_tag}^{{tree}}").decode().strip()
    token = uuid.uuid4().hex
    parent = CAPTURE_WORKSPACE_PARENT.resolve()
    worktree = parent / f"compatibility-firewall-authenticated-worktree-{token}"
    extraction = parent / f"compatibility-firewall-authenticated-extraction-{token}"
    source_workspace = parent / f"compatibility-firewall-authenticated-source-{token}"
    package_workspace = parent / f"compatibility-firewall-authenticated-package-{token}"
    registered = False
    try:
        _git(repository_root, "worktree", "add", "--detach", str(worktree), commit)
        registered = True
        _checked_process(
            [
                str(dotnet_path), "build", "Actorwright.sln", "-c", "Release",
                "-p:NuGetAudit=false",
            ],
            cwd=worktree,
            timeout=1800,
        )
        source_entrypoint = worktree / "src" / "NpcManager.Cli" / "bin" / "Release" / "net10.0" / "actorwright.dll"
        source_workspace = prepare_capture_workspace(source_workspace)
        source_snapshot = capture_cli_contract(
            CliTarget("source", source_entrypoint, source_workspace, _sha256_file(source_entrypoint), dotnet_path)
        )

        extraction.mkdir(parents=True)
        with zipfile.ZipFile(sealed_zip) as archive:
            infos = archive.infolist()
            if not infos:
                raise FirewallError("sealed ZIP is empty")
            names = [info.filename.replace("\\", "/") for info in infos]
            if any(Path(name).is_absolute() or ".." in Path(name).parts for name in names):
                raise FirewallError("sealed ZIP contains unsafe member path")
            roots = {name.split("/", 1)[0] for name in names}
            if len(roots) != 1:
                raise FirewallError("sealed ZIP must have one wrapper root")
            wrapper = next(iter(roots))
            archive.extractall(extraction)
        package_root = extraction / wrapper
        release_bytes = (package_root / "actorwright-release.json").read_bytes()
        release = parse_json_output(release_bytes, "sealed release manifest")
        if release.get("sourceTag") != source_tag or release.get("sourceCommit") != commit:
            raise FirewallError("sealed release source tag/commit mismatch")
        if str(release.get("sourceTree", "")).casefold() != tree.casefold():
            raise FirewallError("sealed release source tree mismatch")
        capabilities_path = package_root / "evidence" / "capabilities.json"
        capabilities_sha256 = _sha256_file(capabilities_path).upper()
        if capabilities_sha256 != str(release.get("capabilitiesSha256", "")).upper():
            raise FirewallError("sealed capabilities hash mismatch")
        test_summary_path = package_root / "evidence" / "test-summary.json"
        test_summary_bytes = test_summary_path.read_bytes()
        pytest_summary = _require_object(
            parse_json_output(test_summary_bytes, "sealed test summary"),
            "sealed test summary",
        )
        test_summary_sha256 = _sha256(test_summary_bytes).upper()
        if test_summary_sha256 != str(release.get("testSummarySha256", "")).upper():
            raise FirewallError("sealed pytest summary hash mismatch")

        sums: dict[str, str] = {}
        for line in (package_root / "SHA256SUMS").read_text(encoding="utf-8-sig").splitlines():
            if not line.strip():
                continue
            digest, relative = line.split(maxsplit=1)
            relative = relative.lstrip(" *").replace("\\", "/")
            if relative in sums:
                raise FirewallError(f"sealed release inventory duplicate checksum path: {relative}")
            path = package_root / Path(relative)
            if not path.is_file() or _sha256_file(path).casefold() != digest.casefold():
                raise FirewallError(f"sealed release inventory mismatch: {relative}")
            sums[relative] = digest.upper()
        _validate_release_inventory_closure(names, wrapper, set(sums))
        inventory_document = [{"path": name, "sha256": sums[name]} for name in sorted(sums)]
        if len(infos) != 28 or len(sums) != 27:
            raise FirewallError(
                f"sealed release inventory count mismatch: members={len(infos)}, checksums={len(sums)}"
            )

        sums_text = (package_root / "SHA256SUMS").read_text(encoding="utf-8-sig")
        expected_entrypoint_hash = next(
            line.split()[0] for line in sums_text.splitlines()
            if line.replace("\\", "/").endswith("cli/actorwright.exe")
        )
        package_workspace = prepare_capture_workspace(package_workspace)
        package_snapshot = capture_cli_contract(
            CliTarget("package", package_root / "cli" / "actorwright.exe", package_workspace, expected_entrypoint_hash)
        )
        if _stable_command_contracts(source_snapshot) != _stable_command_contracts(package_snapshot):
            raise FirewallError("authenticated source/package command overlap differs")

        selector_bytes = (worktree / "tests" / "standalone-test-matrix.json").read_bytes()
        selectors = parse_json_output(selector_bytes, "selector inventory")
        selector_rows = _require_array(selectors.get("tests"), "selector inventory/tests")
        selector_classes = {"runnable": 0, "fixture-bound": 0, "unverified": 0}
        for index, raw_selector in enumerate(selector_rows):
            selector = _require_object(raw_selector, f"selector inventory/tests/{index}")
            classification = _require_string(
                selector.get("classification"), f"selector inventory/tests/{index}/classification"
            )
            if classification not in selector_classes:
                raise FirewallError(f"unknown selector classification {classification!r}")
            selector_classes[classification] += 1
        readiness: dict[str, int] = {"v2": 0, "legacy": 0}
        for command in source_snapshot["commands"]:
            value = command["protocol2Contract"].get("readiness")
            if value not in readiness:
                raise FirewallError(f"unknown protocol readiness {value!r}")
            readiness[value] += 1
        expected_summary = {
            "schemaVersion": 1,
            "status": "PASS",
            "sourceCommit": commit,
            "sourceTag": source_tag,
            "configuration": "Release",
            "projectsCompiled": 25,
            "warnings": 0,
            "errors": 0,
            "exactCommandNames": 142,
            "standalonePythonCases": 317,
            "legacyWorkspaceBoundSuites": "COMPILE_ONLY",
            "runtimeAuthority": False,
            "visualAuthority": False,
        }
        required_facts = (
            ("capabilities SHA-256", capabilities_sha256, EXPECTED_PREVIEW_275_CAPABILITIES_SHA256),
            ("command order SHA-256", source_snapshot["orderedCommandNamesSha256"], EXPECTED_PREVIEW_275_COMMAND_ORDER_SHA256),
            ("readiness breakdown", readiness, EXPECTED_PREVIEW_275_READINESS),
            ("selector total", len(selector_rows), 158),
            ("selector classifications", selector_classes, EXPECTED_PREVIEW_275_SELECTOR_CLASSES),
            ("selector SHA-256", _sha256(canonical_json_bytes(selectors)).upper(), EXPECTED_PREVIEW_275_SELECTOR_SHA256),
            ("pytest summary", pytest_summary, expected_summary),
            ("pytest summary SHA-256", test_summary_sha256, EXPECTED_PREVIEW_275_TEST_SUMMARY_SHA256),
        )
        for label, actual, expected in required_facts:
            if actual != expected:
                raise FirewallError(f"authenticated {label} mismatch: {actual!r}")
        return {
            "sourceTag": source_tag,
            "sourceCommit": commit,
            "sourceTree": tree,
            "releaseZip": {"length": expected_zip_length, "sha256": zip_sha256},
            "capabilitiesSha256": capabilities_sha256,
            "releaseInventory": {"count": len(infos), "sha256": _sha256(canonical_json_bytes(inventory_document)).upper(), "verifiedEntries": len(sums)},
            "commandCount": source_snapshot["commandCount"],
            "readinessCounts": readiness,
            "selectorInventory": {"count": len(selector_rows), "classifications": selector_classes, "sha256": _sha256(canonical_json_bytes(selectors)).upper(), "ids": [row["id"] for row in selector_rows]},
            "pytestSummary": {**pytest_summary, "sha256": test_summary_sha256},
            "sourcePackageOverlap": "PASS",
            "orderedCommandNamesSha256": source_snapshot["orderedCommandNamesSha256"],
            "sourceSnapshot": source_snapshot,
            "packageSnapshot": package_snapshot,
        }
    finally:
        _cleanup_authenticated_capture(
            repository_root,
            worktree,
            registered=registered,
            disposable_paths=(source_workspace, package_workspace, extraction, worktree),
        )


def _json_pointer_token(token: str) -> str:
    return token.replace("~", "~0").replace("/", "~1")


_POLICY_SCHEMA_ID = "urn:actorwright:compatibility:v1:policy"
_NORMALIZER_FIELDS = {
    "temporary-root": {"workspaceRoot", "outputRoot"},
    "generated-timestamp": {"generatedAtUtc"},
    "operation-id": {"operationId"},
}
_PROTECTED_POINTER_ROOTS = {
    "$schema",
    "formatVersion",
    "target",
    "id",
    "provenance",
    "commandCount",
    "orderedCommandNamesSha256",
    "commands",
    "exitMeanings",
    "voiceDialogueSentinels",
    "members",
    "requiredPackagePaths",
    "launcher",
    "version",
    "capabilities",
    "behaviorProbes",
    "behaviorFacts",
    "workspaceInventoryBeforeNoInput",
    "workspaceInventoryAfterNoInput",
    "pinRows",
}
_PROTECTED_POINTER_TOKENS = {
    "$schema",
    "formatVersion",
    "target",
    "id",
    "kind",
    "role",
    "schemaId",
    "baselineId",
    "gateId",
    "workflowId",
    "provenance",
    "pinRows",
    "pinId",
    "stage",
    "producer",
    "consumer",
    "producerValue",
    "consumerValue",
    "identityClass",
    "producerEvidencePath",
    "consumerEvidencePath",
    "release",
    "releaseZip",
    "sourceCommit",
    "sourceTree",
    "sourceTag",
    "productVersion",
    "sourceLine",
}
_STABLE_ISSUE_ID = re.compile(r"^[A-Z][A-Z0-9]*(?:-[A-Z0-9]+)*-\d+$")
_MISSING = object()
_MISSING_EVIDENCE = {
    "$actorwrightCompatibilityPresence": (
        "missing-v1-6e2cf75d7a8c4f3aa2a7894981b14cf1"
    )
}
_OPERATION_ID = re.compile(
    r"^(?:[0-9A-Fa-f]{32}|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-"
    r"[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})$"
)
_UTC_TIMESTAMP = re.compile(
    r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$"
)
_OWNED_TEMP_ROOT = re.compile(
    r"^(?:compatibility-firewall-[A-Za-z0-9][A-Za-z0-9-]*|[jcw]-[0-9A-Fa-f]{32})$"
)


def _strict_json_equal(expected: Any, actual: Any) -> bool:
    if type(expected) is not type(actual):
        return False
    if isinstance(expected, dict):
        return expected.keys() == actual.keys() and all(
            _strict_json_equal(value, actual[key])
            for key, value in expected.items()
        )
    if isinstance(expected, list):
        return len(expected) == len(actual) and all(
            _strict_json_equal(expected_value, actual_value)
            for expected_value, actual_value in zip(expected, actual)
        )
    return expected == actual


def _json_pointer_tokens(pointer: Any, location: str) -> tuple[str, ...]:
    if not isinstance(pointer, str) or not pointer.startswith("/") or pointer == "/":
        raise FirewallError(f"{location}: expected exact full JSON Pointer")
    raw_tokens = pointer[1:].split("/")
    tokens: list[str] = []
    for raw in raw_tokens:
        if not raw:
            raise FirewallError(f"{location}: expected exact full JSON Pointer")
        if "*" in raw:
            raise FirewallError(f"{location}: wildcard JSON Pointer is forbidden")
        if re.search(r"~(?:[^01]|$)", raw):
            raise FirewallError(f"{location}: malformed JSON Pointer escape")
        token = raw.replace("~1", "/").replace("~0", "~")
        if token == "-":
            raise FirewallError(f"{location}: append JSON Pointer is forbidden")
        tokens.append(token)
    return tuple(tokens)


def _reject_protected_pointer(tokens: tuple[str, ...], location: str) -> None:
    if (
        tokens[0] in _PROTECTED_POINTER_ROOTS
        or any(token in _PROTECTED_POINTER_TOKENS for token in tokens)
        or any(
            token.casefold().endswith(("sha256", "hash", "digest"))
            for token in tokens
        )
    ):
        raise FirewallError(f"{location}: protected compatibility field")


def _nonempty_policy_string(value: Any, location: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise FirewallError(f"{location}: expected non-empty string")
    return value


def validate_policy(value: dict[str, Any]) -> ValidatedPolicy:
    """Validate the closed, exact compatibility policy contract."""

    policy = _require_object(value, "policy")
    _require_closed(
        policy,
        "policy",
        required={"$schema", "formatVersion", "semanticRules", "acceptedDifferences"},
    )
    if policy["$schema"] != _POLICY_SCHEMA_ID:
        raise FirewallError("policy/$schema: unsupported policy schema")
    if policy["formatVersion"] != 1 or isinstance(policy["formatVersion"], bool):
        raise FirewallError("policy/formatVersion: unsupported format version")

    semantic_rules: list[SemanticRule] = []
    semantic_keys: set[tuple[str, str]] = set()
    for index, raw_rule in enumerate(_require_array(policy["semanticRules"], "policy/semanticRules")):
        location = f"policy/semanticRules/{index}"
        rule = _require_object(raw_rule, location)
        _require_closed(
            rule,
            location,
            required={"workflowId", "pointer", "normalizer"},
        )
        workflow_id = _nonempty_policy_string(rule["workflowId"], f"{location}/workflowId")
        pointer = rule["pointer"]
        tokens = _json_pointer_tokens(pointer, f"{location}/pointer")
        _reject_protected_pointer(tokens, f"{location}/pointer")
        normalizer = rule["normalizer"]
        if not isinstance(normalizer, str) or normalizer not in _NORMALIZER_FIELDS:
            raise FirewallError(f"{location}/normalizer: unknown normalizer {normalizer!r}")
        if tokens[-1] not in _NORMALIZER_FIELDS[normalizer]:
            raise FirewallError(
                f"{location}/pointer: normalizer {normalizer} may target only its named leaf field"
            )
        key = (workflow_id, pointer)
        if key in semantic_keys:
            raise FirewallError(f"{location}: duplicate semantic rule")
        semantic_keys.add(key)
        semantic_rules.append(SemanticRule(workflow_id, pointer, normalizer))

    accepted_differences: list[AcceptedDifference] = []
    accepted_keys: set[tuple[str, str]] = set()
    accepted_required = {
        "issueId",
        "rationale",
        "regressionSelector",
        "affectedCommand",
        "workflowId",
        "pointer",
        "baselineValue",
        "candidateValue",
    }
    for index, raw_accepted in enumerate(
        _require_array(policy["acceptedDifferences"], "policy/acceptedDifferences")
    ):
        location = f"policy/acceptedDifferences/{index}"
        accepted = _require_object(raw_accepted, location)
        _require_closed(accepted, location, required=accepted_required)
        issue_id = _nonempty_policy_string(accepted["issueId"], f"{location}/issueId")
        if _STABLE_ISSUE_ID.fullmatch(issue_id) is None:
            raise FirewallError(f"{location}/issueId: expected stable issue ID")
        rationale = _nonempty_policy_string(accepted["rationale"], f"{location}/rationale")
        selector = _nonempty_policy_string(
            accepted["regressionSelector"], f"{location}/regressionSelector"
        )
        if not (
            selector.startswith("--test-")
            or selector.startswith("test_")
            or "::" in selector
        ):
            raise FirewallError(
                f"{location}/regressionSelector: expected owning regression selector"
            )
        affected_command = accepted["affectedCommand"]
        affected_command = _nonempty_policy_string(
            affected_command, f"{location}/affectedCommand"
        )
        workflow_id = _nonempty_policy_string(
            accepted["workflowId"], f"{location}/workflowId"
        )
        pointer = accepted["pointer"]
        tokens = _json_pointer_tokens(pointer, f"{location}/pointer")
        _reject_protected_pointer(tokens, f"{location}/pointer")
        if len(tokens) < 2 or tokens[0] != "semantics":
            raise FirewallError(
                f"{location}/pointer: accepted difference is not an exception-eligible journey field"
            )
        key = (workflow_id, pointer)
        if key in accepted_keys:
            raise FirewallError(f"{location}: duplicate accepted difference")
        accepted_keys.add(key)
        canonical_json_bytes(accepted["baselineValue"])
        canonical_json_bytes(accepted["candidateValue"])
        accepted_differences.append(
            AcceptedDifference(
                issue_id=issue_id,
                rationale=rationale,
                regression_selector=selector,
                affected_command=affected_command,
                workflow_id=workflow_id,
                pointer=pointer,
                baseline_value=accepted["baselineValue"],
                candidate_value=accepted["candidateValue"],
            )
        )
    return ValidatedPolicy(tuple(semantic_rules), tuple(accepted_differences))


def _value_at_pointer(document: Any, pointer: str) -> Any:
    value = document
    for token in _json_pointer_tokens(pointer, "policy pointer"):
        if isinstance(value, dict):
            if token not in value:
                return _MISSING
            value = value[token]
        elif isinstance(value, list):
            if not token.isdigit():
                return _MISSING
            index = int(token)
            if index >= len(value):
                return _MISSING
            value = value[index]
        else:
            return _MISSING
    return value


def _document_subject(document: Any) -> str:
    if isinstance(document, dict):
        value = document.get("id")
        if isinstance(value, str) and value:
            return value
    return "snapshot"


def _reject_unknown_candidate_data(expected: Any, actual: Any, pointer: str = "") -> None:
    if isinstance(expected, dict) and isinstance(actual, dict):
        for key in actual:
            child = f"{pointer}/{_json_pointer_token(str(key))}"
            if key not in expected:
                raise FirewallError(f"unknown field before comparison: {child}")
            _reject_unknown_candidate_data(expected[key], actual[key], child)
    elif isinstance(expected, list) and isinstance(actual, list):
        for index in range(min(len(expected), len(actual))):
            _reject_unknown_candidate_data(expected[index], actual[index], f"{pointer}/{index}")


def _validate_snapshot_for_comparison(document: dict[str, Any]) -> None:
    try:
        _validate_snapshot_document(document)
    except FirewallError as exc:
        match = re.fullmatch(r"(.+): unknown field ([^ ]+)", str(exc))
        if match is not None:
            location, field = match.groups()
            separator = "" if location.endswith("/") else "/"
            raise FirewallError(
                f"unknown field before comparison: {location}{separator}{field}"
            ) from exc
        raise


def _validate_comparison_discriminators(document: dict[str, Any]) -> None:
    if "formatVersion" in document and (
        document["formatVersion"] != 1
        or isinstance(document["formatVersion"], bool)
    ):
        raise FirewallError("/formatVersion: unsupported format version")
    if "target" in document and document["target"] not in {"source", "package"}:
        raise FirewallError("/target: unsupported snapshot target")
    if "id" in document and (
        not isinstance(document["id"], str) or not document["id"].strip()
    ):
        raise FirewallError("/id: invalid snapshot identity")


def _canonicalize_normalized_value(
    normalizer: str,
    value: Any,
    pointer: str,
) -> str:
    if not isinstance(value, str) or not value:
        raise FirewallError(f"semantic rule {pointer} requires a non-empty string")
    if normalizer == "temporary-root":
        path = PureWindowsPath(value)
        parts = path.parts
        approved_parent = PureWindowsPath(str(CAPTURE_WORKSPACE_PARENT.resolve()))
        parent_parts = approved_parent.parts
        parent_length = len(parent_parts)
        if (
            not path.is_absolute()
            or ".." in parts
            or len(parts) <= parent_length
            or tuple(part.casefold() for part in parts[:parent_length])
            != tuple(part.casefold() for part in parent_parts)
            or _OWNED_TEMP_ROOT.fullmatch(parts[parent_length]) is None
        ):
            raise FirewallError(
                f"semantic rule {pointer} requires an owned K-local temporary root"
            )
        relative_parts = parts[parent_length:]
        field = _json_pointer_tokens(pointer, "semantic rule pointer")[-1]
        if field == "workspaceRoot":
            if _OWNED_TEMP_ROOT.fullmatch(relative_parts[-1]) is None:
                raise FirewallError(
                    f"semantic rule {pointer} requires an owned root value"
                )
            return "<temporary-root>"
        if field == "outputRoot":
            workspace_index = max(
                (
                    index
                    for index, part in enumerate(relative_parts)
                    if _OWNED_TEMP_ROOT.fullmatch(part) is not None
                ),
                default=-1,
            )
            descendant = relative_parts[workspace_index + 1:]
            if workspace_index < 0 or not descendant:
                raise FirewallError(
                    f"semantic rule {pointer} requires a safe descendant value"
                )
            return str(PureWindowsPath("<temporary-root>", *descendant))
        raise FirewallError(
            f"semantic rule {pointer} has no temporary-root value shape"
        )
    if normalizer == "generated-timestamp":
        if _UTC_TIMESTAMP.fullmatch(value) is None:
            raise FirewallError(
                f"semantic rule {pointer} requires a UTC generated timestamp"
            )
        try:
            parsed = datetime.fromisoformat(value[:-1] + "+00:00")
        except ValueError as exc:
            raise FirewallError(
                f"semantic rule {pointer} requires a UTC generated timestamp"
            ) from exc
        if parsed.tzinfo is None or parsed.utcoffset() != timezone.utc.utcoffset(parsed):
            raise FirewallError(
                f"semantic rule {pointer} requires a UTC generated timestamp"
            )
        return "<generated-timestamp>"
    if normalizer == "operation-id":
        if _OPERATION_ID.fullmatch(value) is None:
            raise FirewallError(f"semantic rule {pointer} requires a valid operation ID")
        return "<operation-id>"
    raise FirewallError(f"unknown normalizer {normalizer!r}")


def _subject_for_pointer(expected: Any, actual: Any, pointer: str) -> str:
    parts = pointer.split("/")
    if len(parts) > 2 and parts[1] == "commands" and parts[2].isdigit():
        index = int(parts[2])
        for document in (expected, actual):
            if isinstance(document, dict):
                commands = document.get("commands")
                if isinstance(commands, list) and index < len(commands):
                    command = commands[index]
                    if isinstance(command, dict) and isinstance(command.get("name"), str):
                        return command["name"]
    subject = _document_subject(expected)
    return subject if subject != "snapshot" else _document_subject(actual)


def _leaf_differences(
    expected: Any,
    actual: Any,
    pointer: str,
    *,
    root_expected: Any,
    root_actual: Any,
    layer: str,
    gate: str,
    normalized: dict[str, tuple[Any, Any]],
) -> list[Difference]:
    if pointer in normalized:
        normalized_expected, normalized_actual = normalized[pointer]
        if _strict_json_equal(normalized_expected, normalized_actual):
            return []
    if expected is _MISSING:
        if isinstance(actual, dict) and actual:
            differences: list[Difference] = []
            for key, value in actual.items():
                differences.extend(
                    _leaf_differences(
                        _MISSING,
                        value,
                        f"{pointer}/{_json_pointer_token(str(key))}",
                        root_expected=root_expected,
                        root_actual=root_actual,
                        layer=layer,
                        gate=gate,
                        normalized=normalized,
                    )
                )
            return differences
        if isinstance(actual, list) and actual:
            differences = []
            for index, value in enumerate(actual):
                differences.extend(
                    _leaf_differences(
                        _MISSING,
                        value,
                        f"{pointer}/{index}",
                        root_expected=root_expected,
                        root_actual=root_actual,
                        layer=layer,
                        gate=gate,
                        normalized=normalized,
                    )
                )
            return differences
    if actual is _MISSING:
        if isinstance(expected, dict) and expected:
            differences = []
            for key, value in expected.items():
                differences.extend(
                    _leaf_differences(
                        value,
                        _MISSING,
                        f"{pointer}/{_json_pointer_token(str(key))}",
                        root_expected=root_expected,
                        root_actual=root_actual,
                        layer=layer,
                        gate=gate,
                        normalized=normalized,
                    )
                )
            return differences
        if isinstance(expected, list) and expected:
            differences = []
            for index, value in enumerate(expected):
                differences.extend(
                    _leaf_differences(
                        value,
                        _MISSING,
                        f"{pointer}/{index}",
                        root_expected=root_expected,
                        root_actual=root_actual,
                        layer=layer,
                        gate=gate,
                        normalized=normalized,
                    )
                )
            return differences
    if type(expected) is type(actual) and isinstance(expected, dict):
        differences = []
        for key in expected:
            child = f"{pointer}/{_json_pointer_token(str(key))}"
            differences.extend(
                _leaf_differences(
                    expected[key],
                    actual.get(key, _MISSING),
                    child,
                    root_expected=root_expected,
                    root_actual=root_actual,
                    layer=layer,
                    gate=gate,
                    normalized=normalized,
                )
            )
        return differences
    if type(expected) is type(actual) and isinstance(expected, list):
        differences = []
        for index in range(max(len(expected), len(actual))):
            differences.extend(
                _leaf_differences(
                    expected[index] if index < len(expected) else _MISSING,
                    actual[index] if index < len(actual) else _MISSING,
                    f"{pointer}/{index}",
                    root_expected=root_expected,
                    root_actual=root_actual,
                    layer=layer,
                    gate=gate,
                    normalized=normalized,
                )
            )
        return differences
    if (
        expected is not _MISSING
        and actual is not _MISSING
        and _strict_json_equal(expected, actual)
    ):
        return []
    return [
        Difference(
            layer=layer,
            subject=_subject_for_pointer(root_expected, root_actual, pointer),
            pointer=pointer or "/",
            expected=(dict(_MISSING_EVIDENCE) if expected is _MISSING else expected),
            actual=(dict(_MISSING_EVIDENCE) if actual is _MISSING else actual),
            gate=gate,
        )
    ]


class ExactComparator:
    def __init__(
        self,
        policy: ValidatedPolicy,
        *,
        layer: str = "baseline-candidate",
        gate: str = "exact-comparison",
    ) -> None:
        self.policy = policy
        self.layer = layer
        self.gate = gate
        self.accepted_differences: tuple[AcceptedDifference, ...] = ()

    def compare(self, baseline: dict[str, Any], candidate: dict[str, Any]) -> list[Difference]:
        baseline = _require_object(baseline, "baseline snapshot")
        candidate = _require_object(candidate, "candidate snapshot")
        _validate_comparison_discriminators(baseline)
        _validate_comparison_discriminators(candidate)
        if "$schema" in baseline or "$schema" in candidate:
            _validate_snapshot_for_comparison(baseline)
            _validate_snapshot_for_comparison(candidate)
        baseline_subject = _document_subject(baseline)
        candidate_subject = _document_subject(candidate)
        normalized: dict[str, tuple[Any, Any]] = {}
        for rule in self.policy.semantic_rules:
            if baseline_subject != rule.workflow_id or candidate_subject != rule.workflow_id:
                raise FirewallError(
                    f"unused semantic rule {rule.workflow_id}{rule.pointer}"
                )
            expected = _value_at_pointer(baseline, rule.pointer)
            actual = _value_at_pointer(candidate, rule.pointer)
            if expected is _MISSING or actual is _MISSING:
                raise FirewallError(
                    f"semantic rule requires both snapshots to contain {rule.pointer}"
                )
            if isinstance(expected, (dict, list)) or isinstance(actual, (dict, list)):
                raise FirewallError(
                    f"semantic rule {rule.pointer} must target one parsed leaf field"
                )
            normalized[rule.pointer] = (
                _canonicalize_normalized_value(rule.normalizer, expected, rule.pointer),
                _canonicalize_normalized_value(rule.normalizer, actual, rule.pointer),
            )

        _reject_unknown_candidate_data(baseline, candidate)
        differences = _leaf_differences(
            baseline,
            candidate,
            "",
            root_expected=baseline,
            root_actual=candidate,
            layer=self.layer,
            gate=self.gate,
            normalized=normalized,
        )

        accepted: list[AcceptedDifference] = []
        remaining = list(differences)
        for rule in self.policy.accepted_differences:
            if baseline_subject != rule.workflow_id or candidate_subject != rule.workflow_id:
                raise FirewallError(
                    f"unused accepted difference {rule.issue_id} at {rule.pointer}"
                )
            expected = _value_at_pointer(baseline, rule.pointer)
            actual = _value_at_pointer(candidate, rule.pointer)
            if expected is _MISSING or actual is _MISSING:
                raise FirewallError(
                    f"accepted difference requires both snapshots to contain {rule.pointer}"
                )
            if isinstance(expected, (dict, list)) or isinstance(actual, (dict, list)):
                raise FirewallError(
                    f"accepted difference {rule.pointer} must target one parsed leaf field"
                )
            match = next(
                (
                    difference
                    for difference in remaining
                    if difference.subject == rule.workflow_id
                    and difference.pointer == rule.pointer
                    and _strict_json_equal(
                        difference.expected, rule.baseline_value
                    )
                    and _strict_json_equal(
                        difference.actual, rule.candidate_value
                    )
                ),
                None,
            )
            if match is None:
                raise FirewallError(
                    f"unused accepted difference {rule.issue_id} at {rule.pointer}"
                )
            remaining.remove(match)
            accepted.append(rule)
        self.accepted_differences = tuple(accepted)
        return remaining


def compare_snapshots(
    baseline: dict[str, Any],
    candidate: dict[str, Any],
    policy: dict[str, Any],
) -> list[Difference]:
    """Compare two parsed snapshots exactly under one validated policy."""

    layer = (
        "source-package"
        if baseline.get("target") == "source" and candidate.get("target") == "package"
        else "baseline-candidate"
    )
    return ExactComparator(ValidatedPolicy.parse(policy), layer=layer).compare(
        baseline, candidate
    )


def _first_json_difference(
    expected: Any,
    actual: Any,
    pointer: str,
) -> tuple[str, Any, Any] | None:
    if type(expected) is not type(actual):
        return pointer or "/", expected, actual
    if isinstance(expected, dict):
        ordered_keys = list(expected)
        for key in actual:
            if key not in expected:
                ordered_keys.append(key)
        for key in ordered_keys:
            child = f"{pointer}/{_json_pointer_token(str(key))}"
            if key not in expected:
                return child, None, actual[key]
            if key not in actual:
                return child, expected[key], None
            difference = _first_json_difference(expected[key], actual[key], child)
            if difference is not None:
                return difference
        return None
    if isinstance(expected, list):
        limit = max(len(expected), len(actual))
        for index in range(limit):
            child = f"{pointer}/{index}"
            if index >= len(expected):
                return child, None, actual[index]
            if index >= len(actual):
                return child, expected[index], None
            difference = _first_json_difference(expected[index], actual[index], child)
            if difference is not None:
                return difference
        return None
    if expected != actual:
        return pointer or "/", expected, actual
    return None


def find_capture_disagreement(
    source: dict[str, Any],
    package: dict[str, Any],
) -> dict[str, Any] | None:
    """Return the first focused source/package capture difference.

    Task 3 owns the general compatibility comparator.  This helper exists only
    to prove that a captured command disagreement retains its full pointer and
    both canonical values/hashes.
    """

    source_commands = source.get("commands")
    package_commands = package.get("commands")
    difference = _first_json_difference(
        source_commands,
        package_commands,
        "/commands",
    )
    if difference is None:
        return None
    pointer, expected, actual = difference
    command: str | None = None
    match = pointer.split("/")
    if len(match) > 2 and match[1] == "commands" and match[2].isdigit():
        index = int(match[2])
        commands = source.get("commands", [])
        if index < len(commands) and isinstance(commands[index], dict):
            candidate = commands[index].get("name")
            if isinstance(candidate, str):
                command = candidate
    return {
        "layer": "source-package",
        "command": command,
        "pointer": pointer,
        "expected": expected,
        "actual": actual,
        "expectedSha256": _sha256(canonical_json_bytes(expected)).upper(),
        "actualSha256": _sha256(canonical_json_bytes(actual)).upper(),
    }


def _load_json(path: Path) -> Any:
    try:
        with path.open("r", encoding="utf-8-sig") as stream:
            return json.load(
                stream,
                object_pairs_hook=_object_without_duplicates,
                parse_constant=_reject_json_numeric_constant,
            )
    except FirewallError:
        raise
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise FirewallError(f"cannot parse JSON {path}: {exc}") from exc


def _require_object(value: Any, location: str) -> dict[str, Any]:
    if not isinstance(value, dict):
        raise FirewallError(f"{location}: expected object")
    return value


def _require_array(value: Any, location: str) -> list[Any]:
    if not isinstance(value, list):
        raise FirewallError(f"{location}: expected array")
    return value


def _require_string(value: Any, location: str) -> str:
    if not isinstance(value, str) or not value:
        raise FirewallError(f"{location}: expected non-empty string")
    return value


def _require_boolean(value: Any, location: str) -> bool:
    if not isinstance(value, bool):
        raise FirewallError(f"{location}: expected boolean")
    return value


def _require_integer(value: Any, location: str, *, minimum: int = 0) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
        raise FirewallError(f"{location}: expected integer >= {minimum}")
    return value


def _require_closed(
    value: dict[str, Any],
    location: str,
    *,
    required: set[str],
    optional: set[str] | None = None,
) -> None:
    optional = optional or set()
    unknown = sorted(set(value) - required - optional)
    if unknown:
        raise FirewallError(f"{location}: unknown field {unknown[0]}")
    missing = sorted(required - set(value))
    if missing:
        raise FirewallError(f"{location}: missing field {missing[0]}")


def _require_hex(value: Any, location: str, length: int) -> str:
    text = _require_string(value, location)
    if len(text) != length or any(character not in "0123456789abcdefABCDEF" for character in text):
        raise FirewallError(f"{location}: expected {length}-character hexadecimal value")
    return text


def _require_string_array(value: Any, location: str) -> list[str]:
    items = _require_array(value, location)
    for index, item in enumerate(items):
        _require_string(item, f"{location}/{index}")
    return items


def _validate_file_identity(value: Any, location: str) -> None:
    identity = _require_object(value, location)
    _require_closed(identity, location, required={"length", "sha256"})
    _require_integer(identity["length"], f"{location}/length")
    _require_hex(identity["sha256"], f"{location}/sha256", _SHA256_LENGTH)


def _validate_provenance(value: Any) -> None:
    location = "/provenance"
    provenance = _require_object(value, location)
    required = {
        "productVersion",
        "sourceLine",
        "sourceTag",
        "sourceCommit",
        "sourceTree",
        "releaseZip",
        "capabilitiesSha256",
        "candidateBundleManifestSha256",
        "approvedBundleManifestSha256",
    }
    _require_closed(provenance, location, required=required)
    for key in ("productVersion", "sourceLine", "sourceTag"):
        _require_string(provenance[key], f"{location}/{key}")
    for key in ("sourceCommit", "sourceTree"):
        _require_hex(provenance[key], f"{location}/{key}", _GIT_OBJECT_LENGTH)
    _validate_file_identity(provenance["releaseZip"], f"{location}/releaseZip")
    for key in (
        "capabilitiesSha256",
        "candidateBundleManifestSha256",
        "approvedBundleManifestSha256",
    ):
        _require_hex(provenance[key], f"{location}/{key}", _SHA256_LENGTH)


def _validate_source_closure(value: Any, location: str, expected_root: str) -> None:
    closure = _require_object(value, location)
    _require_closed(
        closure, location,
        required={"root", "tree", "fileCount", "sha256"},
    )
    if _require_string(closure["root"], f"{location}/root") != expected_root:
        raise FirewallError(f"{location}/root: source closure root mismatch")
    _require_hex(closure["tree"], f"{location}/tree", _GIT_OBJECT_LENGTH)
    _require_integer(closure["fileCount"], f"{location}/fileCount", minimum=1)
    _require_hex(closure["sha256"], f"{location}/sha256", _SHA256_LENGTH)


def _validate_selector_build_evidence(value: Any, location: str) -> None:
    rows = _require_string_array(value, location)
    expected_projects = ("NpcManager.Cli.Tests", "NpcManager.Architecture.Tests")
    if len(rows) != len(expected_projects) * 4:
        raise FirewallError(f"{location}: selector build evidence row count mismatch")
    for index, project in enumerate(expected_projects):
        offset = index * 4
        if rows[offset:offset + 2] != [f"buildProject={project}", "buildExit=0"]:
            raise FirewallError(f"{location}: selector build identity mismatch")
        for label, row in zip(
            ("buildStdoutBase64=", "buildStderrBase64="),
            rows[offset + 2:offset + 4],
            strict=True,
        ):
            if not row.startswith(label):
                raise FirewallError(f"{location}: selector build log identity missing")
            try:
                base64.b64decode(row.removeprefix(label), validate=True)
            except (ValueError, binascii.Error) as exc:
                raise FirewallError(f"{location}: selector build log is malformed") from exc


def _validate_synthetic_package_build(value: Any, location: str, configuration: str) -> None:
    build = _require_object(value, location)
    _require_closed(
        build, location,
        required={
            "configuration", "exitCode", "command", "powershell", "packageScript",
            "python", "stdout", "stderr",
        },
    )
    if build["configuration"] != configuration:
        raise FirewallError(f"{location}/configuration: candidate build configuration mismatch")
    if _require_integer(build["exitCode"], f"{location}/exitCode") != 0:
        raise FirewallError(f"{location}/exitCode: package build did not succeed")
    powershell = _require_object(build["powershell"], f"{location}/powershell")
    executable = _require_string(powershell.get("name"), f"{location}/powershell/name")
    if executable.casefold() not in {"pwsh.exe", "powershell.exe"}:
        raise FirewallError(f"{location}/powershell/name: unsupported executable")
    expected_command = [
        executable, "-NoProfile", "-NonInteractive", "-File",
        "tools/build/package.ps1", "-Configuration", configuration,
        "-Runtime", "win-x64", "-DotNetPath", "<pinned-dotnet>",
        "-PythonPath", "<python>", "-OutputRoot", "<candidate-output>",
    ]
    command = _require_string_array(build["command"], f"{location}/command")
    if command != expected_command:
        raise FirewallError(f"{location}/command: package invocation differs from the capture contract")
    for key in ("powershell", "packageScript", "python", "stdout", "stderr"):
        identity_location = f"{location}/{key}"
        identity = _require_object(build[key], identity_location)
        if key == "powershell":
            _require_closed(identity, identity_location, required={"name", "length", "sha256"})
            _require_string(identity["name"], f"{identity_location}/name")
            _validate_file_identity(
                {"length": identity["length"], "sha256": identity["sha256"]},
                identity_location,
            )
        else:
            _validate_file_identity(identity, identity_location)
        if identity["length"] <= 0 and key not in {"stderr"}:
            raise FirewallError(f"{identity_location}/length: must be positive")


def _validate_synthetic_provenance(value: Any) -> None:
    location = "/provenance"
    provenance = _require_object(value, location)
    _require_closed(
        provenance, location,
        required={
            "kind", "productVersion", "sourceLine", "source", "candidate",
            "harness", "pinnedDotnet",
        },
    )
    if provenance["kind"] != "public-synthetic":
        raise FirewallError(f"{location}/kind: unsupported synthetic baseline kind")
    for key in ("productVersion", "sourceLine"):
        _require_string(provenance[key], f"{location}/{key}")

    source = _require_object(provenance["source"], f"{location}/source")
    _require_closed(
        source, f"{location}/source",
        required={
            "configuration", "commit", "tree", "sourceClosure", "cli",
            "managedAssembly", "selectorBuildEvidence",
        },
    )
    if source["configuration"] != "Release":
        raise FirewallError(f"{location}/source/configuration: expected Release")
    source_commit = _require_hex(
        source["commit"], f"{location}/source/commit", _GIT_OBJECT_LENGTH
    )
    source_tree = _require_hex(
        source["tree"], f"{location}/source/tree", _GIT_OBJECT_LENGTH
    )
    _validate_source_closure(source["sourceClosure"], f"{location}/source/sourceClosure", "src")
    _validate_file_identity(source["cli"], f"{location}/source/cli")
    if source["cli"]["length"] == 0:
        raise FirewallError(f"{location}/source/cli/length: must be positive")
    _validate_file_identity(source["managedAssembly"], f"{location}/source/managedAssembly")
    if source["managedAssembly"]["length"] == 0:
        raise FirewallError(f"{location}/source/managedAssembly/length: must be positive")
    _validate_selector_build_evidence(
        source["selectorBuildEvidence"], f"{location}/source/selectorBuildEvidence"
    )

    candidate = _require_object(provenance["candidate"], f"{location}/candidate")
    _require_closed(
        candidate, f"{location}/candidate",
        required={
            "configuration", "privateOnly", "runtimeAuthority", "manifest", "cli",
            "packageBuild",
        },
    )
    if candidate["configuration"] not in {"Debug", "Release"}:
        raise FirewallError(f"{location}/candidate/configuration: unsupported build configuration")
    if _require_boolean(candidate["privateOnly"], f"{location}/candidate/privateOnly") is not True:
        raise FirewallError(f"{location}/candidate/privateOnly: must remain true")
    if _require_boolean(candidate["runtimeAuthority"], f"{location}/candidate/runtimeAuthority") is not False:
        raise FirewallError(f"{location}/candidate/runtimeAuthority: must remain false")
    for key in ("manifest", "cli"):
        _validate_file_identity(candidate[key], f"{location}/candidate/{key}")
        if candidate[key]["length"] == 0:
            raise FirewallError(f"{location}/candidate/{key}/length: must be positive")
    _validate_synthetic_package_build(
        candidate["packageBuild"], f"{location}/candidate/packageBuild",
        candidate["configuration"],
    )

    harness = _require_object(provenance["harness"], f"{location}/harness")
    _require_closed(
        harness, f"{location}/harness",
        required={"commit", "tree", "sourceClosure", "runner"},
    )
    harness_commit = _require_hex(
        harness["commit"], f"{location}/harness/commit", _GIT_OBJECT_LENGTH
    )
    harness_tree = _require_hex(
        harness["tree"], f"{location}/harness/tree", _GIT_OBJECT_LENGTH
    )
    _validate_source_closure(
        harness["sourceClosure"], f"{location}/harness/sourceClosure",
        "tests/NpcManager.Cli.Tests",
    )
    _validate_file_identity(harness["runner"], f"{location}/harness/runner")
    if harness["runner"]["length"] == 0:
        raise FirewallError(f"{location}/harness/runner/length: must be positive")
    if source_commit.casefold() != harness_commit.casefold() or source_tree.casefold() != harness_tree.casefold():
        raise FirewallError(f"{location}: source and harness Git identities differ")

    _validate_file_identity(provenance["pinnedDotnet"], f"{location}/pinnedDotnet")
    if provenance["pinnedDotnet"]["length"] == 0:
        raise FirewallError(f"{location}/pinnedDotnet/length: must be positive")


def _validate_options(value: Any, command_location: str) -> None:
    options = _require_array(value, f"{command_location}/options")
    required = {
        "name",
        "tokens",
        "valueKind",
        "arity",
        "required",
        "repeatable",
        "hasDefault",
        "defaultValue",
        "allowedValues",
        "caseSensitive",
        "requires",
        "excludes",
        "diagnostics",
    }
    for index, raw_option in enumerate(options):
        location = f"{command_location}/options/{index}"
        option = _require_object(raw_option, location)
        _require_closed(option, location, required=required)
        for key in ("name", "valueKind", "arity"):
            _require_string(option[key], f"{location}/{key}")
        for key in ("tokens", "requires", "excludes"):
            _require_string_array(option[key], f"{location}/{key}")
        for key in ("required", "repeatable", "hasDefault", "caseSensitive"):
            _require_boolean(option[key], f"{location}/{key}")
        _require_array(option["allowedValues"], f"{location}/allowedValues")
        diagnostics = _require_object(option["diagnostics"], f"{location}/diagnostics")
        diagnostic_keys = {"missing", "invalid", "duplicate", "unknown"}
        _require_closed(
            diagnostics, f"{location}/diagnostics", required=diagnostic_keys
        )
        for key in diagnostic_keys:
            if not isinstance(diagnostics[key], str):
                raise FirewallError(
                    f"{location}/diagnostics/{key}: expected string"
                )


def _validate_commands(value: Any) -> list[str]:
    commands = _require_array(value, "/commands")
    if not commands:
        raise FirewallError("/commands: expected at least one command")
    names: list[str] = []
    required = {
        "position",
        "name",
        "pathTokens",
        "aliases",
        "options",
        "help",
        "protocol1",
        "protocol2Readiness",
        "schemas",
        "authority",
        "effects",
        "recovery",
        "noInputProbe",
    }
    for index, raw_command in enumerate(commands):
        location = f"/commands/{index}"
        command = _require_object(raw_command, location)
        _require_closed(command, location, required=required)
        position = _require_integer(command["position"], f"{location}/position")
        if position != index:
            raise FirewallError(
                f"{location}: command position {position} does not equal {index}"
            )
        name = _require_string(command["name"], f"{location}/name")
        if name in names:
            raise FirewallError(f"{location}/name: duplicate command {name}")
        names.append(name)
        path_tokens = _require_string_array(
            command["pathTokens"], f"{location}/pathTokens"
        )
        if path_tokens != name.split():
            raise FirewallError(f"{location}/pathTokens: does not match command name")
        _require_string_array(command["aliases"], f"{location}/aliases")
        _validate_options(command["options"], location)
        help_identity = _require_object(command["help"], f"{location}/help")
        _require_closed(help_identity, f"{location}/help", required={"sha256"})
        _require_hex(
            help_identity["sha256"], f"{location}/help/sha256", _SHA256_LENGTH
        )
        _require_boolean(command["protocol1"], f"{location}/protocol1")
        if command["protocol2Readiness"] not in {"v2", "legacy"}:
            raise FirewallError(f"{location}/protocol2Readiness: unknown value")
        schemas = _require_array(command["schemas"], f"{location}/schemas")
        for schema_index, raw_schema in enumerate(schemas):
            schema_location = f"{location}/schemas/{schema_index}"
            schema = _require_object(raw_schema, schema_location)
            _require_closed(schema, schema_location, required={"role", "id", "sha256"})
            _require_string(schema["role"], f"{schema_location}/role")
            _require_string(schema["id"], f"{schema_location}/id")
            _require_hex(schema["sha256"], f"{schema_location}/sha256", _SHA256_LENGTH)
        for key in ("authority", "effects", "recovery"):
            _require_string_array(command[key], f"{location}/{key}")
        probe = _require_object(command["noInputProbe"], f"{location}/noInputProbe")
        _require_closed(
            probe,
            f"{location}/noInputProbe",
            required={"resultClass", "exitMeaning"},
        )
        _require_string(probe["resultClass"], f"{location}/noInputProbe/resultClass")
        _require_string(probe["exitMeaning"], f"{location}/noInputProbe/exitMeaning")
    return names


def _validate_members(value: Any) -> None:
    members = _require_array(value, "/members")
    seen_ids: set[str] = set()
    seen_paths: set[str] = set()
    for index, raw_member in enumerate(members):
        location = f"/members/{index}"
        member = _require_object(raw_member, location)
        _require_closed(
            member,
            location,
            required={"id", "kind", "path", "length", "sha256"},
        )
        member_id = _require_string(member["id"], f"{location}/id")
        if member_id in seen_ids:
            raise FirewallError(f"{location}/id: duplicate member {member_id}")
        seen_ids.add(member_id)
        if member["kind"] not in {"journey", "evidence"}:
            raise FirewallError(f"{location}/kind: unknown value")
        member_path = _require_string(member["path"], f"{location}/path")
        if member_path in seen_paths:
            raise FirewallError(f"{location}/path: duplicate member path")
        seen_paths.add(member_path)
        _require_integer(member["length"], f"{location}/length")
        _require_hex(member["sha256"], f"{location}/sha256", _SHA256_LENGTH)


def _validate_exit_meanings(value: Any) -> None:
    exit_meanings = _require_array(value, "/exitMeanings")
    observed: list[tuple[int, str]] = []
    for index, raw_exit_meaning in enumerate(exit_meanings):
        location = f"/exitMeanings/{index}"
        exit_meaning = _require_object(raw_exit_meaning, location)
        _require_closed(
            exit_meaning, location, required={"code", "meaning"}
        )
        code = _require_integer(exit_meaning["code"], f"{location}/code")
        meaning = _require_string(
            exit_meaning["meaning"], f"{location}/meaning"
        )
        observed.append((code, meaning))
    if tuple(observed) != EXIT_MEANINGS:
        raise FirewallError("/exitMeanings: exit meanings mismatch")


def _validate_package_contract(snapshot: dict[str, Any]) -> None:
    package_paths = _require_array(
        snapshot["requiredPackagePaths"], "/requiredPackagePaths"
    )
    launcher_rows: list[dict[str, Any]] = []
    for index, raw_path in enumerate(package_paths):
        location = f"/requiredPackagePaths/{index}"
        package_path = _require_object(raw_path, location)
        _require_closed(package_path, location, required={"path", "role"})
        _require_string(package_path["path"], f"{location}/path")
        role = _require_string(package_path["role"], f"{location}/role")
        if role == "launcher":
            launcher_rows.append(package_path)
    if len(launcher_rows) != 1:
        raise FirewallError("/requiredPackagePaths: exactly one launcher is required")
    launcher = _require_object(snapshot["launcher"], "/launcher")
    _require_closed(launcher, "/launcher", required={"entrypoint", "shape"})
    entrypoint = _require_string(launcher["entrypoint"], "/launcher/entrypoint")
    if entrypoint != launcher_rows[0]["path"]:
        raise FirewallError("/launcher/entrypoint: launcher path mismatch")
    if launcher["shape"] != "powershell-forwarding-wrapper":
        raise FirewallError("/launcher/shape: launcher shape mismatch")
    members = _require_array(snapshot["members"], "/members")
    matches = [
        member for member in members
        if isinstance(member, dict)
        and member.get("id") == "package.launcher"
        and member.get("kind") == "evidence"
        and member.get("path") == entrypoint
    ]
    if len(matches) != 1:
        raise FirewallError("/launcher: launcher path lacks one authenticated member")


def _validate_snapshot_document(value: Any) -> dict[str, Any]:
    snapshot = _require_object(value, "/")
    required = {
        "$schema",
        "formatVersion",
        "provenance",
        "commandCount",
        "orderedCommandNamesSha256",
        "commands",
        "exitMeanings",
        "voiceDialogueSentinels",
        "members",
        "requiredPackagePaths",
        "launcher",
    }
    _require_closed(snapshot, "/", required=required)
    if snapshot["$schema"] == SNAPSHOT_SCHEMA_ID and snapshot["formatVersion"] == 1:
        _validate_provenance(snapshot["provenance"])
    elif (
        snapshot["$schema"] == SYNTHETIC_SNAPSHOT_SCHEMA_ID
        and snapshot["formatVersion"] == 2
    ):
        _validate_synthetic_provenance(snapshot["provenance"])
    else:
        raise FirewallError("/$schema: unsupported snapshot schema or format version")
    if isinstance(snapshot["formatVersion"], bool):
        raise FirewallError("/formatVersion: unsupported format version")
    names = _validate_commands(snapshot["commands"])
    command_count = _require_integer(snapshot["commandCount"], "/commandCount", minimum=1)
    if command_count != len(names):
        raise FirewallError("/commandCount: does not match commands")
    ordered_hash = hashlib.sha256("\n".join(names).encode("utf-8")).hexdigest()
    declared_hash = _require_hex(
        snapshot["orderedCommandNamesSha256"],
        "/orderedCommandNamesSha256",
        _SHA256_LENGTH,
    )
    if declared_hash.casefold() != ordered_hash:
        raise FirewallError("/orderedCommandNamesSha256: ordered command hash mismatch")
    _validate_exit_meanings(snapshot["exitMeanings"])
    sentinels = _require_string_array(
        snapshot["voiceDialogueSentinels"], "/voiceDialogueSentinels"
    )
    if tuple(sentinels) != VOICE_DIALOGUE_SENTINELS:
        raise FirewallError("/voiceDialogueSentinels: voice/dialogue sentinels mismatch")
    if not set(VOICE_DIALOGUE_SENTINELS).issubset(names):
        raise FirewallError("/commands: voice/dialogue sentinels missing")
    _validate_members(snapshot["members"])
    _validate_package_contract(snapshot)
    return snapshot


def validate_snapshot(path: str | Path) -> dict[str, Any]:
    """Parse and strictly validate a compatibility snapshot."""

    return _validate_snapshot_document(_load_json(Path(path)))


def _validate_registry(value: Any) -> list[dict[str, Any]]:
    registry = _require_object(value, "/")
    _require_closed(registry, "/", required={"formatVersion", "baselines"})
    if registry["formatVersion"] != 1 or isinstance(registry["formatVersion"], bool):
        raise FirewallError("/formatVersion: unsupported format version")
    entries = _require_array(registry["baselines"], "/baselines")
    seen: set[str] = set()
    for index, raw_entry in enumerate(entries):
        location = f"/baselines/{index}"
        entry = _require_object(raw_entry, location)
        _require_closed(
            entry,
            location,
            required={"id", "manifestPath", "manifestLength", "manifestSha256"},
        )
        baseline_id = _require_string(entry["id"], f"{location}/id")
        if baseline_id in seen:
            raise FirewallError(f"{location}/id: duplicate baseline {baseline_id}")
        seen.add(baseline_id)
        _require_string(entry["manifestPath"], f"{location}/manifestPath")
        _require_integer(entry["manifestLength"], f"{location}/manifestLength")
        _require_hex(
            entry["manifestSha256"],
            f"{location}/manifestSha256",
            _SHA256_LENGTH,
        )
    return entries


def _resolved_member(root: Path, relative: str, location: str) -> Path:
    relative_path = Path(relative)
    if relative_path.is_absolute():
        raise FirewallError(f"{location}: absolute path is forbidden")
    resolved_root = root.resolve()
    resolved = (resolved_root / relative_path).resolve()
    if resolved != resolved_root and resolved_root not in resolved.parents:
        raise FirewallError(f"{location}: path escapes bundle root")
    return resolved


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _sha256_stream(stream: Any) -> str:
    return hashlib.file_digest(stream, "sha256").hexdigest()


def _sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return _sha256_stream(stream)


def _sha256_file_and_length(path: Path) -> tuple[int, str]:
    digest = hashlib.sha256()
    length = 0
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            length += len(chunk)
            digest.update(chunk)
    return length, digest.hexdigest()


def _authenticated_bytes(
    path: Path,
    *,
    expected_length: int,
    expected_sha256: str,
    subject: str,
) -> bytes:
    try:
        data = path.read_bytes()
    except OSError as exc:
        raise FirewallError(f"{subject} missing: {path}") from exc
    if _sha256(data).casefold() != expected_sha256.casefold():
        if subject.startswith("member "):
            raise FirewallError(f"member sha256 mismatch ({subject[7:]})")
        raise FirewallError(f"{subject} sha256 mismatch")
    if len(data) != expected_length:
        if subject.startswith("member "):
            raise FirewallError(f"member length mismatch ({subject[7:]})")
        raise FirewallError(f"{subject} length mismatch")
    return data


def load_baseline(
    baseline_id: str,
    *,
    registry_path: str | Path = DEFAULT_BASELINE_REGISTRY,
    bundle_root: str | Path | None = None,
) -> Baseline:
    """Authenticate and load a baseline from its repository-pinned registry.

    Tests may supply explicit temporary ``registry_path`` and ``bundle_root``
    values.  Production callers that omit them remain pinned to repository
    locations; no mutable module-level override exists.
    """

    registry = Path(registry_path)
    entries = _validate_registry(_load_json(registry))
    entry = next((item for item in entries if item["id"] == baseline_id), None)
    if entry is None:
        raise FirewallError(f"unknown baseline: {baseline_id}")

    root = (
        Path(bundle_root)
        if bundle_root is not None
        else DEFAULT_BASELINE_ROOT / baseline_id
    ).resolve()
    manifest_path = _resolved_member(root, entry["manifestPath"], "/manifestPath")
    manifest_bytes = _authenticated_bytes(
        manifest_path,
        expected_length=entry["manifestLength"],
        expected_sha256=entry["manifestSha256"],
        subject="baseline manifest",
    )
    try:
        snapshot_value = json.loads(
            manifest_bytes.decode("utf-8"),
            object_pairs_hook=_object_without_duplicates,
            parse_constant=_reject_json_numeric_constant,
        )
    except FirewallError:
        raise
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise FirewallError(f"cannot parse JSON {manifest_path}: {exc}") from exc
    snapshot = _validate_snapshot_document(snapshot_value)
    if baseline_id == "publicSynthetic" and snapshot["formatVersion"] != 2:
        raise FirewallError("publicSynthetic baseline must use the v2 public-synthetic schema")
    if baseline_id == "preview275" and snapshot["formatVersion"] != 1:
        raise FirewallError("preview275 baseline must retain its v1 sealed-release schema")

    member_paths: list[Path] = []
    for index, member in enumerate(snapshot["members"]):
        member_path = _resolved_member(root, member["path"], f"/members/{index}/path")
        _authenticated_bytes(
            member_path,
            expected_length=member["length"],
            expected_sha256=member["sha256"],
            subject=f"member {member['id']}",
        )
        member_paths.append(member_path)

    return Baseline(
        baseline_id=baseline_id,
        manifest_path=manifest_path,
        snapshot=snapshot,
        member_paths=tuple(member_paths),
    )


def _schema_identity_values(value: Any) -> list[str]:
    values: set[str] = set()

    def visit(node: Any, key: str = "") -> None:
        if isinstance(node, dict):
            for child_key, child in node.items():
                visit(child, child_key)
        elif isinstance(node, list):
            for child in node:
                visit(child, key)
        elif isinstance(node, str) and "schema" in key.casefold() and node:
            values.add(node)

    visit(value)
    return sorted(values)


def _project_manifest_command(
    command: dict[str, Any],
    behavior_facts: list[dict[str, Any]],
) -> dict[str, Any]:
    contract = _require_object(command["protocol2Contract"], "command contract")
    options: list[dict[str, Any]] = []
    for raw_option in _require_array(contract.get("options"), "command options"):
        option = _require_object(raw_option, "command option")
        name = _require_string(option.get("cliName"), "command option/cliName")
        observed = {
            fact["field"]: fact["value"]
            for fact in behavior_facts
            if fact["command"] == command["name"] and fact["option"] == name
        }
        options.append(
            {
                "name": name,
                "tokens": [f"--{name}"],
                "valueKind": _require_string(option.get("valueKind"), "command option/valueKind"),
                "arity": _require_string(option.get("valueSyntax"), "command option/valueSyntax"),
                "required": bool(option.get("required")),
                "repeatable": bool(observed.get("repeatable", False)),
                "hasDefault": "defaultValue" in observed,
                "defaultValue": observed.get("defaultValue"),
                "allowedValues": _require_array(option.get("allowedValues"), "command option/allowedValues"),
                "caseSensitive": bool(observed.get("caseSensitive", True)),
                "requires": [],
                "excludes": _require_string_array(option.get("conflictsWith"), "command option/conflictsWith"),
                "diagnostics": {
                    "missing": str(observed.get("missingValueDiagnostic", "option-value-required")),
                    "invalid": str(observed.get("invalidDiagnostic", "option-invalid")),
                    "duplicate": "option-duplicate",
                    "unknown": "option-unknown",
                },
            }
        )
    schema_export = _require_object(command["schemaExport"], "command schema export")
    schema_hash = _require_hex(schema_export["sha256"], "command schema export/sha256", _SHA256_LENGTH)
    schema_ids = _schema_identity_values(schema_export["document"])
    return {
        "position": command["position"],
        "name": command["name"],
        "pathTokens": command["pathTokens"],
        "aliases": command["aliases"],
        "options": options,
        "help": {"sha256": command["help"]["sha256"]},
        "protocol1": True,
        "protocol2Readiness": contract["readiness"],
        "schemas": [
            {"role": "schema-export", "id": schema_id, "sha256": schema_hash}
            for schema_id in schema_ids
        ],
        "authority": [
            canonical_json_bytes(row).decode("utf-8").strip()
            for row in _require_array(contract.get("authority"), "command authority")
        ],
        "effects": [
            canonical_json_bytes(row).decode("utf-8").strip()
            for row in _require_array(contract.get("effects"), "command effects")
        ],
        "recovery": [
            f"retryPolicy={contract.get('retryPolicy')}",
            f"determinism={contract.get('determinism')}",
            *[
                canonical_json_bytes(row).decode("utf-8").strip()
                for row in _require_array(contract.get("transitions"), "command transitions")
            ],
        ],
        "noInputProbe": {
            "resultClass": command["noInputProbe"]["resultClass"],
            "exitMeaning": command["noInputProbe"]["exitMeaning"],
        },
    }


def _find_baseline_projection_disagreement(
    source: dict[str, Any],
    package: dict[str, Any],
) -> dict[str, Any] | None:
    """Compare exactly the authenticated command fields retained by the baseline."""

    source_facts = _require_array(source["behaviorFacts"], "source behavior facts")
    package_facts = _require_array(package["behaviorFacts"], "package behavior facts")
    source_commands = [
        _project_manifest_command(_require_object(command, "source command"), source_facts)
        for command in _require_array(source["commands"], "source commands")
    ]
    package_commands = [
        _project_manifest_command(_require_object(command, "package command"), package_facts)
        for command in _require_array(package["commands"], "package commands")
    ]
    return find_capture_disagreement(
        {"commands": source_commands},
        {"commands": package_commands},
    )


def _validate_public_synthetic_capture_pair(
    source: dict[str, Any],
    candidate: dict[str, Any],
    candidate_manifest: dict[str, Any],
    candidate_configuration: str,
) -> tuple[str, str]:
    disagreement = _find_baseline_projection_disagreement(source, candidate)
    if disagreement is not None:
        raise FirewallError(
            "source/candidate command projection differs at "
            f"{disagreement['pointer']}"
        )
    for label, snapshot in (("source", source), ("candidate", candidate)):
        differences = _capture_identity_differences(
            snapshot, label, "public-synthetic-capture"
        )
        if differences:
            raise FirewallError(
                f"{label} CLI identity is internally inconsistent: "
                f"{differences[0].pointer}"
            )
    source_version = _require_object(source["version"], "source version")
    candidate_version = _require_object(candidate["version"], "candidate version")
    source_v1 = _require_object(source_version["protocol1"], "source protocol 1 version")
    candidate_v1 = _require_object(
        candidate_version["protocol1"], "candidate protocol 1 version"
    )
    version = _require_string(source_v1.get("version"), "source product version")
    source_line = _require_string(source_v1.get("sourceLine"), "source line")
    if (
        candidate_configuration not in {"Debug", "Release"}
        or candidate_v1.get("version") != version
        or candidate_v1.get("sourceLine") != source_line
        or candidate_manifest.get("version") != version
        or candidate_manifest.get("sourceLine") != source_line
        or candidate_manifest.get("privateOnly") is not True
        or candidate_manifest.get("runtimeAuthority") is not False
    ):
        raise FirewallError("candidate package identity or authority differs from source")
    return version, source_line


def _extract_sealed_cli(
    release_zip: Path,
    extraction: Path,
) -> tuple[Path, str, list[dict[str, Any]]]:
    with zipfile.ZipFile(release_zip) as archive:
        infos = archive.infolist()
        if not infos:
            raise FirewallError("sealed release ZIP is empty")
        names = [item.filename.replace("\\", "/") for item in infos]
        if any(Path(name).is_absolute() or ".." in Path(name).parts for name in names):
            raise FirewallError("sealed release ZIP contains an unsafe member")
        wrappers = {name.split("/", 1)[0] for name in names if "/" in name}
        if len(wrappers) != 1:
            raise FirewallError("sealed release ZIP wrapper mismatch")
        wrapper = next(iter(wrappers))
        archive.extractall(extraction)
    package_root = extraction / wrapper
    sums = (package_root / "SHA256SUMS").read_text(encoding="utf-8-sig")
    rows = {
        line.split(maxsplit=1)[1].lstrip(" *").replace("\\", "/"):
        line.split(maxsplit=1)[0].upper()
        for line in sums.splitlines()
        if line.strip()
    }
    identities: list[dict[str, Any]] = []
    for relative in sorted(rows):
        member = package_root.joinpath(*PureWindowsPath(relative).parts)
        if not member.is_file():
            raise FirewallError(f"sealed release checksum member missing: {relative}")
        length, sha256 = _authenticate_selector_file(
            member, rows[relative], "sealed package member"
        )
        identities.append({"path": relative, "length": length, "sha256": sha256})
    target_relative = "cli/actorwright.exe"
    if target_relative not in rows:
        raise FirewallError("sealed release lacks CLI checksum row")
    target = package_root / target_relative
    _authenticate_selector_file(target, rows[target_relative], "target CLI")
    if not any(row["path"] == "cli/actorwright.ps1" for row in identities):
        raise FirewallError("sealed release lacks the preferred PowerShell launcher")
    return target.resolve(), rows[target_relative], identities


def capture_preview275_baseline(
    *,
    repository_root: str | Path,
    source_ref: str,
    release_zip: str | Path,
    output_root: str | Path,
    expected_harness: dict[str, Any],
    dotnet_path: str | Path = PINNED_DOTNET,
) -> Path:
    """Materialize a complete authenticated Preview.275 baseline in a new root."""

    repository = Path(repository_root).resolve()
    release = Path(release_zip).resolve()
    if source_ref != "v1.0.0-preview.275":
        raise FirewallError("capture requires exact source ref v1.0.0-preview.275")
    if not release.is_file():
        raise FirewallError(f"sealed release ZIP missing: {release}")
    output = prepare_capture_workspace(output_root)
    extraction = output / "sealed-preview275"
    try:
        harness = authenticate_harness_checkout(
            repository,
            expected_commit=expected_harness["commit"],
            expected_tree=expected_harness["tree"],
            expected_source_closure=expected_harness["sourceClosure"],
        )
        evidence = capture_authenticated_release(
            repository_root=repository,
            source_tag=source_ref,
            sealed_zip=release,
            expected_zip_length=193031680,
            expected_zip_sha256="148A2626F608B79C68742A53A45A658F13B02A4AFB0CBCE2D17E0B9647ED3181",
            dotnet_path=Path(dotnet_path),
        )
        target, target_hash, package_identities = _extract_sealed_cli(release, extraction)
        dotnet = Path(dotnet_path).resolve()
        project = repository / "tests" / "NpcManager.Cli.Tests" / "NpcManager.Cli.Tests.csproj"
        _checked_process(
            [
                str(dotnet), "build", str(project), "-c", "Release",
                "--no-restore", "-p:NuGetAudit=false",
                "-p:UseSharedCompilation=false", "-m:1",
            ],
            cwd=repository,
            timeout=600,
        )
        if authenticate_harness_checkout(
            repository,
            expected_commit=harness["commit"],
            expected_tree=harness["tree"],
            expected_source_closure=harness["sourceClosure"],
        ) != harness:
            raise FirewallError("journey harness identity changed during build")
        runner = repository / "tests" / "NpcManager.Cli.Tests" / "bin" / "Release" / "net10.0" / "NpcManager.Cli.Tests.dll"
        runner_hash = _sha256_file(runner).upper()
        dotnet_hash = _sha256_file(dotnet).upper()
        journeys_root = output / "journeys"
        journeys = run_structured_journeys(
            repository_root=repository,
            runner_dll=runner,
            expected_runner_sha256=runner_hash,
            target_cli=target,
            expected_target_sha256=target_hash,
            dotnet_path=dotnet,
            expected_dotnet_sha256=dotnet_hash,
            output_root=journeys_root,
            expected_harness=harness,
        )
        if [journey["id"] for journey in journeys] != [
            "journey.create-to-package", "journey.wave-b-v1"
        ]:
            raise FirewallError("both structured journeys are required")

        source_snapshot = _require_object(evidence["sourceSnapshot"], "source snapshot")
        package_snapshot = _require_object(evidence["packageSnapshot"], "package snapshot")
        disagreement = _find_baseline_projection_disagreement(
            source_snapshot, package_snapshot
        )
        if disagreement is not None:
            raise FirewallError(
                f"authenticated source/package observations disagree at {disagreement['pointer']}"
            )
        version = _require_object(
            _require_object(source_snapshot["version"], "source version")["protocol1"],
            "source protocol-1 version",
        )
        members = []
        for member_id, relative in (
            ("journey.create-to-package", "journeys/create-to-package.json"),
            ("journey.wave-b-v1", "journeys/wave-b-v1.json"),
        ):
            length, sha256 = _sha256_file_and_length(output / relative)
            members.append({
                "id": member_id,
                "kind": "journey",
                "path": relative,
                "length": length,
                "sha256": sha256.upper(),
            })
        launcher_identity = next(
            row for row in package_identities
            if row["path"] == "cli/actorwright.ps1"
        )
        launcher_destination = output / launcher_identity["path"]
        launcher_destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(target.with_name("actorwright.ps1"), launcher_destination)
        members.append({
            "id": "package.launcher",
            "kind": "evidence",
            **launcher_identity,
        })
        manifest = {
            "$schema": SNAPSHOT_SCHEMA_ID,
            "formatVersion": 1,
            "provenance": {
                "productVersion": version["version"],
                "sourceLine": version["sourceLine"],
                "sourceTag": source_ref,
                "sourceCommit": evidence["sourceCommit"],
                "sourceTree": evidence["sourceTree"],
                "releaseZip": evidence["releaseZip"],
                "capabilitiesSha256": evidence["capabilitiesSha256"],
                "candidateBundleManifestSha256": "88AABD4E80AF573113A605EE15998D70AC9EB0F5FFFD5341CC50CCBA4474FC97",
                "approvedBundleManifestSha256": "5B31DB92CCF2870C0D1C607941C467D80030EEAF0DA0F6E39DA15EF778C6A4B3",
            },
            "commandCount": source_snapshot["commandCount"],
            "orderedCommandNamesSha256": source_snapshot["orderedCommandNamesSha256"],
            "commands": [
                _project_manifest_command(command, source_snapshot["behaviorFacts"])
                for command in source_snapshot["commands"]
            ],
            "exitMeanings": source_snapshot["exitMeanings"],
            "voiceDialogueSentinels": source_snapshot["voiceDialogueSentinels"],
            "members": members,
            "requiredPackagePaths": [
                {
                    "path": path,
                    "role": (
                        "launcher" if path == "cli/actorwright.ps1"
                        else "cli" if path.startswith("cli/")
                        else "release-evidence"
                    ),
                }
                for row in package_identities
                for path in [row["path"]]
            ],
            "launcher": {
                "entrypoint": "cli/actorwright.ps1",
                "shape": "powershell-forwarding-wrapper",
            },
        }
        _validate_snapshot_document(manifest)
        (output / "manifest.json").write_bytes(canonical_json_bytes(manifest))
        return output
    except Exception:
        _remove_disposable_path(output)
        raise
    finally:
        if extraction.exists():
            _remove_disposable_path(extraction)


def _git_source_closure(repository: Path, root: str) -> dict[str, Any]:
    tree = _git(repository, "rev-parse", f"HEAD:{root}").decode("utf-8").strip()
    listing = _git(
        repository, "ls-tree", "-r", "--full-tree", "HEAD", "--", root
    )
    lines = tuple(line for line in listing.splitlines() if line)
    if not lines:
        raise FirewallError(f"source closure is empty: {root}")
    return {
        "root": root,
        "tree": _require_hex(tree, f"{root} tree", _GIT_OBJECT_LENGTH),
        "fileCount": len(lines),
        "sha256": _sha256(listing).upper(),
    }


def _current_harness_expectation(repository: Path) -> dict[str, Any]:
    return {
        "commit": _git(repository, "rev-parse", "HEAD").decode("utf-8").strip(),
        "tree": _git(repository, "rev-parse", "HEAD^{tree}").decode("utf-8").strip(),
        "sourceClosure": _git_source_closure(
            repository, "tests/NpcManager.Cli.Tests"
        ),
    }


def _build_synthetic_candidate(
    *,
    repository: Path,
    output_root: Path,
    configuration: str,
    dotnet_path: Path,
    capture_root: Path,
) -> dict[str, Any]:
    if configuration not in {"Debug", "Release"}:
        raise FirewallError("synthetic candidate configuration must be Debug or Release")
    candidate, reparse = _lexical_path_reparse(output_root)
    package_parent = (repository / "artifacts" / "packages").resolve()
    if (
        reparse is not None
        or candidate.exists()
        or candidate.drive.casefold() != "k:"
        or candidate.resolve().parent != package_parent
        or not candidate.name.startswith("compatibility-firewall-candidate-")
    ):
        raise FirewallError(
            "synthetic candidate output must be a fresh compatibility-firewall-candidate-* "
            "directory under artifacts/packages"
        )
    candidate = candidate.resolve()
    package_script = repository / "tools" / "build" / "package.ps1"
    package_script, script_reparse = _lexical_path_reparse(package_script)
    if script_reparse is not None or not package_script.is_file():
        raise FirewallError("synthetic package script is missing or contains a reparse point")
    powershell_executable = shutil.which("pwsh.exe") or shutil.which("powershell.exe")
    if not powershell_executable:
        raise FirewallError("PowerShell executable is required for synthetic package capture")
    powershell = Path(powershell_executable).resolve()
    python_path = _explicit_file(sys.executable, "capture Python", ".exe")
    powershell_length, powershell_sha256 = _sha256_file_and_length(powershell)
    script_length, script_sha256 = _sha256_file_and_length(package_script)
    python_length, python_sha256 = _sha256_file_and_length(python_path)

    command = [
        str(powershell), "-NoProfile", "-NonInteractive", "-File",
        str(package_script), "-Configuration", configuration, "-Runtime", "win-x64",
        "-DotNetPath", str(dotnet_path), "-PythonPath", str(python_path),
        "-OutputRoot", str(candidate),
    ]
    environment = os.environ.copy()
    environment["ACTORWRIGHT_TEST_DOTNET"] = str(dotnet_path)
    environment["ACTORWRIGHT_COMPATIBILITY_CAPTURE_PARENT"] = str(
        CAPTURE_WORKSPACE_PARENT.resolve()
    )
    stdout_log = capture_root.with_name(capture_root.name + ".package.stdout.log")
    stderr_log = capture_root.with_name(capture_root.name + ".package.stderr.log")
    if stdout_log.exists() or stderr_log.exists():
        raise FirewallError("synthetic package build log destination already exists")
    completed = _run_bounded_process(
        command,
        cwd=str(repository),
        env=environment,
        shell=False,
        timeout=1800,
        max_output_bytes=16 * 1024 * 1024,
    )
    if stdout_log.exists() or stderr_log.exists():
        raise FirewallError("synthetic package build log destination already exists")
    stdout_log.write_bytes(completed.stdout)
    stderr_log.write_bytes(completed.stderr)
    if completed.returncode != 0:
        raise FirewallError(
            f"synthetic {configuration} package build failed with exit code "
            f"{completed.returncode}; logs={stdout_log},{stderr_log}"
        )
    expected_pass = f"Actorwright package: PASS {candidate}"
    try:
        stdout_text = completed.stdout.decode("utf-8-sig", errors="strict")
    except UnicodeError as exc:
        raise FirewallError("synthetic package build stdout is not UTF-8") from exc
    if expected_pass not in stdout_text.splitlines():
        raise FirewallError(
            "synthetic package build exited successfully without the package PASS line; "
            f"logs={stdout_log},{stderr_log}"
        )

    stdout_length, stdout_sha256 = _sha256_file_and_length(stdout_log)
    stderr_length, stderr_sha256 = _sha256_file_and_length(stderr_log)
    return {
        "configuration": configuration,
        "exitCode": completed.returncode,
        "command": [
            powershell.name, "-NoProfile", "-NonInteractive", "-File",
            "tools/build/package.ps1", "-Configuration", configuration,
            "-Runtime", "win-x64", "-DotNetPath", "<pinned-dotnet>",
            "-PythonPath", "<python>", "-OutputRoot", "<candidate-output>",
        ],
        "powershell": {
            "name": powershell.name,
            "length": powershell_length,
            "sha256": powershell_sha256.upper(),
        },
        "packageScript": {
            "length": script_length,
            "sha256": script_sha256.upper(),
        },
        "python": {"length": python_length, "sha256": python_sha256.upper()},
        "stdout": {"length": stdout_length, "sha256": stdout_sha256.upper()},
        "stderr": {"length": stderr_length, "sha256": stderr_sha256.upper()},
    }


def _validate_synthetic_candidate_launcher(
    candidate_path: Path,
    manifest: dict[str, Any],
) -> tuple[Path, int, str]:
    from tools.release import verify_release

    launcher = candidate_path / "cli" / "actorwright.ps1"
    expected = verify_release.POWERSHELL_WRAPPER
    if launcher.is_symlink() or not launcher.is_file():
        raise FirewallError("synthetic candidate lacks its PowerShell launcher")
    if launcher.stat().st_size != len(expected):
        raise FirewallError("synthetic candidate launcher content differs")
    launcher_bytes = launcher.read_bytes()
    if launcher_bytes != expected:
        raise FirewallError("synthetic candidate launcher content differs")

    launcher_length = len(launcher_bytes)
    launcher_sha256 = hashlib.sha256(launcher_bytes).hexdigest().upper()
    rows = manifest.get("files")
    declared = [
        row for row in rows
        if isinstance(row, dict) and row.get("path") == "cli/actorwright.ps1"
    ] if isinstance(rows, list) else []
    if (
        len(declared) != 1
        or declared[0].get("size") != launcher_length
        or str(declared[0].get("sha256", "")).casefold()
        != launcher_sha256.casefold()
    ):
        raise FirewallError(
            "synthetic candidate launcher differs from its package manifest")
    return launcher, launcher_length, launcher_sha256


def capture_public_synthetic_baseline(
    *,
    repository_root: str | Path,
    source_cli: str | Path,
    candidate_output_root: str | Path,
    candidate_configuration: str,
    output_root: str | Path,
) -> Path:
    """Build and capture one unsealed package against the current source CLI."""

    repository = Path(repository_root).resolve()
    if repository != REPOSITORY_ROOT.resolve():
        raise FirewallError("synthetic capture repository root mismatch")
    source_path, source_reparse = _lexical_path_reparse(source_cli)
    expected_source = (
        repository / "src" / "NpcManager.Cli" / "bin" / "Release"
        / "net10.0" / "actorwright.exe"
    ).resolve()
    if (
        source_reparse is not None
        or source_path.resolve() != expected_source
        or not source_path.is_file()
    ):
        raise FirewallError("synthetic capture requires the canonical Release source CLI")

    if candidate_configuration not in {"Debug", "Release"}:
        raise FirewallError("synthetic candidate configuration must be Debug or Release")

    expected_harness = _current_harness_expectation(repository)
    harness = authenticate_harness_checkout(
        repository,
        expected_commit=expected_harness["commit"],
        expected_tree=expected_harness["tree"],
        expected_source_closure=expected_harness["sourceClosure"],
    )
    source_closure = _git_source_closure(repository, "src")
    sdk_length, sdk_sha256 = _authenticated_pinned_sdk()
    sdk_path = _explicit_file(PINNED_DOTNET, "pinned dotnet", ".exe")
    sdk_identity = {"length": sdk_length, "sha256": sdk_sha256.upper()}

    output = prepare_capture_workspace(output_root)
    candidate_path = Path(candidate_output_root)
    try:
        package_build = _build_synthetic_candidate(
            repository=repository,
            output_root=candidate_path,
            configuration=candidate_configuration,
            dotnet_path=sdk_path,
            capture_root=output,
        )
        candidate_path = candidate_path.resolve()
        candidate_cli = candidate_path / "cli" / "actorwright.exe"
        candidate_manifest_path = candidate_path / "manifest.json"
        if not candidate_cli.is_file() or not candidate_manifest_path.is_file():
            raise FirewallError("synthetic candidate package is incomplete")
        try:
            from tools.release import verify_release

            verify_release.verify_package_staging(candidate_path)
        except Exception as exc:
            raise FirewallError(f"synthetic candidate package verification failed: {exc}") from exc

        candidate_manifest = _require_object(
            _load_json(candidate_manifest_path), "synthetic candidate manifest"
        )
        if candidate_manifest.get("privateOnly") is not True or candidate_manifest.get(
            "runtimeAuthority"
        ) is not False:
            raise FirewallError("synthetic candidate must remain private-only and non-authoritative")
        launcher, launcher_length, launcher_sha256 = (
            _validate_synthetic_candidate_launcher(candidate_path, candidate_manifest)
        )

        _sdk_sha256, cli_runner_sha256, _architecture_runner_sha256, build_evidence = (
            _build_source_selector_projects()
        )
        if _sdk_sha256.casefold() != sdk_sha256.casefold():
            raise FirewallError("selector build used a different pinned SDK")
        if authenticate_harness_checkout(
            repository,
            expected_commit=harness["commit"],
            expected_tree=harness["tree"],
            expected_source_closure=harness["sourceClosure"],
        ) != harness:
            raise FirewallError("synthetic journey harness identity changed during build")

        runner = (
            repository / "tests" / "NpcManager.Cli.Tests" / "bin" / "Release"
            / "net10.0" / "NpcManager.Cli.Tests.dll"
        )
        runner_length, runner_sha256 = _authenticate_selector_file(
            runner, cli_runner_sha256, "synthetic journey runner"
        )
        if runner_length <= 0:
            raise FirewallError("synthetic journey runner is empty")
        source_length, source_sha256 = _sha256_file_and_length(source_path)
        source_assembly = (
            repository / "src" / "NpcManager.Cli" / "bin" / "Release"
            / "net10.0" / "actorwright.dll"
        )
        source_assembly_length, source_assembly_sha256 = _sha256_file_and_length(source_assembly)
        candidate_length, candidate_sha256 = _sha256_file_and_length(candidate_cli)
        manifest_length, manifest_sha256 = _sha256_file_and_length(candidate_manifest_path)
    except Exception:
        if output.exists() and _require_owned_capture_workspace(output) == output:
            _remove_disposable_path(output)
        raise

    source_workspace: Path | None = None
    candidate_workspace: Path | None = None
    try:
        source_workspace = prepare_capture_workspace(
            CAPTURE_WORKSPACE_PARENT
            / (CAPTURE_WORKSPACE_PREFIX + "source-" + uuid.uuid4().hex)
        )
        source_snapshot = capture_cli_contract(CliTarget(
            "source", source_path, source_workspace, source_sha256, sdk_path
        ))
        candidate_workspace = prepare_capture_workspace(
            CAPTURE_WORKSPACE_PARENT
            / (CAPTURE_WORKSPACE_PREFIX + "candidate-" + uuid.uuid4().hex)
        )
        candidate_snapshot = capture_cli_contract(CliTarget(
            "candidate", candidate_cli, candidate_workspace, candidate_sha256, sdk_path
        ))
        product_version, source_line = _validate_public_synthetic_capture_pair(
            source_snapshot, candidate_snapshot, candidate_manifest,
            candidate_configuration,
        )

        journeys = run_structured_journeys(
            repository_root=repository,
            runner_dll=runner,
            expected_runner_sha256=runner_sha256,
            target_cli=candidate_cli,
            expected_target_sha256=candidate_sha256,
            dotnet_path=sdk_path,
            expected_dotnet_sha256=sdk_sha256,
            output_root=output / "journeys",
            expected_harness=harness,
        )
        if [row["id"] for row in journeys] != [
            "journey.create-to-package", "journey.wave-b-v1"
        ]:
            raise FirewallError("both public structured journeys are required")

        launcher_relative = "cli/actorwright.ps1"
        launcher_destination = output / launcher_relative
        launcher_destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(launcher, launcher_destination)
        members: list[dict[str, Any]] = []
        for journey in journeys:
            relative = (
                "journeys/create-to-package.json"
                if journey["id"] == "journey.create-to-package"
                else "journeys/wave-b-v1.json"
            )
            journey_path = output / relative
            length, sha256 = _sha256_file_and_length(journey_path)
            members.append({
                "id": journey["id"], "kind": "journey", "path": relative,
                "length": length, "sha256": sha256.upper(),
            })
        members.append({
            "id": "package.launcher", "kind": "evidence",
            "path": launcher_relative, "length": launcher_length,
            "sha256": launcher_sha256.upper(),
        })

        manifest = {
            "$schema": SYNTHETIC_SNAPSHOT_SCHEMA_ID,
            "formatVersion": 2,
            "provenance": {
                "kind": "public-synthetic",
                "productVersion": product_version,
                "sourceLine": source_line,
                "source": {
                    "configuration": "Release",
                    "commit": harness["commit"],
                    "tree": harness["tree"],
                    "sourceClosure": source_closure,
                    "cli": {"length": source_length, "sha256": source_sha256.upper()},
                    "managedAssembly": {
                        "length": source_assembly_length,
                        "sha256": source_assembly_sha256.upper(),
                    },
                    "selectorBuildEvidence": list(build_evidence),
                },
                "candidate": {
                    "configuration": candidate_configuration,
                    "privateOnly": True,
                    "runtimeAuthority": False,
                    "packageBuild": package_build,
                    "manifest": {
                        "length": manifest_length,
                        "sha256": manifest_sha256.upper(),
                    },
                    "cli": {
                        "length": candidate_length,
                        "sha256": candidate_sha256.upper(),
                    },
                },
                "harness": {
                    "commit": harness["commit"],
                    "tree": harness["tree"],
                    "sourceClosure": harness["sourceClosure"],
                    "runner": {"length": runner_length, "sha256": runner_sha256.upper()},
                },
                "pinnedDotnet": sdk_identity,
            },
            "commandCount": source_snapshot["commandCount"],
            "orderedCommandNamesSha256": source_snapshot["orderedCommandNamesSha256"],
            "commands": [
                _project_manifest_command(command, source_snapshot["behaviorFacts"])
                for command in source_snapshot["commands"]
            ],
            "exitMeanings": source_snapshot["exitMeanings"],
            "voiceDialogueSentinels": source_snapshot["voiceDialogueSentinels"],
            "members": members,
            "requiredPackagePaths": [{"path": launcher_relative, "role": "launcher"}],
            "launcher": {
                "entrypoint": launcher_relative,
                "shape": "powershell-forwarding-wrapper",
            },
        }
        _validate_snapshot_document(manifest)
        (output / "manifest.json").write_bytes(canonical_json_bytes(manifest))

        powershell_path = shutil.which(package_build["powershell"]["name"])
        if not powershell_path:
            raise FirewallError("package PowerShell executable disappeared during capture")
        package_shell = Path(powershell_path).resolve()
        package_script = repository / "tools" / "build" / "package.ps1"
        python_path = Path(sys.executable).resolve()
        stdout_log = output.with_name(output.name + ".package.stdout.log")
        stderr_log = output.with_name(output.name + ".package.stderr.log")

        def still_matches(path: Path, identity: dict[str, Any]) -> bool:
            if not path.is_file():
                return False
            length, sha256 = _sha256_file_and_length(path)
            return (
                length == identity["length"]
                and sha256.casefold() == identity["sha256"].casefold()
            )

        if (
            _sha256_file(source_path).upper() != source_sha256.upper()
            or _sha256_file(source_assembly).upper() != source_assembly_sha256.upper()
            or _sha256_file(candidate_cli).upper() != candidate_sha256.upper()
            or _sha256_file(candidate_manifest_path).upper() != manifest_sha256.upper()
            or not still_matches(launcher, {
                "length": launcher_length, "sha256": launcher_sha256,
            })
            or not still_matches(launcher_destination, {
                "length": launcher_length, "sha256": launcher_sha256,
            })
            or _sha256_file(runner).upper() != runner_sha256.upper()
            or _authenticated_pinned_sdk() != (sdk_length, sdk_sha256)
            or _git_source_closure(repository, "src") != source_closure
            or not still_matches(package_shell, package_build["powershell"])
            or not still_matches(package_script, package_build["packageScript"])
            or not still_matches(python_path, package_build["python"])
            or not still_matches(stdout_log, package_build["stdout"])
            or not still_matches(stderr_log, package_build["stderr"])
        ):
            raise FirewallError("a synthetic capture input changed while capturing")
        if authenticate_harness_checkout(
            repository,
            expected_commit=harness["commit"],
            expected_tree=harness["tree"],
            expected_source_closure=harness["sourceClosure"],
        ) != harness:
            raise FirewallError("synthetic journey harness identity changed during capture")
        return output
    except Exception:
        if output.exists() and _require_owned_capture_workspace(output) == output:
            _remove_disposable_path(output)
        raise
    finally:
        for workspace in (candidate_workspace, source_workspace):
            if workspace is not None and workspace.exists():
                if _require_owned_capture_workspace(workspace) == workspace:
                    _remove_disposable_path(workspace)


def _capture_identity_differences(
    snapshot: dict[str, Any], label: str, gate: str,
) -> tuple[Difference, ...]:
    """Check one CLI's protocol and capabilities identity against its protocol 1 version."""
    protocol1 = snapshot["version"]["protocol1"]
    expected = {
        "version": _require_string(protocol1.get("version"), f"{label} protocol 1 version"),
        "sourceLine": _require_string(protocol1.get("sourceLine"), f"{label} protocol 1 source line"),
    }
    differences: list[Difference] = []
    protocol2 = snapshot["version"]["protocol2"]
    if "version" in protocol2:
        differences.append(Difference(
            "candidate-identity", label, "/identity/version", None,
            protocol2["version"], gate,
        ))
    for identity, version_key in (
        (protocol2, "productVersion"),
        (snapshot["capabilities"]["protocol1"]["document"], "version"),
    ):
        for field in ("version", "sourceLine"):
            actual = identity.get(version_key if field == "version" else field)
            if actual != expected[field]:
                differences.append(Difference(
                    "candidate-identity", label, f"/identity/{field}",
                    expected[field], actual, gate,
                ))
    return tuple(differences)


def _source_build_identity() -> dict[str, str]:
    path = REPOSITORY_ROOT / "src/NpcManager.Application/BuildInfo.cs"
    source = re.sub(r"/\*.*?(?:\*/|\Z)", "", path.read_text(encoding="utf-8"),
                    flags=re.DOTALL)
    identity: dict[str, str] = {}
    for field, constant in (("version", "ProductVersion"), ("sourceLine", "SourceLine")):
        matches = re.findall(
            rf'^[ \t]*public[ \t]+const[ \t]+string[ \t]+{constant}'
            rf'[ \t]*=[ \t]*"([^"\r\n]+)"[ \t]*;[ \t]*(?://[^\r\n]*)?$',
            source, flags=re.MULTILINE,
        )
        if len(matches) != 1:
            raise FirewallError(f"BuildInfo requires exactly one {constant} declaration")
        identity[field] = matches[0]
    return identity


def _source_report(baseline_id: str, source_cli: str) -> dict[str, Any]:
    """Run the Source inventory against one live, repository-owned CLI."""

    if not Path(source_cli).is_absolute():
        raise FirewallError("source CLI path must be absolute")
    cli, reparse = _lexical_path_reparse(source_cli)
    if reparse is not None or not cli.is_file() or cli.suffix.casefold() not in {".exe", ".dll"}:
        raise FirewallError("source CLI must be an ordinary absolute executable")
    if cli.drive.casefold() != "k:" or not cli.is_relative_to(REPOSITORY_ROOT):
        raise FirewallError("source CLI must be inside the K-local repository worktree")
    if (
        cli.parent != REPOSITORY_ROOT / "src" / "NpcManager.Cli" / "bin" / "Release" / "net10.0"
        or cli.name not in {"actorwright.exe", "actorwright.dll"}
    ):
        raise FirewallError("source CLI must be the canonical Release CLI")
    baseline = load_baseline(baseline_id)
    policy_path = DEFAULT_BASELINE_ROOT / "compatibility-policy.json"
    policy = _require_object(_load_json(policy_path), "compatibility policy")
    ValidatedPolicy.parse(policy)
    cli_sha256 = _sha256_file(cli).upper()
    capture_root = prepare_capture_workspace(
        CAPTURE_WORKSPACE_PARENT / (CAPTURE_WORKSPACE_PREFIX + uuid.uuid4().hex)
    )
    try:
        snapshot = capture_cli_contract(
            CliTarget("source", cli, capture_root, cli_sha256)
        )
    finally:
        if _require_owned_capture_workspace(capture_root) == capture_root:
            _remove_disposable_path(capture_root)

    expected = {
        "identity": {
            "version": baseline.snapshot["provenance"]["productVersion"],
            "sourceLine": baseline.snapshot["provenance"]["sourceLine"],
        },
        **{
            key: baseline.snapshot[key] for key in (
                "commandCount", "orderedCommandNamesSha256", "commands",
                "exitMeanings", "voiceDialogueSentinels",
            )
        },
    }
    observed = {
        "identity": {
            "version": snapshot["version"]["protocol1"]["version"],
            "sourceLine": snapshot["version"]["protocol1"]["sourceLine"],
        },
        "commandCount": snapshot["commandCount"],
        "orderedCommandNamesSha256": snapshot["orderedCommandNamesSha256"],
        "commands": [
            _project_manifest_command(command, snapshot["behaviorFacts"])
            for command in snapshot["commands"]
        ],
        "exitMeanings": snapshot["exitMeanings"],
        "voiceDialogueSentinels": snapshot["voiceDialogueSentinels"],
    }
    differences = tuple(compare_snapshots(
        {key: value for key, value in expected.items() if key != "identity"},
        {key: value for key, value in observed.items() if key != "identity"},
        policy,
    )) + _capture_identity_differences(snapshot, "source", "comparison:source-baseline")
    build_identity = _source_build_identity()
    for field in ("version", "sourceLine"):
        if observed["identity"][field] != build_identity[field]:
            differences += (Difference(
                "candidate-identity", "source", f"/identity/{field}",
                build_identity[field], observed["identity"][field],
                "comparison:source-baseline",
            ),)
    gates: list[GateResult] = []

    def gate(gate_id: str, passes: bool, *evidence: str) -> None:
        gates.append(GateResult(
            gate_id, True, "PASS" if passes else "FAIL", evidence,
            differences if gate_id in {
                "contract:metadata-help-schema-protocol-exit",
                "comparison:source-baseline",
            } else (),
        ))

    gate("contract:manifest-format", True, f"manifest={baseline.manifest_path}",
         f"manifestSha256={_sha256_file(baseline.manifest_path).upper()}")
    gate("contract:command-surface", (
        observed["commandCount"] == EXPECTED_COMMAND_COUNT
        and observed["commandCount"] == expected["commandCount"]
        and observed["orderedCommandNamesSha256"] == expected["orderedCommandNamesSha256"]
    ), f"cliSha256={cli_sha256}", f"commandCount={observed['commandCount']}")
    gate("contract:voice-dialogue-sentinels", (
        observed["voiceDialogueSentinels"] == list(VOICE_DIALOGUE_SENTINELS)
        and observed["voiceDialogueSentinels"] == expected["voiceDialogueSentinels"]
    ), f"sentinels={len(observed['voiceDialogueSentinels'])}")
    gate("contract:metadata-help-schema-protocol-exit", not differences,
         f"comparedCommands={len(observed['commands'])}")
    gate("dispatch:no-input-no-write", (
        snapshot["workspaceInventoryBeforeNoInput"]
        == snapshot["workspaceInventoryAfterNoInput"]
    ), f"cliSha256={cli_sha256}")
    gate("comparison:source-baseline", not differences,
         f"differences={len(differences)}")

    focused = _run_bounded_process(
        [sys.executable, "-m", "pytest",
         str(REPOSITORY_ROOT / "tests" / "release" / "test_compatibility_firewall.py"),
         "-q", "-k", (
             "test_unchanged_recapture_has_no_differences_or_accepted_differences "
             "or test_required_gate_never_passes_without_explicit_pass "
             "or test_tiers_are_closed_complete_plan_inventories_not_task4_subsets"
         )],
        cwd=str(REPOSITORY_ROOT), env=os.environ.copy(), shell=False,
        timeout=300, max_output_bytes=CLI_MAX_OUTPUT_BYTES,
    )
    gate("verification:focused-tests", focused.returncode == 0,
         f"exit={focused.returncode}",
         f"stdoutBase64={base64.b64encode(focused.stdout).decode('ascii')}",
         f"stderrBase64={base64.b64encode(focused.stderr).decode('ascii')}",
         f"stdoutSha256={_sha256(focused.stdout).upper()}",
         f"stderrSha256={_sha256(focused.stderr).upper()}")

    cli_dll = (REPOSITORY_ROOT / "tests" / "NpcManager.Cli.Tests" / "bin"
               / "Release" / "net10.0" / "NpcManager.Cli.Tests.dll")
    architecture_dll = (REPOSITORY_ROOT / "tests" / "NpcManager.Architecture.Tests"
                        / "bin" / "Release" / "net10.0"
                        / "NpcManager.Architecture.Tests.dll")
    try:
        sdk_hash, cli_hash, architecture_hash, build_evidence = (
            _build_source_selector_projects()
        )
        selector_results = run_required_selectors(
            cli_project_dll=cli_dll,
            architecture_project_dll=architecture_dll,
            dotnet_path=PINNED_DOTNET,
            working_directory=REPOSITORY_ROOT,
            expected_dotnet_sha256=sdk_hash,
            expected_cli_project_sha256=cli_hash,
            expected_architecture_project_sha256=architecture_hash,
        )
        gates.extend(
            GateResult(result.gate_id, result.required, result.status,
                       (*build_evidence, *result.evidence), result.differences)
            for result in selector_results
        )
    except (FirewallError, OSError) as exc:
        gates.extend(
            GateResult(gate_id, True, "BLOCKED", (str(exc),), ())
            for gate_id in _SELECTOR_GATE_IDS
        )
    evaluation = evaluate_tier("Source", gates)
    return {
        "$schema": "urn:actorwright:compatibility:v1:report",
        "formatVersion": 1,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "tier": "Source", "baselineId": baseline.baseline_id,
        "compatible": evaluation.compatible, "verdict": evaluation.verdict,
        "fixtureBacked": False, "realInstallation": False,
        "gates": [
            {"gateId": item.gate_id, "required": item.required,
             "status": item.status, "evidence": list(item.evidence),
             "differences": [
                 {"subject": difference.subject, "layer": difference.layer,
                  "pointer": difference.pointer, "expected": difference.expected,
                  "actual": difference.actual, "gateId": difference.gate}
                 for difference in item.differences
             ]}
            for item in evaluation.gates
        ],
        "differences": [
            {"subject": difference.subject, "layer": difference.layer,
             "pointer": difference.pointer, "expected": difference.expected,
             "actual": difference.actual, "gateId": difference.gate}
            for difference in differences
        ],
        "pinRows": [],
    }


def _package_report(baseline_id: str, source_cli: str, release_root: str) -> dict[str, Any]:
    """Verify current binary staging and compare two live CLI captures."""

    from tools.release import verify_release

    source, source_reparse = _lexical_path_reparse(source_cli)
    root, root_reparse = _lexical_path_reparse(release_root)
    canonical_source = (REPOSITORY_ROOT / "src" / "NpcManager.Cli" / "bin"
                        / "Release" / "net10.0")
    if (source_reparse is not None or not source.is_absolute()
            or source.parent != canonical_source
            or source.name not in {"actorwright.exe", "actorwright.dll"}
            or not source.is_file()):
        raise FirewallError("source CLI must be the ordinary canonical Release CLI")
    if (root_reparse is not None or not root.is_absolute()
            or root.drive.casefold() != "k:"
            or not root.is_relative_to(REPOSITORY_ROOT)
            or not root.is_dir()):
        raise FirewallError("package staging root must be an ordinary K-local repository directory")

    baseline = load_baseline(baseline_id)
    policy = _require_object(_load_json(DEFAULT_BASELINE_ROOT / "compatibility-policy.json"),
                             "compatibility policy")
    ValidatedPolicy.parse(policy)
    gates: list[GateResult] = []
    differences: list[Difference] = []

    try:
        staging = verify_release.verify_package_staging(root, metadata_only=False)
        if (staging.get("status") != "PASS"
                or staging.get("artifactKind") != "binary-package-staging"
                or staging.get("protocolV2Kernel") is not True
                or staging.get("zipVerified") is not False):
            raise FirewallError("package staging verifier returned incomplete evidence")
        gates.append(GateResult("verification:package-staging", True, "PASS",
                                (f"root={root}", "metadataOnly=False",
                                 f"files={staging['files']}"), ()))
    except (verify_release.ReleaseError, FirewallError, OSError, ValueError,
            KeyError, TypeError) as exc:
        gates.append(GateResult("verification:package-staging", True, "FAIL",
                                (f"{type(exc).__name__}:{exc}",), ()))
        gates.extend(GateResult(gate_id, True, "BLOCKED", ("staging verification failed",), ())
                     for gate_id in ("package:inventory-entrypoint",
                                     "comparison:source-package-baseline"))
    else:
        manifest = _require_object(_load_json(root / "manifest.json"), "package manifest")
        rows = manifest["files"]
        package_cli = root / "cli" / "actorwright.exe"
        cli_path, cli_reparse = _lexical_path_reparse(package_cli)
        declared = {row["path"]: row for row in rows}
        cli_row = declared.get("cli/actorwright.exe")
        if cli_reparse is not None or not cli_path.is_file():
            raise FirewallError("package CLI entrypoint is missing or a reparse point")
        cli_sha = _sha256_file(cli_path).upper()
        entrypoint_ok = (
            set(manifest) == {"schemaVersion", "product", "version", "sourceLine",
                                  "runtime", "privateOnly", "runtimeAuthority", "commandCount",
                                  "embeddedResourceClosures", "files"}
            and cli_row is not None and cli_row["sha256"].upper() == cli_sha
            and cli_row["size"] == cli_path.stat().st_size
        )
        gates.append(GateResult("package:inventory-entrypoint", True,
                                "PASS" if entrypoint_ok else "FAIL",
                                (f"entrypoint={cli_path}", f"cliSha256={cli_sha}",
                                 f"manifest={root / 'manifest.json'}"), ()))
        if entrypoint_ok:
            source_sha = _sha256_file(source).upper()
            def capture(name: str, path: Path) -> dict[str, Any]:
                workspace = prepare_capture_workspace(
                    CAPTURE_WORKSPACE_PARENT / (CAPTURE_WORKSPACE_PREFIX + uuid.uuid4().hex))
                try:
                    return capture_cli_contract(CliTarget(
                        name, path, workspace, _sha256_file(path).upper()))
                finally:
                    if _require_owned_capture_workspace(workspace) == workspace:
                        _remove_disposable_path(workspace)

            def project(snapshot: dict[str, Any]) -> dict[str, Any]:
                return {
                    "identity": {
                        "version": snapshot["version"]["protocol1"]["version"],
                        "sourceLine": snapshot["version"]["protocol1"]["sourceLine"],
                    },
                    "commandCount": snapshot["commandCount"],
                    "orderedCommandNamesSha256": snapshot["orderedCommandNamesSha256"],
                    "commands": [_project_manifest_command(row, snapshot["behaviorFacts"])
                                 for row in snapshot["commands"]],
                    "exitMeanings": snapshot["exitMeanings"],
                    "voiceDialogueSentinels": snapshot["voiceDialogueSentinels"],
                }

            try:
                source_snapshot = capture("source", source)
                package_snapshot = capture("package", cli_path)
                expected = {
                    "identity": {
                        "version": baseline.snapshot["provenance"]["productVersion"],
                        "sourceLine": baseline.snapshot["provenance"]["sourceLine"],
                    },
                    **{key: baseline.snapshot[key] for key in (
                        "commandCount", "orderedCommandNamesSha256", "commands",
                        "exitMeanings", "voiceDialogueSentinels")},
                }
                source_observed, package_observed = project(source_snapshot), project(package_snapshot)
                for label, snapshot, observed in (
                    ("source", source_snapshot, source_observed),
                    ("package", package_snapshot, package_observed),
                ):
                    differences.extend(_capture_identity_differences(
                        snapshot, label, "comparison:source-package-baseline"))
                    differences.extend(compare_snapshots(
                        {key: value for key, value in expected.items() if key != "identity"},
                        {key: value for key, value in observed.items() if key != "identity"},
                        policy))
                differences.extend(ExactComparator(
                    ValidatedPolicy.parse(policy), layer="source-package",
                    gate="comparison:source-package-baseline").compare(
                        source_observed, package_observed))
                for field in ("version", "sourceLine"):
                    if manifest[field] != package_observed["identity"][field]:
                        differences.append(Difference(
                            "candidate-identity", "package", f"/identity/{field}",
                            package_observed["identity"][field], manifest[field],
                            "comparison:source-package-baseline"))
                if (_sha256_file(source).upper() != source_sha
                        or _sha256_file(cli_path).upper() != cli_sha):
                    raise FirewallError("source or package CLI changed during capture")
                gates.append(GateResult("comparison:source-package-baseline", True,
                                        "FAIL" if differences else "PASS",
                                        (f"differences={len(differences)}",
                                         f"sourceSha256={source_sha}",
                                         f"packageSha256={cli_sha}"), tuple(differences)))
            except (FirewallError, OSError, KeyError, TypeError, ValueError) as exc:
                gates.append(GateResult("comparison:source-package-baseline", True,
                                        "BLOCKED", (f"{type(exc).__name__}:{exc}",), ()))
        else:
            gates.append(GateResult("comparison:source-package-baseline", True,
                                    "BLOCKED", ("package entrypoint authentication failed",), ()))

    evaluation = evaluate_tier("Package", gates)
    return {
        "$schema": "urn:actorwright:compatibility:v1:report", "formatVersion": 1,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
        "tier": "Package", "baselineId": baseline.baseline_id,
        "compatible": evaluation.compatible, "verdict": evaluation.verdict,
        "fixtureBacked": False, "realInstallation": False,
        "gates": [{"gateId": item.gate_id, "required": item.required,
                   "status": item.status, "evidence": list(item.evidence),
                   "differences": [{"subject": d.subject, "layer": d.layer,
                                    "pointer": d.pointer, "expected": d.expected,
                                    "actual": d.actual, "gateId": d.gate}
                                   for d in item.differences]} for item in gates],
        "differences": [{"subject": d.subject, "layer": d.layer,
                         "pointer": d.pointer, "expected": d.expected,
                         "actual": d.actual, "gateId": d.gate} for d in differences],
        "pinRows": [],
    }


_TRANCHE_START_COMMIT = "a57f5608eef7b743b10a4c3eb8dddd0aa00f6a2a"
_SOURCE_SCOPE_ALLOWED_PREFIXES = (
    ".superpowers/sdd/2026-09-22-compatibility-firewall/",
    "contracts/compatibility/v1/", "docs/superpowers/", "tests/fixtures/compatibility-firewall/",
    "tests/release/", "tests/exchange/", "tests/NpcManager.Cli.Tests/",
    "tools/verification/", "tools/manifests/", "tools/build/", "tools/release/",
    "tools/scripts/bootstrap-dotnet10.ps1", "tests/test_docs_lint.py",
    "docs/build.md", "docs/cli-contract.md", "tasks/todo.md", "tasks/lessons.md",
)
_SOURCE_SCOPE_ALLOWED_EXACT = frozenset({
    ".gitattributes",
    ".github/workflows/build.yml",
    "README.md", "docs/product-capabilities.md",
    "docs/observability.md",
    "docs/security/public-release-gate.md",
    "docs/releases/1.0.0-preview.276.md",
    "docs/releases/1.0.0-preview.277.md",
    "docs/releases/1.0.0-preview.278.md",
    "docs/releases/1.0.0-preview.279.md",
    "docs/releases/1.0.0-preview.280.md",
    "src/NpcManager.Application/ActorwrightObservabilityEventSource.cs",
    "src/NpcManager.Application/BuildInfo.cs",
    "src/NpcManager.Assets/SkyrimMeshPreviewService.cs",
    "src/NpcManager.Cli/PackageCommandHandler.cs",
    "src/NpcManager.Cli/Program.cs",
    "src/NpcManager.Cli/ProtocolV2Runner.cs",
    "src/NpcManager.Desktop/App.xaml.cs",
    "src/NpcManager.Desktop/AsyncCommand.cs",
    "src/NpcManager.Desktop/CreateNpcViewModel.cs",
    "src/NpcManager.Desktop/DesktopCrashReportWriter.cs",
    "src/NpcManager.Desktop/DesktopFailureEvidenceListener.cs",
    "src/NpcManager.Desktop/DesktopStartupFailureBoundary.cs",
    "src/NpcManager.Desktop/ExistingNpcEditViewModel.cs",
    "src/NpcManager.Desktop/MainWindow.xaml.cs",
    "src/NpcManager.Desktop/RaceMenuNpcBuildViewModel.cs",
    "src/NpcManager.Desktop/ReferencePresetAuthoringViewModel.cs",
    "src/NpcManager.Desktop/RaceMenuNpcDesktopComposition.cs",
    "src/NpcManager.Desktop/SkyrimArmorProductionWorkspaceViewModel.cs",
    "src/NpcManager.Desktop/SkyrimBodyEditWorkspaceViewModel.cs",
    "src/NpcManager.Desktop/SkyrimCharGenOptionsProductionDesktopComposition.cs",
    "src/NpcManager.Desktop/SkyrimCharGenOptionsProductionWorkspaceViewModel.cs",
    "src/NpcManager.Desktop/SkyrimFaceEditWorkspaceViewModel.cs",
    "src/NpcManager.Desktop/SkyrimHeadPartEditViewModel.cs",
    "src/NpcManager.Desktop/SkyrimHeadPartPickerWindow.xaml",
    "src/NpcManager.Desktop/SkyrimLeveledListProductionWorkspaceViewModel.cs",
    "src/NpcManager.Desktop/SkyrimMeshPickerWindow.xaml",
    "src/NpcManager.Desktop/SkyrimNpcVoiceViewModel.cs",
    "src/NpcManager.Desktop/SkyrimOutfitProductionWorkspaceViewModel.cs",
    "src/NpcManager.Desktop/SkyrimSavePackageViewModel.cs",
    "src/NpcManager.Desktop/SkyrimSelectiveAppearancePasteWorkspaceViewModel.cs",
    "src/NpcManager.Desktop/WorkspaceShellViewModel.cs",
    "src/NpcManager.Domain/ActorwrightWorkspace.cs",
    "src/NpcManager.FaceGen/TexconvFaceTintTextureDecoder.cs",
    "src/NpcManager.FaceGen/TexconvFaceTintTextureEncoder.cs",
    "src/NpcManager.FaceGen/TexconvProcessRunner.cs",
    "src/NpcManager.Formats.Bethesda/BethesdaNpcStandaloneCopyAdapter.cs",
    "src/NpcManager.Infrastructure/DesktopEvidenceFileStore.cs",
    "src/NpcManager.Infrastructure/DesktopLaunchService.cs",
    "src/NpcManager.Infrastructure/FaceGeomHairRegionsPinnedFileSystem.Leases.cs",
    "src/NpcManager.Infrastructure/FaceGeomHairRegionsPinnedFileSystem.cs",
    "src/NpcManager.Infrastructure/ArchiveConsistencyService.cs",
    "src/NpcManager.Infrastructure/ArmorAddonModelProposalService.cs",
    "src/NpcManager.Infrastructure/ArmorAddonProposalService.cs",
    "src/NpcManager.Infrastructure/ArmorDamageResistanceService.cs",
    "src/NpcManager.Infrastructure/ArmorProposalService.cs",
    "src/NpcManager.Infrastructure/ChangeActionService.cs",
    "src/NpcManager.Infrastructure/ChangeTrackingService.cs",
    "src/NpcManager.Infrastructure/LeveledListResolveService.cs",
    "src/NpcManager.Infrastructure/LeveledListProposalService.cs",
    "src/NpcManager.Infrastructure/MaterialSwapProposalService.cs",
    "src/NpcManager.Infrastructure/ObjectTemplatePropertyProposalService.cs",
    "src/NpcManager.Infrastructure/ObjectTemplateProposalService.cs",
    "src/NpcManager.Infrastructure/OutfitProposalService.cs",
    "src/NpcManager.Infrastructure/ReferencePresetSessionService.cs",
    "src/NpcManager.Presets/PresetService.cs",
    "src/NpcManager.Infrastructure/AgentWorkflowBundleTransitionService.cs",
    "src/NpcManager.Infrastructure/KOnlyWorkspacePolicy.cs",
    "src/NpcManager.Infrastructure/LocalOperationJournal.cs",
    "src/NpcManager.Infrastructure/PackageArchiveService.cs",
    "src/NpcManager.Infrastructure/RuntimeScriptBuildService.cs",
    "src/NpcManager.Infrastructure/WorkspacePolicyShadow.cs",
    "src/NpcManager.Infrastructure/WindowsAdmittedNativeRuntimeLoader.cs",
    "src/NpcManager.Pipeline/ActorAssemblyPreflightService.cs",
    "src/NpcManager.Pipeline/BlankNpcBuildService.PathSafety.cs",
    "src/NpcManager.Pipeline/SkyrimFollowerFinishService.Apply.cs",
    "src/NpcManager.Pipeline/SkyrimNpcFinishCoreService.Packaging.cs",
    "src/NpcManager.Rendering/BlenderFaceGeomHairRegionsRenderer.cs",
    "src/NpcManager.Rendering/BlenderNpcVisualPreviewRenderer.cs",
    "src/NpcManager.Rendering/BlenderPreviewImageRenderer.cs",
    "src/NpcManager.Rendering/BlenderPreviewNifExporter.Process.cs",
    "src/NpcManager.Rendering/BlenderFaceGeomHairRegionsRenderer.Status.cs",
    "src/NpcManager.Rendering/PreviewArtifactFileWriter.cs",
    "src/NpcManager.Rendering/PreviewNifExportService.cs",
    "src/NpcManager.Rendering/PreviewRerollService.cs",
    "src/NpcManager.Rendering/PreviewSceneService.cs",
    "src/NpcManager.Rendering/PreviewSceneService.Validation.cs",
    "tests/NpcManager.Architecture.Tests/AgentProtocolContractTests.cs",
    "tests/NpcManager.Architecture.Tests/AgentReviewReceiptTests.cs",
    "tests/NpcManager.Architecture.Tests/AgentWorkflowContractTests.cs",
    "tests/NpcManager.Architecture.Tests/ActorAssemblyPreflightServiceTests.cs",
    "tests/NpcManager.Architecture.Tests/BethesdaNpcVisualSourceComposerTests.cs",
    "tests/NpcManager.Architecture.Tests/BoundedReadinessArchitectureTests.cs",
    "tests/NpcManager.Architecture.Tests/ExternalHeadPartDependencyDiscoveryTests.cs",
    "tests/NpcManager.Architecture.Tests/ExternalHeadPartRouteGraphTests.cs",
    "tests/NpcManager.Architecture.Tests/FaceGeomHairRegionsTransactionTests.cs",
    "tests/NpcManager.Architecture.Tests/FaceGeomRecordAppearanceTests.cs",
    "tests/NpcManager.Architecture.Tests/LocalOperationJournalTests.cs",
    "tests/NpcManager.Architecture.Tests/PhysicalReparseFixture.cs",
    "tests/NpcManager.Architecture.Tests/Program.cs",
    "tests/NpcManager.Architecture.Tests/PreviewArtifactFileWriterTests.cs",
    "tests/NpcManager.Architecture.Tests/Preview254ExternalSmpArchitectureTestRegistry.cs",
    "tests/NpcManager.Architecture.Tests/RaceMenuStandaloneSchema8Tests.cs",
    "tests/NpcManager.Architecture.Tests/SkyrimNpcFinishCoreOutfitRaceTests.cs",
    "tests/NpcManager.Architecture.Tests/WorkspacePolicyShadowTests.cs",
    "tests/NpcManager.Architecture.Tests/XttsClientTests.cs",
    "tests/NpcManager.Assets.Tests/FaceBakeAuthorityLoaderTests.cs",
    "tests/NpcManager.Assets.Tests/NpcManager.Assets.Tests.csproj",
    "tests/NpcManager.Assets.Tests/Program.cs",
    "tests/NpcManager.BethesdaFaceRouting.Tests/Program.cs",
    "tests/Directory.Build.props",
    "tests/NpcManager.Cli.Tests/FaceGeomHairRegionsCliTests.cs",
    "tests/NpcManager.Cli.Tests/GoldenSkyrimWorkflowResumptionTests.cs",
    "tests/NpcManager.Cli.Tests/NpcManager.Cli.Tests.csproj",
    "tests/NpcManager.Cli.Tests/OutputOwnedHeadPartTests.cs",
    "tests/NpcManager.Cli.Tests/Program.cs",
    "tests/NpcManager.Cli.Tests/ProtocolV2FinishCoreTests.cs",
    "tests/NpcManager.Desktop.Smoke/AsyncCommandTests.cs",
    "tests/NpcManager.Desktop.Smoke/DesktopCrashReporterTests.cs",
    "tests/NpcManager.Desktop.Smoke/DesktopFailureEvidenceTests.cs",
    "tests/NpcManager.Desktop.Smoke/Program.cs",
    "tests/NpcManager.Desktop.Smoke/DesktopStartupFailureBoundaryTests.cs",
    "tests/NpcManager.Desktop.Smoke/SkyrimMainWorkspaceViewModelTests.cs",
    "tests/NpcManager.Desktop.Smoke/Preview254ExternalSmpDesktopTestRegistry.cs",
    "tests/NpcManager.FaceGeomOrchestration.Tests/OrchestrationFixture.cs",
    "tests/NpcManager.FaceGeomOrchestration.Tests/Program.cs",
    "tests/NpcManager.FaceGen.Tests/Program.cs",
    "tests/NpcManager.Gate1.Tests/Program.cs",
    "tests/NpcManager.Gate2.PipelineIntegration.Tests/Program.cs",
    "tests/NpcManager.Gate2.Tests/Program.cs",
    "tests/NpcManager.NpcCreationAppearance.Tests/Program.cs",
    "tests/NpcManager.ReferencePreset.Tests/NativeRuntimeLoaderTests.cs",
    "tests/NpcManager.ReferencePreset.Tests/DependencyAdmissionTests.cs",
    "tests/NpcManager.ReferencePreset.Tests/MediaPipeInferenceTests.cs",
    "tests/NpcManager.ReferencePreset.Tests/MediaPipeNativeApiTests.cs",
    "tests/NpcManager.ReferencePreset.Tests/Program.cs",
    "tests/NpcManager.ReferencePreset.Tests/ReferencePresetSessionTests.cs",
    "tests/TestInfrastructure/StandaloneSelectorInventory.cs",
    "tests/architecture/test_standalone_test_registry.py",
    "tests/architecture/test_validate_architecture.py",
    "tests/conftest.py",
    "tools/architecture/fixture-path-allowlist.json",
    "tools/architecture/validate_architecture.py",
    "tools/architecture/validate_standalone_test_registry.py",
    "tools/hardening/finalize_package.py",
    "tools/hardening/generate_sbom.py",
    "tests/standalone-test-matrix.json",
})
_SOURCE_SCOPE_PROTECTED_PREFIXES = (
    "src/", "artifacts/", "releases/", ".git/",
)


_PUBLIC_SOURCE_SCOPE_MARKER_PATH = "tools/manifests/public-source-scope.json"
_PUBLIC_SOURCE_SCOPE_MARKER = {"schemaVersion": 1, "mode": "public-root"}


def _public_source_scope_enabled(revision: str = "HEAD") -> bool:
    marker_path = _PUBLIC_SOURCE_SCOPE_MARKER_PATH
    entries = [entry for entry in _git(
        REPOSITORY_ROOT, "ls-tree", "-z", revision, "--", marker_path
    ).split(b"\0") if entry]
    if not entries:
        return False
    if len(entries) != 1 or b"\t" not in entries[0]:
        raise FirewallError("public source-scope marker entry is ambiguous")
    metadata, path = entries[0].split(b"\t", 1)
    fields = metadata.decode("ascii").split()
    if (len(fields) != 3 or fields[0] != "100644" or fields[1] != "blob"
            or path.decode("utf-8") != marker_path):
        raise FirewallError("public source-scope marker must be a tracked regular file")
    try:
        marker = json.loads(_git(
            REPOSITORY_ROOT, "show", f"{revision}:{marker_path}"
        ).decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise FirewallError("public source-scope marker is invalid JSON") from exc
    if (not isinstance(marker, dict)
            or set(marker) != set(_PUBLIC_SOURCE_SCOPE_MARKER)
            or type(marker.get("schemaVersion")) is not int
            or marker != _PUBLIC_SOURCE_SCOPE_MARKER):
        raise FirewallError("public source-scope marker has an unsupported format")
    return True


def _public_source_scope_gate() -> GateResult:
    shallow = _git(REPOSITORY_ROOT, "rev-parse", "--is-shallow-repository").decode().strip()
    if shallow != "false":
        raise FirewallError("public source-scope requires non-shallow history")
    replacements = _git(REPOSITORY_ROOT, "replace", "-l").decode().strip()
    if replacements:
        raise FirewallError("public source-scope refuses replacement refs")
    grafts_path = Path(_git(
        REPOSITORY_ROOT, "rev-parse", "--git-path", "info/grafts"
    ).decode().strip())
    if not grafts_path.is_absolute():
        grafts_path = REPOSITORY_ROOT / grafts_path
    if grafts_path.exists():
        raise FirewallError("public source-scope refuses grafted history")

    head = _git(REPOSITORY_ROOT, "rev-parse", "HEAD").decode().strip()
    tree = _git(REPOSITORY_ROOT, "rev-parse", "HEAD^{tree}").decode().strip()
    roots = tuple(
        root for root in _git(
            REPOSITORY_ROOT, "rev-list", "--max-parents=0", "HEAD"
        ).decode("ascii").splitlines() if root
    )
    if len(roots) != 1:
        raise FirewallError("public source-scope requires a single history root")
    root = roots[0]
    if not _public_source_scope_enabled(root):
        raise FirewallError("public history root does not contain the public source-scope marker")
    if head.casefold() == root.casefold():
        tracked_entries = _git(
            REPOSITORY_ROOT, "ls-tree", "-r", "-z", "--full-tree", "HEAD"
        )
        tracked_count = sum(bool(entry) for entry in tracked_entries.split(b"\0"))
        return GateResult("audit:source-scope", True, "PASS", (
            "mode=public-root-baseline", f"rootCommit={root}",
            f"commit={head}", f"tree={tree}",
            f"trackedEntryCount={tracked_count}",
        ), ())

    ancestor = _git(REPOSITORY_ROOT, "merge-base", root, head).decode().strip()
    if ancestor.casefold() != root.casefold():
        raise FirewallError("public history root is not an ancestor of HEAD")
    changed = _git(
        REPOSITORY_ROOT, "diff", "--name-only", "-z", root, head
    ).decode("utf-8").strip("\0")
    paths = tuple(path for path in changed.split("\0") if path)
    if not paths:
        raise FirewallError("source-scope diff has no changed paths")
    refused = [path for path in paths if (
        path not in _SOURCE_SCOPE_ALLOWED_EXACT
        and (path.startswith(_SOURCE_SCOPE_PROTECTED_PREFIXES)
             or not path.startswith(_SOURCE_SCOPE_ALLOWED_PREFIXES))
    )]
    if refused:
        raise FirewallError(f"source-scope diff contains out-of-scope paths: {refused}")
    return GateResult("audit:source-scope", True, "PASS", (
        "mode=public-root-diff", f"rootCommit={root}", f"commit={head}",
        f"tree={tree}", f"changedPathCount={len(paths)}",
        *[f"changedPath={path}" for path in paths],
    ), ())


def _private_tranche_anchor_exists() -> bool:
    arguments = [
        "git", "-c", f"safe.directory={REPOSITORY_ROOT.as_posix()}",
        "-c", "core.longpaths=true", "rev-parse", "--verify", "--quiet",
        f"{_TRANCHE_START_COMMIT}^{{commit}}",
    ]
    try:
        completed = _run_bounded_process(
            arguments,
            cwd=str(REPOSITORY_ROOT),
            env=os.environ.copy(),
            shell=False,
            timeout=120,
            max_output_bytes=4096,
        )
    except (FirewallError, OSError) as exc:
        raise FirewallError("private source anchor could not be inspected") from exc
    if completed.returncode == 0:
        observed = completed.stdout.decode("ascii", errors="strict").strip()
        if observed.casefold() != _TRANCHE_START_COMMIT.casefold():
            raise FirewallError("private source anchor resolved to a different commit")
        return True
    if completed.returncode == 1 and not completed.stdout and not completed.stderr:
        return False
    detail = (completed.stderr or completed.stdout)[-2048:].decode(
        "utf-8", errors="replace"
    )
    raise FirewallError(f"private source anchor inspection failed: {detail}")


def _source_scope_gate() -> GateResult:
    """Bind the allowed source history and audit descendant changes."""
    try:
        status = _git(REPOSITORY_ROOT, "status", "--porcelain=v1", "--untracked-files=all")
        if status:
            raise FirewallError("tranche audit requires a clean tracked and untracked worktree")
        if not _private_tranche_anchor_exists():
            if not _public_source_scope_enabled():
                raise FirewallError(
                    "private source anchor is unavailable and no public source-scope marker is committed"
                )
            return _public_source_scope_gate()

        start = _git(REPOSITORY_ROOT, "rev-parse", _TRANCHE_START_COMMIT).decode().strip()
        head = _git(REPOSITORY_ROOT, "rev-parse", "HEAD").decode().strip()
        tree = _git(REPOSITORY_ROOT, "rev-parse", "HEAD^{tree}").decode().strip()
        if start.casefold() != _TRANCHE_START_COMMIT:
            raise FirewallError("tranche start commit mismatch")
        ancestor = _git(REPOSITORY_ROOT, "merge-base", start, head).decode().strip()
        if ancestor.casefold() != start.casefold():
            raise FirewallError("tranche start is not an ancestor of HEAD")
        changed = _git(REPOSITORY_ROOT, "diff", "--name-only", "-z", start, head).decode("utf-8").strip("\0")
        paths = tuple(path for path in changed.split("\0") if path)
        if not paths:
            raise FirewallError("tranche diff has no changed paths")
        refused = [path for path in paths if (
            path not in _SOURCE_SCOPE_ALLOWED_EXACT
            and (path.startswith(_SOURCE_SCOPE_PROTECTED_PREFIXES)
                 or not path.startswith(_SOURCE_SCOPE_ALLOWED_PREFIXES))
        )]
        if refused:
            raise FirewallError(f"tranche diff contains out-of-scope paths: {refused}")
        return GateResult("audit:source-scope", True, "PASS", (
            "mode=private-tranche-diff", f"startCommit={start}",
            f"commit={head}", f"tree={tree}",
            f"changedPathCount={len(paths)}",
            *[f"changedPath={path}" for path in paths],
        ), ())
    except (FirewallError, OSError, UnicodeError) as exc:
        return GateResult("audit:source-scope", True, "BLOCKED", (str(exc),), ())


def _full_identity_differences(
    release: dict[str, Any], release_source_line: str, head: str, tree: str,
    source: dict[str, Any], package: dict[str, Any],
) -> tuple[Difference, ...]:
    """Bind two observed CLI identities to one verified release and clean HEAD."""
    differences: list[Difference] = []
    for pointer, expected, actual in (
        ("/sourceCommit", release.get("sourceCommit"), head),
        ("/sourceTree", release.get("sourceTree"), tree),
        ("/source/version", release.get("version"), source.get("version")),
        ("/source/sourceLine", release_source_line, source.get("sourceLine")),
        ("/package/version", release.get("version"), package.get("version")),
        ("/package/sourceLine", release_source_line, package.get("sourceLine")),
    ):
        equal = (isinstance(expected, str) and isinstance(actual, str)
                 and (expected.casefold() == actual.casefold()
                      if pointer in {"/sourceCommit", "/sourceTree"}
                      else expected == actual))
        if not equal:
            differences.append(Difference("release-identity", "source-package", pointer,
                                          expected, actual, "comparison:source-package-baseline"))
    return tuple(differences)


def _fresh_journey_runner() -> tuple[Path, str, str, tuple[str, ...]]:
    """Rebuild the canonical runner with the pinned SDK and authenticate its bytes."""
    sdk_sha, runner_sha, _architecture_sha, build_evidence = _build_source_selector_projects()
    runner = (REPOSITORY_ROOT / "tests/NpcManager.Cli.Tests/bin/Release/net10.0"
              / "NpcManager.Cli.Tests.dll")
    if not runner.is_file() or _sha256_file(runner).upper() != runner_sha.upper():
        raise FirewallError("fresh journey runner does not match authenticated build")
    return runner, runner_sha, sdk_sha, build_evidence


def _full_release_gates(
    baseline_id: str, source_cli: str, release_root: str, release_zip: str,
    pin_rows: list[PinRow] | None = None,
) -> tuple[GateResult, ...]:
    """Collect independent current-release proof without fabricating a gate."""
    from tools.release import verify_release

    remaining = tuple(gate_id for gate_id in required_gate_ids("Full")
                      if gate_id not in required_gate_ids("Source"))
    gates: dict[str, GateResult] = {}

    def record(gate_id: str, status: str, *evidence: str,
               differences: tuple[Difference, ...] = ()) -> None:
        gates[gate_id] = GateResult(gate_id, True, status, evidence or ("no evidence",), differences)

    try:
        root, root_reparse = _lexical_path_reparse(release_root)
        archive, zip_reparse = _lexical_path_reparse(release_zip)
        source, source_reparse = _lexical_path_reparse(source_cli)
        approved_release_roots = (REPOSITORY_ROOT, CAPTURE_WORKSPACE_PARENT.parent)
        if (root_reparse or zip_reparse or source_reparse
                or not root.is_absolute() or not archive.is_absolute()
                or root.drive.casefold() != "k:" or archive.drive.casefold() != "k:"
                or not any(root.is_relative_to(parent) for parent in approved_release_roots)
                or not any(archive.is_relative_to(parent) for parent in approved_release_roots)
                or not root.is_dir() or not archive.is_file()):
            raise FirewallError("Full release root and ZIP must be ordinary K-local build artifacts")
        canonical = REPOSITORY_ROOT / "src/NpcManager.Cli/bin/Release/net10.0"
        if (not source.is_absolute() or source.parent != canonical
                or source.name not in {"actorwright.exe", "actorwright.dll"}
                or not source.is_file()):
            raise FirewallError("Full source CLI must be the canonical Release CLI")
        baseline = load_baseline(baseline_id)
        release_result = verify_release.verify(root, archive, defer_compatibility=True)
        graph = verify_release_root_compatibility(root, archive)
        if (release_result.get("status") != "PASS" or release_result.get("zipVerified") is not True
                or not graph.compatible or graph.verdict != "FULL_COMPATIBLE"):
            raise FirewallError(f"sealed release verification failed: {graph.errors}")
        if (not graph.pin_rows
                or {row.pin_id for row in graph.pin_rows} != FULL_ZIP_PIN_IDS
                or {row.identity_class for row in graph.pin_rows} != FULL_ZIP_PIN_CLASSES
                or any(row.status != "PASS" for row in graph.pin_rows)):
            raise FirewallError("sealed release Full pin inventory is incomplete")
        if pin_rows is not None:
            pin_rows.extend(graph.pin_rows)
        release = _require_object(_load_json(root / "actorwright-release.json"), "Full release")
        summary = _require_object(_load_json(root / "evidence/test-summary.json"), "Full test summary")
        release_capabilities = _require_object(_load_json(root / "evidence/capabilities.json"),
                                               "Full release capabilities")
        release_source_line = _require_string(release_capabilities.get("sourceLine"),
                                              "Full release source line")
        head = _git(REPOSITORY_ROOT, "rev-parse", "HEAD").decode().strip()
        tree = _git(REPOSITORY_ROOT, "rev-parse", "HEAD^{tree}").decode().strip()
        zip_length = archive.stat().st_size
        zip_sha = _sha256_file(archive).upper()
        record("verification:release", "PASS", f"root={root}", f"zip={archive}",
               f"zipLength={zip_length}", f"zipSha256={zip_sha}",
               f"files={release_result['files']}", f"pinRows={len(graph.pin_rows)}")
        if (summary.get("status") == "PASS" and summary.get("projectsCompiled") == 25
                and summary.get("warnings") == 0 and summary.get("errors") == 0):
            record("build:pinned-release-solution", "PASS", "projects=25", "warnings=0",
                   "errors=0", f"buildLog={root / 'evidence/canonical-build.log'}",
                   f"sourceCommit={release['sourceCommit']}")
        else:
            record("build:pinned-release-solution", "FAIL", "release build summary mismatch")
    except (FirewallError, verify_release.ReleaseError, OSError, ValueError,
            KeyError, TypeError, zipfile.BadZipFile) as exc:
        record("verification:release", "BLOCKED", f"{type(exc).__name__}:{exc}")
        return tuple(gates.get(gate_id, GateResult(gate_id, True, "BLOCKED",
                     ("sealed release verification unavailable",), ())) for gate_id in remaining)

    try:
        policy = _require_object(_load_json(DEFAULT_BASELINE_ROOT / "compatibility-policy.json"),
                                 "compatibility policy")
        ValidatedPolicy.parse(policy)
        package_cli = root / "cli/actorwright.exe"
        package_sha = _sha256_file(package_cli).upper()
        evidence = SealedPackageEvidence(root, archive, zip_length, zip_sha,
                                         release["sourceTag"], release["sourceCommit"],
                                         release["sourceTree"])
        workspace = prepare_capture_workspace(CAPTURE_WORKSPACE_PARENT /
                                              (CAPTURE_WORKSPACE_PREFIX + uuid.uuid4().hex))
    except (FirewallError, OSError, KeyError, TypeError, ValueError) as exc:
        record("comparison:source-package-baseline", "BLOCKED", f"{type(exc).__name__}:{exc}")
        return tuple(gates.get(gate_id, GateResult(gate_id, True, "BLOCKED",
                     ("current package capture unavailable",), ())) for gate_id in remaining)
    try:
        target = CliTarget("package", package_cli, workspace, package_sha)
        for result in run_voice_dialogue_package_probes(target, package_evidence=evidence):
            gates[result.gate_id] = result
        package_snapshot = capture_cli_contract(target)
        source_sha = _sha256_file(source).upper()
        source_snapshot = capture_cli_contract(CliTarget(
            "source", source, workspace, source_sha))
        def project(snapshot: dict[str, Any]) -> dict[str, Any]:
            return {
                "identity": {"version": snapshot["version"]["protocol1"]["version"],
                             "sourceLine": snapshot["version"]["protocol1"]["sourceLine"]},
                "commandCount": snapshot["commandCount"],
                "orderedCommandNamesSha256": snapshot["orderedCommandNamesSha256"],
                "commands": [_project_manifest_command(row, snapshot["behaviorFacts"])
                             for row in snapshot["commands"]],
                "exitMeanings": snapshot["exitMeanings"],
                "voiceDialogueSentinels": snapshot["voiceDialogueSentinels"],
            }
        expected = {
            "identity": {"version": baseline.snapshot["provenance"]["productVersion"],
                         "sourceLine": baseline.snapshot["provenance"]["sourceLine"]},
            **{key: baseline.snapshot[key] for key in (
                "commandCount", "orderedCommandNamesSha256", "commands",
                "exitMeanings", "voiceDialogueSentinels")},
        }
        observed_source, observed_package = project(source_snapshot), project(package_snapshot)
        baseline_behavior = {key: value for key, value in expected.items() if key != "identity"}
        source_behavior = {key: value for key, value in observed_source.items() if key != "identity"}
        package_behavior = {key: value for key, value in observed_package.items() if key != "identity"}
        differences = tuple(compare_snapshots(baseline_behavior, source_behavior, policy)) + tuple(
            compare_snapshots(baseline_behavior, package_behavior, policy)) + tuple(ExactComparator(
                ValidatedPolicy.parse(policy), layer="source-package",
                gate="comparison:source-package-baseline").compare(
                    observed_source, observed_package))
        differences += _full_identity_differences(
            release, release_source_line, head, tree,
            observed_source["identity"], observed_package["identity"])
        for label, snapshot in (("source", source_snapshot), ("package", package_snapshot)):
            differences += _capture_identity_differences(
                snapshot, label, "comparison:source-package-baseline")
        if (_sha256_file(source).upper() != source_sha
                or _sha256_file(package_cli).upper() != package_sha):
            raise FirewallError("source or sealed package CLI changed during Full capture")
        record("comparison:source-package-baseline", "FAIL" if differences else "PASS",
               f"sourceSha256={source_sha}",
               f"packageSha256={package_sha}", f"differences={len(differences)}",
               differences=differences)
    except (FirewallError, OSError, KeyError, TypeError, ValueError) as exc:
        record("comparison:source-package-baseline", "BLOCKED", f"{type(exc).__name__}:{exc}")
    finally:
        try:
            if _require_owned_capture_workspace(workspace) == workspace:
                _remove_disposable_path(workspace)
        except (FirewallError, OSError) as exc:
            record("comparison:source-package-baseline", "BLOCKED",
                   f"owned CLI capture cleanup failed: {exc}")

    matrix = REPOSITORY_ROOT / "tests/standalone-test-matrix.json"
    try:
        from tools.architecture import validate_standalone_test_registry
        errors = validate_standalone_test_registry.validate(REPOSITORY_ROOT)
        rows = _require_array(_require_object(_load_json(matrix), "matrix").get("tests"), "matrix/tests")
        counts = {kind: sum(row.get("classification") == kind for row in rows)
                  for kind in ("runnable", "fixture-bound", "unverified")}
        if errors or counts["unverified"] or counts["runnable"] == 0:
            raise FirewallError(f"standalone matrix invalid: {errors}; counts={counts}")
        sdk_length, sdk_sha = _authenticated_pinned_sdk()
        completed = _run_bounded_process([
            sys.executable, str(REPOSITORY_ROOT / "tools/build/run_standalone_selectors.py"),
            "--dotnet", str(PINNED_DOTNET), "--project-root", str(REPOSITORY_ROOT),
            "--matrix", str(matrix), "--configuration", "Release",
        ], cwd=str(REPOSITORY_ROOT), env=os.environ.copy(), shell=False,
            timeout=7200, max_output_bytes=CLI_MAX_OUTPUT_BYTES)
        if completed.returncode != 0 or b"standalone selector launcher: PASS" not in completed.stdout:
            raise FirewallError(f"standalone selector matrix failed: exit={completed.returncode}")
        record("selectors:complete-standalone-matrix", "PASS", f"matrixSha256={_sha256_file(matrix).upper()}",
               f"runnablePass={counts['runnable']}", f"fixtureBoundSkip={counts['fixture-bound']}",
               "unverified=0", f"sdkLength={sdk_length}", f"sdkSha256={sdk_sha}",
               f"stdoutSha256={_sha256(completed.stdout).upper()}")
        record("architecture:standalone-registry", "PASS",
               f"runnable={counts['runnable']}", f"fixtureBound={counts['fixture-bound']}",
               "unverified=0",
               f"matrixSha256={_sha256_file(matrix).upper()}")
    except (FirewallError, OSError, ValueError, KeyError, TypeError) as exc:
        record("selectors:complete-standalone-matrix", "BLOCKED", str(exc))
        record("architecture:standalone-registry", "BLOCKED", str(exc))

    try:
        from tools.architecture import validate_repository_independence
        errors = validate_repository_independence.validate_repository(REPOSITORY_ROOT)
        record("architecture:repository-independence", "FAIL" if errors else "PASS",
               f"errors={errors}", f"root={REPOSITORY_ROOT}")
    except (OSError, ValueError) as exc:
        record("architecture:repository-independence", "BLOCKED", str(exc))

    try:
        pytest_artifacts = REPOSITORY_ROOT / "artifacts"
        pytest_base = pytest_artifacts / f"pytest-full-{uuid.uuid4().hex}"
        pytest_environment = os.environ.copy()
        pytest_environment.update({
            "GIT_CONFIG_COUNT": "1",
            "GIT_CONFIG_KEY_0": "safe.directory",
            "GIT_CONFIG_VALUE_0": "*",
            "ACTORWRIGHT_TEST_DOTNET": str(PINNED_DOTNET),
        })
        try:
            completed = _run_bounded_process([
                sys.executable, "-m", "pytest", str(REPOSITORY_ROOT / "tests/test_permission_profile.py"),
                str(REPOSITORY_ROOT / "tests/independence"), str(REPOSITORY_ROOT / "tests/exchange"),
                str(REPOSITORY_ROOT / "tests/release"),
                str(REPOSITORY_ROOT / "tests/architecture/test_validate_architecture.py"),
                str(REPOSITORY_ROOT / "tests/architecture/test_standalone_test_registry.py"), "-q",
                "--basetemp", str(pytest_base),
            ], cwd=str(REPOSITORY_ROOT), env=pytest_environment, shell=False,
                timeout=3600, max_output_bytes=CLI_MAX_OUTPUT_BYTES)
        except (FirewallError, OSError) as exc:
            raise FirewallError(
                f"Python suite invocation failed; temporary output is preserved at {pytest_base}: {exc}"
            ) from exc
        output = completed.stdout.decode("utf-8", errors="replace")
        summary_match = re.search(r"(?m)^(\d+) passed(?:, (\d+) skipped)?(?:, (\d+) warnings?)?", output)
        if completed.returncode != 0 or not summary_match:
            raise FirewallError(
                f"Python suite failed: exit={completed.returncode}; "
                f"temporary output is preserved at {pytest_base}"
            )
        try:
            _, pytest_reparse = _lexical_path_reparse(pytest_base)
            _, artifacts_reparse = _lexical_path_reparse(pytest_artifacts)
            if pytest_reparse is not None or artifacts_reparse is not None:
                raise FirewallError(
                    f"Python suite cleanup refused a reparse-point path; temporary output is preserved at {pytest_base}"
                )
            if pytest_base.exists():
                if not pytest_base.is_dir() or not pytest_artifacts.is_dir():
                    raise FirewallError(
                        f"Python suite cleanup refused a non-directory path; temporary output is preserved at {pytest_base}"
                    )
                from tools.storage_retention import assert_inside, remove_exact_path

                resolved_root = REPOSITORY_ROOT.resolve(strict=True)
                resolved_artifacts = assert_inside(pytest_artifacts, REPOSITORY_ROOT)
                resolved_pytest_base = assert_inside(pytest_base, REPOSITORY_ROOT)
                if (
                    resolved_artifacts.parent != resolved_root
                    or resolved_artifacts.name.casefold() != "artifacts"
                    or resolved_pytest_base.parent != resolved_artifacts
                    or resolved_pytest_base.name != pytest_base.name
                    or not re.fullmatch(r"pytest-full-[0-9a-f]{32}", resolved_pytest_base.name)
                ):
                    raise FirewallError(
                        f"Python suite cleanup refused a path outside its exact run-owned artifacts child; "
                        f"temporary output is preserved at {pytest_base}"
                    )
                remove_exact_path(resolved_pytest_base)
                if resolved_pytest_base.exists():
                    raise FirewallError(
                        f"Python suite cleanup left temporary output at {pytest_base}"
                    )
        except (FirewallError, OSError, RuntimeError) as exc:
            raise FirewallError(
                f"Python suite passed but disposable-output cleanup failed at {pytest_base}: {exc}"
            ) from exc
        record("verification:python-suite", "PASS", f"passed={summary_match.group(1)}",
               f"skipped={summary_match.group(2) or 0}", f"warnings={summary_match.group(3) or 0}",
               f"stdoutSha256={_sha256(completed.stdout).upper()}")
    except (FirewallError, OSError, ValueError) as exc:
        record("verification:python-suite", "BLOCKED", str(exc))

    journey_ids = ("journey:create-to-package", "journey:wave-b-v1")
    owned_journey_root: Path | None = None
    journey_token: str | None = None
    registered = False
    try:
        if _source_scope_gate().status != "PASS":
            raise FirewallError("structured journeys require a committed clean source scope")
        owned_journey_root, journey_token = create_owned_journey_capture(CAPTURE_WORKSPACE_PARENT)
        journey_worktree = owned_journey_root / "worktree"
        journey_output = owned_journey_root / "observations"
        if (_lexical_path_reparse(journey_worktree)[1] is not None
                or _lexical_path_reparse(journey_output)[1] is not None
                or journey_worktree.exists() or journey_output.exists()):
            raise FirewallError("owned journey paths are not fresh ordinary children")
        head = _git(REPOSITORY_ROOT, "rev-parse", "HEAD").decode().strip()
        if head.casefold() != release["sourceCommit"].casefold():
            raise FirewallError("journey harness commit differs from sealed release")
        tree = _git(REPOSITORY_ROOT, "rev-parse", "HEAD^{tree}").decode().strip()
        source_root = "tests/NpcManager.Cli.Tests"
        source_tree = _git(REPOSITORY_ROOT, "rev-parse", f"HEAD:{source_root}").decode().strip()
        listing = _git(REPOSITORY_ROOT, "ls-tree", "-r", "--full-tree", "HEAD", "--", source_root)
        harness = {"commit": head, "tree": tree, "sourceClosure": {
            "root": source_root, "tree": source_tree,
            "fileCount": len([line for line in listing.splitlines() if line]),
            "sha256": _sha256(listing).upper(),
        }}
        sdk_length, sdk_sha = _authenticated_pinned_sdk()
        runner, runner_sha, rebuilt_sdk_sha, runner_build_evidence = _fresh_journey_runner()
        if rebuilt_sdk_sha.casefold() != sdk_sha.casefold():
            raise FirewallError("fresh journey runner SDK identity changed")
        _git(REPOSITORY_ROOT, "worktree", "add", "--detach", str(journey_worktree), head)
        registered = True
        observations = run_structured_journeys(
            repository_root=journey_worktree, runner_dll=runner,
            expected_runner_sha256=runner_sha, target_cli=package_cli,
            expected_target_sha256=package_sha, dotnet_path=PINNED_DOTNET,
            expected_dotnet_sha256=sdk_sha, output_root=journey_output,
            expected_harness=harness)
        baseline_journeys = {
            member["path"].split("/")[-1].removesuffix(".json"):
                _load_json(path)
            for member, path in zip(baseline.snapshot["members"], baseline.member_paths,
                                    strict=True) if member["kind"] == "journey"
        }
        for gate_id, observed in zip(journey_ids, observations, strict=True):
            journey_id = gate_id.removeprefix("journey:")
            baseline_cli_sha = None
            if journey_id == "wave-b-v1":
                baseline_cli_sha = next((
                    row["projection"]["executableSha256"]
                    for row in baseline_journeys[journey_id]["semantics"]["artifactInventory"]
                    if row["path"] == "evidence/initial-preflight.json"
                ), None)
                if not isinstance(baseline_cli_sha, str):
                    raise FirewallError("baseline Wave B preflight CLI identity missing")
            expected = _release_journey_policy_projection(
                journey_policy_projection(baseline_journeys[journey_id]),
                product_version=baseline.snapshot["provenance"]["productVersion"],
                source_line=baseline.snapshot["provenance"]["sourceLine"],
                executable_sha256=baseline_cli_sha,
            )
            actual = _release_journey_policy_projection(
                journey_policy_projection(observed),
                product_version=release["version"], source_line=release_source_line,
                executable_sha256=package_sha if baseline_cli_sha is not None else None,
            )
            mismatch = _first_json_difference(expected, actual, "")
            if mismatch:
                record(gate_id, "FAIL", f"firstDifference={mismatch[0]}",
                       f"expectedSha256={_sha256(canonical_json_bytes(expected)).upper()}",
                       f"actualSha256={_sha256(canonical_json_bytes(actual)).upper()}")
            else:
                record(gate_id, "PASS", f"journey={journey_id}",
                       f"semanticSha256={_sha256(canonical_json_bytes(actual)).upper()}",
                       f"runnerSha256={runner_sha}", f"sdkLength={sdk_length}",
                       *runner_build_evidence,
                       f"harnessCommit={head}", f"harnessTree={tree}")
    except (FirewallError, OSError, KeyError, TypeError, ValueError) as exc:
        for gate_id in journey_ids:
            if gate_id not in gates:
                record(gate_id, "BLOCKED", f"{type(exc).__name__}:{exc}")
    finally:
        cleanup_error: str | None = None
        if registered:
            try:
                _git(REPOSITORY_ROOT, "worktree", "remove", "--force", str(journey_worktree))
                registered = False
                _git(REPOSITORY_ROOT, "worktree", "prune")
            except (FirewallError, OSError) as exc:
                cleanup_error = f"journey worktree cleanup failed: {exc}"
        elif owned_journey_root is not None and (owned_journey_root / "worktree").exists():
            cleanup_error = "journey worktree creation left an unverified checkout"
        if owned_journey_root is not None and journey_token is not None and not registered:
            if cleanup_error is None:
                try:
                    if not remove_owned_journey_capture(
                        owned_journey_root, journey_token, CAPTURE_WORKSPACE_PARENT
                    ):
                        cleanup_error = "owned journey cleanup refused its marker or lock"
                except (FirewallError, OSError) as exc:
                    cleanup_error = f"owned journey cleanup failed: {exc}"
        if cleanup_error is not None:
            for gate_id in journey_ids:
                record(gate_id, "BLOCKED", cleanup_error)

    gates["audit:source-scope"] = _source_scope_gate()
    return tuple(gates.get(gate_id, GateResult(gate_id, True, "BLOCKED",
                 ("Full gate has no independently authenticated evidence",), ()))
                 for gate_id in remaining)


def _full_report(baseline_id: str, source_cli: str, release_root: str,
                 release_zip: str) -> dict[str, Any]:
    try:
        source = _source_report(baseline_id, source_cli)
    except (FirewallError, OSError, ValueError, KeyError, TypeError) as exc:
        source = {
            "gates": [{"gateId": gate_id, "required": True, "status": "BLOCKED",
                       "evidence": [f"Source evidence unavailable: {type(exc).__name__}:{exc}"],
                       "differences": []} for gate_id in required_gate_ids("Source")],
            "differences": [],
        }
    pin_rows: list[PinRow] = []
    rows = [*source["gates"]]
    rows.extend({"gateId": gate.gate_id, "required": gate.required,
                 "status": gate.status, "evidence": list(gate.evidence),
                 "differences": [{"subject": diff.subject, "layer": diff.layer,
                                  "pointer": diff.pointer, "expected": diff.expected,
                                  "actual": diff.actual, "gateId": diff.gate}
                                 for diff in gate.differences]}
                for gate in _full_release_gates(baseline_id, source_cli, release_root,
                                                release_zip, pin_rows))
    present = {row["gateId"] for row in rows}
    rows.extend({"gateId": gate_id, "required": True, "status": "BLOCKED",
                 "evidence": ["Full gate has no independently authenticated evidence"],
                 "differences": []}
                for gate_id in required_gate_ids("Full") if gate_id not in present)
    evaluations = [GateResult(row["gateId"], row["required"], row["status"],
                              tuple(row["evidence"]), ()) for row in rows]
    result = evaluate_tier("Full", evaluations)
    closed_pins = (bool(pin_rows)
                   and {row.pin_id for row in pin_rows} == FULL_ZIP_PIN_IDS
                   and {row.identity_class for row in pin_rows} == FULL_ZIP_PIN_CLASSES
                   and len(pin_rows) == len(FULL_ZIP_PIN_IDS)
                   and all(row.status == "PASS" for row in pin_rows))
    compatible = result.compatible and closed_pins
    return {"$schema": "urn:actorwright:compatibility:v1:report", "formatVersion": 1,
            "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
            "tier": "Full", "baselineId": baseline_id, "compatible": compatible,
            "verdict": "FULL_COMPATIBLE" if compatible else "INCOMPATIBLE",
            "fixtureBacked": False, "realInstallation": False,
            "gates": rows, "differences": [*source["differences"],
                                           *(diff for row in rows for diff in row["differences"])],
            "pinRows": [{"pinId": row.pin_id, "stage": row.stage,
                         "producer": row.producer, "consumer": row.consumer,
                         "producerValue": row.producer_value,
                         "consumerValue": row.consumer_value,
                         "identityClass": row.identity_class,
                         "producerEvidencePath": row.producer_evidence_path,
                         "consumerEvidencePath": row.consumer_evidence_path,
                         "status": row.status} for row in pin_rows]}


def _saved_full_report(
    baseline_id: str, report_path: str | Path, expected_sha256: str,
    release_root: str | Path, release_zip: str | Path,
) -> tuple[dict[str, Any], Path, str]:
    """Reauthenticate saved Full evidence against its exact bytes and current release inputs."""
    raw_report_file = Path(report_path)
    if not raw_report_file.is_absolute():
        raise FirewallError("saved Full report path must be absolute")
    report_file, report_reparse = _lexical_path_reparse(report_path)
    if report_reparse is not None or not report_file.is_file():
        raise FirewallError("saved Full report must be an existing ordinary absolute file")
    if not isinstance(expected_sha256, str) or not re.fullmatch(r"[A-Fa-f0-9]{64}", expected_sha256):
        raise FirewallError("saved Full report SHA-256 must be 64 hexadecimal characters")

    report_bytes = report_file.read_bytes()
    report_sha256 = _sha256(report_bytes).upper()
    if report_sha256 != expected_sha256.upper():
        raise FirewallError("saved Full report SHA-256 does not match")
    full = _require_object(parse_json_output(report_bytes, "saved Full report"),
                           "saved Full report")
    expected_fields = {"$schema", "formatVersion", "generatedAtUtc", "tier",
                       "baselineId", "compatible", "verdict", "fixtureBacked",
                       "realInstallation", "gates", "differences", "pinRows"}
    if (set(full) != expected_fields
            or full.get("$schema") != "urn:actorwright:compatibility:v1:report"
            or type(full.get("formatVersion")) is not int or full.get("formatVersion") != 1
            or not isinstance(full.get("generatedAtUtc"), str)
            or full.get("tier") != "Full" or full.get("baselineId") != baseline_id
            or full.get("compatible") is not True or full.get("verdict") != "FULL_COMPATIBLE"
            or full.get("fixtureBacked") is not False
            or full.get("realInstallation") is not False
            or full.get("differences") != []
            or canonical_json_bytes(full) != report_bytes):
        raise FirewallError("saved Full report is not a canonical closed compatible report")

    gates = full.get("gates")
    if (not isinstance(gates, list) or len(gates) != len(required_gate_ids("Full"))
            or any(not isinstance(row, dict)
                   or set(row) != {"gateId", "required", "status", "evidence", "differences"}
                   or not isinstance(row.get("gateId"), str)
                   or row.get("required") is not True or row.get("status") != "PASS"
                   or not isinstance(row.get("evidence"), list) or not row["evidence"]
                   or not all(isinstance(item, str) and item for item in row["evidence"])
                   or row.get("differences") != [] for row in gates)
            or {row["gateId"] for row in gates} != set(required_gate_ids("Full"))):
        raise FirewallError("saved Full gate inventory is incomplete or not all PASS")
    gate_by_id = {row["gateId"]: row for row in gates}

    def evidence_value(gate_id: str, token_name: str) -> str:
        evidence = gate_by_id[gate_id]["evidence"]
        prefix = token_name + "="
        values = [item[len(prefix):] for item in evidence if item.startswith(prefix)]
        if len(values) != 1 or not values[0]:
            raise FirewallError(f"saved Full {gate_id} lacks one {token_name}")
        return values[0]

    def evidence_token(gate_id: str, token_name: str) -> str:
        value = evidence_value(gate_id, token_name)
        if not re.fullmatch(r"[A-Fa-f0-9]{64}", value):
            raise FirewallError(f"saved Full {gate_id} lacks one valid {token_name}")
        return value.upper()

    source_cli_sha = evidence_token("contract:command-surface", "cliSha256")
    comparison_source_sha = evidence_token(
        "comparison:source-package-baseline", "sourceSha256")
    package_cli_sha = evidence_token(
        "comparison:source-package-baseline", "packageSha256")
    comparison_evidence = gate_by_id["comparison:source-package-baseline"]["evidence"]
    if (source_cli_sha != comparison_source_sha
            or "commandCount=142" not in gate_by_id["contract:command-surface"]["evidence"]
            or "differences=0" not in comparison_evidence):
        raise FirewallError("saved Full source/package CLI identities are inconsistent")

    saved_root = evidence_value("verification:release", "root")
    saved_zip = evidence_value("verification:release", "zip")
    saved_zip_length = evidence_value("verification:release", "zipLength")
    saved_zip_sha256 = evidence_token("verification:release", "zipSha256")
    if (not re.fullmatch(r"0|[1-9][0-9]*", saved_zip_length)
            or not Path(saved_root).is_absolute() or not Path(saved_zip).is_absolute()
            or Path(saved_root).drive.casefold() != "k:"
            or Path(saved_zip).drive.casefold() != "k:"
            or ".." in Path(saved_root).parts or ".." in Path(saved_zip).parts):
        raise FirewallError("saved Full release gate paths or ZIP length are invalid")

    raw_root, raw_archive = Path(release_root), Path(release_zip)
    if not raw_root.is_absolute() or not raw_archive.is_absolute():
        raise FirewallError("Full release root and ZIP paths must be absolute")
    root, root_reparse = _lexical_path_reparse(raw_root)
    archive, archive_reparse = _lexical_path_reparse(raw_archive)
    root, archive = root.resolve(), archive.resolve()
    if (root_reparse is not None or archive_reparse is not None
            or root.drive.casefold() != "k:" or archive.drive.casefold() != "k:"
            or not root.is_dir() or not archive.is_file()):
        raise FirewallError("Full release root and ZIP must be ordinary absolute K-local artifacts")
    saved_zip_path = Path(saved_zip)
    _, saved_zip_reparse = _lexical_path_reparse(saved_zip_path)
    if (saved_zip_reparse is not None or saved_root != str(root)
            or saved_zip_path.drive.casefold() != "k:"):
        raise FirewallError("saved Full release gate paths do not match the supplied release root")
    with archive.open("rb") as zip_stream:
        current_zip_length = os.fstat(zip_stream.fileno()).st_size
        current_zip_sha256 = hashlib.file_digest(zip_stream, "sha256").hexdigest().upper()
        unchanged_zip_length = os.fstat(zip_stream.fileno()).st_size
    if (current_zip_length != int(saved_zip_length)
            or unchanged_zip_length != current_zip_length
            or current_zip_sha256 != saved_zip_sha256):
        raise FirewallError(
            "saved Full release ZIP length or SHA-256 differs from current bytes")
    package_cli, package_cli_reparse = _lexical_path_reparse(root / "cli" / "actorwright.exe")
    if package_cli_reparse is not None or not package_cli.is_file():
        raise FirewallError("saved Full package CLI is missing or not an ordinary file")
    if _sha256_file(package_cli).upper() != package_cli_sha:
        raise FirewallError("saved Full package CLI SHA-256 differs from current release bytes")

    graph = verify_release_root_compatibility(root, archive)
    if (not graph.compatible or graph.verdict != "FULL_COMPATIBLE" or graph.errors
            or not graph.pin_rows
            or {row.pin_id for row in graph.pin_rows} != FULL_ZIP_PIN_IDS
            or {row.identity_class for row in graph.pin_rows} != FULL_ZIP_PIN_CLASSES
            or any(row.status != "PASS" for row in graph.pin_rows)):
        raise FirewallError(f"current release Full pin graph is incomplete: {graph.errors}")
    current_pin_rows = [{
        "pinId": row.pin_id, "stage": row.stage,
        "producer": row.producer, "consumer": row.consumer,
        "producerValue": row.producer_value,
        "consumerValue": row.consumer_value,
        "identityClass": row.identity_class,
        "producerEvidencePath": row.producer_evidence_path,
        "consumerEvidencePath": row.consumer_evidence_path,
        "status": row.status,
    } for row in graph.pin_rows]
    saved_pin_rows = full.get("pinRows")
    if not isinstance(saved_pin_rows, list) or not all(
            isinstance(row, dict) for row in saved_pin_rows):
        raise FirewallError("saved Full pin rows differ from current release inputs")
    inventory_rows = [row for row in saved_pin_rows
                      if row.get("pinId") == "source-overlap:releaseFileInventory"]
    if (len(inventory_rows) != 1
            or inventory_rows[0].get("consumerEvidencePath") != saved_zip):
        raise FirewallError(
            "saved Full release inventory path does not match verification gate")
    rebound_pin_rows = [dict(row) for row in saved_pin_rows]
    rebound_inventory = next(
        row for row in rebound_pin_rows
        if row["pinId"] == "source-overlap:releaseFileInventory")
    rebound_inventory["consumerEvidencePath"] = str(archive)
    if rebound_pin_rows != current_pin_rows:
        raise FirewallError("saved Full pin rows differ from current release inputs")
    return full, report_file, report_sha256


def _candidate_report(baseline_id: str, source_cli: str | None, release_root: str,
                      release_zip: str, candidate_bundle: str,
                      tag_repository: str | Path, *, fixture_backed: bool = False,
                      full_report: str | Path | None = None,
                      full_report_sha256: str | None = None) -> dict[str, Any]:
    """Extend the authenticated Full report with the exact candidate pin graph."""
    saved_full_path: Path | None = None
    saved_full_sha256: str | None = None
    if full_report is None and full_report_sha256 is None:
        if source_cli is None:
            raise FirewallError("fresh Candidate verification requires a source CLI")
        full = _full_report(baseline_id, source_cli, release_root, release_zip)
    elif full_report is not None and full_report_sha256 is not None:
        if source_cli is not None:
            raise FirewallError("saved Full mode must omit SourceCli")
        full, saved_full_path, saved_full_sha256 = _saved_full_report(
            baseline_id, full_report, full_report_sha256, release_root, release_zip)
    else:
        raise FirewallError("saved Full mode requires both report path and SHA-256")
    candidate = verify_candidate_bundle_compatibility(
        candidate_bundle, release_root, release_zip, tag_repository,
        fixture_backed=fixture_backed,
    )
    evidence = list(candidate.errors)
    if candidate.compatible:
        manifest = Path(candidate_bundle) / "bundle-manifest.json"
        evidence = [
            f"candidateBundleManifest={manifest}",
            f"candidateBundleManifestSha256={_sha256_file(manifest).upper()}",
            f"releaseRoot={release_root}", f"releaseZip={release_zip}",
        ]
    if saved_full_path is not None and saved_full_sha256 is not None:
        evidence.extend((f"savedFullReport={saved_full_path}",
                         f"savedFullReportSha256={saved_full_sha256}",
                         "fullTierEvidence=REUSED"))
    rows = [*full["gates"], {
        "gateId": "pins:candidate", "required": True,
        "status": "PASS" if candidate.compatible else "FAIL",
        "evidence": evidence, "differences": [],
    }]
    evaluation = evaluate_tier("Candidate", [
        GateResult(row["gateId"], row["required"], row["status"],
                   tuple(row["evidence"]), ()) for row in rows
    ])
    compatible = full["compatible"] and candidate.compatible and evaluation.compatible
    return {
        **full,
        "tier": "Candidate", "compatible": compatible,
        "verdict": "CANDIDATE_COMPATIBLE" if compatible else "INCOMPATIBLE",
        "fixtureBacked": fixture_backed,
        "gates": rows,
        "pinRows": [{
            "pinId": row.pin_id, "stage": row.stage,
            "producer": row.producer, "consumer": row.consumer,
            "producerValue": row.producer_value,
            "consumerValue": row.consumer_value,
            "identityClass": row.identity_class,
            "producerEvidencePath": row.producer_evidence_path,
            "consumerEvidencePath": row.consumer_evidence_path,
            "status": row.status,
        } for row in candidate.pin_rows],
    }


def _later_report(stage: str, baseline_id: str, producer_report: str | Path,
                  release_root: str | Path, release_zip: str | Path,
                  candidate_bundle: str | Path, promotion_bundle: str | Path,
                  admission_report: str | Path,
                  activation_report: str | Path | None = None, *,
                  tag_repository: str | Path, fixture_backed: bool) -> dict[str, Any]:
    """Authenticate saved producer and consumer evidence without executing a workflow."""
    if stage not in {"Admission", "Activation"}:
        raise FirewallError(f"unsupported consumer stage: {stage}")

    def ordinary(path_value: str | Path, subject: str, directory: bool = False) -> Path:
        path, reparse = _lexical_path_reparse(path_value)
        if reparse is not None or not path.is_absolute() or (
            not path.is_dir() if directory else not path.is_file()
        ):
            raise FirewallError(f"{subject} must be an existing ordinary absolute path")
        return path

    def pin(pin_id: str, level: str, left: object, right: object,
            left_path: str, right_path: str, identity_class: str = "sha256") -> dict[str, Any]:
        left_value, right_value = str(left), str(right)
        if not left_value or left_value.casefold() != right_value.casefold():
            raise FirewallError(f"pin-mismatch:{pin_id}")
        return {"pinId": pin_id, "stage": level,
                "producer": f"{pin_id}.producer", "consumer": f"{pin_id}.consumer",
                "producerValue": left_value, "consumerValue": right_value,
                "identityClass": identity_class,
                "producerEvidencePath": left_path, "consumerEvidencePath": right_path,
                "status": "PASS"}

    try:
        producer_path = ordinary(producer_report, "Candidate report")
        candidate_root = ordinary(candidate_bundle, "candidate bundle", directory=True)
        promotion_root = ordinary(promotion_bundle, "promotion bundle", directory=True)
        admission_path = ordinary(admission_report, "consumer Admission report")
        producer_bytes = producer_path.read_bytes()
        producer = _require_object(_load_pin_json(producer_path), "Candidate report")
        expected_fields = {"$schema", "formatVersion", "generatedAtUtc", "tier",
                           "baselineId", "compatible", "verdict", "fixtureBacked",
                           "realInstallation", "gates", "differences", "pinRows"}
        if (set(producer) != expected_fields
                or producer.get("$schema") != "urn:actorwright:compatibility:v1:report"
                or type(producer.get("formatVersion")) is not int
                or producer.get("formatVersion") != 1 or producer.get("tier") != "Candidate"
                or producer.get("baselineId") != baseline_id
                or producer.get("compatible") is not True
                or producer.get("verdict") != "CANDIDATE_COMPATIBLE"
                or producer.get("fixtureBacked") is not fixture_backed
                or producer.get("realInstallation") is not False
                or producer.get("differences") != []
                or canonical_json_bytes(producer) != producer_bytes):
            raise FirewallError("saved Candidate report is not a closed compatible report")
        gates = producer.get("gates")
        if (not isinstance(gates, list) or len(gates) != len(required_gate_ids("Candidate"))
                or {row.get("gateId") for row in gates if isinstance(row, dict)}
                   != set(required_gate_ids("Candidate"))
                or any(not isinstance(row, dict)
                       or set(row) != {"gateId", "required", "status", "evidence", "differences"}
                       or row["required"] is not True or row["status"] != "PASS"
                       or not isinstance(row["evidence"], list) or not row["evidence"]
                       or row["differences"] != [] for row in gates)):
            raise FirewallError("saved Candidate gate inventory is incomplete")
        candidate = verify_candidate_bundle_compatibility(
            candidate_root, release_root, release_zip, tag_repository,
            fixture_backed=fixture_backed)
        if not candidate.compatible:
            raise FirewallError(f"Candidate bundle authentication failed: {candidate.errors}")
        candidate_rows = [{"pinId": row.pin_id, "stage": row.stage,
                           "producer": row.producer, "consumer": row.consumer,
                           "producerValue": row.producer_value,
                           "consumerValue": row.consumer_value,
                           "identityClass": row.identity_class,
                           "producerEvidencePath": row.producer_evidence_path,
                           "consumerEvidencePath": row.consumer_evidence_path,
                           "status": row.status} for row in candidate.pin_rows]
        if producer.get("pinRows") != candidate_rows:
            raise FirewallError("saved Candidate pin rows differ from authenticated bundle")
        candidate_manifest = candidate_root / "bundle-manifest.json"
        candidate_manifest_hash = _sha256_file(candidate_manifest).upper()
        promotion_manifest = promotion_root / "bundle-manifest.json"
        promotion_payload = promotion_root / "promotion.json"
        if set(promotion_root.iterdir()) != {promotion_manifest, promotion_payload}:
            raise FirewallError("promotion bundle inventory is not closed")
        ordinary(promotion_manifest, "promotion manifest")
        ordinary(promotion_payload, "promotion payload")
        manifest = _require_object(_load_pin_json(promotion_manifest), "promotion manifest")
        payload = _require_object(_load_pin_json(promotion_payload), "promotion payload")
        payload_length, payload_sha256 = _sha256_file_and_length(promotion_payload)
        promotion_manifest_hash = _sha256_file(promotion_manifest).upper()
        if (set(manifest) != {"$schema", "version", "kind", "payload", "files"}
                or manifest["$schema"] != "urn:actorwright:exchange:v1:bundle-manifest"
                or type(manifest["version"]) is not int or manifest["version"] != 1
                or manifest["kind"] != "promotion"
                or manifest["payload"] != "promotion.json"
                or manifest["files"] != [{"path": "promotion.json", "size": payload_length,
                                          "sha256": payload_sha256.upper()}]
                or set(payload) != {"$schema", "version", "candidateManifestSha256",
                                    "createdUtc", "decidedBy", "decision"}
                or payload["$schema"] != "urn:actorwright:exchange:v1:promotion"
                or type(payload["version"]) is not int or payload["version"] != 1
                or payload["decision"] != "APPROVED"
                or not isinstance(payload["decidedBy"], str) or not payload["decidedBy"]
                or payload["candidateManifestSha256"].upper() != candidate_manifest_hash):
            raise FirewallError("promotion does not approve the exact candidate manifest")
        package = candidate_root / Path(release_zip).name
        package_length, package_hash = _sha256_file_and_length(package)
        package_hash = package_hash.upper()
        schema_hash = _sha256_file(CONSUMER_STAGE_REPORT_SCHEMA).upper()
        producer_hash = _sha256(producer_bytes).upper()
        pins = list(candidate_rows)
        pins.append(pin("promotion-candidate-manifest-sha256", "Admission",
                        candidate_manifest_hash, payload["candidateManifestSha256"],
                        str(candidate_manifest), f"{promotion_payload}#/candidateManifestSha256"))
        pins.append(pin("promotion-member-sha256", "Admission", payload_sha256.upper(),
                        manifest["files"][0]["sha256"], str(promotion_payload),
                        f"{promotion_manifest}#/files/0/sha256"))

        def stage_file(path: Path, expected_stage: str) -> tuple[
            dict[str, Any], list[dict[str, Any]], dict[str, str]
        ]:
            value = _require_object(_load_pin_json(path), f"consumer {expected_stage} report")
            keys = {"$schema", "formatVersion", "generatedAtUtc", "stage",
                    "candidateBundleManifestSha256", "packageLength", "packageSha256",
                    "contractSchemaSha256", "producerReportSha256", "status", "gates", "evidence"}
            gate_id = f"consumer-{expected_stage.lower()}-pin-closure"
            gate_rows = value.get("gates")
            if (set(value) != keys
                    or value["$schema"] != "urn:actorwright:compatibility:v1:consumer-stage-report"
                    or type(value["formatVersion"]) is not int
                    or value["formatVersion"] != 1 or value["stage"] != expected_stage
                    or value["status"] != "PASS" or not isinstance(value["evidence"], list)
                    or not value["evidence"]
                    or not all(isinstance(item, str) and item for item in value["evidence"])
                    or not isinstance(gate_rows, list)
                    or len(gate_rows) != 1 or not isinstance(gate_rows[0], dict)
                    or set(gate_rows[0]) != {"gateId", "required", "status", "evidence"}
                    or gate_rows[0]["gateId"] != gate_id
                    or gate_rows[0]["required"] is not True
                    or gate_rows[0]["status"] != "PASS"
                    or gate_rows[0]["evidence"] != value["evidence"]
                    or canonical_json_bytes(value) != path.read_bytes()):
                raise FirewallError(f"consumer {expected_stage} report is not closed PASS evidence")
            evidence = value["evidence"]
            statement = ("consumer Analyze and candidate/promotion readback"
                         if expected_stage == "Admission"
                         else "consumer Apply/Verify and activation readback")
            if expected_stage == "Admission":
                required_names = {"producerReportFixtureBacked", "targetManifestSha256",
                                  "promotionBundleManifestSha256", "activeManifestSha256",
                                  "preActivationWrapperSha256", "rollbackExecutablePath",
                                  "rollbackExecutableLength", "rollbackExecutableSha256",
                                  "packagedLauncherSha256"}
            else:
                required_names = {"producerReportFixtureBacked", "admissionReportSha256",
                                  "packagedLauncherSha256", "activeWrapperSha256",
                                  "rollbackExecutableSha256"}
            if evidence.count(statement) != 1 or len(evidence) != len(required_names) + 1:
                raise FirewallError(f"consumer {expected_stage} evidence inventory is incomplete")
            tokens: dict[str, str] = {}
            for item in evidence:
                if item == statement:
                    continue
                name, separator, token_value = item.partition(":")
                if (not separator or name not in required_names or name in tokens
                        or not token_value):
                    raise FirewallError(f"consumer {expected_stage} evidence token is unknown or duplicated")
                tokens[name] = token_value
            if (set(tokens) != required_names
                    or tokens["producerReportFixtureBacked"] != str(fixture_backed).lower()
                    or tokens["packagedLauncherSha256"].upper()
                       != EXPECTED_PREVIEW_275_LAUNCHER_SHA256
                    or any(not re.fullmatch(r"[A-Fa-f0-9]{64}", token_value)
                           for name, token_value in tokens.items() if name.endswith("Sha256"))
                    or (expected_stage == "Admission" and (
                        tokens["promotionBundleManifestSha256"].upper()
                        != promotion_manifest_hash
                        or not re.fullmatch(r"\d+", tokens["rollbackExecutableLength"])))):
                raise FirewallError(f"consumer {expected_stage} evidence identity is invalid")
            values = (
                ("candidate-manifest-sha256", candidate_manifest_hash,
                 value["candidateBundleManifestSha256"], str(candidate_manifest),
                 f"{path}#/candidateBundleManifestSha256"),
                ("package-sha256", package_hash, value["packageSha256"], str(package),
                 f"{path}#/packageSha256"),
                ("package-length", package_length, value["packageLength"], str(package),
                 f"{path}#/packageLength"),
                ("contract-schema-sha256", schema_hash, value["contractSchemaSha256"],
                 str(CONSUMER_STAGE_REPORT_SCHEMA), f"{path}#/contractSchemaSha256"),
                ("producer-report-sha256", producer_hash, value["producerReportSha256"],
                 str(producer_path), f"{path}#/producerReportSha256"),
            )
            result = [pin(f"consumer:{name}", expected_stage, left, right, left_path,
                          right_path, "package-length" if name == "package-length" else "sha256")
                      for name, left, right, left_path, right_path in values]
            return value, result, tokens

        admission, admission_pins, admission_tokens = stage_file(admission_path, "Admission")
        pins.extend(admission_pins)
        if stage == "Activation":
            activation_path = ordinary(activation_report, "consumer Activation report")
            activation, activation_pins, activation_tokens = stage_file(
                activation_path, "Activation")
            admission_hash = _sha256_file(admission_path).upper()
            if activation_tokens["admissionReportSha256"].upper() != admission_hash:
                raise FirewallError("Activation does not bind exact Admission report bytes")
            for name in ("producerReportFixtureBacked", "packagedLauncherSha256",
                         "rollbackExecutableSha256"):
                if admission_tokens[name].casefold() != activation_tokens[name].casefold():
                    raise FirewallError(f"Activation {name} differs from Admission")
            pins.extend(activation_pins)
            pins.append(pin("consumer:admission-report-sha256", "Activation",
                            admission_hash, admission_hash, str(admission_path),
                            f"{activation_path}#/evidence/admissionReportSha256"))
            pins.append(pin("consumer:rollback-executable-sha256", "Activation",
                            admission_tokens["rollbackExecutableSha256"],
                            activation_tokens["rollbackExecutableSha256"],
                            f"{admission_path}#/evidence/rollbackExecutableSha256",
                            f"{activation_path}#/evidence/rollbackExecutableSha256"))
        extra_gates = [
            {"gateId": "pins:promotion", "required": True, "status": "PASS",
             "evidence": [f"promotionBundleManifestSha256={promotion_manifest_hash}"],
             "differences": []},
            {"gateId": "pins:consumer-admission", "required": True, "status": "PASS",
             "evidence": [f"consumerAdmissionReportSha256={_sha256_file(admission_path).upper()}"],
             "differences": []},
        ]
        if stage == "Activation":
            extra_gates.append({"gateId": "pins:consumer-activation", "required": True,
                                "status": "PASS", "evidence": [
                                    f"consumerActivationReportSha256={_sha256_file(activation_path).upper()}"],
                                "differences": []})
        all_gates = [*gates, *extra_gates]
        evaluation = evaluate_tier(stage, [GateResult(
            row["gateId"], True, "PASS", tuple(row["evidence"]), ()) for row in all_gates])
        if not evaluation.compatible:
            raise FirewallError(f"{stage} gate inventory incomplete")
        return {**producer, "tier": stage, "compatible": True,
                "verdict": evaluation.verdict, "fixtureBacked": fixture_backed,
                "realInstallation": False, "gates": all_gates, "pinRows": pins}
    except (FirewallError, OSError, UnicodeError, ValueError, TypeError, KeyError,
            AttributeError) as exc:
        rows = [{"gateId": gate_id, "required": True, "status": "BLOCKED",
                 "evidence": [f"{type(exc).__name__}:{exc}"], "differences": []}
                for gate_id in required_gate_ids(stage)]
        return {"$schema": "urn:actorwright:compatibility:v1:report", "formatVersion": 1,
                "generatedAtUtc": datetime.now(timezone.utc).isoformat().replace("+00:00", "Z"),
                "tier": stage, "baselineId": baseline_id, "compatible": False,
                "verdict": "INCOMPATIBLE", "fixtureBacked": fixture_backed,
                "realInstallation": False, "gates": rows, "differences": [], "pinRows": []}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="compatibility-firewall")
    subparsers = parser.add_subparsers(dest="mode", required=True)
    capture = subparsers.add_parser("capture")
    capture.add_argument("-SourceRef", "--source-ref", required=True)
    capture.add_argument("-ReleaseZip", "--release-zip", required=True)
    capture.add_argument("-Output", "--output", required=True)
    capture.add_argument("-HarnessCommit", "--harness-commit", required=True)
    capture.add_argument("-HarnessTree", "--harness-tree", required=True)
    capture.add_argument("-HarnessSourceTree", "--harness-source-tree", required=True)
    capture.add_argument("-HarnessSourceCount", "--harness-source-count", required=True, type=int)
    capture.add_argument("-HarnessSourceSha256", "--harness-source-sha256", required=True)
    synthetic_capture = subparsers.add_parser("capture-synthetic")
    synthetic_capture.add_argument("-SourceCli", "--source-cli", required=True)
    synthetic_capture.add_argument(
        "-CandidateOutputRoot", "--candidate-output-root", required=True
    )
    synthetic_capture.add_argument(
        "-CandidateConfiguration", "--candidate-configuration",
        choices=("Debug", "Release"), required=True,
    )
    synthetic_capture.add_argument("-Output", "--output", required=True)
    verify = subparsers.add_parser("verify")
    verify.add_argument("-Tier", "--tier")
    verify.add_argument("-Baseline", "--baseline", required=True)
    verify.add_argument("-SourceCli", "--source-cli")
    verify.add_argument("-ReleaseRoot", "--release-root")
    verify.add_argument("-ReleaseZip", "--release-zip")
    verify.add_argument("-CandidateBundle", "--candidate-bundle")
    verify.add_argument("-FullReport", "--full-report")
    verify.add_argument("-FullReportSha256", "--full-report-sha256")
    verify.add_argument("-CandidateReport", "--candidate-report", "-ProducerReport", "--producer-report")
    verify.add_argument("-TagRepository", "--tag-repository")
    verify.add_argument("-FixtureBacked", "--fixture-backed", action="store_true")
    verify.add_argument("-PromotionBundle", "--promotion-bundle")
    verify.add_argument("-ConsumerAdmission", "--consumer-admission")
    verify.add_argument("-ActivationReport", "--activation-report")
    verify.add_argument("-Output", "--output")
    arguments = parser.parse_args(argv)
    try:
        if arguments.mode == "capture":
            captured = capture_preview275_baseline(
                repository_root=REPOSITORY_ROOT,
                source_ref=arguments.source_ref,
                release_zip=arguments.release_zip,
                output_root=arguments.output,
                expected_harness={
                    "commit": arguments.harness_commit,
                    "tree": arguments.harness_tree,
                    "sourceClosure": {
                        "root": "tests/NpcManager.Cli.Tests",
                        "tree": arguments.harness_source_tree,
                        "fileCount": arguments.harness_source_count,
                        "sha256": arguments.harness_source_sha256,
                    },
                },
            )
            print(captured)
            return 0
        if arguments.mode == "capture-synthetic":
            captured = capture_public_synthetic_baseline(
                repository_root=REPOSITORY_ROOT,
                source_cli=arguments.source_cli,
                candidate_output_root=arguments.candidate_output_root,
                candidate_configuration=arguments.candidate_configuration,
                output_root=arguments.output,
            )
            print(captured)
            return 0
        if (arguments.tier not in {"Source", "Package", "Full", "Candidate",
                                   "Admission", "Activation"}
                or not arguments.output
                or (arguments.tier in {"Source", "Package", "Full"}
                    and not arguments.source_cli)
                or (arguments.tier in {"Admission", "Activation"} and arguments.source_cli)
                or (arguments.tier == "Source" and (arguments.release_root or arguments.release_zip
                                                    or arguments.candidate_bundle))
                or (arguments.tier == "Package" and (not arguments.release_root or arguments.release_zip
                                                     or arguments.candidate_bundle))
                or (arguments.tier in {"Full", "Candidate", "Admission", "Activation"}
                    and (not arguments.release_root or not arguments.release_zip))
                or (arguments.tier == "Full" and arguments.candidate_bundle)
                or (arguments.tier != "Candidate"
                    and (arguments.full_report is not None
                         or arguments.full_report_sha256 is not None))
                or (arguments.tier in {"Source", "Package", "Full"}
                    and (arguments.candidate_report or arguments.tag_repository
                         or arguments.fixture_backed))
                or (arguments.tier == "Candidate" and (
                    not arguments.candidate_bundle or not arguments.tag_repository
                    or ((arguments.full_report is None)
                        != (arguments.full_report_sha256 is None))
                    or (arguments.full_report is None and arguments.source_cli is None)
                    or (arguments.full_report is not None and arguments.source_cli is not None)
                    or arguments.candidate_report or arguments.promotion_bundle
                    or arguments.consumer_admission or arguments.activation_report))
                or (arguments.tier in {"Admission", "Activation"} and (
                    not arguments.candidate_report or not arguments.candidate_bundle
                    or not arguments.tag_repository or not arguments.promotion_bundle
                    or not arguments.consumer_admission))
                or (arguments.tier == "Admission" and arguments.activation_report)
                or (arguments.tier == "Activation" and not arguments.activation_report)
                or (arguments.tier in {"Source", "Package", "Full"}
                    and any((arguments.promotion_bundle, arguments.consumer_admission,
                             arguments.activation_report)))):
            raise FirewallError("verify requires exact tier inputs")
        if arguments.tag_repository:
            tag_root, tag_reparse = _lexical_path_reparse(arguments.tag_repository)
            if (tag_reparse is not None or not tag_root.is_absolute()
                    or tag_root.drive.casefold() != "k:"
                    or not tag_root.is_dir()
                    or tag_root.resolve() == REPOSITORY_ROOT.resolve()
                    or not (tag_root / ".git").is_dir()):
                raise FirewallError("tag repository must be an ordinary disposable K-local Git repository")
        output = Path(arguments.output)
        if not output.is_absolute():
            raise FirewallError("verification output must be absolute")
        output_path, output_reparse = _lexical_path_reparse(output)
        if (output_reparse is not None or output_path.drive.casefold() != "k:"
                or output_path.parent.resolve() != CAPTURE_WORKSPACE_PARENT.resolve()
                or output_path.exists() or not output_path.parent.is_dir()):
            raise FirewallError("verification output must be a new ordinary K-local file")
        if arguments.tier == "Source":
            result = _source_report(arguments.baseline, arguments.source_cli)
        elif arguments.tier == "Package":
            result = _package_report(arguments.baseline, arguments.source_cli,
                                     arguments.release_root)
        elif arguments.tier == "Full":
            result = _full_report(arguments.baseline, arguments.source_cli,
                                  arguments.release_root, arguments.release_zip)
        elif arguments.tier == "Candidate":
            result = _candidate_report(arguments.baseline, arguments.source_cli,
                                       arguments.release_root, arguments.release_zip,
                                       arguments.candidate_bundle,
                                       arguments.tag_repository,
                                       fixture_backed=arguments.fixture_backed,
                                       full_report=arguments.full_report,
                                       full_report_sha256=arguments.full_report_sha256)
        else:
            result = _later_report(arguments.tier, arguments.baseline,
                                   arguments.candidate_report, arguments.release_root,
                                   arguments.release_zip, arguments.candidate_bundle,
                                   arguments.promotion_bundle, arguments.consumer_admission,
                                   arguments.activation_report,
                                   tag_repository=arguments.tag_repository,
                                   fixture_backed=arguments.fixture_backed)
        data = canonical_json_bytes(result)
        with output_path.open("xb") as stream:
            stream.write(data)
        return 0 if result["compatible"] else 1
    except (FirewallError, OSError, ValueError, KeyError, TypeError) as exc:
        print(f"compatibility-firewall: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
