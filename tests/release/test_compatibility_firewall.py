import base64
import hashlib
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import uuid
import zipfile
from concurrent.futures import ThreadPoolExecutor
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from pathlib import Path
from types import SimpleNamespace

import pytest

import tools.verification.compatibility_firewall as firewall
import tools.release.verify_release as release_verifier
from tools.verification.compatibility_firewall import (
    CliTarget,
    FirewallError,
    canonical_json_bytes,
    capture_cli_contract,
    find_capture_disagreement,
    load_baseline,
    run_cli,
    validate_snapshot,
)


def test_canonical_build_runs_source_firewall_after_compilation_and_selectors() -> None:
    script = (Path(__file__).resolve().parents[2] / "tools" / "build" / "build.ps1").read_text(
        encoding="utf-8"
    )
    continued = script.replace("`", "")
    compiled = script.index("& $dotnet build $solution --configuration $Configuration --no-restore")
    selectors = script.index("& $python $selectorLauncher")
    firewall_call = script.index("& $firewallScript verify")
    later_validators = script.index("foreach ($validator in @(")
    assert compiled < selectors < firewall_call < later_validators
    assert "if ($Configuration -eq 'Release') {" in script[selectors:firewall_call]
    assert "$firewallReportRoot = Join-Path $projectRoot 'artifacts\\test-work'" in script
    assert re.search(
        r"\$sourceCli\s*=\s*Join-Path \$projectRoot\s+"
        r"'src\\NpcManager\.Cli\\bin\\Release\\net10\.0\\actorwright\.exe'",
        continued,
    )
    assert re.search(
        r"\$firewallReport\s*=\s*Join-Path \$firewallReportRoot\s+"
        r'\(\s*"compatibility-firewall-source-"\s*\+\s*\[Guid\]::NewGuid\(\)'
        r"\.ToString\('N'\)\s*\+\s*'\.json'\)",
        continued,
    )
    assert re.search(
        r"& \$firewallScript verify\s+-Tier Source\s+"
        r"-Baseline publicSynthetic\s+-SourceCli \$sourceCli\s+"
        r"-Output \$firewallReport",
        continued,
    )
    firewall_section = script[firewall_call:later_validators]
    assert re.search(
        r"if \(\$LASTEXITCODE -ne 0\) \{\s*throw "
        r'"Source compatibility firewall failed \(exit \$LASTEXITCODE\)"',
        firewall_section,
    )
    assert "Assert-SourceFirewallReport -Path $firewallReport" in firewall_section
    assert "capture" not in firewall_section.lower()
    assert not re.search(r"install|elevat|promot|tag", firewall_section, re.IGNORECASE)


def test_release_package_runs_package_firewall_after_staging_evidence() -> None:
    script = (Path(__file__).resolve().parents[2] / "tools/build/package.ps1").read_text(
        encoding="utf-8")
    continued = script.replace("`", "")
    staging = script.index("$verificationOutput = & $python")
    evidence = script.index("'tools\\build\\write_package_verification_evidence.ps1'")
    firewall_call = script.index("& $firewallScript verify")
    final_pass = script.index('Write-Output "Actorwright package: PASS')
    assert staging < evidence < firewall_call < final_pass
    assert "if ($Configuration -eq 'Release') {" in script[evidence:firewall_call]
    assert re.search(
        r"\$sourceCli\s*=\s*Join-Path \$projectRoot\s+"
        r"'src\\NpcManager\.Cli\\bin\\Release\\net10\.0\\actorwright\.exe'",
        continued,
    )
    assert "$firewallReportRoot = Join-Path $projectRoot 'artifacts\\test-work'" in script
    assert re.search(
        r"\$firewallReport\s*=\s*Join-Path \$firewallReportRoot\s+"
        r'\(\s*"compatibility-firewall-package-"\s*\+\s*\[Guid\]::NewGuid\(\)'
        r"\.ToString\('N'\)\s*\+\s*'\.json'\)", continued,
    )
    section = script[firewall_call:final_pass]
    assert re.search(
        r"& \$firewallScript verify\s+-Tier Package\s+"
        r"-Baseline publicSynthetic\s+-SourceCli \$sourceCli\s+"
        r"-ReleaseRoot \$OutputRoot\s+-Output \$firewallReport",
        section.replace("`", ""),
    )
    assert 'throw "Package compatibility firewall failed (exit $LASTEXITCODE)"' in section
    assert "Assert-PackageFirewallReport -Path $firewallReport" in section
    assert not re.search(r"\b(?:capture|install|elevat|promot|tag|ReleaseZip)\b",
                         section, re.IGNORECASE)
    assert "-Tier Package" not in script[:script.index("if ($Configuration -eq 'Release') {", evidence)]


def test_package_firewall_wrapper_imports_repo_tools_from_non_root_cwd(
    tmp_path: Path,
) -> None:
    root = Path(__file__).resolve().parents[2]
    report = ROOT / "artifacts" / "test-work" / (
        f"package-import-probe-{uuid.uuid4().hex}.json"
    )
    environment = os.environ.copy()
    environment.pop("PYTHONPATH", None)
    fake_dotnet = tmp_path / "dotnet.exe"
    fake_dotnet.write_bytes(b"test-only SDK placeholder; this path is not executed")
    environment["ACTORWRIGHT_TEST_DOTNET"] = str(fake_dotnet)
    result = subprocess.run(
        ["pwsh", "-NoProfile", "-NonInteractive", "-File",
         str(root / "tools/verification/compatibility-firewall.ps1"),
         "verify", "-Tier", "Package", "-Baseline", "publicSynthetic",
         "-SourceCli", str(tmp_path / "missing.exe"),
         "-ReleaseRoot", str(tmp_path), "-Output", str(report)],
        cwd=tmp_path,
        env=environment,
        capture_output=True,
        text=True,
        check=False,
    )
    assert result.returncode == 2, result.stdout + result.stderr
    assert "source CLI must be the ordinary canonical Release CLI" in result.stderr
    assert not report.exists()


def test_debug_package_keeps_staging_verifier_without_claiming_package_compatibility() -> None:
    script = (Path(__file__).resolve().parents[2] / "tools/build/package.ps1").read_text(
        encoding="utf-8")
    assert "[ValidateSet('Debug', 'Release')]" in script
    assert script.index("$verificationOutput = & $python") < script.index(
        "if ($Configuration -eq 'Release') {", script.index("$verificationOutput = & $python"))
    release_branch = script.index("if ($Configuration -eq 'Release') {",
                                  script.index("$verificationOutput = & $python"))
    assert script.count("-Tier Package") == 1
    assert release_branch < script.index("-Tier Package") < script.index(
        'Write-Output "Actorwright package: PASS')
    assert "PACKAGE_COMPATIBLE" not in script


def test_package_firewall_report_guard_rejects_non_ordinary_or_empty_file(
    tmp_path: Path,
) -> None:
    script = (Path(__file__).resolve().parents[2] / "tools/build/package.ps1").read_text(
        encoding="utf-8")
    function = re.search(
        r"(?ms)^function Assert-PackageFirewallReport\(\[string\]\$Path\) \{.*?^\}",
        script,
    )
    assert function is not None
    assert "Get-Item -LiteralPath $Path" in function.group()
    assert "[IO.FileAttributes]::ReparsePoint" in function.group()

    def probe(path: Path) -> int:
        command = (
            "$ErrorActionPreference = 'Stop'\n" + function.group()
            + "\nAssert-PackageFirewallReport -Path $env:ACTORWRIGHT_REPORT_PROBE\n"
        )
        environment = os.environ.copy()
        environment["ACTORWRIGHT_REPORT_PROBE"] = str(path)
        result = subprocess.run(
            ["pwsh", "-NoProfile", "-NonInteractive", "-EncodedCommand",
             base64.b64encode(command.encode("utf-16le")).decode("ascii")],
            env=environment, capture_output=True, text=True, check=False,
        )
        return result.returncode

    report = tmp_path / "report.json"
    report.write_bytes(b"{}")
    assert probe(report) == 0
    report.write_bytes(b"")
    assert probe(report) != 0
    report.unlink()
    assert probe(report) != 0
    assert probe(tmp_path) != 0


def test_package_firewall_report_guard_rejects_reparse_alias(
    tmp_path: Path,
    require_ntfs_fixture,
    filesystem_fixture_unavailable,
) -> None:
    filesystem = require_ntfs_fixture(tmp_path, "package firewall report symlink")
    script = (Path(__file__).resolve().parents[2] / "tools/build/package.ps1").read_text(
        encoding="utf-8")
    function = re.search(
        r"(?ms)^function Assert-PackageFirewallReport\(\[string\]\$Path\) \{.*?^\}",
        script,
    )
    assert function is not None
    report = tmp_path / "report.json"
    report.write_bytes(b"{}")
    alias = tmp_path / "alias.json"
    try:
        alias.symlink_to(report)
    except (OSError, NotImplementedError) as error:
        filesystem_fixture_unavailable(
            tmp_path, filesystem, "package firewall report symlink", error
        )

    command = (
        "$ErrorActionPreference = 'Stop'\n"
        + function.group()
        + "\nAssert-PackageFirewallReport -Path $env:ACTORWRIGHT_REPORT_PROBE\n"
    )
    environment = os.environ.copy()
    environment["ACTORWRIGHT_REPORT_PROBE"] = str(alias)
    result = subprocess.run(
        ["pwsh", "-NoProfile", "-NonInteractive", "-EncodedCommand",
         base64.b64encode(command.encode("utf-16le")).decode("ascii")],
        env=environment, capture_output=True, text=True, check=False,
    )
    assert result.returncode != 0
    assert "not an ordinary file" in (result.stdout + result.stderr).lower()


def test_filesystem_fixture_failures_are_classified_by_filesystem(
    tmp_path: Path,
    filesystem_fixture_unavailable,
) -> None:
    error = OSError("symbolic links are disabled")
    with pytest.raises(pytest.fail.Exception, match=r"root=.*filesystem=NTFS.*reason="):
        filesystem_fixture_unavailable(tmp_path, "NTFS", "reparse-point", error)
    if os.environ.get("ACTORWRIGHT_REQUIRE_NTFS_FIXTURES") == "1":
        with pytest.raises(pytest.fail.Exception, match=r"root=.*filesystem=exFAT.*reason="):
            filesystem_fixture_unavailable(tmp_path, "exFAT", "reparse-point", error)
    else:
        with pytest.raises(pytest.skip.Exception, match=r"root=.*filesystem=exFAT.*reason="):
            filesystem_fixture_unavailable(tmp_path, "exFAT", "reparse-point", error)


def _source_firewall_report_probe():
    script = (Path(__file__).resolve().parents[2] / "tools" / "build" / "build.ps1").read_text(
        encoding="utf-8"
    )
    function = re.search(
        r"(?ms)^function Assert-SourceFirewallReport\(\[string\]\$Path\) \{.*?^\}",
        script,
    )
    assert function is not None
    assert "Get-Item -LiteralPath $Path" in function.group()
    assert "[IO.FileAttributes]::ReparsePoint" in function.group()

    def probe(path: Path) -> subprocess.CompletedProcess[str]:
        command = (
            "$ErrorActionPreference = 'Stop'\n"
            + function.group()
            + "\nAssert-SourceFirewallReport -Path $env:ACTORWRIGHT_REPORT_PROBE\n"
        )
        environment = os.environ.copy()
        environment["ACTORWRIGHT_REPORT_PROBE"] = str(path)
        result = subprocess.run(
            ["pwsh", "-NoProfile", "-NonInteractive", "-EncodedCommand",
             base64.b64encode(command.encode("utf-16le")).decode("ascii")],
            env=environment, capture_output=True, text=True, check=False,
        )
        return result

    return function, probe


def test_source_firewall_report_guard_requires_ordinary_nonempty_file(tmp_path: Path) -> None:
    function, probe = _source_firewall_report_probe()
    assert "Get-Item -LiteralPath $Path" in function.group()
    assert "[IO.FileAttributes]::ReparsePoint" in function.group()

    report = tmp_path / "report.json"
    report.write_bytes(b"{}")
    assert probe(report).returncode == 0
    report.write_bytes(b"")
    assert probe(report).returncode != 0
    report.unlink()
    assert probe(report).returncode != 0
    assert probe(tmp_path).returncode != 0


def test_source_firewall_report_guard_rejects_reparse_alias(
    tmp_path: Path,
    require_ntfs_fixture,
    filesystem_fixture_unavailable,
) -> None:
    filesystem = require_ntfs_fixture(tmp_path, "source firewall report symlink")
    _function, probe = _source_firewall_report_probe()
    report = tmp_path / "report.json"
    report.write_bytes(b"{}")
    alias = tmp_path / "alias.json"
    try:
        alias.symlink_to(report)
    except (OSError, NotImplementedError) as error:
        filesystem_fixture_unavailable(
            tmp_path, filesystem, "source firewall report symlink", error
        )
    result = probe(alias)
    assert result.returncode != 0
    assert "not an ordinary file" in (result.stdout + result.stderr).lower()


def _pin_node(
    pin_id: str,
    stage: str,
    value: str,
    *,
    path: Path | None = None,
    resolved: bool = True,
) -> "firewall.PinNode":
    return firewall.PinNode(
        pin_id=pin_id,
        stage=stage,
        value=value,
        evidence_path=str(path or Path("evidence") / f"{pin_id}.json"),
        byte_path=path,
        expected_length=path.stat().st_size if path else None,
        expected_sha256=(
            hashlib.sha256(path.read_bytes()).hexdigest().upper() if path else None
        ),
        resolved=resolved,
    )


def _pin_edge(
    pin_id: str,
    stage: str,
    producer: str,
    consumer: str,
    identity_class: str = "sha256",
) -> "firewall.PinEdge":
    return firewall.PinEdge(pin_id, stage, producer, consumer, identity_class)


def test_pin_graph_validates_bytes_before_edges(tmp_path: Path) -> None:
    package = tmp_path / "release.zip"
    package.write_bytes(b"sealed")
    producer = _pin_node("zip.actual", "Candidate", "A" * 64, path=package)
    producer = firewall.PinNode(
        **{**producer.__dict__, "expected_sha256": "0" * 64}
    )
    consumer = _pin_node("candidate.package", "Candidate", "B" * 64)

    result = firewall.validate_pin_graph(
        "Candidate",
        [producer, consumer],
        [_pin_edge("candidate-package", "Candidate", producer.pin_id, consumer.pin_id)],
    )

    assert result.verdict == "INCOMPATIBLE"
    assert result.pin_rows == ()
    assert result.errors[0].startswith("byte-sha256:")


@pytest.mark.parametrize("missing", ["producer", "consumer"])
def test_pin_graph_rejects_missing_edge_endpoint(missing: str) -> None:
    nodes = [_pin_node("producer", "Candidate", "A" * 64)]
    if missing == "producer":
        nodes = [_pin_node("consumer", "Candidate", "A" * 64)]

    result = firewall.validate_pin_graph(
        "Candidate", nodes,
        [_pin_edge("missing-endpoint", "Candidate", "producer", "consumer")],
    )

    assert result.verdict == "INCOMPATIBLE"
    assert result.pin_rows[0].status == "FAIL"
    assert result.pin_rows[0].producer_value
    assert result.pin_rows[0].consumer_value
    assert result.pin_rows[0].producer_evidence_path
    assert result.pin_rows[0].consumer_evidence_path


@pytest.mark.parametrize("case", ["self", "cycle"])
def test_pin_graph_rejects_self_hash_and_cycle(case: str) -> None:
    nodes = [_pin_node("a", "Candidate", "A" * 64), _pin_node("b", "Candidate", "A" * 64)]
    edges = (
        [_pin_edge("self", "Candidate", "a", "a")]
        if case == "self"
        else [
            _pin_edge("a-b", "Candidate", "a", "b"),
            _pin_edge("b-a", "Candidate", "b", "a"),
        ]
    )

    result = firewall.validate_pin_graph("Candidate", nodes, edges)

    assert result.verdict == "INCOMPATIBLE"
    assert any(("self-hash" if case == "self" else "cycle") in item for item in result.errors)


def test_pin_graph_rejects_earlier_node_reference_to_later_node() -> None:
    result = firewall.validate_pin_graph(
        "Admission",
        [
            _pin_node("promotion", "Admission", "A" * 64),
            _pin_node("activation", "Activation", "A" * 64),
        ],
        [_pin_edge("back-reference", "Admission", "activation", "promotion")],
    )

    assert result.verdict == "INCOMPATIBLE"
    assert any("later-node" in item for item in result.errors)


def test_pin_graph_rejects_partial_refresh_with_diagnostic_values() -> None:
    result = firewall.validate_pin_graph(
        "Candidate",
        [
            _pin_node("zip.actual", "Candidate", "A" * 64),
            _pin_node("candidate.package", "Candidate", "B" * 64),
        ],
        [_pin_edge("candidate-package", "Candidate", "zip.actual", "candidate.package")],
    )

    assert result.verdict == "INCOMPATIBLE"
    row = result.pin_rows[0]
    assert row.status == "FAIL"
    assert row.producer_value == "A" * 64
    assert row.consumer_value == "B" * 64
    assert row.producer_evidence_path.endswith("zip.actual.json")
    assert row.consumer_evidence_path.endswith("candidate.package.json")


def test_pin_graph_candidate_requires_resolved_disposable_tag(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    repository = tmp_path / "repository"
    repository.mkdir()
    subprocess.run(["git", "init"], cwd=repository, check=True, capture_output=True)
    monkeypatch.setenv("GIT_CONFIG_COUNT", "1")
    monkeypatch.setenv("GIT_CONFIG_KEY_0", "safe.directory")
    monkeypatch.setenv("GIT_CONFIG_VALUE_0", repository.as_posix())
    subprocess.run(["git", "config", "user.name", "Fixture"], cwd=repository, check=True)
    subprocess.run(["git", "config", "user.email", "fixture@example.invalid"], cwd=repository, check=True)
    (repository / "source.txt").write_text("source\n", encoding="utf-8")
    subprocess.run(["git", "add", "source.txt"], cwd=repository, check=True)
    subprocess.run(["git", "commit", "-m", "fixture"], cwd=repository, check=True, capture_output=True)
    tag = "v9.9.9-fixture"
    subprocess.run(["git", "tag", "-a", tag, "-m", "fixture"], cwd=repository, check=True)
    resolved = firewall.resolve_tag_pin_nodes(repository, tag)
    commit = next(node for node in resolved if node.pin_id == "source.tag.peeledCommit")
    tree = next(node for node in resolved if node.pin_id == "source.tag.peeledTree")
    graph = firewall.validate_pin_graph(
        "Candidate",
        [
            *resolved,
            _pin_node("release.sourceCommit", "Candidate", commit.value),
            _pin_node("release.sourceTree", "Candidate", tree.value),
        ],
        [
            _pin_edge("tag-commit", "Candidate", commit.pin_id, "release.sourceCommit", "git-commit"),
            _pin_edge("tag-tree", "Candidate", tree.pin_id, "release.sourceTree", "git-tree"),
        ],
        fixture_backed=True,
    )
    assert graph.verdict == "PIN_GRAPH_VALID"
    assert graph.fixture_backed is True

    planned = [
        firewall.PinNode(**{**node.__dict__, "resolved": False})
        if node.pin_id.startswith("source.tag.peeled") else node
        for node in resolved
    ]
    rejected = firewall.validate_pin_graph(
        "Candidate", planned,
        [_pin_edge("tag-commit", "Candidate", "source.tag.peeledCommit", "source.tag.peeledTree")],
    )
    assert rejected.verdict == "INCOMPATIBLE"
    assert any("unresolved-tag" in item for item in rejected.errors)


def test_pin_graph_stage_inventory_never_demands_future_pins() -> None:
    nodes = [
        _pin_node("source", "Source", "same"),
        _pin_node("package", "Package", "same"),
        _pin_node("candidate", "Candidate", "same"),
        _pin_node("promotion", "Admission", "same"),
    ]
    edges = [
        _pin_edge("source-package", "Full", "source", "package", "source-overlap"),
        _pin_edge("package-candidate", "Candidate", "package", "candidate"),
        _pin_edge("candidate-promotion", "Admission", "candidate", "promotion"),
    ]

    source = firewall.validate_pin_graph("Source", nodes, edges)
    package = firewall.validate_pin_graph("Package", nodes, edges)
    full = firewall.validate_pin_graph("Full", nodes, edges)

    assert source.verdict == "PIN_GRAPH_VALID" and source.pin_rows == ()
    assert package.verdict == "PIN_GRAPH_VALID" and package.pin_rows == ()
    assert full.verdict == "PIN_GRAPH_VALID"
    assert [row.pin_id for row in full.pin_rows] == ["source-package"]


def test_pin_graph_promotion_must_bind_sealed_candidate() -> None:
    result = firewall.validate_pin_graph(
        "Admission",
        [
            _pin_node("candidate.manifest", "Candidate", "A" * 64),
            _pin_node("promotion.candidate", "Admission", "B" * 64),
        ],
        [_pin_edge("promotion-candidate", "Admission", "candidate.manifest", "promotion.candidate")],
        fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert result.pin_rows[0].status == "FAIL"


def test_generic_graph_never_issues_admission_or_activation_milestones() -> None:
    nodes = [
        _pin_node("producer", "Candidate", "same"),
        _pin_node("admitted", "Admission", "same"),
        _pin_node("activated", "Activation", "same"),
    ]
    edges = [
        _pin_edge("admission", "Admission", "producer", "admitted"),
        _pin_edge("activation", "Activation", "admitted", "activated"),
    ]
    activation = firewall.validate_pin_graph(
        "Activation", nodes, edges, fixture_backed=True
    )
    admission = firewall.validate_pin_graph(
        "Admission", nodes, edges, fixture_backed=True
    )

    assert admission.verdict == "BLOCKED"
    assert activation.verdict == "BLOCKED"
    assert admission.compatible is activation.compatible is False
    assert admission.fixture_backed is activation.fixture_backed is True
    assert admission.real_installation is activation.real_installation is False


def test_caller_boolean_cannot_claim_real_installation() -> None:
    nodes = [
        _pin_node("admitted", "Admission", "same"),
        _pin_node("activated", "Activation", "same"),
    ]
    edges = [_pin_edge("activation", "Activation", "admitted", "activated")]

    real = firewall.validate_pin_graph("Activation", nodes, edges)
    fixture = firewall.validate_pin_graph(
        "Activation", nodes, edges, fixture_backed=True
    )
    admission = firewall.validate_pin_graph(
        "Admission",
        [_pin_node("producer", "Candidate", "same"), nodes[0]],
        [_pin_edge("admission", "Admission", "producer", "admitted")],
    )
    with pytest.raises(TypeError, match="real_installation"):
        firewall.validate_pin_graph(  # type: ignore[call-arg]
            "Activation", nodes, edges, real_installation=True,
        )

    assert real.real_installation is False
    assert fixture.real_installation is False
    assert admission.real_installation is False


@pytest.mark.parametrize("stage", ["Source", "Package", "Full", "Candidate"])
def test_generic_graph_never_issues_milestone_verdict(stage: str) -> None:
    result = firewall.validate_pin_graph(stage, [], [])

    assert result.verdict not in {
        "SOURCE_COMPATIBLE", "PACKAGE_COMPATIBLE", "FULL_COMPATIBLE",
        "CANDIDATE_COMPATIBLE", "ADMISSION_COMPATIBLE", "ACTIVATION_COMPATIBLE",
    }


@pytest.mark.parametrize("case", ["duplicate-edge", "unknown-edge-stage"])
def test_pin_graph_rejects_duplicate_or_unknown_edge_rows(case: str) -> None:
    nodes = [_pin_node("a", "Candidate", "same"), _pin_node("b", "Candidate", "same")]
    edge = _pin_edge("a-b", "Candidate", "a", "b")
    edges = [edge, edge] if case == "duplicate-edge" else [
        firewall.PinEdge("a-b", "Future", "a", "b", "sha256")
    ]

    result = firewall.validate_pin_graph("Candidate", nodes, edges)

    assert result.verdict == "INCOMPATIBLE"
    assert any(case.split("-")[0] in error for error in result.errors)


def _release_overlap_fixture(
    tmp_path: Path,
    *,
    commit: str = "C" * 40,
    tree: str = "D" * 40,
    tag: str = "v9.9.9-planned-not-created",
    registry_metadata: str = " default-entry=12",
) -> tuple[Path, Path]:
    root = tmp_path / "release"
    evidence = root / "evidence"
    evidence.mkdir(parents=True)
    version = "9.9.9-fixture"
    documents = {
        "evidence/capabilities.json": {
            "product": "Actorwright",
            "version": version,
            "sourceLine": "fixture-private",
            "commands": [
                {"name": "version", "readiness": "v2"},
                {"name": "legacy", "readiness": "legacy"},
            ],
        },
        "evidence/test-summary.json": {
            "status": "PASS",
            "sourceCommit": commit,
            "sourceTag": tag,
            "exactCommandNames": 2,
            "standalonePythonCases": 1,
            "standalonePythonSkipped": 0,
        },
        "evidence/protocol-v2-capabilities.json": {
            "result": {"commands": [
                {"name": "version", "readiness": "v2"},
                {"name": "legacy", "readiness": "legacy"},
            ]},
        },
        "evidence/protocol-v2-schema-exports.json": {
            "productVersion": version,
            "sourceLine": "fixture-private",
            "commands": [{"command": "version", "export": {}}],
        },
        "evidence/sbom.spdx.json": {
            "spdxVersion": "SPDX-2.3",
            "name": f"Actorwright-{version}",
            "documentNamespace": f"https://actorwright.invalid/spdx/{version}/{commit}",
            "packages": [{"name": "Fixture.Dependency", "versionInfo": "1.0.0"}],
        },
        "evidence/dependency-vulnerability-report.json": {"status": "PASS"},
    }
    for relative, value in documents.items():
        path = root / relative
        path.write_bytes(canonical_json_bytes(value))
    (evidence / "canonical-build.log").write_text(
        "EVIDENCE_SOURCE_COMMIT=" + commit + "\n"
        "EVIDENCE_SOURCE_TREE=" + tree + "\n"
        "standalone test registry: PASS runnable=1 "
        f"fixture-bound=0 unverified=0{registry_metadata}\n"
        "Standalone selector registry: runnable=1 fixture-bound=0 unverified=0\n"
        "SELECTOR_RESULT id=version status=PASS\n"
        "PASS Actorwright help identity\n"
        "1 passed, 0 skipped, 0 warnings in 0.01s\n",
        encoding="utf-8",
    )
    summary_path = evidence / "test-summary.json"
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    protocol = documents["evidence/protocol-v2-capabilities.json"]["result"]["commands"]
    summary["orderedHelpSha256"] = hashlib.sha256(canonical_json_bytes(protocol)).hexdigest().upper()
    summary["protocolReadinessSha256"] = hashlib.sha256(canonical_json_bytes([
        {"name": row["name"], "readiness": row["readiness"]} for row in protocol
    ])).hexdigest().upper()
    summary["selectorInventorySha256"] = hashlib.sha256(
        canonical_json_bytes(["version"])
    ).hexdigest().upper()
    summary["selectorResultsSha256"] = hashlib.sha256(
        canonical_json_bytes([{"id": "version", "status": "PASS"}])
    ).hexdigest().upper()
    summary_path.write_bytes(canonical_json_bytes(summary))
    release = {
        "product": "Actorwright",
        "version": version,
        "sourceCommit": commit,
        "sourceTree": tree,
        "sourceTag": tag,
        "capabilitiesSha256": hashlib.sha256(
            (evidence / "capabilities.json").read_bytes()
        ).hexdigest().upper(),
        "sbomSha256": hashlib.sha256(
            (evidence / "sbom.spdx.json").read_bytes()
        ).hexdigest().upper(),
        "testSummarySha256": hashlib.sha256(
            (evidence / "test-summary.json").read_bytes()
        ).hexdigest().upper(),
        "dependencyVulnerabilityReportSha256": hashlib.sha256(
            (evidence / "dependency-vulnerability-report.json").read_bytes()
        ).hexdigest().upper(),
        "canonicalBuildLogSha256": hashlib.sha256(
            (evidence / "canonical-build.log").read_bytes()
        ).hexdigest().upper(),
    }
    (root / "actorwright-release.json").write_bytes(canonical_json_bytes(release))
    rows = []
    for path in sorted(root.rglob("*")):
        if path.is_file() and path.name != "SHA256SUMS":
            relative = path.relative_to(root).as_posix()
            rows.append(
                f"{hashlib.sha256(path.read_bytes()).hexdigest().upper()}  {relative}\n"
            )
    (root / "SHA256SUMS").write_text("".join(rows), encoding="utf-8")
    archive = tmp_path / "release.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_STORED) as handle:
        for path in sorted(root.rglob("*")):
            if path.is_file():
                handle.write(path, f"fixture/{path.relative_to(root).as_posix()}")
    return root, archive


def _reseal_release_fixture(root: Path, archive: Path) -> None:
    release_path = root / "actorwright-release.json"
    release = json.loads(release_path.read_text(encoding="utf-8"))
    for field, relative in {
        "capabilitiesSha256": "evidence/capabilities.json",
        "sbomSha256": "evidence/sbom.spdx.json",
        "testSummarySha256": "evidence/test-summary.json",
        "dependencyVulnerabilityReportSha256": "evidence/dependency-vulnerability-report.json",
        "canonicalBuildLogSha256": "evidence/canonical-build.log",
    }.items():
        release[field] = hashlib.sha256((root / relative).read_bytes()).hexdigest().upper()
    release_path.write_bytes(canonical_json_bytes(release))
    rows = []
    for path in sorted(root.rglob("*")):
        if path.is_file() and path.name != "SHA256SUMS":
            relative = path.relative_to(root).as_posix()
            rows.append(f"{hashlib.sha256(path.read_bytes()).hexdigest().upper()}  {relative}\n")
    (root / "SHA256SUMS").write_text("".join(rows), encoding="utf-8")
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_STORED) as handle:
        for path in sorted(root.rglob("*")):
            if path.is_file():
                handle.write(path, f"fixture/{path.relative_to(root).as_posix()}")


def _candidate_fixture(
    tmp_path: Path, *, commit: str = "C" * 40, tree: str = "D" * 40,
    tag: str = "v9.9.9-planned-not-created", package_cli: bytes | None = None,
) -> tuple[Path, Path, Path]:
    root, archive = _release_overlap_fixture(tmp_path, commit=commit, tree=tree, tag=tag)
    if package_cli is not None:
        cli = root / "cli" / "actorwright.exe"
        cli.parent.mkdir(parents=True)
        cli.write_bytes(package_cli)
        _reseal_release_fixture(root, archive)
    bundle = tmp_path / "candidate"
    bundle.mkdir()
    bundle_archive = bundle / archive.name
    shutil.copy2(archive, bundle_archive)
    shutil.copy2(root / "actorwright-release.json", bundle / "actorwright-release.json")
    release = json.loads((root / "actorwright-release.json").read_text(encoding="utf-8"))
    payload = {
        "packageSha256": hashlib.sha256(archive.read_bytes()).hexdigest().upper(),
        "capabilitiesSha256": release["capabilitiesSha256"],
        "sbomSha256": release["sbomSha256"],
        "testSummarySha256": release["testSummarySha256"],
        "sourceCommit": release["sourceCommit"],
        "sourceTag": release["sourceTag"],
        "productVersion": release["version"],
    }
    (bundle / "release-candidate.json").write_bytes(canonical_json_bytes(payload))
    files = []
    for path in sorted(bundle.iterdir()):
        if path.name == "bundle-manifest.json":
            continue
        files.append({
            "path": path.name,
            "size": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
        })
    manifest = {
        "$schema": "urn:actorwright:exchange:v1:bundle-manifest",
        "version": 1,
        "kind": "release-candidate", "payload": "release-candidate.json",
        "files": files,
    }
    (bundle / "bundle-manifest.json").write_bytes(canonical_json_bytes(manifest))
    return bundle, root, archive


def _resolved_fixture_tag() -> tuple[firewall.PinNode, ...]:
    return (
        _pin_node("source.tag.name", "Candidate", "v9.9.9-planned-not-created"),
        _pin_node("source.tag.peeledCommit", "Candidate", "C" * 40),
        _pin_node("source.tag.peeledTree", "Candidate", "D" * 40),
    )


@pytest.mark.parametrize("registry_metadata", ["", " default-entry=12"])
def test_release_root_overlap_is_tag_free_and_closes_zip(
    tmp_path: Path, registry_metadata: str
) -> None:
    root, archive = _release_overlap_fixture(
        tmp_path, registry_metadata=registry_metadata
    )
    derived = subprocess.run(
        [sys.executable, "-m", "tools.release.derive_build_pins",
         str(root / "evidence" / "canonical-build.log"),
         str(root / "evidence" / "protocol-v2-capabilities.json")],
        cwd=Path(__file__).resolve().parents[2], capture_output=True, text=True,
        check=True,
    )
    summary = json.loads((root / "evidence" / "test-summary.json").read_text())
    assert json.loads(derived.stdout) == {
        key: summary[key] for key in (
            "standalonePythonCases", "standalonePythonSkipped", "orderedHelpSha256",
            "protocolReadinessSha256", "selectorInventorySha256",
            "selectorResultsSha256",
        )
    }
    missing_selector_log = tmp_path / "missing-selector.log"
    missing_selector_log.write_text(
        (root / "evidence" / "canonical-build.log").read_text().replace(
            "SELECTOR_RESULT id=version status=PASS\n", ""
        ),
        encoding="utf-8",
    )
    rejected = subprocess.run(
        [sys.executable, "-m", "tools.release.derive_build_pins",
         str(missing_selector_log),
         str(root / "evidence" / "protocol-v2-capabilities.json")],
        cwd=Path(__file__).resolve().parents[2], capture_output=True, text=True,
    )
    assert rejected.returncode != 0
    assert "selector results do not match runnable registry" in rejected.stderr

    result = firewall.verify_release_root_compatibility(root, archive)

    assert result.verdict == "FULL_COMPATIBLE"
    assert result.compatible is True
    assert not any(row.identity_class == "git-tag-resolution" for row in result.pin_rows)


def test_derive_build_pins_rejects_unrecognized_registry_metadata(tmp_path: Path) -> None:
    root, _archive = _release_overlap_fixture(
        tmp_path, registry_metadata=" default-entry=12 unexpected=1"
    )
    rejected = subprocess.run(
        [sys.executable, "-m", "tools.release.derive_build_pins",
         str(root / "evidence" / "canonical-build.log"),
         str(root / "evidence" / "protocol-v2-capabilities.json")],
        cwd=Path(__file__).resolve().parents[2], capture_output=True, text=True,
    )
    assert rejected.returncode != 0
    assert (
        "canonical build must have exactly one selector registry result"
        in rejected.stderr
    )


def test_source_and_package_milestones_require_artifact_backed_closed_inventories(
    tmp_path: Path,
) -> None:
    root, archive = _release_overlap_fixture(tmp_path)

    source = firewall.verify_release_source_compatibility(root)
    package = firewall.verify_release_package_compatibility(root, archive)

    assert source.verdict == "SOURCE_COMPATIBLE"
    assert package.verdict == "PACKAGE_COMPATIBLE"
    assert {row.pin_id for row in source.pin_rows} == firewall.SOURCE_PIN_IDS
    assert {row.pin_id for row in package.pin_rows} == firewall.PACKAGE_ZIP_PIN_IDS
    assert {row.identity_class for row in source.pin_rows} == firewall.SOURCE_PIN_CLASSES
    assert {row.identity_class for row in package.pin_rows} == firewall.PACKAGE_ZIP_PIN_CLASSES


def test_source_stage_runs_before_release_manifest_or_checksums_exist(
    tmp_path: Path,
) -> None:
    root, _archive = _release_overlap_fixture(tmp_path)

    result = firewall.verify_release_source_compatibility(root / "evidence")

    assert result.verdict == "SOURCE_COMPATIBLE"
    assert result.compatible is True
    assert {row.pin_id for row in result.pin_rows} == firewall.SOURCE_PIN_IDS


def test_package_stage_authenticates_bytes_without_parsing_source_contract(
    tmp_path: Path,
) -> None:
    root, archive = _release_overlap_fixture(tmp_path)
    protocol = root / "evidence" / "protocol-v2-capabilities.json"
    protocol.write_bytes(b"not source-contract JSON")
    _reseal_release_fixture(root, archive)

    root_result = firewall.verify_release_package_compatibility(root)
    zip_result = firewall.verify_release_package_compatibility(root, archive)

    assert root_result.verdict == "PACKAGE_COMPATIBLE"
    assert zip_result.verdict == "PACKAGE_COMPATIBLE"
    assert {row.pin_id for row in root_result.pin_rows} == firewall.PACKAGE_ROOT_PIN_IDS
    assert {row.pin_id for row in zip_result.pin_rows} == firewall.PACKAGE_ZIP_PIN_IDS


@pytest.mark.parametrize(
    "mutation",
    [
        "source-tree", "source-line", "ordered-help", "readiness-swap",
        "selectors", "skip-count", "test-count", "sbom-source",
    ],
)
def test_release_overlap_requires_independent_evidence(
    tmp_path: Path, mutation: str,
) -> None:
    root, archive = _release_overlap_fixture(tmp_path)
    if mutation == "source-tree":
        path = root / "evidence" / "canonical-build.log"
        path.write_text(path.read_text(encoding="utf-8").replace("D" * 40, "E" * 40), encoding="utf-8")
    elif mutation == "source-line":
        path = root / "evidence" / "protocol-v2-schema-exports.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["sourceLine"] = "wrong-line"
        path.write_bytes(canonical_json_bytes(value))
    elif mutation == "ordered-help":
        path = root / "evidence" / "protocol-v2-capabilities.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["result"]["commands"][0]["purpose"] = "metadata drift"
        path.write_bytes(canonical_json_bytes(value))
    elif mutation == "readiness-swap":
        path = root / "evidence" / "protocol-v2-capabilities.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["result"]["commands"][0]["readiness"] = "legacy"
        value["result"]["commands"][1]["readiness"] = "v2"
        path.write_bytes(canonical_json_bytes(value))
    elif mutation == "selectors":
        path = root / "evidence" / "canonical-build.log"
        path.write_text(path.read_text(encoding="utf-8").replace(
            "SELECTOR_RESULT id=version status=PASS",
            "SELECTOR_RESULT id=other status=PASS",
        ), encoding="utf-8")
    elif mutation == "skip-count":
        path = root / "evidence" / "test-summary.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["standalonePythonSkipped"] = 1
        path.write_bytes(canonical_json_bytes(value))
    elif mutation == "sbom-source":
        path = root / "evidence" / "sbom.spdx.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["documentNamespace"] = value["documentNamespace"].replace("C" * 40, "E" * 40)
        path.write_bytes(canonical_json_bytes(value))
    else:
        path = root / "evidence" / "test-summary.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["standalonePythonCases"] = 2
        path.write_bytes(canonical_json_bytes(value))
    _reseal_release_fixture(root, archive)

    result = firewall.verify_release_root_compatibility(root, archive)

    assert result.verdict == "INCOMPATIBLE"
    assert result.errors


@pytest.mark.parametrize(
    "mutation", ["capabilities-type", "protocol-result-type", "schema-json", "build-utf8"],
)
def test_public_release_seam_turns_malformed_evidence_into_diagnostics(
    tmp_path: Path, mutation: str,
) -> None:
    root, archive = _release_overlap_fixture(tmp_path)
    if mutation == "capabilities-type":
        path = root / "evidence" / "capabilities.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["commands"] = "not-an-array"
        path.write_bytes(canonical_json_bytes(value))
    elif mutation == "protocol-result-type":
        path = root / "evidence" / "protocol-v2-capabilities.json"
        value = json.loads(path.read_text(encoding="utf-8"))
        value["result"] = []
        path.write_bytes(canonical_json_bytes(value))
    elif mutation == "schema-json":
        (root / "evidence" / "protocol-v2-schema-exports.json").write_bytes(b"{")
    else:
        (root / "evidence" / "canonical-build.log").write_bytes(b"\xff\xfe")
    _reseal_release_fixture(root, archive)

    result = firewall.verify_release_root_compatibility(root, archive)

    assert result.verdict == "INCOMPATIBLE"
    assert result.errors


@pytest.mark.parametrize(
    "mutation",
    [
        "backslash-alias", "case-alias", "duplicate", "absolute", "dotdot",
        "rootless", "second-root", "empty-directory", "symlink",
    ],
)
def test_public_zip_seam_rejects_unsafe_inventory(
    tmp_path: Path, mutation: str,
) -> None:
    root, archive = _release_overlap_fixture(tmp_path)
    with zipfile.ZipFile(archive, "a", compression=zipfile.ZIP_STORED) as handle:
        if mutation == "backslash-alias":
            handle.writestr(
                "fixture/evidence\\capabilities.json",
                (root / "evidence" / "capabilities.json").read_bytes(),
            )
        elif mutation == "case-alias":
            handle.writestr(
                "fixture/EVIDENCE/capabilities.json",
                (root / "evidence" / "capabilities.json").read_bytes(),
            )
        elif mutation == "duplicate":
            handle.writestr(
                "fixture/evidence/capabilities.json",
                (root / "evidence" / "capabilities.json").read_bytes(),
            )
        elif mutation == "absolute":
            handle.writestr("/fixture/absolute.txt", b"unsafe")
        elif mutation == "dotdot":
            handle.writestr("fixture/../escape.txt", b"unsafe")
        elif mutation == "rootless":
            handle.writestr("rootless.txt", b"unsafe")
        elif mutation == "second-root":
            handle.writestr("other/extra.txt", b"unsafe")
        elif mutation == "empty-directory":
            handle.writestr("fixture/empty/", b"")
        else:
            info = zipfile.ZipInfo("fixture/link")
            info.create_system = 3
            info.external_attr = 0o120777 << 16
            handle.writestr(info, b"actorwright-release.json")

    result = firewall.verify_release_root_compatibility(root, archive)

    assert result.verdict == "INCOMPATIBLE"
    assert any("release-zip-unsafe" in error for error in result.errors)


def test_candidate_rejects_substituted_bundle_bytes(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    monkeypatch.setattr(firewall, "resolve_tag_pin_nodes", lambda *_: _resolved_fixture_tag())
    (bundle / archive.name).write_bytes(b"internally substituted zip")
    (bundle / "actorwright-release.json").write_bytes(
        canonical_json_bytes({"internally": "substituted"})
    )
    manifest_path = bundle / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    for row in manifest["files"]:
        path = bundle / row["path"]
        row["size"] = path.stat().st_size
        row["sha256"] = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    manifest_path.write_bytes(canonical_json_bytes(manifest))

    result = firewall.verify_candidate_bundle_compatibility(
        bundle, root, archive, tmp_path, fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert any("candidate-producer" in error for error in result.errors)


def test_candidate_complete_authenticated_fixture_closes_with_disposable_tag(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    monkeypatch.setattr(firewall, "resolve_tag_pin_nodes", lambda *_: _resolved_fixture_tag())

    result = firewall.verify_candidate_bundle_compatibility(
        bundle, root, archive, tmp_path, fixture_backed=True,
    )

    assert result.verdict == "CANDIDATE_COMPATIBLE"
    assert result.fixture_backed is True
    assert result.real_installation is False
    assert {"tag-name", "tag-commit", "tag-tree"} <= {
        row.pin_id for row in result.pin_rows
    }
    schema_row = next(
        row for row in result.pin_rows
        if row.pin_id == "consumer-stage-report-schema-sha256"
    )
    assert schema_row.identity_class == "contract-schema-sha256"
    assert schema_row.status == "PASS"
    assert schema_row.producer_value == schema_row.consumer_value == (
        "67CE223DAD7C277AE6AFB3BA19EE54E546DE654DC16C5C82774492A799EBB937"
    )


@pytest.mark.parametrize("mutation", ["missing", "mutated"])
def test_candidate_refuses_missing_or_mutated_consumer_schema(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, mutation: str,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    monkeypatch.setattr(firewall, "resolve_tag_pin_nodes", lambda *_: _resolved_fixture_tag())
    schema = tmp_path / "consumer-stage-report.schema.json"
    if mutation == "mutated":
        schema.write_bytes((CONTRACT_ROOT / schema.name).read_bytes() + b" ")
    monkeypatch.setattr(firewall, "CONSUMER_STAGE_REPORT_SCHEMA", schema)

    result = firewall.verify_candidate_bundle_compatibility(
        bundle, root, archive, tmp_path, fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert result.errors
    if mutation == "mutated":
        row = next(row for row in result.pin_rows if row.pin_id == "consumer-stage-report-schema-sha256")
        assert row.status == "FAIL"
        assert row.producer_value != row.consumer_value


def test_candidate_tier_requires_explicit_passing_pin_gate() -> None:
    required = set(firewall.required_gate_ids("Candidate"))
    assert required == set(firewall.required_gate_ids("Full")) | {"pins:candidate"}
    gates = _required_gate_set("Full")
    assert firewall.evaluate_tier("Candidate", gates).verdict == "INCOMPATIBLE"
    gates.append(firewall.GateResult("pins:candidate", True, "PASS", ("pin graph closed",), ()))
    assert firewall.evaluate_tier("Candidate", gates).verdict == "CANDIDATE_COMPATIBLE"
    gates[-1] = firewall.GateResult("pins:candidate", True, "FAIL", ("pin drift",), ())
    assert firewall.evaluate_tier("Candidate", gates).verdict == "INCOMPATIBLE"


@pytest.mark.parametrize(
    "unsafe_path", ["../outside", "/absolute", "C:/absolute", "release-candidate.json\\alias"],
)
def test_candidate_manifest_rejects_unsafe_paths_before_member_io(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, unsafe_path: str,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    monkeypatch.setattr(firewall, "resolve_tag_pin_nodes", lambda *_: _resolved_fixture_tag())
    manifest_path = bundle / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["files"][0]["path"] = unsafe_path
    manifest_path.write_bytes(canonical_json_bytes(manifest))

    result = firewall.verify_candidate_bundle_compatibility(
        bundle, root, archive, tmp_path, fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert any("candidate-manifest-path-unsafe" in error for error in result.errors)


def test_candidate_public_boundary_contains_path_conversion_failure() -> None:
    class BrokenPath:
        def __fspath__(self) -> str:
            raise OSError("path conversion failed")

    result = firewall.verify_candidate_bundle_compatibility(
        BrokenPath(), BrokenPath(), BrokenPath(), BrokenPath(),
        fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert result.fixture_backed is True
    assert any("candidate-compatibility-invalid" in error for error in result.errors)


def test_candidate_refuses_reparse_ancestor_before_following_root(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    original = firewall._unresolved_path_kind
    observed: list[Path] = []

    def simulated_kind(path: Path) -> tuple[bool, bool, bool]:
        observed.append(path)
        if path == bundle.parent:
            return True, True, True
        return original(path)

    monkeypatch.setattr(firewall, "_unresolved_path_kind", simulated_kind)
    monkeypatch.setattr(
        Path, "resolve",
        lambda *_args, **_kwargs: pytest.fail("must refuse before resolve"),
    )

    result = firewall.verify_candidate_bundle_compatibility(
        bundle, root, archive, tmp_path, fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert bundle.parent in observed
    assert any("candidate-root-ancestor-reparse" in error for error in result.errors)


@pytest.mark.parametrize("target", ["release-root", "release-zip", "repository-root"])
def test_candidate_refuses_other_input_reparse_ancestors_before_resolution(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, target: str,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    paths = {
        "release-root": root.parent / "junction" / ".." / root.name,
        "release-zip": archive.parent / "junction" / ".." / archive.name,
        "repository-root": tmp_path / "junction" / "..",
    }
    junction = tmp_path / "junction"
    original = firewall._unresolved_path_kind

    def simulated_kind(path: Path) -> tuple[bool, bool, bool]:
        if path == junction:
            return True, True, True
        return original(path)

    monkeypatch.setattr(firewall, "_unresolved_path_kind", simulated_kind)
    monkeypatch.setattr(
        Path, "resolve", lambda *_args, **_kwargs: pytest.fail("resolved before reparse check"),
    )
    result = firewall.verify_candidate_bundle_compatibility(
        bundle, paths["release-root"] if target == "release-root" else root,
        paths["release-zip"] if target == "release-zip" else archive,
        paths["repository-root"] if target == "repository-root" else tmp_path,
        fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert any(f"candidate-{target}-ancestor-reparse" in error for error in result.errors)


@pytest.mark.parametrize("stage", ["Package", "Full"])
@pytest.mark.parametrize("target", ["release-root", "release-zip"])
def test_public_release_paths_refuse_reparse_ancestors_before_resolution(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, stage: str, target: str,
) -> None:
    root, archive = _release_overlap_fixture(tmp_path)
    junction = tmp_path / "junction"
    original = firewall._unresolved_path_kind

    def simulated_kind(path: Path) -> tuple[bool, bool, bool]:
        if path == junction:
            return True, True, True
        return original(path)

    monkeypatch.setattr(firewall, "_unresolved_path_kind", simulated_kind)
    monkeypatch.setattr(
        Path, "resolve", lambda *_args, **_kwargs: pytest.fail("resolved before reparse check"),
    )
    root_arg = root.parent / "junction" / ".." / root.name if target == "release-root" else root
    archive_arg = archive.parent / "junction" / ".." / archive.name if target == "release-zip" else archive
    verifier = (firewall.verify_release_package_compatibility if stage == "Package"
                else firewall.verify_release_root_compatibility)
    result = verifier(root_arg, archive_arg)

    assert result.verdict == "INCOMPATIBLE"
    assert any(f"{target}-ancestor-reparse" in error for error in result.errors)


def test_candidate_contains_ancestor_lstat_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    original = Path.lstat

    def failed_lstat(path: Path) -> os.stat_result:
        if path == bundle.parent:
            raise PermissionError("ancestor metadata denied")
        return original(path)

    monkeypatch.setattr(Path, "lstat", failed_lstat)

    result = firewall.verify_candidate_bundle_compatibility(
        bundle, root, archive, tmp_path, fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert any("candidate-compatibility-invalid" in error for error in result.errors)


def test_candidate_public_boundary_contains_member_io_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    original = Path.open

    def failed_open(path: Path, *args, **kwargs):
        if path == bundle / "release-candidate.json":
            raise OSError("candidate member read failed")
        return original(path, *args, **kwargs)

    monkeypatch.setattr(Path, "open", failed_open)

    result = firewall.verify_candidate_bundle_compatibility(
        bundle, root, archive, tmp_path, fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert any(
        "candidate-compatibility-invalid:OSError:candidate member read failed" in error
        for error in result.errors
    )


def test_verify_and_tag_requires_explicit_sealed_compatibility_verdict() -> None:
    script = (ROOT / "tools" / "release" / "verify_and_tag.ps1").read_text(
        encoding="utf-8"
    )

    assert "ConvertFrom-Json" in script
    assert "FULL_COMPATIBLE" in script
    assert script.index("FULL_COMPATIBLE") < script.index("tag -a")
    assert script.index("--create-zip") < script.index("-Tier Full") < script.index("tag -a")
    assert script.count("tag -a") == 1
    assert not re.search(r"\b(?:capture|install|elevat)\b", script[
        script.index("-Tier Full"):script.index("tag -a")], re.IGNORECASE)


def test_verify_and_tag_refuses_success_json_without_compatibility_verdict(
    tmp_path: Path,
) -> None:
    powershell, git = shutil.which("powershell.exe"), shutil.which("git")
    assert powershell is not None and git is not None
    helper_dir = tmp_path / "tools" / "release"
    helper_dir.mkdir(parents=True)
    shutil.copy2(ROOT / "tools" / "release" / "verify_and_tag.ps1", helper_dir)
    (helper_dir / "verify_release.py").write_text(
        "print('{\"status\":\"PASS\",\"zipVerified\":true}')\n",
        encoding="utf-8",
    )
    repository = tmp_path / "repository"
    repository.mkdir()
    (repository / "tracked.txt").write_text("sealed source\n", encoding="utf-8")
    for command in (
        (git, "init"),
        (git, "config", "user.email", "firewall@example.invalid"),
        (git, "config", "user.name", "Compatibility Firewall"),
        (git, "add", "tracked.txt"),
        (git, "commit", "-m", "sealed source"),
    ):
        completed = subprocess.run(command, cwd=repository, capture_output=True, text=True)
        assert completed.returncode == 0, completed.stdout + completed.stderr
    commit = subprocess.run(
        (git, "rev-parse", "HEAD"), cwd=repository, capture_output=True,
        text=True, check=True,
    ).stdout.strip()
    tag = "v9.9.9-missing-compatibility"

    completed = subprocess.run(
        (
            powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            str(helper_dir / "verify_and_tag.ps1"), "-ProjectRoot", str(repository),
            "-PythonPath", sys.executable, "-GitPath", git,
            "-OutputRoot", str(tmp_path / "release"),
            "-ZipPath", str(tmp_path / "release.zip"), "-Tag", tag,
            "-Commit", commit, "-Version", "9.9.9-test",
        ),
        cwd=repository, capture_output=True, text=True,
    )

    assert completed.returncode != 0
    assert "did not establish FULL_COMPATIBLE" in completed.stdout + completed.stderr
    missing = subprocess.run(
        (git, "show-ref", "--verify", "--quiet", f"refs/tags/{tag}"),
        cwd=repository, capture_output=True, text=True,
    )
    assert missing.returncode == 1
    (helper_dir / "verify_release.py").write_text(
        "print('{\"status\":\"PASS\",\"zipVerified\":true,"
        f"\"compatibilityVerdict\":\"FULL_COMPATIBLE\","
        f"\"sourceCommit\":\"{commit}\",\"version\":\"9.9.9-test\"}}')\n",
        encoding="utf-8",
    )
    accepted_tag = "v9.9.9-test"
    firewall_dir = repository / "tools" / "verification"
    firewall_dir.mkdir(parents=True)
    firewall_args = repository / "firewall-args.json"
    (firewall_dir / "compatibility-firewall.ps1").write_text(
        "param([string]$Mode, [Parameter(ValueFromRemainingArguments=$true)][string[]]$ForwardedArguments)\n"
        "$ForwardedArguments | ConvertTo-Json -Compress | Set-Content -LiteralPath '"
        + str(firewall_args).replace("'", "''") + "'\n"
        "$index = [array]::IndexOf($ForwardedArguments, '-Output')\n"
        "$report = $ForwardedArguments[$index + 1]\n"
        "@{tier='Full'; baselineId='publicSynthetic'; compatible=$true; verdict='FULL_COMPATIBLE'; "
        "fixtureBacked=$false; realInstallation=$false} | ConvertTo-Json -Compress | "
        "Set-Content -LiteralPath $report\nexit 0\n",
        encoding="utf-8",
    )
    accepted = subprocess.run(
        (
            powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            str(helper_dir / "verify_and_tag.ps1"), "-ProjectRoot", str(repository),
            "-PythonPath", sys.executable, "-GitPath", git,
            "-OutputRoot", str(tmp_path / "release"),
            "-ZipPath", str(tmp_path / "release.zip"), "-Tag", accepted_tag,
            "-Commit", commit, "-Version", "9.9.9-test",
        ),
        cwd=repository, capture_output=True, text=True,
    )
    assert accepted.returncode == 0, accepted.stdout + accepted.stderr
    forwarded = json.loads(firewall_args.read_text(encoding="utf-8"))
    assert forwarded[:4] == ["-Tier", "Full", "-Baseline", "publicSynthetic"]
    assert forwarded[forwarded.index("-SourceCli") + 1] == str(
        repository / "src/NpcManager.Cli/bin/Release/net10.0/actorwright.exe")
    assert forwarded[forwarded.index("-ReleaseRoot") + 1] == str(tmp_path / "release")
    assert forwarded[forwarded.index("-ReleaseZip") + 1] == str(tmp_path / "release.zip")
    Path(forwarded[forwarded.index("-Output") + 1]).unlink(missing_ok=True)
    assert subprocess.run(
        (git, "show-ref", "--verify", "--quiet", f"refs/tags/{accepted_tag}"),
        cwd=repository, capture_output=True, text=True,
    ).returncode == 0


@pytest.mark.parametrize("failure", [
    "nonzero", "missing", "empty", "directory", "reparse", "invalid-json",
    "compatible-string", "compatible-false", "verdict-array", "fixture-true",
])
def test_verify_and_tag_full_firewall_failure_leaves_no_tag(
    tmp_path: Path, failure: str,
) -> None:
    powershell, git = shutil.which("powershell.exe"), shutil.which("git")
    assert powershell is not None and git is not None
    helper_dir = tmp_path / "tools/release"
    helper_dir.mkdir(parents=True)
    shutil.copy2(ROOT / "tools/release/verify_and_tag.ps1", helper_dir)
    repository = tmp_path / "repository"
    repository.mkdir()
    (repository / "tracked.txt").write_text("sealed source\n", encoding="utf-8")
    for command in ((git, "init"), (git, "config", "user.email", "firewall@example.invalid"),
                    (git, "config", "user.name", "Firewall Test"),
                    (git, "add", "tracked.txt"), (git, "commit", "-m", "sealed")):
        completed = subprocess.run(command, cwd=repository, capture_output=True, text=True)
        assert completed.returncode == 0, completed.stdout + completed.stderr
    commit = subprocess.run((git, "rev-parse", "HEAD"), cwd=repository,
                            capture_output=True, text=True, check=True).stdout.strip()
    (helper_dir / "verify_release.py").write_text(
        "import json\nprint(json.dumps(" + repr({
            "status": "PASS", "zipVerified": True,
            "compatibilityVerdict": "FULL_COMPATIBLE", "sourceCommit": commit,
            "version": "9.9.9-test",
        }) + "))\n", encoding="utf-8")
    firewall_dir = repository / "tools/verification"
    firewall_dir.mkdir(parents=True)
    report_argument = repository / "full-report-path.txt"
    payload = {"tier": "Full", "baselineId": "publicSynthetic", "compatible": True,
               "verdict": "FULL_COMPATIBLE", "fixtureBacked": False,
               "realInstallation": False}
    if failure == "compatible-string":
        payload["compatible"] = "true"
    elif failure == "compatible-false":
        payload["compatible"] = False
    elif failure == "verdict-array":
        payload["verdict"] = ["FULL_COMPATIBLE"]
    elif failure == "fixture-true":
        payload["fixtureBacked"] = True
    report_text = "not-json" if failure == "invalid-json" else json.dumps(payload)
    script = (
        "param([string]$Mode, [Parameter(ValueFromRemainingArguments=$true)][string[]]$ForwardedArguments)\n"
        "$index = [array]::IndexOf($ForwardedArguments, '-Output')\n"
        "$report = $ForwardedArguments[$index + 1]\n"
        "Set-Content -LiteralPath '" + str(report_argument).replace("'", "''")
        + "' -Value $report\n"
    )
    if failure == "nonzero":
        script += "exit 7\n"
    elif failure == "missing":
        script += "exit 0\n"
    elif failure == "empty":
        script += "[IO.File]::WriteAllBytes($report, [byte[]]@())\nexit 0\n"
    elif failure == "directory":
        script += "New-Item -ItemType Directory -Path $report | Out-Null\nexit 0\n"
    elif failure == "reparse":
        script += (
            "New-Item -ItemType SymbolicLink -Path $report -Target '"
            + str(report_argument).replace("'", "''") + "' -ErrorAction Stop | Out-Null\nexit 0\n"
        )
    else:
        script += ("Set-Content -LiteralPath $report -Value '"
                   + report_text.replace("'", "''") + "'\nexit 0\n")
    (firewall_dir / "compatibility-firewall.ps1").write_text(script, encoding="utf-8")
    tag = "v9.9.9-test"
    completed = subprocess.run((
        powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
        str(helper_dir / "verify_and_tag.ps1"), "-ProjectRoot", str(repository),
        "-PythonPath", sys.executable, "-GitPath", git,
        "-OutputRoot", str(tmp_path / "release"), "-ZipPath", str(tmp_path / "release.zip"),
        "-Tag", tag, "-Commit", commit, "-Version", "9.9.9-test",
    ), cwd=repository, capture_output=True, text=True)
    try:
        assert completed.returncode != 0
        diagnostic = completed.stdout + completed.stderr
        if failure == "nonzero":
            assert "failed with exit code 7" in diagnostic
        elif failure == "reparse" and "NewItemSymbolicLinkElevationRequired" in diagnostic:
            assert "NewItemSymbolicLinkElevationRequired" in diagnostic
        elif failure in {"missing", "empty", "directory", "reparse"}:
            assert "report is missing, empty, or not an ordinary file" in diagnostic
        elif failure == "invalid-json":
            assert "report is not valid JSON" in diagnostic
        else:
            assert "did not establish FULL_COMPATIBLE" in diagnostic
        assert subprocess.run((git, "show-ref", "--verify", "--quiet", f"refs/tags/{tag}"),
                              cwd=repository, capture_output=True).returncode == 1
    finally:
        if report_argument.is_file():
            report_path = Path(report_argument.read_text(encoding="utf-8").strip())
            if report_path.is_symlink() or report_path.is_file():
                report_path.unlink()
            elif report_path.is_dir():
                report_path.rmdir()


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("zipVerified", "true"),
        ("zipVerified", 1),
        ("zipVerified", None),
        ("zipVerified", False),
        ("zipVerified", {}),
        ("zipVerified", [True]),
        ("status", ["PASS"]),
        ("compatibilityVerdict", ["FULL_COMPATIBLE"]),
        ("sourceCommit", ["commit"]),
        ("version", ["9.9.9-test"]),
    ],
)
def test_verify_and_tag_refuses_coercible_or_non_scalar_authority(
    tmp_path: Path, field: str, value: object,
) -> None:
    powershell, git = shutil.which("powershell.exe"), shutil.which("git")
    assert powershell is not None and git is not None
    helper_dir = tmp_path / "tools" / "release"
    helper_dir.mkdir(parents=True)
    shutil.copy2(ROOT / "tools" / "release" / "verify_and_tag.ps1", helper_dir)
    repository = tmp_path / "repository"
    repository.mkdir()
    (repository / "tracked.txt").write_text("sealed source\n", encoding="utf-8")
    for command in (
        (git, "init"),
        (git, "config", "user.email", "firewall@example.invalid"),
        (git, "config", "user.name", "Compatibility Firewall"),
        (git, "add", "tracked.txt"),
        (git, "commit", "-m", "sealed source"),
    ):
        completed = subprocess.run(command, cwd=repository, capture_output=True, text=True)
        assert completed.returncode == 0, completed.stdout + completed.stderr
    commit = subprocess.run(
        (git, "rev-parse", "HEAD"), cwd=repository, capture_output=True,
        text=True, check=True,
    ).stdout.strip()
    report = {
        "status": "PASS", "zipVerified": True,
        "compatibilityVerdict": "FULL_COMPATIBLE",
        "sourceCommit": commit, "version": "9.9.9-test",
    }
    report[field] = [commit] if field == "sourceCommit" else value
    (helper_dir / "verify_release.py").write_text(
        "import json\nprint(json.dumps(" + repr(report) + "))\n", encoding="utf-8",
    )
    tag = "v9.9.9-test"
    completed = subprocess.run(
        (
            powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            str(helper_dir / "verify_and_tag.ps1"), "-ProjectRoot", str(repository),
            "-PythonPath", sys.executable, "-GitPath", git,
            "-OutputRoot", str(tmp_path / "release"),
            "-ZipPath", str(tmp_path / "release.zip"), "-Tag", tag,
            "-Commit", commit, "-Version", "9.9.9-test",
        ),
        cwd=repository, capture_output=True, text=True,
    )
    assert completed.returncode != 0
    diagnostic = (
        "did not establish FULL_COMPATIBLE"
        if field in {"zipVerified", "status", "compatibilityVerdict"}
        else "Release verification source commit"
        if field == "sourceCommit"
        else "Release verification version and tag"
    )
    assert diagnostic in completed.stdout + completed.stderr
    assert subprocess.run(
        (git, "show-ref", "--verify", "--quiet", f"refs/tags/{tag}"),
        cwd=repository, capture_output=True, text=True,
    ).returncode == 1


@pytest.mark.parametrize("root_kind", ["singleton-array", "multi-array", "null", "scalar"])
def test_verify_and_tag_refuses_non_object_json_root(
    tmp_path: Path, root_kind: str,
) -> None:
    powershell, git = shutil.which("powershell.exe"), shutil.which("git")
    assert powershell is not None and git is not None
    helper_dir = tmp_path / "tools" / "release"
    helper_dir.mkdir(parents=True)
    shutil.copy2(ROOT / "tools" / "release" / "verify_and_tag.ps1", helper_dir)
    repository = tmp_path / "repository"
    repository.mkdir()
    (repository / "tracked.txt").write_text("sealed source\n", encoding="utf-8")
    for command in (
        (git, "init"),
        (git, "config", "user.email", "firewall@example.invalid"),
        (git, "config", "user.name", "Compatibility Firewall"),
        (git, "add", "tracked.txt"),
        (git, "commit", "-m", "sealed source"),
    ):
        completed = subprocess.run(command, cwd=repository, capture_output=True, text=True)
        assert completed.returncode == 0, completed.stdout + completed.stderr
    commit = subprocess.run(
        (git, "rev-parse", "HEAD"), cwd=repository, capture_output=True,
        text=True, check=True,
    ).stdout.strip()
    valid = {
        "status": "PASS", "zipVerified": True,
        "compatibilityVerdict": "FULL_COMPATIBLE",
        "sourceCommit": commit, "version": "9.9.9-test",
    }
    payload = {
        "singleton-array": [valid], "multi-array": [valid, valid],
        "null": None, "scalar": 1,
    }[root_kind]
    (helper_dir / "verify_release.py").write_text(
        "import json\nprint(json.dumps(" + repr(payload) + "))\n", encoding="utf-8",
    )
    tag = "v9.9.9-test"
    completed = subprocess.run(
        (
            powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            str(helper_dir / "verify_and_tag.ps1"), "-ProjectRoot", str(repository),
            "-PythonPath", sys.executable, "-GitPath", git,
            "-OutputRoot", str(tmp_path / "release"),
            "-ZipPath", str(tmp_path / "release.zip"), "-Tag", tag,
            "-Commit", commit, "-Version", "9.9.9-test",
        ),
        cwd=repository, capture_output=True, text=True,
    )
    assert completed.returncode != 0
    assert "Release verification" in completed.stdout + completed.stderr
    assert subprocess.run(
        (git, "show-ref", "--verify", "--quiet", f"refs/tags/{tag}"),
        cwd=repository, capture_output=True, text=True,
    ).returncode == 1


def test_verify_and_tag_refuses_compatible_report_for_different_commit(
    tmp_path: Path,
) -> None:
    powershell, git = shutil.which("powershell.exe"), shutil.which("git")
    assert powershell is not None and git is not None
    helper_dir = tmp_path / "tools" / "release"
    helper_dir.mkdir(parents=True)
    shutil.copy2(ROOT / "tools" / "release" / "verify_and_tag.ps1", helper_dir)
    repository = tmp_path / "repository"
    repository.mkdir()
    for command in (
        (git, "init"),
        (git, "config", "user.email", "firewall@example.invalid"),
        (git, "config", "user.name", "Compatibility Firewall"),
    ):
        completed = subprocess.run(command, cwd=repository, capture_output=True, text=True)
        assert completed.returncode == 0, completed.stdout + completed.stderr
    (repository / "tracked.txt").write_text("source A\n", encoding="utf-8")
    subprocess.run((git, "add", "tracked.txt"), cwd=repository, check=True)
    subprocess.run((git, "commit", "-m", "source A"), cwd=repository,
                   check=True, capture_output=True)
    source_a = subprocess.run((git, "rev-parse", "HEAD"), cwd=repository,
                              check=True, capture_output=True, text=True).stdout.strip()
    (repository / "tracked.txt").write_text("source B\n", encoding="utf-8")
    subprocess.run((git, "commit", "-am", "source B"), cwd=repository,
                   check=True, capture_output=True)
    source_b = subprocess.run((git, "rev-parse", "HEAD"), cwd=repository,
                              check=True, capture_output=True, text=True).stdout.strip()
    assert source_a != source_b
    (helper_dir / "verify_release.py").write_text(
        "import json\nprint(json.dumps(" + repr({
            "status": "PASS", "zipVerified": True,
            "compatibilityVerdict": "FULL_COMPATIBLE",
            "sourceCommit": source_a, "version": "9.9.9-different-source",
        }) + "))\n", encoding="utf-8",
    )
    tag = "v9.9.9-different-source"
    completed = subprocess.run(
        (
            powershell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
            str(helper_dir / "verify_and_tag.ps1"), "-ProjectRoot", str(repository),
            "-PythonPath", sys.executable, "-GitPath", git,
            "-OutputRoot", str(tmp_path / "release"),
            "-ZipPath", str(tmp_path / "release.zip"), "-Tag", tag,
            "-Commit", source_b, "-Version", "9.9.9-different-source",
        ), cwd=repository, capture_output=True, text=True,
    )
    assert completed.returncode != 0
    assert "source commit" in (completed.stdout + completed.stderr).lower()
    assert subprocess.run((git, "show-ref", "--verify", "--quiet", f"refs/tags/{tag}"),
                          cwd=repository, capture_output=True).returncode == 1


@pytest.mark.parametrize("mutation", ["duplicate", "case-alias", "extra-field", "reparse"])
def test_candidate_manifest_rejects_nonclosed_or_aliased_rows(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, mutation: str,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    monkeypatch.setattr(firewall, "resolve_tag_pin_nodes", lambda *_: _resolved_fixture_tag())
    manifest_path = bundle / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if mutation == "duplicate":
        manifest["files"].append(dict(manifest["files"][0]))
    elif mutation == "case-alias":
        row = dict(manifest["files"][0])
        row["path"] = row["path"].upper()
        manifest["files"].append(row)
    elif mutation == "extra-field":
        manifest["files"][0]["unexpected"] = True
    else:
        target = manifest["files"][0]["path"]
        original = firewall._path_has_reparse_component
        monkeypatch.setattr(
            firewall, "_path_has_reparse_component",
            lambda root_path, path: path.name == target or original(root_path, path),
        )
    manifest_path.write_bytes(canonical_json_bytes(manifest))

    result = firewall.verify_candidate_bundle_compatibility(
        bundle, root, archive, tmp_path, fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert any(
        marker in error
        for error in result.errors
        for marker in ("candidate-manifest-row-shape", "candidate-manifest-path-alias", "reparse")
    )


@pytest.mark.parametrize("mutation", ["extra-key", "version-type", "kind-type", "files-type"])
def test_candidate_manifest_rejects_nonclosed_top_level_shape(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, mutation: str,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    monkeypatch.setattr(firewall, "resolve_tag_pin_nodes", lambda *_: _resolved_fixture_tag())
    manifest_path = bundle / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    if mutation == "extra-key":
        manifest["unexpected"] = True
    elif mutation == "version-type":
        manifest["version"] = "1"
    elif mutation == "kind-type":
        manifest["kind"] = ["release-candidate"]
    else:
        manifest["files"] = {}
    manifest_path.write_bytes(canonical_json_bytes(manifest))

    result = firewall.verify_candidate_bundle_compatibility(
        bundle, root, archive, tmp_path, fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert "candidate-manifest-shape" in result.errors


@pytest.mark.parametrize("target", ["root", "manifest"])
def test_candidate_reparse_is_rejected_before_resolution_or_member_read(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, target: str,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    monkeypatch.setattr(firewall, "resolve_tag_pin_nodes", lambda *_: _resolved_fixture_tag())
    manifest_path = bundle / "bundle-manifest.json"
    original_lstat = Path.lstat
    original_resolve = Path.resolve

    def fake_lstat(path: Path, *args: object, **kwargs: object) -> object:
        if path == (bundle if target == "root" else manifest_path):
            return SimpleNamespace(st_mode=0o120777, st_file_attributes=0x400)
        return original_lstat(path, *args, **kwargs)

    def guarded_resolve(path: Path, *args: object, **kwargs: object) -> Path:
        if target == "root" and path == bundle:
            raise AssertionError("candidate root resolved before unresolved lstat admission")
        return original_resolve(path, *args, **kwargs)

    monkeypatch.setattr(Path, "lstat", fake_lstat)
    monkeypatch.setattr(Path, "resolve", guarded_resolve)

    result = firewall.verify_candidate_bundle_compatibility(
        bundle, root, archive, tmp_path, fixture_backed=True,
    )

    assert result.verdict == "INCOMPATIBLE"
    assert any(f"candidate-{target}-reparse" in error for error in result.errors)


@pytest.mark.parametrize("mutation", ["stale-zip", "evidence-mismatch"])
def test_release_root_overlap_rejects_stale_zip_and_evidence(
    tmp_path: Path, mutation: str
) -> None:
    root, archive = _release_overlap_fixture(tmp_path)
    if mutation == "stale-zip":
        capabilities = root / "evidence" / "capabilities.json"
        capabilities.write_bytes(capabilities.read_bytes() + b" ")
        sums = root / "SHA256SUMS"
        lines = []
        for line in sums.read_text(encoding="utf-8").splitlines():
            digest, relative = line.split("  ", 1)
            if relative == "evidence/capabilities.json":
                digest = hashlib.sha256(capabilities.read_bytes()).hexdigest().upper()
            lines.append(f"{digest}  {relative}\n")
        sums.write_text("".join(lines), encoding="utf-8")
    else:
        release_path = root / "actorwright-release.json"
        release = json.loads(release_path.read_text(encoding="utf-8"))
        release["capabilitiesSha256"] = "0" * 64
        release_path.write_bytes(canonical_json_bytes(release))

    result = firewall.verify_release_root_compatibility(root, archive)

    assert result.verdict == "INCOMPATIBLE"
    assert result.errors


def test_release_verifier_calls_public_overlap_function(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    calls: list[tuple[Path, Path | None]] = []
    root = tmp_path / "release"
    root.mkdir()
    monkeypatch.setattr(release_verifier, "inventory", lambda _: {})
    monkeypatch.setattr(release_verifier, "verify_hashes", lambda *_: None)
    monkeypatch.setattr(
        release_verifier,
        "verify_metadata",
        lambda _: {
            "version": "fixture", "sourceCommit": "C" * 40,
            "canonicalBuildLogSha256": "D" * 64,
        },
    )
    monkeypatch.setattr(release_verifier, "verify_release_wrapper", lambda *_: None)
    monkeypatch.setattr(release_verifier, "verify_embedded_resource_closures", lambda *_: None)
    monkeypatch.setattr(
        release_verifier,
        "verify_release_root_compatibility",
        lambda release_root, archive=None: calls.append((release_root, archive))
        or SimpleNamespace(compatible=True, errors=()),
        raising=False,
    )

    release_verifier.verify(root)

    assert calls == [(root, None)]


def test_create_zip_runs_sealed_compatibility_before_returning(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = tmp_path / "release"
    root.mkdir()
    archive = tmp_path / "release.zip"
    order: list[str] = []
    monkeypatch.setattr(
        release_verifier,
        "verify",
        lambda *_args, **_kwargs: order.append("release") or {
            "status": "PASS", "version": "fixture", "sourceCommit": "C" * 40,
            "files": 1, "zipVerified": False,
        },
    )
    monkeypatch.setattr(
        release_verifier, "create_zip",
        lambda *_: order.append("create") or archive.write_bytes(b"zip"),
    )
    monkeypatch.setattr(
        release_verifier, "verify_zip", lambda *_: order.append("zip")
    )
    monkeypatch.setattr(release_verifier, "inventory", lambda *_: {})
    monkeypatch.setattr(
        release_verifier, "verify_release_root_compatibility",
        lambda *_: order.append("compatibility")
        or SimpleNamespace(compatible=True, errors=(), verdict="FULL_COMPATIBLE"),
    )
    monkeypatch.setattr(
        sys, "argv",
        ["verify_release.py", str(root), "--create-zip", str(archive), "--json"],
    )

    assert release_verifier.main() == 0
    assert order == ["release", "create", "zip", "compatibility"]


ROOT = Path(__file__).resolve().parents[2]
CONTRACT_ROOT = ROOT / "contracts" / "compatibility" / "v1"
SCHEMA_NAMES = (
    "compatibility-snapshot.schema.json",
    "compatibility-policy.schema.json",
    "compatibility-report.schema.json",
    "consumer-stage-report.schema.json",
)
SENTINELS = [
    "npc voice discover",
    "npc voice import",
    "npc voice synthesize",
    "npc dialogue analyze",
    "npc dialogue apply",
    "npc dialogue verify",
]
EXIT_MEANINGS = [
    {"code": 0, "meaning": "Success"},
    {"code": 1, "meaning": "General failure"},
    {"code": 2, "meaning": "Usage or schema error"},
    {"code": 3, "meaning": "Security refusal"},
    {"code": 4, "meaning": "Domain validation failure"},
    {"code": 5, "meaning": "Cancellation"},
]
PINNED_DOTNET = firewall.PINNED_DOTNET
SOURCE_CLI = ROOT / "src" / "NpcManager.Cli" / "bin" / "Release" / "net10.0" / "actorwright.dll"


def _expected_command_names() -> list[str]:
    snapshot = ROOT / "tests" / "NpcManager.Cli.Tests" / "Preview231CommandSnapshot.cs"
    names = re.findall(
        r'^\s*"([a-z0-9 -]+)",?\s*$',
        snapshot.read_text(encoding="utf-8"),
        flags=re.MULTILINE,
    )
    assert len(names) == 142 and len(set(names)) == 142
    return names


EXPECTED_142 = _expected_command_names()
EXPECTED_SOURCE_ORDER_SHA256 = (
    "0218342F89443C8BDBA380C89DA12B052653FF056CC4A33424D138C8264EA79E"
)


@pytest.fixture
def k_root() -> Path:
    root = firewall.CAPTURE_WORKSPACE_PARENT / f"compatibility-firewall-{uuid.uuid4().hex}"
    root = firewall.prepare_capture_workspace(root.resolve())
    try:
        yield root
    finally:
        shutil.rmtree(root, ignore_errors=True)


def _contract(name: str) -> dict[str, object]:
    return {
        "name": name,
        "aliases": [f"{name}-alias"] if name == "capabilities" else [],
        "commandSchemaVersion": "1",
        "readiness": "v2" if name in {"capabilities", "version", "schema export"} else "legacy",
        "supportedGames": ["fallout4", "skyrimSpecialEdition"],
        "purpose": f"Public help for {name}.",
        "limitations": [],
        "options": [
            {
                "cliName": "example",
                "jsonName": "example",
                "valueKind": "string",
                "required": False,
                "allowedValues": ["one", "two"],
                "conflictsWith": [],
                "secretLike": False,
                "valueSyntax": "one|two",
                "description": "A public example option.",
                "aliasFor": None,
            }
        ] if name == "capabilities" else [],
        "inputArtifactKinds": [],
        "resultSchemaIds": [],
        "effects": [],
        "retryPolicy": "safeUnchanged",
        "determinism": "deterministic",
        "supportsDryRun": False,
        "authority": [],
        "transitions": [],
        "contractStatus": "complete",
        "canonicalCommand": None,
        "optionRelationships": [],
        "inputArtifacts": [],
        "outputArtifacts": [],
        "resultShape": "object",
        "resultDescription": f"Result for {name}.",
    }


def _envelope(command: str, result: object, exit_code: int = 0) -> dict[str, object]:
    return {
        "protocolVersion": "2",
        "schemaVersion": "1",
        "command": command,
        "outcome": "succeeded" if exit_code == 0 else "refused",
        "exitCode": exit_code,
        "requestDigest": "A" * 64,
        "effects": [],
        "diagnostics": [],
        "artifacts": [],
        "authority": [],
        "nextActions": [],
        "result": result,
    }


class FakeCliRunner:
    def __init__(self, path: Path, workspace: Path) -> None:
        self.path = path
        self.workspace = workspace
        self.calls: list[tuple[list[str], dict[str, object]]] = []
        self.mutation_during_probe: str | None = None
        self.corrupt_behavior_probe = False

    def __call__(self, arguments, **kwargs):
        argv = [str(item) for item in arguments]
        self.calls.append((argv, kwargs))
        cli_args = argv[1:]
        if argv[0].casefold().endswith("dotnet.exe"):
            cli_args = argv[2:]

        if cli_args == ["version", "--json"]:
            value = {
                "product": "Actorwright",
                "version": "1.0.0-preview.275",
                "sourceLine": "preview.275-private",
                "protocol": "1",
                "targetFramework": "net10.0-windows",
                "hostRuntime": "10.0.9",
            }
            return subprocess.CompletedProcess(argv, 0, canonical_json_bytes(value), b"")
        if cli_args == ["capabilities", "--json"]:
            value = {
                "product": "Actorwright",
                "version": "1.0.0-preview.275",
                "sourceLine": "preview.275-private",
                "protocol": "1",
                "commands": [
                    {
                        "name": name,
                        "description": f"Public help for {name}.",
                        "schemaVersion": "1",
                        "mutates": False,
                        "supportedGames": ["fallout4", "skyrimSpecialEdition"],
                        "limitations": [],
                    }
                    for name in EXPECTED_142
                ],
                "ledgerMappings": [],
            }
            return subprocess.CompletedProcess(argv, 0, canonical_json_bytes(value), b"")
        if cli_args == ["version", "--protocol", "2", "--json"]:
            value = _envelope(
                "version",
                {
                    "schemaId": "urn:actorwright:protocol-v2:version-result:v1",
                    "productName": "Actorwright",
                    "productVersion": "1.0.0-preview.275",
                    "sourceLine": "preview.275-private",
                    "targetFramework": "net10.0-windows",
                    "protocolVersion": "2",
                    "supportedProtocolVersions": ["1", "2"],
                },
            )
            return subprocess.CompletedProcess(argv, 0, canonical_json_bytes(value), b"")
        if cli_args == ["capabilities", "--protocol", "2", "--json"]:
            value = _envelope(
                "capabilities",
                {
                    "schemaId": "urn:actorwright:protocol-v2:capabilities-result:v1",
                    "protocolVersion": "2",
                    "commands": [_contract(name) for name in EXPECTED_142],
                },
            )
            return subprocess.CompletedProcess(argv, 0, canonical_json_bytes(value), b"")

        behavior_diagnostics = {
            (
                "workspace", "preflight", "--protocol", "2", "--json",
                "--compatibility-unknown", "x",
            ): ["option-unknown"],
            (
                "workspace", "preflight", "--protocol", "2", "--json",
                "--game", "skyrimse", "--game", "skyrimse",
            ): ["option-duplicate"],
            (
                "workspace", "preflight", "--protocol", "2", "--json",
                "--game", "not-a-game",
            ): ["option-enum-value"],
            (
                "workspace", "preflight", "--protocol", "2", "--json",
                "--game", "SKYRIMSE",
            ): ["option-required"],
            (
                "workspace", "preflight", "--protocol", "2", "--json",
                "--game",
            ): ["option-value-required"],
            (
                "npc", "assembly", "preflight", "--protocol", "2", "--json",
            ): ["option-required", "option-required", "option-required"],
        }
        behavior_codes = behavior_diagnostics.get(tuple(cli_args))
        if behavior_codes is not None:
            if self.corrupt_behavior_probe and "option-unknown" in behavior_codes:
                behavior_codes = ["unexpected-diagnostic"]
            value = _envelope(
                "workspace preflight" if cli_args[0] == "workspace" else "npc assembly preflight",
                None,
                2,
            )
            value["diagnostics"] = [
                {"code": code, "severity": "error", "message": code}
                for code in behavior_codes
            ]
            return subprocess.CompletedProcess(argv, 2, canonical_json_bytes(value), b"")

        if "--help" in cli_args and cli_args[-4:] == ["--help", "--protocol", "2", "--json"]:
            name = " ".join(cli_args[:-4])
            value = _envelope(
                name,
                {
                    "schemaId": "urn:actorwright:protocol-v2:scoped-help-result:v1",
                    "contract": _contract(name),
                },
            )
            return subprocess.CompletedProcess(argv, 0, canonical_json_bytes(value), b"")

        if cli_args[:5] == ["schema", "export", "--protocol", "2", "--json"]:
            assert cli_args[5] == "--command"
            name = cli_args[6]
            value = _envelope(
                "schema export",
                {
                    "schemaId": "urn:actorwright:protocol-v2:schema-export-result:v1",
                    "protocolVersion": "2",
                    "scopedHelpResultSchema": {"schemaIdentifier": "help", "jsonSchema": {}},
                    "pathConventions": {},
                    "contract": _contract(name),
                    "resultSchemas": [],
                    "documentSchemas": [],
                },
            )
            return subprocess.CompletedProcess(argv, 0, canonical_json_bytes(value), b"")

        assert cli_args[-3:] == ["--protocol", "1", "--json"]
        name = " ".join(cli_args[:-3])
        if self.mutation_during_probe and name == EXPECTED_142[0]:
            seed = self.workspace / "seed.txt"
            if self.mutation_during_probe == "add":
                (self.workspace / "unexpected.txt").write_text("write", encoding="utf-8")
            elif self.mutation_during_probe == "overwrite":
                seed.write_text("changed", encoding="utf-8")
            elif self.mutation_during_probe == "recreate":
                seed.unlink()
                seed.write_text("recreate", encoding="utf-8")
            else:
                raise AssertionError(self.mutation_during_probe)
        exit_code = 0 if name in {"capabilities", "version", "diagnose", "gui"} else 2
        value = {"status": "available"} if exit_code == 0 else {
            "code": "usage-error",
            "message": f"Missing input for {name}.",
        }
        return subprocess.CompletedProcess(argv, exit_code, canonical_json_bytes(value), b"")


@pytest.fixture
def fake_runner(monkeypatch: pytest.MonkeyPatch, k_root: Path) -> FakeCliRunner:
    tool_root = (
        ROOT / "artifacts" / "test-work" /
        f"compatibility-firewall-entrypoint-{uuid.uuid4().hex}"
    )
    tool_root.mkdir(parents=True)
    entrypoint = tool_root / "actorwright.exe"
    entrypoint.write_bytes(b"fake-cli")
    runner = FakeCliRunner(entrypoint, k_root)
    monkeypatch.setattr(firewall, "_run_bounded_process", runner)
    try:
        yield runner
    finally:
        shutil.rmtree(tool_root, ignore_errors=True)


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def _write_json(path: Path, value: object) -> bytes:
    data = (json.dumps(value, indent=2) + "\n").encode("utf-8")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data)
    return data


def _synthetic_snapshot(member_bytes: bytes) -> dict[str, object]:
    launcher_bytes = b"param([Parameter(ValueFromRemainingArguments=$true)]$Arguments)\n"
    commands = [
        {
            "position": position,
            "name": name,
            "pathTokens": name.split(),
            "aliases": [],
            "options": [],
            "help": {"sha256": "1" * 64},
            "protocol1": True,
            "protocol2Readiness": "legacy",
            "schemas": [],
            "authority": [],
            "effects": [],
            "recovery": [],
            "noInputProbe": {"resultClass": "REFUSED", "exitMeaning": "INVALID_INPUT"},
        }
        for position, name in enumerate(SENTINELS)
    ]
    ordered_names = "\n".join(SENTINELS).encode("utf-8")
    return {
        "$schema": "urn:actorwright:compatibility:v1:snapshot",
        "formatVersion": 1,
        "provenance": {
            "productVersion": "synthetic",
            "sourceLine": "synthetic-private",
            "sourceTag": "synthetic-tag",
            "sourceCommit": "a" * 40,
            "sourceTree": "B" * 40,
            "releaseZip": {"length": 1, "sha256": "C" * 64},
            "capabilitiesSha256": "D" * 64,
            "candidateBundleManifestSha256": "E" * 64,
            "approvedBundleManifestSha256": "F" * 64,
        },
        "commandCount": len(commands),
        "orderedCommandNamesSha256": _sha256(ordered_names),
        "commands": commands,
        "exitMeanings": EXIT_MEANINGS,
        "voiceDialogueSentinels": SENTINELS,
        "members": [
            {
                "id": "journey.synthetic",
                "kind": "journey",
                "path": "journeys/synthetic.json",
                "length": len(member_bytes),
                "sha256": _sha256(member_bytes),
            },
            {
                "id": "package.launcher",
                "kind": "evidence",
                "path": "cli/actorwright.ps1",
                "length": len(launcher_bytes),
                "sha256": _sha256(launcher_bytes),
            },
        ],
        "requiredPackagePaths": [
            {"path": "cli/actorwright.ps1", "role": "launcher"}
        ],
        "launcher": {
            "entrypoint": "cli/actorwright.ps1",
            "shape": "powershell-forwarding-wrapper",
        },
    }


def _write_synthetic_bundle(tmp_path: Path) -> tuple[Path, Path]:
    bundle = tmp_path / "bundle"
    member_bytes = _write_json(
        bundle / "journeys" / "synthetic.json",
        {"formatVersion": 1, "id": "journey.synthetic"},
    )
    launcher = bundle / "cli" / "actorwright.ps1"
    launcher.parent.mkdir(parents=True, exist_ok=True)
    launcher.write_bytes(b"param([Parameter(ValueFromRemainingArguments=$true)]$Arguments)\n")
    manifest_bytes = _write_json(
        bundle / "manifest.json",
        _synthetic_snapshot(member_bytes),
    )
    registry = tmp_path / "compatibility-baselines.json"
    _write_json(
        registry,
        {
            "formatVersion": 1,
            "baselines": [
                {
                    "id": "synthetic",
                    "manifestPath": "manifest.json",
                    "manifestLength": len(manifest_bytes),
                    "manifestSha256": _sha256(manifest_bytes),
                }
            ],
        },
    )
    return bundle, registry


def _public_synthetic_snapshot(member_bytes: bytes) -> dict[str, object]:
    snapshot = _synthetic_snapshot(member_bytes)
    snapshot["$schema"] = firewall.SYNTHETIC_SNAPSHOT_SCHEMA_ID
    snapshot["formatVersion"] = 2
    snapshot["provenance"] = {
        "kind": "public-synthetic",
        "productVersion": "synthetic",
        "sourceLine": "synthetic-private",
        "source": {
            "configuration": "Release",
            "commit": "a" * 40,
            "tree": "b" * 40,
            "sourceClosure": {
                "root": "src", "tree": "c" * 40,
                "fileCount": 1, "sha256": "d" * 64,
            },
            "cli": {"length": 1, "sha256": "1" * 64},
            "managedAssembly": {"length": 1, "sha256": "2" * 64},
            "selectorBuildEvidence": [
                "buildProject=NpcManager.Cli.Tests", "buildExit=0",
                "buildStdoutBase64=MA==", "buildStderrBase64=",
                "buildProject=NpcManager.Architecture.Tests", "buildExit=0",
                "buildStdoutBase64=MA==", "buildStderrBase64=",
            ],
        },
        "candidate": {
            "configuration": "Debug",
            "privateOnly": True,
            "runtimeAuthority": False,
            "packageBuild": {
                "configuration": "Debug",
                "exitCode": 0,
                "command": [
                    "pwsh.exe", "-NoProfile", "-NonInteractive", "-File",
                    "tools/build/package.ps1", "-Configuration", "Debug",
                    "-Runtime", "win-x64", "-DotNetPath", "<pinned-dotnet>",
                    "-PythonPath", "<python>", "-OutputRoot", "<candidate-output>",
                ],
                "powershell": {"name": "pwsh.exe", "length": 1, "sha256": "3" * 64},
                "packageScript": {"length": 1, "sha256": "4" * 64},
                "python": {"length": 1, "sha256": "5" * 64},
                "stdout": {"length": 1, "sha256": "6" * 64},
                "stderr": {"length": 0, "sha256": "7" * 64},
            },
            "manifest": {"length": 1, "sha256": "8" * 64},
            "cli": {"length": 1, "sha256": "9" * 64},
        },
        "harness": {
            "commit": "a" * 40,
            "tree": "b" * 40,
            "sourceClosure": {
                "root": "tests/NpcManager.Cli.Tests", "tree": "a" * 40,
                "fileCount": 1, "sha256": "b" * 64,
            },
            "runner": {"length": 1, "sha256": "c" * 64},
        },
        "pinnedDotnet": {"length": 1, "sha256": "d" * 64},
    }
    return snapshot


def _write_public_synthetic_bundle(tmp_path: Path) -> tuple[Path, Path]:
    bundle = tmp_path / "bundle"
    member_bytes = _write_json(
        bundle / "journeys" / "synthetic.json",
        {"formatVersion": 1, "id": "journey.synthetic"},
    )
    launcher = bundle / "cli" / "actorwright.ps1"
    launcher.parent.mkdir(parents=True, exist_ok=True)
    launcher_bytes = b"param([Parameter(ValueFromRemainingArguments=$true)]$Arguments)\n"
    launcher.write_bytes(launcher_bytes)
    snapshot = _public_synthetic_snapshot(member_bytes)
    snapshot["members"][1]["length"] = len(launcher_bytes)
    snapshot["members"][1]["sha256"] = _sha256(launcher_bytes)
    manifest_bytes = _write_json(bundle / "manifest.json", snapshot)
    registry = tmp_path / "compatibility-baselines.json"
    _write_json(registry, {
        "formatVersion": 1,
        "baselines": [{
            "id": "publicSynthetic",
            "manifestPath": "manifest.json",
            "manifestLength": len(manifest_bytes),
            "manifestSha256": _sha256(manifest_bytes),
        }],
    })
    return bundle, registry


def _policy(
    *,
    semantic_rules: list[dict[str, object]] | None = None,
    accepted_differences: list[dict[str, object]] | None = None,
) -> dict[str, object]:
    return {
        "$schema": "urn:actorwright:compatibility:v1:policy",
        "formatVersion": 1,
        "semanticRules": semantic_rules or [],
        "acceptedDifferences": accepted_differences or [],
    }


def _semantic_rule(
    pointer: str,
    normalizer: str = "operation-id",
    workflow_id: str = "journey.synthetic",
) -> dict[str, object]:
    return {
        "workflowId": workflow_id,
        "pointer": pointer,
        "normalizer": normalizer,
    }


def _capture_root(name: str) -> str:
    return str(firewall.CAPTURE_WORKSPACE_PARENT / name)


def _accepted_difference(**overrides: object) -> dict[str, object]:
    value: dict[str, object] = {
        "issueId": "NPCM-20260922-1001",
        "rationale": "A proven correction with focused regression coverage.",
        "regressionSelector": "--test-synthetic-regression",
        "affectedCommand": "npc finish verify",
        "workflowId": "journey.synthetic",
        "pointer": "/semantics/result",
        "baselineValue": "old",
        "candidateValue": "new",
    }
    value.update(overrides)
    return value


def _rewrite_manifest(bundle: Path, mutation) -> None:
    path = bundle / "manifest.json"
    document = json.loads(path.read_text(encoding="utf-8"))
    mutation(document)
    _write_json(path, document)


def _write_non_finite_default(bundle: Path, constant: str) -> bytes:
    path = bundle / "manifest.json"
    document = json.loads(path.read_text(encoding="utf-8"))
    document["commands"][0]["options"] = [
        {
            "name": "example",
            "tokens": ["--example"],
            "valueKind": "number",
            "arity": "one",
            "required": False,
            "repeatable": False,
            "hasDefault": True,
            "defaultValue": "NON_FINITE_SENTINEL",
            "allowedValues": [],
            "caseSensitive": True,
            "requires": [],
            "excludes": [],
            "diagnostics": {
                "missing": "missing",
                "invalid": "invalid",
                "duplicate": "duplicate",
                "unknown": "unknown",
            },
        }
    ]
    data = (json.dumps(document, indent=2) + "\n").encode("utf-8")
    data = data.replace(b'"NON_FINITE_SENTINEL"', constant.encode("ascii"))
    path.write_bytes(data)
    return data


def test_schema_contracts_are_closed_and_format_version_one() -> None:
    for name in SCHEMA_NAMES:
        schema = json.loads((CONTRACT_ROOT / name).read_text(encoding="utf-8"))
        assert schema["$schema"] == "https://json-schema.org/draft/2020-12/schema"
        assert schema["type"] == "object"
        assert schema["additionalProperties"] is False
        assert schema["properties"]["formatVersion"] == {"const": 1}


def test_policy_schema_requires_nonblank_affected_command() -> None:
    schema = json.loads(
        (CONTRACT_ROOT / "compatibility-policy.schema.json").read_text(
            encoding="utf-8"
        )
    )

    affected_command = schema["$defs"]["acceptedDifference"]["properties"][
        "affectedCommand"
    ]
    assert affected_command == {
        "type": "string",
        "minLength": 1,
        "pattern": r"\S",
    }


@pytest.mark.parametrize(
    ("value", "valid"),
    [
        (None, False),
        ("", False),
        (" \t", False),
        ("npc finish verify", True),
    ],
)
def test_policy_schema_and_runtime_agree_on_affected_command(
    value: object,
    valid: bool,
) -> None:
    schema = json.loads(
        (CONTRACT_ROOT / "compatibility-policy.schema.json").read_text(
            encoding="utf-8"
        )
    )
    command_schema = schema["$defs"]["acceptedDifference"]["properties"][
        "affectedCommand"
    ]
    schema_accepts = (
        isinstance(value, str)
        and len(value) >= command_schema["minLength"]
        and re.search(command_schema.get("pattern", ""), value) is not None
    )

    runtime_accepts = True
    try:
        firewall.validate_policy(
            _policy(
                accepted_differences=[
                    _accepted_difference(affectedCommand=value)
                ]
            )
        )
    except FirewallError:
        runtime_accepts = False

    assert schema_accepts is valid
    assert runtime_accepts is valid


def test_snapshot_schema_freezes_ordered_commands_and_exact_sentinels() -> None:
    schema = json.loads(
        (CONTRACT_ROOT / "compatibility-snapshot.schema.json").read_text(
            encoding="utf-8"
        )
    )
    assert schema["properties"]["commands"]["type"] == "array"
    command_reference = schema["properties"]["commands"]["items"]["$ref"]
    assert command_reference == "#/$defs/command"
    assert schema["$defs"]["command"]["additionalProperties"] is False
    sentinels = schema["properties"]["voiceDialogueSentinels"]
    assert sentinels["minItems"] == sentinels["maxItems"] == 6
    assert [item["const"] for item in sentinels["prefixItems"]] == SENTINELS


def test_snapshot_schema_freezes_ordered_closed_exit_meanings() -> None:
    schema = json.loads(
        (CONTRACT_ROOT / "compatibility-snapshot.schema.json").read_text(
            encoding="utf-8"
        )
    )
    exit_meanings = schema["properties"]["exitMeanings"]
    assert exit_meanings["minItems"] == exit_meanings["maxItems"] == 6
    assert [
        {
            "code": item["properties"]["code"]["const"],
            "meaning": item["properties"]["meaning"]["const"],
        }
        for item in exit_meanings["prefixItems"]
    ] == EXIT_MEANINGS
    assert all(
        item["additionalProperties"] is False
        and item["required"] == ["code", "meaning"]
        for item in exit_meanings["prefixItems"]
    )


def test_public_synthetic_schema_freezes_build_identity_fields() -> None:
    schema = json.loads(
        (firewall.REPOSITORY_ROOT / "contracts" / "compatibility" / "v2"
         / "compatibility-snapshot.schema.json").read_text(encoding="utf-8")
    )
    provenance = schema["$defs"]["provenance"]
    source = provenance["properties"]["source"]
    candidate = provenance["properties"]["candidate"]

    assert "managedAssembly" in source["required"]
    assert "selectorBuildEvidence" in source["required"]
    assert candidate["properties"]["configuration"]["enum"] == ["Debug", "Release"]
    assert "packageBuild" in candidate["required"]
    assert schema["$defs"]["packageBuild"]["properties"]["exitCode"] == {"const": 0}


def test_report_schemas_freeze_exact_gate_states() -> None:
    expected = ["PASS", "FAIL", "BLOCKED", "NOT_APPLICABLE"]
    report = json.loads(
        (CONTRACT_ROOT / "compatibility-report.schema.json").read_text(
            encoding="utf-8"
        )
    )
    stage = json.loads(
        (CONTRACT_ROOT / "consumer-stage-report.schema.json").read_text(
            encoding="utf-8"
        )
    )
    assert report["$defs"]["gate"]["properties"]["status"]["enum"] == expected
    assert stage["properties"]["gates"]["items"] == {"$ref": "#/$defs/gate"}
    assert stage["$defs"]["gate"]["properties"]["status"]["enum"] == expected


def test_report_schema_gate_differences_are_structured() -> None:
    report = json.loads(
        (CONTRACT_ROOT / "compatibility-report.schema.json").read_text(
            encoding="utf-8"
        )
    )
    gate_differences = report["$defs"]["gate"]["properties"]["differences"]
    assert gate_differences == {
        "type": "array",
        "items": {"$ref": "#/$defs/difference"},
    }


def test_report_schema_requires_truthful_fixture_and_installation_flags() -> None:
    report = json.loads(
        (CONTRACT_ROOT / "compatibility-report.schema.json").read_text(
            encoding="utf-8"
        )
    )

    assert "fixtureBacked" in report["required"]
    assert "realInstallation" in report["required"]
    assert report["properties"]["fixtureBacked"] == {"type": "boolean"}
    assert report["properties"]["realInstallation"] == {"const": False}
    assert report["allOf"] == [
        {
            "if": {"properties": {"fixtureBacked": {"const": True}}},
            "then": {"properties": {"realInstallation": {"const": False}}},
        },
        {
            "if": {"properties": {"tier": {"not": {"const": "Activation"}}}},
            "then": {"properties": {"realInstallation": {"const": False}}},
        },
    ]


def test_manifest_load_authenticates_external_digest_and_members(tmp_path: Path) -> None:
    bundle, registry = _write_synthetic_bundle(tmp_path)
    baseline = load_baseline(
        "synthetic", registry_path=registry, bundle_root=bundle
    )
    assert baseline.baseline_id == "synthetic"
    assert baseline.snapshot["voiceDialogueSentinels"] == SENTINELS
    assert baseline.member_paths == (
        bundle / "journeys" / "synthetic.json",
        bundle / "cli" / "actorwright.ps1",
    )


def test_public_synthetic_v2_load_binds_candidate_build_and_source_assembly(
    tmp_path: Path,
) -> None:
    bundle, registry = _write_public_synthetic_bundle(tmp_path)

    baseline = load_baseline(
        "publicSynthetic", registry_path=registry, bundle_root=bundle
    )

    provenance = baseline.snapshot["provenance"]
    assert provenance["source"]["managedAssembly"]["length"] == 1
    assert len(provenance["source"]["selectorBuildEvidence"]) == 8
    assert provenance["candidate"]["configuration"] == "Debug"
    assert provenance["candidate"]["packageBuild"]["exitCode"] == 0
    assert provenance["candidate"]["packageBuild"]["command"][6] == "Debug"


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (
            lambda document: document["provenance"]["candidate"].__setitem__(
                "configuration", "Release"
            ),
            "build configuration mismatch",
        ),
        (
            lambda document: document["provenance"]["candidate"]["packageBuild"]
            ["command"].__setitem__(6, "Release"),
            "package invocation differs",
        ),
        (
            lambda document: document["provenance"]["harness"].__setitem__(
                "tree", "f" * 40
            ),
            "Git identities differ",
        ),
    ],
)
def test_public_synthetic_v2_rejects_candidate_or_harness_identity_drift(
    tmp_path: Path, mutation, message: str,
) -> None:
    bundle, _ = _write_public_synthetic_bundle(tmp_path)
    _rewrite_manifest(bundle, mutation)

    with pytest.raises(FirewallError, match=message):
        validate_snapshot(bundle / "manifest.json")


def test_public_synthetic_v2_load_rejects_changed_member_bytes(
    tmp_path: Path,
) -> None:
    bundle, registry = _write_public_synthetic_bundle(tmp_path)
    (bundle / "journeys" / "synthetic.json").write_bytes(b"changed")

    with pytest.raises(FirewallError, match=r"member sha256 mismatch \(journey.synthetic\)"):
        load_baseline("publicSynthetic", registry_path=registry, bundle_root=bundle)


def test_synthetic_candidate_builder_records_invoked_configuration_and_logs(
    k_root: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    repository = k_root / "synthetic-root"
    package_script = repository / "tools" / "build" / "package.ps1"
    package_script.parent.mkdir(parents=True)
    original_script_bytes = b"# package fixture\n"
    package_script.write_bytes(original_script_bytes)
    package_parent = repository / "artifacts" / "packages"
    package_parent.mkdir(parents=True)
    candidate = package_parent / "compatibility-firewall-candidate-test"
    powershell = repository / "pwsh.exe"
    powershell.write_bytes(b"fixture powershell")
    sdk = repository / "dotnet.exe"
    sdk.write_bytes(b"fixture SDK")
    capture_root = k_root / "capture"
    capture_root.mkdir()
    observed: dict[str, object] = {}

    def fake_process(arguments, **kwargs):
        observed["arguments"] = arguments
        observed["kwargs"] = kwargs
        candidate.mkdir()
        package_script.write_bytes(b"# package changed during build\n")
        stdout = f"Actorwright package: PASS {candidate}\n".encode()
        return subprocess.CompletedProcess(arguments, 0, stdout=stdout, stderr=b"")

    monkeypatch.setattr(firewall.shutil, "which", lambda name: str(powershell))
    monkeypatch.setattr(firewall, "_run_bounded_process", fake_process)

    build = firewall._build_synthetic_candidate(
        repository=repository,
        output_root=candidate,
        configuration="Debug",
        dotnet_path=sdk,
        capture_root=capture_root,
    )

    assert build["configuration"] == "Debug"
    assert build["exitCode"] == 0
    assert build["command"][6] == "Debug"
    assert observed["arguments"][6] == "Debug"
    assert observed["kwargs"]["shell"] is False
    assert build["packageScript"] == {
        "length": len(original_script_bytes),
        "sha256": _sha256(original_script_bytes),
    }
    assert build["stdout"]["sha256"] == _sha256(
        capture_root.with_name(capture_root.name + ".package.stdout.log").read_bytes()
    )
    assert build["stderr"]["length"] == 0


def test_synthetic_candidate_builder_refuses_retained_log_before_build(
    k_root: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    repository = k_root / "synthetic-root"
    package_script = repository / "tools" / "build" / "package.ps1"
    package_script.parent.mkdir(parents=True)
    package_script.write_bytes(b"# package fixture\n")
    package_parent = repository / "artifacts" / "packages"
    package_parent.mkdir(parents=True)
    candidate = package_parent / "compatibility-firewall-candidate-retained-log"
    powershell = repository / "pwsh.exe"
    powershell.write_bytes(b"fixture powershell")
    sdk = repository / "dotnet.exe"
    sdk.write_bytes(b"fixture SDK")
    capture_root = k_root / "capture-retained-log"
    capture_root.mkdir()
    stdout_log = capture_root.with_name(capture_root.name + ".package.stdout.log")
    stdout_log.write_bytes(b"preserve prior failure evidence")
    invoked = False

    def unexpected_process(*_arguments, **_kwargs):
        nonlocal invoked
        invoked = True
        raise AssertionError("package process should not run with a retained log")

    monkeypatch.setattr(firewall.shutil, "which", lambda _name: str(powershell))
    monkeypatch.setattr(firewall, "_run_bounded_process", unexpected_process)

    with pytest.raises(FirewallError, match="log destination already exists"):
        firewall._build_synthetic_candidate(
            repository=repository,
            output_root=candidate,
            configuration="Debug",
            dotnet_path=sdk,
            capture_root=capture_root,
        )

    assert not invoked
    assert stdout_log.read_bytes() == b"preserve prior failure evidence"
    assert not candidate.exists()


def test_public_capture_passes_requested_candidate_output_root(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    repository = tmp_path / "repository"
    source = repository / "src/NpcManager.Cli/bin/Release/net10.0/actorwright.exe"
    source.parent.mkdir(parents=True)
    source.write_bytes(b"source apphost")
    sdk = tmp_path / "dotnet.exe"
    sdk.write_bytes(b"pinned SDK")
    candidate = repository / "artifacts/packages/compatibility-firewall-candidate-requested"
    capture = tmp_path / "compatibility-firewall-capture-test"
    harness = {
        "commit": "a" * 40,
        "tree": "b" * 40,
        "sourceClosure": {
            "root": "tests/NpcManager.Cli.Tests",
            "tree": "c" * 40,
            "fileCount": 1,
            "sha256": "d" * 64,
        },
    }
    observed: dict[str, Path] = {}

    def stop_after_candidate_build(**kwargs):
        observed["candidate"] = kwargs["output_root"]
        raise FirewallError("stop after candidate path assertion")

    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", repository)
    monkeypatch.setattr(firewall, "CAPTURE_WORKSPACE_PARENT", tmp_path)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", sdk)
    monkeypatch.setattr(firewall, "_current_harness_expectation", lambda _root: harness)
    monkeypatch.setattr(
        firewall, "authenticate_harness_checkout", lambda *_args, **_kwargs: harness
    )
    monkeypatch.setattr(
        firewall, "_git_source_closure", lambda _root, _path: {"root": "src"}
    )
    monkeypatch.setattr(
        firewall, "_authenticated_pinned_sdk", lambda: (len(b"pinned SDK"), "e" * 64)
    )
    monkeypatch.setattr(firewall, "_build_synthetic_candidate", stop_after_candidate_build)

    with pytest.raises(FirewallError, match="stop after candidate path assertion"):
        firewall.capture_public_synthetic_baseline(
            repository_root=repository,
            source_cli=source,
            candidate_output_root=candidate,
            candidate_configuration="Debug",
            output_root=capture,
        )

    assert observed["candidate"] == candidate
    assert not capture.exists()


@pytest.mark.parametrize("launcher_state", ["missing", "tampered"])
def test_public_capture_rejects_bad_launcher_before_selector_builds(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    launcher_state: str,
) -> None:
    repository = tmp_path / "repository"
    source = repository / "src/NpcManager.Cli/bin/Release/net10.0/actorwright.exe"
    source.parent.mkdir(parents=True)
    source.write_bytes(b"source apphost")
    sdk = tmp_path / "dotnet.exe"
    sdk.write_bytes(b"pinned SDK")
    candidate = repository / "artifacts/packages/compatibility-firewall-candidate-launcher"
    capture = tmp_path / f"compatibility-firewall-capture-launcher-{launcher_state}"
    harness = {
        "commit": "a" * 40,
        "tree": "b" * 40,
        "sourceClosure": {
            "root": "tests/NpcManager.Cli.Tests",
            "tree": "c" * 40,
            "fileCount": 1,
            "sha256": "d" * 64,
        },
    }
    selector_build_called = False

    def build_candidate(**kwargs):
        output = Path(kwargs["output_root"])
        launcher = output / "cli" / "actorwright.ps1"
        launcher.parent.mkdir(parents=True)
        (launcher.parent / "actorwright.exe").write_bytes(b"candidate apphost")
        manifest = {
            "privateOnly": True,
            "runtimeAuthority": False,
            "files": [],
        }
        if launcher_state == "tampered":
            launcher_bytes = b"tampered wrapper"
            launcher.write_bytes(launcher_bytes)
            manifest["files"] = [{
                "path": "cli/actorwright.ps1",
                "size": len(launcher_bytes),
                "sha256": hashlib.sha256(launcher_bytes).hexdigest().upper(),
            }]
        (output / "manifest.json").write_text(
            json.dumps(manifest), encoding="utf-8")
        return {"configuration": "Debug", "exitCode": 0}

    def unexpected_selector_build():
        nonlocal selector_build_called
        selector_build_called = True
        pytest.fail("selector builds must follow synthetic launcher validation")

    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", repository)
    monkeypatch.setattr(firewall, "CAPTURE_WORKSPACE_PARENT", tmp_path)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", sdk)
    monkeypatch.setattr(firewall, "_current_harness_expectation", lambda _root: harness)
    monkeypatch.setattr(
        firewall, "authenticate_harness_checkout", lambda *_args, **_kwargs: harness
    )
    monkeypatch.setattr(
        firewall, "_git_source_closure", lambda _root, _path: {"root": "src"}
    )
    monkeypatch.setattr(
        firewall, "_authenticated_pinned_sdk", lambda: (len(b"pinned SDK"), "e" * 64)
    )
    monkeypatch.setattr(firewall, "_build_synthetic_candidate", build_candidate)
    monkeypatch.setattr(
        release_verifier, "verify_package_staging", lambda *_args, **_kwargs: {}
    )
    monkeypatch.setattr(
        firewall, "_build_source_selector_projects", unexpected_selector_build
    )

    with pytest.raises(FirewallError, match="synthetic candidate.*launcher"):
        firewall.capture_public_synthetic_baseline(
            repository_root=repository,
            source_cli=source,
            candidate_output_root=candidate,
            candidate_configuration="Debug",
            output_root=capture,
        )

    assert not selector_build_called
    assert not capture.exists()


def test_trust_anchor_rejects_manifest_mutation(tmp_path: Path) -> None:
    bundle, registry = _write_synthetic_bundle(tmp_path)
    _rewrite_manifest(
        bundle,
        lambda document: document["commands"][0].__setitem__("name", "changed"),
    )
    with pytest.raises(FirewallError, match="baseline manifest sha256 mismatch"):
        load_baseline("synthetic", registry_path=registry, bundle_root=bundle)


def test_manifest_rejects_unknown_field(tmp_path: Path) -> None:
    bundle, _ = _write_synthetic_bundle(tmp_path)
    _rewrite_manifest(bundle, lambda document: document.__setitem__("surprise", True))
    with pytest.raises(FirewallError, match="unknown field"):
        validate_snapshot(bundle / "manifest.json")


def test_manifest_rejects_duplicate_json_key(tmp_path: Path) -> None:
    bundle, _ = _write_synthetic_bundle(tmp_path)
    path = bundle / "manifest.json"
    path.write_text(
        '{"$schema":"urn:actorwright:compatibility:v1:snapshot",'
        '"formatVersion":1,"formatVersion":1}\n',
        encoding="utf-8",
    )
    with pytest.raises(FirewallError, match="duplicate JSON key: formatVersion"):
        validate_snapshot(path)


@pytest.mark.parametrize("constant", ["NaN", "Infinity", "-Infinity"])
@pytest.mark.parametrize("through_registry", [False, True])
def test_manifest_rejects_non_json_numeric_constants(
    tmp_path: Path, constant: str, through_registry: bool
) -> None:
    bundle, registry = _write_synthetic_bundle(tmp_path)
    manifest_bytes = _write_non_finite_default(bundle, constant)
    if through_registry:
        registry_document = json.loads(registry.read_text(encoding="utf-8"))
        registry_document["baselines"][0]["manifestLength"] = len(manifest_bytes)
        registry_document["baselines"][0]["manifestSha256"] = _sha256(
            manifest_bytes
        )
        _write_json(registry, registry_document)
        operation = lambda: load_baseline(
            "synthetic", registry_path=registry, bundle_root=bundle
        )
    else:
        operation = lambda: validate_snapshot(bundle / "manifest.json")

    with pytest.raises(
        FirewallError,
        match=f"invalid JSON numeric constant: {constant}",
    ):
        operation()


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (
            lambda document: document["commands"][0].__setitem__("position", 2),
            "command position",
        ),
        (
            lambda document: document["voiceDialogueSentinels"].reverse(),
            "voice/dialogue sentinels",
        ),
        (
            lambda document: document["provenance"].pop("sourceCommit"),
            "missing field",
        ),
    ],
)
def test_manifest_schema_rejects_contract_drift(
    tmp_path: Path, mutation, message: str
) -> None:
    bundle, _ = _write_synthetic_bundle(tmp_path)
    _rewrite_manifest(bundle, mutation)
    with pytest.raises(FirewallError, match=message):
        validate_snapshot(bundle / "manifest.json")


@pytest.mark.parametrize(
    "mutation",
    [
        lambda document: document["exitMeanings"].reverse(),
        lambda document: document["exitMeanings"][0].__setitem__(
            "meaning", "Succeeded"
        ),
        lambda document: document["exitMeanings"][0].__setitem__(
            "surprise", True
        ),
    ],
)
def test_manifest_rejects_exit_meaning_drift(tmp_path: Path, mutation) -> None:
    bundle, _ = _write_synthetic_bundle(tmp_path)
    _rewrite_manifest(bundle, mutation)
    with pytest.raises(FirewallError, match="exit meanings|unknown field"):
        validate_snapshot(bundle / "manifest.json")


@pytest.mark.parametrize("field", ["length", "sha256"])
def test_manifest_load_rejects_member_pin_drift(tmp_path: Path, field: str) -> None:
    bundle, registry = _write_synthetic_bundle(tmp_path)
    manifest_path = bundle / "manifest.json"
    document = json.loads(manifest_path.read_text(encoding="utf-8"))
    document["members"][0][field] = 0 if field == "length" else "0" * 64
    manifest_bytes = _write_json(manifest_path, document)
    registry_document = json.loads(registry.read_text(encoding="utf-8"))
    registry_document["baselines"][0]["manifestLength"] = len(manifest_bytes)
    registry_document["baselines"][0]["manifestSha256"] = _sha256(manifest_bytes)
    _write_json(registry, registry_document)

    with pytest.raises(FirewallError, match=f"member {field} mismatch"):
        load_baseline("synthetic", registry_path=registry, bundle_root=bundle)


def test_manifest_load_requires_explicit_synthetic_registry_and_bundle_roots(
    tmp_path: Path,
) -> None:
    bundle, registry = _write_synthetic_bundle(tmp_path)
    assert registry.parent == tmp_path
    assert bundle.parent == tmp_path
    loaded = load_baseline(
        "synthetic", registry_path=registry.resolve(), bundle_root=bundle.resolve()
    )
    assert loaded.manifest_path == (bundle / "manifest.json").resolve()


def test_capture_run_cli_uses_exact_bounded_process_contract(
    fake_runner: FakeCliRunner,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("DOTNET_ROOT", r"C:\Program Files\dotnet")
    monkeypatch.setenv("DOTNET_ROOT_X64", r"C:\Program Files\dotnet")
    monkeypatch.setenv("DOTNET_MULTILEVEL_LOOKUP", "1")
    target = CliTarget(
        "source",
        fake_runner.path,
        k_root,
        hashlib.sha256(b"fake-cli").hexdigest(),
    )

    observed = run_cli(target, ["version", "--json"], timeout_seconds=7)

    assert observed.exit_code == 0
    assert observed.json_value["version"] == "1.0.0-preview.275"
    arguments, kwargs = fake_runner.calls[-1]
    assert arguments == [str(fake_runner.path), "version", "--json"]
    assert kwargs["shell"] is False
    assert kwargs["timeout"] == 7
    assert kwargs["cwd"] == str(k_root)
    assert kwargs["max_output_bytes"] == firewall.CLI_MAX_OUTPUT_BYTES
    assert kwargs["env"]["ACTORWRIGHT_WORKSPACE_ROOT"] == str(k_root)
    assert kwargs["env"]["DOTNET_ROOT"] == str(PINNED_DOTNET.parent)
    assert kwargs["env"]["DOTNET_ROOT_X64"] == str(PINNED_DOTNET.parent)
    assert kwargs["env"]["DOTNET_MULTILEVEL_LOOKUP"] == "0"


@pytest.mark.parametrize("invalid_dotnet", ["missing", "substituted"])
def test_capture_run_cli_rejects_invalid_pinned_runtime_before_launch(
    fake_runner: FakeCliRunner,
    k_root: Path,
    invalid_dotnet: str,
) -> None:
    invalid_path = k_root / "dotnet.exe"
    if invalid_dotnet == "substituted":
        invalid_path.write_bytes(b"untrusted-dotnet")
    target = CliTarget(
        "source",
        fake_runner.path,
        k_root,
        hashlib.sha256(b"fake-cli").hexdigest(),
        invalid_path,
    )

    with pytest.raises(FirewallError, match="pinned dotnet"):
        run_cli(target, ["version", "--json"])
    assert fake_runner.calls == []


@pytest.mark.parametrize("reparse_component", ["file", "ancestor"])
def test_capture_run_cli_rejects_reparse_alias_to_pinned_runtime(
    fake_runner: FakeCliRunner,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
    reparse_component: str,
) -> None:
    alias_parent = k_root / "runtime-alias"
    alias_parent.mkdir()
    alias = alias_parent / "dotnet.exe"
    alias.write_bytes(b"alias")
    reparse_path = alias if reparse_component == "file" else alias_parent
    original_lstat = Path.lstat
    original_resolve = Path.resolve

    def fake_lstat(path: Path, *args: object, **kwargs: object) -> object:
        if path == reparse_path:
            return SimpleNamespace(st_mode=0o120777, st_file_attributes=0x400)
        return original_lstat(path, *args, **kwargs)

    def fake_resolve(path: Path, *args: object, **kwargs: object) -> Path:
        if path == alias:
            return PINNED_DOTNET.resolve()
        return original_resolve(path, *args, **kwargs)

    monkeypatch.setattr(Path, "lstat", fake_lstat)
    monkeypatch.setattr(Path, "resolve", fake_resolve)
    target = CliTarget("source", fake_runner.path, k_root, None, alias)

    with pytest.raises(FirewallError, match="pinned dotnet.*reparse"):
        run_cli(target, ["version", "--json"])
    assert fake_runner.calls == []


def test_capture_run_cli_invokes_dll_through_supplied_pinned_dotnet(
    fake_runner: FakeCliRunner,
    k_root: Path,
) -> None:
    dll = k_root / "actorwright.dll"
    dll.write_bytes(b"fake-dll")
    target = CliTarget(
        "source",
        dll,
        k_root,
        hashlib.sha256(b"fake-dll").hexdigest(),
        PINNED_DOTNET,
    )

    run_cli(target, ["version", "--json"])

    arguments, _ = fake_runner.calls[-1]
    assert arguments == [str(PINNED_DOTNET), str(dll), "version", "--json"]


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (lambda target: target.__class__(target.name, Path("actorwright.exe"), target.workspace_root, None), "absolute"),
        (lambda target: target.__class__(target.name, target.entrypoint, target.workspace_root, "0" * 64), "sha256"),
    ],
)
def test_capture_run_cli_rejects_untrusted_entrypoint(
    fake_runner: FakeCliRunner,
    k_root: Path,
    mutation,
    message: str,
) -> None:
    target = CliTarget("source", fake_runner.path, k_root, None)
    with pytest.raises(FirewallError, match=message):
        run_cli(mutation(target), ["version", "--json"])


def test_capture_run_cli_enforces_stdout_bound(
    fake_runner: FakeCliRunner,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def oversized(arguments, **kwargs):
        raise FirewallError("CLI stdout exceeded 16-byte bound")

    monkeypatch.setattr(firewall, "_run_bounded_process", oversized)
    target = CliTarget("source", fake_runner.path, k_root, None)
    with pytest.raises(FirewallError, match="stdout.*16"):
        run_cli(target, ["version", "--json"], max_output_bytes=16)


def test_capture_run_cli_parses_json_from_single_nonempty_stderr(
    fake_runner: FakeCliRunner,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    value = {"code": "usage-error", "message": "Missing input."}

    def stderr_json(arguments, **kwargs):
        return subprocess.CompletedProcess(arguments, 2, b"", canonical_json_bytes(value))

    monkeypatch.setattr(firewall, "_run_bounded_process", stderr_json)
    observed = run_cli(
        CliTarget("source", fake_runner.path, k_root, None),
        ["workspace", "preflight", "--protocol", "1", "--json"],
    )

    assert observed.exit_code == 2
    assert observed.stdout == b""
    assert observed.stderr == canonical_json_bytes(value)
    assert observed.json_value == value


def test_capture_canonical_json_is_stable_and_strict(tmp_path: Path) -> None:
    assert canonical_json_bytes({"z": 1, "a": [True, None]}) == (
        b'{"a":[true,null],"z":1}\n'
    )
    package_manifest = tmp_path / "manifest.json"
    package_manifest.write_bytes(b'\xef\xbb\xbf{"schemaVersion":1}')
    assert firewall._load_json(package_manifest) == {"schemaVersion": 1}
    with pytest.raises(FirewallError, match="duplicate JSON key"):
        firewall.parse_json_output(b'{"value":1,"value":2}\n', "test")
    with pytest.raises(FirewallError, match="invalid JSON numeric constant"):
        firewall.parse_json_output(b'{"value":NaN}\n', "test")


def test_capture_requires_exact_142_order_and_six_sentinels(
    fake_runner: FakeCliRunner,
    k_root: Path,
) -> None:
    snapshot = capture_cli_contract(
        CliTarget("source", fake_runner.path, k_root, None)
    )

    assert [command["name"] for command in snapshot["commands"]] == EXPECTED_142
    assert snapshot["voiceDialogueSentinels"] == SENTINELS
    assert snapshot["commandCount"] == 142
    assert snapshot["exitMeanings"] == EXIT_MEANINGS
    assert snapshot["workspaceInventoryBeforeNoInput"] == (
        snapshot["workspaceInventoryAfterNoInput"]
    )
    assert all(
        call[1]["shell"] is False and call[1]["timeout"] == firewall.CLI_TIMEOUT_SECONDS
        for call in fake_runner.calls
    )

    capabilities = next(
        command for command in snapshot["commands"]
        if command["name"] == "capabilities"
    )
    assert capabilities["pathTokens"] == ["capabilities"]
    assert capabilities["aliases"] == ["capabilities-alias"]
    assert capabilities["help"]["document"]["contract"]["purpose"] == (
        "Public help for capabilities."
    )
    assert capabilities["schemaExport"]["document"]["contract"]["name"] == "capabilities"
    assert capabilities["noInputProbe"]["exitCode"] == 0


def test_capture_emits_only_per_field_facts_backed_by_declared_probes(
    fake_runner: FakeCliRunner,
    k_root: Path,
) -> None:
    snapshot = capture_cli_contract(
        CliTarget("source", fake_runner.path, k_root, None)
    )
    capabilities = next(
        command for command in snapshot["commands"]
        if command["name"] == "capabilities"
    )
    option = capabilities["protocol2Contract"]["options"][0]

    assert option["cliName"] == "example"
    assert "options" not in capabilities
    assert snapshot["behaviorFacts"] == [
        {
            "command": "workspace preflight",
            "option": "game",
            "field": "repeatable",
            "value": False,
            "evidenceProbe": "duplicate-option",
        },
        {
            "command": "workspace preflight",
            "option": "game",
            "field": "invalidDiagnostic",
            "value": "option-enum-value",
            "evidenceProbe": "invalid-enum",
        },
        {
            "command": "workspace preflight",
            "option": "game",
            "field": "caseSensitive",
            "value": False,
            "evidenceProbe": "accepted-enum-case",
        },
        {
            "command": "workspace preflight",
            "option": "game",
            "field": "missingValueDiagnostic",
            "value": "option-value-required",
            "evidenceProbe": "missing-option-value",
        },
    ]
    assert [probe["id"] for probe in snapshot["behaviorProbes"]] == [
        "unknown-option",
        "duplicate-option",
        "invalid-enum",
        "accepted-enum-case",
        "missing-option-value",
        "required-option",
    ]


def test_capture_fails_closed_when_declared_behavior_probe_does_not_match(
    fake_runner: FakeCliRunner,
    k_root: Path,
) -> None:
    fake_runner.corrupt_behavior_probe = True
    with pytest.raises(FirewallError, match="behavior probe unknown-option"):
        capture_cli_contract(CliTarget("source", fake_runner.path, k_root, None))


def test_ambiguous_default_prose_is_not_promoted_to_semantic_fact(
    fake_runner: FakeCliRunner,
    k_root: Path,
) -> None:
    snapshot = capture_cli_contract(CliTarget("source", fake_runner.path, k_root, None))
    capability = next(row for row in snapshot["commands"] if row["name"] == "capabilities")
    capability["protocol2Contract"]["options"][0]["description"] = (
        "Selection defaults to 1 and must remain compatible."
    )

    assert not any(fact["field"] in {"hasDefault", "defaultValue"} for fact in snapshot["behaviorFacts"])


def test_capture_fails_closed_when_required_per_field_probe_evidence_is_missing(
    fake_runner: FakeCliRunner,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    catalogue = [dict(item) for item in firewall.BEHAVIOR_PROBE_CATALOGUE]
    catalogue[1] = {**catalogue[1], "facts": []}
    monkeypatch.setattr(firewall, "BEHAVIOR_PROBE_CATALOGUE", tuple(catalogue))

    with pytest.raises(FirewallError, match="required behavior fact.*repeatable"):
        capture_cli_contract(CliTarget("source", fake_runner.path, k_root, None))


def test_capture_requires_protocol2_envelope_and_process_exit_agreement(
    fake_runner: FakeCliRunner,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    original = fake_runner.__call__

    def disagree(arguments, **kwargs):
        result = original(arguments, **kwargs)
        argv = [str(value) for value in arguments]
        if argv[-4:] == ["version", "--protocol", "2", "--json"]:
            value = json.loads(result.stdout)
            value["exitCode"] = 2
            return subprocess.CompletedProcess(arguments, 0, canonical_json_bytes(value), b"")
        return result

    monkeypatch.setattr(firewall, "_run_bounded_process", disagree)
    with pytest.raises(FirewallError, match="envelope exitCode 2.*process exit 0"):
        capture_cli_contract(CliTarget("source", fake_runner.path, k_root, None))


def test_capture_rejects_no_input_workspace_mutation(
    fake_runner: FakeCliRunner,
    k_root: Path,
) -> None:
    fake_runner.mutation_during_probe = "add"
    with pytest.raises(FirewallError, match="no-input probes changed workspace"):
        capture_cli_contract(CliTarget("source", fake_runner.path, k_root, None))


@pytest.mark.parametrize("mutation", ["overwrite", "recreate"])
def test_capture_detects_same_path_workspace_mutation(
    fake_runner: FakeCliRunner,
    k_root: Path,
    mutation: str,
) -> None:
    (k_root / "seed.txt").write_text("original", encoding="utf-8")
    fake_runner.mutation_during_probe = mutation

    with pytest.raises(FirewallError, match="no-input probes changed workspace"):
        capture_cli_contract(CliTarget("source", fake_runner.path, k_root, None))


def test_capture_rejects_repository_and_unowned_workspace_roots(
    fake_runner: FakeCliRunner,
    k_root: Path,
) -> None:
    unowned = ROOT / "artifacts" / "test-work" / f"compatibility-firewall-unowned-{uuid.uuid4().hex}"
    unowned.mkdir(parents=True)
    try:
        with pytest.raises(
            FirewallError,
            match="approved artifacts/test-work boundary|owned capture workspace",
        ):
            run_cli(CliTarget("source", fake_runner.path, unowned, None), ["version", "--json"])
        with pytest.raises(FirewallError, match="approved.*test-work|owned capture workspace"):
            run_cli(CliTarget("source", fake_runner.path, ROOT, None), ["version", "--json"])
    finally:
        shutil.rmtree(unowned, ignore_errors=True)


def test_capture_bounded_reader_terminates_noisy_process() -> None:
    with pytest.raises(FirewallError, match="stdout exceeded 1024-byte bound"):
        firewall._run_bounded_process(
            [sys.executable, "-c", "import sys,time;sys.stdout.write('x'*1048576);sys.stdout.flush();time.sleep(30)"],
            cwd=str(ROOT),
            env=dict(os.environ),
            shell=False,
            timeout=10,
            max_output_bytes=1024,
        )


def test_capture_source_package_disagreement_reports_full_pointer(
    fake_runner: FakeCliRunner,
    k_root: Path,
) -> None:
    source = capture_cli_contract(CliTarget("source", fake_runner.path, k_root, None))
    package = json.loads(canonical_json_bytes(source))
    package["target"] = "package"
    package["commands"][0]["help"]["document"]["contract"]["purpose"] = "Changed."

    difference = find_capture_disagreement(source, package)

    first_name = EXPECTED_142[0]
    assert difference == {
        "layer": "source-package",
        "command": first_name,
        "pointer": "/commands/0/help/document/contract/purpose",
        "expected": f"Public help for {first_name}.",
        "actual": "Changed.",
        "expectedSha256": hashlib.sha256(
            canonical_json_bytes(f"Public help for {first_name}.")
        ).hexdigest().upper(),
        "actualSha256": hashlib.sha256(canonical_json_bytes("Changed.")).hexdigest().upper(),
    }


def test_baseline_projection_ignores_only_unretained_probe_document(
    fake_runner: FakeCliRunner,
    k_root: Path,
) -> None:
    source = capture_cli_contract(CliTarget("source", fake_runner.path, k_root, None))
    package = json.loads(canonical_json_bytes(source))
    package["target"] = "package"
    source["commands"][135]["noInputProbe"]["stdout"]["document"] = {
        "probedUtc": "2026-09-22T12:00:00Z"
    }
    package["commands"][135]["noInputProbe"]["stdout"]["document"] = {
        "probedUtc": "2026-09-22T12:00:01Z"
    }

    assert firewall._find_baseline_projection_disagreement(source, package) is None

    package["commands"][0]["protocol2Contract"]["readiness"] = "changed"
    disagreement = firewall._find_baseline_projection_disagreement(source, package)
    assert disagreement is not None
    assert disagreement["pointer"] == "/commands/0/protocol2Readiness"


def test_capture_current_source_cli_contract(k_root: Path) -> None:
    assert SOURCE_CLI.is_file(), (
        "Build the source CLI first with pinned SDK 10.0.301: " + str(SOURCE_CLI)
    )
    assert PINNED_DOTNET.is_file()

    snapshot = capture_cli_contract(
        CliTarget(
            "source",
            SOURCE_CLI.resolve(),
            k_root,
            hashlib.sha256(SOURCE_CLI.read_bytes()).hexdigest(),
            PINNED_DOTNET,
        )
    )

    observed_names = [command["name"] for command in snapshot["commands"]]
    assert len(observed_names) == 142
    assert set(observed_names) == set(EXPECTED_142)
    assert snapshot["orderedCommandNamesSha256"] == EXPECTED_SOURCE_ORDER_SHA256
    assert snapshot["voiceDialogueSentinels"] == SENTINELS
    assert snapshot["workspaceInventoryBeforeNoInput"] == (
        snapshot["workspaceInventoryAfterNoInput"]
    )


def test_authenticated_cleanup_failure_is_visible(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    disposable = tmp_path / "compatibility-firewall-authenticated-stuck"
    disposable.mkdir()
    monkeypatch.setattr(
        firewall.shutil,
        "rmtree",
        lambda path: (_ for _ in ()).throw(PermissionError("cleanup denied")),
    )

    with pytest.raises(FirewallError, match="could not remove disposable.*cleanup denied"):
        firewall._remove_disposable_path(disposable)


def test_release_inventory_rejects_count_preserving_path_substitution() -> None:
    archive_members = [
        "actorwright-1.0.0-preview.275/SHA256SUMS",
        "actorwright-1.0.0-preview.275/cli/actorwright.exe",
        "actorwright-1.0.0-preview.275/evidence/capabilities.json",
    ]
    checksum_paths = {
        "cli/actorwright.exe",
        "evidence/substituted.json",
    }

    with pytest.raises(
        FirewallError,
        match="inventory path closure mismatch.*capabilities.json.*substituted.json",
    ):
        firewall._validate_release_inventory_closure(
            archive_members,
            "actorwright-1.0.0-preview.275",
            checksum_paths,
        )


def test_authenticated_cleanup_attempts_every_action_and_aggregates_errors(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    worktree = tmp_path / "worktree"
    source = tmp_path / "source"
    package = tmp_path / "package"
    for path in (worktree, source, package):
        path.mkdir()
    actions: list[str] = []

    def failing_git(repository_root: Path, *arguments: str) -> bytes:
        action = " ".join(arguments[:2])
        actions.append(action)
        raise FirewallError(f"{action} denied")

    def partial_remove(path: Path) -> None:
        actions.append(f"remove {path.name}")
        if path == source:
            raise FirewallError("source removal denied")
        shutil.rmtree(path)

    monkeypatch.setattr(firewall, "_git", failing_git)
    monkeypatch.setattr(firewall, "_remove_disposable_path", partial_remove)

    with pytest.raises(FirewallError, match="cleanup failed.*worktree remove denied.*source removal denied.*worktree prune denied"):
        firewall._cleanup_authenticated_capture(
            ROOT,
            worktree,
            registered=True,
            disposable_paths=(source, package, worktree),
        )

    assert actions == [
        "worktree remove",
        "remove source",
        "remove package",
        "remove worktree",
        "worktree prune",
    ]
    assert not package.exists()
    assert not worktree.exists()


def _comparison_snapshot() -> dict[str, object]:
    snapshot = _synthetic_snapshot(b"{}\n")
    snapshot["commands"][0]["aliases"] = ["voice-discover"]
    snapshot["commands"][0]["options"] = [
        {
            "name": "input",
            "tokens": ["--input", "-i"],
            "valueKind": "path",
            "arity": "one",
            "required": True,
            "repeatable": False,
            "hasDefault": False,
            "defaultValue": None,
            "allowedValues": [],
            "caseSensitive": True,
            "requires": [],
            "excludes": [],
            "diagnostics": {
                "missing": "option-required",
                "invalid": "option-invalid",
                "duplicate": "option-duplicate",
                "unknown": "option-unknown",
            },
        }
    ]
    snapshot["commands"][0]["schemas"] = [
        {"role": "result", "id": "urn:synthetic:result:v1", "sha256": "2" * 64}
    ]
    return snapshot


@pytest.mark.parametrize(
    ("pointer", "mutate"),
    [
        ("/commands/0/aliases/0", lambda value: value["commands"][0]["aliases"].__setitem__(0, "changed")),
        ("/commands/0/options/0/tokens/0", lambda value: value["commands"][0]["options"][0]["tokens"].__setitem__(0, "--changed")),
        ("/commands/0/options/0/required", lambda value: value["commands"][0]["options"][0].__setitem__("required", False)),
        ("/commands/0/options/0/valueKind", lambda value: value["commands"][0]["options"][0].__setitem__("valueKind", "string")),
        ("/commands/0/help/sha256", lambda value: value["commands"][0]["help"].__setitem__("sha256", "3" * 64)),
        ("/commands/0/schemas/0/sha256", lambda value: value["commands"][0]["schemas"][0].__setitem__("sha256", "4" * 64)),
        ("/commands/0/protocol2Readiness", lambda value: value["commands"][0].__setitem__("protocol2Readiness", "v2")),
        ("/provenance/sourceCommit", lambda value: value["provenance"].__setitem__("sourceCommit", "9" * 40)),
    ],
)
def test_compare_reports_one_focused_difference_for_each_leaf_mutation(
    pointer: str,
    mutate,
) -> None:
    baseline = _comparison_snapshot()
    candidate = deepcopy(baseline)
    mutate(candidate)

    differences = firewall.compare_snapshots(baseline, candidate, _policy())

    assert [difference.pointer for difference in differences] == [pointer]
    difference = differences[0]
    assert difference.layer == "baseline-candidate"
    expected_subject = (
        "npc voice discover" if pointer.startswith("/commands/0/") else "snapshot"
    )
    assert difference.subject == expected_subject
    assert difference.gate == "exact-comparison"
    assert difference.expected != difference.actual


def test_compare_preserves_command_and_array_order() -> None:
    baseline = {
        "commands": [
            {"name": "first", "position": 0},
            {"name": "second", "position": 1},
        ],
        "authority": ["read", "write"],
    }
    candidate = deepcopy(baseline)
    candidate["commands"][0], candidate["commands"][1] = (
        candidate["commands"][1],
        candidate["commands"][0],
    )
    candidate["authority"].reverse()

    differences = firewall.compare_snapshots(baseline, candidate, _policy())
    pointers = {difference.pointer for difference in differences}

    assert "/commands/0/name" in pointers
    assert "/commands/1/name" in pointers
    assert "/authority/0" in pointers
    assert "/authority/1" in pointers


def test_compare_reports_each_leaf_removed_from_protected_data() -> None:
    baseline = _comparison_snapshot()
    candidate = deepcopy(baseline)
    del candidate["commands"][0]["schemas"][0]

    differences = firewall.compare_snapshots(baseline, candidate, _policy())

    assert {difference.pointer for difference in differences} == {
        "/commands/0/schemas/0/role",
        "/commands/0/schemas/0/id",
        "/commands/0/schemas/0/sha256",
    }
    assert all(difference.actual == firewall._MISSING_EVIDENCE for difference in differences)


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        ("add", "commandCount.*does not match"),
        ("remove", "voice/dialogue sentinels mismatch"),
        ("reorder", "exit meanings mismatch"),
    ],
)
def test_compare_rejects_malformed_protected_array_add_remove_and_reorder(
    mutation: str,
    message: str,
) -> None:
    baseline = _comparison_snapshot()
    candidate = deepcopy(baseline)
    if mutation == "add":
        added = deepcopy(candidate["commands"][-1])
        added["position"] = len(candidate["commands"])
        added["name"] = "synthetic added command"
        added["pathTokens"] = ["synthetic", "added", "command"]
        candidate["commands"].append(added)
    elif mutation == "remove":
        candidate["voiceDialogueSentinels"].pop()
    else:
        candidate["exitMeanings"][0], candidate["exitMeanings"][1] = (
            candidate["exitMeanings"][1],
            candidate["exitMeanings"][0],
        )

    with pytest.raises(FirewallError, match=message):
        firewall.compare_snapshots(baseline, candidate, _policy())


def test_compare_distinguishes_protected_bool_int_and_int_float_mutations() -> None:
    baseline = {
        "commands": [
            {"name": "synthetic", "position": 0, "protocol1": True},
        ]
    }
    candidate = deepcopy(baseline)
    candidate["commands"][0]["position"] = 0.0
    candidate["commands"][0]["protocol1"] = 1

    differences = firewall.compare_snapshots(baseline, candidate, _policy())

    assert [(difference.pointer, difference.expected, difference.actual) for difference in differences] == [
        ("/commands/0/position", 0, 0.0),
        ("/commands/0/protocol1", True, 1),
    ]


@pytest.mark.parametrize(
    ("mutate", "message"),
    [
        (lambda value: value.__setitem__("formatVersion", 2), "unsupported format version"),
        (lambda value: value.__setitem__("formatVersion", "1"), "unsupported format version"),
        (lambda value: value.__setitem__("$schema", "urn:wrong"), "unsupported snapshot schema"),
        (lambda value: value["commands"][0].pop("name"), "missing field name"),
        (lambda value: value["commands"][0].__setitem__("protocol1", 1), "expected boolean"),
        (lambda value: value["commands"][0].__setitem__("name", "changed"), "pathTokens.*command name"),
        (lambda value: value["exitMeanings"][0].__setitem__("meaning", "Changed"), "exit meanings mismatch"),
        (lambda value: value["voiceDialogueSentinels"].__setitem__(0, "changed"), "voice/dialogue sentinels mismatch"),
    ],
)
def test_compare_rejects_every_malformed_snapshot_before_comparison(
    mutate,
    message: str,
) -> None:
    baseline = _comparison_snapshot()
    candidate = deepcopy(baseline)
    mutate(candidate)

    with pytest.raises(FirewallError, match=message):
        firewall.compare_snapshots(baseline, candidate, _policy())


@pytest.mark.parametrize(
    ("field", "value", "message"),
    [
        ("formatVersion", 2, "unsupported format version"),
        ("formatVersion", "1", "unsupported format version"),
        ("target", "installed", "unsupported snapshot target"),
        ("id", "", "invalid snapshot identity"),
    ],
)
def test_compare_rejects_malformed_capture_discriminators(
    field: str,
    value: object,
    message: str,
) -> None:
    baseline = {"formatVersion": 1, "target": "source", "id": "capture.source"}
    candidate = deepcopy(baseline)
    candidate[field] = value

    with pytest.raises(FirewallError, match=message):
        firewall.compare_snapshots(baseline, candidate, _policy())


def test_compare_rejects_unknown_data_before_emitting_differences() -> None:
    baseline = _comparison_snapshot()
    candidate = deepcopy(baseline)
    candidate["commands"][0]["surprise"] = True

    with pytest.raises(FirewallError, match=r"unknown field.*/commands/0/surprise"):
        firewall.compare_snapshots(baseline, candidate, _policy())


def test_compare_rejects_unknown_data_even_when_both_snapshots_contain_it() -> None:
    baseline = _comparison_snapshot()
    baseline["commands"][0]["surprise"] = True

    with pytest.raises(FirewallError, match=r"unknown field.*/commands/0"):
        firewall.compare_snapshots(baseline, deepcopy(baseline), _policy())


@pytest.mark.parametrize(
    "pointer",
    [
        "/$schema",
        "/formatVersion",
        "/target",
        "/id",
        "/commands/0/name",
        "/commands/0/help",
        "/commands/0/schemas/0/sha256",
        "/commands/0/protocol2Readiness",
        "/commands/0/noInputProbe/exitMeaning",
        "/exitMeanings/0/code",
        "/voiceDialogueSentinels/0",
        "/provenance/sourceCommit",
        "/requiredPackagePaths/0/path",
        "/pinRows/0/producerValue",
        "/semantics/provenance/sourceCommit",
        "/semantics/release/packageSha256",
        "/semantics/kind",
        "/semantics/role",
    ],
)
def test_policy_cannot_normalize_protected_pointer(pointer: str) -> None:
    with pytest.raises(FirewallError, match="protected compatibility field"):
        firewall.validate_policy(_policy(semantic_rules=[_semantic_rule(pointer)]))


@pytest.mark.parametrize(
    ("pointer", "message"),
    [
        ("commands/0/name", "full JSON Pointer"),
        ("/semantics/*/operationId", "wildcard"),
        ("/semantics", "leaf field"),
        ("/commands", "protected compatibility field"),
    ],
)
def test_policy_rejects_non_exact_or_subtree_pointer(pointer: str, message: str) -> None:
    with pytest.raises(FirewallError, match=message):
        firewall.validate_policy(_policy(semantic_rules=[_semantic_rule(pointer)]))


def test_policy_rejects_unknown_normalizer() -> None:
    with pytest.raises(FirewallError, match="unknown normalizer"):
        firewall.validate_policy(
            _policy(semantic_rules=[_semantic_rule("/operationId", "basename")])
        )


@pytest.mark.parametrize(
    ("normalizer", "pointer"),
    [
        ("temporary-root", "/operationId"),
        ("generated-timestamp", "/workspaceRoot"),
        ("operation-id", "/generatedAtUtc"),
    ],
)
def test_policy_normalizers_are_limited_to_their_named_leaf(
    normalizer: str,
    pointer: str,
) -> None:
    with pytest.raises(FirewallError, match="leaf field"):
        firewall.validate_policy(
            _policy(semantic_rules=[_semantic_rule(pointer, normalizer)])
        )


def test_compare_normalizes_only_exact_declared_parsed_fields() -> None:
    baseline_root = _capture_root("compatibility-firewall-a1")
    candidate_root = _capture_root("compatibility-firewall-b2")
    baseline = {
        "id": "journey.synthetic",
        "workspaceRoot": baseline_root,
        "outputRoot": str(Path(baseline_root) / "output"),
        "generatedAtUtc": "2026-09-21T12:00:00Z",
        "operationId": "11111111111111111111111111111111",
        "artifact": r"K:\Actorwright\artifacts\test-work\compatibility-firewall-a1\output\result.json",
    }
    candidate = {
        "id": "journey.synthetic",
        "workspaceRoot": candidate_root,
        "outputRoot": str(Path(candidate_root) / "output"),
        "generatedAtUtc": "2026-09-22T13:00:00Z",
        "operationId": "22222222222222222222222222222222",
        "artifact": r"K:\Actorwright\artifacts\test-work\compatibility-firewall-b2\output\result.json",
    }
    policy = _policy(
        semantic_rules=[
            _semantic_rule("/workspaceRoot", "temporary-root"),
            _semantic_rule("/outputRoot", "temporary-root"),
            _semantic_rule("/generatedAtUtc", "generated-timestamp"),
            _semantic_rule("/operationId", "operation-id"),
        ]
    )

    differences = firewall.compare_snapshots(baseline, candidate, policy)

    assert [difference.pointer for difference in differences] == ["/artifact"]
    assert differences[0].expected == baseline["artifact"]
    assert differences[0].actual == candidate["artifact"]


def test_compare_preserves_temporary_output_root_suffix() -> None:
    baseline = {
        "id": "journey.synthetic",
        "outputRoot": str(
            Path(_capture_root("compatibility-firewall-a1")) / "output"
        ),
    }
    candidate = {
        "id": "journey.synthetic",
        "outputRoot": str(
            Path(_capture_root("compatibility-firewall-b2")) / "other-output"
        ),
    }

    differences = firewall.compare_snapshots(
        baseline,
        candidate,
        _policy(
            semantic_rules=[
                _semantic_rule("/outputRoot", "temporary-root")
            ]
        ),
    )

    assert [difference.pointer for difference in differences] == ["/outputRoot"]
    assert differences[0].expected == baseline["outputRoot"]
    assert differences[0].actual == candidate["outputRoot"]


@pytest.mark.parametrize(
    ("normalizer", "pointer", "baseline_value", "candidate_value", "message"),
    [
        (
            "temporary-root",
            "/workspaceRoot",
            r"F:\ExampleGame",
            _capture_root("compatibility-firewall-b2"),
            "owned K-local temporary root",
        ),
        (
            "temporary-root",
            "/workspaceRoot",
            r"K:\Actorwright",
            _capture_root("compatibility-firewall-b2"),
            "owned K-local temporary root",
        ),
        (
            "temporary-root",
            "/workspaceRoot",
            r"K:\outside\artifacts\test-work\compatibility-firewall-a1",
            _capture_root("compatibility-firewall-b2"),
            "owned K-local temporary root",
        ),
        (
            "temporary-root",
            "/workspaceRoot",
            _capture_root("create-finish-a1"),
            _capture_root("compatibility-firewall-b2"),
            "owned K-local temporary root",
        ),
        (
            "temporary-root",
            "/workspaceRoot",
            str(
                Path(_capture_root("compatibility-firewall-a1"))
                / "output"
                / "result.json"
            ),
            _capture_root("compatibility-firewall-b2"),
            "owned root value",
        ),
        (
            "temporary-root",
            "/outputRoot",
            _capture_root("compatibility-firewall-a1"),
            str(Path(_capture_root("compatibility-firewall-b2")) / "output"),
            "safe descendant value",
        ),
        (
            "temporary-root",
            "/outputRoot",
            _capture_root("compatibility-firewall-a1") + r"\..\escape",
            str(Path(_capture_root("compatibility-firewall-b2")) / "output"),
            "owned K-local temporary root",
        ),
        (
            "generated-timestamp",
            "/generatedAtUtc",
            "not-a-timestamp",
            "2026-09-22T13:00:00Z",
            "UTC generated timestamp",
        ),
        (
            "operation-id",
            "/operationId",
            "not-an-operation-id",
            "22222222222222222222222222222222",
            "operation ID",
        ),
    ],
)
def test_compare_rejects_invalid_semantic_normalizer_values(
    normalizer: str,
    pointer: str,
    baseline_value: str,
    candidate_value: str,
    message: str,
) -> None:
    field = pointer.removeprefix("/")
    baseline = {"id": "journey.synthetic", field: baseline_value}
    candidate = {"id": "journey.synthetic", field: candidate_value}

    with pytest.raises(FirewallError, match=message):
        firewall.compare_snapshots(
            baseline,
            candidate,
            _policy(semantic_rules=[_semantic_rule(pointer, normalizer)]),
        )


def test_compare_keeps_nested_release_pin_mutation_exact_and_unacceptable() -> None:
    baseline = {
        "id": "journey.synthetic",
        "semantics": {
            "pinRows": [
                {
                    "producer": "release-manifest",
                    "consumer": "candidate",
                    "producerValue": "A" * 64,
                    "consumerValue": "A" * 64,
                }
            ]
        },
    }
    candidate = deepcopy(baseline)
    candidate["semantics"]["pinRows"][0]["consumerValue"] = "B" * 64

    differences = firewall.compare_snapshots(baseline, candidate, _policy())

    assert [difference.pointer for difference in differences] == [
        "/semantics/pinRows/0/consumerValue"
    ]
    with pytest.raises(FirewallError, match="protected compatibility field"):
        firewall.validate_policy(
            _policy(
                accepted_differences=[
                    _accepted_difference(
                        pointer="/semantics/pinRows/0/consumerValue",
                        baselineValue="A" * 64,
                        candidateValue="B" * 64,
                    )
                ]
            )
        )


@pytest.mark.parametrize("missing_from", ["baseline", "candidate"])
def test_compare_requires_normalized_pointer_in_both_snapshots(missing_from: str) -> None:
    baseline = {"id": "journey.synthetic", "operationId": "baseline"}
    candidate = {"id": "journey.synthetic", "operationId": "candidate"}
    del (baseline if missing_from == "baseline" else candidate)["operationId"]

    with pytest.raises(FirewallError, match="both snapshots.*operationId"):
        firewall.compare_snapshots(
            baseline,
            candidate,
            _policy(semantic_rules=[_semantic_rule("/operationId")]),
        )


def test_compare_rejects_unused_or_duplicate_semantic_policy_rule() -> None:
    baseline = {"id": "journey.other", "operationId": "same"}

    with pytest.raises(FirewallError, match="unused semantic rule"):
        firewall.compare_snapshots(
            baseline,
            deepcopy(baseline),
            _policy(semantic_rules=[_semantic_rule("/operationId")]),
        )

    duplicate = _semantic_rule("/operationId")
    with pytest.raises(FirewallError, match="duplicate semantic rule"):
        firewall.validate_policy(
            _policy(semantic_rules=[duplicate, deepcopy(duplicate)])
        )


@pytest.mark.parametrize(
    "missing",
    [
        "issueId",
        "rationale",
        "regressionSelector",
        "pointer",
        "baselineValue",
        "candidateValue",
        "affectedCommand",
    ],
)
def test_policy_rejects_incomplete_accepted_difference(missing: str) -> None:
    accepted = _accepted_difference()
    del accepted[missing]

    with pytest.raises(FirewallError, match=f"missing field {missing}"):
        firewall.validate_policy(_policy(accepted_differences=[accepted]))


@pytest.mark.parametrize(
    ("overrides", "message"),
    [
        ({"issueId": "temporary"}, "stable issue ID"),
        ({"rationale": "  "}, "rationale"),
        ({"regressionSelector": "later"}, "regression selector"),
        ({"affectedCommand": None}, "affectedCommand"),
        ({"affectedCommand": "  "}, "affectedCommand"),
        ({"pointer": "/commands/0/name"}, "protected compatibility field"),
        ({"pointer": "/semantics/kind"}, "protected compatibility field"),
        ({"pointer": "/semantics/*/result"}, "wildcard"),
    ],
)
def test_policy_rejects_unsafe_accepted_difference(overrides, message: str) -> None:
    with pytest.raises(FirewallError, match=message):
        firewall.validate_policy(
            _policy(accepted_differences=[_accepted_difference(**overrides)])
        )


def test_compare_accepts_only_exact_declared_old_and_new_values() -> None:
    baseline = {"id": "journey.synthetic", "semantics": {"result": "old"}}
    candidate = {"id": "journey.synthetic", "semantics": {"result": "new"}}
    policy = _policy(accepted_differences=[_accepted_difference()])

    assert firewall.compare_snapshots(baseline, candidate, policy) == []

    changed_again = deepcopy(candidate)
    changed_again["semantics"]["result"] = "unexpected"
    with pytest.raises(FirewallError, match="unused accepted difference"):
        firewall.compare_snapshots(baseline, changed_again, policy)


@pytest.mark.parametrize(
    ("baseline_value", "candidate_value", "policy_baseline", "policy_candidate"),
    [
        (True, 1, 1, True),
        (1, 1.0, 1.0, 1),
    ],
)
def test_accepted_difference_matching_requires_strict_json_types(
    baseline_value: object,
    candidate_value: object,
    policy_baseline: object,
    policy_candidate: object,
) -> None:
    baseline = {
        "id": "journey.synthetic",
        "semantics": {"result": baseline_value},
    }
    candidate = {
        "id": "journey.synthetic",
        "semantics": {"result": candidate_value},
    }
    policy = _policy(
        accepted_differences=[
            _accepted_difference(
                baselineValue=policy_baseline,
                candidateValue=policy_candidate,
            )
        ]
    )

    with pytest.raises(FirewallError, match="unused accepted difference"):
        firewall.compare_snapshots(baseline, candidate, policy)


def test_compare_removed_nullable_leaf_has_unambiguous_missing_evidence() -> None:
    baseline = {"id": "journey.synthetic", "semantics": {"optional": None}}
    candidate = {"id": "journey.synthetic", "semantics": {}}

    differences = firewall.compare_snapshots(baseline, candidate, _policy())

    assert len(differences) == 1
    assert differences[0].pointer == "/semantics/optional"
    assert differences[0].expected is None
    assert differences[0].actual == {
        "$actorwrightCompatibilityPresence": (
            "missing-v1-6e2cf75d7a8c4f3aa2a7894981b14cf1"
        )
    }
    assert differences[0].expected != differences[0].actual


def test_unchanged_recapture_has_no_differences_or_accepted_differences(
    fake_runner: FakeCliRunner,
    k_root: Path,
) -> None:
    snapshot = capture_cli_contract(CliTarget("source", fake_runner.path, k_root, None))
    policy_path = (
        ROOT / "tests" / "fixtures" / "compatibility-firewall" /
        "compatibility-policy.json"
    )
    policy = json.loads(policy_path.read_text(encoding="utf-8"))

    assert policy["acceptedDifferences"] == []
    assert firewall.compare_snapshots(snapshot, deepcopy(snapshot), policy) == []


def _required_gate_set(
    tier: str,
    *,
    replace: dict[str, str | None] | None = None,
) -> list[object]:
    replacements = replace or {}
    results: list[object] = []
    for gate_id in firewall.required_gate_ids(tier):
        status = replacements.get(gate_id, "PASS")
        if status is None:
            continue
        results.append(firewall.GateResult(gate_id, True, status, ("explicit test evidence",), ()))
    return results


@pytest.mark.parametrize("tier", ["Source", "Full"])
@pytest.mark.parametrize("status", ["FAIL", "BLOCKED", "NOT_APPLICABLE", None])
def test_required_gate_never_passes_without_explicit_pass(
    tier: str,
    status: str | None,
) -> None:
    gate_id = firewall.required_gate_ids(tier)[0]
    result = firewall.evaluate_tier(
        tier,
        _required_gate_set(tier, replace={gate_id: status}),
    )

    assert result.compatible is False
    assert result.verdict == "INCOMPATIBLE"


def test_tier_rejects_duplicate_required_gate_even_when_both_say_pass() -> None:
    gates = _required_gate_set("Source")
    gates.append(gates[0])

    assert firewall.evaluate_tier("Source", gates).verdict == "INCOMPATIBLE"


@pytest.mark.parametrize("status", ["PASS", "FAIL", "BLOCKED", "NOT_APPLICABLE"])
def test_tier_rejects_unknown_supplied_gate_even_when_declared_optional(
    status: str,
) -> None:
    gates = _required_gate_set("Source")
    gates.append(
        firewall.GateResult(
            "unknown:untrusted-gate", False, status, ("untrusted",), ()
        )
    )

    assert firewall.evaluate_tier("Source", gates).verdict == "INCOMPATIBLE"


def test_tiers_are_closed_complete_plan_inventories_not_task4_subsets() -> None:
    selector_ids = {
        "selector:preview231-command-set",
        "selector:preview261-v1-command-verification",
        "selector:agent-protocol-compatibility",
        "selector:agent-protocol-registry",
        "selector:skyrim-npc-voice-cli",
        "selector:skyrim-dialogue-command-binding",
        "selector:skyrim-dialogue-cli",
    }
    package_ids = {f"package:{command}" for command in SENTINELS}
    source_foundation = {
        "contract:manifest-format",
        "contract:command-surface",
        "contract:voice-dialogue-sentinels",
        "contract:metadata-help-schema-protocol-exit",
        "dispatch:no-input-no-write",
        "comparison:source-baseline",
        "verification:focused-tests",
    }
    full_only = {
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
    }

    assert set(firewall.required_gate_ids("Source")) == selector_ids | source_foundation
    assert set(firewall.required_gate_ids("Full")) == (
        selector_ids | source_foundation | package_ids | full_only
    )
    assert firewall.evaluate_tier("Source", _required_gate_set("Source")).verdict == "SOURCE_COMPATIBLE"
    assert firewall.evaluate_tier("Full", _required_gate_set("Full")).verdict == "FULL_COMPATIBLE"

    task4_source = [
        firewall.GateResult(gate_id, True, "PASS", ("selector pass",), ())
        for gate_id in sorted(selector_ids)
    ]
    task4_full = [
        *task4_source,
        *[
            firewall.GateResult(gate_id, True, "PASS", ("package pass",), ())
            for gate_id in sorted(package_ids)
        ],
    ]
    assert firewall.evaluate_tier("Source", task4_source).verdict == "INCOMPATIBLE"
    assert firewall.evaluate_tier("Full", task4_full).verdict == "INCOMPATIBLE"


def test_tier_rejects_extra_fail_instead_of_ignoring_it() -> None:
    gates = _required_gate_set("Full")
    gates.append(
        firewall.GateResult(
            "future:mandatory-gate", True, "FAIL", ("explicit failure",), ()
        )
    )

    assert firewall.evaluate_tier("Full", gates).verdict == "INCOMPATIBLE"


SELECTOR_PASS_LINES = {
    "--test-preview231-command-set": ("PASS exact 142-command set",),
    "--test-preview261-v1-command-verification": (
        "PASS Preview.261 exhaustive V1 command verification",
    ),
    "--test-agent-protocol-compatibility": (
        "PASS agent protocol compatibility baseline",
    ),
    "--test-agent-protocol-registry": ("PASS agent protocol registry",),
    "--test-skyrim-npc-voice-cli": ("PASS skyrim-npc-voice-cli",),
    "--test-skyrim-dialogue-command-binding": (
        "PASS skyrim-dialogue-command-binding",
    ),
    "--test-skyrim-dialogue-cli": (
        "PASS saved Windows Mantella setup -> CLI discovery/native synthesis/resume; discovery sends no POST",
        "PASS voice import -> full template -> fixture synthesis -> resume -> dialogue analyze/apply/verify (78 lines)",
        "PASS language override regeneration -> apply/verify; mixed or blank language refused",
    ),
}


def _selector_files(tmp_path: Path) -> tuple[Path, Path, Path]:
    paths = (
        tmp_path / "dotnet-sdk-10.0.301" / "dotnet.exe",
        tmp_path / "tests" / "NpcManager.Cli.Tests" / "bin" / "Release" /
        "net10.0" / "NpcManager.Cli.Tests.dll",
        tmp_path / "tests" / "NpcManager.Architecture.Tests" / "bin" /
        "Release" / "net10.0" / "NpcManager.Architecture.Tests.dll",
    )
    for path in paths:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(path.name.encode("ascii"))
    return paths


def _selector_kwargs(
    root: Path,
    dotnet: Path,
    cli_dll: Path,
    architecture_dll: Path,
) -> dict[str, object]:
    return {
        "cli_project_dll": cli_dll.resolve(),
        "architecture_project_dll": architecture_dll.resolve(),
        "dotnet_path": dotnet.resolve(),
        "working_directory": root.resolve(),
        "expected_dotnet_sha256": _sha256(dotnet.read_bytes()),
        "expected_cli_project_sha256": _sha256(cli_dll.read_bytes()),
        "expected_architecture_project_sha256": _sha256(
            architecture_dll.read_bytes()
        ),
    }


def test_selector_runner_uses_explicit_dlls_and_supplied_pinned_dotnet(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    dotnet, cli_dll, architecture_dll = _selector_files(tmp_path)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", dotnet.resolve())
    invocations: list[list[str]] = []

    def pass_runner(arguments, **kwargs):
        invocation = [str(item) for item in arguments]
        invocations.append(invocation)
        if invocation == [str(dotnet.resolve()), "--version"]:
            return subprocess.CompletedProcess(invocation, 0, b"10.0.301\n", b"")
        output = "\n".join(SELECTOR_PASS_LINES[invocation[-1]]) + "\n"
        return subprocess.CompletedProcess(
            invocation, 0, output.encode(), b""
        )

    monkeypatch.setattr(firewall, "_run_bounded_process", pass_runner)

    results = firewall.run_required_selectors(
        **_selector_kwargs(tmp_path, dotnet, cli_dll, architecture_dll)
    )

    assert len(results) == 7
    assert all(result.status == "PASS" for result in results)
    selector_calls = [call for call in invocations if call[-1] != "--version"]
    assert all(call[0] == str(dotnet.resolve()) for call in selector_calls)
    assert {call[1] for call in selector_calls} == {
        str(cli_dll.resolve()), str(architecture_dll.resolve())
    }
    assert {call[-1] for call in selector_calls} == {
        "--test-preview231-command-set",
        "--test-preview261-v1-command-verification",
        "--test-agent-protocol-compatibility",
        "--test-agent-protocol-registry",
        "--test-skyrim-npc-voice-cli",
        "--test-skyrim-dialogue-command-binding",
        "--test-skyrim-dialogue-cli",
    }
    assert all("sdkVersion=10.0.301" in result.evidence for result in results)
    assert all(
        any(item.startswith("projectSha256=") for item in result.evidence)
        for result in results
    )


@pytest.mark.parametrize(
    ("return_code", "stdout", "stderr"),
    [
        (0, b"not a pass\n", b""),
        (0, b"PASS\n", b""),
        (0, b"prefix PASS selector\n", b""),
        (0, b"PASS selector\nunstructured trailing output\n", b""),
        (0, b"PASS selector\n", b"unexpected stderr"),
        (0, b"\xff", b""),
        (1, b"PASS selector\n", b""),
    ],
)
def test_selector_runner_never_promotes_malformed_or_nonzero_output_to_pass(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    return_code: int,
    stdout: bytes,
    stderr: bytes,
) -> None:
    dotnet, cli_dll, architecture_dll = _selector_files(tmp_path)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", dotnet.resolve())

    def malformed_runner(arguments, **kwargs):
        if list(arguments) == [str(dotnet.resolve()), "--version"]:
            return subprocess.CompletedProcess(arguments, 0, b"10.0.301\n", b"")
        return subprocess.CompletedProcess(arguments, return_code, stdout, stderr)

    monkeypatch.setattr(
        firewall,
        "_run_bounded_process",
        malformed_runner,
    )

    results = firewall.run_required_selectors(
        **_selector_kwargs(tmp_path, dotnet, cli_dll, architecture_dll)
    )

    assert all(result.status == "FAIL" for result in results)
    assert firewall.evaluate_tier("Source", results).verdict == "INCOMPATIBLE"


def test_selector_timeout_is_recorded_as_blocked_and_fails_tier(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    dotnet, cli_dll, architecture_dll = _selector_files(tmp_path)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", dotnet.resolve())

    def timeout(arguments, **kwargs):
        if list(arguments) == [str(dotnet.resolve()), "--version"]:
            return subprocess.CompletedProcess(arguments, 0, b"10.0.301\n", b"")
        raise FirewallError("selector timed out after 1s")

    monkeypatch.setattr(firewall, "_run_bounded_process", timeout)
    results = firewall.run_required_selectors(
        **_selector_kwargs(tmp_path, dotnet, cli_dll, architecture_dll)
    )

    assert all(result.status == "BLOCKED" for result in results)
    assert firewall.evaluate_tier("Source", results).verdict == "INCOMPATIBLE"


def test_selector_runner_refuses_implicit_or_non_dll_execution_targets(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    dotnet, cli_dll, architecture_dll = _selector_files(tmp_path)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", dotnet.resolve())
    cli_exe = tmp_path / "cli.exe"
    cli_exe.write_bytes(b"test")
    kwargs = _selector_kwargs(tmp_path, dotnet, cli_dll, architecture_dll)
    kwargs["cli_project_dll"] = cli_exe.resolve()
    kwargs["expected_cli_project_sha256"] = _sha256(cli_exe.read_bytes())

    with pytest.raises(FirewallError, match="explicit project DLL"):
        firewall.run_required_selectors(**kwargs)


def test_selector_runner_rejects_replayed_pass_from_another_selector(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    dotnet, cli_dll, architecture_dll = _selector_files(tmp_path)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", dotnet.resolve())

    def replay(arguments, **kwargs):
        if list(arguments) == [str(dotnet.resolve()), "--version"]:
            return subprocess.CompletedProcess(arguments, 0, b"10.0.301\n", b"")
        return subprocess.CompletedProcess(
            arguments, 0, b"PASS exact 142-command set\n", b""
        )

    monkeypatch.setattr(firewall, "_run_bounded_process", replay)
    results = firewall.run_required_selectors(
        **_selector_kwargs(tmp_path, dotnet, cli_dll, architecture_dll)
    )

    assert results[0].status == "PASS"
    assert all(result.status == "FAIL" for result in results[1:])


def test_selector_runner_rejects_wrong_sdk_version(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    dotnet, cli_dll, architecture_dll = _selector_files(tmp_path)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", dotnet.resolve())
    monkeypatch.setattr(
        firewall,
        "_run_bounded_process",
        lambda arguments, **kwargs: subprocess.CompletedProcess(
            arguments, 0, b"10.0.999\n", b""
        ),
    )

    with pytest.raises(FirewallError, match="SDK version"):
        firewall.run_required_selectors(
            **_selector_kwargs(tmp_path, dotnet, cli_dll, architecture_dll)
        )


@pytest.mark.parametrize("identity", ["dotnet", "cli", "architecture"])
def test_selector_runner_rejects_wrong_identity_hash(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    identity: str,
) -> None:
    dotnet, cli_dll, architecture_dll = _selector_files(tmp_path)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", dotnet.resolve())
    kwargs = _selector_kwargs(tmp_path, dotnet, cli_dll, architecture_dll)
    kwargs[f"expected_{identity}_project_sha256" if identity != "dotnet" else "expected_dotnet_sha256"] = "0" * 64

    with pytest.raises(FirewallError, match="sha256 mismatch"):
        firewall.run_required_selectors(**kwargs)


def test_selector_runner_rejects_wrong_canonical_project_output_path(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    dotnet, cli_dll, architecture_dll = _selector_files(tmp_path)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", dotnet.resolve())
    replay = tmp_path / "replayed" / "NpcManager.Cli.Tests.dll"
    replay.parent.mkdir()
    replay.write_bytes(cli_dll.read_bytes())
    kwargs = _selector_kwargs(tmp_path, dotnet, cli_dll, architecture_dll)
    kwargs["cli_project_dll"] = replay.resolve()

    with pytest.raises(FirewallError, match="canonical project output"):
        firewall.run_required_selectors(**kwargs)


def _journey_document(journey_id: str = "journey.create-to-package") -> dict[str, object]:
    def artifact(path: str, role: str) -> dict[str, object]:
        if path.endswith(".json") or path.endswith(".zip"):
            projection = (
                {"members": [{"path": "Data/PackagedNpc.esp", "length": 1,
                    "identityKind": "raw", "sha256": "4" * 64}]}
                if path.endswith(".zip") else {"stable": path}
            )
            return {
                "path": path, "role": role,
                "identityKind": "semantic-zip" if path.endswith(".zip") else "semantic-json",
                "length": 1,
                "rawSha256": "7" * 64,
                "semanticSha256": _sha256(canonical_json_bytes(projection)),
                "projection": projection,
                "relationships": [],
            }
        return {
            "path": path, "role": role, "identityKind": "raw",
            "length": 1, "sha256": "4" * 64,
        }

    selection_prefix = "companion/selection/racemenu-selection-" + "a" * 16 + "-" + "b" * 32
    def selection_inventory() -> list[dict[str, object]]:
        bundle_path = "planned-npc-output/Data/NPCManager/Evidence/racemenu-bundle.json"
        rows = []
        bundle_projection = {}
        relations = []
        for role, filename, digest in (
            ("recordAuthority", "record-authority.json", "8"),
            ("runtimeRoutes", "runtime-routes.json", "9"),
        ):
            source_path = "planned-npc-output/Data/NPCManager/Evidence/" + filename
            row = artifact(source_path, "inventory-member")
            row["rawSha256"] = digest * 64
            rows.append(row)
            canonical = _sha256(canonical_json_bytes(row["projection"]))
            bundle_projection[role] = {
                "manifestPath": selection_prefix[:-32] + "<token>/" + filename,
                "manifestSha256": canonical,
            }
            relations.append({
                "kind": "raw-file", "sourcePath": source_path,
                "targetPointer": "/" + role + "/manifestSha256",
                "domain": "sha256-raw-file", "expectedSha256": digest * 64,
                "computedSha256": digest * 64, "canonicalSha256": canonical,
                "closure": [{"path": source_path, "length": 1,
                             "rawSha256": digest * 64}],
            })
        bundle = artifact(bundle_path, "inventory-member")
        bundle["projection"] = bundle_projection
        bundle["semanticSha256"] = _sha256(canonical_json_bytes(bundle_projection))
        bundle["relationships"] = relations
        rows.append(bundle)
        return rows

    transient = [
        {"role": role, "path": selection_prefix + "/" + filename,
         "length": 1, "rawSha256": digest * 64,
         "captureStep": "npc create-from-jslot:selection-before-promotion"}
        for role, filename, digest in (
            ("recordAuthority", "record-authority.json", "8"),
            ("runtimeRoutes", "runtime-routes.json", "9"),
        )
    ]

    token = "0123456789abcdef0123456789abcdef"
    workspace = rf"K:\Actorwright\artifacts\test-work\j-{token}\c-{token}"
    if journey_id == "journey.wave-b-v1":
        document = _journey_document()
        document["id"] = journey_id
        workspace = rf"K:\Actorwright\artifacts\test-work\j-{token}\w-{token}"
        document["workspaceRoot"] = workspace
        document["outputRoot"] = workspace + r"\finished"
        document["steps"] = [
            {
                "position": index,
                "command": command,
                "exitCode": 4 if index in {19, 25, 32} else 0,
                "exitMeaning": (
                    "Domain validation failure" if index in {19, 25, 32} else "Success"
                ),
                "schemaIds": sorted(firewall.EXPECTED_JOURNEY_SCHEMA_IDS.get(command, set())),
            }
            for index, command in enumerate(firewall.WAVE_B_JOURNEY_COMMANDS)
        ]
        document["artifacts"] = [artifact(path, role) for path, role in sorted(firewall.WAVE_B_JOURNEY_ARTIFACTS.items())]
        document["semantics"] = {
            "topologyRefusal": True,
            "privateHdptRepair": True,
            "readyForReviewedWrite": True,
            "applied": True,
            "verified": True,
            "placementVerified": True,
            "packageVerification": {
                "code": "workflow-human-review-required",
                "verified": False,
            },
            "pluginAudit": True,
            "source": {"plugin": "PackagedNpc.esp", "masters": firewall.WAVE_B_SOURCE_MASTERS,
                "actorFormId": "0x00000800", "beforeSha256": "5" * 64,
                "afterSha256": "5" * 64, "unchanged": True},
            "final": {"plugin": "PackagedNpc.esp", "masters": firewall.WAVE_B_MASTERS,
                "actorFormId": "0x00000800", "sha256": "6" * 64},
            "artifactInventory": sorted(
                [artifact("finished.zip", "inventory-member"), *selection_inventory()],
                key=lambda row: row["path"]),
            "nulRawMembers": [],
        }
        document["transientSourceIdentities"] = transient
        return document
    commands = [
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
    ]
    return {
        "formatVersion": 1,
        "id": journey_id,
        "workspaceRoot": workspace,
        "outputRoot": workspace + r"\finished",
        "operationId": token,
        "generatedAtUtc": "2026-09-22T12:00:00Z",
        "captureTool": {
            "harnessCommit": "1" * 40,
            "harnessTree": "7" * 40,
            "harnessSourceClosure": {"root": "tests/NpcManager.Cli.Tests", "tree": "8" * 40, "fileCount": 2, "sha256": "9" * 64},
            "runner": {"length": 7, "sha256": "2" * 64},
            "target": {"length": 8, "sha256": "3" * 64},
        },
        "steps": [
            {
                "position": index,
                "command": command,
                "exitCode": 4 if command == "package verify" else 0,
                "exitMeaning": (
                    "Domain validation failure" if command == "package verify" else "Success"
                ),
                "schemaIds": sorted(firewall.EXPECTED_JOURNEY_SCHEMA_IDS.get(command, set())),
            }
            for index, command in enumerate(commands)
        ],
        "artifacts": [artifact(path, role) for path, role in sorted(firewall.CREATE_JOURNEY_ARTIFACTS.items())],
        "transientSourceIdentities": transient,
        "semantics": {
            "readyForReviewedWrite": True,
            "applied": True,
            "verified": True,
            "source": {"plugin": "PackagedNpc.esp", "masters": ["Skyrim.esm", "ActorwrightBlankNpcProvider.esp"],
                "actorFormId": "0x00000800", "beforeSha256": "5" * 64,
                "afterSha256": "5" * 64, "unchanged": True},
            "genericPackageVerification": {
                "exitCode": 4,
                "code": "workflow-human-review-required",
                "verified": False,
            },
            "final": {"plugin": "PackagedNpc.esp", "masters": ["Skyrim.esm", "ActorwrightBlankNpcProvider.esp"],
                "actorFormId": "0x00000800", "sha256": "6" * 64},
            "packageInventoryExact": True,
            "artifactInventory": sorted(
                [*(artifact(path, "inventory-member")
                   for path in firewall.CREATE_JOURNEY_ARTIFACTS),
                 *selection_inventory()], key=lambda row: row["path"]),
            "nulRawMembers": [],
        },
        "runtimeAuthority": False,
        "visualAuthority": False,
    }


def test_structured_create_journey_requires_complete_semantics() -> None:
    observed = firewall._validate_journey_shape(_journey_document())

    assert observed["id"] == "journey.create-to-package"
    assert observed["runtimeAuthority"] is False
    assert observed["visualAuthority"] is False


def test_semantic_journey_artifact_requires_raw_identity_and_closed_relationships() -> None:
    observed = _journey_document()
    artifact = next(item for item in observed["artifacts"]
                    if item["identityKind"] == "semantic-json")
    del artifact["rawSha256"]
    with pytest.raises(FirewallError, match="rawSha256"):
        firewall._validate_journey_shape(observed)

    artifact["rawSha256"] = "7" * 64
    artifact["relationships"] = [{"unexpected": True}]
    with pytest.raises(FirewallError, match="relationship"):
        firewall._validate_journey_shape(observed)


def test_journey_relationship_rejects_missing_source_and_digest_tamper() -> None:
    observed = _journey_document()
    artifact = next(item for item in observed["artifacts"]
                    if item["path"] == "planned-npc-output/npcmanager-package.json")
    artifact["projection"] = {"derivedSha256": "4" * 64}
    artifact["semanticSha256"] = _sha256(canonical_json_bytes(artifact["projection"]))
    inventory_copy = next(item for item in observed["semantics"]["artifactInventory"]
                          if item["path"] == artifact["path"])
    inventory_copy["projection"] = dict(artifact["projection"])
    inventory_copy["semanticSha256"] = artifact["semanticSha256"]
    source = "planned-npc-output/Data/PackagedNpc.esp"
    relation = {
        "kind": "raw-file", "sourcePath": source,
        "targetPointer": "/derivedSha256", "domain": "sha256-raw-file",
        "expectedSha256": "4" * 64, "computedSha256": "4" * 64,
        "canonicalSha256": "4" * 64,
        "closure": [{"path": source, "length": 1, "rawSha256": "4" * 64}],
    }
    artifact["relationships"] = [relation]
    inventory_copy["relationships"] = [relation]
    firewall._validate_journey_shape(observed)
    relation["sourcePath"] = "absent.json"
    relation["closure"][0]["path"] = "absent.json"
    with pytest.raises(FirewallError, match="source.*missing"):
        firewall._validate_journey_shape(observed)
    relation["sourcePath"] = source
    relation["closure"][0]["path"] = source
    relation["computedSha256"] = "0" * 64
    with pytest.raises(FirewallError, match="relationship digest mismatch"):
        firewall._validate_journey_shape(observed)


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (lambda value: value["transientSourceIdentities"].pop(), "two selection identities"),
        (lambda value: value["transientSourceIdentities"][0].update(
            captureStep="npc finish apply"), "capture step"),
        (lambda value: value["transientSourceIdentities"][0].update(
            path="companion/selection/foreign/record-authority.json"), "source path"),
        (lambda value: value["transientSourceIdentities"][1].update(
            path=value["transientSourceIdentities"][1]["path"].replace(
                "-" + "b" * 32 + "/", "-" + "c" * 32 + "/")),
         "different transactions"),
        (lambda value: value["transientSourceIdentities"][0].update(
            rawSha256="0" * 64), "retained selection copy"),
        (lambda value: next(row for row in value["semantics"]["artifactInventory"]
             if row["path"].endswith("record-authority.json")).update(
                 rawSha256="0" * 64), "retained selection copy"),
    ],
)
def test_transient_selection_identity_mutations_fail_closed(mutation, message: str) -> None:
    observed = _journey_document()
    mutation(observed)
    with pytest.raises(FirewallError, match=message):
        firewall._validate_journey_shape(observed)


def test_declared_digest_graph_covers_every_captured_dynamic_pointer() -> None:
    fixture = json.loads((firewall.REPOSITORY_ROOT / "tests" / "fixtures" /
        "compatibility-firewall" / "publicSynthetic" / "journeys" /
        "wave-b-v1.json").read_text(encoding="utf-8"))
    rows = {row["path"]: row for row in fixture["semantics"]["artifactInventory"]}
    for archive_name in ("finished.zip", "placement.zip"):
        archive = rows[archive_name]
        for member in archive["projection"]["members"]:
            rows.setdefault(archive_name.removesuffix(".zip") + "/" + member["path"], member)
    assert "<dynamic-sha256>" not in json.dumps(fixture)
    expected = {(path, relation["targetPointer"])
                for path, row in rows.items()
                for relation in row.get("relationships", [])}
    graph = firewall._journey_digest_specs(rows)
    create_only = {path: row for path, row in rows.items()
                   if path.startswith("finished/") or path == "finished.zip"}
    assert ("/requestSha256", "raw-file",
            "finished/NPCManager/Evidence/finish-core-request.json",
            "sha256-raw-file") in firewall._journey_digest_specs(create_only)[
                "finished/NPCManager/Evidence/finish-core-proposal.json"]
    assert graph["evidence/25-response.json"] == [
        ("/manifestSha256", "raw-file",
         "evidence/create-before-finish-fixture/npcmanager-package.json",
         "sha256-raw-file")]
    declared = {(path, pointer) for path, specs in graph.items()
                for pointer, _, _, _ in specs}
    assert declared == expected
    for specs in graph.values():
        for _, kind, source, _ in specs:
            if kind == "tree":
                assert any(path.startswith(source + "/") for path in rows)
            else:
                assert source in rows


def test_offline_nul_tree_rejects_coordinated_canonical_digest_tamper() -> None:
    path = (firewall.REPOSITORY_ROOT / "tests" / "fixtures" /
            "compatibility-firewall" / "publicSynthetic" / "journeys" / "wave-b-v1.json")
    observed = json.loads(path.read_text(encoding="utf-8"))
    response = next(row for row in observed["semantics"]["artifactInventory"]
                    if row["path"] == "evidence/29-response.json")
    relation = next(item for item in response["relationships"]
                    if item["targetPointer"] == "/verification/packageTreeSha256/value")
    forged = "F" * 64
    relation["canonicalSha256"] = forged
    response["projection"]["verification"]["packageTreeSha256"]["value"] = forged
    response["semanticSha256"] = _sha256(
        firewall.canonical_json_bytes(response["projection"])).upper()
    with pytest.raises(FirewallError, match="canonical.*tree|canonical relationship digest"):
        firewall.validate_journey_observation(observed)


@pytest.mark.parametrize("mutation", ["bytes", "hash", "content", "path", "order", "excluded", "prefix", "duplicate"])
def test_offline_nul_tree_rejects_retained_witness_and_closure_tamper(mutation: str) -> None:
    path = (firewall.REPOSITORY_ROOT / "tests" / "fixtures" /
            "compatibility-firewall" / "publicSynthetic" / "journeys" / "wave-b-v1.json")
    observed = json.loads(path.read_text(encoding="utf-8"))
    response = next(row for row in observed["semantics"]["artifactInventory"]
                    if row["path"] == "evidence/29-response.json")
    relation = next(item for item in response["relationships"]
                    if item["targetPointer"] == "/verification/packageTreeSha256/value")
    if mutation == "bytes":
        member = observed["semantics"]["nulRawMembers"][0]
        member["bytesBase64"] = ("A" if member["bytesBase64"][0] != "A" else "B") + member["bytesBase64"][1:]
    elif mutation == "hash":
        observed["semantics"]["nulRawMembers"][0]["rawSha256"] = "F" * 64
    elif mutation == "content":
        # This canonical tree member has no other retained projection copy.
        source_path = "finished/evidence/dependency-manifest.json"
        source = next(row for row in observed["semantics"]["artifactInventory"]
                      if row["path"] == source_path)
        source["projection"]["forgery"] = "changed canonical member"
        source["semanticSha256"] = _sha256(firewall.canonical_json_bytes(source["projection"])).upper()
    elif mutation == "path":
        relation["closure"][0]["path"] = "finished/foreign.txt"
    elif mutation == "order":
        relation["closure"][0], relation["closure"][1] = relation["closure"][1], relation["closure"][0]
    elif mutation == "excluded":
        relation["closure"].append({"path": "finished/README-Finish-Core.txt",
                                     "length": 1, "rawSha256": "F" * 64})
    elif mutation == "prefix":
        # The output domain requires its fixed prefix; a source-domain digest is not equivalent.
        identities = {row["path"]: row for row in observed["semantics"]["artifactInventory"]}
        raw_members = {row["path"]: base64.b64decode(row["bytesBase64"])
                       for row in observed["semantics"]["nulRawMembers"]}
        payload = bytearray()
        for item in relation["closure"]:
            member_path = item["path"]
            row = identities[member_path]
            data = (raw_members[member_path] if row["identityKind"] == "raw"
                    else firewall.canonical_json_bytes(row["projection"]))
            payload.extend(member_path[len(relation["sourcePath"]) + 1:].encode("utf-8") +
                           b"\0" + data + b"\0")
        forged = _sha256(bytes(payload)).upper()
        assert forged != relation["canonicalSha256"]
        relation["canonicalSha256"] = forged
        response["projection"]["verification"]["packageTreeSha256"]["value"] = forged
        response["semanticSha256"] = _sha256(firewall.canonical_json_bytes(response["projection"])).upper()
    else:
        manifest = next(row for row in observed["semantics"]["artifactInventory"]
                        if row["path"] == "finished/NPCManager/Evidence/finish-core-manifest.json")
        duplicate = next(item for item in manifest["relationships"]
                         if item["targetPointer"] == "/evidence/packageTreeSha256")
        duplicate["canonicalSha256"] = "E" * 64
        manifest["projection"]["evidence"]["packageTreeSha256"] = "E" * 64
        manifest["semanticSha256"] = _sha256(firewall.canonical_json_bytes(manifest["projection"])).upper()
    with pytest.raises(FirewallError):
        firewall.validate_journey_observation(observed)


def test_journey_policy_projection_keeps_canonical_digest_and_hides_raw_witness() -> None:
    relation = {
        "kind": "raw-file", "sourcePath": "evidence/source.json",
        "targetPointer": "/sha256", "domain": "sha256-raw-file",
        "expectedSha256": "a" * 64, "computedSha256": "a" * 64,
        "canonicalSha256": "b" * 64,
        "closure": [{"path": "evidence/source.json", "length": 10,
                     "rawSha256": "a" * 64}],
    }
    artifact = {
        "path": "evidence/response.json", "role": "inventory-member",
        "identityKind": "semantic-json", "length": 21,
        "rawSha256": "a" * 64, "semanticSha256": "c" * 64,
        "projection": {"sha256": "b" * 64}, "relationships": [relation],
    }
    observed = {
        "id": "journey.wave-b-v1", "formatVersion": 1,
        "captureTool": {}, "steps": [], "artifacts": [],
        "semantics": {"artifactInventory": [artifact], "nulRawMembers": []},
        "transientSourceIdentities": [{
            "role": "record-authority",
            "path": "companion/selection/racemenu-selection-" + "a" * 16 + "-" + "b" * 32 + "/record-authority.json",
            "captureStep": "npc create-from-jslot:selection-before-promotion",
            "rawSha256": "a" * 64, "length": 10,
        }],
        "runtimeAuthority": False, "visualAuthority": False,
    }
    expected = firewall._journey_policy_projection_from_validated(observed)
    assert "nulRawMembers" not in expected["semantics"]
    candidate = deepcopy(observed)
    candidate_artifact = candidate["semantics"]["artifactInventory"][0]
    candidate_artifact["rawSha256"] = "d" * 64
    candidate_artifact["length"] = 99
    candidate_relation = candidate_artifact["relationships"][0]
    candidate_relation["expectedSha256"] = candidate_relation["computedSha256"] = "d" * 64
    candidate_relation["closure"][0].update(length=99, rawSha256="d" * 64)
    candidate["transientSourceIdentities"][0]["rawSha256"] = "d" * 64
    candidate["transientSourceIdentities"][0]["length"] = 99
    assert firewall._journey_policy_projection_from_validated(candidate) == expected
    candidate_artifact["projection"]["sha256"] = "e" * 64
    assert firewall._journey_policy_projection_from_validated(candidate) != expected


def test_release_journey_comparison_ignores_only_authenticated_release_and_runtime_identities() -> None:
    root = Path(__file__).resolve().parents[2]
    fixture = root / "tests/fixtures/compatibility-firewall/publicSynthetic/journeys"
    create = firewall.journey_policy_projection(firewall._load_json(
        fixture / "create-to-package.json"))
    changed_create = deepcopy(create)
    changed_create["artifacts"][4]["semanticSha256"] = "F" * 64
    baseline_create = firewall._release_journey_policy_projection(
        create, product_version="1.0.0-preview.275",
        source_line="preview.275-private", executable_sha256=None)
    current_create = firewall._release_journey_policy_projection(
        changed_create, product_version="1.0.0-preview.276",
        source_line="preview.276-private", executable_sha256=None)
    assert baseline_create == current_create

    wave = firewall.journey_policy_projection(firewall._load_json(fixture / "wave-b-v1.json"))
    changed_wave = deepcopy(wave)
    def advance_release_identity(node: object) -> None:
        if isinstance(node, dict):
            for key, item in node.items():
                if key == "productVersion" and item == "1.0.0-preview.275":
                    node[key] = "1.0.0-preview.276"
                elif key == "sourceLine" and item == "preview.275-private":
                    node[key] = "preview.276-private"
                else:
                    advance_release_identity(item)
        elif isinstance(node, list):
            for item in node:
                advance_release_identity(item)
    advance_release_identity(changed_wave)
    rows = {row["path"]: row for row in changed_wave["semantics"]["artifactInventory"]}
    for path in ("evidence/19-response.json", "evidence/24-response.json"):
        row = rows[path]
        row["projection"]["sha256"] = "A" * 64
        row["relationships"][0]["canonicalSha256"] = "A" * 64
        row["projection"]["optionalPreview"][0]["detail"] = row["projection"][
            "optionalPreview"][0]["detail"].replace("Y4Q9BpMSr3BD", "B1jB_6BFW7-e")
    archive = rows["evidence/29-response.json"]
    archive["projection"]["verification"]["archiveSha256"]["value"] = "B" * 64
    archive["relationships"][0]["canonicalSha256"] = "B" * 64
    for path in ("evidence/initial-preflight.json", "evidence/repaired-preflight.json"):
        rows[path]["projection"]["executableSha256"] = "C" * 64
        rows[path]["projection"]["optionalPreview"][0]["detail"] = rows[path][
            "projection"]["optionalPreview"][0]["detail"].replace(
                "Y4Q9BpMSr3BD", "B1jB_6BFW7-e")
    old_cli_sha = next(row for row in wave["semantics"]["artifactInventory"]
                       if row["path"] == "evidence/initial-preflight.json")[
                           "projection"]["executableSha256"]
    baseline_wave = firewall._release_journey_policy_projection(
        wave, product_version="1.0.0-preview.275",
        source_line="preview.275-private", executable_sha256=old_cli_sha)
    current_wave = firewall._release_journey_policy_projection(
        changed_wave, product_version="1.0.0-preview.276",
        source_line="preview.276-private", executable_sha256="C" * 64)
    assert baseline_wave == current_wave
    rows["evidence/19-response.json"]["projection"]["readyForBuild"] = False
    assert firewall._release_journey_policy_projection(
        changed_wave, product_version="1.0.0-preview.276",
        source_line="preview.276-private", executable_sha256="C" * 64) != baseline_wave
    rows["evidence/19-response.json"]["projection"]["readyForBuild"] = True
    rows["evidence/initial-preflight.json"]["projection"]["executableSha256"] = "D" * 64
    assert firewall._release_journey_policy_projection(
        changed_wave, product_version="1.0.0-preview.276",
        source_line="preview.276-private", executable_sha256="C" * 64) != baseline_wave


def test_digest_graph_recomputes_only_declared_pointer_and_refuses_tamper(
    tmp_path: Path,
) -> None:
    capture_root = tmp_path / "j-capture"
    workspace = capture_root / "w-journey"
    evidence = workspace / "evidence"
    evidence.mkdir(parents=True)
    preflight_bytes = b'{"stable":1}'
    preflight_hash = _sha256(preflight_bytes).upper()
    response_projection = {"sha256": preflight_hash, "neighborSha256": preflight_hash}
    response_bytes = json.dumps(response_projection, separators=(",", ":")).encode()
    (evidence / "initial-preflight.json").write_bytes(preflight_bytes)
    (evidence / "19-response.json").write_bytes(response_bytes)
    source = {
        "path": "evidence/initial-preflight.json", "role": "inventory-member",
        "identityKind": "semantic-json", "length": len(preflight_bytes),
        "rawSha256": preflight_hash,
        "semanticSha256": _sha256(canonical_json_bytes({"stable": 1})),
        "projection": {"stable": 1}, "relationships": [],
    }
    target = {
        "path": "evidence/19-response.json", "role": "inventory-member",
        "identityKind": "semantic-json", "length": len(response_bytes),
        "rawSha256": _sha256(response_bytes).upper(),
        "semanticSha256": _sha256(canonical_json_bytes(response_projection)),
        "projection": dict(response_projection), "relationships": [],
    }
    observed = {"workspaceRoot": str(workspace), "artifacts": [],
                "semantics": {"artifactInventory": [target, source]}}
    firewall._canonicalize_journey_digests(observed, capture_root)
    canonical = _sha256(canonical_json_bytes({"stable": 1})).upper()
    assert target["projection"]["sha256"] == canonical
    assert target["projection"]["neighborSha256"] == preflight_hash
    assert target["relationships"][0]["expectedSha256"] == preflight_hash
    assert target["relationships"][0]["canonicalSha256"] == canonical
    rows = {source["path"]: source, target["path"]: target}
    firewall._validate_declared_digest_graph(rows)
    declared_relation = target["relationships"].pop()
    with pytest.raises(FirewallError, match="missing, unknown"):
        firewall._validate_declared_digest_graph(rows)
    target["relationships"] = [dict(declared_relation,
                                    targetPointer="/neighborSha256")]
    with pytest.raises(FirewallError, match="missing, unknown"):
        firewall._validate_declared_digest_graph(rows)
    target["relationships"] = [declared_relation]

    target["projection"] = {"sha256": "0" * 64, "neighborSha256": preflight_hash}
    target["rawSha256"] = _sha256(response_bytes).upper()
    with pytest.raises(FirewallError, match="product digest relation failed"):
        firewall._canonicalize_journey_digests(observed, capture_root)


def test_digest_graph_authenticates_tree_domains_and_archive_bytes(tmp_path: Path) -> None:
    capture_root = tmp_path / "j-capture"
    workspace = capture_root / "w-journey"
    (workspace / "planned-npc-output").mkdir(parents=True)
    (workspace / "finished").mkdir()
    (workspace / "evidence").mkdir()
    (workspace / "planned-npc-output" / "a.dat").write_bytes(b"a")
    (workspace / "finished" / "b.dat").write_bytes(b"b")
    with zipfile.ZipFile(workspace / "finished.zip", "w") as archive:
        archive.writestr("b.dat", b"b")
    archive_bytes = (workspace / "finished.zip").read_bytes()
    a_hash = _sha256(b"a").upper()
    b_hash = _sha256(b"b").upper()
    source_tree = _sha256(b"a.dat\0a\0").upper()
    output_tree = _sha256(b"npc.finish-core.output-tree.v2\0b.dat\0b\0").upper()
    projection = {"verification": {
        "archiveSha256": {"value": _sha256(archive_bytes).upper()},
        "packageTreeSha256": {"value": output_tree},
        "sourcePackageTreeSha256": {"value": source_tree},
    }}
    response_bytes = json.dumps(projection, separators=(",", ":")).encode()
    (workspace / "evidence" / "29-response.json").write_bytes(response_bytes)
    rows = [
        {"path": "planned-npc-output/a.dat", "role": "inventory-member",
         "identityKind": "raw", "length": 1, "sha256": a_hash},
        {"path": "finished/b.dat", "role": "inventory-member",
         "identityKind": "raw", "length": 1, "sha256": b_hash},
        {"path": "finished.zip", "role": "inventory-member", "identityKind": "semantic-zip",
         "length": len(archive_bytes), "rawSha256": _sha256(archive_bytes).upper(),
         "semanticSha256": "0" * 64,
         "projection": {"members": [{"path": "b.dat", "length": 1,
             "identityKind": "raw", "sha256": b_hash}]}, "relationships": []},
        {"path": "evidence/29-response.json", "role": "inventory-member",
         "identityKind": "semantic-json", "length": len(response_bytes),
         "rawSha256": _sha256(response_bytes).upper(),
         "semanticSha256": "0" * 64, "projection": projection,
         "relationships": []},
    ]
    observed = {"workspaceRoot": str(workspace), "artifacts": [],
                "semantics": {"artifactInventory": rows}}
    firewall._canonicalize_journey_digests(observed, capture_root)
    response = next(row for row in rows if row["path"] == "evidence/29-response.json")
    assert len(response["relationships"]) == 3
    assert {relation["domain"] for relation in response["relationships"]} == {
        "sha256-raw-file", "sha256-finish-output-tree-v2",
        "sha256-finish-source-tree-v1",
    }
    assert next(relation for relation in response["relationships"]
                if relation["domain"] == "sha256-finish-source-tree-v1")["closure"] == [
        {"path": "planned-npc-output/a.dat", "length": 1, "rawSha256": a_hash}]
    firewall._validate_declared_digest_graph({row["path"]: row for row in rows})


def test_digest_graph_verifies_self_excluding_proposal_authority(tmp_path: Path) -> None:
    capture_root = tmp_path / "j-capture"
    workspace = capture_root / "w-journey"
    evidence = workspace / "evidence"
    evidence.mkdir(parents=True)
    manifest_path = workspace / "finished" / "NPCManager" / "Evidence" / "finish-core-manifest.json"
    manifest_path.parent.mkdir(parents=True)
    manifest_path.write_bytes(b'{"manifest":1}')
    manifest_hash = _sha256(manifest_path.read_bytes()).upper()
    request = {"finishCore": {"manifestSha256": manifest_hash}, "stable": 1}
    request_bytes = json.dumps(request, separators=(",", ":")).encode()
    (evidence / "placement-request.json").write_bytes(request_bytes)
    request_hash = _sha256(canonical_json_bytes(request)[:-1]).upper()
    proposal_without_self = {"requestSha256": request_hash, "stable": 2}
    proposal_hash = _sha256(canonical_json_bytes(proposal_without_self)[:-1]).upper()
    proposal = dict(proposal_without_self, proposalSha256=proposal_hash)
    proposal_bytes = json.dumps(proposal, separators=(",", ":")).encode()
    (evidence / "placement-proposal.json").write_bytes(proposal_bytes)
    rows = [
        {"path": "finished/NPCManager/Evidence/finish-core-manifest.json",
         "role": "inventory-member", "identityKind": "raw",
         "length": len(manifest_path.read_bytes()), "sha256": manifest_hash},
        {"path": "evidence/placement-request.json", "role": "inventory-member",
         "identityKind": "semantic-json", "length": len(request_bytes),
         "rawSha256": _sha256(request_bytes).upper(), "projection": request,
         "semanticSha256": "0" * 64, "relationships": []},
        {"path": "evidence/placement-proposal.json", "role": "inventory-member",
         "identityKind": "semantic-json", "length": len(proposal_bytes),
         "rawSha256": _sha256(proposal_bytes).upper(), "projection": proposal,
         "semanticSha256": "0" * 64, "relationships": []},
    ]
    observed = {"workspaceRoot": str(workspace), "artifacts": [],
                "semantics": {"artifactInventory": rows}}
    firewall._canonicalize_journey_digests(observed, capture_root)
    final = rows[2]
    assert len(final["relationships"]) == 2
    assert final["projection"]["proposalSha256"] == proposal_hash
    firewall._validate_declared_digest_graph({row["path"]: row for row in rows})
    final["relationships"][1]["computedSha256"] = "0" * 64
    with pytest.raises(FirewallError, match="relationship digest mismatch"):
        firewall._validate_journey_relationships(final, "proposal")


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (lambda value: value["artifacts"].pop(), "required artifact"),
        (lambda value: value["steps"][8].update(exitCode=1), "exit meaning"),
        (lambda value: value["semantics"]["final"].update(masters=["Changed.esm"]), "master semantics"),
        (lambda value: value["semantics"]["final"].update(actorFormId="0x00000001"), "form semantics"),
        (lambda value: value["semantics"]["source"].update(afterSha256="0" * 64), "source mutation"),
        (lambda value: value["steps"][2]["schemaIds"].append("changed.schema"), "exact schema identity"),
        (lambda value: value["artifacts"][0]["projection"].update(stable="same-len-mutated"), "semantic projection hash"),
        (lambda value: next(item for item in value["artifacts"] if item["path"] == "finished.zip")["projection"]["members"][0].update(sha256="b" * 64), "semantic projection hash"),
        (lambda value: value.update(runtimeAuthority=True), "runtime authority"),
        (lambda value: value.update(transcript="# plausible transcript"), "unknown field"),
    ],
)
def test_structured_create_journey_mutations_fail_closed(mutation, message: str) -> None:
    observed = _journey_document()
    mutation(observed)

    with pytest.raises(FirewallError, match=message):
        firewall._validate_journey_shape(observed)


def test_structured_wave_b_journey_requires_exact_sequence_and_semantics() -> None:
    observed = firewall._validate_journey_shape(
        _journey_document("journey.wave-b-v1")
    )

    assert len(observed["steps"]) == 34
    assert observed["semantics"]["privateHdptRepair"] is True
    assert observed["semantics"]["packageVerification"] == {
        "code": "workflow-human-review-required",
        "verified": False,
    }


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (lambda value: value["steps"][19].update(command="plugin audit"), "exact command sequence"),
        (lambda value: value["semantics"]["final"].update(masters=["Skyrim.esm"]), "master semantics"),
        (lambda value: value["semantics"]["final"].update(actorFormId="0x00000001"), "form semantics"),
        (lambda value: value["semantics"]["source"].update(unchanged=False), "source mutation"),
        (lambda value: value["artifacts"].pop(), "required artifact"),
    ],
)
def test_structured_wave_b_mutations_fail_closed(mutation, message: str) -> None:
    observed = _journey_document("journey.wave-b-v1")
    mutation(observed)

    with pytest.raises(FirewallError, match=message):
        firewall._validate_journey_shape(observed)


def test_journey_rejects_retained_output_outside_owned_root() -> None:
    observed = _journey_document()
    observed["workspaceRoot"] = r"K:\unowned\journey"
    observed["outputRoot"] = r"K:\unowned\journey\finished"

    with pytest.raises(FirewallError, match="retained output outside owned root"):
        firewall._validate_journey_shape(observed)


@pytest.mark.parametrize("prefix", ["c-", "w-"])
def test_journey_accepts_short_owned_windows_root(prefix: str) -> None:
    observed = _journey_document()
    workspace = rf"K:\Actorwright\artifacts\test-work\j-{'1' * 32}\{prefix}{'1' * 32}"
    observed["workspaceRoot"] = workspace
    observed["outputRoot"] = workspace + r"\finished"

    assert firewall._validate_journey_shape(observed)["workspaceRoot"] == workspace


def test_journey_runner_authenticates_runner_and_target_independently(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = tmp_path / "repo"
    runner = root / "tests" / "NpcManager.Cli.Tests" / "bin" / "Release" / "net10.0" / "NpcManager.Cli.Tests.dll"
    target = k_root / "sealed" / "cli" / "actorwright.exe"
    runner.parent.mkdir(parents=True)
    target.parent.mkdir(parents=True)
    runner.write_bytes(b"runner")
    target.write_bytes(b"target")
    output = tmp_path / "observations"
    calls: list[tuple[list[str], dict[str, str]]] = []
    harness_identity = {"commit": "1" * 40, "tree": "7" * 40,
        "sourceClosure": {"root": "tests/NpcManager.Cli.Tests", "tree": "8" * 40,
            "fileCount": 2, "sha256": "9" * 64}}
    monkeypatch.setattr(
        firewall, "authenticate_harness_checkout",
        lambda value, **kwargs: harness_identity,
    )

    def fake_process(arguments, **kwargs):
        environment = kwargs["env"]
        calls.append(([str(item) for item in arguments], dict(environment)))
        observation = _journey_document(
            "journey.create-to-package"
            if arguments[-1] == "--test-finish-package-publication"
            else "journey.wave-b-v1"
        )
        Path(environment["ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT"]).parent.mkdir(
            parents=True, exist_ok=True
        )
        _write_json(
            Path(environment["ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT"]),
            observation,
        )
        return subprocess.CompletedProcess(arguments, 0, b"PASS selector\n", b"")

    monkeypatch.setattr(firewall, "_run_bounded_process", fake_process)
    monkeypatch.setattr(firewall, "_canonicalize_journey_digests", lambda *_: None)
    monkeypatch.setattr(firewall, "validate_journey_observation", lambda value: value)
    journeys = firewall.run_structured_journeys(
        repository_root=root,
        runner_dll=runner,
        expected_runner_sha256=_sha256(b"runner"),
        target_cli=target,
        expected_target_sha256=_sha256(b"target"),
        dotnet_path=PINNED_DOTNET,
        expected_dotnet_sha256=_sha256(PINNED_DOTNET.read_bytes()),
        output_root=output,
        expected_harness=harness_identity,
    )

    assert [item["id"] for item in journeys] == [
        "journey.create-to-package",
        "journey.wave-b-v1",
    ]
    assert len(calls) == 2
    assert all(
        call[1]["ACTORWRIGHT_COMPATIBILITY_TEST_CLI"] == str(target.resolve())
        and call[1]["ACTORWRIGHT_COMPATIBILITY_TEST_CLI_SHA256"] == _sha256(b"target")
        for call in calls
    )

    with pytest.raises(FirewallError, match="runner sha256 mismatch"):
        firewall.run_structured_journeys(
            repository_root=root,
            runner_dll=runner,
            expected_runner_sha256="0" * 64,
            target_cli=target,
            expected_target_sha256=_sha256(b"target"),
            dotnet_path=PINNED_DOTNET,
            expected_dotnet_sha256=_sha256(PINNED_DOTNET.read_bytes()),
            output_root=tmp_path / "runner-drift",
            expected_harness=harness_identity,
        )
    with pytest.raises(FirewallError, match="target CLI sha256 mismatch"):
        firewall.run_structured_journeys(
            repository_root=root,
            runner_dll=runner,
            expected_runner_sha256=_sha256(b"runner"),
            target_cli=target,
            expected_target_sha256="0" * 64,
            dotnet_path=PINNED_DOTNET,
            expected_dotnet_sha256=_sha256(PINNED_DOTNET.read_bytes()),
            output_root=tmp_path / "target-drift",
            expected_harness=harness_identity,
        )


@pytest.mark.parametrize(
    ("stdout", "stderr", "stream_name", "expected_excerpt", "is_truncated"),
    [
        (
            b"ignored stdout",
            b"System.InvalidOperationException: injected stderr failure\n"
            + b"x" * 5000 + b"TAIL_SENTINEL",
            "stderr",
            "injected stderr failure",
            True,
        ),
        (
            b"System.InvalidOperationException: injected stdout fallback",
            b"",
            "stdout",
            "injected stdout fallback",
            False,
        ),
    ],
)
def test_journey_runner_failure_includes_bounded_process_output(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
    stdout: bytes,
    stderr: bytes,
    stream_name: str,
    expected_excerpt: str,
    is_truncated: bool,
) -> None:
    root = tmp_path / "repo"
    runner = root / "tests" / "NpcManager.Cli.Tests" / "bin" / "Release" / "net10.0" / "NpcManager.Cli.Tests.dll"
    target = k_root / "sealed" / "cli" / "actorwright.exe"
    runner.parent.mkdir(parents=True)
    target.parent.mkdir(parents=True)
    runner.write_bytes(b"runner")
    target.write_bytes(b"target")
    harness_identity = {
        "commit": "1" * 40, "tree": "7" * 40,
        "sourceClosure": {"root": "tests/NpcManager.Cli.Tests", "tree": "8" * 40,
            "fileCount": 2, "sha256": "9" * 64}}
    monkeypatch.setattr(
        firewall, "authenticate_harness_checkout",
        lambda value, **kwargs: harness_identity,
    )
    create_capture = firewall.create_owned_journey_capture
    remove_capture = firewall.remove_owned_journey_capture
    monkeypatch.setattr(
        firewall, "create_owned_journey_capture", lambda: create_capture(k_root)
    )
    monkeypatch.setattr(
        firewall, "remove_owned_journey_capture",
        lambda root, token: remove_capture(root, token, k_root),
    )
    monkeypatch.setattr(
        firewall,
        "_run_bounded_process",
        lambda arguments, **kwargs: subprocess.CompletedProcess(
            arguments, 3762504530, stdout, stderr
        ),
    )

    with pytest.raises(FirewallError) as failure:
        firewall.run_structured_journeys(
            repository_root=root,
            runner_dll=runner,
            expected_runner_sha256=_sha256(b"runner"),
            target_cli=target,
            expected_target_sha256=_sha256(b"target"),
            dotnet_path=PINNED_DOTNET,
            expected_dotnet_sha256=_sha256(PINNED_DOTNET.read_bytes()),
            output_root=tmp_path / "failed-journey",
            expected_harness=harness_identity,
        )

    message = str(failure.value)
    assert "journey selector --test-finish-package-publication exited 3762504530" in message
    assert f"{stream_name}=" in message
    assert expected_excerpt in message
    assert len(message) <= 4200
    assert ("[truncated]" in message) is is_truncated
    assert "TAIL_SENTINEL" not in message


def test_journey_runner_has_no_current_product_fallback(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = tmp_path / "repo"
    runner = root / "tests" / "NpcManager.Cli.Tests" / "bin" / "Release" / "net10.0" / "NpcManager.Cli.Tests.dll"
    runner.parent.mkdir(parents=True)
    runner.write_bytes(b"runner")
    harness_identity = {
        "commit": "1" * 40, "tree": "7" * 40,
        "sourceClosure": {"root": "tests/NpcManager.Cli.Tests", "tree": "8" * 40,
            "fileCount": 2, "sha256": "9" * 64}}
    monkeypatch.setattr(
        firewall, "authenticate_harness_checkout",
        lambda value, **kwargs: harness_identity,
    )
    missing_target = k_root / "sealed" / "cli" / "actorwright.exe"
    monkeypatch.setattr(
        firewall,
        "_run_bounded_process",
        lambda *args, **kwargs: pytest.fail("runner must not start without sealed target"),
    )

    with pytest.raises(FirewallError, match="target CLI missing"):
        firewall.run_structured_journeys(
            repository_root=root,
            runner_dll=runner,
            expected_runner_sha256=_sha256(b"runner"),
            target_cli=missing_target,
            expected_target_sha256="0" * 64,
            dotnet_path=PINNED_DOTNET,
            expected_dotnet_sha256=_sha256(PINNED_DOTNET.read_bytes()),
            output_root=tmp_path / "observations",
            expected_harness=harness_identity,
        )


def test_harness_checkout_authentication_binds_clean_commit_and_sources(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    repository = tmp_path / "repo"
    harness = repository / "tests" / "NpcManager.Cli.Tests"
    harness.mkdir(parents=True)
    (harness / "CompatibilityJourneyObservation.cs").write_text("sealed harness\n")
    (harness / "Program.cs").write_text("sealed program\n")
    (repository / ".gitignore").write_text(
        "tests/NpcManager.Cli.Tests/untracked.cs\n"
        "tools/verification/ignored.py\n"
        "src/Injected.cs\n"
    )
    subprocess.run(["git", "init"], cwd=repository, check=True, capture_output=True)
    monkeypatch.setenv("GIT_CONFIG_COUNT", "1")
    monkeypatch.setenv("GIT_CONFIG_KEY_0", "safe.directory")
    monkeypatch.setenv("GIT_CONFIG_VALUE_0", repository.as_posix())
    subprocess.run(["git", "config", "user.email", "test@example.invalid"], cwd=repository, check=True)
    subprocess.run(["git", "config", "user.name", "Compatibility Test"], cwd=repository, check=True)
    subprocess.run(["git", "add", "."], cwd=repository, check=True)
    subprocess.run(["git", "commit", "-m", "fixture"], cwd=repository, check=True, capture_output=True)

    commit = subprocess.check_output(
        ["git", "rev-parse", "HEAD"], cwd=repository, text=True
    ).strip()
    tree = subprocess.check_output(
        ["git", "rev-parse", "HEAD^{tree}"], cwd=repository, text=True
    ).strip()
    source_tree = subprocess.check_output(
        ["git", "rev-parse", "HEAD:tests/NpcManager.Cli.Tests"],
        cwd=repository, text=True,
    ).strip()
    listing = subprocess.check_output([
        "git", "ls-tree", "-r", "--full-tree", "HEAD", "--",
        "tests/NpcManager.Cli.Tests",
    ], cwd=repository)
    expected_closure = {"root": "tests/NpcManager.Cli.Tests",
        "tree": source_tree, "fileCount": 2, "sha256": _sha256(listing).upper()}
    with pytest.raises(FirewallError, match="detached HEAD"):
        firewall.authenticate_harness_checkout(
            repository, expected_commit=commit, expected_tree=tree,
            expected_source_closure=expected_closure)
    subprocess.run(["git", "checkout", "--detach", commit], cwd=repository,
                   check=True, capture_output=True)
    identity = firewall.authenticate_harness_checkout(
        repository, expected_commit=commit, expected_tree=tree,
        expected_source_closure=expected_closure)
    assert identity == {"commit": commit, "tree": tree,
                        "sourceClosure": expected_closure}
    with pytest.raises(FirewallError, match="expected immutable identity"):
        firewall.authenticate_harness_checkout(
            repository, expected_commit="0" * 40, expected_tree=tree,
            expected_source_closure=expected_closure)
    with pytest.raises(FirewallError, match="expected immutable identity"):
        firewall.authenticate_harness_checkout(
            repository, expected_commit=commit, expected_tree="0" * 40,
            expected_source_closure=expected_closure)
    wrong_closure = dict(expected_closure, sha256="0" * 64)
    with pytest.raises(FirewallError, match="expected immutable identity"):
        firewall.authenticate_harness_checkout(
            repository, expected_commit=commit, expected_tree=tree,
            expected_source_closure=wrong_closure)
    (harness / "Program.cs").write_text("dirty\n")
    with pytest.raises(FirewallError, match="dirty or uncommitted"):
        firewall.authenticate_harness_checkout(
            repository, expected_commit=commit, expected_tree=tree,
            expected_source_closure=expected_closure)
    (harness / "Program.cs").write_text("sealed program\n")
    (harness / "untracked.cs").write_text("untracked source\n")
    with pytest.raises(FirewallError, match="dirty or uncommitted"):
        firewall.authenticate_harness_checkout(
            repository, expected_commit=commit, expected_tree=tree,
            expected_source_closure=expected_closure)
    (harness / "untracked.cs").unlink()
    coordinator = repository / "tools" / "verification"
    coordinator.mkdir(parents=True)
    (coordinator / "ignored.py").write_text("print(1)\n")
    with pytest.raises(FirewallError, match="dirty or uncommitted"):
        firewall.authenticate_harness_checkout(
            repository, expected_commit=commit, expected_tree=tree,
            expected_source_closure=expected_closure)
    (coordinator / "ignored.py").unlink()
    injected = repository / "src" / "Injected.cs"
    injected.parent.mkdir()
    injected.write_text("untracked injected source\n")
    with pytest.raises(FirewallError, match="dirty or uncommitted"):
        firewall.authenticate_harness_checkout(
            repository, expected_commit=commit, expected_tree=tree,
            expected_source_closure=expected_closure)


def test_owned_journey_capture_cleanup_is_token_bound_and_never_deletes_foreign_root(
    k_root: Path,
) -> None:
    first_root, first_token = firewall.create_owned_journey_capture(k_root)
    second_root, second_token = firewall.create_owned_journey_capture(k_root)
    foreign = k_root / ("j-" + "f" * 32)
    foreign.mkdir()
    (foreign / firewall.CAPTURE_OWNER_MARKER).write_text(
        json.dumps({"formatVersion": 1, "kind": "journey-capture", "token": "0" * 32})
    )

    assert first_root != second_root
    assert not firewall.remove_owned_journey_capture(foreign, "f" * 32, k_root)
    assert foreign.is_dir()
    first_lock = k_root / (first_root.name + firewall.CAPTURE_OWNER_LOCK_SUFFIX)
    first_lock.write_text("foreign", encoding="ascii")
    assert not firewall.remove_owned_journey_capture(first_root, first_token, k_root)
    assert first_root.is_dir()
    first_lock.write_text(first_token, encoding="ascii")
    marker = first_root / firewall.CAPTURE_OWNER_MARKER
    marker.write_text("{}", encoding="utf-8")
    assert not firewall.remove_owned_journey_capture(first_root, first_token, k_root)
    assert first_root.is_dir()
    marker.write_bytes(firewall.canonical_json_bytes({
        "formatVersion": 1, "kind": "journey-capture", "token": first_token,
    }))
    assert firewall.remove_owned_journey_capture(first_root, first_token, k_root)
    assert not first_root.exists()
    assert second_root.exists()
    assert firewall.remove_owned_journey_capture(second_root, second_token, k_root)


def test_journey_capture_failure_before_graph_handoff_cleans_only_owned_root(
    k_root: Path,
) -> None:
    owned, token = firewall.create_owned_journey_capture(k_root)
    workspace = owned / ("c-" + token)
    workspace.mkdir()
    (workspace / "unverified.json").write_text("{", encoding="utf-8")
    foreign = k_root / ("j-" + "e" * 32)
    foreign.mkdir()
    (foreign / "unrelated.txt").write_text("foreign", encoding="utf-8")
    try:
        with pytest.raises(FirewallError, match="synthetic handoff failure"):
            raise FirewallError("synthetic handoff failure")
    finally:
        assert firewall.remove_owned_journey_capture(owned, token, k_root)
    assert not owned.exists()
    assert foreign.is_dir()
    assert (foreign / "unrelated.txt").read_text(encoding="utf-8") == "foreign"


def test_owned_journey_capture_is_concurrent_and_preexisting_collision_safe(
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    with ThreadPoolExecutor(max_workers=4) as pool:
        owned = list(pool.map(lambda _: firewall.create_owned_journey_capture(k_root), range(8)))
    assert len({root for root, _ in owned}) == 8
    for root, token in owned:
        assert firewall.remove_owned_journey_capture(root, token, k_root)

    collision = "a" * 32
    replacement = "b" * 32
    stale = k_root / ("j-" + collision)
    stale.mkdir()
    (stale / firewall.CAPTURE_OWNER_MARKER).write_text("crash-stale-owner")
    values = iter([SimpleNamespace(hex=collision), SimpleNamespace(hex=replacement)])
    monkeypatch.setattr(firewall.uuid, "uuid4", lambda: next(values))
    root, token = firewall.create_owned_journey_capture(k_root)
    assert (root, token) == (k_root / ("j-" + replacement), replacement)
    assert stale.is_dir()
    assert firewall.remove_owned_journey_capture(root, token, k_root)


def test_owned_journey_capture_preserves_foreign_owner_lock(
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    collision = "c" * 32
    replacement = "d" * 32
    foreign_lock = k_root / ("j-" + collision + firewall.CAPTURE_OWNER_LOCK_SUFFIX)
    foreign_lock.write_text("foreign", encoding="ascii")
    values = iter([SimpleNamespace(hex=collision), SimpleNamespace(hex=replacement)])
    monkeypatch.setattr(firewall.uuid, "uuid4", lambda: next(values))

    root, token = firewall.create_owned_journey_capture(k_root)

    assert (root, token) == (k_root / ("j-" + replacement), replacement)
    assert foreign_lock.read_text(encoding="ascii") == "foreign"
    assert not (k_root / ("j-" + collision)).exists()
    assert firewall.remove_owned_journey_capture(root, token, k_root)


@pytest.mark.parametrize(
    ("mutation", "message"),
    [
        (lambda value: value["requiredPackagePaths"].clear(), "exactly one launcher"),
        (lambda value: value["requiredPackagePaths"].append({"path": "other.ps1", "role": "launcher"}), "exactly one launcher"),
        (lambda value: value["requiredPackagePaths"][0].update(role="cli"), "exactly one launcher"),
        (lambda value: value["launcher"].update(entrypoint="cli/wrong.ps1"), "launcher path"),
    ],
)
def test_snapshot_launcher_is_exactly_one_authenticated_package_member(
    mutation, message: str,
) -> None:
    launcher = b"param([Parameter(ValueFromRemainingArguments=$true)]$Arguments)\n"
    snapshot = _synthetic_snapshot(b"{}")
    snapshot["requiredPackagePaths"] = [
        {"path": "cli/actorwright.ps1", "role": "launcher"}
    ]
    snapshot["launcher"] = {
        "entrypoint": "cli/actorwright.ps1",
        "shape": "powershell-forwarding-wrapper",
    }
    mutation(snapshot)

    with pytest.raises(FirewallError, match=message):
        firewall._validate_snapshot_document(snapshot)


def test_verify_rejects_baseline_only_pass(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    manifest = tmp_path / "manifest.json"
    manifest.write_bytes(b"{}")
    monkeypatch.setattr(
        firewall,
        "load_baseline",
        lambda value: SimpleNamespace(
            baseline_id="publicSynthetic",
            manifest_path=manifest,
        ),
    )

    assert firewall.main(["verify", "-Baseline", str(tmp_path)]) != 0


def test_source_verify_rejects_noncanonical_repo_cli(
    fake_runner: FakeCliRunner, monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(
        firewall, "load_baseline",
        lambda _id: (_ for _ in ()).throw(AssertionError("noncanonical CLI admitted")),
    )
    with pytest.raises(FirewallError, match="canonical Release CLI"):
        firewall._source_report("publicSynthetic", str(fake_runner.path))


def _source_sdk_fixture(root: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    installed = root / "artifacts" / "tools" / "dotnet-sdk-10.0.301" / "dotnet.exe"
    installed.parent.mkdir(parents=True)
    installed.write_bytes(b"authentic-sdk")
    manifest = root / "tools" / "manifests" / firewall.PINNED_DOTNET_MANIFEST
    manifest.parent.mkdir(parents=True)
    manifest.write_text(json.dumps({
        "archive_sha512": "a" * 128,
        "source": "https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.301/dotnet-sdk-10.0.301-win-x64.zip",
        "dotnet_length": len(b"authentic-sdk"),
        "dotnet_sha256": _sha256(b"authentic-sdk"),
    }), encoding="utf-8")
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", root)
    monkeypatch.setattr(firewall, "PINNED_DOTNET", installed)
    return installed


def test_pinned_sdk_bytes_come_from_closed_manifest_without_archive(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    installed = _source_sdk_fixture(tmp_path, monkeypatch)
    assert firewall._authenticated_pinned_sdk() == (
        len(b"authentic-sdk"), _sha256(b"authentic-sdk"),
    )
    installed.write_bytes(b"spoofed-sdk-same-version")
    with pytest.raises(FirewallError, match="pinned dotnet (sha256|length) mismatch"):
        firewall._authenticated_pinned_sdk()


@pytest.mark.parametrize("mutation", ["missing", "extra", "length-type", "sha-malformed", "source"])
def test_pinned_sdk_refuses_malformed_manifest(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, mutation: str,
) -> None:
    _source_sdk_fixture(tmp_path, monkeypatch)
    path = tmp_path / "tools" / "manifests" / firewall.PINNED_DOTNET_MANIFEST
    value = json.loads(path.read_text(encoding="utf-8"))
    if mutation == "missing":
        del value["dotnet_length"]
    elif mutation == "extra":
        value["unexpected"] = True
    elif mutation == "length-type":
        value["dotnet_length"] = True
    elif mutation == "sha-malformed":
        value["dotnet_sha256"] = "not-a-sha"
    else:
        value["source"] = "https://example.invalid/sdk.zip"
    path.write_text(json.dumps(value), encoding="utf-8")
    with pytest.raises(FirewallError, match="pinned SDK manifest"):
        firewall._authenticated_pinned_sdk()


def _mock_source_builds(
    root: Path, monkeypatch: pytest.MonkeyPatch, *, focused_failure: bool = False,
    build_failure: str | None = None, build_warning: str | None = None,
) -> list[tuple[str, ...]]:
    calls: list[tuple[str, ...]] = []
    _source_sdk_fixture(root, monkeypatch)
    for name in ("NpcManager.Cli.Tests", "NpcManager.Architecture.Tests"):
        project = root / "tests" / name / f"{name}.csproj"
        project.parent.mkdir(parents=True, exist_ok=True)
        project.write_text("<Project />", encoding="utf-8")

    def run(arguments, **kwargs):
        calls.append(tuple(arguments))
        if "build" not in arguments:
            return subprocess.CompletedProcess(
                arguments, 1 if focused_failure else 0,
                b"1 failed\n" if focused_failure else b"3 passed\n", b"",
            )
        name = Path(arguments[2]).stem
        if name != build_failure:
            dll = root / "tests" / name / "bin" / "Release" / "net10.0" / f"{name}.dll"
            dll.parent.mkdir(parents=True, exist_ok=True)
            dll.write_bytes(("fresh-" + name).encode("ascii"))
        output = b"Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n"
        if name == build_warning:
            output = b"warning W1: injected warning\n" + output
        return subprocess.CompletedProcess(arguments, 1 if name == build_failure else 0, output, b"")

    monkeypatch.setattr(firewall, "_run_bounded_process", run)
    return calls


def test_source_selector_build_replaces_stale_dlls_and_binds_hashes(
    k_root: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    calls = _mock_source_builds(k_root, monkeypatch)
    for name in ("NpcManager.Cli.Tests", "NpcManager.Architecture.Tests"):
        dll = k_root / "tests" / name / "bin" / "Release" / "net10.0" / f"{name}.dll"
        dll.parent.mkdir(parents=True, exist_ok=True)
        dll.write_bytes(b"substituted-stale-dll")
    sdk_hash, cli_hash, architecture_hash, evidence = firewall._build_source_selector_projects()
    assert sdk_hash == _sha256(b"authentic-sdk")
    assert cli_hash == _sha256(b"fresh-NpcManager.Cli.Tests")
    assert architecture_hash == _sha256(b"fresh-NpcManager.Architecture.Tests")
    assert len(calls) == 2
    assert all(call[0] == str(firewall.PINNED_DOTNET) and call[1] == "build" for call in calls)
    assert all("--no-restore" in call and "--no-incremental" in call for call in calls)
    assert [Path(call[2]).stem for call in calls] == [
        "NpcManager.Cli.Tests", "NpcManager.Architecture.Tests",
    ]
    assert sum(item.startswith("buildStdoutBase64=") for item in evidence) == 2


@pytest.mark.parametrize("condition", ["failure", "warning", "missing-output"])
def test_source_selector_build_refuses_unproven_output(
    k_root: Path, monkeypatch: pytest.MonkeyPatch, condition: str,
) -> None:
    name = "NpcManager.Cli.Tests"
    _mock_source_builds(
        k_root, monkeypatch,
        build_failure=name if condition in {"failure", "missing-output"} else None,
        build_warning=name if condition == "warning" else None,
    )
    if condition == "missing-output":
        original = firewall._run_bounded_process
        def success_without_output(arguments, **kwargs):
            result = original(arguments, **kwargs)
            return subprocess.CompletedProcess(arguments, 0, result.stdout, result.stderr)
        monkeypatch.setattr(firewall, "_run_bounded_process", success_without_output)
    with pytest.raises(FirewallError, match="build (failed or warned|produced no DLL)"):
        firewall._build_source_selector_projects()


@pytest.mark.parametrize("unsafe_project", [
    "NpcManager.Cli.Tests", "NpcManager.Architecture.Tests",
])
def test_source_selector_build_refuses_reparse_ancestor_before_any_unlink(
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
    unsafe_project: str,
    require_ntfs_fixture,
    filesystem_fixture_unavailable,
) -> None:
    filesystem = require_ntfs_fixture(k_root, "source selector output reparse ancestor")
    calls = _mock_source_builds(k_root, monkeypatch)
    dlls = []
    foreign = k_root / "foreign" / f"{unsafe_project}.dll"
    foreign.parent.mkdir()
    foreign.write_bytes(b"external-output-must-survive")
    for name in ("NpcManager.Cli.Tests", "NpcManager.Architecture.Tests"):
        dll = k_root / "tests" / name / "bin" / "Release" / "net10.0" / f"{name}.dll"
        if name != unsafe_project:
            dll.parent.mkdir(parents=True, exist_ok=True)
            dll.write_bytes(b"existing-canonical-output")
        dlls.append(dll)
    reparse_ancestor = (k_root / "tests" / unsafe_project / "bin" / "Release" / "net10.0")
    reparse_ancestor.parent.mkdir(parents=True, exist_ok=True)
    try:
        reparse_ancestor.symlink_to(foreign.parent, target_is_directory=True)
    except (OSError, NotImplementedError) as error:
        filesystem_fixture_unavailable(
            k_root, filesystem, "source selector output reparse ancestor", error
        )

    with pytest.raises(FirewallError, match="DLL path contains reparse point"):
        firewall._build_source_selector_projects()
    assert all(dll.read_bytes() == b"existing-canonical-output" for dll in dlls if dll.parent != reparse_ancestor)
    assert foreign.read_bytes() == b"external-output-must-survive"
    assert calls == []


def test_source_selector_build_path_inspection_error_fails_closed(
    k_root: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    calls = _mock_source_builds(k_root, monkeypatch)
    first_dll = (k_root / "tests" / "NpcManager.Cli.Tests" / "bin" / "Release"
                 / "net10.0" / "NpcManager.Cli.Tests.dll")
    first_dll.parent.mkdir(parents=True)
    first_dll.write_bytes(b"existing-canonical-output")
    second_ancestor = (k_root / "tests" / "NpcManager.Architecture.Tests" / "bin"
                       / "Release" / "net10.0")
    original_kind = firewall._unresolved_path_kind
    def unreadable_kind(path: Path):
        if path == second_ancestor:
            raise OSError("unreadable path metadata")
        return original_kind(path)
    monkeypatch.setattr(firewall, "_unresolved_path_kind", unreadable_kind)

    with pytest.raises(FirewallError, match="cannot inspect canonical selector DLL path"):
        firewall._build_source_selector_projects()
    assert first_dll.read_bytes() == b"existing-canonical-output"
    assert calls == []


@pytest.mark.parametrize(
    ("missing_selector", "wrong_version", "failed_focused", "cross_version",
     "build_mismatch", "identity_mutation"),
    [(False, False, False, False, False, None),
     (True, False, False, False, False, None),
     (False, True, False, False, False, None),
     (False, False, True, False, False, None),
     (False, False, False, True, False, None),
     (False, False, False, False, True, None),
     (False, False, False, True, False, "protocol2"),
     (False, False, False, True, False, "capabilities")],
)
def test_source_verify_emits_only_observed_closed_gates(
    fake_runner: FakeCliRunner, k_root: Path,
    monkeypatch: pytest.MonkeyPatch, missing_selector: bool,
    wrong_version: bool, failed_focused: bool, cross_version: bool,
    build_mismatch: bool, identity_mutation: str | None,
) -> None:
    snapshot = firewall.capture_cli_contract(
        CliTarget("source", fake_runner.path, k_root, None)
    )
    canonical_cli = k_root / "src" / "NpcManager.Cli" / "bin" / "Release" / "net10.0" / "actorwright.exe"
    canonical_cli.parent.mkdir(parents=True)
    shutil.copy2(fake_runner.path, canonical_cli)
    calls = _mock_source_builds(k_root, monkeypatch, focused_failure=failed_focused)
    for project in ("NpcManager.Cli.Tests", "NpcManager.Architecture.Tests"):
        dll = k_root / "tests" / project / "bin" / "Release" / "net10.0" / f"{project}.dll"
        dll.parent.mkdir(parents=True)
        dll.write_bytes(b"fixture-dll")
    projection = {
        "provenance": {
            "productVersion": snapshot["version"]["protocol1"]["version"],
            "sourceLine": snapshot["version"]["protocol1"]["sourceLine"],
        },
        "commandCount": snapshot["commandCount"],
        "orderedCommandNamesSha256": snapshot["orderedCommandNamesSha256"],
        "commands": [
            firewall._project_manifest_command(row, snapshot["behaviorFacts"])
            for row in snapshot["commands"]
        ],
        "exitMeanings": snapshot["exitMeanings"],
        "voiceDialogueSentinels": snapshot["voiceDialogueSentinels"],
    }
    manifest = k_root / "fixture-manifest.json"
    manifest.write_bytes(b"{}")
    monkeypatch.setattr(
        firewall, "load_baseline",
        lambda _id: SimpleNamespace(
            baseline_id="fixture", manifest_path=manifest, snapshot=projection,
        ),
    )
    observed_snapshot = deepcopy(snapshot)
    candidate_version = "1.0.0-preview.276" if cross_version or build_mismatch else "1.0.0-preview.275"
    candidate_line = "preview.276-private" if cross_version or build_mismatch else "preview.275-private"
    build_info = k_root / "src/NpcManager.Application/BuildInfo.cs"
    build_info.parent.mkdir(parents=True, exist_ok=True)
    build_info.write_text(
        f'public const string ProductVersion = "{candidate_version}";\n'
        f'public const string SourceLine = "{candidate_line}";\n', encoding="utf-8")
    if cross_version:
        observed_snapshot["version"]["protocol1"].update(
            version=candidate_version, sourceLine=candidate_line)
        observed_snapshot["version"]["protocol2"].update(
            productVersion=candidate_version, sourceLine=candidate_line)
        observed_snapshot["capabilities"]["protocol1"]["document"].update(
            version=candidate_version, sourceLine=candidate_line)
    if wrong_version:
        observed_snapshot["version"]["protocol1"]["version"] = "wrong-version"
    if identity_mutation == "protocol2":
        observed_snapshot["version"]["protocol2"]["productVersion"] = "wrong-version"
    if identity_mutation == "capabilities":
        observed_snapshot["capabilities"]["protocol1"]["document"]["sourceLine"] = "wrong-line"
    monkeypatch.setattr(firewall, "capture_cli_contract", lambda _target: observed_snapshot)
    selector_ids = [
        gate_id for gate_id in firewall.required_gate_ids("Source")
        if gate_id.startswith("selector:")
    ]
    observed_selector_ids = selector_ids[:-1] if missing_selector else selector_ids
    def selectors(**kwargs):
        assert [Path(call[2]).stem for call in calls if "build" in call] == [
            "NpcManager.Cli.Tests", "NpcManager.Architecture.Tests",
        ]
        assert kwargs["expected_dotnet_sha256"] == _sha256(b"authentic-sdk")
        assert kwargs["expected_cli_project_sha256"] == _sha256(b"fresh-NpcManager.Cli.Tests")
        assert kwargs["expected_architecture_project_sha256"] == _sha256(
            b"fresh-NpcManager.Architecture.Tests"
        )
        return tuple(
            firewall.GateResult(gate_id, True, "PASS", ("selector executed",), ())
            for gate_id in observed_selector_ids
        )
    monkeypatch.setattr(firewall, "run_required_selectors", selectors)
    report_path = firewall.CAPTURE_WORKSPACE_PARENT / f"source-report-{uuid.uuid4().hex}.json"
    try:
        exit_code = firewall.main([
            "verify", "-Tier", "Source", "-Baseline", "fixture",
            "-SourceCli", str(canonical_cli), "-Output", str(report_path),
        ])
        report = json.loads(report_path.read_text(encoding="utf-8"))
        assert set(report) == {
            "$schema", "formatVersion", "generatedAtUtc", "tier", "baselineId",
            "compatible", "verdict", "fixtureBacked", "realInstallation",
            "gates", "differences", "pinRows",
        }
        assert report["tier"] == "Source"
        assert report["realInstallation"] is False
        assert report["fixtureBacked"] is False
        focused_gate = next(
            row for row in report["gates"]
            if row["gateId"] == "verification:focused-tests"
        )
        assert any(item.startswith("stdoutBase64=") for item in focused_gate["evidence"])
        assert any(item.startswith("stderrBase64=") for item in focused_gate["evidence"])
        assert len([call for call in calls if "build" in call]) == 2
        selector_gate = next(row for row in report["gates"] if row["gateId"] == selector_ids[0])
        assert sum(item.startswith("buildStdoutBase64=") for item in selector_gate["evidence"]) == 2
        assert {row["gateId"] for row in report["gates"]} == (
            set(firewall.required_gate_ids("Source"))
            - ({selector_ids[-1]} if missing_selector else set())
        )
        assert report["compatible"] is (
            not missing_selector and not wrong_version and not failed_focused
            and not build_mismatch and identity_mutation is None
        )
        if identity_mutation:
            assert any(row["pointer"].startswith("/identity/") for row in report["differences"])
        assert exit_code == (1 if missing_selector or wrong_version or failed_focused
                               or build_mismatch or identity_mutation else 0)
    finally:
        report_path.unlink(missing_ok=True)


def test_source_verify_reports_unavailable_selectors_as_blocked(
    fake_runner: FakeCliRunner, k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    snapshot = firewall.capture_cli_contract(
        CliTarget("source", fake_runner.path, k_root, None)
    )
    canonical_cli = k_root / "src" / "NpcManager.Cli" / "bin" / "Release" / "net10.0" / "actorwright.exe"
    canonical_cli.parent.mkdir(parents=True)
    shutil.copy2(fake_runner.path, canonical_cli)
    build_info = k_root / "src/NpcManager.Application/BuildInfo.cs"
    build_info.parent.mkdir(parents=True, exist_ok=True)
    build_info.write_text(
        'public const string ProductVersion = "1.0.0-preview.275";\n'
        'public const string SourceLine = "preview.275-private";\n', encoding="utf-8")
    _mock_source_builds(k_root, monkeypatch)
    for project in ("NpcManager.Cli.Tests", "NpcManager.Architecture.Tests"):
        dll = k_root / "tests" / project / "bin" / "Release" / "net10.0" / f"{project}.dll"
        dll.parent.mkdir(parents=True)
        dll.write_bytes(b"fixture-dll")
    manifest = k_root / "fixture-manifest.json"
    manifest.write_bytes(b"{}")
    monkeypatch.setattr(
        firewall, "load_baseline",
        lambda _id: SimpleNamespace(
            baseline_id="fixture", manifest_path=manifest,
            snapshot={
                "provenance": {
                    "productVersion": snapshot["version"]["protocol1"]["version"],
                    "sourceLine": snapshot["version"]["protocol1"]["sourceLine"],
                },
                "commandCount": snapshot["commandCount"],
                "orderedCommandNamesSha256": snapshot["orderedCommandNamesSha256"],
                "commands": [
                    firewall._project_manifest_command(row, snapshot["behaviorFacts"])
                    for row in snapshot["commands"]
                ],
                "exitMeanings": snapshot["exitMeanings"],
                "voiceDialogueSentinels": snapshot["voiceDialogueSentinels"],
            },
        ),
    )
    monkeypatch.setattr(firewall, "capture_cli_contract", lambda _target: snapshot)
    monkeypatch.setattr(
        firewall, "run_required_selectors",
        lambda **kwargs: (_ for _ in ()).throw(FirewallError("selector DLL absent")),
    )
    report_path = firewall.CAPTURE_WORKSPACE_PARENT / f"source-report-{uuid.uuid4().hex}.json"
    try:
        assert firewall.main([
            "verify", "-Tier", "Source", "-Baseline", "fixture",
            "-SourceCli", str(canonical_cli), "-Output", str(report_path),
        ]) == 1
        report = json.loads(report_path.read_text(encoding="utf-8"))
        selector_gates = [row for row in report["gates"] if row["gateId"].startswith("selector:")]
        assert len(selector_gates) == 7
        assert all(row["status"] == "BLOCKED" for row in selector_gates)
    finally:
        report_path.unlink(missing_ok=True)


def test_package_tier_requires_all_three_observed_gates() -> None:
    required = set(firewall.required_gate_ids("Package"))
    assert required == {
        "verification:package-staging",
        "package:inventory-entrypoint",
        "comparison:source-package-baseline",
    }
    gates = [firewall.GateResult(item, True, "PASS", ("observed",), ()) for item in required]
    assert firewall.evaluate_tier("Package", gates).verdict == "PACKAGE_COMPATIBLE"
    assert firewall.evaluate_tier("Package", gates[:-1]).verdict == "INCOMPATIBLE"
    assert firewall.evaluate_tier("Package", [*gates, gates[0]]).verdict == "INCOMPATIBLE"


def test_package_verify_rejects_non_package_arguments() -> None:
    for extra in ("-ReleaseZip", "-CandidateBundle", "-PromotionBundle",
                  "-ConsumerAdmission", "-ActivationReport"):
        assert firewall.main([
            "verify", "-Tier", "Package", "-Baseline", "publicSynthetic",
            "-SourceCli", "K:\\Actorwright\\src\\NpcManager.Cli\\bin\\Release\\net10.0\\actorwright.exe",
            "-ReleaseRoot", "K:\\Actorwright\\artifacts\\stage",
            "-Output", "K:\\Actorwright\\artifacts\\test-work\\new.json",
            extra, "K:\\Actorwright\\extra",
        ]) == 2


def test_full_verify_requires_exact_public_inputs() -> None:
    common = [
        "verify", "-Tier", "Full", "-Baseline", "publicSynthetic",
        "-SourceCli", r"K:\Actorwright\src\NpcManager.Cli\bin\Release\net10.0\actorwright.exe",
        "-ReleaseRoot", r"K:\Actorwright\artifacts\release",
        "-ReleaseZip", r"K:\Actorwright\artifacts\release.zip",
        "-Output", r"K:\Actorwright\artifacts\test-work\fresh.json",
    ]
    for flag in ("-SourceCli", "-ReleaseRoot", "-ReleaseZip", "-Output"):
        index = common.index(flag)
        assert firewall.main(common[:index] + common[index + 2:]) == 2
    for flag in ("-CandidateBundle", "-CandidateReport", "-TagRepository",
                 "-PromotionBundle", "-ConsumerAdmission", "-ActivationReport"):
        assert firewall.main([*common, flag, r"K:\Actorwright\extra"]) == 2
    assert firewall.main([*common, "-FixtureBacked"]) == 2


@pytest.fixture
def disposable_tag_repository() -> Path:
    parent = ROOT / "artifacts" / "test-work"
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="compatibility-tag-fixture-", dir=parent) as name:
        repository = Path(name)
        subprocess.run(["git", "init", "-q", str(repository)], check=True)
        yield repository


def test_candidate_verify_serializes_full_gates_and_authenticated_bundle(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
    disposable_tag_repository: Path,
) -> None:
    repository = disposable_tag_repository
    (repository / "fixture.txt").write_text("candidate fixture\n", encoding="utf-8")
    subprocess.run(["git", "add", "fixture.txt"], cwd=repository, check=True)
    subprocess.run([
        "git", "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
        "commit", "-q", "-m", "fixture",
    ], cwd=repository, check=True)
    tag = "v9.9.9-disposable-test"
    subprocess.run([
        "git", "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
        "tag", "-a", tag, "-m", "fixture tag",
    ], cwd=repository, check=True)
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=repository).decode().strip()
    tree = subprocess.check_output(["git", "rev-parse", "HEAD^{tree}"], cwd=repository).decode().strip()
    bundle, root, archive = _candidate_fixture(tmp_path, commit=commit, tree=tree, tag=tag)
    full_gates = [
        {"gateId": gate_id, "required": True, "status": "PASS",
         "evidence": ["observed"], "differences": []}
        for gate_id in firewall.required_gate_ids("Full")
    ]
    monkeypatch.setattr(firewall, "_full_report", lambda *_: {
        "$schema": "urn:actorwright:compatibility:v1:report", "formatVersion": 1,
        "generatedAtUtc": "2026-09-24T00:00:00Z", "tier": "Full",
        "baselineId": "publicSynthetic", "compatible": True,
        "verdict": "FULL_COMPATIBLE", "fixtureBacked": False,
        "realInstallation": False, "gates": full_gates,
        "differences": [], "pinRows": [],
    })
    output_parent = ROOT / "artifacts" / "test-work"
    output_parent.mkdir(parents=True, exist_ok=True)
    monkeypatch.setattr(firewall, "CAPTURE_WORKSPACE_PARENT", output_parent)
    output = output_parent / f"candidate-report-{uuid.uuid4().hex}.json"

    assert firewall.main([
        "verify", "-Tier", "Candidate", "-Baseline", "publicSynthetic",
        "-SourceCli", str(tmp_path / "source.exe"),
        "-ReleaseRoot", str(root), "-ReleaseZip", str(archive),
        "-CandidateBundle", str(bundle), "-TagRepository", str(repository),
        "-FixtureBacked", "-Output", str(output),
    ]) == 0
    report = json.loads(output.read_text(encoding="utf-8"))
    assert set(report) == set(json.loads((CONTRACT_ROOT / "compatibility-report.schema.json").read_text(
        encoding="utf-8"))["required"])
    assert report["tier"] == "Candidate"
    assert report["verdict"] == "CANDIDATE_COMPATIBLE"
    assert report["compatible"] is True
    assert report["fixtureBacked"] is True
    assert report["realInstallation"] is False
    assert {gate["gateId"] for gate in report["gates"]} == set(firewall.required_gate_ids("Candidate"))
    assert next(gate for gate in report["gates"] if gate["gateId"] == "pins:candidate")["status"] == "PASS"
    assert {"tag-name", "tag-commit", "tag-tree", "candidate-producer-package-sha256"} <= {
        row["pinId"] for row in report["pinRows"]
    }
    assert next(row for row in report["pinRows"] if row["pinId"] == "tag-commit")[
        "producerValue"] == commit
    output.unlink()


def _saved_full_fixture(
    workspace: Path, *, commit: str = "C" * 40, tree: str = "D" * 40,
) -> tuple[Path, Path, Path, Path, dict[str, object]]:
    bundle, root, archive = _candidate_fixture(
        workspace, commit=commit, tree=tree, tag="v9.9.9-disposable-test",
        package_cli=b"sealed package CLI",
    )
    fresh = firewall.verify_release_root_compatibility(root, archive)
    assert fresh.compatible and fresh.verdict == "FULL_COMPATIBLE"
    source_cli_sha = "A" * 64
    package_cli_sha = hashlib.sha256((root / "cli" / "actorwright.exe").read_bytes()).hexdigest().upper()
    gates = []
    for gate_id in firewall.required_gate_ids("Full"):
        evidence = ["observed"]
        if gate_id == "contract:command-surface":
            evidence = [f"cliSha256={source_cli_sha}", "commandCount=142"]
        elif gate_id == "verification:release":
            evidence = [f"root={root.resolve()}", f"zip={archive.resolve()}",
                        f"zipLength={archive.stat().st_size}",
                        f"zipSha256={hashlib.sha256(archive.read_bytes()).hexdigest().upper()}"]
        elif gate_id == "comparison:source-package-baseline":
            evidence = [f"sourceSha256={source_cli_sha}",
                        f"packageSha256={package_cli_sha}", "differences=0"]
        gates.append({"gateId": gate_id, "required": True, "status": "PASS",
                      "evidence": evidence, "differences": []})
    report: dict[str, object] = {
        "$schema": "urn:actorwright:compatibility:v1:report", "formatVersion": 1,
        "generatedAtUtc": "2026-09-24T00:00:00Z", "tier": "Full",
        "baselineId": "publicSynthetic", "compatible": True,
        "verdict": "FULL_COMPATIBLE", "fixtureBacked": False,
        "realInstallation": False, "gates": gates, "differences": [],
        "pinRows": [{
            "pinId": row.pin_id, "stage": row.stage,
            "producer": row.producer, "consumer": row.consumer,
            "producerValue": row.producer_value,
            "consumerValue": row.consumer_value,
            "identityClass": row.identity_class,
            "producerEvidencePath": row.producer_evidence_path,
            "consumerEvidencePath": row.consumer_evidence_path,
            "status": row.status,
        } for row in fresh.pin_rows],
    }
    full_path = workspace / "full-report.json"
    full_path.write_bytes(firewall.canonical_json_bytes(report))
    candidate_archive = workspace / "actorwright-1.0.0-preview.276.zip"
    shutil.copy2(archive, candidate_archive)
    (bundle / archive.name).rename(bundle / candidate_archive.name)
    manifest_path = bundle / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["files"] = [{
        "path": path.name, "size": path.stat().st_size,
        "sha256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
    } for path in sorted(bundle.iterdir()) if path != manifest_path]
    manifest_path.write_bytes(firewall.canonical_json_bytes(manifest))
    return bundle, root, candidate_archive, full_path, report


def test_candidate_reuses_saved_full_only_after_reauthentication(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
    disposable_tag_repository: Path,
) -> None:
    workspace = ROOT / "artifacts" / "test-work" / f"saved-full-{uuid.uuid4().hex}"
    workspace.mkdir(parents=True)
    try:
        repository = disposable_tag_repository
        (repository / "fixture.txt").write_text("candidate fixture\n", encoding="utf-8")
        subprocess.run(["git", "-c", "user.name=Fixture", "-c",
                        "user.email=fixture@example.invalid", "-C", str(repository),
                        "add", "fixture.txt"], check=True)
        subprocess.run(["git", "-c", "user.name=Fixture", "-c",
                        "user.email=fixture@example.invalid", "-C", str(repository),
                        "commit", "-q", "-m", "fixture"], check=True)
        subprocess.run(["git", "-c", "user.name=Fixture", "-c",
                        "user.email=fixture@example.invalid", "-C", str(repository),
                        "tag", "-a", "v9.9.9-disposable-test", "-m", "fixture tag"], check=True)
        commit = subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=repository,
        ).decode().strip()
        tree = subprocess.check_output(
            ["git", "rev-parse", "HEAD^{tree}"], cwd=repository,
        ).decode().strip()
        bundle, root, archive, full_path, _ = _saved_full_fixture(
            workspace, commit=commit, tree=tree)
        full_sha = hashlib.sha256(full_path.read_bytes()).hexdigest().upper()
        def unexpected_full_rerun(*_args: object, **_kwargs: object) -> dict[str, object]:
            pytest.fail("saved-Full Candidate path reran Full")
        monkeypatch.setattr(firewall, "_full_report", unexpected_full_rerun)
        output_parent = ROOT / "artifacts" / "test-work"
        output = output_parent / f"candidate-report-{uuid.uuid4().hex}.json"
        monkeypatch.setattr(firewall, "CAPTURE_WORKSPACE_PARENT", output_parent)

        assert firewall.main([
            "verify", "-Tier", "Candidate", "-Baseline", "publicSynthetic",
            "-ReleaseRoot", str(root), "-ReleaseZip", str(archive),
            "-CandidateBundle", str(bundle), "-TagRepository", str(repository),
            "-FullReport", str(full_path), "-FullReportSha256", full_sha,
            "-FixtureBacked", "-Output", str(output),
        ]) == 0
        report = json.loads(output.read_text(encoding="utf-8"))
        candidate_gate = next(row for row in report["gates"] if row["gateId"] == "pins:candidate")
        assert f"savedFullReport={full_path}" in candidate_gate["evidence"]
        assert f"savedFullReportSha256={full_sha}" in candidate_gate["evidence"]
        assert "fullTierEvidence=REUSED" in candidate_gate["evidence"]
        assert report["verdict"] == "CANDIDATE_COMPATIBLE"
        output.unlink()
    finally:
        shutil.rmtree(workspace)


@pytest.mark.parametrize(
    "tamper", ["gate", "gate-inventory", "pin", "zip-path-pin", "zip",
               "source", "package", "duplicate"]
)
def test_candidate_refuses_saved_full_evidence_drift(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
    disposable_tag_repository: Path, tamper: str,
) -> None:
    workspace = ROOT / "artifacts" / "test-work" / f"saved-full-drift-{uuid.uuid4().hex}"
    workspace.mkdir(parents=True)
    try:
        bundle, root, archive, full_path, report = _saved_full_fixture(workspace)
        if tamper == "gate":
            report["gates"][0]["status"] = "FAIL"
        elif tamper == "gate-inventory":
            report["gates"].pop()
        elif tamper == "pin":
            report["pinRows"][0]["producerValue"] = "tampered"
        elif tamper == "zip-path-pin":
            next(row for row in report["pinRows"]
                 if row["pinId"] == "source-overlap:releaseFileInventory")[
                     "consumerEvidencePath"] = str(workspace / "unbound.zip")
        elif tamper == "zip":
            archive.write_bytes(archive.read_bytes() + b"tampered")
        elif tamper == "source":
            next(row for row in report["gates"]
                 if row["gateId"] == "comparison:source-package-baseline")["evidence"][0] = (
                     f"sourceSha256={'B' * 64}")
        elif tamper == "package":
            next(row for row in report["gates"]
                 if row["gateId"] == "comparison:source-package-baseline")["evidence"][1] = (
                     f"packageSha256={'B' * 64}")
        if tamper == "duplicate":
            full_path.write_bytes(
                b'{"$schema":"urn:actorwright:compatibility:v1:report",'
                b'"$schema":"urn:actorwright:compatibility:v1:report"}'
            )
        else:
            full_path.write_bytes(firewall.canonical_json_bytes(report))
        monkeypatch.setattr(firewall, "resolve_tag_pin_nodes", lambda *_: _resolved_fixture_tag())

        expected_error = {
            "gate": "saved Full gate inventory",
            "gate-inventory": "saved Full gate inventory",
            "pin": "saved Full pin rows differ",
            "zip-path-pin": "saved Full release inventory path does not match verification gate",
            "zip": "saved Full release ZIP length or SHA-256 differs from current bytes",
            "source": "source/package CLI identities are inconsistent",
            "package": "package CLI SHA-256 differs",
            "duplicate": "duplicate JSON key",
        }[tamper]
        with pytest.raises(firewall.FirewallError, match=expected_error):
            firewall._candidate_report(
                "publicSynthetic", None, str(root), str(archive), str(bundle),
                disposable_tag_repository, full_report=full_path,
                full_report_sha256=hashlib.sha256(full_path.read_bytes()).hexdigest().upper(),
            )
    finally:
        shutil.rmtree(workspace)


def test_candidate_refuses_saved_full_report_sha256_mismatch(
    tmp_path: Path, disposable_tag_repository: Path,
) -> None:
    workspace = ROOT / "artifacts" / "test-work" / f"saved-full-sha-{uuid.uuid4().hex}"
    workspace.mkdir(parents=True)
    try:
        bundle, root, archive, full_path, _ = _saved_full_fixture(workspace)
        with pytest.raises(firewall.FirewallError, match="SHA-256"):
            firewall._candidate_report(
                "publicSynthetic", None, str(root), str(archive), str(bundle),
                disposable_tag_repository, full_report=full_path,
                full_report_sha256="0" * 64,
            )
    finally:
        shutil.rmtree(workspace)


def test_candidate_saved_full_requires_hash_pair_and_no_source_cli() -> None:
    common = [
        "verify", "-Tier", "Candidate", "-Baseline", "publicSynthetic",
        "-ReleaseRoot", r"K:\Actorwright\artifacts\release",
        "-ReleaseZip", r"K:\Actorwright\artifacts\release.zip",
        "-CandidateBundle", r"K:\ExampleExchange\releases\candidate",
        "-TagRepository", r"K:\Actorwright\artifacts\test-work\tag-repository",
        "-Output", r"K:\Actorwright\artifacts\test-work\fresh.json",
    ]
    report, report_sha = r"K:\Actorwright\artifacts\test-work\full.json", "A" * 64
    assert firewall.main([*common, "-FullReport", report]) == 2
    assert firewall.main([*common, "-FullReportSha256", report_sha]) == 2
    assert firewall.main([*common, "-FullReport", report, "-FullReportSha256", report_sha,
                          "-SourceCli", r"K:\Actorwright\src\NpcManager.Cli\bin\Release\net10.0\actorwright.exe"]) == 2


def test_candidate_verify_refuses_missing_bundle_and_consumer_stage_inputs() -> None:
    common = [
        "verify", "-Tier", "Candidate", "-Baseline", "publicSynthetic",
        "-SourceCli", r"K:\Actorwright\src\NpcManager.Cli\bin\Release\net10.0\actorwright.exe",
        "-ReleaseRoot", r"K:\Actorwright\artifacts\release",
        "-ReleaseZip", r"K:\Actorwright\artifacts\release.zip",
        "-CandidateBundle", r"K:\Actorwright\artifacts\candidate",
        "-TagRepository", r"K:\Actorwright\artifacts\test-work\disposable-tag-repository",
        "-Output", r"K:\Actorwright\artifacts\test-work\fresh.json",
    ]
    for flag in ("-SourceCli", "-ReleaseRoot", "-ReleaseZip", "-CandidateBundle",
                 "-TagRepository", "-Output"):
        index = common.index(flag)
        assert firewall.main(common[:index] + common[index + 2:]) == 2
    for flag in ("-PromotionBundle", "-ConsumerAdmission", "-ActivationReport"):
        assert firewall.main([*common, flag, r"K:\Actorwright\extra"]) == 2


@pytest.mark.parametrize("full_compatible", [False, True])
def test_candidate_report_fails_closed_when_full_or_bundle_fails(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, full_compatible: bool,
    disposable_tag_repository: Path,
) -> None:
    bundle, root, archive = _candidate_fixture(tmp_path)
    monkeypatch.setattr(firewall, "resolve_tag_pin_nodes", lambda *_: _resolved_fixture_tag())
    if full_compatible:
        (bundle / archive.name).write_bytes(b"substituted package")
    full_gates = [
        {"gateId": gate_id, "required": True, "status": "PASS",
         "evidence": ["observed"], "differences": []}
        for gate_id in firewall.required_gate_ids("Full")
    ]
    if not full_compatible:
        full_gates[0]["status"] = "FAIL"
    monkeypatch.setattr(firewall, "_full_report", lambda *_: {
        "gates": full_gates, "compatible": full_compatible,
        "differences": [], "pinRows": [], "fixtureBacked": False,
        "realInstallation": False,
    })

    report = firewall._candidate_report(
        "publicSynthetic", "source", str(root), str(archive), str(bundle),
        disposable_tag_repository)

    assert report["verdict"] == "INCOMPATIBLE"
    assert report["compatible"] is False
    pin_gate = next(gate for gate in report["gates"] if gate["gateId"] == "pins:candidate")
    assert pin_gate["status"] == ("FAIL" if full_compatible else "PASS")


def _consumer_stage_fixture(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, tag_repository: Path,
) -> tuple[Path, Path, Path, Path, Path, Path, Path]:
    bundle, root, archive = _candidate_fixture(tmp_path)
    monkeypatch.setattr(firewall, "resolve_tag_pin_nodes", lambda *_: _resolved_fixture_tag())
    gates = [{"gateId": gate_id, "required": True, "status": "PASS",
              "evidence": ["observed"], "differences": []}
             for gate_id in firewall.required_gate_ids("Full")]
    monkeypatch.setattr(firewall, "_full_report", lambda *_: {
        "$schema": "urn:actorwright:compatibility:v1:report", "formatVersion": 1,
        "generatedAtUtc": "2026-09-24T00:00:00Z", "tier": "Full",
        "baselineId": "publicSynthetic", "compatible": True,
        "verdict": "FULL_COMPATIBLE", "fixtureBacked": False,
        "realInstallation": False, "gates": gates,
        "differences": [], "pinRows": [],
    })
    producer = tmp_path / "candidate-report.json"
    producer.write_bytes(canonical_json_bytes(firewall._candidate_report(
        "publicSynthetic", "source", str(root), str(archive), str(bundle),
        tag_repository, fixture_backed=True)))
    promotion = tmp_path / "promotion"
    promotion.mkdir()
    manifest_hash = _sha256((bundle / "bundle-manifest.json").read_bytes())
    payload = {"$schema": "urn:actorwright:exchange:v1:promotion", "version": 1,
               "candidateManifestSha256": manifest_hash, "createdUtc": "2026-09-24T00:00:00Z",
               "decidedBy": "Fixture", "decision": "APPROVED"}
    (promotion / "promotion.json").write_bytes(canonical_json_bytes(payload))
    promotion_manifest = {
        "$schema": "urn:actorwright:exchange:v1:bundle-manifest", "version": 1,
        "kind": "promotion", "payload": "promotion.json",
        "files": [{"path": "promotion.json", "size": (promotion / "promotion.json").stat().st_size,
                   "sha256": _sha256((promotion / "promotion.json").read_bytes())}],
    }
    (promotion / "bundle-manifest.json").write_bytes(canonical_json_bytes(promotion_manifest))
    package = bundle / archive.name
    base = {"$schema": "urn:actorwright:compatibility:v1:consumer-stage-report",
            "formatVersion": 1, "generatedAtUtc": "2026-09-24T00:00:01Z",
            "candidateBundleManifestSha256": manifest_hash,
            "packageLength": package.stat().st_size, "packageSha256": _sha256(package.read_bytes()),
            "contractSchemaSha256": _sha256(firewall.CONSUMER_STAGE_REPORT_SCHEMA.read_bytes()),
            "producerReportSha256": _sha256(producer.read_bytes()), "status": "PASS"}
    admission = tmp_path / "admission.json"
    activation = tmp_path / "activation.json"
    for path, stage in ((admission, "Admission"), (activation, "Activation")):
        if stage == "Admission":
            evidence = [
                "producerReportFixtureBacked:true",
                "consumer Analyze and candidate/promotion readback",
                f"targetManifestSha256:{'A' * 64}",
                f"promotionBundleManifestSha256:{_sha256((promotion / 'bundle-manifest.json').read_bytes())}",
                f"activeManifestSha256:{'B' * 64}",
                f"preActivationWrapperSha256:{'C' * 64}",
                "rollbackExecutablePath:K:\\fixture\\rollback.exe",
                "rollbackExecutableLength:42",
                f"rollbackExecutableSha256:{'D' * 64}",
                f"packagedLauncherSha256:{firewall.EXPECTED_PREVIEW_275_LAUNCHER_SHA256}",
            ]
        else:
            evidence = [
                "producerReportFixtureBacked:true",
                "consumer Apply/Verify and activation readback",
                f"admissionReportSha256:{_sha256(admission.read_bytes())}",
                f"packagedLauncherSha256:{firewall.EXPECTED_PREVIEW_275_LAUNCHER_SHA256}",
                f"activeWrapperSha256:{'E' * 64}",
                f"rollbackExecutableSha256:{'D' * 64}",
            ]
        path.write_bytes(canonical_json_bytes({**base, "stage": stage,
            "gates": [{"gateId": f"consumer-{stage.lower()}-pin-closure",
                       "required": True, "status": "PASS", "evidence": evidence}],
            "evidence": evidence}))
    return producer, bundle, root, archive, promotion, admission, activation


def test_admission_preserves_candidate_report_and_closes_consumer_lineage(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, disposable_tag_repository: Path,
) -> None:
    producer, bundle, root, archive, promotion, admission, _ = _consumer_stage_fixture(
        tmp_path, monkeypatch, disposable_tag_repository)

    result = firewall._later_report(
        "Admission", "publicSynthetic", producer, root, archive, bundle, promotion, admission,
        tag_repository=disposable_tag_repository, fixture_backed=True)

    assert result["verdict"] == "ADMISSION_COMPATIBLE"
    assert result["fixtureBacked"] is True
    assert result["realInstallation"] is False
    assert {"pins:candidate", "pins:promotion", "pins:consumer-admission"} <= {
        row["gateId"] for row in result["gates"]}
    assert {"consumer:producer-report-sha256", "promotion-candidate-manifest-sha256"} <= {
        row["pinId"] for row in result["pinRows"]}


def test_admission_refuses_rewritten_candidate_report_bytes(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, disposable_tag_repository: Path,
) -> None:
    producer, bundle, root, archive, promotion, admission, _ = _consumer_stage_fixture(
        tmp_path, monkeypatch, disposable_tag_repository)
    producer.write_bytes(producer.read_bytes() + b" ")

    result = firewall._later_report(
        "Admission", "publicSynthetic", producer, root, archive, bundle, promotion, admission,
        tag_repository=disposable_tag_repository, fixture_backed=True)

    assert result["verdict"] == "INCOMPATIBLE"
    assert any(row["status"] != "PASS" for row in result["gates"])


def test_activation_requires_admission_and_matching_activation_report(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, disposable_tag_repository: Path,
) -> None:
    producer, bundle, root, archive, promotion, admission, activation = _consumer_stage_fixture(
        tmp_path, monkeypatch, disposable_tag_repository)
    result = firewall._later_report(
        "Activation", "publicSynthetic", producer, root, archive, bundle, promotion,
        admission, activation, tag_repository=disposable_tag_repository,
        fixture_backed=True)
    assert result["verdict"] == "ACTIVATION_COMPATIBLE"
    assert result["fixtureBacked"] is True
    assert result["realInstallation"] is False
    changed = json.loads(activation.read_text(encoding="utf-8"))
    changed["producerReportSha256"] = "0" * 64
    activation.write_bytes(canonical_json_bytes(changed))
    refused = firewall._later_report(
        "Activation", "publicSynthetic", producer, root, archive, bundle, promotion,
        admission, activation, tag_repository=disposable_tag_repository,
        fixture_backed=True)
    assert refused["verdict"] == "INCOMPATIBLE"


def test_activation_refuses_admission_report_byte_drift(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, disposable_tag_repository: Path,
) -> None:
    producer, bundle, root, archive, promotion, admission, activation = _consumer_stage_fixture(
        tmp_path, monkeypatch, disposable_tag_repository)
    admission.write_bytes(admission.read_bytes() + b" ")

    report = firewall._later_report(
        "Activation", "publicSynthetic", producer, root, archive, bundle, promotion,
        admission, activation, tag_repository=disposable_tag_repository,
        fixture_backed=True)

    assert report["verdict"] == "INCOMPATIBLE"


@pytest.mark.parametrize("mutation", [
    "rollback-hash-drift", "duplicate-rollback-key", "fixture-token-suffix",
    "malformed-rollback-hash", "launcher-token-suffix",
])
def test_activation_requires_exact_shared_consumer_evidence(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, disposable_tag_repository: Path,
    mutation: str,
) -> None:
    producer, bundle, root, archive, promotion, admission, activation = _consumer_stage_fixture(
        tmp_path, monkeypatch, disposable_tag_repository)
    value = json.loads(activation.read_text(encoding="utf-8"))
    evidence = value["evidence"]
    if mutation == "rollback-hash-drift":
        evidence[-1] = f"rollbackExecutableSha256:{'F' * 64}"
    elif mutation == "duplicate-rollback-key":
        evidence.append(evidence[-1])
    elif mutation == "fixture-token-suffix":
        evidence[0] += "-spoof"
    elif mutation == "malformed-rollback-hash":
        evidence[-1] = "rollbackExecutableSha256:xyz"
    else:
        evidence[3] += "-spoof"
    value["gates"][0]["evidence"] = evidence
    activation.write_bytes(canonical_json_bytes(value))

    report = firewall._later_report(
        "Activation", "publicSynthetic", producer, root, archive, bundle, promotion,
        admission, activation, tag_repository=disposable_tag_repository,
        fixture_backed=True)

    assert report["verdict"] == "INCOMPATIBLE"


def test_admission_refuses_promotion_manifest_drift(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, disposable_tag_repository: Path,
) -> None:
    producer, bundle, root, archive, promotion, admission, _ = _consumer_stage_fixture(
        tmp_path, monkeypatch, disposable_tag_repository)
    (promotion / "bundle-manifest.json").write_bytes(
        (promotion / "bundle-manifest.json").read_bytes() + b" ")

    report = firewall._later_report(
        "Admission", "publicSynthetic", producer, root, archive, bundle, promotion,
        admission, tag_repository=disposable_tag_repository, fixture_backed=True)

    assert report["verdict"] == "INCOMPATIBLE"


def test_admission_requires_matching_fixture_flag(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, disposable_tag_repository: Path,
) -> None:
    producer, bundle, root, archive, promotion, admission, _ = _consumer_stage_fixture(
        tmp_path, monkeypatch, disposable_tag_repository)

    report = firewall._later_report(
        "Admission", "publicSynthetic", producer, root, archive, bundle, promotion,
        admission, tag_repository=disposable_tag_repository, fixture_backed=False)

    assert report["verdict"] == "INCOMPATIBLE"


@pytest.mark.parametrize("mutation", ["boolean-version", "missing-analyze", "wrong-fixture-flag"])
def test_admission_refuses_malformed_consumer_stage_evidence(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, disposable_tag_repository: Path,
    mutation: str,
) -> None:
    producer, bundle, root, archive, promotion, admission, _ = _consumer_stage_fixture(
        tmp_path, monkeypatch, disposable_tag_repository)
    value = json.loads(admission.read_text(encoding="utf-8"))
    if mutation == "boolean-version":
        value["formatVersion"] = True
    elif mutation == "missing-analyze":
        value["evidence"] = value["gates"][0]["evidence"] = [
            item for item in value["evidence"] if item != "consumer Analyze and candidate/promotion readback"]
    else:
        value["evidence"] = value["gates"][0]["evidence"] = [
            item.replace("producerReportFixtureBacked:true", "producerReportFixtureBacked:false")
            for item in value["evidence"]]
    admission.write_bytes(canonical_json_bytes(value))

    result = firewall._later_report(
        "Admission", "publicSynthetic", producer, root, archive, bundle, promotion,
        admission, tag_repository=disposable_tag_repository, fixture_backed=True)

    assert result["verdict"] == "INCOMPATIBLE"


@pytest.mark.parametrize("stage", ["Admission", "Activation"])
def test_consumer_stages_are_exposed_through_verify_cli(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, stage: str,
    disposable_tag_repository: Path,
) -> None:
    producer, bundle, root, archive, promotion, admission, activation = _consumer_stage_fixture(
        tmp_path, monkeypatch, disposable_tag_repository)
    output_parent = ROOT / "artifacts" / "test-work"
    output_parent.mkdir(parents=True, exist_ok=True)
    monkeypatch.setattr(firewall, "CAPTURE_WORKSPACE_PARENT", output_parent)
    output = output_parent / f"consumer-{stage.lower()}-{uuid.uuid4().hex}.json"
    args = ["verify", "-Tier", stage, "-Baseline", "publicSynthetic",
            "-ReleaseRoot", str(root), "-ReleaseZip", str(archive),
            "-CandidateReport", str(producer), "-CandidateBundle", str(bundle),
            "-TagRepository", str(disposable_tag_repository), "-FixtureBacked",
            "-PromotionBundle", str(promotion), "-ConsumerAdmission", str(admission),
            "-Output", str(output)]
    if stage == "Activation":
        args.extend(["-ActivationReport", str(activation)])
    try:
        assert firewall.main(args) == 0
        report = json.loads(output.read_text(encoding="utf-8"))
        assert report["verdict"] == f"{stage.upper()}_COMPATIBLE"
        assert report["fixtureBacked"] is True
        assert report["realInstallation"] is False
    finally:
        output.unlink(missing_ok=True)


def test_consumer_stage_cli_requires_saved_candidate_and_no_source_cli() -> None:
    common = ["verify", "-Tier", "Admission", "-Baseline", "publicSynthetic",
              "-ReleaseRoot", r"K:\Actorwright\artifacts\release",
              "-ReleaseZip", r"K:\Actorwright\artifacts\release.zip",
              "-CandidateReport", r"K:\Actorwright\artifacts\candidate-report.json",
              "-CandidateBundle", r"K:\Actorwright\artifacts\candidate",
              "-TagRepository", r"K:\Actorwright\artifacts\test-work\disposable-tag-repository",
              "-PromotionBundle", r"K:\Actorwright\artifacts\promotion",
              "-ConsumerAdmission", r"K:\Actorwright\artifacts\admission.json",
              "-Output", r"K:\Actorwright\artifacts\test-work\fresh.json"]
    for flag in ("-ReleaseRoot", "-ReleaseZip", "-CandidateReport", "-CandidateBundle",
                 "-TagRepository",
                 "-PromotionBundle", "-ConsumerAdmission", "-Output"):
        index = common.index(flag)
        assert firewall.main(common[:index] + common[index + 2:]) == 2
    assert firewall.main([*common, "-SourceCli", r"K:\Actorwright\source.exe"]) == 2
    assert firewall.main([*common, "-ActivationReport", r"K:\Actorwright\activation.json"]) == 2
    assert firewall.main([*common[:common.index("Admission")], "Activation",
                          *common[common.index("Admission") + 1:]]) == 2


def test_full_report_never_passes_unproved_gates(monkeypatch: pytest.MonkeyPatch) -> None:
    source_gates = [
        {"gateId": gate_id, "required": True, "status": "PASS", "evidence": ["observed"],
         "differences": []}
        for gate_id in firewall.required_gate_ids("Source")
    ]
    monkeypatch.setattr(firewall, "_source_report", lambda *_: {
        "gates": source_gates, "differences": [], "compatible": True,
    })
    monkeypatch.setattr(firewall, "_full_release_gates", lambda *_: ())
    report = firewall._full_report("publicSynthetic", "source", "root", "zip")
    assert report["verdict"] == "INCOMPATIBLE"
    assert report["compatible"] is False
    assert {row["gateId"] for row in report["gates"]} == set(firewall.required_gate_ids("Full"))
    assert all(row["status"] == "BLOCKED" for row in report["gates"] if row["gateId"] not in
               firewall.required_gate_ids("Source"))


def test_full_report_records_unavailable_source_evidence(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(firewall, "_source_report", lambda *_: (
        _ for _ in ()).throw(FirewallError("source unavailable")))
    monkeypatch.setattr(firewall, "_full_release_gates", lambda *_: ())
    report = firewall._full_report("publicSynthetic", "source", "root", "zip")
    assert report["verdict"] == "INCOMPATIBLE"
    assert all(row["status"] == "BLOCKED" for row in report["gates"])
    assert any("source unavailable" in row["evidence"][0] for row in report["gates"])


def test_full_report_passes_only_with_each_observed_full_gate(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    source_ids = set(firewall.required_gate_ids("Source"))
    monkeypatch.setattr(firewall, "_source_report", lambda *_: {
        "gates": [{"gateId": gate_id, "required": True, "status": "PASS",
                   "evidence": ["observed"], "differences": []}
                  for gate_id in source_ids],
        "differences": [],
    })
    full_only = [firewall.GateResult(gate_id, True, "PASS", ("observed",), ())
                 for gate_id in firewall.required_gate_ids("Full") if gate_id not in source_ids]
    monkeypatch.setattr(firewall, "_full_release_gates", lambda *_: full_only)
    result = firewall._full_report("publicSynthetic", "source", "root", "zip")
    assert result["compatible"] is False
    assert result["verdict"] == "INCOMPATIBLE"  # mandatory pins are absent
    monkeypatch.setattr(firewall, "_full_release_gates", lambda *_: full_only[:-1])
    assert firewall._full_report("publicSynthetic", "source", "root", "zip")["verdict"] == "INCOMPATIBLE"


def test_full_report_carries_verified_pin_rows(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(firewall, "_source_report", lambda *_: {
        "gates": [], "differences": [],
    })
    expected = firewall.PinRow("source-overlap:version", "Source", "left", "right",
                               "1", "1", "source-version", "a", "b", "PASS")
    def full_gates(_baseline, _source, _root, _zip, rows):
        rows.append(expected)
        return ()
    monkeypatch.setattr(firewall, "_full_release_gates", full_gates)
    report = firewall._full_report("publicSynthetic", "source", "root", "zip")
    assert report["pinRows"] == [{
        "pinId": "source-overlap:version", "stage": "Source",
        "producer": "left", "consumer": "right", "producerValue": "1",
        "consumerValue": "1", "identityClass": "source-version",
        "producerEvidencePath": "a", "consumerEvidencePath": "b", "status": "PASS",
    }]


def test_full_identity_binds_head_and_cli_versions_without_binary_equality() -> None:
    release = {"sourceCommit": "a" * 40, "sourceTree": "b" * 40,
               "version": "1.0.0-preview.275"}
    source = {"version": "1.0.0-preview.275", "sourceLine": "preview.275-private"}
    package = dict(source)
    assert firewall._full_identity_differences(
        release, "preview.275-private", "a" * 40, "b" * 40, source, package) == ()
    assert [row.pointer for row in firewall._full_identity_differences(
        release, "preview.275-private", "c" * 40, "b" * 40, source, package)] == [
            "/sourceCommit"]
    assert [row.pointer for row in firewall._full_identity_differences(
        release, "preview.275-private", "a" * 40, "b" * 40, source,
        {**package, "sourceLine": "wrong"})] == ["/package/sourceLine"]
    assert [row.pointer for row in firewall._full_identity_differences(
        {**release, "version": "wrong-release-version"}, "preview.275-private",
        "a" * 40, "b" * 40, source, package)] == [
            "/source/version", "/package/version"]
    assert [row.pointer for row in firewall._full_identity_differences(
        release, "wrong-release-line", "a" * 40, "b" * 40,
        source, package)] == ["/source/sourceLine", "/package/sourceLine"]
    assert [row.pointer for row in firewall._full_identity_differences(
        {**release, "version": "1.0.0-PREVIEW.275"}, "preview.275-private",
        "a" * 40, "b" * 40, source, package)] == [
            "/source/version", "/package/version"]
    assert [row.pointer for row in firewall._full_identity_differences(
        release, "PREVIEW.275-PRIVATE", "a" * 40, "b" * 40,
        source, package)] == ["/source/sourceLine", "/package/sourceLine"]


def test_source_build_identity_ignores_commented_spoof_and_rejects_ambiguity(
    k_root: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", k_root)
    path = k_root / "src/NpcManager.Application/BuildInfo.cs"
    path.parent.mkdir(parents=True)
    path.write_text(
        '// public const string ProductVersion = "spoof";\n'
        '// public const string SourceLine = "spoof";\n'
        '/*\npublic const string ProductVersion = "block-spoof";\n'
        'public const string SourceLine = "block-spoof";\n*/\n'
        'public const string ProductVersion = "1.0.0-preview.276";\n'
        'public const string SourceLine = "preview.276-private";\n', encoding="utf-8")
    assert firewall._source_build_identity() == {
        "version": "1.0.0-preview.276", "sourceLine": "preview.276-private"}
    with path.open("a", encoding="utf-8") as stream:
        stream.write('public const string ProductVersion = "duplicate";\n')
    with pytest.raises(FirewallError, match="ProductVersion"):
        firewall._source_build_identity()
    path.write_text(
        '// public const string ProductVersion = "spoof";\n'
        'public const string SourceLine = "preview.276-private";\n', encoding="utf-8")
    with pytest.raises(FirewallError, match="ProductVersion"):
        firewall._source_build_identity()


def test_journey_runner_requires_fresh_authenticated_build(
    k_root: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    runner = k_root / "tests/NpcManager.Cli.Tests/bin/Release/net10.0/NpcManager.Cli.Tests.dll"
    runner.parent.mkdir(parents=True)
    runner.write_bytes(b"stale")
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", k_root)
    monkeypatch.setattr(firewall, "_build_source_selector_projects", lambda: (
        "d" * 64, _sha256(b"fresh"), "e" * 64, ("rebuilt",)))
    with pytest.raises(FirewallError, match="fresh.*runner|runner.*build"):
        firewall._fresh_journey_runner()


def test_full_release_fixture_carries_real_pins_and_binds_distinct_clis(
    k_root: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root, archive = _release_overlap_fixture(k_root)
    package_cli = root / "cli/actorwright.exe"
    package_cli.parent.mkdir()
    package_cli.write_bytes(b"sealed-package")
    _reseal_release_fixture(root, archive)
    source_cli = k_root / "src/NpcManager.Cli/bin/Release/net10.0/actorwright.exe"
    source_cli.parent.mkdir(parents=True)
    source_cli.write_bytes(b"different-source")
    temp_parent = k_root / "test-work"
    temp_parent.mkdir()
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", k_root)
    monkeypatch.setattr(firewall, "CAPTURE_WORKSPACE_PARENT", temp_parent)
    monkeypatch.setattr(release_verifier, "verify", lambda *_args, **_kwargs: {
        "status": "PASS", "zipVerified": True, "files": 8,
    })
    release = json.loads((root / "actorwright-release.json").read_text(encoding="utf-8"))
    baseline_snapshot = {
        "provenance": {"productVersion": "1.0.0-preview.275",
                       "sourceLine": "preview.275-private"},
        "commandCount": 2, "orderedCommandNamesSha256": "order", "commands": [],
        "exitMeanings": [], "voiceDialogueSentinels": [], "members": [],
    }
    monkeypatch.setattr(firewall, "load_baseline", lambda _: SimpleNamespace(
        snapshot=baseline_snapshot, member_paths=()))
    monkeypatch.setattr(firewall, "run_voice_dialogue_package_probes", lambda *_args, **_kwargs: (
        firewall.GateResult(gate_id, True, "PASS", ("observed",), ())
        for gate_id in firewall._PACKAGE_GATE_IDS))
    source_version = [release["version"]]
    package_identity_mutation = [None]
    def capture(target):
        version = source_version[0] if target.name == "source" else release["version"]
        result = {"version": {"protocol1": {"version": version, "sourceLine": "fixture-private"},
                            "protocol2": {"productVersion": version, "sourceLine": "fixture-private"}},
                "capabilities": {"protocol1": {"document": {
                    "version": version, "sourceLine": "fixture-private"}}},
                "commandCount": 2, "orderedCommandNamesSha256": "order",
                "commands": [], "behaviorFacts": [], "exitMeanings": [],
                "voiceDialogueSentinels": []}
        if target.name == "package" and package_identity_mutation[0] == "protocol2":
            result["version"]["protocol2"]["productVersion"] = "wrong-version"
        if target.name == "package" and package_identity_mutation[0] == "capabilities":
            result["capabilities"]["protocol1"]["document"]["sourceLine"] = "wrong-line"
        return result
    monkeypatch.setattr(firewall, "capture_cli_contract", capture)
    def git(_root, *args):
        if args == ("rev-parse", "HEAD"):
            return ("C" * 40 + "\n").encode()
        if args == ("rev-parse", "HEAD^{tree}"):
            return ("D" * 40 + "\n").encode()
        pytest.fail(f"unexpected Git operation: {args}")
    monkeypatch.setattr(firewall, "_git", git)
    monkeypatch.setattr(firewall, "_source_scope_gate", lambda: firewall.GateResult(
        "audit:source-scope", True, "BLOCKED", ("fixture stops journeys",), ()))
    from tools.architecture import validate_standalone_test_registry, validate_repository_independence
    monkeypatch.setattr(validate_standalone_test_registry, "validate", lambda _: [])
    monkeypatch.setattr(validate_repository_independence, "validate_repository", lambda _: [])
    matrix = k_root / "tests/standalone-test-matrix.json"
    matrix.parent.mkdir(exist_ok=True)
    matrix.write_text(json.dumps({"tests": [
        {"classification": "runnable"}, {"classification": "fixture-bound"}]}), encoding="utf-8")
    monkeypatch.setattr(firewall, "_authenticated_pinned_sdk", lambda: (7, "a" * 64))
    pytest_invocations = []
    pytest_bases: list[Path] = []
    pytest_outcome = ["success"]
    pytest_artifacts = k_root / "artifacts"
    artifact_sibling = pytest_artifacts / "keep"
    outside_artifacts = k_root / "outside-artifacts"
    artifact_sibling.mkdir(parents=True, exist_ok=True)
    outside_artifacts.mkdir()
    (artifact_sibling / "sentinel.txt").write_bytes(b"keep")
    (outside_artifacts / "sentinel.txt").write_bytes(b"keep")
    def bounded(arguments, **kwargs):
        if "pytest" in arguments:
            pytest_invocations.append((arguments, kwargs["env"]))
            basetemp_index = arguments.index("--basetemp")
            pytest_base = Path(arguments[basetemp_index + 1])
            pytest_base.mkdir(parents=True)
            pytest_bases.append(pytest_base)
            scratch = pytest_base / "fixture.txt"
            scratch.write_bytes(b"pytest scratch")
            readonly_scratch = pytest_base / "readonly.txt"
            readonly_scratch.write_bytes(b"readonly pytest scratch")
            readonly_scratch.chmod(0o444)
            assert readonly_scratch.stat().st_file_attributes & stat.FILE_ATTRIBUTE_READONLY
            if pytest_outcome[0] == "timeout":
                raise FirewallError("simulated pytest timeout")
            if pytest_outcome[0] == "failure":
                return SimpleNamespace(
                    returncode=17,
                    stdout=b"2 passed, 0 skipped, 0 warnings in 0.01s\n",
                    stderr=b"failed fixture details",
                )
            if pytest_outcome[0] == "malformed":
                return SimpleNamespace(
                    returncode=0, stdout=b"pytest exited without a summary\n", stderr=b"",
                )
        stdout = (b"standalone selector launcher: PASS\n" if "run_standalone_selectors.py" in
                  " ".join(map(str, arguments)) else b"2 passed, 0 skipped, 0 warnings in 0.01s\n")
        return SimpleNamespace(returncode=0, stdout=stdout, stderr=b"")
    monkeypatch.setattr(firewall, "_run_bounded_process", bounded)
    rows: list[firewall.PinRow] = []
    gates = firewall._full_release_gates("publicSynthetic", str(source_cli), str(root),
                                          str(archive), rows)
    by_id = {gate.gate_id: gate for gate in gates}
    assert by_id["verification:release"].status == "PASS"
    assert by_id["comparison:source-package-baseline"].status == "PASS"
    for mutation in ("protocol2", "capabilities"):
        package_identity_mutation[0] = mutation
        inconsistent = firewall._full_release_gates(
            "publicSynthetic", str(source_cli), str(root), str(archive))
        comparison = {gate.gate_id: gate for gate in inconsistent}[
            "comparison:source-package-baseline"]
        assert comparison.status == "FAIL"
        assert any(row.pointer.startswith("/identity/") for row in comparison.differences)
    package_identity_mutation[0] = None
    assert {row.pin_id for row in rows} == firewall.FULL_ZIP_PIN_IDS
    assert "runnablePass=1" in by_id["selectors:complete-standalone-matrix"].evidence
    assert "fixtureBoundSkip=1" in by_id["selectors:complete-standalone-matrix"].evidence
    assert pytest_invocations
    for invocation, environment in pytest_invocations:
        index = invocation.index("--basetemp")
        assert Path(invocation[index + 1]).is_relative_to(k_root / "artifacts")
        assert environment["GIT_CONFIG_COUNT"] == "1"
        assert environment["GIT_CONFIG_KEY_0"] == "safe.directory"
        assert environment["GIT_CONFIG_VALUE_0"] == "*"
        assert environment["ACTORWRIGHT_TEST_DOTNET"] == str(firewall.PINNED_DOTNET)
    assert pytest_bases and all(not path.exists() for path in pytest_bases)
    assert (artifact_sibling / "sentinel.txt").read_bytes() == b"keep"
    assert (outside_artifacts / "sentinel.txt").read_bytes() == b"keep"
    preserved_paths: list[Path] = []
    for outcome in ("failure", "malformed", "timeout"):
        pytest_outcome[0] = outcome
        failed_gates = firewall._full_release_gates(
            "publicSynthetic", str(source_cli), str(root), str(archive))
        python_gate = {gate.gate_id: gate for gate in failed_gates}["verification:python-suite"]
        failed_path = pytest_bases[-1]
        assert python_gate.status == "BLOCKED"
        assert str(failed_path) in " ".join(python_gate.evidence)
        assert failed_path.is_dir() and (failed_path / "fixture.txt").is_file()
        preserved_paths.append(failed_path)
    pytest_outcome[0] = "success"
    from tools.storage_retention import remove_exact_path

    for failed_path in preserved_paths:
        remove_exact_path(failed_path)
    monkeypatch.setattr(firewall, "_source_report", lambda *_: {
        "gates": [{"gateId": gate_id, "required": True, "status": "PASS",
                   "evidence": ["observed"], "differences": []}
                  for gate_id in firewall.required_gate_ids("Source")],
        "differences": [],
    })
    report = firewall._full_report("publicSynthetic", str(source_cli), str(root), str(archive))
    assert report["pinRows"]
    assert {row["pinId"] for row in report["pinRows"]} == firewall.FULL_ZIP_PIN_IDS
    assert report["verdict"] == "INCOMPATIBLE"  # fixture declines real journeys
    assert {row["gateId"] for row in report["gates"]} == set(firewall.required_gate_ids("Full"))
    with monkeypatch.context() as cleanup_patch:
        cleanup_patch.setattr(firewall, "_remove_disposable_path", lambda _: (
            _ for _ in ()).throw(FirewallError("owner cleanup refused")))
        capture_cleanup = firewall._full_release_gates(
            "publicSynthetic", str(source_cli), str(root), str(archive))
    assert {gate.gate_id: gate for gate in capture_cleanup}[
        "comparison:source-package-baseline"].status == "BLOCKED"
    source_version[0] = "drifted"
    drift = firewall._full_release_gates("publicSynthetic", str(source_cli), str(root),
                                          str(archive))
    assert {gate.gate_id: gate for gate in drift}["comparison:source-package-baseline"].status == "FAIL"
    source_version[0] = release["version"]
    monkeypatch.setattr(firewall, "_source_scope_gate", lambda: firewall.GateResult(
        "audit:source-scope", True, "PASS", ("fixture",), ()))
    def journey_git(_root, *args):
        if args == ("rev-parse", "HEAD:tests/NpcManager.Cli.Tests"):
            return b"E" * 40 + b"\n"
        if args[:2] == ("ls-tree", "-r"):
            return b"100644 blob " + b"f" * 40 + b"\ttests/NpcManager.Cli.Tests/CompatibilityJourneyObservation.cs\n"
        return git(_root, *args)
    monkeypatch.setattr(firewall, "_git", journey_git)
    monkeypatch.setattr(firewall, "_fresh_journey_runner", lambda: (
        _ for _ in ()).throw(FirewallError("fresh build unavailable")))
    monkeypatch.setattr(firewall, "remove_owned_journey_capture", lambda *_: False)
    cleanup_report = firewall._full_report("publicSynthetic", str(source_cli), str(root),
                                           str(archive))
    journey_rows = [row for row in cleanup_report["gates"] if row["gateId"].startswith("journey:")]
    assert len(journey_rows) == 2
    assert all(row["status"] == "BLOCKED" and "cleanup refused" in row["evidence"][0]
               for row in journey_rows)
    assert cleanup_report["verdict"] == "INCOMPATIBLE"


def test_full_release_gates_block_without_sealed_inputs() -> None:
    gates = firewall._full_release_gates("publicSynthetic", "source", "root", "zip")
    assert {gate.gate_id for gate in gates} == (
        set(firewall.required_gate_ids("Full")) - set(firewall.required_gate_ids("Source")))
    assert all(gate.status == "BLOCKED" for gate in gates)


def test_private_source_scope_rejects_dirty_tree_before_diff(monkeypatch: pytest.MonkeyPatch) -> None:
    calls = []
    def git(_root, *args):
        calls.append(args)
        if args[:2] == ("status", "--porcelain=v1"):
            return b" M src/NpcManager.Cli/Program.cs\n"
        pytest.fail("dirty audit continued to Git diff")
    monkeypatch.setattr(firewall, "_git", git)
    result = firewall._source_scope_gate()
    assert result.status == "BLOCKED"
    assert "clean" in result.evidence[0]
    assert len(calls) == 1


@pytest.mark.parametrize("changed", [
    b"src/NpcManager.Application/UnapprovedSource.cs\0",
    b"artifacts/generated.json\0",
    b"tests/unknown/private.txt\0",
])
def test_private_source_scope_refuses_protected_or_undeclared_paths(
    monkeypatch: pytest.MonkeyPatch, changed: bytes,
) -> None:
    start = firewall._TRANCHE_START_COMMIT.encode("ascii")
    def git(_root, *args):
        if args[0] == "status":
            return b""
        if args[0] == "ls-tree":
            return b""
        if args[0] == "rev-parse":
            return {firewall._TRANCHE_START_COMMIT: start,
                    "HEAD": b"b" * 40, "HEAD^{tree}": b"c" * 40}[args[1]] + b"\n"
        if args[0] == "merge-base":
            return start + b"\n"
        if args[0] == "diff":
            return changed
        pytest.fail(f"unexpected Git operation: {args}")
    monkeypatch.setattr(firewall, "_git", git)
    monkeypatch.setattr(firewall, "_private_tranche_anchor_exists", lambda: True)
    gate = firewall._source_scope_gate()
    assert gate.status == "BLOCKED"
    assert "out-of-scope" in gate.evidence[0]


@pytest.mark.parametrize("changed", [
    b".gitattributes\0README.md\0docs/observability.md\0"
    b"docs/releases/1.0.0-preview.277.md\0"
    b"docs/releases/1.0.0-preview.278.md\0"
    b"docs/releases/1.0.0-preview.279.md\0"
    b"docs/releases/1.0.0-preview.280.md\0"
    b"src/NpcManager.Application/ActorwrightObservabilityEventSource.cs\0"
    b"src/NpcManager.Cli/Program.cs\0"
    b"src/NpcManager.Infrastructure/AgentWorkflowBundleTransitionService.cs\0"
    b"src/NpcManager.Infrastructure/KOnlyWorkspacePolicy.cs\0"
    b"src/NpcManager.Infrastructure/WorkspacePolicyShadow.cs\0"
    b"tests/NpcManager.Architecture.Tests/Program.cs\0"
    b"tests/NpcManager.Architecture.Tests/WorkspacePolicyShadowTests.cs\0",
    b"src/NpcManager.Infrastructure/ArchiveConsistencyService.cs\0"
    b"src/NpcManager.Infrastructure/ArmorAddonModelProposalService.cs\0"
    b"src/NpcManager.Infrastructure/ArmorAddonProposalService.cs\0"
    b"src/NpcManager.Infrastructure/ArmorDamageResistanceService.cs\0"
    b"src/NpcManager.Infrastructure/ArmorProposalService.cs\0"
    b"src/NpcManager.Infrastructure/ChangeActionService.cs\0"
    b"src/NpcManager.Infrastructure/ChangeTrackingService.cs\0"
    b"src/NpcManager.Infrastructure/LeveledListProposalService.cs\0"
    b"src/NpcManager.Infrastructure/MaterialSwapProposalService.cs\0"
    b"src/NpcManager.Infrastructure/ObjectTemplatePropertyProposalService.cs\0"
    b"src/NpcManager.Infrastructure/ObjectTemplateProposalService.cs\0"
    b"src/NpcManager.Infrastructure/OutfitProposalService.cs\0"
    b"src/NpcManager.Infrastructure/ReferencePresetSessionService.cs\0"
    b"src/NpcManager.Presets/PresetService.cs\0"
    b"tests/NpcManager.Architecture.Tests/SkyrimNpcFinishCoreOutfitRaceTests.cs\0"
    b"tests/Directory.Build.props\0"
    b"tests/NpcManager.Assets.Tests/Program.cs\0"
    b"tests/NpcManager.BethesdaFaceRouting.Tests/Program.cs\0"
    b"tests/NpcManager.Desktop.Smoke/Preview254ExternalSmpDesktopTestRegistry.cs\0"
    b"tests/NpcManager.FaceGen.Tests/Program.cs\0"
    b"tests/NpcManager.FaceGeomOrchestration.Tests/Program.cs\0"
    b"tests/NpcManager.Gate1.Tests/Program.cs\0"
    b"tests/NpcManager.Gate2.PipelineIntegration.Tests/Program.cs\0"
    b"tests/NpcManager.Gate2.Tests/Program.cs\0"
    b"tests/NpcManager.NpcCreationAppearance.Tests/Program.cs\0"
    b"tests/NpcManager.ReferencePreset.Tests/Program.cs\0"
    b"tests/NpcManager.ReferencePreset.Tests/ReferencePresetSessionTests.cs\0"
    b"tests/TestInfrastructure/StandaloneSelectorInventory.cs\0",
    b"docs/product-capabilities.md\0",
    b"docs/releases/1.0.0-preview.276.md\0",
    b"src/NpcManager.Application/BuildInfo.cs\0",
    b"tests/NpcManager.Architecture.Tests/AgentProtocolContractTests.cs\0",
    b"tests/standalone-test-matrix.json\0",
])
def test_private_source_scope_accepts_exact_approved_release_paths(
    monkeypatch: pytest.MonkeyPatch, changed: bytes,
) -> None:
    start = firewall._TRANCHE_START_COMMIT.encode("ascii")
    def git(_root, *args):
        if args[0] == "status":
            return b""
        if args[0] == "ls-tree":
            return b""
        if args[0] == "rev-parse":
            return {firewall._TRANCHE_START_COMMIT: start,
                    "HEAD": b"b" * 40, "HEAD^{tree}": b"c" * 40}[args[1]] + b"\n"
        if args[0] == "merge-base":
            return start + b"\n"
        if args[0] == "diff":
            return changed
        pytest.fail(f"unexpected Git operation: {args}")
    monkeypatch.setattr(firewall, "_git", git)
    monkeypatch.setattr(firewall, "_private_tranche_anchor_exists", lambda: True)
    gate = firewall._source_scope_gate()
    assert gate.status == "PASS"
    assert "mode=private-tranche-diff" in gate.evidence
    assert f"startCommit={firewall._TRANCHE_START_COMMIT}" in gate.evidence


_PUBLIC_SOURCE_SCOPE_MARKER = {
    "schemaVersion": 1,
    "mode": "public-root",
}


def _test_git(root: Path, *arguments: str) -> bytes:
    environment = os.environ.copy()
    environment.update({
        "GIT_AUTHOR_NAME": "Source scope test",
        "GIT_AUTHOR_EMAIL": "source-scope@example.invalid",
        "GIT_COMMITTER_NAME": "Source scope test",
        "GIT_COMMITTER_EMAIL": "source-scope@example.invalid",
    })
    repo_root = str(root.resolve())
    completed = subprocess.run(
        ["git", "-c", f"safe.directory={repo_root}", "-C", repo_root, *arguments],
        check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        env=environment,
    )
    return completed.stdout


def _source_scope_test_repo(tmp_path: Path, *, marker: bool = True) -> Path:
    root = tmp_path / "source-scope-repo"
    root.mkdir()
    _test_git(root, "init")
    _test_git(root, "config", "user.name", "Source scope test")
    _test_git(root, "config", "user.email", "source-scope@example.invalid")
    (root / "README.md").write_text("public baseline\n", encoding="utf-8")
    if marker:
        marker_path = root / "tools/manifests/public-source-scope.json"
        marker_path.parent.mkdir(parents=True)
        marker_path.write_text(
            json.dumps(_PUBLIC_SOURCE_SCOPE_MARKER, indent=2) + "\n",
            encoding="utf-8",
        )
    _test_git(root, "add", "--all")
    _test_git(root, "commit", "-m", "public source baseline")
    return root


def _commit_source_scope_change(root: Path, relative_path: str) -> str:
    path = root / relative_path
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("change\n", encoding="utf-8")
    _test_git(root, "add", "--all")
    _test_git(root, "commit", "-m", "source scope change")
    return _test_git(root, "rev-parse", "HEAD").decode().strip()


def test_public_source_scope_accepts_initial_identity_baseline(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = _source_scope_test_repo(tmp_path)
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", root)

    gate = firewall._source_scope_gate()
    head = _test_git(root, "rev-parse", "HEAD").decode().strip()
    tree = _test_git(root, "rev-parse", "HEAD^{tree}").decode().strip()
    tracked = _test_git(root, "ls-tree", "-r", "-z", "--full-tree", "HEAD")
    tracked_count = len([entry for entry in tracked.split(b"\0") if entry])

    assert gate.status == "PASS"
    assert gate.gate_id == "audit:source-scope"
    assert "mode=public-root-baseline" in gate.evidence
    assert f"rootCommit={head}" in gate.evidence
    assert f"commit={head}" in gate.evidence
    assert f"tree={tree}" in gate.evidence
    assert f"trackedEntryCount={tracked_count}" in gate.evidence
    assert not any(item.startswith("changedPathCount=") for item in gate.evidence)


def test_public_source_scope_checks_descendant_paths(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = _source_scope_test_repo(tmp_path)
    root_commit = _test_git(root, "rev-parse", "HEAD").decode().strip()
    head = _commit_source_scope_change(root, "tools/verification/example.py")
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", root)

    gate = firewall._source_scope_gate()

    assert gate.status == "PASS"
    assert "mode=public-root-diff" in gate.evidence
    assert f"rootCommit={root_commit}" in gate.evidence
    assert f"commit={head}" in gate.evidence
    assert "changedPath=tools/verification/example.py" in gate.evidence


def test_public_source_scope_refuses_out_of_scope_descendant(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = _source_scope_test_repo(tmp_path)
    _commit_source_scope_change(root, "src/unapproved.py")
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", root)

    gate = firewall._source_scope_gate()

    assert gate.status == "BLOCKED"
    assert "out-of-scope" in gate.evidence[0]


def test_public_source_scope_refuses_dirty_tree(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = _source_scope_test_repo(tmp_path)
    (root / "README.md").write_text("uncommitted\n", encoding="utf-8")
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", root)

    gate = firewall._source_scope_gate()

    assert gate.status == "BLOCKED"
    assert "clean" in gate.evidence[0]


def test_public_source_scope_refuses_shallow_history(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = _source_scope_test_repo(tmp_path)
    boundary = _test_git(root, "rev-parse", "HEAD").decode().strip()
    _commit_source_scope_change(root, "docs/build.md")
    (root / ".git/shallow").write_text(boundary + "\n", encoding="ascii")
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", root)

    gate = firewall._source_scope_gate()

    assert gate.status == "BLOCKED"
    assert "shallow" in gate.evidence[0].casefold()


def test_public_source_scope_refuses_multiple_history_roots(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = _source_scope_test_repo(tmp_path)
    other = tmp_path / "unrelated-root"
    other.mkdir()
    _test_git(other, "init")
    _test_git(other, "config", "user.name", "Source scope test")
    _test_git(other, "config", "user.email", "source-scope@example.invalid")
    (other / "unrelated.txt").write_text("separate history\n", encoding="utf-8")
    _test_git(other, "add", "--all")
    _test_git(other, "commit", "-m", "unrelated root")
    _test_git(root, "fetch", str(other), "HEAD")
    _test_git(root, "merge", "--allow-unrelated-histories", "FETCH_HEAD",
              "-m", "merge unrelated history")
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", root)

    gate = firewall._source_scope_gate()

    assert gate.status == "BLOCKED"
    assert "single" in gate.evidence[0].casefold() and "root" in gate.evidence[0].casefold()


def test_private_anchor_takes_precedence_over_public_marker(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = _source_scope_test_repo(tmp_path)
    private_anchor = _test_git(root, "rev-parse", "HEAD").decode().strip()
    _commit_source_scope_change(root, "tools/verification/example.py")
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", root)
    monkeypatch.setattr(firewall, "_TRANCHE_START_COMMIT", private_anchor)

    gate = firewall._source_scope_gate()

    assert gate.status == "PASS"
    assert "mode=private-tranche-diff" in gate.evidence
    assert f"startCommit={private_anchor}" in gate.evidence


def test_public_marker_must_exist_at_actual_history_root(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = _source_scope_test_repo(tmp_path, marker=False)
    marker_path = root / "tools/manifests/public-source-scope.json"
    marker_path.parent.mkdir(parents=True)
    marker_path.write_text(
        json.dumps(_PUBLIC_SOURCE_SCOPE_MARKER, indent=2) + "\n",
        encoding="utf-8",
    )
    _test_git(root, "add", "--all")
    _test_git(root, "commit", "-m", "add public scope marker after root")
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", root)

    gate = firewall._source_scope_gate()

    assert gate.status == "BLOCKED"
    assert "root" in gate.evidence[0].casefold()
    assert "marker" in gate.evidence[0].casefold()



def test_source_scope_without_public_marker_keeps_private_anchor_fail_closed(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    root = _source_scope_test_repo(tmp_path, marker=False)
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", root)

    gate = firewall._source_scope_gate()

    assert gate.status == "BLOCKED"
    assert not any(item.startswith("mode=public-root") for item in gate.evidence)


def test_package_report_compares_two_captures_and_authenticates_staging(
    k_root: Path, monkeypatch: pytest.MonkeyPatch,
) -> None:
    source = k_root / "src/NpcManager.Cli/bin/Release/net10.0/actorwright.exe"
    source.parent.mkdir(parents=True)
    source.write_bytes(b"source-cli")
    stage = k_root / "artifacts/package-stage"
    package_cli = stage / "cli/actorwright.exe"
    package_cli.parent.mkdir(parents=True)
    package_cli.write_bytes(b"package-cli")
    (stage / "manifest.json").write_text(json.dumps({
        "schemaVersion": 1, "product": "Actorwright", "version": "1.0.0-preview.276",
        "sourceLine": "preview.276-private", "runtime": "win-x64", "privateOnly": True,
        "runtimeAuthority": False, "commandCount": 142,
        "embeddedResourceClosures": [],
        "files": [{"path": "cli/actorwright.exe", "size": 11,
                   "sha256": _sha256(b"package-cli")}],
    }), encoding="utf-8")
    monkeypatch.setattr(firewall, "REPOSITORY_ROOT", k_root)
    monkeypatch.setattr(firewall, "CAPTURE_WORKSPACE_PARENT", k_root / "artifacts/test-work")
    firewall.CAPTURE_WORKSPACE_PARENT.mkdir(parents=True)
    monkeypatch.setattr(release_verifier, "verify_package_staging", lambda root, *, metadata_only: {
        "status": "PASS", "artifactKind": "binary-package-staging",
        "version": "1.0.0-preview.276", "files": 2, "zipVerified": False,
        "protocolV2Kernel": True, "protocolV2WorkflowCommands": [],
        "protocolV2ConsumerRequiredCommands": [],
    } if root == stage and metadata_only is False else pytest.fail("wrong verifier input"))
    baseline = {"provenance": {"productVersion": "1.0.0-preview.275", "sourceLine": "preview"},
                "commandCount": 142, "orderedCommandNamesSha256": "order",
                "commands": [], "exitMeanings": [], "voiceDialogueSentinels": []}
    manifest = k_root / "fixture-baseline.json"
    manifest.write_text("{}", encoding="utf-8")
    monkeypatch.setattr(firewall, "load_baseline", lambda _: SimpleNamespace(
        baseline_id="publicSynthetic", manifest_path=manifest, snapshot=baseline))
    monkeypatch.setattr(firewall, "_load_json", lambda path: json.loads(Path(path).read_text(encoding="utf-8"))
                        if Path(path) == stage / "manifest.json" else {})
    monkeypatch.setattr(firewall.ValidatedPolicy, "parse", lambda _: SimpleNamespace(
        semantic_rules=(), accepted_differences=()))
    captures = []
    package_count = [142]
    identity_mutation = [None]
    def capture(target):
        captures.append((target.name, target.entrypoint, target.expected_sha256))
        result = {"version": {"protocol1": {"version": "1.0.0-preview.276", "sourceLine": "preview.276-private"},
                            "protocol2": {"productVersion": "1.0.0-preview.276", "sourceLine": "preview.276-private"}},
                "capabilities": {"protocol1": {"document": {
                    "version": "1.0.0-preview.276", "sourceLine": "preview.276-private"}}},
                "commandCount": package_count[0] if target.name == "package" else 142,
                "orderedCommandNamesSha256": "order",
                "commands": [], "behaviorFacts": [], "exitMeanings": [],
                "voiceDialogueSentinels": []}
        if target.name == "package" and identity_mutation[0] == "protocol2":
            result["version"]["protocol2"]["productVersion"] = "wrong-version"
        if target.name == "package" and identity_mutation[0] == "missing-protocol2":
            result["version"]["protocol2"].pop("productVersion")
            result["version"]["protocol2"]["version"] = "1.0.0-preview.276"
        if target.name == "package" and identity_mutation[0] == "dual-protocol2":
            result["version"]["protocol2"]["version"] = "wrong-version"
        if target.name == "package" and identity_mutation[0] == "capabilities":
            result["capabilities"]["protocol1"]["document"]["sourceLine"] = "wrong-line"
        return result
    monkeypatch.setattr(firewall, "capture_cli_contract", capture)
    report = firewall._package_report("publicSynthetic", str(source), str(stage))
    assert report["verdict"] == "PACKAGE_COMPATIBLE"
    assert report["differences"] == []
    assert captures == [("source", source, _sha256(b"source-cli")),
                        ("package", package_cli, _sha256(b"package-cli"))]
    for mutation in ("protocol2", "missing-protocol2", "dual-protocol2", "capabilities"):
        identity_mutation[0] = mutation
        inconsistent = firewall._package_report("publicSynthetic", str(source), str(stage))
        assert inconsistent["verdict"] == "INCOMPATIBLE"
        assert any(item["pointer"].startswith("/identity/")
                   for item in inconsistent["differences"])
    identity_mutation[0] = None
    manifest_value = json.loads((stage / "manifest.json").read_text(encoding="utf-8"))
    manifest_value["sourceLine"] = "wrong-line"
    (stage / "manifest.json").write_text(json.dumps(manifest_value), encoding="utf-8")
    inconsistent = firewall._package_report("publicSynthetic", str(source), str(stage))
    assert inconsistent["verdict"] == "INCOMPATIBLE"
    assert any(item["pointer"] == "/identity/sourceLine"
               for item in inconsistent["differences"])
    manifest_value["sourceLine"] = "preview.276-private"
    (stage / "manifest.json").write_text(json.dumps(manifest_value), encoding="utf-8")
    package_count[0] = 141
    drift = firewall._package_report("publicSynthetic", str(source), str(stage))
    assert drift["verdict"] == "INCOMPATIBLE"
    assert any(item["pointer"] == "/commandCount" for item in drift["differences"])
    package_count[0] = 142
    package_cli.write_bytes(b"tampered")
    assert firewall._package_report("publicSynthetic", str(source), str(stage))["verdict"] == "INCOMPATIBLE"


def test_public_synthetic_baseline_authenticates_both_structured_journeys() -> None:
    baseline = load_baseline("publicSynthetic")

    assert baseline.snapshot["provenance"]["kind"] == "public-synthetic"
    journey_paths = [
        path for member, path in zip(
            baseline.snapshot["members"], baseline.member_paths, strict=True
        ) if member["kind"] == "journey"
    ]
    assert [
        firewall.validate_journey_observation(firewall._load_json(path))["id"]
        for path in journey_paths
    ] == ["journey.create-to-package", "journey.wave-b-v1"]


def test_preview275_v1_baseline_is_not_substituted_by_public_synthetic() -> None:
    registry = ROOT / "tools" / "manifests" / "compatibility-baselines.json"
    with pytest.raises(FirewallError, match="unknown baseline: preview275"):
        load_baseline("preview275", registry_path=registry)


VOICE_DIALOGUE_SCHEMAS = {
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
VOICE_DIALOGUE_REQUIRED_REFUSALS = {
    "npc voice import": "Voice command requires --sample.",
    "npc voice synthesize": "Voice command requires --manifest.",
    "npc dialogue analyze": "Analyze mode requires --manifest.",
    "npc dialogue apply": "Dialogue command requires --proposal.",
    "npc dialogue verify": "Dialogue command requires --manifest.",
}


def _sealed_package_target(
    tmp_path: Path,
    workspace: Path,
) -> tuple[CliTarget, object]:
    package_root = tmp_path / "actorwright-test"
    executable = package_root / "cli" / "actorwright.exe"
    executable.parent.mkdir(parents=True)
    executable.write_bytes(b"sealed-cli")
    executable_sha = _sha256(executable.read_bytes())
    source_commit = "a" * 40
    source_tree = "b" * 40
    source_tag = "v1.0.0-test"
    release = {
        "schemaVersion": 2,
        "product": "Actorwright",
        "version": "1.0.0-test",
        "sourceCommit": source_commit,
        "sourceTree": source_tree,
        "sourceTag": source_tag,
    }
    (package_root / "actorwright-release.json").write_bytes(
        canonical_json_bytes(release)
    )
    (package_root / "SHA256SUMS").write_text(
        f"{executable_sha}  cli/actorwright.exe\n", encoding="utf-8"
    )
    archive = tmp_path / "actorwright-test.zip"
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_STORED) as output:
        for path in sorted(package_root.rglob("*")):
            if path.is_file():
                output.write(path, f"{package_root.name}/{path.relative_to(package_root).as_posix()}")
    evidence = firewall.SealedPackageEvidence(
        package_root=package_root.resolve(),
        release_zip=archive.resolve(),
        expected_zip_length=archive.stat().st_size,
        expected_zip_sha256=_sha256(archive.read_bytes()),
        source_tag=source_tag,
        source_commit=source_commit,
        source_tree=source_tree,
    )
    return (
        CliTarget("package", executable.resolve(), workspace, executable_sha),
        evidence,
    )


def _package_observation(arguments, exit_code: int, value: object):
    data = canonical_json_bytes(value)
    return firewall.CliObservation(tuple(arguments), exit_code, data, b"", value)


def _package_probe_double(
    calls: list[tuple[str, ...]],
    *,
    schema_mutation: tuple[str, str] | None = None,
    envelope_mutation: tuple[str, str] | None = None,
    option_mutation: tuple[str, str] | None = None,
    refusal_mutation: str | None = None,
    discover_mutation: str | None = None,
    discover_timestamp: str | None = None,
):
    def package_cli(target: CliTarget, arguments, **kwargs):
        args = tuple(arguments)
        calls.append(args)
        if args == ("capabilities", "--json"):
            return _package_observation(
                args, 0, {"commands": [{"name": name} for name in EXPECTED_142]}
            )
        if args == ("capabilities", "--protocol", "2", "--json"):
            return _package_observation(
                args, 0, _envelope("capabilities", {"commands": [_contract(name) for name in EXPECTED_142]})
            )
        if "--help" in args:
            name = " ".join(args[:-4])
            return _package_observation(args, 0, _envelope(name, {"contract": _contract(name)}))
        if args[:5] == ("schema", "export", "--protocol", "2", "--json"):
            name = args[-1]
            schemas = _synthetic_schema_rows(name)
            if schema_mutation is not None and schema_mutation[0] == name:
                mutation = schema_mutation[1]
                document = schemas[0]["jsonSchema"]
                if mutation == "required":
                    document["required"] = ["schema"]
                elif mutation == "options":
                    document["properties"]["options"]["items"]["enum"] = ["changed"]
                elif mutation == "type":
                    document["properties"]["payload"]["type"] = "array"
                elif mutation == "additionalProperties":
                    document["additionalProperties"] = True
                elif mutation == "description":
                    document["description"] = "mutated description"
                else:
                    raise AssertionError(f"unknown schema mutation: {mutation}")
            return _package_observation(
                args,
                0,
                _envelope(
                    "schema export",
                    {"contract": _contract(name), "documentSchemas": schemas},
                ),
            )
        name = " ".join(args[:3])
        if args[3:] == ("--protocol", "2", "--json"):
            envelope = _legacy_protocol_envelope(name)
            if envelope_mutation is not None and envelope_mutation[0] == name:
                mutation = envelope_mutation[1]
                if mutation == "wrong-command":
                    envelope["command"] = "npc voice replayed"
                elif mutation == "missing-command":
                    del envelope["command"]
                elif mutation == "exit":
                    envelope["exitCode"] = 0
                elif mutation == "payload":
                    envelope["result"] = {"accepted": True}
                elif mutation == "missing-error":
                    envelope["diagnostics"] = []
                elif mutation == "wrong-error":
                    envelope["diagnostics"][0]["code"] = "usage"
                else:
                    raise AssertionError(f"unknown envelope mutation: {mutation}")
            return _package_observation(args, 2, envelope)
        if "--compatibility-unknown" in args:
            family = "Voice" if name.startswith("npc voice") else "Dialogue"
            option_name = "--compatibility-unknown"
            code = "usage"
            if option_mutation is not None and option_mutation[0] == name:
                if option_mutation[1] == "wrong-option":
                    option_name = "--different-option"
                elif option_mutation[1] == "missing-error":
                    code = ""
                else:
                    raise AssertionError(
                        f"unknown option mutation: {option_mutation[1]}"
                    )
            return _package_observation(
                args,
                2,
                {
                    "code": code,
                    "message": f"{family} commands do not support {option_name}.",
                },
            )
        if name == "npc voice discover":
            document = _voice_discovery_refusal()
            if discover_timestamp is not None:
                document["probedUtc"] = discover_timestamp
            if refusal_mutation == name:
                document["diagnostics"][0]["code"] = "mutated-dependency"
            if discover_mutation is not None:
                _mutate_voice_discovery_refusal(document, discover_mutation)
            return _package_observation(
                args,
                4,
                document,
            )
        message = VOICE_DIALOGUE_REQUIRED_REFUSALS[name]
        if refusal_mutation == name:
            message = "Generic usage refusal."
        return _package_observation(
            args, 2, {"code": "usage", "message": message}
        )

    return package_cli


def _voice_discovery_refusal() -> dict[str, object]:
    return {
        "schema": "npc.voice-services.v1",
        "probedUtc": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "requestTextLoggedByService": True,
        "selectedEndpoint": None,
        "services": [
            {
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
        ],
        "diagnostics": [
            {
                "code": "voice-service-none",
                "message": "No compatible storage-safe XTTS endpoint was found.",
                "severity": "error",
            }
        ],
    }


def _mutate_voice_discovery_refusal(
    document: dict[str, object], mutation: str
) -> None:
    service = document["services"][0]
    nested = service["diagnostics"][0]
    top_diagnostic = document["diagnostics"][0]
    mutations = {
        "top-extra": lambda: document.__setitem__("extra", True),
        "top-missing": lambda: document.pop("schema"),
        "schema": lambda: document.__setitem__("schema", "npc.voice-sample.v1"),
        "probedUtc": lambda: document.__setitem__("probedUtc", "replayed"),
        "probedUtc-replay": lambda: document.__setitem__("probedUtc", "2020-01-01T00:00:00Z"),
        "requestTextLoggedByService": lambda: document.__setitem__("requestTextLoggedByService", False),
        "selectedEndpoint": lambda: document.__setitem__("selectedEndpoint", "http://127.0.0.1:1"),
        "services-extra": lambda: document["services"].append(deepcopy(service)),
        "top-diagnostic-code": lambda: top_diagnostic.__setitem__("code", "voice-service-unreachable"),
        "top-diagnostic-message": lambda: top_diagnostic.__setitem__("message", "Generic refusal."),
        "top-diagnostic-severity": lambda: top_diagnostic.__setitem__("severity", "warning"),
        "top-diagnostic-extra": lambda: top_diagnostic.__setitem__("extra", True),
        "top-diagnostic-missing": lambda: top_diagnostic.pop("message"),
        "service-extra": lambda: service.__setitem__("extra", True),
        "service-missing": lambda: service.pop("apiTitle"),
        "endpoint-replay": lambda: service.__setitem__("endpoint", "http://127.0.0.1:2"),
        "source": lambda: service.__setitem__("source", "discovered"),
        "apiTitle": lambda: service.__setitem__("apiTitle", "XTTS"),
        "apiVersion": lambda: service.__setitem__("apiVersion", "1"),
        "models": lambda: service.__setitem__("models", ["model"]),
        "languages": lambda: service.__setitem__("languages", ["en"]),
        "speakerCount": lambda: service.__setitem__("speakerCount", 1),
        "speakerCount-bool": lambda: service.__setitem__("speakerCount", False),
        "speakerCount-float": lambda: service.__setitem__("speakerCount", 0.0),
        "models-tuple": lambda: service.__setitem__("models", ()),
        "diagnostics-tuple": lambda: service.__setitem__("diagnostics", tuple(service["diagnostics"])),
        "speakerFolder": lambda: service.__setitem__("speakerFolder", "C:\\Windows"),
        "outputFolder": lambda: service.__setitem__("outputFolder", "F:\\protected"),
        "modelFolder": lambda: service.__setitem__("modelFolder", "C:\\models"),
        "settingsJson": lambda: service.__setitem__("settingsJson", "{}"),
        "storageVerdict": lambda: service.__setitem__("storageVerdict", "compatible"),
        "storageDetail": lambda: service.__setitem__("storageDetail", "arbitrary"),
        "compatible": lambda: service.__setitem__("compatible", True),
        "nested-code": lambda: nested.__setitem__("code", "voice-service-none"),
        "nested-message": lambda: nested.__setitem__("message", "arbitrary"),
        "nested-severity": lambda: nested.__setitem__("severity", "warning"),
        "nested-extra": lambda: nested.__setitem__("extra", True),
        "nested-missing": lambda: nested.pop("message"),
        "fingerprint": lambda: service.__setitem__("fingerprint", "replayed"),
        "platform": lambda: service.__setitem__("platform", "windows"),
    }
    mutations[mutation]()


def _synthetic_schema_rows(command: str) -> list[dict[str, object]]:
    rows = []
    for schema_name, direction, identifier in VOICE_DIALOGUE_SCHEMAS[command]:
        rows.append(
            {
                "name": schema_name,
                "direction": direction,
                "schemaIdentifier": identifier,
                "jsonSchema": {
                    "$id": f"urn:actorwright:schema:{identifier}",
                    "$schema": "https://json-schema.org/draft/2020-12/schema",
                    "title": schema_name,
                    "description": f"Complete synthetic contract for {identifier}.",
                    "type": "object",
                    "additionalProperties": False,
                    "required": ["schema", "payload", "options"],
                    "properties": {
                        "schema": {"const": identifier, "type": "string"},
                        "payload": {
                            "description": "Typed payload.",
                            "type": "object",
                            "additionalProperties": False,
                            "required": ["value"],
                            "properties": {
                                "value": {
                                    "description": "Payload identity.",
                                    "type": "string",
                                }
                            },
                        },
                        "options": {
                            "description": "Closed route options.",
                            "type": "array",
                            "items": {
                                "type": "string",
                                "enum": ["deterministic"],
                            },
                        },
                    },
                },
            }
        )
    return rows


def _trust_synthetic_schema_hashes(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(
        firewall,
        "_VOICE_DIALOGUE_SCHEMA_HASHES",
        {
            command: _sha256(canonical_json_bytes(_synthetic_schema_rows(command)))
            for command in SENTINELS
        },
        raising=False,
    )
    monkeypatch.setattr(
        firewall,
        "_VOICE_DIALOGUE_LEGACY_REQUEST_DIGESTS",
        {command: "A" * 64 for command in SENTINELS},
        raising=False,
    )


def _legacy_protocol_envelope(command: str) -> dict[str, object]:
    message = f"Command '{command}' is not ready for protocol 2."
    return {
        "protocolVersion": "2",
        "schemaVersion": "1",
        "command": command,
        "outcome": "failed",
        "exitCode": 2,
        "requestDigest": "A" * 64,
        "effects": [
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
        ],
        "diagnostics": [
            {
                "code": "protocol-command-legacy",
                "severity": "error",
                "class": "usage",
                "message": message,
                "recovery": {
                    "action": "correctInput",
                    "retryUnchangedSafe": False,
                    "constraint": message,
                },
            }
        ],
        "artifacts": [],
        "authority": [],
        "nextActions": [],
    }


def test_package_voice_dialogue_probes_cover_every_route_without_skip(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    calls: list[tuple[str, ...]] = []
    target, package_evidence = _sealed_package_target(tmp_path, k_root)
    _trust_synthetic_schema_hashes(monkeypatch)
    monkeypatch.setattr(firewall, "run_cli", _package_probe_double(calls))
    results = firewall.run_voice_dialogue_package_probes(
        target, package_evidence=package_evidence
    )

    assert [result.gate_id for result in results] == [
        f"package:{command}" for command in SENTINELS
    ]
    assert all(result.status == "PASS" for result in results)
    assert all(result.required for result in results)
    assert all("discovery=PASS" in result.evidence for result in results)
    assert all("help=PASS" in result.evidence for result in results)
    assert all("schema=PASS" in result.evidence for result in results)
    assert all(
        any(item.startswith("schemaSha256=") for item in result.evidence)
        for result in results
    )
    assert all("routeProtocol=1-legacy" in result.evidence for result in results)
    assert all("protocol2-legacy-envelope=PASS" in result.evidence for result in results)
    assert all("option-refusal=PASS" in result.evidence for result in results)
    assert all(
        any(item.startswith("packagePath=") for item in result.evidence)
        for result in results
    )
    assert all(
        any(item.startswith("releaseZipSha256=") for item in result.evidence)
        for result in results
    )
    discover = next(
        result for result in results if result.gate_id.endswith("voice discover")
    )
    assert "dependency-refusal=voice-service-none" in discover.evidence
    assert all(
        any(item.startswith("dependency-refusal=") for item in result.evidence)
        for result in results
    )
    for command in SENTINELS:
        tokens = tuple(command.split())
        assert (*tokens, "--help", "--protocol", "2", "--json") in calls
        assert (
            "schema", "export", "--protocol", "2", "--json", "--command", command
        ) in calls
        assert (*tokens, "--protocol", "2", "--json") in calls
        assert (*tokens, "--compatibility-unknown", "x", "--json") in calls
        if command != "npc voice discover":
            assert (*tokens, "--json") in calls


def test_package_probes_reject_unhashed_or_wrong_hash_target(
    tmp_path: Path,
    k_root: Path,
) -> None:
    target, evidence = _sealed_package_target(tmp_path, k_root)

    with pytest.raises(FirewallError, match="expected executable sha256"):
        firewall.run_voice_dialogue_package_probes(
            CliTarget(target.name, target.entrypoint, target.workspace_root, None),
            package_evidence=evidence,
        )
    with pytest.raises(FirewallError, match="entrypoint sha256 mismatch"):
        firewall.run_voice_dialogue_package_probes(
            CliTarget(target.name, target.entrypoint, target.workspace_root, "0" * 64),
            package_evidence=evidence,
        )


@pytest.mark.parametrize("mutation", ["zip-sha", "source-commit", "package-root"])
def test_package_probes_reject_wrong_authenticated_package_evidence(
    tmp_path: Path,
    k_root: Path,
    mutation: str,
) -> None:
    target, evidence = _sealed_package_target(tmp_path, k_root)
    values = dict(evidence.__dict__)
    if mutation == "zip-sha":
        values["expected_zip_sha256"] = "0" * 64
    elif mutation == "source-commit":
        values["source_commit"] = "c" * 40
    else:
        values["package_root"] = tmp_path.resolve()

    with pytest.raises(FirewallError, match="package evidence"):
        firewall.run_voice_dialogue_package_probes(
            target,
            package_evidence=firewall.SealedPackageEvidence(**values),
        )


@pytest.mark.parametrize("command", SENTINELS)
@pytest.mark.parametrize(
    "leaf", ["required", "options", "type", "additionalProperties", "description"]
)
def test_package_probe_rejects_any_deep_schema_mutation(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
    command: str,
    leaf: str,
) -> None:
    target, evidence = _sealed_package_target(tmp_path, k_root)
    _trust_synthetic_schema_hashes(monkeypatch)
    monkeypatch.setattr(
        firewall,
        "run_cli",
        _package_probe_double([], schema_mutation=(command, leaf)),
    )

    results = firewall.run_voice_dialogue_package_probes(
        target, package_evidence=evidence
    )

    result = next(item for item in results if item.gate_id == f"package:{command}")
    assert result.status == "FAIL"
    assert "schema" in result.evidence[-1]


@pytest.mark.parametrize("command", SENTINELS)
@pytest.mark.parametrize(
    "mutation",
    ["wrong-command", "missing-command", "exit", "payload", "missing-error", "wrong-error"],
)
def test_package_probe_rejects_wrong_legacy_protocol_envelope(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
    command: str,
    mutation: str,
) -> None:
    target, evidence = _sealed_package_target(tmp_path, k_root)
    _trust_synthetic_schema_hashes(monkeypatch)
    monkeypatch.setattr(
        firewall,
        "run_cli",
        _package_probe_double([], envelope_mutation=(command, mutation)),
    )

    results = firewall.run_voice_dialogue_package_probes(
        target, package_evidence=evidence
    )

    result = next(item for item in results if item.gate_id == f"package:{command}")
    assert result.status == "FAIL"
    assert "protocol" in result.evidence[-1]


@pytest.mark.parametrize("command", SENTINELS)
@pytest.mark.parametrize("mutation", ["wrong-option", "missing-error"])
def test_package_probe_rejects_wrong_or_missing_option_identity(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
    command: str,
    mutation: str,
) -> None:
    target, evidence = _sealed_package_target(tmp_path, k_root)
    _trust_synthetic_schema_hashes(monkeypatch)
    monkeypatch.setattr(
        firewall,
        "run_cli",
        _package_probe_double([], option_mutation=(command, mutation)),
    )

    results = firewall.run_voice_dialogue_package_probes(
        target, package_evidence=evidence
    )

    result = next(item for item in results if item.gate_id == f"package:{command}")
    assert result.status == "FAIL"
    assert "option" in result.evidence[-1]


@pytest.mark.parametrize("command", SENTINELS)
def test_package_probe_rejects_route_bound_refusal_mutation(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
    command: str,
) -> None:
    target, evidence = _sealed_package_target(tmp_path, k_root)
    _trust_synthetic_schema_hashes(monkeypatch)
    monkeypatch.setattr(
        firewall,
        "run_cli",
        _package_probe_double([], refusal_mutation=command),
    )

    results = firewall.run_voice_dialogue_package_probes(
        target, package_evidence=evidence
    )

    result = next(item for item in results if item.gate_id == f"package:{command}")
    assert result.status == "FAIL"
    assert "refusal" in result.evidence[-1]


@pytest.mark.parametrize(
    "mutation",
    [
        "top-extra",
        "top-missing",
        "schema",
        "probedUtc",
        "probedUtc-replay",
        "requestTextLoggedByService",
        "selectedEndpoint",
        "services-extra",
        "top-diagnostic-code",
        "top-diagnostic-message",
        "top-diagnostic-severity",
        "top-diagnostic-extra",
        "top-diagnostic-missing",
        "service-extra",
        "service-missing",
        "endpoint-replay",
        "source",
        "apiTitle",
        "apiVersion",
        "models",
        "languages",
        "speakerCount",
        "speakerCount-bool",
        "speakerCount-float",
        "models-tuple",
        "diagnostics-tuple",
        "speakerFolder",
        "outputFolder",
        "modelFolder",
        "settingsJson",
        "storageVerdict",
        "storageDetail",
        "compatible",
        "nested-code",
        "nested-message",
        "nested-severity",
        "nested-extra",
        "nested-missing",
        "fingerprint",
        "platform",
    ],
)
def test_voice_discover_dependency_refusal_rejects_any_deep_mutation(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
    mutation: str,
) -> None:
    target, evidence = _sealed_package_target(tmp_path, k_root)
    _trust_synthetic_schema_hashes(monkeypatch)
    monkeypatch.setattr(
        firewall,
        "run_cli",
        _package_probe_double([], discover_mutation=mutation),
    )

    results = firewall.run_voice_dialogue_package_probes(
        target, package_evidence=evidence
    )

    result = next(
        item for item in results if item.gate_id == "package:npc voice discover"
    )
    assert result.status == "FAIL"
    assert "voice dependency" in result.evidence[-1]


@pytest.mark.parametrize(
    ("offset_seconds", "expected_status"),
    [(-240, "FAIL"), (-2, "FAIL"), (-1, "FAIL"), (0, "PASS"), (1, "FAIL")],
)
def test_voice_discover_timestamp_is_bound_to_this_invocation(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
    offset_seconds: int,
    expected_status: str,
) -> None:
    target, evidence = _sealed_package_target(tmp_path, k_root)
    _trust_synthetic_schema_hashes(monkeypatch)
    start = datetime(2026, 9, 22, 12, 0, 0, 200000, tzinfo=timezone.utc)
    end = start + timedelta(milliseconds=100)
    moments = iter((start, end))
    monkeypatch.setattr(firewall, "_utc_now", lambda: next(moments), raising=False)
    timestamp = (start + timedelta(seconds=offset_seconds)).strftime("%Y-%m-%dT%H:%M:%SZ")
    monkeypatch.setattr(
        firewall,
        "run_cli",
        _package_probe_double([], discover_timestamp=timestamp),
    )

    results = firewall.run_voice_dialogue_package_probes(
        target, package_evidence=evidence
    )
    result = next(item for item in results if item.gate_id == "package:npc voice discover")
    assert result.status == expected_status
    if expected_status == "PASS":
        assert f"dependencyRefusalProbedUtc={timestamp}" in result.evidence
        assert any(item.startswith("dependencyRefusalInvocationStartUtc=") for item in result.evidence)
        assert any(item.startswith("dependencyRefusalInvocationEndUtc=") for item in result.evidence)


def test_package_probe_failure_is_explicit_not_a_skip(
    tmp_path: Path,
    k_root: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    target, package_evidence = _sealed_package_target(tmp_path, k_root)

    def blocked(target, arguments, **kwargs):
        raise FirewallError("package probe timed out")

    monkeypatch.setattr(firewall, "run_cli", blocked)
    results = firewall.run_voice_dialogue_package_probes(
        target, package_evidence=package_evidence
    )

    assert len(results) == 6
    assert all(result.status == "BLOCKED" for result in results)
    gates = [*_required_gate_set("Source"), *results]
    assert firewall.evaluate_tier("Full", gates).verdict == "INCOMPATIBLE"
