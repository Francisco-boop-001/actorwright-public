using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reads only the NPC-record fields that can seed the bounded production body
/// transaction. RaceMenu sidecars are intentionally not inferred from a
/// plugin record.
/// </summary>
public sealed class BethesdaSkyrimBodyEditSourceReader : ISkyrimBodyEditSourceReader
{
    public SkyrimBodyEditSourceSnapshot Read(
        WorkspacePath pluginPath,
        FormId targetFormId)
    {
        BethesdaNpcSnapshot snapshot = BethesdaNpcMutationAdapter.Read(
            GameEdition.SkyrimSpecialEdition,
            pluginPath,
            targetFormId);
        if (string.IsNullOrWhiteSpace(snapshot.EditorId))
        {
            throw new InvalidDataException(
                $"NPC {targetFormId} has no exact EditorID authority.");
        }
        return new SkyrimBodyEditSourceSnapshot(
            new EditorId(snapshot.EditorId),
            snapshot.SkyrimWeight);
    }
}
