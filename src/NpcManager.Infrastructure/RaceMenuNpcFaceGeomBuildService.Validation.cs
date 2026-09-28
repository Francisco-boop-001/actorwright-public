using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class RaceMenuNpcFaceGeomBuildService
{
    private const long MaximumNifBytes = 256L * 1024 * 1024;
    private static readonly WorkspacePath LabRoot = ActorwrightWorkspace.ResolveRoot();

    private static async ValueTask<Preflight?> ValidatePreflightAsync(
        RaceMenuNpcFaceGeomBuildRequest? request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            diagnostics.Add(Error("facegeom-request-absent", "The FaceGeom build request is absent."));
            return null;
        }

        RaceMenuNpcAppearancePlan? plan = request.AppearancePlan;
        if (plan is null || !plan.IsReady || plan.Diagnostics.IsDefault ||
            plan.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(Error("facegeom-plan-not-accepted",
                "FaceGeom compilation requires a complete accepted RaceMenu appearance plan."));
            return null;
        }

        if (plan.Request.Edition != GameEdition.SkyrimSpecialEdition ||
            plan.Preset.Edition != GameEdition.SkyrimSpecialEdition ||
            plan.Provider.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error("facegeom-edition",
                "The product-owned FaceGeom bake accepts Skyrim Special Edition authorities only."));
        }
        if (plan.RaceBinding.Reference != plan.Request.References.Race)
        {
            diagnostics.Add(Error("facegeom-race-binding-drift",
                "The accepted appearance race binding differs from the NPC creation request."));
        }
        if (plan.Preset.Appearance.RaceMenu is null)
        {
            diagnostics.Add(Error("facegeom-racemenu-payload",
                "The accepted preset does not carry RaceMenu sculpt metadata."));
        }

        ValidateOrderedCustomMorphs(plan, request.OrderedCustomMorphs, diagnostics);
        ValidateNativeMorphs(request.NativeMorphs, diagnostics);
        ValidatePlanAuthorities(plan, request, diagnostics);
        ValidatePaths(request, diagnostics);
        if (HasErrors(diagnostics))
        {
            return null;
        }

        Sha256Hash sourceHash = await HashBoundedFileAsync(request.SourceCharGenNif,
            MaximumNifBytes, cancellationToken).ConfigureAwait(false);
        if (sourceHash != request.ExpectedSourceCharGenNifSha256)
        {
            diagnostics.Add(Error("facegeom-chargen-hash-mismatch",
                $"Source CharGen NIF hash {sourceHash} differs from {request.ExpectedSourceCharGenNifSha256}."));
        }

        Sha256Hash carrierHash = await HashBoundedFileAsync(request.CompleteCarrierNif,
            MaximumNifBytes, cancellationToken).ConfigureAwait(false);
        if (carrierHash != request.ExpectedCompleteCarrierNifSha256)
        {
            diagnostics.Add(Error("facegeom-carrier-hash-mismatch",
                $"Complete carrier NIF hash {carrierHash} differs from {request.ExpectedCompleteCarrierNifSha256}."));
        }

        var generatedDirectory = new WorkspacePath(Path.Combine(
            request.OwnedStagingRoot.Value,
            ".facegeom-generated-" + DeterministicStageId(request)));
        if (File.Exists(generatedDirectory.Value) || Directory.Exists(generatedDirectory.Value))
        {
            diagnostics.Add(Error("facegeom-generated-stage-exists",
                "The deterministic generated-XYZ evidence directory already exists; overwrite is refused."));
        }
        if (!generatedDirectory.IsUnder(request.OwnedStagingRoot))
        {
            diagnostics.Add(Error("facegeom-generated-stage-escape",
                "The generated-XYZ evidence directory escaped the owned staging root."));
        }

        return HasErrors(diagnostics)
            ? null
            : new Preflight(sourceHash, carrierHash, generatedDirectory);
    }

    private static void ValidatePlanAuthorities(
        RaceMenuNpcAppearancePlan plan,
        RaceMenuNpcFaceGeomBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.SourceCharGenNif != plan.Request.PresetBundle.CharGenFaceGeom ||
            request.ExpectedSourceCharGenNifSha256 !=
            plan.Request.PresetBundle.ExpectedCharGenFaceGeomSha256 ||
            request.ExpectedSourceCharGenNifSha256 != plan.CharGenFaceGeomSha256)
        {
            diagnostics.Add(Error("facegeom-chargen-plan-drift",
                "The source CharGen NIF identity is not the exact accepted preset-bundle authority."));
        }

        BlankNpcProviderBindingRequest provider = plan.Request.ProviderContext;
        if (request.CompleteCarrierNif != provider.FaceGeomCarrier ||
            request.ExpectedCompleteCarrierNifSha256 !=
            provider.ExpectedFaceGeomCarrierSha256)
        {
            diagnostics.Add(Error("facegeom-carrier-plan-drift",
                "The complete carrier NIF identity is not the accepted provider authority."));
        }

        if (plan.HeadPartDispositions.IsDefault || plan.ResolvedHeadParts.IsDefault ||
            plan.HeadPartDispositions.IsEmpty || plan.ResolvedHeadParts.IsEmpty)
        {
            diagnostics.Add(Error("facegeom-plan-headparts",
                "The accepted appearance plan has no explicit mapped headpart closure."));
            return;
        }

        RaceMenuResolvedHeadPart[] mapped = plan.HeadPartDispositions
            .Where(item => item.Kind == RaceMenuNpcHeadPartDispositionKind.MappedRecord)
            .Select(item => item.MappedHeadPart)
            .Where(item => item is not null)
            .Cast<RaceMenuResolvedHeadPart>()
            .ToArray();
        if (mapped.Length != plan.ResolvedHeadParts.Length ||
            !mapped.SequenceEqual(plan.ResolvedHeadParts))
        {
            diagnostics.Add(Error("facegeom-plan-headpart-closure",
                "ResolvedHeadParts does not exactly match mapped headpart dispositions."));
        }
        if (plan.HeadPartDispositions.Any(item =>
                item.Kind == RaceMenuNpcHeadPartDispositionKind.MappedRecord &&
                item.MappedHeadPart is null))
        {
            diagnostics.Add(Error("facegeom-plan-mapped-headpart-payload",
                "A mapped headpart disposition omitted its resolved HDPT binding."));
        }
    }

    private static void ValidateNativeMorphs(
        SkyrimFaceMorphSnapshot? snapshot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (snapshot is null || snapshot.Nam9Sliders.IsDefault ||
            snapshot.NamaValues.IsDefault)
        {
            diagnostics.Add(Error("facegeom-native-morphs",
                "The native morph snapshot must contain explicit NAM9 and NAMA arrays."));
            return;
        }
        if (snapshot.HasNam9 && (snapshot.Nam9Sliders.Length != 18 ||
                                !float.IsFinite(snapshot.Nam9Trailing) ||
                                snapshot.Nam9Sliders.Any(value => !float.IsFinite(value))))
        {
            diagnostics.Add(Error("facegeom-native-nam9",
                "A present NAM9 snapshot requires 18 finite slider values and a finite trailing value."));
        }
        if (snapshot.HasNama && snapshot.NamaValues.Length != 4)
        {
            diagnostics.Add(Error("facegeom-native-nama",
                "A present NAMA snapshot requires exactly four values."));
        }
    }

    private static void ValidateOrderedCustomMorphs(
        RaceMenuNpcAppearancePlan plan,
        ImmutableArray<SkyrimRaceMenuCustomMorphValue> ordered,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (ordered.IsDefault)
        {
            diagnostics.Add(Error("facegeom-custom-order",
                "Ordered custom morphs must be supplied explicitly, including when empty."));
            return;
        }

        var lastValues = new Dictionary<string, float>(StringComparer.Ordinal);
        bool hasInvalidSuppliedRow = false;
        foreach (SkyrimRaceMenuCustomMorphValue? row in ordered)
        {
            if (row is null)
            {
                diagnostics.Add(Error("facegeom-custom-order-row-absent",
                    "Ordered custom morph rows may not contain null entries."));
                hasInvalidSuppliedRow = true;
                continue;
            }
            if (string.IsNullOrWhiteSpace(row.Name) ||
                row.Name.Length > 512 || row.Name.Any(char.IsControl) ||
                !float.IsFinite(row.Value))
            {
                diagnostics.Add(Error("facegeom-custom-order-invalid",
                    "Ordered custom morph rows require exact names and finite values."));
                hasInvalidSuppliedRow = true;
                continue;
            }

            lastValues[row.Name] = row.Value;
        }
        if (hasInvalidSuppliedRow)
        {
            return;
        }

        ImmutableArray<SkyrimRaceMenuCustomMorphValue> parsedOrder =
            plan.Preset.Appearance.OrderedCustomMorphs;
        if (parsedOrder.IsDefault)
        {
            diagnostics.Add(Error("facegeom-custom-source-order",
                "The accepted preset must preserve its custom morph order explicitly."));
            return;
        }
        if (parsedOrder.Any(row => row is null))
        {
            diagnostics.Add(Error("facegeom-custom-source-order-row-absent",
                "The parser-preserved custom morph order contains a null row."));
            return;
        }
        if (parsedOrder.Length != ordered.Length)
        {
            diagnostics.Add(Error("facegeom-custom-source-order-count",
                "Supplied custom morph order does not cover the parser-preserved .jslot order."));
        }
        else
        {
            for (int index = 0; index < parsedOrder.Length; index++)
            {
                SkyrimRaceMenuCustomMorphValue expected = parsedOrder[index];
                SkyrimRaceMenuCustomMorphValue actual = ordered[index];
                if (!string.Equals(expected.Name, actual.Name, StringComparison.Ordinal) ||
                    BitConverter.SingleToInt32Bits(expected.Value) !=
                    BitConverter.SingleToInt32Bits(actual.Value))
                {
                    diagnostics.Add(Error("facegeom-custom-source-order-drift",
                        $"Custom morph row {index} differs from the parser-preserved .jslot order."));
                }
            }
        }

        ImmutableDictionary<string, float> planned =
            plan.Preset.Appearance.CustomMorphs;
        foreach ((string name, float foldedValue) in lastValues)
        {
            if (!planned.TryGetValue(name, out float plannedValue) ||
                BitConverter.SingleToInt32Bits(foldedValue) !=
                BitConverter.SingleToInt32Bits(plannedValue))
            {
                diagnostics.Add(Error("facegeom-custom-order-drift",
                    $"Ordered custom morph '{name}' differs from the accepted plan."));
            }
        }
    }

    private static void ValidatePaths(
        RaceMenuNpcFaceGeomBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.AllowedRoot.IsUnder(LabRoot) ||
            !Directory.Exists(request.AllowedRoot.Value))
        {
            diagnostics.Add(Error("facegeom-allowed-root",
                "The allowed root must be an existing directory under the configured Actorwright workspace."));
            return;
        }
        CheckNoReparse(LabRoot, request.AllowedRoot, "allowed-root", diagnostics);

        ValidateExistingFile(request, request.FaceBakeAuthorityManifest,
            ".json", "authority-manifest", diagnostics);
        ValidateExistingFile(request, request.SourceCharGenNif,
            ".nif", "chargen", diagnostics);
        ValidateExistingFile(request, request.CompleteCarrierNif,
            ".nif", "carrier", diagnostics);

        if (!request.OwnedStagingRoot.IsUnder(request.AllowedRoot) ||
            !Directory.Exists(request.OwnedStagingRoot.Value))
        {
            diagnostics.Add(Error("facegeom-staging-root",
                "The owned staging root must already exist beneath the allowed root."));
        }
        else
        {
            CheckNoReparse(request.AllowedRoot, request.OwnedStagingRoot,
                "staging-root", diagnostics);
        }

        string? outputParentText = Path.GetDirectoryName(request.OutputNif.Value);
        if (!request.OutputNif.IsUnder(request.OwnedStagingRoot) ||
            !string.Equals(Path.GetExtension(request.OutputNif.Value), ".nif",
                StringComparison.OrdinalIgnoreCase) || outputParentText is null ||
            !Directory.Exists(outputParentText))
        {
            diagnostics.Add(Error("facegeom-output-path",
                "The absent .nif output must have an existing parent beneath the owned staging root."));
        }
        else
        {
            CheckNoReparse(request.AllowedRoot, new WorkspacePath(outputParentText),
                "output-parent", diagnostics);
        }
        if (File.Exists(request.OutputNif.Value) || Directory.Exists(request.OutputNif.Value))
        {
            diagnostics.Add(Error("facegeom-output-exists",
                "FaceGeom compilation never overwrites an existing output path."));
        }

        WorkspacePath finalRoot = request.AppearancePlan.Request.OutputRoot;
        if (request.OwnedStagingRoot.IsUnder(finalRoot) || finalRoot.IsUnder(request.OwnedStagingRoot) ||
            request.OutputNif.IsUnder(finalRoot))
        {
            diagnostics.Add(Error("facegeom-final-root-overlap",
                "FaceGeom staging must remain disjoint from the final NPC output root."));
        }

        string[] distinct =
        [
            request.SourceCharGenNif.Value,
            request.CompleteCarrierNif.Value,
            request.OutputNif.Value
        ];
        if (distinct.Distinct(StringComparer.OrdinalIgnoreCase).Count() != distinct.Length)
        {
            diagnostics.Add(Error("facegeom-path-alias",
                "CharGen, carrier, and FaceGeom output paths must be distinct."));
        }
    }

    private static void ValidateExistingFile(
        RaceMenuNpcFaceGeomBuildRequest request,
        WorkspacePath path,
        string extension,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!path.IsUnder(request.AllowedRoot) || !File.Exists(path.Value) ||
            Directory.Exists(path.Value) ||
            !string.Equals(Path.GetExtension(path.Value), extension,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error($"facegeom-{role}-path",
                $"The {role} must be an existing {extension} file beneath the allowed root."));
            return;
        }
        CheckNoReparse(request.AllowedRoot, path, role, diagnostics);
    }

    private static void CheckNoReparse(
        WorkspacePath ancestor,
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!path.IsUnder(ancestor))
        {
            diagnostics.Add(Error($"facegeom-{role}-escape",
                $"The {role} path escaped its allowed root."));
            return;
        }
        string relative = Path.GetRelativePath(ancestor.Value, path.Value);
        string current = ancestor.Value;
        IEnumerable<string> segments = relative == "."
            ? Array.Empty<string>()
            : relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (string segment in segments.Prepend(string.Empty))
        {
            if (segment.Length > 0)
            {
                current = Path.Combine(current, segment);
            }
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error($"facegeom-{role}-reparse",
                    $"The {role} path crosses a reparse point."));
                return;
            }
        }
    }

    private static async ValueTask<Sha256Hash> HashBoundedFileAsync(
        WorkspacePath path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.Value);
        if (!info.Exists || info.Length <= 0 || info.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"'{path.Value}' must contain 1 to {maximumBytes} bytes.");
        }
        await using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read,
            FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
    }

    private static string DeterministicStageId(RaceMenuNpcFaceGeomBuildRequest request)
    {
        string identity = string.Join("\n",
            request.ExpectedFaceBakeAuthorityManifestSha256.Value,
            request.ExpectedSourceCharGenNifSha256.Value,
            request.ExpectedCompleteCarrierNifSha256.Value,
            request.OutputNif.Value.ToUpperInvariant());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..20];
    }

    private sealed record Preflight(
        Sha256Hash SourceCharGenSha256,
        Sha256Hash CarrierSha256,
        WorkspacePath GeneratedXyzDirectory);
}
