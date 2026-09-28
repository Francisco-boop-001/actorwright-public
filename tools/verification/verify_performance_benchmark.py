#!/usr/bin/env python3
"""Independently validate the bounded M9 performance benchmark evidence."""

from __future__ import annotations

import argparse
import json
import math
from pathlib import Path
from typing import Any


def _median(values: list[float]) -> float:
    ordered = sorted(values)
    middle = len(ordered) // 2
    if len(ordered) % 2:
        return ordered[middle]
    return round((ordered[middle - 1] + ordered[middle]) / 2, 3)


def _number(value: Any, label: str, errors: list[str]) -> float | None:
    if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value) or value <= 0:
        errors.append(f"{label} must be a finite positive number")
        return None
    return float(value)


def verify(report_path: Path) -> dict[str, Any]:
    errors: list[str] = []
    try:
        document = json.loads(report_path.read_text(encoding="utf-8-sig"))
    except (OSError, json.JSONDecodeError) as exc:
        return {"result": "FAIL", "errors": [str(exc)]}

    schema_version = document.get("schemaVersion")
    if schema_version not in (1, 2):
        errors.append("unsupported benchmark schema")
    if document.get("status") != "PASS":
        errors.append("benchmark status is not PASS")
    if document.get("releaseClaim") is not False:
        errors.append("benchmark must not claim runtime or release performance")
    if "no runtime" not in str(document.get("scope", "")).lower():
        errors.append("benchmark scope must state that runtime performance is not claimed")

    budgets = document.get("budgetsMs")
    measurements = document.get("measurementsMs")
    summary = document.get("summaryMs")
    if not isinstance(budgets, dict) or not isinstance(measurements, dict) or not isinstance(summary, dict):
        errors.append("budgets, measurements, and summary must be objects")
        return {"result": "FAIL", "errors": errors}

    scalar = {
        "capabilities": ("cliStartupColdMs", "cliStartupCold", 5),
        "packageVerification": ("packageVerificationColdMs", "packageVerificationCold", 3),
    }
    for key, (budget_key, summary_key, expected_count) in scalar.items():
        budget = _number(budgets.get(budget_key), f"budget {budget_key}", errors)
        values = measurements.get(key)
        if not isinstance(values, list) or len(values) != expected_count:
            errors.append(f"{key} must contain {expected_count} measurements")
            continue
        parsed = [item for index, item in enumerate(values) if _number(item, f"{key}[{index}]", errors) is not None]
        if len(parsed) != len(values):
            continue
        cold = _number(summary.get(summary_key), f"summary {summary_key}", errors)
        if budget is not None and cold is not None and cold > budget:
            errors.append(f"{summary_key} exceeds its budget")
        if cold is not None and not math.isclose(cold, parsed[0], abs_tol=0.002):
            errors.append(f"summary {summary_key} does not match the cold measurement")
        warm_key = "cliStartupWarmMedian" if key == "capabilities" else "packageVerificationWarmMedian"
        warm = _number(summary.get(warm_key), f"summary {warm_key}", errors)
        if warm is not None and not math.isclose(warm, _median(parsed[1:]), abs_tol=0.002):
            errors.append(f"summary {warm_key} does not match the warm median")

    if schema_version == 2:
        expanded_scalars = {
            "previewRender": ("previewRenderColdMs", "previewRenderCold", "previewRenderWarmMedian"),
            "faceGenBatch": ("faceGenBatchColdMs", "faceGenBatchCold", "faceGenBatchWarmMedian"),
        }
        for key, (budget_key, cold_key, warm_key) in expanded_scalars.items():
            budget = _number(budgets.get(budget_key), f"budget {budget_key}", errors)
            values = measurements.get(key)
            if not isinstance(values, list) or len(values) != 3:
                errors.append(f"{key} must contain three measurements")
                continue
            parsed = [item for index, item in enumerate(values) if _number(item, f"{key}[{index}]", errors) is not None]
            if len(parsed) != len(values):
                continue
            cold = _number(summary.get(cold_key), f"summary {cold_key}", errors)
            warm = _number(summary.get(warm_key), f"summary {warm_key}", errors)
            if budget is not None and cold is not None and cold > budget:
                errors.append(f"{cold_key} exceeds its budget")
            if cold is not None and not math.isclose(cold, parsed[0], abs_tol=0.002):
                errors.append(f"summary {cold_key} does not match the cold measurement")
            if warm is not None and not math.isclose(warm, _median(parsed[1:]), abs_tol=0.002):
                errors.append(f"summary {warm_key} does not match the warm median")

        memory_budget = _number(budgets.get("faceGenBatchPeakWorkingSetBytes"),
                                "budget faceGenBatchPeakWorkingSetBytes", errors)
        memory_values = measurements.get("faceGenBatchPeakWorkingSetBytes")
        if not isinstance(memory_values, list) or len(memory_values) != 3:
            errors.append("faceGenBatchPeakWorkingSetBytes must contain three measurements")
        else:
            parsed_memory = [item for index, item in enumerate(memory_values)
                             if _number(item, f"faceGenBatchPeakWorkingSetBytes[{index}]", errors) is not None]
            if len(parsed_memory) == len(memory_values):
                maximum = _number(summary.get("faceGenBatchPeakWorkingSetBytesMax"),
                                  "summary faceGenBatchPeakWorkingSetBytesMax", errors)
                median = _number(summary.get("faceGenBatchPeakWorkingSetBytesMedian"),
                                 "summary faceGenBatchPeakWorkingSetBytesMedian", errors)
                if memory_budget is not None and maximum is not None and maximum > memory_budget:
                    errors.append("faceGenBatch peak working set exceeds its budget")
                if maximum is not None and not math.isclose(maximum, max(parsed_memory), abs_tol=1):
                    errors.append("summary faceGenBatchPeakWorkingSetBytesMax does not match measurements")
                if median is not None and not math.isclose(median, _median(parsed_memory), abs_tol=1):
                    errors.append("summary faceGenBatchPeakWorkingSetBytesMedian does not match measurements")

    edition_groups = {
        "assetIndex": ("assetIndexColdMs", "assetIndexCold", "assetIndexWarmMedian"),
        "workspacePreflight": ("workspacePreflightColdMs", "workspacePreflightCold", "workspacePreflightWarmMedian"),
        "faceGenProviderResolution": ("faceGenProviderResolutionColdMs", "faceGenProviderResolutionCold", "faceGenProviderResolutionWarmMedian"),
        "faceGeomProviderBound": ("faceGeomProviderBoundColdMs", "faceGeomProviderBoundCold", "faceGeomProviderBoundWarmMedian"),
    }
    for key, (budget_key, cold_key, warm_key) in edition_groups.items():
        budget = _number(budgets.get(budget_key), f"budget {budget_key}", errors)
        values_by_edition = measurements.get(key)
        cold_by_edition = summary.get(cold_key)
        warm_by_edition = summary.get(warm_key)
        if not isinstance(values_by_edition, dict) or set(values_by_edition) != {"fallout4", "skyrimse"}:
            errors.append(f"{key} must contain exactly fallout4 and skyrimse")
            continue
        if not isinstance(cold_by_edition, dict) or not isinstance(warm_by_edition, dict):
            errors.append(f"{key} summaries must be edition objects")
            continue
        for edition in ("fallout4", "skyrimse"):
            values = values_by_edition.get(edition)
            if not isinstance(values, list) or len(values) != 3:
                errors.append(f"{key}.{edition} must contain three measurements")
                continue
            parsed = [item for index, item in enumerate(values) if _number(item, f"{key}.{edition}[{index}]", errors) is not None]
            if len(parsed) != len(values):
                continue
            cold = _number(cold_by_edition.get(edition), f"summary {cold_key}.{edition}", errors)
            warm = _number(warm_by_edition.get(edition), f"summary {warm_key}.{edition}", errors)
            if budget is not None and cold is not None and cold > budget:
                errors.append(f"{cold_key}.{edition} exceeds its budget")
            if cold is not None and not math.isclose(cold, parsed[0], abs_tol=0.002):
                errors.append(f"summary {cold_key}.{edition} does not match the cold measurement")
            if warm is not None and not math.isclose(warm, _median(parsed[1:]), abs_tol=0.002):
                errors.append(f"summary {warm_key}.{edition} does not match the warm median")

    return {
        "result": "PASS" if not errors else "FAIL",
        "schemaVersion": document.get("schemaVersion"),
        "errors": errors,
        "report": str(report_path),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    result = verify(args.report.resolve())
    print(json.dumps(result, sort_keys=True))
    return 0 if result["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
