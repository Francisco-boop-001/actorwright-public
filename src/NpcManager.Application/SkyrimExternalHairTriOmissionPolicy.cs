using System.Collections.Immutable;

namespace NpcManager.Application;

/// <summary>
/// The one reviewed external-hair omission boundary admitted by the Ruby
/// production request. It is deliberately provider-hash bound and only
/// applies to record-declared NAM0 mesh TRI routes; no source asset is made
/// up or substituted.
/// </summary>
public static class SkyrimExternalHairTriOmissionPolicy
{
    public const string DintPlugin = "[dint999] HairPack02.esp";
    public const string DintPluginSha256 =
        "104F3E6A8DC7EF7D142070E0B0742E3D27CD967A93C00313DA0883CAE79414A5";

    private static readonly ImmutableHashSet<string> DintDeclaredAbsentHairNam0 =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            "0000BC05|meshes/armor/[dint999]/02 hair/hairs/16/1hl.tri",
            "00003090|meshes/armor/[dint999]/02 hair/hairs/16/a5_1.tri",
            "0000308F|meshes/armor/[dint999]/02 hair/hairs/16/a5_4.tri",
            "0000308E|meshes/armor/[dint999]/02 hair/hairs/16/a5_2.tri",
            "0000308D|meshes/armor/[dint999]/02 hair/hairs/16/a5_2.tri",
            "0000308C|meshes/armor/[dint999]/02 hair/hairs/16/a5_1.tri",
            "0000308B|meshes/armor/[dint999]/02 hair/hairs/16/a4_4.tri",
            "0000308A|meshes/armor/[dint999]/02 hair/hairs/16/a4_3.tri",
            "00003089|meshes/armor/[dint999]/02 hair/hairs/16/a4_2.tri",
            "00003088|meshes/armor/[dint999]/02 hair/hairs/16/a4_1.tri",
            "00003087|meshes/armor/[dint999]/02 hair/hairs/16/a3_4.tri",
            "00003086|meshes/armor/[dint999]/02 hair/hairs/16/a3_3.tri",
            "00003085|meshes/armor/[dint999]/02 hair/hairs/16/a3_2.tri",
            "00003084|meshes/armor/[dint999]/02 hair/hairs/16/a3_1.tri",
            "00003083|meshes/armor/[dint999]/02 hair/hairs/16/a2_4.tri",
            "00003082|meshes/armor/[dint999]/02 hair/hairs/16/a2_3.tri",
            "00003081|meshes/armor/[dint999]/02 hair/hairs/16/a2_2.tri",
            "00003080|meshes/armor/[dint999]/02 hair/hairs/16/a2_1.tri",
            "0000307F|meshes/armor/[dint999]/02 hair/hairs/16/a1_4.tri",
            "0000307E|meshes/armor/[dint999]/02 hair/hairs/16/a1_3.tri",
            "0000307D|meshes/armor/[dint999]/02 hair/hairs/16/a1_2.tri",
            "0000307C|meshes/armor/[dint999]/02 hair/hairs/16/a1_1.tri");

    public static bool IsAllowedMissingHairNam0(
        SkyrimFaceHeadPartRecordRoute headPart,
        SkyrimHdptTriRoute tri) =>
        tri.Role == SkyrimHdptTriRole.Mesh &&
        string.Equals(headPart.Provider.Plugin.Value, DintPlugin,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(headPart.Provider.Sha256.Value, DintPluginSha256,
            StringComparison.OrdinalIgnoreCase) &&
        DintDeclaredAbsentHairNam0.Contains(
            $"{headPart.Reference.FormId.Value:X8}|{tri.Path.Value}");

    public static ImmutableArray<string> DeclaredAbsentHairNam0Paths =>
        DintDeclaredAbsentHairNam0
            .Select(item => item[(item.IndexOf('|') + 1)..])
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
}
