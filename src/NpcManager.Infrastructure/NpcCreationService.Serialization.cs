using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class NpcCreationService
{
    private static readonly JsonSerializerOptions ProposalJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static void WriteProposal(NpcCreationProposal proposal)
    {
        var bytes = SerializeProposal(proposal);
        var temporary = proposal.Proposal.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, proposal.Proposal.Value, overwrite: false);
        }
        finally
        {
            TryDeleteTransient(temporary);
        }
    }

    private static byte[] SerializeProposal(NpcCreationProposal proposal) =>
        JsonSerializer.SerializeToUtf8Bytes(CreateDocument(proposal), ProposalJsonOptions);

    private static ProposalDocument CreateDocument(NpcCreationProposal proposal) => new(
        "3",
        "skyrim-npc-creation-proposal",
        proposal.Edition.ToWireName(),
        proposal.TemplatePlugin.Value,
        proposal.TemplateHash.Value,
        proposal.TemplateNpcFormId.ToString(),
        proposal.Proposal.Value,
        proposal.Output.Value,
        proposal.OutputPlugin.Value,
        proposal.PluginType,
        proposal.AllocatedFormId.ToString(),
        proposal.Masters.Select(item => item.Value).ToImmutableArray(),
        proposal.PluginAuthorities.Select(item => new PluginAuthorityDocument(
            item.Plugin.Value,
            item.PluginPath.Value,
            item.ExpectedSha256.Value)).ToImmutableArray(),
        new IdentityDocument(proposal.Identity.EditorId.Value, proposal.Identity.Name.Value),
        new TraitsDocument(proposal.Traits.Sex, proposal.Traits.Role, proposal.Traits.IsUnique,
            proposal.Traits.IsEssential, proposal.Traits.IsProtected, proposal.Traits.Respawns,
            proposal.Traits.AutoCalcStats),
        new ReferencesDocument(proposal.References.Race.ToString(), proposal.References.Voice.ToString(),
            proposal.References.Class.ToString(), proposal.References.CombatStyle.ToString(),
            proposal.References.DefaultOutfit?.ToString()),
        CreateAppearanceDocument(proposal.Appearance),
        new StatsDocument(proposal.Stats.Level.Mode, proposal.Stats.Level.Value,
            proposal.Stats.MagickaOffset, proposal.Stats.StaminaOffset, proposal.Stats.HealthOffset,
            proposal.Stats.CalcMinLevel, proposal.Stats.CalcMaxLevel, proposal.Stats.SpeedMultiplier,
            proposal.Stats.DispositionBase, proposal.Stats.BleedoutOverride, proposal.Stats.BaseHealth,
            proposal.Stats.BaseMagicka, proposal.Stats.BaseStamina, proposal.Stats.Height,
            proposal.Stats.Weight, proposal.Stats.FarAwayModelDistance),
        proposal.RuntimeAppearance);

    private static AppearanceDocument CreateAppearanceDocument(
        NpcCreationAppearanceSource appearance)
    {
        if (appearance is null)
        {
            throw new JsonException("The NPC creation appearance source is missing.");
        }
        return appearance switch
        {
            TemplateCarrierNpcAppearanceSource => new("template-carrier", null),
            FullyAuthoredSkyrimNpcAppearanceSource authored => new(
                "fully-authored-skyrim", NpcAppearanceProposalJson.Create(authored)),
            _ => throw new JsonException("The NPC creation appearance source is unsupported.")
        };
    }

    private sealed record ProposalDocument(
        string SchemaVersion,
        string ArtifactKind,
        string Edition,
        string TemplatePlugin,
        string TemplateSha256,
        string TemplateNpcFormId,
        string Proposal,
        string Output,
        string OutputPlugin,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        BlankNpcPluginType PluginType,
        string AllocatedFormId,
        ImmutableArray<string> Masters,
        ImmutableArray<PluginAuthorityDocument> PluginAuthorities,
        IdentityDocument Identity,
        TraitsDocument Traits,
        ReferencesDocument References,
        AppearanceDocument Appearance,
        StatsDocument Stats,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SkyrimNpcApplySseVmadPayload? RuntimeAppearance);

    private sealed record PluginAuthorityDocument(
        string Plugin,
        string PluginPath,
        string ExpectedSha256);

    private sealed record IdentityDocument(string EditorId, string Name);
    private sealed record TraitsDocument(NpcSex Sex, NpcCreationRole Role, bool IsUnique,
        bool IsEssential, bool IsProtected, bool Respawns, bool AutoCalcStats);
    private sealed record ReferencesDocument(string Race, string Voice, string Class,
        string CombatStyle, string? DefaultOutfit);
    private sealed record AppearanceDocument(
        string Kind,
        NpcAppearanceProposalJson.FullyAuthoredDocument? FullyAuthored);
    private sealed record StatsDocument(NpcLevelMode LevelMode, decimal Level, short MagickaOffset,
        short StaminaOffset, short HealthOffset, ushort CalcMinLevel, ushort CalcMaxLevel,
        short SpeedMultiplier, short DispositionBase, short BleedoutOverride, ushort BaseHealth,
        ushort BaseMagicka, ushort BaseStamina, float Height, float Weight, ushort FarAwayModelDistance);
}
