import hashlib
import json
import os
import subprocess
import sys
from pathlib import Path

import pytest


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools" / "exchange"))
import publish_candidate  # noqa: E402
import validate_bundle  # noqa: E402
from tools.verification import compatibility_firewall as firewall  # noqa: E402
VALIDATOR = ROOT / "tools" / "exchange" / "validate_bundle.py"
RESPONSE_WRITER = ROOT / "tools" / "exchange" / "new_response.py"
PUBLISHER = ROOT / "tools" / "exchange" / "publish_candidate.py"

def _write_json(path: Path, value: object) -> None:
    path.write_text(json.dumps(value, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")


def _issue() -> dict:
    return {
        "$schema": "urn:actorwright:exchange:v1:issue",
        "version": 1,
        "issueId": "NPCM-20260808-0001",
        "createdUtc": "2026-08-08T12:00:00Z",
        "toolVersion": "1.0.0-preview.233",
        "command": "capabilities --json",
        "requestedOutcome": "Read the command catalogue.",
        "observedBehavior": "The command completed.",
        "expectedBehavior": "The command completes.",
        "classificationHint": "FEATURE_REQUEST",
        "capabilitiesSha256": "A" * 64,
        "sanitizedExecutionResult": {"exitCode": 0, "diagnostics": []},
        "environmentFacts": {"game": "skyrimSpecialEdition", "runtimeAuthority": False},
        "artifacts": [],
    }


def _bundle(tmp_path: Path) -> Path:
    root = tmp_path / "bundle"
    root.mkdir(parents=True)
    payload = root / "issue.json"
    _write_json(payload, _issue())
    raw = payload.read_bytes()
    manifest = {
        "$schema": "urn:actorwright:exchange:v1:bundle-manifest",
        "version": 1,
        "kind": "issue",
        "payload": "issue.json",
        "files": [{"path": "issue.json", "size": len(raw), "sha256": hashlib.sha256(raw).hexdigest().upper()}],
    }
    _write_json(root / "bundle-manifest.json", manifest)
    return root


def _historical_issue(tmp_path: Path, issue_id: str, *, narrative: bool = False,
                      facegeom: bool = False, follower_finish: bool = False,
                      attachment: bool = False) -> Path:
    """Synthetic sanitized copies of the five audited historical shapes."""
    root = tmp_path / issue_id
    issue = root / "issue"
    issue.mkdir(parents=True)
    if follower_finish:
        payload = {
            "$schema": "urn:actorwright:exchange:v1:issue",
            "version": 1,
            "issueId": issue_id,
            "createdUtc": "2026-08-19T14:05:00Z",
            "toolVersion": "1.0.0-preview.251",
            "command": "npc follower-finish analyze --json",
            "requestedOutcome": "Finish the product-owned private-body output.",
            "observedBehavior": "A fixed inventory refused the valid output.",
            "impact": "The product could not finish its own output.",
            "expectedBehavior": "Validate roles and bound relationships.",
            "reproduction": ["Build", "Analyze"],
            "diagnosticsObserved": [{"code": "follower-finish-source-plugin-inventory"}],
            "consumerErrorsAlreadyEliminated": ["archive layout"],
            "documentationGapsEncountered": ["schema unavailable"],
            "environment": {"runtimeAuthority": False},
            "authorityNotClaimed": ["gameRuntimeVerification"],
        }
    elif narrative:
        payload = {
            "$schema": "urn:actorwright:exchange:v1:issue",
            "version": 1,
            "issueId": issue_id,
            "createdUtc": "2026-08-12T21:30:00Z",
            "toolVersion": "1.0.0-preview.242",
            "command": "outfit propose / npc edit-package",
            "classificationHint": "DEFECT",
            "relatedTo": ["NPCM-20260812-0004"],
            "purpose": "Sanitized historical narrative.",
            "whatIsAlreadyWorking": "The accepted path remains evidenced.",
            "items": [{"id": "item1", "status": "open"}],
            "whatWeWillNotDo": "No local byte patching.",
            "environmentFacts": {"runtimeAuthority": False, "visualAuthority": False},
            "artifacts": [],
            "amendedUtc": "2026-08-13T01:15:00Z",
            "amendments": [{"kind": "correction", "text": "Sanitized."}],
            "whatWeDidInstead": "Recorded the measured consequence.",
            "itemStatusAfterAmendment": {"item1": "STILL OPEN"},
        }
    else:
        payload = _issue()
        payload["issueId"] = issue_id
        if attachment:
            payload["artifacts"] = [{
                "path": "evidence.md",
                "sha256": hashlib.sha256(b"bound evidence\n").hexdigest().upper(),
                "size": len(b"bound evidence\n"),
                "provenance": "sanitized historical fixture",
                "license": "CC0-1.0",
                "redistributable": True,
            }]
    payload_path = issue / "issue.json"
    _write_json(payload_path, payload)
    if facegeom:
        (issue / "facegeom-build-geom-nif-evidence.json").write_bytes(
            b'{"evidence":"sanitized-facegeom-bytes"}\n'
        )
        (issue / "reproduction.md").write_bytes(
            b"# Sanitized reproduction\n\nSigned evidence bytes remain opaque.\n"
        )
        files = []
        for path in sorted(issue.iterdir()):
            raw = path.read_bytes()
            files.append({"path": path.name, "size": len(raw),
                          "sha256": hashlib.sha256(raw).hexdigest().upper()})
        manifest = {
            "$schema": "urn:actorwright:exchange:v1:bundle-manifest",
            "version": 1,
            "issueId": issue_id,
            "createdUtc": payload["createdUtc"],
            "files": files,
        }
    else:
        raw = payload_path.read_bytes()
        files = [{"path": "issue.json", "size": len(raw),
                  "sha256": hashlib.sha256(raw).hexdigest().upper()}]
        if attachment:
            evidence = issue / "evidence.md"
            evidence.write_bytes(b"bound evidence\n")
            files.append({"path": "evidence.md", "size": evidence.stat().st_size,
                          "sha256": hashlib.sha256(evidence.read_bytes()).hexdigest().upper()})
        manifest = {
            "$schema": "urn:actorwright:exchange:v1:bundle-manifest",
            "version": 1,
            "kind": "issue",
            "payload": "issue.json",
            "files": files,
        }
    _write_json(issue / "bundle-manifest.json", manifest)
    return root


def _candidate(tmp_path: Path) -> Path:
    root = tmp_path / "candidate"
    root.mkdir(parents=True)
    payload = {
        "$schema": "urn:actorwright:exchange:v1:release-candidate",
        "version": 1,
        "productVersion": "1.0.0-preview.254",
        "sourceCommit": "C" * 40,
        "sourceTag": "v1.0.0-preview.254",
        "capabilitiesSha256": "D" * 64,
        "packageSha256": "E" * 64,
        "sbomSha256": "F" * 64,
        "testSummarySha256": "1" * 64,
        "runtimeAuthority": False,
        "visualAuthority": False,
    }
    payload_path = root / "release-candidate.json"
    _write_json(payload_path, payload)
    raw = payload_path.read_bytes()
    _write_json(root / "bundle-manifest.json", {
        "$schema": "urn:actorwright:exchange:v1:bundle-manifest",
        "version": 1,
        "kind": "release-candidate",
        "payload": "release-candidate.json",
        "files": [{"path": "release-candidate.json", "size": len(raw),
                   "sha256": hashlib.sha256(raw).hexdigest().upper()}],
    })
    return root


def test_shape_valid_candidate_with_wrong_cross_hashes_fails_firewall(
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
    commit = subprocess.run(
        ["git", "rev-parse", "HEAD"], cwd=repository, check=True,
        capture_output=True, text=True,
    ).stdout.strip()
    tree = subprocess.run(
        ["git", "rev-parse", "HEAD^{tree}"], cwd=repository, check=True,
        capture_output=True, text=True,
    ).stdout.strip()
    tag = "v9.9.9-fixture"
    subprocess.run(["git", "tag", "-a", tag, "-m", "fixture"], cwd=repository, check=True)

    release_root = tmp_path / "release-root"
    evidence = release_root / "evidence"
    evidence.mkdir(parents=True)
    commands = [{"name": "version", "readiness": "v2"}]
    capabilities = {"version": "9.9.9-fixture", "sourceLine": "fixture-private", "commands": commands}
    summary = {
        "sourceCommit": commit,
        "sourceTag": tag,
        "status": "PASS",
        "exactCommandNames": 1,
        "standalonePythonCases": 1,
        "standalonePythonSkipped": 0,
        "orderedHelpSha256": hashlib.sha256(
            firewall.canonical_json_bytes(commands)
        ).hexdigest().upper(),
        "protocolReadinessSha256": hashlib.sha256(
            firewall.canonical_json_bytes(commands)
        ).hexdigest().upper(),
        "selectorInventorySha256": hashlib.sha256(
            firewall.canonical_json_bytes(["version"])
        ).hexdigest().upper(),
        "selectorResultsSha256": hashlib.sha256(
            firewall.canonical_json_bytes([{"id": "version", "status": "PASS"}])
        ).hexdigest().upper(),
    }
    for name, value in (
        ("capabilities.json", capabilities),
        ("test-summary.json", summary),
        ("sbom.spdx.json", {
            "spdxVersion": "SPDX-2.3",
            "name": "Actorwright-9.9.9-fixture",
            "documentNamespace": (
                f"https://actorwright.invalid/spdx/9.9.9-fixture/{commit}"
            ),
            "packages": [{"name": "Fixture.Dependency", "versionInfo": "1.0.0"}],
        }),
        ("dependency-vulnerability-report.json", {"status": "PASS"}),
        ("protocol-v2-capabilities.json", {"result": {"commands": commands}}),
            ("protocol-v2-schema-exports.json", {
                "productVersion": "9.9.9-fixture",
                "sourceLine": "fixture-private", "commands": [{"command": "version"}],
            }),
    ):
        _write_json(evidence / name, value)
    (evidence / "canonical-build.log").write_text(
        f"EVIDENCE_SOURCE_COMMIT={commit}\n"
        f"EVIDENCE_SOURCE_TREE={tree}\n"
        "standalone test registry: PASS runnable=1 fixture-bound=0 unverified=0\n"
        "Standalone selector registry: runnable=1 fixture-bound=0 unverified=0\n"
        "SELECTOR_RESULT id=version status=PASS\n"
        "PASS Actorwright help identity\n"
        "1 passed, 0 skipped, 0 warnings in 0.01s\n",
        encoding="utf-8",
    )
    release = {
        "version": "9.9.9-fixture",
        "sourceCommit": commit,
        "sourceTree": tree,
        "sourceTag": tag,
        "capabilitiesSha256": hashlib.sha256((evidence / "capabilities.json").read_bytes()).hexdigest().upper(),
        "sbomSha256": hashlib.sha256((evidence / "sbom.spdx.json").read_bytes()).hexdigest().upper(),
        "testSummarySha256": hashlib.sha256((evidence / "test-summary.json").read_bytes()).hexdigest().upper(),
        "dependencyVulnerabilityReportSha256": hashlib.sha256(
            (evidence / "dependency-vulnerability-report.json").read_bytes()
        ).hexdigest().upper(),
        "canonicalBuildLogSha256": hashlib.sha256(
            (evidence / "canonical-build.log").read_bytes()
        ).hexdigest().upper(),
    }
    _write_json(release_root / "actorwright-release.json", release)
    sums = []
    for path in sorted(release_root.rglob("*")):
        if path.is_file() and path.name != "SHA256SUMS":
            sums.append(
                f"{hashlib.sha256(path.read_bytes()).hexdigest().upper()}  "
                f"{path.relative_to(release_root).as_posix()}\n"
            )
    (release_root / "SHA256SUMS").write_text("".join(sums), encoding="utf-8")
    package = tmp_path / "candidate.zip"
    import zipfile
    with zipfile.ZipFile(package, "w", compression=zipfile.ZIP_STORED) as handle:
        for path in sorted(release_root.rglob("*")):
            if path.is_file():
                handle.write(path, f"fixture/{path.relative_to(release_root).as_posix()}")

    candidate = tmp_path / "candidate"
    candidate.mkdir()
    copied_zip = candidate / package.name
    copied_zip.write_bytes(package.read_bytes())
    copied_release = candidate / "actorwright-release.json"
    copied_release.write_bytes((release_root / "actorwright-release.json").read_bytes())
    payload = {
        "$schema": "urn:actorwright:exchange:v1:release-candidate",
        "version": 1,
        "productVersion": release["version"],
        "sourceCommit": commit,
        "sourceTag": tag,
        "capabilitiesSha256": "A" * 64,
        "packageSha256": "B" * 64,
        "sbomSha256": "C" * 64,
        "testSummarySha256": "D" * 64,
        "runtimeAuthority": False,
        "visualAuthority": False,
    }
    _write_json(candidate / "release-candidate.json", payload)
    files = []
    for path in (copied_zip, copied_release, candidate / "release-candidate.json"):
        files.append({
            "path": path.name,
            "size": path.stat().st_size,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest().upper(),
        })
    _write_json(candidate / "bundle-manifest.json", {
        "$schema": "urn:actorwright:exchange:v1:bundle-manifest",
        "version": 1,
        "kind": "release-candidate",
        "payload": "release-candidate.json",
        "files": files,
    })

    assert _run_kind(candidate, "release-candidate").returncode == 0
    result = firewall.verify_candidate_bundle_compatibility(
        candidate, release_root, package, repository,
    )

    assert result.verdict == "INCOMPATIBLE"
    mismatches = {row.pin_id for row in result.pin_rows if row.status == "FAIL"}
    assert {"candidate-package", "candidate-capabilities", "candidate-sbom", "candidate-tests"} <= mismatches


def _rewrite_payload(root: Path, payload: dict) -> None:
    payload_path = root / "issue.json"
    _write_json(payload_path, payload)
    raw = payload_path.read_bytes()
    manifest_path = root / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["files"][0].update(size=len(raw), sha256=hashlib.sha256(raw).hexdigest().upper())
    _write_json(manifest_path, manifest)


def _rewrite_narrative_payload(root: Path, payload: dict) -> None:
    payload_path = root / "issue" / "issue.json"
    _write_json(payload_path, payload)
    raw = payload_path.read_bytes()
    manifest_path = root / "issue" / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    row = next(row for row in manifest["files"] if row["path"] == "issue.json")
    row.update(size=len(raw), sha256=hashlib.sha256(raw).hexdigest().upper())
    _write_json(manifest_path, manifest)


def _run(root: Path, *extra: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(VALIDATOR), str(root), "--expect-kind", "issue", *extra],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )


def _typed_bundle(tmp_path: Path, kind: str, payload: dict) -> Path:
    root = tmp_path / kind
    root.mkdir()
    payload_name = f"{kind}.json"
    payload_path = root / payload_name
    _write_json(payload_path, payload)
    raw = payload_path.read_bytes()
    _write_json(
        root / "bundle-manifest.json",
        {
            "$schema": "urn:actorwright:exchange:v1:bundle-manifest",
            "version": 1,
            "kind": kind,
            "payload": payload_name,
            "files": [{"path": payload_name, "size": len(raw), "sha256": hashlib.sha256(raw).hexdigest().upper()}],
        },
    )
    return root


def _run_kind(root: Path, kind: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, str(VALIDATOR), str(root), "--expect-kind", kind, "--json"],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )


def test_valid_issue_bundle(tmp_path: Path) -> None:
    result = _run(_bundle(tmp_path), "--json")
    assert result.returncode == 0, result.stdout + result.stderr
    assert json.loads(result.stdout)["status"] == "PASS"


def test_v1_issue_artifact_requires_exact_redistributable_contract(tmp_path: Path) -> None:
    valid = {
        "path": "evidence/result.json", "sha256": "A" * 64, "size": 0,
        "provenance": "sanitized test fixture", "license": "CC0-1.0",
        "redistributable": True,
    }
    root = _bundle(tmp_path / "valid")
    payload = json.loads((root / "issue.json").read_text(encoding="utf-8"))
    payload["artifacts"] = [valid]
    _rewrite_payload(root, payload)
    assert _run(root).returncode == 0

    for mutation in (
        lambda item: item.pop("provenance"),
        lambda item: item.update(extra="refused"),
        lambda item: item.update(redistributable=False),
        lambda item: item.update(size=True),
        lambda item: item.update(sha256=123),
    ):
        case = _bundle(tmp_path / f"invalid-{id(mutation)}")
        invalid = json.loads((case / "issue.json").read_text(encoding="utf-8"))
        item = dict(valid)
        mutation(item)
        invalid["artifacts"] = [item]
        _rewrite_payload(case, invalid)
        result = _run(case)
        assert result.returncode != 0
        message = (result.stdout + result.stderr).lower()
        assert any(token in message for token in ("artifact", "provenance", "unexpected", "redistributable"))


def test_rejects_undeclared_file(tmp_path: Path) -> None:
    root = _bundle(tmp_path)
    (root / "secret.txt").write_text("not declared", encoding="utf-8")
    result = _run(root)
    assert result.returncode != 0
    assert "undeclared" in (result.stdout + result.stderr).lower()


def test_rejects_hash_mismatch(tmp_path: Path) -> None:
    root = _bundle(tmp_path)
    payload = root / "issue.json"
    payload.write_text("{}\n", encoding="utf-8")
    manifest_path = root / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["files"][0]["size"] = len(payload.read_bytes())
    _write_json(manifest_path, manifest)
    result = _run(root)
    assert result.returncode != 0
    assert "hash" in (result.stdout + result.stderr).lower()


def test_rejects_duplicate_json_keys(tmp_path: Path) -> None:
    root = _bundle(tmp_path)
    payload = root / "issue.json"
    payload.write_text('{"$schema":"urn:actorwright:exchange:v1:issue","version":1,"version":1}\n', encoding="utf-8")
    raw = payload.read_bytes()
    manifest_path = root / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["files"][0].update(size=len(raw), sha256=hashlib.sha256(raw).hexdigest().upper())
    _write_json(manifest_path, manifest)
    result = _run(root)
    assert result.returncode != 0
    assert "duplicate" in (result.stdout + result.stderr).lower()


def test_rejects_secret_like_fields(tmp_path: Path) -> None:
    root = _bundle(tmp_path)
    payload = root / "issue.json"
    issue = json.loads(payload.read_text(encoding="utf-8"))
    issue["environmentFacts"]["token"] = "forbidden"
    _write_json(payload, issue)
    raw = payload.read_bytes()
    manifest_path = root / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["files"][0].update(size=len(raw), sha256=hashlib.sha256(raw).hexdigest().upper())
    _write_json(manifest_path, manifest)
    result = _run(root)
    assert result.returncode != 0
    assert "secret" in (result.stdout + result.stderr).lower()


def test_valid_response_candidate_and_promotion(tmp_path: Path) -> None:
    payloads = {
        "response": {
            "$schema": "urn:actorwright:exchange:v1:response",
            "version": 1,
            "issueId": "NPCM-20260808-0001",
            "createdUtc": "2026-08-08T12:30:00Z",
            "classification": "FEATURE_REQUEST",
            "issueManifestSha256": "B" * 64,
            "summary": "The request is admitted for product review.",
            "recommendedAction": "Track it in Actorwright.",
            "artifacts": [],
        },
        "release-candidate": {
            "$schema": "urn:actorwright:exchange:v1:release-candidate",
            "version": 1,
            "productVersion": "1.0.0-preview.233",
            "sourceCommit": "C" * 40,
            "sourceTag": "v1.0.0-preview.233",
            "capabilitiesSha256": "D" * 64,
            "packageSha256": "E" * 64,
            "sbomSha256": "F" * 64,
            "testSummarySha256": "1" * 64,
            "runtimeAuthority": False,
            "visualAuthority": False,
        },
        "promotion": {
            "$schema": "urn:actorwright:exchange:v1:promotion",
            "version": 1,
            "createdUtc": "2026-08-08T13:00:00Z",
            "candidateManifestSha256": "2" * 64,
            "decision": "APPROVED",
            "decidedBy": "human-user",
        },
    }
    for kind, payload in payloads.items():
        result = _run_kind(_typed_bundle(tmp_path, kind, payload), kind)
        assert result.returncode == 0, result.stdout + result.stderr


def test_rejects_path_traversal_and_forbidden_source(tmp_path: Path) -> None:
    root = _bundle(tmp_path)
    manifest_path = root / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    manifest["files"][0]["path"] = "../issue.json"
    _write_json(manifest_path, manifest)
    result = _run(root)
    assert result.returncode != 0
    assert "traversal" in (result.stdout + result.stderr).lower()

    second = _bundle(tmp_path / "second")
    payload = second / "issue.json"
    source = second / "source.py"
    source.write_text("print('no')\n", encoding="utf-8")
    manifest_path = second / "bundle-manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    raw = source.read_bytes()
    manifest["files"].append({"path": "source.py", "size": len(raw), "sha256": hashlib.sha256(raw).hexdigest().upper()})
    _write_json(manifest_path, manifest)
    result = _run(second)
    assert result.returncode != 0
    assert "forbidden" in (result.stdout + result.stderr).lower()


def test_rejects_unbound_response(tmp_path: Path) -> None:
    payload = {
        "$schema": "urn:actorwright:exchange:v1:response",
        "version": 1,
        "issueId": "NPCM-20260808-0001",
        "createdUtc": "2026-08-08T12:30:00Z",
        "classification": "BUG",
        "issueManifestSha256": "not-a-hash",
        "summary": "No binding.",
        "recommendedAction": "Reject.",
        "artifacts": [],
    }
    result = _run_kind(_typed_bundle(tmp_path, "response", payload), "response")
    assert result.returncode != 0
    assert "binding" in (result.stdout + result.stderr).lower()


def test_response_writer_binds_issue_manifest(tmp_path: Path) -> None:
    issue = _bundle(tmp_path / "source")
    candidate = _candidate(tmp_path / "candidate-source")
    output = tmp_path / "response"
    result = subprocess.run(
        [
            sys.executable,
            str(RESPONSE_WRITER),
            "--issue-bundle", str(issue),
            "--candidate-bundle", str(candidate),
            "--output", str(output),
            "--classification", "FEATURE_REQUEST",
            "--summary", "Admitted for review.",
            "--recommended-action", "Track in Actorwright.",
            "--created-utc", "2026-08-08T13:00:00Z",
        ],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    response = json.loads((output / "response.json").read_text(encoding="utf-8"))
    expected = hashlib.sha256((issue / "bundle-manifest.json").read_bytes()).hexdigest().upper()
    assert response["issueManifestSha256"] == expected
    assert response["$schema"] == "actorwright-exchange-response/2"
    assert _run_kind(output, "response").returncode == 0


def test_all_closed_historical_issue_layouts_validate(tmp_path: Path) -> None:
    layouts = [
        _historical_issue(tmp_path, "NPCM-20260812-0005", narrative=True),
        _historical_issue(tmp_path, "NPCM-20260813-0001", narrative=True),
        _historical_issue(tmp_path, "NPCM-20260814-0001", facegeom=True),
        _historical_issue(tmp_path, "NPCM-20260815-0002"),
        _historical_issue(tmp_path, "NPCM-20260815-0003"),
        _historical_issue(tmp_path, "NPCM-20260819-0003", follower_finish=True),
        _historical_issue(tmp_path, "NPCM-20260823-0001", attachment=True),
    ]
    for root in layouts:
        result = _run_kind(root, "issue")
        assert result.returncode == 0, result.stdout + result.stderr
        assert json.loads(result.stdout)["layout"] in {
            "historical-one-file-narrative",
            "historical-one-file-current",
            "historical-follower-finish",
            "historical-standard-wrapper",
            "historical-three-file-facegeom",
        }


def test_historical_narrative_artifacts_use_closed_contract(tmp_path: Path) -> None:
    valid = {
        "path": "evidence/result.json", "sha256": "A" * 64, "size": 0,
        "provenance": "sanitized historical fixture", "license": "CC0-1.0",
        "redistributable": True,
    }
    for index, issue_id in enumerate(("NPCM-20260812-0005", "NPCM-20260813-0001")):
        root = _historical_issue(tmp_path / f"valid-{index}", issue_id, narrative=True)
        payload = json.loads((root / "issue" / "issue.json").read_text(encoding="utf-8"))
        payload["artifacts"] = [valid]
        _rewrite_narrative_payload(root, payload)
        result = _run_kind(root, "issue")
        assert result.returncode == 0, result.stdout + result.stderr

    invalid_rows = (
        ("malformed", ["not-an-artifact"]),
        ("empty", [{}]),
        ("extra", [{**valid, "extra": "refused"}]),
        ("wrong-type", [{**valid, "size": "0"}]),
        ("non-redistributable", [{**valid, "redistributable": False}]),
    )
    for index, (label, rows) in enumerate(invalid_rows):
        root = _historical_issue(tmp_path / f"invalid-{index}-{label}",
                                 f"NPCM-20260812-{index + 10:04d}", narrative=True)
        payload = json.loads((root / "issue" / "issue.json").read_text(encoding="utf-8"))
        payload["artifacts"] = rows
        _rewrite_narrative_payload(root, payload)
        result = _run_kind(root, "issue")
        assert result.returncode != 0
        assert any(word in (result.stdout + result.stderr).lower()
                   for word in ("artifact", "narrative", "redistributable", "unexpected"))


def test_historical_signed_bytes_are_not_normalized(tmp_path: Path) -> None:
    root = _historical_issue(tmp_path, "NPCM-20260814-0001", facegeom=True)
    evidence = root / "issue" / "reproduction.md"
    evidence.write_bytes(b"# Sanitized reproduction\r\n\r\nSigned evidence bytes remain opaque.\r\n")
    result = _run_kind(root, "issue")
    assert result.returncode != 0
    assert any(word in (result.stdout + result.stderr).lower() for word in ("hash", "size"))


def test_response_writer_emits_v2_and_binds_both_manifests(tmp_path: Path) -> None:
    issue = _historical_issue(tmp_path, "NPCM-20260815-0002")
    candidate = _candidate(tmp_path)
    output = tmp_path / "response-v2"
    result = subprocess.run(
        [sys.executable, str(RESPONSE_WRITER), "--issue-bundle", str(issue),
         "--candidate-bundle", str(candidate), "--output", str(output),
         "--classification", "BUG", "--summary", "Fixed.",
         "--recommended-action", "Use the candidate.",
         "--created-utc", "2026-08-17T12:00:00Z"],
        cwd=ROOT, text=True, capture_output=True, check=False,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    response = json.loads((output / "response.json").read_text(encoding="utf-8"))
    assert response["$schema"] == "actorwright-exchange-response/2"
    assert response["version"] == 2
    assert response["issueManifestSha256"] == hashlib.sha256(
        (issue / "issue" / "bundle-manifest.json").read_bytes()).hexdigest().upper()
    assert response["candidateManifestSha256"] == hashlib.sha256(
        (candidate / "bundle-manifest.json").read_bytes()).hexdigest().upper()
    assert _run_kind(output, "response").returncode == 0


def test_response_v2_secret_artifact_is_refused_before_publication(tmp_path: Path) -> None:
    issue = _historical_issue(tmp_path, "NPCM-20260815-0002")
    candidate = _candidate(tmp_path)
    artifact_rows = tmp_path / "artifacts.json"
    _write_json(artifact_rows, [{"path": "evidence.json", "token": "must-not-cross"}])
    output = tmp_path / "response-secret"
    result = subprocess.run(
        [sys.executable, str(RESPONSE_WRITER), "--issue-bundle", str(issue),
         "--candidate-bundle", str(candidate), "--output", str(output),
         "--classification", "BUG", "--summary", "No secret.",
         "--recommended-action", "Reject.", "--created-utc", "2026-08-17T12:00:00Z",
         "--artifacts-json", str(artifact_rows)],
        cwd=ROOT, text=True, capture_output=True, check=False,
    )
    assert result.returncode != 0
    assert "secret" in (result.stdout + result.stderr).lower()
    assert not output.exists()


def test_rejects_injected_alternate_data_stream(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    root = _bundle(tmp_path)
    monkeypatch.setattr(validate_bundle, "_enumerate_named_streams",
                        lambda path: [":review:$DATA"])
    with pytest.raises(validate_bundle.BundleError, match="alternate data stream"):
        validate_bundle.validate(root, "issue")


def test_rejects_real_ntfs_alternate_data_stream_when_supported(
    tmp_path: Path,
    require_ntfs_fixture,
    filesystem_fixture_unavailable,
) -> None:
    filesystem = require_ntfs_fixture(tmp_path, "alternate data stream")
    root = _bundle(tmp_path)
    stream = Path(str(root / "issue.json") + ":review")
    try:
        stream.write_bytes(b"signed bytes must remain outside the bundle")
    except OSError as exc:
        filesystem_fixture_unavailable(tmp_path, filesystem, "alternate data stream", exc)
    try:
        with pytest.raises(validate_bundle.BundleError, match="alternate data stream"):
            validate_bundle.validate(root, "issue")
    finally:
        try:
            stream.unlink()
        except OSError as exc:
            filesystem_fixture_unavailable(
                tmp_path, filesystem, "alternate data stream cleanup", exc
            )


def test_response_writer_rejects_invalid_candidate_without_output(tmp_path: Path) -> None:
    issue = _historical_issue(tmp_path, "NPCM-20260815-0003")
    candidate = _candidate(tmp_path)
    (candidate / "release-candidate.json").write_text("tampered\n", encoding="utf-8")
    output = tmp_path / "response-invalid"
    result = subprocess.run(
        [sys.executable, str(RESPONSE_WRITER), "--issue-bundle", str(issue),
         "--candidate-bundle", str(candidate), "--output", str(output),
         "--classification", "BUG", "--summary", "No.",
         "--recommended-action", "Reject.", "--created-utc", "2026-08-17T12:00:00Z"],
        cwd=ROOT, text=True, capture_output=True, check=False,
    )
    assert result.returncode != 0
    assert not output.exists()


def test_candidate_and_promotion_publisher_basic_no_overwrite(tmp_path: Path) -> None:
    source = _candidate(tmp_path)
    output = tmp_path / "published"
    result = subprocess.run(
        [sys.executable, str(PUBLISHER), "--source", str(source), "--output", str(output)],
        cwd=ROOT, text=True, capture_output=True, check=False,
    )
    assert result.returncode == 0, result.stdout + result.stderr
    assert output.is_dir()
    assert (output / "bundle-manifest.json").read_bytes() == (source / "bundle-manifest.json").read_bytes()

    promotion_parent = tmp_path / "promotion-source"
    promotion_parent.mkdir()
    promotion_source = _typed_bundle(promotion_parent, "promotion", {
        "$schema": "urn:actorwright:exchange:v1:promotion",
        "version": 1,
        "createdUtc": "2026-09-05T12:00:00Z",
        "candidateManifestSha256": "A" * 64,
        "decision": "APPROVED",
        "decidedBy": "Synthetic reviewer",
    })
    promotion_output = tmp_path / "published-promotion"
    promotion_result = subprocess.run(
        [sys.executable, str(PUBLISHER),
         "--source", str(promotion_source),
         "--output", str(promotion_output),
         "--expect-kind", "promotion"],
        cwd=ROOT, text=True, capture_output=True, check=False,
    )
    assert promotion_result.returncode == 0, (
        promotion_result.stdout + promotion_result.stderr)
    assert (promotion_output / "bundle-manifest.json").read_bytes() == (
        promotion_source / "bundle-manifest.json").read_bytes()
    assert (promotion_output / "promotion.json").read_bytes() == (
        promotion_source / "promotion.json").read_bytes()

    issue_source = _bundle(tmp_path / "issue-source")
    response_parent = tmp_path / "response-source"
    response_parent.mkdir()
    response_source = _typed_bundle(response_parent, "response", {
        "$schema": "urn:actorwright:exchange:v1:response",
        "version": 1,
        "issueId": "NPCM-20260808-0001",
        "createdUtc": "2026-09-05T12:00:00Z",
        "classification": "FEATURE_REQUEST",
        "issueManifestSha256": "B" * 64,
        "summary": "The request is admitted for product review.",
        "recommendedAction": "Track it in Actorwright.",
        "artifacts": [],
    })
    for kind, forbidden_source in (
            ("issue", issue_source), ("response", response_source)):
        forbidden_output = tmp_path / f"published-{kind}"
        with pytest.raises(
                publish_candidate.PublicationError,
                match="unsupported publication kind"):
            publish_candidate.publish(
                forbidden_source, forbidden_output, expected_kind=kind)
        assert not forbidden_output.exists()
