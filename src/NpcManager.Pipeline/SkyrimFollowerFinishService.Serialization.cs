using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimFollowerFinishService
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions =
        new()
        {
            WriteIndented = false
        };

    private static byte[] SerializeProposal(
        SkyrimFollowerFinishProposal proposal) =>
        Serialize(
            new JsonObject
            {
                ["schemaVersion"] = proposal.SchemaVersion,
                ["operation"] = proposal.Operation,
                ["requestSha256"] = proposal.RequestSha256.Value,
                ["request"] = RequestJson(proposal.Request),
                ["sourceSnapshot"] =
                    SnapshotJson(proposal.SourceSnapshot),
                ["existingRecordChanges"] =
                    StringArray(proposal.ExistingRecordChanges),
                ["newRecords"] = StringArray(proposal.NewRecords),
                ["nextFormId"] = proposal.NextFormId.ToString(),
                ["rawGroupTreeSurface"] =
                    StringArray(proposal.RawGroupTreeSurface),
                ["allowedPackageFiles"] =
                    AssetArray(proposal.AllowedPackageFiles),
                ["runtimeAuthority"] = proposal.RuntimeAuthority
            });

    private static byte[] SerializeRequest(
        SkyrimFollowerFinishRequest request) =>
        Serialize(RequestJson(request));

    private static JsonObject RequestJson(
        SkyrimFollowerFinishRequest request) =>
        new()
        {
            ["schemaVersion"] = request.SchemaVersion,
            ["operation"] = request.Operation,
            ["source"] = new JsonObject
            {
                ["zip"] = request.Source.Zip.Value,
                ["zipByteLength"] = request.Source.ZipByteLength,
                ["zipSha256"] = request.Source.ZipSha256.Value,
                ["packageManifest"] =
                    request.Source.PackageManifest.Value,
                ["packageManifestSha256"] =
                    request.Source.PackageManifestSha256.Value,
                ["plugin"] = request.Source.Plugin.Value,
                ["pluginSha256"] = request.Source.PluginSha256.Value,
                ["faceGeomSha256"] =
                    request.Source.FaceGeomSha256.Value,
                ["faceTintSha256"] =
                    request.Source.FaceTintSha256.Value
            },
            ["externalAuthorities"] =
                ExternalAuthoritiesJson(
                    request.ExternalAuthorities ??
                    throw new InvalidDataException(
                        "Follower-finish canonical serialization requires external authorities.")),
            ["npcEditorId"] = request.NpcEditorId.Value,
            ["npcFormId"] = request.NpcFormId.ToString(),
            ["occupiedLocalFormIds"] =
                FormIdArray(request.OccupiedLocalFormIds),
            ["expectedRace"] = request.ExpectedRace.ToString(),
            ["expectedBodyRoute"] = request.ExpectedBodyRoute,
            ["expectedDefaultOutfitNull"] =
                request.ExpectedDefaultOutfitNull,
            ["expectedFactionRanks"] =
                FactionArray(request.ExpectedFactionRanks),
            ["relationshipFormId"] =
                request.RelationshipFormId.ToString(),
            ["expectedRelationshipRank"] =
                request.ExpectedRelationshipRank,
            ["expectedRelationshipRankRawDiscriminator"] =
                request.ExpectedRelationshipRankRawDiscriminator,
            ["hair"] = new JsonObject
            {
                ["colorFormId"] = request.Hair.ColorFormId.ToString(),
                ["oldPackedRgb"] = request.Hair.OldPackedRgb.Value,
                ["newPackedRgb"] = request.Hair.NewPackedRgb.Value
            },
            ["setEslFlag"] = request.SetEslFlag,
            ["compactFormIds"] = request.CompactFormIds,
            ["sandbox"] = new JsonObject
            {
                ["procedure"] = request.Sandbox.Procedure,
                ["radius"] = request.Sandbox.Radius,
                ["schedule"] = request.Sandbox.Schedule,
                ["target"] = request.Sandbox.Target.ToString(),
                ["condition"] = request.Sandbox.Condition
            },
            ["placement"] = new JsonObject
            {
                ["worldspace"] = request.Placement.Worldspace.ToString(),
                ["cell"] = request.Placement.Cell.ToString(),
                ["markerBase"] =
                    request.Placement.MarkerBase.ToString(),
                ["actor"] = TransformJson(request.Placement.Actor),
                ["anchor"] = TransformJson(request.Placement.Anchor)
            },
            ["allocation"] = new JsonObject
            {
                ["package"] = request.Allocation.Package.ToString(),
                ["anchor"] = request.Allocation.Anchor.ToString(),
                ["actor"] = request.Allocation.Actor.ToString(),
                ["nextFormId"] =
                    request.Allocation.NextFormId.ToString()
            },
            ["allowedNewRecords"] =
                StringArray(request.AllowedNewRecords),
            ["allowedExistingRecordChanges"] =
                StringArray(request.AllowedExistingRecordChanges),
            ["allowedPackageFiles"] =
                AssetArray(request.AllowedPackageFiles),
            ["outputRoot"] = request.OutputRoot.Value,
            ["outputZip"] = request.OutputZip.Value,
            ["narrative"] = request.Narrative
        };

    private static JsonObject ExternalAuthoritiesJson(
        SkyrimFollowerFinishExternalAuthorities authorities) =>
        new()
        {
            ["placementEvidence"] = ExternalFileAuthorityJson(
                authorities.PlacementEvidence),
            ["providers"] = PluginProviderAuthorityArray(
                authorities.Providers)
        };

    private static JsonObject ExternalFileAuthorityJson(
        SkyrimFollowerFinishFileAuthority authority) =>
        new()
        {
            ["path"] = authority.Path.Value,
            ["byteLength"] = authority.ByteLength,
            ["sha256"] = authority.Sha256.Value
        };

    private static JsonArray PluginProviderAuthorityArray(
        ImmutableArray<SkyrimFollowerFinishPluginProviderAuthority>
            providers)
    {
        var result = new JsonArray();
        foreach (SkyrimFollowerFinishPluginProviderAuthority provider in
                 providers)
        {
            result.Add(
                new JsonObject
                {
                    ["plugin"] = provider.Plugin.Value,
                    ["path"] = provider.Path.Value,
                    ["byteLength"] = provider.ByteLength,
                    ["sha256"] = provider.Sha256.Value
                });
        }
        return result;
    }

    private static JsonObject SnapshotJson(
        SkyrimFollowerFinishPluginSnapshot snapshot) =>
        new()
        {
            ["valid"] = snapshot.Valid,
            ["plugin"] = snapshot.Plugin.Value,
            ["pluginSha256"] = snapshot.PluginSha256.Value,
            ["tes4Flags"] = snapshot.Tes4Flags,
            ["masters"] = PluginArray(snapshot.Masters),
            ["nextFormId"] = snapshot.NextFormId.ToString(),
            ["recordInventory"] =
                StringArray(snapshot.RecordInventory),
            ["actorSubrecordDigests"] =
                StringArray(snapshot.ActorSubrecordDigests),
            ["hairPackedRgb"] = snapshot.HairPackedRgb.Value,
            ["actorHairColor"] = snapshot.ActorHairColor.ToString(),
            ["defaultOutfitNull"] = snapshot.DefaultOutfitNull,
            ["factionRanks"] = FactionArray(snapshot.FactionRanks),
            ["relationshipRank"] = snapshot.RelationshipRank,
            ["relationshipRankRawDiscriminator"] =
                snapshot.RelationshipRankRawDiscriminator,
            ["absentSignatures"] =
                StringArray(snapshot.AbsentSignatures),
            ["diagnostics"] = DiagnosticArray(snapshot.Diagnostics)
        };

    private static JsonObject TransformJson(
        SkyrimExteriorTransform transform) =>
        new()
        {
            ["x"] = transform.X,
            ["y"] = transform.Y,
            ["z"] = transform.Z,
            ["rotationX"] = transform.RotationX,
            ["rotationY"] = transform.RotationY,
            ["rotationZ"] = transform.RotationZ
        };

    private static JsonArray FactionArray(
        ImmutableArray<NpcFactionEntry> entries)
    {
        var result = new JsonArray();
        foreach (NpcFactionEntry entry in entries)
        {
            result.Add(
                new JsonObject
                {
                    ["faction"] = entry.Faction.ToString(),
                    ["rank"] = entry.Rank
                });
        }
        return result;
    }

    private static JsonArray DiagnosticArray(
        ImmutableArray<Diagnostic> diagnostics)
    {
        var result = new JsonArray();
        foreach (Diagnostic diagnostic in diagnostics)
        {
            result.Add(
                new JsonObject
                {
                    ["code"] = diagnostic.Code,
                    ["severity"] = diagnostic.Severity switch
                    {
                        DiagnosticSeverity.Info => "info",
                        DiagnosticSeverity.Warning => "warning",
                        DiagnosticSeverity.Error => "error",
                        _ => throw new InvalidDataException(
                            "An unsupported diagnostic severity cannot be serialized.")
                    },
                    ["message"] = diagnostic.Message
                });
        }
        return result;
    }

    private static JsonArray StringArray(
        ImmutableArray<string> values)
    {
        var result = new JsonArray();
        foreach (string value in values)
            result.Add(value);
        return result;
    }

    private static JsonArray AssetArray(
        ImmutableArray<AssetPath> values)
    {
        var result = new JsonArray();
        foreach (AssetPath value in values)
            result.Add(value.Value);
        return result;
    }

    private static JsonArray FormIdArray(
        ImmutableArray<FormId> values)
    {
        var result = new JsonArray();
        foreach (FormId value in values)
            result.Add(value.ToString());
        return result;
    }

    private static JsonArray PluginArray(
        ImmutableArray<PluginName> values)
    {
        var result = new JsonArray();
        foreach (PluginName value in values)
            result.Add(value.Value);
        return result;
    }

    private static byte[] Serialize(JsonNode node) =>
        JsonSerializer.SerializeToUtf8Bytes(
            node,
            CanonicalJsonOptions);
}
