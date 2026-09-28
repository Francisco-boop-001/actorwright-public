#!/usr/bin/env python3
"""Independent binary-only Actorwright release and ZIP verifier."""

from __future__ import annotations

import argparse
import base64
import binascii
import hashlib
import importlib.util
import json
import os
import re
import shutil
import stat
import struct
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path, PurePosixPath, PureWindowsPath
from typing import Any


REQUIRED_DIRS = {"cli", "desktop", "schemas", "docs", "licenses", "evidence"}
REQUIRED_FILES = {"actorwright-release.json", "SHA256SUMS"}
REQUIRED_RELEASE_DOCUMENTS_BY_VERSION = {
    "1.0.0-preview.281": frozenset({
        "docs/external-rendering-prerequisites.md",
        "docs/licenses.md",
        "docs/third-party-licenses.md",
    }),
    "1.0.0-preview.280": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.279": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.278": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.277": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.276": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.275": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.274": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.273": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.272": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.271": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.270": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.269": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.268": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.267": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.266": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.265": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.264": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.263": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.262": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.261": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.260": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.258": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.257": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.256": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.255": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.254": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.253": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.252": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
    "1.0.0-preview.251": frozenset({
        "docs/external-rendering-prerequisites.md",
    }),
}
PACKAGE_DIRS = {"cli", "desktop", "schemas"}
PACKAGE_FILES = {
    "capabilities.json",
    "cli-resource-closure.json",
    "desktop-resource-closure.json",
    "manifest.json",
}
PACKAGE_SCHEMA_FILES = {
    "schemas/exchange/v1/bundle-manifest.schema.json",
    "schemas/exchange/v1/issue.schema.json",
    "schemas/exchange/v1/promotion.schema.json",
    "schemas/exchange/v1/release-candidate.schema.json",
    "schemas/exchange/v1/response.schema.json",
    "schemas/exchange/v2/response.schema.json",
}
FORBIDDEN_DIRS = {".git", "src", "source", "tests", "test", "obj", "bin"}
FORBIDDEN_SUFFIXES = {".cs", ".csproj", ".sln", ".py", ".ps1", ".pdb", ".pem", ".key", ".pfx"}
POWERSHELL_WRAPPER = (
    b"$exe = Join-Path $PSScriptRoot 'actorwright.exe'; "
    b"if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { "
    b'[Console]::Error.WriteLine("Actorwright executable is missing: $exe"); exit 1 }; '
    b"& $exe @args; exit $LASTEXITCODE\r\n"
)
HASH_RE = re.compile(r"^[A-F0-9]{64}$")
EXPECTED_SBOM_PACKAGE_COUNT = 36
LEGACY_STANDALONE_PYTHON_CASES = 37
EXPECTED_PREVIEW279_STANDALONE_PYTHON_CASES = 893
EXPECTED_PREVIEW278_STANDALONE_PYTHON_CASES = 893
EXPECTED_PREVIEW277_STANDALONE_PYTHON_CASES = 893
EXPECTED_PREVIEW276_STANDALONE_PYTHON_CASES = 880
EXPECTED_PREVIEW275_STANDALONE_PYTHON_CASES = 317
EXPECTED_PREVIEW274_STANDALONE_PYTHON_CASES = 317
EXPECTED_PREVIEW273_STANDALONE_PYTHON_CASES = 316
EXPECTED_PREVIEW272_STANDALONE_PYTHON_CASES = 315
EXPECTED_PREVIEW271_STANDALONE_PYTHON_CASES = 314
EXPECTED_PREVIEW270_STANDALONE_PYTHON_CASES = 287
EXPECTED_PREVIEW269_STANDALONE_PYTHON_CASES = 286
EXPECTED_PREVIEW268_STANDALONE_PYTHON_CASES = 286
EXPECTED_PREVIEW267_STANDALONE_PYTHON_CASES = 284
EXPECTED_PREVIEW266_STANDALONE_PYTHON_CASES = 231
EXPECTED_PREVIEW265_STANDALONE_PYTHON_CASES = 234
EXPECTED_PREVIEW264_STANDALONE_PYTHON_CASES = 233
EXPECTED_PREVIEW263_STANDALONE_PYTHON_CASES = 225
EXPECTED_PREVIEW262_STANDALONE_PYTHON_CASES = 225
EXPECTED_PREVIEW261_STANDALONE_PYTHON_CASES = 225
EXPECTED_PREVIEW260_STANDALONE_PYTHON_CASES = 224
EXPECTED_PREVIEW258_STANDALONE_PYTHON_CASES = 219
EXPECTED_PREVIEW257_STANDALONE_PYTHON_CASES = 213
EXPECTED_ROLLBACK_STANDALONE_PYTHON_CASES = 208
EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION = {
    "1.0.0-preview.279": EXPECTED_PREVIEW279_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.278": EXPECTED_PREVIEW278_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.277": EXPECTED_PREVIEW277_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.276": EXPECTED_PREVIEW276_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.275": EXPECTED_PREVIEW275_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.274": EXPECTED_PREVIEW274_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.273": EXPECTED_PREVIEW273_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.272": EXPECTED_PREVIEW272_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.271": EXPECTED_PREVIEW271_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.270": EXPECTED_PREVIEW270_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.269": EXPECTED_PREVIEW269_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.268": EXPECTED_PREVIEW268_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.267": EXPECTED_PREVIEW267_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.266": EXPECTED_PREVIEW266_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.265": EXPECTED_PREVIEW265_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.264": EXPECTED_PREVIEW264_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.263": EXPECTED_PREVIEW263_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.262": EXPECTED_PREVIEW262_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.261": EXPECTED_PREVIEW261_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.260": EXPECTED_PREVIEW260_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.258": EXPECTED_PREVIEW258_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.257": EXPECTED_PREVIEW257_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.256": EXPECTED_ROLLBACK_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.255": EXPECTED_ROLLBACK_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.254": EXPECTED_ROLLBACK_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.253": EXPECTED_ROLLBACK_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.252": EXPECTED_ROLLBACK_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.251": EXPECTED_ROLLBACK_STANDALONE_PYTHON_CASES,
    "1.0.0-preview.250": EXPECTED_ROLLBACK_STANDALONE_PYTHON_CASES,
}
LEGACY_PREVIEW_243_SOURCE_COMMIT = "df154c82772b9ea9ede1c82ad558c524a4716b1f"
BINARY_RELEASE_IDENTITIES: dict[str, dict[str, str]] = {
    "1.0.0-preview.281": {
        "version": "1.0.0-preview.281",
        "sourceLine": "preview.281-public",
        "sourceTag": "v1.0.0-preview.281",
    },
    "1.0.0-preview.280": {
        "version": "1.0.0-preview.280",
        "sourceLine": "preview.280-private",
        "sourceTag": "v1.0.0-preview.280",
    },
    "1.0.0-preview.279": {
        "version": "1.0.0-preview.279",
        "sourceLine": "preview.279-private",
        "sourceTag": "v1.0.0-preview.279",
    },
    "1.0.0-preview.278": {
        "version": "1.0.0-preview.278",
        "sourceLine": "preview.278-private",
        "sourceTag": "v1.0.0-preview.278",
    },
    "1.0.0-preview.277": {
        "version": "1.0.0-preview.277",
        "sourceLine": "preview.277-private",
        "sourceTag": "v1.0.0-preview.277",
    },
    "1.0.0-preview.276": {
        "version": "1.0.0-preview.276",
        "sourceLine": "preview.276-private",
        "sourceTag": "v1.0.0-preview.276",
    },
    "1.0.0-preview.275": {
        "version": "1.0.0-preview.275",
        "sourceLine": "preview.275-private",
        "sourceTag": "v1.0.0-preview.275",
    },
    "1.0.0-preview.274": {
        "version": "1.0.0-preview.274",
        "sourceLine": "preview.274-private",
        "sourceTag": "v1.0.0-preview.274",
    },
    "1.0.0-preview.273": {
        "version": "1.0.0-preview.273",
        "sourceLine": "preview.273-private",
        "sourceTag": "v1.0.0-preview.273",
    },
    "1.0.0-preview.272": {
        "version": "1.0.0-preview.272",
        "sourceLine": "preview.272-private",
        "sourceTag": "v1.0.0-preview.272",
    },
    "1.0.0-preview.271": {
        "version": "1.0.0-preview.271",
        "sourceLine": "preview.271-private",
        "sourceTag": "v1.0.0-preview.271",
    },
    "1.0.0-preview.270": {
        "version": "1.0.0-preview.270",
        "sourceLine": "preview.270-private",
        "sourceTag": "v1.0.0-preview.270",
    },
    "1.0.0-preview.269": {
        "version": "1.0.0-preview.269",
        "sourceLine": "preview.269-private",
        "sourceTag": "v1.0.0-preview.269",
    },
    "1.0.0-preview.268": {
        "version": "1.0.0-preview.268",
        "sourceLine": "preview.268-private",
        "sourceTag": "v1.0.0-preview.268",
    },
    "1.0.0-preview.267": {
        "version": "1.0.0-preview.267",
        "sourceLine": "preview.267-private",
        "sourceTag": "v1.0.0-preview.267",
    },
    "1.0.0-preview.266": {
        "version": "1.0.0-preview.266",
        "sourceLine": "preview.266-private",
        "sourceTag": "v1.0.0-preview.266",
    },
    "1.0.0-preview.265": {
        "version": "1.0.0-preview.265",
        "sourceLine": "preview.265-private",
        "sourceTag": "v1.0.0-preview.265",
    },
    "1.0.0-preview.264": {
        "version": "1.0.0-preview.264",
        "sourceLine": "preview.264-private",
        "sourceTag": "v1.0.0-preview.264",
    },
    "1.0.0-preview.263": {
        "version": "1.0.0-preview.263",
        "sourceLine": "preview.263-private",
        "sourceTag": "v1.0.0-preview.263",
    },
    "1.0.0-preview.262": {
        "version": "1.0.0-preview.262",
        "sourceLine": "preview.262-private",
        "sourceTag": "v1.0.0-preview.262",
    },
    "1.0.0-preview.261": {
        "version": "1.0.0-preview.261",
        "sourceLine": "preview.261-private",
        "sourceTag": "v1.0.0-preview.261",
    },
    "1.0.0-preview.260": {
        "version": "1.0.0-preview.260",
        "sourceLine": "preview.260-private",
        "sourceTag": "v1.0.0-preview.260",
    },
    "1.0.0-preview.258": {
        "version": "1.0.0-preview.258",
        "sourceLine": "preview.258-private",
        "sourceTag": "v1.0.0-preview.258",
    },
    "1.0.0-preview.257": {
        "version": "1.0.0-preview.257",
        "sourceLine": "preview.257-private",
        "sourceTag": "v1.0.0-preview.257",
    },
    "1.0.0-preview.256": {
        "version": "1.0.0-preview.256",
        "sourceLine": "preview.256-private",
        "sourceTag": "v1.0.0-preview.256",
    },
    "1.0.0-preview.255": {
        "version": "1.0.0-preview.255",
        "sourceLine": "preview.255-private",
        "sourceTag": "v1.0.0-preview.255",
    },
    "1.0.0-preview.254": {
        "version": "1.0.0-preview.254",
        "sourceLine": "preview.254-private",
        "sourceTag": "v1.0.0-preview.254",
    },
    "1.0.0-preview.253": {
        "version": "1.0.0-preview.253",
        "sourceLine": "preview.253-private",
        "sourceTag": "v1.0.0-preview.253",
    },
    "1.0.0-preview.252": {
        "version": "1.0.0-preview.252",
        "sourceLine": "preview.252-private",
        "sourceTag": "v1.0.0-preview.252",
    },
    "1.0.0-preview.251": {
        "version": "1.0.0-preview.251",
        "sourceLine": "preview.251-private",
        "sourceTag": "v1.0.0-preview.251",
    },
    "1.0.0-preview.250": {
        "version": "1.0.0-preview.250",
        "sourceLine": "preview.250-private",
        "sourceTag": "v1.0.0-preview.250",
    },
}
EXPECTED_COMMAND_COUNTS_BY_VERSION = {
    **{version: 136 for version in BINARY_RELEASE_IDENTITIES},
    "1.0.0-preview.243": 136,
    "1.0.0-preview.281": 142,
    "1.0.0-preview.280": 142,
    "1.0.0-preview.279": 142,
    "1.0.0-preview.278": 142,
    "1.0.0-preview.277": 142,
    "1.0.0-preview.276": 142,
    "1.0.0-preview.275": 142,
    "1.0.0-preview.274": 142,
    "1.0.0-preview.273": 142,
}
BINARY_RELEASE_WORKFLOW_COMMANDS: dict[str, tuple[str, ...]] = {
    "1.0.0-preview.281": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.280": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.279": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.278": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.277": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.276": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.275": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.274": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.273": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.272": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.271": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.270": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.269": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.268": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.267": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish analyze",
        "npc finish apply",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.266": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.265": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.264": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.263": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.262": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.261": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.260": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.258": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.257": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.256": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.255": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.254": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.253": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.252": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.251": (
        "npc assembly preflight",
        "npc create-from-jslot",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
    "1.0.0-preview.250": (
        "npc create-from-jslot",
        "npc finish verify",
        "preset inspect",
        "preview npc",
        "workspace preflight",
    ),
}
CURRENT_PREVIEW257_FINISH_DESTINATIONS = frozenset({
    "output.zip", "source/Data/BrigitteBardotNpcManager.esp",
    "source/Skyrim.esm", "source/npcmanager-package.json",
    "source/evidence/npc-creation-proposal.json",
    "source/Data/meshes/Actors/Character/FaceGenData/FaceGeom/BrigitteBardotNpcManager.esp/00000800.nif",
    "source/Data/textures/Actors/Character/FaceGenData/FaceTint/BrigitteBardotNpcManager.esp/00000800.dds",
    "output/Data/BrigitteBardotNpcManager.esp", "output/Skyrim.esm",
    "output/npcmanager-package.json", "output/README-Finish-Core.txt",
    "output/evidence/npc-creation-proposal.json",
    "output/Data/meshes/Actors/Character/FaceGenData/FaceGeom/BrigitteBardotNpcManager.esp/00000800.nif",
    "output/Data/textures/Actors/Character/FaceGenData/FaceTint/BrigitteBardotNpcManager.esp/00000800.dds",
    "output/NPCManager/Evidence/finish-core-request.json",
    "output/NPCManager/Evidence/finish-core-proposal.json",
    "output/NPCManager/Evidence/finish-core-manifest.json",
    "output/NPCManager/Evidence/finish-core-verification.json",
    "output/NPCManager/Evidence/runtime-identities.json",
    "output/NPCManager/Evidence/diag-BrigitteBardotNpcManager.txt",
})
CURRENT_PREVIEW258_FINISH_DESTINATIONS = frozenset(
    CURRENT_PREVIEW257_FINISH_DESTINATIONS)
CURRENT_PREVIEW260_FINISH_DESTINATIONS = frozenset(
    CURRENT_PREVIEW258_FINISH_DESTINATIONS)
CURRENT_PREVIEW256_FINISH_DESTINATIONS = frozenset(
    CURRENT_PREVIEW257_FINISH_DESTINATIONS)
CURRENT_PREVIEW255_FINISH_DESTINATIONS = frozenset({
    "output.zip", "source/Data/BrigitteBardotNpcManager.esp",
    "source/Skyrim.esm", "source/npcmanager-package.json",
    "source/evidence/npc-creation-proposal.json",
    "source/Data/meshes/Actors/Character/FaceGenData/FaceGeom/BrigitteBardotNpcManager.esp/00000800.nif",
    "source/Data/textures/Actors/Character/FaceGenData/FaceTint/BrigitteBardotNpcManager.esp/00000800.dds",
    "output/Data/BrigitteBardotNpcManager.esp", "output/Skyrim.esm",
    "output/npcmanager-package.json", "output/README-Finish-Core.txt",
    "output/evidence/npc-creation-proposal.json",
    "output/Data/meshes/Actors/Character/FaceGenData/FaceGeom/BrigitteBardotNpcManager.esp/00000800.nif",
    "output/Data/textures/Actors/Character/FaceGenData/FaceTint/BrigitteBardotNpcManager.esp/00000800.dds",
    "output/NPCManager/Evidence/finish-core-request.json",
    "output/NPCManager/Evidence/finish-core-proposal.json",
    "output/NPCManager/Evidence/finish-core-manifest.json",
    "output/NPCManager/Evidence/finish-core-verification.json",
    "output/NPCManager/Evidence/runtime-identities.json",
    "output/NPCManager/Evidence/diag-BrigitteBardotNpcManager.txt",
})
CURRENT_PREVIEW254_FINISH_DESTINATIONS = CURRENT_PREVIEW255_FINISH_DESTINATIONS
HISTORICAL_PREVIEW250_FINISH_DESTINATIONS = frozenset({
    "output.zip", "source/Data/BrigitteBardotNpcManager.esp",
    "source/Skyrim.esm", "source/npcmanager-package.json",
    "source/Data/meshes/actors/character/FaceGenData/FaceGeom/BrigitteBardotNpcManager.esp/00000800.nif",
    "source/Data/textures/actors/character/FaceGenData/FaceTint/BrigitteBardotNpcManager.esp/00000800.dds",
    "output/Data/BrigitteBardotNpcManager.esp", "output/Skyrim.esm",
    "output/npcmanager-package.json", "output/README-Finish-Core.txt",
    "output/Data/meshes/actors/character/FaceGenData/FaceGeom/BrigitteBardotNpcManager.esp/00000800.nif",
    "output/Data/textures/actors/character/FaceGenData/FaceTint/BrigitteBardotNpcManager.esp/00000800.dds",
    "output/NPCManager/Evidence/finish-core-request.json",
    "output/NPCManager/Evidence/finish-core-proposal.json",
    "output/NPCManager/Evidence/finish-core-manifest.json",
    "output/NPCManager/Evidence/finish-core-verification.json",
    "output/NPCManager/Evidence/runtime-identities.json",
    "output/NPCManager/Evidence/diag-BrigitteBardotNpcManager.txt",
})
PROTOCOL_V2_KERNEL_READY_COMMANDS = {
    "capabilities": "urn:actorwright:protocol-v2:capabilities-result:v1",
    "schema export": "urn:actorwright:protocol-v2:schema-export-result:v1",
    "version": "urn:actorwright:protocol-v2:version-result:v1",
}
PROTOCOL_V2_ENVELOPE_FIELDS = {
    "protocolVersion", "schemaVersion", "command", "outcome", "exitCode",
    "requestDigest", "effects", "diagnostics", "artifacts", "authority",
    "nextActions",
}
REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
if str(REPOSITORY_ROOT) not in sys.path:
    sys.path.insert(0, str(REPOSITORY_ROOT))
from tools.verify_skyrim_follower_finish_package import (  # noqa: E402
    MAX_ENTRY_BYTES, VerificationError, parse_plugin,
)
from tools.verification.compatibility_firewall import (  # noqa: E402
    verify_release_root_compatibility,
)
PROTOCOL_V2_TEMPORARY_PARENT = REPOSITORY_ROOT / "artifacts" / "x"
PROTOCOL_V2_WORKFLOW_PROBE_ROOT = (
    Path(__file__).resolve().parent / "protocol-v2-workflow-probes")
PROTOCOL_V2_WORKFLOW_PROBE_CATALOG = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "catalog.json")
CURRENT_STAGING_PROBE_ROOT = PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "current-staging"
PUBLIC_CURRENT_STAGING_PROBE_ROOT = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "public-current-staging")
PREVIEW274_PROBE_ROOT = PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview274"
PREVIEW274_STAGING_SCHEMA_INVENTORY_SHA256 = (
    "AA1E590C2EFA6F5595BFADC4056BDAF01AE150B6ECDEFBA6F4D4E36BD973E0AA")
PREVIEW273_PROBE_ROOT = PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview273"
PREVIEW273_STAGING_SCHEMA_INVENTORY_SHA256 = (
    "AA1E590C2EFA6F5595BFADC4056BDAF01AE150B6ECDEFBA6F4D4E36BD973E0AA")
PREVIEW272_PROBE_ROOT = PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview272"
PREVIEW272_STAGING_CATALOG_SHA256 = (
    "D1A255FF010588FA57E4F4BACFD337A6C2A679E1B74B3B639D6A4D87D8E93632")
PREVIEW272_STAGING_SCHEMA_INVENTORY_SHA256 = (
    "EC4A0766F7C383448F65188448A2E5FE600539112A187340DFEC6193584B5942")
CURRENT_STAGING_CATALOG_SHA256 = PREVIEW272_STAGING_CATALOG_SHA256
PUBLIC_CURRENT_STAGING_CATALOG_SHA256 = "1298050398DBBF5925458B465EDEC84D5941F0B73E4F8AAA63BBA783FD1E215A"
CURRENT_STAGING_SCHEMA_INVENTORY_SHA256 = (
    "A77D5A555EF4234B22CEAECAC9D103555D5835FB0BD4F481FD0ADF6D6BFB5C09")
CURRENT_STAGING_FINISH_POLICY_TEMPLATE_SHA256 = (
    "C4A1A674F83D9661734CA216006BC8D77E70D18BF59F3FD4736600F5C50665C5")
PUBLIC_CURRENT_STAGING_FINISH_POLICY_TEMPLATE_SHA256 = "D1A437DF9195B198F8411929969C4571246594C3B7A610AE028D73F4094FDE91"
PUBLIC_FINISH_MASTER_ENVIRONMENT_VARIABLE = "ACTORWRIGHT_TEST_SKYRIM_MASTER"
PUBLIC_FINISH_TEMPLATE_GROUP_BOUND_SHA256 = (
    "FBA3CCA0EFF98528DA3985962FF9058EE7662EA944C250F3FFB90A0490D52685")
PUBLIC_FINISH_TEMPLATE_RAW_LENGTH = 532
CURRENT_STAGING_WORKFLOWS = frozenset({
    "npc assembly preflight", "workspace preflight", "preset inspect",
    "npc create-from-jslot", "npc finish analyze", "npc finish apply",
    "npc finish verify", "preview npc",
})
PROTOCOL_V2_WORKFLOW_PROBE_ROOTS: dict[str, Path] = {
    "1.0.0-preview.281": PUBLIC_CURRENT_STAGING_PROBE_ROOT,
    "1.0.0-preview.280": CURRENT_STAGING_PROBE_ROOT,
    "1.0.0-preview.279": CURRENT_STAGING_PROBE_ROOT,
    "1.0.0-preview.278": CURRENT_STAGING_PROBE_ROOT,
    "1.0.0-preview.277": CURRENT_STAGING_PROBE_ROOT,
    "1.0.0-preview.276": CURRENT_STAGING_PROBE_ROOT,
    "1.0.0-preview.275": CURRENT_STAGING_PROBE_ROOT,
    "1.0.0-preview.274": PREVIEW274_PROBE_ROOT,
    "1.0.0-preview.273": PREVIEW273_PROBE_ROOT,
    "1.0.0-preview.272": PREVIEW272_PROBE_ROOT,
    "1.0.0-preview.271": PREVIEW272_PROBE_ROOT,
    "1.0.0-preview.270": PREVIEW272_PROBE_ROOT,
    "1.0.0-preview.269": PREVIEW272_PROBE_ROOT,
    "1.0.0-preview.268": PREVIEW272_PROBE_ROOT,
    "1.0.0-preview.267": PREVIEW272_PROBE_ROOT,
    "1.0.0-preview.266": PROTOCOL_V2_WORKFLOW_PROBE_ROOT,
    "1.0.0-preview.265": PROTOCOL_V2_WORKFLOW_PROBE_ROOT,
    "1.0.0-preview.264": PROTOCOL_V2_WORKFLOW_PROBE_ROOT,
    "1.0.0-preview.263": PROTOCOL_V2_WORKFLOW_PROBE_ROOT,
    "1.0.0-preview.262": PROTOCOL_V2_WORKFLOW_PROBE_ROOT,
    "1.0.0-preview.261": PROTOCOL_V2_WORKFLOW_PROBE_ROOT,
    "1.0.0-preview.260": PROTOCOL_V2_WORKFLOW_PROBE_ROOT,
    "1.0.0-preview.258": PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview258",
    "1.0.0-preview.257": PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview257",
    "1.0.0-preview.256": PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview256",
    "1.0.0-preview.255": PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview255",
    # Preview.253 and Preview.254 shipped the same probe bytes as Preview.252.
    # Keep those rollback identities on the frozen snapshot now that the
    # current Finish verifier requires the post-Preview.254 archive contract.
    "1.0.0-preview.254": PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview252",
    "1.0.0-preview.253": PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview252",
    "1.0.0-preview.252": PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview252",
    "1.0.0-preview.251": PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview251",
    "1.0.0-preview.250": PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "preview250",
}
WORKSPACE_PREFLIGHT_RESULT_SCHEMA = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT /
    "workspace-preflight-result.schema.json")
WORKSPACE_PREFLIGHT_RESULT_SCHEMA_CANONICAL_SHA256 = (
    "2C7870422CCE68705D304D41C1A98043DD51E489DE1F34F8C0ADDA4348D9AED1")
PRESET_INSPECT_RESULT_SCHEMA_CANONICAL_SHA256 = (
    "BCC3FBF053F42E0FAC88E99C031C6FF38E84E4067F5E4ACF128CCBA652A52DF6")
PRESET_INSPECTION_RECEIPT_SCHEMA_CANONICAL_SHA256 = (
    "CCD1A6A6B950BE453AB94C6617BAAD8FEB6FA7E036A03F8FAAC22745AE50361A")
PRESET_INSPECTION_CURRENT_RECEIPT_SCHEMA = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT /
    "preset-inspection-current-receipt.schema.json")
PRESET_INSPECTION_CURRENT_RECEIPT_SCHEMA_CANONICAL_SHA256 = (
    "F832C6924D1521E2F5EDDBB22B381B3C478406CF489F80FF5335700F96F8C2A9")
NPC_CREATE_PREFLIGHT_RESULT_SCHEMA = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT /
    "npc-create-preflight-result.schema.json")
NPC_CREATE_PREFLIGHT_RESULT_SCHEMA_CANONICAL_SHA256 = (
    "E65B08CC542D9A307EC15F7AC05FA938B9E09EE2B14B4510A12DE66172A9E24B")
NPC_BUILD_PREFLIGHT_ARTIFACT_SCHEMA = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT /
    "npc-build-preflight-artifact.schema.json")
NPC_BUILD_PREFLIGHT_ARTIFACT_SCHEMA_CANONICAL_SHA256 = (
    "E4EC6ED7CD022D71866469DCBE67745C391630E11FF7A7555FDB6E26490C604A")
NPC_CREATE_REQUEST_SCHEMA_IDENTIFIER = "npc.create-from-jslot.request.v1"
NPC_CREATE_REQUEST_JSON_SCHEMA_IDENTIFIER = (
    "urn:actorwright:schema:npc.create-from-jslot.request.v1")
PREVIEW272_NPC_CREATE_REQUEST_JSON_SCHEMA_CANONICAL_SHA256 = (
    "B9ABF5511BE3901E50DCD220678F6EC1FB7D13E11C7AE2F67D27C239E186D68D")
CURRENT_NPC_CREATE_REQUEST_JSON_SCHEMA_CANONICAL_SHA256 = (
    "5FAD1E9A3C27A01B4D585B8D966BFD8A4786DB6AD8D9818DF2EC4690DB3098A4")
NPC_CREATE_REQUEST_REQUIRED_DEFINITIONS = frozenset({
    "nonEmptyString", "path", "sha256", "plugin", "formId",
    "formReference", "nullableFormReference", "presetBundle",
    "productFixtureBundle", "providerContext", "standaloneAssets",
    "output", "existingNpcTarget", "identity", "editorId", "npcName",
    "traits", "references", "stats",
})
FINISH_VERIFY_RESULT_SCHEMA = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "finish-verify-result.schema.json")
FINISH_VERIFY_RESULT_SCHEMA_CANONICAL_SHA256 = (
    "E3FA424E15AA291C5F5F417D5C0D6E0A86C15A40C4923354BA7F5AF32E4F4FD8")
ACTOR_ASSEMBLY_PROTOCOL_RESULT_SCHEMA = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT /
    "actor-assembly-preflight-result.schema.json")
ACTOR_ASSEMBLY_PROTOCOL_RESULT_SCHEMA_CANONICAL_SHA256 = (
    "E3817BA715453ECA61511740AA7A3AF3F7B023905D65EA241C64933B1E1991C9")
ACTOR_ASSEMBLY_CONTRACT_SCHEMA = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "actor-assembly-contract.schema.json")
ACTOR_ASSEMBLY_CONTRACT_SCHEMA_CANONICAL_SHA256 = (
    "D8C0091B955AD54898CC895B56BF3A075F3E7B281E619FFF2C1511BE4D67E4B6")
ACTOR_ASSEMBLY_DOCUMENT_RESULT_SCHEMA = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT /
    "actor-assembly-document-result.schema.json")
ACTOR_ASSEMBLY_DOCUMENT_RESULT_SCHEMA_CANONICAL_SHA256 = (
    "BF7154039641CB8C8F95D2BD028CDB5C60564E043EBEF5A0FE65C55EA6A4044E")
ACTOR_ASSEMBLY_ERROR_SCHEMA = (
    PROTOCOL_V2_WORKFLOW_PROBE_ROOT / "actor-assembly-error.schema.json")
ACTOR_ASSEMBLY_ERROR_SCHEMA_CANONICAL_SHA256 = (
    "D262393D96C205BC6CCBA0644E2293FD3AA632086AA5D49D66304FAB955B45D7")
PREVIEW255_WORKFLOW_SCHEMA_CANONICAL_SHA256 = {
    "workspace-preflight-result.schema.json":
        WORKSPACE_PREFLIGHT_RESULT_SCHEMA_CANONICAL_SHA256,
    "preset-inspect-result.schema.json":
        PRESET_INSPECT_RESULT_SCHEMA_CANONICAL_SHA256,
    "preset-inspection-receipt.schema.json":
        PRESET_INSPECTION_RECEIPT_SCHEMA_CANONICAL_SHA256,
    "preset-inspection-current-receipt.schema.json":
        PRESET_INSPECTION_CURRENT_RECEIPT_SCHEMA_CANONICAL_SHA256,
    "npc-create-preflight-result.schema.json":
        NPC_CREATE_PREFLIGHT_RESULT_SCHEMA_CANONICAL_SHA256,
    "npc-build-preflight-artifact.schema.json":
        NPC_BUILD_PREFLIGHT_ARTIFACT_SCHEMA_CANONICAL_SHA256,
    "finish-verify-result.schema.json":
        FINISH_VERIFY_RESULT_SCHEMA_CANONICAL_SHA256,
    "actor-assembly-preflight-result.schema.json":
        ACTOR_ASSEMBLY_PROTOCOL_RESULT_SCHEMA_CANONICAL_SHA256,
    "actor-assembly-contract.schema.json":
        ACTOR_ASSEMBLY_CONTRACT_SCHEMA_CANONICAL_SHA256,
    "actor-assembly-document-result.schema.json":
        ACTOR_ASSEMBLY_DOCUMENT_RESULT_SCHEMA_CANONICAL_SHA256,
    "actor-assembly-error.schema.json":
        ACTOR_ASSEMBLY_ERROR_SCHEMA_CANONICAL_SHA256,
}
PREVIEW256_WORKFLOW_SCHEMA_CANONICAL_SHA256 = {
    "workspace-preflight-result.schema.json":
        "2C7870422CCE68705D304D41C1A98043DD51E489DE1F34F8C0ADDA4348D9AED1",
    "preset-inspect-result.schema.json":
        "BCC3FBF053F42E0FAC88E99C031C6FF38E84E4067F5E4ACF128CCBA652A52DF6",
    "preset-inspection-receipt.schema.json":
        "CCD1A6A6B950BE453AB94C6617BAAD8FEB6FA7E036A03F8FAAC22745AE50361A",
    "preset-inspection-current-receipt.schema.json":
        "F832C6924D1521E2F5EDDBB22B381B3C478406CF489F80FF5335700F96F8C2A9",
    "npc-create-preflight-result.schema.json":
        "E65B08CC542D9A307EC15F7AC05FA938B9E09EE2B14B4510A12DE66172A9E24B",
    "npc-build-preflight-artifact.schema.json":
        "E4EC6ED7CD022D71866469DCBE67745C391630E11FF7A7555FDB6E26490C604A",
    "finish-verify-result.schema.json":
        "E3FA424E15AA291C5F5F417D5C0D6E0A86C15A40C4923354BA7F5AF32E4F4FD8",
    "actor-assembly-preflight-result.schema.json":
        "E3817BA715453ECA61511740AA7A3AF3F7B023905D65EA241C64933B1E1991C9",
    "actor-assembly-contract.schema.json":
        "D8C0091B955AD54898CC895B56BF3A075F3E7B281E619FFF2C1511BE4D67E4B6",
    "actor-assembly-document-result.schema.json":
        "BF7154039641CB8C8F95D2BD028CDB5C60564E043EBEF5A0FE65C55EA6A4044E",
    "actor-assembly-error.schema.json":
        "D262393D96C205BC6CCBA0644E2293FD3AA632086AA5D49D66304FAB955B45D7",
}
PREVIEW257_WORKFLOW_SCHEMA_CANONICAL_SHA256 = {
    "workspace-preflight-result.schema.json":
        "2C7870422CCE68705D304D41C1A98043DD51E489DE1F34F8C0ADDA4348D9AED1",
    "preset-inspect-result.schema.json":
        "BCC3FBF053F42E0FAC88E99C031C6FF38E84E4067F5E4ACF128CCBA652A52DF6",
    "preset-inspection-receipt.schema.json":
        "CCD1A6A6B950BE453AB94C6617BAAD8FEB6FA7E036A03F8FAAC22745AE50361A",
    "preset-inspection-current-receipt.schema.json":
        "F832C6924D1521E2F5EDDBB22B381B3C478406CF489F80FF5335700F96F8C2A9",
    "npc-create-preflight-result.schema.json":
        "E65B08CC542D9A307EC15F7AC05FA938B9E09EE2B14B4510A12DE66172A9E24B",
    "npc-build-preflight-artifact.schema.json":
        "E4EC6ED7CD022D71866469DCBE67745C391630E11FF7A7555FDB6E26490C604A",
    "finish-verify-result.schema.json":
        "E3FA424E15AA291C5F5F417D5C0D6E0A86C15A40C4923354BA7F5AF32E4F4FD8",
    "actor-assembly-preflight-result.schema.json":
        "E3817BA715453ECA61511740AA7A3AF3F7B023905D65EA241C64933B1E1991C9",
    "actor-assembly-contract.schema.json":
        "D8C0091B955AD54898CC895B56BF3A075F3E7B281E619FFF2C1511BE4D67E4B6",
    "actor-assembly-document-result.schema.json":
        "BF7154039641CB8C8F95D2BD028CDB5C60564E043EBEF5A0FE65C55EA6A4044E",
    "actor-assembly-error.schema.json":
        "D262393D96C205BC6CCBA0644E2293FD3AA632086AA5D49D66304FAB955B45D7",
}
PREVIEW258_WORKFLOW_SCHEMA_CANONICAL_SHA256 = {
    "workspace-preflight-result.schema.json":
        "2C7870422CCE68705D304D41C1A98043DD51E489DE1F34F8C0ADDA4348D9AED1",
    "preset-inspect-result.schema.json":
        "BCC3FBF053F42E0FAC88E99C031C6FF38E84E4067F5E4ACF128CCBA652A52DF6",
    "preset-inspection-receipt.schema.json":
        "CCD1A6A6B950BE453AB94C6617BAAD8FEB6FA7E036A03F8FAAC22745AE50361A",
    "preset-inspection-current-receipt.schema.json":
        "F832C6924D1521E2F5EDDBB22B381B3C478406CF489F80FF5335700F96F8C2A9",
    "npc-create-preflight-result.schema.json":
        "E65B08CC542D9A307EC15F7AC05FA938B9E09EE2B14B4510A12DE66172A9E24B",
    "npc-build-preflight-artifact.schema.json":
        "E4EC6ED7CD022D71866469DCBE67745C391630E11FF7A7555FDB6E26490C604A",
    "finish-verify-result.schema.json":
        "E3FA424E15AA291C5F5F417D5C0D6E0A86C15A40C4923354BA7F5AF32E4F4FD8",
    "actor-assembly-preflight-result.schema.json":
        "E3817BA715453ECA61511740AA7A3AF3F7B023905D65EA241C64933B1E1991C9",
    "actor-assembly-contract.schema.json":
        "D8C0091B955AD54898CC895B56BF3A075F3E7B281E619FFF2C1511BE4D67E4B6",
    "actor-assembly-document-result.schema.json":
        "BF7154039641CB8C8F95D2BD028CDB5C60564E043EBEF5A0FE65C55EA6A4044E",
    "actor-assembly-error.schema.json":
        "D262393D96C205BC6CCBA0644E2293FD3AA632086AA5D49D66304FAB955B45D7",
}
JSON_SCHEMA_DRAFT = "https://json-schema.org/draft/2020-12/schema"
PROTOCOL_AUTHORITY_KINDS = {
    "inputAdmission",
    "sourceProviderIdentity",
    "deterministicMaterialization",
    "independentStaticVerification",
    "offEnginePreview",
    "humanVisualAcceptance",
    "gameRuntimeVerification",
    "promotionApproval",
}
PROTOCOL_AUTHORITY_STATES = {
    "established", "required", "blocked", "notApplicable",
}
PROHIBITED_KERNEL_AUTHORITY = {
    "humanVisualAcceptance",
    "gameRuntimeVerification",
    "promotionApproval",
}


class ReleaseError(ValueError):
    pass


def _select_binary_release_identity(
    version: object,
    source_line: object,
) -> dict[str, str]:
    identity = BINARY_RELEASE_IDENTITIES.get(str(version))
    if identity is None or source_line != identity["sourceLine"]:
        raise ReleaseError(
            "binary package identity is not a closed current-or-rollback release")
    return identity


def _select_protocol_v2_probe_root(version: str) -> Path:
    root = PROTOCOL_V2_WORKFLOW_PROBE_ROOTS.get(version)
    if root is None:
        raise ReleaseError("protocol 2 workflow release identity is unknown")
    shared_root = PROTOCOL_V2_WORKFLOW_PROBE_ROOTS["1.0.0-preview.266"]
    if version in {
        "1.0.0-preview.260", "1.0.0-preview.261", "1.0.0-preview.262",
        "1.0.0-preview.263", "1.0.0-preview.264", "1.0.0-preview.265",
        "1.0.0-preview.266",
    }:
        # Preview.266 and its direct Preview.265/Preview.264/Preview.263/
        # Preview.262/Preview.261/
        # Preview.260 rollback identities
        # share the live catalogue tree. Keep the test seam for an explicitly
        # replaced shared catalogue while older rollback identities remain immutable.
        if PROTOCOL_V2_WORKFLOW_PROBE_ROOT != shared_root:
            root = PROTOCOL_V2_WORKFLOW_PROBE_ROOT
        elif PROTOCOL_V2_WORKFLOW_PROBE_CATALOG.parent != shared_root:
            root = PROTOCOL_V2_WORKFLOW_PROBE_CATALOG.parent
    return root


def digest(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def _extract_canonical_finish_pack_record(data: bytes) -> tuple[bytes, str]:
    try:
        plugin = parse_plugin(
            data, target_record=("PACK", 0x0001B217))
    except VerificationError as exception:
        raise ReleaseError(
            "the supplied Skyrim.esm has an invalid bounded plugin structure") from exception
    if len(plugin.records) != 1:
        raise ReleaseError(
            "the supplied Skyrim.esm must contain exactly one canonical Finish PACK record")
    record = plugin.records[0]
    if (
        record.form_id != 0x0001B217
        or record.groups != ((0, b"PACK"),)
        or len(record.raw) != PUBLIC_FINISH_TEMPLATE_RAW_LENGTH
    ):
        raise ReleaseError(
            "the supplied Skyrim.esm Finish PACK record has the wrong group path or size")
    digest_input = b"PACK:00000000\n" + record.raw
    group_digest = hashlib.sha256(digest_input).hexdigest().upper()
    if group_digest != PUBLIC_FINISH_TEMPLATE_GROUP_BOUND_SHA256:
        raise ReleaseError(
            "the supplied Skyrim.esm Finish PACK record does not match the approved template")
    return record.raw, group_digest.lower()


def _external_file_stamp(details: os.stat_result) -> tuple[int, int, int, int]:
    return (
        details.st_dev,
        details.st_ino,
        details.st_size,
        getattr(details, "st_mtime_ns", int(details.st_mtime * 1_000_000_000)),
    )


def _read_external_finish_pack_record() -> tuple[bytes, str]:
    variable = PUBLIC_FINISH_MASTER_ENVIRONMENT_VARIABLE
    configured = os.environ.get(variable)
    if not configured or configured != configured.strip():
        raise ReleaseError(
            f"{variable} is required and must name an ordinary local Skyrim.esm")
    windows_path = PureWindowsPath(configured)
    if (
        configured.startswith(("\\\\", "//"))
        or configured.startswith(("\\\\?\\", "\\\\.\\"))
        or not re.fullmatch(r"[A-Za-z]:", windows_path.drive)
        or windows_path.root != "\\"
        or not windows_path.is_absolute()
        or windows_path.name.casefold() != "skyrim.esm"
        or any(":" in part for part in windows_path.parts[1:])
        or os.name != "nt"
    ):
        raise ReleaseError(
            f"{variable} must be an absolute local drive-letter Skyrim.esm path")
    path = Path(os.path.abspath(configured))
    try:
        for component in reversed((path, *path.parents)):
            if (component.exists() or component.is_symlink()) and _is_reparse_point(component):
                raise ReleaseError(
                    f"{variable} must not use a reparse-point file or ancestor")
        before = path.stat()
        if not stat.S_ISREG(before.st_mode):
            raise ReleaseError(f"{variable} must name an ordinary file")
        if before.st_size <= 0 or before.st_size > MAX_ENTRY_BYTES:
            raise ReleaseError(f"{variable} file size is outside the verification bound")
        with path.open("rb") as stream:
            opened = os.fstat(stream.fileno())
            if _external_file_stamp(opened) != _external_file_stamp(before):
                raise ReleaseError(f"{variable} changed while it was opened")
            data = stream.read(MAX_ENTRY_BYTES + 1)
            after_open = os.fstat(stream.fileno())
        after = path.stat()
        if (
            len(data) != before.st_size
            or _external_file_stamp(after_open) != _external_file_stamp(before)
            or _external_file_stamp(after) != _external_file_stamp(before)
        ):
            raise ReleaseError(f"{variable} changed while it was read")
        for component in reversed((path, *path.parents)):
            if (component.exists() or component.is_symlink()) and _is_reparse_point(component):
                raise ReleaseError(
                    f"{variable} must not use a reparse-point file or ancestor")
    except ReleaseError:
        raise
    except (OSError, ValueError) as exception:
        raise ReleaseError(
            f"{variable} could not be read as an ordinary local Skyrim.esm") from exception
    return _extract_canonical_finish_pack_record(data)


def _append_public_finish_master(
    base_path: Path, destination: Path, pack_record: bytes,
) -> None:
    base = base_path.read_bytes()
    if len(base) < 38 or base[:4] != b"TES4":
        raise ReleaseError("the pinned public synthetic Skyrim master base is malformed")
    tes4_length = struct.unpack_from("<I", base, 4)[0]
    tes4_end = 24 + tes4_length
    if tes4_end < 38 or tes4_end > len(base) or base[24:28] != b"HEDR":
        raise ReleaseError("the pinned public synthetic Skyrim master base is malformed")
    hedr_length = struct.unpack_from("<H", base, 28)[0]
    if hedr_length < 8 or 30 + hedr_length > tes4_end:
        raise ReleaseError("the pinned public synthetic Skyrim master HEDR is malformed")
    record_count = struct.unpack_from("<I", base, 34)[0]
    if record_count > 0xFFFFFFFD:
        raise ReleaseError("the public synthetic Skyrim master record count cannot be extended")
    extended = bytearray(base)
    struct.pack_into("<I", extended, 34, record_count + 2)
    group = struct.pack(
        "<4sI4siHHI", b"GRUP", 24 + len(pack_record), b"PACK", 0, 0, 0, 0)
    _ensure_ordinary_directory(destination.parent, "public Finish authority directory")
    try:
        with destination.open("xb") as stream:
            stream.write(extended)
            stream.write(group)
            stream.write(pack_record)
    except OSError as exception:
        raise ReleaseError(
            "the public Finish authority master could not be created in owned scratch") from exception


def _pinned_json_schema(
    path: Path,
    expected_sha256: str,
    label: str,
) -> dict[str, Any]:
    if not path.is_file() or _is_reparse_point(path):
        raise ReleaseError(f"protocol 2 verifier-owned {label} is missing or unsafe")
    value = read_json(path)
    canonical = json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")
    if hashlib.sha256(canonical).hexdigest().upper() != expected_sha256:
        raise ReleaseError(f"protocol 2 verifier-owned {label} changed")
    return value


def _pinned_workflow_json_schema(
    release_version: str | None,
    filename: str,
    current_sha256: str,
    label: str,
) -> dict[str, Any]:
    root = PROTOCOL_V2_WORKFLOW_PROBE_ROOTS.get(
        release_version, PROTOCOL_V2_WORKFLOW_PROBE_ROOT)
    if release_version == "1.0.0-preview.255":
        expected_sha256 = PREVIEW255_WORKFLOW_SCHEMA_CANONICAL_SHA256[filename]
    elif release_version == "1.0.0-preview.256":
        expected_sha256 = PREVIEW256_WORKFLOW_SCHEMA_CANONICAL_SHA256[filename]
    elif release_version == "1.0.0-preview.257":
        expected_sha256 = PREVIEW257_WORKFLOW_SCHEMA_CANONICAL_SHA256[filename]
    elif release_version == "1.0.0-preview.258":
        expected_sha256 = PREVIEW258_WORKFLOW_SCHEMA_CANONICAL_SHA256[filename]
    else:
        expected_sha256 = current_sha256
    return _pinned_json_schema(root / filename, expected_sha256, label)


def safe_relative(value: str) -> str:
    if not value or "\\" in value or ":" in value:
        raise ReleaseError(f"non-canonical or absolute path: {value!r}")
    pure = PurePosixPath(value)
    if pure.is_absolute() or any(part in ("", ".", "..") for part in pure.parts):
        raise ReleaseError(f"path traversal refused: {value!r}")
    return pure.as_posix()


def read_json(path: Path) -> Any:
    seen: list[str] = []

    def unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        value: dict[str, Any] = {}
        for key, child in pairs:
            if key in value:
                raise ReleaseError(f"duplicate JSON key in {path.name}: {key}")
            value[key] = child
            seen.append(key)
        return value

    try:
        return json.loads(path.read_text(encoding="utf-8-sig"), object_pairs_hook=unique)
    except ReleaseError:
        raise
    except Exception as exc:
        raise ReleaseError(f"invalid JSON {path.name}: {exc}") from exc


def verify_dependency_vulnerability_report(
    path: Path,
    expected_sha256: object,
    *,
    public_release: bool,
) -> None:
    if not path.is_file() or path.is_symlink():
        raise ReleaseError("dependency vulnerability report is missing")
    if str(expected_sha256).upper() != digest(path):
        raise ReleaseError(
            "release metadata hash mismatch: "
            "dependencyVulnerabilityReportSha256")
    validator_path = (
        Path(__file__).resolve().parents[1]
        / "hardening"
        / "validate_dependency_vulnerability_report.py"
    )
    spec = importlib.util.spec_from_file_location(
        "actorwright_dependency_vulnerability_validator", validator_path)
    if spec is None or spec.loader is None:
        raise ReleaseError("dependency vulnerability validator is unavailable")
    validator = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(validator)
    errors = validator.validate_report(
        read_json(path), public_release=public_release)
    if errors:
        raise ReleaseError("; ".join(errors))


def verify_release_manifest_shape(release: object) -> None:
    common = {
        "schemaVersion", "product", "version", "sourceCommit", "sourceTag",
        "privateOnly", "runtimeAuthority", "visualAuthority",
        "capabilitiesSha256", "sbomSha256", "testSummarySha256",
        "embeddedResourceClosures",
    }
    if not isinstance(release, dict):
        raise ReleaseError("actorwright-release.json field set mismatch")
    if release.get("schemaVersion") == 1:
        if set(release) != common:
            raise ReleaseError("actorwright-release.json field set mismatch")
        binary_identity = BINARY_RELEASE_IDENTITIES.get(
            str(release.get("version")))
        if (
            binary_identity is not None
            and release.get("product") == "Actorwright"
            and release.get("sourceTag") == binary_identity["sourceTag"]
            and release.get("privateOnly") is True
            and release.get("runtimeAuthority") is False
            and release.get("visualAuthority") is False
        ):
            return
        legacy_identity = (
            release.get("product") == "Actorwright"
            and release.get("version") == "1.0.0-preview.243"
            and release.get("sourceTag") == "v1.0.0-preview.243"
            and release.get("sourceCommit") == LEGACY_PREVIEW_243_SOURCE_COMMIT
            and release.get("privateOnly") is True
            and release.get("runtimeAuthority") is False
            and release.get("visualAuthority") is False
        )
        if not legacy_identity:
            raise ReleaseError("unknown legacy release manifest identity")
        return
    if release.get("schemaVersion") == 2:
        fields = common | {"dependencyVulnerabilityReportSha256"}
        if release.get("version") in {
            "1.0.0-preview.267", "1.0.0-preview.268", "1.0.0-preview.269",
            "1.0.0-preview.270", "1.0.0-preview.271", "1.0.0-preview.272",
            "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
            "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
            "1.0.0-preview.280", "1.0.0-preview.281"}:
            fields |= {"sourceTree", "canonicalBuildLogSha256"}
        if set(release) == fields:
            return
    raise ReleaseError("actorwright-release.json field set mismatch")


def inventory(root: Path) -> dict[str, Path]:
    if not root.is_dir() or root.is_symlink():
        raise ReleaseError(f"release root missing or reparse point: {root}")
    top_dirs = {path.name for path in root.iterdir() if path.is_dir()}
    top_files = {path.name for path in root.iterdir() if path.is_file()}
    if top_dirs != REQUIRED_DIRS:
        forbidden = sorted(top_dirs & FORBIDDEN_DIRS)
        if forbidden:
            raise ReleaseError(f"source/test/Git directory refused: {forbidden}")
        raise ReleaseError(f"release directory set mismatch: {sorted(top_dirs)}")
    if top_files != REQUIRED_FILES:
        raise ReleaseError(f"release top-level file set mismatch: {sorted(top_files)}")
    result: dict[str, Path] = {}
    for path in root.rglob("*"):
        relative = path.relative_to(root).as_posix()
        if path.is_symlink():
            raise ReleaseError(f"reparse point refused: {relative}")
        if path.is_dir():
            if any(part.lower() in FORBIDDEN_DIRS for part in PurePosixPath(relative).parts):
                raise ReleaseError(f"source/test/Git directory refused: {relative}")
            continue
        if any(part.lower() in FORBIDDEN_DIRS for part in PurePosixPath(relative).parts) or (path.suffix.lower() in FORBIDDEN_SUFFIXES and relative != "cli/actorwright.ps1"):
            raise ReleaseError(f"source, test, credential, or debug file refused: {relative}")
        result[relative] = path
    if "cli/actorwright.exe" not in result or "cli/npcm.cmd" not in result or "desktop/Actorwright.Desktop.exe" not in result:
        raise ReleaseError("required CLI, compatibility launcher, or desktop executable missing")
    wrapper = result.get("cli/actorwright.ps1")
    if wrapper is not None and wrapper.read_bytes() != POWERSHELL_WRAPPER:
        raise ReleaseError("PowerShell wrapper content differs")
    if {p for p in result if p.startswith("cli/")} not in (
        {"cli/actorwright.exe", "cli/npcm.cmd"},
        {"cli/actorwright.exe", "cli/npcm.cmd", "cli/actorwright.ps1"},
    ):
        raise ReleaseError("CLI directory contains undeclared entry points")
    if {p for p in result if p.startswith("desktop/")} != {"desktop/Actorwright.Desktop.exe"}:
        raise ReleaseError("desktop directory must contain exactly Actorwright.Desktop.exe")
    return result


def verify_release_wrapper(files: dict[str, Path], version: str) -> None:
    if version in {
        "1.0.0-preview.267", "1.0.0-preview.268", "1.0.0-preview.269",
        "1.0.0-preview.270", "1.0.0-preview.271", "1.0.0-preview.272",
        "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
        "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
        "1.0.0-preview.280",
        "1.0.0-preview.281"
    } and "cli/actorwright.ps1" not in files:
        raise ReleaseError("required PowerShell wrapper missing")


def inventory_package_staging(
    root: Path,
    *,
    allow_launcher: bool = False,
) -> dict[str, Path]:
    if not root.is_dir() or root.is_symlink():
        raise ReleaseError(
            f"binary package root missing or reparse point: {root}")
    top_dirs = {path.name for path in root.iterdir() if path.is_dir()}
    top_files = {path.name for path in root.iterdir() if path.is_file()}
    if top_dirs != PACKAGE_DIRS:
        raise ReleaseError(
            f"binary package directory set mismatch: {sorted(top_dirs)}")
    if top_files != PACKAGE_FILES:
        raise ReleaseError(
            f"binary package top-level file set mismatch: {sorted(top_files)}")
    result: dict[str, Path] = {}
    for path in root.rglob("*"):
        relative = path.relative_to(root).as_posix()
        if path.is_symlink():
            raise ReleaseError(f"reparse point refused: {relative}")
        if path.is_dir():
            continue
        if path.suffix.lower() in FORBIDDEN_SUFFIXES and not (
            allow_launcher and relative == "cli/actorwright.ps1"
        ):
            raise ReleaseError(
                f"source, credential, or debug file refused: {relative}")
        result[relative] = path
    cli_files = {path for path in result if path.startswith("cli/")}
    expected_cli_files = {"cli/actorwright.exe"}
    if allow_launcher and "cli/actorwright.ps1" in cli_files:
        expected_cli_files.add("cli/actorwright.ps1")
    if cli_files != expected_cli_files:
        raise ReleaseError(
            "binary package CLI directory must contain exactly actorwright.exe")
    launcher = result.get("cli/actorwright.ps1")
    if launcher is not None and (
        launcher.stat().st_size != len(POWERSHELL_WRAPPER)
        or launcher.read_bytes() != POWERSHELL_WRAPPER
    ):
        raise ReleaseError("PowerShell wrapper content differs")
    if {path for path in result if path.startswith("desktop/")} != {
        "desktop/Actorwright.Desktop.exe"
    }:
        raise ReleaseError(
            "binary package desktop directory must contain exactly Actorwright.Desktop.exe")
    schema_files = {path for path in result if path.startswith("schemas/")}
    if schema_files != PACKAGE_SCHEMA_FILES:
        raise ReleaseError(
            "binary package Exchange schema inventory mismatch: "
            f"actual={sorted(schema_files)}")
    for relative in sorted(schema_files):
        if b"\r" in result[relative].read_bytes():
            raise ReleaseError(
                f"binary package Exchange schema must use canonical UTF-8 LF bytes: {relative}")
    return result


def verify_hashes(root: Path, files: dict[str, Path]) -> None:
    hash_file = root / "SHA256SUMS"
    lines = hash_file.read_text(encoding="utf-8-sig").splitlines()
    declared: dict[str, str] = {}
    for line in lines:
        if "  " not in line:
            raise ReleaseError("malformed SHA256SUMS line")
        sha, relative = line.split("  ", 1)
        relative = safe_relative(relative)
        if relative in declared or not HASH_RE.fullmatch(sha):
            raise ReleaseError(f"duplicate path or malformed uppercase hash: {relative}")
        declared[relative] = sha
    expected = set(files) - {"SHA256SUMS"}
    if list(declared) != sorted(declared) or set(declared) != expected:
        raise ReleaseError(f"SHA256SUMS inventory mismatch: undeclared={sorted(expected-set(declared))}, missing={sorted(set(declared)-expected)}")
    for relative, sha in declared.items():
        if digest(files[relative]) != sha:
            raise ReleaseError(f"hash mismatch: {relative}")


def _normalized_closure(value: Any) -> tuple[str, tuple[tuple[str, str, int, str, str], ...]]:
    if not isinstance(value, dict) or set(value) != {
        "entrypoint", "probeSha256", "runtimeManifestSha256", "entries"
    }:
        raise ReleaseError("embedded resource closure field set mismatch")
    entrypoint = safe_relative(str(value["entrypoint"]))
    if entrypoint not in {"cli/actorwright.exe", "desktop/Actorwright.Desktop.exe"}:
        raise ReleaseError(f"unexpected embedded resource entrypoint: {entrypoint}")
    for field in ("probeSha256", "runtimeManifestSha256"):
        if not re.fullmatch(r"[A-Fa-f0-9]{64}", str(value[field])):
            raise ReleaseError(f"malformed embedded closure hash: {field}")
    entries = value["entries"]
    if not isinstance(entries, list) or len(entries) not in {11, 19}:
        raise ReleaseError(
            "embedded resource closure must contain the exact base resources, "
            "optionally followed by the complete product-provider bundle")
    normalized: list[tuple[str, str, int, str, str]] = []
    roles: set[str] = set()
    for row in entries:
        if not isinstance(row, dict) or set(row) != {"role", "path", "length", "sha256", "storage"}:
            raise ReleaseError("embedded resource entry field set mismatch")
        role = str(row["role"])
        path = str(row["path"])
        length = row["length"]
        sha = str(row["sha256"]).upper()
        storage = str(row["storage"])
        if not role or role in roles or not isinstance(length, int) or length < 0 or not HASH_RE.fullmatch(sha):
            raise ReleaseError(f"invalid embedded resource entry: {role!r}")
        if storage == "content":
            safe_relative(path)
        elif storage == "managed-resource":
            if not path.startswith("assembly://NpcManager.Rendering/") or ":" in path.removeprefix("assembly:"):
                raise ReleaseError(f"invalid managed resource path: {path!r}")
        else:
            raise ReleaseError(f"invalid embedded resource storage: {storage!r}")
        roles.add(role)
        normalized.append((role, path, length, sha, storage))
    required_roles = {
        "reference-runtime-manifest", "native-c-api", "opencv-runtime",
        "vc-runtime-concurrency", "vc-runtime-cpp", "vc-runtime-core",
        "vc-runtime-core-1", "face-detector-model", "face-landmarker-model",
        "npc-preview-profile-manifest", "npc-preview-render-script",
    }
    optional_provider_roles = {
        "product-provider-registry", "product-provider-provenance",
        "product-provider-manifest", "product-provider-template-plugin",
        "product-provider-facegeom-carrier", "product-provider-facetint-manifest",
        "product-provider-facetint-source", "product-provider-dependency-manifest",
    }
    if (roles != required_roles and
            roles != required_roles | optional_provider_roles):
        raise ReleaseError(f"embedded resource role set mismatch: {sorted(roles)}")
    return entrypoint, tuple(normalized)


def verify_stored_resource_closures(
    root: Path,
    declarations: Any,
    probe_names: dict[str, str],
    artifact: str,
) -> None:
    if not isinstance(declarations, list) or len(declarations) != 2:
        raise ReleaseError(
            f"{artifact} must bind exactly two embedded resource closures")
    expected = dict(_normalized_closure(item) for item in declarations)
    if set(expected) != set(probe_names):
        raise ReleaseError(
            f"{artifact} closure entrypoint set mismatch")
    for declaration in declarations:
        entrypoint, normalized = _normalized_closure(declaration)
        probe_path = root / probe_names[entrypoint]
        if str(declaration["probeSha256"]).upper() != digest(probe_path):
            raise ReleaseError(
                f"{artifact} probe hash mismatch: {entrypoint}")
        probe = read_json(probe_path)
        if (
            not isinstance(probe, dict)
            or set(probe) != {
                "schemaVersion", "accepted", "resourceBase",
                "runtimeManifestSha256", "entries",
            }
            or probe["schemaVersion"] != 1
            or probe["accepted"] is not True
        ):
            raise ReleaseError(
                f"stored {artifact} probe is invalid: {entrypoint}")
        observed = {
            "entrypoint": entrypoint,
            "probeSha256": declaration["probeSha256"],
            "runtimeManifestSha256": probe["runtimeManifestSha256"],
            "entries": probe["entries"],
        }
        _, observed_normalized = _normalized_closure(observed)
        if (
            str(declaration["runtimeManifestSha256"]).upper()
            != str(probe["runtimeManifestSha256"]).upper()
        ):
            raise ReleaseError(
                f"stored {artifact} runtime manifest closure drift: "
                f"{entrypoint}")
        if observed_normalized != normalized:
            raise ReleaseError(
                f"stored {artifact} closure drift: {entrypoint}")


def verify_embedded_resource_closures(root: Path, release: dict[str, Any]) -> None:
    declarations = release["embeddedResourceClosures"]
    if not isinstance(declarations, list) or len(declarations) != 2:
        raise ReleaseError("release must bind exactly two embedded resource closures")
    expected = dict(_normalized_closure(item) for item in declarations)
    if set(expected) != {"cli/actorwright.exe", "desktop/Actorwright.Desktop.exe"}:
        raise ReleaseError("embedded resource closure entrypoint set mismatch")

    with tempfile.TemporaryDirectory(prefix="actorwright-resource-probe-") as temporary:
        temporary_root = Path(temporary)
        for index, relative in enumerate(sorted(expected)):
            extraction_root = temporary_root / f"extract-{index}"
            workspace_root = temporary_root / f"workspace-{index}"
            extraction_root.mkdir()
            workspace_root.mkdir()
            environment = os.environ.copy()
            environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = str(extraction_root)
            environment["ACTORWRIGHT_WORKSPACE_ROOT"] = str(workspace_root)
            try:
                process = subprocess.run(
                    [str(root / relative), "--resource-extraction-probe"],
                    cwd=workspace_root,
                    env=environment,
                    text=True,
                    capture_output=True,
                    timeout=90,
                    check=False,
                )
            except subprocess.TimeoutExpired as exc:
                raise ReleaseError(
                    f"embedded resource probe timed out: {relative}") from exc
            if process.returncode != 0:
                raise ReleaseError(
                    f"embedded resource probe failed for {relative}: "
                    f"exit={process.returncode} stderr={process.stderr.strip()}"
                )
            try:
                observed = json.loads(process.stdout)
            except Exception as exc:
                raise ReleaseError(f"invalid resource probe JSON for {relative}: {exc}") from exc
            if not isinstance(observed, dict) or observed.get("schemaVersion") != 1 or observed.get("accepted") is not True:
                raise ReleaseError(f"resource probe refused for {relative}")
            resource_base = Path(str(observed.get("resourceBase", ""))).resolve()
            try:
                resource_base.relative_to(extraction_root.resolve())
            except ValueError as exc:
                raise ReleaseError(f"resource probe escaped fresh extraction root for {relative}: {resource_base}") from exc
            probe_closure = {
                "entrypoint": relative,
                "probeSha256": next(item["probeSha256"] for item in declarations if item["entrypoint"] == relative),
                "runtimeManifestSha256": observed.get("runtimeManifestSha256"),
                "entries": observed.get("entries"),
            }
            _, normalized = _normalized_closure(probe_closure)
            declaration = next(
                item for item in declarations
                if item["entrypoint"] == relative)
            if (
                str(declaration["runtimeManifestSha256"]).upper()
                != str(observed.get("runtimeManifestSha256", "")).upper()
            ):
                raise ReleaseError(
                    f"extracted runtime manifest closure drift: {relative}")
            if normalized != expected[relative]:
                raise ReleaseError(f"extracted resource closure drift: {relative}")


def _is_reparse_point(path: Path) -> bool:
    try:
        attributes = path.lstat().st_file_attributes
    except AttributeError:
        attributes = 0
    return path.is_symlink() or bool(
        attributes & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0))


def _ensure_ordinary_directory(path: Path, label: str) -> None:
    try:
        if path.exists() or path.is_symlink():
            if not path.is_dir() or _is_reparse_point(path):
                raise ReleaseError(f"{label} must be an ordinary directory: {path}")
        else:
            path.mkdir()
        if not path.is_dir() or _is_reparse_point(path):
            raise ReleaseError(f"{label} must be an ordinary directory: {path}")
    except ReleaseError:
        raise
    except OSError as exc:
        raise ReleaseError(f"{label} could not be prepared: {path}") from exc


def _verified_protocol_temporary_parent() -> Path:
    _ensure_ordinary_directory(REPOSITORY_ROOT, "repository root")
    artifacts = REPOSITORY_ROOT / "artifacts"
    _ensure_ordinary_directory(artifacts, "protocol 2 artifacts root")
    _ensure_ordinary_directory(
        PROTOCOL_V2_TEMPORARY_PARENT,
        "protocol 2 temporary root",
    )
    try:
        if (
            artifacts.parent.resolve(strict=True)
                != REPOSITORY_ROOT.resolve(strict=True)
            or PROTOCOL_V2_TEMPORARY_PARENT.parent.resolve(strict=True)
                != artifacts.resolve(strict=True)
        ):
            raise ReleaseError(
                "protocol 2 temporary root escaped repository-local artifacts/x")
    except OSError as exc:
        raise ReleaseError(
            "protocol 2 temporary root could not be resolved") from exc
    return PROTOCOL_V2_TEMPORARY_PARENT


def _cleanup_protocol_temporary_root(root: Path, parent: Path) -> None:
    if not root.exists() and not root.is_symlink():
        return
    if (
        parent != PROTOCOL_V2_TEMPORARY_PARENT
        or root.parent != parent
        or not root.name.startswith("p-")
        or not parent.is_dir()
        or _is_reparse_point(parent)
        or not root.is_dir()
        or _is_reparse_point(root)
    ):
        raise ReleaseError(
            "protocol 2 temporary child is unsafe to clean")
    try:
        for current, directories, files in os.walk(
            root, topdown=True, followlinks=False
        ):
            for name in [*directories, *files]:
                candidate = Path(current) / name
                if _is_reparse_point(candidate):
                    raise ReleaseError(
                        "protocol 2 temporary child contains a reparse point")
        shutil.rmtree(root)
    except ReleaseError:
        raise
    except OSError as exc:
        raise ReleaseError(
            "protocol 2 temporary child cleanup failed") from exc


def _is_nonempty_string(value: Any) -> bool:
    return isinstance(value, str) and bool(value.strip())


def _validate_envelope_items(
    envelope: dict[str, Any],
    expected_command: str,
) -> None:
    for index, effect in enumerate(envelope["effects"]):
        if (
            not isinstance(effect, dict)
            or set(effect) != {"kind", "status", "scope"}
            or any(not _is_nonempty_string(effect.get(field)) for field in (
                "kind", "status", "scope"))
        ):
            raise ReleaseError(
                f"protocol 2 effect shape mismatch: {expected_command}[{index}]")

    for index, diagnostic in enumerate(envelope["diagnostics"]):
        if (
            not isinstance(diagnostic, dict)
            or set(diagnostic) != {
                "code", "severity", "message", "class", "recovery"}
            or not all(_is_nonempty_string(diagnostic.get(field)) for field in (
                "code", "severity", "message", "class"))
            or not isinstance(diagnostic.get("recovery"), dict)
        ):
            raise ReleaseError(
                f"protocol 2 diagnostic shape mismatch: "
                f"{expected_command}[{index}]")

    for index, artifact in enumerate(envelope["artifacts"]):
        fields = set(artifact) if isinstance(artifact, dict) else set()
        required = {
            "kind", "schemaOrMediaType", "path", "producerCommand",
            "requestDigest", "inputBindings", "state",
        }
        if (
            not isinstance(artifact, dict)
            or not required.issubset(fields)
            or not fields.issubset(required | {"size", "sha256"})
            or any(not _is_nonempty_string(artifact.get(field)) for field in (
                "kind", "schemaOrMediaType", "path", "producerCommand",
                "state"))
            or not HASH_RE.fullmatch(
                str(artifact.get("requestDigest", "")).upper())
            or not isinstance(artifact.get("inputBindings"), list)
            or any(not _is_nonempty_string(value)
                   for value in artifact.get("inputBindings", []))
            or (
                "size" in artifact
                and (
                    isinstance(artifact["size"], bool)
                    or not isinstance(artifact["size"], int)
                    or artifact["size"] < 0
                )
            )
            or (
                "sha256" in artifact
                and not HASH_RE.fullmatch(str(artifact["sha256"]).upper())
            )
        ):
            raise ReleaseError(
                f"protocol 2 artifact shape mismatch: {expected_command}[{index}]")

    authority_kinds: set[str] = set()
    for index, authority in enumerate(envelope["authority"]):
        if (
            not isinstance(authority, dict)
            or set(authority) != {"kind", "state", "reason"}
            or authority.get("kind") not in PROTOCOL_AUTHORITY_KINDS
            or authority.get("state") not in PROTOCOL_AUTHORITY_STATES
            or not _is_nonempty_string(authority.get("reason"))
            or authority.get("kind") in authority_kinds
        ):
            raise ReleaseError(
                f"protocol 2 authority shape mismatch: "
                f"{expected_command}[{index}]")
        authority_kinds.add(authority["kind"])
        if (
            authority["kind"] in PROHIBITED_KERNEL_AUTHORITY
            and authority["state"] == "established"
        ):
            raise ReleaseError(
                f"protocol 2 kernel authority overclaim: {expected_command}")

    for index, action in enumerate(envelope["nextActions"]):
        if (
            not isinstance(action, dict)
            or set(action) != {
                "command", "reason", "requiredBindings",
                "missingPrerequisites", "requiresHumanAction",
            }
            or not _is_nonempty_string(action.get("command"))
            or not _is_nonempty_string(action.get("reason"))
            or not isinstance(action.get("requiredBindings"), list)
            or not isinstance(action.get("missingPrerequisites"), list)
            or any(not _is_nonempty_string(value)
                   for value in action.get("missingPrerequisites", []))
            or not isinstance(action.get("requiresHumanAction"), bool)
        ):
            raise ReleaseError(
                f"protocol 2 next action shape mismatch: "
                f"{expected_command}[{index}]")
        for binding_index, binding in enumerate(action["requiredBindings"]):
            fields = set(binding) if isinstance(binding, dict) else set()
            if (
                not isinstance(binding, dict)
                or not {"option", "value"}.issubset(fields)
                or not fields.issubset(
                    {"option", "value", "artifactSha256"})
                or not _is_nonempty_string(binding.get("option"))
                or not _is_nonempty_string(binding.get("value"))
                or (
                    "artifactSha256" in binding
                    and not HASH_RE.fullmatch(
                        str(binding["artifactSha256"]).upper())
                )
            ):
                raise ReleaseError(
                    f"protocol 2 next action binding shape mismatch: "
                    f"{expected_command}[{index}][{binding_index}]")


def _validate_schema_definition(
    value: Any,
    label: str,
    *,
    expected_identifier: str | None = None,
    expected_json_identifier: str | None = None,
) -> str:
    if (
        not isinstance(value, dict)
        or set(value) != {"schemaIdentifier", "jsonSchema"}
        or not _is_nonempty_string(value.get("schemaIdentifier"))
        or not isinstance(value.get("jsonSchema"), dict)
    ):
        raise ReleaseError(f"protocol 2 {label} definition is malformed")
    schema_identifier = value["schemaIdentifier"]
    json_schema = value["jsonSchema"]
    required = json_schema.get("required")
    properties = json_schema.get("properties")
    if (
        json_schema.get("$schema") != JSON_SCHEMA_DRAFT
        or not _is_nonempty_string(json_schema.get("$id"))
        or json_schema.get("type") != "object"
        or not isinstance(required, list)
        or not required
        or any(not _is_nonempty_string(item) for item in required)
        or len(required) != len(set(required))
        or not isinstance(properties, dict)
        or not set(required).issubset(properties)
        or (
            expected_identifier is not None
            and schema_identifier != expected_identifier
        )
        or (
            expected_json_identifier is not None
            and json_schema.get("$id") != expected_json_identifier
        )
    ):
        raise ReleaseError(f"protocol 2 {label} JSON Schema is malformed")
    return schema_identifier


def _validate_local_schema_references(
    json_schema: dict[str, Any],
    definitions: dict[str, Any],
) -> None:
    def visit(node: Any) -> None:
        if isinstance(node, dict):
            reference = node.get("$ref")
            if reference is not None:
                if (
                    not isinstance(reference, str)
                    or not reference.startswith("#/$defs/")
                    or reference.removeprefix("#/$defs/") not in definitions
                ):
                    raise ReleaseError(
                        "protocol 2 NPC create request schema has an unresolved $ref")
            for child in node.values():
                visit(child)
        elif isinstance(node, list):
            for child in node:
                visit(child)

    visit(json_schema)


def _validate_npc_create_request_document(
    value: Any,
    release_version: str,
) -> None:
    if (
        not isinstance(value, dict)
        or set(value) != {"name", "direction", "schemaIdentifier", "jsonSchema"}
    ):
        raise ReleaseError("protocol 2 NPC create request document is malformed")
    json_schema = value["jsonSchema"]
    definitions = json_schema.get("$defs") if isinstance(json_schema, dict) else None
    if not isinstance(definitions, dict):
        raise ReleaseError("protocol 2 NPC create request schema mismatch")
    _validate_local_schema_references(json_schema, definitions)
    canonical_sha256 = hashlib.sha256(json.dumps(
        json_schema, sort_keys=True, separators=(",", ":"),
        ensure_ascii=False).encode("utf-8")).hexdigest().upper()
    expected_sha256 = (
        CURRENT_NPC_CREATE_REQUEST_JSON_SCHEMA_CANONICAL_SHA256
        if release_version in {
            "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275",
            "1.0.0-preview.276", "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
            "1.0.0-preview.280",
            "1.0.0-preview.281",
        }
        else PREVIEW272_NPC_CREATE_REQUEST_JSON_SCHEMA_CANONICAL_SHA256
    )
    if canonical_sha256 != expected_sha256:
        raise ReleaseError(
            "protocol 2 NPC create request schema canonical bytes changed")
    _validate_schema_definition(
        {"schemaIdentifier": value["schemaIdentifier"],
         "jsonSchema": value["jsonSchema"]},
        "NPC create request document",
        expected_identifier=NPC_CREATE_REQUEST_SCHEMA_IDENTIFIER,
        expected_json_identifier=NPC_CREATE_REQUEST_JSON_SCHEMA_IDENTIFIER,
    )
    properties = json_schema.get("properties")
    required = json_schema.get("required")
    if (
        value.get("name") != "request"
        or value.get("direction") != "input"
        or json_schema.get("additionalProperties") is not False
        or required != [
            "schemaVersion", "edition", "presetBundle", "providerContext",
            "standaloneAssets", "output", "identity", "traits", "references",
            "stats",
        ]
        or not isinstance(properties, dict)
        or set(properties) != {
            "schemaVersion", "edition", "presetBundle", "providerContext",
            "standaloneAssets", "output", "existingNpcTarget",
            "applyBodySlide", "allowInheritedMeshEmbeddedSkinTextureRoute",
            "faceGeomSkeletonAuthority", "identity", "traits", "references",
            "stats",
        }
        or properties.get("schemaVersion") != {
            "type": "integer", "enum": [1, 2, 3]}
        or not isinstance(json_schema.get("oneOf"), list)
        or len(json_schema["oneOf"]) != 3
    ):
        raise ReleaseError("protocol 2 NPC create request schema mismatch")
    if not NPC_CREATE_REQUEST_REQUIRED_DEFINITIONS.issubset(definitions):
        raise ReleaseError("protocol 2 NPC create request schema mismatch")
    composite_definitions = {
        "presetBundle", "productFixtureBundle", "providerContext",
        "standaloneAssets", "output", "identity", "traits", "references",
        "stats",
    }
    for name in composite_definitions:
        definition = definitions.get(name)
        if (
            not isinstance(definition, dict)
            or definition.get("type") != "object"
            or definition.get("additionalProperties") is not False
            or not isinstance(definition.get("properties"), dict)
        ):
            raise ReleaseError("protocol 2 NPC create request schema mismatch")
    existing_target = definitions.get("existingNpcTarget")
    if (
        not isinstance(existing_target, dict)
        or existing_target.get("type") not in ("object", ["object", "null"])
        or existing_target.get("additionalProperties") is not False
        or not isinstance(existing_target.get("properties"), dict)
        or existing_target.get("required") != [
            "sourcePlugin", "sourcePluginSha256", "targetFormId"]
    ):
        raise ReleaseError("protocol 2 NPC create request schema mismatch")

    def excludes_existing_target(arm: dict[str, Any]) -> bool:
        exclusion = arm.get("not")
        if not isinstance(exclusion, dict):
            return False
        if exclusion.get("required") == ["existingNpcTarget"]:
            return True
        alternatives = exclusion.get("anyOf")
        return isinstance(alternatives, list) and any(
            isinstance(item, dict)
            and item.get("required") == ["existingNpcTarget"]
            for item in alternatives
        )

    versions: dict[int, dict[str, Any]] = {}
    for arm in json_schema["oneOf"]:
        if not isinstance(arm, dict):
            raise ReleaseError("protocol 2 NPC create request schema mismatch")
        arm_properties = arm.get("properties")
        version = (
            arm_properties.get("schemaVersion", {}).get("const")
            if isinstance(arm_properties, dict)
            else None)
        if (
            not isinstance(version, int)
            or version in versions
            or not isinstance(arm_properties, dict)
            or arm_properties.get("schemaVersion") != {"const": version}
        ):
            raise ReleaseError("protocol 2 NPC create request schema mismatch")
        versions[version] = arm
    if set(versions) != {1, 2, 3}:
        raise ReleaseError("protocol 2 NPC create request schema mismatch")
    for version in (1, 3):
        if "existingNpcTarget" in versions[version].get("required", []):
            raise ReleaseError("protocol 2 NPC create request schema mismatch")
        if not excludes_existing_target(versions[version]):
            raise ReleaseError("protocol 2 NPC create request schema mismatch")
    if versions[2].get("required") != ["existingNpcTarget"]:
        raise ReleaseError("protocol 2 NPC create request schema mismatch")


def _run_protocol_v2_probe(
    executable: Path,
    arguments: tuple[str, ...],
    expected_command: str,
    expected_exit: int,
    *,
    workspace_root: Path,
    extraction_root: Path,
) -> dict[str, Any]:
    environment = os.environ.copy()
    environment["ACTORWRIGHT_WORKSPACE_ROOT"] = str(workspace_root)
    environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = str(extraction_root)
    try:
        process = subprocess.run(
            [str(executable), *arguments],
            cwd=workspace_root,
            env=environment,
            text=True,
            capture_output=True,
            timeout=90,
            check=False,
        )
    except subprocess.TimeoutExpired as exc:
        raise ReleaseError(
            f"protocol 2 probe timed out: {expected_command}") from exc
    if process.returncode != expected_exit or process.stderr:
        stdout = process.stdout.strip()
        if len(stdout) > 4096:
            stdout = f"{stdout[:4096]}...[truncated]"
        raise ReleaseError(
            f"protocol 2 probe failed for {expected_command}: "
            f"exit={process.returncode} stderr={process.stderr.strip()} "
            f"stdout={stdout}")
    lines = process.stdout.splitlines()
    if len(lines) != 1 or not lines[0].strip():
        raise ReleaseError(
            f"protocol 2 probe must emit exactly one JSON envelope: "
            f"{expected_command}")
    try:
        envelope = json.loads(lines[0])
    except Exception as exc:
        raise ReleaseError(
            f"protocol 2 probe emitted invalid JSON for {expected_command}: "
            f"{exc}") from exc
    fields = set(envelope) if isinstance(envelope, dict) else set()
    if (
        not isinstance(envelope, dict)
        or not PROTOCOL_V2_ENVELOPE_FIELDS.issubset(fields)
        or not fields.issubset(
            PROTOCOL_V2_ENVELOPE_FIELDS | {"correlation", "result"})
        or envelope.get("protocolVersion") != "2"
        or envelope.get("schemaVersion") != "1"
        or envelope.get("command") != expected_command
        or envelope.get("exitCode") != expected_exit
        or not HASH_RE.fullmatch(str(envelope.get("requestDigest", "")).upper())
        or any(not isinstance(envelope.get(field), list) for field in (
            "effects", "diagnostics", "artifacts", "authority", "nextActions"))
    ):
        raise ReleaseError(
            f"protocol 2 envelope contract mismatch: {expected_command}")
    _validate_envelope_items(envelope, expected_command)
    return envelope


def _validate_protocol_v2_schema_export(
    schema: dict[str, Any], release_version: str,
) -> None:
    schema_result = schema.get("result")
    required_fields = {
        "schemaId", "protocolVersion", "scopedHelpResultSchema", "contract",
        "resultSchemas", "documentSchemas",
    }
    if (
        schema.get("outcome") != "succeeded"
        or not isinstance(schema_result, dict)
        or set(schema_result) != required_fields
        or schema_result.get("schemaId")
            != PROTOCOL_V2_KERNEL_READY_COMMANDS["schema export"]
        or schema_result.get("protocolVersion") != "2"
    ):
        raise ReleaseError(
            "protocol 2 schema export result structure is malformed")

    _validate_schema_definition(
        schema_result["scopedHelpResultSchema"],
        "scoped help schema",
        expected_identifier=(
            "urn:actorwright:protocol-v2:scoped-help-result:v1"),
        expected_json_identifier=(
            "urn:actorwright:protocol-v2:scoped-help-result:v1"),
    )

    contract = schema_result["contract"]
    if (
        not isinstance(contract, dict)
        or contract.get("name") != "npc finish analyze"
        or contract.get("readiness") != "legacy"
        or not isinstance(contract.get("resultSchemaIds"), list)
        or any(not _is_nonempty_string(identifier)
               for identifier in contract.get("resultSchemaIds", []))
        or len(contract.get("resultSchemaIds", []))
            != len(set(contract.get("resultSchemaIds", [])))
    ):
        raise ReleaseError(
            "protocol 2 schema export Finish Core contract is malformed")

    result_schemas = schema_result["resultSchemas"]
    if not isinstance(result_schemas, list):
        raise ReleaseError(
            "protocol 2 result schema projection must be an array")
    projected_result_ids = [
        _validate_schema_definition(value, "result schema")
        for value in result_schemas
    ]
    if (
        len(projected_result_ids) != len(set(projected_result_ids))
        or projected_result_ids != contract["resultSchemaIds"]
    ):
        raise ReleaseError(
            "protocol 2 result schema projection does not match the contract")

    document_schemas = schema_result["documentSchemas"]
    if not isinstance(document_schemas, list):
        raise ReleaseError(
            "protocol 2 document schema projection must be an array")
    expected_documents = {
        "request": ("input", "npc.finish-core.request.v2"),
        "proposal": ("output", "npc.finish-core.proposal.v2"),
        "validation": ("output", "npc.finish-core.validation.v1"),
    }
    if release_version in {
        "1.0.0-preview.255", "1.0.0-preview.256", "1.0.0-preview.257",
        "1.0.0-preview.258", "1.0.0-preview.260", "1.0.0-preview.261",
        "1.0.0-preview.262", "1.0.0-preview.263", "1.0.0-preview.264",
        "1.0.0-preview.265", "1.0.0-preview.266", "1.0.0-preview.267",
        "1.0.0-preview.268", "1.0.0-preview.269", "1.0.0-preview.270",
        "1.0.0-preview.271", "1.0.0-preview.272", "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
        "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
        "1.0.0-preview.280",
        "1.0.0-preview.281",
    }:
        expected_documents.update({
            "request-legacy": ("input", "npc.finish-core.request.v1"),
            "request-external": ("input", "npc.finish-core.request.v3"),
            "proposal-legacy": ("output", "npc.finish-core.proposal.v1"),
            "proposal-external": ("output", "npc.finish-core.proposal.v3"),
        })
    if release_version in {
        "1.0.0-preview.267", "1.0.0-preview.268", "1.0.0-preview.269",
        "1.0.0-preview.270", "1.0.0-preview.271", "1.0.0-preview.272",
        "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
        "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
        "1.0.0-preview.280", "1.0.0-preview.281"}:
        expected_documents.update({
            "request-policy": ("input", "npc.finish-core.request.v4"),
            "proposal-policy": ("output", "npc.finish-core.proposal.v4"),
        })
    observed_documents: dict[str, tuple[str, str]] = {}
    observed_identifiers: set[str] = set()
    for index, value in enumerate(document_schemas):
        if (
            not isinstance(value, dict)
            or set(value) != {
                "name", "direction", "schemaIdentifier", "jsonSchema"}
            or not _is_nonempty_string(value.get("name"))
            or not _is_nonempty_string(value.get("direction"))
        ):
            raise ReleaseError(
                f"protocol 2 document schema row is malformed: {index}")
        name = value["name"]
        direction = value["direction"]
        identifier = _validate_schema_definition(
            {
                "schemaIdentifier": value["schemaIdentifier"],
                "jsonSchema": value["jsonSchema"],
            },
            "document schema",
            expected_json_identifier=(
                f"urn:actorwright:schema:{value['schemaIdentifier']}"),
        )
        if name in observed_documents or identifier in observed_identifiers:
            raise ReleaseError(
                "protocol 2 document schema rows must be nonduplicate")
        observed_documents[name] = (direction, identifier)
        observed_identifiers.add(identifier)
    if observed_documents != expected_documents:
        raise ReleaseError(
            "protocol 2 document schema projection does not bind Finish Core")


def _validate_workspace_preflight_catalog_probe(probe: dict[str, Any]) -> None:
    if (
        probe.get("command") != "workspace preflight"
        or probe.get("resultSchemaId")
            != "urn:actorwright:protocol-v2:workspace-preflight-result:v1"
        or probe.get("arguments") != [
            "workspace", "preflight", "--protocol", "2", "--json",
            "--game", "skyrimse", "--workspace-root", "{workspace}",
            "--data-root", "{dataRoot}", "--output-root", "{outputRoot}",
            "--load-order", "{loadOrder}", "--intake-output", "{intakeOutput}",
            "--npc-editor-id", "ActorwrightPackagedNpc",
            "--workflow-output", "{workspaceWorkflow}",
        ]
        or probe.get("expectedEffects") != [
            "readWorkspace|completed|workspace",
            "writeNewArtifact|completed|k-local-output",
            "appendLocalOperationJournal|attempted|workspace-local-journal",
            "appendLocalOperationJournal|completed|workspace-local-journal",
        ]
        or probe.get("expectedArtifact") != {
            "kind": "reviewed-workspace-intake",
            "schemaOrMediaType": "npcmanager-reviewed-game-intake/2",
            "producerCommand": "workspace preflight",
            "state": "independentlyVerified",
        }
        or probe.get("expectedAuthority") != {
            "inputAdmission": "established",
            "sourceProviderIdentity": "established",
            "deterministicMaterialization": "established",
            "independentStaticVerification": "established",
            "offEnginePreview": "notApplicable",
            "humanVisualAcceptance": "notApplicable",
            "gameRuntimeVerification": "required",
            "promotionApproval": "notApplicable",
        }
        or probe.get("expectedNextAction") != {
            "command": "preset inspect",
            "missingPrerequisites": [
                "--input", "--input-sha256", "--inspection-output",
                "--workflow-output"],
            "requiresHumanAction": False,
        }
        or probe.get("expectedWorkflow") != {
            "phase": "analyze",
            "artifactKinds": ["reviewed-workspace-intake"],
            "nextAction": "preset inspect",
        }
    ):
        raise ReleaseError(
            "workspace-preflight-v1 catalog expectations are not exact")


def _validate_preset_inspect_catalog_probe(
    probe: dict[str, Any],
    *,
    historical_preview250: bool = False,
) -> None:
    expected_document_schema = (
        "actorwright-preset-inspection/1"
        if historical_preview250 else "actorwright-preset-inspection/2")
    expected_effects = [
        "readWorkspace|completed|workspace",
        "writeNewArtifact|completed|k-local-output",
    ]
    if not historical_preview250:
        expected_effects.append("writeNewArtifact|completed|k-local-output")
    expected_effects.extend([
        "appendLocalOperationJournal|attempted|workspace-local-journal",
        "appendLocalOperationJournal|completed|workspace-local-journal",
    ])
    if (
        probe.get("command") != "preset inspect"
        or probe.get("resultSchemaId")
            != "urn:actorwright:protocol-v2:preset-inspect-result:v1"
        or probe.get("arguments") != [
            "preset", "inspect", "--protocol", "2", "--json",
            "--format", "racemenu-jslot", "--edition", "skyrimse",
            "--input", "{presetInput}",
            "--input-sha256", "{presetSha256}",
            "--inspection-output", "{inspectionOutput}",
            "--workflow-bundle", "{workspaceWorkflow}",
            "--workflow-bundle-sha256", "{workspaceWorkflowSha256}",
            "--workflow-output", "{presetWorkflow}",
        ]
        or probe.get("expectedEffects") != expected_effects
        or probe.get("expectedArtifact") != {
            "kind": "preset-inspection",
            "schemaOrMediaType": expected_document_schema,
            "producerCommand": "preset inspect",
            "state": "independentlyVerified",
        }
        or probe.get("expectedAuthority") != {
            "inputAdmission": "established",
            "sourceProviderIdentity": "required",
            "deterministicMaterialization": "established",
            "independentStaticVerification": "established",
            "offEnginePreview": "notApplicable",
            "humanVisualAcceptance": "notApplicable",
            "gameRuntimeVerification": "required",
            "promotionApproval": "notApplicable",
        }
        or probe.get("expectedNextAction") != {
            "command": "npc create-from-jslot",
            "missingPrerequisites": [
                "--request", "--request-sha256", "--data-root", "--plugins",
                "--companion-root", "--preflight-output", "--workflow-output",
            ],
            "requiresHumanAction": False,
        }
        or probe.get("expectedWorkflow") != {
            "phase": "analyze",
            "artifactKinds": [
                "racemenu-jslot", "reviewed-workspace-intake"],
            "nextAction": "npc create-from-jslot",
        }
    ):
        raise ReleaseError(
            "preset-inspect-v1 catalog expectations are not exact")


def _validate_npc_create_preflight_catalog_probe(probe: dict[str, Any]) -> None:
    expected_destinations = {
        "npc-preflight/fixture.jslot", "Data/Skyrim.esm",
        "npc-preflight/face.nif", "npc-preflight/face.dds",
        "npc-preflight/record-authority.json",
        "npc-preflight/runtime-routes.json",
        "npc-preflight/standalone-assets.json",
        "npc-preflight/preset-bundle.json", "npc-preflight/request.json",
    }
    if (
        probe.get("command") != "npc create-from-jslot"
        or probe.get("validator") != "npc-create-preflight-v1"
        or probe.get("resultSchemaId")
            != "urn:actorwright:protocol-v2:npc-create-preflight-result:v1"
        or {row.get("destination") for row in probe.get("fixtures", [])}
            != expected_destinations
        or probe.get("arguments") != [
            "npc", "create-from-jslot", "--protocol", "2", "--json",
            "--request", "{request}",
            "--request-sha256", "{requestSha256}",
            "--preset", "{preset}",
            "--preset-sha256", "{presetSha256}",
            "--data-root", "{dataRoot}", "--plugins", "Skyrim.esm",
            "--companion-root", "{companionRoot}",
            "--preflight-output", "{preflightOutput}",
            "--workflow-bundle", "{presetWorkflow}",
            "--workflow-bundle-sha256", "{presetWorkflowSha256}",
            "--workflow-output", "{preflightWorkflow}",
        ]
        or probe.get("expectedEffects") != [
            "readWorkspace|completed|workspace",
            "writeNewArtifact|completed|k-local-output",
            "appendLocalOperationJournal|attempted|workspace-local-journal",
            "appendLocalOperationJournal|completed|workspace-local-journal",
        ]
        or probe.get("expectedArtifact") != {
            "kind": "npc-build-preflight",
            "schemaOrMediaType": "actorwright-npc-build-preflight/1",
            "producerCommand": "npc create-from-jslot",
            "state": "independentlyVerified",
        }
        or probe.get("expectedAuthority") != {
            "inputAdmission": "established",
            "sourceProviderIdentity": "established",
            "deterministicMaterialization": "established",
            "independentStaticVerification": "established",
            "offEnginePreview": "notApplicable",
            "humanVisualAcceptance": "notApplicable",
            "gameRuntimeVerification": "required",
            "promotionApproval": "notApplicable",
        }
        or probe.get("expectedNextAction") != {
            "command": "npc create-from-jslot",
            "missingPrerequisites": [
                "--request", "--request-sha256", "--data-root", "--plugins",
                "--companion-root", "--workflow-output"],
            "requiresHumanAction": False,
        }
        or probe.get("expectedWorkflow") != {
            "phase": "apply",
            "artifactKinds": [
                "npc-build-preflight", "racemenu-jslot",
                "reviewed-workspace-intake"],
            "nextAction": "npc create-from-jslot",
        }
    ):
        raise ReleaseError(
            "npc-create-preflight-v1 catalog expectations are not exact")


def _validate_finish_verify_catalog_probe(
    probe: dict[str, Any],
    *,
    release_version: str | None = None,
) -> None:
    destinations = {row.get("destination") for row in probe.get("fixtures", [])}
    expected_destinations = {
        "1.0.0-preview.260": CURRENT_PREVIEW260_FINISH_DESTINATIONS,
        "1.0.0-preview.258": CURRENT_PREVIEW258_FINISH_DESTINATIONS,
        "1.0.0-preview.257": CURRENT_PREVIEW257_FINISH_DESTINATIONS,
        "1.0.0-preview.256": CURRENT_PREVIEW256_FINISH_DESTINATIONS,
        "1.0.0-preview.250": HISTORICAL_PREVIEW250_FINISH_DESTINATIONS,
        "1.0.0-preview.255": CURRENT_PREVIEW255_FINISH_DESTINATIONS,
    }.get(release_version, CURRENT_PREVIEW260_FINISH_DESTINATIONS)
    if (
        probe.get("command") != "npc finish verify"
        or probe.get("validator") != "finish-verify-v1"
        or probe.get("resultSchemaId")
            != "urn:actorwright:protocol-v2:finish-verify-result:v1"
        or destinations != expected_destinations
        or probe.get("arguments") != [
            "npc", "finish", "verify", "--protocol", "2", "--json",
            "--manifest", "{finishManifest}",
            "--manifest-sha256", "{finishManifestSha256}",
            "--verification-output", "{finishVerificationOutput}",
            "--workflow-bundle", "{finishManifestWorkflow}",
            "--workflow-bundle-sha256", "{finishManifestWorkflowSha256}",
            "--workflow-output", "{finishRuntimeWorkflow}",
        ]
        or probe.get("expectedEffects") != [
            "readWorkspace|completed|workspace",
            "writeNewArtifact|completed|k-local-output",
            "appendLocalOperationJournal|attempted|workspace-local-journal",
            "appendLocalOperationJournal|completed|workspace-local-journal",
        ]
        or probe.get("expectedArtifact") != {
            "kind": "npc-finish-core-verification",
            "schemaOrMediaType": "npc.finish-core.verification.v1",
            "producerCommand": "npc finish verify",
            "state": "independentlyVerified",
        }
        or probe.get("expectedAuthority") != {
            "inputAdmission": "established",
            "sourceProviderIdentity": "established",
            "deterministicMaterialization": "established",
            "independentStaticVerification": "established",
            "offEnginePreview": "notApplicable",
            "humanVisualAcceptance": "required",
            "gameRuntimeVerification": "required",
            "promotionApproval": "required",
        }
        or probe.get("expectedNextAction") != {
            "command": "runtime smoke verify",
            "requiredBindings": [{"option": "--edition", "value": "skyrimse"}],
            "missingPrerequisites": ["--runtime-report", "--package-acceptance"],
            "requiresHumanAction": True,
        }
        or probe.get("expectedWorkflow") != {
            "phase": "runtimeAcceptance",
            "artifactKinds": [
                "npc-finish-core-verification", "package-archive"],
            "nextAction": "runtime smoke verify",
        }
    ):
        raise ReleaseError("finish-verify-v1 catalog expectations are not exact")


def _validate_npc_assembly_preflight_catalog_probe(
    probe: dict[str, Any],
) -> None:
    if (
        probe.get("command") != "npc assembly preflight"
        or probe.get("validator") != "npc-assembly-preflight-v1"
        or probe.get("resultSchemaId")
            != "urn:actorwright:protocol-v2:npc-assembly-preflight-result:v1"
        or probe.get("resultSchemaIds") != [
            "urn:actorwright:protocol-v2:npc-assembly-preflight-result:v1"]
        or probe.get("fixtures") != [{
            "source": "assembly-preflight/invalid-contract.json",
            "destination": "assembly-preflight/invalid-contract.json",
            "encoding": "raw",
            "size": 3,
            "sha256": "CA3D163BAB055381827226140568F3BEF7EAAC187CEBD76878E0B63E9E442356",
        }]
        or probe.get("arguments") != [
            "npc", "assembly", "preflight", "--protocol", "2", "--json",
            "--contract", "{contract}",
            "--contract-sha256", "{contractSha256}",
            "--output", "{output}",
        ]
        or probe.get("expectedEffects") != [
            "readWorkspace|completed|workspace",
            "appendLocalOperationJournal|attempted|workspace-local-journal",
            "appendLocalOperationJournal|completed|workspace-local-journal",
        ]
        or probe.get("expectedArtifact") != {}
        or probe.get("expectedAuthority") != {
            "inputAdmission": "blocked",
            "sourceProviderIdentity": "established",
            "deterministicMaterialization": "required",
            "independentStaticVerification": "required",
            "offEnginePreview": "notApplicable",
            "humanVisualAcceptance": "required",
            "gameRuntimeVerification": "required",
            "promotionApproval": "required",
        }
        or probe.get("expectedNextAction") != {}
        or probe.get("expectedWorkflow") != {}
    ):
        raise ReleaseError(
            "npc-assembly-preflight-v1 catalog expectations are not exact")


def _validate_preview_consumer_required_catalog_probe(
    probe: dict[str, Any],
) -> None:
    if (
        probe.get("command") != "preview npc"
        or probe.get("resultSchemaId")
            != "urn:actorwright:protocol-v2:npc-visual-preview-result:v1"
        or probe.get("fixtures") != []
        or probe.get("arguments") != []
        or probe.get("expectedEffects") != []
        or probe.get("expectedArtifact") != {}
        or probe.get("expectedAuthority") != {}
        or probe.get("expectedNextAction") != {}
        or probe.get("expectedWorkflow") != {}
    ):
        raise ReleaseError(
            "preview-consumer-required-v1 must remain schema-only")


def _load_protocol_v2_workflow_probe_catalog(
    workflow_commands: set[str],
    advertised_schemas: dict[str, Any],
    *,
    release_version: str,
    root: Path,
) -> list[dict[str, Any]]:
    if root in {CURRENT_STAGING_PROBE_ROOT, PUBLIC_CURRENT_STAGING_PROBE_ROOT, PREVIEW274_PROBE_ROOT, PREVIEW273_PROBE_ROOT, PREVIEW272_PROBE_ROOT}:
        return _load_current_staging_catalog(
            workflow_commands, advertised_schemas, root=root)
    catalog_path = root / "catalog.json"
    if (
        not root.is_dir()
        or _is_reparse_point(root)
        or not catalog_path.is_file()
        or _is_reparse_point(catalog_path)
    ):
        raise ReleaseError("protocol 2 workflow probe catalog is missing or unsafe")
    catalog = read_json(catalog_path)
    if (
        not isinstance(catalog, dict)
        or set(catalog) != {"schemaVersion", "probes"}
        or catalog.get("schemaVersion") != 2
        or not isinstance(catalog.get("probes"), list)
    ):
        raise ReleaseError("protocol 2 workflow probe catalog shape mismatch")

    expected_probe_fields = {
        "command", "validator", "resultSchemaId", "resultSchemaIds",
        "fixtures", "arguments",
        "expectedEffects", "expectedArtifact", "expectedAuthority",
        "expectedNextAction", "expectedWorkflow",
    }
    probes: dict[str, dict[str, Any]] = {}
    for index, probe in enumerate(catalog["probes"]):
        if (
            not isinstance(probe, dict)
            or set(probe) != expected_probe_fields
            or not _is_nonempty_string(probe.get("command"))
            or probe.get("validator") not in {
                "workspace-preflight-v1", "preset-inspect-v1",
                "npc-create-preflight-v1", "finish-verify-v1",
                "npc-assembly-preflight-v1", "preview-consumer-required-v1"}
            or not _is_nonempty_string(probe.get("resultSchemaId"))
            or not isinstance(probe.get("resultSchemaIds"), list)
            or not probe["resultSchemaIds"]
            or any(not _is_nonempty_string(value)
                   for value in probe["resultSchemaIds"])
            or len(probe["resultSchemaIds"]) != len(set(probe["resultSchemaIds"]))
            or probe["resultSchemaId"] not in probe["resultSchemaIds"]
            or not isinstance(probe.get("fixtures"), list)
            or (not probe["fixtures"] and
                probe.get("validator") != "preview-consumer-required-v1")
            or not isinstance(probe.get("arguments"), list)
            or any(not _is_nonempty_string(value)
                   for value in probe.get("arguments", []))
            or not isinstance(probe.get("expectedEffects"), list)
            or any(not _is_nonempty_string(value)
                   for value in probe.get("expectedEffects", []))
            or not isinstance(probe.get("expectedArtifact"), dict)
            or not isinstance(probe.get("expectedAuthority"), dict)
            or not isinstance(probe.get("expectedNextAction"), dict)
            or not isinstance(probe.get("expectedWorkflow"), dict)
        ):
            raise ReleaseError(
                f"protocol 2 workflow probe catalog row is malformed: {index}")
        command = probe["command"]
        if command in probes or command in PROTOCOL_V2_KERNEL_READY_COMMANDS:
            raise ReleaseError(
                f"protocol 2 workflow probe catalog has duplicate or kernel entry: {command}")
        catalog_validators = {
            "workspace-preflight-v1": _validate_workspace_preflight_catalog_probe,
            "preset-inspect-v1": _validate_preset_inspect_catalog_probe,
            "npc-create-preflight-v1": _validate_npc_create_preflight_catalog_probe,
            "finish-verify-v1": _validate_finish_verify_catalog_probe,
            "npc-assembly-preflight-v1":
                _validate_npc_assembly_preflight_catalog_probe,
            "preview-consumer-required-v1":
                _validate_preview_consumer_required_catalog_probe,
        }
        validator = catalog_validators[probe["validator"]]
        if probe["validator"] == "finish-verify-v1":
            validator(probe, release_version=release_version)
        elif probe["validator"] == "preset-inspect-v1":
            validator(
                probe,
                historical_preview250=(
                    release_version == "1.0.0-preview.250"),
            )
        else:
            validator(probe)
        probes[command] = probe

    # Preview.266 through Preview.261 keep the current probe tree for five ready
    # workflows; the Finish Verify row remains present only for Preview.260
    # rollback verification, where that command was still protocol-v2 ready.
    if release_version in {
        "1.0.0-preview.261", "1.0.0-preview.262", "1.0.0-preview.263",
        "1.0.0-preview.264", "1.0.0-preview.265", "1.0.0-preview.266",
    }:
        probes.pop("npc finish verify", None)

    if set(probes) != workflow_commands:
        raise ReleaseError(
            "protocol 2 workflow probe catalog membership mismatch: "
            f"missing={sorted(workflow_commands - set(probes))}, "
            f"stale={sorted(set(probes) - workflow_commands)}")
    for command, probe in probes.items():
        if advertised_schemas.get(command) != probe["resultSchemaIds"]:
            raise ReleaseError(
                f"protocol 2 workflow probe schema differs from capabilities: {command}")
    order = [
        "npc assembly preflight", "workspace preflight", "preset inspect",
        "npc create-from-jslot", "npc finish verify", "preview npc"]
    return [probes[name] for name in order if name in probes]


def _read_protocol_v2_fixture(
    probe: dict[str, Any],
    row: Any,
    root: Path,
) -> tuple[str, bytes]:
    if (
        not isinstance(row, dict)
        or set(row) != {"source", "destination", "encoding", "size", "sha256"}
        or row.get("encoding") not in {"raw", "base64"}
        or isinstance(row.get("size"), bool)
        or not isinstance(row.get("size"), int)
        or row["size"] < 0
        or not HASH_RE.fullmatch(str(row.get("sha256", "")))
    ):
        raise ReleaseError(
            f"protocol 2 workflow probe fixture is malformed: {probe['command']}")
    source_relative = safe_relative(str(row["source"]))
    destination_relative = safe_relative(str(row["destination"]))
    source = root / source_relative
    try:
        source.relative_to(root)
    except ValueError as exc:
        raise ReleaseError("protocol 2 workflow fixture escaped its catalog") from exc
    if not source.is_file() or _is_reparse_point(source):
        raise ReleaseError(
            f"protocol 2 workflow fixture is missing or unsafe: {source_relative}")
    encoded = source.read_bytes()
    if row["encoding"] == "raw":
        content = encoded
    else:
        try:
            text = encoded.decode("ascii")
            if text != text.strip() + "\n":
                raise ValueError("non-canonical base64 fixture")
            content = base64.b64decode(text.strip(), validate=True)
            if base64.b64encode(content).decode("ascii") + "\n" != text:
                raise ValueError("non-canonical base64 fixture")
        except (UnicodeDecodeError, ValueError, binascii.Error) as exc:
            raise ReleaseError(
                f"protocol 2 workflow fixture encoding is invalid: {source_relative}") from exc
    if len(content) != row["size"] or hashlib.sha256(content).hexdigest().upper() != row["sha256"]:
        raise ReleaseError(
            f"protocol 2 workflow fixture size or hash mismatch: {source_relative}")
    return destination_relative, content


def _prepare_protocol_v2_workflow_fixture(
    probe: dict[str, Any],
    workspace_root: Path,
    root: Path,
) -> dict[str, Any]:
    destinations: set[str] = set()
    fixture_hashes: dict[str, str] = {}
    for row in probe["fixtures"]:
        relative, content = _read_protocol_v2_fixture(probe, row, root)
        if relative in destinations:
            raise ReleaseError(
                f"protocol 2 workflow fixture destination is duplicate: {relative}")
        destinations.add(relative)
        destination = workspace_root / relative
        current = workspace_root
        for part in PurePosixPath(relative).parts[:-1]:
            current = current / part
            _ensure_ordinary_directory(
                current, "protocol 2 workflow fixture directory")
        if destination.exists():
            if (
                not destination.is_file()
                or _is_reparse_point(destination)
                or destination.read_bytes() != content
            ):
                raise ReleaseError(
                    f"protocol 2 workflow fixture collision: {relative}")
        else:
            try:
                with destination.open("xb") as stream:
                    stream.write(content)
            except OSError as exc:
                raise ReleaseError(
                    f"protocol 2 workflow fixture could not be materialized: {relative}") from exc
        if (
            not destination.is_file()
            or _is_reparse_point(destination)
            or destination.stat().st_size != len(content)
            or digest(destination) != row["sha256"]
        ):
            raise ReleaseError(
                f"protocol 2 workflow fixture readback mismatch: {relative}")
        fixture_hashes[relative] = row["sha256"]

    evidence = workspace_root / "evidence"
    _ensure_ordinary_directory(evidence, "protocol 2 workflow evidence directory")
    if probe["validator"] == "workspace-preflight-v1":
        if destinations != {"Data/Probe.esp", "load-order.json"}:
            raise ReleaseError(
                "workspace-preflight-v1 requires exact Probe.esp and load-order fixtures")
        return {
            "workspace": workspace_root,
            "dataRoot": workspace_root / "Data",
            "outputRoot": workspace_root / "reserved-output",
            "loadOrder": workspace_root / "load-order.json",
            "intakeOutput": evidence / "reviewed-intake.json",
            "workspaceWorkflow": evidence / "workflow-workspace.json",
            "pluginSha256": fixture_hashes["Data/Probe.esp"],
            "loadOrderSha256": fixture_hashes["load-order.json"],
        }
    if probe["validator"] == "npc-create-preflight-v1":
        expected = {
            "npc-preflight/fixture.jslot", "Data/Skyrim.esm",
            "npc-preflight/face.nif", "npc-preflight/face.dds",
            "npc-preflight/record-authority.json",
            "npc-preflight/runtime-routes.json",
            "npc-preflight/standalone-assets.json",
            "npc-preflight/preset-bundle.json", "npc-preflight/request.json",
        }
        if destinations != expected:
            raise ReleaseError(
                "npc-create-preflight-v1 fixture set is not exact")
        return {
            "workspace": workspace_root,
            "request": workspace_root / "npc-preflight" / "request.json",
            "requestSha256": fixture_hashes["npc-preflight/request.json"],
            "preset": workspace_root / "npc-preflight" / "fixture.jslot",
            "presetSha256": fixture_hashes["npc-preflight/fixture.jslot"],
            "dataRoot": workspace_root / "Data",
            "companionRoot": workspace_root / "planned-companion",
            "preflightOutput": evidence / "npc-build-preflight.json",
            "presetWorkflow": evidence / "workflow-preset.json",
            "preflightWorkflow": evidence / "workflow-preflight.json",
        }
    if probe["validator"] == "npc-assembly-preflight-v1":
        if destinations != {"assembly-preflight/invalid-contract.json"}:
            raise ReleaseError(
                "npc-assembly-preflight-v1 fixture set is not exact")
        contract = workspace_root / "assembly-preflight" / "invalid-contract.json"
        return {
            "workspace": workspace_root,
            "contract": contract,
            "contractSha256": fixture_hashes[
                "assembly-preflight/invalid-contract.json"],
            "output": evidence / "assembly-preflight.json",
        }
    if probe["validator"] == "finish-verify-v1":
        manifest = workspace_root / "output" / "NPCManager" / "Evidence" / "finish-core-manifest.json"
        if "output.zip" not in destinations or str(manifest.relative_to(workspace_root)).replace("\\", "/") not in destinations:
            raise ReleaseError("finish-verify-v1 fixture set is incomplete")
        paths = {
            "workspace": workspace_root,
            "finishManifest": manifest,
            "finishManifestSha256": fixture_hashes[
                "output/NPCManager/Evidence/finish-core-manifest.json"],
            "finishVerificationOutput": evidence / "finish-verification.json",
            "finishArchiveSha256": fixture_hashes["output.zip"],
            "finishManifestWorkflow": evidence / "workflow-finish-manifest.json",
            "finishRuntimeWorkflow": evidence / "workflow-finish-runtime.json",
        }
        paths["finishManifestWorkflowSha256"] = _write_finish_workflow_seed(
            paths)
        return paths
    if destinations != {"npc-preflight/fixture.jslot"}:
        raise ReleaseError(
            "preset-inspect-v1 requires exactly one JSlot fixture")
    return {
        "workspace": workspace_root,
        "presetInput": workspace_root / "npc-preflight" / "fixture.jslot",
        "presetSha256": fixture_hashes["npc-preflight/fixture.jslot"],
        "inspectionOutput": evidence / "preset-inspection.json",
        "workspaceWorkflow": evidence / "workflow-workspace.json",
        "presetWorkflow": evidence / "workflow-preset.json",
    }


def _strict_protocol_json(path: Path, label: str) -> tuple[dict[str, Any], bytes]:
    if not path.is_file() or _is_reparse_point(path):
        raise ReleaseError(f"{label} is missing or unsafe")
    content = path.read_bytes()
    content_without_crlf = content.replace(b"\r\n", b"")
    if (
        not content
        or content.startswith(b"\xef\xbb\xbf")
        or b"\r" in content_without_crlf
        or (b"\r\n" in content and b"\n" in content_without_crlf)
        or content[:1] != b"{"
        or content[-1:] != b"}"
    ):
        raise ReleaseError(f"{label} is not strict canonical UTF-8 JSON")

    def unique(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
        value: dict[str, Any] = {}
        for key, child in pairs:
            if key in value:
                raise ReleaseError(f"{label} has duplicate JSON key: {key}")
            value[key] = child
        return value

    try:
        value = json.loads(content.decode("utf-8"), object_pairs_hook=unique)
    except ReleaseError:
        raise
    except Exception as exc:
        raise ReleaseError(f"{label} is invalid JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise ReleaseError(f"{label} root must be an object")
    return value, content


def _validate_protocol_v2_workflow_schema_export(
    envelope: dict[str, Any],
    command: str,
    result_schema_ids: list[str],
    result_schema_id: str,
    preset_document_schema_identifier: str | None = None,
    *,
    release_version: str | None = None,
) -> None:
    result = envelope.get("result")
    if (
        envelope.get("outcome") != "succeeded"
        or envelope.get("diagnostics") != []
        or not isinstance(result, dict)
        or set(result) != {
            "schemaId", "protocolVersion", "scopedHelpResultSchema",
            "contract", "resultSchemas", "documentSchemas"}
        or result.get("schemaId")
            != PROTOCOL_V2_KERNEL_READY_COMMANDS["schema export"]
        or result.get("protocolVersion") != "2"
    ):
        raise ReleaseError(
            f"protocol 2 workflow schema export is malformed: {command}")
    _validate_schema_definition(
        result["scopedHelpResultSchema"],
        "workflow scoped help schema",
        expected_identifier="urn:actorwright:protocol-v2:scoped-help-result:v1",
        expected_json_identifier="urn:actorwright:protocol-v2:scoped-help-result:v1",
    )
    contract = result["contract"]
    definitions = result["resultSchemas"]
    if (
        not isinstance(contract, dict)
        or contract.get("name") != command
        or contract.get("readiness") != "v2"
        or contract.get("resultSchemaIds") != result_schema_ids
        or not isinstance(definitions, list)
        or len(definitions) != len(result_schema_ids)
    ):
        raise ReleaseError(
            f"protocol 2 workflow schema contract mismatch: {command}")
    projected_ids: list[str] = []
    for value in definitions:
        json_definition = (
            value.get("jsonSchema") if isinstance(value, dict) else None)
        if (
            not isinstance(value, dict)
            or set(value) != {"schemaIdentifier", "jsonSchema"}
            or not _is_nonempty_string(value.get("schemaIdentifier"))
            or not isinstance(json_definition, dict)
            or json_definition.get("$schema") != JSON_SCHEMA_DRAFT
            or json_definition.get("$id") != value["schemaIdentifier"]
            or (
                json_definition.get("type") != "object"
                and not (
                    command == "npc assembly preflight"
                    and isinstance(json_definition.get("oneOf"), list)
                )
            )
        ):
            raise ReleaseError(
                f"protocol 2 workflow result schema is malformed: {command}")
        projected_ids.append(value["schemaIdentifier"])
    if projected_ids != result_schema_ids:
        raise ReleaseError(
            f"protocol 2 workflow schema contract mismatch: {command}")
    definition = next(
        value for value in definitions
        if value["schemaIdentifier"] == result_schema_id)
    json_schema = definition.get("jsonSchema") if isinstance(definition, dict) else None
    if command == "preview npc":
        required = [
            "composed", "status", "runtimeAuthority", "bundlePath",
            "bundleSize", "bundleSha256", "hashManifestPath",
            "hashManifestSize", "hashManifestSha256", "contactSheetPath",
            "contactSheetSha256", "views", "diagnostics"]
        if (
            not isinstance(definition, dict)
            or set(definition) != {"schemaIdentifier", "jsonSchema"}
            or definition.get("schemaIdentifier") != result_schema_id
            or not isinstance(json_schema, dict)
            or json_schema.get("$schema") != JSON_SCHEMA_DRAFT
            or json_schema.get("$id") != result_schema_id
            or json_schema.get("type") != "object"
            or json_schema.get("additionalProperties") is not False
            or json_schema.get("required") != required
            or not isinstance(json_schema.get("properties"), dict)
            or json_schema["properties"].get("runtimeAuthority")
                != {"const": False}
            or not isinstance(json_schema.get("oneOf"), list)
            or len(json_schema["oneOf"]) != 2
            or not isinstance(json_schema.get("$defs"), dict)
            or result.get("documentSchemas") != []
        ):
            raise ReleaseError(
                "protocol 2 NPC preview result schema mismatch")
        return
    if command == "npc create-from-jslot":
        documents = result.get("documentSchemas")
        expected_result = _pinned_workflow_json_schema(
            release_version, "npc-create-preflight-result.schema.json",
            NPC_CREATE_PREFLIGHT_RESULT_SCHEMA_CANONICAL_SHA256,
            "NPC preflight result schema")
        expected_document = _pinned_workflow_json_schema(
            release_version, "npc-build-preflight-artifact.schema.json",
            NPC_BUILD_PREFLIGHT_ARTIFACT_SCHEMA_CANONICAL_SHA256,
            "NPC preflight artifact schema")
        if release_version not in BINARY_RELEASE_IDENTITIES:
            raise ReleaseError(
                "protocol 2 NPC preflight schema release identity is unknown")
        expected_document_count = (
            2 if release_version in {
                "1.0.0-preview.252", "1.0.0-preview.253",
                "1.0.0-preview.254", "1.0.0-preview.255",
                "1.0.0-preview.256", "1.0.0-preview.257",
                "1.0.0-preview.258", "1.0.0-preview.260",
                "1.0.0-preview.261", "1.0.0-preview.262",
                "1.0.0-preview.263", "1.0.0-preview.264",
                "1.0.0-preview.265", "1.0.0-preview.266",
                "1.0.0-preview.267", "1.0.0-preview.268",
                "1.0.0-preview.269", "1.0.0-preview.270",
                "1.0.0-preview.271", "1.0.0-preview.272",
                "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
                "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
                "1.0.0-preview.280",
                "1.0.0-preview.281",
            } else 1)
        if (
            not isinstance(definition, dict)
            or set(definition) != {"schemaIdentifier", "jsonSchema"}
            or definition.get("schemaIdentifier") != result_schema_id
            or json_schema != expected_result
            or not isinstance(documents, list)
            or len(documents) != expected_document_count
        ):
            raise ReleaseError("protocol 2 NPC preflight schema mismatch")
        if release_version in {
            "1.0.0-preview.252", "1.0.0-preview.253",
            "1.0.0-preview.254", "1.0.0-preview.255",
            "1.0.0-preview.256", "1.0.0-preview.257",
            "1.0.0-preview.258", "1.0.0-preview.260",
            "1.0.0-preview.261", "1.0.0-preview.262",
            "1.0.0-preview.263", "1.0.0-preview.264",
            "1.0.0-preview.265", "1.0.0-preview.266",
            "1.0.0-preview.267", "1.0.0-preview.268",
            "1.0.0-preview.269", "1.0.0-preview.270",
            "1.0.0-preview.271", "1.0.0-preview.272",
            "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
            "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
            "1.0.0-preview.280",
            "1.0.0-preview.281",
        }:
            _validate_npc_create_request_document(documents[0], release_version)
        if documents[-1] != {
            "name": "preflight",
            "direction": "output",
            "schemaIdentifier": "actorwright-npc-build-preflight/1",
            "jsonSchema": expected_document,
        }:
            raise ReleaseError("protocol 2 NPC preflight schema mismatch")
        return
    if command == "npc assembly preflight":
        documents = result.get("documentSchemas")
        expected_result = _pinned_workflow_json_schema(
            release_version, "actor-assembly-preflight-result.schema.json",
            ACTOR_ASSEMBLY_PROTOCOL_RESULT_SCHEMA_CANONICAL_SHA256,
            "Actor Assembly protocol result schema")
        expected_contract = _pinned_workflow_json_schema(
            release_version, "actor-assembly-contract.schema.json",
            ACTOR_ASSEMBLY_CONTRACT_SCHEMA_CANONICAL_SHA256,
            "Actor Assembly contract schema")
        expected_document = _pinned_workflow_json_schema(
            release_version, "actor-assembly-document-result.schema.json",
            ACTOR_ASSEMBLY_DOCUMENT_RESULT_SCHEMA_CANONICAL_SHA256,
            "Actor Assembly document result schema")
        expected_error = _pinned_workflow_json_schema(
            release_version, "actor-assembly-error.schema.json",
            ACTOR_ASSEMBLY_ERROR_SCHEMA_CANONICAL_SHA256,
            "Actor Assembly error schema")
        if (
            not isinstance(definition, dict)
            or set(definition) != {"schemaIdentifier", "jsonSchema"}
            or definition.get("schemaIdentifier") != result_schema_id
            or json_schema != expected_result
            or not isinstance(documents, list)
            or documents != [
                {
                    "name": "contract",
                    "direction": "input",
                    "schemaIdentifier":
                        "npc.actor-assembly-preflight.contract.v1",
                    "jsonSchema": expected_contract,
                },
                {
                    "name": "result",
                    "direction": "output",
                    "schemaIdentifier":
                        "npc.actor-assembly-preflight.result.v1",
                    "jsonSchema": expected_document,
                },
                {
                    "name": "error",
                    "direction": "output",
                    "schemaIdentifier":
                        "npc.actor-assembly-preflight.error.v1",
                    "jsonSchema": expected_error,
                },
            ]
        ):
            raise ReleaseError("protocol 2 Actor Assembly schema mismatch")
        return
    if command == "preset inspect":
        documents = result.get("documentSchemas")
        if preset_document_schema_identifier == "actorwright-preset-inspection/2":
            expected_current_document = _pinned_workflow_json_schema(
                release_version, "preset-inspection-current-receipt.schema.json",
                PRESET_INSPECTION_CURRENT_RECEIPT_SCHEMA_CANONICAL_SHA256,
                "current preset inspection receipt schema")
            expected_legacy_document = _pinned_workflow_json_schema(
                release_version, "preset-inspection-receipt.schema.json",
                PRESET_INSPECTION_RECEIPT_SCHEMA_CANONICAL_SHA256,
                "legacy preset inspection receipt schema")
            expected_documents = [
                {
                    "name": "inspection-current",
                    "direction": "output",
                    "schemaIdentifier": "actorwright-preset-inspection/2",
                    "jsonSchema": expected_current_document,
                },
                {
                    "name": "inspection-legacy-read",
                    "direction": "input",
                    "schemaIdentifier": "actorwright-preset-inspection/1",
                    "jsonSchema": expected_legacy_document,
                },
            ]
        elif preset_document_schema_identifier == "actorwright-preset-inspection/1":
            expected_legacy_document = _pinned_workflow_json_schema(
                release_version, "preset-inspection-receipt.schema.json",
                PRESET_INSPECTION_RECEIPT_SCHEMA_CANONICAL_SHA256,
                "legacy preset inspection receipt schema")
            expected_documents = [{
                "name": "inspection",
                "direction": "output",
                "schemaIdentifier": "actorwright-preset-inspection/1",
                "jsonSchema": expected_legacy_document,
            }]
        else:
            expected_documents = None
        if (
            not isinstance(definition, dict)
            or set(definition) != {"schemaIdentifier", "jsonSchema"}
            or definition.get("schemaIdentifier") != result_schema_id
            or not isinstance(json_schema, dict)
            or hashlib.sha256(json.dumps(
                json_schema, sort_keys=True, separators=(",", ":"),
                ensure_ascii=False).encode("utf-8")).hexdigest().upper()
                != (
                    {
                        "1.0.0-preview.255":
                            PREVIEW255_WORKFLOW_SCHEMA_CANONICAL_SHA256,
                        "1.0.0-preview.256":
                            PREVIEW256_WORKFLOW_SCHEMA_CANONICAL_SHA256,
                        "1.0.0-preview.257":
                            PREVIEW257_WORKFLOW_SCHEMA_CANONICAL_SHA256,
                        "1.0.0-preview.258":
                            PREVIEW258_WORKFLOW_SCHEMA_CANONICAL_SHA256,
                    }.get(release_version, {
                        "preset-inspect-result.schema.json":
                            PRESET_INSPECT_RESULT_SCHEMA_CANONICAL_SHA256,
                    })["preset-inspect-result.schema.json"])
            or not isinstance(documents, list)
            or expected_documents is None
            or documents != expected_documents
        ):
            raise ReleaseError(
                "protocol 2 preset inspect result schema mismatch")
        return
    if command == "npc finish verify":
        documents = result.get("documentSchemas")
        expected_documents = (
            {
                "manifest-legacy": ("input", "npc.finish-core.manifest.v1"),
                "manifest-external": ("input", "npc.finish-core.manifest.v2"),
                "verification-legacy": (
                    "output", "npc.finish-core.verification.v1"),
                "verification-external": (
                    "output", "npc.finish-core.verification.v2"),
            }
            if release_version in {
                "1.0.0-preview.255", "1.0.0-preview.256",
                "1.0.0-preview.257", "1.0.0-preview.258",
                "1.0.0-preview.260",
            }
            else {}
        )
        observed_documents: dict[str, tuple[str, str]] = {}
        observed_identifiers: set[str] = set()
        if not isinstance(documents, list):
            raise ReleaseError(
                "protocol 2 Finish Verify document schemas changed")
        for index, value in enumerate(documents):
            if (
                not isinstance(value, dict)
                or set(value) != {
                    "name", "direction", "schemaIdentifier", "jsonSchema"}
                or not _is_nonempty_string(value.get("name"))
                or not _is_nonempty_string(value.get("direction"))
            ):
                raise ReleaseError(
                    f"protocol 2 Finish Verify document schema row is "
                    f"malformed: {index}")
            name = value["name"]
            identifier = _validate_schema_definition(
                {
                    "schemaIdentifier": value["schemaIdentifier"],
                    "jsonSchema": value["jsonSchema"],
                },
                "Finish Verify document schema",
                expected_json_identifier=(
                    f"urn:actorwright:schema:{value['schemaIdentifier']}"),
            )
            if name in observed_documents or identifier in observed_identifiers:
                raise ReleaseError(
                    "protocol 2 Finish Verify document schemas must be "
                    "nonduplicate")
            observed_documents[name] = (value["direction"], identifier)
            observed_identifiers.add(identifier)
        if observed_documents != expected_documents:
            raise ReleaseError(
                "protocol 2 Finish Verify document schemas changed")
        expected = _pinned_workflow_json_schema(
            release_version, "finish-verify-result.schema.json",
            FINISH_VERIFY_RESULT_SCHEMA_CANONICAL_SHA256,
            "Finish Verify result schema")
        if (
            not isinstance(definition, dict)
            or set(definition) != {"schemaIdentifier", "jsonSchema"}
            or definition.get("schemaIdentifier") != result_schema_id
            or json_schema != expected
        ):
            raise ReleaseError("protocol 2 Finish Verify result schema mismatch")
        return
    if command == "workspace preflight":
        documents = result.get("documentSchemas")
        if release_version in {
            "1.0.0-preview.261", "1.0.0-preview.262",
            "1.0.0-preview.263", "1.0.0-preview.264",
            "1.0.0-preview.265", "1.0.0-preview.266",
            "1.0.0-preview.267", "1.0.0-preview.268",
            "1.0.0-preview.269", "1.0.0-preview.270",
            "1.0.0-preview.271", "1.0.0-preview.272",
            "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
            "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
            "1.0.0-preview.280",
            "1.0.0-preview.281",
        }:
            if (
                not isinstance(documents, list)
                or len(documents) != 1
                or not isinstance(documents[0], dict)
                or set(documents[0]) != {
                    "name", "direction", "schemaIdentifier", "jsonSchema"}
                or documents[0].get("name") != "reviewed-intake"
                or documents[0].get("direction") != "output"
            ):
                raise ReleaseError(
                    "protocol 2 workspace preflight document schema mismatch")
            _validate_schema_definition(
                {
                    "schemaIdentifier": documents[0].get("schemaIdentifier"),
                    "jsonSchema": documents[0].get("jsonSchema"),
                },
                "workspace preflight reviewed-intake document schema",
                expected_identifier="npcmanager-reviewed-game-intake/2",
                expected_json_identifier=(
                    "urn:actorwright:schema:"
                    "npcmanager-reviewed-game-intake:2"),
            )
        elif documents != []:
            raise ReleaseError(
                "protocol 2 workspace preflight document schema mismatch")
        expected_json_schema = _pinned_workflow_json_schema(
            release_version, "workspace-preflight-result.schema.json",
            WORKSPACE_PREFLIGHT_RESULT_SCHEMA_CANONICAL_SHA256,
            "workspace result schema")
        if (
            not isinstance(definition, dict)
            or set(definition) != {"schemaIdentifier", "jsonSchema"}
            or definition.get("schemaIdentifier") != result_schema_id
            or not isinstance(json_schema, dict)
            or json_schema != expected_json_schema
        ):
            raise ReleaseError(
                "protocol 2 workflow result schema mismatch: "
                "workspace preflight")
        return
    if result.get("documentSchemas") != []:
        raise ReleaseError(
            f"protocol 2 workflow schema contract mismatch: {command}")
    expected_json_schema = _pinned_workflow_json_schema(
        release_version, "workspace-preflight-result.schema.json",
        WORKSPACE_PREFLIGHT_RESULT_SCHEMA_CANONICAL_SHA256,
        "workspace result schema")
    if (
        not isinstance(definition, dict)
        or set(definition) != {"schemaIdentifier", "jsonSchema"}
        or definition.get("schemaIdentifier") != result_schema_id
        or not isinstance(json_schema, dict)
        or json_schema != expected_json_schema
    ):
        raise ReleaseError(
            f"protocol 2 workflow result schema mismatch: {command}")


def _effect_rows(effects: Any) -> list[str]:
    if not isinstance(effects, list):
        return []
    return [
        f"{effect.get('kind')}|{effect.get('status')}|{effect.get('scope')}"
        for effect in effects if isinstance(effect, dict)
    ]


def _journal_effects(effects: Any) -> list[dict[str, Any]]:
    if not isinstance(effects, list):
        return []
    return [
        {
            "kind": effect.get("kind"),
            "status": effect.get("status"),
            "scope": effect.get("scope"),
        }
        for effect in effects if isinstance(effect, dict)
    ]


def _workspace_preflight_fixture_fingerprints(
    paths: dict[str, Any],
) -> tuple[str, str]:
    asset_fingerprint = hashlib.sha256(b"").hexdigest()
    intake_hash = hashlib.sha256()
    for value in (
        "skyrimse",
        str(paths["workspace"]),
        str(paths["dataRoot"]),
        str(paths["loadOrder"]),
        str(paths["outputRoot"]),
        paths["loadOrderSha256"].lower(),
        "Probe.esp",
        "0",
        paths["pluginSha256"].lower(),
        asset_fingerprint,
    ):
        intake_hash.update(value.encode("utf-8"))
        intake_hash.update(b"\0")
    return asset_fingerprint, intake_hash.hexdigest()


def _validate_workspace_preflight_artifact(
    envelope: dict[str, Any],
    probe: dict[str, Any],
    paths: dict[str, Any],
) -> tuple[str, dict[str, Any]]:
    artifacts = envelope["artifacts"]
    if not isinstance(artifacts, list) or len(artifacts) != 2:
        raise ReleaseError(
            "workspace-preflight-v1 must emit intake and workflow artifacts")
    artifact = next((row for row in artifacts
        if isinstance(row, dict)
        and row.get("kind") == "reviewed-workspace-intake"), None)
    expected_artifact = probe["expectedArtifact"]
    if set(expected_artifact) != {
        "kind", "schemaOrMediaType", "producerCommand", "state"
    }:
        raise ReleaseError("workspace-preflight-v1 artifact expectation is malformed")
    if (
        not isinstance(artifact, dict)
        or artifact.get("path") != str(paths["intakeOutput"])
        or artifact.get("requestDigest") != envelope["requestDigest"]
        or any(artifact.get(field) != value
               for field, value in expected_artifact.items())
        or isinstance(artifact.get("size"), bool)
        or not isinstance(artifact.get("size"), int)
        or not HASH_RE.fullmatch(str(artifact.get("sha256", "")))
    ):
        raise ReleaseError("workspace-preflight-v1 artifact metadata mismatch")
    document, content = _strict_protocol_json(
        paths["intakeOutput"], "workspace-preflight-v1 reviewed intake")
    physical_sha = hashlib.sha256(content).hexdigest().upper()
    if artifact["size"] != len(content) or artifact["sha256"] != physical_sha:
        raise ReleaseError("workspace-preflight-v1 artifact physical binding mismatch")

    expected_document_fields = {
        "schemaVersion", "edition", "isAccepted", "workspaceRoot", "dataRoot",
        "loadOrderPath", "outputRoot", "loadOrderHash", "assetIndexFingerprint",
        "intakeFingerprint", "plugins", "bodySidecarCount",
        "generatedPluginCount", "generatedSidecarCount", "bodySidecars",
        "generatedPlugins", "generatedSidecars", "assetProviderCount",
        "runtimeAuthority", "diagnostics",
    }
    plugins = document.get("plugins")
    body_sidecars = document.get("bodySidecars")
    generated_plugins = document.get("generatedPlugins")
    generated_sidecars = document.get("generatedSidecars")
    plugin = plugins[0] if isinstance(plugins, list) and len(plugins) == 1 else None
    expected_asset_fingerprint, expected_intake_fingerprint = \
        _workspace_preflight_fixture_fingerprints(paths)
    if (
        set(document) != expected_document_fields
        or document.get("schemaVersion") != "2"
        or document.get("edition") != "skyrimse"
        or document.get("isAccepted") is not True
        or document.get("workspaceRoot") != str(paths["workspace"])
        or document.get("dataRoot") != str(paths["dataRoot"])
        or document.get("loadOrderPath") != str(paths["loadOrder"])
        or document.get("outputRoot") != str(paths["outputRoot"])
        or document.get("loadOrderHash") != str(paths["loadOrderSha256"]).lower()
        or document.get("runtimeAuthority") is not False
        or document.get("assetIndexFingerprint") != expected_asset_fingerprint
        or document.get("intakeFingerprint") != expected_intake_fingerprint
        or not isinstance(plugin, dict)
        or set(plugin) != {
            "plugin", "order", "active", "requested", "requiredMaster",
            "sourceHash", "masters"}
        or plugin.get("plugin") != "Probe.esp"
        or isinstance(plugin.get("order"), bool)
        or not isinstance(plugin.get("order"), int)
        or plugin.get("order") != 0
        or plugin.get("active") is not True
        or plugin.get("requested") is not True
        or plugin.get("requiredMaster") is not False
        or plugin.get("sourceHash") != paths["pluginSha256"].lower()
        or plugin.get("masters") != []
        or not isinstance(body_sidecars, list)
        or not isinstance(generated_plugins, list)
        or not isinstance(generated_sidecars, list)
        or any(
            isinstance(document.get(field), bool)
            or not isinstance(document.get(field), int)
            for field in (
                "bodySidecarCount", "generatedPluginCount",
                "generatedSidecarCount"))
        or document.get("bodySidecarCount") != len(body_sidecars)
        or document.get("generatedPluginCount") != len(generated_plugins)
        or document.get("generatedSidecarCount") != len(generated_sidecars)
        or body_sidecars != []
        or generated_plugins != []
        or generated_sidecars != []
        or isinstance(document.get("assetProviderCount"), bool)
        or not isinstance(document.get("assetProviderCount"), int)
        or document.get("assetProviderCount") != 0
        or document.get("diagnostics") != []
    ):
        raise ReleaseError("workspace-preflight-v1 reviewed intake shape mismatch")

    bindings = artifact.get("inputBindings")
    expected_bindings = sorted({
        paths["loadOrderSha256"],
        expected_asset_fingerprint.upper(),
        paths["pluginSha256"],
    })
    if (
        not isinstance(bindings, list)
        or bindings != expected_bindings
    ):
        raise ReleaseError("workspace-preflight-v1 input bindings mismatch")
    return physical_sha, document


def _validate_workspace_preflight_journal(
    envelope: dict[str, Any],
    workspace_root: Path,
    artifact_hashes: list[str],
) -> None:
    operations = workspace_root / ".actorwright" / "operations"
    if not operations.is_dir() or _is_reparse_point(operations):
        raise ReleaseError("workspace-preflight-v1 journal directory is missing or unsafe")
    matches: list[dict[str, Any]] = []
    for path in operations.iterdir():
        if path.suffix != ".json":
            continue
        record, _ = _strict_protocol_json(path, "workspace-preflight-v1 journal record")
        if (
            record.get("command") == "workspace preflight"
            and record.get("requestDigest") == envelope["requestDigest"]
        ):
            matches.append(record)
    if len(matches) != 1:
        raise ReleaseError("workspace-preflight-v1 matching journal record is not unique")
    record = matches[0]
    if (
        set(record) != {
            "command", "requestDigest", "effects", "diagnosticCodes",
            "diagnosticClasses", "artifactHashes", "durationMilliseconds",
            "outcome", "exitCode"}
        or _effect_rows(record.get("effects")) != [
            "readWorkspace|completed|workspace",
            "writeNewArtifact|completed|k-local-output",
            "appendLocalOperationJournal|attempted|workspace-local-journal",
        ]
        or record.get("diagnosticCodes") != []
        or record.get("diagnosticClasses") != []
        or record.get("artifactHashes") != artifact_hashes
        or record.get("outcome") != "succeeded"
        or record.get("exitCode") != 0
    ):
        raise ReleaseError("workspace-preflight-v1 journal record mismatch")


def _verify_workspace_preflight_probe(
    executable: Path,
    probe: dict[str, Any],
    workspace_root: Path,
    extraction_root: Path,
    catalog_root: Path,
    release_version: str,
) -> None:
    paths = _prepare_protocol_v2_workflow_fixture(
        probe, workspace_root, catalog_root)
    schema = _run_protocol_v2_probe(
        executable,
        ("schema", "export", "--protocol", "2", "--json", "--command", probe["command"]),
        "schema export",
        0,
        workspace_root=workspace_root,
        extraction_root=extraction_root,
    )
    _validate_protocol_v2_workflow_schema_export(
        schema, probe["command"], probe["resultSchemaIds"],
        probe["resultSchemaId"], release_version=release_version)

    allowed_placeholders = {f"{{{name}}}" for name in (
        "workspace", "dataRoot", "outputRoot", "loadOrder", "intakeOutput",
        "workspaceWorkflow")}
    observed_placeholders = {
        value for value in probe["arguments"] if value.startswith("{")
    }
    if (
        observed_placeholders != allowed_placeholders
        or probe["arguments"][:2] != ["workspace", "preflight"]
        or probe["arguments"][2:5] != ["--protocol", "2", "--json"]
    ):
        raise ReleaseError("workspace-preflight-v1 arguments are not closed")
    arguments = tuple(
        str(paths[value[1:-1]]) if value in allowed_placeholders else value
        for value in probe["arguments"]
    )
    envelope = _run_protocol_v2_probe(
        executable,
        arguments,
        probe["command"],
        0,
        workspace_root=workspace_root,
        extraction_root=extraction_root,
    )
    if (
        envelope.get("outcome") != "succeeded"
        or envelope.get("diagnostics") != []
        or _effect_rows(envelope.get("effects")) != probe["expectedEffects"]
    ):
        raise ReleaseError("workspace-preflight-v1 outcome or effect mismatch")
    authority = {
        row.get("kind"): row.get("state")
        for row in envelope["authority"] if isinstance(row, dict)
    }
    if len(envelope["authority"]) != 8 or authority != probe["expectedAuthority"]:
        raise ReleaseError("workspace-preflight-v1 authority mismatch")
    expected_action = probe["expectedNextAction"]
    workflow_path = paths["workspaceWorkflow"]
    if (
        set(expected_action) != {
            "command", "missingPrerequisites", "requiresHumanAction"}
        or len(envelope["nextActions"]) != 1
        or envelope["nextActions"][0].get("command")
            != expected_action["command"]
        or envelope["nextActions"][0].get("missingPrerequisites")
            != expected_action["missingPrerequisites"]
        or envelope["nextActions"][0].get("requiresHumanAction")
            is not expected_action["requiresHumanAction"]
    ):
        raise ReleaseError("workspace-preflight-v1 next action mismatch")

    artifact_sha, document = _validate_workspace_preflight_artifact(
        envelope, probe, paths)
    workflow_sha, _ = _validate_workflow_bundle_artifact(
        envelope, probe, workflow_path)
    if envelope["nextActions"][0].get("requiredBindings") != [
        {"option": "--format", "value": "racemenu-jslot"},
        {"option": "--edition", "value": "skyrimse"},
        {"option": "--workflow-bundle", "value": str(workflow_path),
         "artifactSha256": workflow_sha},
        {"option": "--workflow-bundle-sha256", "value": workflow_sha,
         "artifactSha256": workflow_sha},
    ]:
        raise ReleaseError("workspace-preflight-v1 next action binding mismatch")
    result = envelope.get("result")
    if not isinstance(result, dict) or result != document:
        raise ReleaseError("workspace-preflight-v1 result mismatch")
    _validate_workspace_preflight_journal(
        envelope, workspace_root, sorted([artifact_sha, workflow_sha]))


def _validate_preset_inspection_journal(
    envelope: dict[str, Any],
    workspace_root: Path,
    artifact_hashes: list[str],
    expected_effects: list[str],
) -> None:
    operations = workspace_root / ".actorwright" / "operations"
    if not operations.is_dir() or _is_reparse_point(operations):
        raise ReleaseError("preset-inspect-v1 journal directory is missing or unsafe")
    matches: list[dict[str, Any]] = []
    for path in operations.iterdir():
        if path.suffix != ".json":
            continue
        record, _ = _strict_protocol_json(path, "preset-inspect-v1 journal record")
        if (record.get("command") == "preset inspect"
                and record.get("requestDigest") == envelope["requestDigest"]):
            matches.append(record)
    if len(matches) != 1:
        raise ReleaseError("preset-inspect-v1 matching journal record is not unique")
    record = matches[0]
    if (
        set(record) != {
            "command", "requestDigest", "effects", "diagnosticCodes",
            "diagnosticClasses", "artifactHashes", "durationMilliseconds",
            "outcome", "exitCode"}
        or _effect_rows(record.get("effects")) != expected_effects
        or record.get("diagnosticCodes") != []
        or record.get("diagnosticClasses") != []
        or record.get("artifactHashes") != artifact_hashes
        or record.get("outcome") != "succeeded"
        or record.get("exitCode") != 0
    ):
        raise ReleaseError("preset-inspect-v1 journal record mismatch")


def _verify_npc_assembly_preflight_probe(
    executable: Path,
    probe: dict[str, Any],
    workspace_root: Path,
    extraction_root: Path,
    catalog_root: Path,
) -> None:
    paths = _prepare_protocol_v2_workflow_fixture(
        probe, workspace_root, catalog_root)
    schema = _run_protocol_v2_probe(
        executable,
        ("schema", "export", "--protocol", "2", "--json", "--command",
         probe["command"]),
        "schema export", 0, workspace_root=workspace_root,
        extraction_root=extraction_root)
    _validate_protocol_v2_workflow_schema_export(
        schema, probe["command"], probe["resultSchemaIds"],
        probe["resultSchemaId"])
    placeholders = {"{contract}", "{contractSha256}", "{output}"}
    if (
        {value for value in probe["arguments"] if value.startswith("{")}
        != placeholders
        or tuple(probe["arguments"][:6]) != (
            "npc", "assembly", "preflight", "--protocol", "2", "--json")
    ):
        raise ReleaseError(
            "npc-assembly-preflight-v1 arguments are not closed")
    arguments = tuple(
        str(paths[value[1:-1]]) if value in placeholders else value
        for value in probe["arguments"])
    envelope = _run_protocol_v2_probe(
        executable, arguments, probe["command"], 4,
        workspace_root=workspace_root, extraction_root=extraction_root)
    authority_rows = envelope.get("authority")
    authority = {
        row.get("kind"): row.get("state")
        for row in authority_rows if isinstance(row, dict)
    } if isinstance(authority_rows, list) else {}
    diagnostics = envelope.get("diagnostics")
    result = envelope.get("result")
    if (
        envelope.get("outcome") != "failed"
        or _effect_rows(envelope.get("effects")) != probe["expectedEffects"]
        or authority != probe["expectedAuthority"]
        or envelope.get("artifacts") != []
        or envelope.get("nextActions") != []
        or not isinstance(diagnostics, list)
        or len(diagnostics) != 1
        or not isinstance(diagnostics[0], dict)
        or diagnostics[0].get("code") != "npc-build-preflight-validation-failed"
        or diagnostics[0].get("severity") != "error"
        or diagnostics[0].get("class") != "validation"
        or not isinstance(result, dict)
        or set(result) != {
            "schemaVersion", "artifactKind", "contractAdmitted", "diagnostics"}
        or result.get("schemaVersion") != 1
        or result.get("artifactKind") != "actor-assembly-preflight-error"
        or result.get("contractAdmitted") is not False
        or result.get("diagnostics") != [{
            "code": "npc-build-preflight-validation-failed",
            "severity": "error",
            "message": diagnostics[0].get("message"),
        }]
        or paths["output"].exists()
    ):
        raise ReleaseError("npc-assembly-preflight-v1 envelope mismatch")


def _verify_finish_verify_probe(
    executable: Path,
    probe: dict[str, Any],
    workspace_root: Path,
    extraction_root: Path,
    catalog_root: Path,
    release_version: str,
) -> None:
    paths = _prepare_protocol_v2_workflow_fixture(
        probe, workspace_root, catalog_root)
    schema = _run_protocol_v2_probe(
        executable,
        ("schema", "export", "--protocol", "2", "--json", "--command",
         probe["command"]),
        "schema export", 0, workspace_root=workspace_root,
        extraction_root=extraction_root)
    _validate_protocol_v2_workflow_schema_export(
        schema, probe["command"], probe["resultSchemaIds"],
        probe["resultSchemaId"], release_version=release_version)
    placeholders = {
        "{finishManifest}", "{finishManifestSha256}",
        "{finishVerificationOutput}",
        "{finishManifestWorkflow}", "{finishManifestWorkflowSha256}",
        "{finishRuntimeWorkflow}",
    }
    if {value for value in probe["arguments"] if value.startswith("{")} \
            != placeholders:
        raise ReleaseError("finish-verify-v1 arguments are not closed")
    arguments = tuple(
        str(paths[value[1:-1]]) if value in placeholders else value
        for value in probe["arguments"])
    envelope = _run_protocol_v2_probe(
        executable, arguments, probe["command"], 0,
        workspace_root=workspace_root, extraction_root=extraction_root)
    authority_rows = envelope.get("authority")
    authority = {
        row.get("kind"): row.get("state")
        for row in authority_rows if isinstance(row, dict)
    } if isinstance(authority_rows, list) else {}
    artifacts = envelope.get("artifacts")
    if (
        envelope.get("outcome") != "succeeded"
        or envelope.get("diagnostics") != []
        or _effect_rows(envelope.get("effects")) != probe["expectedEffects"]
        or authority != probe["expectedAuthority"]
        or not isinstance(artifacts, list) or len(artifacts) != 2
    ):
        raise ReleaseError("finish-verify-v1 envelope mismatch")
    artifact = next((row for row in artifacts
        if isinstance(row, dict)
        and row.get("kind") == "npc-finish-core-verification"), None)
    expected_artifact = probe["expectedArtifact"]
    output = paths["finishVerificationOutput"]
    document, content = _strict_protocol_json(
        output, "finish-verify-v1 external verification")
    output_sha = hashlib.sha256(content).hexdigest().upper()
    expected_bindings = sorted([
        paths["finishManifestSha256"], paths["finishArchiveSha256"]])
    if (
        not isinstance(artifact, dict)
        or {key: artifact.get(key) for key in expected_artifact}
            != expected_artifact
        or artifact.get("path") != str(output)
        or artifact.get("size") != len(content)
        or artifact.get("sha256") != output_sha
        or artifact.get("requestDigest") != envelope["requestDigest"]
        or artifact.get("inputBindings") != expected_bindings
    ):
        raise ReleaseError("finish-verify-v1 artifact binding mismatch")
    result = envelope.get("result")
    if (
        not isinstance(result, dict)
        or set(result) != {
            "schemaVersion", "verified", "status", "manifestPath",
            "manifestSha256", "verificationPath", "verificationSize",
            "verificationSha256", "verification", "diagnostics"}
        or result.get("schemaVersion") != "1"
        or result.get("verified") is not True
        or result.get("status") != "staticPassRuntimeRequired"
        or result.get("manifestPath") != str(paths["finishManifest"])
        or result.get("manifestSha256") != paths["finishManifestSha256"]
        or result.get("verificationPath") != str(output)
        or result.get("verificationSize") != len(content)
        or result.get("verificationSha256") != output_sha
        or result.get("diagnostics") != []
        or not isinstance(result.get("verification"), dict)
        or result["verification"].get("verified") is not True
        or result["verification"].get("runtimeAuthority") is not False
        or result["verification"].get("visualAuthority") is not False
    ):
        raise ReleaseError("finish-verify-v1 result mismatch")
    nested = result["verification"]
    if (
        set(document) != {
            "archiveSha256", "packageTreeSha256", "placementIncluded",
            "pluginSha256", "rawForbiddenCounts", "runtimeAuthority",
            "runtimeIdentity", "schema", "sourcePackageTreeSha256", "status",
            "typedForbiddenCounts", "verified", "visualAuthority"}
        or document.get("schema") != "npc.finish-core.verification.v1"
        or document.get("status") != "StaticPassRuntimeRequired"
        or document.get("verified") is not True
        or document.get("runtimeAuthority") is not False
        or document.get("visualAuthority") is not False
        or document.get("placementIncluded") is not False
        or document.get("pluginSha256")
            != nested.get("pluginSha256", {}).get("value")
        or document.get("packageTreeSha256")
            != nested.get("packageTreeSha256", {}).get("value")
        or document.get("sourcePackageTreeSha256")
            != nested.get("sourcePackageTreeSha256", {}).get("value")
        or document.get("archiveSha256")
            != nested.get("archiveSha256", {}).get("value")
        or document.get("typedForbiddenCounts")
            != nested.get("typedForbiddenCounts")
        or document.get("rawForbiddenCounts")
            != nested.get("rawForbiddenCounts")
    ):
        raise ReleaseError("finish-verify-v1 persisted verification drift")
    actions = envelope.get("nextActions")
    if not isinstance(actions, list) or len(actions) != 1:
        raise ReleaseError("finish-verify-v1 next action mismatch")
    action = actions[0]
    expected_action = probe["expectedNextAction"]
    if (
        not isinstance(action, dict)
        or action.get("command") != expected_action["command"]
        or action.get("requiredBindings") != expected_action["requiredBindings"]
        or action.get("missingPrerequisites")
            != expected_action["missingPrerequisites"]
        or action.get("requiresHumanAction")
            is not expected_action["requiresHumanAction"]
    ):
        raise ReleaseError("finish-verify-v1 next action mismatch")
    workflow_sha, _ = _validate_workflow_bundle_artifact(
        envelope, probe, paths["finishRuntimeWorkflow"])
    _validate_finish_verify_journal(
        envelope, workspace_root, sorted([output_sha, workflow_sha]))


def _validate_finish_verify_journal(
    envelope: dict[str, Any],
    workspace_root: Path,
    artifact_hashes: list[str],
) -> None:
    operations = workspace_root / ".actorwright" / "operations"
    if not operations.is_dir() or _is_reparse_point(operations):
        raise ReleaseError("finish-verify-v1 journal directory is unsafe")
    matches: list[dict[str, Any]] = []
    for path in operations.iterdir():
        if path.suffix != ".json":
            continue
        record, _ = _strict_protocol_json(path, "finish-verify-v1 journal record")
        if (record.get("command") == "npc finish verify"
                and record.get("requestDigest") == envelope["requestDigest"]):
            matches.append(record)
    if (
        len(matches) != 1
        or set(matches[0]) != {
            "command", "requestDigest", "effects", "diagnosticCodes",
            "diagnosticClasses", "artifactHashes", "durationMilliseconds",
            "outcome", "exitCode"}
        or matches[0].get("effects") != _journal_effects(envelope["effects"][:3])
        or matches[0].get("diagnosticCodes") != []
        or matches[0].get("diagnosticClasses") != []
        or matches[0].get("artifactHashes") != artifact_hashes
        or matches[0].get("outcome") != "succeeded"
        or matches[0].get("exitCode") != 0
    ):
        raise ReleaseError("finish-verify-v1 journal record mismatch")


def _validate_npc_preflight_journal(
    envelope: dict[str, Any],
    workspace_root: Path,
    artifact_hashes: list[str],
) -> None:
    operations = workspace_root / ".actorwright" / "operations"
    if not operations.is_dir() or _is_reparse_point(operations):
        raise ReleaseError("npc-create-preflight-v1 journal directory is unsafe")
    matches: list[dict[str, Any]] = []
    for path in operations.iterdir():
        if path.suffix != ".json":
            continue
        record, _ = _strict_protocol_json(
            path, "npc-create-preflight-v1 journal record")
        if (record.get("command") == "npc create-from-jslot"
                and record.get("requestDigest") == envelope["requestDigest"]):
            matches.append(record)
    diagnostics = envelope.get("diagnostics")
    if not isinstance(diagnostics, list) or any(
            not isinstance(row, dict) for row in diagnostics):
        raise ReleaseError("npc-create-preflight-v1 diagnostics are malformed")
    expected_codes = sorted({row.get("code") for row in diagnostics})
    expected_classes = sorted({row.get("class") for row in diagnostics})
    if (
        len(matches) != 1
        or set(matches[0]) != {
            "command", "requestDigest", "effects", "diagnosticCodes",
            "diagnosticClasses", "artifactHashes", "durationMilliseconds",
            "outcome", "exitCode"}
        or matches[0].get("effects") != _journal_effects(envelope["effects"][:3])
        or matches[0].get("diagnosticCodes") != expected_codes
        or matches[0].get("diagnosticClasses") != expected_classes
        or matches[0].get("artifactHashes") != artifact_hashes
        or matches[0].get("outcome") != "succeeded"
        or matches[0].get("exitCode") != 0
    ):
        raise ReleaseError("npc-create-preflight-v1 journal record mismatch")


def _require_preflight_rows(document: dict[str, Any]) -> list[str]:
    def embedded_hash(value: Any) -> bool:
        return isinstance(value, str) and re.fullmatch(
            r"[0-9A-Fa-f]{64}", value) is not None

    expected_fields = {
        "schema", "product", "productVersion", "sourceLine",
        "executableSha256", "derivationVersion", "sourceRequest",
        "sourceRequestSha256", "presetSha256", "race", "sex",
        "winningPluginOrder", "authorities", "headParts", "appearance",
        "finalDependencyClosure", "requiredGates", "optionalPreview",
        "plannedOutputs", "readyForBuild", "previewReady",
        "runtimeAuthority",
    }
    if (
        set(document) != expected_fields
        or document.get("schema") != "actorwright-npc-build-preflight/1"
        or document.get("derivationVersion") != 1
        or document.get("race") != "Skyrim.esm|0x00013746"
        or document.get("sex") != "Female"
        or document.get("winningPluginOrder") != ["Skyrim.esm"]
        or document.get("readyForBuild") is not True
        or not isinstance(document.get("previewReady"), bool)
        or document.get("runtimeAuthority") is not False
        or not all(_is_nonempty_string(document.get(name)) for name in (
            "product", "productVersion", "sourceLine", "sourceRequest"))
        or (document.get("executableSha256") is not None
            and not embedded_hash(document.get("executableSha256")))
        or not embedded_hash(document.get("sourceRequestSha256"))
        or not embedded_hash(document.get("presetSha256"))
    ):
        raise ReleaseError("npc-create-preflight-v1 artifact root mismatch")

    hashes = [
        document["sourceRequestSha256"].upper(),
        document["presetSha256"].upper(),
    ]
    for collection in ("authorities", "finalDependencyClosure"):
        rows = document.get(collection)
        if not isinstance(rows, list) or not rows:
            raise ReleaseError(
                f"npc-create-preflight-v1 {collection} is empty or malformed")
        for row in rows:
            if (
                not isinstance(row, dict)
                or set(row) != {"role", "origin", "identity", "sha256"}
                or not all(_is_nonempty_string(row.get(name))
                           for name in ("role", "origin", "identity"))
                or not embedded_hash(row.get("sha256"))
            ):
                raise ReleaseError(
                    f"npc-create-preflight-v1 {collection} row mismatch")
            hashes.append(row["sha256"].upper())
    for collection, required in (("requiredGates", True),
                                 ("optionalPreview", False)):
        rows = document.get(collection)
        if not isinstance(rows, list) or not rows:
            raise ReleaseError(f"npc-create-preflight-v1 {collection} missing")
        for row in rows:
            if (
                not isinstance(row, dict)
                or set(row) != {"id", "required", "passed", "detail"}
                or not _is_nonempty_string(row.get("id"))
                or row.get("required") is not required
                or not isinstance(row.get("passed"), bool)
                or not _is_nonempty_string(row.get("detail"))
            ):
                raise ReleaseError(
                    f"npc-create-preflight-v1 {collection} row mismatch")
    if not all(row["passed"] for row in document["requiredGates"]):
        raise ReleaseError("npc-create-preflight-v1 required gate did not pass")
    for collection in ("headParts", "appearance", "plannedOutputs"):
        if not isinstance(document.get(collection), list):
            raise ReleaseError(f"npc-create-preflight-v1 {collection} mismatch")
    return sorted(set(hashes))


def _workflow_authority_rows(
    artifact_rows: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    artifacts = {row["kind"]: row for row in artifact_rows}

    def evidence(
        kind: str,
        state: str,
        reason: str,
        *artifact_kinds: str,
    ) -> dict[str, Any]:
        return {
            "kind": kind,
            "state": state,
            "reason": reason,
            "artifactHashes": sorted(
                artifacts[name]["sha256"]
                for name in artifact_kinds if name in artifacts),
        }

    def state(kind: str) -> str:
        return "established" if kind in artifacts else "required"

    deterministic = (
        "established" if (
            "npc-package-manifest" in artifacts
            or "npc-finish-core-manifest" in artifacts
        ) else "required")
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
            "deterministicMaterialization", deterministic,
            "Only hash-bound package or Finish Core manifests establish deterministic materialization.",
            "npc-package-manifest", "npc-finish-core-manifest"),
        evidence(
            "independentStaticVerification",
            "established" if (
                "npc-package-manifest" in artifacts
                or "npc-finish-core-verification" in artifacts) else "required",
            "A retained-read package or Finish verification artifact establishes independent static verification.",
            "npc-package-manifest", "npc-finish-core-verification"),
        evidence(
            "offEnginePreview", state("npc-preview-manifest"),
            "Preview production is off-engine and is not human visual acceptance.",
            "npc-preview-manifest"),
        evidence(
            "humanVisualAcceptance", "required",
            "Operator-attested review does not establish human visual acceptance."),
        evidence(
            "gameRuntimeVerification", "required",
            "Generic runtime bytes do not establish structurally verified game runtime evidence.",
            "runtime-evidence"),
        evidence(
            "promotionApproval", "required",
            "Workflow artifacts and review receipts never grant promotion approval."),
    ]


def _write_finish_workflow_seed(paths: dict[str, Any]) -> str:
    request_digest = "B" * 64
    manifest = paths["finishManifest"]
    binding = {
        "kind": "npc-finish-core-manifest",
        "schemaOrMediaType": "npc.finish-core.manifest.v1",
        "path": str(manifest),
        "size": manifest.stat().st_size,
        "sha256": paths["finishManifestSha256"],
        "producerCommand": "npc finish apply",
        "requestDigest": request_digest,
        "inputArtifactHashes": [],
        "semanticSha256": None,
    }
    document = {
        "schema": "actorwright.agent-workflow-bundle.v1",
        "workflowKind": "skyrim-jslot-follower-v1",
        "game": "skyrimSpecialEdition",
        "npc": {
            "editorId": "FinishProbeNpc",
            "displayName": "Finish Probe NPC",
            "plugin": "BrigitteBardotNpcManager.esp",
            "localFormId": "00000800",
        },
        "phase": "verify",
        "requestDigest": request_digest,
        "artifacts": [binding],
        "authority": _workflow_authority_rows([binding]),
        "nextActions": _workflow_action([binding]),
    }
    content = json.dumps(
        document, ensure_ascii=False, indent=2,
        separators=(",", ": ")).encode("utf-8")
    output = paths["finishManifestWorkflow"]
    try:
        with output.open("xb") as stream:
            stream.write(content)
    except OSError as exc:
        raise ReleaseError(
            "finish-verify-v1 workflow seed could not be materialized") from exc
    return hashlib.sha256(content).hexdigest().upper()


def _workflow_action(
    artifact_rows: list[dict[str, Any]],
) -> list[dict[str, Any]]:
    artifacts = {row["kind"]: row for row in artifact_rows}
    signature = tuple(sorted(artifacts))
    if signature == ("reviewed-workspace-intake",):
        return [{
            "command": "preset inspect",
            "reason": "Inspect an explicitly supplied RaceMenu JSlot.",
            "requiredBindings": [
                {"option": "--format", "value": "racemenu-jslot",
                 "artifactSha256": None},
                {"option": "--edition", "value": "skyrimse",
                 "artifactSha256": None},
            ],
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
                 "artifactSha256": preset["sha256"]},
            ],
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
                 "artifactSha256": preflight["sha256"]},
            ],
            "missingPrerequisites": [
                "--request", "--request-sha256", "--data-root", "--plugins",
                "--companion-root", "--workflow-bundle",
                "--workflow-bundle-sha256", "--workflow-output"],
            "requiresHumanAction": False,
        }]
    if signature == ("npc-finish-core-manifest",):
        manifest = artifacts["npc-finish-core-manifest"]
        return [{
            "command": "npc finish verify",
            "reason": "Independently verify the exact Finish Core manifest.",
            "requiredBindings": [
                {"option": "--manifest", "value": manifest["path"],
                 "artifactSha256": manifest["sha256"]},
                {"option": "--manifest-sha256", "value": manifest["sha256"],
                 "artifactSha256": manifest["sha256"]},
            ],
            "missingPrerequisites": [
                "--verification-output", "--workflow-bundle",
                "--workflow-bundle-sha256", "--workflow-output"],
            "requiresHumanAction": False,
        }]
    if signature == ("npc-finish-core-verification", "package-archive"):
        return [{
            "command": "runtime smoke verify",
            "reason": "Runtime authority requires a typed runtime report and package acceptance.",
            "requiredBindings": [
                {"option": "--edition", "value": "skyrimse",
                 "artifactSha256": None}],
            "missingPrerequisites": [
                "--runtime-report", "--package-acceptance"],
            "requiresHumanAction": True,
        }]
    raise ReleaseError("protocol 2 workflow bundle signature is not admitted")


def _validate_workflow_bundle_artifact(
    envelope: dict[str, Any],
    probe: dict[str, Any],
    output: Path,
) -> tuple[str, dict[str, Any]]:
    artifacts = envelope.get("artifacts")
    rows = [row for row in artifacts if isinstance(row, dict)] \
        if isinstance(artifacts, list) else []
    workflows = [row for row in rows if row.get("kind") == "workflow-bundle"]
    if len(workflows) != 1:
        raise ReleaseError(
            f"{probe['validator']} must emit exactly one workflow bundle")
    artifact = workflows[0]
    document, content = _strict_protocol_json(
        output, f"{probe['validator']} workflow bundle")
    physical_sha = hashlib.sha256(content).hexdigest().upper()
    expected = probe["expectedWorkflow"]
    if (
        artifact.get("schemaOrMediaType")
            != "actorwright.agent-workflow-bundle.v1"
        or artifact.get("producerCommand") != probe["command"]
        or artifact.get("state") != "independentlyVerified"
        or artifact.get("path") != str(output)
        or artifact.get("size") != len(content)
        or artifact.get("sha256") != physical_sha
        or artifact.get("requestDigest") != envelope.get("requestDigest")
        or set(document) != {
            "schema", "workflowKind", "game", "npc", "phase",
            "requestDigest", "artifacts", "authority", "nextActions"}
        or document.get("schema") != "actorwright.agent-workflow-bundle.v1"
        or document.get("workflowKind") != "skyrim-jslot-follower-v1"
        or document.get("game") != "skyrimSpecialEdition"
        or document.get("phase") != expected["phase"]
        or document.get("requestDigest") != envelope.get("requestDigest")
        or not isinstance(document.get("npc"), dict)
        or set(document["npc"]) != {
            "editorId", "displayName", "plugin", "localFormId"}
        or not _is_nonempty_string(document["npc"].get("editorId"))
        or any(
            value is not None and not _is_nonempty_string(value)
            for value in (
                document["npc"].get("displayName"),
                document["npc"].get("plugin"),
                document["npc"].get("localFormId")))
        or not isinstance(document.get("artifacts"), list)
    ):
        raise ReleaseError(f"{probe['validator']} workflow bundle mismatch")
    bindings = document["artifacts"]
    if any(not isinstance(row, dict) for row in bindings):
        raise ReleaseError(
            f"{probe['validator']} workflow artifact binding mismatch")
    for binding in bindings:
        if (
            set(binding) != {
                "kind", "schemaOrMediaType", "path", "size", "sha256",
                "producerCommand", "requestDigest", "inputArtifactHashes",
                "semanticSha256"}
            or not _is_nonempty_string(binding.get("kind"))
            or not _is_nonempty_string(binding.get("schemaOrMediaType"))
            or not _is_nonempty_string(binding.get("path"))
            or not _is_nonempty_string(binding.get("producerCommand"))
            or not HASH_RE.fullmatch(str(binding.get("requestDigest", "")))
            or not HASH_RE.fullmatch(str(binding.get("sha256", "")))
            or isinstance(binding.get("size"), bool)
            or not isinstance(binding.get("size"), int)
            or binding["size"] < 0
            or not isinstance(binding.get("inputArtifactHashes"), list)
            or any(not HASH_RE.fullmatch(str(value))
                   for value in binding["inputArtifactHashes"])
            or binding["inputArtifactHashes"]
                != sorted(set(binding["inputArtifactHashes"]))
            or (binding.get("semanticSha256") is not None
                and not HASH_RE.fullmatch(str(binding["semanticSha256"])))
        ):
            raise ReleaseError(
                f"{probe['validator']} workflow artifact binding mismatch")
        path = Path(binding["path"])
        if (
            not path.is_file() or _is_reparse_point(path)
            or path.stat().st_size != binding["size"]
            or digest(path) != binding["sha256"]
        ):
            raise ReleaseError(
                f"{probe['validator']} workflow artifact drift")
    if (
        sorted(row["kind"] for row in bindings)
            != expected["artifactKinds"]
        or document.get("authority") != _workflow_authority_rows(bindings)
        or document.get("nextActions") != _workflow_action(bindings)
        or artifact.get("inputBindings")
            != sorted(row["sha256"] for row in bindings)
    ):
        raise ReleaseError(
            f"{probe['validator']} workflow derived state mismatch")
    expected_next = expected["nextAction"]
    if expected_next is None:
        if document["nextActions"] != []:
            raise ReleaseError(
                f"{probe['validator']} workflow next action mismatch")
    elif (
        len(document["nextActions"]) != 1
        or document["nextActions"][0].get("command") != expected_next
    ):
        raise ReleaseError(
            f"{probe['validator']} workflow next action mismatch")
    return physical_sha, document


def _verify_npc_create_preflight_probe(
    executable: Path,
    probe: dict[str, Any],
    workspace_root: Path,
    extraction_root: Path,
    catalog_root: Path,
    release_version: str,
) -> None:
    paths = _prepare_protocol_v2_workflow_fixture(
        probe, workspace_root, catalog_root)
    schema = _run_protocol_v2_probe(
        executable,
        ("schema", "export", "--protocol", "2", "--json", "--command",
         probe["command"]),
        "schema export", 0, workspace_root=workspace_root,
        extraction_root=extraction_root)
    _validate_protocol_v2_workflow_schema_export(
        schema, probe["command"], probe["resultSchemaIds"],
        probe["resultSchemaId"], release_version=release_version)
    placeholders = {
        "{request}", "{requestSha256}", "{preset}", "{presetSha256}",
        "{dataRoot}", "{companionRoot}", "{preflightOutput}",
        "{presetWorkflow}", "{presetWorkflowSha256}",
        "{preflightWorkflow}",
    }
    paths["presetWorkflowSha256"] = digest(paths["presetWorkflow"])
    if {value for value in probe["arguments"] if value.startswith("{")} \
            != placeholders:
        raise ReleaseError("npc-create-preflight-v1 arguments are not closed")
    arguments = tuple(
        str(paths[value[1:-1]]) if value in placeholders else value
        for value in probe["arguments"])
    envelope = _run_protocol_v2_probe(
        executable, arguments, probe["command"], 0,
        workspace_root=workspace_root, extraction_root=extraction_root)
    authority_rows = envelope.get("authority")
    authority = {
        row.get("kind"): row.get("state")
        for row in authority_rows if isinstance(row, dict)
    } if isinstance(authority_rows, list) else {}
    if (
        envelope.get("outcome") != "succeeded"
        or envelope.get("exitCode") != 0
        or _effect_rows(envelope.get("effects")) != probe["expectedEffects"]
        or len(authority_rows or []) != 8
        or authority != probe["expectedAuthority"]
    ):
        raise ReleaseError("npc-create-preflight-v1 envelope mismatch")
    diagnostics = envelope.get("diagnostics")
    if (
        not isinstance(diagnostics, list)
        or any(not isinstance(row, dict) for row in diagnostics)
        or len({row.get("code") for row in diagnostics}) != len(diagnostics)
        or any(row.get("severity") == "error" for row in diagnostics)
        or any(
            set(row) != {
                "code", "severity", "message", "class", "recovery"}
            or not _is_nonempty_string(row.get("code"))
            or row.get("severity") not in {"info", "warning"}
            or not _is_nonempty_string(row.get("message"))
            or row.get("class") != "validation"
            for row in diagnostics
        )
    ):
        raise ReleaseError("npc-create-preflight-v1 diagnostics mismatch")

    artifacts = envelope.get("artifacts")
    artifact = next((row for row in artifacts
        if isinstance(row, dict) and row.get("kind") == "npc-build-preflight"), None) \
        if isinstance(artifacts, list) and len(artifacts) == 2 else None
    expected = probe["expectedArtifact"]
    output = paths["preflightOutput"]
    document, content = _strict_protocol_json(
        output, "npc-create-preflight-v1 artifact")
    artifact_sha = hashlib.sha256(content).hexdigest().upper()
    expected_bindings = _require_preflight_rows(document)
    if (
        not isinstance(artifact, dict)
        or artifact.get("kind") != expected["kind"]
        or artifact.get("schemaOrMediaType") != expected["schemaOrMediaType"]
        or artifact.get("producerCommand") != expected["producerCommand"]
        or artifact.get("state") != expected["state"]
        or artifact.get("path") != str(output)
        or artifact.get("size") != len(content)
        or artifact.get("sha256") != artifact_sha
        or artifact.get("requestDigest") != envelope.get("requestDigest")
        or artifact.get("inputBindings") != expected_bindings
        or document.get("sourceRequest") != str(paths["request"])
        or str(document.get("sourceRequestSha256", "")).upper()
            != paths["requestSha256"]
        or str(document.get("presetSha256", "")).upper()
            != paths["presetSha256"]
    ):
        raise ReleaseError("npc-create-preflight-v1 artifact binding mismatch")
    result = envelope.get("result")
    result_diagnostics = result.get("diagnostics") \
        if isinstance(result, dict) else None
    if (
        not isinstance(result, dict)
        or set(result) != {
            "created", "readyForBuild", "previewReady", "path", "sha256",
            "requiredGates", "optionalPreview", "diagnostics"}
        or result.get("created") is not True
        or result.get("readyForBuild") is not True
        or result.get("previewReady") != document["previewReady"]
        or result.get("path") != str(output)
        or result.get("sha256") != artifact_sha
        or result.get("requiredGates") != document["requiredGates"]
        or result.get("optionalPreview") != document["optionalPreview"]
        or not isinstance(result_diagnostics, list)
        or any(
            not isinstance(row, dict)
            or set(row) != {"code", "severity", "message"}
            or not _is_nonempty_string(row.get("code"))
            or isinstance(row.get("severity"), bool)
            or row.get("severity") not in {0, 1}
            or not _is_nonempty_string(row.get("message"))
            for row in result_diagnostics
        )
    ):
        raise ReleaseError("npc-create-preflight-v1 result mismatch")
    action_rows = envelope.get("nextActions")
    action = action_rows[0] \
        if isinstance(action_rows, list) and len(action_rows) == 1 else None
    expected_action = probe["expectedNextAction"]
    preflight_workflow_sha = digest(paths["preflightWorkflow"])
    if (
        not isinstance(action, dict)
        or action.get("command") != expected_action["command"]
        or action.get("missingPrerequisites")
            != expected_action["missingPrerequisites"]
        or action.get("requiresHumanAction")
            is not expected_action["requiresHumanAction"]
        or action.get("requiredBindings") != [
            {"option": "--preset", "value": str(paths["preset"]),
             "artifactSha256": paths["presetSha256"]},
            {"option": "--preset-sha256", "value": paths["presetSha256"],
             "artifactSha256": paths["presetSha256"]},
            {"option": "--reviewed-preflight", "value": str(output),
             "artifactSha256": artifact_sha},
            {"option": "--reviewed-preflight-sha256", "value": artifact_sha,
             "artifactSha256": artifact_sha},
            {"option": "--workflow-bundle",
             "value": str(paths["preflightWorkflow"]),
             "artifactSha256": preflight_workflow_sha},
            {"option": "--workflow-bundle-sha256",
             "value": preflight_workflow_sha,
             "artifactSha256": preflight_workflow_sha},
        ]
    ):
        raise ReleaseError("npc-create-preflight-v1 next action mismatch")
    workflow_sha, _ = _validate_workflow_bundle_artifact(
        envelope, probe, paths["preflightWorkflow"])
    _validate_npc_preflight_journal(
        envelope, workspace_root, sorted([artifact_sha, workflow_sha]))


def _verify_preset_inspect_probe(
    executable: Path,
    probe: dict[str, Any],
    workspace_root: Path,
    extraction_root: Path,
    catalog_root: Path,
) -> None:
    paths = _prepare_protocol_v2_workflow_fixture(
        probe, workspace_root, catalog_root)
    schema = _run_protocol_v2_probe(
        executable,
        ("schema", "export", "--protocol", "2", "--json", "--command", probe["command"]),
        "schema export",
        0,
        workspace_root=workspace_root,
        extraction_root=extraction_root,
    )
    _validate_protocol_v2_workflow_schema_export(
        schema, probe["command"], probe["resultSchemaIds"],
        probe["resultSchemaId"],
        (probe.get("expectedArtifact") or {}).get("schemaOrMediaType"))

    placeholders = {"{presetInput}", "{presetSha256}", "{inspectionOutput}"}
    paths["workspaceWorkflowSha256"] = digest(paths["workspaceWorkflow"])
    placeholders.update({
        "{workspaceWorkflow}", "{workspaceWorkflowSha256}",
        "{presetWorkflow}"})
    if {value for value in probe["arguments"] if value.startswith("{")} != placeholders:
        raise ReleaseError("preset-inspect-v1 arguments are not closed")
    arguments = tuple(
        str(paths[value[1:-1]]) if value in placeholders else value
        for value in probe["arguments"])
    envelope = _run_protocol_v2_probe(
        executable,
        arguments,
        probe["command"],
        0,
        workspace_root=workspace_root,
        extraction_root=extraction_root,
    )
    if (
        envelope.get("outcome") != "succeeded"
        or envelope.get("diagnostics") != []
        or _effect_rows(envelope.get("effects")) != probe["expectedEffects"]
    ):
        raise ReleaseError("preset-inspect-v1 outcome or effect mismatch")
    authority_rows = envelope.get("authority")
    authority = {
        row.get("kind"): row.get("state")
        for row in authority_rows if isinstance(row, dict)
    } if isinstance(authority_rows, list) else {}
    if len(authority_rows or []) != 8 or authority != probe["expectedAuthority"]:
        raise ReleaseError("preset-inspect-v1 authority mismatch")

    source = paths["presetInput"]
    source_bytes = source.read_bytes()
    source_sha = hashlib.sha256(source_bytes).hexdigest().upper()
    output = paths["inspectionOutput"]
    receipt, receipt_bytes = _strict_protocol_json(
        output, "preset-inspect-v1 inspection receipt")
    receipt_sha = hashlib.sha256(receipt_bytes).hexdigest().upper()
    expected_artifact = probe["expectedArtifact"]
    receipt_schema_identifier = (
        expected_artifact.get("schemaOrMediaType")
        if isinstance(expected_artifact, dict) else None)
    expected_receipt_fields = {
        "schema", "sourcePath", "sourceSha256", "format", "edition",
        "isValid", "appearance", "diagnostics"}
    appearance = receipt.get("appearance")
    if (
        set(receipt) != expected_receipt_fields
        or receipt.get("schema") != receipt_schema_identifier
        or receipt.get("sourcePath") != str(source)
        or receipt.get("sourceSha256") != source_sha
        or receipt.get("format") != "racemenu-jslot"
        or receipt.get("edition") != "skyrimse"
        or receipt.get("isValid") is not True
        or not isinstance(appearance, dict)
        or set(appearance) != {
            "gender", "headParts", "hairColor", "weight", "morphs",
            "bodyMorphs", "customMorphs", "sliderMorphs", "tints",
            "overlays", "skin", "presence", "unknownFields",
            "fallout4BodyMorphs", "chargenFaceMorphs", "faceBoneRegions",
            "facialMorphIntensity", "raceMenu", "orderedCustomMorphs"}
        or appearance.get("gender") is not None
        or appearance.get("headParts") != []
        or appearance.get("hairColor") is not None
        or appearance.get("weight") != {
            "value": 50, "thin": None, "muscular": None, "fat": None}
        or any(appearance.get(name) != [] for name in (
            "sliderMorphs", "tints", "overlays", "unknownFields",
            "orderedCustomMorphs"))
        or any(appearance.get(name) != {} for name in (
            "morphs", "bodyMorphs", "customMorphs"))
        or appearance.get("skin") is not None
        or appearance.get("fallout4BodyMorphs") is not None
        or appearance.get("chargenFaceMorphs") is not None
        or appearance.get("faceBoneRegions") is not None
        or appearance.get("raceMenu") != {
            "headTexture": None, "faceMorphPresets": [],
            "sculptDivisor": 10000, "sculptParts": [],
            "bodyMorphsKeyed": {}, "bodyOverlays": [],
            "nodeTransforms": [], "skinOverrides": [],
            "version": {
                "formatVersion": 4, "runtimeVersion": 17039360,
                "signature": 1397442898, "skseVersion": 131840},
            "faceTextures": [], "modNames": [], "mods": []}
        or appearance.get("facialMorphIntensity") != 1
        or appearance.get("presence") != {
            "gender": False, "headParts": False, "hairColor": False,
            "weight": True, "morphs": False, "bodyMorphs": False,
            "tints": False, "overlays": False, "skin": False,
            "fallout4BodyMorphs": False, "chargenFaceMorphs": False,
            "faceBoneRegions": False, "facialMorphIntensity": False}
        or receipt.get("diagnostics") != []
    ):
        raise ReleaseError("preset-inspect-v1 inspection receipt mismatch")

    artifacts = envelope.get("artifacts")
    if not isinstance(artifacts, list) or len(artifacts) != 3:
        raise ReleaseError(
            "preset-inspect-v1 must emit source, receipt, and workflow artifacts")
    source_artifact = next((row for row in artifacts
        if isinstance(row, dict) and row.get("kind") == "racemenu-jslot"), None)
    receipt_artifact = next((row for row in artifacts
        if isinstance(row, dict) and row.get("kind") == "preset-inspection"), None)
    if (
        not isinstance(source_artifact, dict)
        or source_artifact.get("path") != str(source)
        or source_artifact.get("size") != len(source_bytes)
        or source_artifact.get("sha256") != source_sha
        or source_artifact.get("schemaOrMediaType") != "application/json"
        or source_artifact.get("producerCommand") != "preset inspect"
        or source_artifact.get("requestDigest") != envelope["requestDigest"]
        or source_artifact.get("inputBindings") != []
        or source_artifact.get("state") != "independentlyVerified"
        or not isinstance(receipt_artifact, dict)
        or receipt_artifact.get("path") != str(output)
        or receipt_artifact.get("size") != len(receipt_bytes)
        or receipt_artifact.get("sha256") != receipt_sha
        or any(receipt_artifact.get(key) != value
               for key, value in expected_artifact.items())
        or receipt_artifact.get("requestDigest") != envelope["requestDigest"]
        or receipt_artifact.get("inputBindings") != [source_sha]
    ):
        raise ReleaseError("preset-inspect-v1 artifact binding mismatch")

    result = envelope.get("result")
    if (
        not isinstance(result, dict)
        or set(result) != {
            "schemaVersion", "format", "edition", "sourcePath",
            "sourceSha256", "isValid", "appearance", "diagnostics",
            "inspectionPath", "inspectionSize", "inspectionSha256"}
        or result.get("schemaVersion") != "1"
        or result.get("format") != "racemenu-jslot"
        or result.get("edition") != "skyrimse"
        or result.get("sourcePath") != str(source)
        or result.get("sourceSha256") != source_sha
        or result.get("isValid") is not True
        or result.get("appearance") != receipt["appearance"]
        or result.get("diagnostics") != []
        or result.get("inspectionPath") != str(output)
        or result.get("inspectionSize") != len(receipt_bytes)
        or result.get("inspectionSha256") != receipt_sha
    ):
        raise ReleaseError("preset-inspect-v1 result mismatch")

    action_rows = envelope.get("nextActions")
    action = action_rows[0] if isinstance(action_rows, list) and len(action_rows) == 1 else None
    expected_action = probe["expectedNextAction"]
    if (
        not isinstance(action, dict)
        or action.get("command") != expected_action["command"]
        or action.get("missingPrerequisites")
            != expected_action["missingPrerequisites"]
        or action.get("requiresHumanAction")
            is not expected_action["requiresHumanAction"]
        or action.get("requiredBindings") != [
            {"option": "--preset", "value": str(source),
             "artifactSha256": source_sha},
            {"option": "--preset-sha256", "value": source_sha,
             "artifactSha256": source_sha},
            {"option": "--workflow-bundle",
             "value": str(paths["presetWorkflow"]),
             "artifactSha256": digest(paths["presetWorkflow"])},
            {"option": "--workflow-bundle-sha256",
             "value": digest(paths["presetWorkflow"]),
             "artifactSha256": digest(paths["presetWorkflow"])},
        ]
    ):
        raise ReleaseError("preset-inspect-v1 next action mismatch")
    workflow_sha, _ = _validate_workflow_bundle_artifact(
        envelope, probe, paths["presetWorkflow"])
    _validate_preset_inspection_journal(
        envelope, workspace_root,
        sorted([source_sha, receipt_sha, workflow_sha]),
        probe["expectedEffects"][:-1])


def _verify_preview_consumer_required_probe(
    executable: Path,
    probe: dict[str, Any],
    workspace_root: Path,
    extraction_root: Path,
    catalog_root: Path,
) -> None:
    schema = _run_protocol_v2_probe(
        executable,
        ("schema", "export", "--protocol", "2", "--json", "--command",
         probe["command"]),
        "schema export",
        0,
        workspace_root=workspace_root,
        extraction_root=extraction_root)
    _validate_protocol_v2_workflow_schema_export(
        schema, probe["command"], probe["resultSchemaIds"],
        probe["resultSchemaId"])


def _load_current_staging_catalog(
    workflows: set[str], schemas: dict[str, Any],
    *, root: Path = CURRENT_STAGING_PROBE_ROOT,
) -> list[dict[str, Any]]:
    if workflows != CURRENT_STAGING_WORKFLOWS:
        raise ReleaseError("current staging requires exactly eleven protocol-2-ready commands")
    catalog_sha256 = (
        PUBLIC_CURRENT_STAGING_CATALOG_SHA256
        if root == PUBLIC_CURRENT_STAGING_PROBE_ROOT
        else CURRENT_STAGING_CATALOG_SHA256)
    catalog = _pinned_json_schema(
        root / "catalog.json", catalog_sha256,
        "current staging catalog")
    probes = catalog["probes"]
    if {probe["command"] for probe in probes} != workflows or len(probes) != len(workflows):
        raise ReleaseError("current staging catalog command set mismatch")
    for probe in probes:
        if schemas.get(probe["command"]) != probe["resultSchemaIds"]:
            raise ReleaseError("current staging advertised result schemas changed: " + probe["command"])
    return probes


def _validate_current_staging_schema(
    envelope: dict[str, Any],
    command: str,
    *, root: Path = CURRENT_STAGING_PROBE_ROOT,
) -> None:
    inventory_sha256 = (
        PREVIEW272_STAGING_SCHEMA_INVENTORY_SHA256
        if root == PREVIEW272_PROBE_ROOT
        else PREVIEW273_STAGING_SCHEMA_INVENTORY_SHA256
        if root == PREVIEW273_PROBE_ROOT
        else PREVIEW274_STAGING_SCHEMA_INVENTORY_SHA256
        if root == PREVIEW274_PROBE_ROOT
        else CURRENT_STAGING_SCHEMA_INVENTORY_SHA256
    )
    hashes = _pinned_json_schema(
        root / "schema-hashes.json",
        inventory_sha256,
        "current staging schema inventory")
    expected = _pinned_json_schema(
        root / "schemas" / (command.replace(" ", "-") + ".schema.json"),
        hashes[command], "current staging " + command + " schema")
    if envelope.get("outcome") != "succeeded" or envelope.get("result") != expected:
        raise ReleaseError("current staging exact schema export changed: " + command)


def _current_staging_file_binding(row: dict[str, Any], workspace: Path) -> None:
    path = Path(row["path"])
    try:
        relative = safe_relative(path.relative_to(workspace).as_posix())
    except ValueError as exc:
        raise ReleaseError("current staging artifact escaped its workspace") from exc
    current = workspace
    for component in PurePosixPath(relative).parts:
        current = current / component
        if _is_reparse_point(current):
            raise ReleaseError("current staging artifact is a reparse point")
    if not path.is_file() or path.stat().st_size != row.get("size") or digest(path) != str(row.get("sha256", "")).upper():
        raise ReleaseError("current staging artifact physical binding mismatch")


def _current_staging_artifacts(envelope: dict[str, Any], workspace: Path, phase: str | None) -> None:
    artifacts = envelope["artifacts"]
    if not artifacts:
        raise ReleaseError("current staging workflow emitted no artifacts: " + envelope["command"])
    for artifact in artifacts:
        _current_staging_file_binding(artifact, workspace)
        if (artifact["producerCommand"] != envelope["command"] or
                artifact["requestDigest"] != envelope["requestDigest"]):
            raise ReleaseError("current staging artifact binding mismatch: " + artifact["kind"])
    if phase is not None:
        workflows = [row for row in artifacts if row["kind"] == "workflow-bundle"]
        if len(workflows) != 1:
            raise ReleaseError("current staging must emit one workflow bundle")
        workflow, _ = _strict_protocol_json(Path(workflows[0]["path"]), "current staging workflow")
        if workflow.get("phase") != phase or workflow.get("requestDigest") != envelope["requestDigest"]:
            raise ReleaseError("current staging workflow phase or request binding mismatch")
        for row in workflow["artifacts"]:
            _current_staging_file_binding(row, workspace)
        if workflow.get("authority") != _workflow_authority_rows(workflow["artifacts"]):
            raise ReleaseError("current staging workflow authority derivation mismatch")
        if phase == "review-required":
            authority = {row["kind"]: row["state"] for row in envelope["authority"]}
            proposals = [row for row in workflow["artifacts"] if row["kind"] == "npc-finish-core-proposal"]
            actions = [action for action in workflow["nextActions"] if action.get("command") == "gui"]
            if (authority.get("humanVisualAcceptance") != "required" or
                    any(row["kind"] == "review-receipt" for row in workflow["artifacts"]) or
                    len(proposals) != 1 or len(actions) != 1 or
                    actions[0].get("requiresHumanAction") is not True):
                raise ReleaseError("current staging Finish lost its human-review requirement")
            proposal = proposals[0]
            if actions[0].get("requiredBindings") != [
                {"option": "--proposal", "value": proposal["path"],
                 "artifactSha256": proposal["sha256"]},
                {"option": "--proposal-sha256", "value": proposal["sha256"],
                 "artifactSha256": proposal["sha256"]},
            ]:
                raise ReleaseError("current staging Finish lost its human-review requirement")


def _current_staging_tree_hash(package: Path) -> str:
    # Same public Finish source binding: ordinal relative path|length|uppercase SHA256\n.
    rows = []
    for path in sorted(package.rglob("*"), key=lambda item: item.relative_to(package).as_posix()):
        if _is_reparse_point(path):
            raise ReleaseError("current staging source package contains a reparse point")
        if path.is_file():
            rows.append(f"{path.relative_to(package).as_posix()}|{path.stat().st_size}|{digest(path)}\n")
    return hashlib.sha256("".join(rows).encode("utf-8")).hexdigest().upper()


def _verify_current_staging_workflows(
    executable: Path, probes: list[dict[str, Any]], workspace: Path, extraction: Path,
    catalog_root: Path,
    external_pack: tuple[bytes, str] | None = None,
) -> None:
    for directory in ("workflow", "evidence"):
        _ensure_ordinary_directory(workspace / directory, "current staging " + directory)
    # Only portable synthetic inputs are retained. All workflow/output authority is produced here.
    inputs: dict[Path, str] = {}
    for probe in probes:
        for row in probe["fixtures"]:
            relative, data = _read_protocol_v2_fixture(
                probe, row, catalog_root)
            target = workspace / relative
            current = workspace
            for component in PurePosixPath(relative).parts[:-1]:
                current = current / component
                _ensure_ordinary_directory(current, "current staging fixture directory")
            if target.exists():
                raise ReleaseError("current staging fixture destination already exists")
            with target.open("xb") as stream:
                stream.write(data)
            inputs[target] = row["sha256"]
            if digest(target) != row["sha256"]:
                raise ReleaseError("current staging fixture readback failed")

    if catalog_root == PUBLIC_CURRENT_STAGING_PROBE_ROOT:
        if external_pack is None:
            raise ReleaseError(
                "ACTORWRIGHT_TEST_SKYRIM_MASTER is required for public current staging")
        _append_public_finish_master(
            workspace / "Data" / "Skyrim.esm",
            workspace / "authority" / "Skyrim.esm",
            external_pack[0],
        )

    def resolve(value: str) -> str:
        if value == "{workspace}":
            return str(workspace)
        for prefix in ("{path:", "{sha256:"):
            if value.startswith(prefix) and value.endswith("}"):
                path = workspace / safe_relative(value[len(prefix):-1])
                return digest(path) if prefix == "{sha256:" else str(path)
        return value

    source_tree: str | None = None
    for probe in probes:
        command = probe["command"]
        schema = _run_protocol_v2_probe(executable,
            ("schema", "export", "--protocol", "2", "--json", "--command", command),
            "schema export", 0, workspace_root=workspace, extraction_root=extraction)
        _validate_current_staging_schema(schema, command, root=catalog_root)
        if command == "preview npc":
            continue  # The pinned export is contract evidence; no authentic rendering claim.
        if command == "npc finish analyze":
            template_sha256 = (
                PUBLIC_CURRENT_STAGING_FINISH_POLICY_TEMPLATE_SHA256
                if catalog_root == PUBLIC_CURRENT_STAGING_PROBE_ROOT
                else CURRENT_STAGING_FINISH_POLICY_TEMPLATE_SHA256)
            request = _pinned_json_schema(
                catalog_root / "finish-request-template.json",
                template_sha256, "current staging Finish policy template")
            if catalog_root == PUBLIC_CURRENT_STAGING_PROBE_ROOT:
                request["sandboxAuthority"]["copiedMasterSha256"] = digest(
                    workspace / "authority" / "Skyrim.esm")
                request["sandboxAuthority"]["rawRecordDigest"] = external_pack[1]
            package = workspace / "planned-npc-output"
            plugin = package / "Data" / "PackagedNpc.esp"
            source_tree = _current_staging_tree_hash(package)
            request["source"]["packageTreeSha256"] = source_tree
            request["source"]["packageManifestSha256"] = digest(package / "npcmanager-package.json")
            request["source"]["pluginSha256"] = digest(plugin)
            request["authorities"]["providers"][0].update(sha256=digest(plugin), byteLength=plugin.stat().st_size)
            with (workspace / "finish-request.json").open("x", encoding="utf-8", newline="\n") as stream:
                json.dump(request, stream, indent=2, ensure_ascii=False)
        args = tuple(resolve(value) for value in probe["arguments"])
        code = 4 if command == "npc assembly preflight" else 0
        envelope = _run_protocol_v2_probe(executable, args, command, code,
            workspace_root=workspace, extraction_root=extraction)
        if command == "npc assembly preflight":
            if (envelope.get("outcome") != "failed" or envelope["artifacts"] != [] or
                    envelope.get("result", {}).get("contractAdmitted") is not False or
                    not any(row["code"] == "npc-build-preflight-validation-failed" for row in envelope["diagnostics"]) or
                    (workspace / "evidence/assembly.json").exists()):
                raise ReleaseError("current staging invalid assembly contract was not refused")
            continue
        elif envelope.get("outcome") != "succeeded":
            raise ReleaseError("current staging workflow did not succeed: " + command)
        _current_staging_artifacts(envelope, workspace, probe["expectedWorkflow"].get("phase"))
        if command == "npc create-from-jslot":
            if envelope["result"].get("readyForBuild") is not True:
                raise ReleaseError("current staging NPC preflight was not ready")
            build_args = args[:args.index("--preflight-output")] + (
                "--reviewed-preflight", str(workspace / "evidence/preflight.json"),
                "--reviewed-preflight-sha256", digest(workspace / "evidence/preflight.json"),
                "--workflow-bundle", str(workspace / "workflow/preflight.json"),
                "--workflow-bundle-sha256", digest(workspace / "workflow/preflight.json"),
                "--workflow-output", str(workspace / "workflow/created.json"))
            built = _run_protocol_v2_probe(executable, build_args, command, 0,
                workspace_root=workspace, extraction_root=extraction)
            if built.get("outcome") != "succeeded" or built.get("result", {}).get("completed") is not True:
                raise ReleaseError("current staging actual NPC build did not complete")
            _current_staging_artifacts(built, workspace, "verify")
        if command == "npc finish apply" and envelope.get("result", {}).get("applied") is not True:
            raise ReleaseError("current staging Finish did not apply")
        if command == "npc finish verify":
            if envelope.get("result", {}).get("verified") is not True:
                raise ReleaseError("current staging Finish did not independently verify")
            if _current_staging_tree_hash(workspace / "planned-npc-output") != source_tree:
                raise ReleaseError("current staging Finish changed its source package")
    if any(digest(path) != sha for path, sha in inputs.items()):
        raise ReleaseError("current staging changed a source fixture")


def _verify_protocol_v2_workflow_probes(
    executable: Path,
    probes: list[dict[str, Any]],
    workspace_root: Path,
    extraction_root: Path,
    catalog_root: Path,
    release_version: str,
) -> None:
    validators = {
        "npc-assembly-preflight-v1": _verify_npc_assembly_preflight_probe,
        "finish-verify-v1": _verify_finish_verify_probe,
        "npc-create-preflight-v1": _verify_npc_create_preflight_probe,
        "preset-inspect-v1": _verify_preset_inspect_probe,
        "workspace-preflight-v1": _verify_workspace_preflight_probe,
        "preview-consumer-required-v1":
            _verify_preview_consumer_required_probe,
    }
    for probe in probes:
        validator = validators.get(probe["validator"])
        if validator is None:
            raise ReleaseError(
                f"protocol 2 workflow probe validator is unknown: {probe['validator']}")
        if probe["validator"] in {
            "npc-create-preflight-v1", "finish-verify-v1",
            "workspace-preflight-v1",
        }:
            validator(
                executable, probe, workspace_root, extraction_root, catalog_root,
                release_version)
        else:
            validator(
                executable, probe, workspace_root, extraction_root, catalog_root)


def _require_workflow_fixture_sources(
    probes: list[dict[str, Any]], catalog_root: Path,
) -> None:
    missing = False
    for probe in probes:
        fixtures = probe.get("fixtures")
        if not isinstance(fixtures, list):
            raise ReleaseError("protocol 2 workflow fixture list is malformed")
        for row in fixtures:
            if not isinstance(row, dict) or not isinstance(row.get("source"), str):
                raise ReleaseError("protocol 2 workflow fixture row is malformed")
            relative = safe_relative(row["source"])
            source = catalog_root / relative
            try:
                source.relative_to(catalog_root)
            except ValueError as exception:
                raise ReleaseError(
                    "protocol 2 workflow fixture escaped its catalog") from exception
            if (source.exists() or source.is_symlink()) and _is_reparse_point(source):
                raise ReleaseError("protocol 2 workflow fixture is unsafe")
            if not source.is_file():
                missing = True
    if missing:
        if catalog_root == PUBLIC_CURRENT_STAGING_PROBE_ROOT:
            raise ReleaseError(
                "public current staging has a missing pinned synthetic fixture")
        raise ReleaseError(
            "historical-positive-unavailable: authenticated historical workflow "
            "payloads are absent from this source-only release")


def _verify_protocol_v2_kernel_probes(
    root: Path,
    expected_command_names: set[str],
    workspace_root: Path,
    extraction_root: Path,
    identity: dict[str, str],
    *, current_staging: bool = False,
    external_pack: tuple[bytes, str] | None = None,
) -> list[str]:
    expected_command_count = EXPECTED_COMMAND_COUNTS_BY_VERSION[
        identity["version"]]
    executable = root / "cli" / "actorwright.exe"
    capabilities = _run_protocol_v2_probe(
        executable,
        ("capabilities", "--protocol", "2", "--json"),
        "capabilities",
        0,
        workspace_root=workspace_root,
        extraction_root=extraction_root,
    )
    capabilities_result = capabilities.get("result")
    rows = (
        capabilities_result.get("commands", [])
        if isinstance(capabilities_result, dict) else []
    )
    names = [row.get("name") for row in rows if isinstance(row, dict)]
    if (
        not isinstance(rows, list)
        or len(names) != len(rows)
        or any(not _is_nonempty_string(name) for name in names)
        or len(names) != len(set(names))
        or set(names) != expected_command_names
    ):
        raise ReleaseError(
            "protocol 2 capabilities command name set differs from protocol 1")
    readiness = {
        str(row.get("name")): row.get("readiness")
        for row in rows if isinstance(row, dict)
    }
    advertised_schemas = {
        str(row.get("name")): row.get("resultSchemaIds")
        for row in rows if isinstance(row, dict)
    }
    ready_commands = {
        name for name, state in readiness.items() if state == "v2"
    }
    if (
        not isinstance(capabilities_result, dict)
        or capabilities.get("outcome") != "succeeded"
        or capabilities_result.get("schemaId")
            != PROTOCOL_V2_KERNEL_READY_COMMANDS["capabilities"]
        or capabilities_result.get("protocolVersion") != "2"
        or len(rows) != expected_command_count
        or len(names) != expected_command_count
        or len(set(names)) != expected_command_count
        or not set(PROTOCOL_V2_KERNEL_READY_COMMANDS).issubset(ready_commands)
        or any(state not in {"v2", "legacy"} for state in readiness.values())
        or any(
            advertised_schemas.get(name) != [schema]
            for name, schema in PROTOCOL_V2_KERNEL_READY_COMMANDS.items()
        )
        or readiness.get("npc create") != "legacy"
        or advertised_schemas.get("npc create") != []
        or any(
            advertised_schemas.get(name) != []
            for name, state in readiness.items() if state == "legacy"
        )
    ):
        raise ReleaseError(
            "protocol 2 capabilities do not preserve the exact kernel and legacy sentinel")

    workflow_commands = ready_commands - set(PROTOCOL_V2_KERNEL_READY_COMMANDS)
    expected_workflows = CURRENT_STAGING_WORKFLOWS if current_staging else BINARY_RELEASE_WORKFLOW_COMMANDS.get(identity["version"])
    catalog_root = _select_protocol_v2_probe_root(identity["version"])
    if expected_workflows is None:
        raise ReleaseError("protocol 2 workflow release identity is unknown")
    workflow_probes = _load_protocol_v2_workflow_probe_catalog(
        workflow_commands,
        advertised_schemas,
        release_version=identity["version"],
        root=catalog_root,
    )
    _require_workflow_fixture_sources(workflow_probes, catalog_root)
    if workflow_commands != set(expected_workflows):
        raise ReleaseError(
            "protocol 2 workflow command set differs from release identity")

    version = _run_protocol_v2_probe(
        executable,
        ("version", "--protocol", "2", "--json"),
        "version",
        0,
        workspace_root=workspace_root,
        extraction_root=extraction_root,
    )
    version_result = version.get("result")
    if (
        version.get("outcome") != "succeeded"
        or not isinstance(version_result, dict)
        or version_result.get("schemaId")
            != PROTOCOL_V2_KERNEL_READY_COMMANDS["version"]
        or version_result.get("productName") != "Actorwright"
        or version_result.get("productVersion") != identity["version"]
        or version_result.get("sourceLine") != identity["sourceLine"]
        or version_result.get("targetFramework") != "net10.0-windows"
        or version_result.get("protocolVersion") != "2"
        or version_result.get("supportedProtocolVersions") != ["1", "2"]
    ):
        raise ReleaseError("protocol 2 version result is not truthful")

    schema = _run_protocol_v2_probe(
        executable,
        (
            "schema", "export", "--protocol", "2", "--json",
            "--command", "npc finish analyze",
        ),
        "schema export",
        0,
        workspace_root=workspace_root,
        extraction_root=extraction_root,
    )
    if current_staging:
        _validate_current_staging_schema(
            schema, "npc finish analyze", root=catalog_root)
    else:
        _validate_protocol_v2_schema_export(schema, identity["version"])

    refusal = _run_protocol_v2_probe(
        executable,
        ("npc", "create", "--protocol", "2", "--json"),
        "npc create",
        2,
        workspace_root=workspace_root,
        extraction_root=extraction_root,
    )
    diagnostics = refusal.get("diagnostics")
    if (
        refusal.get("outcome") != "failed"
        or "result" in refusal
        or not isinstance(diagnostics, list)
        or len(diagnostics) != 1
        or not isinstance(diagnostics[0], dict)
        or diagnostics[0].get("code") != "protocol-command-legacy"
        or diagnostics[0].get("severity") != "error"
        or diagnostics[0].get("class") != "usage"
        or not isinstance(diagnostics[0].get("recovery"), dict)
        or diagnostics[0]["recovery"].get("action") != "correctInput"
    ):
        raise ReleaseError(
            "protocol 2 legacy command refusal is not typed and truthful")

    if current_staging:
        _verify_current_staging_workflows(
            executable, workflow_probes, workspace_root, extraction_root,
            catalog_root, external_pack)
    else:
        _verify_protocol_v2_workflow_probes(
            executable, workflow_probes, workspace_root, extraction_root,
            catalog_root, identity["version"])
    return sorted(workflow_commands)


def verify_protocol_v2_kernel(
    root: Path,
    expected_command_names: set[str],
    identity: dict[str, str],
    *, current_staging: bool = False,
) -> list[str]:
    catalog_root = _select_protocol_v2_probe_root(identity["version"])
    external_pack = (
        _read_external_finish_pack_record()
        if catalog_root == PUBLIC_CURRENT_STAGING_PROBE_ROOT
        else None
    )
    temporary_parent = _verified_protocol_temporary_parent()
    try:
        temporary_root = Path(tempfile.mkdtemp(
            prefix="p-", dir=temporary_parent))
    except OSError as exc:
        raise ReleaseError(
            "protocol 2 temporary child could not be created") from exc
    try:
        if (
            temporary_root.parent != temporary_parent
            or not temporary_root.name.startswith("p-")
        ):
            raise ReleaseError(
                "protocol 2 temporary child escaped repository-local artifacts/x")
        _ensure_ordinary_directory(
            temporary_root, "protocol 2 temporary child")
        workspace_root = temporary_root / "w"
        extraction_root = temporary_root / "e"
        _ensure_ordinary_directory(
            workspace_root, "protocol 2 probe workspace")
        _ensure_ordinary_directory(
            extraction_root, "protocol 2 extraction root")
        return _verify_protocol_v2_kernel_probes(
            root,
            expected_command_names,
            workspace_root,
            extraction_root,
            identity,
            current_staging=current_staging,
            external_pack=external_pack,
        )
    finally:
        _cleanup_protocol_temporary_root(
            temporary_root, temporary_parent)


def verify_package_staging(
    root: Path,
    *,
    metadata_only: bool = False,
) -> dict[str, Any]:
    files = inventory_package_staging(root, allow_launcher=True)
    manifest = read_json(root / "manifest.json")
    required = {
        "schemaVersion", "product", "version", "sourceLine", "runtime",
        "privateOnly", "runtimeAuthority", "commandCount",
        "embeddedResourceClosures", "files",
    }
    if not isinstance(manifest, dict) or set(manifest) != required:
        raise ReleaseError("binary package manifest field set mismatch")
    identity = _select_binary_release_identity(
        manifest.get("version"), manifest.get("sourceLine"))
    expected_command_count = EXPECTED_COMMAND_COUNTS_BY_VERSION[
        identity["version"]]
    if (
        manifest["schemaVersion"] != 1
        or manifest["product"] != "Actorwright"
        or manifest["version"] != identity["version"]
        or manifest["sourceLine"] != identity["sourceLine"]
        or manifest["runtime"] != "win-x64"
        or manifest["privateOnly"] is not True
        or manifest["runtimeAuthority"] is not False
        or manifest["commandCount"] != expected_command_count
    ):
        raise ReleaseError("binary package identity or authority mismatch")
    requires_launcher = identity["version"] == "1.0.0-preview.281"
    if ("cli/actorwright.ps1" in files) != requires_launcher:
        raise ReleaseError("binary package PowerShell launcher requirement mismatch")

    rows = manifest["files"]
    if not isinstance(rows, list):
        raise ReleaseError("binary package file inventory must be an array")
    declared: dict[str, tuple[int, str]] = {}
    for row in rows:
        if not isinstance(row, dict) or set(row) != {
            "path", "size", "sha256"
        }:
            raise ReleaseError("binary package file row field set mismatch")
        relative = safe_relative(str(row["path"]))
        size = row["size"]
        sha = str(row["sha256"]).upper()
        if (
            relative in declared
            or not isinstance(size, int)
            or size < 0
            or not HASH_RE.fullmatch(sha)
        ):
            raise ReleaseError(
                f"invalid binary package file declaration: {relative}")
        declared[relative] = (size, sha)
    actual = {relative: path for relative, path in files.items()
              if relative != "manifest.json"}
    if set(declared) != set(actual):
        raise ReleaseError(
            "binary package inventory mismatch: "
            f"undeclared={sorted(set(actual) - set(declared))}, "
            f"missing={sorted(set(declared) - set(actual))}")
    for relative, (size, sha) in declared.items():
        path = actual[relative]
        if path.stat().st_size != size or digest(path) != sha:
            raise ReleaseError(
                f"binary package size or hash mismatch: {relative}")

    capabilities = read_json(root / "capabilities.json")
    names = [row.get("name") for row in capabilities.get("commands", [])
             if isinstance(row, dict)]
    if (
        capabilities.get("version") != identity["version"]
        or capabilities.get("sourceLine") != identity["sourceLine"]
        or len(names) != expected_command_count
        or len(set(names)) != expected_command_count
    ):
        raise ReleaseError(
            "binary package capabilities must contain exactly "
            f"{expected_command_count} unique commands")

    probe_names = {
        "cli/actorwright.exe": "cli-resource-closure.json",
        "desktop/Actorwright.Desktop.exe":
            "desktop-resource-closure.json",
    }
    verify_stored_resource_closures(
        root,
        manifest["embeddedResourceClosures"],
        probe_names,
        "binary package",
    )
    workflow_commands: list[str] = []
    if not metadata_only:
        verify_embedded_resource_closures(root, manifest)
        workflow_commands = verify_protocol_v2_kernel(
            root, set(names), identity,
            current_staging=identity["version"] in {
                "1.0.0-preview.267", "1.0.0-preview.268",
                "1.0.0-preview.269", "1.0.0-preview.270",
                "1.0.0-preview.271", "1.0.0-preview.272",
                "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
                "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
                "1.0.0-preview.280", "1.0.0-preview.281"})
    result = {
        "status": "PASS",
        "artifactKind": "binary-package-staging",
        "version": manifest["version"],
        "files": len(files),
        "zipVerified": False,
    }
    if not metadata_only:
        result["protocolV2Kernel"] = True
        result["protocolV2WorkflowCommands"] = workflow_commands
        result["protocolV2ConsumerRequiredCommands"] = [
            command for command in workflow_commands
            if command == "preview npc"]
    return result


def verify_protocol_v2_schema_snapshot(
    path: Path,
    capabilities: dict[str, Any],
    identity: dict[str, str],
) -> None:
    snapshot = read_json(path)
    if (
        not isinstance(snapshot, dict)
        or set(snapshot) != {
            "schemaVersion", "productVersion", "sourceLine",
            "protocolVersion", "commands"}
        or snapshot.get("schemaVersion") != 1
        or snapshot.get("productVersion") != identity["version"]
        or snapshot.get("sourceLine") != identity["sourceLine"]
        or snapshot.get("protocolVersion") != "2"
        or not isinstance(snapshot.get("commands"), list)
    ):
        raise ReleaseError("protocol-v2 schema snapshot identity or shape mismatch")

    capability_rows = capabilities.get("commands")
    if not isinstance(capability_rows, list):
        raise ReleaseError("capabilities command rows are unavailable")
    expected = {
        row.get("name"): row.get("resultSchemaIds")
        for row in capability_rows
        if isinstance(row, dict) and row.get("readiness") == "v2"
    }
    expected_workflows = BINARY_RELEASE_WORKFLOW_COMMANDS.get(
        identity["version"])
    if expected_workflows is None:
        raise ReleaseError("protocol-v2 schema snapshot release identity is unknown")
    expected_commands = set(PROTOCOL_V2_KERNEL_READY_COMMANDS) | set(
        expected_workflows)
    if set(expected) != expected_commands:
        raise ReleaseError(
            "protocol-v2 schema snapshot workflow command set mismatch")
    observed: dict[str, Any] = {}
    requires_path_conventions = identity["version"] in {
        "1.0.0-preview.267", "1.0.0-preview.268", "1.0.0-preview.269",
        "1.0.0-preview.270", "1.0.0-preview.271", "1.0.0-preview.272",
        "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
        "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
        "1.0.0-preview.280",
        "1.0.0-preview.281",
    }
    for index, row in enumerate(snapshot["commands"]):
        if (
            not isinstance(row, dict)
            or set(row) != {"command", "resultSchemaIds", "export"}
            or not _is_nonempty_string(row.get("command"))
            or not isinstance(row.get("resultSchemaIds"), list)
            or not isinstance(row.get("export"), dict)
        ):
            raise ReleaseError(
                f"protocol-v2 schema snapshot row is malformed: {index}")
        command = row["command"]
        export = row["export"]
        contract = export.get("contract")
        definitions = export.get("resultSchemas")
        expected_export_fields = {
            "schemaId", "protocolVersion", "scopedHelpResultSchema",
            "contract", "resultSchemas", "documentSchemas",
        }
        if requires_path_conventions:
            expected_export_fields.add("pathConventions")
        path_conventions = export.get("pathConventions")
        projected_ids = [
            value.get("schemaIdentifier")
            for value in definitions
            if isinstance(value, dict)
        ] if isinstance(definitions, list) else []
        if (
            command in observed
            or row["resultSchemaIds"] != expected.get(command)
            or set(export) != expected_export_fields
            or (
                requires_path_conventions
                and (
                    not isinstance(path_conventions, dict)
                    or set(path_conventions) != {"cli", "documents", "assets"}
                    or any(not _is_nonempty_string(value)
                           for value in path_conventions.values())
                )
            )
            or export.get("schemaId")
                != PROTOCOL_V2_KERNEL_READY_COMMANDS["schema export"]
            or export.get("protocolVersion") != "2"
            or not isinstance(contract, dict)
            or contract.get("name") != command
            or contract.get("readiness") != "v2"
            or contract.get("resultSchemaIds") != row["resultSchemaIds"]
            or projected_ids != row["resultSchemaIds"]
            or len(projected_ids) != len(set(projected_ids))
            or not isinstance(export.get("documentSchemas"), list)
            or not isinstance(export.get("scopedHelpResultSchema"), dict)
        ):
            raise ReleaseError(
                f"protocol-v2 schema snapshot drifted from capabilities: {command}")
        observed[command] = row["resultSchemaIds"]
    if list(observed) != sorted(expected) or observed != {
        name: expected[name] for name in sorted(expected)
    }:
        raise ReleaseError("protocol-v2 schema snapshot command set mismatch")


def verify_sbom(sbom: Any, release: dict[str, Any]) -> None:
    required = {
        "spdxVersion", "dataLicense", "SPDXID", "name",
        "documentNamespace", "creationInfo", "packages",
        "documentDescribes",
    }
    if not isinstance(sbom, dict) or set(sbom) != required:
        raise ReleaseError("SPDX SBOM field set mismatch")
    if (
        sbom["spdxVersion"] != "SPDX-2.3"
        or sbom["dataLicense"] != "CC0-1.0"
        or sbom["SPDXID"] != "SPDXRef-DOCUMENT"
        or sbom["name"] != f"Actorwright-{release['version']}"
        or sbom["documentNamespace"] != (
            f"https://actorwright.invalid/spdx/{release['version']}/"
            f"{release['sourceCommit']}"
        )
    ):
        raise ReleaseError("SPDX SBOM document identity mismatch")
    creation = sbom["creationInfo"]
    if (
        not isinstance(creation, dict)
        or set(creation) != {"created", "creators"}
        or not re.fullmatch(
            r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z",
            str(creation["created"]),
        )
        or creation["creators"] != ["Tool: Actorwright-generate-sbom"]
    ):
        raise ReleaseError("SPDX SBOM creation information mismatch")
    packages = sbom["packages"]
    if (
        not isinstance(packages, list)
        or len(packages) != EXPECTED_SBOM_PACKAGE_COUNT
    ):
        raise ReleaseError(
            "SPDX SBOM package inventory must contain exactly "
            f"{EXPECTED_SBOM_PACKAGE_COUNT} packages")
    package_fields = {
        "SPDXID", "name", "versionInfo", "downloadLocation",
        "filesAnalyzed", "licenseConcluded", "licenseDeclared",
        "copyrightText", "externalRefs",
    }
    package_ids: list[str] = []
    package_keys: set[tuple[str, str]] = set()
    for package in packages:
        if not isinstance(package, dict) or set(package) != package_fields:
            raise ReleaseError("SPDX SBOM package field set mismatch")
        name = str(package["name"])
        version = str(package["versionInfo"])
        package_id = str(package["SPDXID"])
        expected_id = "SPDXRef-Package-" + re.sub(
            r"[^A-Za-z0-9.-]", "-", f"{name}-{version}")
        expected_reference = [{
            "referenceCategory": "PACKAGE-MANAGER",
            "referenceType": "purl",
            "referenceLocator": f"pkg:nuget/{name}@{version}",
        }]
        if (
            not name
            or not version
            or package_id != expected_id
            or package["downloadLocation"] != (
                f"https://www.nuget.org/packages/{name}/{version}")
            or package["filesAnalyzed"] is not False
            or package["licenseConcluded"] != "NOASSERTION"
            or package["licenseDeclared"] != "NOASSERTION"
            or package["copyrightText"] != "NOASSERTION"
            or package["externalRefs"] != expected_reference
            or (name, version) in package_keys
        ):
            raise ReleaseError(
                f"SPDX SBOM package declaration mismatch: {package_id}")
        package_keys.add((name, version))
        package_ids.append(package_id)
    if sbom["documentDescribes"] != package_ids or len(set(package_ids)) != len(package_ids):
        raise ReleaseError("SPDX SBOM document package bindings mismatch")


def verify_test_summary(
    tests: Any,
    release: dict[str, Any],
    *,
    root: Path | None = None,
) -> None:
    version = str(release.get("version"))
    derives_python_counts_from_canonical_log = (
        version in {"1.0.0-preview.280", "1.0.0-preview.281"})
    if derives_python_counts_from_canonical_log:
        if root is None:
            raise ReleaseError(
                "current test summary requires its release evidence root")
        summary_path = root / "evidence" / "test-summary.json"
        if str(release.get("testSummarySha256", "")).upper() != digest(
            summary_path
        ):
            raise ReleaseError("release metadata hash mismatch: testSummarySha256")
        if tests != read_json(summary_path):
            raise ReleaseError("test summary differs from release evidence")
        verify_canonical_build_evidence(root, release)
    required = {
        "schemaVersion", "status", "sourceCommit", "sourceTag",
        "configuration", "projectsCompiled", "warnings", "errors",
        "exactCommandNames", "standalonePythonCases",
        "legacyWorkspaceBoundSuites", "runtimeAuthority",
        "visualAuthority",
    }
    if version in {"1.0.0-preview.276", "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279", "1.0.0-preview.280", "1.0.0-preview.281"}:
        required.update({
            "standalonePythonSkipped", "orderedHelpSha256",
            "protocolReadinessSha256", "selectorInventorySha256",
            "selectorResultsSha256",
        })
    if not isinstance(tests, dict) or set(tests) != required:
        raise ReleaseError("test summary field set mismatch")
    if version in {"1.0.0-preview.276", "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279", "1.0.0-preview.280", "1.0.0-preview.281"} and (
        type(tests["standalonePythonSkipped"]) is not int
        or tests["standalonePythonSkipped"] < 0
        or any(not isinstance(tests[field], str) or not HASH_RE.fullmatch(tests[field])
               for field in (
                   "orderedHelpSha256", "protocolReadinessSha256",
                   "selectorInventorySha256", "selectorResultsSha256",
               ))
    ):
        raise ReleaseError("test summary overlap pins are malformed")
    if "dependencyVulnerabilityReportSha256" in release:
        expected_python_cases = EXPECTED_STANDALONE_PYTHON_CASES_BY_VERSION.get(
            version)
        if (
            expected_python_cases is None
            and not derives_python_counts_from_canonical_log
        ):
            raise ReleaseError("test summary is not a truthful static PASS")
    else:
        expected_python_cases = LEGACY_STANDALONE_PYTHON_CASES
    expected_command_count = EXPECTED_COMMAND_COUNTS_BY_VERSION.get(
        str(release.get("version")))
    if (
        tests["schemaVersion"] != 1
        or tests["status"] != "PASS"
        or tests["sourceCommit"] != release["sourceCommit"]
        or tests["sourceTag"] != release["sourceTag"]
        or tests["configuration"] != "Release"
        or tests["projectsCompiled"] != 25
        or tests["warnings"] != 0
        or tests["errors"] != 0
        or tests["exactCommandNames"] != expected_command_count
        or (
            derives_python_counts_from_canonical_log
            and (
                type(tests["standalonePythonCases"]) is not int
                or tests["standalonePythonCases"] < 1
            )
        )
        or (
            expected_python_cases is not None
            and tests["standalonePythonCases"] != expected_python_cases
        )
        or tests["legacyWorkspaceBoundSuites"] != "COMPILE_ONLY"
        or tests["runtimeAuthority"] is not False
        or tests["visualAuthority"] is not False
    ):
        raise ReleaseError("test summary is not a truthful static PASS")


def verify_canonical_build_evidence(root: Path, release: dict[str, Any]) -> None:
    log = root / "evidence" / "canonical-build.log"
    if not log.is_file() or log.is_symlink():
        raise ReleaseError("canonical build evidence is missing")
    if str(release.get("canonicalBuildLogSha256", "")).upper() != digest(log):
        raise ReleaseError("release metadata hash mismatch: canonicalBuildLogSha256")
    text = log.read_text(encoding="utf-8")
    names = {
        "EVIDENCE_SOURCE_COMMIT", "EVIDENCE_SOURCE_TREE",
        "EVIDENCE_WORKTREE_STATUS", "EVIDENCE_RESTORE_CONFIG",
        "EVIDENCE_RESTORE_CONFIG_MODE", "EVIDENCE_RESTORE_CONFIG_SHA256",
        "EVIDENCE_TRACKED_NUGET_CONFIG_SHA256", "EVIDENCE_NUGET_AUDIT",
        "EVIDENCE_NUGET_HTTP_CACHE", "EVIDENCE_FINAL_SOURCE_COMMIT",
        "EVIDENCE_FINAL_SOURCE_TREE", "EVIDENCE_FINAL_WORKTREE_STATUS",
        "EVIDENCE_FINAL_RESTORE_CONFIG_SHA256",
    }
    stamps: dict[str, str] = {}
    for name in names:
        values = re.findall(rf"(?m)^{re.escape(name)}=([^\r\n]+)$", text)
        if len(values) != 1:
            raise ReleaseError(f"canonical build evidence stamp mismatch: {name}")
        stamps[name] = values[0]
    commit = str(release.get("sourceCommit", "")).upper()
    tree = str(release.get("sourceTree", "")).upper()
    hashes = (
        stamps["EVIDENCE_RESTORE_CONFIG_SHA256"],
        stamps["EVIDENCE_TRACKED_NUGET_CONFIG_SHA256"],
    )
    if (
        not re.fullmatch(r"[A-F0-9]{40}", tree)
        or stamps["EVIDENCE_SOURCE_COMMIT"] != commit
        or stamps["EVIDENCE_FINAL_SOURCE_COMMIT"] != commit
        or stamps["EVIDENCE_SOURCE_TREE"] != tree
        or stamps["EVIDENCE_FINAL_SOURCE_TREE"] != tree
        or stamps["EVIDENCE_WORKTREE_STATUS"] != "CLEAN"
        or stamps["EVIDENCE_FINAL_WORKTREE_STATUS"] != "CLEAN"
        or not stamps["EVIDENCE_RESTORE_CONFIG"].strip()
        or stamps["EVIDENCE_RESTORE_CONFIG_MODE"]
            not in {"tracked-default", "explicit-override"}
        or any(not re.fullmatch(r"[A-F0-9]{64}", value) for value in hashes)
        or (
            stamps["EVIDENCE_RESTORE_CONFIG_MODE"] == "tracked-default"
            and hashes[0] != hashes[1]
        )
        or stamps["EVIDENCE_FINAL_RESTORE_CONFIG_SHA256"] != hashes[0]
        or stamps["EVIDENCE_NUGET_AUDIT"].lower() in {"false", "0", "off"}
        or stamps["EVIDENCE_NUGET_HTTP_CACHE"] != "BYPASS"
    ):
        raise ReleaseError("canonical build evidence is not publication-grade")
    if release.get("version") in {"1.0.0-preview.280", "1.0.0-preview.281"}:
        pytest_summaries = re.findall(
            r"(?m)^(\d+) passed, (\d+) skipped, \d+ warnings in [^\r\n]+$",
            text,
        )
        if len(pytest_summaries) != 1:
            raise ReleaseError(
                "canonical build must contain exactly one standalone pytest summary")
        passed, skipped = map(int, pytest_summaries[0])
        summary = read_json(root / "evidence" / "test-summary.json")
        if (
            passed < 1
            or not isinstance(summary, dict)
            or type(summary.get("standalonePythonCases")) is not int
            or summary["standalonePythonCases"] != passed
            or type(summary.get("standalonePythonSkipped")) is not int
            or summary["standalonePythonSkipped"] != skipped
        ):
            raise ReleaseError(
                "test summary Python counts do not match canonical build")
    report_path = root / "evidence" / "dependency-vulnerability-report.json"
    if report_path.is_file():
        audit_transport = read_json(report_path).get("auditTransport")
        if (
            release.get("version") in {
                "1.0.0-preview.271", "1.0.0-preview.272",
                "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
                "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
                "1.0.0-preview.280", "1.0.0-preview.281"}
            and stamps["EVIDENCE_RESTORE_CONFIG_MODE"] == "explicit-override"
            and audit_transport is None
        ):
            raise ReleaseError(
                "explicit restore config lacks bound audit transport")
        if audit_transport is not None and (
            not isinstance(audit_transport, dict)
            or audit_transport.get("restoreConfigSha256") != hashes[0]
        ):
            raise ReleaseError(
                "audit transport restore config does not match canonical build")


def requires_public_advisory_evidence(release: dict[str, Any]) -> bool:
    return (
        release.get("version") in {
            "1.0.0-preview.267", "1.0.0-preview.268", "1.0.0-preview.269",
            "1.0.0-preview.270", "1.0.0-preview.271", "1.0.0-preview.272",
            "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
            "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279",
            "1.0.0-preview.280", "1.0.0-preview.281"}
        or release.get("privateOnly") is False
    )


def verify_metadata(root: Path) -> dict[str, Any]:
    for schema in (root / "schemas").rglob("*"):
        if schema.is_file() and b"\r" in schema.read_bytes():
            raise ReleaseError(
                f"schema must use canonical UTF-8 LF bytes: "
                f"{schema.relative_to(root / 'schemas').as_posix()}")
    release = read_json(root / "actorwright-release.json")
    verify_release_manifest_shape(release)
    release_binary_identity = BINARY_RELEASE_IDENTITIES.get(
        str(release.get("version")))
    current_or_rollback_identity = (
        release_binary_identity is not None
        and release.get("sourceTag") == release_binary_identity["sourceTag"])
    legacy_identity = (
        release.get("version") == "1.0.0-preview.243"
        and release.get("sourceTag") == "v1.0.0-preview.243"
        and release.get("sourceCommit") == LEGACY_PREVIEW_243_SOURCE_COMMIT)
    if (
        release["schemaVersion"] not in {1, 2}
        or release["product"] != "Actorwright"
        or not (current_or_rollback_identity or legacy_identity)
    ):
        raise ReleaseError("release product/version/tag mismatch")
    if not re.fullmatch(r"[A-Fa-f0-9]{40}", str(release["sourceCommit"])):
        raise ReleaseError("release source commit is malformed")
    if not isinstance(release["privateOnly"], bool) or release["runtimeAuthority"] is not False or release["visualAuthority"] is not False:
        raise ReleaseError("release authority/private-state mismatch")
    bindings = {
        "capabilitiesSha256": root / "evidence" / "capabilities.json",
        "sbomSha256": root / "evidence" / "sbom.spdx.json",
        "testSummarySha256": root / "evidence" / "test-summary.json",
    }
    for field, path in bindings.items():
        if str(release[field]).upper() != digest(path):
            raise ReleaseError(f"release metadata hash mismatch: {field}")
    if release["schemaVersion"] == 2:
        verify_dependency_vulnerability_report(
            root / "evidence" / "dependency-vulnerability-report.json",
            release["dependencyVulnerabilityReportSha256"],
            public_release=requires_public_advisory_evidence(release),
        )
        if release.get("version") in {
            "1.0.0-preview.267", "1.0.0-preview.268", "1.0.0-preview.269",
            "1.0.0-preview.270", "1.0.0-preview.271", "1.0.0-preview.272",
            "1.0.0-preview.273", "1.0.0-preview.274", "1.0.0-preview.275", "1.0.0-preview.276",
            "1.0.0-preview.277", "1.0.0-preview.278", "1.0.0-preview.279"}:
            verify_canonical_build_evidence(root, release)
    capabilities = read_json(bindings["capabilitiesSha256"])
    names = [row.get("name") for row in capabilities.get("commands", []) if isinstance(row, dict)]
    expected_source_line = (
        release_binary_identity["sourceLine"]
        if release_binary_identity is not None
        else "preview.243-private")
    expected_command_count = EXPECTED_COMMAND_COUNTS_BY_VERSION.get(
        str(release.get("version")))
    if capabilities.get("version") != release["version"] or capabilities.get("sourceLine") != expected_source_line or len(names) != expected_command_count or len(set(names)) != expected_command_count:
        raise ReleaseError(
            "capabilities must contain exactly "
            f"{expected_command_count} unique command names for the release identity")
    if release_binary_identity is not None:
        protocol_capabilities = read_json(
            root / "evidence" / "protocol-v2-capabilities.json")
        protocol_result = protocol_capabilities.get("result") \
            if isinstance(protocol_capabilities, dict) else None
        if (
            not isinstance(protocol_capabilities, dict)
            or protocol_capabilities.get("outcome") != "succeeded"
            or not isinstance(protocol_result, dict)
            or protocol_result.get("schemaId")
                != PROTOCOL_V2_KERNEL_READY_COMMANDS["capabilities"]
            or protocol_result.get("protocolVersion") != "2"
        ):
            raise ReleaseError(
                "protocol-v2 capability snapshot identity mismatch")
        verify_protocol_v2_schema_snapshot(
            root / "evidence" / "protocol-v2-schema-exports.json",
            protocol_result,
            release_binary_identity)
    verify_sbom(read_json(bindings["sbomSha256"]), release)
    verify_test_summary(
        read_json(bindings["testSummarySha256"]), release, root=root)
    verify_stored_resource_closures(
        root,
        release["embeddedResourceClosures"],
        {
            "cli/actorwright.exe": "evidence/cli-resource-closure.json",
            "desktop/Actorwright.Desktop.exe":
                "evidence/desktop-resource-closure.json",
        },
        "release",
    )
    renderer = read_json(root / "evidence" / "renderer-scripts.json")
    if renderer.get("embedded") != 6 or renderer.get("loosePython") != 0:
        raise ReleaseError("embedded renderer evidence is incomplete")
    return release


def create_zip(root: Path, archive: Path) -> None:
    if archive.exists() or archive.is_symlink():
        raise ReleaseError(f"ZIP output already exists: {archive}")
    with zipfile.ZipFile(archive, "x", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as handle:
        for path in sorted((p for p in root.rglob("*") if p.is_file()), key=lambda p: p.relative_to(root).as_posix()):
            relative = f"{root.name}/{path.relative_to(root).as_posix()}"
            info = zipfile.ZipInfo(relative, date_time=(1980, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.external_attr = 0o100644 << 16
            handle.writestr(info, path.read_bytes(), compress_type=zipfile.ZIP_DEFLATED, compresslevel=9)


def verify_zip(root: Path, archive: Path, files: dict[str, Path]) -> None:
    if not archive.is_file() or archive.is_symlink():
        raise ReleaseError(f"ZIP missing or reparse point: {archive}")
    prefix = root.name + "/"
    expected = {prefix + relative for relative in files}
    with zipfile.ZipFile(archive) as handle:
        names: list[str] = []
        for info in handle.infolist():
            name = info.filename
            pure = PurePosixPath(name)
            if pure.is_absolute() or any(part in ("", ".", "..") for part in pure.parts) or "\\" in name or ":" in name:
                raise ReleaseError(f"ZIP path traversal refused: {name}")
            if info.is_dir():
                continue
            if name in names:
                raise ReleaseError(f"duplicate ZIP entry: {name}")
            names.append(name)
            if name not in expected:
                raise ReleaseError(f"undeclared ZIP entry: {name}")
            relative = name[len(prefix):]
            if hashlib.sha256(handle.read(info)).hexdigest().upper() != digest(files[relative]):
                raise ReleaseError(f"ZIP hash mismatch: {relative}")
        if set(names) != expected:
            raise ReleaseError(f"ZIP inventory mismatch: missing={sorted(expected-set(names))}")


def verify(
    root: Path,
    archive: Path | None = None,
    *,
    metadata_only: bool = False,
    defer_compatibility: bool = False,
) -> dict[str, Any]:
    files = inventory(root)
    verify_hashes(root, files)
    release = verify_metadata(root)
    verify_release_wrapper(files, str(release.get("version")))
    required_documents = REQUIRED_RELEASE_DOCUMENTS_BY_VERSION.get(
        str(release.get("version")), frozenset())
    missing_documents = required_documents - set(files)
    if missing_documents:
        raise ReleaseError(
            f"required release document missing: {sorted(missing_documents)}")
    if not metadata_only:
        verify_embedded_resource_closures(root, release)
    if archive is not None:
        verify_zip(root, archive, files)
    # Historical packages predate the canonical evidence contract.  Do not
    # manufacture a Full verdict for them; every package that declares the
    # canonical build evidence is required to close the compatibility graph.
    if "canonicalBuildLogSha256" in release and not defer_compatibility:
        compatibility = verify_release_root_compatibility(root, archive)
        if not compatibility.compatible:
            raise ReleaseError(
                "release compatibility overlap failed: "
                + "; ".join(compatibility.errors)
            )
    return {"status": "PASS", "version": release["version"], "sourceCommit": release["sourceCommit"], "files": len(files), "zipVerified": archive is not None}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("release")
    parser.add_argument("--zip")
    parser.add_argument("--create-zip")
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--metadata-only", action="store_true")
    parser.add_argument("--package-staging", action="store_true")
    args = parser.parse_args()
    root = Path(args.release).resolve()
    try:
        if args.package_staging:
            if args.zip or args.create_zip:
                raise ReleaseError(
                    "binary package staging verification does not create or accept release ZIPs")
            result = verify_package_staging(
                root, metadata_only=args.metadata_only)
        else:
            supplied_archive = Path(args.zip).resolve() if args.zip else None
            result = verify(
                root,
                archive=supplied_archive,
                metadata_only=args.metadata_only,
                defer_compatibility=bool(args.create_zip),
            )
        if args.create_zip:
            create_zip(root, Path(args.create_zip).resolve())
            verify_zip(root, Path(args.create_zip).resolve(), inventory(root))
            compatibility = verify_release_root_compatibility(
                root, Path(args.create_zip).resolve())
            if not compatibility.compatible:
                raise ReleaseError(
                    "sealed ZIP compatibility failed: "
                    + "; ".join(compatibility.errors)
                )
            result["zipVerified"] = True
            result["compatibilityVerdict"] = compatibility.verdict
        if args.zip:
            result["zipVerified"] = True
    except subprocess.TimeoutExpired:
        print("FAIL: release verification subprocess timed out", file=sys.stderr)
        return 2
    except (ReleaseError, OSError, zipfile.BadZipFile) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(result, sort_keys=True) if args.json else f"PASS version={result['version']} files={result['files']} zip={result['zipVerified']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
