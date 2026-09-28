using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Creates and validates the exact canonical JSON byte authority used by the
/// FaceGeom HairTint transaction. A typed value is never accepted as a
/// substitute for its persisted bytes.
/// </summary>
public sealed class FaceGeomHairRegionsDocumentCodec
{
    private readonly JsonSerializerOptions options = CreateOptions();
    private readonly FaceGeomHairRegionsWorkspaceBoundary boundary;

    public FaceGeomHairRegionsDocumentCodec()
        : this(ActorwrightWorkspace.ResolveRoot())
    {
    }

    public FaceGeomHairRegionsDocumentCodec(
        WorkspacePath workspaceRoot)
        : this(
            new FaceGeomHairRegionsWorkspaceBoundary(
                workspaceRoot))
    {
    }

    public FaceGeomHairRegionsDocumentCodec(
        FaceGeomHairRegionsWorkspaceBoundary boundary)
    {
        this.boundary = boundary ??
            throw new ArgumentNullException(nameof(boundary));
    }

    public FaceGeomHairRegionsWorkspaceBoundary WorkspaceBoundary =>
        boundary;

    public StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
        BindAnalysis(FaceGeomHairRegionsAnalysis value) =>
        Bind(value, FaceGeomHairRegionSchemas.Analysis);

    public StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
        BindRequest(FaceGeomHairRegionsRequest value) =>
        Bind(value, FaceGeomHairRegionSchemas.Request);

    public StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
        BindProposal(FaceGeomHairRegionsProposal value) =>
        Bind(value, FaceGeomHairRegionSchemas.Proposal);

    public StrictJsonDocumentAuthority<FaceGeomHairRegionsManifest>
        BindManifest(FaceGeomHairRegionsManifest value) =>
        Bind(value, FaceGeomHairRegionSchemas.Manifest);

    public StrictJsonDocumentAuthority<
        FaceGeomHairRegionsPreviewEvidenceDocument>
        BindPreviewEvidence(
            FaceGeomHairRegionsPreviewEvidenceDocument value) =>
        Bind(value, FaceGeomHairRegionSchemas.PreviewEvidence);

    public FaceGeomHairRegionsAnalysis ValidateAnalysis(
        StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>
            document) =>
        Validate(document, FaceGeomHairRegionSchemas.Analysis);

    public FaceGeomHairRegionsRequest ValidateRequest(
        StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>
            document) =>
        Validate(document, FaceGeomHairRegionSchemas.Request);

    public FaceGeomHairRegionsProposal ValidateProposal(
        StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>
            document) =>
        Validate(document, FaceGeomHairRegionSchemas.Proposal);

    public FaceGeomHairRegionsManifest ValidateManifest(
        StrictJsonDocumentAuthority<FaceGeomHairRegionsManifest>
            document) =>
        Validate(document, FaceGeomHairRegionSchemas.Manifest);

    public StrictJsonDocumentAuthority<
        FaceGeomHairRegionsPreviewEvidenceDocument>
        DecodePreviewEvidence(
            ReadOnlyMemory<byte> bytes,
            Sha256Hash expectedSha256) =>
        Decode<FaceGeomHairRegionsPreviewEvidenceDocument>(
            bytes.ToArray(),
            expectedSha256,
            FaceGeomHairRegionSchemas.PreviewEvidence);

    public ValueTask<StrictJsonDocumentAuthority<FaceGeomHairRegionsAnalysis>>
        LoadAnalysisAsync(
            WorkspacePath path,
            Sha256Hash expectedSha256,
            CancellationToken cancellationToken) =>
        LoadAsync<FaceGeomHairRegionsAnalysis>(
            path,
            expectedSha256,
            FaceGeomHairRegionSchemas.Analysis,
            cancellationToken);

    public ValueTask<StrictJsonDocumentAuthority<FaceGeomHairRegionsRequest>>
        LoadRequestAsync(
            WorkspacePath path,
            Sha256Hash expectedSha256,
            CancellationToken cancellationToken) =>
        LoadAsync<FaceGeomHairRegionsRequest>(
            path,
            expectedSha256,
            FaceGeomHairRegionSchemas.Request,
            cancellationToken);

    public ValueTask<StrictJsonDocumentAuthority<FaceGeomHairRegionsProposal>>
        LoadProposalAsync(
            WorkspacePath path,
            Sha256Hash expectedSha256,
            CancellationToken cancellationToken) =>
        LoadAsync<FaceGeomHairRegionsProposal>(
            path,
            expectedSha256,
            FaceGeomHairRegionSchemas.Proposal,
            cancellationToken);

    public async ValueTask<
        StrictJsonDocumentAuthority<FaceGeomHairRegionsManifest>>
        LoadManifestAsync(
            WorkspacePath path,
            CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadOnceAsync(path, cancellationToken);
        return Decode<FaceGeomHairRegionsManifest>(
            bytes,
            Hash(bytes),
            FaceGeomHairRegionSchemas.Manifest);
    }

    public async ValueTask<ExactJsonFileAuthority> LoadExactJsonAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadOnceAsync(path, cancellationToken);
        try
        {
            _ = JsonSerializer.Deserialize<JsonElement>(
                bytes,
                options);
            using JsonDocument parsed = JsonDocument.Parse(
                bytes,
                StrictDocumentOptions);
            RejectDuplicateProperties(parsed.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The exact JSON document is invalid.",
                exception);
        }
        return new ExactJsonFileAuthority(
            path,
            bytes.ToImmutableArray(),
            bytes.LongLength,
            Hash(bytes));
    }

    public ReviewedGameIntakeDocumentAuthority BindReviewedIntake(
        ReviewedGameIntake value,
        WorkspacePath authorityPath)
    {
        ArgumentNullException.ThrowIfNull(value);
        ReviewedGameIntake validated =
            ValidateReviewedIntakeValue(value);
        ReviewedIntakeDocument document =
            ToReviewedIntakeDocument(validated);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            document,
            options);
        return new ReviewedGameIntakeDocumentAuthority(
            validated,
            new ExactJsonFileAuthority(
                authorityPath,
                bytes.ToImmutableArray(),
                bytes.LongLength,
                Hash(bytes)));
    }

    public async ValueTask<ReviewedGameIntakeDocumentAuthority>
        LoadReviewedIntakeAsync(
            WorkspacePath path,
            CancellationToken cancellationToken)
    {
        ExactJsonFileAuthority exact =
            await LoadExactJsonAsync(path, cancellationToken);
        return ParseReviewedIntake(
            exact);
    }

    internal ReviewedGameIntakeDocumentAuthority ParseReviewedIntake(
        ExactJsonFileAuthority exact)
    {
        ArgumentNullException.ThrowIfNull(exact);
        byte[] exactBytes =
            exact.Utf8Json.ToArray();
        if (exact.Utf8Json.IsDefaultOrEmpty ||
            exact.ByteLength != exactBytes.LongLength ||
            exact.Sha256 !=
                Hash(exactBytes))
        {
            throw new InvalidDataException(
                "The reviewed-intake JSON authority bytes, length, and hash do not agree.");
        }
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(
                exactBytes,
                StrictDocumentOptions);
            RejectDuplicateProperties(
                parsed.RootElement);
            ReviewedIntakeDocument document =
                JsonSerializer.Deserialize<ReviewedIntakeDocument>(
                    exactBytes,
                    options) ??
                throw Invalid(
                    "The reviewed intake deserialized to null.");
            return new ReviewedGameIntakeDocumentAuthority(
                ToReviewedIntake(document),
                exact);
        }
        catch (Exception exception) when (
            exception is JsonException or
                ArgumentException or
                InvalidOperationException or
                OverflowException)
        {
            throw new InvalidDataException(
                "The reviewed intake is invalid.",
                exception);
        }
    }

    private StrictJsonDocumentAuthority<T> Bind<T>(
        T value,
        string expectedSchema)
    {
        ArgumentNullException.ThrowIfNull(value);
        RequireSchema(value, expectedSchema);
        RequireDocumentShape(value);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            value,
            options);
        return new StrictJsonDocumentAuthority<T>(
            value,
            bytes.ToImmutableArray(),
            Hash(bytes));
    }

    private async ValueTask<StrictJsonDocumentAuthority<T>> LoadAsync<T>(
        WorkspacePath path,
        Sha256Hash expectedSha256,
        string expectedSchema,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await ReadOnceAsync(path, cancellationToken);
        return Decode<T>(bytes, expectedSha256, expectedSchema);
    }

    private StrictJsonDocumentAuthority<T> Decode<T>(
        byte[] bytes,
        Sha256Hash expectedSha256,
        string expectedSchema)
    {
        if (Hash(bytes) != expectedSha256)
            throw Invalid(
                "The exact JSON file does not match the expected SHA-256.");
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(
                bytes,
                StrictDocumentOptions);
            RejectDuplicateProperties(parsed.RootElement);
            T value = JsonSerializer.Deserialize<T>(bytes, options) ??
                throw Invalid(
                    "The exact JSON file deserialized to null.");
            byte[] canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(
                value,
                options);
            var authority = new StrictJsonDocumentAuthority<T>(
                value,
                bytes.ToImmutableArray(),
                expectedSha256)
            {
                CanonicalUtf8Json = canonicalBytes.ToImmutableArray(),
                CanonicalSha256 = Hash(canonicalBytes)
            };
            Validate(authority, expectedSchema);
            return authority;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The exact JSON file is invalid.",
                exception);
        }
    }

    private T Validate<T>(
        StrictJsonDocumentAuthority<T> document,
        string expectedSchema)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Utf8Json.IsDefaultOrEmpty)
            throw Invalid("The exact source JSON document is empty.");
        byte[] bytes = document.Utf8Json.ToArray();
        if (Hash(bytes) != document.Sha256)
            throw Invalid(
                "The exact source JSON bytes do not match their SHA-256 authority.");
        if (document.CanonicalUtf8Json.IsDefaultOrEmpty)
            throw Invalid("The canonical JSON document is empty.");
        byte[] canonicalBytes = document.CanonicalUtf8Json.ToArray();
        if (Hash(canonicalBytes) != document.CanonicalSha256)
            throw Invalid(
                "The canonical JSON bytes do not match their SHA-256 authority.");

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(
                bytes,
                StrictDocumentOptions);
            RejectDuplicateProperties(parsed.RootElement);
            T value = JsonSerializer.Deserialize<T>(
                    bytes,
                    options) ??
                throw Invalid(
                    "The canonical JSON document deserialized to null.");
            RequireSchema(value, expectedSchema);
            RequireDocumentShape(value);
            byte[] parsedCanonical =
                JsonSerializer.SerializeToUtf8Bytes(value, options);
            byte[] typedCanonical =
                JsonSerializer.SerializeToUtf8Bytes(
                    document.Value,
                    options);
            if (!canonicalBytes.AsSpan().SequenceEqual(parsedCanonical))
                throw Invalid(
                    "The canonical JSON identity does not match the exact source document.");
            if (!canonicalBytes.AsSpan().SequenceEqual(typedCanonical))
                throw Invalid(
                    "The typed value does not equal its canonical JSON bytes.");
            return value;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The canonical JSON document is invalid.",
                exception);
        }
    }

    private static void RejectDuplicateProperties(
        JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (JsonProperty property in
                     element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Invalid(
                        $"Duplicate JSON property '{property.Name}' is forbidden.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in
                     element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static void RequireSchema<T>(
        T value,
        string expected)
    {
        string? actual = value switch
        {
            FaceGeomHairRegionsAnalysis analysis =>
                analysis.Schema,
            FaceGeomHairRegionsRequest request =>
                request.Schema,
            FaceGeomHairRegionsProposal proposal =>
                proposal.Schema,
            FaceGeomHairRegionsManifest manifest =>
                manifest.Schema,
            FaceGeomHairRegionsPreviewEvidenceDocument evidence =>
                evidence.Schema,
            _ => null
        };
        if (!string.Equals(
                actual,
                expected,
                StringComparison.Ordinal))
            throw Invalid(
                $"Expected schema '{expected}'.");
    }

    private static void RequireDocumentShape<T>(T value)
    {
        switch (value)
        {
            case FaceGeomHairRegionsAnalysis analysis:
                RequireFile(analysis.Source, "analysis source");
                RequireFingerprints(
                    analysis.Fingerprints,
                    "analysis fingerprints");
                if (analysis.Regions.IsDefault ||
                    analysis.Regions.Any(region =>
                        region is null ||
                        string.IsNullOrWhiteSpace(
                            region.StructuralId) ||
                        region.Name is null ||
                        string.IsNullOrWhiteSpace(
                            region.ShapeBlockType) ||
                        string.IsNullOrWhiteSpace(
                            region.ShaderBlockType) ||
                        string.IsNullOrWhiteSpace(
                            region.SharedShaderGroupId) ||
                        region.ShaderOwnerStructuralIds.IsDefault ||
                        region.ShaderOwnerStructuralIds.Any(
                            string.IsNullOrWhiteSpace) ||
                        region.SharedStructuralIds.IsDefault ||
                        region.SharedStructuralIds.Any(
                            string.IsNullOrWhiteSpace) ||
                        region.TextureRoutes.IsDefault ||
                        region.TextureRoutes.Any(
                            route => route is null) ||
                        string.IsNullOrWhiteSpace(
                            region.CurrentColor) ||
                        region.ColorFloatBits.IsDefault))
                    throw Invalid(
                        "The analysis contains a null or incomplete region.");
                RequirePluginColorContext(
                    analysis.PluginColorContext);
                break;

            case FaceGeomHairRegionsRequest request:
                RequireHash(
                    request.AnalysisSha256,
                    "request analysis SHA-256");
                RequireFile(request.Source, "request source");
                RequireAssignments(request.Assignments);
                RequirePath(request.Output, "request output");
                RequirePath(request.Manifest, "request manifest");
                if (request.PrimaryColor is null ||
                    request.AccentColor is null)
                    throw Invalid(
                        "The request colors may not be null.");
                break;

            case FaceGeomHairRegionsProposal proposal:
                RequireHash(
                    proposal.AnalysisSha256,
                    "proposal analysis SHA-256");
                RequireHash(
                    proposal.RequestSha256,
                    "proposal request SHA-256");
                RequireFile(proposal.Source, "proposal source");
                RequirePath(proposal.Output, "proposal output");
                RequirePath(proposal.Manifest, "proposal manifest");
                RequireAssignments(proposal.Assignments);
                RequireEnvelopes(proposal.AuthorizedEnvelopes);
                if (proposal.PredictedChangedByteOffsets.IsDefault ||
                    proposal.PrimaryColor is null ||
                    proposal.AccentColor is null)
                    throw Invalid(
                        "The proposal contains a null or missing collection or color.");
                RequireFingerprints(
                    proposal.SourceFingerprints,
                    "proposal source fingerprints");
                RequireFingerprints(
                    proposal.ExpectedOutputFingerprints,
                    "proposal output fingerprints");
                RequireFile(
                    proposal.ExpectedOutput,
                    "proposal expected output");
                RequirePluginColorContext(
                    proposal.PluginColorContext);
                break;

            case FaceGeomHairRegionsManifest manifest:
                RequireHash(
                    manifest.ProposalSha256,
                    "manifest proposal SHA-256");
                RequireFile(manifest.Source, "manifest source");
                RequireFile(manifest.Output, "manifest output");
                RequireEnvelopes(manifest.AuthorizedEnvelopes);
                if (manifest.ChangedByteOffsets.IsDefault ||
                    manifest.SurvivingArtifacts.IsDefault ||
                    manifest.SurvivingArtifacts.Any(path =>
                        string.IsNullOrWhiteSpace(path.Value)))
                    throw Invalid(
                        "The manifest contains a null or missing collection.");
                RequireFingerprints(
                    manifest.Fingerprints,
                    "manifest fingerprints");
                break;

            case FaceGeomHairRegionsPreviewEvidenceDocument evidence:
                RequireHash(
                    evidence.StagedFaceGeomSha256,
                    "preview staged FaceGeom SHA-256");
                RequireHash(
                    evidence.ProposalSha256,
                    "preview proposal SHA-256");
                RequireHash(
                    evidence.IntakeSha256,
                    "preview intake SHA-256");
                RequireHash(
                    evidence.RendererSha256,
                    "preview renderer SHA-256");
                RequireHash(
                    evidence.TextureFingerprintSha256,
                    "preview texture fingerprint SHA-256");
                RequireRenderAuthority(
                    evidence.RenderAuthority);
                RequireArtifactEvidence(
                    evidence.Artifacts);
                break;
        }
    }

    private static void RequireArtifactEvidence(
        ImmutableArray<FaceGeomHairArtifactEvidence>
            artifacts)
    {
        if (artifacts.IsDefaultOrEmpty ||
            artifacts.Length > 130 ||
            artifacts.Select(item => item.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != artifacts.Length)
            throw Invalid(
                "Preview artifact evidence is missing, duplicated, or unbounded.");
        FaceGeomHairRegionsPreviewArtifactKind[] kinds =
        [
            FaceGeomHairRegionsPreviewArtifactKind
                .CombinedFace,
            FaceGeomHairRegionsPreviewArtifactKind
                .RegionThumbnail,
            FaceGeomHairRegionsPreviewArtifactKind
                .RegionMask,
            FaceGeomHairRegionsPreviewArtifactKind
                .ContactSheet
        ];
        foreach (FaceGeomHairArtifactEvidence artifact in
                 artifacts)
        {
            if (artifact is null ||
                !kinds.Contains(
                    artifact.Kind) ||
                string.IsNullOrWhiteSpace(
                    artifact.RelativePath) ||
                artifact.RelativePath !=
                    Path.GetFileName(
                        artifact.RelativePath) ||
                !artifact.RelativePath.EndsWith(
                    ".png",
                    StringComparison.OrdinalIgnoreCase) ||
                artifact.Bytes <= 0 ||
                artifact.Width <= 0 ||
                artifact.Height <= 0 ||
                artifact.NonEmptyPixelCount <= 0 ||
                (artifact.Kind is
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionThumbnail or
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionMask) !=
                    !string.IsNullOrWhiteSpace(
                        artifact.StructuralId))
                throw Invalid(
                    "Preview artifact evidence contains an invalid role, filename, size, or dimension.");
            RequireHash(
                artifact.Sha256,
                "preview artifact SHA-256");
        }
        FaceGeomHairArtifactEvidence[] canonical =
            artifacts
                .OrderBy(item => item.Kind switch
                {
                    FaceGeomHairRegionsPreviewArtifactKind
                        .CombinedFace => 0,
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionThumbnail => 1,
                    FaceGeomHairRegionsPreviewArtifactKind
                        .RegionMask => 2,
                    FaceGeomHairRegionsPreviewArtifactKind
                        .ContactSheet => 3,
                    _ => 4
                })
                .ThenBy(
                    item => item.StructuralId ?? "",
                    StringComparer.Ordinal)
                .ToArray();
        if (!artifacts.SequenceEqual(canonical))
            throw Invalid(
                "Preview artifact evidence is not in canonical role/structural order.");
    }

    private static void RequireRenderAuthority(
        FaceGeomHairRegionsRenderAuthority authority)
    {
        if (authority is null)
            throw Invalid(
                "Preview render authority is missing.");
        RequireHash(
            authority.BlenderSha256,
            "preview Blender SHA-256");
        RequireHash(
            authority.PyniflyArchiveSha256,
            "preview PyNifly archive SHA-256");
        RequireHash(
            authority.PyniflySourceProfileSha256,
            "preview PyNifly source-profile SHA-256");
        RequireHash(
            authority.RendererScriptSha256,
            "preview renderer-script SHA-256");
        RequireHash(
            authority.TexconvSha256,
            "preview Texconv SHA-256");
        RequireHash(
            authority.TextureSourceFingerprintSha256,
            "preview texture-source fingerprint SHA-256");
        RequireHash(
            authority.LoadedTextureObservationFingerprintSha256,
            "preview loaded-texture observation SHA-256");
        RequireHash(
            authority.PyniflyModuleSourceFingerprintSha256,
            "preview PyNifly module-source SHA-256");
        RequireHash(
            authority.OriginalProfileFingerprintSha256,
            "preview original-profile SHA-256");
        if (authority.PyniflyModuleCount <= 0 ||
            authority.OriginalProfileFileCount <= 0 ||
            authority.Textures.IsDefault ||
            authority.PyniflyModules.IsDefaultOrEmpty ||
            authority.PyniflyModules.Length !=
                authority.PyniflyModuleCount)
            throw Invalid(
                "Preview render authority contains an incomplete tool or module-closure count.");
        foreach (FaceGeomHairPyniflyModuleEvidence? module in
                 authority.PyniflyModules)
        {
            if (module is null)
                throw Invalid(
                    "Preview render authority contains a null PyNifly module row.");
            if (string.IsNullOrWhiteSpace(
                    module.ModuleName) ||
                string.IsNullOrWhiteSpace(
                    module.RelativeSource) ||
                (module.Kind ==
                     FaceGeomHairPyniflyModuleKind
                         .SourceFile &&
                 !module.RelativeSource.EndsWith(
                     ".py",
                     StringComparison.OrdinalIgnoreCase)) ||
                (module.Kind ==
                     FaceGeomHairPyniflyModuleKind
                         .NamespaceDirectory &&
                 !module.RelativeSource.EndsWith('/')) ||
                !Enum.IsDefined(module.Kind))
                throw Invalid(
                    "Preview render authority contains an invalid PyNifly module row: " +
                    $"{module.ModuleName} [{module.Kind}] " +
                    $"{module.RelativeSource}.");
        }
        foreach (FaceGeomHairTextureEvidence? texture in
                 authority.Textures)
        {
            if (texture is null)
                throw Invalid(
                    "Preview render authority contains a null texture row.");
            RequireAssetPath(
                texture.AssetPath,
                "preview texture asset path");
            if (!Enum.IsDefined(texture.ProviderKind) ||
                string.IsNullOrWhiteSpace(
                    texture.Provider) ||
                texture.SourceBytes <= 0 ||
                texture.DecodedPreviewBytes <= 0 ||
                texture.DecodeKind !=
                    "texconv-dds-to-png" ||
                texture.Bindings.IsDefaultOrEmpty)
                throw Invalid(
                    "Preview render authority contains an incomplete texture row: " +
                    texture.AssetPath.Value);
            foreach (FaceGeomHairTextureBindingEvidence? binding in
                     texture.Bindings)
            {
                if (binding is null)
                    throw Invalid(
                        "Preview render authority contains a null material binding: " +
                        texture.AssetPath.Value);
                if (!Enum.IsDefined(binding.Mode) ||
                    string.IsNullOrWhiteSpace(
                        binding.BindingSemantic) ||
                    (binding.Mode ==
                         FaceGeomHairTextureBindingMode
                             .BoundUnmodeled &&
                     binding.BindingSemantic is not
                         "BSShaderTextureSet_EnvMap" and not
                         "BSShaderTextureSet_EnvMask") ||
                    (binding.Mode ==
                         FaceGeomHairTextureBindingMode
                             .SampledImage &&
                     binding.BindingSemantic is
                         "BSShaderTextureSet_EnvMap" or
                         "BSShaderTextureSet_EnvMask") ||
                    string.IsNullOrWhiteSpace(
                        binding.ObjectName) ||
                    string.IsNullOrWhiteSpace(
                        binding.MaterialName) ||
                    string.IsNullOrWhiteSpace(
                        binding.NodeName))
                    throw Invalid(
                        "Preview render authority contains an incomplete material binding: " +
                        $"{texture.AssetPath.Value} [{binding.Mode}] " +
                        $"{binding.BindingSemantic}.");
            }
        }
        string[] duplicateTexturePaths =
            authority.Textures
                .GroupBy(
                    item => item.AssetPath.Value,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .OrderBy(
                    item => item,
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        if (duplicateTexturePaths.Length > 0)
            throw Invalid(
                "Preview render authority contains duplicate texture rows: " +
                string.Join(", ", duplicateTexturePaths));
        FaceGeomHairPyniflyModuleEvidence[] canonicalModules =
            authority.PyniflyModules
                .OrderBy(
                    item => item.ModuleName,
                    StringComparer.Ordinal)
                .ToArray();
        if (!authority.PyniflyModules.SequenceEqual(
                canonicalModules) ||
            canonicalModules.Select(item =>
                    item.ModuleName)
                .Distinct(StringComparer.Ordinal)
                .Count() != canonicalModules.Length)
            throw Invalid(
                "Preview PyNifly module rows are duplicated or non-canonical.");
        using IncrementalHash moduleFingerprint =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (FaceGeomHairPyniflyModuleEvidence module in
                 canonicalModules)
        {
            RequireHash(
                module.SourceSha256,
                "preview PyNifly module SHA-256");
            moduleFingerprint.AppendData(
                System.Text.Encoding.UTF8.GetBytes(
                    $"{module.ModuleName}\0" +
                    $"{module.Kind}\0" +
                    $"{module.RelativeSource}\0" +
                    $"{module.SourceSha256.Value}\n"));
        }
        if (new Sha256Hash(Convert.ToHexString(
                moduleFingerprint.GetHashAndReset())) !=
            authority
                .PyniflyModuleSourceFingerprintSha256)
            throw Invalid(
                "Preview PyNifly module rows do not reproduce their fingerprint.");
    }

    private static void RequireAssignments(
        ImmutableArray<FaceGeomHairRegionAssignment> assignments)
    {
        if (assignments.IsDefault ||
            assignments.Any(assignment =>
                assignment is null ||
                string.IsNullOrWhiteSpace(
                    assignment.StructuralId)))
            throw Invalid(
                "Hair-region assignments may not be null or incomplete.");
    }

    private static void RequireEnvelopes(
        ImmutableArray<FaceGeomHairRegionsAuthorizedEnvelope>
            envelopes)
    {
        if (envelopes.IsDefault ||
            envelopes.Any(envelope =>
                envelope is null ||
                string.IsNullOrWhiteSpace(
                    envelope.SharedShaderGroupId) ||
                envelope.StructuralIds.IsDefault ||
                envelope.StructuralIds.Any(
                    string.IsNullOrWhiteSpace) ||
                envelope.OldFloatBits.IsDefault ||
                envelope.NewFloatBits.IsDefault))
            throw Invalid(
                "Authorized envelopes may not be null or incomplete.");
    }

    private static void RequireFile(
        FaceGeomHairRegionsFile? file,
        string role)
    {
        if (file is null)
            throw Invalid($"The {role} may not be null.");
        RequirePath(file.Path, $"{role} path");
        RequireHash(file.Sha256, $"{role} SHA-256");
    }

    private static void RequireFingerprints(
        FaceGeomHairRegionsFingerprints? fingerprints,
        string role)
    {
        if (fingerprints is null)
            throw Invalid($"The {role} may not be null.");
        RequireHash(fingerprints.Topology, $"{role} topology");
        RequireHash(fingerprints.Geometry, $"{role} geometry");
        RequireHash(fingerprints.Skinning, $"{role} skinning");
        RequireHash(fingerprints.Textures, $"{role} textures");
        RequireHash(fingerprints.Shaders, $"{role} shaders");
    }

    private static void RequirePluginColorContext(
        FaceGeomHairRegionPluginColorContext? context)
    {
        if (context is not null &&
            string.IsNullOrWhiteSpace(context.Plugin))
            throw Invalid(
                "Plugin color context is incomplete.");
    }

    private static void RequirePath(
        WorkspacePath path,
        string role)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
            throw Invalid($"The {role} may not be missing.");
    }

    private static void RequireHash(
        Sha256Hash hash,
        string role)
    {
        if (string.IsNullOrWhiteSpace(hash.Value))
            throw Invalid($"The {role} may not be missing.");
    }

    private static void RequireAssetPath(
        AssetPath path,
        string role)
    {
        if (string.IsNullOrWhiteSpace(path.Value))
            throw Invalid($"The {role} may not be missing.");
        try
        {
            var canonical = new AssetPath(path.Value);
            if (!string.Equals(
                    canonical.Value,
                    path.Value,
                    StringComparison.Ordinal))
                throw Invalid(
                    $"The {role} is not in canonical asset-path form.");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"The {role} is invalid.",
                exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
            MaxDepth = 32,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false
        };
        options.Converters.Add(
            new FaceGeomHairRegionRoleConverter());
        options.Converters.Add(
            new ClosedWireEnumConverter<
                FaceGeomHairRegionsPreviewArtifactKind>(
                [
                    (
                        FaceGeomHairRegionsPreviewArtifactKind
                            .CombinedFace,
                        "combinedFace"),
                    (
                        FaceGeomHairRegionsPreviewArtifactKind
                            .RegionThumbnail,
                        "regionThumbnail"),
                    (
                        FaceGeomHairRegionsPreviewArtifactKind
                            .RegionMask,
                        "regionMask"),
                    (
                        FaceGeomHairRegionsPreviewArtifactKind
                            .ContactSheet,
                        "contactSheet"),
                    (
                        FaceGeomHairRegionsPreviewArtifactKind
                            .EvidenceDocument,
                        "evidenceDocument")
                ]));
        options.Converters.Add(
            new ClosedWireEnumConverter<
                FaceGeomHairTextureBindingMode>(
                [
                    (
                        FaceGeomHairTextureBindingMode
                            .SampledImage,
                        "sampledImage"),
                    (
                        FaceGeomHairTextureBindingMode
                            .BoundUnmodeled,
                        "boundUnmodeled")
                ]));
        options.Converters.Add(
            new ClosedWireEnumConverter<
                FaceGeomHairPyniflyModuleKind>(
                [
                    (
                        FaceGeomHairPyniflyModuleKind
                            .SourceFile,
                        "sourceFile"),
                    (
                        FaceGeomHairPyniflyModuleKind
                            .NamespaceDirectory,
                        "namespaceDirectory")
                ]));
        options.Converters.Add(
            new ClosedWireEnumConverter<AssetProviderKind>(
                [
                    (AssetProviderKind.Loose, "loose"),
                    (AssetProviderKind.Archive, "archive")
                ]));
        options.Converters.Add(new AssetPathConverter());
        options.Converters.Add(new WorkspacePathConverter());
        options.Converters.Add(new Sha256HashConverter());
        return options;
    }

    private sealed class AssetPathConverter :
        JsonConverter<AssetPath>
    {
        public override AssetPath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException(
                    "Asset paths must be JSON strings.");
            string? value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
                throw new JsonException(
                    "Asset paths may not be empty.");
            try
            {
                var canonical = new AssetPath(value);
                if (!string.Equals(
                        canonical.Value,
                        value,
                        StringComparison.Ordinal))
                    throw new JsonException(
                        "Asset path is not in canonical form.");
                return canonical;
            }
            catch (ArgumentException exception)
            {
                throw new JsonException(
                    "Asset path is invalid.",
                    exception);
            }
        }

        public override void Write(
            Utf8JsonWriter writer,
            AssetPath value,
            JsonSerializerOptions options)
        {
            if (string.IsNullOrWhiteSpace(value.Value))
                throw new JsonException(
                    "Asset paths may not be empty.");
            try
            {
                var canonical = new AssetPath(value.Value);
                if (!string.Equals(
                        canonical.Value,
                        value.Value,
                        StringComparison.Ordinal))
                    throw new JsonException(
                        "Asset path is not in canonical form.");
            }
            catch (ArgumentException exception)
            {
                throw new JsonException(
                    "Asset path is invalid.",
                    exception);
            }
            writer.WriteStringValue(value.Value);
        }
    }

    private sealed class ClosedWireEnumConverter<TEnum> :
        JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private readonly Dictionary<string, TEnum>
            fromWire;
        private readonly Dictionary<TEnum, string>
            toWire;

        public ClosedWireEnumConverter(
            IEnumerable<(TEnum Value, string Wire)> values)
        {
            (TEnum Value, string Wire)[] entries =
                values.ToArray();
            fromWire = entries.ToDictionary(
                item => item.Wire,
                item => item.Value,
                StringComparer.Ordinal);
            toWire = entries.ToDictionary(
                item => item.Value,
                item => item.Wire);
        }

        public override TEnum Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException(
                    $"{typeof(TEnum).Name} must be an exact camelCase string.");
            string? wire = reader.GetString();
            if (wire is null ||
                !fromWire.TryGetValue(wire, out TEnum value))
                throw new JsonException(
                    $"{typeof(TEnum).Name} is unknown or non-canonical.");
            return value;
        }

        public override void Write(
            Utf8JsonWriter writer,
            TEnum value,
            JsonSerializerOptions options)
        {
            if (!toWire.TryGetValue(value, out string? wire))
                throw new JsonException(
                    $"{typeof(TEnum).Name} is outside the closed enum.");
            writer.WriteStringValue(wire);
        }
    }

    private static ReviewedIntakeDocument ToReviewedIntakeDocument(
        ReviewedGameIntake value) =>
        new(
            "2",
            value.Edition.ToWireName(),
            IsAccepted: true,
            value.WorkspaceRoot.Value,
            value.DataRoot.Value,
            value.LoadOrderPath.Value,
            value.OutputRoot.Value,
            value.LoadOrderHash.Value,
            value.AssetIndexFingerprint.Value,
            value.IntakeFingerprint.Value,
            value.Plugins.Select(item =>
                    new ReviewedPluginDocument(
                        item.Plugin.Value,
                        item.Order,
                        item.Enabled,
                        item.Requested,
                        item.RequiredMaster,
                        item.SourceHash?.Value ?? string.Empty,
                        item.Masters.Select(master => master.Value)
                            .ToImmutableArray()))
                .ToImmutableArray(),
            value.BodySidecars.Length,
            value.GeneratedPlugins.Length,
            value.GeneratedSidecars.Length,
            value.BodySidecars.Select(item =>
                    new ReviewedBodySidecarDocument(
                        item.Plugin.Value,
                        item.Path.Value,
                        item.SourceHash.Value,
                        item.CanonicalHash.Value,
                        item.NpcCount))
                .ToImmutableArray(),
            value.GeneratedPlugins.Select(item =>
                    new ReviewedGeneratedPluginDocument(
                        item.Plugin.Value,
                        item.Path.Value,
                        item.Author,
                        item.Sha256?.Value,
                        item.ReadSucceeded,
                        item.NpcFormIds.Select(formId =>
                                formId.ToString())
                            .ToImmutableArray()))
                .ToImmutableArray(),
            value.GeneratedSidecars.Select(item =>
                    new ReviewedGeneratedSidecarDocument(
                        item.OriginPlugin.Value,
                        item.Kind.ToWireName(),
                        item.Variant.ToWireName(),
                        item.RelativePath.Value,
                        item.Path.Value,
                        item.FormId?.ToString(),
                        item.Size,
                        item.Sha256.Value))
                .ToImmutableArray(),
            value.AssetProviderCount,
            value.RuntimeAuthority,
            []);

    private ReviewedGameIntake ToReviewedIntake(
        ReviewedIntakeDocument document)
    {
        bool legacyDocument =
            document.SchemaVersion == "1";
        bool exactDocument =
            document.SchemaVersion == "2";
        if (document.Plugins.IsDefault ||
            document.Diagnostics.IsDefault ||
            !(legacyDocument || exactDocument) ||
            !document.IsAccepted ||
            document.RuntimeAuthority ||
            string.IsNullOrWhiteSpace(
                document.LoadOrderHash) ||
            string.IsNullOrWhiteSpace(
                document.AssetIndexFingerprint) ||
            string.IsNullOrWhiteSpace(
                document.IntakeFingerprint) ||
            !GameEditionExtensions.TryParseWireName(
                document.Edition,
                out GameEdition edition) ||
            edition != GameEdition.SkyrimSpecialEdition ||
            document.AssetProviderCount < 0 ||
            document.BodySidecarCount < 0 ||
            document.GeneratedPluginCount < 0 ||
            document.GeneratedSidecarCount < 0 ||
            document.Plugins.Length is < 1 or > 512)
            throw Invalid(
                "The reviewed intake header is invalid or not accepted Skyrim SE authority.");
        if (legacyDocument &&
            (document.BodySidecarCount != 0 ||
             document.GeneratedPluginCount != 0 ||
             document.GeneratedSidecarCount != 0))
            throw Invalid(
                "Legacy reviewed intake counts cannot stand in for missing exact authority rows.");
        if (exactDocument &&
            (document.BodySidecars.IsDefault ||
             document.GeneratedPlugins.IsDefault ||
             document.GeneratedSidecars.IsDefault ||
             document.BodySidecarCount !=
                document.BodySidecars.Length ||
             document.GeneratedPluginCount !=
                document.GeneratedPlugins.Length ||
             document.GeneratedSidecarCount !=
                document.GeneratedSidecars.Length))
            throw Invalid(
                "Exact reviewed intake authority arrays do not match their declared counts.");
        foreach (ReviewedDiagnosticDocument diagnostic in
                 document.Diagnostics)
        {
            if (diagnostic is null ||
                diagnostic.Severity is not (
                    "info" or "warning" or "error") ||
                string.IsNullOrWhiteSpace(diagnostic.Code) ||
                string.IsNullOrWhiteSpace(diagnostic.Message))
                throw Invalid(
                    "A reviewed intake diagnostic is malformed.");
            if (diagnostic.Severity == "error")
                throw Invalid(
                    "An accepted reviewed intake cannot contain an Error diagnostic.");
        }
        var workspaceRoot =
            new WorkspacePath(document.WorkspaceRoot);
        if (workspaceRoot != boundary.WorkspaceRoot)
            throw new UnauthorizedAccessException(
                "The reviewed intake workspace root does not equal the configured K-local authority.");
        boundary.RequireExistingDirectory(
            workspaceRoot,
            "reviewed intake workspace root");
        var dataRoot =
            new WorkspacePath(document.DataRoot);
        if (!dataRoot.IsUnder(workspaceRoot))
            throw new UnauthorizedAccessException(
                "The reviewed intake Data root is outside its workspace.");
        boundary.RequireExistingDirectory(
            dataRoot,
            "reviewed intake Data root");
        var loadOrderPath =
            new WorkspacePath(document.LoadOrderPath);
        if (!loadOrderPath.IsUnder(workspaceRoot))
            throw new UnauthorizedAccessException(
                "The reviewed intake load order is outside its workspace.");
        var loadOrderHash =
            new Sha256Hash(document.LoadOrderHash);
        RequireExactFileHash(
            loadOrderPath,
            loadOrderHash,
            "reviewed intake load order");
        var outputRoot =
            new WorkspacePath(document.OutputRoot);
        if (!outputRoot.IsUnder(workspaceRoot))
            throw new UnauthorizedAccessException(
                "The reviewed intake output root is outside its workspace.");
        boundary.RequireNewDirectory(
            outputRoot,
            "reviewed intake output root");
        var names = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var plugins =
            ImmutableArray.CreateBuilder<PluginClosureReviewEntry>();
        int previousOrder = -1;
        for (int index = 0;
             index < document.Plugins.Length;
             index++)
        {
            ReviewedPluginDocument item =
                document.Plugins[index];
            if (item is null ||
                string.IsNullOrWhiteSpace(item.Plugin) ||
                string.IsNullOrWhiteSpace(item.SourceHash) ||
                item.Masters.IsDefault)
                throw Invalid(
                    "A reviewed intake plugin entry is incomplete.");
            var name = new PluginName(item.Plugin);
            if (item.Order < 0 ||
                item.Order <= previousOrder ||
                !names.Add(name.Value))
                throw Invalid(
                    "Reviewed intake plugins must be unique and preserve distinct strictly ascending global order indexes.");
            previousOrder = item.Order;
            var pluginPath = new WorkspacePath(Path.Combine(
                dataRoot.Value,
                name.Value));
            var sourceHash =
                new Sha256Hash(item.SourceHash);
            RequireExactFileHash(
                pluginPath,
                sourceHash,
                $"reviewed plugin {name.Value}");
            plugins.Add(new PluginClosureReviewEntry(
                name,
                item.Order,
                item.Active,
                true,
                item.Requested,
                item.RequiredMaster,
                true,
                pluginPath,
                sourceHash,
                item.Masters.Select(master =>
                        new PluginName(master))
                    .ToImmutableArray()));
        }
        var bodySidecars =
            ImmutableArray.CreateBuilder<ReviewedBodySidecar>();
        foreach (ReviewedBodySidecarDocument item in
                 legacyDocument
                     ? []
                     : document.BodySidecars)
        {
            if (item is null ||
                string.IsNullOrWhiteSpace(item.Plugin) ||
                string.IsNullOrWhiteSpace(item.Path) ||
                string.IsNullOrWhiteSpace(item.SourceHash) ||
                string.IsNullOrWhiteSpace(item.CanonicalHash))
                throw Invalid(
                    "A reviewed body-sidecar authority row is incomplete.");
            bodySidecars.Add(new ReviewedBodySidecar(
                new PluginName(item.Plugin),
                new WorkspacePath(item.Path),
                new Sha256Hash(item.SourceHash),
                new Sha256Hash(item.CanonicalHash),
                item.NpcCount));
        }
        var generatedPlugins =
            ImmutableArray.CreateBuilder<
                GeneratedPluginScanEntry>();
        foreach (ReviewedGeneratedPluginDocument item in
                 legacyDocument
                     ? []
                     : document.GeneratedPlugins)
        {
            if (item is null ||
                string.IsNullOrWhiteSpace(item.Plugin) ||
                string.IsNullOrWhiteSpace(item.Path) ||
                string.IsNullOrWhiteSpace(item.Author) ||
                item.NpcFormIds.IsDefault)
                throw Invalid(
                    "A reviewed generated-plugin authority row is incomplete.");
            var formIds =
                ImmutableArray.CreateBuilder<FormId>();
            foreach (string formIdText in item.NpcFormIds)
            {
                if (string.IsNullOrWhiteSpace(formIdText) ||
                    !FormId.TryParse(
                        formIdText,
                        out FormId formId))
                    throw Invalid(
                        "A reviewed generated-plugin FormID is invalid.");
                formIds.Add(formId);
            }
            generatedPlugins.Add(
                new GeneratedPluginScanEntry(
                    new PluginName(item.Plugin),
                    new WorkspacePath(item.Path),
                    item.Author,
                    string.IsNullOrWhiteSpace(item.Sha256)
                        ? null
                        : new Sha256Hash(item.Sha256),
                    item.ReadSucceeded,
                    formIds.ToImmutable()));
        }
        var generatedSidecars =
            ImmutableArray.CreateBuilder<
                GeneratedSidecarEntry>();
        foreach (ReviewedGeneratedSidecarDocument item in
                 legacyDocument
                     ? []
                     : document.GeneratedSidecars)
        {
            if (item is null ||
                string.IsNullOrWhiteSpace(item.OriginPlugin) ||
                string.IsNullOrWhiteSpace(item.Kind) ||
                string.IsNullOrWhiteSpace(item.Variant) ||
                string.IsNullOrWhiteSpace(item.RelativePath) ||
                string.IsNullOrWhiteSpace(item.Path) ||
                string.IsNullOrWhiteSpace(item.Sha256) ||
                !TryParseGeneratedSidecarKind(
                    item.Kind,
                    out GeneratedSidecarKind kind) ||
                !TryParseGeneratedSidecarVariant(
                    item.Variant,
                    out GeneratedSidecarVariant variant))
                throw Invalid(
                    "A reviewed generated-sidecar authority row is incomplete.");
            FormId? formId = null;
            if (!string.IsNullOrWhiteSpace(item.FormId))
            {
                if (!FormId.TryParse(
                        item.FormId,
                        out FormId parsed))
                    throw Invalid(
                        "A reviewed generated-sidecar FormID is invalid.");
                formId = parsed;
            }
            generatedSidecars.Add(
                new GeneratedSidecarEntry(
                    new PluginName(item.OriginPlugin),
                    kind,
                    variant,
                    new AssetPath(item.RelativePath),
                    new WorkspacePath(item.Path),
                    formId,
                    item.Size,
                    new Sha256Hash(item.Sha256)));
        }
        var intake = new ReviewedGameIntake(
            edition,
            workspaceRoot,
            dataRoot,
            loadOrderPath,
            outputRoot,
            loadOrderHash,
            plugins.ToImmutable(),
            bodySidecars.ToImmutable(),
            generatedPlugins.ToImmutable(),
            generatedSidecars.ToImmutable(),
            document.AssetProviderCount,
            new Sha256Hash(document.AssetIndexFingerprint),
            new Sha256Hash(document.IntakeFingerprint),
            false);
        ValidateReviewedIntakeAuxiliaryAuthority(
            intake);
        return intake;
    }

    private ReviewedGameIntake ValidateReviewedIntakeValue(
        ReviewedGameIntake value)
    {
        if (value.Plugins.IsDefault ||
            value.BodySidecars.IsDefault ||
            value.GeneratedPlugins.IsDefault ||
            value.GeneratedSidecars.IsDefault ||
            value.Edition !=
                GameEdition.SkyrimSpecialEdition ||
            value.RuntimeAuthority ||
            value.AssetProviderCount < 0 ||
            value.Plugins.Length is < 1 or > 512)
            throw Invalid(
                "The reviewed intake value is not accepted Skyrim SE authority.");
        if (value.WorkspaceRoot != boundary.WorkspaceRoot)
            throw new UnauthorizedAccessException(
                "The reviewed intake workspace root does not equal the configured K-local authority.");
        boundary.RequireExistingDirectory(
            value.WorkspaceRoot,
            "reviewed intake workspace root");
        if (!value.DataRoot.IsUnder(value.WorkspaceRoot))
            throw new UnauthorizedAccessException(
                "The reviewed intake Data root is outside its workspace.");
        boundary.RequireExistingDirectory(
            value.DataRoot,
            "reviewed intake Data root");
        if (!value.LoadOrderPath.IsUnder(value.WorkspaceRoot))
            throw new UnauthorizedAccessException(
                "The reviewed intake load order is outside its workspace.");
        RequireExactFileHash(
            value.LoadOrderPath,
            value.LoadOrderHash,
            "reviewed intake load order");
        if (!value.OutputRoot.IsUnder(value.WorkspaceRoot))
            throw new UnauthorizedAccessException(
                "The reviewed intake output root is outside its workspace.");
        boundary.RequireNewDirectory(
            value.OutputRoot,
            "reviewed intake output root");

        var names = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        int previousOrder = -1;
        for (int index = 0;
             index < value.Plugins.Length;
             index++)
        {
            PluginClosureReviewEntry plugin =
                value.Plugins[index];
            if (plugin.Masters.IsDefault)
                throw Invalid(
                    "A reviewed intake plugin master list is missing.");
            var expectedPath = new WorkspacePath(Path.Combine(
                value.DataRoot.Value,
                plugin.Plugin.Value));
            if (plugin.Order < 0 ||
                plugin.Order <= previousOrder ||
                !names.Add(plugin.Plugin.Value) ||
                !plugin.Exists ||
                !plugin.ReadSucceeded ||
                plugin.SourceHash is null ||
                plugin.Path != expectedPath)
                throw Invalid(
                    "Reviewed intake plugin authority is incomplete or inconsistent.");
            previousOrder = plugin.Order;
            RequireExactFileHash(
                plugin.Path,
                plugin.SourceHash.Value,
                $"reviewed plugin {plugin.Plugin.Value}");
        }
        ValidateReviewedIntakeAuxiliaryAuthority(
            value);
        return value;
    }

    private void ValidateReviewedIntakeAuxiliaryAuthority(
        ReviewedGameIntake value)
    {
        if (value.BodySidecars.Length > 512 ||
            value.GeneratedPlugins.Length > 512 ||
            value.GeneratedSidecars.Length > 16384)
            throw Invalid(
                "Reviewed intake auxiliary authority exceeds its bounded row count.");

        var closureNames = value.Plugins
            .Select(item => item.Plugin.Value)
            .ToHashSet(
                StringComparer.OrdinalIgnoreCase);
        var bodyNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ReviewedBodySidecar sidecar in
                 value.BodySidecars)
        {
            string expectedName =
                Path.GetFileNameWithoutExtension(
                    sidecar.Plugin.Value) +
                ".bssliders";
            var expectedPath = new WorkspacePath(
                Path.Combine(
                    value.DataRoot.Value,
                    expectedName));
            if (!closureNames.Contains(
                    sidecar.Plugin.Value) ||
                !bodyNames.Add(
                    sidecar.Plugin.Value) ||
                sidecar.Path != expectedPath ||
                sidecar.NpcCount < 0)
                throw Invalid(
                    "Reviewed body-sidecar authority is incomplete or inconsistent.");
            RequireExactFileHash(
                sidecar.Path,
                sidecar.SourceHash,
                $"reviewed body sidecar {expectedName}");
        }

        var generatedNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (GeneratedPluginScanEntry plugin in
                 value.GeneratedPlugins)
        {
            var expectedPath = new WorkspacePath(
                Path.Combine(
                    value.DataRoot.Value,
                    plugin.Plugin.Value));
            if (!generatedNames.Add(
                    plugin.Plugin.Value) ||
                plugin.Path != expectedPath ||
                string.IsNullOrWhiteSpace(plugin.Author) ||
                plugin.Sha256 is null ||
                !plugin.ReadSucceeded ||
                plugin.NpcFormIds.IsDefault)
                throw Invalid(
                    "Reviewed generated-plugin authority is incomplete or inconsistent.");
            uint previous = 0;
            bool first = true;
            foreach (FormId formId in plugin.NpcFormIds)
            {
                if (!first &&
                    formId.Value <= previous)
                    throw Invalid(
                        "Reviewed generated-plugin FormIDs must be distinct and strictly ascending.");
                previous = formId.Value;
                first = false;
            }
            RequireExactFileHash(
                plugin.Path,
                plugin.Sha256.Value,
                $"reviewed generated plugin {plugin.Plugin.Value}");
        }

        var sidecarPaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (GeneratedSidecarEntry sidecar in
                 value.GeneratedSidecars)
        {
            var expectedPath = new WorkspacePath(
                Path.Combine(
                    value.DataRoot.Value,
                    sidecar.RelativePath.Value.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
            if (!generatedNames.Contains(
                    sidecar.OriginPlugin.Value) ||
                !sidecarPaths.Add(
                    sidecar.RelativePath.Value) ||
                sidecar.Path != expectedPath ||
                sidecar.Size <= 0)
                throw Invalid(
                    "Reviewed generated-sidecar authority is incomplete or inconsistent.");
            byte[] bytes = boundary.ReadExactFile(
                sidecar.Path,
                512L * 1024L * 1024L,
                $"reviewed generated sidecar {sidecar.RelativePath.Value}");
            if (bytes.LongLength != sidecar.Size ||
                Hash(bytes) != sidecar.Sha256)
                throw Invalid(
                    "Reviewed generated-sidecar bytes do not match their exact authority.");
        }

        if (ReviewedGameIntakeFingerprintAuthority
                .Fingerprint(value) !=
            value.IntakeFingerprint)
            throw Invalid(
                "Reviewed intake fingerprint does not match its exact typed authority.");
    }

    private static bool TryParseGeneratedSidecarKind(
        string value,
        out GeneratedSidecarKind kind)
    {
        kind = value switch
        {
            "faceGeom" =>
                GeneratedSidecarKind.FaceGeom,
            "faceCustomizationDiffuse" =>
                GeneratedSidecarKind
                    .FaceCustomizationDiffuse,
            "faceCustomizationNormal" =>
                GeneratedSidecarKind
                    .FaceCustomizationNormal,
            "faceCustomizationSpecular" =>
                GeneratedSidecarKind
                    .FaceCustomizationSpecular,
            "faceTint" =>
                GeneratedSidecarKind.FaceTint,
            "faceDiffuse" =>
                GeneratedSidecarKind.FaceDiffuse,
            "faceNormal" =>
                GeneratedSidecarKind.FaceNormal,
            "faceDetailNeutral" =>
                GeneratedSidecarKind.FaceDetailNeutral,
            _ => default
        };
        return value is
            "faceGeom" or
            "faceCustomizationDiffuse" or
            "faceCustomizationNormal" or
            "faceCustomizationSpecular" or
            "faceTint" or
            "faceDiffuse" or
            "faceNormal" or
            "faceDetailNeutral";
    }

    private static bool TryParseGeneratedSidecarVariant(
        string value,
        out GeneratedSidecarVariant variant)
    {
        variant = value switch
        {
            "canonical" =>
                GeneratedSidecarVariant.Canonical,
            "debugSandbox" =>
                GeneratedSidecarVariant.DebugSandbox,
            _ => default
        };
        return value is
            "canonical" or
            "debugSandbox";
    }

    private void RequireExactFileHash(
        WorkspacePath path,
        Sha256Hash expected,
        string role)
    {
        byte[] bytes = boundary.ReadExactFile(
            path,
            512L * 1024L * 1024L,
            role);
        if (Hash(bytes) != expected)
            throw Invalid(
                $"The {role} hash does not match its accepted authority.");
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static InvalidDataException Invalid(string message) =>
        new(message);

    private static JsonDocumentOptions StrictDocumentOptions =>
        new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        };

    private async ValueTask<byte[]> ReadOnceAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        return await boundary.ReadExactFileAsync(
            path,
            128L * 1024L * 1024L,
            "exact JSON document",
            cancellationToken);
    }

    private sealed class FaceGeomHairRegionRoleConverter :
        JsonConverter<FaceGeomHairRegionRole>
    {
        public override FaceGeomHairRegionRole Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException(
                    "Hair-region roles must be lowercase camelCase strings.");
            return reader.GetString() switch
            {
                "preserve" => FaceGeomHairRegionRole.Preserve,
                "primary" => FaceGeomHairRegionRole.Primary,
                "accent" => FaceGeomHairRegionRole.Accent,
                _ => throw new JsonException(
                    "Hair-region role is unknown or non-canonical.")
            };
        }

        public override void Write(
            Utf8JsonWriter writer,
            FaceGeomHairRegionRole value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value switch
            {
                FaceGeomHairRegionRole.Preserve => "preserve",
                FaceGeomHairRegionRole.Primary => "primary",
                FaceGeomHairRegionRole.Accent => "accent",
                _ => throw new JsonException(
                    "Hair-region role is outside the closed enum.")
            });
    }

    private sealed class WorkspacePathConverter :
        JsonConverter<WorkspacePath>
    {
        public override WorkspacePath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException(
                    "Workspace path must be a string.");
            return new WorkspacePath(
                reader.GetString() ??
                throw new JsonException(
                    "Workspace path must be a string."));
        }

        public override void Write(
            Utf8JsonWriter writer,
            WorkspacePath value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class Sha256HashConverter :
        JsonConverter<Sha256Hash>
    {
        public override Sha256Hash Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException(
                    "SHA-256 must be a string.");
            return new Sha256Hash(
                reader.GetString() ??
                throw new JsonException(
                    "SHA-256 must be a string."));
        }

        public override void Write(
            Utf8JsonWriter writer,
            Sha256Hash value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed record ReviewedIntakeDocument(
        string SchemaVersion,
        string Edition,
        bool IsAccepted,
        string WorkspaceRoot,
        string DataRoot,
        string LoadOrderPath,
        string OutputRoot,
        string LoadOrderHash,
        string AssetIndexFingerprint,
        string IntakeFingerprint,
        ImmutableArray<ReviewedPluginDocument> Plugins,
        int BodySidecarCount,
        int GeneratedPluginCount,
        int GeneratedSidecarCount,
        ImmutableArray<ReviewedBodySidecarDocument>
            BodySidecars,
        ImmutableArray<ReviewedGeneratedPluginDocument>
            GeneratedPlugins,
        ImmutableArray<ReviewedGeneratedSidecarDocument>
            GeneratedSidecars,
        int AssetProviderCount,
        bool RuntimeAuthority,
        ImmutableArray<ReviewedDiagnosticDocument> Diagnostics);

    private sealed record ReviewedPluginDocument(
        string Plugin,
        int Order,
        bool Active,
        bool Requested,
        bool RequiredMaster,
        string SourceHash,
        ImmutableArray<string> Masters);

    private sealed record ReviewedBodySidecarDocument(
        string Plugin,
        string Path,
        string SourceHash,
        string CanonicalHash,
        int NpcCount);

    private sealed record ReviewedGeneratedPluginDocument(
        string Plugin,
        string Path,
        string Author,
        string? Sha256,
        bool ReadSucceeded,
        ImmutableArray<string> NpcFormIds);

    private sealed record ReviewedGeneratedSidecarDocument(
        string OriginPlugin,
        string Kind,
        string Variant,
        string RelativePath,
        string Path,
        string? FormId,
        long Size,
        string Sha256);

    private sealed record ReviewedDiagnosticDocument(
        string Code,
        string Severity,
        string Message);
}
