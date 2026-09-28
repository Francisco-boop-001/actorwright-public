#!/usr/bin/env python3
"""Run the complete NPC Manager executable test campaign with durable evidence."""

from __future__ import annotations

import argparse
import dataclasses
import hashlib
import json
import os
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path, PureWindowsPath
from typing import Iterable, Mapping, Sequence


PRODUCTION_ASSEMBLIES = (
    "NpcManager.Application",
    "NpcManager.Assets",
    "NpcManager.BodyGen",
    "NpcManager.Cli",
    "NpcManager.Desktop",
    "NpcManager.Domain",
    "NpcManager.FaceGen",
    "NpcManager.Formats.Bethesda",
    "NpcManager.Infrastructure",
    "NpcManager.Pipeline",
    "NpcManager.Presets",
    "NpcManager.Rendering",
    "NpcManager.Verification",
)

TEST_PROJECTS = (
    "NpcManager.Architecture.Tests",
    "NpcManager.Assets.Tests",
    "NpcManager.BethesdaFaceRouting.Tests",
    "NpcManager.Cli.Tests",
    "NpcManager.Desktop.Smoke",
    "NpcManager.FaceGen.Tests",
    "NpcManager.FaceGeomOrchestration.Tests",
    "NpcManager.Gate1.Tests",
    "NpcManager.Gate2.PipelineIntegration.Tests",
    "NpcManager.Gate2.Tests",
    "NpcManager.NpcCreationAppearance.Tests",
    "NpcManager.ReferencePreset.Tests",
)

ARCHITECTURE_VALIDATORS = (
    "tools/architecture/validate_architecture.py",
    "tools/architecture/validate_desktop_shell.py",
)

TEST_AUTHORITY_ENVIRONMENT = "NPCMANAGER_TEST_AUTHORITY_WORKSPACE"
REQUIRED_TEST_AUTHORITY_PATHS = (
    Path(
        "projects/NpcManagerReimplementation/01-source-copies/"
        "sophia-live-closure-20260723/Data/Skyrim.esm"
    ),
    Path(
        "projects/NpcManagerReimplementation/01-source-copies/"
        "sophia-live-closure-20260723/Data/textures/!COR/"
        "TintMasks/femalehead_cheeks.dds"
    ),
    Path(
        "projects/NpcManagerReimplementation/01-source-copies/"
        "m5-fixtures/gate1-blank-provider-bundle.json"
    ),
    Path(
        "projects/NpcManagerReimplementation/01-source-copies/"
        "gate2-emi2/execution-request-v5b.json"
    ),
    Path(
        "projects/Emi2FreshBuild/03-builds/feasibility-probes/"
        "ck-carrier-root/Data/EmiCarrierProbe.esp"
    ),
)

TERMINAL_RESULT = re.compile(
    r"^RESULT\s+(PASS|FAIL)\s+([0-9]+)/([0-9]+)\s*$",
    re.MULTILINE,
)
EVIDENCE_ID = re.compile(r"^[a-z0-9][a-z0-9.-]{0,63}$")


class EvidenceError(RuntimeError):
    """The evidence campaign cannot make a trustworthy claim."""


@dataclasses.dataclass(frozen=True)
class TerminalResult:
    passed: int
    total: int


@dataclasses.dataclass(frozen=True)
class ProcessOutcome:
    command: tuple[str, ...]
    cwd: str
    exit_code: int
    timed_out: bool
    elapsed_seconds: float
    stdout: str
    stderr: str
    environment_overrides: tuple[tuple[str, str], ...]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def sha256_text(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest().upper()


def require_file(path: Path, label: str) -> Path:
    if not path.is_file():
        raise EvidenceError(f"{label} tool is missing: {path}")
    return path


def require_new_outputs(paths: Iterable[Path]) -> None:
    existing = [str(path) for path in paths if path.exists()]
    if existing:
        raise EvidenceError(
            "evidence output already exists; refusing to overwrite: "
            + ", ".join(existing)
        )


def parse_terminal_result(stdout: str) -> TerminalResult:
    matches = TERMINAL_RESULT.findall(stdout)
    if len(matches) != 1:
        raise EvidenceError(
            "suite must emit exactly one terminal RESULT PASS n/n line; "
            f"found {len(matches)}"
        )
    state, passed_text, total_text = matches[0]
    passed = int(passed_text)
    total = int(total_text)
    if state != "PASS" or total <= 0 or passed != total:
        raise EvidenceError(
            f"suite terminal result is not a complete pass: "
            f"RESULT {state} {passed}/{total}"
        )
    return TerminalResult(passed=passed, total=total)


def _decode_timeout_text(value: str | bytes | None) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode("utf-8", errors="replace")
    return value


def run_process(
    command: Sequence[str],
    cwd: Path,
    *,
    timeout_seconds: float,
    environment: Mapping[str, str] | None = None,
) -> ProcessOutcome:
    started = time.monotonic()
    creation_flags = (
        subprocess.CREATE_NEW_PROCESS_GROUP
        if os.name == "nt"
        else 0
    )
    child_environment = os.environ.copy()
    if environment:
        child_environment.update(environment)
    process = subprocess.Popen(
        [str(part) for part in command],
        cwd=str(cwd),
        env=child_environment,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=creation_flags,
    )
    timed_out = False
    try:
        stdout, stderr = process.communicate(timeout=timeout_seconds)
        exit_code = int(process.returncode)
    except subprocess.TimeoutExpired as exception:
        timed_out = True
        partial_stdout = _decode_timeout_text(exception.stdout)
        partial_stderr = _decode_timeout_text(exception.stderr)
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                check=False,
            )
        else:
            process.kill()
        tail_stdout, tail_stderr = process.communicate()
        stdout = partial_stdout
        stderr = partial_stderr
        if tail_stdout and not stdout.endswith(tail_stdout):
            stdout += tail_stdout
        if tail_stderr and not stderr.endswith(tail_stderr):
            stderr += tail_stderr
        exit_code = -9
    return ProcessOutcome(
        command=tuple(str(part) for part in command),
        cwd=str(cwd),
        exit_code=exit_code,
        timed_out=timed_out,
        elapsed_seconds=round(time.monotonic() - started, 6),
        stdout=stdout,
        stderr=stderr,
        environment_overrides=tuple(
            sorted((environment or {}).items())
        ),
    )


def _local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def _cobertura_packages(root: ET.Element) -> dict[str, dict[str, float | None]]:
    packages: dict[str, dict[str, float | None]] = {}
    for element in root.iter():
        if _local_name(element.tag) != "package":
            continue
        name = element.attrib.get("name", "").strip()
        if not name:
            continue
        line_rate = element.attrib.get("line-rate")
        branch_rate = element.attrib.get("branch-rate")
        packages[name] = {
            "lineRate": float(line_rate) if line_rate is not None else None,
            "branchRate": (
                float(branch_rate) if branch_rate is not None else None
            ),
        }
    return packages


def read_cobertura_inventory(
    report: Path,
    expected_assemblies: Sequence[str],
) -> list[dict[str, object]]:
    try:
        root = ET.parse(report).getroot()
        if _local_name(root.tag) != "coverage":
            raise ValueError(f"unexpected root element: {root.tag}")
        packages = _cobertura_packages(root)
    except (ET.ParseError, OSError, ValueError) as exception:
        raise EvidenceError(
            f"invalid Cobertura report {report}: {exception}"
        ) from exception
    return [
        {
            "assembly": assembly,
            "loaded": assembly in packages,
            "lineRate": (
                packages[assembly]["lineRate"]
                if assembly in packages
                else None
            ),
            "branchRate": (
                packages[assembly]["branchRate"]
                if assembly in packages
                else None
            ),
        }
        for assembly in expected_assemblies
    ]


def _find_workspace_root(project_root: Path) -> Path:
    current = project_root.resolve()
    while current.parent != current:
        if (
            (current / "AGENTS.md").is_file()
            and (current / "WORKSPACE_MANIFEST.json").is_file()
        ):
            return current
        current = current.parent
    raise EvidenceError("could not locate the ExampleWorkspace root")


def _find_tool_workspace_root(workspace_root: Path) -> Path:
    resolved_workspace = workspace_root.resolve()
    relative_launcher = (
        Path("tools")
        / "external"
        / "dotnet-sdk-10.0.301-win-x64"
        / "dotnet.exe"
    )
    if (resolved_workspace / relative_launcher).is_file():
        return resolved_workspace
    if (
        (resolved_workspace / Path("tools") / "external").is_dir()
        and not (resolved_workspace / ".git").exists()
    ):
        return resolved_workspace

    common_text = _git_value(
        resolved_workspace,
        "rev-parse",
        "--git-common-dir",
    )
    common_directory = Path(common_text)
    if not common_directory.is_absolute():
        common_directory = resolved_workspace / common_directory
    canonical_workspace = common_directory.resolve().parent
    if (
        not (canonical_workspace / "AGENTS.md").is_file()
        or not (canonical_workspace / "WORKSPACE_MANIFEST.json").is_file()
        or not (canonical_workspace / relative_launcher).is_file()
    ):
        raise EvidenceError(
            "the Git common workspace does not expose the pinned "
            "K-local .NET tool authority"
        )
    return canonical_workspace


def _find_test_authority_workspace_root(
    workspace_root: Path,
    canonical_workspace_root: Path,
) -> Path:
    candidates = (
        workspace_root.resolve(),
        canonical_workspace_root.resolve(),
    )
    missing_by_candidate: list[str] = []
    for candidate in dict.fromkeys(candidates):
        missing = [
            str(relative)
            for relative in REQUIRED_TEST_AUTHORITY_PATHS
            if not (candidate / relative).is_file()
        ]
        if not missing:
            return candidate
        missing_by_candidate.append(
            f"{candidate}: {', '.join(missing)}"
        )
    raise EvidenceError(
        "authentic test authority is incomplete; "
        + " | ".join(missing_by_candidate)
    )


def _resolve_tool_root(project_root: Path, explicit: Path | None = None) -> Path:
    workspace_root = _find_workspace_root(project_root)
    candidate = (
        explicit
        if explicit is not None
        else _find_tool_workspace_root(workspace_root) / "tools" / "external"
    ).resolve()
    if PureWindowsPath(str(candidate)).drive.casefold() == "f:":
        raise EvidenceError("tool root on F: is forbidden; F:\\ExampleGame is look-only")
    return candidate


def _build_command(dotnet: Path, project_root: Path, configuration: str, no_restore: bool) -> list[str]:
    command = [
        str(dotnet),
        "build",
        str(project_root / "NpcManager.sln"),
        "--configuration",
        configuration,
        "--nologo",
    ]
    if no_restore:
        command.append("--no-restore")
    return command


def _source_record(project_root: Path, tool_root: Path, commit: str, dirty_state: str) -> dict[str, object]:
    return {
        "root": str(project_root.resolve()),
        "commit": commit,
        "dirtyStateSha256": sha256_text(dirty_state),
        "dirtyStateEntryCount": 0 if not dirty_state else len(dirty_state.splitlines()),
        "toolRoot": str(tool_root.resolve()),
    }


def _write_new_text(path: Path, value: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("x", encoding="utf-8", newline="\n") as stream:
        stream.write(value)


def _write_process_logs(
    raw_root: Path,
    stem: str,
    outcome: ProcessOutcome,
) -> tuple[Path, Path]:
    stdout_path = raw_root / f"{stem}.stdout.log"
    stderr_path = raw_root / f"{stem}.stderr.log"
    _write_new_text(stdout_path, outcome.stdout)
    _write_new_text(stderr_path, outcome.stderr)
    return stdout_path, stderr_path


def _process_record(
    raw_root: Path,
    stem: str,
    outcome: ProcessOutcome,
) -> dict[str, object]:
    stdout_path, stderr_path = _write_process_logs(raw_root, stem, outcome)
    return {
        "command": list(outcome.command),
        "cwd": outcome.cwd,
        "environmentOverrides": dict(outcome.environment_overrides),
        "exitCode": outcome.exit_code,
        "timedOut": outcome.timed_out,
        "elapsedSeconds": outcome.elapsed_seconds,
        "stdout": {
            "path": str(stdout_path),
            "length": stdout_path.stat().st_size,
            "sha256": sha256_file(stdout_path),
        },
        "stderr": {
            "path": str(stderr_path),
            "length": stderr_path.stat().st_size,
            "sha256": sha256_file(stderr_path),
        },
    }


def _coverage_settings() -> str:
    includes = "\n".join(
        f"          <ModulePath>.*[\\\\/]{re.escape(assembly)}\\.dll$</ModulePath>"
        for assembly in PRODUCTION_ASSEMBLIES
    )
    return (
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        "<Configuration>\n"
        "  <CodeCoverage>\n"
        "    <ModulePaths>\n"
        "      <Include>\n"
        f"{includes}\n"
        "      </Include>\n"
        "      <Exclude>\n"
        "        <ModulePath>.*\\.Tests\\.dll$</ModulePath>\n"
        "        <ModulePath>.*NpcManager\\.Desktop\\.Smoke\\.dll$</ModulePath>\n"
        "        <ModulePath>.*testhost.*</ModulePath>\n"
        "      </Exclude>\n"
        "    </ModulePaths>\n"
        "  </CodeCoverage>\n"
        "</Configuration>\n"
    )


def _git_value(workspace_root: Path, *arguments: str) -> str:
    outcome = run_process(
        ["git", *arguments],
        workspace_root,
        timeout_seconds=60,
    )
    if outcome.exit_code != 0:
        raise EvidenceError(
            f"git {' '.join(arguments)} failed: {outcome.stderr.strip()}"
        )
    return outcome.stdout.strip()


def _tool_record(path: Path) -> dict[str, object]:
    return {
        "path": str(path),
        "length": path.stat().st_size,
        "sha256": sha256_file(path),
    }


def _build_junit(
    evidence_id: str,
    suites: Sequence[dict[str, object]],
) -> str:
    failures = sum(1 for suite in suites if suite["outcome"] != "PASS")
    elapsed = sum(float(suite["process"]["elapsedSeconds"]) for suite in suites)
    root = ET.Element(
        "testsuites",
        {
            "name": f"NpcManager-{evidence_id}",
            "tests": str(len(suites)),
            "failures": str(failures),
            "errors": "0",
            "time": f"{elapsed:.6f}",
        },
    )
    suite_element = ET.SubElement(
        root,
        "testsuite",
        {
            "name": "NpcManager executable suites",
            "tests": str(len(suites)),
            "failures": str(failures),
            "errors": "0",
            "time": f"{elapsed:.6f}",
        },
    )
    for suite in suites:
        process = suite["process"]
        testcase = ET.SubElement(
            suite_element,
            "testcase",
            {
                "classname": "NpcManager.ExecutableSuites",
                "name": str(suite["name"]),
                "time": f"{float(process['elapsedSeconds']):.6f}",
            },
        )
        if suite["outcome"] != "PASS":
            failure = ET.SubElement(
                testcase,
                "failure",
                {
                    "message": str(suite.get("diagnostic", "suite failed")),
                    "type": "NpcManagerEvidenceFailure",
                },
            )
            failure.text = str(suite.get("diagnostic", "suite failed"))
        ET.SubElement(testcase, "system-out").text = str(
            process["stdout"]["sha256"]
        )
        ET.SubElement(testcase, "system-err").text = str(
            process["stderr"]["sha256"]
        )
    ET.indent(root, space="  ")
    return '<?xml version="1.0" encoding="utf-8"?>\n' + ET.tostring(
        root,
        encoding="unicode",
    )


def _suite_project(project_root: Path, name: str) -> Path:
    return project_root / "tests" / name / f"{name}.csproj"


def _run_campaign(
    *,
    evidence_id: str,
    configuration: str,
    coverage: bool,
    timeout_seconds: float,
    tool_root: Path | None,
    no_restore: bool,
) -> int:
    project_root = Path(__file__).resolve().parents[2]
    workspace_root = _find_workspace_root(project_root)
    tool_workspace_root = _find_tool_workspace_root(workspace_root)
    resolved_tool_root = _resolve_tool_root(project_root, tool_root)
    test_authority_workspace_root = (
        _find_test_authority_workspace_root(
            workspace_root,
            tool_workspace_root,
        )
    )
    test_environment = {
        TEST_AUTHORITY_ENVIRONMENT: str(
            test_authority_workspace_root
        )
    }
    reports_root = project_root / "05-reports"
    raw_root = (
        project_root
        / "03-builds"
        / "work"
        / "test-evidence"
        / evidence_id
    )
    json_report = reports_root / f"npc-manager-test-evidence-{evidence_id}.json"
    junit_report = (
        reports_root / f"npc-manager-test-evidence-{evidence_id}.junit.xml"
    )
    cobertura_report = (
        reports_root
        / f"npc-manager-test-evidence-{evidence_id}.cobertura.xml"
    )
    outputs = [raw_root, json_report, junit_report]
    if coverage:
        outputs.append(cobertura_report)
    require_new_outputs(outputs)

    dotnet = require_file(
        resolved_tool_root
        / "dotnet-sdk-10.0.301-win-x64"
        / "dotnet.exe",
        "pinned .NET SDK",
    )
    coverage_tool = (
        resolved_tool_root
        / "dotnet-coverage-18.9.0"
        / "dotnet-coverage.exe"
    )
    if coverage:
        require_file(coverage_tool, "coverage")
    for name in TEST_PROJECTS:
        require_file(_suite_project(project_root, name), f"{name} project")

    raw_root.mkdir(parents=True)
    settings_path = raw_root / "coverage.settings.xml"
    if coverage:
        _write_new_text(settings_path, _coverage_settings())

    source_commit = _git_value(workspace_root, "rev-parse", "HEAD")
    dirty_state = _git_value(
        workspace_root,
        "status",
        "--porcelain=v2",
        "--untracked-files=normal",
    )
    started_utc = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
    campaign_started = time.monotonic()

    build_outcome = run_process(
        _build_command(dotnet, project_root, configuration, no_restore),
        project_root,
        timeout_seconds=timeout_seconds,
        environment=test_environment,
    )
    build_record = _process_record(raw_root, "build", build_outcome)
    build_passed = build_outcome.exit_code == 0 and not build_outcome.timed_out

    suites: list[dict[str, object]] = []
    raw_coverage: list[Path] = []
    if build_passed:
        for index, name in enumerate(TEST_PROJECTS, start=1):
            project_path = _suite_project(project_root, name)
            child_command = [
                str(dotnet),
                "run",
                "--project",
                str(project_path),
                "--configuration",
                configuration,
                "--no-build",
                "--no-restore",
            ]
            coverage_path = raw_root / f"{index:02d}-{name}.coverage"
            if coverage:
                command = [
                    str(coverage_tool),
                    "collect",
                    "--settings",
                    str(settings_path),
                    "--output",
                    str(coverage_path),
                    "--output-format",
                    "coverage",
                    *child_command,
                ]
            else:
                command = child_command
            outcome = run_process(
                command,
                project_root,
                timeout_seconds=timeout_seconds,
                environment=test_environment,
            )
            process_record = _process_record(
                raw_root,
                f"{index:02d}-{name}",
                outcome,
            )
            diagnostic = ""
            terminal: TerminalResult | None = None
            try:
                if outcome.timed_out:
                    raise EvidenceError("suite timed out")
                if outcome.exit_code != 0:
                    raise EvidenceError(
                        f"suite process exited {outcome.exit_code}"
                    )
                terminal = parse_terminal_result(outcome.stdout)
                if coverage and not coverage_path.is_file():
                    raise EvidenceError(
                        f"coverage tool did not create {coverage_path}"
                    )
            except EvidenceError as exception:
                diagnostic = str(exception)
            if coverage_path.is_file():
                raw_coverage.append(coverage_path)
            suites.append(
                {
                    "name": name,
                    "outcome": "PASS" if not diagnostic else "FAIL",
                    "diagnostic": diagnostic or None,
                    "terminalResult": (
                        dataclasses.asdict(terminal)
                        if terminal is not None
                        else None
                    ),
                    "coverageArtifact": (
                        {
                            "path": str(coverage_path),
                            "length": coverage_path.stat().st_size,
                            "sha256": sha256_file(coverage_path),
                        }
                        if coverage_path.is_file()
                        else None
                    ),
                    "process": process_record,
                }
            )

    validators: list[dict[str, object]] = []
    if build_passed:
        for index, relative_path in enumerate(ARCHITECTURE_VALIDATORS, start=1):
            validator_path = require_file(
                project_root / relative_path,
                f"architecture validator {relative_path}",
            )
            outcome = run_process(
                [sys.executable, str(validator_path)],
                project_root,
                timeout_seconds=timeout_seconds,
            )
            validators.append(
                {
                    "name": relative_path,
                    "outcome": (
                        "PASS"
                        if outcome.exit_code == 0 and not outcome.timed_out
                        else "FAIL"
                    ),
                    "process": _process_record(
                        raw_root,
                        f"validator-{index:02d}",
                        outcome,
                    ),
                }
            )

    coverage_record: dict[str, object] | None = None
    coverage_passed = not coverage
    if coverage:
        coverage_diagnostic = ""
        merge_record: dict[str, object] | None = None
        inventory: list[dict[str, object]] = []
        packages: list[str] = []
        if len(raw_coverage) != len(TEST_PROJECTS):
            coverage_diagnostic = (
                f"expected {len(TEST_PROJECTS)} raw coverage files, "
                f"found {len(raw_coverage)}"
            )
        else:
            merge_outcome = run_process(
                [
                    str(coverage_tool),
                    "merge",
                    "--output",
                    str(cobertura_report),
                    "--output-format",
                    "cobertura",
                    *(str(path) for path in raw_coverage),
                ],
                project_root,
                timeout_seconds=timeout_seconds,
            )
            merge_record = _process_record(
                raw_root,
                "coverage-merge",
                merge_outcome,
            )
            try:
                if merge_outcome.timed_out:
                    raise EvidenceError("coverage merge timed out")
                if merge_outcome.exit_code != 0:
                    raise EvidenceError(
                        f"coverage merge exited {merge_outcome.exit_code}"
                    )
                inventory = read_cobertura_inventory(
                    cobertura_report,
                    PRODUCTION_ASSEMBLIES,
                )
                root = ET.parse(cobertura_report).getroot()
                packages = sorted(_cobertura_packages(root))
                unexpected = [
                    package
                    for package in packages
                    if package.startswith("NpcManager.")
                    and package not in PRODUCTION_ASSEMBLIES
                ]
                if unexpected:
                    raise EvidenceError(
                        "coverage contains excluded NPC Manager assemblies: "
                        + ", ".join(unexpected)
                    )
            except (EvidenceError, ET.ParseError, OSError) as exception:
                coverage_diagnostic = str(exception)
        coverage_passed = not coverage_diagnostic
        coverage_record = {
            "outcome": "PASS" if coverage_passed else "FAIL",
            "diagnostic": coverage_diagnostic or None,
            "threshold": None,
            "expectedProductionAssemblyCount": len(PRODUCTION_ASSEMBLIES),
            "inventory": inventory,
            "packagesInReport": packages,
            "unloadedAssemblies": [
                row["assembly"] for row in inventory if not row["loaded"]
            ],
            "mergeProcess": merge_record,
            "report": (
                {
                    "path": str(cobertura_report),
                    "length": cobertura_report.stat().st_size,
                    "sha256": sha256_file(cobertura_report),
                }
                if cobertura_report.is_file()
                else None
            ),
        }

    suite_passed = (
        len(suites) == len(TEST_PROJECTS)
        and all(suite["outcome"] == "PASS" for suite in suites)
    )
    validators_passed = (
        len(validators) == len(ARCHITECTURE_VALIDATORS)
        and all(row["outcome"] == "PASS" for row in validators)
    )
    final_passed = (
        build_passed
        and suite_passed
        and validators_passed
        and coverage_passed
    )

    junit_text = _build_junit(evidence_id, suites)
    _write_new_text(junit_report, junit_text)
    total_declared_tests = sum(
        int(suite["terminalResult"]["total"])
        for suite in suites
        if suite["terminalResult"] is not None
    )
    payload: dict[str, object] = {
        "schemaVersion": 1,
        "evidenceId": evidence_id,
        "startedUtc": started_utc,
        "completedUtc": time.strftime(
            "%Y-%m-%dT%H:%M:%SZ",
            time.gmtime(),
        ),
        "elapsedSeconds": round(time.monotonic() - campaign_started, 6),
        "source": {
            **_source_record(
                project_root,
                resolved_tool_root,
                source_commit,
                dirty_state,
            ),
            "testAuthorityWorkspace": str(test_authority_workspace_root),
        },
        "configuration": configuration,
        "coverageRequested": coverage,
        "tools": {
            "python": _tool_record(Path(sys.executable)),
            "dotnet": _tool_record(dotnet),
            "dotnetCoverage": (
                _tool_record(coverage_tool) if coverage else None
            ),
        },
        "build": {
            "outcome": "PASS" if build_passed else "FAIL",
            "process": build_record,
        },
        "suiteCount": len(suites),
        "requiredSuiteCount": len(TEST_PROJECTS),
        "declaredTestCount": total_declared_tests,
        "suites": suites,
        "architectureValidators": validators,
        "coverage": coverage_record,
        "artifacts": {
            "junit": {
                "path": str(junit_report),
                "length": junit_report.stat().st_size,
                "sha256": sha256_file(junit_report),
            },
            "cobertura": (
                coverage_record["report"]
                if coverage_record is not None
                else None
            ),
            "jsonSelfHash": None,
            "jsonSelfHashReason": (
                "The JSON report cannot contain its own cryptographic hash; "
                "package verification records it independently."
            ),
        },
        "outcome": "PASS" if final_passed else "FAIL",
    }
    _write_new_text(
        json_report,
        json.dumps(payload, indent=2, sort_keys=True) + "\n",
    )
    print(
        f"RESULT {'PASS' if final_passed else 'FAIL'} "
        f"{sum(1 for suite in suites if suite['outcome'] == 'PASS')}/"
        f"{len(TEST_PROJECTS)}"
    )
    print(f"JSON {json_report}")
    print(f"JUNIT {junit_report}")
    if coverage:
        print(f"COBERTURA {cobertura_report}")
    return 0 if final_passed else 1


def _parse_args(argv: Sequence[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Build NPC Manager once and run all executable suites with "
            "fail-closed JSON, JUnit, and optional Cobertura evidence."
        )
    )
    parser.add_argument("--evidence-id", required=True)
    parser.add_argument(
        "--configuration",
        choices=("Debug", "Release"),
        default="Release",
    )
    parser.add_argument("--coverage", action="store_true")
    parser.add_argument("--tool-root", type=Path, default=None)
    parser.add_argument("--no-restore", action="store_true")
    parser.add_argument("--timeout-seconds", type=float, default=1800.0)
    arguments = parser.parse_args(argv)
    if not EVIDENCE_ID.fullmatch(arguments.evidence_id):
        parser.error(
            "--evidence-id must use lowercase letters, digits, dots, or hyphens"
        )
    if arguments.timeout_seconds <= 0:
        parser.error("--timeout-seconds must be positive")
    return arguments


def main(argv: Sequence[str] | None = None) -> int:
    arguments = _parse_args(sys.argv[1:] if argv is None else argv)
    try:
        return _run_campaign(
            evidence_id=arguments.evidence_id,
            configuration=arguments.configuration,
            coverage=arguments.coverage,
            timeout_seconds=arguments.timeout_seconds,
            tool_root=arguments.tool_root,
            no_restore=arguments.no_restore,
        )
    except (EvidenceError, OSError, subprocess.SubprocessError) as exception:
        print(f"RESULT FAIL 0/{len(TEST_PROJECTS)}", file=sys.stderr)
        print(f"ERROR {exception}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
