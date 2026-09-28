from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
import zipfile
from pathlib import Path, PureWindowsPath
from typing import Any, Callable, Iterable


EXPECTED_COMMIT = "4b097b3d9c7c945a5396c5137bbb9a9beb17ceb8"
EXPECTED_TAG = "npcmanager/preview.224-source"
EXPECTED_PREVIOUS_COMMAND_COUNT = 124
EXPECTED_CURRENT_COMMAND_COUNT = 129
EXPECTED_SOURCE_FILE_COUNT = 1500
EXPECTED_ADDED_COMMANDS = {
    "facegen hair-regions analyze",
    "facegen hair-regions apply",
    "facegen hair-regions preview",
    "facegen hair-regions propose",
    "facegen hair-regions verify",
}
FROZEN_STATUS = "FROZEN_PENDING_CONTROLLED_INTEGRATION"
INTEGRATED_STATUS = "INTEGRATED_DEVELOPMENT_SOURCE_PREVIEW225_DEV"


class AuthorityError(ValueError):
    """A fail-closed authority verification error."""


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_json_object(path: Path, label: str) -> dict[str, object]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise AuthorityError(f"{label} could not be read as JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise AuthorityError(f"{label} must be a JSON object")
    return value


def _hash(value: object) -> str:
    return str(value).strip().casefold()


def _is_forbidden_drive(path: Path) -> bool:
    drive = PureWindowsPath(str(path)).drive.casefold()
    return drive == "f:"


def _safe_relative(root: Path, relative: object, label: str) -> Path:
    if not isinstance(relative, str) or not relative.strip():
        raise AuthorityError(f"{label} must be a non-empty relative path")
    windows = PureWindowsPath(relative)
    if windows.is_absolute() or windows.drive:
        raise AuthorityError(f"{label} must be relative")
    candidate = (root / Path(relative)).resolve()
    resolved_root = root.resolve()
    if candidate != resolved_root and resolved_root not in candidate.parents:
        raise AuthorityError(f"{label} escapes its selected root")
    if _is_forbidden_drive(candidate):
        raise AuthorityError(f"{label} may not target F:")
    return candidate


def _relative_name(value: object, label: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise AuthorityError(f"{label} must be a non-empty relative path")
    windows = PureWindowsPath(value)
    if windows.is_absolute() or windows.drive:
        raise AuthorityError(f"{label} must be relative")
    normalized = Path(value).as_posix()
    if normalized == "." or normalized.startswith("../") or "/../" in normalized:
        raise AuthorityError(f"{label} escapes its selected root")
    return normalized


def validate_declaration(declaration: dict[str, object], phase: str) -> list[str]:
    errors: list[str] = []
    if phase not in {"frozen-package", "pre-integration", "post-integration", "cutover"}:
        errors.append(f"unsupported verification phase: {phase}")
    if declaration.get("schemaVersion") != 1:
        errors.append("declaration schemaVersion must be 1")
    if declaration.get("product") != "NpcManagerReimplementation":
        errors.append("declaration product is not NpcManagerReimplementation")
    if declaration.get("overallState") != "M9_IN_PROGRESS":
        errors.append("overall state must remain M9_IN_PROGRESS")

    package = declaration.get("registeredPackage")
    source = declaration.get("preservedSource")
    development = declaration.get("developmentSource")
    catalogue = declaration.get("catalogue")
    baseline = declaration.get("verificationBaseline")
    authority = declaration.get("authority")
    objects = {
        "registered package": package,
        "preserved source": source,
        "development source": development,
        "catalogue": catalogue,
        "verification baseline": baseline,
        "authority": authority,
    }
    for label, value in objects.items():
        if not isinstance(value, dict):
            errors.append(f"{label} must be an object")
    if not all(isinstance(value, dict) for value in objects.values()):
        return errors

    assert isinstance(package, dict)
    assert isinstance(source, dict)
    assert isinstance(development, dict)
    assert isinstance(catalogue, dict)
    assert isinstance(baseline, dict)
    assert isinstance(authority, dict)

    if package.get("version") != "1.0.0-preview.224":
        errors.append("registered package must remain preview.224")
    if package.get("internalVersion") != "0.1.0-m1":
        errors.append("registered package internal version changed")
    if package.get("registered") is not True:
        errors.append("registered package must remain registered")
    if source.get("commit") != EXPECTED_COMMIT:
        errors.append("preserved source commit is not the frozen implementation")
    if source.get("tag") != EXPECTED_TAG:
        errors.append("preserved source tag is not the immutable preview.224 tag")
    if source.get("snapshotFileCount") != EXPECTED_SOURCE_FILE_COUNT:
        errors.append("source snapshot count must be exactly 1500")
    if source.get("snapshotMismatchCount") != 0:
        errors.append("source snapshot mismatch count must be zero")

    expected_status = FROZEN_STATUS if phase in {"frozen-package", "pre-integration"} else INTEGRATED_STATUS
    if source.get("status") != expected_status:
        errors.append(f"source status must be {expected_status} for {phase}")
    if development.get("version") != "preview.225-dev":
        errors.append("development source must be preview.225-dev")
    if development.get("registered") is not False:
        errors.append("development source must remain unregistered")
    if phase in {"post-integration", "cutover"}:
        if development.get("status") != INTEGRATED_STATUS:
            errors.append("development source status is not integrated")
        if not isinstance(development.get("commit"), str) or not development.get("commit"):
            errors.append("integrated development source must name a commit")
    elif development.get("status") not in {"NOT_YET_INTEGRATED", INTEGRATED_STATUS}:
        errors.append("pre-integration development source status is invalid")

    if catalogue.get("previousUniqueCommands") != EXPECTED_PREVIOUS_COMMAND_COUNT:
        errors.append("catalogue previous command count must be 124")
    if catalogue.get("registeredUniqueCommands") != EXPECTED_CURRENT_COMMAND_COUNT:
        errors.append("catalogue current command count must be 129")
    additions = catalogue.get("exactAddedCommands")
    if not isinstance(additions, list) or set(additions) != EXPECTED_ADDED_COMMANDS:
        errors.append("catalogue exact addition set is not the five hair-region phases")

    if baseline.get("suiteCount") != 12 or baseline.get("declaredTestCount") != 516:
        errors.append("verification baseline must remain 12 suites and 516 tests")
    if authority.get("staticGuiCoverage") != "26/26":
        errors.append("static GUI coverage must remain 26/26")
    if authority.get("canonicalJourneys") != "2/4":
        errors.append("canonical journeys must remain 2/4")
    if authority.get("runtimeReleaseClaim") is not False:
        errors.append("runtime release authority must remain false")
    if authority.get("blanketVisualAuthority") is not False:
        errors.append("blanket visual authority must remain false")
    return errors


def _parse_hash_manifest(path: Path) -> dict[str, str]:
    result: dict[str, str] = {}
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeError) as exc:
        raise AuthorityError(f"hash manifest could not be read: {exc}") from exc
    for line_number, line in enumerate(lines, 1):
        if not line.strip():
            continue
        parts = line.split(maxsplit=1)
        if len(parts) != 2:
            raise AuthorityError(f"hash manifest line {line_number} is malformed")
        digest, relative = parts
        name = _relative_name(relative, f"hash manifest line {line_number}")
        if name in result:
            raise AuthorityError(f"hash manifest repeats {name}")
        result[name] = _hash(digest)
    return result


def verify_package_inventory(artifact_root: Path, package: dict[str, object]) -> list[str]:
    errors: list[str] = []
    try:
        expanded = _safe_relative(artifact_root, package.get("expandedRoot"), "package expandedRoot")
        archive = _safe_relative(artifact_root, package.get("archive"), "package archive")
        manifest_path = _safe_relative(
            artifact_root,
            package.get("packageManifest", f"{package.get('expandedRoot')}/package-manifest.json"),
            "package manifest",
        )
        hash_path = _safe_relative(
            artifact_root,
            package.get("hashManifest", f"{package.get('expandedRoot')}/package.hashes.sha256"),
            "hash manifest",
        )
    except AuthorityError as exc:
        return [str(exc)]

    try:
        manifest = load_json_object(manifest_path, "package manifest")
        entries = manifest.get("files")
        if not isinstance(entries, list):
            return ["package manifest files must be an array"]
        expected_count = package.get("manifestedFileCount")
        if len(entries) != expected_count:
            errors.append(f"manifest inventory count is {len(entries)}, expected {expected_count}")
        manifest_map: dict[str, str] = {}
        for index, entry in enumerate(entries):
            if not isinstance(entry, dict):
                errors.append(f"manifest entry {index} is not an object")
                continue
            try:
                name = _relative_name(entry.get("path"), f"manifest entry {index} path")
            except AuthorityError as exc:
                errors.append(str(exc))
                continue
            if name in manifest_map:
                errors.append(f"manifest repeats {name}")
                continue
            declared_hash = _hash(entry.get("sha256"))
            manifest_map[name] = declared_hash
            target = expanded / Path(name)
            if not target.is_file():
                errors.append(f"manifest file is missing: {name}")
                continue
            declared_bytes = entry.get("bytes")
            actual_bytes = target.stat().st_size
            if actual_bytes != declared_bytes:
                errors.append(f"manifest byte mismatch for {name}: {actual_bytes} != {declared_bytes}")
            actual_hash = sha256_file(target)
            if actual_hash != declared_hash:
                errors.append(f"manifest hash mismatch for {name}")

        try:
            hash_map = _parse_hash_manifest(hash_path)
            if hash_map != manifest_map:
                errors.append("package.hashes.sha256 does not exactly match package manifest")
        except AuthorityError as exc:
            errors.append(str(exc))

        if package.get("packageManifestSha256") and _hash(package["packageManifestSha256"]) != sha256_file(manifest_path):
            errors.append("package manifest hash mismatch")
        if package.get("hashManifestSha256") and _hash(package["hashManifestSha256"]) != sha256_file(hash_path):
            errors.append("hash manifest hash mismatch")
        if not archive.is_file():
            errors.append("package archive is missing")
            return errors
        archive_hash = sha256_file(archive)
        if _hash(package.get("archiveSha256")) != archive_hash:
            errors.append("archive hash mismatch")
        expected_members = set(manifest_map) | {"package-manifest.json", "package.hashes.sha256"}
        try:
            with zipfile.ZipFile(archive) as handle:
                actual_members = set(handle.namelist())
                if actual_members != expected_members:
                    errors.append("archive member inventory mismatch")
                for name in expected_members:
                    if name not in actual_members:
                        continue
                    if name in manifest_map:
                        expected_bytes = (expanded / Path(name)).read_bytes()
                    elif name == "package-manifest.json":
                        expected_bytes = manifest_path.read_bytes()
                    else:
                        expected_bytes = hash_path.read_bytes()
                    if handle.read(name) != expected_bytes:
                        errors.append(f"archive bytes mismatch for {name}")
        except (OSError, zipfile.BadZipFile) as exc:
            errors.append(f"archive could not be inspected: {exc}")
    except AuthorityError as exc:
        errors.append(str(exc))
    return errors


def _git_args(repo_root: Path, *args: str) -> list[str]:
    return ["git", "-c", f"safe.directory={repo_root}", "-C", str(repo_root), *args]


def _git_snapshot(repo_root: Path, commit: str, allowed_paths: set[str] | None = None) -> dict[str, bytes]:
    listing = subprocess.run(
        _git_args(repo_root, "ls-tree", "-r", "-z", commit),
        check=True,
        capture_output=True,
    ).stdout
    project_objects: dict[str, str] = {}
    workspace_objects: dict[str, str] = {}
    prefix = b"projects/NpcManagerReimplementation/"
    for record in listing.split(b"\0"):
        if not record:
            continue
        header, raw_path = record.split(b"\t", 1)
        fields = header.split()
        if len(fields) != 3 or fields[1] != b"blob":
            continue
        object_id = fields[2].decode("ascii")
        if raw_path.startswith(prefix):
            project_objects[raw_path[len(prefix):].decode("utf-8")] = object_id
        else:
            workspace_objects[raw_path.decode("utf-8")] = object_id
    objects: dict[str, str] = {}
    candidate_paths = allowed_paths if allowed_paths is not None else set(project_objects) | set(workspace_objects)
    for relative in candidate_paths:
        if relative in project_objects:
            objects[relative] = project_objects[relative]
        elif relative in workspace_objects:
            objects[relative] = workspace_objects[relative]
    if not objects:
        raise AuthorityError("frozen Git source tree is empty")
    request = ("\n".join(objects.values()) + "\n").encode("ascii")
    result = subprocess.run(
        _git_args(repo_root, "cat-file", "--batch"),
        input=request,
        check=True,
        capture_output=True,
    ).stdout
    output: dict[str, bytes] = {}
    cursor = 0
    for relative, object_id in objects.items():
        end = result.find(b"\n", cursor)
        if end < 0:
            raise AuthorityError("git cat-file batch response is truncated")
        header = result[cursor:end].split()
        cursor = end + 1
        if len(header) != 3 or header[0].decode("ascii") != object_id or header[1] != b"blob":
            raise AuthorityError(f"git cat-file did not return blob {relative}")
        size = int(header[2])
        content = result[cursor : cursor + size]
        if len(content) != size or result[cursor + size : cursor + size + 1] != b"\n":
            raise AuthorityError(f"git cat-file content is truncated for {relative}")
        output[relative] = content
        cursor += size + 1
    return output


def _live_snapshot(repo_root: Path) -> dict[str, bytes]:
    project = repo_root / "projects" / "NpcManagerReimplementation"
    return {
        path.relative_to(project).as_posix(): path.read_bytes()
        for path in project.rglob("*")
        if path.is_file()
    }


def _canonical_source_bytes(relative: str, content: bytes) -> bytes:
    """Compare source text independently of Windows checkout line endings."""
    text_extensions = {
        ".cs", ".csproj", ".json", ".md", ".py", ".ps1", ".sln", ".targets",
        ".props", ".xml", ".xaml", ".xsd", ".txt", ".yml", ".yaml", ".toml", ".ini", ".sh", ".cmd", ".jslot",
    }
    name = Path(relative).name.casefold()
    if b"\x00" in content or (Path(relative).suffix.casefold() not in text_extensions and name not in {".editorconfig", ".gitattributes", ".gitignore"}):
        return content
    return content.replace(b"\r\n", b"\n")


def verify_source_snapshot(repo_root: Path, artifact_root: Path, source: dict[str, object]) -> list[str]:
    errors: list[str] = []
    try:
        snapshot_root = _safe_relative(artifact_root, source.get("snapshotRoot"), "source snapshotRoot")
        package_files = {
            path.relative_to(snapshot_root).as_posix(): path.read_bytes()
            for path in snapshot_root.rglob("*")
            if path.is_file()
        }
        declared_count = source.get("snapshotFileCount")
        if len(package_files) != declared_count:
            errors.append(f"source snapshot count is {len(package_files)}, expected {declared_count}")
        if len(package_files) != EXPECTED_SOURCE_FILE_COUNT:
            errors.append(f"source snapshot count must be exactly {EXPECTED_SOURCE_FILE_COUNT}")
        if (repo_root / ".git").exists():
            try:
                frozen_files = _git_snapshot(
                    repo_root,
                    str(source.get("commit")),
                    set(package_files),
                )
            except (OSError, subprocess.CalledProcessError, AuthorityError, ValueError) as exc:
                errors.append(f"source Git snapshot could not be read: {exc}")
                frozen_files = {}
        else:
            frozen_files = _live_snapshot(repo_root)
        mismatches: list[str] = []
        if set(package_files) != set(frozen_files):
            mismatches.extend(sorted((set(package_files) ^ set(frozen_files)))[:10])
        for relative in sorted(set(package_files) & set(frozen_files)):
            if _canonical_source_bytes(relative, package_files[relative]) != _canonical_source_bytes(relative, frozen_files[relative]):
                mismatches.append(relative)
        if mismatches:
            errors.append(f"source snapshot mismatch ({len(mismatches)}): {', '.join(mismatches[:10])}")
        if source.get("snapshotMismatchCount") != 0:
            errors.append("declared source snapshot mismatch count is not zero")
    except (OSError, AuthorityError) as exc:
        errors.append(str(exc))
    return errors


def _command_names(capabilities: dict[str, object]) -> list[str]:
    raw = capabilities.get("commands")
    if not isinstance(raw, list):
        raise AuthorityError("capability response has no commands array")
    names: list[str] = []
    for item in raw:
        if isinstance(item, str):
            names.append(item)
        elif isinstance(item, dict) and isinstance(item.get("name"), str):
            names.append(item["name"])
        else:
            raise AuthorityError("capability response contains an invalid command")
    return names


def verify_catalogue(previous: dict[str, object], current: dict[str, object]) -> list[str]:
    errors: list[str] = []
    try:
        previous_names = _command_names(previous)
        current_names = _command_names(current)
    except AuthorityError as exc:
        return [f"catalogue: {exc}"]
    previous_set = set(previous_names)
    current_set = set(current_names)
    if len(previous_names) != len(previous_set) or len(current_names) != len(current_set):
        errors.append("catalogue command names must be unique")
    if len(previous_set) != EXPECTED_PREVIOUS_COMMAND_COUNT:
        errors.append(f"catalogue previous command count is {len(previous_set)}, expected 124")
    if len(current_set) != EXPECTED_CURRENT_COMMAND_COUNT:
        errors.append(f"catalogue current command count is {len(current_set)}, expected 129")
    missing = previous_set - current_set
    if missing:
        errors.append(f"catalogue missing preview.223 commands: {sorted(missing)[:10]}")
    additions = current_set - previous_set
    if additions != EXPECTED_ADDED_COMMANDS:
        errors.append(f"catalogue addition set is not exact: {sorted(additions)}")
    for required in ("npc create-from-jslot", "preview npc"):
        if required not in current_set:
            errors.append(f"catalogue missing required command: {required}")
    return errors


def verify_metadata(repo_root: Path, declaration: dict[str, object], phase: str) -> list[str]:
    del repo_root
    return validate_declaration(declaration, phase)


def write_new_report(path: Path, payload: dict[str, object]) -> None:
    if path.exists():
        raise AuthorityError(f"report already exists: {path}")
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def resolve_git_ref(repo_root: Path, ref: str) -> str:
    if ref.startswith("ancestor:"):
        commit = ref.split(":", 1)[1]
        result = subprocess.run(
            _git_args(repo_root, "merge-base", "--is-ancestor", commit, "HEAD"),
            capture_output=True,
        )
        return "true" if result.returncode == 0 else "false"
    result = subprocess.run(
        _git_args(repo_root, "rev-parse", f"{ref}^{{commit}}"),
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


def read_capabilities(path: Path) -> dict[str, object]:
    if not path.is_file():
        raise AuthorityError(f"capability binary is missing: {path}")
    result = subprocess.run(
        [str(path), "capabilities", "--json"],
        cwd=path.parent,
        check=False,
        capture_output=True,
        text=True,
    )
    try:
        value = json.loads(result.stdout)
    except json.JSONDecodeError as exc:
        raise AuthorityError(f"capability response is not JSON: {exc}") from exc
    if not isinstance(value, dict):
        raise AuthorityError("capability response is not an object")
    if result.returncode != 0:
        raise AuthorityError(f"capability command exited {result.returncode}")
    return value


def _check(checks: list[dict[str, object]], check_id: str, expected: object, actual: object, errors: list[str]) -> None:
    passed = not errors
    checks.append({
        "id": check_id,
        "outcome": "PASS" if passed else "FAIL",
        "expected": expected,
        "actual": actual,
    })


def verify_authority(
    repo_root: Path,
    artifact_root: Path,
    declaration: dict[str, object],
    phase: str,
    *,
    git_ref_resolver: Callable[[str], str] | None = None,
    capability_reader: Callable[[Path], dict[str, object]] | None = None,
) -> dict[str, object]:
    checks: list[dict[str, object]] = []
    errors: list[str] = []
    observed = {
        "sourceRoot": str(repo_root.resolve()),
        "artifactRoot": str(artifact_root.resolve()),
    }
    if _is_forbidden_drive(artifact_root):
        errors.append("artifact root on F: is forbidden; F:\\ExampleGame is look-only")
        _check(checks, "artifact-root-boundary", "K-local artifact root", str(artifact_root), errors)
        return {"schemaVersion": 1, "phase": phase, "outcome": "FAIL", "checks": checks, "observed": observed, "errors": errors}

    ref_resolver = git_ref_resolver or (lambda ref: resolve_git_ref(repo_root, ref))
    reader = capability_reader or read_capabilities
    declaration_errors = verify_metadata(repo_root, declaration, phase)
    errors.extend(declaration_errors)
    _check(checks, "declaration-metadata", "bounded preview.224 authority metadata", "valid" if not declaration_errors else declaration_errors, declaration_errors)

    package = declaration.get("registeredPackage")
    source = declaration.get("preservedSource")
    if isinstance(package, dict):
        package_errors = verify_package_inventory(artifact_root, package)
        errors.extend(package_errors)
        _check(checks, "package-inventory", "all manifested package bytes and archive members", "valid" if not package_errors else package_errors, package_errors)
    else:
        package_errors = ["registered package is not an object"]
        errors.extend(package_errors)
    if isinstance(source, dict):
        source_errors = verify_source_snapshot(repo_root, artifact_root, source)
        errors.extend(source_errors)
        _check(checks, "source-snapshot", "1500 files match frozen source commit", "valid" if not source_errors else source_errors, source_errors)
    else:
        source_errors = ["preserved source is not an object"]
        errors.extend(source_errors)

    tag_errors: list[str] = []
    try:
        resolved = ref_resolver(EXPECTED_TAG)
        if resolved != EXPECTED_COMMIT:
            tag_errors.append(f"tag resolves to {resolved}, expected {EXPECTED_COMMIT}")
    except Exception as exc:  # fail closed and preserve the error in the report
        tag_errors.append(f"tag could not be resolved: {exc}")
    errors.extend(tag_errors)
    _check(checks, "source-tag", EXPECTED_COMMIT, "valid" if not tag_errors else tag_errors, tag_errors)

    catalogue_errors: list[str] = []
    if isinstance(package, dict):
        try:
            expanded = _safe_relative(artifact_root, package.get("expandedRoot"), "package expandedRoot")
            previous_root_value = package.get(
                "previousExpandedRoot",
                str(Path(str(package.get("expandedRoot"))).parent / "npcmanager-1.0.0-preview.223"),
            )
            previous_root = _safe_relative(artifact_root, previous_root_value, "previous package root")
            current_cli = expanded / "cli" / "npcm.exe"
            previous_cli = previous_root / "cli" / "npcm.exe"
            development = declaration.get("developmentSource")
            if phase in {"post-integration", "cutover"} and isinstance(development, dict) and development.get("cliPath"):
                current_cli = _safe_relative(repo_root, development.get("cliPath"), "development CLI")
            catalogue_errors = verify_catalogue(reader(previous_cli), reader(current_cli))
        except (AuthorityError, OSError, subprocess.SubprocessError, json.JSONDecodeError) as exc:
            catalogue_errors = [f"catalogue could not be read: {exc}"]
    else:
        catalogue_errors = ["catalogue package root is unavailable"]
    errors.extend(catalogue_errors)
    _check(checks, "command-catalogue", "124 previous commands plus exact five additions", "valid" if not catalogue_errors else catalogue_errors, catalogue_errors)

    development = declaration.get("developmentSource")
    history_errors: list[str] = []
    if phase in {"post-integration", "cutover"} and isinstance(development, dict):
        commit = development.get("commit")
        try:
            if ref_resolver(f"ancestor:{commit}").casefold() != "true":
                history_errors.append("development commit is outside current HEAD history")
        except Exception as exc:
            history_errors.append(f"development commit history could not be verified: {exc}")
    errors.extend(history_errors)
    _check(checks, "development-lineage", "development commit is in HEAD history", "valid" if not history_errors else history_errors, history_errors)

    return {
        "schemaVersion": 1,
        "phase": phase,
        "outcome": "PASS" if not errors else "FAIL",
        "checks": checks,
        "observed": observed,
        "errors": errors,
    }


def _resolve_cli_path(value: str, project_root: Path, repo_root: Path) -> Path:
    path = Path(value)
    if path.is_absolute():
        return path
    candidate = (project_root / path).resolve()
    if candidate.exists():
        return candidate
    return (repo_root / path).resolve()


def main(argv: Iterable[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Verify registered NPC Manager preview.224 authority.")
    parser.add_argument("--declaration", required=True)
    parser.add_argument("--artifact-root", default=None)
    parser.add_argument("--phase", required=True, choices=["frozen-package", "pre-integration", "post-integration", "cutover"])
    parser.add_argument("--output", required=True)
    args = parser.parse_args(list(argv) if argv is not None else None)

    project_root = Path(__file__).resolve().parents[2]
    repo_root = project_root.parents[1]
    declaration_path = _resolve_cli_path(args.declaration, project_root, repo_root)
    output_path = _resolve_cli_path(args.output, project_root, repo_root)
    artifact_root = Path(args.artifact_root).resolve() if args.artifact_root else repo_root
    try:
        declaration = load_json_object(declaration_path, "preservation declaration")
        report = verify_authority(repo_root, artifact_root, declaration, args.phase)
        write_new_report(output_path, report)
    except (AuthorityError, OSError, json.JSONDecodeError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if report["outcome"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
