"""Public documentation subset; private agent permission probes are not shipped."""

from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def test_public_agent_guidance_states_the_two_protocol_contract() -> None:
    agents = (ROOT / "AGENTS.md").read_text(encoding="utf-8")
    for phrase in (
        "Protocol 1",
        "complete current 142-command interface",
        'readiness: "legacy"',
        "ACTORWRIGHT_WORKSPACE_ROOT",
        "workspace-root-not-k-local",
        "ACTORWRIGHT_PROTECTED_ROOT",
        "dotnet-sdk-10.0.301",
    ):
        assert phrase in agents, phrase


def test_public_docs_define_a_generic_exchange_handoff() -> None:
    agents = (ROOT / "AGENTS.md").read_text(encoding="utf-8")
    claude = (ROOT / "CLAUDE.md").read_text(encoding="utf-8")
    handoff = (ROOT / "docs" / "product-to-mod-handoff.md").read_text(encoding="utf-8")
    for guidance in (agents, claude, handoff):
        assert "product repository" in guidance.lower()
        assert "mod-work" in guidance.lower()
    for word in ("release candidate", "human promotion", "analyze", "apply", "verify", "rollback"):
        assert word in handoff.lower()
    assert "explicitly approves" in handoff
    assert "<exchange-root>" in handoff
    assert "ExampleWorkspace" not in handoff
    assert "ExampleExchange" not in handoff
