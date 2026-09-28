using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimFaceGenNpcBakeService
{
    private sealed record RecordAppearanceBinding(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> Authorities,
        SkyrimFaceGenRecordAppearance Appearance,
        SkyrimFaceRecordRoute Route);

    private async ValueTask<RecordAppearanceBinding?> BindRecordAppearanceAsync(
        FaceGenNpcBakeRequest request, ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await new SkyrimFaceRecordPluginAuthorityLoader(workspacePolicy, labRoot)
                .LoadAsync(new(request.Edition, request.DataRoot, request.PluginOrder)
                { StagedPluginAuthorities = request.StagedPluginAuthorities }, cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, loaded.Diagnostics);
            if (!loaded.Accepted) throw new InvalidDataException("Copied appearance providers could not be bound.");
            var appearance = await new BethesdaSkyrimFaceGenAppearanceReader(workspacePolicy, labRoot)
                .ReadAsync(new(request.Target.OriginatingPlugin, request.Target.FormId),
                    loaded.Authorities, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(appearance.WinningPlugin.Value, request.Target.WinningPlugin.Value,
                    StringComparison.OrdinalIgnoreCase) || appearance.Sex != request.Target.Sex ||
                !SameReference(appearance.Race, request.Target.Race) || request.Target.HeadParts.IsDefault ||
                !appearance.HeadParts.Select(ReferenceKey).SequenceEqual(
                    request.Target.HeadParts.Select(ReferenceKey), StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("The caller's NPC winner, sex, RACE, or ordered PNAM differs from the winning copied record.");
            if (request.EffectiveHairColorPackedRgb is { } supplied && supplied != appearance.HairColorPackedRgb)
                throw new InvalidDataException("The caller's hair colour differs from the winning NPC HCLF/CLFM authority.");
            var resolved = await new BethesdaSkyrimFaceRecordRouteResolver(workspacePolicy, labRoot)
                .ResolveAsync(new(request.Edition, appearance.Race, appearance.Sex,
                    appearance.HeadParts.Select(part => new SkyrimFaceRecordHeadPartSelection(part, [])).ToImmutableArray(),
                    loaded.Authorities), cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, resolved.Diagnostics);
            if (!resolved.Accepted || resolved.Route is null)
                throw new InvalidDataException("Winning PNAM/RACE head-part and texture-set authority could not be resolved.");
            return new(loaded.Authorities, appearance, resolved.Route);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error(FaceGenDiagnosticCodes.RecordCarrierMismatch, exception.Message));
            return null;
        }
    }

    private async ValueTask<bool> VerifyRecordAuthoritiesAsync(
        FaceGenNpcBakeRequest request, RecordAppearanceBinding binding,
        ImmutableArray<Diagnostic>.Builder diagnostics, CancellationToken cancellationToken)
    {
        var loaded = await new SkyrimFaceRecordPluginAuthorityLoader(workspacePolicy, labRoot)
            .LoadAsync(new(request.Edition, request.DataRoot, request.PluginOrder)
            { StagedPluginAuthorities = binding.Authorities }, cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, loaded.Diagnostics);
        if (loaded.Accepted) return true;
        diagnostics.Add(Error(FaceGenDiagnosticCodes.RecordCarrierMismatch,
            "The copied record authority changed while the FaceGen pair was staged."));
        return false;
    }

    private async ValueTask<bool> VerifyRecordCarrierAsync(
        WorkspacePath dataRoot, RecordAppearanceBinding binding, SkyrimNativeFaceGeomBuildArtifact artifact,
        WorkspacePath nif, ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!artifact.PluginAuthorities.SequenceEqual(binding.Authorities))
                throw new InvalidDataException("Native carrier evidence is not bound to the admitted appearance providers.");

            // Native discovery and its existing exclusion verifier remain the admission authority.
            // Record-only members stay in PNAM, but do not require a shape in the owned carrier.
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in artifact.ExternalHeadParts)
                if (FormReference.TryParse(row.SourceFormIdentifier, out FormReference root))
                    excluded.Add(ReferenceKey(root));
            bool changed;
            do
            {
                changed = false;
                foreach (var part in binding.Route.HeadParts)
                    if (part.Parent is { } parent && excluded.Contains(ReferenceKey(parent)))
                        changed |= excluded.Add(ReferenceKey(part.Reference));
            } while (changed);
            foreach (var descriptor in artifact.ExternalHeadPartDependencies ?? [])
                foreach (var member in descriptor.Members) excluded.Add(ReferenceKey(member.OriginForm));
            foreach (var omitted in artifact.OmittedShaderlessDummies ?? [])
            {
                var part = binding.Route.HeadParts.Single(item => SameReference(item.Reference, omitted.HeadPart) &&
                    item.ModelNif == omitted.ModelNif);
                var authority = artifact.AssetAuthorities.Single(item => item.AssetPath == omitted.ModelNif &&
                    item.ContentSha256 == omitted.ModelSha256);
                var source = new SkyrimAssetContentAuthority(authority.ProviderId,
                    authority.ProviderKind switch
                    {
                        AssetProviderKind.Loose => SkyrimAssetContentProviderKind.Loose,
                        AssetProviderKind.Archive => SkyrimAssetContentProviderKind.Bsa,
                        _ => throw new InvalidDataException("Dummy model has an unsupported provider kind.")
                    }, authority.ProviderPath, authority.ProviderSha256, authority.AssetPath,
                    authority.ContentLength, authority.ContentSha256);
                var resolved = await new SkyrimAssetContentResolver(workspacePolicy, labRoot)
                    .ResolveAsync(new(dataRoot, [source]), cancellationToken).ConfigureAwait(false);
                AddDistinct(diagnostics, resolved.Diagnostics);
                if (!resolved.Resolved || resolved.Assets.Length != 1)
                    throw new InvalidDataException($"Omitted dummy model '{omitted.ModelNif}' failed bound source readback.");
                var model = resolved.Assets[0];
                var geometry = new SseSelectedHeadpartNifGeometryReader().Read(new(
                    model.AssetPath, model.ContentSha256, model.Content));
                if (!geometry.Accepted || geometry.Document?.Shapes is not [{ IsShaderlessDummy: true } shape] ||
                    !string.Equals(shape.Name, omitted.ModelShapeName, StringComparison.Ordinal) ||
                    !excluded.Add(ReferenceKey(part.Reference)))
                    throw new InvalidDataException($"Omitted shape '{omitted.ModelNif}/{omitted.ModelShapeName}' lacks unique reader admission.");
            }
            var owned = binding.Route.HeadParts.Where(part => !excluded.Contains(ReferenceKey(part.Reference))).ToArray();
            if (owned.Length != artifact.Shapes.Length || owned.Any(part =>
                    artifact.Shapes.Count(shape => SameReference(shape.HeadPart, part.Reference) &&
                        shape.EffectiveType == part.EffectiveType && shape.ModelNif == part.ModelNif &&
                        string.Equals(shape.OutputShapeName, part.EditorId, StringComparison.Ordinal)) != 1))
                throw new InvalidDataException("Materialized head-part evidence does not match the winning PNAM/RACE route and EditorIDs.");

            var hairParts = owned.Where(part => part.EffectiveType == NpcHeadPartType.Hair).ToArray();

            // Verify an expected assembly rebound to records, not the native caller's appearance fields.
            // The carrier verifier rebuilds in memory and reopens exact staged bytes. Model-rest
            // placement intake is intentionally separate: assembled FaceGen bone transforms differ.
            var proposal = artifact.Materialization.Proposal;
            if (proposal.Assembly.Parts.Length != owned.Length)
                throw new InvalidDataException("Carrier assembly membership differs from the winning head-part route.");
            var expectedParts = ImmutableArray.CreateBuilder<SseFaceGeomCarrierAssemblyPart>(owned.Length);
            foreach (var part in owned)
            {
                var source = proposal.Assembly.Parts.Single(item => SameReference(item.HeadPart, part.Reference) &&
                    item.SourcePath == part.ModelNif);
                bool usesFaceTint = part.EffectiveType == NpcHeadPartType.Face;
                ImmutableArray<string> slots = [];
                if (part.TextureSet is not null)
                {
                    var plan = new FinalFaceGeomTexturePlanService().Plan(new(
                        Enumerable.Repeat(string.Empty, 8).ToImmutableArray(), part.TextureSet,
                        usesFaceTint, CanonicalPaths(artifact.Target).Dds));
                    if (!plan.Accepted) throw new InvalidDataException("Winning head-part TXST slots could not be bound.");
                    slots = plan.NifSlots;
                }
                expectedParts.Add(source with
                {
                    OutputShapeName = part.EditorId,
                    UsesFaceTint = usesFaceTint,
                    TextureSetOverride = slots,
                    HairTintPackedRgb = part.EffectiveType == NpcHeadPartType.Hair ? binding.Appearance.HairColorPackedRgb : null
                });
            }
            var verification = await faceGeomVerifier.VerifyAsync(proposal with
            {
                OutputNif = nif,
                Assembly = proposal.Assembly with { Parts = expectedParts.ToImmutable() }
            }, cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, verification.Diagnostics);
            if (!verification.Verified || verification.OutputSha256 != artifact.Materialization.OutputSha256)
                throw new InvalidDataException("The staged carrier bytes differ from the winning head-part names, texture sets, or HCLF/CLFM colour.");

            if (hairParts.Length != 0 && binding.Appearance.HairColorPackedRgb is { } expectedHairColor)
            {
                var hair = await new FaceGeomHairRegionsAnalyzer(labRoot).AnalyzeAsync(nif,
                    artifact.Materialization.OutputSha256, null, cancellationToken).ConfigureAwait(false);
                string expected = "#" + expectedHairColor.ToString("X6");
                if (hairParts.Any(part => hair.Regions.Count(region => region.Name == part.EditorId &&
                        region.CurrentColor == expected) != 1))
                    throw new InvalidDataException("The staged carrier HairTint differs from the winning NPC HCLF/CLFM colour.");
            }
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error(FaceGenDiagnosticCodes.RecordCarrierMismatch, exception.Message));
            return false;
        }
    }

    private static string ReferenceKey(FormReference reference) => $"{reference.Plugin.Value}|{reference.FormId.Value:X8}";
    private static bool SameReference(FormReference left, FormReference right) =>
        string.Equals(ReferenceKey(left), ReferenceKey(right), StringComparison.OrdinalIgnoreCase);
}
