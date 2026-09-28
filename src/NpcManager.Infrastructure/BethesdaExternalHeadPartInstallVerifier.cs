using System.Collections.Immutable;
using System.IO.Abstractions;
using System.Security.Cryptography;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>
/// Composes package-owned selected-dependency evidence with a fresh, read-only
/// external-provider inspection.  The package verifier owns package-byte
/// semantics; this service owns only the separate install-dependency state.
/// </summary>
public sealed class BethesdaExternalHeadPartInstallVerifier :
    IExternalHeadPartInstallVerifier,
    IExternalHeadPartPromotedOutputVerifier
{
    private const long MaximumPluginBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumAssetBytes = 512L * 1024 * 1024;
    private const long MaximumPromotedBindingBytes = 8L * 1024 * 1024;
    private const int MaximumPlugins = 64;
    private static readonly Sha256Hash ZeroHash =
        new(new string('0', 64));

    private readonly IRaceMenuSelectedDependencyManifestReader selectedReader;
    private readonly IExternalHeadPartPhysicsBindingResolver?
        physicsBindingResolver;

    public BethesdaExternalHeadPartInstallVerifier()
        : this(new RaceMenuSelectedDependencyManifestReader())
    {
    }

    public BethesdaExternalHeadPartInstallVerifier(
        IRaceMenuSelectedDependencyManifestReader selectedReader,
        IExternalHeadPartPhysicsBindingResolver? physicsBindingResolver = null)
    {
        this.selectedReader = selectedReader ??
            throw new ArgumentNullException(nameof(selectedReader));
        this.physicsBindingResolver = physicsBindingResolver;
    }

    public async ValueTask<ExternalHeadPartInstallVerificationResult>
        VerifyAsync(
            ExternalHeadPartInstallVerificationRequest request,
            CancellationToken cancellationToken)
        => await VerifyCoreAsync(
            request,
            null,
            request.TargetActorFormId,
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<ExternalHeadPartInstallVerificationResult>
        VerifyPromotedOutputAsync(
            ExternalHeadPartPromotedOutputVerificationRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bindingDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.TargetActorFormId is not { Value: > 0 })
            return RefusedPromoted(
                request,
                "Promoted output verification requires a positive target actor FormID.");
        if (!IsKLocalOrdinaryRoot(request.PackageRoot, bindingDiagnostics, "package root") ||
            !IsKLocalOrdinaryPath(request.PackageRoot, request.SelectedManifestPath,
                bindingDiagnostics, "selected dependency manifest") ||
            !IsKLocalOrdinaryPath(request.PackageRoot, request.OutputPlugin,
                bindingDiagnostics, "output plugin") ||
            !IsKLocalOrdinaryPath(request.PackageRoot,
                request.PromotedOutputBindingPath,
                bindingDiagnostics,
                "promoted output binding"))
            return RefusedPromoted(request, string.Join("; ", bindingDiagnostics.Select(item => item.Message)));
        if (!IsSha256(request.ExpectedOutputPluginSha256) ||
            !IsSha256(request.ExpectedSelectedManifestSha256) ||
            !IsSha256(request.ExpectedPromotedOutputBindingSha256))
            return RefusedPromoted(request,
                "Promoted output verification requires non-default SHA-256 authorities.");
        byte[] bindingBytes;
        try
        {
            FileInfo bindingInfoBefore = new(request.PromotedOutputBindingPath.Value);
            if (bindingInfoBefore.Length > MaximumPromotedBindingBytes)
                return RefusedPromoted(request,
                    "The promoted output binding exceeds the bounded byte limit.");
            bindingBytes = await File.ReadAllBytesAsync(
                request.PromotedOutputBindingPath.Value, cancellationToken)
                .ConfigureAwait(false);
            FileInfo bindingInfoAfter = new(request.PromotedOutputBindingPath.Value);
            if (bindingInfoBefore.Length != bindingInfoAfter.Length ||
                bindingBytes.LongLength != bindingInfoAfter.Length ||
                HashBytes(bindingBytes) != request.ExpectedPromotedOutputBindingSha256)
                return RefusedPromoted(request, "The promoted output binding hash drifted.");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            return RefusedPromoted(request, exception.Message);
        }
        SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding binding;
        try
        {
            binding = SkyrimNpcFinishCorePromotedOutputBindingCodec.Parse(bindingBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or
            JsonException or InvalidOperationException or KeyNotFoundException or
            ArgumentException)
        {
            return RefusedPromoted(request, exception.Message);
        }
            if (!File.Exists(request.OutputPlugin.Value) ||
                binding.SourceSelectedManifestPath.Value !=
                SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath ||
            binding.SourceSelectedManifestSha256 != request.ExpectedSelectedManifestSha256 ||
            !PluginNamesEqual(binding.OutputPlugin, request.OutputPluginName) ||
            binding.OutputPluginSha256 != request.ExpectedOutputPluginSha256 ||
            binding.OutputPluginByteLength != new FileInfo(request.OutputPlugin.Value).Length)
            return RefusedPromoted(request, "The promoted output binding does not match the package request.");
        return await VerifyCoreAsync(
            new ExternalHeadPartInstallVerificationRequest(
                request.PackageRoot,
                request.OutputPlugin,
                request.OutputPluginName,
                request.ExpectedOutputPluginSha256,
                request.SelectedManifestPath,
                request.ExpectedSelectedManifestSha256,
                request.Context,
                request.RequireCurrentAuthority,
                request.TargetActorFormId),
            binding,
            request.TargetActorFormId,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ExternalHeadPartInstallVerificationResult>
        VerifyCoreAsync(
            ExternalHeadPartInstallVerificationRequest request,
            SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding? promotedBinding,
            FormId? targetActorFormId,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        if (!IsKLocalOrdinaryRoot(request.PackageRoot, diagnostics,
                "package root") ||
            !IsKLocalOrdinaryPath(request.PackageRoot, request.SelectedManifestPath,
                diagnostics, "selected dependency manifest") ||
            !IsKLocalOrdinaryPath(request.PackageRoot, request.OutputPlugin,
                diagnostics, "output plugin"))
        {
            return Refused(request, diagnostics, []);
        }
        if (!IsSha256(request.ExpectedOutputPluginSha256) ||
            !IsSha256(request.ExpectedSelectedManifestSha256))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordDrift,
                "External verification requires non-default SHA-256 authorities."));
            return Refused(request, diagnostics, []);
        }

        RaceMenuSelectedDependencyManifestReadResult selected =
            await selectedReader.ReadAsync(
                request.SelectedManifestPath,
                request.ExpectedSelectedManifestSha256,
                request.PackageRoot,
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(selected.Diagnostics);
        if (selected.Artifact is null ||
            selected.Artifact.ExternalInstallDependencies.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                "external-headpart-descriptor-lost",
                "The canonical schema-3 selected dependency manifest did not contain an admitted external descriptor."));
            return Refused(request, diagnostics, [request.ExpectedSelectedManifestSha256]);
        }

        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency> groups =
            selected.Artifact.ExternalInstallDependencies;
        ImmutableArray<ExternalHeadPartDependencyDescriptor> descriptors =
            groups.Select(item => item.Descriptor).ToImmutableArray();
        ImmutableArray<Sha256Hash> descriptorIds = descriptors
            .Select(item => item.DescriptorId)
            .OrderBy(item => item.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var prerequisites = ImmutableArray.CreateBuilder<
            ExternalHeadPartInstallPrerequisite>();
        bool descriptorClosureValid = ValidatePackageClosure(
            request,
            selected.Artifact,
            groups,
            promotedBinding,
            targetActorFormId,
            diagnostics,
            prerequisites);

        ImmutableArray<ExternalHeadPartInstallProviderObservation> providerObservations;
        ExternalHeadPartInstallContextFingerprint? fingerprint = null;
        ExternalInstallDependencyState state;
        bool installDependencyAuthority = false;

        if (request.Context is null)
        {
            providerObservations = BuildContextFreeProviderObservations(
                descriptors);
            foreach (ExternalHeadPartInstallProviderObservation observation in
                     providerObservations)
            {
                prerequisites.Add(new ExternalHeadPartInstallPrerequisite(
                    ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                    observation.ProviderPlugin.Value,
                    observation.ExpectedSha256,
                    null,
                    null,
                    "Supply a complete enabled plugin order and rerun strict verification."));
            }
            diagnostics.Add(new Diagnostic(
                ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                request.RequireCurrentAuthority
                    ? DiagnosticSeverity.Error
                    : DiagnosticSeverity.Info,
                "No ephemeral enabled-plugin context was supplied; current external install authority is not established."));
            state = ExternalInstallDependencyState.DeclaredUnverified;
        }
        else
        {
            FreshVerification fresh = await VerifyFreshContextAsync(
                request,
                descriptors,
                physicsBindingResolver,
                descriptorClosureValid,
                diagnostics,
                prerequisites,
                cancellationToken).ConfigureAwait(false);
            providerObservations = fresh.ProviderObservations;
            fingerprint = fresh.Fingerprint;
            state = descriptorClosureValid && fresh.Accepted
                ? ExternalInstallDependencyState.Verified
                : ExternalInstallDependencyState.DeclaredUnverified;
            installDependencyAuthority = state == ExternalInstallDependencyState.Verified;
        }

        if (request.RequireCurrentAuthority &&
            state != ExternalInstallDependencyState.Verified)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                "Strict package verification requires current verified external install dependency authority."));
        }

        ExternalHeadPartVerifiedInstallSnapshot? snapshot = null;
        bool? historicalSnapshotValid = null;
        if (fingerprint is not null && state == ExternalInstallDependencyState.Verified)
        {
            snapshot = new ExternalHeadPartVerifiedInstallSnapshot(
                selected.Artifact.ManifestSha256,
                descriptorIds,
                fingerprint);
            historicalSnapshotValid = request.CreatePrepublication ? null : true;
        }

        var artifact = new ExternalHeadPartInstallVerificationArtifact(
            ExternalHeadPartSchemaIdentifiers.InstallVerification,
            PackageIntegrity: true,
            descriptorClosureValid,
            historicalSnapshotValid,
            descriptorIds,
            snapshot,
            state,
            InstallReady: state == ExternalInstallDependencyState.Verified,
            installDependencyAuthority,
            RuntimeAuthority: false,
            VisualAuthority: false,
            providerObservations,
            prerequisites.ToImmutable());
        return new ExternalHeadPartInstallVerificationResult(
            descriptorClosureValid,
            artifact,
            diagnostics.ToImmutable());
    }

    private static ImmutableArray<
        ExternalHeadPartInstallProviderObservation>
        BuildContextFreeProviderObservations(
            IEnumerable<ExternalHeadPartDependencyDescriptor> descriptors)
    {
        return descriptors
            .Select(item => item.Provider)
            .GroupBy(item => item.Plugin.Value,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Plugin.Value, StringComparer.Ordinal)
            .Select(item => new ExternalHeadPartInstallProviderObservation(
                item.Plugin,
                item.PluginSha256,
                null,
                null))
            .ToImmutableArray();
    }

    private static bool ValidatePackageClosure(
        ExternalHeadPartInstallVerificationRequest request,
        RaceMenuSelectedDependencyManifestArtifact selected,
        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency> groups,
        SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding? promotedBinding,
        FormId? targetActorFormId,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<ExternalHeadPartInstallPrerequisite>.Builder prerequisites)
    {
        bool valid = true;
        if (request.RequireCurrentAuthority && targetActorFormId is null)
        {
            valid = false;
            AddClosureFailure(
                ExternalHeadPartDiagnosticCodes.RecordDrift,
                request.OutputPluginName.Value,
                "Strict external verification requires the package manifest target actor FormID.",
                diagnostics,
                prerequisites);
        }
        if (!groups.IsDefaultOrEmpty)
        {
            ImmutableArray<PluginName> expectedMasters =
                groups[0].OutputPlugin.Masters;
            if (groups.Skip(1).Any(group =>
                    !PluginSequenceEqualIgnoreCase(
                        group.OutputPlugin.Masters, expectedMasters)))
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.OutputMasterMissing,
                    request.OutputPluginName.Value,
                    "The selected external groups disagree on the ordered output-plugin master list.",
                    diagnostics,
                    prerequisites);
            }
        }
        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, ExternalHeadPartProviderIdentity> providerGroup in
                 groups.Select(item => item.Descriptor.Provider)
                     .GroupBy(item => item.Plugin.Value,
                         StringComparer.OrdinalIgnoreCase))
        {
            if (providerGroup.Select(item =>
                    (item.PluginSha256, item.PluginByteLength,
                        item.RedistributionMode)).Distinct().Count() > 1)
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    providerGroup.Key,
                    "Case-colliding selected provider descriptors disagree on provider identity.",
                    diagnostics,
                    prerequisites);
            }
        }
        if (promotedBinding is not null &&
            (promotedBinding.Groups.Length != groups.Length ||
             promotedBinding.Groups.Select(item => item.DescriptorId)
                 .SequenceEqual(groups.Select(item => item.Descriptor.DescriptorId)) == false))
        {
            valid = false;
            AddClosureFailure(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                request.OutputPluginName.Value,
                "The promoted output binding group count or order differs from the selected groups.",
                diagnostics,
                prerequisites);
        }
        foreach (RaceMenuSelectedDependencyManifestExternalInstallDependency group in groups)
        {
            ExternalHeadPartDependencyDescriptor descriptor = group.Descriptor;
            SkyrimNpcFinishCoreExternalHeadPartPromotedOutputGroupBinding? promotedGroup =
                promotedBinding?.Groups.SingleOrDefault(item =>
                    item.DescriptorId == descriptor.DescriptorId);
            if (promotedBinding is not null && promotedGroup is null)
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    descriptor.DescriptorId.Value,
                    "The promoted output binding is missing a selected descriptor.",
                    diagnostics,
                    prerequisites);
                continue;
            }
            if (promotedGroup is not null &&
                (promotedGroup.AttestationSha256 != group.Attestation.AttestationSha256 ||
                 !FormReferenceSequenceEqualIgnoreCase(
                     promotedGroup.DeclaredExternalPnam,
                     group.OutputPlugin.PnamBindings)))
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    descriptor.DescriptorId.Value,
                    "The promoted group attestation or PNAM sequence differs from the immutable selected group.",
                    diagnostics,
                    prerequisites);
                continue;
            }
            PluginName expectedOutputPlugin = promotedGroup?.OutputPlugin ??
                group.OutputPlugin.Plugin;
            Sha256Hash expectedOutputHash = promotedGroup?.OutputPluginSha256 ??
                group.OutputPlugin.Sha256;
            long expectedOutputLength = promotedGroup?.OutputPluginByteLength ??
                group.OutputPlugin.ByteLength;
            if (!groupNames.Add(descriptor.DescriptorId.Value))
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    descriptor.DescriptorId.Value,
                    "The selected dependency manifest repeats an external descriptor.",
                    diagnostics,
                    prerequisites);
            }

            if (!PluginNamesEqual(expectedOutputPlugin, request.OutputPluginName))
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    group.OutputPlugin.Plugin.Value,
                    "The selected dependency output-plugin binding does not match the package output plugin.",
                    diagnostics,
                    prerequisites);
            }

            if (request.ExpectedOutputPluginSha256 == ZeroHash ||
                !File.Exists(request.OutputPlugin.Value))
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    request.OutputPluginName.Value,
                    "The package output plugin is not available as a declared, hash-bound package artifact.",
                    diagnostics,
                    prerequisites);
            }
            else
            {
                FileInfo outputInfo = new(request.OutputPlugin.Value);
                if (outputInfo.Length <= 0 || outputInfo.Length > MaximumPluginBytes ||
                    outputInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    ContainsReparseBetween(request.PackageRoot.Value,
                        request.OutputPlugin.Value))
                {
                    valid = false;
                    AddClosureFailure(
                        ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                        request.OutputPluginName.Value,
                        "The package output plugin traverses a reparse path.",
                        diagnostics,
                        prerequisites);
                }
                else
                {
                    Sha256Hash actual = HashFile(request.OutputPlugin.Value);
                    if (outputInfo.Length != expectedOutputLength ||
                        actual != expectedOutputHash ||
                        actual != request.ExpectedOutputPluginSha256)
                    {
                        valid = false;
                        AddClosureFailure(
                            ExternalHeadPartDiagnosticCodes.RecordDrift,
                            request.OutputPluginName.Value,
                            "The package output plugin differs from its selected-dependency binding.",
                            diagnostics,
                            prerequisites);
                    }
                }
            }

            if (!TryResolvePackagePath(request.PackageRoot, group.FaceGeomPath,
                    out WorkspacePath faceGeomPath) ||
                !File.Exists(faceGeomPath.Value))
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    group.FaceGeomPath.Value,
                    "The package FaceGeom output referenced by the external attestation is missing.",
                    diagnostics,
                    prerequisites);
            }
            else
            {
                FileInfo info = new(faceGeomPath.Value);
                if (info.Length <= 0 || info.Length > MaximumAssetBytes ||
                    info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    ContainsReparseBetween(request.PackageRoot.Value,
                        faceGeomPath.Value))
                {
                    valid = false;
                    AddClosureFailure(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        group.FaceGeomPath.Value,
                        "The package FaceGeom output differs from its attestation.",
                        diagnostics,
                        prerequisites);
                }
                else
                {
                    Sha256Hash actual = HashFile(faceGeomPath.Value);
                    if (info.Length != group.FaceGeomByteLength ||
                        actual != group.FaceGeomSha256)
                    {
                        valid = false;
                        AddClosureFailure(
                            ExternalHeadPartDiagnosticCodes.RecordDrift,
                            group.FaceGeomPath.Value,
                            "The package FaceGeom output differs from its attestation.",
                            diagnostics,
                            prerequisites);
                    }
                }
            }

            if (descriptor.Members.Length == 0 ||
                descriptor.Members[0].OriginForm != descriptor.RootSourceForm)
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    descriptor.DescriptorId.Value,
                    "The external descriptor graph root is not closed over its declared members.",
                    diagnostics,
                    prerequisites);
            }
        }

        if (valid)
        {
            ImmutableArray<PluginName> expectedMasters = promotedBinding?.MasterOrder ??
                groups[0].OutputPlugin.Masters;
            ImmutableArray<FormReference> fullMemberPnam = groups
                .SelectMany(item => item.OutputPlugin.PnamBindings)
                .ToImmutableArray();
            ImmutableHashSet<string> selectedProviderPlugins = groups
                .Select(item => item.Descriptor.Provider.Plugin.Value)
                .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            ImmutableArray<FormReference> expectedSelectedProviderPnam =
                promotedBinding?.DeclaredExternalPnam ?? fullMemberPnam
                    .Where(item => selectedProviderPlugins.Contains(item.Plugin.Value))
                    .ToImmutableArray();
            if (!OutputBindingsMatch(
                    request.OutputPlugin,
                    request.OutputPluginName,
                    expectedMasters,
                    fullMemberPnam,
                    selectedProviderPlugins,
                    expectedSelectedProviderPnam,
                    targetActorFormId,
                    diagnostics))
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.OutputMasterMissing,
                    request.OutputPluginName.Value,
                    "The package output plugin master table or PNAM sequence differs from its selected-dependency binding.",
                    diagnostics,
                    prerequisites);
            }
        }

        return valid;
    }

    private static async ValueTask<FreshVerification> VerifyFreshContextAsync(
        ExternalHeadPartInstallVerificationRequest request,
        ImmutableArray<ExternalHeadPartDependencyDescriptor> descriptors,
        IExternalHeadPartPhysicsBindingResolver? physicsBindingResolver,
        bool descriptorClosureValid,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<ExternalHeadPartInstallPrerequisite>.Builder prerequisites,
        CancellationToken cancellationToken)
    {
        ExternalHeadPartInstallVerificationContext context = request.Context!;
        var providerObservations = new List<
            ExternalHeadPartInstallProviderObservation>();
        var allProviders = descriptors
            .Select(item => item.Provider)
            .GroupBy(item => item.Plugin.Value,
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Plugin.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Plugin.Value, StringComparer.Ordinal)
            .ToArray();
        var observations = new List<ExternalHeadPartInstallObservation>();
        var pluginAuthorities = new List<SkyrimFaceRecordPluginAuthority>();
        bool contextShapeValid = ValidateContextShape(context, diagnostics);
        bool allPluginsReadable = contextShapeValid;
        bool providersMatch = contextShapeValid;
        if (contextShapeValid)
        {
            for (int index = 0; index < context.EnabledPluginOrder.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PluginName plugin = context.EnabledPluginOrder[index];
                if (!TryResolveDataPlugin(context.DataRoot, plugin,
                        out WorkspacePath pluginPath) ||
                    !File.Exists(pluginPath.Value) ||
                    !IsOrdinaryExistingFile(context.DataRoot, pluginPath))
                {
                    allPluginsReadable = false;
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.ProviderMissing,
                        $"Enabled plugin '{plugin}' is not an ordinary readable file below the supplied Data root."));
                    continue;
                }

                FileInfo info = new(pluginPath.Value);
                if (info.Length <= 0 || info.Length > MaximumPluginBytes)
                {
                    allPluginsReadable = false;
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.ProviderMissing,
                        $"Enabled plugin '{plugin}' is outside the admitted byte bound."));
                    continue;
                }
                Sha256Hash hash = await HashFileAsync(pluginPath.Value,
                    cancellationToken).ConfigureAwait(false);
                pluginAuthorities.Add(new SkyrimFaceRecordPluginAuthority(
                    plugin, pluginPath, hash));
                observations.Add(new ExternalHeadPartInstallObservation(
                    "enabled-plugin",
                    plugin.Value,
                    hash,
                    info.Length,
                    index));
            }

            if (!ValidateEnabledMasterClosure(
                    context,
                    pluginAuthorities,
                    diagnostics,
                    prerequisites))
                allPluginsReadable = false;
        }

        foreach (ExternalHeadPartProviderIdentity provider in allProviders)
        {
            bool enabled = contextShapeValid && context.EnabledPluginOrder.Any(
                item => string.Equals(item.Value, provider.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase));
            if (!TryResolveDataPlugin(context.DataRoot, provider.Plugin,
                    out WorkspacePath providerPath) ||
                !File.Exists(providerPath.Value) ||
                !IsOrdinaryExistingFile(context.DataRoot, providerPath))
            {
                providersMatch = false;
                providerObservations.Add(new ExternalHeadPartInstallProviderObservation(
                    provider.Plugin, provider.PluginSha256, null, null));
                AddPrerequisite(
                    ExternalHeadPartDiagnosticCodes.ProviderMissing,
                    provider.Plugin.Value,
                    provider.PluginSha256,
                    null,
                    null,
                    "Install the declared provider plugin below the supplied Data root and enable it.",
                    diagnostics,
                    prerequisites);
                continue;
            }

            FileInfo info = new(providerPath.Value);
            Sha256Hash current = await HashFileAsync(providerPath.Value,
                cancellationToken).ConfigureAwait(false);
            bool identityMatches = current == provider.PluginSha256 &&
                info.Length == provider.PluginByteLength;
            providerObservations.Add(new ExternalHeadPartInstallProviderObservation(
                provider.Plugin,
                provider.PluginSha256,
                current,
                enabled));
            if (!enabled)
            {
                providersMatch = false;
                AddPrerequisite(
                    ExternalHeadPartDiagnosticCodes.ProviderDisabled,
                    provider.Plugin.Value,
                    provider.PluginSha256,
                    current,
                    false,
                    "Enable the exact declared provider plugin in the supplied load order.",
                    diagnostics,
                    prerequisites);
            }
            else if (!identityMatches)
            {
                providersMatch = false;
                AddPrerequisite(
                    ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch,
                    provider.Plugin.Value,
                    provider.PluginSha256,
                    current,
                    true,
                    "Restore the declared provider plugin bytes and rerun verification.",
                    diagnostics,
                    prerequisites);
            }
        }

        bool routeMatches = false;
        bool assetsMatch = false;
        bool physicsMatches = false;
        if (contextShapeValid && allPluginsReadable && providersMatch &&
            descriptorClosureValid)
        {
            routeMatches = await VerifyRecordClosureAsync(
                request,
                context,
                descriptors,
                pluginAuthorities,
                diagnostics,
                prerequisites,
                cancellationToken).ConfigureAwait(false);
            assetsMatch = await VerifyAssetClosureAsync(
                context,
                descriptors,
                diagnostics,
                prerequisites,
                observations,
                cancellationToken).ConfigureAwait(false);
            physicsMatches = await VerifyPhysicsClosureAsync(
                context,
                descriptors,
                physicsBindingResolver,
                diagnostics,
                prerequisites,
                observations,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            AddPrerequisite(
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                "install-context",
                null,
                null,
                null,
                "Supply a complete, readable K-local enabled plugin order before strict verification.",
                diagnostics,
                prerequisites);
        }

        bool accepted = descriptorClosureValid && contextShapeValid &&
            allPluginsReadable && providersMatch && routeMatches &&
            assetsMatch && physicsMatches;

        if (accepted)
        {
            if (File.Exists(request.OutputPlugin.Value) &&
                request.ExpectedOutputPluginSha256 != ZeroHash)
            {
                FileInfo outputInfo = new(request.OutputPlugin.Value);
                observations.Add(new ExternalHeadPartInstallObservation(
                    "package-output-plugin",
                    request.OutputPluginName.Value,
                    request.ExpectedOutputPluginSha256,
                    outputInfo.Length,
                    observations.Count));
            }
            foreach (ExternalHeadPartDependencyDescriptor descriptor in descriptors)
            {
                foreach (ExternalHeadPartRecordDependency member in descriptor.Members)
                {
                    observations.Add(new ExternalHeadPartInstallObservation(
                        "winning-record",
                        member.OriginForm.ToString(),
                        member.WinningRecordSha256,
                        member.WinningPluginByteLength,
                        observations.Count));
                }
            }

            ExternalHeadPartInstallContextFingerprint contextFingerprint =
                BuildFingerprint(observations);
            return new FreshVerification(
                true,
                contextFingerprint,
                providerObservations
                    .OrderBy(item => item.ProviderPlugin.Value,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.ProviderPlugin.Value,
                        StringComparer.Ordinal)
                    .ToImmutableArray());
        }

        return new FreshVerification(
            false,
            null,
            providerObservations
                .OrderBy(item => item.ProviderPlugin.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.ProviderPlugin.Value,
                    StringComparer.Ordinal)
                .ToImmutableArray());
    }

    private static bool ValidateEnabledMasterClosure(
        ExternalHeadPartInstallVerificationContext context,
        IReadOnlyList<SkyrimFaceRecordPluginAuthority> authorities,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<ExternalHeadPartInstallPrerequisite>.Builder prerequisites)
    {
        var order = context.EnabledPluginOrder
            .Select((plugin, index) => (plugin.Value, index))
            .ToDictionary(item => item.Value,
                item => item.index,
                StringComparer.OrdinalIgnoreCase);
        bool valid = true;
        foreach (SkyrimFaceRecordPluginAuthority authority in authorities)
        {
            try
            {
                using var mod = SkyrimMod.CreateFromBinaryOverlay(
                    authority.Path.Value, SkyrimRelease.SkyrimSE);
                int authorityIndex = order[authority.Plugin.Value];
                foreach (var master in mod.MasterReferences)
                {
                    string name = master.Master.ToString();
                    if (order.TryGetValue(name, out int masterIndex) &&
                        masterIndex < authorityIndex)
                        continue;
                    valid = false;
                    AddPrerequisite(
                        ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                        authority.Plugin.Value,
                        null,
                        null,
                        null,
                        "Supply every plugin master in the complete enabled order before the dependent plugin.",
                        diagnostics,
                        prerequisites);
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                        $"Enabled plugin '{authority.Plugin}' has master '{name}' outside the supplied ordered identity list."));
                }
            }
            catch (Exception exception) when (exception is IOException or
                InvalidDataException or ArgumentException or NotSupportedException)
            {
                valid = false;
                AddPrerequisite(
                    ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                    authority.Plugin.Value,
                    null,
                    null,
                    null,
                    "Reopen every enabled plugin and provide a complete ordered identity list.",
                    diagnostics,
                    prerequisites);
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                    $"Enabled plugin '{authority.Plugin}' could not be reopened for master closure: {exception.Message}"));
            }
        }

        return valid;
    }

    private static async ValueTask<bool> VerifyRecordClosureAsync(
        ExternalHeadPartInstallVerificationRequest request,
        ExternalHeadPartInstallVerificationContext context,
        ImmutableArray<ExternalHeadPartDependencyDescriptor> descriptors,
        List<SkyrimFaceRecordPluginAuthority> pluginAuthorities,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<ExternalHeadPartInstallPrerequisite>.Builder prerequisites,
        CancellationToken cancellationToken)
    {
        bool valid = true;
        var policy = CreateKOnlyPolicy(context.DataRoot, diagnostics);
        if (policy is null) return false;

        // The package output is a Manager-owned record authority.  It is
        // admitted to the graph only after the package-bound bytes pass their
        // independent check; a missing output is already a closure failure.
        var routeAuthorities = new List<SkyrimFaceRecordPluginAuthority>(
            pluginAuthorities);
        if (File.Exists(request.OutputPlugin.Value) &&
            request.ExpectedOutputPluginSha256 != ZeroHash &&
            IsOrdinaryExistingFile(new WorkspacePath(
                Path.GetDirectoryName(request.OutputPlugin.Value)!),
                request.OutputPlugin))
        {
            routeAuthorities.Add(new SkyrimFaceRecordPluginAuthority(
                request.OutputPluginName,
                request.OutputPlugin,
                request.ExpectedOutputPluginSha256));
        }

        foreach (ExternalHeadPartDependencyDescriptor descriptor in descriptors)
        {
            ImmutableArray<SkyrimFaceRecordHeadPartSelection> selections =
                [new SkyrimFaceRecordHeadPartSelection(
                    descriptor.RootSourceForm,
                    descriptor.Members[0].TriRoutes.Select(item => item.Role)
                        .ToImmutableArray())];
            var routeRequest = new SkyrimFaceRecordRouteRequest(
                GameEdition.SkyrimSpecialEdition,
                context.TargetRace,
                descriptor.Members.Select(item => item.AppliesToSex)
                    .FirstOrDefault(item => item is not null) ?? NpcSex.Male,
                selections,
                routeAuthorities.ToImmutableArray());
            var resolver = new BethesdaSkyrimFaceRecordRouteResolver(
                policy, GetLabRoot(context.DataRoot));
            SkyrimFaceRecordRouteResult route = await resolver.ResolveAsync(
                routeRequest, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(route.Diagnostics);
            if (!route.Accepted || route.Route is null ||
                !GraphMatchesDescriptor(route.Route.HeadPartGraph, descriptor))
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    descriptor.RootSourceForm.ToString(),
                    "The current winning HDPT/HNAM route differs from the selected external descriptor.",
                    diagnostics,
                    prerequisites);
            }
        }

        return valid;
    }

    private static async ValueTask<bool> VerifyAssetClosureAsync(
        ExternalHeadPartInstallVerificationContext context,
        ImmutableArray<ExternalHeadPartDependencyDescriptor> descriptors,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<ExternalHeadPartInstallPrerequisite>.Builder prerequisites,
        List<ExternalHeadPartInstallObservation> observations,
        CancellationToken cancellationToken)
    {
        bool valid = true;
        foreach (ExternalHeadPartDependencyDescriptor descriptor in descriptors)
        {
            foreach (ExternalHeadPartAssetDependency asset in descriptor.Assets
                         .OrderBy(item => item.Path.Value,
                             StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssetReadEvidence? current = await ReadAssetAsync(
                    context.DataRoot,
                    asset,
                    cancellationToken).ConfigureAwait(false);
                if (current is null || current.Sha256 != asset.Sha256 ||
                    current.ByteLength != asset.ByteLength)
                {
                    valid = false;
                    AddClosureFailure(
                        current is null
                            ? ExternalHeadPartDiagnosticCodes.AssetMissing
                            : ExternalHeadPartDiagnosticCodes.AssetDrift,
                        asset.Path.Value,
                        "An external provider asset is missing or differs from its hash-bound authority.",
                        diagnostics,
                        prerequisites);
                    continue;
                }

                observations.Add(new ExternalHeadPartInstallObservation(
                    "asset",
                    asset.Path.Value,
                    current.Sha256,
                    current.ByteLength,
                    observations.Count));
                if (current.Archive is { } archive)
                {
                    observations.Add(new ExternalHeadPartInstallObservation(
                        "archive",
                        archive.Path,
                        archive.Sha256,
                        archive.ByteLength,
                        observations.Count));
                }
            }
        }

        return valid;
    }

    private static async ValueTask<bool> VerifyPhysicsClosureAsync(
        ExternalHeadPartInstallVerificationContext context,
        ImmutableArray<ExternalHeadPartDependencyDescriptor> descriptors,
        IExternalHeadPartPhysicsBindingResolver? physicsBindingResolver,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<ExternalHeadPartInstallPrerequisite>.Builder prerequisites,
        List<ExternalHeadPartInstallObservation> observations,
        CancellationToken cancellationToken)
    {
        bool valid = true;
        var policy = CreateKOnlyPolicy(context.DataRoot, diagnostics);
        if (policy is null) return false;
        IExternalHeadPartPhysicsBindingResolver resolver =
            physicsBindingResolver ?? new ExternalHeadPartPhysicsBindingResolver(
                policy, GetLabRoot(context.DataRoot));
        foreach (ExternalHeadPartDependencyDescriptor descriptor in descriptors)
        {
            ImmutableArray<ExternalHeadPartRecordDependency> members =
                SelectPrimaryProviderPhysicsMembers(descriptor);
            if (!IsProviderPhysicsSubsetValid(descriptor, members))
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.PhysicsMissing,
                    descriptor.DescriptorId.Value,
                    "The descriptor physics contract does not close over provider-owned model members.",
                    diagnostics,
                    prerequisites);
                continue;
            }

            ExternalHeadPartPhysicsBindingResult result = await resolver.ResolveAsync(
                new ExternalHeadPartPhysicsBindingRequest(
                    context.DataRoot,
                    context.EnabledPluginOrder,
                    descriptor.Provider,
                    members,
                    descriptor.PhysicsBinding,
                    descriptor.Members),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(result.Diagnostics);
            if (!result.Accepted || result.Binding is null ||
                !PhysicsBindingMatches(result.Binding, descriptor.Physics))
            {
                valid = false;
                AddClosureFailure(
                    ExternalHeadPartDiagnosticCodes.PhysicsMissing,
                    descriptor.DescriptorId.Value,
                    "The current physics XML, mapping, or shape binding differs from the descriptor.",
                    diagnostics,
                    prerequisites);
            }

            if (result.Binding is { } binding)
            {
                foreach (ExternalHeadPartPhysicsShapeBinding shape in
                         binding.Shapes.OrderBy(item => item.XmlPath.Value,
                             StringComparer.Ordinal)
                             .ThenBy(item => item.MemberForm.ToString(),
                                 StringComparer.Ordinal)
                             .ThenBy(item => item.ModelNif.Value,
                                 StringComparer.Ordinal)
                             .ThenBy(item => item.ShapeName,
                                 StringComparer.Ordinal))
                    observations.Add(new ExternalHeadPartInstallObservation(
                        "physics-xml",
                        shape.XmlPath.Value,
                        shape.XmlSha256,
                        shape.XmlByteLength,
                        observations.Count));
                if (binding.MappingAuthority is { } mapping)
                    observations.Add(new ExternalHeadPartInstallObservation(
                        "physics-default-bbp",
                        mapping.Path.Value,
                        mapping.Sha256,
                        mapping.ByteLength,
                        observations.Count));
            }
        }

        return valid;
    }

    internal static ImmutableArray<ExternalHeadPartRecordDependency>
        SelectPrimaryProviderPhysicsMembers(
            ExternalHeadPartDependencyDescriptor descriptor) =>
        descriptor.Members
            .Where(item => item.ModelNif is not null &&
                ExternalHeadPartMemberAuthority.Classify(
                    item, descriptor.Provider) ==
                    ExternalHeadPartMemberAuthorityKind.PrimaryProvider)
            .OrderBy(item => item.RouteOrder)
            .ToImmutableArray();

    internal static bool IsProviderPhysicsSubsetValid(
        ExternalHeadPartDependencyDescriptor descriptor,
        ImmutableArray<ExternalHeadPartRecordDependency> members)
    {
        if (members.IsDefaultOrEmpty || descriptor.Physics.Shapes.IsDefaultOrEmpty)
            return false;
        HashSet<FormReference> providerForms = members
            .Select(item => item.OriginForm)
            .ToHashSet();
        return descriptor.Physics.Shapes.All(shape =>
            providerForms.Contains(shape.MemberForm));
    }

    private static bool GraphMatchesDescriptor(
        ImmutableArray<SkyrimFaceHeadPartGraphRoute> graph,
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        ExternalHeadPartRecordDependency[] expected = descriptor.Members
            .OrderBy(item => item.RouteOrder)
            .ToArray();
        if (graph.Length != expected.Length) return false;
        for (int index = 0; index < expected.Length; index++)
        {
            SkyrimFaceHeadPartGraphRoute actual = graph[index];
            ExternalHeadPartRecordDependency item = expected[index];
            if (actual.OriginForm != item.OriginForm ||
                actual.RequiredOutputMaster != item.RequiredOutputMaster ||
                actual.WinningForm != item.WinningForm ||
                actual.WinningPlugin != item.WinningPlugin ||
                actual.WinningPluginSha256 != item.WinningPluginSha256 ||
                actual.WinningPluginByteLength != item.WinningPluginByteLength ||
                actual.WinningRecordSha256 != item.WinningRecordSha256 ||
                !string.Equals(actual.EditorId, item.EditorId,
                    StringComparison.Ordinal) ||
                actual.DeclaredType != item.DeclaredType ||
                actual.EffectiveType != item.EffectiveType ||
                actual.ModelNif != item.ModelNif ||
                !actual.TriRoutes.SequenceEqual(item.TriRoutes) ||
                !actual.HnamEdges.SequenceEqual(item.HnamEdges) ||
                actual.Parent != item.Parent ||
                actual.Depth != item.Depth ||
                actual.RouteOrder != item.RouteOrder ||
                actual.AppliesToSex != item.AppliesToSex ||
                actual.ValidRace != item.ValidRace)
                return false;
        }

        return true;
    }

    private static bool OutputBindingsMatch(
        WorkspacePath outputPlugin,
        PluginName expectedOutputPluginName,
        ImmutableArray<PluginName> expectedMasters,
        ImmutableArray<FormReference> expectedFullMemberPnam,
        ImmutableHashSet<string> selectedProviderPlugins,
        ImmutableArray<FormReference> expectedSelectedProviderPnam,
        FormId? targetActorFormId,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            using var mod = SkyrimMod.CreateFromBinaryOverlay(
                outputPlugin.Value, SkyrimRelease.SkyrimSE);
            ModKey requestedOutputModKey = ModKey.FromNameAndExtension(
                expectedOutputPluginName.Value);
            if (mod.ModKey != requestedOutputModKey)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "The decoded output plugin identity differs from its requested output-plugin name."));
                return false;
            }
            ImmutableArray<PluginName> actual = mod.MasterReferences
                .Select(item => new PluginName(item.Master.ToString()))
                .ToImmutableArray();
            if (!PluginSequenceEqualIgnoreCase(actual, expectedMasters))
                return false;

            var outputModKey = mod.ModKey;
            var expectedMasterPlugins = expectedMasters
                .Select(item => item.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            IEnumerable<INpcGetter> observedNpcs = mod.Npcs;
            INpcGetter? targetNpc = null;
            if (targetActorFormId is { Value: <= 0 or > 0x00FF_FFFF })
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "The target actor FormID is outside Skyrim's local plugin range."));
                return false;
            }
            if (targetActorFormId is { } target)
            {
                FormKey targetKey = new(outputModKey, target.Value);
                INpcGetter[] matches = mod.Npcs
                    .Where(npc => npc.FormKey == targetKey)
                    .ToArray();
                if (matches.Length != 1)
                {
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        "The package output plugin did not contain exactly one requested actor for PNAM closure."));
                    return false;
                }
                targetNpc = matches[0];
                observedNpcs = matches;
            }
            else if (mod.Npcs.Count == 1)
            {
                // PackageVerifyService preserves the legacy nullable target
                // field.  A single output NPC is still an exact closure
                // subject; reject ambiguity rather than admitting output
                // records against an arbitrary NPC.
                targetNpc = mod.Npcs.Single();
                if (targetNpc.FormKey.ModKey != outputModKey ||
                    targetNpc.FormKey.ID == 0 ||
                    targetNpc.FormKey.ID > 0x00FF_FFFF)
                {
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        "The sole output NPC is not owned by the output plugin or has an invalid local FormID."));
                    return false;
                }
            }
            else
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "Strict external verification requires one unambiguous output actor when the target FormID is absent."));
                return false;
            }

            var targetOutputHeadPartReferences = targetNpc?.HeadParts
                .Select(link => link.FormKey)
                .Where(key => key.ModKey == outputModKey)
                .GroupBy(key => key)
                .ToDictionary(group => group.Key, group => group.Count());
            IHeadPartGetter[] outputOwnedHeadParts = mod.HeadParts
                .Where(item => item.FormKey.ModKey == outputModKey)
                .ToArray();
            ITextureSetGetter[] outputOwnedTextureSets = mod.TextureSets
                .Where(item => item.FormKey.ModKey == outputModKey)
                .ToArray();
            if (mod.TextureSets.Any(item => item.FormKey.ModKey != outputModKey))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "The package output plugin carries an unbound non-output TXST record."));
                return false;
            }
            FormKey? privateOutputTextureSet = null;
            if (outputOwnedHeadParts.Length != 0 ||
                outputOwnedTextureSets.Length != 0)
            {
                if (targetNpc is null || outputOwnedHeadParts.Length != 1 ||
                    outputOwnedTextureSets.Length != 1 ||
                    targetOutputHeadPartReferences is null)
                {
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        "The package output plugin does not contain exactly one target-linked output Face HDPT and private TXST pair."));
                    return false;
                }
                IHeadPartGetter outputFace = outputOwnedHeadParts[0];
                ITextureSetGetter outputTextureSet = outputOwnedTextureSets[0];
                if (!targetOutputHeadPartReferences.TryGetValue(
                        outputFace.FormKey, out int outputFaceReferences) ||
                    outputFaceReferences != 1 ||
                    !IsLegitimateOutputOwnedFaceHeadPart(mod, outputFace) ||
                    outputFace.TextureSet.FormKeyNullable != outputTextureSet.FormKey ||
                    targetNpc.HeadTexture.FormKeyNullable != outputTextureSet.FormKey ||
                    outputTextureSet.IsDeleted)
                {
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        "The output-owned Face HDPT and private TXST are not an exact target-linked pair."));
                    return false;
                }
                privateOutputTextureSet = outputTextureSet.FormKey;
            }
            else if (targetNpc?.HeadTexture.FormKeyNullable is { } targetTexture &&
                     targetTexture.ModKey == outputModKey)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "The target actor references an output-owned private TXST without its Face HDPT pair."));
                return false;
            }
            if (privateOutputTextureSet is { } privateTexture &&
                mod.Npcs.Any(npc => npc.FormKey != targetNpc?.FormKey &&
                    npc.HeadTexture.FormKeyNullable == privateTexture))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "A non-target NPC references the target private output TXST."));
                return false;
            }
            if (targetNpc is not null && mod.Npcs.Any(npc =>
                    npc.FormKey != targetNpc.FormKey &&
                    npc.HeadParts.Any(link => link.FormKey.ModKey == outputModKey)))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "A non-target output NPC references an output-owned HDPT."));
                return false;
            }
            if (targetActorFormId is not null &&
                mod.Npcs.Any(npc => npc.FormKey != targetNpc?.FormKey))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "The package output plugin contains an unrelated NPC row outside the requested target closure."));
                return false;
            }
            if (mod.HeadParts.Any(headPart =>
                    headPart.FormKey.ModKey != outputModKey &&
                    headPart.TextureSet.FormKeyNullable is { } texture &&
                    texture.ModKey == outputModKey))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "A provider-owned HDPT references an output-owned private TXST."));
                return false;
            }
            var observedHeadPartKeys = new HashSet<FormKey>();
            foreach (IHeadPartGetter headPart in mod.HeadParts)
            {
                if (!observedHeadPartKeys.Add(headPart.FormKey))
                {
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        $"The package output plugin contains duplicate HDPT '{headPart.FormKey}'."));
                    return false;
                }
                var form = new FormReference(
                    new PluginName(headPart.FormKey.ModKey.ToString()),
                    new FormId(headPart.FormKey.ID));
                if (headPart.FormKey.ModKey == outputModKey)
                {
                    if (targetOutputHeadPartReferences is null ||
                        !targetOutputHeadPartReferences.TryGetValue(
                            headPart.FormKey, out int referenceCount) ||
                        referenceCount != 1 ||
                        !IsLegitimateOutputOwnedFaceHeadPart(mod, headPart))
                    {
                        diagnostics.Add(Error(
                            ExternalHeadPartDiagnosticCodes.RecordDrift,
                            $"The package output plugin contains an undeclared HDPT '{form}'."));
                        return false;
                    }
                }
                else
                {
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        $"The package output plugin contains a provider-owned HDPT override '{form}', which is not accepted from an output-plugin record."));
                    return false;
                }
            }

            if (targetNpc is not null)
            {
                foreach (FormKey key in targetOutputHeadPartReferences!.Keys)
                {
                    if (mod.HeadParts.Count(item => item.FormKey == key) != 1)
                    {
                        diagnostics.Add(Error(
                            ExternalHeadPartDiagnosticCodes.RecordDrift,
                            $"The requested actor references an undeclared output-owned HDPT '{key}'."));
                        return false;
                    }
                }
            }
            ImmutableArray<FormReference> outputPnam = observedNpcs
                .SelectMany(npc => npc.HeadParts.Select(link =>
                    new FormReference(
                        new PluginName(link.FormKey.ModKey.ToString()),
                        new FormId(link.FormKey.ID))))
                .ToImmutableArray();
            foreach (FormReference link in outputPnam)
            {
                bool isOutputOwned = ModKey.FromNameAndExtension(link.Plugin.Value) ==
                    outputModKey;
                if (!isOutputOwned &&
                    selectedProviderPlugins.Contains(link.Plugin.Value) &&
                    !ContainsFormReferenceIgnoreCase(expectedSelectedProviderPnam, link))
                {
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        $"The package output plugin contains an undeclared selected-provider PNAM link '{link}'."));
                    return false;
                }
                if (!isOutputOwned &&
                    !expectedMasterPlugins.Contains(link.Plugin.Value))
                {
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        $"The package output plugin PNAM link '{link}' does not belong to an expected output master."));
                    return false;
                }
            }
            ImmutableArray<FormReference> actualNonOutputPnam = outputPnam
                .Where(link => ModKey.FromNameAndExtension(link.Plugin.Value) !=
                    outputModKey)
                .ToImmutableArray();
            ImmutableArray<FormReference> actualSelectedProviderPnam = actualNonOutputPnam
                .Where(link => selectedProviderPlugins.Contains(link.Plugin.Value))
                .ToImmutableArray();
            if (!FormReferenceSequenceEqualIgnoreCase(
                    actualSelectedProviderPnam, expectedSelectedProviderPnam))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "The package output plugin selected-provider PNAM sequence differs from its descriptor binding."));
                return false;
            }
            if (!IsOrderedSubsequence(expectedFullMemberPnam, actualNonOutputPnam))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    "The package output plugin PNAM sequence does not contain the ordered descriptor-member binding."));
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or
            InvalidDataException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.OutputMasterMissing,
                $"The package output plugin could not be decoded for master closure: {exception.Message}"));
            return false;
        }
    }

    private static bool IsOrderedSubsequence(
        ImmutableArray<FormReference> expected,
        ImmutableArray<FormReference> actual)
    {
        int expectedIndex = 0;
        foreach (FormReference observed in actual)
        {
            if (expectedIndex < expected.Length &&
                FormReferencesEqualIgnoreCase(observed, expected[expectedIndex]))
                expectedIndex++;
        }

        return expectedIndex == expected.Length;
    }

    private static bool PluginNamesEqual(PluginName left, PluginName right) =>
        string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private static bool PluginSequenceEqualIgnoreCase(
        ImmutableArray<PluginName> left,
        ImmutableArray<PluginName> right) =>
        left.Length == right.Length &&
        left.Zip(right).All(pair => PluginNamesEqual(pair.First, pair.Second));

    private static bool FormReferencesEqualIgnoreCase(
        FormReference left,
        FormReference right) =>
        left.FormId == right.FormId &&
        PluginNamesEqual(left.Plugin, right.Plugin);

    private static bool ContainsFormReferenceIgnoreCase(
        ImmutableArray<FormReference> values,
        FormReference expected) =>
        values.Any(value => FormReferencesEqualIgnoreCase(value, expected));

    private static bool FormReferenceSequenceEqualIgnoreCase(
        ImmutableArray<FormReference> left,
        ImmutableArray<FormReference> right) =>
        left.Length == right.Length &&
        left.Zip(right).All(pair =>
            FormReferencesEqualIgnoreCase(pair.First, pair.Second));

    private static bool IsLegitimateOutputOwnedFaceHeadPart(
        ISkyrimModGetter mod,
        IHeadPartGetter headPart)
    {
        if (headPart.Type != HeadPart.TypeEnum.Face ||
            headPart.IsDeleted || headPart.IsCompressed ||
            !headPart.Flags.HasFlag(HeadPart.Flag.Playable) ||
            headPart.Flags.HasFlag(HeadPart.Flag.Male) &&
                headPart.Flags.HasFlag(HeadPart.Flag.Female) ||
            headPart.Model is not { } model || model.File is null ||
            (model.AlternateTextures?.Count ?? 0) != 0 ||
            headPart.ExtraParts.Count != 0 || !headPart.Color.IsNull ||
            headPart.Parts.Count is < 1 or > 4 ||
            headPart.Parts.Any(item => item.FileName is null) ||
            headPart.TextureSet.FormKeyNullable is not { } textureKey ||
            textureKey.ModKey != mod.ModKey)
            return false;

        string path = model.File.ToString();
        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            _ = new AssetPath(path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        ITextureSetGetter? textureSet = mod.TextureSets.FirstOrDefault(
            item => item.FormKey == textureKey);
        return textureSet is not null && !textureSet.IsDeleted &&
            textureSet.Flags is { } flags &&
            flags.HasFlag(TextureSet.Flag.FaceGenTextures) &&
            flags.HasFlag(TextureSet.Flag.HasModelSpaceNormalMap);
    }

    private static bool PhysicsBindingMatches(
        ExternalHeadPartPhysicsBinding actual,
        ExternalHeadPartPhysicsBinding expected)
    {
        if (actual.Mode != expected.Mode ||
            actual.MappingAuthority != expected.MappingAuthority)
            return false;
        var actualShapes = actual.Shapes
            .OrderBy(item => item.MemberForm.ToString(), StringComparer.Ordinal)
            .ThenBy(item => item.ModelNif.Value, StringComparer.Ordinal)
            .ThenBy(item => item.ShapeName, StringComparer.Ordinal)
            .ToArray();
        var expectedShapes = expected.Shapes
            .OrderBy(item => item.MemberForm.ToString(), StringComparer.Ordinal)
            .ThenBy(item => item.ModelNif.Value, StringComparer.Ordinal)
            .ThenBy(item => item.ShapeName, StringComparer.Ordinal)
            .ToArray();
        return actualShapes.SequenceEqual(expectedShapes);
    }

    private static ExternalHeadPartInstallContextFingerprint BuildFingerprint(
        IEnumerable<ExternalHeadPartInstallObservation> observations)
    {
        ImmutableArray<ExternalHeadPartInstallObservation> ordered = observations
            .Select((item, index) => item with { Order = index })
            .ToImmutableArray();
        return new ExternalHeadPartInstallContextFingerprint(
            ExternalHeadPartDependencyDescriptorCodec
                .ComputeInstallContextFingerprintHash(ordered),
            ordered);
    }

    private static bool ValidateContextShape(
        ExternalHeadPartInstallVerificationContext context,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        bool valid = true;
        if (!IsKLocalOrdinaryRoot(context.DataRoot, diagnostics, "Data root"))
            valid = false;
        if (!Directory.Exists(context.DataRoot.Value))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                "The supplied Data root does not exist."));
            valid = false;
        }
        if (context.EnabledPluginOrder.IsDefaultOrEmpty ||
            context.EnabledPluginOrder.Length > MaximumPlugins)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                "A complete enabled plugin order is required for install authority."));
            valid = false;
        }
        else if (context.EnabledPluginOrder.Select(item => item.Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                 context.EnabledPluginOrder.Length)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                "The enabled plugin order contains duplicate plugin identities."));
            valid = false;
        }
        if (!context.EnabledPluginOrder.Any(item =>
                string.Equals(item.Value, context.TargetRace.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                $"The exact target race plugin '{context.TargetRace.Plugin.Value}' is not present in the enabled plugin order."));
            valid = false;
        }

        return valid;
    }

    private static KOnlyWorkspacePolicy? CreateKOnlyPolicy(
        WorkspacePath dataRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            WorkspacePath labRoot = GetLabRoot(dataRoot);
            var policy = new KOnlyWorkspacePolicy(
                labRoot, ActorwrightWorkspace.ResolveProtectedRoot(labRoot));
            ImmutableArray<Diagnostic> preflight = policy.EvaluateReadRoot(
                labRoot, dataRoot);
            diagnostics.AddRange(preflight);
            return preflight.Any(item => item.Severity == DiagnosticSeverity.Error)
                ? null
                : policy;
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                exception.Message));
            return null;
        }
    }

    private static WorkspacePath GetLabRoot(WorkspacePath path) =>
        new(Path.GetPathRoot(path.Value) ?? throw new ArgumentException(
            "A fully qualified K-local path is required."));

    private static async ValueTask<AssetReadEvidence?> ReadAssetAsync(
        WorkspacePath dataRoot,
        ExternalHeadPartAssetDependency expected,
        CancellationToken cancellationToken)
    {
        if (expected.ArchiveMember is not { } archive)
        {
            if (!TryResolveDataAsset(dataRoot, expected.Path,
                    out WorkspacePath loosePath) ||
                !IsOrdinaryExistingFile(dataRoot, loosePath))
                return null;
            FileInfo info = new(loosePath.Value);
            if (info.Length <= 0 || info.Length > MaximumAssetBytes)
                return null;
            return new AssetReadEvidence(
                await HashFileAsync(loosePath.Value, cancellationToken)
                    .ConfigureAwait(false),
                info.Length,
                null);
        }

        if (TryResolveDataAsset(dataRoot, expected.Path,
                out WorkspacePath loose) && File.Exists(loose.Value))
            return null;
        if (!TryResolveArchivePath(dataRoot, archive.ArchivePath,
                out WorkspacePath archivePath) ||
            !IsOrdinaryExistingFile(dataRoot, archivePath))
            return null;
        FileInfo archiveInfo = new(archivePath.Value);
        if (archiveInfo.Length != archive.ArchiveByteLength ||
            archiveInfo.Length <= 0 || archiveInfo.Length > MaximumAssetBytes * 8)
            return null;
        Sha256Hash archiveHash = await HashFileAsync(archivePath.Value,
            cancellationToken).ConfigureAwait(false);
        if (archiveHash != archive.ArchiveSha256)
            return null;

        try
        {
            var reader = Archive.CreateReader(
                GameRelease.SkyrimSE,
                new FilePath(archivePath.Value),
                new FileSystem());
            if (reader.Files
                    .GroupBy(item => NormalizeArchivePath(item.Path),
                        StringComparer.OrdinalIgnoreCase)
                    .Any(group => group.Count() > 1))
                return null;
            IArchiveFile? member = reader.Files.FirstOrDefault(item =>
                string.Equals(NormalizeArchivePath(item.Path),
                    expected.Path.Value, StringComparison.OrdinalIgnoreCase));
            if (member is null || member.Size != archive.MemberByteLength ||
                member.Size <= 0 || member.Size > MaximumAssetBytes)
                return null;
            using Stream stream = member.AsStream();
            Sha256Hash memberHash = await HashStreamAsync(stream,
                cancellationToken).ConfigureAwait(false);
            if (memberHash != archive.MemberSha256)
                return null;
            return new AssetReadEvidence(
                memberHash,
                member.Size,
                new ArchiveReadEvidence(
                    archive.ArchivePath.Value,
                    archiveHash,
                    archiveInfo.Length));
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string NormalizeArchivePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            return ZeroHash;
        }
    }

    private static Sha256Hash HashFile(string path)
    {
        try
        {
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            using IncrementalHash hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            byte[] buffer = new byte[64 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
            return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            return ZeroHash;
        }
    }

    private static Sha256Hash HashBytes(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static bool IsSha256(Sha256Hash hash) =>
        hash.Value is { Length: 64 } && hash.Value.All(Uri.IsHexDigit);

    private static async ValueTask<Sha256Hash> HashStreamAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return new Sha256Hash(Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static bool TryResolveDataPlugin(
        WorkspacePath dataRoot,
        PluginName plugin,
        out WorkspacePath path)
    {
        path = default;
        try
        {
            string candidate = Path.Combine(dataRoot.Value, plugin.Value);
            path = new WorkspacePath(candidate);
            return path.IsUnder(dataRoot) &&
                string.Equals(Path.GetDirectoryName(path.Value),
                    dataRoot.Value, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryResolveDataAsset(
        WorkspacePath dataRoot,
        AssetPath asset,
        out WorkspacePath path)
    {
        path = default;
        try
        {
            path = new WorkspacePath(Path.Combine(
                dataRoot.Value,
                asset.Value.Replace('/', Path.DirectorySeparatorChar)));
            return path.IsUnder(dataRoot);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryResolveArchivePath(
        WorkspacePath dataRoot,
        AssetPath archive,
        out WorkspacePath path)
    {
        path = default;
        if (archive.Value.Contains('/') || archive.Value.Contains('\\'))
            return false;
        try
        {
            path = new WorkspacePath(Path.Combine(dataRoot.Value, archive.Value));
            return path.IsUnder(dataRoot);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsKLocalOrdinaryRoot(
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string role)
    {
        string value = path.Value;
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(Error(
                ProtocolV2DiagnosticCodes.ReparsePointRefused,
                $"The {role} path is required and must be ordinary."));
            return false;
        }
        string root = Path.GetPathRoot(value) ?? string.Empty;
        bool kLocal = root.Equals(@"K:\", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith(@"\\", StringComparison.Ordinal) &&
            !value.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !value.StartsWith(@"\\.\", StringComparison.Ordinal) &&
            !HasAlternateDataStream(value);
        if (!kLocal)
        {
            diagnostics.Add(Error(
                ProtocolV2DiagnosticCodes.WorkspaceRootOutsideLab,
                $"The {role} must remain on the ordinary K: drive."));
            return false;
        }
        if (!Directory.Exists(value) || ContainsReparseBetween(value, value))
        {
            diagnostics.Add(Error(
                ProtocolV2DiagnosticCodes.ReparsePointRefused,
                $"The {role} must be an existing ordinary directory with no reparse ancestry."));
            return false;
        }
        return true;
    }

    private static bool IsKLocalOrdinaryPath(
        WorkspacePath root,
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string role)
    {
        if (string.IsNullOrWhiteSpace(root.Value) ||
            string.IsNullOrWhiteSpace(path.Value))
        {
            diagnostics.Add(Error(
                ProtocolV2DiagnosticCodes.ReparsePointRefused,
                $"The {role} path is required and must be ordinary."));
            return false;
        }
        try
        {
            if (!path.IsUnder(root) || path == root || HasAlternateDataStream(path.Value) ||
                ContainsReparseBetween(root.Value, path.Value))
            {
                diagnostics.Add(Error(
                    ProtocolV2DiagnosticCodes.ReparsePointRefused,
                    $"The {role} must remain an ordinary path below the package root."));
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Add(Error(
                ProtocolV2DiagnosticCodes.ReparsePointRefused,
                $"The {role} path is invalid or unsafe: {exception.Message}"));
            return false;
        }
        return true;
    }

    private static bool IsOrdinaryExistingFile(
        WorkspacePath root,
        WorkspacePath path)
    {
        try
        {
            if (!path.IsUnder(root) || !File.Exists(path.Value)) return false;
            FileAttributes attributes = File.GetAttributes(path.Value);
            return !attributes.HasFlag(FileAttributes.ReparsePoint) &&
                !attributes.HasFlag(FileAttributes.Device) &&
                !attributes.HasFlag(FileAttributes.Directory) &&
                !ContainsReparseBetween(root.Value, path.Value);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryResolvePackagePath(
        WorkspacePath packageRoot,
        AssetPath relative,
        out WorkspacePath path)
    {
        path = default;
        try
        {
            path = new WorkspacePath(Path.Combine(
                packageRoot.Value,
                relative.Value.Replace('/', Path.DirectorySeparatorChar)));
            return path.IsUnder(packageRoot);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool ContainsReparseBetween(string root, string path)
    {
        try
        {
            string rootFull = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = Path.GetFullPath(path);
            while (current.Length >= rootFull.Length)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(current);
                }
                catch (FileNotFoundException)
                {
                    attributes = 0;
                }
                catch (DirectoryNotFoundException)
                {
                    attributes = 0;
                }
                if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    attributes.HasFlag(FileAttributes.Device))
                    return true;
                if (string.Equals(current, rootFull,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                string? parent = Path.GetDirectoryName(current);
                if (parent is null || string.Equals(parent, current,
                        StringComparison.OrdinalIgnoreCase))
                    return true;
                current = parent;
            }
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
        return true;
    }

    private static bool HasAlternateDataStream(string value)
    {
        string root = Path.GetPathRoot(value) ?? string.Empty;
        return value.AsSpan(root.Length).Contains(':');
    }

    private static void AddClosureFailure(
        string code,
        string identity,
        string message,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<ExternalHeadPartInstallPrerequisite>.Builder prerequisites)
    {
        diagnostics.Add(Error(code, message));
        AddPrerequisite(code, identity, null, null, null, "Repair the hash-bound package or provider evidence and rerun verification.",
            diagnostics, prerequisites, addDiagnostic: false);
    }

    private static void AddPrerequisite(
        string code,
        string identity,
        Sha256Hash? expected,
        Sha256Hash? current,
        bool? enabled,
        string nextAction,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<ExternalHeadPartInstallPrerequisite>.Builder prerequisites,
        bool addDiagnostic = true)
    {
        if (addDiagnostic)
            diagnostics.Add(Error(code,
                $"External install prerequisite '{identity}' is not satisfied."));
        if (string.IsNullOrWhiteSpace(identity) || identity.Any(char.IsControl) ||
            identity.Contains('\\') || identity.Contains('/') || identity.Contains(':'))
            identity = "install-context";
        if (!prerequisites.Any(item =>
                item.DiagnosticCode == code && item.PortableIdentity == identity))
        {
            prerequisites.Add(new ExternalHeadPartInstallPrerequisite(
                code,
                identity,
                expected,
                current,
                enabled,
                nextAction));
        }
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ExternalHeadPartInstallVerificationResult RefusedPromoted(
        ExternalHeadPartPromotedOutputVerificationRequest request,
        string message)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.Add(Error(
            ExternalHeadPartDiagnosticCodes.RecordDrift,
            message));
        return Refused(
            new ExternalHeadPartInstallVerificationRequest(
                request.PackageRoot,
                request.OutputPlugin,
                request.OutputPluginName,
                request.ExpectedOutputPluginSha256,
                request.SelectedManifestPath,
                request.ExpectedSelectedManifestSha256,
                request.Context,
                request.RequireCurrentAuthority,
                request.TargetActorFormId),
            diagnostics,
            []);
    }

    private static ExternalHeadPartInstallVerificationResult Refused(
        ExternalHeadPartInstallVerificationRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<Sha256Hash> descriptorIds)
    {
        ImmutableArray<Sha256Hash> ids = descriptorIds.IsDefaultOrEmpty
            ? [request.ExpectedSelectedManifestSha256]
            : descriptorIds;
        var prerequisites = new ExternalHeadPartInstallPrerequisite(
            ExternalHeadPartDiagnosticCodes.DescriptorLost,
            "selected-dependencies",
            null,
            null,
            null,
            "Restore the canonical schema-3 selected dependency manifest and rerun verification.");
        var artifact = new ExternalHeadPartInstallVerificationArtifact(
            ExternalHeadPartSchemaIdentifiers.InstallVerification,
            PackageIntegrity: false,
            DescriptorClosureValid: false,
            HistoricalSnapshotValid: null,
            ids,
            null,
            ExternalInstallDependencyState.DeclaredUnverified,
            InstallReady: false,
            InstallDependencyAuthority: false,
            RuntimeAuthority: false,
            VisualAuthority: false,
            ProviderObservations: [],
            MissingPrerequisites: [prerequisites]);
        return new ExternalHeadPartInstallVerificationResult(
            false, artifact, diagnostics.ToImmutable());
    }

    private sealed record FreshVerification(
        bool Accepted,
        ExternalHeadPartInstallContextFingerprint? Fingerprint,
        ImmutableArray<ExternalHeadPartInstallProviderObservation>
            ProviderObservations);

    private sealed record AssetReadEvidence(
        Sha256Hash Sha256,
        long ByteLength,
        ArchiveReadEvidence? Archive);

    private sealed record ArchiveReadEvidence(
        string Path,
        Sha256Hash Sha256,
        long ByteLength);
}
