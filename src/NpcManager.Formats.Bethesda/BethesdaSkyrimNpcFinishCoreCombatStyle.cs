using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;

namespace NpcManager.Formats.Bethesda;

internal static class BethesdaSkyrimNpcFinishCoreCombatStyle
{
    internal static CombatStyle ReadTemplate(
        ISkyrimModGetter source, INpcGetter npc, SkyrimNpcFinishCoreRequest request)
    {
        if (npc.CombatStyle.FormKeyNullable is not { } key || key.IsNull)
            throw new InvalidDataException("finish-core-combat-source: combatPolicy requires an existing NPC combat-style reference.");
        if (key.ModKey == source.ModKey)
            return CopyExact(source, key);
        if (request.CombatPolicy?.SeedLocalStyle != true)
            throw new InvalidDataException("finish-core-combat-seed-required: a master CSTY requires combatPolicy.seedLocalStyle=true.");
        if (!source.ModHeader.MasterReferences.Any(row => row.Master == key.ModKey))
            throw new InvalidDataException("finish-core-combat-master: the combat-style owner is not a source master.");

        string? path = null;
        string? hash = null;
        long? length = null;
        if (key.ModKey == ModKey.FromNameAndExtension("Skyrim.esm") && request.SandboxAuthority.CopiedMaster is { } copied)
        {
            path = copied.Value;
            hash = request.SandboxAuthority.CopiedMasterSha256?.Value;
        }
        else
        {
            var provider = request.Authorities.Providers.SingleOrDefault(row =>
                string.Equals(row.Plugin?.Value, key.ModKey.ToString(), StringComparison.OrdinalIgnoreCase));
            var additional = request.Authorities.AdditionalMasters.SingleOrDefault(row =>
                string.Equals(row.Plugin.Value, key.ModKey.ToString(), StringComparison.OrdinalIgnoreCase));
            path = provider?.Path?.Value ?? additional?.Path.Value;
            hash = provider?.Sha256?.Value ?? additional?.Sha256.Value;
            length = provider?.ByteLength ?? additional?.ByteLength;
        }
        if (path is null || hash is null || !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("finish-core-combat-authority: the referenced master CSTY needs an ordinary hash-bound master file.");
        using var boundMaster = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[checked((int)boundMaster.Length)];
        boundMaster.ReadExactly(bytes);
        if ((length is { } size && size != bytes.LongLength) ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("finish-core-combat-authority-hash: the referenced combat-style master changed.");
        using var master = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(key.ModKey, new FilePath(path)), SkyrimRelease.SkyrimSE);
        int ownerIndex = master.ModHeader.MasterReferences.Count;
        if (ownerIndex > byte.MaxValue)
            throw new InvalidDataException("finish-core-combat-master: the referenced master owner index exceeds the raw FormID range.");
        uint rawFormId = ((uint)ownerIndex << 24) | key.ID;
        if (BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(bytes).NonTes4Records.Count(row =>
                row.Signature == "CSTY" && row.RawFormId == rawFormId) != 1)
            throw new InvalidDataException("finish-core-combat-source-count: the referenced master requires exactly one CSTY with the requested owner and local ID.");
        return CopyExact(master, key);
    }

    private static CombatStyle CopyExact(ISkyrimModGetter mod, FormKey key)
    {
        ICombatStyleGetter[] matches = mod.CombatStyles.Where(row => row.FormKey == key).Take(2).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("finish-core-combat-source-count: the referenced source requires exactly one CSTY.");
        CombatStyle style = (CombatStyle)matches[0].DeepCopy();
        float[] values = [style.OffensiveMult, style.DefensiveMult, style.EquipmentScoreMultMelee,
            style.EquipmentScoreMultRanged, style.EquipmentScoreMultMagic, style.EquipmentScoreMultStaff,
            style.EquipmentScoreMultShout, style.EquipmentScoreMultUnarmed];
        if (values.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("finish-core-combat-profile-finite: combat profile inputs must be finite.");
        return style;
    }

    internal static float PreferredEquipmentScore(ICombatStyleGetter style)
    {
        float maximum = new[] { style.EquipmentScoreMultMelee, style.EquipmentScoreMultRanged,
            style.EquipmentScoreMultMagic, style.EquipmentScoreMultStaff,
            style.EquipmentScoreMultShout, style.EquipmentScoreMultUnarmed, 1f }.Max();
        float preferred = maximum + 1f;
        if (!float.IsFinite(preferred) || preferred <= maximum)
            throw new InvalidDataException("finish-core-combat-profile-finite: equipment preference cannot be represented as a finite greater value.");
        return preferred;
    }

    internal static void ApplyProfile(
        CombatStyle style,
        ICombatStyleGetter template,
        SkyrimNpcFinishCoreCombatPolicy policy)
    {
        switch (policy.Profile)
        {
            case SkyrimNpcFinishCoreCombatProfile.Defensive:
                style.OffensiveMult = template.OffensiveMult * 0.5f;
                style.DefensiveMult = Math.Max(template.DefensiveMult, 1f);
                break;
            case SkyrimNpcFinishCoreCombatProfile.RangedFirst:
                style.EquipmentScoreMultRanged = PreferredEquipmentScore(template);
                break;
            case SkyrimNpcFinishCoreCombatProfile.MeleeFirst:
                style.EquipmentScoreMultMelee = PreferredEquipmentScore(template);
                break;
        }
    }
}
