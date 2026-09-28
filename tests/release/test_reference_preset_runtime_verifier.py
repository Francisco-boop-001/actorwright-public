from importlib.util import module_from_spec, spec_from_file_location
from pathlib import Path

import pytest


SCRIPT = Path(__file__).resolve().parents[2] / "tools" / "dependencies" / "verify_reference_preset_runtime.py"
SPEC = spec_from_file_location("reference_preset_runtime_verifier", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
verifier = module_from_spec(SPEC)
SPEC.loader.exec_module(verifier)


def test_repository_root_is_derived_from_the_verifier_location():
    assert verifier.WORKSPACE == SCRIPT.resolve().parents[2]
    assert verifier.PROJECT == verifier.WORKSPACE


def test_runtime_root_admission_accepts_checkout_directory(tmp_path, monkeypatch):
    workspace = tmp_path / "checkout"
    runtime = workspace / "runtime"
    runtime.mkdir(parents=True)
    monkeypatch.setattr(verifier, "WORKSPACE", workspace)

    assert verifier.resolve_runtime_root(runtime) == runtime.resolve()


def test_runtime_root_admission_rejects_outside_directory(tmp_path, monkeypatch):
    workspace = tmp_path / "checkout"
    outside = tmp_path / "outside"
    workspace.mkdir()
    outside.mkdir()
    monkeypatch.setattr(verifier, "WORKSPACE", workspace)

    with pytest.raises(SystemExit, match="runtime root outside, absent, or reparse"):
        verifier.resolve_runtime_root(outside)


def test_runtime_root_admission_rejects_reparse_ancestor(tmp_path, monkeypatch):
    workspace = tmp_path / "checkout"
    target = workspace / "real"
    runtime = target / "runtime"
    runtime.mkdir(parents=True)
    alias = workspace / "alias"
    try:
        alias.symlink_to(target, target_is_directory=True)
    except (OSError, NotImplementedError) as error:
        pytest.skip(f"directory symlink unavailable: {error}")
    monkeypatch.setattr(verifier, "WORKSPACE", workspace)

    with pytest.raises(SystemExit, match="runtime root outside, absent, or reparse"):
        verifier.resolve_runtime_root(alias / "runtime")
