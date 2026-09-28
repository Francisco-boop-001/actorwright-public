using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen.Tests;

internal static partial class Program
{
    private const string Emi2FaceTintPath =
        @"K:\ExampleWorkspace\projects\Emi2FreshBuild\01-source-copies\fresh-export-drop\emi2-neutral.dds";

    private static void TestBgra8FaceTintTextureDecoder()
    {
        var root = new WorkspacePath(@"K:\ExampleWorkspace");
        var primary = new Bgra8FaceTintTextureDecoder(root);
        var fallback = new RefusingFallbackDecoder();
        var decoder = new ChainedFaceTintTextureDecoder(primary, fallback);
        var decoded = decoder.DecodeAsync(
                new WorkspacePath(Emi2FaceTintPath), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();
        Assert(decoded.Decoded && decoded.Width == 2048 && decoded.Height == 2048 &&
               decoded.Bytes is { Length: 2048 * 2048 * 4 } &&
               decoded.SourceSha256 == new Sha256Hash(
                   "B3E89813D2A6F1CA2D6742B7EF1BFE04FB7CD939451A3D2C7372B96A412369F9") &&
               fallback.Calls == 0,
            "The real RaceMenu FaceTint did not decode exactly in-process: " +
            Format(decoded.Diagnostics));

        string scratch = Path.Combine(root.Value, "projects", "NpcManagerReimplementation",
            "03-builds", "work", "bgra8-decoder-proof");
        Directory.CreateDirectory(scratch);
        string refusedPath = Path.Combine(scratch,
            $"compressed-{Guid.NewGuid():N}.dds");
        try
        {
            byte[] bytes = File.ReadAllBytes(Emi2FaceTintPath);
            "DX10"u8.CopyTo(bytes.AsSpan(84, 4));
            File.WriteAllBytes(refusedPath, bytes);
            var refused = primary.DecodeAsync(
                    new WorkspacePath(refusedPath), CancellationToken.None)
                .AsTask().GetAwaiter().GetResult();
            Assert(!refused.Decoded && refused.Diagnostics.Any(item =>
                    item.Code == "facetint-bgra8-format-refused"),
                "The in-process RaceMenu decoder accepted a non-BGRA8 DDS marker.");
        }
        finally
        {
            if (File.Exists(refusedPath)) File.Delete(refusedPath);
        }
    }

    private sealed class RefusingFallbackDecoder : IFaceTintTextureDecoder
    {
        public int Calls { get; private set; }

        public ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
            WorkspacePath sourceDds,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new FaceTintTextureDecodeResult(
                false, 0, 0, null, null,
                [new Diagnostic("test-fallback-called", DiagnosticSeverity.Error,
                    "The external fallback must not run for a RaceMenu RGBA8 FaceTint.")]));
        }
    }
}
