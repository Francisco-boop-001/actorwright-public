"""Independent Draft 2020-12 checks against product-written session fixtures.

Run with the controller-approved task-local jsonschema==4.25.1 interpreter.
This is test tooling only; no product dependency or schema generation lives here.
"""
import copy
import base64
import importlib.metadata
import json
from pathlib import Path
import sys
import struct

from jsonschema import Draft202012Validator


def reject(validator, document, label):
    if validator.is_valid(document):
        raise AssertionError(f"schema admitted {label}")


def first_hash(node):
    if isinstance(node, dict):
        for key, value in node.items():
            if key.lower().endswith("sha256") and isinstance(value, str):
                node[key] = "invalid-sha256"
                return True
            if first_hash(value):
                return True
    elif isinstance(node, list):
        return any(first_hash(value) for value in node)
    return False


directory = Path(sys.argv[1]).resolve()
fixtures = Path((directory / "fixture-root.txt").read_text()).resolve()
if not fixtures.is_relative_to(directory):
    raise AssertionError("fixture root escaped the owned schema test directory")
payloads = ("intake", "inferenceProposal", "reviewedDesign", "resourceSnapshot",
            "authoringProposal", "verifiedPreset", "verifiedNpcHandoff")
names = ("intake", "inference-proposal", "reviewed-design", "resource-snapshot",
         "authoring-proposal", "verified-preset", "verified-npc-handoff")
negative_count = 0
validators = {}
documents = {}
for kind, (name, payload) in enumerate(zip(names, payloads)):
    schema = json.loads((directory / f"{name}.json").read_text())
    document = json.loads((fixtures / f"{name}.json").read_text())
    Draft202012Validator.check_schema(schema)
    validator = Draft202012Validator(schema)
    validators[name] = validator
    documents[name] = document
    validator.validate(document)
    for label, edit in (
        ("unknown envelope member", lambda value: value.update(unknown=True)),
        ("unknown payload member", lambda value: value[payload].update(unknown=True)),
        ("string kind", lambda value: value.update(kind=str(kind))),
        ("wrong numeric kind", lambda value: value.update(kind=(kind + 1) % 7)),
        ("wrong payload version", lambda value: value[payload].update(schemaVersion=2)),
        ("absent active payload", lambda value: value.pop(payload)),
        ("null active payload", lambda value: value.update({payload: None})),
    ):
        changed = copy.deepcopy(document)
        edit(changed)
        reject(validator, changed, f"{name}: {label}")
        negative_count += 1
    changed = copy.deepcopy(document)
    assert first_hash(changed), f"no hash-bearing fixture for {name}"
    reject(validator, changed, f"{name}: malformed hash")
    negative_count += 1
    print(f"PASS {name}: actual typed envelope and eight invalid mutations")
for name, path, invalid in (
    ("intake", ("intake", "sex"), 99),
    ("intake", ("intake", "sex"), "Female"),
    ("intake", ("intake", "race"), {"plugin": "Skyrim.esm", "id": 0x13746}),
    ("intake", ("intake", "baselineJslot"), "relative.jslot"),
    ("resource-snapshot", ("resourceSnapshot", "catalogAuthority", "headParts", 0, "modelNif"), "C:/absolute.nif"),
    ("resource-snapshot", ("resourceSnapshot", "catalogAuthority", "headParts", 0, "provider", "plugin"), "not-a-plugin"),
    ("resource-snapshot", ("resourceSnapshot", "baseline", "appearance", "headParts", 0, "identifier", "formId"), "not-hex"),
    ("resource-snapshot", ("resourceSnapshot", "renderShapes", 0, "restPositions"), [[0, 0, 0]]),
    ("resource-snapshot", ("resourceSnapshot", "renderShapes", 0, "triangleIndices"), [0, 1, 2]),
    ("resource-snapshot", ("resourceSnapshot", "renderShapes", 0, "textureCoordinates"), "%%%"),
    ("resource-snapshot", ("resourceSnapshot", "morphChannels", 0, "deltas"), [{"vertexIndex": 0, "delta": [0, 0, 0]}]),
):
    changed = copy.deepcopy(documents[name])
    node = changed
    for key in path[:-1]:
        node = node[key]
    node[path[-1]] = invalid
    reject(validators[name], changed, name + ": " + str(path))
    negative_count += 1
snapshot = documents["resource-snapshot"]["resourceSnapshot"]
shape = snapshot["renderShapes"][0]
assert struct.unpack("<9f", base64.b64decode(shape["restPositions"], validate=True)) == (0, 0, 0, 1, 0, 0, 0, 1, 0)
assert struct.unpack("<3i", base64.b64decode(shape["triangleIndices"], validate=True)) == (0, 1, 2)
assert struct.unpack("<6f", base64.b64decode(shape["textureCoordinates"], validate=True)) == (0, 0, 1, 0, 0, 1)
assert struct.unpack("<i3f", base64.b64decode(snapshot["morphChannels"][0]["deltas"], validate=True)) == (0, 0.25, 0.5, 0.75)
filled = Path((directory.parent / "filled-template-path.txt").read_text()).resolve()
assert filled.is_relative_to(directory.parent), "filled template escaped owned directory"
validators["intake"].validate(json.loads(filled.read_text()))
print(f"PASS seven actual envelopes, filled template, exact four compact encodings and {negative_count} refusals; jsonschema {importlib.metadata.version('jsonschema')}")
