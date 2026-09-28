using NpcManager.Domain;

namespace NpcManager.Rendering;

public static class NpcVisualPreviewRendererAuthority
{
    public const string ScriptId = "render_npc_preview_bundle";

    public static Sha256Hash ScriptSha256 { get; } = new(
        "60E863A0224F5834042EF124CE99648D31BBE21E6CFADF1C832EEDE86FCE69C9");
}
