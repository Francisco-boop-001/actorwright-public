from __future__ import annotations

import hashlib
import json
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def _sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def test_manifest_pins_the_verified_upstream_renderer_payloads() -> None:
    manifest = json.loads(
        (ROOT / "tools/manifests/external-tools.json").read_text(encoding="utf-8")
    )
    blender, pynifly, texconv = manifest["tools"]
    assert blender["downloadArtifactUrl"] == (
        "https://download.blender.org/release/Blender4.5/"
        "blender-4.5.1-windows-x64.zip"
    )
    assert blender["archiveSha256"] == (
        "AE2EADB2656D710FFD6CA74A899519FCCB800C7A18E6C05B6F39627E48D17AED"
    )
    assert blender["archiveLength"] == 399708581
    assert pynifly["downloadArtifactUrl"] == (
        "https://github.com/BadDogSkyrim/PyNifly/releases/download/"
        "V27.4.0/io_scene_nifly.zip"
    )
    assert pynifly["archiveSha256"] == (
        "296427A5E30F151223700298A7875DF2C0A61B5F422818FC7002B4F4D8DDAEE7"
    )
    assert pynifly["archiveLength"] == 32006190
    assert texconv["downloadArtifactUrl"] == (
        "https://github.com/microsoft/DirectXTex/releases/download/"
        "may2026/texconv.exe"
    )
    assert texconv["downloadArtifactSha256"] == (
        "DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06"
    )
    assert texconv["downloadArtifactLength"] == 966480


def test_installed_renderer_files_match_committed_inventory() -> None:
    manifest = json.loads(
        (ROOT / "tools/manifests/external-tools.json").read_text(encoding="utf-8")
    )
    blender, pynifly, texconv = manifest["tools"]
    for tool in (blender, texconv):
        executable = ROOT / tool["verifiedArtifact"]["path"]
        assert executable.is_file()
        assert executable.stat().st_size == tool["verifiedArtifact"]["length"]
        assert _sha256(executable) == tool["verifiedArtifact"]["sha256"]

    inventory = json.loads(
        (ROOT / "runtime/rendering/npc-preview-profile-manifest.json").read_text(
            encoding="utf-8"
        )
    )
    profile_root = ROOT / pynifly["installPath"]
    addon_root = profile_root / "scripts/addons/io_scene_nifly"
    expected: set[str] = set()
    for record in inventory["files"]:
        prefix = "scripts/addons/io_scene_nifly/"
        assert record["path"].startswith(prefix)
        relative = record["path"][len(prefix):]
        expected.add(relative)
        path = addon_root / Path(relative)
        assert path.is_file(), relative
        assert path.stat().st_size == record["length"], relative
        assert _sha256(path) == record["sha256"], relative

    actual = {
        path.relative_to(addon_root).as_posix()
        for path in addon_root.rglob("*")
        if path.is_file()
        and "__pycache__" not in path.parts
        and path.suffix.lower() != ".pyc"
    }
    assert actual == expected
    assert (profile_root / "config").is_dir()
    assert (profile_root / "data").is_dir()
