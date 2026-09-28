using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed partial class NpcAppearanceOverrideService
{
    private static readonly ImmutableArray<string> BaseChangedSubrecords =
    [
        "RNAM", "ACBS", "PNAM", "HCLF", "FTST", "NAM7", "NAM9", "NAMA",
        "TINI", "TINC", "TINV", "TIAS", "QNAM"
    ];

    private ImmutableArray<Diagnostic> ValidateRequest(
        NpcAppearanceOverrideRequest request,
        bool outputMustExist,
        bool proposalMustExist,
        bool proposalMayExist = false)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("npc-appearance-override-edition",
                "Appearance overrides support Skyrim SE/AE only."));
        if (!request.SourcePlugin.IsUnder(labRoot) ||
            !request.OutputPlugin.IsUnder(labRoot) ||
            !request.ProposalPath.IsUnder(labRoot))
        {
            diagnostics.Add(Error("npc-appearance-override-outside-lab",
                "Source, proposal, and output paths must remain under the K-only workspace."));
        }
        if (!File.Exists(request.SourcePlugin.Value))
            diagnostics.Add(Error("npc-appearance-override-source-missing",
                "The source plugin does not exist."));
        if (request.TargetFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("npc-appearance-override-form-id",
                "The target must be a nonzero plugin-local 24-bit FormID."));
        if (!request.SourcePlugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) &&
            !request.SourcePlugin.Value.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) &&
            !request.SourcePlugin.Value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("npc-appearance-override-source-extension",
                "The source plugin must use .esp, .esm, or .esl."));
        }
        if (!request.OutputPlugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("npc-appearance-override-output-extension",
                "The appearance override output must be an ordinary .esp."));
        if (string.Equals(Path.GetFileName(request.SourcePlugin.Value),
                Path.GetFileName(request.OutputPlugin.Value),
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("npc-appearance-override-name-collision",
                "The output plugin must have a different filename from its source plugin."));
        }
        NpcCreationService.ValidateFullyAuthoredAppearance(
            request.Appearance.Weight,
            request.Appearance,
            diagnostics);
        NpcCreationService.ValidateRuntimeAppearance(
            request.RuntimeAppearance,
            request.Sex,
            diagnostics);
        ValidatePluginAuthorities(request.PluginAuthorities, diagnostics);

        ValidateParent(request.OutputPlugin, "output", diagnostics);
        ValidateParent(request.ProposalPath, "proposal", diagnostics);
        AddReparseDiagnostic(diagnostics, request.SourcePlugin.Value, "source plugin");
        if (outputMustExist && !File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("npc-appearance-override-output-missing",
                "The appearance override plugin does not exist."));
        if (!outputMustExist && File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("npc-appearance-override-output-exists",
                "Appearance override writes never overwrite an output plugin."));
        if (proposalMustExist && !File.Exists(request.ProposalPath.Value))
            diagnostics.Add(Error("npc-appearance-override-proposal-missing",
                "Apply requires the exact persisted proposal."));
        if (!proposalMustExist && !proposalMayExist && File.Exists(request.ProposalPath.Value))
            diagnostics.Add(Error("npc-appearance-override-proposal-exists",
                "Appearance override analysis never overwrites a proposal."));
        if (File.Exists(request.ProposalPath.Value))
            AddReparseDiagnostic(diagnostics, request.ProposalPath.Value, "proposal");
        return diagnostics.ToImmutable();
    }

    private void ValidatePluginAuthorities(
        ImmutableArray<NpcCreationPluginAuthority> authorities,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (authorities.IsDefaultOrEmpty) return;
        if (authorities.Length > 64 || authorities.Select(a => a.Plugin.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != authorities.Length)
        {
            diagnostics.Add(Error("npc-appearance-override-provider-authority",
                "Copied plugin authorities must contain at most 64 unique plugin names."));
            return;
        }
        foreach (var authority in authorities)
        {
            if (!authority.PluginPath.IsUnder(labRoot) || !string.Equals(
                Path.GetFileName(authority.PluginPath.Value), authority.Plugin.Value,
                StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("npc-appearance-override-provider-authority",
                    $"Copied authority {authority.Plugin.Value} must match its filename under the workspace."));
                continue;
            }
            int previousCount = diagnostics.Count;
            diagnostics.AddRange(policy.Evaluate(labRoot, authority.PluginPath));
            AddReparseDiagnostic(diagnostics, authority.PluginPath.Value, "copied plugin authority");
            if (diagnostics.Count != previousCount) continue;
            if (!File.Exists(authority.PluginPath.Value) ||
                new FileInfo(authority.PluginPath.Value).Length > 512L * 1024 * 1024 ||
                HashFile(authority.PluginPath.Value) != authority.ExpectedSha256)
                diagnostics.Add(Error("npc-appearance-override-provider-authority",
                    $"Copied authority {authority.Plugin.Value} is missing, oversized, or differs from its bound SHA-256."));
        }
    }

    private void ValidateParent(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var parent = Path.GetDirectoryName(path.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error($"npc-appearance-override-{role}-parent",
                $"The {role} parent directory must already exist."));
            return;
        }
        diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        AddReparseDiagnostic(diagnostics, parent, $"{role} parent");
    }

    private static void ValidateProposalBinding(
        NpcAppearanceOverrideRequest request,
        NpcAppearanceOverrideProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (proposal.SchemaVersion != 1 ||
            proposal.Edition != request.Edition ||
            proposal.SourcePlugin != request.SourcePlugin ||
            proposal.SourceSha256 != request.ExpectedSourceSha256 ||
            proposal.TargetFormId != request.TargetFormId ||
            proposal.ProposalPath != request.ProposalPath ||
            proposal.OutputPlugin != request.OutputPlugin ||
            proposal.Race != request.Race ||
            proposal.Sex != request.Sex ||
            !AppearanceEquivalent(proposal.Appearance, request.Appearance) ||
            !NpcCreationService.RuntimeAppearancePayloadsEqual(
                proposal.RuntimeAppearance,
                request.RuntimeAppearance) ||
            proposal.OutfitPatch != request.OutfitPatch ||
            proposal.IsCharGenFacePreset != request.IsCharGenFacePreset)
        {
            diagnostics.Add(Error("npc-appearance-override-proposal-binding",
                "The supplied proposal is not bound to the exact appearance request."));
        }
        if (proposal.ProposalSha256 is null ||
            !File.Exists(request.ProposalPath.Value) ||
            HashFile(request.ProposalPath.Value) != proposal.ProposalSha256)
        {
            diagnostics.Add(Error("npc-appearance-override-proposal-hash",
                "The persisted proposal hash does not match the analyzed proposal."));
        }
        if (!File.Exists(request.SourcePlugin.Value) ||
            HashFile(request.SourcePlugin.Value) != proposal.SourceSha256)
        {
            diagnostics.Add(Error("npc-appearance-override-source-stale",
                "The source plugin changed after appearance analysis."));
        }
    }

    private static bool SameProposalSemantics(
        NpcAppearanceOverrideProposal fresh,
        NpcAppearanceOverrideProposal persisted) =>
        fresh.SourceSha256 == persisted.SourceSha256 &&
        fresh.SourceEditorId == persisted.SourceEditorId &&
        fresh.OutfitPatch == persisted.OutfitPatch &&
        fresh.IsCharGenFacePreset == persisted.IsCharGenFacePreset &&
        fresh.RequiredMasters.SequenceEqual(persisted.RequiredMasters) &&
        fresh.ExpectedMajorRecordSignatures.SequenceEqual(
            persisted.ExpectedMajorRecordSignatures) &&
        fresh.ChangedNpcSubrecords.SequenceEqual(persisted.ChangedNpcSubrecords) &&
        !HasErrors(fresh.Diagnostics);

    private static bool AppearanceEquivalent(
        FullyAuthoredSkyrimNpcAppearanceSource left,
        FullyAuthoredSkyrimNpcAppearanceSource right) =>
        left.OrderedHeadParts.SequenceEqual(right.OrderedHeadParts) &&
        left.HairColor == right.HairColor &&
        left.FaceTextureSet == right.FaceTextureSet &&
        BitConverter.SingleToInt32Bits(left.Weight) ==
            BitConverter.SingleToInt32Bits(right.Weight) &&
        left.FaceMorphs.Nam9Sliders.SequenceEqual(
            right.FaceMorphs.Nam9Sliders) &&
        BitConverter.SingleToInt32Bits(left.FaceMorphs.Nam9Trailing) ==
            BitConverter.SingleToInt32Bits(right.FaceMorphs.Nam9Trailing) &&
        left.FaceMorphs.NamaValues.SequenceEqual(
            right.FaceMorphs.NamaValues) &&
        left.FaceTints.Layers.SequenceEqual(right.FaceTints.Layers) &&
        left.Qnam == right.Qnam &&
        left.ExposedOutfitSkinBinding == right.ExposedOutfitSkinBinding &&
        NakedSkinEquivalent(left.NakedSkinBinding, right.NakedSkinBinding);

    private static bool NakedSkinEquivalent(
        OutputOwnedSkyrimNpcNakedSkinBinding? left,
        OutputOwnedSkyrimNpcNakedSkinBinding? right) =>
        left is null ? right is null : right is not null &&
        left.AllocatedArmorLocalFormId == right.AllocatedArmorLocalFormId &&
        left.SourceSkinArmor == right.SourceSkinArmor &&
        left.Regions.SequenceEqual(right.Regions);

    private static ImmutableArray<string> ChangedSubrecords(
        bool runtime,
        NpcOutfitPatch? outfitPatch)
    {
        var changed = BaseChangedSubrecords;
        if (runtime) changed = changed.Add("VMAD");
        if (outfitPatch?.DefaultOutfit.IsSpecified == true)
            changed = changed.Add("DOFT");
        if (outfitPatch?.SleepingOutfit.IsSpecified == true)
            changed = changed.Add("SOFT");
        return changed;
    }

    private static void ValidateOutfitPatch(
        WorkspacePath dataRoot,
        NpcOutfitPatch? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch is null) return;
        if (patch.IsEmpty)
        {
            diagnostics.Add(Error(
                "npc-appearance-override-outfit-empty",
                "An outfit patch must explicitly set or clear DOFT or SOFT."));
            return;
        }
        ValidateOutfitReference(
            dataRoot,
            patch.DefaultOutfit,
            "default",
            diagnostics);
        ValidateOutfitReference(
            dataRoot,
            patch.SleepingOutfit,
            "sleeping",
            diagnostics);
    }

    private static void ValidateOutfitReference(
        WorkspacePath dataRoot,
        OptionalFormReference reference,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!reference.IsSpecified || reference.Value is null) return;
        if (!BethesdaNpcAppearanceOverrideAdapter.OutfitExists(
                dataRoot,
                reference.Value.Value))
        {
            diagnostics.Add(Error(
                $"npc-appearance-override-{role}-outfit-invalid",
                $"The requested {role} outfit does not resolve to an OTFT record in the copied provider root."));
        }
    }
}
