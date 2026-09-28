using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Closed, deterministic JSON codec for external head-part dependency facts.
/// It deliberately does not use the application's global JSON options because
/// these documents are portable hash inputs.
/// </summary>
public static class ExternalHeadPartDependencyDescriptorCodec
{
    private const int MaxCollection = 512;
    private const int MaxPortableToken = 1024;
    private const int MaxPath = 2048;
    private const long MaxByteLength = 1L << 40;
    private const string DescriptorDomain =
        "Actorwright.ExternalHeadPartDescriptor.v1\0";
    private const string AttestationDomain =
        "Actorwright.ExternalHeadPartFaceGeomExclusion.v1\0";
    private const string TriEvidenceDomain =
        "Actorwright.ExternalHeadPartTriEvidence.v1\0";
    private const string InstallContextDomain =
        "Actorwright.ExternalHeadPartInstallContext.v1\0";
    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow
    };
    private static readonly HashSet<string> WindowsReservedDeviceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7",
            "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
            "LPT8", "LPT9",
            "CONIN$", "CONOUT$", "CLOCK$"
        };

    public static byte[] SerializeDescriptor(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        ValidateDescriptor(descriptor, requireDescriptorId: true);
        Require(descriptor.DescriptorId == ComputeDescriptorId(descriptor),
            "DescriptorId does not match the canonical descriptor identity.");
        return Write(writer => WriteDescriptor(writer, descriptor, includeId: true));
    }

    public static ExternalHeadPartDependencyDescriptor ParseDescriptor(
        ReadOnlySpan<byte> utf8Json)
    {
        using var document = ParseDocument(utf8Json, "descriptor");
        var root = document.RootElement;
        RequireObject(root, "descriptor");
        RequireProperties(root,
            ["schemaIdentifier", "descriptorId", "disposition", "rootSourceForm",
             "rootWinningForm", "rootType", "graphSha256", "provider", "members",
             "physics", "assets", "runtimePrerequisites"], ["physicsBinding"]);
        var descriptor = new ExternalHeadPartDependencyDescriptor(
            RequiredString(root, "schemaIdentifier"),
            ParseHash(root, "descriptorId"),
            ParseDisposition(root, "disposition"),
            ParseFormReference(root, "rootSourceForm"),
            ParseFormReference(root, "rootWinningForm"),
            ParseHeadPartType(root, "rootType"),
            ParseHash(root, "graphSha256"),
            ParseProvider(root.GetProperty("provider")),
            ParseMembers(root.GetProperty("members")),
            ParsePhysics(root.GetProperty("physics")),
            ParseAssets(root.GetProperty("assets")),
            ParseRuntimePrerequisites(root.GetProperty("runtimePrerequisites")),
            root.TryGetProperty("physicsBinding", out _) ? ParsePhysicsDisposition(root) : null);
        ValidateDescriptor(descriptor, requireDescriptorId: true);
        Require(descriptor.DescriptorId == ComputeDescriptorId(descriptor),
            "DescriptorId does not match the canonical descriptor identity.");
        Require(SerializeDescriptor(descriptor).AsSpan().SequenceEqual(utf8Json),
            "Descriptor JSON is not in canonical form.");
        return descriptor;
    }

    public static Sha256Hash ComputeDescriptorId(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        // Identity calculation is also used by callers to probe a proposed
        // mutation.  It must include the mutated portable fields even when a
        // deliberately incomplete proposal has not yet re-established its
        // cross-object closure; publication/parse validation remains strict.
        ValidateDescriptor(descriptor, requireDescriptorId: false, enforceBindings: false);
        return HashWithDomain(DescriptorDomain,
            Write(writer => WriteDescriptor(writer, descriptor, includeId: false)));
    }

    public static Sha256Hash ComputeTriEvidenceId(SkyrimRaceMenuFaceBakeTriEvidence evidence)
    {
        ValidateTriEvidence(evidence, validateId: false);
        return HashWithDomain(TriEvidenceDomain,
            Write(writer => WriteTriEvidence(writer, evidence, includeId: false)));
    }

    public static byte[] SerializeAttestation(
        ExternalHeadPartFaceGeomExclusionAttestation attestation)
    {
        ValidateAttestation(attestation, requireAttestationHash: true);
        Require(attestation.AttestationSha256 == ComputeAttestationHash(attestation),
            "AttestationSha256 does not match the canonical attestation identity.");
        return Write(writer => WriteAttestation(writer, attestation, includeHash: true));
    }

    public static ExternalHeadPartFaceGeomExclusionAttestation ParseAttestation(
        ReadOnlySpan<byte> utf8Json)
    {
        using var document = ParseDocument(utf8Json, "attestation");
        var root = document.RootElement;
        RequireObject(root, "attestation");
        RequireProperties(root,
            ["schemaIdentifier", "attestationSha256", "descriptorId", "outputFaceGeomPath",
             "outputFaceGeomSha256", "outputFaceGeomByteLength", "includedOrdinaryShapes",
             "excludedShapes", "excludedMetadata", "verifierVersion"]);
        var attestation = new ExternalHeadPartFaceGeomExclusionAttestation(
            RequiredString(root, "schemaIdentifier"),
            ParseHash(root, "attestationSha256"),
            ParseHash(root, "descriptorId"),
            ParseAssetPath(root, "outputFaceGeomPath"),
            ParseHash(root, "outputFaceGeomSha256"),
            RequiredPositiveLong(root, "outputFaceGeomByteLength"),
            ParseIncludedShapes(root.GetProperty("includedOrdinaryShapes")),
            ParseExcludedShapes(root.GetProperty("excludedShapes")),
            ParseExcludedMetadata(root.GetProperty("excludedMetadata")),
            RequiredPortableString(root, "verifierVersion", "verifierVersion"));
        ValidateAttestation(attestation, requireAttestationHash: true);
        Require(attestation.AttestationSha256 == ComputeAttestationHash(attestation),
            "AttestationSha256 does not match the canonical attestation identity.");
        Require(SerializeAttestation(attestation).AsSpan().SequenceEqual(utf8Json),
            "Attestation JSON is not in canonical form.");
        return attestation;
    }

    public static Sha256Hash ComputeAttestationHash(
        ExternalHeadPartFaceGeomExclusionAttestation attestation)
    {
        ValidateAttestation(attestation, requireAttestationHash: false);
        return HashWithDomain(AttestationDomain,
            Write(writer => WriteAttestation(writer, attestation, includeHash: false)));
    }

    public static byte[] SerializeInstallVerificationArtifact(
        ExternalHeadPartInstallVerificationArtifact artifact)
    {
        ValidateInstallArtifact(artifact);
        return Write(writer => WriteInstallArtifact(writer, artifact));
    }

    public static ExternalHeadPartInstallVerificationArtifact
        ParseInstallVerificationArtifact(ReadOnlySpan<byte> utf8Json)
    {
        using var document = ParseDocument(utf8Json, "install verification artifact");
        var root = document.RootElement;
        RequireObject(root, "install verification artifact");
        RequireProperties(root,
            ["schemaIdentifier", "packageIntegrity", "descriptorClosureValid",
             "descriptorIds", "currentInstallDependencyState", "installReady", "installDependencyAuthority",
             "runtimeAuthority", "visualAuthority", "providerObservations",
             "missingPrerequisites"],
            ["historicalSnapshotValid", "verifiedInstallSnapshot"]);
        var artifact = new ExternalHeadPartInstallVerificationArtifact(
            RequiredString(root, "schemaIdentifier"),
            RequiredBool(root, "packageIntegrity"),
            RequiredBool(root, "descriptorClosureValid"),
            OptionalBool(root, "historicalSnapshotValid"),
            ParseHashArray(root.GetProperty("descriptorIds"), "descriptorIds"),
            ParseOptionalSnapshot(root, "verifiedInstallSnapshot"),
            ParseInstallState(root, "currentInstallDependencyState"),
            RequiredBool(root, "installReady"),
            RequiredBool(root, "installDependencyAuthority"),
            RequiredBool(root, "runtimeAuthority"),
            RequiredBool(root, "visualAuthority"),
            ParseProviderObservations(root.GetProperty("providerObservations")),
            ParsePrerequisites(root.GetProperty("missingPrerequisites")));
        ValidateInstallArtifact(artifact);
        Require(SerializeInstallVerificationArtifact(artifact).AsSpan().SequenceEqual(utf8Json),
            "Install verification JSON is not in canonical form.");
        return artifact;
    }

    private static void WriteDescriptor(Utf8JsonWriter writer,
        ExternalHeadPartDependencyDescriptor descriptor, bool includeId)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaIdentifier", descriptor.SchemaIdentifier);
        if (includeId)
            writer.WriteString("descriptorId", descriptor.DescriptorId.Value);
        writer.WriteString("disposition", DispositionToken(descriptor.Disposition));
        if (descriptor.PhysicsBinding is { } physicsDisposition)
            writer.WriteString("physicsBinding", PhysicsDispositionToken(physicsDisposition));
        writer.WriteString("rootSourceForm", descriptor.RootSourceForm.ToString());
        writer.WriteString("rootWinningForm", descriptor.RootWinningForm.ToString());
        writer.WriteString("rootType", descriptor.RootType.ToWireName());
        writer.WriteString("graphSha256", descriptor.GraphSha256.Value);
        WriteProvider(writer, descriptor.Provider);
        writer.WritePropertyName("members");
        writer.WriteStartArray();
        foreach (var member in descriptor.Members)
            WriteMember(writer, member);
        writer.WriteEndArray();
        WritePhysics(writer, descriptor.Physics);
        writer.WritePropertyName("assets");
        writer.WriteStartArray();
        foreach (var asset in descriptor.Assets.OrderBy(item => item.Path.Value,
                     StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Path.Value,
                     StringComparer.Ordinal))
            WriteAsset(writer, asset);
        writer.WriteEndArray();
        writer.WritePropertyName("runtimePrerequisites");
        writer.WriteStartArray();
        foreach (var item in descriptor.RuntimePrerequisites.OrderBy(item => item.Kind,
                     StringComparer.Ordinal).ThenBy(item => item.Requirement,
                     StringComparer.Ordinal).ThenBy(item => item.EvidenceSource,
                     StringComparer.Ordinal))
            WriteRuntimePrerequisite(writer, item);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteProvider(Utf8JsonWriter writer,
        ExternalHeadPartProviderIdentity provider)
    {
        writer.WritePropertyName("provider");
        writer.WriteStartObject();
        writer.WriteString("plugin", provider.Plugin.Value);
        writer.WriteString("pluginSha256", provider.PluginSha256.Value);
        writer.WriteNumber("pluginByteLength", provider.PluginByteLength);
        writer.WriteString("redistributionMode", RedistributionToken(provider.RedistributionMode));
        writer.WriteEndObject();
    }

    private static void WriteMember(Utf8JsonWriter writer,
        ExternalHeadPartRecordDependency member)
    {
        writer.WriteStartObject();
        writer.WriteString("originForm", member.OriginForm.ToString());
        writer.WriteString("requiredOutputMaster", member.RequiredOutputMaster.Value);
        writer.WriteString("winningForm", member.WinningForm.ToString());
        writer.WriteString("winningPlugin", member.WinningPlugin.Value);
        writer.WriteString("winningPluginSha256", member.WinningPluginSha256.Value);
        writer.WriteNumber("winningPluginByteLength", member.WinningPluginByteLength);
        writer.WriteString("winningRecordSha256", member.WinningRecordSha256.Value);
        writer.WriteString("editorId", member.EditorId);
        writer.WriteString("declaredType", member.DeclaredType.ToWireName());
        writer.WriteString("effectiveType", member.EffectiveType.ToWireName());
        WriteNullableAsset(writer, "modelNif", member.ModelNif);
        writer.WritePropertyName("triRoutes");
        writer.WriteStartArray();
        foreach (var route in member.TriRoutes)
        {
            writer.WriteStartObject();
            writer.WriteString("role", TriRoleToken(route.Role));
            writer.WriteString("path", route.Path.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteFormReferenceArray(writer, "hnamEdges", member.HnamEdges);
        WriteNullableFormReference(writer, "parent", member.Parent);
        writer.WriteNumber("depth", member.Depth);
        writer.WriteNumber("routeOrder", member.RouteOrder);
        WriteNullableSex(writer, "appliesToSex", member.AppliesToSex);
        WriteNullableFormReference(writer, "validRace", member.ValidRace);
        writer.WriteEndObject();
    }

    private static void WritePhysics(Utf8JsonWriter writer,
        ExternalHeadPartPhysicsBinding physics)
    {
        writer.WritePropertyName("physics");
        writer.WriteStartObject();
        writer.WriteString("mode", PhysicsModeToken(physics.Mode));
        writer.WritePropertyName("shapes");
        writer.WriteStartArray();
        foreach (var shape in physics.Shapes.OrderBy(item => item.MemberForm.ToString(),
                     StringComparer.Ordinal).ThenBy(item => item.ModelNif.Value,
                     StringComparer.Ordinal).ThenBy(item => item.ShapeName,
                     StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("memberForm", shape.MemberForm.ToString());
            writer.WriteString("modelNif", shape.ModelNif.Value);
            writer.WriteString("shapeName", shape.ShapeName);
            writer.WriteString("xmlPath", shape.XmlPath.Value);
            writer.WriteString("xmlSha256", shape.XmlSha256.Value);
            writer.WriteNumber("xmlByteLength", shape.XmlByteLength);
            if (shape.Origin is { } origin) writer.WriteString("origin", PhysicsOriginToken(origin));
            if (shape.InheritedFromRoot is { } inherited)
            {
                writer.WritePropertyName("inheritedFromRoot");
                writer.WriteStartObject();
                writer.WriteString("memberForm", inherited.MemberForm.ToString());
                writer.WriteString("modelNif", inherited.ModelNif.Value);
                writer.WriteString("shapeName", inherited.ShapeName);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        if (physics.MappingAuthority is null)
            writer.WriteNull("mappingAuthority");
        else
        {
            writer.WritePropertyName("mappingAuthority");
            writer.WriteStartObject();
            writer.WriteString("path", physics.MappingAuthority.Path.Value);
            writer.WriteString("sha256", physics.MappingAuthority.Sha256.Value);
            writer.WriteNumber("byteLength", physics.MappingAuthority.ByteLength);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteAsset(Utf8JsonWriter writer,
        ExternalHeadPartAssetDependency asset)
    {
        writer.WriteStartObject();
        writer.WriteString("path", asset.Path.Value);
        writer.WriteString("sha256", asset.Sha256.Value);
        writer.WriteNumber("byteLength", asset.ByteLength);
        writer.WriteString("providerPlugin", asset.ProviderPlugin.Value);
        writer.WriteString("providerPluginSha256", asset.ProviderPluginSha256.Value);
        if (asset.ArchiveMember is null)
            writer.WriteNull("archiveMember");
        else
        {
            writer.WritePropertyName("archiveMember");
            writer.WriteStartObject();
            writer.WriteString("archivePath", asset.ArchiveMember.ArchivePath.Value);
            writer.WriteString("archiveSha256", asset.ArchiveMember.ArchiveSha256.Value);
            writer.WriteNumber("archiveByteLength", asset.ArchiveMember.ArchiveByteLength);
            writer.WriteString("memberPath", asset.ArchiveMember.MemberPath.Value);
            writer.WriteString("memberSha256", asset.ArchiveMember.MemberSha256.Value);
            writer.WriteNumber("memberByteLength", asset.ArchiveMember.MemberByteLength);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteRuntimePrerequisite(Utf8JsonWriter writer,
        ExternalHeadPartRuntimePrerequisite item)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", item.Kind);
        writer.WriteString("requirement", item.Requirement);
        writer.WriteString("evidenceSource", item.EvidenceSource);
        writer.WriteEndObject();
    }

    private static void WriteAttestation(Utf8JsonWriter writer,
        ExternalHeadPartFaceGeomExclusionAttestation attestation, bool includeHash)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaIdentifier", attestation.SchemaIdentifier);
        if (includeHash)
            writer.WriteString("attestationSha256", attestation.AttestationSha256.Value);
        writer.WriteString("descriptorId", attestation.DescriptorId.Value);
        writer.WriteString("outputFaceGeomPath", attestation.OutputFaceGeomPath.Value);
        writer.WriteString("outputFaceGeomSha256", attestation.OutputFaceGeomSha256.Value);
        writer.WriteNumber("outputFaceGeomByteLength", attestation.OutputFaceGeomByteLength);
        writer.WritePropertyName("includedOrdinaryShapes");
        writer.WriteStartArray();
        foreach (var shape in attestation.IncludedOrdinaryShapes.OrderBy(item => item.HeadPart.ToString(),
                     StringComparer.Ordinal).ThenBy(item => item.ModelNif.Value,
                     StringComparer.Ordinal).ThenBy(item => item.OutputShapeName,
                     StringComparer.Ordinal))
            WriteIncludedShape(writer, shape);
        writer.WriteEndArray();
        writer.WritePropertyName("excludedShapes");
        writer.WriteStartArray();
        foreach (var shape in attestation.ExcludedShapes.OrderBy(item => item.ProviderModel.Value,
                     StringComparer.Ordinal).ThenBy(item => item.ShapeName, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("providerModel", shape.ProviderModel.Value);
            writer.WriteString("shapeName", shape.ShapeName);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("excludedMetadata");
        writer.WriteStartArray();
        foreach (var item in attestation.ExcludedMetadata.OrderBy(item => item.Kind,
                     StringComparer.Ordinal).ThenBy(item => item.PortableValue,
                     StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", item.Kind);
            writer.WriteString("portableValue", item.PortableValue);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteString("verifierVersion", attestation.VerifierVersion);
        writer.WriteEndObject();
    }

    private static void WriteIncludedShape(Utf8JsonWriter writer,
        SkyrimNativeFaceGeomShapeEvidence shape)
    {
        writer.WriteStartObject();
        writer.WriteString("headPart", shape.HeadPart.ToString());
        writer.WriteString("effectiveType", shape.EffectiveType.ToWireName());
        writer.WriteString("modelNif", shape.ModelNif.Value);
        writer.WriteString("modelSha256", shape.ModelSha256.Value);
        writer.WriteString("modelShapeName", shape.ModelShapeName);
        writer.WriteString("outputShapeName", shape.OutputShapeName);
        writer.WriteNumber("vertexCount", shape.VertexCount);
        writer.WriteString("topologySha256", shape.TopologySha256.Value);
        writer.WriteString("basePositionSha256", shape.BasePositionSha256.Value);
        writer.WriteString("finalPositionSha256", shape.FinalPositionSha256.Value);
        writer.WritePropertyName("triEvidence");
        writer.WriteStartArray();
        foreach (var evidence in shape.TriEvidence)
            WriteTriEvidence(writer, evidence);
        writer.WriteEndArray();
        writer.WriteBoolean("usesFaceTint", shape.UsesFaceTint);
        writer.WriteEndObject();
    }

    private static void WriteTriEvidence(Utf8JsonWriter writer,
        SkyrimRaceMenuFaceBakeTriEvidence evidence, bool includeId = true)
    {
        writer.WriteStartObject();
        writer.WriteString("role", FaceMorphRoleToken(evidence.Role));
        writer.WriteString("sourcePath", evidence.SourcePath.Value);
        writer.WriteString("sourceSha256", evidence.SourceSha256.Value);
        writer.WriteNumber("declaredVertexCount", evidence.DeclaredVertexCount);
        writer.WriteNumber("morphCount", evidence.MorphCount);
        writer.WriteString("disposition", FaceBakeDispositionToken(evidence.Disposition));
        if (includeId && evidence.EvidenceId is Sha256Hash id)
            writer.WriteString("evidenceId", id.Value);
        writer.WriteEndObject();
    }

    private static void WriteInstallArtifact(Utf8JsonWriter writer,
        ExternalHeadPartInstallVerificationArtifact artifact)
    {
        writer.WriteStartObject();
        writer.WriteString("schemaIdentifier", artifact.SchemaIdentifier);
        writer.WriteBoolean("packageIntegrity", artifact.PackageIntegrity);
        writer.WriteBoolean("descriptorClosureValid", artifact.DescriptorClosureValid);
        if (artifact.HistoricalSnapshotValid is bool historical)
            writer.WriteBoolean("historicalSnapshotValid", historical);
        writer.WritePropertyName("descriptorIds");
        writer.WriteStartArray();
        foreach (var id in artifact.DescriptorIds)
            writer.WriteStringValue(id.Value);
        writer.WriteEndArray();
        if (artifact.VerifiedInstallSnapshot is not null)
        {
            writer.WritePropertyName("verifiedInstallSnapshot");
            WriteSnapshot(writer, artifact.VerifiedInstallSnapshot);
        }
        writer.WriteString("currentInstallDependencyState",
            InstallStateToken(artifact.CurrentInstallDependencyState));
        writer.WriteBoolean("installReady", artifact.InstallReady);
        writer.WriteBoolean("installDependencyAuthority", artifact.InstallDependencyAuthority);
        writer.WriteBoolean("runtimeAuthority", artifact.RuntimeAuthority);
        writer.WriteBoolean("visualAuthority", artifact.VisualAuthority);
        writer.WritePropertyName("providerObservations");
        writer.WriteStartArray();
        foreach (var item in artifact.ProviderObservations.OrderBy(item => item.ProviderPlugin.Value,
                     StringComparer.OrdinalIgnoreCase).ThenBy(item => item.ProviderPlugin.Value,
                     StringComparer.Ordinal))
            WriteProviderObservation(writer, item);
        writer.WriteEndArray();
        writer.WritePropertyName("missingPrerequisites");
        writer.WriteStartArray();
        foreach (var item in artifact.MissingPrerequisites.OrderBy(item => item.DiagnosticCode,
                     StringComparer.Ordinal).ThenBy(item => item.PortableIdentity,
                     StringComparer.Ordinal).ThenBy(item => item.NextAction, StringComparer.Ordinal))
            WritePrerequisite(writer, item);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSnapshot(Utf8JsonWriter writer,
        ExternalHeadPartVerifiedInstallSnapshot snapshot)
    {
        writer.WriteStartObject();
        writer.WriteString("selectedManifestSha256", snapshot.SelectedManifestSha256.Value);
        writer.WritePropertyName("descriptorIds");
        writer.WriteStartArray();
        foreach (var id in snapshot.DescriptorIds)
            writer.WriteStringValue(id.Value);
        writer.WriteEndArray();
        writer.WritePropertyName("contextFingerprint");
        writer.WriteStartObject();
        writer.WriteString("sha256", snapshot.ContextFingerprint.Sha256.Value);
        writer.WritePropertyName("observations");
        writer.WriteStartArray();
        foreach (var item in snapshot.ContextFingerprint.Observations)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", item.Kind);
            writer.WriteString("portableIdentity", item.PortableIdentity);
            writer.WriteString("sha256", item.Sha256.Value);
            writer.WriteNumber("byteLength", item.ByteLength);
            writer.WriteNumber("order", item.Order);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteProviderObservation(Utf8JsonWriter writer,
        ExternalHeadPartInstallProviderObservation item)
    {
        writer.WriteStartObject();
        writer.WriteString("providerPlugin", item.ProviderPlugin.Value);
        writer.WriteString("expectedSha256", item.ExpectedSha256.Value);
        if (item.CurrentSha256 is Sha256Hash current)
            writer.WriteString("currentSha256", current.Value);
        if (item.Enabled is bool enabled)
            writer.WriteBoolean("enabled", enabled);
        writer.WriteEndObject();
    }

    private static void WritePrerequisite(Utf8JsonWriter writer,
        ExternalHeadPartInstallPrerequisite item)
    {
        writer.WriteStartObject();
        writer.WriteString("diagnosticCode", item.DiagnosticCode);
        writer.WriteString("portableIdentity", item.PortableIdentity);
        if (item.ExpectedSha256 is Sha256Hash expected)
            writer.WriteString("expectedSha256", expected.Value);
        if (item.CurrentSha256 is Sha256Hash current)
            writer.WriteString("currentSha256", current.Value);
        if (item.Enabled is bool enabled)
            writer.WriteBoolean("enabled", enabled);
        writer.WriteString("nextAction", item.NextAction);
        writer.WriteEndObject();
    }

    private static ExternalHeadPartProviderIdentity ParseProvider(JsonElement element)
    {
        RequireObject(element, "provider");
        RequireProperties(element, ["plugin", "pluginSha256", "pluginByteLength", "redistributionMode"]);
        return new(ParsePlugin(element, "plugin"), ParseHash(element, "pluginSha256"),
            RequiredPositiveLong(element, "pluginByteLength"), ParseRedistribution(element, "redistributionMode"));
    }

    private static ImmutableArray<ExternalHeadPartRecordDependency> ParseMembers(JsonElement element)
    {
        RequireArray(element, "members");
        RequireCount(element, "members", 1, MaxCollection);
        var values = ImmutableArray.CreateBuilder<ExternalHeadPartRecordDependency>();
        foreach (var item in element.EnumerateArray())
        {
            RequireObject(item, "member");
            RequireProperties(item,
                ["originForm", "requiredOutputMaster", "winningForm", "winningPlugin",
                 "winningPluginSha256", "winningPluginByteLength", "winningRecordSha256",
                 "editorId", "declaredType", "effectiveType", "modelNif", "triRoutes",
                 "hnamEdges", "parent", "depth", "routeOrder", "appliesToSex", "validRace"],
                ["modelNif", "parent", "appliesToSex", "validRace"]);
            var routesElement = item.GetProperty("triRoutes");
            RequireArray(routesElement, "triRoutes");
            RequireCount(routesElement, "triRoutes", 0, MaxCollection);
            var routes = ImmutableArray.CreateBuilder<SkyrimHdptTriRoute>();
            foreach (var route in routesElement.EnumerateArray())
            {
                RequireObject(route, "triRoute");
                RequireProperties(route, ["role", "path"]);
                routes.Add(new(ParseTriRole(route, "role"), ParseAssetPath(route, "path")));
            }
            values.Add(new(
                ParseFormReference(item, "originForm"), ParsePlugin(item, "requiredOutputMaster"),
                ParseFormReference(item, "winningForm"), ParsePlugin(item, "winningPlugin"),
                ParseHash(item, "winningPluginSha256"), RequiredPositiveLong(item, "winningPluginByteLength"),
                ParseHash(item, "winningRecordSha256"), RequiredPortableString(item, "editorId", "editorId"),
                ParseHeadPartType(item, "declaredType"), ParseHeadPartType(item, "effectiveType"),
                ParseOptionalAsset(item, "modelNif"), routes.ToImmutable(),
                ParseFormReferenceArray(item.GetProperty("hnamEdges"), "hnamEdges"),
                ParseOptionalFormReference(item, "parent"), RequiredInt(item, "depth"),
                RequiredInt(item, "routeOrder"), ParseOptionalSex(item, "appliesToSex"),
                ParseOptionalFormReference(item, "validRace")));
        }
        return values.ToImmutable();
    }

    private static ExternalHeadPartPhysicsBinding ParsePhysics(JsonElement element)
    {
        RequireObject(element, "physics");
        RequireProperties(element, ["mode", "shapes", "mappingAuthority"], ["mappingAuthority"]);
        var shapeElement = element.GetProperty("shapes");
        RequireArray(shapeElement, "physics.shapes");
        RequireCount(shapeElement, "physics.shapes", 0, MaxCollection);
        var shapes = ImmutableArray.CreateBuilder<ExternalHeadPartPhysicsShapeBinding>();
        foreach (var shape in shapeElement.EnumerateArray())
        {
            RequireObject(shape, "physics.shape");
            RequireProperties(shape, ["memberForm", "modelNif", "shapeName", "xmlPath", "xmlSha256", "xmlByteLength"],
                ["origin", "inheritedFromRoot"]);
            ExternalHeadPartPhysicsBindingRoot? inherited = null;
            if (shape.TryGetProperty("inheritedFromRoot", out var root))
            {
                RequireObject(root, "physics.inheritedFromRoot");
                RequireProperties(root, ["memberForm", "modelNif", "shapeName"]);
                inherited = new(ParseFormReference(root, "memberForm"), ParseAssetPath(root, "modelNif"),
                    RequiredPortableString(root, "shapeName", "physics.inheritedFromRoot.shapeName"));
            }
            shapes.Add(new(ParseFormReference(shape, "memberForm"), ParseAssetPath(shape, "modelNif"),
                RequiredPortableString(shape, "shapeName", "physics.shapeName"), ParseAssetPath(shape, "xmlPath"),
                ParseHash(shape, "xmlSha256"), RequiredPositiveLong(shape, "xmlByteLength"),
                shape.TryGetProperty("origin", out _) ? ParsePhysicsOrigin(shape) : null, inherited));
        }
        ExternalHeadPartPhysicsMappingAuthority? mapping = null;
        var mappingElement = element.GetProperty("mappingAuthority");
        if (mappingElement.ValueKind != JsonValueKind.Null)
        {
            RequireObject(mappingElement, "mappingAuthority");
            RequireProperties(mappingElement, ["path", "sha256", "byteLength"]);
            mapping = new(ParseAssetPath(mappingElement, "path"), ParseHash(mappingElement, "sha256"),
                RequiredPositiveLong(mappingElement, "byteLength"));
        }
        return new(ParsePhysicsMode(element, "mode"), shapes.ToImmutable(), mapping);
    }

    private static ImmutableArray<ExternalHeadPartAssetDependency> ParseAssets(JsonElement element)
    {
        RequireArray(element, "assets");
        RequireCount(element, "assets", 0, MaxCollection);
        var values = ImmutableArray.CreateBuilder<ExternalHeadPartAssetDependency>();
        foreach (var asset in element.EnumerateArray())
        {
            RequireObject(asset, "asset");
            RequireProperties(asset,
                ["path", "sha256", "byteLength", "providerPlugin", "providerPluginSha256", "archiveMember"],
                ["archiveMember"]);
            ExternalHeadPartArchiveMemberAuthority? archiveMember = null;
            var archive = asset.GetProperty("archiveMember");
            if (archive.ValueKind != JsonValueKind.Null)
            {
                RequireObject(archive, "archiveMember");
                RequireProperties(archive,
                    ["archivePath", "archiveSha256", "archiveByteLength", "memberPath", "memberSha256", "memberByteLength"]);
                archiveMember = new(ParseAssetPath(archive, "archivePath"), ParseHash(archive, "archiveSha256"),
                    RequiredPositiveLong(archive, "archiveByteLength"), ParseAssetPath(archive, "memberPath"),
                    ParseHash(archive, "memberSha256"), RequiredPositiveLong(archive, "memberByteLength"));
            }
            values.Add(new(ParseAssetPath(asset, "path"), ParseHash(asset, "sha256"),
                RequiredPositiveLong(asset, "byteLength"), ParsePlugin(asset, "providerPlugin"),
                ParseHash(asset, "providerPluginSha256"), archiveMember));
        }
        return values.ToImmutable();
    }

    private static ImmutableArray<ExternalHeadPartRuntimePrerequisite> ParseRuntimePrerequisites(JsonElement element)
    {
        RequireArray(element, "runtimePrerequisites");
        RequireCount(element, "runtimePrerequisites", 0, MaxCollection);
        var values = ImmutableArray.CreateBuilder<ExternalHeadPartRuntimePrerequisite>();
        foreach (var item in element.EnumerateArray())
        {
            RequireObject(item, "runtimePrerequisite");
            RequireProperties(item, ["kind", "requirement", "evidenceSource"]);
            values.Add(new(RequiredPortableString(item, "kind", "runtime.kind"),
                RequiredPortableString(item, "requirement", "runtime.requirement"),
                RequiredPortableString(item, "evidenceSource", "runtime.evidenceSource")));
        }
        return values.ToImmutable();
    }

    private static ImmutableArray<SkyrimNativeFaceGeomShapeEvidence> ParseIncludedShapes(JsonElement element)
    {
        RequireArray(element, "includedOrdinaryShapes");
        RequireCount(element, "includedOrdinaryShapes", 0, MaxCollection);
        var values = ImmutableArray.CreateBuilder<SkyrimNativeFaceGeomShapeEvidence>();
        foreach (var shape in element.EnumerateArray())
        {
            RequireObject(shape, "includedShape");
            RequireProperties(shape,
                ["headPart", "effectiveType", "modelNif", "modelSha256", "modelShapeName",
                 "outputShapeName", "vertexCount", "topologySha256", "basePositionSha256",
                 "finalPositionSha256", "triEvidence", "usesFaceTint"]);
            var triElement = shape.GetProperty("triEvidence");
            RequireArray(triElement, "triEvidence");
            RequireCount(triElement, "triEvidence", 0, MaxCollection);
            var tri = ImmutableArray.CreateBuilder<SkyrimRaceMenuFaceBakeTriEvidence>();
            foreach (var evidence in triElement.EnumerateArray())
            {
                RequireObject(evidence, "triEvidence");
                RequireProperties(evidence,
                    ["role", "sourcePath", "sourceSha256", "declaredVertexCount", "morphCount", "disposition"], ["evidenceId"]);
                tri.Add(new(ParseFaceMorphRole(evidence, "role"), ParseAssetPath(evidence, "sourcePath"),
                    ParseHash(evidence, "sourceSha256"), RequiredNonNegativeInt(evidence, "declaredVertexCount"),
                    RequiredNonNegativeInt(evidence, "morphCount"), ParseFaceBakeDisposition(evidence, "disposition"),
                    evidence.TryGetProperty("evidenceId", out _) ? ParseHash(evidence, "evidenceId") : null));
            }
            values.Add(new(ParseFormReference(shape, "headPart"), ParseHeadPartType(shape, "effectiveType"),
                ParseAssetPath(shape, "modelNif"), ParseHash(shape, "modelSha256"),
                RequiredPortableString(shape, "modelShapeName", "modelShapeName"),
                RequiredPortableString(shape, "outputShapeName", "outputShapeName"),
                RequiredPositiveInt(shape, "vertexCount"), ParseHash(shape, "topologySha256"),
                ParseHash(shape, "basePositionSha256"), ParseHash(shape, "finalPositionSha256"),
                tri.ToImmutable(), RequiredBool(shape, "usesFaceTint")));
        }
        return values.ToImmutable();
    }

    private static ImmutableArray<ExternalHeadPartExcludedShapeEvidence> ParseExcludedShapes(JsonElement element)
    {
        RequireArray(element, "excludedShapes");
        RequireCount(element, "excludedShapes", 0, MaxCollection);
        var values = ImmutableArray.CreateBuilder<ExternalHeadPartExcludedShapeEvidence>();
        foreach (var item in element.EnumerateArray())
        {
            RequireObject(item, "excludedShape");
            RequireProperties(item, ["providerModel", "shapeName"]);
            values.Add(new(ParseAssetPath(item, "providerModel"),
                RequiredPortableString(item, "shapeName", "excluded.shapeName")));
        }
        return values.ToImmutable();
    }

    private static ImmutableArray<ExternalHeadPartExcludedMetadataEvidence> ParseExcludedMetadata(JsonElement element)
    {
        RequireArray(element, "excludedMetadata");
        RequireCount(element, "excludedMetadata", 0, MaxCollection);
        var values = ImmutableArray.CreateBuilder<ExternalHeadPartExcludedMetadataEvidence>();
        foreach (var item in element.EnumerateArray())
        {
            RequireObject(item, "excludedMetadata");
            RequireProperties(item, ["kind", "portableValue"]);
            values.Add(new(RequiredPortableString(item, "kind", "excludedMetadata.kind"),
                RequiredPortableString(item, "portableValue", "excludedMetadata.value")));
        }
        return values.ToImmutable();
    }

    private static ExternalHeadPartVerifiedInstallSnapshot? ParseOptionalSnapshot(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element))
            return null;
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        RequireObject(element, property);
        RequireProperties(element, ["selectedManifestSha256", "descriptorIds", "contextFingerprint"]);
        var fingerprint = element.GetProperty("contextFingerprint");
        RequireObject(fingerprint, "contextFingerprint");
        RequireProperties(fingerprint, ["sha256", "observations"]);
        var observations = fingerprint.GetProperty("observations");
        RequireArray(observations, "contextFingerprint.observations");
        RequireCount(observations, "contextFingerprint.observations", 0, MaxCollection);
        var observationValues = ImmutableArray.CreateBuilder<ExternalHeadPartInstallObservation>();
        foreach (var item in observations.EnumerateArray())
        {
            RequireObject(item, "installObservation");
            RequireProperties(item, ["kind", "portableIdentity", "sha256", "byteLength", "order"]);
            observationValues.Add(new(RequiredPortableString(item, "kind", "observation.kind"),
                RequiredPortableString(item, "portableIdentity", "observation.identity"), ParseHash(item, "sha256"),
                RequiredPositiveLong(item, "byteLength"), RequiredInt(item, "order")));
        }
        return new(ParseHash(element, "selectedManifestSha256"),
            ParseHashArray(element.GetProperty("descriptorIds"), "snapshot.descriptorIds"),
            new(ParseHash(fingerprint, "sha256"), observationValues.ToImmutable()));
    }

    private static ImmutableArray<ExternalHeadPartInstallProviderObservation> ParseProviderObservations(JsonElement element)
    {
        RequireArray(element, "providerObservations");
        RequireCount(element, "providerObservations", 0, MaxCollection);
        var values = ImmutableArray.CreateBuilder<ExternalHeadPartInstallProviderObservation>();
        foreach (var item in element.EnumerateArray())
        {
            RequireObject(item, "providerObservation");
            RequireProperties(item, ["providerPlugin", "expectedSha256"],
                ["currentSha256", "enabled"]);
            values.Add(new(ParsePlugin(item, "providerPlugin"), ParseHash(item, "expectedSha256"),
                ParseOptionalHash(item, "currentSha256"), OptionalBool(item, "enabled")));
        }
        return values.ToImmutable();
    }

    private static ImmutableArray<ExternalHeadPartInstallPrerequisite> ParsePrerequisites(JsonElement element)
    {
        RequireArray(element, "missingPrerequisites");
        RequireCount(element, "missingPrerequisites", 0, MaxCollection);
        var values = ImmutableArray.CreateBuilder<ExternalHeadPartInstallPrerequisite>();
        foreach (var item in element.EnumerateArray())
        {
            RequireObject(item, "prerequisite");
            RequireProperties(item,
                ["diagnosticCode", "portableIdentity", "nextAction"],
                ["expectedSha256", "currentSha256", "enabled"]);
            values.Add(new(RequiredPortableString(item, "diagnosticCode", "prerequisite.code"),
                RequiredPortableString(item, "portableIdentity", "prerequisite.identity"),
                ParseOptionalHash(item, "expectedSha256"), ParseOptionalHash(item, "currentSha256"),
                OptionalBool(item, "enabled"), RequiredPortableString(item, "nextAction", "prerequisite.nextAction")));
        }
        return values.ToImmutable();
    }

    private static ImmutableArray<Sha256Hash> ParseHashArray(JsonElement element, string role)
    {
        RequireArray(element, role);
        RequireCount(element, role, 1, MaxCollection);
        var values = ImmutableArray.CreateBuilder<Sha256Hash>();
        foreach (var item in element.EnumerateArray())
        {
            Require(item.ValueKind == JsonValueKind.String, role + " must contain hash strings.");
            values.Add(ParseHash(item.GetString() ?? string.Empty, role));
        }
        return values.ToImmutable();
    }

    private static ImmutableArray<FormReference> ParseFormReferenceArray(JsonElement element, string role)
    {
        RequireArray(element, role);
        RequireCount(element, role, 0, MaxCollection);
        var values = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var item in element.EnumerateArray())
        {
            Require(item.ValueKind == JsonValueKind.String, role + " must contain form-reference strings.");
            values.Add(ParseFormReference(item.GetString() ?? string.Empty, role));
        }
        return values.ToImmutable();
    }

    private static void ValidateDescriptor(ExternalHeadPartDependencyDescriptor descriptor,
        bool requireDescriptorId, bool enforceBindings = true)
    {
        Require(descriptor.SchemaIdentifier == ExternalHeadPartSchemaIdentifiers.Descriptor,
            "Unsupported external headpart descriptor schema.");
        if (requireDescriptorId)
            ValidateHash(descriptor.DescriptorId, "descriptorId");
        ValidateHash(descriptor.GraphSha256, "graphSha256");
        Require(descriptor.Disposition == ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
            "Unsupported descriptor disposition.");
        if (descriptor.PhysicsBinding is { } physicsDisposition)
        {
            _ = PhysicsDispositionToken(physicsDisposition);
            Require(descriptor.Physics.MappingAuthority is null,
                "Reviewed inherit-root cannot use default-BBP mapping authority.");
        }
        ValidateFormReference(descriptor.RootSourceForm, "rootSourceForm");
        ValidateFormReference(descriptor.RootWinningForm, "rootWinningForm");
        ValidateHeadPartType(descriptor.RootType, "rootType");
        ValidateProvider(descriptor.Provider);
        Require(descriptor.Members.Length is >= 1 and <= MaxCollection,
            "Descriptor member count is outside the portable bound.");
        var origins = new HashSet<FormReference>();
        var routeOrders = new HashSet<int>();
        for (var index = 0; index < descriptor.Members.Length; index++)
        {
            var member = descriptor.Members[index];
            ValidateMember(member, descriptor.Provider, origins, routeOrders, enforceBindings);
            if (index > 0 && member.RouteOrder <= descriptor.Members[index - 1].RouteOrder)
                throw Invalid("Descriptor members must be in ascending route order.");
        }
        if (enforceBindings)
        {
            Require(descriptor.Members[0].OriginForm == descriptor.RootSourceForm,
                "The first descriptor member must be the root source form.");
            Require(descriptor.Members[0].WinningForm == descriptor.RootWinningForm,
                "The first descriptor member must be the root winning form.");
            Require(ExternalHeadPartMemberAuthority.Classify(
                    descriptor.Members[0], descriptor.Provider) ==
                ExternalHeadPartMemberAuthorityKind.PrimaryProvider,
                "The descriptor root must remain owned and won by the primary provider.");
        }
        ValidateHnamGraph(descriptor, enforceBindings);
        ValidatePhysics(descriptor.Physics, descriptor.Members, enforceBindings);
        Require(descriptor.Assets.Length <= MaxCollection,
            "Descriptor asset count is outside the portable bound.");
        var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in descriptor.Assets)
            ValidateAsset(
                asset, descriptor.Provider, descriptor.Members, assetPaths,
                enforceBindings);
        Require(descriptor.RuntimePrerequisites.Length <= MaxCollection,
            "Runtime prerequisite count is outside the portable bound.");
        var runtimeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in descriptor.RuntimePrerequisites)
        {
            ValidatePortable(item.Kind, "runtime.kind");
            ValidatePortable(item.Requirement, "runtime.requirement");
            ValidatePortable(item.EvidenceSource, "runtime.evidenceSource");
            Require(runtimeKeys.Add(item.Kind + "\0" + item.Requirement + "\0" + item.EvidenceSource),
                "Duplicate runtime prerequisite.");
        }
    }

    private static void ValidateProvider(ExternalHeadPartProviderIdentity provider)
    {
        ValidatePlugin(provider.Plugin, "provider.plugin");
        ValidateHash(provider.PluginSha256, "provider.pluginSha256");
        ValidateLength(provider.PluginByteLength, "provider.pluginByteLength");
        Require(provider.RedistributionMode == ExternalHeadPartRedistributionMode.ExternalProviderRequired,
            "Unsupported redistribution mode.");
    }

    private static void ValidateMember(ExternalHeadPartRecordDependency member,
        ExternalHeadPartProviderIdentity provider, HashSet<FormReference> origins,
        HashSet<int> routeOrders, bool enforceBindings)
    {
        ValidateFormReference(member.OriginForm, "member.originForm");
        Require(origins.Add(member.OriginForm), "Duplicate descriptor member origin form.");
        ValidatePlugin(member.RequiredOutputMaster, "member.requiredOutputMaster");
        ValidateFormReference(member.WinningForm, "member.winningForm");
        ValidatePlugin(member.WinningPlugin, "member.winningPlugin");
        ExternalHeadPartMemberAuthorityKind authority = enforceBindings
            ? ExternalHeadPartMemberAuthority.Classify(member, provider)
            : ExternalHeadPartMemberAuthorityKind.PrimaryProvider;
        if (enforceBindings)
            Require(authority != ExternalHeadPartMemberAuthorityKind.Unsupported,
                "External descriptor member has an unsupported provider authority.");
        ValidateHash(member.WinningPluginSha256, "member.winningPluginSha256");
        if (enforceBindings &&
            authority == ExternalHeadPartMemberAuthorityKind.PrimaryProvider)
            Require(member.WinningPluginSha256 == provider.PluginSha256,
                "Member provider hash differs from the descriptor provider hash.");
        ValidateLength(member.WinningPluginByteLength, "member.winningPluginByteLength");
        if (enforceBindings &&
            authority == ExternalHeadPartMemberAuthorityKind.PrimaryProvider)
            Require(member.WinningPluginByteLength == provider.PluginByteLength,
                "Member provider length differs from the descriptor provider length.");
        ValidateHash(member.WinningRecordSha256, "member.winningRecordSha256");
        ValidatePortable(member.EditorId, "member.editorId");
        ValidateHeadPartType(member.DeclaredType, "member.declaredType");
        ValidateHeadPartType(member.EffectiveType, "member.effectiveType");
        if (member.ModelNif is AssetPath model)
            ValidateAssetPath(model, "member.modelNif");
        Require(member.TriRoutes.Length <= MaxCollection,
            "Member TRI route count is outside the portable bound.");
        var triRoles = new HashSet<SkyrimHdptTriRole>();
        foreach (var route in member.TriRoutes)
        {
            ValidateTriRole(route.Role, "member.triRoute.role");
            Require(triRoles.Add(route.Role), "Duplicate member TRI route role.");
            ValidateAssetPath(route.Path, "member.triRoute.path");
        }
        Require(member.HnamEdges.Length <= MaxCollection,
            "Member HNAM edge count is outside the portable bound.");
        var edges = new HashSet<FormReference>();
        foreach (var edge in member.HnamEdges)
        {
            ValidateFormReference(edge, "member.hnamEdge");
            Require(edges.Add(edge), "Duplicate member HNAM edge.");
        }
        if (member.Parent is FormReference parent)
            ValidateFormReference(parent, "member.parent");
        Require(member.Depth is >= 0 and <= MaxCollection,
            "Member depth is outside the portable bound.");
        Require(member.RouteOrder is >= 0 and <= MaxCollection,
            "Member route order is outside the portable bound.");
        Require(routeOrders.Add(member.RouteOrder), "Duplicate member route order.");
        if (member.AppliesToSex is NpcSex sex)
            ValidateSex(sex, "member.appliesToSex");
        if (member.ValidRace is FormReference race)
            ValidateFormReference(race, "member.validRace");
    }

    private static void ValidateHnamGraph(
        ExternalHeadPartDependencyDescriptor descriptor, bool enforceBindings)
    {
        var members = descriptor.Members;
        var byOrigin = members.ToDictionary(item => item.OriginForm);
        foreach (var member in members)
        {
            if (member.Parent is FormReference parent)
            {
                Require(byOrigin.ContainsKey(parent), "Member parent is outside the descriptor graph.");
                Require(byOrigin[parent].HnamEdges.Contains(member.OriginForm),
                    "Member parent and HNAM edge are not reciprocal.");
            }
            foreach (var edge in member.HnamEdges)
            {
                if (!byOrigin.TryGetValue(edge, out var child))
                    throw Invalid("HNAM edge is outside the descriptor graph.");
                Require(child.Parent == member.OriginForm,
                    "HNAM edge and child parent disagree.");
            }
        }
        foreach (var start in members)
        {
            var seen = new HashSet<FormReference>();
            var current = start;
            while (current.Parent is FormReference parent)
            {
                Require(seen.Add(current.OriginForm), "HNAM graph contains a cycle.");
                current = byOrigin[parent];
            }
        }
        if (!enforceBindings)
            return;

        var root = descriptor.RootSourceForm;
        Require(members[0].OriginForm == root && members[0].Parent is null &&
                members[0].Depth == 0,
            "The descriptor graph root must be the first member at depth zero.");
        var reachable = new HashSet<FormReference>();
        var pending = new Stack<FormReference>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!reachable.Add(current))
                continue;
            foreach (var child in byOrigin[current].HnamEdges.Reverse())
                pending.Push(child);
        }
        Require(reachable.Count == members.Length,
            "Descriptor graph contains a disconnected member.");
        foreach (var member in members)
        {
            if (member.Parent is FormReference parent)
                Require(member.Depth == byOrigin[parent].Depth + 1,
                    "Descriptor member depth does not follow its HNAM parent.");
            else
                Require(member.OriginForm == root,
                    "Only the descriptor graph root may omit a parent.");
        }
    }

    private static void ValidatePhysics(ExternalHeadPartPhysicsBinding physics,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        bool enforceBindings)
    {
        ValidatePhysicsMode(physics.Mode, "physics.mode");
        Require(physics.Shapes.Length <= MaxCollection,
            "Physics shape count is outside the portable bound.");
        var memberByForm = members.ToDictionary(item => item.OriginForm);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shape in physics.Shapes)
        {
            ValidateFormReference(shape.MemberForm, "physics.shape.memberForm");
            if (!memberByForm.TryGetValue(shape.MemberForm, out var member))
                throw Invalid("Physics shape member is outside the descriptor graph.");
            ValidateAssetPath(shape.ModelNif, "physics.shape.modelNif");
            if (enforceBindings)
            {
                if (member.ModelNif is not AssetPath memberModel)
                    throw Invalid("Physics shape member has no model binding.");
                Require(shape.ModelNif == memberModel,
                    "Physics shape model does not match its member model.");
            }
            ValidatePortable(shape.ShapeName, "physics.shape.shapeName");
            ValidateAssetPath(shape.XmlPath, "physics.shape.xmlPath");
            ValidateHash(shape.XmlSha256, "physics.shape.xmlSha256");
            ValidateLength(shape.XmlByteLength, "physics.shape.xmlByteLength");
            Require(keys.Add(shape.MemberForm + "\0" + shape.ModelNif.Value + "\0" + shape.ShapeName),
                "Duplicate physics member/model/shape binding.");
            if (shape.Origin is { } origin) _ = PhysicsOriginToken(origin);
            Require((shape.Origin == ExternalHeadPartPhysicsBindingOrigin.InheritedFromRoot) ==
                (shape.InheritedFromRoot is not null), "Inherited physics origin requires exact root provenance.");
            if (shape.Origin == ExternalHeadPartPhysicsBindingOrigin.DefaultBbp)
                Require(physics.MappingAuthority is not null, "Default-BBP origin requires mapping authority.");
            if (shape.InheritedFromRoot is { } inherited)
            {
                var root = physics.Shapes.FirstOrDefault(candidate => candidate.MemberForm == inherited.MemberForm &&
                    candidate.ModelNif == inherited.ModelNif && candidate.ShapeName == inherited.ShapeName);
                Require(root is not null && root.Origin == ExternalHeadPartPhysicsBindingOrigin.Direct &&
                    root.MemberForm != shape.MemberForm && root.XmlPath == shape.XmlPath &&
                    root.XmlSha256 == shape.XmlSha256 && root.XmlByteLength == shape.XmlByteLength,
                    "Inherited physics does not match an exact direct root shape and XML authority.");
                Require(ExternalHeadPartMemberAuthority.TryGetChainRoot(shape.MemberForm, memberByForm, out var chain) &&
                    ExternalHeadPartMemberAuthority.TryGetChainRoot(inherited.MemberForm, memberByForm, out var rootChain) &&
                    chain == rootChain, "Inherited physics root is outside the admitted HDPT chain.");
            }
        }
        if (physics.MappingAuthority is ExternalHeadPartPhysicsMappingAuthority mapping)
        {
            ValidateAssetPath(mapping.Path, "physics.mapping.path");
            ValidateHash(mapping.Sha256, "physics.mapping.sha256");
            ValidateLength(mapping.ByteLength, "physics.mapping.byteLength");
        }
        if (physics.Mode == ExternalHeadPartPhysicsBindingMode.DirectNifExtraData)
            Require(physics.MappingAuthority is null,
                "Direct NIF physics cannot carry default-BBP mapping authority.");
        else if (physics.Mode == ExternalHeadPartPhysicsBindingMode.DefaultBbpMap)
            Require(physics.MappingAuthority is not null,
                "Default-BBP physics requires mapping authority.");
        Require(physics.Shapes.Length > 0,
            "Physics binding must contain at least one shape binding.");
    }

    private static void ValidateAsset(ExternalHeadPartAssetDependency asset,
        ExternalHeadPartProviderIdentity provider,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        HashSet<string> paths, bool enforceBindings)
    {
        ValidateAssetPath(asset.Path, "asset.path");
        Require(paths.Add(asset.Path.Value), "Duplicate or case-colliding asset path.");
        ValidateHash(asset.Sha256, "asset.sha256");
        ValidateLength(asset.ByteLength, "asset.byteLength");
        ValidatePlugin(asset.ProviderPlugin, "asset.providerPlugin");
        bool primaryAsset = asset.ProviderPlugin == provider.Plugin;
        if (enforceBindings && !primaryAsset)
            Require(ExternalHeadPartMemberAuthority.IsOfficialVanillaMaster(
                    asset.ProviderPlugin) &&
                FindVanillaAssetOwners(asset, members).Length > 0,
                "Non-primary asset provider is not a declared official vanilla authority.");
        ValidateHash(asset.ProviderPluginSha256, "asset.providerPluginSha256");
        if (enforceBindings && primaryAsset)
            Require(asset.ProviderPluginSha256 == provider.PluginSha256,
                "Asset provider hash differs from descriptor provider.");
        if (enforceBindings && !primaryAsset)
        {
            ExternalHeadPartRecordDependency[] owners =
                FindVanillaAssetOwners(asset, members);
            Require(owners.All(owner =>
                    owner.OriginForm.Plugin == asset.ProviderPlugin &&
                    owner.WinningPluginSha256 == asset.ProviderPluginSha256),
                "Vanilla asset provider does not match every declared owner.");
            Require(owners.Select(owner =>
                    $"{owner.OriginForm.Plugin.Value}\0{owner.WinningPluginSha256.Value}\0{owner.WinningPluginByteLength}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() == 1,
                "Vanilla asset has conflicting self-owned official authorities.");
        }
        if (asset.ArchiveMember is ExternalHeadPartArchiveMemberAuthority archive)
        {
            ValidateAssetPath(archive.ArchivePath, "asset.archivePath");
            ValidateHash(archive.ArchiveSha256, "asset.archiveSha256");
            ValidateLength(archive.ArchiveByteLength, "asset.archiveByteLength");
            ValidateAssetPath(archive.MemberPath, "asset.memberPath");
            ValidateHash(archive.MemberSha256, "asset.memberSha256");
            ValidateLength(archive.MemberByteLength, "asset.memberByteLength");
            Require(archive.MemberPath == asset.Path,
                "Archive member path must equal its asset path.");
            if (enforceBindings)
                Require(archive.MemberSha256 == asset.Sha256 && archive.MemberByteLength == asset.ByteLength,
                    "Archive member authority disagrees with asset authority.");
        }
    }

    private static ExternalHeadPartRecordDependency[] FindVanillaAssetOwners(
        ExternalHeadPartAssetDependency asset,
        ImmutableArray<ExternalHeadPartRecordDependency> members) =>
        members.Where(member =>
            ExternalHeadPartMemberAuthority.IsSelfOwnedVanillaMember(member) &&
            (member.ModelNif == asset.Path || member.TriRoutes.Any(route =>
                route.Path == asset.Path)))
            .ToArray() is { Length: > 0 } declared
            ? declared
            : members.Where(member =>
                ExternalHeadPartMemberAuthority.IsSelfOwnedVanillaMember(member) &&
            member.OriginForm.Plugin == asset.ProviderPlugin &&
            member.WinningPluginSha256 == asset.ProviderPluginSha256)
            .ToArray();

    private static void ValidateAttestation(
        ExternalHeadPartFaceGeomExclusionAttestation attestation, bool requireAttestationHash)
    {
        Require(attestation.SchemaIdentifier == ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
            "Unsupported FaceGeom exclusion attestation schema.");
        if (requireAttestationHash)
            ValidateHash(attestation.AttestationSha256, "attestationSha256");
        ValidateHash(attestation.DescriptorId, "attestation.descriptorId");
        ValidateAssetPath(attestation.OutputFaceGeomPath, "outputFaceGeomPath");
        ValidateHash(attestation.OutputFaceGeomSha256, "outputFaceGeomSha256");
        ValidateLength(attestation.OutputFaceGeomByteLength, "outputFaceGeomByteLength");
        Require(attestation.IncludedOrdinaryShapes.Length <= MaxCollection,
            "Included FaceGeom shape count is outside the portable bound.");
        var shapeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shape in attestation.IncludedOrdinaryShapes)
        {
            ValidateFormReference(shape.HeadPart, "included.headPart");
            ValidateHeadPartType(shape.EffectiveType, "included.effectiveType");
            ValidateAssetPath(shape.ModelNif, "included.modelNif");
            ValidateHash(shape.ModelSha256, "included.modelSha256");
            ValidatePortable(shape.ModelShapeName, "included.modelShapeName");
            ValidatePortable(shape.OutputShapeName, "included.outputShapeName");
            Require(shape.VertexCount is > 0 and <= 1_000_000,
                "Included FaceGeom vertex count is outside the portable bound.");
            ValidateHash(shape.TopologySha256, "included.topologySha256");
            ValidateHash(shape.BasePositionSha256, "included.basePositionSha256");
            ValidateHash(shape.FinalPositionSha256, "included.finalPositionSha256");
            Require(shape.TriEvidence.Length <= MaxCollection,
                "Included TRI evidence count is outside the portable bound.");
            var triKeys = new HashSet<(SkyrimFaceMorphTriRole Role, AssetPath SourcePath)>();
            foreach (var evidence in shape.TriEvidence)
            {
                ValidateTriEvidence(evidence);
                Require(triKeys.Add((evidence.Role, evidence.SourcePath)), "Duplicate included TRI evidence role/sourcePath.");
            }
            Require(shapeKeys.Add(shape.HeadPart + "\0" + shape.ModelNif.Value + "\0" + shape.OutputShapeName),
                "Duplicate included FaceGeom shape.");
        }
        Require(attestation.ExcludedShapes.Length <= MaxCollection,
            "Excluded shape count is outside the portable bound.");
        var excludedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in attestation.ExcludedShapes)
        {
            ValidateAssetPath(item.ProviderModel, "excluded.providerModel");
            ValidatePortable(item.ShapeName, "excluded.shapeName");
            Require(excludedKeys.Add(item.ProviderModel.Value + "\0" + item.ShapeName),
                "Duplicate excluded shape evidence.");
        }
        Require(attestation.ExcludedMetadata.Length <= MaxCollection,
            "Excluded metadata count is outside the portable bound.");
        var metadataKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in attestation.ExcludedMetadata)
        {
            ValidatePortable(item.Kind, "excludedMetadata.kind");
            ValidatePortable(item.PortableValue, "excludedMetadata.value");
            Require(metadataKeys.Add(item.Kind + "\0" + item.PortableValue),
                "Duplicate excluded metadata evidence.");
        }
        ValidatePortable(attestation.VerifierVersion, "verifierVersion");
    }

    private static void ValidateInstallArtifact(ExternalHeadPartInstallVerificationArtifact artifact)
    {
        Require(artifact.SchemaIdentifier == ExternalHeadPartSchemaIdentifiers.InstallVerification,
            "Unsupported install verification schema.");
        ValidateInstallState(artifact.CurrentInstallDependencyState, "currentInstallDependencyState");
        Require(!artifact.RuntimeAuthority && !artifact.VisualAuthority,
            "External install artifacts cannot claim runtime or visual authority.");
        Require(artifact.DescriptorIds.Length is >= 1 and <= MaxCollection,
            "Install artifact descriptor ID count is outside the portable bound.");
        var previous = string.Empty;
        foreach (var id in artifact.DescriptorIds)
        {
            ValidateHash(id, "descriptorIds");
            Require(string.CompareOrdinal(previous, id.Value) < 0,
                "Descriptor IDs must be unique and ordinally sorted.");
            previous = id.Value;
        }
        // Create-time verification may carry a current snapshot without a
        // historical Finish manifest. A non-null historical marker still
        // requires its corresponding snapshot; null is the create value.
        Require(artifact.HistoricalSnapshotValid != false ||
                artifact.VerifiedInstallSnapshot is not null,
            "A false historical snapshot marker requires a snapshot payload.");
        Require(artifact.HistoricalSnapshotValid != true ||
                artifact.VerifiedInstallSnapshot is not null,
            "A true historical snapshot marker requires a snapshot payload.");
        if (artifact.VerifiedInstallSnapshot is ExternalHeadPartVerifiedInstallSnapshot snapshot)
        {
            ValidateHash(snapshot.SelectedManifestSha256, "snapshot.selectedManifestSha256");
            ValidateDescriptorIds(snapshot.DescriptorIds, artifact.DescriptorIds, "snapshot.descriptorIds");
            ValidateFingerprint(snapshot.ContextFingerprint);
        }
        Require(artifact.ProviderObservations.Length <= MaxCollection,
            "Provider observation count is outside the portable bound.");
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in artifact.ProviderObservations)
        {
            ValidatePlugin(item.ProviderPlugin, "providerObservation.providerPlugin");
            ValidateHash(item.ExpectedSha256, "providerObservation.expectedSha256");
            Require(item.CurrentSha256.HasValue == item.Enabled.HasValue,
                "Provider current hash and enabled state must appear together.");
            if (item.CurrentSha256 is Sha256Hash current)
                ValidateHash(current, "providerObservation.currentSha256");
            Require(providers.Add(item.ProviderPlugin.Value), "Duplicate provider observation.");
        }
        var providerObservations = artifact.ProviderObservations.ToDictionary(
            item => item.ProviderPlugin.Value,
            StringComparer.OrdinalIgnoreCase);
        Require(artifact.MissingPrerequisites.Length <= MaxCollection,
            "Missing prerequisite count is outside the portable bound.");
        var prerequisites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in artifact.MissingPrerequisites)
        {
            ValidatePortable(item.DiagnosticCode, "prerequisite.code");
            ValidatePortable(item.PortableIdentity, "prerequisite.identity");
            if (item.ExpectedSha256 is Sha256Hash expected)
                ValidateHash(expected, "prerequisite.expectedSha256");
            if (item.CurrentSha256 is Sha256Hash current)
                ValidateHash(current, "prerequisite.currentSha256");
            if (item.Enabled is bool enabled)
                _ = enabled;
            ValidatePortable(item.NextAction, "prerequisite.nextAction");
            Require(prerequisites.Add(item.DiagnosticCode + "\0" + item.PortableIdentity),
                "Duplicate missing prerequisite.");
            if (item.DiagnosticCode is ExternalHeadPartDiagnosticCodes.ProviderMissing
                or ExternalHeadPartDiagnosticCodes.ProviderDisabled)
            {
                if (!providerObservations.TryGetValue(item.PortableIdentity,
                        out var observation))
                    throw Invalid("Provider prerequisite has no matching observation.");
                Require(item.ExpectedSha256 == observation.ExpectedSha256 &&
                        item.CurrentSha256 == observation.CurrentSha256 &&
                        item.Enabled == observation.Enabled,
                    "Provider prerequisite disagrees with provider observation.");
                if (item.DiagnosticCode == ExternalHeadPartDiagnosticCodes.ProviderMissing)
                {
                    Require(observation.CurrentSha256 is null &&
                            observation.Enabled is null,
                        "ProviderMissing requires an absent provider observation.");
                }
                else
                {
                    Require(observation.CurrentSha256 is Sha256Hash &&
                            observation.Enabled == false,
                        "ProviderDisabled requires a disabled provider observation.");
                }
            }
        }
        foreach (var observation in artifact.ProviderObservations)
        {
            if (observation.CurrentSha256 is Sha256Hash current &&
                observation.Enabled == true &&
                current == observation.ExpectedSha256)
                continue;
            Require(artifact.MissingPrerequisites.Any(item =>
                    string.Equals(item.PortableIdentity,
                        observation.ProviderPlugin.Value,
                        StringComparison.OrdinalIgnoreCase) &&
                    item.ExpectedSha256 == observation.ExpectedSha256 &&
                    item.CurrentSha256 == observation.CurrentSha256 &&
                    item.Enabled == observation.Enabled),
                "Non-verified provider observation requires a matching prerequisite.");
        }
        switch (artifact.CurrentInstallDependencyState)
        {
            case ExternalInstallDependencyState.Verified:
                Require(artifact.PackageIntegrity && artifact.DescriptorClosureValid,
                    "Verified install state requires package and descriptor closure integrity.");
                Require(artifact.InstallReady && artifact.InstallDependencyAuthority,
                    "Verified install state requires ready dependency authority.");
                Require(artifact.MissingPrerequisites.Length == 0,
                    "Verified install state cannot have missing prerequisites.");
                Require(artifact.ProviderObservations.Length > 0,
                    "Verified install state requires provider observations.");
                foreach (var observation in artifact.ProviderObservations)
                {
                    Require(observation.CurrentSha256 is Sha256Hash &&
                            observation.Enabled == true &&
                            observation.CurrentSha256 == observation.ExpectedSha256,
                        "Verified install state requires enabled, hash-matching providers.");
                }
                break;
            case ExternalInstallDependencyState.DeclaredUnverified:
                Require(!artifact.InstallReady && !artifact.InstallDependencyAuthority,
                    "Declared-unverified state cannot claim install authority.");
                Require(artifact.MissingPrerequisites.Length > 0,
                    "Declared-unverified state requires at least one prerequisite.");
                break;
            case ExternalInstallDependencyState.NotRequired:
                Require(artifact.InstallReady && !artifact.InstallDependencyAuthority,
                    "Not-required state requires ready package semantics without install authority.");
                Require(artifact.ProviderObservations.Length == 0,
                    "Not-required state cannot carry provider observations.");
                Require(artifact.MissingPrerequisites.Length == 0,
                    "Not-required state cannot carry missing prerequisites.");
                break;
            default:
                throw Invalid("Unknown install dependency state.");
        }
        if (artifact.HistoricalSnapshotValid == false)
            Require(artifact.CurrentInstallDependencyState != ExternalInstallDependencyState.Verified &&
                    !artifact.InstallReady && !artifact.InstallDependencyAuthority,
                "An invalid historical install snapshot cannot claim current authority.");
    }

    private static void ValidateFingerprint(ExternalHeadPartInstallContextFingerprint fingerprint)
    {
        ValidateHash(fingerprint.Sha256, "contextFingerprint.sha256");
        Require(fingerprint.Observations.Length <= MaxCollection,
            "Install observation count is outside the portable bound.");
        var orders = new HashSet<int>();
        var previous = int.MinValue;
        foreach (var item in fingerprint.Observations)
        {
            ValidatePortable(item.Kind, "observation.kind");
            ValidatePortable(item.PortableIdentity, "observation.identity");
            ValidateHash(item.Sha256, "observation.sha256");
            ValidateLength(item.ByteLength, "observation.byteLength");
            Require(item.Order >= 0 && item.Order <= MaxCollection && orders.Add(item.Order) && item.Order > previous,
                "Install observations must be unique and in ascending order.");
            previous = item.Order;
        }
        Require(fingerprint.Sha256 ==
                ComputeInstallContextFingerprintHash(fingerprint.Observations),
            "Install context fingerprint does not match its canonical observations.");
    }

    internal static Sha256Hash ComputeInstallContextFingerprintHash(
        ImmutableArray<ExternalHeadPartInstallObservation> observations)
    {
        byte[] canonical = Write(writer =>
        {
            writer.WriteStartArray();
            foreach (var item in observations)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", item.Kind);
                writer.WriteString("portableIdentity", item.PortableIdentity);
                writer.WriteString("sha256", item.Sha256.Value);
                writer.WriteNumber("byteLength", item.ByteLength);
                writer.WriteNumber("order", item.Order);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
        return HashWithDomain(InstallContextDomain, canonical);
    }

    private static void ValidateDescriptorIds(ImmutableArray<Sha256Hash> actual,
        ImmutableArray<Sha256Hash> expected, string role)
    {
        Require(actual.SequenceEqual(expected), role + " must match artifact descriptor IDs.");
        var previous = string.Empty;
        foreach (var id in actual)
        {
            ValidateHash(id, role);
            Require(string.CompareOrdinal(previous, id.Value) < 0, role + " must be ordinally sorted.");
            previous = id.Value;
        }
    }

    private static JsonDocument ParseDocument(ReadOnlySpan<byte> bytes, string role)
    {
        try
        {
            var document = JsonDocument.Parse(bytes.ToArray(), StrictDocumentOptions);
            RejectDuplicateProperties(document.RootElement, role);
            return document;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Malformed {role} JSON.", exception);
        }
    }

    private static void RejectDuplicateProperties(JsonElement element, string role)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                Require(seen.Add(property.Name), $"Duplicate JSON property '{property.Name}' in {role}.");
                RejectDuplicateProperties(property.Value, role + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray())
                RejectDuplicateProperties(child, role + "[]");
    }

    private static void RequireObject(JsonElement element, string role) =>
        Require(element.ValueKind == JsonValueKind.Object, role + " must be an object.");

    private static void RequireArray(JsonElement element, string role) =>
        Require(element.ValueKind == JsonValueKind.Array, role + " must be an array.");

    private static void RequireProperties(JsonElement element, string[] required,
        string[]? optional = null)
    {
        var allowed = required.Concat(optional ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            Require(allowed.Contains(property.Name), $"Unknown JSON property '{property.Name}'.");
        foreach (var name in required)
            Require(element.TryGetProperty(name, out _), $"Required JSON property '{name}' is missing.");
    }

    private static string RequiredString(JsonElement parent, string property)
    {
        var element = parent.GetProperty(property);
        Require(element.ValueKind == JsonValueKind.String, $"'{property}' must be a string.");
        return element.GetString() ?? throw Invalid($"'{property}' cannot be null.");
    }

    private static string RequiredPortableString(JsonElement parent, string property, string role)
    {
        var value = RequiredString(parent, property);
        ValidatePortable(value, role);
        return value;
    }

    private static bool RequiredBool(JsonElement parent, string property)
    {
        var element = parent.GetProperty(property);
        Require(element.ValueKind is JsonValueKind.True or JsonValueKind.False,
            $"'{property}' must be a boolean.");
        return element.GetBoolean();
    }

    private static bool? OptionalBool(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element))
            return null;
        return element.ValueKind == JsonValueKind.Null ? null : RequiredBool(parent, property);
    }

    private static long RequiredPositiveLong(JsonElement parent, string property)
    {
        var element = parent.GetProperty(property);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var value))
            throw Invalid($"'{property}' must be an integer.");
        Require(value > 0 && value <= MaxByteLength,
            $"'{property}' is outside the positive length bound.");
        return value;
    }

    private static int RequiredInt(JsonElement parent, string property)
    {
        var element = parent.GetProperty(property);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            throw Invalid($"'{property}' must be a 32-bit integer.");
        return value;
    }

    private static int RequiredPositiveInt(JsonElement parent, string property)
    {
        var value = RequiredInt(parent, property);
        Require(value > 0, $"'{property}' must be positive.");
        return value;
    }

    private static int RequiredNonNegativeInt(JsonElement parent, string property)
    {
        var value = RequiredInt(parent, property);
        Require(value >= 0, $"'{property}' must be non-negative.");
        return value;
    }

    private static void RequireCount(JsonElement array, string role, int minimum, int maximum)
    {
        var count = array.GetArrayLength();
        Require(count >= minimum && count <= maximum, $"{role} count is outside the portable bound.");
    }

    private static AssetPath ParseAssetPath(JsonElement parent, string property) =>
        ParseAssetPath(RequiredString(parent, property), property);

    private static AssetPath ParseAssetPath(string value, string role)
    {
        try
        {
            var path = new AssetPath(value);
            Require(path.Value == value, role + " must use canonical slash separators.");
            ValidateAssetPath(path, role);
            return path;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Invalid asset path in {role}.", exception);
        }
    }

    private static AssetPath? ParseOptionalAsset(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element))
            return null;
        return element.ValueKind == JsonValueKind.Null
            ? null
            : ParseAssetPath(OptionalString(element, property), property);
    }

    private static FormReference ParseFormReference(JsonElement parent, string property) =>
        ParseFormReference(RequiredString(parent, property), property);

    private static FormReference ParseFormReference(string value, string role)
    {
        Require(FormReference.TryParse(value, out var reference), role + " is not a form reference.");
        Require(reference.ToString() == value, role + " must use canonical form-reference text.");
        return reference;
    }

    private static FormReference? ParseOptionalFormReference(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element))
            return null;
        return element.ValueKind == JsonValueKind.Null
            ? null
            : ParseFormReference(OptionalString(element, property), property);
    }

    private static Sha256Hash ParseHash(JsonElement parent, string property) =>
        ParseHash(RequiredString(parent, property), property);

    private static Sha256Hash ParseHash(string value, string role)
    {
        Require(value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            role + " must be a lowercase SHA-256 hash.");
        return new Sha256Hash(value);
    }

    private static Sha256Hash? ParseOptionalHash(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element))
            return null;
        return element.ValueKind == JsonValueKind.Null
            ? null
            : ParseHash(OptionalString(element, property), property);
    }

    private static PluginName ParsePlugin(JsonElement parent, string property)
    {
        var value = RequiredString(parent, property);
        try
        {
            var plugin = new PluginName(value);
            ValidatePortable(value, property, 255);
            return plugin;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Invalid plugin in {property}.", exception);
        }
    }

    private static void ValidatePlugin(PluginName plugin, string role)
    {
        ValidatePortable(plugin.Value, role, 255);
        _ = new PluginName(plugin.Value);
    }

    private static void ValidateAssetPath(AssetPath path, string role)
    {
        Require(path.Value.Length > 0 && path.Value.Length <= MaxPath,
            role + " exceeds the portable path bound.");
        Require(path.Value == path.Value.Replace('\\', '/'), role + " contains a noncanonical separator.");
        Require(path.Value[0] != '/' && !path.Value.Contains(':') &&
                !path.Value.Split('/').Any(part => part is "" or "." or ".."),
            role + " must be relative and traversal-free.");
        Require(!path.Value.Any(char.IsControl), role + " contains a control character.");
        Require(!ContainsWindowsReservedDeviceSegment(path.Value),
            role + " contains a Windows reserved device segment.");
    }

    private static void ValidateTriEvidence(SkyrimRaceMenuFaceBakeTriEvidence evidence, bool validateId = true)
    {
        ValidateFaceMorphRole(evidence.Role, "included.tri.role");
        ValidateAssetPath(evidence.SourcePath, "included.tri.sourcePath");
        ValidateHash(evidence.SourceSha256, "included.tri.sourceSha256");
        Require(evidence.DeclaredVertexCount is >= 0 and <= 1_000_000,
            "Included TRI vertex count is outside the portable bound.");
        Require(evidence.MorphCount is >= 0 and <= MaxCollection,
            "Included TRI morph count is outside the portable bound.");
        ValidateFaceBakeDisposition(evidence.Disposition, "included.tri.disposition");
        if (validateId && evidence.EvidenceId is Sha256Hash id)
        {
            ValidateHash(id, "included.tri.evidenceId");
            Require(id == ComputeTriEvidenceId(evidence),
                "Included TRI evidenceId does not match its canonical binding.");
        }
    }

    private static void ValidatePortable(string value, string role, int maximum = MaxPortableToken)
    {
        Require(!string.IsNullOrWhiteSpace(value), role + " cannot be empty.");
        Require(value.Length <= maximum, role + " exceeds the portable token bound.");
        Require(!value.Any(char.IsControl), role + " contains a control character.");
        var lower = value.ToLowerInvariant();
        Require(!lower.Contains("install-state", StringComparison.Ordinal) &&
                !lower.Contains("install state", StringComparison.Ordinal) &&
                !lower.Contains("historical snapshot", StringComparison.Ordinal),
            role + " contains install-state prose.");
        Require(!System.Text.RegularExpressions.Regex.IsMatch(value,
                "^\\d{4}-\\d{2}-\\d{2}(?:[Tt ]|$)"),
            role + " contains a timestamp-shaped value.");
        Require(!System.Text.RegularExpressions.Regex.IsMatch(value,
                "^(?:[A-Za-z]:[\\\\/]|[\\\\/]{2})") &&
                !lower.Contains("worktree", StringComparison.Ordinal) &&
                !lower.Contains("\\\\actorwright", StringComparison.Ordinal),
            role + " contains a host or worktree path.");
        Require(!ContainsWindowsReservedDeviceSegment(value),
            role + " contains a Windows reserved device segment.");
        var pathLike = value.Contains('/') || value.Contains('\\') || value.Contains(':');
        if (pathLike)
        {
            Require(!value.Contains('\\'), role + " contains a noncanonical separator.");
            Require(!value.Contains(':'), role + " contains a device or ADS separator.");
            Require(value[0] != '/' &&
                    !value.Split('/').Any(part => part is "" or "." or ".."),
                role + " contains a rooted or traversal path.");
        }
        Require(!System.Text.RegularExpressions.Regex.IsMatch(lower,
                "(?:^|[\\s_/.-])(not-required|declared-unverified|verified)(?:$|[\\s_/.-])") &&
                !lower.Contains("install-ready", StringComparison.Ordinal) &&
                !lower.Contains("install-dependency-authority", StringComparison.Ordinal),
            role + " contains install-state prose.");
        Require(!value.Contains('\0'), role + " contains a NUL character.");
    }

    private static bool ContainsWindowsReservedDeviceSegment(string value)
    {
        foreach (string segment in value.Replace('\\', '/').Split('/'))
        {
            string withoutTrailing = segment.TrimEnd(' ', '.');
            int extensionSeparator = withoutTrailing.IndexOf('.');
            string deviceName = extensionSeparator >= 0
                ? withoutTrailing[..extensionSeparator]
                : withoutTrailing;
            if (WindowsReservedDeviceNames.Contains(deviceName))
                return true;
        }

        return false;
    }

    private static void ValidateHash(Sha256Hash value, string role) =>
        Require(value.Value.Length == 64 && value.Value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            role + " is not a canonical SHA-256 hash.");

    private static void ValidateLength(long value, string role) =>
        Require(value > 0 && value <= MaxByteLength, role + " is outside the positive length bound.");

    private static void ValidateFormReference(FormReference value, string role)
    {
        ValidatePlugin(value.Plugin, role + ".plugin");
        Require(value.ToString().Length <= MaxPortableToken, role + " exceeds the portable bound.");
    }

    private static void ValidateHeadPartType(NpcHeadPartType value, string role) =>
        Require(Enum.IsDefined(value), role + " is undefined.");

    private static void ValidateTriRole(SkyrimHdptTriRole value, string role) =>
        Require(Enum.IsDefined(value), role + " is undefined.");

    private static void ValidatePhysicsMode(ExternalHeadPartPhysicsBindingMode value, string role) =>
        Require(Enum.IsDefined(value), role + " is undefined.");

    private static void ValidateFaceMorphRole(SkyrimFaceMorphTriRole value, string role) =>
        Require(Enum.IsDefined(value), role + " is undefined.");

    private static void ValidateFaceBakeDisposition(SkyrimRaceMenuFaceBakeTriDisposition value, string role) =>
        Require(Enum.IsDefined(value), role + " is undefined.");

    private static void ValidateSex(NpcSex value, string role) =>
        Require(Enum.IsDefined(value), role + " is undefined.");

    private static void ValidateInstallState(ExternalInstallDependencyState value, string role) =>
        Require(Enum.IsDefined(value), role + " is undefined.");

    private static string DispositionToken(ExternalHeadPartDependencyDisposition value) => value switch
    {
        ExternalHeadPartDependencyDisposition.RecordOnlyExternal => "record-only-external",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string PhysicsModeToken(ExternalHeadPartPhysicsBindingMode value) => value switch
    {
        ExternalHeadPartPhysicsBindingMode.DirectNifExtraData => "direct-nif-extra-data",
        ExternalHeadPartPhysicsBindingMode.DefaultBbpMap => "default-bbp-map",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string PhysicsDispositionToken(ExternalHeadPartPhysicsBindingDisposition value) => value switch
    {
        ExternalHeadPartPhysicsBindingDisposition.InheritRoot => "inherit-root",
        _ => throw Invalid("Unknown physics binding disposition.")
    };

    private static ExternalHeadPartPhysicsBindingDisposition ParsePhysicsDisposition(JsonElement parent) =>
        RequiredString(parent, "physicsBinding") switch
        {
            "inherit-root" => ExternalHeadPartPhysicsBindingDisposition.InheritRoot,
            _ => throw Invalid("Unknown physics binding disposition token.")
        };

    private static string PhysicsOriginToken(ExternalHeadPartPhysicsBindingOrigin value) => value switch
    {
        ExternalHeadPartPhysicsBindingOrigin.Direct => "direct",
        ExternalHeadPartPhysicsBindingOrigin.DefaultBbp => "default-bbp",
        ExternalHeadPartPhysicsBindingOrigin.InheritedFromRoot => "inherited-from-root",
        _ => throw Invalid("Unknown physics binding origin.")
    };

    private static ExternalHeadPartPhysicsBindingOrigin ParsePhysicsOrigin(JsonElement parent) =>
        RequiredString(parent, "origin") switch
        {
            "direct" => ExternalHeadPartPhysicsBindingOrigin.Direct,
            "default-bbp" => ExternalHeadPartPhysicsBindingOrigin.DefaultBbp,
            "inherited-from-root" => ExternalHeadPartPhysicsBindingOrigin.InheritedFromRoot,
            _ => throw Invalid("Unknown physics binding origin token.")
        };

    private static string RedistributionToken(ExternalHeadPartRedistributionMode value) => value switch
    {
        ExternalHeadPartRedistributionMode.ExternalProviderRequired => "external-provider-required",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string InstallStateToken(ExternalInstallDependencyState value) => value switch
    {
        ExternalInstallDependencyState.NotRequired => "not-required",
        ExternalInstallDependencyState.DeclaredUnverified => "declared-unverified",
        ExternalInstallDependencyState.Verified => "verified",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string SexToken(NpcSex value) => value switch
    {
        NpcSex.Male => "male",
        NpcSex.Female => "female",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string TriRoleToken(SkyrimHdptTriRole value) => value switch
    {
        SkyrimHdptTriRole.RaceMorph => "race-morph",
        SkyrimHdptTriRole.Mesh => "mesh",
        SkyrimHdptTriRole.CharGen => "char-gen",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string FaceMorphRoleToken(SkyrimFaceMorphTriRole value) => value switch
    {
        SkyrimFaceMorphTriRole.Race => "race",
        SkyrimFaceMorphTriRole.Chargen => "chargen",
        SkyrimFaceMorphTriRole.Mesh => "mesh",
        SkyrimFaceMorphTriRole.Extended => "extended",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static string FaceBakeDispositionToken(SkyrimRaceMenuFaceBakeTriDisposition value) => value switch
    {
        SkyrimRaceMenuFaceBakeTriDisposition.EligibleMorphSource => "eligible-morph-source",
        SkyrimRaceMenuFaceBakeTriDisposition.EmptyMorphNoOp => "empty-morph-no-op",
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };

    private static ExternalHeadPartDependencyDisposition ParseDisposition(JsonElement parent, string property) =>
        RequiredString(parent, property) switch
        {
            "record-only-external" => ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
            _ => throw Invalid("Unknown disposition token.")
        };

    private static ExternalHeadPartPhysicsBindingMode ParsePhysicsMode(JsonElement parent, string property) =>
        RequiredString(parent, property) switch
        {
            "direct-nif-extra-data" => ExternalHeadPartPhysicsBindingMode.DirectNifExtraData,
            "default-bbp-map" => ExternalHeadPartPhysicsBindingMode.DefaultBbpMap,
            _ => throw Invalid("Unknown physics mode token.")
        };

    private static ExternalHeadPartRedistributionMode ParseRedistribution(JsonElement parent, string property) =>
        RequiredString(parent, property) switch
        {
            "external-provider-required" => ExternalHeadPartRedistributionMode.ExternalProviderRequired,
            _ => throw Invalid("Unknown redistribution token.")
        };

    private static ExternalInstallDependencyState ParseInstallState(JsonElement parent, string property) =>
        RequiredString(parent, property) switch
        {
            "not-required" => ExternalInstallDependencyState.NotRequired,
            "declared-unverified" => ExternalInstallDependencyState.DeclaredUnverified,
            "verified" => ExternalInstallDependencyState.Verified,
            _ => throw Invalid("Unknown install dependency state token.")
        };

    private static NpcHeadPartType ParseHeadPartType(JsonElement parent, string property)
    {
        var value = RequiredString(parent, property);
        Require(NpcHeadPartTypeExtensions.TryParseWireName(value, out var type) && type.ToWireName() == value,
            "Unknown or noncanonical head-part token.");
        return type;
    }

    private static SkyrimHdptTriRole ParseTriRole(JsonElement parent, string property) =>
        RequiredString(parent, property) switch
        {
            "race-morph" => SkyrimHdptTriRole.RaceMorph,
            "mesh" => SkyrimHdptTriRole.Mesh,
            "char-gen" => SkyrimHdptTriRole.CharGen,
            _ => throw Invalid("Unknown TRI role token.")
        };

    private static SkyrimFaceMorphTriRole ParseFaceMorphRole(JsonElement parent, string property) =>
        RequiredString(parent, property) switch
        {
            "race" => SkyrimFaceMorphTriRole.Race,
            "chargen" => SkyrimFaceMorphTriRole.Chargen,
            "mesh" => SkyrimFaceMorphTriRole.Mesh,
            "extended" => SkyrimFaceMorphTriRole.Extended,
            _ => throw Invalid("Unknown face morph role token.")
        };

    private static SkyrimRaceMenuFaceBakeTriDisposition ParseFaceBakeDisposition(JsonElement parent, string property) =>
        RequiredString(parent, property) switch
        {
            "eligible-morph-source" => SkyrimRaceMenuFaceBakeTriDisposition.EligibleMorphSource,
            "empty-morph-no-op" => SkyrimRaceMenuFaceBakeTriDisposition.EmptyMorphNoOp,
            _ => throw Invalid("Unknown TRI disposition token.")
        };

    private static NpcSex? ParseOptionalSex(JsonElement parent, string property)
    {
        var element = parent.GetProperty(property);
        if (element.ValueKind == JsonValueKind.Null)
            return null;
        return OptionalString(element, property) switch
        {
            "male" => NpcSex.Male,
            "female" => NpcSex.Female,
            _ => throw Invalid("Unknown sex token.")
        };
    }

    private static string OptionalString(JsonElement element, string property)
    {
        Require(element.ValueKind == JsonValueKind.String,
            $"'{property}' must be a string or null.");
        return element.GetString() ?? throw Invalid($"'{property}' cannot be null.");
    }

    private static void WriteNullableAsset(Utf8JsonWriter writer, string property, AssetPath? value)
    {
        if (value is AssetPath path)
            writer.WriteString(property, path.Value);
        else
            writer.WriteNull(property);
    }

    private static void WriteNullableFormReference(Utf8JsonWriter writer, string property, FormReference? value)
    {
        if (value is FormReference reference)
            writer.WriteString(property, reference.ToString());
        else
            writer.WriteNull(property);
    }

    private static void WriteNullableSex(Utf8JsonWriter writer, string property, NpcSex? value)
    {
        if (value is NpcSex sex)
            writer.WriteString(property, SexToken(sex));
        else
            writer.WriteNull(property);
    }

    private static void WriteFormReferenceArray(Utf8JsonWriter writer, string property,
        ImmutableArray<FormReference> values)
    {
        writer.WritePropertyName(property);
        writer.WriteStartArray();
        foreach (var value in values)
            writer.WriteStringValue(value.ToString());
        writer.WriteEndArray();
    }

    private static byte[] Write(Action<Utf8JsonWriter> action)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.Default, Indented = false }))
        {
            action(writer);
            writer.Flush();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static Sha256Hash HashWithDomain(string domain, byte[] bytes)
    {
        var prefix = Encoding.UTF8.GetBytes(domain);
        var combined = new byte[prefix.Length + bytes.Length];
        prefix.CopyTo(combined, 0);
        bytes.CopyTo(combined, prefix.Length);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(combined)));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw Invalid(message);
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
