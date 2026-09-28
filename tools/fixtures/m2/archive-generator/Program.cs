using System.Security.Cryptography;
using System.Text;
using BSA_BA2_Library_DLL.BethesdaArchive.Core;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: archive-generator <m2-fixture-root>");
    return 2;
}

var fixtureRoot = Path.GetFullPath(args[0]);
const string projectRoot = "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation";
var projectRootPath = Path.GetFullPath(projectRoot);
var relativeFixtureRoot = Path.GetRelativePath(projectRootPath, fixtureRoot);
if (!Path.IsPathFullyQualified(fixtureRoot) || Path.IsPathRooted(relativeFixtureRoot) ||
    relativeFixtureRoot.Equals("..", StringComparison.Ordinal) || relativeFixtureRoot.StartsWith("..\\", StringComparison.Ordinal) ||
    relativeFixtureRoot.StartsWith("../", StringComparison.Ordinal))
    throw new InvalidOperationException("Archive fixtures must remain under the K-local project root.");

var entry = new VirtualEntry
{
    Directory = "meshes\\m2-fixture",
    FileName = "head.nif",
    Data = Encoding.UTF8.GetBytes("archive-provider-head"),
    PreferCompress = false
};

var ba2 = Ba2WriterGNRL.Build([entry], new Ba2WriterGNRL.Options
{
    Version = 1,
    IncludeStrings = true,
    Encoding = Encoding.UTF8,
    CompressionFormat = Ba2WriterCommon.CompressionFormat.Zip
});
var bsa = BuildBsa(entry);
var outputs = new[]
{
    (Path.Combine(fixtureRoot, "fo4", "Data", "M2Fixture - Main.ba2"), ba2),
    (Path.Combine(fixtureRoot, "sse", "Data", "M2Fixture.bsa"), bsa)
};

foreach (var (path, bytes) in outputs)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (File.Exists(path))
    {
        if (!CryptographicOperations.FixedTimeEquals(File.ReadAllBytes(path), bytes))
            throw new IOException($"Existing fixture differs from deterministic output: {path}");
    }
    else
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
    Console.WriteLine($"{Path.GetFileName(path)} {bytes.Length} {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
}

return 0;

static byte[] BuildBsa(VirtualEntry entry)
{
    var options = new BsaWriter.Options
    {
        Encoding = Encoding.UTF8,
        UseDirectoryStrings = true,
        UseFileStrings = true,
        GlobalCompressed = false,
        EmbedNames = false
    };
    using var stream = new MemoryStream();
    BsaWriter.Write(stream, [entry], options);
    return stream.ToArray();
}
