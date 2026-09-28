import json
import shutil
import sys
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "exchange"))
import publish_candidate  # noqa: E402
import validate_bundle  # noqa: E402


def _candidate(tmp_path: Path) -> Path:
    root = tmp_path / "candidate"
    root.mkdir()
    payload = root / "release-candidate.json"
    payload.write_text(json.dumps({
        "$schema": "urn:actorwright:exchange:v1:release-candidate",
        "version": 1, "productVersion": "1.0.0-preview.254",
        "sourceCommit": "C" * 40, "sourceTag": "v1.0.0-preview.254",
        "capabilitiesSha256": "D" * 64, "packageSha256": "E" * 64,
        "sbomSha256": "F" * 64, "testSummarySha256": "1" * 64,
        "runtimeAuthority": False, "visualAuthority": False,
    }, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")
    import hashlib
    raw = payload.read_bytes()
    (root / "bundle-manifest.json").write_text(json.dumps({
        "$schema": "urn:actorwright:exchange:v1:bundle-manifest", "version": 1,
        "kind": "release-candidate", "payload": "release-candidate.json",
        "files": [{"path": "release-candidate.json", "size": len(raw),
                   "sha256": hashlib.sha256(raw).hexdigest().upper()}],
    }, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")
    return root


def _promotion(tmp_path: Path) -> Path:
    root = tmp_path / "promotion"
    root.mkdir()
    payload = root / "promotion.json"
    payload.write_text(json.dumps({
        "$schema": "urn:actorwright:exchange:v1:promotion",
        "version": 1, "createdUtc": "2026-09-05T12:00:00Z",
        "candidateManifestSha256": "A" * 64,
        "decision": "APPROVED", "decidedBy": "Synthetic reviewer",
    }, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")
    import hashlib
    raw = payload.read_bytes()
    (root / "bundle-manifest.json").write_text(json.dumps({
        "$schema": "urn:actorwright:exchange:v1:bundle-manifest", "version": 1,
        "kind": "promotion", "payload": "promotion.json",
        "files": [{"path": "promotion.json", "size": len(raw),
                   "sha256": hashlib.sha256(raw).hexdigest().upper()}],
    }, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")
    return root


def test_raced_destination_preserves_foreign_sentinel(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    source = _candidate(tmp_path)
    output = tmp_path / "published"
    original_rename = publish_candidate.os.rename

    def race(src: str | bytes, dst: str | bytes) -> None:
        foreign = Path(dst)
        foreign.mkdir()
        (foreign / "sentinel.txt").write_text("foreign", encoding="utf-8")
        original_rename(src, dst)

    monkeypatch.setattr(publish_candidate.os, "rename", race)
    with pytest.raises(publish_candidate.PublicationError):
        publish_candidate.publish(source, output)
    assert (output / "sentinel.txt").read_text(encoding="utf-8") == "foreign"
    assert not [p for p in tmp_path.iterdir() if p.name.startswith(".published.staging-")]


def test_empty_raced_destination_is_not_replaced(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    source = _candidate(tmp_path)
    output = tmp_path / "published-empty-race"
    original_atomic = validate_bundle._atomic_noreplace

    def race(stage, final):
        Path(final).mkdir()
        original_atomic(stage, final)

    monkeypatch.setattr(validate_bundle, "_atomic_noreplace", race)
    with pytest.raises(publish_candidate.PublicationError):
        publish_candidate.publish(source, output)
    assert output.is_dir()
    assert list(output.iterdir()) == []
    assert not [p for p in tmp_path.iterdir() if p.name.startswith(".published-empty-race.staging-")]


def test_late_failure_removes_only_owned_staging(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    source = _candidate(tmp_path)
    output = tmp_path / "published"
    foreign = tmp_path / "foreign"
    foreign.mkdir()
    (foreign / "sentinel.txt").write_text("keep", encoding="utf-8")

    original_validate = publish_candidate.validate
    calls = 0

    def fail_validation(root, kind):
        nonlocal calls
        calls += 1
        result = original_validate(root, kind)
        if calls == 2:
            assert (Path(root) / "bundle-manifest.json").is_file()
            raise publish_candidate.PublicationError("late validation failure")
        return result

    monkeypatch.setattr(publish_candidate, "validate", fail_validation)
    with pytest.raises(publish_candidate.PublicationError):
        publish_candidate.publish(source, output)
    assert not output.exists()
    assert (foreign / "sentinel.txt").read_text(encoding="utf-8") == "keep"
    assert not [p for p in tmp_path.iterdir() if p.name.startswith(".published.staging-")]


def test_substituted_stage_is_not_published_or_deleted(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    source = _candidate(tmp_path)
    output = tmp_path / "published-substituted"
    original_copy = publish_candidate._copy_exact
    handles = []

    def substitute(source_root, handle, relative_paths):
        original_copy(source_root, handle, relative_paths)
        shutil.rmtree(handle.path)
        handle.path.mkdir()
        handles.append(handle)

    monkeypatch.setattr(publish_candidate, "_copy_exact", substitute)
    with pytest.raises(publish_candidate.PublicationError):
        publish_candidate.publish(source, output)
    assert not output.exists()
    assert len(handles) == 1
    assert handles[0].path.is_dir()
    assert list(handles[0].path.iterdir()) == []


def test_occupied_destination_is_not_replaced(tmp_path: Path) -> None:
    source = _candidate(tmp_path)
    output = tmp_path / "published"
    output.mkdir()
    (output / "sentinel.txt").write_text("keep", encoding="utf-8")
    with pytest.raises(publish_candidate.PublicationError):
        publish_candidate.publish(source, output)
    assert (output / "sentinel.txt").read_text(encoding="utf-8") == "keep"

    promotion_source = _promotion(tmp_path)
    promotion_output = tmp_path / "published-promotion"
    promotion_output.mkdir()
    (promotion_output / "sentinel.txt").write_text("keep", encoding="utf-8")
    with pytest.raises(publish_candidate.PublicationError):
        publish_candidate.publish(
            promotion_source, promotion_output, expected_kind="promotion")
    assert (promotion_output / "sentinel.txt").read_text(
        encoding="utf-8") == "keep"
