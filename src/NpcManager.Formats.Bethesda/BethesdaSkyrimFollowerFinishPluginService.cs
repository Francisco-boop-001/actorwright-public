using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Composes source inspection, frozen Skyrim authority admission, core write,
/// minimal placement write, and independent core/world readback.
/// Filesystem promotion and package/archive transactions remain later stages.
/// </summary>
public sealed class BethesdaSkyrimFollowerFinishPluginService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) :
    ISkyrimFollowerFinishPluginService
{
    private readonly BethesdaSkyrimFollowerFinishSourceReader _sourceReader =
        new();
    private readonly BethesdaSkyrimFollowerFinishCoreWriter _coreWriter =
        new();
    private readonly BethesdaSkyrimFollowerFinishCoreVerifier _coreVerifier =
        new();
    private readonly BethesdaSkyrimFollowerFinishPlacementWriter
        _placementWriter = new();
    private readonly BethesdaSkyrimFollowerFinishWorldVerifier
        _worldVerifier = new();

    public ValueTask<SkyrimFollowerFinishPluginSnapshot> InspectAsync(
        SkyrimFollowerFinishRequest request,
        WorkspacePath extractedPlugin,
        CancellationToken cancellationToken) =>
        _sourceReader.InspectAsync(
            request,
            extractedPlugin,
            cancellationToken);

    public async ValueTask<SkyrimFollowerFinishPluginWriteResult> WriteAsync(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishProposal proposal,
        WorkspacePath extractedPlugin,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        string? corePath = null;
        string? outputStage = null;
        try
        {
            ValidateEnvelope(
                request,
                proposal,
                extractedPlugin,
                outputPlugin,
                diagnostics);
            if (HasErrors(diagnostics))
                return WriteResult(diagnostics);

            PlacementAdmission admission =
                RevalidateAuthorities(request, diagnostics);
            if (HasErrors(diagnostics))
                return WriteResult(diagnostics);
            cancellationToken.ThrowIfCancellationRequested();

            SkyrimFollowerFinishPluginSnapshot sourceSnapshot =
                await _sourceReader.InspectAsync(
                    request,
                    extractedPlugin,
                    cancellationToken);
            diagnostics.AddRange(sourceSnapshot.Diagnostics);
            if (!SnapshotMatches(
                    sourceSnapshot,
                    proposal.SourceSnapshot))
                diagnostics.Add(Error(
                    "follower-finish-plugin-source-snapshot",
                    "The reopened source snapshot differs from the persisted proposal."));
            if (HasErrors(diagnostics))
                return WriteResult(diagnostics);

            SkyrimMod source = SkyrimMod.CreateFromBinary(
                new ModPath(
                    ModKey.FromNameAndExtension(
                        request.Source.Plugin.Value),
                    new FilePath(extractedPlugin.Value)),
                SkyrimRelease.SkyrimSE);
            SkyrimMod core = _coreWriter.Write(
                source,
                proposal,
                admission.Sandbox);
            string parent = Path.GetDirectoryName(outputPlugin.Value)
                ?? throw new InvalidDataException(
                    "The output plugin has no parent directory.");
            Directory.CreateDirectory(parent);
            string nonce = Guid.NewGuid().ToString("N");
            corePath = Path.Combine(
                parent,
                $".{Path.GetFileName(outputPlugin.Value)}.{nonce}.core");
            outputStage = Path.Combine(
                parent,
                $".{Path.GetFileName(outputPlugin.Value)}.{nonce}.stage");
            WriteBinary(core, corePath);
            BethesdaSkyrimFollowerFinishCoreVerification coreVerified =
                _coreVerifier.Verify(
                    extractedPlugin,
                    new WorkspacePath(corePath),
                    proposal,
                    admission.Sandbox);
            diagnostics.AddRange(coreVerified.Diagnostics);
            if (!coreVerified.Verified ||
                HasErrors(diagnostics))
                return WriteResult(diagnostics);

            SkyrimMod placed = _placementWriter.Write(
                core,
                proposal,
                admission.CellGrid,
                admission.Marker);
            WriteBinary(placed, outputStage);
            SkyrimStructuralWorldspaceRecordSanitizer
                .RemoveRequiredPartialMasterRecord(
                    outputStage,
                    request.Placement.Worldspace.Plugin.Value,
                    request.Placement.Worldspace.FormId.Value);
            SkyrimStructuralWorldspaceRecordSanitizer
                .MinimizeRequiredExteriorCellRecord(
                    outputStage,
                    request.Placement.Worldspace.Plugin.Value,
                    request.Placement.Worldspace.FormId.Value,
                    request.Placement.Cell.FormId.Value,
                    admission.CellGrid.X,
                    admission.CellGrid.Y);
            BethesdaSkyrimFollowerFinishWorldVerification worldVerified =
                _worldVerifier.Verify(
                    new WorkspacePath(outputStage),
                    proposal,
                    admission.CellGrid);
            diagnostics.AddRange(worldVerified.Diagnostics);
            if (!worldVerified.Verified ||
                HasErrors(diagnostics))
                return WriteResult(diagnostics);

            File.Move(outputStage, outputPlugin.Value);
            outputStage = null;
            Sha256Hash outputHash = HashFile(outputPlugin.Value);
            diagnostics.Add(new Diagnostic(
                "follower-finish-plugin-written",
                DiagnosticSeverity.Info,
                "The hash-bound core and minimal exterior placement were written and independently reopened."));
            return new SkyrimFollowerFinishPluginWriteResult(
                true,
                outputPlugin,
                outputHash,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or
                OverflowException or JsonException)
        {
            diagnostics.Add(Error(
                "follower-finish-plugin-write-failed",
                exception.Message));
            return WriteResult(diagnostics);
        }
        finally
        {
            DeleteTemporary(corePath);
            DeleteTemporary(outputStage);
        }
    }

    public ValueTask<SkyrimFollowerFinishPluginVerification> VerifyAsync(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishProposal proposal,
        WorkspacePath sourcePlugin,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        string? strippedPath = null;
        try
        {
            ValidateEnvelope(
                request,
                proposal,
                sourcePlugin,
                outputPlugin,
                diagnostics,
                outputMustNotExist: false);
            if (!File.Exists(outputPlugin.Value) ||
                Directory.Exists(outputPlugin.Value))
                diagnostics.Add(Error(
                    "follower-finish-plugin-verify-output",
                    "The output plugin is not an ordinary file."));
            PlacementAdmission admission =
                RevalidateAuthorities(request, diagnostics);
            if (HasErrors(diagnostics))
                return ValueTask.FromResult(
                    VerificationResult(
                        null,
                        null,
                        [],
                        diagnostics));

            SkyrimMod output = SkyrimMod.CreateFromBinary(
                new ModPath(
                    ModKey.FromNameAndExtension(
                        request.Source.Plugin.Value),
                    new FilePath(outputPlugin.Value)),
                SkyrimRelease.SkyrimSE);
            var stripped = (SkyrimMod)output.DeepCopy();
            stripped.Worldspaces.Clear();
            stripped.Cells.Clear();
            string parent = Path.GetDirectoryName(outputPlugin.Value)
                ?? throw new InvalidDataException(
                    "The output plugin has no parent directory.");
            strippedPath = Path.Combine(
                parent,
                $".{Path.GetFileName(outputPlugin.Value)}." +
                $"{Guid.NewGuid():N}.core-verify");
            WriteBinary(stripped, strippedPath);
            BethesdaSkyrimFollowerFinishCoreVerification core =
                _coreVerifier.Verify(
                    sourcePlugin,
                    new WorkspacePath(strippedPath),
                    proposal,
                    admission.Sandbox);
            diagnostics.AddRange(core.Diagnostics);
            BethesdaSkyrimFollowerFinishWorldVerification world =
                _worldVerifier.Verify(
                    outputPlugin,
                    proposal,
                    admission.CellGrid);
            diagnostics.AddRange(world.Diagnostics);
            Sha256Hash sourceHash = HashFile(sourcePlugin.Value);
            Sha256Hash outputHash = HashFile(outputPlugin.Value);
            bool verified =
                core.Verified &&
                world.Verified &&
                sourceHash == request.Source.PluginSha256 &&
                !HasErrors(diagnostics);
            return ValueTask.FromResult(
                new SkyrimFollowerFinishPluginVerification(
                    verified,
                    sourceHash,
                    outputHash,
                    proposal.ExistingRecordChanges,
                    proposal.NewRecords,
                    world.RawGroupTreeSurface,
                    false,
                    diagnostics.ToImmutable()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or
                OverflowException or JsonException)
        {
            diagnostics.Add(Error(
                "follower-finish-plugin-verify-failed",
                exception.Message));
            return ValueTask.FromResult(
                VerificationResult(
                    File.Exists(sourcePlugin.Value)
                        ? HashFile(sourcePlugin.Value)
                        : null,
                    File.Exists(outputPlugin.Value)
                        ? HashFile(outputPlugin.Value)
                        : null,
                    [],
                    diagnostics));
        }
        finally
        {
            DeleteTemporary(strippedPath);
        }
    }

    private void ValidateEnvelope(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishProposal proposal,
        WorkspacePath sourcePlugin,
        WorkspacePath outputPlugin,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        bool outputMustNotExist = true)
    {
        if (!RequestsMatch(proposal.Request, request) ||
            proposal.Operation != request.Operation ||
            proposal.RuntimeAuthority)
            diagnostics.Add(Error(
                "follower-finish-plugin-proposal",
                "The proposal envelope differs from the write request."));
        diagnostics.AddRange(
            policy.EvaluateReadRoot(labRoot, sourcePlugin));
        diagnostics.AddRange(
            policy.Evaluate(labRoot, outputPlugin));
        if (!File.Exists(sourcePlugin.Value) ||
            Directory.Exists(sourcePlugin.Value))
            diagnostics.Add(Error(
                "follower-finish-plugin-source",
                "The source plugin is not an ordinary file."));
        if (outputMustNotExist &&
            (File.Exists(outputPlugin.Value) ||
             Directory.Exists(outputPlugin.Value)))
            diagnostics.Add(Error(
                "follower-finish-plugin-output-exists",
                "The output plugin path already exists."));
        if (!string.Equals(
                Path.GetFileName(sourcePlugin.Value),
                request.Source.Plugin.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                Path.GetFileName(outputPlugin.Value),
                request.Source.Plugin.Value,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error(
                "follower-finish-plugin-name",
                "Source and output filenames must retain the request plugin identity."));
        if (File.Exists(sourcePlugin.Value) &&
            HashFile(sourcePlugin.Value) !=
            request.Source.PluginSha256)
            diagnostics.Add(Error(
                "follower-finish-plugin-source-hash",
                "The source plugin hash differs from the request."));
    }

    private PlacementAdmission RevalidateAuthorities(
        SkyrimFollowerFinishRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        SkyrimFollowerFinishExternalAuthorities? authorities =
            request.ExternalAuthorities;
        if (authorities is null)
        {
            diagnostics.Add(Error(
                "follower-finish-plugin-authorities",
                "External placement/provider authorities are required."));
            return default;
        }
        diagnostics.AddRange(policy.EvaluateReadRoot(
            labRoot,
            authorities.PlacementEvidence.Path));
        foreach (SkyrimFollowerFinishPluginProviderAuthority provider in
                 authorities.Providers)
            diagnostics.AddRange(policy.EvaluateReadRoot(
                labRoot,
                provider.Path));
        if (HasErrors(diagnostics))
            return default;

        using JsonDocument evidence = ReadAndHashJson(
            authorities.PlacementEvidence.Path,
            authorities.PlacementEvidence.ByteLength,
            authorities.PlacementEvidence.Sha256);
        ValidateEvidenceDocument(evidence.RootElement);
        JsonElement target = evidence.RootElement
            .GetProperty("target");
        RequireJsonString(
            target,
            "worldspace",
            request.Placement.Worldspace.ToString());
        RequireJsonString(
            target,
            "cell",
            request.Placement.Cell.ToString());
        RequireJsonString(
            target,
            "markerBase",
            request.Placement.MarkerBase.ToString());
        P2Int grid = ReadGrid(target);
        RequireTransform(
            target.GetProperty("actor"),
            request.Placement.Actor);
        RequireTransform(
            target.GetProperty("anchor"),
            request.Placement.Anchor);

        SkyrimFollowerFinishPluginProviderAuthority[] skyrimProviders =
            authorities.Providers.Where(provider =>
                string.Equals(
                    provider.Plugin.Value,
                    "Skyrim.esm",
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (skyrimProviders.Length != 1)
        {
            diagnostics.Add(Error(
                "follower-finish-plugin-skyrim-provider-cardinality",
                "External authority requires exactly one canonical base-game provider."));
            return default;
        }
        SkyrimFollowerFinishPluginProviderAuthority skyrim =
            skyrimProviders[0];
        var snapshots =
            new List<VerifiedProviderSnapshot>();
        try
        {
            foreach (
                SkyrimFollowerFinishPluginProviderAuthority provider in
                authorities.Providers)
                snapshots.Add(
                    VerifiedProviderSnapshot.Create(provider));
            VerifiedProviderSnapshot skyrimSnapshot =
                snapshots.Single(snapshot =>
                    string.Equals(
                        snapshot.Plugin.Value,
                        skyrim.Plugin.Value,
                        StringComparison.OrdinalIgnoreCase));
            var sandboxAdmission =
                new BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission(
                    policy,
                    labRoot);
            BethesdaSkyrimFollowerFinishSandboxAuthority sandbox =
                sandboxAdmission.Admit(
                    skyrimSnapshot.Path,
                    skyrim.Sha256);
            BethesdaSkyrimFollowerFinishMarkerAuthority marker;
            try
            {
                marker =
                    BethesdaSkyrimFollowerFinishMarkerAuthorityAdmission
                        .Admit(skyrimSnapshot.Path);
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(Error(
                    "follower-finish-plugin-marker-authority",
                    GenericAuthorityMessage(exception.Message)));
                return default;
            }
            if (request.Placement.MarkerBase != marker.MarkerForm)
            {
                diagnostics.Add(Error(
                    "follower-finish-plugin-marker-authority",
                    "The request marker differs from the admitted fixed marker capability."));
                return default;
            }
            diagnostics.Add(new Diagnostic(
                "follower-finish-plugin-provider-snapshot-lock",
                DiagnosticSeverity.Info,
                "The retained immutable provider snapshot rejected write and delete access while the path-only core admission created its own hash-bound snapshot; direct same-stream core consumption remains outside this API."));
            return new PlacementAdmission(
                sandbox,
                marker,
                grid);
        }
        finally
        {
            for (int index = snapshots.Count - 1;
                 index >= 0;
                 index--)
                snapshots[index].Dispose();
        }
    }

    private static void ValidateEvidenceDocument(
        JsonElement root)
    {
        string? artifactKind =
            root.GetProperty("artifactKind").GetString();
        if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
            string.IsNullOrWhiteSpace(artifactKind) ||
            !string.Equals(
                root.GetProperty("status").GetString(),
                "PASS_COORDINATE_QUALIFIED_STATIC_RUNTIME_REQUIRED",
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Placement evidence schema, kind, or static-pass status is not admitted.");
        JsonElement navmesh =
            root.GetProperty("existingNavmeshProof");
        string? navmeshIdentity =
            navmesh.GetProperty("navmesh").GetString();
        string? navmeshConclusion =
            navmesh.GetProperty("conclusion").GetString();
        if (!IsFormReference(navmeshIdentity) ||
            string.IsNullOrWhiteSpace(navmeshConclusion) ||
            !navmeshConclusion.Contains(
                "No NAVM edit is required or admitted.",
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Placement evidence does not contain a closed existing-navmesh proof.");
        if (!string.Equals(
                root.GetProperty("clearanceSummary")
                    .GetProperty("result")
                    .GetString(),
                "PASS",
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "Placement evidence does not carry a passing clearance result.");
    }

    private static bool IsFormReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        int separator = value.LastIndexOf('|');
        if (separator <= 0 ||
            separator == value.Length - 1)
            return false;
        string plugin = value[..separator];
        string formId = value[(separator + 1)..];
        if (string.IsNullOrWhiteSpace(plugin) ||
            formId.Length is < 3 or > 10 ||
            !formId.StartsWith(
                "0x",
                StringComparison.OrdinalIgnoreCase))
            return false;
        return uint.TryParse(
            formId.AsSpan(2),
            NumberStyles.AllowHexSpecifier,
            CultureInfo.InvariantCulture,
            out _);
    }

    private static JsonDocument ReadAndHashJson(
        WorkspacePath path,
        long expectedLength,
        Sha256Hash expectedHash)
    {
        using var stream = new FileStream(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length != expectedLength)
            throw new InvalidDataException(
                "Placement evidence length differs from the proposal.");
        Sha256Hash hash = new(
            Convert.ToHexString(SHA256.HashData(stream)));
        if (hash != expectedHash)
            throw new InvalidDataException(
                "Placement evidence hash differs from the proposal.");
        stream.Position = 0;
        return JsonDocument.Parse(stream);
    }

    private static P2Int ReadGrid(JsonElement target)
    {
        int[] values = target.GetProperty("cellGrid")
            .EnumerateArray()
            .Select(value => value.GetInt32())
            .ToArray();
        if (values.Length != 2)
            throw new InvalidDataException(
                "Placement evidence cellGrid must contain two integers.");
        return new P2Int(
            checked((short)values[0]),
            checked((short)values[1]));
    }

    private static void RequireTransform(
        JsonElement element,
        SkyrimExteriorTransform expected)
    {
        double[] position = element.GetProperty("position")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        double[] rotation = element.GetProperty("rotation")
            .EnumerateArray()
            .Select(value => value.GetDouble())
            .ToArray();
        double[] actual = [.. position, .. rotation];
        double[] requested =
        [
            expected.X,
            expected.Y,
            expected.Z,
            expected.RotationX,
            expected.RotationY,
            expected.RotationZ
        ];
        if (actual.Length != 6 ||
            !actual.Zip(requested).All(pair =>
                Math.Abs(pair.First - pair.Second) < 0.0001))
            throw new InvalidDataException(
                "Placement evidence transform differs from the proposal.");
    }

    private static void RequireJsonString(
        JsonElement parent,
        string property,
        string expected)
    {
        string? actual = parent.GetProperty(property)
            .GetString();
        if (!string.Equals(
                actual,
                expected,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Placement evidence {property} differs from the proposal.");
    }

    private static void WriteBinary(
        SkyrimMod mod,
        string path) =>
        mod.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent =
                    MastersListContentOption.NoCheck,
                MastersListOrdering =
                    MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });

    private static bool SnapshotMatches(
        SkyrimFollowerFinishPluginSnapshot actual,
        SkyrimFollowerFinishPluginSnapshot expected) =>
        actual.Valid &&
        expected.Valid &&
        actual.Plugin == expected.Plugin &&
        actual.PluginSha256 == expected.PluginSha256 &&
        actual.Tes4Flags == expected.Tes4Flags &&
        actual.Masters.SequenceEqual(expected.Masters) &&
        actual.NextFormId == expected.NextFormId &&
        actual.RecordInventory.SequenceEqual(
            expected.RecordInventory) &&
        actual.ActorSubrecordDigests.SequenceEqual(
            expected.ActorSubrecordDigests) &&
        actual.HairPackedRgb == expected.HairPackedRgb &&
        actual.ActorHairColor == expected.ActorHairColor &&
        actual.DefaultOutfitNull == expected.DefaultOutfitNull &&
        actual.FactionRanks.SequenceEqual(expected.FactionRanks) &&
        actual.RelationshipRank == expected.RelationshipRank &&
        actual.RelationshipRankRawDiscriminator ==
        expected.RelationshipRankRawDiscriminator &&
        actual.AbsentSignatures.SequenceEqual(
            expected.AbsentSignatures);

    private static bool RequestsMatch(
        SkyrimFollowerFinishRequest actual,
        SkyrimFollowerFinishRequest expected) =>
        actual.SchemaVersion == expected.SchemaVersion &&
        actual.Operation == expected.Operation &&
        actual.Source == expected.Source &&
        actual.NpcEditorId == expected.NpcEditorId &&
        actual.NpcFormId == expected.NpcFormId &&
        actual.OccupiedLocalFormIds.SequenceEqual(
            expected.OccupiedLocalFormIds) &&
        actual.ExpectedRace == expected.ExpectedRace &&
        actual.ExpectedBodyRoute == expected.ExpectedBodyRoute &&
        actual.ExpectedDefaultOutfitNull ==
        expected.ExpectedDefaultOutfitNull &&
        actual.ExpectedFactionRanks.SequenceEqual(
            expected.ExpectedFactionRanks) &&
        actual.RelationshipFormId == expected.RelationshipFormId &&
        actual.ExpectedRelationshipRank ==
        expected.ExpectedRelationshipRank &&
        actual.ExpectedRelationshipRankRawDiscriminator ==
        expected.ExpectedRelationshipRankRawDiscriminator &&
        actual.Hair == expected.Hair &&
        actual.SetEslFlag == expected.SetEslFlag &&
        actual.CompactFormIds == expected.CompactFormIds &&
        actual.Sandbox == expected.Sandbox &&
        actual.Placement == expected.Placement &&
        actual.Allocation == expected.Allocation &&
        actual.AllowedNewRecords.SequenceEqual(
            expected.AllowedNewRecords) &&
        actual.AllowedExistingRecordChanges.SequenceEqual(
            expected.AllowedExistingRecordChanges) &&
        actual.AllowedPackageFiles.SequenceEqual(
            expected.AllowedPackageFiles) &&
        actual.OutputRoot == expected.OutputRoot &&
        actual.OutputZip == expected.OutputZip &&
        actual.Narrative == expected.Narrative &&
        AuthoritiesMatch(
            actual.ExternalAuthorities,
            expected.ExternalAuthorities);

    private static bool AuthoritiesMatch(
        SkyrimFollowerFinishExternalAuthorities? actual,
        SkyrimFollowerFinishExternalAuthorities? expected) =>
        actual is null && expected is null ||
        actual is not null &&
        expected is not null &&
        actual.PlacementEvidence ==
        expected.PlacementEvidence &&
        actual.Providers.SequenceEqual(expected.Providers);

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static SkyrimFollowerFinishPluginWriteResult WriteResult(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, diagnostics.ToImmutable());

    private static SkyrimFollowerFinishPluginVerification
        VerificationResult(
            Sha256Hash? source,
            Sha256Hash? output,
            ImmutableArray<string> rawSurface,
            ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            false,
            source,
            output,
            [],
            [],
            rawSurface,
            false,
            diagnostics.ToImmutable());

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code,
        string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static string GenericAuthorityMessage(string message)
    {
        int separator = message.IndexOf(':');
        return separator >= 0 &&
               separator + 1 < message.Length
            ? message[(separator + 1)..].Trim()
            : "The fixed marker capability was not admitted.";
    }

    private static void DeleteTemporary(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Unique hidden stages are never promoted or treated as evidence.
        }
    }

    private readonly record struct PlacementAdmission(
        BethesdaSkyrimFollowerFinishSandboxAuthority Sandbox,
        BethesdaSkyrimFollowerFinishMarkerAuthority Marker,
        P2Int CellGrid);

    private sealed class VerifiedProviderSnapshot : IDisposable
    {
        private readonly string _directory;
        private readonly FileStream _lock;
        private bool _disposed;

        private VerifiedProviderSnapshot(
            PluginName plugin,
            string directory,
            string path,
            FileStream snapshotLock)
        {
            Plugin = plugin;
            _directory = directory;
            Path = new WorkspacePath(path);
            _lock = snapshotLock;
        }

        public PluginName Plugin { get; }

        public WorkspacePath Path { get; }

        public static VerifiedProviderSnapshot Create(
            SkyrimFollowerFinishPluginProviderAuthority provider)
        {
            string parent = System.IO.Path.GetDirectoryName(
                provider.Path.Value) ??
                throw new InvalidDataException(
                    "A provider path has no parent directory.");
            string directory = System.IO.Path.Combine(
                parent,
                $".follower-finish-provider-{Guid.NewGuid():N}");
            string path = System.IO.Path.Combine(
                directory,
                provider.Plugin.Value);
            Directory.CreateDirectory(directory);
            FileStream? lockedSnapshot = null;
            try
            {
                using (var source = new FileStream(
                           provider.Path.Value,
                           FileMode.Open,
                           FileAccess.Read,
                           FileShare.Read,
                           1024 * 1024,
                           FileOptions.SequentialScan))
                using (var destination = new FileStream(
                           path,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.Read,
                           1024 * 1024,
                           FileOptions.SequentialScan))
                {
                    source.CopyTo(destination);
                    destination.Flush(flushToDisk: true);
                }

                lockedSnapshot = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    1024 * 1024,
                    FileOptions.SequentialScan);
                if (lockedSnapshot.Length != provider.ByteLength)
                    throw new InvalidDataException(
                        "A provider snapshot length differs from its hash-bound authority.");
                Sha256Hash hash = new(
                    Convert.ToHexString(
                        SHA256.HashData(lockedSnapshot)));
                if (hash != provider.Sha256)
                    throw new InvalidDataException(
                        "A provider snapshot hash differs from its hash-bound authority.");
                lockedSnapshot.Position = 0;
                ProveRetainedWindowsLock(path);
                var result = new VerifiedProviderSnapshot(
                    provider.Plugin,
                    directory,
                    path,
                    lockedSnapshot);
                lockedSnapshot = null;
                return result;
            }
            catch
            {
                lockedSnapshot?.Dispose();
                DeleteSnapshot(path, directory);
                throw;
            }
        }

        private static void ProveRetainedWindowsLock(string path)
        {
            bool writeRejected = false;
            try
            {
                using var unexpectedWriter = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.Read);
            }
            catch (IOException)
            {
                writeRejected = true;
            }
            catch (UnauthorizedAccessException)
            {
                writeRejected = true;
            }

            bool deleteRejected = false;
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                deleteRejected = true;
            }
            catch (UnauthorizedAccessException)
            {
                deleteRejected = true;
            }

            if (!writeRejected || !deleteRejected)
                throw new InvalidDataException(
                    "The retained provider snapshot did not reject write and delete access.");

            // The Task 4 path/hash API deliberately creates its own one-handle
            // snapshot. Changing that API is outside Task 5; retaining this
            // proven lock keeps its input immutable throughout both admissions.
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _lock.Dispose();
            DeleteSnapshot(Path.Value, _directory);
        }

        private static void DeleteSnapshot(
            string path,
            string directory)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                if (Directory.Exists(directory))
                    Directory.Delete(directory);
            }
            catch (IOException)
            {
                // A unique snapshot is never promoted or treated as evidence.
            }
        }
    }
}
