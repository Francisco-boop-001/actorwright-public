"""Independent semantic oracle for the bounded body-overlay layer resolver."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--game", required=True, choices=("fallout4", "skyrimse"))
    args = parser.parse_args()
    layers = json.loads(args.input.read_text(encoding="utf-8"))
    response = json.loads(args.response.read_text(encoding="utf-8-sig"))
    assert response["isValid"], response
    assert response["game"] == args.game
    assert response["sourceSha256"] == hashlib.sha256(args.input.read_bytes()).hexdigest()
    output = response["layers"]
    assert len(output) == len(layers)
    canonical_layers = []
    for actual in output:
        canonical_layers.append({
            "order": actual["order"],
            "sourceIndex": actual["sourceIndex"],
            "priority": actual["priority"],
            "template": actual["template"],
            "slots": actual["slots"],
            "target": actual["target"],
            "nodeIndex": actual["nodeIndex"],
            "node": actual["node"],
            "diffuse": actual["diffuse"],
            "normal": actual["normal"],
            "tint": actual["tint"],
            "offsetUV": actual["offsetUv"],
            "scaleUV": actual["scaleUv"],
            "alpha": actual["alpha"],
        })
    canonical = {"schemaVersion": 1, "game": args.game,
                 "npcFormId": response["npcFormId"], "layers": canonical_layers}
    encoded = json.dumps(canonical, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    assert response["canonicalSha256"] == hashlib.sha256(encoded).hexdigest()

    if args.game == "fallout4":
        expected = sorted(enumerate(layers), key=lambda item: (item[1].get("priority", 0), item[0]))
        for order, (source_index, layer) in enumerate(expected):
            actual = output[order]
            assert actual["order"] == order and actual["sourceIndex"] == source_index
            assert actual["template"] == layer["template"]
            assert actual["priority"] == layer.get("priority", 0)
            assert actual["slots"] == layer.get("slots", [])
            for key in ("tint", "offsetUv", "scaleUv"):
                source_key = {"offsetUv": "offsetUV", "scaleUv": "scaleUV"}.get(key, key)
                assert actual[key] == layer.get(source_key, [])
        assert output[-1]["tint"][3] == 0.75
        print(f"BODY OVERLAY INDEPENDENT PASS fallout4 layers={len(output)}")
        return

    def node_key(item: tuple[int, dict]) -> tuple[int, int]:
        source_index, layer = item
        node = layer["node"]
        _, suffix = node.split(" [Ovl", 1)
        return int(suffix[:-1]), source_index

    expected = sorted(enumerate(layers), key=node_key)
    for order, (source_index, layer) in enumerate(expected):
        actual = output[order]
        assert actual["order"] == order and actual["sourceIndex"] == source_index
        assert actual["node"] == layer["node"]
        assert actual["diffuse"] == layer["diffuse"]
        assert actual["normal"] == layer.get("normal")
        assert actual["target"] == layer["node"].split(" [", 1)[0].lower()
        assert actual["alpha"] == layer.get("alpha", 1)
    print(f"BODY OVERLAY INDEPENDENT PASS skyrimse layers={len(output)}")


if __name__ == "__main__":
    main()
