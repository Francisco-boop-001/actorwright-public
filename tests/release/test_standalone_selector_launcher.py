import importlib.util
import io
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[2]
LAUNCHER = ROOT / "tools" / "build" / "run_standalone_selectors.py"


def _launcher_module():
    assert LAUNCHER.is_file(), "canonical standalone selector launcher is missing"
    spec = importlib.util.spec_from_file_location(
        "actorwright_standalone_selector_launcher", LAUNCHER)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_launcher_resolves_unique_outputs_and_bounds_exact_dll_processes(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    launcher = _launcher_module()
    monkeypatch.setenv("DOTNET_ROOT", str(tmp_path / "hostile-runtime"))
    monkeypatch.setenv("DOTNET_ROOT_X64", str(tmp_path / "hostile-x64-runtime"))
    root = tmp_path / "repo"
    dotnet = tmp_path / "pinned-dotnet.exe"
    dotnet.write_bytes(b"dotnet")
    projects = [
        "tests/Architecture/Architecture.csproj",
        "tests/Cli/Cli.csproj",
        "tests/Desktop/Desktop.csproj",
    ]
    tests = [
        {
            "id": "architecture-a",
            "project": projects[0],
            "arguments": ["--test-a"],
            "classification": "runnable",
            "dependencies": [],
            "reason": "fixture",
        },
        {
            "id": "architecture-b",
            "project": projects[0],
            "arguments": ["--test-b"],
            "classification": "runnable",
            "dependencies": [],
            "reason": "fixture",
            "timeoutSeconds": 37,
        },
        {
            "id": "architecture-default",
            "project": projects[0],
            "arguments": [],
            "classification": "runnable",
            "dependencies": [],
            "reason": "Run the project's default test entrypoint.",
        },
        {
            "id": "cli-a",
            "project": projects[1],
            "arguments": ["--test-cli"],
            "classification": "runnable",
            "dependencies": [],
            "reason": "fixture",
        },
        {
            "id": "desktop-a",
            "project": projects[2],
            "arguments": ["--test-desktop"],
            "classification": "runnable",
            "dependencies": [],
            "reason": "fixture",
            "targetFramework": "net10.0-windows",
        },
    ]
    for relative in projects:
        project = root / relative
        project.parent.mkdir(parents=True, exist_ok=True)
        project.write_text("<Project />", encoding="utf-8")
    matrix = root / "tests" / "standalone-test-matrix.json"
    matrix.parent.mkdir(parents=True, exist_ok=True)
    matrix.write_text(json.dumps({"schemaVersion": 2, "tests": tests}), encoding="utf-8")
    (root / "artifacts").mkdir()

    metadata_commands: list[list[str]] = []

    def query(command: list[str], **kwargs: object):
        metadata_commands.append(command)
        return type("Result", (), {
            "returncode": 0,
            "stdout": json.dumps({"Properties": {
                "TargetPath": "bin/Architecture.dll",
                "TargetFramework": "net10.0",
                "TargetFrameworks": "",
            }}),
            "stderr": "",
        })()

    monkeypatch.setattr(launcher.subprocess, "run", query)
    resolved = launcher._evaluate_project(
        dotnet, root / projects[0], "Release", None)
    assert resolved["TargetPath"] == "bin/Architecture.dll"
    assert metadata_commands == [[
        str(dotnet),
        "msbuild",
        str(root / projects[0]),
        "-nologo",
        "-property:Configuration=Release",
        "-getProperty:TargetPath",
        "-getProperty:TargetFramework",
        "-getProperty:TargetFrameworks",
    ]]

    metadata_calls: list[tuple[Path, str | None]] = []

    def metadata(
        actual_dotnet: Path,
        project: Path,
        configuration: str,
        framework: str | None,
    ):
        assert actual_dotnet == dotnet
        assert configuration == "Release"
        metadata_calls.append((project, framework))
        if project.name == "Desktop.csproj":
            return {
                "TargetPath": str(project.parent / "bin" / "Desktop.dll"),
                "TargetFramework": framework or "",
                "TargetFrameworks": "net10.0;net10.0-windows",
            }
        return {
            "TargetPath": str(project.parent / "bin" / f"{project.stem}.dll"),
            "TargetFramework": "net10.0",
            "TargetFrameworks": "",
        }

    launched: list[tuple[list[str], int, Path, dict[str, str], Path, bool]] = []

    def launch(
        command: list[str],
        timeout_seconds: int,
        scratch_root: Path,
        environment: dict[str, str],
        working_directory: Path,
        capture_output: bool = False,
    ) -> tuple[str, str] | None:
        launched.append((
            command,
            timeout_seconds,
            scratch_root,
            environment,
            working_directory,
            capture_output,
        ))
        if capture_output:
            selectors = {
                "Architecture.dll": ["--test-a", "--test-b"],
                "Cli.dll": ["--test-cli"],
                "Desktop.dll": ["--test-desktop"],
            }[Path(command[1]).name]
            return json.dumps(selectors) + "\n", ""
        return None

    monkeypatch.setattr(launcher, "_evaluate_project", metadata)
    monkeypatch.setattr(launcher, "_validate_runtime_output", lambda *args: args[0])
    monkeypatch.setattr(launcher, "_launch_bounded", launch)
    launcher.run(
        dotnet=dotnet,
        project_root=root,
        matrix_path=matrix,
        configuration="Release",
        timeout_seconds=19,
    )
    assert capsys.readouterr().out.splitlines() == [
        "SELECTOR_INVENTORY project=tests/Architecture/Architecture.csproj selectors=2 status=PASS",
        "SELECTOR_INVENTORY project=tests/Cli/Cli.csproj selectors=1 status=PASS",
        "SELECTOR_INVENTORY project=tests/Desktop/Desktop.csproj selectors=1 status=PASS",
        "SELECTOR_RESULT id=architecture-a status=PASS",
        "SELECTOR_RESULT id=architecture-b status=PASS",
        "SELECTOR_RESULT id=architecture-default status=PASS",
        "SELECTOR_RESULT id=cli-a status=PASS",
        "SELECTOR_RESULT id=desktop-a status=PASS",
    ]

    assert metadata_calls == [
        (root / projects[0], None),
        (root / projects[1], None),
        (root / projects[2], "net10.0-windows"),
    ]
    assert [row[:3] for row in launched] == [
        ([str(dotnet), str(root / "tests/Architecture/bin/Architecture.dll"), "--list-selectors"], 19, root / "artifacts"),
        ([str(dotnet), str(root / "tests/Cli/bin/Cli.dll"), "--list-selectors"], 19, root / "artifacts"),
        ([str(dotnet), str(root / "tests/Desktop/bin/Desktop.dll"), "--list-selectors"], 19, root / "artifacts"),
        ([str(dotnet), str(root / "tests/Architecture/bin/Architecture.dll"), "--test-a"], 19, root / "artifacts"),
        ([str(dotnet), str(root / "tests/Architecture/bin/Architecture.dll"), "--test-b"], 37, root / "artifacts"),
        ([str(dotnet), str(root / "tests/Architecture/bin/Architecture.dll")], 19, root / "artifacts"),
        ([str(dotnet), str(root / "tests/Cli/bin/Cli.dll"), "--test-cli"], 19, root / "artifacts"),
        ([str(dotnet), str(root / "tests/Desktop/bin/Desktop.dll"), "--test-desktop"], 19, root / "artifacts"),
    ]
    assert all("run" not in command for command, _, _, _, _, _ in launched)
    assert all(
        environment["DOTNET_ROOT"] == str(dotnet.parent)
        for _, _, _, environment, _, _ in launched
    )
    assert all(working_directory == root for *_, working_directory, _ in launched)
    assert [capture for *_, capture in launched] == [
        True, True, True, False, False, False, False, False,
    ]
    if launcher.os.name == "nt" and launcher.platform.machine().lower() in {
        "amd64", "x86_64",
    }:
        assert all(
            environment["DOTNET_ROOT_X64"] == str(dotnet.parent)
            for _, _, _, environment, _, _ in launched
        )

    launched.clear()
    launcher.run(
        dotnet=dotnet,
        project_root=root,
        matrix_path=matrix,
        configuration="Release",
        timeout_seconds=19,
        inventory_only=True,
    )
    assert capsys.readouterr().out.splitlines() == [
        "SELECTOR_INVENTORY project=tests/Architecture/Architecture.csproj selectors=2 status=PASS",
        "SELECTOR_INVENTORY project=tests/Cli/Cli.csproj selectors=1 status=PASS",
        "SELECTOR_INVENTORY project=tests/Desktop/Desktop.csproj selectors=1 status=PASS",
    ]
    assert len(launched) == 3
    assert all(capture for *_, capture in launched)

    tests[-1].pop("targetFramework")
    matrix.write_text(json.dumps({"schemaVersion": 2, "tests": tests}), encoding="utf-8")
    with pytest.raises(launcher.LauncherError, match="multiple target frameworks"):
        launcher.run(dotnet, root, matrix, "Release", 19)


def test_launcher_runs_exact_selector_from_project_root(tmp_path: Path) -> None:
    launcher = _launcher_module()
    project_root = tmp_path / "repo"
    project_root.mkdir()
    scratch_root = project_root / "artifacts"
    scratch_root.mkdir()
    observed = tmp_path / "selector-cwd.txt"
    selector = (
        "from pathlib import Path; "
        f"Path({str(observed)!r}).write_text(str(Path.cwd()), encoding='utf-8')"
    )

    launcher._launch_bounded(
        [sys.executable, "-c", selector],
        10,
        scratch_root=scratch_root,
        environment=launcher._pinned_runtime_environment(Path(sys.executable)),
        working_directory=project_root,
    )

    assert Path(observed.read_text(encoding="utf-8")).resolve() == project_root.resolve()


def test_launcher_cli_preserves_unicode_in_redirected_output(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    project = root / "tests" / "Unicode" / "Unicode.csproj"
    target = project.parent / "bin" / "Release" / "net10.0" / "Unicode.dll"
    fixture_project = root / "tests" / "FixtureOnly" / "FixtureOnly.csproj"
    fixture_target = (
        fixture_project.parent / "bin" / "Release" / "net10.0" / "FixtureOnly.dll")
    no_selector_project = root / "tests" / "NoSelectors" / "NoSelectors.csproj"
    no_selector_target = (
        no_selector_project.parent / "bin" / "Release" / "net10.0" / "NoSelectors.dll")
    target.parent.mkdir(parents=True)
    fixture_target.parent.mkdir(parents=True)
    no_selector_target.parent.mkdir(parents=True)
    (root / "artifacts").mkdir()
    project.write_text("<Project />", encoding="utf-8")
    fixture_project.write_text("<Project />", encoding="utf-8")
    no_selector_project.write_text("<Project />", encoding="utf-8")
    target.with_suffix(".runtimeconfig.json").write_text("{}", encoding="utf-8")
    fixture_target.with_suffix(".runtimeconfig.json").write_text(
        "{}", encoding="utf-8")
    no_selector_target.with_suffix(".runtimeconfig.json").write_text(
        "{}", encoding="utf-8")
    stdout = "selector \u2192 caf\u00e9 \u6f22\u5b57\n".encode("utf-8")
    stderr = "detail \u2192 caf\u00e9 \u6f22\u5b57\n".encode("utf-8")
    selector_list = json.dumps([
        "--test-fixed-fixture", "--test-unicode",
    ], separators=(",", ":"))
    target.write_text(
        "import pathlib, sys\n"
        "if sys.argv[1:] == ['--list-selectors']:\n"
        f"    sys.stdout.write({selector_list!r} + '\\n')\n"
        "elif sys.argv[1:] == ['--test-unicode']:\n"
        f"    sys.stdout.buffer.write({stdout!r})\n"
        f"    sys.stderr.buffer.write({stderr!r})\n"
        "elif sys.argv[1:] == ['--test-fixed-fixture', '<input>']:\n"
        "    pathlib.Path('fixture-route-ran.txt').write_text('unexpected')\n"
        "    raise SystemExit(90)\n"
        "else:\n"
        "    raise SystemExit(91)\n", encoding="utf-8")
    fixture_selector_list = json.dumps(
        ["--test-default-only-fixture"], separators=(",", ":"))
    fixture_target.write_text(
        "import sys\n"
        "if sys.argv[1:] == ['--list-selectors']:\n"
        f"    sys.stdout.write({fixture_selector_list!r} + '\\n')\n"
        "else:\n"
        "    raise SystemExit(92)\n", encoding="utf-8")
    no_selector_target.write_text(
        "import sys\n"
        "if sys.argv[1:] == ['--list-selectors']:\n"
        "    sys.stdout.write('[]\\n')\n"
        "else:\n"
        "    raise SystemExit(93)\n", encoding="utf-8")
    # Python supplies a tiny metadata host and selector; the CLI/job/output path is real.
    metadata = {
        str(project.resolve()).casefold(): {
            "TargetPath": str(target),
            "TargetFramework": "net10.0",
            "TargetFrameworks": "",
        },
        str(fixture_project.resolve()).casefold(): {
            "TargetPath": str(fixture_target),
            "TargetFramework": "net10.0",
            "TargetFrameworks": "",
        },
        str(no_selector_project.resolve()).casefold(): {
            "TargetPath": str(no_selector_target),
            "TargetFramework": "net10.0",
            "TargetFrameworks": "",
        },
    }
    (root / "msbuild").write_text(
        "import json, pathlib, sys\n"
        f"metadata = {metadata!r}\n"
        "properties = metadata[str(pathlib.Path(sys.argv[1]).resolve()).casefold()]\n"
        "print(json.dumps({'Properties': properties}))\n", encoding="utf-8")
    matrix = root / "matrix.json"
    matrix.write_text(json.dumps({"schemaVersion": 2, "tests": [
        {
            "id": "unicode",
            "project": "tests/Unicode/Unicode.csproj",
            "arguments": ["--test-unicode"],
            "classification": "runnable",
            "targetFramework": "net10.0",
        },
        {
            "id": "fixed-fixture",
            "project": "tests/Unicode/Unicode.csproj",
            "arguments": ["--test-fixed-fixture", "<input>"],
            "classification": "fixture-bound",
            "dependencies": ["external input"],
            "reason": "Requires the parent-provided input.",
        },
        {
            "id": "unicode-default",
            "project": "tests/Unicode/Unicode.csproj",
            "arguments": [],
            "classification": "fixture-bound",
            "dependencies": ["external input"],
            "reason": "The default suite requires its parent fixture.",
        },
        {
            "id": "default-only-fixture",
            "project": "tests/FixtureOnly/FixtureOnly.csproj",
            "arguments": ["--test-default-only-fixture", "<source>"],
            "classification": "fixture-bound",
            "dependencies": ["external source"],
            "reason": "Requires a parent-provided source file.",
        },
        {
            "id": "default-only",
            "project": "tests/FixtureOnly/FixtureOnly.csproj",
            "arguments": [],
            "classification": "fixture-bound",
            "dependencies": ["external source"],
            "reason": "The default suite requires its parent fixture.",
        },
        {
            "id": "no-selector-default",
            "project": "tests/NoSelectors/NoSelectors.csproj",
            "arguments": [],
            "classification": "fixture-bound",
            "dependencies": ["external source"],
            "reason": "The default suite requires its parent fixture.",
        },
    ]}), encoding="utf-8")
    environment = os.environ.copy()
    environment["PYTHONIOENCODING"] = "cp1252:strict"
    result = subprocess.run(
        [sys.executable, str(LAUNCHER), "--dotnet", sys.executable,
         "--project-root", str(root), "--matrix", str(matrix),
         "--configuration", "Release"],
        cwd=root, env=environment, capture_output=True, timeout=30, check=False,
    )
    assert result.returncode == 0, result.stderr.decode("utf-8", errors="replace")
    assert result.stdout.replace(b"\r\n", b"\n") == (
        b"SELECTOR_INVENTORY project=tests/FixtureOnly/FixtureOnly.csproj selectors=1 status=PASS\n" +
        b"SELECTOR_INVENTORY project=tests/NoSelectors/NoSelectors.csproj selectors=0 status=PASS\n" +
        b"SELECTOR_INVENTORY project=tests/Unicode/Unicode.csproj selectors=2 status=PASS\n" +
        stdout + b"SELECTOR_RESULT id=unicode status=PASS\n" +
        b"standalone selector launcher: PASS\n"
    )
    assert result.stderr.replace(b"\r\n", b"\n") == stderr
    assert not (root / "fixture-route-ran.txt").exists()


def test_selector_inventory_rejects_bad_output_and_matrix_mismatch() -> None:
    launcher = _launcher_module()
    assert launcher._parse_selector_inventory("[]\n", "", "empty") == []
    for stdout, stderr, message in [
        ("not json\n", "", "malformed JSON"),
        ('["--test-a","--test-a"]\n', "", "duplicate selectors"),
        ('["--test-b","--test-a"]\n', "", "not ordinally sorted"),
        ('["--test-a"]\n', "unexpected diagnostic", "unexpected stderr"),
    ]:
        with pytest.raises(launcher.LauncherError, match=message):
            launcher._parse_selector_inventory(stdout, stderr, "fixture")

    with pytest.raises(launcher.LauncherError, match="does not match matrix"):
        launcher._require_selector_inventory_match(
            "fixture", ["--test-a"], ["--test-a", "--test-b"])


def test_launcher_matrix_timeout_seconds_is_strict_and_runnable_only(
    tmp_path: Path,
) -> None:
    launcher = _launcher_module()
    path = tmp_path / "standalone-test-matrix.json"
    row = {
        "id": "architecture-a",
        "project": "tests/Architecture/Architecture.csproj",
        "arguments": ["--test-a"],
        "classification": "runnable",
        "dependencies": [],
        "reason": "fixture",
        "timeoutSeconds": 240,
    }
    path.write_text(
        json.dumps({"schemaVersion": 2, "tests": [row]}), encoding="utf-8")
    assert launcher._load_matrix(path)[0]["timeoutSeconds"] == 240

    row["id"] = "bad id"
    path.write_text(
        json.dumps({"schemaVersion": 2, "tests": [row]}), encoding="utf-8")
    with pytest.raises(launcher.LauncherError, match="invalid id"):
        launcher._load_matrix(path)
    row["id"] = "architecture-a"

    for invalid in (True, 0, 3601, 1.5):
        row["timeoutSeconds"] = invalid
        path.write_text(
            json.dumps({"schemaVersion": 2, "tests": [row]}), encoding="utf-8")
        with pytest.raises(
            launcher.LauncherError,
            match="runnable selector row 0 has invalid timeoutSeconds",
        ):
            launcher._load_matrix(path)

    row["timeoutSeconds"] = 240
    row["classification"] = "fixture-bound"
    path.write_text(
        json.dumps({"schemaVersion": 2, "tests": [row]}), encoding="utf-8")
    with pytest.raises(
        launcher.LauncherError,
        match="selector row 0 timeoutSeconds is only valid for runnable selectors",
    ):
        launcher._load_matrix(path)


def test_launcher_reaps_live_descendant_after_selector_root_exits(
    tmp_path: Path,
) -> None:
    launcher = _launcher_module()
    marker = tmp_path / "descendant-survived.txt"
    descendant = (
        "import pathlib,time; time.sleep(1); "
        f"pathlib.Path({str(marker)!r}).write_text('alive', encoding='utf-8')"
    )
    selector = (
        "import subprocess,sys; "
        f"subprocess.Popen([sys.executable, '-c', {descendant!r}]); "
        "print('selector-root-exited', flush=True)"
    )
    started = time.monotonic()
    launcher._launch_bounded(
        [sys.executable, "-c", selector],
        4,
        environment=launcher._pinned_runtime_environment(Path(sys.executable)),
    )
    assert time.monotonic() - started < 4
    time.sleep(1.25)
    assert not marker.exists()


def test_launcher_timeout_is_single_attempt_and_preserves_output(
    tmp_path: Path,
    capsys: pytest.CaptureFixture[str],
) -> None:
    launcher = _launcher_module()
    attempts = tmp_path / "attempts.txt"
    selector = (
        "import pathlib,time; "
        f"pathlib.Path({str(attempts)!r}).open('a', encoding='utf-8').write('1'); "
        "print('selector-before-timeout', flush=True); time.sleep(10)"
    )
    with pytest.raises(launcher.LauncherError, match="timed out after 1 seconds"):
        launcher._launch_bounded(
            [sys.executable, "-c", selector],
            1,
            environment=launcher._pinned_runtime_environment(Path(sys.executable)),
        )
    assert attempts.read_text(encoding="utf-8") == "1"
    assert "selector-before-timeout" in capsys.readouterr().out


@pytest.mark.parametrize(("boundary_exit", "expected"), [
    (7, "failed with exit 7"),
    (None, "timed out after 11 seconds"),
])
def test_launcher_polls_once_at_timeout_boundary_and_preserves_output(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
    boundary_exit: int | None,
    expected: str,
) -> None:
    launcher = _launcher_module()
    command = ["pinned-dotnet", "selector.dll", "--test-selector"]
    starts = 0
    cleanup_calls = 0
    polls = 0

    class BoundaryProcess:
        pid = 4242
        returncode: int | None = None
        stdin = io.BytesIO()

        def wait(self, timeout: int) -> int:
            assert timeout == 11
            self.returncode = boundary_exit
            raise subprocess.TimeoutExpired(command, timeout)

        def poll(self) -> int | None:
            nonlocal polls
            polls += 1
            return self.returncode

    process = BoundaryProcess()

    def start(actual: list[str], **kwargs: object) -> BoundaryProcess:
        nonlocal starts
        starts += 1
        if launcher.os.name == "nt":
            assert actual[-len(command):] == command
        else:
            assert actual == command
        output = kwargs["stdout"]
        assert hasattr(output, "write")
        output.write(b"selector-at-boundary\n")
        output.flush()
        return process

    class Job:
        def __init__(self, process_id: int) -> None:
            assert process_id == process.pid

    def cleanup(actual: BoundaryProcess, job: object) -> None:
        nonlocal cleanup_calls
        assert actual is process
        if launcher.os.name == "nt":
            assert isinstance(job, Job)
        else:
            assert job is None
        cleanup_calls += 1

    monkeypatch.setattr(launcher.subprocess, "Popen", start)
    monkeypatch.setattr(launcher, "_WindowsJob", Job)
    monkeypatch.setattr(launcher, "_close_process_tree", cleanup)

    with pytest.raises(launcher.LauncherError, match=expected):
        launcher._launch_bounded(
            command,
            11,
            scratch_root=tmp_path,
            environment={"DOTNET_ROOT": "pinned"},
        )
    assert starts == 1
    assert polls == 1
    assert cleanup_calls == 1
    assert "selector-at-boundary" in capsys.readouterr().out


def test_launcher_keeps_timeout_primary_when_live_process_cleanup_fails(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    launcher = _launcher_module()
    command = ["pinned-dotnet", "selector.dll", "--test-selector"]
    starts = 0
    cleanup_calls = 0

    class LiveProcess:
        pid = 4343
        returncode: int | None = None
        stdin = io.BytesIO()

        def wait(self, timeout: int) -> int:
            assert timeout == 11
            raise subprocess.TimeoutExpired(command, timeout)

        def poll(self) -> None:
            return None

    process = LiveProcess()

    def start(actual: list[str], **kwargs: object) -> LiveProcess:
        nonlocal starts
        starts += 1
        if launcher.os.name == "nt":
            assert actual[-len(command):] == command
        else:
            assert actual == command
        stdout = kwargs["stdout"]
        stderr = kwargs["stderr"]
        assert hasattr(stdout, "write") and hasattr(stderr, "write")
        stdout.write(b"selector-live-stdout\n")
        stderr.write(b"selector-live-stderr\n")
        stdout.flush()
        stderr.flush()
        return process

    class Job:
        def __init__(self, process_id: int) -> None:
            assert process_id == process.pid

    def cleanup(actual: LiveProcess, job: object) -> None:
        nonlocal cleanup_calls
        assert actual is process
        if launcher.os.name == "nt":
            assert isinstance(job, Job)
        else:
            assert job is None
        cleanup_calls += 1
        raise launcher.LauncherError("cleanup-sentinel")

    monkeypatch.setattr(launcher.subprocess, "Popen", start)
    monkeypatch.setattr(launcher, "_WindowsJob", Job)
    monkeypatch.setattr(launcher, "_close_process_tree", cleanup)

    with pytest.raises(launcher.LauncherError) as caught:
        launcher._launch_bounded(
            command,
            11,
            scratch_root=tmp_path,
            environment={"DOTNET_ROOT": "pinned"},
        )
    message = str(caught.value)
    assert message.startswith("standalone selector timed out after 11 seconds:")
    assert "selector cleanup also failed: cleanup-sentinel" in message
    assert starts == 1
    assert cleanup_calls == 1
    captured = capsys.readouterr()
    assert "selector-live-stdout" in captured.out
    assert "selector-live-stderr" in captured.err


def test_job_child_passes_wrapper_environment_to_exact_command(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    launcher = _launcher_module()
    pinned_root = "X:\\pinned-dotnet"
    monkeypatch.setenv("DOTNET_ROOT", pinned_root)

    class Gate:
        buffer = io.BytesIO(b"1")

    class Child:
        def wait(self) -> int:
            return 7

    observed: list[tuple[list[str], dict[str, str]]] = []

    def start(command: list[str], **kwargs: object) -> Child:
        environment = kwargs["env"]
        assert isinstance(environment, dict)
        observed.append((command, environment))
        return Child()

    monkeypatch.setattr(launcher.sys, "stdin", Gate())
    monkeypatch.setattr(launcher.subprocess, "Popen", start)
    command = ["pinned-dotnet", "selector.dll", "--test-selector"]
    assert launcher._job_child(command) == 7
    assert observed[0][0] == command
    assert observed[0][1]["DOTNET_ROOT"] == pinned_root


def test_posix_cleanup_reaps_root_before_polling_process_group(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    launcher = _launcher_module()
    order: list[str] = []
    posix_sigkill = 99
    monkeypatch.setattr(
        launcher.signal, "SIGKILL", posix_sigkill, raising=False)

    class ExitedRoot:
        pid = 321

        def wait(self, timeout: int) -> int:
            assert timeout == 30
            order.append("root-reaped")
            return -9

    def killpg(process_group: int, requested_signal: int) -> None:
        assert process_group == 321
        if requested_signal == posix_sigkill:
            order.append("group-killed")
            return
        assert requested_signal == 0
        assert order == ["group-killed", "root-reaped"]
        order.append("group-polled")
        raise ProcessLookupError

    monkeypatch.setattr(launcher.os, "killpg", killpg, raising=False)
    launcher._close_process_tree(ExitedRoot(), None)
    assert order == ["group-killed", "root-reaped", "group-polled"]


def test_runtime_output_refuses_external_evaluated_target(tmp_path: Path) -> None:
    launcher = _launcher_module()
    root = tmp_path / "repo"
    expected = root / "tests" / "Project" / "bin" / "Release"
    expected.mkdir(parents=True)
    outside = tmp_path / "outside"
    outside.mkdir()
    target = outside / "Project.dll"
    target.write_bytes(b"dll")
    target.with_suffix(".runtimeconfig.json").write_text("{}", encoding="utf-8")

    with pytest.raises(launcher.LauncherError, match="outside intended project output"):
        launcher._validate_runtime_output(target, root, expected)


def test_runtime_output_refuses_nested_reparse_escape(
    tmp_path: Path,
    require_ntfs_fixture,
    filesystem_fixture_unavailable,
) -> None:
    filesystem = require_ntfs_fixture(tmp_path, "standalone selector output junction")
    launcher = _launcher_module()
    root = tmp_path / "repo"
    expected = root / "tests" / "Project" / "bin" / "Release"
    expected.mkdir(parents=True)
    outside = tmp_path / "outside"
    outside.mkdir()
    (outside / "Project.dll").write_bytes(b"dll")
    (outside / "Project.runtimeconfig.json").write_text("{}", encoding="utf-8")
    junction = expected / "net10.0"
    if os.name == "nt":
        environment = os.environ.copy()
        environment["ACTORWRIGHT_JUNCTION_PATH"] = str(junction)
        environment["ACTORWRIGHT_JUNCTION_TARGET"] = str(outside)
        created = subprocess.run(
            [
                "powershell.exe", "-NoProfile", "-Command",
                "New-Item -ItemType Junction -Path $env:ACTORWRIGHT_JUNCTION_PATH "
                "-Target $env:ACTORWRIGHT_JUNCTION_TARGET | Out-Null",
            ],
            env=environment,
            capture_output=True,
            check=False,
            text=True,
        )
        if created.returncode != 0:
            filesystem_fixture_unavailable(
                tmp_path,
                filesystem,
                "standalone selector output junction",
                RuntimeError(
                    (created.stdout + created.stderr).strip()
                    or "junction creation command failed without a reason"
                ),
            )

    with pytest.raises(launcher.LauncherError, match="reparse"):
        launcher._validate_runtime_output(
            junction / "Project.dll", root, expected)
