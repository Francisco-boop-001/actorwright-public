using System.Text.Json.Serialization;

namespace NpcManager.Assets;

internal sealed class SkyrimFaceBakeAuthorityDocumentDto
{
    [JsonRequired]
    public int SchemaVersion { get; init; }

    [JsonRequired]
    public string? AuthorityId { get; init; }

    [JsonRequired]
    public List<OrderedPluginDto?>? LoadedPlugins { get; init; }

    [JsonRequired]
    public List<RecordPluginDto?>? RecordPlugins { get; init; }

    [JsonRequired]
    public List<AuthorityAssetDto?>? Assets { get; init; }

    [JsonRequired]
    public List<CatalogConfigDto?>? CatalogConfigs { get; init; }

    [JsonRequired]
    public List<ShapeTriInputsDto?>? ShapeTriInputs { get; init; }

    [JsonRequired]
    public List<CarrierShapeDto?>? CarrierShapes { get; init; }

    [JsonRequired]
    public List<RecordOnlyMappedHeadPartDto?>? RecordOnlyMappedHeadParts { get; init; }

    [JsonRequired]
    public List<OptionalUnavailableExtensionDto?>? OptionalUnavailableExtensions { get; init; }
}

internal sealed class OrderedPluginDto
{
    [JsonRequired]
    public int Order { get; init; }

    [JsonRequired]
    public string? Plugin { get; init; }
}

internal sealed class RecordPluginDto
{
    [JsonRequired]
    public int Order { get; init; }

    [JsonRequired]
    public string? Plugin { get; init; }

    [JsonRequired]
    public string? Path { get; init; }

    [JsonRequired]
    public string? Sha256 { get; init; }
}

internal sealed class AuthorityAssetDto
{
    [JsonRequired]
    public int Order { get; init; }

    [JsonRequired]
    public string? Id { get; init; }

    [JsonRequired]
    public string? ProviderId { get; init; }

    [JsonRequired]
    public string? ProviderKind { get; init; }

    [JsonRequired]
    public string? ProviderPath { get; init; }

    [JsonRequired]
    public string? ProviderSha256 { get; init; }

    [JsonRequired]
    public string? AssetPath { get; init; }

    [JsonRequired]
    public long ContentLength { get; init; }

    [JsonRequired]
    public string? ContentSha256 { get; init; }
}

internal sealed class CatalogConfigDto
{
    [JsonRequired]
    public int Order { get; init; }

    [JsonRequired]
    public string? Plugin { get; init; }

    [JsonRequired]
    public string? AssetId { get; init; }
}

internal sealed class ShapeTriInputsDto
{
    [JsonRequired]
    public int Order { get; init; }

    [JsonRequired]
    public string? CarrierShapeName { get; init; }

    [JsonRequired]
    public string? RaceAssetId { get; init; }

    [JsonRequired]
    public string? ChargenAssetId { get; init; }

    [JsonRequired]
    public string? MeshAssetId { get; init; }

    [JsonRequired]
    public List<string?>? ExtendedAssetIds { get; init; }
}

internal sealed class CarrierShapeDto
{
    [JsonRequired]
    public int Order { get; init; }

    [JsonRequired]
    public string? HeadPart { get; init; }

    [JsonRequired]
    public string? ModelAssetId { get; init; }

    [JsonRequired]
    public string? ModelShapeName { get; init; }

    [JsonRequired]
    public string? CarrierShapeName { get; init; }

    [JsonRequired]
    public string? ExpectedModelPositionSha256 { get; init; }

    [JsonRequired]
    public string? ExpectedModelTopologySha256 { get; init; }

    [JsonRequired]
    public string? ExpectedCarrierTopologySha256 { get; init; }
}

internal sealed class OptionalUnavailableExtensionDto
{
    [JsonRequired]
    public int Order { get; init; }

    [JsonRequired]
    public string? AssetPath { get; init; }
}

internal sealed class RecordOnlyMappedHeadPartDto
{
    [JsonRequired]
    public int Order { get; init; }

    [JsonRequired]
    public string? HeadPart { get; init; }
}
