using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Performs the read-only admission pass for a RaceMenu-derived new NPC. It
/// has no writer dependency and therefore cannot create the requested output.
/// </summary>
public sealed partial class RaceMenuNpcAppearancePlanService : IRaceMenuNpcAppearancePlanService
{
    private readonly IPresetService presetService;
    private readonly IBlankNpcProviderService providerService;
    private readonly IWorkspacePolicy policy;
    private readonly WorkspacePath labRoot;

    public RaceMenuNpcAppearancePlanService(
        IPresetService presetService,
        IBlankNpcProviderService providerService,
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        this.presetService = presetService;
        this.providerService = providerService;
        this.policy = policy;
        this.labRoot = labRoot;
    }

    public async ValueTask<RaceMenuNpcAppearancePlanResult> AnalyzeAsync(
        RaceMenuNpcBuildRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var providerResult = await providerService.QualifyAsync(
            request.ProviderContext, cancellationToken);
        diagnostics.AddRange(providerResult.Diagnostics);
        if (!providerResult.Qualified || providerResult.Artifact is null)
            return Refused(diagnostics);

        return await AnalyzeQualifiedAsync(
            request, providerResult.Artifact, diagnostics, cancellationToken);
    }

    private async ValueTask<RaceMenuNpcAppearancePlanResult> AnalyzeQualifiedAsync(
        RaceMenuNpcBuildRequest request,
        BlankNpcProviderArtifact provider,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var bundle = await ReadBundleAsync(request, provider, diagnostics, cancellationToken);
        if (bundle is null || HasErrors(diagnostics)) return Refused(diagnostics);

        await VerifyStaticBundleFilesAsync(request.PresetBundle, diagnostics, cancellationToken);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var presetResult = await presetService.InspectAsync(
            new PresetParseRequest(PresetFormat.RaceMenuJslot, request.Edition,
                request.PresetBundle.PresetPath), cancellationToken);
        diagnostics.AddRange(presetResult.Diagnostics);
        if (presetResult.Document is null || presetResult.Document.SourceHash !=
            request.PresetBundle.ExpectedPresetSha256 || HasErrors(diagnostics))
        {
            if (presetResult.Document is not null && presetResult.Document.SourceHash !=
                request.PresetBundle.ExpectedPresetSha256)
            {
                diagnostics.Add(Error("racemenu-plan-preset-hash-drift",
                    "The parsed .jslot hash no longer matches the admitted bundle."));
            }
            return Refused(diagnostics);
        }

        var sourceDependencies = BuildSourceDependencies(presetResult.Document.Appearance);
        var authority = await ReadRecordAuthorityAsync(
            request.PresetBundle.RecordAuthority,
            request,
            presetResult.Document.Appearance,
            diagnostics,
            cancellationToken);
        if (authority is null || HasErrors(diagnostics)) return Refused(diagnostics);

        var resolved = ResolvePortableReferences(
            presetResult.Document.Appearance, authority, diagnostics);
        sourceDependencies = MergeSourceDependencies(sourceDependencies, authority);
        var pluginAuthorities = BuildPluginAuthorities(request, authority, diagnostics);
        var requiredOutputMasters = BuildRequiredOutputMasters(
            provider.TemplateMasters, request, authority, resolved, diagnostics);
        var runtimeRoutes = await ReadRuntimeRoutesAsync(
            request.PresetBundle.RuntimeRoutes, bundle.BundleId, diagnostics, cancellationToken);
        var coverage = BuildCoverage(
            presetResult.Document.Appearance, resolved, runtimeRoutes,
            request.PresetBundle, authority, diagnostics);

        diagnostics.Add(new Diagnostic("racemenu-plan-runtime-authority-not-claimed",
            DiagnosticSeverity.Info,
            "The plan selects preservation routes but does not claim that Skyrim loaded or rendered them."));
        var plan = new RaceMenuNpcAppearancePlan(
            "1",
            bundle.BundleId,
            request,
            presetResult.Document,
            provider,
            bundle.ManifestSha256,
            authority.AuthorityId,
            authority.ManifestSha256,
            request.PresetBundle.ExpectedCharGenFaceGeomSha256,
            request.PresetBundle.ExpectedCharGenFaceTintSha256,
            authority.RaceBinding,
            authority.HeadPartDispositions,
            resolved.HeadParts,
            resolved.HeadTexture,
            resolved.HairColor,
            authority.TintDispositions,
            authority.TintLayers,
            authority.Qnam,
            sourceDependencies,
            requiredOutputMasters,
            coverage,
            diagnostics.ToImmutable(),
            RuntimeAuthority: false)
        {
            PluginAuthorities = pluginAuthorities
        };
        return new RaceMenuNpcAppearancePlanResult(
            plan.IsReady,
            plan,
            diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidateRequest(RaceMenuNpcBuildRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition ||
            request.ProviderContext.Edition != request.Edition)
        {
            diagnostics.Add(Error("racemenu-plan-edition-mismatch",
                "RaceMenu NPC planning accepts only matching Skyrim SE requests and providers."));
        }
        if (request.Traits.Sex != request.ProviderContext.Sex)
        {
            diagnostics.Add(Error("racemenu-plan-sex-authority-mismatch",
                "The explicitly requested NPC sex does not match the qualified provider context."));
        }
        if (request.References.Race.FormId.Value == 0)
        {
            diagnostics.Add(Error("racemenu-plan-race-authority-missing",
                "A non-zero explicit race reference is required because .jslot does not own race authority."));
        }
        if (Directory.Exists(request.OutputRoot.Value) || File.Exists(request.OutputRoot.Value))
        {
            diagnostics.Add(Error("racemenu-plan-output-exists",
                "The future build output root must not already exist."));
        }

        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputRoot));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.PresetBundle.ManifestPath));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.PresetBundle.PresetPath));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.PresetBundle.CharGenFaceGeom));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.PresetBundle.CharGenFaceTint));
        diagnostics.AddRange(policy.EvaluateReadRoot(
            labRoot, request.PresetBundle.RecordAuthority.ManifestPath));
        if (request.PresetBundle.RuntimeRoutes is { } runtime)
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, runtime.ManifestPath));
        return diagnostics.ToImmutable();
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuNpcAppearancePlanResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
