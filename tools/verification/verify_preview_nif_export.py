"""Independent oracle for the sandboxed semantic preview-NIF export plan."""

import argparse
import hashlib
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--scene", required=True, type=Path)
    parser.add_argument("--plan", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()

    scene = load(args.scene)
    plan = load(args.plan)
    response = load(args.response)
    scene_bytes = args.scene.read_bytes()
    assert plan["artifactKind"] == "preview-nif-export-plan"
    assert plan["schemaVersion"] == "1"
    assert plan["targetFormat"] == "nif"
    assert plan["inputSceneSha256"].casefold() == hashlib.sha256(scene_bytes).hexdigest()
    assert plan["sourceScenePath"].casefold() == str(args.scene.resolve()).casefold()
    included = [item for item in scene["assets"] if item.get("included", True)]
    assert plan["assets"] == [
        {
            "category": item["category"],
            "path": item["path"],
            "provider": item["provider"],
            "sha256": item["sha256"],
        }
        for item in included
    ]
    assert response["written"] is True
    assert response["artifactKind"] == "preview-nif-export-plan"
    assert response["targetFormat"] == "nif"
    assert response["assetCount"] == len(included)
    assert response["inputSceneSha256"].casefold() == plan["inputSceneSha256"].casefold()
    assert all(item["severity"] != "error" for item in response["diagnostics"])
    print(f"PREVIEW NIF EXPORT INDEPENDENT PASS plan={args.plan}")


if __name__ == "__main__":
    main()
