"""Focused regression for donor_diff dynamic vertex-space classification."""

import argparse
import sys
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--static-risk", required=True, type=Path)
    args = parser.parse_args()

    workspace = Path(__file__).resolve().parents[4]
    sys.path.insert(0, str(workspace / "tools" / "gates"))
    import donor_diff  # pylint: disable=import-outside-toplevel

    candidate_rows = donor_diff.analyze(str(args.candidate))
    hairline = next(row for row in candidate_rows
                    if row["name"] == "0_HAIRLINE_Female_Human_Straight")
    assert 117.0 < hairline["vertex_z_min"] < hairline["vertex_z_mean"]
    assert hairline["vertex_z_mean"] < hairline["vertex_z_max"] < 132.0
    candidate_risks = donor_diff.hard_findings(candidate_rows, "candidate")
    assert not any("space-bind-mismatch" in risk for risk in candidate_risks), candidate_risks

    real_mismatch = dict(hairline)
    real_mismatch["vertex_z_min"] = -1.0
    real_mismatch["vertex_z_mean"] = 0.0
    real_mismatch["vertex_z_max"] = 1.0
    mismatch_risks = donor_diff.hard_findings([real_mismatch], "synthetic")
    assert any("space-bind-mismatch" in risk and "vertex_mean_z=0.0" in risk
               for risk in mismatch_risks), mismatch_risks

    static_rows = donor_diff.analyze(str(args.static_risk))
    static_risks = donor_diff.hard_findings(static_rows, "candidate")
    for shape in ("002VoiceofDesire", "000VoiceofDesireHL"):
        assert any("space-bind-mismatch" in risk and shape in risk and
                   "sphere_z=85.5" in risk for risk in static_risks), static_risks

    print("DONOR_DIFF_COORDINATE_SPACE_REGRESSION=PASS")
    print(f"EMI_HAIRLINE_VERTEX_MEAN_Z={hairline['vertex_z_mean']:.6f}")
    print("TRUE_MISMATCH_DETECTED=PASS")
    print("STATIC_FALLBACK_FINDINGS=2/2")


if __name__ == "__main__":
    main()
