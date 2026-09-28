import hashlib
import json
import shutil
import subprocess
from pathlib import Path

from tools.verification.compatibility_firewall import load_baseline


ROOT = Path(__file__).resolve().parents[2]
SCHEMA_RELATIVE = Path(
    "contracts/compatibility/v1/consumer-stage-report.schema.json"
)
BASELINE_RELATIVE = Path("tests/fixtures/compatibility-firewall/publicSynthetic")
BASELINE_REGISTRY_RELATIVE = Path("tools/manifests/compatibility-baselines.json")
SYNTHETIC_SCHEMA_RELATIVE = Path(
    "contracts/compatibility/v2/compatibility-snapshot.schema.json"
)
APPROVED_SCHEMA_SHA256 = (
    "67CE223DAD7C277AE6AFB3BA19EE54E546DE654DC16C5C82774492A799EBB937"
)


def _git(root: Path, *args: str) -> None:
    result = subprocess.run(
        ["git", *args], cwd=root, capture_output=True, text=True, check=False
    )
    assert result.returncode == 0, result.stdout + result.stderr


def _committed_bytes(relative: Path) -> bytes:
    return subprocess.check_output(
        [
            "git",
            "-c",
            f"safe.directory={ROOT.as_posix()}",
            "show",
            f"HEAD:{relative.as_posix()}",
        ],
        cwd=ROOT,
    )


def test_consumer_stage_schema_keeps_approved_bytes_in_windows_checkout(
    tmp_path: Path,
) -> None:
    source = tmp_path / "source"
    checkout = tmp_path / "checkout"
    source.mkdir()
    shutil.copyfile(ROOT / ".gitattributes", source / ".gitattributes")
    schema = source / SCHEMA_RELATIVE
    schema.parent.mkdir(parents=True)
    shutil.copyfile(ROOT / SCHEMA_RELATIVE, schema)

    _git(source, "init", "--quiet")
    _git(source, "config", "core.autocrlf", "false")
    _git(source, "config", "user.name", "Actorwright schema test")
    _git(source, "config", "user.email", "schema-test@example.invalid")
    _git(source, "add", "--", ".gitattributes", SCHEMA_RELATIVE.as_posix())
    _git(source, "commit", "--quiet", "-m", "schema checkout fixture")

    _git(
        tmp_path,
        "-c",
        "core.autocrlf=true",
        "clone",
        "--quiet",
        "--no-hardlinks",
        str(source),
        str(checkout),
    )
    checked_out_schema = (checkout / SCHEMA_RELATIVE).read_bytes()

    assert hashlib.sha256(checked_out_schema).hexdigest().upper() == APPROVED_SCHEMA_SHA256
    assert b"\r\n" not in checked_out_schema


def test_public_synthetic_baseline_and_schema_authenticate_in_windows_checkout(
    tmp_path: Path,
) -> None:
    source = tmp_path / "source"
    checkout = tmp_path / "checkout"
    source.mkdir()
    shutil.copyfile(ROOT / ".gitattributes", source / ".gitattributes")
    snapshot_path = BASELINE_RELATIVE / "manifest.json"
    snapshot = json.loads(_committed_bytes(snapshot_path))
    for relative in (
        BASELINE_REGISTRY_RELATIVE,
        SYNTHETIC_SCHEMA_RELATIVE,
        snapshot_path,
        *(BASELINE_RELATIVE / member["path"] for member in snapshot["members"]),
    ):
        target = source / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(_committed_bytes(relative))

    _git(source, "init", "--quiet")
    _git(source, "config", "core.autocrlf", "false")
    _git(source, "config", "user.name", "Actorwright baseline test")
    _git(source, "config", "user.email", "baseline-test@example.invalid")
    _git(
        source,
        "add",
        "--",
        ".gitattributes",
        BASELINE_REGISTRY_RELATIVE.as_posix(),
        SYNTHETIC_SCHEMA_RELATIVE.as_posix(),
        BASELINE_RELATIVE.as_posix(),
    )
    _git(source, "commit", "--quiet", "-m", "baseline checkout fixture")

    _git(
        tmp_path,
        "-c",
        "core.autocrlf=true",
        "clone",
        "--quiet",
        "--no-hardlinks",
        str(source),
        str(checkout),
    )

    baseline = load_baseline(
        "publicSynthetic",
        registry_path=checkout / BASELINE_REGISTRY_RELATIVE,
        bundle_root=checkout / BASELINE_RELATIVE,
    )

    checked_out_schema = (checkout / SYNTHETIC_SCHEMA_RELATIVE).read_bytes()
    assert checked_out_schema == _committed_bytes(SYNTHETIC_SCHEMA_RELATIVE)
    assert b"\r\n" not in checked_out_schema
    assert baseline.baseline_id == "publicSynthetic"
    assert baseline.snapshot["formatVersion"] == 2
    assert len(baseline.member_paths) == 3
    for source_relative, checked_out in zip(
        (BASELINE_RELATIVE / member["path"] for member in snapshot["members"]),
        baseline.member_paths,
        strict=True,
    ):
        assert checked_out.read_bytes() == _committed_bytes(source_relative)
