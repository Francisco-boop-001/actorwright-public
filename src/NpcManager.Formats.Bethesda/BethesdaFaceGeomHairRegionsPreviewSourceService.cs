using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Resolves only the texture closure embedded in an exact proposed FaceGeom.
/// It deliberately does not compose an NPC record graph and therefore does
/// not inherit the full viewer's UBE refusal.
/// </summary>
public sealed class BethesdaFaceGeomHairRegionsPreviewSourceService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) :
    IFaceGeomHairRegionsPreviewSourceService
{
    private readonly BethesdaNpcVisualSourceComposer composer =
        new(policy, labRoot);

    public ValueTask<FaceGeomHairRegionsPreviewSourceResult>
        ComposeAsync(
            FaceGeomHairRegionsPreviewSourceRequest request,
            CancellationToken cancellationToken) =>
        composer.ComposeHairRegionsSourceAsync(
            request,
            cancellationToken);
}

public sealed partial class BethesdaNpcVisualSourceComposer
{
    private const long MaximumHairRegionsCandidateBytes =
        128L * 1024L * 1024L;
    private const long MaximumHairRegionsLoadOrderBytes =
        1024L * 1024L;
    private const int MaximumHairRegionsLoadOrderEntries =
        4096;

    internal async ValueTask<
        FaceGeomHairRegionsPreviewSourceResult>
        ComposeHairRegionsSourceAsync(
            FaceGeomHairRegionsPreviewSourceRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        if (!ValidateHairRegionsSourceRequest(
                request,
                diagnostics))
            return RefusedHairRegionsSource(diagnostics);

        byte[] candidateOnDisk;
        try
        {
            candidateOnDisk = await File.ReadAllBytesAsync(
                request.Candidate.Path.Value,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-read",
                exception.Message));
            return RefusedHairRegionsSource(diagnostics);
        }
        Sha256Hash candidateHash = Hash(candidateOnDisk);
        if (candidateOnDisk.LongLength !=
                request.Candidate.ByteLength ||
            candidateHash != request.Candidate.Sha256 ||
            !candidateOnDisk.AsSpan().SequenceEqual(
                request.CandidateBytes.AsSpan()))
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-stale",
                "The staged candidate no longer equals the exact in-memory proposal bytes."));
            return RefusedHairRegionsSource(diagnostics);
        }

        PreviewAssetAuthorityPlan? authorityPlan =
            await BuildHairRegionsAuthorityPlanAsync(
                request.Intake,
                diagnostics,
                cancellationToken);
        if (authorityPlan is null ||
            HasErrors(diagnostics))
            return RefusedHairRegionsSource(diagnostics);

        var resolver = new PreviewAssetResolver(
            request.Intake.DataRoot,
            null,
            request.OutputDataRoot,
            diagnostics,
            policy,
            labRoot,
            authorityPlan);
        var materialized =
            new Dictionary<string, NpcVisualAsset>(
                StringComparer.OrdinalIgnoreCase);
        ImmutableArray<NpcVisualMaterial> materials =
            await ResolveNifMaterialsAsync(
                request.Candidate.Path.Value,
                request.CandidateBytes.ToArray(),
                resolver,
                materialized,
                diagnostics,
                cancellationToken);
        if (HasErrors(diagnostics))
            return RefusedHairRegionsSource(diagnostics);

        ImmutableArray<FaceGeomHairTextureAuthority>
            textures =
            FaceGeomHairTextureAuthorityCanonical.Order(
                materialized.Values
                    .Where(item =>
                        item.Role ==
                        NpcVisualAssetRole.Texture)
                    .Select(item =>
                    {
                        if (!authorityPlan.Winners.TryGetValue(
                                item.AssetPath.Value,
                                out PreviewAssetWinner? winner) ||
                            winner.Sha256 != item.Sha256 ||
                            winner.Bytes != item.Bytes ||
                            !string.Equals(
                                winner.Provider,
                                item.Provider,
                                StringComparison.Ordinal))
                            throw new InvalidDataException(
                                $"Materialized texture '{item.AssetPath}' differs from its reviewed winning provider.");
                        return new FaceGeomHairTextureAuthority(
                            item.AssetPath,
                            winner.Kind,
                            winner.Provider,
                            item.Sha256,
                            item.Bytes,
                            item.MaterializedPath);
                    }));
        var candidate = new NpcVisualAsset(
            NpcVisualAssetRole.FaceGeom,
            new AssetPath(
                "meshes/npcmanager/hair-regions/candidate.nif"),
            $"exact-proposal:{request.Candidate.Sha256.Value}",
            request.Candidate.Sha256,
            request.Candidate.ByteLength,
            request.Candidate.Path,
            false,
            materials);
        var source = new FaceGeomHairRegionsPreviewSource(
            candidate,
            textures,
            FaceGeomHairTextureAuthorityCanonical
                .Fingerprint(textures));
        return new FaceGeomHairRegionsPreviewSourceResult(
            true,
            source,
            diagnostics.ToImmutable());
    }

    private bool ValidateHairRegionsSourceRequest(
        FaceGeomHairRegionsPreviewSourceRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.CandidateBytes.IsDefaultOrEmpty ||
            request.Candidate.ByteLength is <= 0 or
                > MaximumHairRegionsCandidateBytes ||
            request.CandidateBytes.Length !=
                request.Candidate.ByteLength ||
            Hash(request.CandidateBytes.AsSpan()) !=
                request.Candidate.Sha256)
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-candidate",
                "The exact proposed FaceGeom bytes are missing or do not match their length/hash authority."));
        if (request.Intake.Edition !=
                GameEdition.SkyrimSpecialEdition ||
            request.Intake.RuntimeAuthority)
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-intake",
                "A reviewed authority-false Skyrim SE/AE intake is required."));
        if (!request.Candidate.Path.IsUnder(labRoot) ||
            !File.Exists(request.Candidate.Path.Value) ||
            Directory.Exists(request.Candidate.Path.Value))
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-path",
                "The staged candidate must be an existing K-local ordinary file."));
        else
            diagnostics.AddRange(policy.EvaluateReadRoot(
                labRoot,
                request.Candidate.Path));
        if (!request.Intake.DataRoot.IsUnder(labRoot) ||
            !Directory.Exists(request.Intake.DataRoot.Value))
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-data",
                "The reviewed copied Data root must be an existing K-local directory."));
        else
            diagnostics.AddRange(policy.EvaluateReadRoot(
                labRoot,
                request.Intake.DataRoot));
        if (!request.OutputDataRoot.IsUnder(labRoot) ||
            !Directory.Exists(request.OutputDataRoot.Value))
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-output",
                "The transient texture root must be an existing K-local directory."));
        else
            diagnostics.AddRange(policy.Evaluate(
                labRoot,
                request.OutputDataRoot));
        return !HasErrors(diagnostics);
    }

    private static async ValueTask<PreviewAssetAuthorityPlan?>
        BuildHairRegionsAuthorityPlanAsync(
            ReviewedGameIntake intake,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        if (ReviewedGameIntakeFingerprintAuthority
                .Fingerprint(intake) !=
            intake.IntakeFingerprint)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-intake-fingerprint",
                "The typed reviewed intake fields do not reproduce their canonical intake fingerprint."));
            return null;
        }
        byte[] loadOrderBytes;
        try
        {
            var loadOrderInfo =
                new FileInfo(
                    intake.LoadOrderPath.Value);
            if (!loadOrderInfo.Exists ||
                loadOrderInfo.Length is <= 0 or
                    > MaximumHairRegionsLoadOrderBytes)
                throw new InvalidDataException(
                    "The reviewed load-order file is missing, empty, or exceeds its bounded size.");
            loadOrderBytes =
                await File.ReadAllBytesAsync(
                    intake.LoadOrderPath.Value,
                    cancellationToken);
            if (loadOrderBytes.LongLength !=
                loadOrderInfo.Length)
                throw new InvalidDataException(
                    "The reviewed load-order file length changed during its exact read.");
        }
        catch (Exception exception) when (
            exception is
                IOException or
                UnauthorizedAccessException or
                InvalidDataException)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-load-order-semantics",
                exception.Message));
            return null;
        }
        Sha256Hash currentLoadOrder =
            Hash(loadOrderBytes);
        if (currentLoadOrder != intake.LoadOrderHash)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-load-order-stale",
                "The reviewed load-order bytes changed after intake."));
            return null;
        }
        ImmutableArray<HairRegionsLoadOrderEntry> loadOrder =
            ParseHairRegionsLoadOrder(
                loadOrderBytes,
                diagnostics);
        if (loadOrder.IsDefaultOrEmpty ||
            HasErrors(diagnostics))
            return null;
        PluginClosureReviewEntry[] plugins =
            intake.Plugins.ToArray();
        if (plugins.Length == 0 ||
            plugins.Select(item => item.Order)
                .Where((order, index) =>
                    index > 0 &&
                    order <= plugins[index - 1].Order)
                .Any() ||
            plugins.Select(item => item.Order)
                .Distinct()
                .Count() != plugins.Length ||
            plugins.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != plugins.Length)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-plugin-order",
                "Reviewed intake lacks one distinct ascending plugin closure."));
            return null;
        }
        Dictionary<string, HairRegionsLoadOrderEntry>
            loadOrderByName =
                loadOrder.ToDictionary(
                    item => item.Plugin.Value,
                    StringComparer.OrdinalIgnoreCase);
        Dictionary<int, HairRegionsLoadOrderEntry>
            loadOrderByOrder =
                loadOrder.ToDictionary(
                    item => item.Order);
        foreach (PluginClosureReviewEntry plugin in plugins)
        {
            if (!loadOrderByName.TryGetValue(
                    plugin.Plugin.Value,
                    out HairRegionsLoadOrderEntry? byName) ||
                !loadOrderByOrder.TryGetValue(
                    plugin.Order,
                    out HairRegionsLoadOrderEntry? byOrder) ||
                byName.Order != plugin.Order ||
                byName.Enabled != plugin.Enabled ||
                !string.Equals(
                    byOrder.Plugin.Value,
                    plugin.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                byOrder.Enabled != plugin.Enabled)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-source-load-order-semantics",
                    $"Reviewed plugin '{plugin.Plugin.Value}' order/enabled authority does not match the bound load-order document."));
                return null;
            }
            if (!plugin.Enabled ||
                !plugin.ReadSucceeded ||
                plugin.SourceHash is null ||
                !File.Exists(plugin.Path.Value) ||
                await HashFileAsync(
                    plugin.Path,
                    cancellationToken) !=
                plugin.SourceHash.Value)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-source-plugin-stale",
                    $"Reviewed plugin '{plugin.Plugin}' changed or is no longer admitted."));
                return null;
            }
        }

        AssetIndex index =
            await new BethesdaAssetIndexer().IndexAsync(
                new AssetIndexRequest(
                    intake.Edition,
                    intake.DataRoot),
                cancellationToken);
        diagnostics.AddRange(index.Diagnostics);
        Sha256Hash fingerprint =
            AssetProviderInventoryAuthority.Fingerprint(
                index.Providers);
        if (index.Edition != intake.Edition ||
            index.Providers.Length !=
                intake.AssetProviderCount ||
            fingerprint !=
                intake.AssetIndexFingerprint)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-asset-index-stale",
                "The copied Data provider inventory differs from the reviewed intake count/fingerprint."));
            return null;
        }

        ImmutableArray<PaintArchivePlanEntry> archives;
        try
        {
            archives =
                BethesdaSkyrimRaceMenuPaintCatalogScanner
                    .BuildArchivePlan(
                        Directory.GetFiles(
                            intake.DataRoot.Value,
                            "*.bsa",
                            SearchOption.TopDirectoryOnly),
                        plugins
                            .Select(item => item.Plugin)
                            .ToImmutableArray());
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                ArgumentException)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-archive-plan",
                exception.Message));
            return null;
        }
        Dictionary<string, PaintArchivePlanEntry>
            archiveByName = archives.ToDictionary(
                item => item.Name,
                StringComparer.OrdinalIgnoreCase);
        var winners = ImmutableDictionary.CreateBuilder<
            string,
            PreviewAssetWinner>(
                StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, AssetProvider> group in
                 index.Providers.GroupBy(
                     item => item.Path.Value,
                     StringComparer.OrdinalIgnoreCase))
        {
            AssetProvider[] loose = group
                .Where(item =>
                    item.Kind ==
                    AssetProviderKind.Loose)
                .ToArray();
            if (loose.Length > 1)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-source-loose-ambiguous",
                    $"Asset '{group.Key}' has multiple loose providers."));
                continue;
            }
            if (loose.Length == 1)
            {
                AssetProvider row = loose[0];
                winners[group.Key] =
                    new PreviewAssetWinner(
                        AssetProviderKind.Loose,
                        "loose",
                        new WorkspacePath(Path.Combine(
                            intake.DataRoot.Value,
                            row.Path.Value.Replace(
                                '/',
                                Path.DirectorySeparatorChar))),
                        new Sha256Hash(row.Sha256),
                        row.Size);
                continue;
            }
            AssetProvider[] archiveProviders = group
                .Where(item =>
                    item.Kind ==
                    AssetProviderKind.Archive)
                .ToArray();
            var admitted = archiveProviders
                .Select(item => (
                    Provider: item,
                    Archive:
                        archiveByName.GetValueOrDefault(
                            item.Source)))
                .Where(item =>
                    item.Archive is not null)
                .ToArray();
            // Intake.Plugins is the selected dependency closure, not an
            // assertion about unrelated enabled plugins. If any competing
            // archive cannot be bound to that reviewed order, this path has
            // no winner in the current authority plan and must refuse if the
            // exact FaceGeom requests it.
            if (admitted.Length == 0 ||
                admitted.Length != archiveProviders.Length)
                continue;
            if (admitted.GroupBy(
                    item => item.Provider.Source,
                    StringComparer.OrdinalIgnoreCase)
                .Any(item => item.Count() != 1))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-source-archive-ambiguous",
                    $"Asset '{group.Key}' has duplicate rows in one admitted archive."));
                continue;
            }
            (AssetProvider Provider,
                PaintArchivePlanEntry? Archive) selected =
                admitted
                    .OrderBy(item =>
                        item.Archive!.SourceOrder)
                    .Last();
            PaintArchivePlanEntry archive =
                selected.Archive!;
            winners[group.Key] =
                new PreviewAssetWinner(
                    AssetProviderKind.Archive,
                    $"bsa:{archive.Name}|plugin:{archive.Plugin.Value}|order:{archive.SourceOrder}",
                    archive.Path,
                    new Sha256Hash(
                        selected.Provider.Sha256),
                    selected.Provider.Size);
        }
        return HasErrors(diagnostics)
            ? null
            : new PreviewAssetAuthorityPlan(
                winners.ToImmutable());
    }

    private static ImmutableArray<HairRegionsLoadOrderEntry>
        ParseHairRegionsLoadOrder(
            byte[] bytes,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        int first = 0;
        while (first < bytes.Length &&
               bytes[first] is
                   (byte)' ' or
                   (byte)'\t' or
                   (byte)'\r' or
                   (byte)'\n')
            first++;
        ImmutableArray<HairRegionsLoadOrderEntry> entries =
            first < bytes.Length &&
            bytes[first] == (byte)'{'
                ? ParseHairRegionsJsonLoadOrder(
                    bytes,
                    diagnostics)
                : ParseHairRegionsTextLoadOrder(
                    bytes,
                    diagnostics);
        if (entries.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-load-order-semantics",
                "The bound load-order document contains no admitted plugin rows."));
            return [];
        }
        var names = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var orders = new HashSet<int>();
        foreach (HairRegionsLoadOrderEntry entry in entries)
        {
            if (!names.Add(entry.Plugin.Value) ||
                !orders.Add(entry.Order))
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-source-load-order-semantics",
                    "The bound load-order document contains a duplicate plugin name or global order index."));
                return [];
            }
        }
        return entries;
    }

    private static ImmutableArray<HairRegionsLoadOrderEntry>
        ParseHairRegionsJsonLoadOrder(
            byte[] bytes,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            using JsonDocument document =
                JsonDocument.Parse(
                    bytes,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling =
                            JsonCommentHandling.Disallow,
                        MaxDepth = 16
                    });
            JsonElement root =
                document.RootElement;
            if (root.ValueKind !=
                    JsonValueKind.Object ||
                HasDuplicateHairRegionsProperties(
                    root) ||
                root.EnumerateObject().Any(property =>
                    property.Name is not (
                        "schemaVersion" or
                        "edition" or
                        "plugins")) ||
                !root.TryGetProperty(
                    "schemaVersion",
                    out JsonElement schema) ||
                schema.ValueKind !=
                    JsonValueKind.Number ||
                !schema.TryGetInt32(
                    out int schemaVersion) ||
                schemaVersion != 1 ||
                !root.TryGetProperty(
                    "edition",
                    out JsonElement edition) ||
                edition.ValueKind !=
                    JsonValueKind.String ||
                !string.Equals(
                    edition.GetString(),
                    "skyrimse",
                    StringComparison.Ordinal) ||
                !root.TryGetProperty(
                    "plugins",
                    out JsonElement plugins) ||
                plugins.ValueKind !=
                    JsonValueKind.Array ||
                plugins.GetArrayLength() is <= 0 or
                    > MaximumHairRegionsLoadOrderEntries)
                throw new InvalidDataException(
                    "JSON load order requires only schemaVersion 1, edition skyrimse, and a bounded nonempty plugins array.");
            var entries =
                ImmutableArray.CreateBuilder<
                    HairRegionsLoadOrderEntry>();
            foreach (JsonElement item in
                     plugins.EnumerateArray())
            {
                if (item.ValueKind !=
                        JsonValueKind.Object ||
                    HasDuplicateHairRegionsProperties(
                        item) ||
                    item.EnumerateObject().Any(property =>
                        property.Name is not (
                            "name" or
                            "order" or
                            "enabled")) ||
                    !item.TryGetProperty(
                        "name",
                        out JsonElement name) ||
                    name.ValueKind !=
                        JsonValueKind.String ||
                    !item.TryGetProperty(
                        "order",
                        out JsonElement order) ||
                    order.ValueKind !=
                        JsonValueKind.Number ||
                    !order.TryGetInt32(
                        out int globalOrder) ||
                    globalOrder < 0 ||
                    !item.TryGetProperty(
                        "enabled",
                        out JsonElement enabled) ||
                    enabled.ValueKind is not (
                        JsonValueKind.True or
                        JsonValueKind.False))
                    throw new InvalidDataException(
                        "A JSON load-order row is not an exact name/order/enabled authority.");
                entries.Add(
                    new HairRegionsLoadOrderEntry(
                        new PluginName(
                            name.GetString()!),
                        globalOrder,
                        enabled.GetBoolean()));
            }
            return entries.ToImmutable();
        }
        catch (Exception exception) when (
            exception is
                JsonException or
                InvalidDataException or
                ArgumentException)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-load-order-semantics",
                exception.Message));
            return [];
        }
    }

    private static ImmutableArray<HairRegionsLoadOrderEntry>
        ParseHairRegionsTextLoadOrder(
            byte[] bytes,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            string text =
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true)
                    .GetString(bytes)
                    .TrimStart('\uFEFF');
            var entries =
                ImmutableArray.CreateBuilder<
                    HairRegionsLoadOrderEntry>();
            foreach (string rawLine in text.Split(
                         [
                             "\r\n",
                             "\n",
                             "\r"
                         ],
                         StringSplitOptions.None))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 ||
                    line.StartsWith(
                        '#'))
                    continue;
                bool enabled = true;
                if (line[0] is '*' or '-')
                {
                    enabled = line[0] == '*';
                    line = line[1..].Trim();
                }
                entries.Add(
                    new HairRegionsLoadOrderEntry(
                        new PluginName(line),
                        entries.Count,
                        enabled));
                if (entries.Count >
                    MaximumHairRegionsLoadOrderEntries)
                    throw new InvalidDataException(
                        "Text load order exceeds its bounded plugin count.");
            }
            return entries.ToImmutable();
        }
        catch (Exception exception) when (
            exception is
                DecoderFallbackException or
                InvalidDataException or
                ArgumentException)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-source-load-order-semantics",
                exception.Message));
            return [];
        }
    }

    private static bool HasDuplicateHairRegionsProperties(
        JsonElement value)
    {
        var names = new HashSet<string>(
            StringComparer.Ordinal);
        return value.EnumerateObject().Any(
            property => !names.Add(
                property.Name));
    }

    private static Sha256Hash Hash(
        ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static FaceGeomHairRegionsPreviewSourceResult
        RefusedHairRegionsSource(
            ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record HairRegionsLoadOrderEntry(
        PluginName Plugin,
        int Order,
        bool Enabled);
}
