"""Independent oracle for the semantic FaceGen correction artifact."""

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

    manifest = load(args.manifest)
    artifact = load(args.output)
    repeat = load(args.repeat)
    response = load(args.response)
    assert artifact == repeat, "Repeated correction artifact changed"
    assert hashlib.sha256(args.manifest.read_bytes()).hexdigest().casefold() == artifact["inputManifestSha256"].casefold()
    assert artifact["artifactKind"] == "facegen-correction-semantic-build"
    assert artifact["edition"] == manifest["edition"]
    expected = {item["kind"]: item for item in manifest.get("corrections", [])}
    decisions = artifact["corrections"]
    assert len(decisions) == len(expected)
    for decision in decisions:
        # JsonStringEnumConverter emits camelCase enum names; the independent
        # check uses the human-readable before/after and trigger fields.
        matching = next((item for name, item in expected.items()
                         if name.replace("-", "").casefold() == decision["kind"].casefold()), None)
        assert matching is not None
        assert decision["triggered"] == matching["trigger"]
        assert decision["before"] == matching["before"]
        assert decision["after"] == (matching["after"] if matching["trigger"] else matching["before"])
        assert decision["decision"] == ("applied" if matching["trigger"] else "preserved-non-trigger")
    assert response["written"] is True
    assert response["artifactKind"] == "facegen-correction-semantic-build"
    assert all(item["severity"] != "error" for item in response["diagnostics"])
    print(f"FACEGEN CORRECTIONS INDEPENDENT PASS output={args.output}")


if __name__ == "__main__":
    main()
