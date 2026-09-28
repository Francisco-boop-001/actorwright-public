"""Compare the real Python byte reader with the focused C# fixture evidence."""
import importlib.util
import json
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[2]
runtime = root / "runtime/rendering/nif_geometry_readback.py"
assert runtime.read_bytes() == (root / "tools/rendering/nif_geometry_readback.py").read_bytes()
spec = importlib.util.spec_from_file_location("task25_readback", runtime)
reader = importlib.util.module_from_spec(spec)
spec.loader.exec_module(reader)
cases = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
for case in cases:
    try:
        actual = reader.parse_nif(Path(case["path"]), case["edition"])
    except ValueError:
        assert not case["accepted"], case["path"]
        continue
    assert case["accepted"], case["path"]
    assert actual["aggregateGeometrySha256"] == case["aggregate"], case["path"]
    assert len(actual["geometryShapes"]) == len(case["shapes"])
    for shape, expected in zip(actual["geometryShapes"], case["shapes"]):
        assert shape["name"] == expected["name"]
        assert shape["vertexDescriptor"] == expected["descriptor"]
        assert shape["vertexPayloadLength"] // shape["vertexCount"] == expected["stride"]
        assert shape["vertexPayloadOffset"] == expected["offset"]
        assert shape["vertexPayloadLength"] == expected["length"]
        assert shape["vertexPayloadSha256"] == expected["hash"]
print(f"PASS actual Python/C# reader parity: {len(cases)} bounded cases, runtime/tool bytes identical")
