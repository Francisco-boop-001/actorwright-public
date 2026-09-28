using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Strict wire codec for Finish Core request/proposal documents.  It keeps
/// paths project-relative on the wire and emits one deterministic UTF-8/LF
/// representation for hash binding.
/// </summary>
public static class SkyrimNpcFinishCoreDocumentCodec
{
    public const string CanonicalSelectedManifestPath =
        "Data/NPCManager/Evidence/selected-preset-dependencies.json";

    public const string CanonicalPromotedOutputBindingPath =
        "Data/NPCManager/Evidence/finish-core-promoted-output-binding.json";

    private static readonly AssetPath CanonicalSelectedManifest =
        new(CanonicalSelectedManifestPath);

    private static readonly JsonDocumentOptions StrictDocumentOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32
    };

    public static SkyrimNpcFinishCoreRequest ParseRequest(
        ReadOnlySpan<byte> bytes,
        WorkspacePath projectRoot)
    {
        JsonElement root = ParseRoot(bytes);
        string schema = RequiredString(root, "schema");
        bool legacy = schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier;
        bool current = schema == SkyrimNpcFinishCoreRequest.SchemaIdentifier;
        bool external = schema == SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier;
        bool policy = schema == SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier;
        if (!legacy && !current && !external && !policy)
            throw new InvalidDataException(
                $"Finish Core request schema must be '{SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier}', '{SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier}', '{SkyrimNpcFinishCoreRequest.SchemaIdentifier}' or '{SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier}'.");
        RequireClosedMembers(
            root,
            "request",
            external || policy
                ? [
                    "schema",
                    "source",
                    "actor",
                    "authorities",
                    "followerPolicy",
                    "aiPolicy",
                    "outfitPolicy",
                    "inventoryPolicy",
                    "sandboxAuthority",
                    "output"
                ]
                : legacy
                ? [
                "schema",
                "source",
                "actor",
                "authorities",
                "followerPolicy",
                "outfitPolicy",
                "inventoryPolicy",
                "sandboxAuthority",
                "output"
            ]
                : [
                    "schema",
                    "source",
                    "actor",
                    "authorities",
                    "followerPolicy",
                    "aiPolicy",
                    "outfitPolicy",
                    "inventoryPolicy",
                    "sandboxAuthority",
                    "output"
                ],
            legacy ? ["aiPolicy", "outfitRacePolicy"] : external || policy ? ["combatPolicy", "perkPolicy", "outfitRacePolicy"] : ["outfitRacePolicy"]);

        RequireKnownMembers(
            root.GetProperty("source"),
            "source",
            "packageRoot",
            "packageManifest",
            "packageManifestSha256",
            "packageTreeSha256",
            "pluginPath",
            "plugin",
            "pluginSha256");
        RequireKnownMembers(root.GetProperty("actor"), "actor", "editorId", "formId");
        if (external)
        {
            RequireClosedMembers(
                root.GetProperty("authorities"),
                "authorities",
                [
                    "bodyRoute",
                    "providers",
                    "additionalMasters",
                    "actorAssemblySha256",
                    "bodyOwnerSha256",
                    "protectedAppearanceTreeSha256",
                    "externalHeadParts"
                ],
                []);
        }
        else if (legacy)
        {
            RequireKnownMembers(
                root.GetProperty("authorities"),
                "authorities",
                "bodyRoute",
                "providers",
                "actorAssemblySha256",
                "bodyOwnerSha256",
                "protectedAppearanceTreeSha256");
        }
        else
        {
            RequireKnownMembers(
                root.GetProperty("authorities"),
                "authorities",
                "bodyRoute",
                "providers",
                "additionalMasters",
                "actorAssemblySha256",
                "bodyOwnerSha256",
                "protectedAppearanceTreeSha256");
        }
        RequireKnownMembers(
            root.GetProperty("followerPolicy"),
            "followerPolicy",
            "recruitable",
            "defensiveOnly",
            "potentialFollowerFaction",
            "currentFollowerFaction",
            "relationshipRank");
        if (root.TryGetProperty("aiPolicy", out JsonElement aiPolicy))
        {
            if (legacy)
            {
                RequireExactMembers(
                    aiPolicy,
                    "aiPolicy",
                    "aggression",
                    "confidence",
                    "energy",
                    "morality",
                    "assistance");
            }
            else
            {
                RequireExactMembers(
                    aiPolicy,
                    "aiPolicy",
                    "aggression",
                    "confidence",
                    "energy",
                    "morality",
                    "assistance",
                    "mood");
            }
        }
        RequireKnownMembers(
            root.GetProperty("outfitPolicy"),
            "outfitPolicy",
            "policy",
            "existingOutfit",
            "armorItems");
        RequireKnownMembers(
            root.GetProperty("inventoryPolicy"),
            "inventoryPolicy",
            "policy",
            "expectedSourceItems",
            "desiredItems");
        RequireKnownMembers(
            root.GetProperty("sandboxAuthority"),
            "sandboxAuthority",
            "copiedMaster",
            "copiedMasterSha256",
            "template",
            "templateEditorId",
            "rawRecordDigest");
        RequireKnownMembers(
            root.GetProperty("output"),
            "output",
            "root",
            "archive",
            "pluginFileName");

        JsonElement source = root.GetProperty("source");
        JsonElement actor = root.GetProperty("actor");
        JsonElement authorities = root.GetProperty("authorities");
        JsonElement follower = root.GetProperty("followerPolicy");
        JsonElement outfit = root.GetProperty("outfitPolicy");
        JsonElement inventory = root.GetProperty("inventoryPolicy");
        JsonElement sandbox = root.GetProperty("sandboxAuthority");
        JsonElement output = root.GetProperty("output");
        var request = new SkyrimNpcFinishCoreRequest
        {
            Schema = schema,
            Source = new SkyrimNpcFinishCoreSource
            {
                PackageRoot = ParsePath(source, "packageRoot", projectRoot),
                PackageManifest = ParsePath(source, "packageManifest", projectRoot),
                PackageManifestSha256 = ParseHash(source, "packageManifestSha256"),
                PackageTreeSha256 = ParseHash(source, "packageTreeSha256"),
                PluginPath = ParsePath(source, "pluginPath", projectRoot),
                Plugin = ParsePlugin(source, "plugin"),
                PluginSha256 = ParseHash(source, "pluginSha256")
            },
            Actor = new SkyrimNpcFinishCoreActor
            {
                EditorId = ParseEditorId(actor, "editorId"),
                FormId = ParseFormId(actor, "formId")
            },
            Authorities = new SkyrimNpcFinishCoreAuthorities
            {
                BodyRoute = ParseEnum<SkyrimNpcFinishCoreBodyRoute>(authorities, "bodyRoute"),
                Providers = ParseProviders(authorities, projectRoot),
                AdditionalMasters = legacy
                    ? ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding>.Empty
                    : ParseAdditionalMasters(authorities, projectRoot),
                ActorAssemblySha256 = ParseHash(authorities, "actorAssemblySha256"),
                BodyOwnerSha256 = ParseHash(authorities, "bodyOwnerSha256"),
                ProtectedAppearanceTreeSha256 = ParseHash(authorities, "protectedAppearanceTreeSha256"),
                ExternalHeadParts = external && authorities.TryGetProperty("externalHeadParts", out _)
                    ? ParseExternalHeadPartAuthority(authorities, "externalHeadParts")
                    : null
            },
            FollowerPolicy = new SkyrimNpcFinishCoreFollowerPolicy
            {
                Recruitable = RequiredBoolean(follower, "recruitable"),
                DefensiveOnly = RequiredBoolean(follower, "defensiveOnly"),
                PotentialFollowerFaction = ParseForm(follower, "potentialFollowerFaction"),
                CurrentFollowerFaction = ParseForm(follower, "currentFollowerFaction"),
                RelationshipRank = RequiredString(follower, "relationshipRank")
            },
            AiPolicy = root.TryGetProperty("aiPolicy", out aiPolicy)
                ? new SkyrimNpcFinishCoreAiPolicy
                {
                    Aggression = ParseEnum<SkyrimNpcFinishCoreAggression>(aiPolicy, "aggression"),
                    Confidence = ParseEnum<SkyrimNpcFinishCoreConfidence>(aiPolicy, "confidence"),
                    Energy = RequiredByte(aiPolicy, "energy", 0, 100),
                    Morality = ParseEnum<SkyrimNpcFinishCoreMorality>(aiPolicy, "morality"),
                    Assistance = ParseEnum<SkyrimNpcFinishCoreAssistance>(aiPolicy, "assistance"),
                    Mood = current || external || policy
                        ? ParseEnum<SkyrimNpcFinishCoreMood>(aiPolicy, "mood")
                        : ParseOptionalEnum<SkyrimNpcFinishCoreMood>(aiPolicy, "mood")
                }
                : null,
            CombatPolicy = ParseCombatPolicy(root),
            PerkPolicy = ParsePerkPolicy(root),
            OutfitRacePolicy = !root.TryGetProperty("outfitRacePolicy", out _) ? SkyrimNpcFinishCoreOutfitRacePolicy.Refuse
                : RequiredString(root, "outfitRacePolicy") switch
                {
                    "refuse" => SkyrimNpcFinishCoreOutfitRacePolicy.Refuse,
                    "clone" => SkyrimNpcFinishCoreOutfitRacePolicy.Clone,
                    _ => throw new InvalidDataException("Finish Core outfitRacePolicy must be refuse or clone.")
                },
            OutfitPolicy = new SkyrimNpcFinishCoreOutfitPolicyDocument
            {
                Policy = ParseEnum<SkyrimNpcFinishCoreOutfitPolicy>(outfit, "policy"),
                ExistingOutfit = ParseForm(outfit, "existingOutfit"),
                ArmorItems = ParseForms(outfit, "armorItems")
            },
            InventoryPolicy = new SkyrimNpcFinishCoreInventoryPolicyDocument
            {
                Policy = ParseEnum<SkyrimNpcFinishCoreInventoryPolicy>(inventory, "policy"),
                ExpectedSourceItems = ParseStrings(inventory, "expectedSourceItems"),
                DesiredItems = ParseStrings(inventory, "desiredItems")
            },
            SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
            {
                CopiedMaster = ParsePath(sandbox, "copiedMaster", projectRoot),
                CopiedMasterSha256 = ParseHash(sandbox, "copiedMasterSha256"),
                Template = ParseRequiredForm(sandbox, "template"),
                TemplateEditorId = RequiredString(sandbox, "templateEditorId"),
                RawRecordDigest = ParseHash(sandbox, "rawRecordDigest")
            },
            Output = new SkyrimNpcFinishCoreOutput
            {
                Root = ParsePath(output, "root", projectRoot),
                Archive = ParsePath(output, "archive", projectRoot),
                PluginFileName = RequiredString(output, "pluginFileName")
            }
        };
        if (external && request.Authorities.ExternalHeadParts is null)
            throw new InvalidDataException(
                "A v3 Finish Core request must carry externalHeadParts authority.");
        if (external && request.Authorities.Providers.Any(provider =>
                provider.Path is WorkspacePath path &&
                path.Value.EndsWith(
                    CanonicalSelectedManifestPath.Replace('/', Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                "External head-part authority must not be placed in Authorities.Providers.");
        return request;
    }

    public static SkyrimNpcFinishCoreProposal ParseProposal(
        ReadOnlySpan<byte> bytes,
        WorkspacePath projectRoot)
    {
        JsonElement root = ParseRoot(bytes);
        string schema = RequiredString(root, "schema");
        bool legacy = schema == SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier;
        bool current = schema == SkyrimNpcFinishCoreProposal.SchemaIdentifier;
        bool external = schema == SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier;
        bool policy = schema == SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier;
        if (!legacy && !current && !external && !policy)
            throw new InvalidDataException(
                $"The Finish Core proposal schema must be '{SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier}', '{SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier}', '{SkyrimNpcFinishCoreProposal.SchemaIdentifier}' or '{SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier}'.");
        RequireClosedMembers(
            root,
            "proposal",
            [
                "schema",
                "status",
                "existingRecordChanges",
                "newRecords",
                "appendedMasters",
                "forbiddenRecordCounts",
                "nextFormId",
                "sourceTes4Flags",
                "masterOrder",
                "packageFiles",
                "runtimeAuthority",
                "request"
            ],
            external
                ? ["requestSha256", "proposalSha256", "externalHeadParts"]
                : ["requestSha256", "proposalSha256"]);
        bool runtimeAuthority = RequiredBoolean(root, "runtimeAuthority");
        ValidateExternalProposalRuntimeAuthority(external, runtimeAuthority);
        if (!root.TryGetProperty("request", out JsonElement requestElement) ||
            requestElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The Finish Core proposal request is required.");
        SkyrimNpcFinishCoreRequest request = ParseRequest(
            Encoding.UTF8.GetBytes(requestElement.GetRawText()), projectRoot);
        if ((request.Authorities.ExternalHeadParts is not null) != root.TryGetProperty("externalHeadParts", out _))
            throw new InvalidDataException("Finish Core proposal and request must carry the same external authority mode.");
        bool requestLegacy = request.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier;
        bool requestCurrent = request.Schema == SkyrimNpcFinishCoreRequest.SchemaIdentifier;
        bool requestExternal = request.Schema == SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier;
        bool requestPolicy = request.Schema == SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier;
        if ((legacy && !requestLegacy) ||
            (current && !requestCurrent) ||
            (external && !requestExternal) ||
            (policy && !requestPolicy))
            throw new InvalidDataException(
                "Finish Core proposal and embedded request schemas must be matching pairs.");
        if (!external && root.TryGetProperty("externalHeadParts", out _))
            throw new InvalidDataException(
                "External head-part proposal authority requires the v3 schema.");
        return new SkyrimNpcFinishCoreProposal
        {
            Schema = schema,
            RequestSha256 = ParseHash(root, "requestSha256"),
            ProposalSha256 = ParseHash(root, "proposalSha256"),
            Request = request,
            Status = ParseFinishCoreStatus(root, "status", external),
            ExistingRecordChanges = ParseStrings(root, "existingRecordChanges"),
            NewRecords = ParseStrings(root, "newRecords"),
            AppendedMasters = ParseStrings(root, "appendedMasters"),
            ForbiddenRecordCounts = ParseStrings(root, "forbiddenRecordCounts"),
            NextFormId = RequiredFormId(root, "nextFormId"),
            SourceTes4Flags = RequiredUInt32(root, "sourceTes4Flags"),
            MasterOrder = ParseStrings(root, "masterOrder"),
            PackageFiles = ParseStrings(root, "packageFiles"),
            RuntimeAuthority = runtimeAuthority,
            ExternalHeadParts = external && root.TryGetProperty("externalHeadParts", out _)
                ? ParseExternalHeadPartProposalAuthority(
                    root.GetProperty("externalHeadParts"),
                    ParseFinishCoreStatus(root, "status", external))
                : null
        };
    }

    public static byte[] SerializeRequest(
        SkyrimNpcFinishCoreRequest request,
        WorkspacePath projectRoot)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool legacy = request.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier;
        bool external = request.Schema == SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier;
        bool policy = request.Schema == SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier;
        if (!legacy && !external && !policy && request.Schema != SkyrimNpcFinishCoreRequest.SchemaIdentifier)
            throw new InvalidDataException(
                $"Finish Core request schema must be '{SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier}', '{SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier}', '{SkyrimNpcFinishCoreRequest.SchemaIdentifier}' or '{SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier}'.");
        if (legacy && request.Authorities.AdditionalMasters.Length != 0)
            throw new InvalidDataException(
                "A v1 Finish Core request must not carry additional-master authority.");
        if (legacy && request.AiPolicy?.Mood is not null)
            throw new InvalidDataException(
                "A v1 Finish Core request must not carry aiPolicy mood.");
        if (!legacy && request.AiPolicy is not { Mood: not null })
            throw new InvalidDataException(
                $"A {(external ? "v3" : policy ? "v4" : "v2")} Finish Core request must carry an aiPolicy mood.");
        if (!external && !policy && (request.CombatPolicy is not null || !request.PerkPolicy.IsDefault))
            throw new InvalidDataException("Combat and perk policy require a v3 or v4 Finish Core request.");
        if (external && request.Authorities.ExternalHeadParts is null)
            throw new InvalidDataException("A v3 Finish Core request must carry externalHeadParts authority.");
        if (!external && request.Authorities.ExternalHeadParts is not null)
            throw new InvalidDataException(
                "External head-part authority requires a v3 Finish Core request.");
        JsonObject root = new()
        {
            ["schema"] = request.Schema,
            ["source"] = SerializeSource(request.Source, projectRoot),
            ["actor"] = SerializeActor(request.Actor),
            ["authorities"] = SerializeAuthorities(
                request.Authorities,
                projectRoot,
                includeAdditionalMasters: !legacy,
                includeExternalHeadParts: external && request.Authorities.ExternalHeadParts is not null),
            ["followerPolicy"] = SerializeFollowerPolicy(request.FollowerPolicy),
            ["outfitPolicy"] = SerializeOutfitPolicy(request.OutfitPolicy),
            ["inventoryPolicy"] = SerializeInventoryPolicy(request.InventoryPolicy),
            ["sandboxAuthority"] = SerializeSandboxAuthority(request.SandboxAuthority, projectRoot),
            ["output"] = SerializeOutput(request.Output, projectRoot)
        };
        if (request.AiPolicy is { } aiPolicy)
            root["aiPolicy"] = SerializeAiPolicy(aiPolicy, includeMood: !legacy);
        if (request.OutfitRacePolicy != SkyrimNpcFinishCoreOutfitRacePolicy.Refuse)
            root["outfitRacePolicy"] = request.OutfitRacePolicy == SkyrimNpcFinishCoreOutfitRacePolicy.Clone
                ? "clone" : throw new InvalidDataException("Invalid Finish Core outfitRacePolicy; expected refuse or clone.");
        if (request.CombatPolicy is { } combat)
        {
            root["combatPolicy"] = new JsonObject { ["seedLocalStyle"] = combat.SeedLocalStyle };
            if (combat.Profile is { } profile)
                root["combatPolicy"]!["profile"] = profile switch
                {
                    SkyrimNpcFinishCoreCombatProfile.Defensive => "defensive",
                    SkyrimNpcFinishCoreCombatProfile.RangedFirst => "rangedFirst",
                    SkyrimNpcFinishCoreCombatProfile.MeleeFirst => "meleeFirst",
                    _ => throw new InvalidDataException("Invalid Finish Core combat profile.")
                };
        }
        if (!request.PerkPolicy.IsDefault)
        {
            ValidatePerks(request.PerkPolicy);
            root["perkPolicy"] = new JsonArray(request.PerkPolicy.Select(perk => (JsonNode)new JsonObject
            {
                ["form"] = perk.Form.ToString(), ["rank"] = perk.Rank
            }).ToArray());
        }
        return CanonicalizeNode(root);
    }

    public static byte[] CanonicalizeRequest(
        ReadOnlySpan<byte> bytes,
        WorkspacePath projectRoot)
    {
        SkyrimNpcFinishCoreRequest request = ParseRequest(bytes, projectRoot);
        return SerializeRequest(request, projectRoot);
    }

    public static byte[] SerializeProposal(
        SkyrimNpcFinishCoreProposal proposal,
        WorkspacePath projectRoot)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        bool legacy = proposal.Schema == SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier;
        bool current = proposal.Schema == SkyrimNpcFinishCoreProposal.SchemaIdentifier;
        bool external = proposal.Schema == SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier;
        bool policy = proposal.Schema == SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier;
        if (!legacy && !current && !external && !policy)
            throw new InvalidDataException(
                $"The Finish Core proposal schema must be '{SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier}', '{SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier}', '{SkyrimNpcFinishCoreProposal.SchemaIdentifier}' or '{SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier}'.");
        if (proposal.Request is null)
            throw new InvalidDataException(
                "The Finish Core proposal request is required.");
        SkyrimNpcFinishCoreRequest embeddedRequest = proposal.Request;
        bool requestLegacy = embeddedRequest.Schema ==
            SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier;
        bool requestCurrent = embeddedRequest.Schema ==
            SkyrimNpcFinishCoreRequest.SchemaIdentifier;
        bool requestExternal = embeddedRequest.Schema ==
            SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier;
        bool requestPolicy = embeddedRequest.Schema ==
            SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier;
        if ((legacy && !requestLegacy) ||
            (current && !requestCurrent) ||
            (external && !requestExternal) ||
            (policy && !requestPolicy))
            throw new InvalidDataException(
                "Finish Core proposal and embedded request schemas must be matching pairs.");
        if (!external && proposal.ExternalHeadParts is not null)
            throw new InvalidDataException(
                "External head-part proposal authority requires the v3 schema.");
        if ((embeddedRequest.Authorities.ExternalHeadParts is not null) != (proposal.ExternalHeadParts is not null))
            throw new InvalidDataException("Finish Core proposal and request must carry the same external authority mode.");
        ValidateExternalProposalRuntimeAuthority(external, proposal.RuntimeAuthority);
        if (!external && proposal.Status ==
            SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired)
            throw new InvalidDataException(
                "StaticPassInstallDependencyRequired is only valid in Finish Core v3 proposals.");
        JsonObject root = new()
        {
            ["schema"] = proposal.Schema,
            ["requestSha256"] = proposal.RequestSha256?.Value,
            ["proposalSha256"] = proposal.ProposalSha256?.Value,
            ["status"] = proposal.Status.ToString(),
            ["existingRecordChanges"] = new JsonArray(
                proposal.ExistingRecordChanges.Select(value => JsonValue.Create(value)).ToArray()),
            ["newRecords"] = new JsonArray(
                proposal.NewRecords.Select(value => JsonValue.Create(value)).ToArray()),
            ["appendedMasters"] = new JsonArray(
                proposal.AppendedMasters.Select(value => JsonValue.Create(value)).ToArray()),
            ["forbiddenRecordCounts"] = new JsonArray(
                proposal.ForbiddenRecordCounts.Select(value => JsonValue.Create(value)).ToArray()),
            ["nextFormId"] = proposal.NextFormId.ToString(),
            ["sourceTes4Flags"] = proposal.SourceTes4Flags,
            ["masterOrder"] = new JsonArray(
                proposal.MasterOrder.Select(value => JsonValue.Create(value)).ToArray()),
            ["packageFiles"] = new JsonArray(
                proposal.PackageFiles.Select(value => JsonValue.Create(value)).ToArray()),
            ["runtimeAuthority"] = proposal.RuntimeAuthority
        };
        if (proposal.ExternalHeadParts is not null)
        {
            root["externalHeadParts"] = SerializeExternalHeadPartProposalAuthority(
                proposal.ExternalHeadParts!,
                proposal.Status);
        }
        using JsonDocument request = JsonDocument.Parse(
            SerializeRequest(embeddedRequest, projectRoot));
        root["request"] = JsonNode.Parse(request.RootElement.GetRawText());
        return CanonicalizeNode(root);
    }

    public static byte[] RemoveProposalHash(ReadOnlySpan<byte> proposalBytes)
    {
        using JsonDocument document = JsonDocument.Parse(
            proposalBytes.ToArray(),
            StrictDocumentOptions);
        JsonNode? node = JsonNode.Parse(document.RootElement.GetRawText());
        if (node is not JsonObject root)
            throw new InvalidDataException("Finish Core proposal must be an object.");
        root.Remove("proposalSha256");
        return CanonicalizeNode(root);
    }

    public static Sha256Hash HashProposalWithoutSelf(
        ReadOnlySpan<byte> proposalBytes) =>
        new(Convert.ToHexString(
            SHA256.HashData(RemoveProposalHash(proposalBytes))));

    public static Sha256Hash HashRequest(
        SkyrimNpcFinishCoreRequest request,
        WorkspacePath projectRoot) =>
        new(Convert.ToHexString(SHA256.HashData(
            SerializeRequest(request, projectRoot))));

    public static byte[] SerializeManifest(
        SkyrimNpcFinishCoreManifest manifest,
        WorkspacePath projectRoot) =>
        CanonicalizeNode(BuildManifestNode(manifest, projectRoot));

    private static JsonObject BuildManifestNode(
        SkyrimNpcFinishCoreManifest manifest,
        WorkspacePath projectRoot)
    {
        bool external = manifest.Schema ==
            SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier;
        if (!external && manifest.Schema != SkyrimNpcFinishCoreManifest.SchemaIdentifier)
            throw new InvalidDataException(
                $"The Finish Core manifest schema must be '{SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier}' or '{SkyrimNpcFinishCoreManifest.SchemaIdentifier}'.");
        if (external && manifest.ExternalHeadParts is null)
            throw new InvalidDataException(
                "A v2 Finish Core manifest must carry external head-part authority.");
        if (!external && manifest.ExternalHeadParts is not null)
            throw new InvalidDataException(
                "External head-part manifest authority requires the v2 schema.");
        ValidateExternalManifestRuntimeVisualAuthority(
            external,
            manifest.RuntimeAuthority,
            manifest.VisualAuthority);

        var root = new JsonObject
        {
            ["schema"] = manifest.Schema,
            ["plugin"] = manifest.Plugin?.Value,
            ["pluginSha256"] = manifest.PluginSha256?.Value,
            ["baseNpc"] = manifest.BaseNpc?.ToString(),
            ["requestSha256"] = manifest.RequestSha256?.Value,
            ["proposalSha256"] = manifest.ProposalSha256?.Value,
            ["placementIncluded"] = manifest.PlacementIncluded,
            ["runtimeAuthority"] = manifest.RuntimeAuthority,
            ["visualAuthority"] = manifest.VisualAuthority,
            ["packageRoot"] = ToWirePath(manifest.PackageRoot, projectRoot, "packageRoot"),
            ["archive"] = ToWirePath(manifest.Archive, projectRoot, "archive"),
            ["archiveSha256"] = manifest.ArchiveSha256?.Value,
            ["sourcePackageTreeSha256"] = manifest.SourcePackageTreeSha256?.Value,
            ["packageTreeSha256"] = manifest.PackageTreeSha256?.Value,
            ["runtimeIdentity"] = new JsonObject
            {
                ["baseNpc"] = manifest.RuntimeIdentity.BaseNpc?.ToString(),
                ["placedReference"] = manifest.RuntimeIdentity.PlacedReference?.ToString(),
                ["placementIncluded"] = manifest.RuntimeIdentity.PlacementIncluded
            },
            ["evidence"] = new JsonObject
            {
                ["files"] = new JsonArray(manifest.Evidence.Files.Select(file =>
                    (JsonNode)new JsonObject
                    {
                        ["path"] = file.Path.Value,
                        ["byteLength"] = file.ByteLength,
                        ["sha256"] = file.Sha256.Value
                    }).ToArray()),
                ["packageTreeSha256"] = manifest.Evidence.PackageTreeSha256?.Value,
                ["sourcePackageTreeSha256"] = manifest.Evidence.SourcePackageTreeSha256?.Value
            }
        };
        if (!manifest.Evidence.Inherited.IsDefaultOrEmpty)
            root["evidence"]!["inherited"] = new JsonArray(manifest.Evidence.Inherited.Select(file =>
                (JsonNode)new JsonObject
                {
                    ["path"] = file.Path.Value,
                    ["sourcePath"] = file.SourcePath.Value,
                    ["byteLength"] = file.ByteLength,
                    ["sha256"] = file.Sha256.Value
                }).ToArray());
        if (external)
            root["externalHeadParts"] = SerializeExternalHeadPartManifestAuthority(
                manifest.ExternalHeadParts!);
        return root;
    }

    public static byte[] SerializeVerification(
        SkyrimNpcFinishCoreVerification verification,
        WorkspacePath projectRoot) =>
        CanonicalizeNode(BuildVerificationNode(verification));

    private static JsonObject BuildVerificationNode(
        SkyrimNpcFinishCoreVerification verification)
    {
        bool external = verification.Schema ==
            SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier;
        if (!external && verification.Schema != SkyrimNpcFinishCoreVerification.SchemaIdentifier)
            throw new InvalidDataException(
                $"The Finish Core verification schema must be '{SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier}' or '{SkyrimNpcFinishCoreVerification.SchemaIdentifier}'.");
        if (external && verification.ExternalHeadParts is null)
            throw new InvalidDataException(
                "A v2 Finish Core verification must carry external head-part authority.");
        if (!external && verification.ExternalHeadParts is not null)
            throw new InvalidDataException(
                "External head-part verification authority requires the v2 schema.");
        if (!external && verification.Status ==
            SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired)
            throw new InvalidDataException(
                "StaticPassInstallDependencyRequired is only valid in Finish Core v2 verification.");
        if (external && (verification.RuntimeAuthority || verification.VisualAuthority))
            throw new InvalidDataException(
                "External Finish Core verification cannot claim runtime or visual authority.");

        var root = new JsonObject
        {
            ["schema"] = verification.Schema,
            ["status"] = verification.Status.ToString(),
            ["verified"] = verification.Verified,
            ["placementIncluded"] = verification.PlacementIncluded,
            ["runtimeAuthority"] = verification.RuntimeAuthority,
            ["visualAuthority"] = verification.VisualAuthority,
            ["pluginSha256"] = verification.PluginSha256?.Value,
            ["packageTreeSha256"] = verification.PackageTreeSha256?.Value,
            ["sourcePackageTreeSha256"] = verification.SourcePackageTreeSha256?.Value,
            ["archiveSha256"] = verification.ArchiveSha256?.Value,
            ["typedForbiddenCounts"] = new JsonObject(
                verification.TypedForbiddenCounts.OrderBy(row => row.Key, StringComparer.Ordinal)
                    .Select(row => KeyValuePair.Create(row.Key, (JsonNode?)JsonValue.Create(row.Value)))),
            ["rawForbiddenCounts"] = new JsonObject(
                verification.RawForbiddenCounts.OrderBy(row => row.Key, StringComparer.Ordinal)
                    .Select(row => KeyValuePair.Create(row.Key, (JsonNode?)JsonValue.Create(row.Value)))),
            ["runtimeIdentity"] = new JsonObject
            {
                ["baseNpc"] = verification.RuntimeIdentity.BaseNpc?.ToString(),
                ["placedReference"] = verification.RuntimeIdentity.PlacedReference?.ToString(),
                ["placementIncluded"] = verification.RuntimeIdentity.PlacementIncluded
            }
        };
        if (external)
            root["externalHeadParts"] = SerializeExternalHeadPartVerification(
                verification.ExternalHeadParts!);
        return root;
    }

    public static SkyrimNpcFinishCoreManifest ParseManifest(
        ReadOnlySpan<byte> bytes,
        WorkspacePath projectRoot)
    {
        JsonElement root = ParseRoot(bytes);
        string schema = RequiredString(root, "schema");
        bool external = schema == SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier;
        if (!external && schema != SkyrimNpcFinishCoreManifest.SchemaIdentifier)
            throw new InvalidDataException(
                $"The Finish Core manifest schema must be '{SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier}' or '{SkyrimNpcFinishCoreManifest.SchemaIdentifier}'.");
        RequireClosedMembers(
            root,
            "manifest",
            [
                "schema",
                "plugin",
                "pluginSha256",
                "baseNpc",
                "requestSha256",
                "proposalSha256",
                "placementIncluded",
                "runtimeAuthority",
                "visualAuthority",
                "packageRoot",
                "archive",
                "archiveSha256",
                "sourcePackageTreeSha256",
                "packageTreeSha256",
                "runtimeIdentity",
                "evidence"
            ],
            external ? ["externalHeadParts"] : []);
        if (external && (!root.TryGetProperty("externalHeadParts", out JsonElement externalElement) ||
                         externalElement.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException(
                "A v2 Finish Core manifest must carry external head-part authority.");
        JsonElement runtimeIdentity = RequiredObject(
            root,
            "runtimeIdentity");
        RequireExactMembers(
            runtimeIdentity,
            "manifest runtime identity",
            "baseNpc",
            "placedReference",
            "placementIncluded");
        JsonElement evidence = RequiredObject(root, "evidence");
        RequireClosedMembers(
            evidence,
            "manifest evidence",
            ["files", "packageTreeSha256", "sourcePackageTreeSha256"],
            ["inherited"]);
        bool runtimeAuthority = RequiredBoolean(root, "runtimeAuthority");
        bool visualAuthority = RequiredBoolean(root, "visualAuthority");
        ValidateExternalManifestRuntimeVisualAuthority(
            external,
            runtimeAuthority,
            visualAuthority);
        return new SkyrimNpcFinishCoreManifest
        {
            Schema = schema,
            Plugin = ParsePlugin(root, "plugin"),
            PluginSha256 = ParseHash(root, "pluginSha256"),
            BaseNpc = ParseForm(root, "baseNpc"),
            RequestSha256 = ParseHash(root, "requestSha256"),
            ProposalSha256 = ParseHash(root, "proposalSha256"),
            PlacementIncluded = RequiredBoolean(root, "placementIncluded"),
            RuntimeAuthority = runtimeAuthority,
            VisualAuthority = visualAuthority,
            PackageRoot = ParsePath(root, "packageRoot", projectRoot),
            Archive = ParsePath(root, "archive", projectRoot),
            ArchiveSha256 = ParseHash(root, "archiveSha256"),
            SourcePackageTreeSha256 = ParseHash(
                root,
                "sourcePackageTreeSha256"),
            PackageTreeSha256 = ParseHash(root, "packageTreeSha256"),
            RuntimeIdentity = new SkyrimNpcFinishCoreRuntimeIdentity
            {
                BaseNpc = ParseForm(runtimeIdentity, "baseNpc"),
                PlacedReference = ParseForm(
                    runtimeIdentity,
                    "placedReference"),
                PlacementIncluded = RequiredBoolean(
                    runtimeIdentity,
                    "placementIncluded")
            },
            Evidence = new SkyrimNpcFinishCoreManifestEvidence
            {
                Files = ParseEvidenceEntries(evidence),
                Inherited = ParseInheritedEvidenceEntries(evidence),
                PackageTreeSha256 = ParseHash(
                    evidence,
                    "packageTreeSha256"),
                SourcePackageTreeSha256 = ParseHash(
                    evidence,
                    "sourcePackageTreeSha256")
            },
            ExternalHeadParts = external
                ? ParseExternalHeadPartManifestAuthority(
                    root.GetProperty("externalHeadParts"))
                : null
        };
    }

    public static byte[] CanonicalizeManifest(
        ReadOnlySpan<byte> bytes,
        WorkspacePath projectRoot) =>
        SerializeManifest(ParseManifest(bytes, projectRoot), projectRoot);

    public static SkyrimNpcFinishCoreVerification ParseVerification(
        ReadOnlySpan<byte> bytes)
    {
        JsonElement root = ParseRoot(bytes);
        string schema = RequiredString(root, "schema");
        bool external = schema == SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier;
        if (!external && schema != SkyrimNpcFinishCoreVerification.SchemaIdentifier)
            throw new InvalidDataException(
                $"The Finish Core verification schema must be '{SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier}' or '{SkyrimNpcFinishCoreVerification.SchemaIdentifier}'.");
        RequireClosedMembers(
            root,
            "verification",
            [
                "schema",
                "status",
                "verified",
                "placementIncluded",
                "runtimeAuthority",
                "visualAuthority",
                "pluginSha256",
                "packageTreeSha256",
                "sourcePackageTreeSha256",
                "archiveSha256",
                "typedForbiddenCounts",
                "rawForbiddenCounts",
                "runtimeIdentity"
            ],
            external ? ["externalHeadParts"] : []);
        if (external && (!root.TryGetProperty("externalHeadParts", out JsonElement externalElement) ||
                         externalElement.ValueKind != JsonValueKind.Object))
            throw new InvalidDataException(
                "A v2 Finish Core verification must carry external head-part authority.");
        JsonElement runtimeIdentity = RequiredObject(
            root,
            "runtimeIdentity");
        RequireExactMembers(
            runtimeIdentity,
            "verification runtime identity",
            "baseNpc",
            "placedReference",
            "placementIncluded");
        if (external &&
            (RequiredBoolean(root, "runtimeAuthority") ||
             RequiredBoolean(root, "visualAuthority")))
            throw new InvalidDataException(
                "External Finish Core verification cannot claim runtime or visual authority.");
        return new SkyrimNpcFinishCoreVerification
        {
            Schema = schema,
            Status = ParseFinishCoreStatus(root, "status", external),
            Verified = RequiredBoolean(root, "verified"),
            PlacementIncluded = RequiredBoolean(root, "placementIncluded"),
            RuntimeAuthority = RequiredBoolean(root, "runtimeAuthority"),
            VisualAuthority = RequiredBoolean(root, "visualAuthority"),
            PluginSha256 = ParseHash(root, "pluginSha256"),
            PackageTreeSha256 = ParseHash(root, "packageTreeSha256"),
            SourcePackageTreeSha256 = ParseHash(
                root,
                "sourcePackageTreeSha256"),
            ArchiveSha256 = ParseHash(root, "archiveSha256"),
            TypedForbiddenCounts = ParseCounts(
                root,
                "typedForbiddenCounts"),
            RawForbiddenCounts = ParseCounts(
                root,
                "rawForbiddenCounts"),
            RuntimeIdentity = new SkyrimNpcFinishCoreRuntimeIdentity
            {
                BaseNpc = ParseForm(runtimeIdentity, "baseNpc"),
                PlacedReference = ParseForm(
                    runtimeIdentity,
                    "placedReference"),
                PlacementIncluded = RequiredBoolean(
                    runtimeIdentity,
                    "placementIncluded")
            },
            ExternalHeadParts = external
                ? ParseExternalHeadPartVerification(root.GetProperty("externalHeadParts"))
                : null
        };
    }

    public static byte[] CanonicalizeVerification(
        ReadOnlySpan<byte> bytes,
        WorkspacePath projectRoot) =>
        SerializeVerification(ParseVerification(bytes), projectRoot);

    private static JsonObject SerializeExternalHeadPartAuthority(
        SkyrimNpcFinishCoreExternalHeadPartAuthority authority)
    {
        ValidateExternalHeadPartAuthority(authority);
        return new JsonObject
        {
            ["selectedManifestPath"] = authority.SelectedManifestPath.Value,
            ["selectedManifestSha256"] = authority.SelectedManifestSha256.Value,
            ["bindings"] = new JsonArray(authority.Bindings.Select(binding =>
                (JsonNode)new JsonObject
                {
                    ["descriptorId"] = binding.DescriptorId.Value,
                    ["faceGeomExclusionAttestationSha256"] =
                        binding.FaceGeomExclusionAttestationSha256.Value
                }).ToArray())
        };
    }

    private static SkyrimNpcFinishCoreExternalHeadPartAuthority
        ParseExternalHeadPartAuthority(JsonElement parent, string property)
    {
        JsonElement element = RequiredObject(parent, property);
        RequireExactMembers(
            element,
            "external head-part authority",
            "selectedManifestPath",
            "selectedManifestSha256",
            "bindings");
        var authority = new SkyrimNpcFinishCoreExternalHeadPartAuthority(
            ParseCanonicalAssetPath(element, "selectedManifestPath"),
            ParseRequiredHash(element, "selectedManifestSha256"),
            ParseExternalHeadPartBindings(element.GetProperty("bindings")));
        ValidateExternalHeadPartAuthority(authority);
        return authority;
    }

    private static ImmutableArray<SkyrimNpcFinishCoreExternalHeadPartBinding>
        ParseExternalHeadPartBindings(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
            throw new InvalidDataException(
                "Finish Core external head-part bindings must be a non-empty array.");
        var bindings = ImmutableArray.CreateBuilder<SkyrimNpcFinishCoreExternalHeadPartBinding>();
        foreach (JsonElement binding in element.EnumerateArray())
        {
            RequireExactMembers(
                binding,
                "external head-part binding",
                "descriptorId",
                "faceGeomExclusionAttestationSha256");
            bindings.Add(new SkyrimNpcFinishCoreExternalHeadPartBinding(
                ParseRequiredHash(binding, "descriptorId"),
                ParseRequiredHash(binding, "faceGeomExclusionAttestationSha256")));
        }
        return bindings.ToImmutable();
    }

    private static JsonObject SerializeExternalHeadPartProposalAuthority(
        SkyrimNpcFinishCoreExternalHeadPartProposalAuthority authority,
        SkyrimNpcFinishCoreStatus status)
    {
        ValidateExternalHeadPartProposalAuthority(authority, status);
        var result = SerializeExternalHeadPartAuthority(authority.Authority);
        result["verification"] = ParseExternalNode(
            ExternalHeadPartDependencyDescriptorCodec
                .SerializeInstallVerificationArtifact(authority.Verification));
        if (authority.ContextFingerprint is ExternalHeadPartInstallContextFingerprint fingerprint)
            result["contextFingerprint"] = SerializeContextFingerprint(fingerprint);
        return result;
    }

    private static SkyrimNpcFinishCoreExternalHeadPartProposalAuthority
        ParseExternalHeadPartProposalAuthority(
            JsonElement element,
            SkyrimNpcFinishCoreStatus status)
    {
        RequireClosedMembers(
            element,
            "external head-part proposal authority",
            [
                "selectedManifestPath",
                "selectedManifestSha256",
                "bindings",
                "verification"
            ],
            ["contextFingerprint"]);
        var authority = new SkyrimNpcFinishCoreExternalHeadPartAuthority(
            ParseCanonicalAssetPath(element, "selectedManifestPath"),
            ParseRequiredHash(element, "selectedManifestSha256"),
            ParseExternalHeadPartBindings(element.GetProperty("bindings")));
        ValidateExternalHeadPartAuthority(authority);
        ExternalHeadPartInstallVerificationArtifact verification =
            ParseInstallVerificationArtifact(element.GetProperty("verification"));
        ExternalHeadPartInstallContextFingerprint? fingerprint =
            ParseOptionalContextFingerprint(element, "contextFingerprint");
        var result = new SkyrimNpcFinishCoreExternalHeadPartProposalAuthority(
            authority,
            verification,
            fingerprint);
        ValidateExternalHeadPartProposalAuthority(result, status);
        return result;
    }

    private static JsonObject SerializeExternalHeadPartManifestAuthority(
        SkyrimNpcFinishCoreExternalHeadPartManifestAuthority authority)
    {
        ValidateExternalHeadPartManifestAuthority(authority);
        return new JsonObject
        {
            ["selectedManifestPath"] = authority.SelectedManifestPath.Value,
            ["selectedManifestSha256"] = authority.SelectedManifestSha256.Value,
            ["promotedOutputBindingPath"] = authority.PromotedOutputBindingPath.Value,
            ["promotedOutputBindingSha256"] = authority.PromotedOutputBindingSha256.Value,
            ["descriptors"] = new JsonArray(authority.Descriptors.Select(descriptor =>
                ParseExternalNode(ExternalHeadPartDependencyDescriptorCodec
                    .SerializeDescriptor(descriptor))).ToArray()),
            ["attestations"] = new JsonArray(authority.Attestations.Select(attestation =>
                ParseExternalNode(ExternalHeadPartDependencyDescriptorCodec
                    .SerializeAttestation(attestation))).ToArray()),
            ["verifiedInstallSnapshot"] = SerializeInstallSnapshot(
                authority.VerifiedInstallSnapshot)
        };
    }

    private static SkyrimNpcFinishCoreExternalHeadPartManifestAuthority
        ParseExternalHeadPartManifestAuthority(JsonElement element)
    {
        RequireExactMembers(
            element,
            "external head-part manifest authority",
            "selectedManifestPath",
            "selectedManifestSha256",
            "promotedOutputBindingPath",
            "promotedOutputBindingSha256",
            "descriptors",
            "attestations",
            "verifiedInstallSnapshot");
        AssetPath selectedPath = ParseCanonicalAssetPath(element, "selectedManifestPath");
        Sha256Hash selectedHash = ParseRequiredHash(element, "selectedManifestSha256");
        JsonElement descriptorsElement = RequiredArray(element, "descriptors");
        JsonElement attestationsElement = RequiredArray(element, "attestations");
        if (descriptorsElement.GetArrayLength() == 0 ||
            descriptorsElement.GetArrayLength() != attestationsElement.GetArrayLength())
            throw new InvalidDataException(
                "Finish Core external head-part descriptors and attestations must have equal non-zero counts.");
        var descriptors = ImmutableArray.CreateBuilder<ExternalHeadPartDependencyDescriptor>();
        foreach (JsonElement descriptor in descriptorsElement.EnumerateArray())
            descriptors.Add(ExternalHeadPartDependencyDescriptorCodec.ParseDescriptor(
                Encoding.UTF8.GetBytes(descriptor.GetRawText())));
        var attestations = ImmutableArray.CreateBuilder<ExternalHeadPartFaceGeomExclusionAttestation>();
        foreach (JsonElement attestation in attestationsElement.EnumerateArray())
            attestations.Add(ExternalHeadPartDependencyDescriptorCodec.ParseAttestation(
                Encoding.UTF8.GetBytes(attestation.GetRawText())));
        var authority = new SkyrimNpcFinishCoreExternalHeadPartManifestAuthority(
            selectedPath,
            selectedHash,
            descriptors.ToImmutable(),
            attestations.ToImmutable(),
            ParseInstallSnapshot(element.GetProperty("verifiedInstallSnapshot")))
        {
            PromotedOutputBindingPath = ParseCanonicalPromotedBindingPath(element),
            PromotedOutputBindingSha256 = ParseRequiredHash(element, "promotedOutputBindingSha256")
        };
        ValidateExternalHeadPartManifestAuthority(authority);
        return authority;
    }

    private static JsonObject SerializeExternalHeadPartVerification(
        SkyrimNpcFinishCoreExternalHeadPartVerification external)
    {
        ValidateExternalHeadPartVerification(external);
        return new JsonObject
        {
            ["verification"] = ParseExternalNode(
                ExternalHeadPartDependencyDescriptorCodec
                    .SerializeInstallVerificationArtifact(external.Verification))
        };
    }

    private static SkyrimNpcFinishCoreExternalHeadPartVerification
        ParseExternalHeadPartVerification(JsonElement element)
    {
        RequireExactMembers(
            element,
            "external head-part verification",
            "verification");
        var external = new SkyrimNpcFinishCoreExternalHeadPartVerification(
            ParseInstallVerificationArtifact(element.GetProperty("verification")));
        ValidateExternalHeadPartVerification(external);
        return external;
    }

    private static JsonNode ParseExternalNode(byte[] bytes) =>
        JsonNode.Parse(Encoding.UTF8.GetString(bytes)) ??
        throw new InvalidDataException("External Finish Core authority JSON was empty.");

    private static ExternalHeadPartInstallVerificationArtifact
        ParseInstallVerificationArtifact(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "Finish Core external install verification must be an object.");
        return ExternalHeadPartDependencyDescriptorCodec
            .ParseInstallVerificationArtifact(Encoding.UTF8.GetBytes(element.GetRawText()));
    }

    private static JsonObject SerializeInstallSnapshot(
        ExternalHeadPartVerifiedInstallSnapshot snapshot)
    {
        ValidateInstallSnapshot(snapshot);
        return new JsonObject
        {
            ["selectedManifestSha256"] = snapshot.SelectedManifestSha256.Value,
            ["descriptorIds"] = new JsonArray(snapshot.DescriptorIds
                .Select(id => JsonValue.Create(id.Value)).ToArray()),
            ["contextFingerprint"] = SerializeContextFingerprint(snapshot.ContextFingerprint)
        };
    }

    private static ExternalHeadPartVerifiedInstallSnapshot ParseInstallSnapshot(
        JsonElement element)
    {
        RequireExactMembers(
            element,
            "external install snapshot",
            "selectedManifestSha256",
            "descriptorIds",
            "contextFingerprint");
        var snapshot = new ExternalHeadPartVerifiedInstallSnapshot(
            ParseRequiredHash(element, "selectedManifestSha256"),
            ParseHashArray(element.GetProperty("descriptorIds"), "external snapshot descriptorIds"),
            ParseContextFingerprint(element.GetProperty("contextFingerprint")));
        ValidateInstallSnapshot(snapshot);
        return snapshot;
    }

    private static JsonObject SerializeContextFingerprint(
        ExternalHeadPartInstallContextFingerprint fingerprint)
    {
        ValidateContextFingerprint(fingerprint);
        return new JsonObject
        {
            ["sha256"] = fingerprint.Sha256.Value,
            ["observations"] = new JsonArray(fingerprint.Observations.Select(observation =>
                (JsonNode)new JsonObject
                {
                    ["kind"] = observation.Kind,
                    ["portableIdentity"] = observation.PortableIdentity,
                    ["sha256"] = observation.Sha256.Value,
                    ["byteLength"] = observation.ByteLength,
                    ["order"] = observation.Order
                }).ToArray())
        };
    }

    private static ExternalHeadPartInstallContextFingerprint ParseContextFingerprint(
        JsonElement element)
    {
        RequireExactMembers(
            element,
            "external install context fingerprint",
            "sha256",
            "observations");
        JsonElement observationsElement = RequiredArray(element, "observations");
        var observations = ImmutableArray.CreateBuilder<ExternalHeadPartInstallObservation>();
        foreach (JsonElement observation in observationsElement.EnumerateArray())
        {
            RequireExactMembers(
                observation,
                "external install observation",
                "kind",
                "portableIdentity",
                "sha256",
                "byteLength",
                "order");
            observations.Add(new ExternalHeadPartInstallObservation(
                RequiredString(observation, "kind"),
                RequiredString(observation, "portableIdentity"),
                ParseRequiredHash(observation, "sha256"),
                RequiredInt64(observation, "byteLength"),
                RequiredInt32(observation, "order")));
        }
        var fingerprint = new ExternalHeadPartInstallContextFingerprint(
            ParseRequiredHash(element, "sha256"),
            observations.ToImmutable());
        ValidateContextFingerprint(fingerprint);
        return fingerprint;
    }

    private static ExternalHeadPartInstallContextFingerprint?
        ParseOptionalContextFingerprint(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value))
            return null;
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        return ParseContextFingerprint(value);
    }

    private static AssetPath ParseCanonicalAssetPath(JsonElement parent, string property)
    {
        string value = RequiredString(parent, property);
        AssetPath path;
        try
        {
            path = new AssetPath(value);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException(
                $"Finish Core asset path '{property}' is invalid.", exception);
        }
        if (!string.Equals(path.Value, value, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Finish Core asset path '{property}' is not canonical.");
        return path;
    }

    private static Sha256Hash ParseRequiredHash(JsonElement parent, string property) =>
        ParseHash(parent, property) ??
        throw new InvalidDataException(
            $"Finish Core SHA-256 member '{property}' is required.");

    private static ImmutableArray<Sha256Hash> ParseHashArray(
        JsonElement element,
        string role)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Finish Core {role} must be an array.");
        var result = ImmutableArray.CreateBuilder<Sha256Hash>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException(
                    $"Finish Core {role} contains an invalid SHA-256.");
            result.Add(new Sha256Hash(item.GetString()!));
        }
        return result.ToImmutable();
    }

    private static int RequiredInt32(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out JsonElement value) ||
            !value.TryGetInt32(out int result))
            throw new InvalidDataException(
                $"Finish Core member '{property}' must be a 32-bit integer.");
        return result;
    }

    private static void ValidateExternalHeadPartAuthority(
        SkyrimNpcFinishCoreExternalHeadPartAuthority authority)
    {
        if (authority.SelectedManifestPath != CanonicalSelectedManifest)
            throw new InvalidDataException(
                $"Finish Core selected dependency manifest path must be '{CanonicalSelectedManifestPath}'.");
        ValidateOrderedBindings(authority.Bindings);
        _ = authority.SelectedManifestSha256;
    }

    private static AssetPath ParseCanonicalPromotedBindingPath(
        JsonElement element)
    {
        AssetPath path = new(RequiredString(element, "promotedOutputBindingPath"));
        if (path.Value != CanonicalPromotedOutputBindingPath)
            throw new InvalidDataException(
                $"Finish Core promoted output binding path must be '{CanonicalPromotedOutputBindingPath}'.");
        return path;
    }

    private static void ValidateOrderedBindings(
        ImmutableArray<SkyrimNpcFinishCoreExternalHeadPartBinding> bindings)
    {
        if (bindings.IsDefaultOrEmpty)
            throw new InvalidDataException(
                "Finish Core external head-part bindings must be non-empty.");
        string previous = string.Empty;
        foreach (SkyrimNpcFinishCoreExternalHeadPartBinding binding in bindings)
        {
            if (string.CompareOrdinal(previous, binding.DescriptorId.Value) >= 0)
                throw new InvalidDataException(
                    "Finish Core external head-part bindings must be unique and ordinally sorted by descriptor ID.");
            previous = binding.DescriptorId.Value;
        }
    }

    private static void ValidateExternalHeadPartProposalAuthority(
        SkyrimNpcFinishCoreExternalHeadPartProposalAuthority authority,
        SkyrimNpcFinishCoreStatus status)
    {
        ValidateExternalHeadPartAuthority(authority.Authority);
        ExternalHeadPartInstallVerificationArtifact verification = authority.Verification;
        if (verification.HistoricalSnapshotValid is not null)
            throw new InvalidDataException(
                "Finish Core proposals must not serialize historical snapshot validity.");
        if (verification.RuntimeAuthority || verification.VisualAuthority)
            throw new InvalidDataException(
                "External Finish Core proposals cannot claim runtime or visual authority.");
        var descriptorIds = authority.Authority.Bindings.Select(binding => binding.DescriptorId)
            .ToImmutableArray();
        if (!verification.DescriptorIds.SequenceEqual(descriptorIds))
            throw new InvalidDataException(
                "Finish Core proposal verification descriptor IDs do not match request authority bindings.");
        switch (status)
        {
            case SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired:
                ValidateProposalInstallState(
                    verification,
                    ExternalInstallDependencyState.DeclaredUnverified,
                    packageIntegrity: true,
                    descriptorClosureValid: true,
                    installReady: false,
                    installDependencyAuthority: false,
                    contextFingerprint: authority.ContextFingerprint,
                    contextRequired: false);
                break;
            case SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite:
                ValidateProposalInstallState(
                    verification,
                    ExternalInstallDependencyState.Verified,
                    packageIntegrity: true,
                    descriptorClosureValid: true,
                    installReady: true,
                    installDependencyAuthority: true,
                    contextFingerprint: authority.ContextFingerprint,
                    contextRequired: true);
                break;
            case SkyrimNpcFinishCoreStatus.NoChanges:
            case SkyrimNpcFinishCoreStatus.Refused:
            case SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired:
                if (authority.ContextFingerprint is not ExternalHeadPartInstallContextFingerprint fingerprint)
                    throw new InvalidDataException(
                        "Non-static external proposals must bind an install context fingerprint.");
                ValidateContextFingerprint(fingerprint);
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported external Finish Core proposal status '{status}'.");
        }
    }

    private static void ValidateProposalInstallState(
        ExternalHeadPartInstallVerificationArtifact verification,
        ExternalInstallDependencyState expectedState,
        bool packageIntegrity,
        bool descriptorClosureValid,
        bool installReady,
        bool installDependencyAuthority,
        ExternalHeadPartInstallContextFingerprint? contextFingerprint,
        bool contextRequired)
    {
        if (verification.CurrentInstallDependencyState != expectedState ||
            verification.PackageIntegrity != packageIntegrity ||
            verification.DescriptorClosureValid != descriptorClosureValid ||
            verification.InstallReady != installReady ||
            verification.InstallDependencyAuthority != installDependencyAuthority)
            throw new InvalidDataException(
                $"External Finish Core proposal install state does not match status '{expectedState}'.");
        if (contextRequired && contextFingerprint is not ExternalHeadPartInstallContextFingerprint fingerprint)
            throw new InvalidDataException(
                "Apply-authorized external proposals must bind an install context fingerprint.");
        if (!contextRequired && contextFingerprint is not null)
            throw new InvalidDataException(
                "StaticPassInstallDependencyRequired proposals must remain context-free.");
        if (contextFingerprint is ExternalHeadPartInstallContextFingerprint supplied)
            ValidateContextFingerprint(supplied);
    }

    private static void ValidateExternalManifestRuntimeVisualAuthority(
        bool external,
        bool runtimeAuthority,
        bool visualAuthority)
    {
        if (external && (runtimeAuthority || visualAuthority))
            throw new InvalidDataException(
                "External Finish Core manifests cannot claim runtime or visual authority.");
    }

    private static void ValidateExternalProposalRuntimeAuthority(
        bool external,
        bool runtimeAuthority)
    {
        if (external && runtimeAuthority)
            throw new InvalidDataException(
                "External Finish Core proposals cannot claim runtime authority.");
    }

    private static void ValidateExternalHeadPartManifestAuthority(
        SkyrimNpcFinishCoreExternalHeadPartManifestAuthority authority)
    {
        if (authority.SelectedManifestPath != CanonicalSelectedManifest)
            throw new InvalidDataException(
                $"Finish Core selected dependency manifest path must be '{CanonicalSelectedManifestPath}'.");
        if (!string.Equals(authority.PromotedOutputBindingPath.Value,
                CanonicalPromotedOutputBindingPath, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(authority.PromotedOutputBindingSha256.Value) ||
            authority.PromotedOutputBindingSha256.Value.Length != 64 ||
            authority.PromotedOutputBindingSha256.Value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException(
                $"Finish Core promoted output binding path must be '{CanonicalPromotedOutputBindingPath}' with a SHA-256 hash.");
        if (authority.Descriptors.IsDefaultOrEmpty ||
            authority.Descriptors.Length != authority.Attestations.Length)
            throw new InvalidDataException(
                "Finish Core external manifest descriptors and attestations must have equal non-zero counts.");
        var descriptorIds = authority.Descriptors.Select(item => item.DescriptorId).ToImmutableArray();
        ValidateSortedHashes(descriptorIds, "manifest descriptor IDs");
        var attestationIds = authority.Attestations.Select(item => item.DescriptorId).ToImmutableArray();
        if (!attestationIds.SequenceEqual(descriptorIds))
            throw new InvalidDataException(
                "Finish Core manifest attestation descriptor IDs must match descriptor order.");
        if (authority.VerifiedInstallSnapshot.SelectedManifestSha256 !=
            authority.SelectedManifestSha256)
            throw new InvalidDataException(
                "Finish Core manifest install snapshot is bound to a different selected dependency manifest.");
        if (!authority.VerifiedInstallSnapshot.DescriptorIds.SequenceEqual(descriptorIds))
            throw new InvalidDataException(
                "Finish Core manifest install snapshot descriptor IDs do not match descriptors.");
        ValidateInstallSnapshot(authority.VerifiedInstallSnapshot);
    }

    private static void ValidateExternalHeadPartVerification(
        SkyrimNpcFinishCoreExternalHeadPartVerification external)
    {
        if (external.Verification.HistoricalSnapshotValid is null)
            throw new InvalidDataException(
                "Finish Core verification v2 must report historical snapshot validity explicitly.");
        if (external.Verification.RuntimeAuthority || external.Verification.VisualAuthority)
            throw new InvalidDataException(
                "External Finish Core verification cannot claim runtime or visual authority.");
    }

    private static void ValidateInstallSnapshot(
        ExternalHeadPartVerifiedInstallSnapshot snapshot)
    {
        ValidateSortedHashes(snapshot.DescriptorIds, "install snapshot descriptor IDs");
        ValidateContextFingerprint(snapshot.ContextFingerprint);
    }

    private static void ValidateSortedHashes(
        ImmutableArray<Sha256Hash> hashes,
        string role)
    {
        if (hashes.IsDefaultOrEmpty)
            throw new InvalidDataException($"Finish Core {role} must be non-empty.");
        string previous = string.Empty;
        foreach (Sha256Hash hash in hashes)
        {
            if (string.CompareOrdinal(previous, hash.Value) >= 0)
                throw new InvalidDataException($"Finish Core {role} must be unique and ordinally sorted.");
            previous = hash.Value;
        }
    }

    private static void ValidateContextFingerprint(
        ExternalHeadPartInstallContextFingerprint fingerprint)
    {
        if (fingerprint.Observations.IsDefaultOrEmpty)
            throw new InvalidDataException(
                "Finish Core install context fingerprints must contain observations.");
        for (int index = 0; index < fingerprint.Observations.Length; index++)
        {
            ExternalHeadPartInstallObservation observation = fingerprint.Observations[index];
            if (string.IsNullOrWhiteSpace(observation.Kind) ||
                string.IsNullOrWhiteSpace(observation.PortableIdentity) ||
                observation.ByteLength < 0 || observation.Order != index)
                throw new InvalidDataException(
                    "Finish Core install context observations must be non-empty and ordinally ordered.");
        }
        Sha256Hash computed = ExternalHeadPartDependencyDescriptorCodec
            .ComputeInstallContextFingerprintHash(fingerprint.Observations);
        if (computed != fingerprint.Sha256)
            throw new InvalidDataException(
                "Finish Core install context fingerprint hash does not match observations.");
    }

    private static SkyrimNpcFinishCoreStatus ParseFinishCoreStatus(
        JsonElement parent,
        string property,
        bool external)
    {
        SkyrimNpcFinishCoreStatus status = ParseEnum<SkyrimNpcFinishCoreStatus>(parent, property);
        if (!external && status == SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired)
            throw new InvalidDataException(
                "StaticPassInstallDependencyRequired is only valid in Finish Core v3/v2 external documents.");
        return status;
    }

    private static WorkspacePath? ParsePath(
        JsonElement parent,
        string name,
        WorkspacePath projectRoot)
    {
        string? value = OptionalString(parent, name);
        if (value is null)
            return null;
        if (value.StartsWith('/') || Path.IsPathRooted(value))
            throw InvalidProjectPath(name, value, "rooted paths are not allowed");
        if (value.Contains('\\'))
            throw InvalidProjectPath(name, value, "backslash separators are not allowed");
        string[] segments = value.Split('/');
        if (segments.Any(part => part == ".."))
            throw InvalidProjectPath(name, value, "project-root escape segments are not allowed");
        if (value.Length == 0 || segments.Any(part => part is "" or "."))
            throw InvalidProjectPath(name, value, "dot-or-empty segments are not allowed");
        WorkspacePath result = new(Path.GetFullPath(Path.Combine(
            projectRoot.Value, value.Replace('/', Path.DirectorySeparatorChar))));
        if (!result.IsUnder(projectRoot))
            throw InvalidProjectPath(name, value, "the resolved path escapes the project root");
        return result;
    }

    private static Sha256Hash? ParseHash(JsonElement parent, string name)
    {
        string? value = OptionalString(parent, name);
        return value is null ? null : new Sha256Hash(value);
    }

    private static PluginName? ParsePlugin(JsonElement parent, string name)
    {
        string? value = OptionalString(parent, name);
        return value is null ? null : new PluginName(value);
    }

    private static EditorId? ParseEditorId(JsonElement parent, string name)
    {
        string? value = OptionalString(parent, name);
        return value is null ? null : new EditorId(value);
    }

    private static FormId? ParseFormId(JsonElement parent, string name)
    {
        string? value = OptionalString(parent, name);
        if (value is null)
            return null;
        return FormId.TryParse(value, out FormId parsed)
            ? parsed
            : throw new InvalidDataException($"Finish Core FormID '{name}' is invalid.");
    }

    private static FormReference? ParseForm(JsonElement parent, string name)
    {
        string? value = OptionalString(parent, name);
        if (value is null)
            return null;
        return FormReference.TryParse(value, out FormReference parsed) &&
               parsed.ToString() == value
            ? parsed
            : throw new InvalidDataException($"Finish Core FormReference '{name}' is not canonical.");
    }

    private static FormReference ParseRequiredForm(JsonElement parent, string name) =>
        ParseForm(parent, name) ??
        throw new InvalidDataException($"Finish Core FormReference '{name}' is required.");

    private static FormId RequiredFormId(JsonElement parent, string name) =>
        ParseFormId(parent, name) ??
        throw new InvalidDataException($"Finish Core FormID '{name}' is required.");

    private static ImmutableArray<FormReference> ParseForms(JsonElement parent, string name)
    {
        JsonElement array = RequiredArray(parent, name);
        var result = ImmutableArray.CreateBuilder<FormReference>();
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String ||
                !FormReference.TryParse(element.GetString()!, out FormReference parsed) ||
                parsed.ToString() != element.GetString())
                throw new InvalidDataException($"Finish Core array '{name}' contains a noncanonical FormReference.");
            result.Add(parsed);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<string> ParseStrings(JsonElement parent, string name)
    {
        JsonElement array = RequiredArray(parent, name);
        var result = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(element.GetString()))
                throw new InvalidDataException($"Finish Core array '{name}' contains a non-string.");
            result.Add(element.GetString()!);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<SkyrimNpcFinishCoreProviderAuthority> ParseProviders(
        JsonElement parent,
        WorkspacePath projectRoot)
    {
        JsonElement array = RequiredArray(parent, "providers");
        var result = ImmutableArray.CreateBuilder<SkyrimNpcFinishCoreProviderAuthority>();
        foreach (JsonElement element in array.EnumerateArray())
        {
            RequireKnownMembers(element, "provider", "plugin", "path", "sha256", "byteLength");
            result.Add(new SkyrimNpcFinishCoreProviderAuthority
            {
                Plugin = ParsePlugin(element, "plugin"),
                Path = ParsePath(element, "path", projectRoot),
                Sha256 = ParseHash(element, "sha256"),
                ByteLength = RequiredInt64(element, "byteLength")
            });
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding>
        ParseAdditionalMasters(
            JsonElement parent,
            WorkspacePath projectRoot)
    {
        JsonElement array = RequiredArray(parent, "additionalMasters");
        var result = ImmutableArray.CreateBuilder<SkyrimNpcFinishCoreAdditionalMasterBinding>();
        foreach (JsonElement element in array.EnumerateArray())
        {
            RequireExactMembers(
                element,
                "additionalMaster",
                "plugin",
                "path",
                "sha256",
                "byteLength",
                "loadOrderIndex");
            PluginName plugin = ParsePlugin(element, "plugin") ??
                throw new InvalidDataException(
                    "Finish Core additional-master plugin is required.");
            WorkspacePath path = ParsePath(element, "path", projectRoot) ??
                throw new InvalidDataException(
                    "Finish Core additional-master path is required.");
            string hashText = RequiredString(element, "sha256");
            if (!string.Equals(hashText, hashText.ToUpperInvariant(), StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Finish Core additional-master SHA-256 must use uppercase hexadecimal.");
            Sha256Hash hash = new(hashText);
            long byteLength = RequiredInt64(element, "byteLength");
            long loadOrderIndex = RequiredInt64(element, "loadOrderIndex");
            if (byteLength <= 0 || loadOrderIndex < 0 || loadOrderIndex > int.MaxValue)
                throw new InvalidDataException(
                    "Finish Core additional-master byteLength must be positive and loadOrderIndex must be a non-negative Int32.");
            result.Add(new SkyrimNpcFinishCoreAdditionalMasterBinding(
                plugin,
                path,
                hash,
                byteLength,
                checked((int)loadOrderIndex)));
        }

        return result.ToImmutable();
    }

    private static T ParseEnum<T>(JsonElement parent, string name)
        where T : struct, Enum
    {
        string value = RequiredString(parent, name);
        if (Enum.TryParse(value, ignoreCase: false, out T result) &&
            Enum.IsDefined(result))
            return result;
        string expected = string.Join(", ", Enum.GetNames<T>());
        throw new InvalidDataException(
            $"Finish Core enum '{name}' is invalid; expected one of [{expected}].");
    }

    private static SkyrimNpcFinishCoreCombatPolicy? ParseCombatPolicy(JsonElement root)
    {
        if (!root.TryGetProperty("combatPolicy", out JsonElement policy)) return null;
        RequireClosedMembers(policy, "combatPolicy", ["seedLocalStyle"], ["profile"]);
        SkyrimNpcFinishCoreCombatProfile? profile = null;
        if (policy.TryGetProperty("profile", out _))
            profile = RequiredString(policy, "profile") switch
            {
                "defensive" => SkyrimNpcFinishCoreCombatProfile.Defensive,
                "rangedFirst" => SkyrimNpcFinishCoreCombatProfile.RangedFirst,
                "meleeFirst" => SkyrimNpcFinishCoreCombatProfile.MeleeFirst,
                _ => throw new InvalidDataException("Finish Core enum 'profile' is invalid; expected one of [defensive, rangedFirst, meleeFirst].")
            };
        return new SkyrimNpcFinishCoreCombatPolicy
        {
            SeedLocalStyle = RequiredBoolean(policy, "seedLocalStyle"), Profile = profile
        };
    }

    private static ImmutableArray<SkyrimNpcFinishCorePerk> ParsePerkPolicy(JsonElement root)
    {
        if (!root.TryGetProperty("perkPolicy", out JsonElement policy)) return default;
        if (policy.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("perkPolicy must be an array.");
        var perks = ImmutableArray.CreateBuilder<SkyrimNpcFinishCorePerk>();
        foreach (JsonElement row in policy.EnumerateArray())
        {
            RequireExactMembers(row, "perkPolicy row", "form", "rank");
            perks.Add(new(ParseRequiredForm(row, "form"), RequiredByte(row, "rank", 1, 255)));
        }
        var result = perks.ToImmutable();
        ValidatePerks(result);
        return result;
    }

    private static void ValidatePerks(ImmutableArray<SkyrimNpcFinishCorePerk> perks)
    {
        if (perks.Any(perk => perk.Rank == 0 || perk.Form.FormId.Value == 0) ||
            perks.Select(perk => perk.Form.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != perks.Length)
            throw new InvalidDataException("perkPolicy requires unique non-null FormRefs and ranks in [1, 255].");
    }

    private static T? ParseOptionalEnum<T>(JsonElement parent, string name)
        where T : struct, Enum
    {
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind == JsonValueKind.Null)
            return null;
        return ParseEnum<T>(parent, name);
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Finish Core member '{name}' must be a string or null.");
        return element.GetString();
    }

    private static bool RequiredBoolean(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Finish Core member '{name}' must be boolean.");
        return element.GetBoolean();
    }

    private static long RequiredInt64(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            !element.TryGetInt64(out long value))
            throw new InvalidDataException($"Finish Core member '{name}' must be an integer.");
        return value;
    }

    private static uint RequiredUInt32(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Number || !element.TryGetUInt32(out uint value))
            throw new InvalidDataException($"Finish Core member '{name}' must be an unsigned integer.");
        return value;
    }

    private static byte RequiredByte(
        JsonElement parent,
        string name,
        byte minimum,
        byte maximum)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) ||
            element.ValueKind != JsonValueKind.Number ||
            !element.TryGetByte(out byte value) || value < minimum || value > maximum)
            throw new InvalidDataException(
                $"Finish Core member '{name}' must be an integer from {minimum} through {maximum}.");
        return value;
    }

    private static JsonElement RequiredArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Finish Core member '{name}' must be an array.");
        return value;
    }

    private static JsonElement RequiredObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                $"Finish Core member '{name}' must be an object.");
        return value;
    }

    private static ImmutableDictionary<string, int> ParseCounts(
        JsonElement parent,
        string name)
    {
        JsonElement value = RequiredObject(parent, name);
        var result = ImmutableDictionary.CreateBuilder<string, int>(
            StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) ||
                property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt32(out int count) ||
                count < 0)
                throw new InvalidDataException(
                    $"Finish Core count '{name}.{property.Name}' must be a nonnegative integer.");
            result.Add(property.Name, count);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<SkyrimNpcFinishCoreEvidenceEntry>
        ParseEvidenceEntries(JsonElement evidence)
    {
        JsonElement files = RequiredArray(evidence, "files");
        var result = ImmutableArray.CreateBuilder<
            SkyrimNpcFinishCoreEvidenceEntry>();
        foreach (JsonElement file in files.EnumerateArray())
        {
            RequireExactMembers(
                file,
                "manifest evidence file",
                "path",
                "byteLength",
                "sha256");
            string path = RequiredString(file, "path");
            var assetPath = new AssetPath(path);
            if (!string.Equals(
                    assetPath.Value,
                    path,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Finish Core manifest evidence paths must be canonical.");
            long byteLength = RequiredInt64(file, "byteLength");
            if (byteLength < 0)
                throw new InvalidDataException(
                    "Finish Core manifest evidence byte lengths cannot be negative.");
            result.Add(new SkyrimNpcFinishCoreEvidenceEntry
            {
                Path = assetPath,
                ByteLength = byteLength,
                Sha256 = ParseHash(file, "sha256") ??
                    throw new InvalidDataException(
                        "Finish Core manifest evidence SHA-256 is required.")
            });
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<SkyrimNpcFinishCoreInheritedEvidenceEntry>
        ParseInheritedEvidenceEntries(JsonElement evidence)
    {
        if (!evidence.TryGetProperty("inherited", out JsonElement files))
            return ImmutableArray<SkyrimNpcFinishCoreInheritedEvidenceEntry>.Empty;
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
            throw new InvalidDataException(
                "Finish Core manifest inherited evidence must be a non-empty array when present.");
        var result = ImmutableArray.CreateBuilder<
            SkyrimNpcFinishCoreInheritedEvidenceEntry>();
        foreach (JsonElement file in files.EnumerateArray())
        {
            RequireExactMembers(
                file,
                "manifest inherited evidence file",
                "path",
                "sourcePath",
                "byteLength",
                "sha256");
            string path = RequiredString(file, "path");
            string sourcePath = RequiredString(file, "sourcePath");
            var assetPath = new AssetPath(path);
            var sourceAssetPath = new AssetPath(sourcePath);
            if (!string.Equals(assetPath.Value, path, StringComparison.Ordinal) ||
                !string.Equals(sourceAssetPath.Value, sourcePath, StringComparison.Ordinal) ||
                !path.StartsWith(
                    SkyrimNpcFinishCoreManifestEvidence.InheritedNamespacePrefix,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Finish Core manifest inherited evidence paths must be canonical members of NPCManager/Evidence/Inherited/.");
            long byteLength = RequiredInt64(file, "byteLength");
            if (byteLength < 0)
                throw new InvalidDataException(
                    "Finish Core manifest inherited evidence byte lengths cannot be negative.");
            result.Add(new SkyrimNpcFinishCoreInheritedEvidenceEntry
            {
                Path = assetPath,
                SourcePath = sourceAssetPath,
                ByteLength = byteLength,
                Sha256 = ParseHash(file, "sha256") ??
                    throw new InvalidDataException(
                        "Finish Core manifest inherited evidence SHA-256 is required.")
            });
        }
        return result.ToImmutable();
    }

    private static JsonElement ParseRoot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Encoding.UTF8.GetPreamble()))
            throw new InvalidDataException("Finish Core JSON must be UTF-8 without a BOM.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                bytes.ToArray(),
                StrictDocumentOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Finish Core JSON is not strict canonical JSON.",
                exception);
        }
        using (document)
        {
            RejectDuplicateProperties(document.RootElement);
            return document.RootElement.Clone();
        }
    }

    private static void RequireExactMembers(
        JsonElement element,
        string context,
        params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Finish Core {context} must be an object.");
        HashSet<string> allowed = expected.ToHashSet(StringComparer.Ordinal);
        string[] actual = element.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        string[] unknown = actual
            .Where(name => !allowed.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] missing = expected
            .Where(name => !actual.Contains(name, StringComparer.Ordinal))
            .ToArray();
        if (unknown.Length != 0 || missing.Length != 0)
            throw new InvalidDataException(
                $"Finish Core {context} members are closed; " +
                $"unknown=[{string.Join(",", unknown)}], " +
                $"missing=[{string.Join(",", missing)}].");
    }

    private static void RequireClosedMembers(
        JsonElement element,
        string context,
        string[] required,
        string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Finish Core {context} must be an object.");
        HashSet<string> allowed = required.Concat(optional).ToHashSet(StringComparer.Ordinal);
        string[] actual = element.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        string[] unknown = actual
            .Where(name => !allowed.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] missing = required
            .Where(name => !actual.Contains(name, StringComparer.Ordinal))
            .ToArray();
        if (unknown.Length != 0 || missing.Length != 0)
            throw new InvalidDataException(
                $"Finish Core {context} members are closed; " +
                $"unknown=[{string.Join(",", unknown)}], " +
                $"missing=[{string.Join(",", missing)}].");
    }

    private static void RequireKnownMembers(
        JsonElement element,
        string context,
        params string[] allowedMembers)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Finish Core {context} must be an object.");
        HashSet<string> allowed = allowedMembers.ToHashSet(StringComparer.Ordinal);
        string[] unknown = element.EnumerateObject()
            .Select(property => property.Name)
            .Where(name => !allowed.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (unknown.Length != 0)
            throw new InvalidDataException(
                $"Finish Core {context} contains unknown members: {string.Join(",", unknown)}.");
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Finish Core member '{name}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Finish Core JSON contains duplicate member '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
                RejectDuplicateProperties(child);
        }
    }

    private static byte[] CanonicalizeElement(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
               {
                   Indented = false,
                   SkipValidation = false
               }))
        {
            WriteCanonical(element, writer);
        }
        return stream.ToArray();
    }

    private static byte[] CanonicalizeNode(JsonNode node)
    {
        using JsonDocument document = JsonDocument.Parse(node.ToJsonString());
        RejectDuplicateProperties(document.RootElement);
        return CanonicalizeElement(document.RootElement);
    }

    private static void WriteCanonical(
        JsonElement element,
        Utf8JsonWriter writer,
        bool preserveExternalOrder = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                bool external = preserveExternalOrder || IsExternalWireObject(element);
                IEnumerable<JsonProperty> properties = external
                    ? element.EnumerateObject()
                    : element.EnumerateObject()
                        .OrderBy(property => property.Name, StringComparer.Ordinal);
                foreach (JsonProperty property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer, external);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement child in element.EnumerateArray())
                    WriteCanonical(child, writer, preserveExternalOrder);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsExternalWireObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("schemaIdentifier", out JsonElement schema) ||
            schema.ValueKind != JsonValueKind.String)
            return false;
        string? value = schema.GetString();
        return value is ExternalHeadPartSchemaIdentifiers.Descriptor or
            ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion or
            ExternalHeadPartSchemaIdentifiers.InstallVerification;
    }

    private static JsonObject SerializeSource(
        SkyrimNpcFinishCoreSource source,
        WorkspacePath projectRoot) =>
        new()
        {
            ["packageRoot"] = ToWirePath(source.PackageRoot, projectRoot, "packageRoot"),
            ["packageManifest"] = ToWirePath(source.PackageManifest, projectRoot, "packageManifest"),
            ["packageManifestSha256"] = source.PackageManifestSha256?.Value,
            ["packageTreeSha256"] = source.PackageTreeSha256?.Value,
            ["pluginPath"] = ToWirePath(source.PluginPath, projectRoot, "pluginPath"),
            ["plugin"] = source.Plugin?.Value,
            ["pluginSha256"] = source.PluginSha256?.Value
        };

    private static JsonObject SerializeActor(SkyrimNpcFinishCoreActor actor) =>
        new()
        {
            ["editorId"] = actor.EditorId?.Value,
            ["formId"] = actor.FormId?.ToString()
        };

    private static JsonObject SerializeAuthorities(
        SkyrimNpcFinishCoreAuthorities authorities,
        WorkspacePath projectRoot,
        bool includeAdditionalMasters,
        bool includeExternalHeadParts)
    {
        var result = new JsonObject
        {
            ["bodyRoute"] = authorities.BodyRoute.ToString(),
            ["providers"] = new JsonArray(authorities.Providers
                .Select(provider => new JsonObject
                {
                    ["plugin"] = provider.Plugin?.Value,
                    ["path"] = ToWirePath(provider.Path, projectRoot, "path"),
                    ["sha256"] = provider.Sha256?.Value,
                    ["byteLength"] = provider.ByteLength
                })
                .ToArray()),
            ["actorAssemblySha256"] = authorities.ActorAssemblySha256?.Value,
            ["bodyOwnerSha256"] = authorities.BodyOwnerSha256?.Value,
            ["protectedAppearanceTreeSha256"] = authorities.ProtectedAppearanceTreeSha256?.Value
        };
        if (includeAdditionalMasters)
        {
            result["additionalMasters"] = new JsonArray(
                authorities.AdditionalMasters.Select(binding => new JsonObject
                {
                    ["plugin"] = binding.Plugin.Value,
                    ["path"] = ToWirePath(binding.Path, projectRoot, "path"),
                    ["sha256"] = binding.Sha256.Value.ToUpperInvariant(),
                    ["byteLength"] = binding.ByteLength,
                    ["loadOrderIndex"] = binding.LoadOrderIndex
                }).ToArray());
        }

        if (includeExternalHeadParts)
        {
            if (authorities.ExternalHeadParts is not
                SkyrimNpcFinishCoreExternalHeadPartAuthority external)
                throw new InvalidDataException(
                    "A v3 Finish Core request must carry external head-part authority.");
            result["externalHeadParts"] = SerializeExternalHeadPartAuthority(external);
        }
        else if (authorities.ExternalHeadParts is not null)
        {
            throw new InvalidDataException(
                "External head-part authority requires a v3 Finish Core request.");
        }

        return result;
    }

    private static JsonObject SerializeFollowerPolicy(
        SkyrimNpcFinishCoreFollowerPolicy policy) =>
        new()
        {
            ["recruitable"] = policy.Recruitable,
            ["defensiveOnly"] = policy.DefensiveOnly,
            ["potentialFollowerFaction"] = ToWireForm(policy.PotentialFollowerFaction),
            ["currentFollowerFaction"] = ToWireForm(policy.CurrentFollowerFaction),
            ["relationshipRank"] = policy.RelationshipRank
        };

    private static JsonObject SerializeAiPolicy(
        SkyrimNpcFinishCoreAiPolicy policy,
        bool includeMood)
    {
        var result = new JsonObject
        {
            ["aggression"] = policy.Aggression.ToString(),
            ["confidence"] = policy.Confidence.ToString(),
            ["energy"] = policy.Energy,
            ["morality"] = policy.Morality.ToString(),
            ["assistance"] = policy.Assistance.ToString()
        };
        if (includeMood)
            result["mood"] = policy.Mood?.ToString();
        return result;
    }

    private static JsonObject SerializeOutfitPolicy(
        SkyrimNpcFinishCoreOutfitPolicyDocument policy) =>
        new()
        {
            ["policy"] = policy.Policy.ToString(),
            ["existingOutfit"] = ToWireForm(policy.ExistingOutfit),
            ["armorItems"] = new JsonArray(policy.ArmorItems
                .Select(item => JsonValue.Create(item.ToString()))
                .ToArray())
        };

    private static JsonObject SerializeInventoryPolicy(
        SkyrimNpcFinishCoreInventoryPolicyDocument policy) =>
        new()
        {
            ["policy"] = policy.Policy.ToString(),
            ["expectedSourceItems"] = new JsonArray(policy.ExpectedSourceItems
                .Select(value => JsonValue.Create(value)).ToArray()),
            ["desiredItems"] = new JsonArray(policy.DesiredItems
                .Select(value => JsonValue.Create(value)).ToArray())
        };

    private static JsonObject SerializeSandboxAuthority(
        SkyrimNpcFinishCoreSandboxAuthority authority,
        WorkspacePath projectRoot) =>
        new()
        {
            ["copiedMaster"] = ToWirePath(authority.CopiedMaster, projectRoot, "copiedMaster"),
            ["copiedMasterSha256"] = authority.CopiedMasterSha256?.Value,
            ["template"] = authority.Template.ToString(),
            ["templateEditorId"] = authority.TemplateEditorId,
            ["rawRecordDigest"] = authority.RawRecordDigest?.Value
        };

    private static JsonObject SerializeOutput(
        SkyrimNpcFinishCoreOutput output,
        WorkspacePath projectRoot) =>
        new()
        {
            ["root"] = ToWirePath(output.Root, projectRoot, "root"),
            ["archive"] = ToWirePath(output.Archive, projectRoot, "archive"),
            ["pluginFileName"] = output.PluginFileName
        };

    private static string? ToWirePath(
        WorkspacePath? path,
        WorkspacePath projectRoot,
        string name)
    {
        if (path is null)
            return null;
        string relative = Path.GetRelativePath(projectRoot.Value, path.Value.Value)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith('/') || Path.IsPathRooted(relative))
            throw InvalidProjectPath(name, path.Value.Value, "rooted paths are not allowed");
        if (relative.Contains('\\'))
            throw InvalidProjectPath(name, path.Value.Value, "backslash separators are not allowed");
        string[] segments = relative.Split('/');
        if (segments.Any(part => part == ".."))
            throw InvalidProjectPath(name, path.Value.Value, "project-root escape segments are not allowed");
        if (relative.Length == 0 || segments.Any(part => part is "" or "."))
            throw InvalidProjectPath(name, path.Value.Value, "dot-or-empty segments are not allowed");
        return relative;
    }

    private static InvalidDataException InvalidProjectPath(
        string name,
        string value,
        string reason) =>
        new($"Finish Core path '{name}' with value '{value}' must be a project-relative forward-slash path: {reason}.");

    private static string? ToWireForm(FormReference? reference) =>
        reference?.ToString();
}
