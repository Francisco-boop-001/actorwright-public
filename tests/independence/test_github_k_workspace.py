from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github" / "workflows" / "build.yml"


def test_github_build_runs_from_temporary_k_workspace() -> None:
    workflow = WORKFLOW.read_text(encoding="utf-8")
    create_volume = 'create vdisk file=`"$vhdPath`" maximum=8192 type=expandable'
    format_volume = 'format fs=ntfs label=ActorwrightCI quick'
    assign_drive = "assign letter=K"
    copy_checkout = 'robocopy.exe "$env:GITHUB_WORKSPACE" $kWorkspace'
    accept_copy = "if ($copyExit -gt 7) {"
    setup_success = "exit 0"
    enter_workspace = "Set-Location $kWorkspace"
    canonical_build = (
        "pwsh -NoProfile -File ./tools/build/build.ps1 -Configuration Release"
    )
    detach_volume = "detach vdisk"

    assert "subst K:" not in workflow, "A path alias is not a real K-local volume"
    assert create_volume in workflow, "GitHub CI must create an isolated K-local VHD"
    assert format_volume in workflow, "The CI volume must be an ordinary NTFS volume"
    assert assign_drive in workflow, "The isolated CI volume must own drive K:"
    assert copy_checkout in workflow, "The checkout must be copied onto the K-local volume"
    assert accept_copy in workflow, "Only robocopy failure codes may fail K: setup"
    assert setup_success in workflow, "Accepted robocopy codes must not leak as step failures"
    assert enter_workspace in workflow, "GitHub CI must enter the real K-only workspace"
    assert "if: always()" in workflow, "VHD cleanup must run after success or failure"
    assert detach_volume in workflow, "GitHub CI must detach its temporary VHD"
    assert workflow.index(create_volume) < workflow.index(copy_checkout)
    assert workflow.index(copy_checkout) < workflow.index(setup_success)
    assert workflow.index(copy_checkout) < workflow.index(enter_workspace)
    assert workflow.index(enter_workspace) < workflow.index(canonical_build)
    assert workflow.index(canonical_build) < workflow.index(detach_volume)
