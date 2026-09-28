#!/usr/bin/env python3
"""Resolve and run canonical standalone selectors from already-built DLLs."""

from __future__ import annotations

import argparse
import json
import os
import platform
import signal
import stat
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any


class LauncherError(RuntimeError):
    """A standalone selector could not be resolved or completed safely."""


def _ordinary_file(path: Path) -> bool:
    try:
        stat = path.lstat()
    except OSError:
        return False
    if path.is_symlink() or not path.is_file():
        return False
    reparse = getattr(stat, "st_file_attributes", 0) & 0x400
    return reparse == 0


def _pinned_runtime_environment(dotnet: Path) -> dict[str, str]:
    try:
        canonical_dotnet = dotnet.resolve(strict=True)
    except OSError as exc:
        raise LauncherError(f"pinned dotnet host cannot be resolved: {dotnet}") from exc
    if not _ordinary_file(canonical_dotnet):
        raise LauncherError(
            f"pinned dotnet host is not an ordinary file: {canonical_dotnet}")
    runtime_root = str(canonical_dotnet.parent)
    environment = os.environ.copy()
    for name in list(environment):
        if name.upper().startswith("DOTNET_ROOT_"):
            environment.pop(name)
    environment["DOTNET_ROOT"] = runtime_root
    if os.name == "nt" and platform.machine().lower() in {"amd64", "x86_64"}:
        environment["DOTNET_ROOT_X64"] = runtime_root
    return environment


def _absolute_lexical(path: Path) -> Path:
    return Path(os.path.abspath(os.fspath(path)))


def _admit_contained_path(
    path: Path,
    project_root: Path,
    intended_root: Path,
    label: str,
) -> Path:
    root = _absolute_lexical(project_root)
    intended = _absolute_lexical(intended_root)
    candidate = _absolute_lexical(path)
    try:
        intended.relative_to(root)
        relative = candidate.relative_to(root)
        candidate.relative_to(intended)
    except ValueError as exc:
        raise LauncherError(
            f"{label} is outside intended project output: {candidate}") from exc
    current = root
    components = [root]
    for part in relative.parts:
        current = current / part
        components.append(current)
    for component in components:
        try:
            metadata = component.lstat()
        except OSError as exc:
            raise LauncherError(f"{label} is missing: {component}") from exc
        attributes = getattr(metadata, "st_file_attributes", 0)
        if component.is_symlink() or attributes & 0x400:
            raise LauncherError(f"{label} traverses a reparse point: {component}")
    try:
        canonical_root = root.resolve(strict=True)
        canonical_intended = intended.resolve(strict=True)
        canonical = candidate.resolve(strict=True)
        canonical_intended.relative_to(canonical_root)
        canonical.relative_to(canonical_root)
        canonical.relative_to(canonical_intended)
    except (OSError, ValueError) as exc:
        raise LauncherError(
            f"{label} escapes canonical project output: {candidate}") from exc
    return canonical


def _admit_directory(
    path: Path,
    project_root: Path,
    intended_root: Path,
    label: str,
) -> Path:
    canonical = _admit_contained_path(path, project_root, intended_root, label)
    try:
        metadata = canonical.stat()
    except OSError as exc:
        raise LauncherError(f"{label} is missing: {canonical}") from exc
    if not stat.S_ISDIR(metadata.st_mode):
        raise LauncherError(f"{label} is not a directory: {canonical}")
    return canonical


def _load_matrix(path: Path) -> list[dict[str, Any]]:
    try:
        matrix = json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise LauncherError(f"cannot read standalone selector matrix: {exc}") from exc
    if not isinstance(matrix, dict) or matrix.get("schemaVersion") != 2:
        raise LauncherError("standalone selector matrix must use schemaVersion 2")
    tests = matrix.get("tests")
    if not isinstance(tests, list):
        raise LauncherError("standalone selector matrix tests must be an array")
    rows: list[dict[str, Any]] = []
    runnable_count = 0
    for index, row in enumerate(tests):
        if not isinstance(row, dict):
            raise LauncherError(f"standalone selector row {index} must be an object")
        classification = row.get("classification")
        if classification not in {"runnable", "fixture-bound"}:
            raise LauncherError(
                f"standalone selector row {index} has an unsupported classification")
        row_kind = "runnable selector row" if classification == "runnable" else "fixture-bound selector row"
        timeout_seconds = row.get("timeoutSeconds")
        if "timeoutSeconds" in row and classification != "runnable":
            raise LauncherError(
                f"selector row {index} timeoutSeconds is only valid for "
                "runnable selectors")
        selector_id = row.get("id")
        if not isinstance(selector_id, str) or not selector_id or any(
            char.isspace() for char in selector_id
        ):
            raise LauncherError(f"{row_kind} {index} has invalid id")
        if "timeoutSeconds" in row and (
            isinstance(timeout_seconds, bool)
            or not isinstance(timeout_seconds, int)
            or not 1 <= timeout_seconds <= 3600
        ):
            raise LauncherError(
                f"{row_kind} {index} has invalid timeoutSeconds")
        project = row.get("project")
        arguments = row.get("arguments")
        if not isinstance(project, str) or not project.strip():
            raise LauncherError(f"{row_kind} {index} has no project")
        if not isinstance(arguments, list) or not all(
            isinstance(argument, str) and argument for argument in arguments
        ):
            raise LauncherError(f"{row_kind} {index} has invalid arguments")
        if arguments and (
            not arguments[0].startswith("--") or
            any(char.isspace() for char in arguments[0])
        ):
            raise LauncherError(f"{row_kind} {index} has an invalid selector route")
        framework = row.get("targetFramework")
        if framework is not None and (
            not isinstance(framework, str) or not framework.strip()
        ):
            raise LauncherError(
                f"{row_kind} {index} has an invalid targetFramework")
        rows.append(row)
        runnable_count += classification == "runnable"
    if not runnable_count:
        raise LauncherError("standalone selector matrix has no runnable selectors")
    return rows


def _evaluate_project(
    dotnet: Path,
    project: Path,
    configuration: str,
    framework: str | None,
) -> dict[str, str]:
    command = [
        str(dotnet),
        "msbuild",
        str(project),
        "-nologo",
        f"-property:Configuration={configuration}",
    ]
    if framework is not None:
        command.append(f"-property:TargetFramework={framework}")
    command.extend([
        "-getProperty:TargetPath",
        "-getProperty:TargetFramework",
        "-getProperty:TargetFrameworks",
    ])
    try:
        result = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            timeout=120,
        )
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise LauncherError(
            f"MSBuild metadata evaluation failed for {project}: {exc}") from exc
    if result.returncode != 0:
        detail = (result.stderr or result.stdout).strip()
        raise LauncherError(
            f"MSBuild metadata evaluation failed for {project} "
            f"(exit {result.returncode}): {detail}")
    try:
        document = json.loads(result.stdout)
        properties = document["Properties"]
    except (json.JSONDecodeError, KeyError, TypeError) as exc:
        raise LauncherError(
            f"MSBuild metadata output was not valid property JSON for {project}") from exc
    values: dict[str, str] = {}
    for name in ("TargetPath", "TargetFramework", "TargetFrameworks"):
        value = properties.get(name, "")
        if not isinstance(value, str):
            raise LauncherError(f"MSBuild property {name} was not a string for {project}")
        values[name] = value.strip()
    return values


def _validate_runtime_output(
    target_path: Path,
    project_root: Path,
    intended_output: Path,
) -> Path:
    canonical_target = _admit_contained_path(
        target_path, project_root, intended_output, "resolved TargetPath")
    try:
        target_metadata = canonical_target.stat()
    except OSError as exc:
        raise LauncherError(
            f"resolved TargetPath is missing: {canonical_target}") from exc
    if (
        canonical_target.suffix.lower() != ".dll"
        or not stat.S_ISREG(target_metadata.st_mode)
    ):
        raise LauncherError(
            f"resolved TargetPath is not an ordinary DLL: {canonical_target}")
    runtime_config = canonical_target.with_suffix(".runtimeconfig.json")
    canonical_runtime_config = _admit_contained_path(
        runtime_config,
        project_root,
        intended_output,
        "resolved runtimeconfig",
    )
    try:
        runtime_metadata = canonical_runtime_config.stat()
    except OSError as exc:
        raise LauncherError(
            f"resolved selector DLL lacks an ordinary adjacent runtimeconfig: "
            f"{canonical_runtime_config}") from exc
    if not stat.S_ISREG(runtime_metadata.st_mode):
        raise LauncherError(
            f"resolved selector DLL lacks an ordinary adjacent runtimeconfig: "
            f"{canonical_runtime_config}")
    return canonical_target


class _WindowsJob:
    """A kill-on-close Windows job assigned before the gated child can spawn."""

    def __init__(self, process_id: int) -> None:
        import ctypes
        from ctypes import wintypes

        class IoCounters(ctypes.Structure):
            _fields_ = [
                ("ReadOperationCount", ctypes.c_ulonglong),
                ("WriteOperationCount", ctypes.c_ulonglong),
                ("OtherOperationCount", ctypes.c_ulonglong),
                ("ReadTransferCount", ctypes.c_ulonglong),
                ("WriteTransferCount", ctypes.c_ulonglong),
                ("OtherTransferCount", ctypes.c_ulonglong),
            ]

        class BasicLimitInformation(ctypes.Structure):
            _fields_ = [
                ("PerProcessUserTimeLimit", ctypes.c_longlong),
                ("PerJobUserTimeLimit", ctypes.c_longlong),
                ("LimitFlags", wintypes.DWORD),
                ("MinimumWorkingSetSize", ctypes.c_size_t),
                ("MaximumWorkingSetSize", ctypes.c_size_t),
                ("ActiveProcessLimit", wintypes.DWORD),
                ("Affinity", ctypes.c_size_t),
                ("PriorityClass", wintypes.DWORD),
                ("SchedulingClass", wintypes.DWORD),
            ]

        class ExtendedLimitInformation(ctypes.Structure):
            _fields_ = [
                ("BasicLimitInformation", BasicLimitInformation),
                ("IoInfo", IoCounters),
                ("ProcessMemoryLimit", ctypes.c_size_t),
                ("JobMemoryLimit", ctypes.c_size_t),
                ("PeakProcessMemoryUsed", ctypes.c_size_t),
                ("PeakJobMemoryUsed", ctypes.c_size_t),
            ]

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.CreateJobObjectW.argtypes = [ctypes.c_void_p, wintypes.LPCWSTR]
        kernel32.CreateJobObjectW.restype = wintypes.HANDLE
        kernel32.SetInformationJobObject.argtypes = [
            wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        kernel32.SetInformationJobObject.restype = wintypes.BOOL
        kernel32.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.AssignProcessToJobObject.argtypes = [wintypes.HANDLE, wintypes.HANDLE]
        kernel32.AssignProcessToJobObject.restype = wintypes.BOOL
        kernel32.TerminateJobObject.argtypes = [wintypes.HANDLE, wintypes.UINT]
        kernel32.TerminateJobObject.restype = wintypes.BOOL
        kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        kernel32.WaitForSingleObject.restype = wintypes.DWORD
        kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        kernel32.CloseHandle.restype = wintypes.BOOL

        job = kernel32.CreateJobObjectW(None, None)
        if not job:
            raise LauncherError(f"CreateJobObject failed: {ctypes.WinError()}")
        self._kernel32 = kernel32
        self._job = job
        try:
            information = ExtendedLimitInformation()
            information.BasicLimitInformation.LimitFlags = 0x2000
            if not kernel32.SetInformationJobObject(
                job, 9, ctypes.byref(information), ctypes.sizeof(information)
            ):
                raise LauncherError(
                    f"SetInformationJobObject failed: {ctypes.WinError()}")
            process_handle = kernel32.OpenProcess(0x0101, False, process_id)
            if not process_handle:
                raise LauncherError(f"OpenProcess failed: {ctypes.WinError()}")
            try:
                if not kernel32.AssignProcessToJobObject(job, process_handle):
                    raise LauncherError(
                        f"AssignProcessToJobObject failed: {ctypes.WinError()}")
            finally:
                kernel32.CloseHandle(process_handle)
        except Exception:
            kernel32.CloseHandle(job)
            self._job = None
            raise

    def terminate(self) -> None:
        import ctypes

        if self._job is None:
            return
        error: LauncherError | None = None
        if not self._kernel32.TerminateJobObject(self._job, 1):
            error = LauncherError(f"TerminateJobObject failed: {ctypes.WinError()}")
        elif self._kernel32.WaitForSingleObject(self._job, 30_000) != 0:
            error = LauncherError("selector Windows job did not become empty")
        self.close()
        if error is not None:
            raise error

    def close(self) -> None:
        import ctypes

        if self._job is None:
            return
        job = self._job
        self._job = None
        if not self._kernel32.CloseHandle(job):
            raise LauncherError(f"CloseHandle(job) failed: {ctypes.WinError()}")


def _close_process_tree(
    process: subprocess.Popen[bytes],
    job: _WindowsJob | None,
) -> None:
    cleanup_error: LauncherError | None = None
    root_wait_attempted = False
    try:
        if job is not None:
            job.terminate()
        else:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            except OSError as exc:
                raise LauncherError(
                    f"failed to terminate selector process tree "
                    f"{process.pid}: {exc}") from exc
            try:
                root_wait_attempted = True
                process.wait(timeout=30)
            except subprocess.TimeoutExpired as exc:
                raise LauncherError(
                    f"selector process root {process.pid} did not terminate") from exc
            deadline = time.monotonic() + 30
            while True:
                try:
                    os.killpg(process.pid, 0)
                except ProcessLookupError:
                    break
                if time.monotonic() >= deadline:
                    raise LauncherError(
                        f"selector process group {process.pid} did not "
                        f"become empty")
                time.sleep(0.01)
    except LauncherError as exc:
        cleanup_error = exc
    if not root_wait_attempted:
        try:
            process.wait(timeout=30)
        except subprocess.TimeoutExpired as exc:
            raise LauncherError(
                f"selector process tree {process.pid} did not terminate") from exc
    if cleanup_error is not None:
        raise cleanup_error


def _job_child(command: list[str]) -> int:
    if sys.stdin.buffer.read(1) != b"1":
        print("selector job gate closed before launch", file=sys.stderr)
        return 125
    try:
        child = subprocess.Popen(
            command,
            stdin=subprocess.DEVNULL,
            env=os.environ.copy(),
        )
    except OSError as exc:
        print(f"could not start standalone selector: {exc}", file=sys.stderr)
        return 125
    return child.wait()


def _launch_bounded(
    command: list[str],
    timeout_seconds: int,
    scratch_root: Path | None = None,
    environment: dict[str, str] | None = None,
    working_directory: Path | None = None,
    capture_output: bool = False,
) -> tuple[str, str] | None:
    if environment is None:
        raise LauncherError("standalone selector environment is required")
    with (
        tempfile.TemporaryFile(mode="w+b", dir=scratch_root) as stdout_file,
        tempfile.TemporaryFile(mode="w+b", dir=scratch_root) as stderr_file,
    ):
        try:
            job: _WindowsJob | None = None
            if os.name == "nt":
                wrapper = [
                    sys.executable,
                    str(Path(__file__).resolve()),
                    "--job-child",
                    *command,
                ]
                process = subprocess.Popen(
                    wrapper,
                    stdin=subprocess.PIPE,
                    stdout=stdout_file,
                    stderr=stderr_file,
                    env=environment,
                    cwd=working_directory,
                    creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
                )
                try:
                    job = _WindowsJob(process.pid)
                    assert process.stdin is not None
                    process.stdin.write(b"1")
                    process.stdin.close()
                except Exception as launch_error:
                    if process.stdin is not None and not process.stdin.closed:
                        process.stdin.close()
                    cleanup_error: LauncherError | None = None
                    if job is not None:
                        try:
                            job.terminate()
                        except LauncherError as exc:
                            cleanup_error = exc
                    try:
                        process.wait(timeout=30)
                    except subprocess.TimeoutExpired as exc:
                        raise LauncherError(
                            f"gated selector wrapper {process.pid} did not "
                            f"terminate") from exc
                    if cleanup_error is not None:
                        raise cleanup_error from launch_error
                    raise launch_error
            else:
                process = subprocess.Popen(
                    command,
                    stdin=subprocess.DEVNULL,
                    stdout=stdout_file,
                    stderr=stderr_file,
                    env=environment,
                    cwd=working_directory,
                    start_new_session=True,
                )
        except OSError as exc:
            raise LauncherError(f"could not start standalone selector: {exc}") from exc
        timed_out = False
        try:
            process.wait(timeout=timeout_seconds)
        except subprocess.TimeoutExpired:
            timed_out = process.poll() is None
        cleanup_error: LauncherError | None = None
        try:
            _close_process_tree(process, job)
        except LauncherError as exc:
            cleanup_error = exc
        stdout_file.seek(0)
        stderr_file.seek(0)
        stdout = stdout_file.read().decode("utf-8", errors="replace")
        stderr = stderr_file.read().decode("utf-8", errors="replace")
    failed = timed_out or cleanup_error is not None or process.returncode != 0
    if stdout and (not capture_output or failed):
        sys.stdout.write(stdout)
    if stderr and (not capture_output or failed):
        sys.stderr.write(stderr)
    if timed_out:
        message = (
            f"standalone selector timed out after {timeout_seconds} seconds: "
            f"{' '.join(command[1:])}")
        if cleanup_error is not None:
            message += f"; selector cleanup also failed: {cleanup_error}"
            raise LauncherError(message) from cleanup_error
        raise LauncherError(message)
    if cleanup_error is not None:
        raise cleanup_error
    if process.returncode != 0:
        raise LauncherError(
            f"standalone selector failed with exit {process.returncode}: "
            f"{' '.join(command[1:])}")
    return (stdout, stderr) if capture_output else None


def _parse_selector_inventory(
    stdout: str,
    stderr: str,
    project: str,
) -> list[str]:
    if stderr:
        raise LauncherError(
            f"selector inventory wrote unexpected stderr for {project}: "
            f"{stderr.strip()}")
    normalized = stdout.replace("\r\n", "\n")
    if "\r" in normalized or not normalized.endswith("\n") or normalized.count("\n") != 1:
        raise LauncherError(
            f"selector inventory did not emit exactly one JSON line for {project}")
    try:
        selectors = json.loads(normalized[:-1])
    except json.JSONDecodeError as exc:
        raise LauncherError(
            f"selector inventory emitted malformed JSON for {project}") from exc
    if not isinstance(selectors, list) or not all(
        isinstance(selector, str)
        and selector.startswith("--")
        and not any(char.isspace() for char in selector)
        for selector in selectors
    ):
        raise LauncherError(
            f"selector inventory emitted malformed selector values for {project}")
    if len(set(selectors)) != len(selectors):
        raise LauncherError(
            f"selector inventory emitted duplicate selectors for {project}")
    if selectors != sorted(selectors):
        raise LauncherError(
            f"selector inventory is not ordinally sorted for {project}")
    return selectors


def _expected_selectors(
    project: str,
    rows: list[dict[str, Any]],
) -> list[str]:
    selectors: list[str] = []
    for row in rows:
        arguments = row["arguments"]
        if not arguments:
            continue
        selector = arguments[0]
        if selector in selectors:
            raise LauncherError(
                f"standalone selector matrix repeats route {selector} for {project}")
        selectors.append(selector)
    return sorted(selectors)


def _require_selector_inventory_match(
    project: str,
    actual: list[str],
    expected: list[str],
) -> None:
    if actual != expected:
        missing = sorted(set(expected) - set(actual))
        extra = sorted(set(actual) - set(expected))
        raise LauncherError(
            f"selector inventory does not match matrix for {project}; "
            f"missing={missing}, extra={extra}")


def run(
    dotnet: Path,
    project_root: Path,
    matrix_path: Path,
    configuration: str,
    timeout_seconds: int,
    inventory_only: bool = False,
) -> None:
    if timeout_seconds < 1:
        raise LauncherError("selector timeout must be positive")
    if not _ordinary_file(dotnet):
        raise LauncherError(f"dotnet host is not an ordinary file: {dotnet}")
    dotnet = dotnet.resolve(strict=True)
    selector_environment = _pinned_runtime_environment(dotnet)
    root_lexical = _absolute_lexical(project_root)
    project_root = _admit_directory(
        root_lexical, root_lexical, root_lexical, "project root")
    scratch_root = _admit_directory(
        project_root / "artifacts",
        project_root,
        project_root,
        "selector scratch root",
    )
    rows = _load_matrix(matrix_path)
    groups: dict[str, list[dict[str, Any]]] = {}
    for row in rows:
        groups.setdefault(row["project"], []).append(row)

    outputs: dict[str, Path] = {}
    for relative_project, project_rows in sorted(groups.items()):
        project = _admit_contained_path(
            project_root / relative_project,
            project_root,
            project_root,
            "selector project",
        )
        try:
            project_metadata = project.stat()
        except OSError as exc:
            raise LauncherError(
                f"selector project is missing: {project}") from exc
        if project.suffix.lower() != ".csproj" or not stat.S_ISREG(
            project_metadata.st_mode
        ):
            raise LauncherError(f"selector project is not an ordinary file: {project}")
        runnable_rows = [
            row for row in project_rows if row["classification"] == "runnable"]
        overrides = {row.get("targetFramework") for row in runnable_rows}
        if not overrides:
            overrides = {None}
        if len(overrides) != 1:
            raise LauncherError(
                f"selector project has inconsistent targetFramework overrides: {relative_project}")
        override = next(iter(overrides))
        metadata = _evaluate_project(
            dotnet, project, configuration, override)
        frameworks = [
            value.strip()
            for value in metadata["TargetFrameworks"].split(";")
            if value.strip()
        ]
        if len(frameworks) > 1 and override is None:
            raise LauncherError(
                f"selector project has multiple target frameworks and requires an "
                f"explicit targetFramework: {relative_project}")
        if override is not None and metadata["TargetFramework"] != override:
            raise LauncherError(
                f"selector project did not resolve requested targetFramework "
                f"{override}: {relative_project}")
        target_value = metadata["TargetPath"]
        if not target_value:
            raise LauncherError(f"selector project resolved an empty TargetPath: {project}")
        target_path = Path(target_value)
        if not target_path.is_absolute():
            target_path = project.parent / target_path
        intended_output = project.parent / "bin" / configuration
        outputs[relative_project] = _validate_runtime_output(
            target_path, project_root, intended_output)

    for relative_project, project_rows in sorted(groups.items()):
        command = [
            str(dotnet), str(outputs[relative_project]), "--list-selectors"]
        captured = _launch_bounded(
            command,
            min(timeout_seconds, 30),
            scratch_root,
            selector_environment,
            working_directory=project_root,
            capture_output=True,
        )
        if captured is None:
            raise LauncherError(
                f"selector inventory output was not captured for {relative_project}")
        actual = _parse_selector_inventory(
            captured[0], captured[1], relative_project)
        expected = _expected_selectors(relative_project, project_rows)
        _require_selector_inventory_match(relative_project, actual, expected)
        print(
            f"SELECTOR_INVENTORY project={relative_project} "
            f"selectors={len(actual)} status=PASS",
            flush=True,
        )

    if inventory_only:
        return

    for row in rows:
        if row["classification"] != "runnable":
            continue
        command = [str(dotnet), str(outputs[row["project"]]), *row["arguments"]]
        row_timeout_seconds = row.get("timeoutSeconds", timeout_seconds)
        _launch_bounded(
            command,
            row_timeout_seconds,
            scratch_root,
            selector_environment,
            working_directory=project_root,
        )
        print(f"SELECTOR_RESULT id={row['id']} status=PASS", flush=True)


def main() -> int:
    if len(sys.argv) >= 2 and sys.argv[1] == "--job-child":
        return _job_child(sys.argv[2:])
    parser = argparse.ArgumentParser()
    parser.add_argument("--dotnet", required=True, type=Path)
    parser.add_argument("--project-root", required=True, type=Path)
    parser.add_argument("--matrix", required=True, type=Path)
    parser.add_argument("--configuration", choices=("Debug", "Release"), required=True)
    parser.add_argument("--timeout-seconds", type=int, default=120)
    args = parser.parse_args()
    try:
        run(
            args.dotnet.resolve(),
            args.project_root.resolve(),
            args.matrix.resolve(),
            args.configuration,
            args.timeout_seconds,
        )
    except LauncherError as exc:
        print(f"standalone selector launcher failed: {exc}", file=sys.stderr)
        return 1
    print("standalone selector launcher: PASS")
    return 0


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    sys.exit(main())
