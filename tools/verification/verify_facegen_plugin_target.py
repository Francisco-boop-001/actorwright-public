"""Independent oracle for a semantic FaceGen plugin-target artifact."""

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

    assert artifact == repeat, "Repeated plugin-target artifact changed"
    assert hashlib.sha256(manifest_bytes).hexdigest().casefold() == artifact["inputTargetManifestSha256"].casefold()
    assert artifact["artifactKind"] == "facegen-plugin-target-semantic-build"
    assert artifact["edition"] == manifest["edition"]
    assert artifact["targetPlugin"] == manifest["targetPlugin"] == "Target.esp"
    entries = artifact["entries"]
    assert artifact["attempted"] == artifact["selected"] == 3
    assert artifact["excluded"] == 1
    assert (artifact["passed"], artifact["skipped"], artifact["failed"]) == (1, 1, 1)
    assert [item["disposition"] for item in entries] == ["selected", "excluded", "selected", "selected"]
    assert [item["status"] for item in entries] == ["passed", "skipped", "skipped", "failed"]
    assert entries[0]["outputRelativePath"] == "FaceGen/Target/0x00000800.json"
    assert entries[2]["outputRelativePath"] == "FaceGen/Target/0x00000802.json"
    assert entries[1]["outputRelativePath"] is None
    assert all(item["outputRelativePath"] is None or item["outputRelativePath"].startswith("FaceGen/Target/")
               for item in entries)
    assert any(item["code"] == "facegen-plugin-target-excluded" for item in entries[1]["diagnostics"])
    assert any(item["code"] == "facegen-manifest-missing" for item in entries[3]["diagnostics"])
    assert response["written"] is True
    assert response["hasFailures"] is True
    assert response["targetPlugin"] == "Target.esp"
    assert response["failed"] == 1
    assert any(item["code"] == "facegen-plugin-target-items-failed" for item in response["diagnostics"])
    print(f"FACEGEN PLUGIN TARGET INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
