"""Independent oracle for deterministic LVLI preview-resolution evidence."""

import argparse
import hashlib
import json
from pathlib import Path


def load(path: Path):
    with path.open("r", encoding="utf-8-sig") as handle:
        return json.load(handle)


def check(proposal_path: Path, resolution_path: Path, response_path: Path) -> None:
    proposal = load(proposal_path)
    artifact = load(resolution_path)
    proposal_hash = hashlib.sha256(proposal_path.read_bytes()).hexdigest().upper()
    assert artifact["artifactKind"] == "leveled-list-resolution"
    assert artifact["schemaVersion"] == "1"
    assert artifact["edition"] == "fallout4"
    assert artifact["sourceProposal"].casefold() == str(proposal_path.resolve()).casefold()
    assert artifact["inputSha256"].casefold() == proposal_hash.casefold()
    assert artifact["listFormId"] == proposal["listFormId"] == "0x00000801"
    assert artifact["seed"] == 42
    assert artifact["listSuppressed"] is False
    assert artifact["levelGateApplied"] is False
    assert artifact["resolvedItems"] == [
        "Source.esp|0x00000900",
        "Source.esp|0x00000901",
        "Source.esp|0x00000901",
    ]
    assert [item["item"] for item in artifact["selections"]] == artifact["resolvedItems"]
    assert [item["repeatOrdinal"] for item in artifact["selections"]] == [0, 0, 1]
    assert load(response_path)["written"] is True


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--proposal", required=True, type=Path)
    parser.add_argument("--resolution", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args()
    check(args.proposal, args.resolution, args.response)
    print(f"LEVELED LIST RESOLUTION INDEPENDENT PASS resolution={args.resolution}")


if __name__ == "__main__":
    main()
