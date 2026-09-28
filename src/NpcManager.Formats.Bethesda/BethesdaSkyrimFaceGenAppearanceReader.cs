using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Reads PNAM/RNAM/sex/HCLF and winning CLFM from already admitted copied authorities.</summary>
public sealed class BethesdaSkyrimFaceGenAppearanceReader(
    IWorkspacePolicy policy, WorkspacePath labRoot)
{
    public async ValueTask<SkyrimFaceGenRecordAppearance> ReadAsync(
        FormReference npc,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> authorities,
        CancellationToken cancellationToken)
    {
        var npcKey = new FormKey(ModKey.FromNameAndExtension(npc.Plugin.Value), npc.FormId.Value);
        SkyrimFaceGenRecordAppearance? appearance = null;
        var colors = new Dictionary<FormKey, uint?>();
        foreach (SkyrimFaceRecordPluginAuthority authority in authorities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (policy.EvaluateReadRoot(labRoot, authority.Path).Any(item =>
                    item.Severity == DiagnosticSeverity.Error))
                throw new InvalidDataException($"Appearance authority '{authority.Path}' is outside the admitted workspace.");
            var info = new FileInfo(authority.Path.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > 2L * 1024 * 1024 * 1024 ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException($"Appearance authority '{authority.Plugin}' is not an ordinary bounded plugin.");

            // Keep the read lock through overlay projection so the hash and records refer to the same file.
            await using var stream = new FileStream(authority.Path.Value, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
            if (hash != authority.ExpectedSha256)
                throw new InvalidDataException($"Appearance authority '{authority.Plugin}' changed after binding.");
            using var mod = SkyrimMod.CreateFromBinaryOverlay(authority.Path.Value, SkyrimRelease.SkyrimSE);
            if (!string.Equals(mod.ModKey.ToString(), authority.Plugin.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Appearance authority '{authority.Plugin}' has a different plugin identity.");
            INpcGetter[] matches = mod.Npcs.Where(record => record.FormKey == npcKey).Take(2).ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException($"Appearance authority repeats NPC {npc}.");
            if (matches.Length == 1)
            {
                INpcGetter record = matches[0];
                appearance = record.IsDeleted || record.Race.FormKey.IsNull ? null : new(
                    authority.Plugin,
                    record.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? NpcSex.Female : NpcSex.Male,
                    Reference(record.Race.FormKey),
                    record.HeadParts.Select(link => Reference(link.FormKey)).ToImmutableArray(),
                    record.HairColor.FormKeyNullable is { IsNull: false } colorKey ? Reference(colorKey) : null,
                    null);
            }
            foreach (IColorRecordGetter color in mod.Colors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                colors[color.FormKey] = color.IsDeleted ? null :
                    ((uint)color.Color.R << 16) | ((uint)color.Color.G << 8) | color.Color.B;
            }
        }
        if (appearance is null)
            throw new InvalidDataException($"Winning NPC {npc} is absent, deleted, or has no RACE authority.");
        if (appearance.HairColor is { } reference)
        {
            var colorKey = new FormKey(ModKey.FromNameAndExtension(reference.Plugin.Value), reference.FormId.Value);
            if (!colors.TryGetValue(colorKey, out uint? rgb) || rgb is null)
                throw new InvalidDataException($"NPC {npc} HCLF references unavailable winning CLFM {reference}.");
            appearance = appearance with { HairColorPackedRgb = rgb };
        }
        return appearance;
    }

    private static FormReference Reference(FormKey key) =>
        new(new PluginName(key.ModKey.ToString()), new FormId(key.ID));
}
