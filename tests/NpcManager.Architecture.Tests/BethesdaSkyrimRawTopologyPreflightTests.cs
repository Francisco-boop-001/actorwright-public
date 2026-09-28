using System.Buffers.Binary;
using Actorwright.PublicFixtures;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestBethesdaSkyrimRawTopologyPreflight()
    {
        string fixtureRoot = Path.Combine(
            ActorwrightWorkspace.ResolveRoot().Value, "tests", "fixtures", "skyrim-plugin-topology");
        string broken = Path.Combine(fixtureRoot, SyntheticSkyrimPluginTopology.OrphanFileName);
        string repaired = Path.Combine(fixtureRoot, SyntheticSkyrimPluginTopology.RepairedFileName);
        BethesdaSkyrimRawTopologyPreflight.Validate(repaired, CancellationToken.None);

        byte[] repairedBytes = File.ReadAllBytes(repaired);
        SyntheticTopologyLayout repairedLayout = SyntheticSkyrimPluginTopology.LocateTopology(repairedBytes);
        SyntheticTopologyLayout orphanLayout = SyntheticSkyrimPluginTopology.LocateTopology(
            File.ReadAllBytes(broken));
        string cellFormId = $"0x{repairedLayout.CellFormId:X8}";
        PluginReadDiagnosticException? orphan = null;
        try
        {
            BethesdaSkyrimRawTopologyPreflight.Validate(broken, CancellationToken.None);
        }
        catch (PluginReadDiagnosticException exception)
        {
            orphan = exception;
        }

        Assert(orphan?.Code == "orphan-cell-children", "Wrong coded diagnostic.");
        Assert(orphan is not null &&
               orphan.Message.Contains($"0x{orphanLayout.CellChildrenGroupOffset:X8}", StringComparison.Ordinal) &&
               orphan.Message.Contains("type=6", StringComparison.Ordinal) &&
               orphan.Message.Contains(cellFormId, StringComparison.Ordinal) &&
               orphan.Message.Contains("observed preceding sibling <none>", StringComparison.Ordinal),
            "The orphan diagnostic lost its structural context.");

        var labelMutation = repairedBytes.ToArray();
        uint wrongCellFormId = checked(repairedLayout.CellFormId + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(
            labelMutation.AsSpan(repairedLayout.CellChildrenGroupOffset + 8, 4), wrongCellFormId);
        AssertCodedDiagnostic(() => BethesdaSkyrimRawTopologyPreflight.Validate(
            labelMutation, CancellationToken.None),
            $"expected preceding CELL 0x{wrongCellFormId:X8}",
            $"observed preceding CELL {cellFormId}");

        var cellMutation = repairedBytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            cellMutation.AsSpan(repairedLayout.CellRecordOffset + 12, 4), wrongCellFormId);
        AssertCodedDiagnostic(() => BethesdaSkyrimRawTopologyPreflight.Validate(
            cellMutation, CancellationToken.None),
            $"expected preceding CELL {cellFormId}",
            $"observed preceding CELL 0x{wrongCellFormId:X8}");

        var signatureMutation = repairedBytes.ToArray();
        "ARMO"u8.CopyTo(signatureMutation.AsSpan(repairedLayout.CellRecordOffset, 4));
        AssertCodedDiagnostic(() => BethesdaSkyrimRawTopologyPreflight.Validate(
            signatureMutation, CancellationToken.None));

        var shortGroup = repairedBytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            shortGroup.AsSpan(repairedLayout.CellChildrenGroupOffset + 4, 4), 23);
        AssertPlainInvalidData(() => BethesdaSkyrimRawTopologyPreflight.Validate(
            shortGroup, CancellationToken.None));

        var truncated = repairedBytes.AsSpan(0, repairedLayout.CellChildrenGroupOffset + 23).ToArray();
        AssertPlainInvalidData(() => BethesdaSkyrimRawTopologyPreflight.Validate(
            truncated, CancellationToken.None));

        var childOverrun = repairedBytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(
            childOverrun.AsSpan(repairedLayout.CellChildrenGroupOffset + 4, 4), 25);
        AssertPlainInvalidData(() => BethesdaSkyrimRawTopologyPreflight.Validate(
            childOverrun, CancellationToken.None));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        AssertThrows<OperationCanceledException>(() => BethesdaSkyrimRawTopologyPreflight.Validate(
            repaired, cancelled.Token));

        var reader = new BethesdaPluginReader();
        PluginReadDiagnosticException? readerOrphan = null;
        try
        {
            await reader.ReadAsync(
                new PluginReadRequest(GameEdition.SkyrimSpecialEdition, new WorkspacePath(broken)),
                CancellationToken.None);
        }
        catch (PluginReadDiagnosticException exception)
        {
            readerOrphan = exception;
        }

        Assert(readerOrphan?.Code == "orphan-cell-children",
            "The Skyrim reader did not preserve the coded topology diagnostic.");
        var repairedInspection = await reader.ReadAsync(
            new PluginReadRequest(GameEdition.SkyrimSpecialEdition, new WorkspacePath(repaired)),
            CancellationToken.None);
        Assert(repairedInspection.Records.Length == 3,
            "The repaired synthetic topology plugin record count changed.");

        var fallout4 = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies",
            "m2-fixtures", "fo4", "Data", "M2FixtureFO4.esp");
        var fallout4Inspection = await reader.ReadAsync(
            new PluginReadRequest(GameEdition.Fallout4, new WorkspacePath(fallout4)),
            CancellationToken.None);
        Assert(fallout4Inspection.Edition == GameEdition.Fallout4,
            "The Fallout 4 reader path changed edition.");

        using var readerCancelled = new CancellationTokenSource();
        readerCancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(
            new PluginReadRequest(GameEdition.SkyrimSpecialEdition, new WorkspacePath(repaired)),
            readerCancelled.Token).AsTask());
    }

    private static void AssertCodedDiagnostic(Action action, params string[] requiredMessages)
    {
        try
        {
            action();
        }
        catch (PluginReadDiagnosticException exception)
        {
            Assert(exception.Code == "orphan-cell-children", "Wrong topology diagnostic code.");
            foreach (var requiredMessage in requiredMessages)
            {
                Assert(exception.Message.Contains(requiredMessage, StringComparison.Ordinal),
                    $"Topology diagnostic lost required context: {requiredMessage}");
            }

            return;
        }

        throw new InvalidOperationException("Expected orphan-cell-children diagnostic.");
    }

    private static void AssertPlainInvalidData(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException exception)
        {
            Assert(exception.GetType() == typeof(InvalidDataException),
                "Malformed topology was incorrectly promoted to a coded diagnostic.");
            return;
        }

        throw new InvalidOperationException("Expected plain InvalidDataException.");
    }
}
