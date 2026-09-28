using System.Collections.Immutable;

namespace NpcManager.Application;

public static partial class SkyrimFaceEditorDocumentRules
{
    private static void ValidateSculpt(
        ImmutableArray<RaceMenuSculptPart> parts,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (parts.IsDefault || parts.Length > MaximumSculptParts ||
            parts.Any(item => item is null || !ValidName(item.Host) ||
                              item.VertexCount < 0 || item.Vertices.IsDefault ||
                              item.Vertices.Length > MaximumSculptVerticesPerPart ||
                              item.Vertices.Any(vertex => vertex.Index < 0 ||
                                  !float.IsFinite(vertex.Dx) ||
                                  !float.IsFinite(vertex.Dy) ||
                                  !float.IsFinite(vertex.Dz))))
        {
            diagnostics.Add(Error("face-editor-sculpt-shape",
                "Sculpt metadata is absent, invalid, or exceeds the bounded part count."));
        }
    }

    private static bool SculptEquivalent(
        ImmutableArray<RaceMenuSculptPart> left,
        ImmutableArray<RaceMenuSculptPart> right)
    {
        if (left.Length != right.Length) return false;
        for (int index = 0; index < left.Length; index++)
        {
            RaceMenuSculptPart a = left[index];
            RaceMenuSculptPart b = right[index];
            if (!string.Equals(a.Host, b.Host, StringComparison.Ordinal) ||
                a.VertexCount != b.VertexCount ||
                a.HasVertexCount != b.HasVertexCount ||
                a.HasData != b.HasData ||
                !a.Vertices.SequenceEqual(b.Vertices)) return false;
        }
        return true;
    }
}
