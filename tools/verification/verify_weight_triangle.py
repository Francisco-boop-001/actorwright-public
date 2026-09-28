"""Independent oracle for the pinned Fallout 4 weight-triangle math."""

import argparse
import json
import math
from pathlib import Path


def parse(text: str) -> tuple[float, float, float]:
    values = {}
    for part in text.split(","):
        name, value = part.split("=", 1)
        values[name.strip().lower()] = float(value)
    if set(values) != {"thin", "muscular", "fat"}:
        raise AssertionError("triangle must contain all three axes")
    return values["thin"], values["muscular"], values["fat"]


def normalize(values: tuple[float, float, float]) -> tuple[float, float, float]:
    values = tuple(max(0.0, value) for value in values)
    total = sum(values)
    return (0.5, 0.5, 0.0) if total < 0.0001 else tuple(value / total for value in values)


def redistribute(values: tuple[float, float, float], axis: str, value: float) -> tuple[float, float, float]:
    current = normalize(values)
    changed = min(1.0, max(0.0, value))
    remaining = 1.0 - changed
    indices = {"thin": 0, "muscular": 1, "fat": 2}
    changed_index = indices[axis]
    others = [index for index in range(3) if index != changed_index]
    total = current[others[0]] + current[others[1]]
    if total < 0.0001:
        first = second = remaining * 0.5
    else:
        first = remaining * current[others[0]] / total
        second = remaining * current[others[1]] / total
    result = [0.0, 0.0, 0.0]
    result[changed_index] = changed
    result[others[0]] = first
    result[others[1]] = second
    return tuple(result)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--operation", choices=["normalize", "redistribute"], required=True)
    parser.add_argument("--triangle", required=True)
    parser.add_argument("--response", required=True, type=Path)
    parser.add_argument("--axis")
    parser.add_argument("--value", type=float)
    args = parser.parse_args()
    values = parse(args.triangle)
    expected = normalize(values) if args.operation == "normalize" else redistribute(values, args.axis, args.value)
    with args.response.open("r", encoding="utf-8-sig") as handle:
        response = json.load(handle)
    actual = (response["thin"], response["muscular"], response["fat"])
    assert all(math.isclose(left, right, rel_tol=0, abs_tol=0.00001) for left, right in zip(expected, actual))
    assert math.isclose(sum(actual), 1.0, rel_tol=0, abs_tol=0.00001)
    print(f"WEIGHT TRIANGLE INDEPENDENT PASS operation={args.operation}")


if __name__ == "__main__":
    main()
