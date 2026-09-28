"""Independent oracle for a semantic FaceGen batch artifact."""

import argparse
import hashlib
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--repeat", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()

    manifest_bytes = args.manifest.read_bytes()
    manifest = load(args.manifest)
    artifact = load(args.output)
    repeat = load(args.repeat)
    response = load(args.response)

    assert artifact == repeat, "Repeated semantic FaceGen batch artifact changed"
    assert hashlib.sha256(manifest_bytes).hexdigest().casefold() == artifact["inputBatchSha256"].casefold()
    assert artifact["artifactKind"] == "facegen-batch-semantic-build"
    assert artifact["edition"] == manifest["edition"]
    entries = artifact["entries"]
    assert artifact["attempted"] == len(manifest["manifests"]) == len(entries)
    assert artifact["passed"] == sum(item["status"] == "passed" for item in entries)
    assert artifact["skipped"] == sum(item["status"] == "skipped" for item in entries)
    assert artifact["failed"] == sum(item["status"] == "failed" for item in entries)
    assert [item["status"] for item in entries] == ["passed", "skipped", "failed"]
    assert entries[0]["manifestPath"] == manifest["manifests"][0]
    assert any(item["code"] == "facegen-zero-shapes" for item in entries[1]["diagnostics"])
    assert any(item["severity"] == "error" for item in entries[2]["diagnostics"])
    assert response["written"] is True
    assert response["hasFailures"] is True
    assert response["artifactKind"] == "facegen-batch-semantic-build"
    assert response["failed"] == 1
    assert any(item["code"] == "facegen-batch-items-failed" for item in response["diagnostics"])
    print(f"FACEGEN BATCH INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
