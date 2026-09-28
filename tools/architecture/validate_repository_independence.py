#!/usr/bin/env python3
"""Validate that Actorwright is buildable without its former workspace."""

from __future__ import annotations

from pathlib import Path
import json
import os
import stat
import sys
import xml.etree.ElementTree as ET


RENDERER_SCRIPTS = {
    "export_facegeom_nif.py",
    "export_preview_nif.py",
    "hair_zap.py",
    "nif_geometry_readback.py",
    "render_npc_preview_bundle.py",
    "render_preview_scene.py",
}
FORBIDDEN_DEPENDENCIES = (
    "K:\\ExampleWorkspace",
    "projects/NpcManagerReimplementation",
    "K:\\ExampleExchange",
    "ExampleMigrationArchive",
    "F:\\ExampleGame",
)
# This exact pure predicate is a negative policy, never an input or dependency.
# Pin its entire member rather than exempting its file, tokens, or comments.
VOICE_EXCLUSION_POLICY = r'''    public static bool IsVoiceExcludedPath(WorkspacePath path) =>
        path.IsUnder(new WorkspacePath(@"F:\")) ||
        path.IsUnder(new WorkspacePath(@"K:\ExampleExchange")) ||
        path.IsUnder(new WorkspacePath(@"K:\ExampleMigrationArchive"));'''
TEXT_FIXTURE_SUFFIXES = {
    ".cs", ".json", ".md", ".props", ".ps1", ".py", ".targets", ".txt", ".xml", ".yaml", ".yml",
}
OFFICIAL_NUGET_SOURCE = "https://api.nuget.org/v3/index.json"
OFFICIAL_NUGET_AUDIT_SOURCES = {
    OFFICIAL_NUGET_SOURCE,
    "https://data.nuget.org/v3/index.json",
}


def _first_project(root: Path, suffix: str) -> Path | None:
    matches = sorted(root.glob(f"src/*{suffix}*/*.csproj"), key=lambda path: str(path).casefold())
    return matches[0] if matches else None


def _assembly_name(project: Path) -> str | None:
    document = ET.parse(project)
    for element in document.getroot().iter():
        if element.tag.rsplit("}", 1)[-1] == "AssemblyName":
            return (element.text or "").strip()
    return None


def _nuget_source_errors(path: Path) -> list[str]:
    if not path.is_file():
        return ["nuget.config must enable the official nuget.org v3 source"]
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as error:
        return [f"invalid nuget.config XML: {error}"]

    sections = {element.tag.rsplit("}", 1)[-1]: element for element in root}
    disabled = {
        element.attrib.get("key", "").casefold()
        for element in sections.get("disabledPackageSources", ())
        if element.tag.rsplit("}", 1)[-1] == "add"
        and element.attrib.get("value", "").strip().casefold() == "true"
    }
    enabled = [
        (element.attrib.get("key", ""), element.attrib.get("value", ""))
        for element in sections.get("packageSources", ())
        if element.tag.rsplit("}", 1)[-1] == "add"
        and element.attrib.get("key", "").casefold() not in disabled
    ]
    normalized_official = OFFICIAL_NUGET_SOURCE.casefold()
    errors = []
    if not any(value.rstrip("/").casefold() == normalized_official for _, value in enabled):
        errors.append("nuget.config must enable the official nuget.org v3 source")
    nonofficial = sorted({value for _, value in enabled
                          if value.rstrip("/").casefold() != normalized_official})
    if nonofficial:
        errors.append("nuget.config must not enable non-official package sources: " + ", ".join(nonofficial))
    audit_sources = [
        element.attrib.get("value", "")
        for element in sections.get("auditSources", ())
        if element.tag.rsplit("}", 1)[-1] == "add"
    ]
    normalized_audit_sources = {source.casefold() for source in OFFICIAL_NUGET_AUDIT_SOURCES}
    nonofficial_audit = sorted({value for value in audit_sources
                                if value.rstrip("/").casefold() not in normalized_audit_sources})
    if nonofficial_audit:
        errors.append("nuget.config must not enable non-official audit sources: " + ", ".join(nonofficial_audit))
    return errors


def _is_within(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def _fixture_reference_errors(root: Path) -> list[str]:
    errors: list[str] = []
    baseline_path = root / "tools/architecture/fixture-path-allowlist.json"
    baseline: dict[str, int] = {}
    try:
        entries = json.loads(baseline_path.read_text(encoding="utf-8")) if baseline_path.is_file() else []
        if not isinstance(entries, list):
            raise ValueError("expected a list of path/occurrences objects")
        for entry in entries:
            if not isinstance(entry, dict) or set(entry) != {"path", "occurrences"}:
                raise ValueError("each entry must contain path and occurrences")
            path, count = entry["path"], entry["occurrences"]
            if (not isinstance(path, str) or not path.startswith("tests/")
                    or Path(path).suffix.lower() not in TEXT_FIXTURE_SUFFIXES
                    or "\\" in path or ".." in path.split("/")
                    or path in baseline or type(count) is not int or count <= 0):
                raise ValueError("entries require unique test source paths and positive occurrence counts")
            baseline[path] = count
    except (OSError, ValueError) as error:
        errors.append(f"invalid workspace fixture baseline: {error}")
        baseline = {}

    retained_count = retained_files = 0
    observed: dict[str, int] = {}
    for directory, directories, filenames in os.walk(root / "tests", followlinks=False):
        # Never enter junctions/symlinks or generated build trees during the source scan.
        directories[:] = [name for name in directories if name not in {"bin", "obj", "artifacts"}
                          and not (getattr((Path(directory) / name).lstat(), "st_file_attributes", 0)
                                   & stat.FILE_ATTRIBUTE_REPARSE_POINT)]
        for name in sorted(filenames):
            path = Path(directory) / name
            if path.suffix.lower() not in TEXT_FIXTURE_SUFFIXES or path.is_symlink() or (
                getattr(path.lstat(), "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
            ):
                continue
            text = path.read_text(encoding="utf-8", errors="replace")
            count = text.replace("\\\\", "\\").replace("\\", "/").casefold().count("k:/exampleworkspace")
            relative = path.relative_to(root).as_posix()
            observed[relative] = count
            allowed = baseline.get(relative, 0)
            if count > allowed:
                errors.append(f"unallowlisted workspace fixture reference: {relative} ({count} occurrences; baseline {allowed})")
            if count and allowed:
                retained_count += min(count, allowed)
                retained_files += 1
    for relative, allowed in baseline.items():
        count = observed.get(relative, 0)
        if count < allowed:
            errors.append(
                f"stale workspace fixture allowlist: {relative} ({count} occurrences; baseline {allowed})"
            )
    if retained_count:
        print(f"WARNING retained workspace fixture references: {retained_count} occurrences in {retained_files} files "
              f"(baseline {sum(baseline.values())} occurrences in {len(baseline)} files); source scan only")
    return errors


def validate_repository(root: Path) -> list[str]:
    root = root.resolve(strict=True)
    errors: list[str] = []
    try:
        tests_root_stat = (root / "tests").lstat()
    except FileNotFoundError:
        pass
    else:
        if stat.S_ISLNK(tests_root_stat.st_mode) or (
            getattr(tests_root_stat, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
        ):
            return ["workspace fixture root must not be a symlink or reparse point: tests"]
    if not (root / "Actorwright.sln").is_file():
        errors.append("missing Actorwright.sln")
    if (root / "NpcManager.sln").exists():
        errors.append("legacy NpcManager.sln remains")

    cli = _first_project(root, "Cli")
    desktop = _first_project(root, "Desktop")
    if cli is None or _assembly_name(cli) != "actorwright":
        errors.append("CLI assembly must be actorwright")
    if desktop is None or _assembly_name(desktop) != "Actorwright.Desktop":
        errors.append("desktop assembly must be Actorwright.Desktop")
    main_window = next(iter(sorted(root.glob("src/*Desktop*/MainWindow.xaml"))), None)
    if main_window is None or 'Title="Actorwright"' not in main_window.read_text(encoding="utf-8"):
        errors.append("desktop title must be Actorwright")

    errors.extend(_nuget_source_errors(root / "nuget.config"))

    for project in sorted((*root.glob("src/**/*.csproj"), *root.glob("tests/**/*.csproj"))):
        try:
            document = ET.parse(project)
        except ET.ParseError as error:
            errors.append(f"invalid project XML {project.relative_to(root)}: {error}")
            continue
        for element in document.getroot().iter():
            kind = element.tag.rsplit("}", 1)[-1]
            if kind not in {"ProjectReference", "Content", "Compile", "EmbeddedResource"}:
                continue
            include = element.attrib.get("Include", "")
            if kind == "Content" and include.lower().endswith(".py"):
                errors.append(
                    f"renderer scripts must be embedded, not Content: {project.relative_to(root)}"
                )
            if not include or "$" in include or any(mark in include for mark in ("*", "?")):
                continue
            resolved = (project.parent / include.replace("\\", "/")).resolve(strict=False)
            if not _is_within(resolved, root):
                errors.append(
                    f"{project.relative_to(root)} {kind} resolves outside repository: {include}"
                )
            elif not resolved.exists():
                errors.append(
                    f"missing static project input: {project.relative_to(root)} {kind} {include}"
                )

    rendering = root / "src/NpcManager.Rendering/NpcManager.Rendering.csproj"
    embedded: set[str] = set()
    if rendering.is_file():
        document = ET.parse(rendering)
        embedded = {
            Path(element.attrib.get("Include", "").replace("\\", "/")).name
            for element in document.getroot().iter()
            if element.tag.rsplit("}", 1)[-1] == "EmbeddedResource"
        }
    if embedded != RENDERER_SCRIPTS:
        errors.append(
            "renderer scripts must be embedded exactly: "
            + ", ".join(sorted(RENDERER_SCRIPTS))
        )
    missing_scripts = sorted(
        script for script in RENDERER_SCRIPTS if not (root / "runtime/rendering" / script).is_file()
    )
    if missing_scripts:
        errors.append("missing renderer sources: " + ", ".join(missing_scripts))

    checked_suffixes = {".ps1", ".py", ".cs", ".csproj", ".props", ".targets", ".yml", ".yaml"}
    scan_roots = [
        root / "src",
        root / "tools/architecture",
        root / "tools/build",
        root / "tools/exchange",
        root / "tools/release",
        root / "tools/scripts",
        root / "ci",
        root / ".github",
    ]
    paths = (
        path
        for scan_root in scan_roots
        if scan_root.exists()
        for path in scan_root.rglob("*")
    )
    for path in paths:
        if not path.is_file() or path.suffix.lower() not in checked_suffixes:
            continue
        relative = path.relative_to(root)
        if any(part in {"artifacts", ".git", ".pytest-tmp"} for part in relative.parts):
            continue
        if relative.as_posix() == "tools/architecture/validate_repository_independence.py":
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        normalized_text = text.replace("\\\\", "\\")
        if relative.as_posix() == "src/NpcManager.Domain/ActorwrightWorkspace.cs":
            if normalized_text.count(VOICE_EXCLUSION_POLICY) != 1:
                errors.append("mandatory pure voice exclusion policy is absent or changed")
            else:
                normalized_text = normalized_text.replace(VOICE_EXCLUSION_POLICY, "")
        for forbidden in FORBIDDEN_DEPENDENCIES:
            if forbidden.casefold() not in normalized_text.casefold():
                continue
            if (
                forbidden == "F:\\ExampleGame"
                and relative.as_posix() == "src/NpcManager.Domain/ActorwrightWorkspace.cs"
                and normalized_text.casefold().count(forbidden.casefold()) == 1
            ):
                continue
            if forbidden.casefold() in normalized_text.casefold():
                errors.append(
                    f"forbidden dependency {forbidden!r} in {relative}"
                )

    workspace_policy = root / "src/NpcManager.Domain/ActorwrightWorkspace.cs"
    if not workspace_policy.is_file() or r'F:\ExampleGame' not in workspace_policy.read_text(
        encoding="utf-8"
    ).replace("\\\\", "\\"):
        errors.append("mandatory F:\\ExampleGame look-only boundary is absent")

    errors.extend(_fixture_reference_errors(root))

    workflow = root / ".github/workflows/build.yml"
    if not workflow.is_file() or "./tools/build/build.ps1" not in workflow.read_text(
        encoding="utf-8"
    ).replace("\\", "/"):
        errors.append("GitHub workflow must invoke ./tools/build/build.ps1 from repository root")
    return errors


def main() -> int:
    root = Path(__file__).resolve().parents[2]
    errors = validate_repository(root)
    if errors:
        print("RESULT FAIL")
        for error in errors:
            print(f"  - {error}")
        return 1
    print("RESULT PASS repository-independent")
    return 0


if __name__ == "__main__":
    sys.exit(main())
