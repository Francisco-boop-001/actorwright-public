using System.Collections.Immutable;
using Mutagen.Bethesda.Skyrim;

namespace NpcManager.Formats.Bethesda;

internal static class BethesdaSkyrimNpcFinishCoreReindex
{
    internal static byte[] Rebase(byte[] original, SkyrimMod source, ImmutableArray<string> masters)
    {
        string[] before = source.ModHeader.MasterReferences.Select(row => row.Master.ToString()).ToArray();
        if (!masters.Take(before.Length).SequenceEqual(before, StringComparer.OrdinalIgnoreCase) || masters.Length > byte.MaxValue)
            throw new InvalidDataException("finish-core-master-reindex: The output must retain the source master prefix within the Skyrim owner-index range.");
        try { return BethesdaSkyrimMasterIndexRewriter.RewriteRecordIndices(original, source, masters); }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException("finish-core-master-reindex: " + exception.Message, exception);
        }
    }
}
