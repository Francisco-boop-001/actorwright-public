using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reads one existing NPC package and its protected authority graph without
/// extracting, rewriting, or otherwise changing any source artifact.
/// </summary>
public sealed class SkyrimNpcFinishCoreSourcePackageReader
{
    private readonly WorkspacePath workspaceRoot;
    private readonly IWorkspacePolicy policy;
    private readonly PackageManifestReader manifestReader;
    private readonly IPackageVerifyService packageVerifier;
    private readonly BethesdaSkyrimNpcFinishCoreSourceReader pluginReader;
    private readonly SkyrimNpcFinishCoreAdditionalMasterAuthorityReader
        additionalMasterAuthorityReader;
    private readonly Action<WorkspacePath>? afterInitialRead;

    public SkyrimNpcFinishCoreSourcePackageReader(
        WorkspacePath workspaceRoot,
        IWorkspacePolicy policy,
        PackageManifestReader manifestReader,
        IPackageVerifyService packageVerifier,
        BethesdaSkyrimNpcFinishCoreSourceReader pluginReader)
        : this(
            workspaceRoot,
            policy,
            manifestReader,
            packageVerifier,
            pluginReader,
            null,
            null)
    {
    }

    private SkyrimNpcFinishCoreSourcePackageReader(
        WorkspacePath workspaceRoot,
        IWorkspacePolicy policy,
        PackageManifestReader manifestReader,
        IPackageVerifyService packageVerifier,
        BethesdaSkyrimNpcFinishCoreSourceReader pluginReader,
        SkyrimNpcFinishCoreAdditionalMasterAuthorityReader?
            additionalMasterAuthorityReader,
        Action<WorkspacePath>? afterInitialRead)
    {
        this.workspaceRoot = workspaceRoot;
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        this.manifestReader = manifestReader ?? throw new ArgumentNullException(nameof(manifestReader));
        this.packageVerifier = packageVerifier ?? throw new ArgumentNullException(nameof(packageVerifier));
        this.pluginReader = pluginReader ?? throw new ArgumentNullException(nameof(pluginReader));
        this.additionalMasterAuthorityReader = additionalMasterAuthorityReader ??
            new SkyrimNpcFinishCoreAdditionalMasterAuthorityReader(
                workspaceRoot, this.policy);
        this.afterInitialRead = afterInitialRead;
    }

    public SkyrimNpcFinishCoreSourcePackageReader WithAfterInitialRead(
        Action<WorkspacePath> callback) =>
        new(
            workspaceRoot,
            policy,
            manifestReader,
            packageVerifier,
            pluginReader,
            additionalMasterAuthorityReader,
            callback);

    public async ValueTask<SkyrimNpcFinishCoreSourceReadResult> InspectAsync(
        SkyrimNpcFinishCoreRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var missingBindings = ImmutableArray.CreateBuilder<string>();
        if (request.Source.PackageRoot is null)
            missingBindings.Add("source.packageRoot");
        if (request.Source.PackageManifest is null)
            missingBindings.Add("source.packageManifest");
        if (request.Source.PackageManifestSha256 is null)
            missingBindings.Add("source.packageManifestSha256");
        if (request.Source.PackageTreeSha256 is null)
            missingBindings.Add("source.packageTreeSha256");
        if (request.Source.PluginPath is null)
            missingBindings.Add("source.pluginPath");
        if (request.Source.Plugin is null)
            missingBindings.Add("source.plugin");
        if (request.Source.PluginSha256 is null)
            missingBindings.Add("source.pluginSha256");
        if (request.Actor.EditorId is null)
            missingBindings.Add("actor.editorId");
        if (request.Actor.FormId is null)
            missingBindings.Add("actor.formId");
        if (missingBindings.Count > 0)
            return Refused(request, "finish-core-source-fields",
                "Finish Core source and actor bindings are incomplete; " +
                $"missing=[{string.Join(',', missingBindings)}].", diagnostics);

        WorkspacePath packageRoot = request.Source.PackageRoot.GetValueOrDefault();
        WorkspacePath manifestPath = request.Source.PackageManifest.GetValueOrDefault();
        Sha256Hash manifestSha256 = request.Source.PackageManifestSha256.GetValueOrDefault();
        Sha256Hash requestedTreeSha256 = request.Source.PackageTreeSha256.GetValueOrDefault();
        WorkspacePath pluginPath = request.Source.PluginPath.GetValueOrDefault();
        PluginName plugin = request.Source.Plugin.GetValueOrDefault();
        Sha256Hash requestedPluginSha256 = request.Source.PluginSha256.GetValueOrDefault();
        EditorId actorEditorId = request.Actor.EditorId.GetValueOrDefault();
        FormId actorFormId = request.Actor.FormId.GetValueOrDefault();

        diagnostics.AddRange(policy.EvaluateReadRoot(workspaceRoot, packageRoot));
        diagnostics.AddRange(policy.EvaluateReadRoot(workspaceRoot, manifestPath));
        diagnostics.AddRange(policy.EvaluateReadRoot(workspaceRoot, pluginPath));
        if (!manifestPath.IsUnder(packageRoot) ||
            !string.Equals(Path.GetDirectoryName(manifestPath.Value), packageRoot.Value,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("finish-core-source-manifest-root",
                "The package manifest must be directly inside the bound package root."));
        if (!pluginPath.IsUnder(packageRoot) ||
            !string.Equals(Path.GetFileName(pluginPath.Value), plugin.Value,
                StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("finish-core-source-plugin-path",
                "The bound plugin must be inside the package root and its filename must equal the bare plugin self-key."));
        if (!Enum.IsDefined(request.Authorities.BodyRoute))
            diagnostics.Add(Error("finish-core-authority-body-route",
                "The body route is not one of the closed Finish Core routes."));
        if (HasErrors(diagnostics))
            return Refused(request, "finish-core-source-path", Join(diagnostics), diagnostics);

        PackageVerifyResult package = await packageVerifier.VerifyAsync(
            new PackageVerifyRequest(manifestPath), cancellationToken);
        diagnostics.AddRange(package.Diagnostics);
        if (!package.Verified || package.Artifact is null)
            return Refused(request, "finish-core-source-package-manifest",
                "The source package manifest or its declared inventory did not verify.", diagnostics,
                package.Artifact);
        if (package.Artifact.ManifestSha256 != manifestSha256)
            return Refused(request, "finish-core-source-manifest-hash",
                "The source package manifest hash differs from the request binding.", diagnostics,
                package.Artifact);
        if (!string.Equals(package.Artifact.OutputPlugin, plugin.Value,
                StringComparison.OrdinalIgnoreCase) ||
            package.Artifact.TargetFormId != actorFormId)
            return Refused(request, "finish-core-source-package-identity",
                "The package manifest output identity does not match the requested base NPC.", diagnostics,
                package.Artifact);

        Sha256Hash packageTree = ComputePackageTreeSha256(packageRoot);
        if (packageTree != requestedTreeSha256)
            return Refused(request, "finish-core-source-package-tree-hash",
                "The package tree differs from the request binding; " +
                $"expected={packageTree.Value}, received={requestedTreeSha256.Value}.", diagnostics,
                package.Artifact, packageTree: packageTree);

        Diagnostic? authorityFailure = await VerifyAuthorityGraphAsync(
            request, packageRoot, package.Artifact, diagnostics, cancellationToken);
        if (authorityFailure is not null)
            return Refused(request, authorityFailure.Code, authorityFailure.Message,
                diagnostics, package.Artifact, packageTree);

        SkyrimNpcFinishCoreAuthorityReadResult additionalMasterAuthority =
            await additionalMasterAuthorityReader.ReadAsync(
                request.Authorities.AdditionalMasters,
                cancellationToken);
        diagnostics.AddRange(additionalMasterAuthority.Diagnostics);
        if (!additionalMasterAuthority.Admitted)
            return RefusedWithDiagnostics(
                request,
                diagnostics.ToImmutable(),
                package.Artifact,
                packageTree);

        string pluginRelativePath = Path.GetRelativePath(packageRoot.Value, pluginPath.Value)
            .Replace('\\', '/');
        PackageFileVerification[] pluginArtifacts = package.Artifact.Files
            .Where(file =>
                string.Equals(file.Kind, "plugin", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(file.RelativePath.Value, pluginRelativePath,
                    StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        if (pluginArtifacts.Length != 1 || !pluginArtifacts[0].Matches)
        {
            string observed = pluginArtifacts.Length switch
            {
                0 => "0",
                1 when pluginArtifacts[0].Matches => "1",
                1 => "1 (not matching)",
                _ => "at least 2"
            };
            return Refused(request, "finish-core-source-plugin-manifest-entry",
                $"The source plugin package-manifest artifact cardinality is not exact-one: identity={plugin.Value}; expected=1; observed={observed}.",
                diagnostics, package.Artifact, packageTree);
        }

        SkyrimNpcFinishCoreMasterPlan outfitMasterPlan;
        try
        {
            ImmutableArray<PluginName> sourceMasters;
            try
            {
                sourceMasters = BethesdaSkyrimNpcFinishCoreSourceReader.ReadMasterDependencies(
                    File.ReadAllBytes(pluginPath.Value));
            }
            catch (InvalidDataException exception)
            {
                return Refused(request, "finish-core-source-plugin-raw-invalid", exception.Message,
                    diagnostics, package.Artifact, packageTree);
            }
            outfitMasterPlan = SkyrimNpcFinishCoreMasterPlanner.Plan(request,
                sourceMasters, additionalMasterAuthority.Masters);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return Refused(request, "finish-core-outfit-master-order", exception.Message,
                diagnostics, package.Artifact, packageTree);
        }
        diagnostics.AddRange(outfitMasterPlan.Diagnostics);
        if (!outfitMasterPlan.Admitted)
            return RefusedWithDiagnostics(request, diagnostics.ToImmutable(), package.Artifact, packageTree);

        SkyrimNpcFinishCorePluginSnapshot pluginSnapshot =
            BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
            pluginPath, plugin, actorFormId, actorEditorId, cancellationToken, request,
            outfitMasterPlan.MasterOrder.Select(value => value.Value).ToImmutableArray());
        diagnostics.AddRange(pluginSnapshot.Diagnostics);
        if (!pluginSnapshot.Admitted)
            return Refused(request, pluginSnapshot.Diagnostics.FirstOrDefault(item =>
                    item.Severity == DiagnosticSeverity.Error)?.Code ??
                "finish-core-source-plugin", "The source plugin was refused by typed/raw admission.",
                diagnostics, package.Artifact, packageTree,
                pluginSnapshot.PluginSha256, pluginSnapshot.BaseNpc,
                pluginSnapshot.TargetEditorId, pluginSnapshot.TypedForbiddenCounts,
                pluginSnapshot.RawForbiddenCounts);

        afterInitialRead?.Invoke(pluginPath);
        Sha256Hash reopenedPluginHash = await HashFileAsync(pluginPath, cancellationToken);
        if (reopenedPluginHash == pluginSnapshot.PluginSha256 &&
            reopenedPluginHash != requestedPluginSha256)
            return Refused(request, "finish-core-source-plugin-hash",
                "The source plugin hash differs from the request binding.", diagnostics,
                package.Artifact, packageTree, reopenedPluginHash,
                pluginSnapshot.BaseNpc, pluginSnapshot.TargetEditorId,
                pluginSnapshot.TypedForbiddenCounts, pluginSnapshot.RawForbiddenCounts);
        if (reopenedPluginHash != pluginSnapshot.PluginSha256)
            return Refused(request, "finish-core-source-drift",
                "The source plugin changed after its initial locked read.", diagnostics,
                package.Artifact, packageTree, reopenedPluginHash,
                pluginSnapshot.BaseNpc, pluginSnapshot.TargetEditorId,
                pluginSnapshot.TypedForbiddenCounts, pluginSnapshot.RawForbiddenCounts);

        bool alreadySatisfied = IsAlreadySatisfied(request, pluginSnapshot);
        return new SkyrimNpcFinishCoreSourceReadResult(
            true,
            package.Artifact,
            packageTree,
            reopenedPluginHash,
            pluginSnapshot.BaseNpc,
            pluginSnapshot.TargetEditorId,
            true,
            "None",
            pluginSnapshot.TypedForbiddenCounts,
            pluginSnapshot.RawForbiddenCounts,
            diagnostics.ToImmutable())
        {
            NextFormId = new FormId(pluginSnapshot.NextFormId),
            Tes4Flags = pluginSnapshot.Tes4Flags,
            MasterOrder = pluginSnapshot.MasterOrder,
            VerifiedAdditionalMasters = additionalMasterAuthority.Masters,
            OccupiedIds = pluginSnapshot.OccupiedIds,
            TargetConfigurationFlags = pluginSnapshot.TargetConfigurationFlags,
            FactionRanks = pluginSnapshot.FactionRanks,
            CombatStyle = pluginSnapshot.CombatStyle,
            CombatStyleMatchesDefensiveContract = pluginSnapshot.CombatStyleMatchesDefensiveContract,
            Perks = pluginSnapshot.Perks,
            DefaultOutfit = pluginSnapshot.DefaultOutfit,
            OutfitArmaturesToClone = pluginSnapshot.OutfitArmaturesToClone,
            OutfitArmorsToClone = pluginSnapshot.OutfitArmorsToClone,
            Inventory = pluginSnapshot.Inventory,
            PackageLinks = pluginSnapshot.PackageLinks,
            Relationships = pluginSnapshot.Relationships,
            AiData = pluginSnapshot.AiData,
            SemanticSurfaceValues = BuildSemanticSurface(pluginSnapshot),
            AlreadySatisfied = alreadySatisfied
        };
    }

    private async ValueTask<Diagnostic?> VerifyAuthorityGraphAsync(
        SkyrimNpcFinishCoreRequest request,
        WorkspacePath packageRoot,
        PackageVerificationArtifact package,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        SkyrimNpcFinishCoreProviderAuthority[] providers =
            request.Authorities.Providers.ToArray();
        if (providers.Length == 0)
            return Error("finish-core-authority-provider-empty",
                "At least one fully bound provider authority is required.");
        if (providers.Select(provider => provider.Plugin?.Value ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != providers.Length ||
            providers.Select(provider => provider.Path?.Value ?? string.Empty)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != providers.Length)
            return Error("finish-core-authority-provider-duplicate",
                "Provider plugin identities and paths must be unique.");

        foreach (SkyrimNpcFinishCoreProviderAuthority provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.Plugin is not { } providerPlugin || provider.Path is not { } providerPath ||
                provider.Sha256 is not { } providerSha || provider.ByteLength <= 0)
                return Error("finish-core-authority-provider-invalid",
                    "Every provider authority must bind plugin, path, byte length, and SHA-256.");
            diagnostics.AddRange(policy.EvaluateReadRoot(workspaceRoot, providerPath));
            if (!providerPath.IsUnder(packageRoot) ||
                !string.Equals(Path.GetFileName(providerPath.Value), providerPlugin.Value,
                    StringComparison.OrdinalIgnoreCase))
                return Error("finish-core-authority-provider-path",
                    "Provider paths must remain inside the package and retain their plugin filenames.");
            if (!File.Exists(providerPath.Value))
                return Error("finish-core-authority-provider-missing",
                    $"Provider '{providerPlugin.Value}' is missing.");
            byte[] bytes = await File.ReadAllBytesAsync(providerPath.Value, cancellationToken);
            if (bytes.LongLength != provider.ByteLength || Hash(bytes) != providerSha)
                return Error("finish-core-authority-provider-hash",
                    $"Provider '{providerPlugin.Value}' failed its bound size/hash check.");
            if (!package.Files.Any(file => string.Equals(file.RelativePath.Value,
                    Path.GetRelativePath(packageRoot.Value, providerPath.Value).Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase)))
                return Error("finish-core-authority-provider-manifest",
                    $"Provider '{providerPlugin.Value}' is not declared by the source package.");
        }

        foreach ((Sha256Hash? hash, string kind) in new[]
                 {
                     (request.Authorities.ActorAssemblySha256, "actor-assembly"),
                     (request.Authorities.BodyOwnerSha256, "body-owner"),
                     (request.Authorities.ProtectedAppearanceTreeSha256, "protected-appearance")
                 })
        {
            if (hash is null)
                continue;
            PackageFileVerification[] evidenceRows = package.Files
                .Where(file => string.Equals(
                    file.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            if (evidenceRows.Length != 1)
            {
                string observed = evidenceRows.Length == 0
                    ? "0"
                    : "at least 2";
                return Error("finish-core-authority-evidence-cardinality",
                    $"The bound {kind} evidence cardinality is not exact-one: expected=1; observed={observed}.");
            }
            PackageFileVerification evidence = evidenceRows[0];
            if (!evidence.Matches || evidence.ActualSha256 != hash)
                return Error("finish-core-authority-evidence-hash",
                    $"The bound {kind} evidence is missing or stale.");
            string path = Path.Combine(packageRoot.Value,
                evidence.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar));
            if (kind == "actor-assembly")
            {
                using JsonDocument document = JsonDocument.Parse(
                    await File.ReadAllBytesAsync(path, cancellationToken));
                if (!document.RootElement.TryGetProperty("outcome", out JsonElement outcome) ||
                    outcome.GetString() != "Pass" ||
                    !document.RootElement.TryGetProperty("placement", out JsonElement placement) ||
                    placement.ValueKind != JsonValueKind.Object ||
                    !placement.TryGetProperty("mode", out JsonElement mode) ||
                    mode.GetString() != "None")
                    return Error("finish-core-authority-actor-assembly",
                        "Actor Assembly evidence must be Pass with placement.mode=None.");
            }
        }

        string[] armor = request.OutfitPolicy.ArmorItems
            .Select(item => item.ToString())
            .ToArray();
        if (armor.Distinct(StringComparer.OrdinalIgnoreCase).Count() != armor.Length ||
            (request.OutfitPolicy.Policy == SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit &&
             request.OutfitPolicy.ExistingOutfit is null) ||
            (request.OutfitPolicy.Policy == SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit &&
             armor.Length == 0))
            return Error("finish-core-authority-outfit",
                "The selected outfit policy is missing or ambiguous.");
        if (request.InventoryPolicy.ExpectedSourceItems
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.InventoryPolicy.ExpectedSourceItems.Length ||
            request.InventoryPolicy.DesiredItems
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.InventoryPolicy.DesiredItems.Length)
            return Error("finish-core-inventory-duplicate",
                "Inventory identities must be unique in the source and desired lists.");
        return null;
    }

    public static Sha256Hash ComputePackageTreeSha256(WorkspacePath packageRoot)
    {
        if (!Directory.Exists(packageRoot.Value))
            throw new DirectoryNotFoundException(packageRoot.Value);
        var lines = new StringBuilder();
        foreach (string path in EnumerateOrdinaryFiles(packageRoot.Value)
                     .OrderBy(path => Path.GetRelativePath(packageRoot.Value, path).Replace('\\', '/'), StringComparer.Ordinal))
        {
            byte[] bytes = File.ReadAllBytes(path);
            string relative = Path.GetRelativePath(packageRoot.Value, path).Replace('\\', '/');
            lines.Append(relative).Append('|').Append(bytes.LongLength).Append('|')
                .Append(Convert.ToHexString(SHA256.HashData(bytes))).Append('\n');
        }
        return Hash(Encoding.UTF8.GetBytes(lines.ToString()));
    }

    private static IEnumerable<string> EnumerateOrdinaryFiles(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot))
            throw new InvalidDataException(
                $"Package root '{root}' is missing or unsafe.");
        FileAttributes rootAttributes = File.GetAttributes(fullRoot);
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint) ||
            rootAttributes.HasFlag(FileAttributes.Device))
            throw new InvalidDataException(
                $"Package root '{root}' is missing or unsafe.");
        var pending = new Stack<string>();
        pending.Push(fullRoot);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            FileAttributes currentAttributes = File.GetAttributes(current);
            if (currentAttributes.HasFlag(FileAttributes.ReparsePoint) ||
                currentAttributes.HasFlag(FileAttributes.Device))
                throw new InvalidDataException(
                    $"Package path '{current}' became a reparse or device path.");
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         current, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    attributes.HasFlag(FileAttributes.Device))
                    throw new InvalidDataException(
                        $"Package path '{entry}' is a reparse or device path.");
                if (Directory.Exists(entry))
                    pending.Push(entry);
                else if (File.Exists(entry))
                    yield return entry;
            }
        }
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path.Value, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static SkyrimNpcFinishCoreSourceReadResult Refused(
        SkyrimNpcFinishCoreRequest request,
        string code,
        string message,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        PackageVerificationArtifact? package = null,
        Sha256Hash? packageTree = null,
        Sha256Hash? pluginSha = null,
        FormReference? baseNpc = null,
        EditorId? editorId = null,
        IReadOnlyDictionary<string, int>? typed = null,
        IReadOnlyDictionary<string, int>? raw = null)
    {
        return RefusedWithDiagnostics(
            request,
            diagnostics.ToImmutable().Add(Error(code, message)),
            package,
            packageTree,
            pluginSha,
            baseNpc,
            editorId,
            typed,
            raw);
    }

    private static SkyrimNpcFinishCoreSourceReadResult RefusedWithDiagnostics(
        SkyrimNpcFinishCoreRequest request,
        ImmutableArray<Diagnostic> diagnostics,
        PackageVerificationArtifact? package = null,
        Sha256Hash? packageTree = null,
        Sha256Hash? pluginSha = null,
        FormReference? baseNpc = null,
        EditorId? editorId = null,
        IReadOnlyDictionary<string, int>? typed = null,
        IReadOnlyDictionary<string, int>? raw = null)
    {
        return new(false, package,
            packageTree ?? request.Source.PackageTreeSha256 ?? new Sha256Hash(new string('0', 64)),
            pluginSha ?? request.Source.PluginSha256 ?? new Sha256Hash(new string('0', 64)),
            baseNpc ?? new FormReference(
                request.Source.Plugin ?? new PluginName("Unknown.esp"),
                request.Actor.FormId ?? new FormId(0)),
            editorId ?? request.Actor.EditorId ?? new EditorId("Unknown"),
            false,
            null,
            (typed ?? BethesdaSkyrimNpcFinishCoreSourceReader.ForbiddenSignatures
                .ToImmutableDictionary(signature => signature, _ => 0, StringComparer.Ordinal))
                .ToImmutableDictionary(StringComparer.Ordinal),
            (raw ?? BethesdaSkyrimNpcFinishCoreSourceReader.ForbiddenSignatures
                .ToImmutableDictionary(signature => signature, _ => 0, StringComparer.Ordinal))
                .ToImmutableDictionary(StringComparer.Ordinal),
            diagnostics);
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ImmutableArray<string> BuildSemanticSurface(
        SkyrimNpcFinishCorePluginSnapshot snapshot)
    {
        var values = ImmutableArray.CreateBuilder<string>();
        values.AddRange(snapshot.FactionRanks.Select(entry =>
            $"FACTION {entry.Faction} rank {entry.Rank}"));
        values.AddRange(snapshot.Relationships.Select(relationship =>
            $"RELATIONSHIP {relationship.Parent}->{relationship.Child} " +
            $"rank {relationship.Rank} flags {relationship.Flags} " +
            $"association {relationship.AssociationType?.ToString() ?? "Null"}"));
        if (snapshot.CombatStyle is { } combatStyle)
            values.Add($"COMBATSTYLE {combatStyle} " +
                       (snapshot.CombatStyleMatchesDefensiveContract ? "DEFENSIVE" : "OTHER"));
        if (snapshot.DefaultOutfit is { } outfit)
            values.Add($"OUTFIT {outfit}");
        else
            values.Add("OUTFIT NONE");
        values.AddRange(snapshot.Inventory.Select(entry =>
            $"INVENTORY {entry.Item} count {entry.Count}"));
        if (snapshot.PackageMatchesFinishCoreContract)
            values.Add("FINISH_CORE_COMPLETE");
        values.AddRange(snapshot.PackageLinks.Select(link => $"PACKAGE {link}"));
        return values.ToImmutable();
    }

    private static bool IsAlreadySatisfied(
        SkyrimNpcFinishCoreRequest request,
        SkyrimNpcFinishCorePluginSnapshot snapshot)
    {
        FormReference actor = new(request.Source.Plugin!.Value, request.Actor.FormId!.Value);
        FormReference player = new(new PluginName("Skyrim.esm"), new FormId(7));
        bool flags = (snapshot.TargetConfigurationFlags & 0x00000820u) == 0x00000820u;
        bool potential = snapshot.FactionRanks.Count(entry =>
            entry.Faction == new FormReference(new PluginName("Skyrim.esm"), new FormId(0x5C84D)) &&
            entry.Rank == 0) == 1;
        bool current = snapshot.FactionRanks.Count(entry =>
            entry.Faction == new FormReference(new PluginName("Skyrim.esm"), new FormId(0x5C84E)) &&
            entry.Rank == -1) == 1;
        bool relationship = snapshot.Relationships.Count(row =>
            row.Parent == actor && row.Child == player &&
            row.Rank == "Ally" && row.Flags == 0 && row.AssociationType is null) == 1;
        bool outfit = request.OutfitPolicy.Policy == SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit &&
                      request.OutfitPolicy.ExistingOutfit == snapshot.DefaultOutfit;
        bool inventory = request.InventoryPolicy.Policy == SkyrimNpcFinishCoreInventoryPolicy.PreserveInventory ||
                         snapshot.Inventory.Select(row => row.Item.ToString())
                             .SequenceEqual(request.InventoryPolicy.DesiredItems,
                                 StringComparer.OrdinalIgnoreCase);
        bool aiData = request.AiPolicy is null || request.AiPolicy == snapshot.AiData;
        return request.CombatPolicy is null &&
               (request.PerkPolicy.IsDefault || request.PerkPolicy.SequenceEqual(snapshot.Perks)) &&
               flags && potential && current && relationship &&
               snapshot.CombatStyleMatchesDefensiveContract && outfit && inventory && aiData &&
               snapshot.PackageMatchesFinishCoreContract;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static string Join(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => item.Code + ":" + item.Message));

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}
