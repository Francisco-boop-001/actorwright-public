using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class RaceMenuCharGenFaceGeomMergeService
{
    private void ValidateAnalyzeRequest(
        RaceMenuCharGenFaceGeomMergeAnalyzeRequest request,
        bool outputMustBeAbsent,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Enum.IsDefined(request.QualificationProfile))
            diagnostics.Add(Error(
                "chargen-carrier-profile-unsupported",
                "The carrier qualification profile is unsupported."));
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("chargen-carrier-edition",
                "CharGen carrier merge supports Skyrim Special Edition only."));
        if (!HasPathValue(request.CharGenNif) || !HasPathValue(request.CarrierNif) ||
            !HasPathValue(request.OutputNif))
        {
            diagnostics.Add(Error("chargen-carrier-path-empty",
                "CharGen, carrier, and output paths must be initialized."));
            return;
        }
        ValidateNifInput(request.CharGenNif, "CharGen", diagnostics);
        ValidateNifInput(request.CarrierNif, "carrier", diagnostics);
        ValidateKLocalPath(request.OutputNif, "output", diagnostics);
        if (!string.Equals(Path.GetExtension(request.OutputNif.Value), ".nif",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("chargen-carrier-output-extension",
                "The output must use the .nif extension."));
        if (string.Equals(request.CharGenNif.Value, request.CarrierNif.Value,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.CharGenNif.Value, request.OutputNif.Value,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.CarrierNif.Value, request.OutputNif.Value,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("chargen-carrier-path-alias",
                "CharGen, carrier, and output paths must be distinct."));

        var outputParentText = Path.GetDirectoryName(request.OutputNif.Value);
        if (outputParentText is null || !Directory.Exists(outputParentText))
            diagnostics.Add(Error("chargen-carrier-output-parent",
                "The K-local output parent must already exist."));
        else
        {
            var outputParent = new WorkspacePath(outputParentText);
            diagnostics.AddRange(policy.Evaluate(labRoot, outputParent));
            AddReparseDiagnostics(outputParent, "output-parent", diagnostics);
        }
        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputNif));
        if (outputMustBeAbsent &&
            (File.Exists(request.OutputNif.Value) || Directory.Exists(request.OutputNif.Value)))
            diagnostics.Add(Error("chargen-carrier-output-exists",
                "CharGen carrier merge never overwrites an existing output path."));

        if (request.ShapeAuthorities.IsDefaultOrEmpty || request.ShapeAuthorities.Length > 128)
        {
            diagnostics.Add(Error("chargen-carrier-authorities",
                "One to 128 explicit shape authorities are required."));
            return;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var authority in request.ShapeAuthorities)
        {
            if (authority is null || string.IsNullOrWhiteSpace(authority.CarrierShapeName) ||
                authority.CarrierShapeName.Contains('\0') ||
                authority.CarrierShapeName.Length > 512 ||
                string.IsNullOrWhiteSpace(authority.Reason) || authority.Reason.Contains('\0') ||
                authority.Reason.Length > 2048 ||
                !names.Add(authority.CarrierShapeName))
            {
                diagnostics.Add(Error("chargen-carrier-authority-values",
                    "Shape authority names/reasons must be unique, non-empty, bounded strings."));
                continue;
            }
            switch (authority)
            {
                case RaceMenuCharGenFaceGeomCharGenXyzAuthority:
                case RaceMenuCharGenFaceGeomCarrierPreservedAuthority:
                    break;
                case RaceMenuCharGenFaceGeomExternalXyzAuthority external:
                    if (!HasPathValue(external.XyzFile))
                    {
                        diagnostics.Add(Error("chargen-carrier-oracle-values",
                            "Each external XYZ oracle path must be initialized."));
                        break;
                    }
                    ValidateKLocalPath(external.XyzFile, "external XYZ oracle", diagnostics);
                    diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, external.XyzFile));
                    AddReparseDiagnostics(external.XyzFile, "external XYZ oracle", diagnostics);
                    if (!File.Exists(external.XyzFile.Value) ||
                        Directory.Exists(external.XyzFile.Value) ||
                        external.ExpectedVertexCount <= 0)
                        diagnostics.Add(Error("chargen-carrier-oracle-values",
                            "Each external XYZ oracle must be an existing K-local file with a positive vertex count."));
                    break;
                case RaceMenuCharGenFaceGeomGeneratedXyzAuthority generated:
                    if (!HasPathValue(generated.GeneratedXyzFile))
                    {
                        diagnostics.Add(Error("chargen-carrier-generated-xyz-values",
                            "Each generated XYZ path must be initialized."));
                        break;
                    }
                    ValidateKLocalPath(generated.GeneratedXyzFile, "generated XYZ", diagnostics);
                    diagnostics.AddRange(policy.EvaluateReadRoot(
                        labRoot, generated.GeneratedXyzFile));
                    AddReparseDiagnostics(
                        generated.GeneratedXyzFile, "generated XYZ", diagnostics);
                    if (!File.Exists(generated.GeneratedXyzFile.Value) ||
                        Directory.Exists(generated.GeneratedXyzFile.Value) ||
                        generated.ExpectedVertexCount <= 0)
                        diagnostics.Add(Error("chargen-carrier-generated-xyz-values",
                            "Each generated XYZ source must be an existing K-local file with a positive vertex count."));
                    break;
                default:
                    diagnostics.Add(Error("chargen-carrier-authority-type",
                        "A shape authority has an unsupported typed route."));
                    break;
            }
        }
    }

    private void ValidateNifInput(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateKLocalPath(path, role, diagnostics);
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, path));
        AddReparseDiagnostics(path, role, diagnostics);
        if (!string.Equals(Path.GetExtension(path.Value), ".nif",
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("chargen-carrier-input-extension",
                $"The {role} input must use the .nif extension."));
        if (!File.Exists(path.Value) || Directory.Exists(path.Value))
            diagnostics.Add(Error("chargen-carrier-input-missing",
                $"The {role} input must be an existing ordinary file."));
    }

    private void ValidateVerificationOutput(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!HasPathValue(output))
        {
            diagnostics.Add(Error("chargen-carrier-path-empty",
                "The output path must be initialized."));
            return;
        }
        ValidateKLocalPath(output, "output", diagnostics);
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, output));
        AddReparseDiagnostics(output, "output", diagnostics);
        if (!File.Exists(output.Value) || Directory.Exists(output.Value))
            diagnostics.Add(Error("chargen-carrier-verification-output",
                "The proposed output NIF does not exist for verification."));
    }

    private static void ValidateProposalEnvelope(
        RaceMenuCharGenFaceGeomMergeProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!Enum.IsDefined(proposal.QualificationProfile))
            diagnostics.Add(Error(
                "chargen-carrier-profile-unsupported",
                "The proposal carrier qualification profile is unsupported."));
        if (!string.Equals(proposal.SchemaVersion, "1", StringComparison.Ordinal) ||
            !string.Equals(proposal.Operation, Operation, StringComparison.Ordinal) ||
            proposal.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("chargen-carrier-proposal-schema",
                "The proposal schema, operation, or edition is unsupported."));
        if (proposal.CreationKitAuthority || proposal.RuntimeAuthority)
            diagnostics.Add(Error("chargen-carrier-authority-overclaim",
                "A static XYZ merge cannot claim Creation Kit or runtime authority."));
        if (proposal.CarrierStructure is null || proposal.ShapeDispositions.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error("chargen-carrier-proposal-values",
                "The proposal requires a carrier structure and explicit shape dispositions."));
            return;
        }
        if (proposal.ShapeDispositions.Any(item => item is null))
        {
            diagnostics.Add(Error("chargen-carrier-proposal-dispositions",
                "The proposal may not contain null shape dispositions."));
            return;
        }
        if (proposal.CharGenByteLength <= 0 || proposal.CarrierByteLength <= 0 ||
            proposal.ExpectedOutputByteLength != proposal.CarrierByteLength ||
            proposal.CharGenBlockCount <= 0 || proposal.CharGenDynamicShapeCount <= 0 ||
            proposal.ShapeDispositions.Length != proposal.CarrierStructure.DynamicShapeCount ||
            proposal.ChangedPositionShapeCount < 0 ||
            proposal.ChangedPositionShapeCount !=
                proposal.ShapeDispositions.Count(item => item.PositionChanged) ||
            proposal.ExpandedRadiusCount !=
                proposal.ShapeDispositions.Count(item => item.RadiusChanged))
            diagnostics.Add(Error("chargen-carrier-proposal-values",
                "The proposal contains inconsistent byte, shape, change, or radius counts."));
        if (proposal.ShapeDispositions.Select(item => item.CarrierShapeName)
                .Distinct(StringComparer.Ordinal).Count() != proposal.ShapeDispositions.Length ||
            proposal.ShapeDispositions.Any(item =>
                string.IsNullOrWhiteSpace(item.CarrierShapeName) ||
                string.IsNullOrWhiteSpace(item.Reason) || item.Source is null ||
                item.Source is not (RaceMenuCharGenFaceGeomCharGenSourceEvidence or
                    RaceMenuCharGenFaceGeomExternalXyzSourceEvidence or
                    RaceMenuCharGenFaceGeomGeneratedXyzSourceEvidence or
                    RaceMenuCharGenFaceGeomCarrierPreservedSourceEvidence) ||
                item.VertexCount <= 0 || item.VertexStride != 16 ||
                item.PositionLaneLength != 12 ||
                item.CarrierVertexDataOffset < 0 || item.CarrierRadiusOffset < 0 ||
                !float.IsFinite(item.CarrierRadius) ||
                !float.IsFinite(item.RequiredRadius) ||
                !float.IsFinite(item.OutputRadius) || item.OutputRadius < item.RequiredRadius ||
                item.Source is RaceMenuCharGenFaceGeomExternalXyzSourceEvidence external &&
                external.VertexCount != item.VertexCount ||
                item.Source is RaceMenuCharGenFaceGeomGeneratedXyzSourceEvidence generated &&
                generated.VertexCount != item.VertexCount))
            diagnostics.Add(Error("chargen-carrier-proposal-dispositions",
                "The proposal contains invalid or duplicate shape dispositions."));
    }

    private void ValidateKLocalPath(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!HasPathValue(path))
        {
            diagnostics.Add(Error("chargen-carrier-path-empty",
                $"The {role} path must be initialized."));
            return;
        }
        var root = Path.GetPathRoot(path.Value);
        if (!path.IsUnder(labRoot) ||
            !string.Equals(root, @"K:\", StringComparison.OrdinalIgnoreCase) ||
            path.Value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.Value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.Value.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Value.IndexOf(':', 2) >= 0)
            diagnostics.Add(Error("chargen-carrier-k-only",
                $"The {role} path must be an ordinary path under the K-only lab root."));
    }

    private static bool HasPathValue(WorkspacePath path) =>
        !string.IsNullOrWhiteSpace(path.Value);

    private void AddReparseDiagnostics(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!path.IsUnder(labRoot)) return;
        try
        {
            var volumeRoot = Path.GetPathRoot(path.Value);
            if (string.IsNullOrEmpty(volumeRoot))
            {
                diagnostics.Add(Error("chargen-carrier-path-inspection",
                    $"The {role} path has no inspectable volume root."));
                return;
            }
            var current = volumeRoot;
            CheckReparse(current, role, diagnostics);
            foreach (var segment in Path.GetRelativePath(volumeRoot, path.Value).Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                CheckReparse(current, role, diagnostics);
                if (HasErrors(diagnostics)) return;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error("chargen-carrier-path-inspection", exception.Message));
        }
    }

    private static void CheckReparse(
        string path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if ((File.Exists(path) || Directory.Exists(path)) &&
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(Error("chargen-carrier-reparse-refused",
                $"The {role} path traverses a reparse point at '{path}'."));
    }

    private static void ValidateIncompleteCharGen(
        SseNifDocument document,
        QualifiedFaceGeomCarrierStructure structure,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (document.UserVersion != 12 || document.BethesdaStreamVersion != 100 ||
            document.Roots.Length != 1 || document.Roots[0] != 0 ||
            structure.BlockCount != structure.ReachableBlockCount ||
            structure.DynamicShapeCount <= 0 || structure.NullChildReferenceCount != 0 ||
            structure.ReachableShapeNames.Any(string.IsNullOrWhiteSpace) ||
            structure.ReachableShapeNames.Distinct(StringComparer.Ordinal).Count() !=
                structure.ReachableShapeNames.Length)
            diagnostics.Add(Error("chargen-carrier-chargen-envelope",
                "The CharGen NIF must be a fully reachable Skyrim SE envelope with one root, no null children, and distinct non-empty dynamic shapes."));
        var allDynamic = document.Blocks.Count(item =>
            string.Equals(item.Type, "BSDynamicTriShape", StringComparison.Ordinal));
        if (allDynamic != structure.DynamicShapeCount)
            diagnostics.Add(Error("chargen-carrier-chargen-hidden-shape",
                "The CharGen NIF may not hide unreachable dynamic shapes."));
    }
}
