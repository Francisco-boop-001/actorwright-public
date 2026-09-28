from __future__ import annotations

from pathlib import Path
import json
import os
import subprocess
import tempfile

import pytest

from tools.architecture.validate_repository_independence import (
    FORBIDDEN_DEPENDENCIES, VOICE_EXCLUSION_POLICY, validate_repository,
)


ROOT = Path(__file__).resolve().parents[2]


def write_valid_repository(root: Path) -> None:
    (root / "src/Actorwright.Cli").mkdir(parents=True)
    (root / "src/Actorwright.Desktop").mkdir(parents=True)
    (root / "src/NpcManager.Domain").mkdir(parents=True)
    (root / "src/NpcManager.Rendering").mkdir(parents=True)
    (root / "runtime/rendering").mkdir(parents=True)
    (root / "tools/build").mkdir(parents=True)
    (root / ".github/workflows").mkdir(parents=True)
    (root / "Actorwright.sln").write_text("Microsoft Visual Studio Solution File\n", encoding="utf-8")
    (root / "nuget.config").write_text(
        '<configuration><packageSources><add key="nuget.org" '
        'value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>',
        encoding="utf-8",
    )
    (root / "src/Actorwright.Cli/Actorwright.Cli.csproj").write_text(
        '<Project><PropertyGroup><AssemblyName>actorwright</AssemblyName></PropertyGroup>'
        '<ItemGroup><ProjectReference Include="../NpcManager.Rendering/NpcManager.Rendering.csproj" />'
        '</ItemGroup></Project>',
        encoding="utf-8",
    )
    (root / "src/Actorwright.Desktop/Actorwright.Desktop.csproj").write_text(
        '<Project><PropertyGroup><AssemblyName>Actorwright.Desktop</AssemblyName></PropertyGroup>'
        '</Project>',
        encoding="utf-8",
    )
    (root / "src/NpcManager.Domain/ActorwrightWorkspace.cs").write_text(
        'namespace NpcManager.Domain; class ActorwrightWorkspace { '
        'const string Protected = @"F:\\ExampleGame";\n' + VOICE_EXCLUSION_POLICY + '\n}\n',
        encoding="utf-8",
    )
    scripts = [
        "export_facegeom_nif.py",
        "export_preview_nif.py",
        "hair_zap.py",
        "nif_geometry_readback.py",
        "render_npc_preview_bundle.py",
        "render_preview_scene.py",
    ]
    for script in scripts:
        (root / "runtime/rendering" / script).write_text("# fixture\n", encoding="utf-8")
    embedded = "".join(
        f'<EmbeddedResource Include="../../runtime/rendering/{script}" />' for script in scripts
    )
    (root / "src/NpcManager.Rendering/NpcManager.Rendering.csproj").write_text(
        f"<Project><ItemGroup>{embedded}</ItemGroup></Project>", encoding="utf-8"
    )
    (root / "tools/build/build.ps1").write_text(
        "$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\\..')).Path\n"
        "$dotnet = Get-Command dotnet -ErrorAction Stop\n",
        encoding="utf-8",
    )
    (root / ".github/workflows/build.yml").write_text(
        "name: build\nsteps:\n  - run: pwsh ./tools/build/build.ps1\n", encoding="utf-8"
    )
    (root / "src/Actorwright.Desktop/MainWindow.xaml").write_text(
        '<Window Title="Actorwright" />', encoding="utf-8"
    )


def test_valid_standalone_repository_passes(tmp_path: Path) -> None:
    write_valid_repository(tmp_path)
    assert validate_repository(tmp_path) == []
    policy = tmp_path / "src/NpcManager.Domain/ActorwrightWorkspace.cs"
    admitted = policy.read_text(encoding="utf-8")
    policy.write_text(admitted + f'\n// extra dependency: {FORBIDDEN_DEPENDENCIES[0]}\n', encoding="utf-8")
    assert any("forbidden dependency" in error for error in validate_repository(tmp_path))
    for replacement in ("", "    public static bool IsVoiceExcludedPath(WorkspacePath path) => System.IO.File.Exists(path.Value);"):
        policy.write_text(admitted.replace(VOICE_EXCLUSION_POLICY, replacement), encoding="utf-8")
        assert "mandatory pure voice exclusion policy is absent or changed" in validate_repository(tmp_path)
    policy.write_text(admitted, encoding="utf-8")
    elsewhere = tmp_path / "src/NpcManager.Domain/UnexpectedPolicy.cs"
    elsewhere.write_text(VOICE_EXCLUSION_POLICY, encoding="utf-8")
    assert any("forbidden dependency" in error for error in validate_repository(tmp_path))


@pytest.mark.parametrize("disabled", ["true", " TRUE "])
def test_disabled_official_nuget_source_is_rejected(tmp_path: Path, disabled: str) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "nuget.config").write_text(
        '<configuration><packageSources><add key="nuget.org" '
        'value="https://api.nuget.org/v3/index.json" /></packageSources>'
        f'<disabledPackageSources><add key="nuget.org" value="{disabled}" />'
        '</disabledPackageSources></configuration>',
        encoding="utf-8",
    )
    assert "nuget.config must enable the official nuget.org v3 source" in validate_repository(tmp_path)


@pytest.mark.parametrize("source", ["http://127.0.0.1:18765/index.json", "artifacts/packages"])
def test_nonofficial_nuget_package_source_is_rejected(tmp_path: Path, source: str) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "nuget.config").write_text(
        '<configuration><packageSources><add key="nuget.org" '
        'value="https://api.nuget.org/v3/index.json" />'
        f'<add key="temporary" value="{source}" /></packageSources></configuration>',
        encoding="utf-8",
    )
    assert any(
        error.startswith("nuget.config must not enable non-official package sources:")
        for error in validate_repository(tmp_path)
    )


def test_nonofficial_nuget_audit_source_is_rejected(tmp_path: Path) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "nuget.config").write_text(
        '<configuration><packageSources><add key="nuget.org" '
        'value="https://api.nuget.org/v3/index.json" /></packageSources>'
        '<auditSources><add key="temporary" '
        'value="http://127.0.0.1:18765/index.json" /></auditSources></configuration>',
        encoding="utf-8",
    )
    assert any(
        error.startswith("nuget.config must not enable non-official audit sources:")
        for error in validate_repository(tmp_path)
    )


def test_outside_project_reference_is_rejected(tmp_path: Path) -> None:
    write_valid_repository(tmp_path)
    project = tmp_path / "src/Actorwright.Cli/Actorwright.Cli.csproj"
    project.write_text(
        '<Project><ItemGroup><ProjectReference Include="../../../../ExampleWorkspace/x.csproj" />'
        '</ItemGroup></Project>',
        encoding="utf-8",
    )
    errors = validate_repository(tmp_path)
    assert any("outside repository" in error for error in errors)


def test_missing_static_project_input_is_rejected(tmp_path: Path) -> None:
    write_valid_repository(tmp_path)
    project = tmp_path / "src/Actorwright.Cli/Actorwright.Cli.csproj"
    project.write_text(
        '<Project><ItemGroup><EmbeddedResource Include="../../runtime/missing.bin" />'
        '</ItemGroup></Project>',
        encoding="utf-8",
    )
    errors = validate_repository(tmp_path)
    assert any("missing static project input" in error for error in errors)


def test_workspace_bound_build_and_physical_renderer_content_are_rejected(tmp_path: Path) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "tools/build/build.ps1").write_text(
        "$sdk = 'K:\\ExampleWorkspace\\tools\\external\\dotnet.exe'\n", encoding="utf-8"
    )
    rendering = tmp_path / "src/NpcManager.Rendering/NpcManager.Rendering.csproj"
    rendering.write_text(
        '<Project><ItemGroup><Content Include="../../../../tools/rendering/render_preview_scene.py" />'
        '</ItemGroup></Project>',
        encoding="utf-8",
    )
    errors = validate_repository(tmp_path)
    assert any("forbidden dependency" in error for error in errors)
    assert any("renderer scripts must be embedded" in error for error in errors)


def test_public_branding_contract_is_enforced(tmp_path: Path) -> None:
    write_valid_repository(tmp_path)
    cli = tmp_path / "src/Actorwright.Cli/Actorwright.Cli.csproj"
    cli.write_text(
        '<Project><PropertyGroup><AssemblyName>npcm</AssemblyName></PropertyGroup></Project>',
        encoding="utf-8",
    )
    (tmp_path / "src/Actorwright.Desktop/MainWindow.xaml").write_text(
        '<Window Title="NPC Manager" />', encoding="utf-8"
    )
    errors = validate_repository(tmp_path)
    assert any("CLI assembly" in error for error in errors)
    assert any("desktop title" in error for error in errors)


@pytest.mark.parametrize("reference", [r"K:\ExampleWorkspace\x", r"K:\\ExampleWorkspace\\x", "k:/exampleworkspace/x"])
def test_tests_tree_workspace_references_must_be_allowlisted(tmp_path: Path, reference: str) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "tests").mkdir()
    (tmp_path / "tests/Some.cs").write_text(reference, encoding="utf-8")
    assert "unallowlisted workspace fixture reference: tests/Some.cs (1 occurrences; baseline 0)" in validate_repository(tmp_path)


def test_tests_root_junction_is_refused_before_traversal() -> None:
    # The workspace volume may not support junctions; use the OS temporary volume.
    with tempfile.TemporaryDirectory(prefix="actorwright-task6-junction-") as temporary:
        repository = Path(temporary) / "repository"
        write_valid_repository(repository)
        source = Path(temporary) / "owned-source"
        source.mkdir()
        (source / "Sentinel.cs").write_text("K:/ExampleWorkspace/sentinel", encoding="utf-8")
        (source / "Sentinel.csproj").write_text("not project XML", encoding="utf-8")
        junction = repository / "tests"
        if os.name == "nt":
            result = subprocess.run(
                ["cmd", "/c", "mklink", "/J", str(junction), str(source)],
                capture_output=True, text=True, check=False,
            )
            assert result.returncode == 0, result.stdout + result.stderr
        else:
            junction.symlink_to(source, target_is_directory=True)
        try:
            assert validate_repository(repository) == [
                "workspace fixture root must not be a symlink or reparse point: tests"
            ]
        finally:
            if os.name == "nt":
                junction.rmdir()  # Remove only this junction, never its target.
            else:
                junction.unlink()


def test_retained_fixture_references_warn_with_exact_current_count(tmp_path: Path, capsys: pytest.CaptureFixture[str]) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "tests").mkdir()
    (tmp_path / "tests/Some.cs").write_text("K:/ExampleWorkspace/x\n" * 2, encoding="utf-8")
    (tmp_path / "tools/architecture").mkdir()
    (tmp_path / "tools/architecture/fixture-path-allowlist.json").write_text(
        json.dumps([{"path": "tests/Some.cs", "occurrences": 2}]), encoding="utf-8"
    )
    assert validate_repository(tmp_path) == []
    assert capsys.readouterr().out == "WARNING retained workspace fixture references: 2 occurrences in 1 files (baseline 2 occurrences in 1 files); source scan only\n"


def test_stale_fixture_allowlist_entry_is_rejected(tmp_path: Path) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "tests").mkdir()
    (tmp_path / "tests/Some.cs").write_text("K:/ExampleWorkspace/x\n", encoding="utf-8")
    (tmp_path / "tools/architecture").mkdir()
    (tmp_path / "tools/architecture/fixture-path-allowlist.json").write_text(
        json.dumps([{"path": "tests/Some.cs", "occurrences": 2}]), encoding="utf-8"
    )
    assert "stale workspace fixture allowlist: tests/Some.cs (1 occurrences; baseline 2)" in validate_repository(tmp_path)


def test_non_csharp_test_fixture_reference_must_be_allowlisted(tmp_path: Path) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "tests").mkdir()
    (tmp_path / "tests/fixture.json").write_text(
        '{"source":"K:/ExampleWorkspace/x"}', encoding="utf-8"
    )
    assert "unallowlisted workspace fixture reference: tests/fixture.json (1 occurrences; baseline 0)" in validate_repository(tmp_path)


def test_release_tool_dependency_is_rejected(tmp_path: Path) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "tools/release").mkdir(parents=True)
    (tmp_path / "tools/release/sentinel.py").write_text(
        "workspace = r'K:\\\\ExampleExchange'\n", encoding="utf-8"
    )
    errors = validate_repository(tmp_path)
    assert any("forbidden dependency" in error and "tools\\release\\sentinel.py" in error for error in errors)


def test_extra_reference_in_existing_allowlisted_file_is_rejected(tmp_path: Path) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "tests").mkdir()
    (tmp_path / "tests/Some.cs").write_text("K:/ExampleWorkspace/x\n" * 2, encoding="utf-8")
    (tmp_path / "tools/architecture").mkdir()
    (tmp_path / "tools/architecture/fixture-path-allowlist.json").write_text(
        json.dumps([{"path": "tests/Some.cs", "occurrences": 1}]), encoding="utf-8"
    )
    assert "unallowlisted workspace fixture reference: tests/Some.cs (2 occurrences; baseline 1)" in validate_repository(tmp_path)


@pytest.mark.parametrize("baseline", ["[", '["tests/Some.cs"]', '[{"path":"tests/Some.cs","occurrences":-1}]'])
def test_invalid_fixture_baseline_is_rejected(tmp_path: Path, baseline: str) -> None:
    write_valid_repository(tmp_path)
    (tmp_path / "tools/architecture").mkdir()
    (tmp_path / "tools/architecture/fixture-path-allowlist.json").write_text(baseline, encoding="utf-8")
    assert any("invalid workspace fixture baseline" in error for error in validate_repository(tmp_path))


def test_gitignore_excludes_runtime_residue_and_external_payloads() -> None:
    ignored = [".actorwright/state.json", "t/scratch.txt", "%SystemDrive%/scratch.txt", ".superpowers/report.md", "resources/preset.jslot", "tools/external/blender/blender.exe"]
    retained = ["tools/external/README.md", "tools/manifests/external-tools.json"]
    result = subprocess.run(
        ["git", "check-ignore", "--no-index", "--stdin", "-z"], cwd=ROOT,
        input=("\0".join(ignored + retained) + "\0").encode(), capture_output=True, check=False,
    )
    assert result.returncode in (0, 1), result.stderr
    assert result.stdout.decode().rstrip("\0").split("\0") == ignored
