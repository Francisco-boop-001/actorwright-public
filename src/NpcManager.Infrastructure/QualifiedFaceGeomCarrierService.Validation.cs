using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class QualifiedFaceGeomCarrierService
{
    private void ValidateAnalyzePaths(
        QualifiedFaceGeomCarrierAnalyzeRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateQualificationProfile(request.QualificationProfile, diagnostics);
        ValidateKLocalPath(request.SourceNif, "source", diagnostics);
        ValidateKLocalPath(request.OutputNif, "output", diagnostics);
        if (!request.SourceNif.IsUnder(_labRoot))
            diagnostics.Add(new Diagnostic("qualified-carrier-source-outside-lab", DiagnosticSeverity.Error,
                "The source NIF must be copied under the K-only lab root."));
        if (!request.OutputNif.IsUnder(_labRoot))
            diagnostics.Add(new Diagnostic("qualified-carrier-output-outside-lab", DiagnosticSeverity.Error,
                "The output NIF must remain under the K-only lab root."));
        if (!HasNifExtension(request.SourceNif) || !HasNifExtension(request.OutputNif))
            diagnostics.Add(new Diagnostic("qualified-carrier-extension-invalid", DiagnosticSeverity.Error,
                "Source and output paths must use the .nif extension."));
        if (string.Equals(request.SourceNif.Value, request.OutputNif.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("qualified-carrier-input-output-same", DiagnosticSeverity.Error,
                "Source and output NIF paths must differ."));
        if (HasErrors(diagnostics)) return;

        diagnostics.AddRange(_policy.EvaluateReadRoot(_labRoot, request.SourceNif));
        diagnostics.AddRange(_policy.Evaluate(_labRoot, request.OutputNif));
        if (HasErrors(diagnostics)) return;

        if (!File.Exists(request.SourceNif.Value))
            diagnostics.Add(new Diagnostic("qualified-carrier-source-missing", DiagnosticSeverity.Error,
                "The hash-bound source NIF does not exist."));
        else
            AddReparseDiagnostic(diagnostics, _labRoot, request.SourceNif, "source-nif");

        var outputParent = Path.GetDirectoryName(request.OutputNif.Value);
        if (outputParent is null || !Directory.Exists(outputParent))
            diagnostics.Add(new Diagnostic("qualified-carrier-output-parent-missing", DiagnosticSeverity.Error,
                "The output directory must already exist."));
        else
        {
            var parent = new WorkspacePath(outputParent);
            diagnostics.AddRange(_policy.Evaluate(_labRoot, parent));
            AddReparseDiagnostic(diagnostics, _labRoot, parent, "output-parent");
        }

        if (File.Exists(request.OutputNif.Value) || Directory.Exists(request.OutputNif.Value))
            diagnostics.Add(new Diagnostic("qualified-carrier-output-exists", DiagnosticSeverity.Error,
                "Qualified carrier materialization never overwrites an existing path."));
        ValidateCanonicalFaceTintPath(request.TargetFaceTintPath, diagnostics);
        ValidateTargetHeadTextures(request.TargetHeadTextures, diagnostics);
        ValidateOutputRouteBinding(request.OutputNif, request.TargetFaceTintPath, diagnostics);
    }

    private void ValidateVerificationPaths(
        QualifiedFaceGeomCarrierProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateKLocalPath(proposal.SourceNif, "source", diagnostics);
        ValidateKLocalPath(proposal.OutputNif, "output", diagnostics);
        if (!proposal.SourceNif.IsUnder(_labRoot) || !proposal.OutputNif.IsUnder(_labRoot))
            diagnostics.Add(new Diagnostic("qualified-carrier-verification-outside-lab", DiagnosticSeverity.Error,
                "Verification reads only the K-local proposal source and output."));
        if (HasErrors(diagnostics)) return;

        diagnostics.AddRange(_policy.EvaluateReadRoot(_labRoot, proposal.SourceNif));
        diagnostics.AddRange(_policy.EvaluateReadRoot(_labRoot, proposal.OutputNif));
        if (HasErrors(diagnostics)) return;

        if (!File.Exists(proposal.SourceNif.Value))
            diagnostics.Add(new Diagnostic("qualified-carrier-source-missing", DiagnosticSeverity.Error,
                "The proposal source NIF does not exist."));
        if (!File.Exists(proposal.OutputNif.Value))
            diagnostics.Add(new Diagnostic("qualified-carrier-output-missing", DiagnosticSeverity.Error,
                "The materialized output NIF does not exist."));
        AddReparseDiagnostic(diagnostics, _labRoot, proposal.SourceNif, "source-nif");
        AddReparseDiagnostic(diagnostics, _labRoot, proposal.OutputNif, "output-nif");
    }

    private static void ValidateKLocalPath(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var root = Path.GetPathRoot(path.Value);
        if (!string.Equals(root, @"K:\", StringComparison.OrdinalIgnoreCase) ||
            path.Value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.Value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.Value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-k-only", DiagnosticSeverity.Error,
                $"The {role} path must be an ordinary K-local path."));
        }

        if (path.Value.IndexOf(':', 2) >= 0)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-alternate-data-stream-refused",
                DiagnosticSeverity.Error,
                $"The {role} path contains an alternate-data-stream delimiter."));
        }
    }

    private static void ValidateCanonicalFaceTintPath(
        AssetPath path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var segments = path.Value.Split('/');
        var prefixMatches = segments.Length == 7 &&
                            string.Equals(segments[0], "textures", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(segments[1], "actors", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(segments[2], "character", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(segments[3], "FaceGenData", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(segments[4], "FaceTint", StringComparison.OrdinalIgnoreCase);
        if (!prefixMatches || !IsPluginName(segments.ElementAtOrDefault(5)) ||
            !IsEightHexDds(segments.ElementAtOrDefault(6)))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-facetint-path-invalid", DiagnosticSeverity.Error,
                "The target route must be textures/actors/character/FaceGenData/FaceTint/<plugin>/<8-hex-form>.dds."));
        }

        if (path.Value.Length > 1024 || path.Value.Any(character => character is < ' ' or > '~'))
            diagnostics.Add(new Diagnostic("qualified-carrier-facetint-encoding-invalid", DiagnosticSeverity.Error,
                "The target FaceTint route must be printable ASCII and at most 1024 characters."));
    }

    private static void ValidateOutputRouteBinding(
        WorkspacePath output,
        AssetPath target,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        const string marker = "/meshes/actors/character/facegendata/facegeom/";
        var normalized = output.Value.Replace('\\', '/');
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-output-route-invalid", DiagnosticSeverity.Error,
                "The output NIF must use the canonical meshes/actors/character/FaceGenData/FaceGeom/<plugin>/<form>.nif route."));
            return;
        }

        var outputTail = normalized[(markerIndex + marker.Length)..].Split('/');
        var tintSegments = target.Value.Split('/');
        if (outputTail.Length != 2 || tintSegments.Length != 7 ||
            !string.Equals(outputTail[0], tintSegments[5], StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileNameWithoutExtension(outputTail[1]),
                Path.GetFileNameWithoutExtension(tintSegments[6]), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(outputTail[1]), ".nif", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-output-facetint-binding", DiagnosticSeverity.Error,
                "The FaceGeom output plugin/FormID route must match the requested FaceTint plugin/FormID route."));
        }
    }

    private static void ValidateTargetBinding(
        QualifiedFaceGeomCarrierAnalyzeRequest request,
        NifTextureTarget? target,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (target is null) return;
        var oldFile = Path.GetFileName(target.OriginalPath.Replace('\\', '/'));
        var newFile = Path.GetFileName(request.TargetFaceTintPath.Value);
        if (request.QualificationProfile !=
                QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete &&
            !string.Equals(oldFile, newFile, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("qualified-carrier-formid-drift", DiagnosticSeverity.Error,
                "Carrier materialization may change the FaceTint provider plugin, but not the bound FormID filename."));
        var requestedWirePath = request.TargetFaceTintPath.Value.Replace('/', '\\');
        if (string.Equals(target.OriginalPath, requestedWirePath, StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("qualified-carrier-facetint-noop", DiagnosticSeverity.Error,
                "The qualified carrier already contains the exact requested FaceTint route."));
    }

    private static void ValidatePrivateHeadTextureTarget(
        SseNifDocument source,
        NifTextureTarget? target,
        SkyrimPrivateHeadTexturePaths? targetHeadTextures,
        QualifiedFaceGeomCarrierProfile profile,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (targetHeadTextures is null || target is null) return;
        int expectedSlot = profile switch
        {
            QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape => 6,
            QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete => 6,
            QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete => 6,
            _ => -1
        };
        if (target.SlotIndex != expectedSlot || target.BlockIndex < 0 ||
            target.BlockIndex >= source.Blocks.Length ||
            source.Blocks[target.BlockIndex].Textures.Length < 8)
        {
            diagnostics.Add(new Diagnostic("qualified-carrier-private-head-texture-shape",
                DiagnosticSeverity.Error,
                $"The private-head route for profile {profile} requires FaceTint slot {expectedSlot} and at least eight texture slots."));
        }
    }

    private static void ValidateTargetHeadTextures(
        SkyrimPrivateHeadTexturePaths? target,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (target is null) return;
        var paths = new (string Role, AssetPath? Path)[]
        {
            ("diffuse", target.Diffuse),
            ("normal-or-gloss", target.NormalOrGloss),
            ("glow-or-detail", target.GlowOrDetailMap),
            ("height", target.Height),
            ("backlight-or-specular", target.BacklightMaskOrSpecular),
            ("environment-mask-or-subsurface", target.EnvironmentMaskOrSubsurfaceTint),
            ("environment", target.Environment),
            ("multilayer", target.Multilayer)
        };
        foreach (var (role, path) in paths.Where(item => item.Path is not null))
        {
            var value = path!.Value.Value;
            if (value.Length > 260 || value.Any(char.IsControl) ||
                value.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("data/", StringComparison.OrdinalIgnoreCase) ||
                !value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("qualified-carrier-private-head-texture-path-invalid",
                    DiagnosticSeverity.Error,
                    $"The private head {role} path must be a bounded traversal-free DDS path relative to Data/Textures, without a Data/ or Textures/ prefix."));
            }
        }
    }

    private static void ValidateProposalEnvelope(
        QualifiedFaceGeomCarrierProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateQualificationProfile(proposal.QualificationProfile, diagnostics);
        if (!string.Equals(proposal.SchemaVersion, "1", StringComparison.Ordinal) ||
            !string.Equals(proposal.Operation, Operation, StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("qualified-carrier-proposal-schema", DiagnosticSeverity.Error,
                "The proposal schema or operation label is unsupported."));
        if (proposal.CreationKitAuthority || proposal.RuntimeAuthority)
            diagnostics.Add(new Diagnostic("qualified-carrier-authority-overclaim", DiagnosticSeverity.Error,
                "A carrier materialization proposal cannot claim CK or runtime authority."));
        if (proposal.SourceByteLength <= 0 || proposal.ExpectedOutputByteLength <= 0 ||
            proposal.TextureSetBlockIndex < 0 || proposal.TextureSlotIndex < 0)
            diagnostics.Add(new Diagnostic("qualified-carrier-proposal-values", DiagnosticSeverity.Error,
                "The proposal contains invalid byte-length or texture-target values."));
        ValidateCanonicalFaceTintPath(proposal.TargetFaceTintPath, diagnostics);
        ValidateTargetHeadTextures(proposal.TargetHeadTextures, diagnostics);
        if (proposal.TargetHeadTexturesBindingSha256 != BindHeadTextures(proposal.TargetHeadTextures))
            diagnostics.Add(new Diagnostic("qualified-carrier-private-head-texture-binding",
                DiagnosticSeverity.Error,
                "The proposal's private-head texture binding does not match its complete typed route."));
        ValidateOutputRouteBinding(proposal.OutputNif, proposal.TargetFaceTintPath, diagnostics);
    }

    private static void ValidateQualificationProfile(
        QualifiedFaceGeomCarrierProfile profile,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Enum.IsDefined(profile))
            diagnostics.Add(new Diagnostic(
                "qualified-carrier-profile-unsupported",
                DiagnosticSeverity.Error,
                "The carrier qualification profile is unsupported."));
    }
}
