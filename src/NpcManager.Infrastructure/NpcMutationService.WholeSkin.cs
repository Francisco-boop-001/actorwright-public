using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class NpcMutationService
{
    private void ValidateWholeSkin(NpcWholeSkinPatch? patch, GameEdition edition, WorkspacePath source,
        FormId targetFormId, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch is null) return;
        try
        {
            if (!Enum.IsDefined(patch.HeadPolicy))
                throw new InvalidDataException("wholeSkin has an undefined typed head policy.");
            if (edition != GameEdition.SkyrimSpecialEdition)
                throw new InvalidDataException("wholeSkin supports female Skyrim SE NPCs only.");
            if (!patch.DataRoot.IsUnder(labRoot) || !Directory.Exists(patch.DataRoot.Value))
                throw new InvalidDataException("wholeSkin.dataRoot must be an existing copied workspace Data root.");
            diagnostics.AddRange(policy.Evaluate(labRoot, patch.DataRoot));
            NpcMutationPathPolicy.AddReparseDiagnostic(diagnostics, patch.DataRoot.Value, "wholeSkin.dataRoot");
            if (diagnostics.Any(row => row.Severity == DiagnosticSeverity.Error)) return;
            if (new Sha256Hash(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(patch.DocumentJson)))) != patch.DocumentSha256)
                throw new InvalidDataException("wholeSkin document hash changed after admission.");
            using (JsonDocument document = JsonDocument.Parse(patch.DocumentJson))
            {
                JsonElement root = document.RootElement;
                JsonProperty[] policies = root.EnumerateObject().Where(row => row.NameEquals("headPolicy")).ToArray();
                bool documentPreserves = policies.Length == 1 && policies[0].Value.ValueKind == JsonValueKind.String &&
                    policies[0].Value.GetString() == "preserve";
                RequireExactWholeSkinObject(root, patch.PreservesHead
                    ? ["schemaVersion", "dataRoot", "pluginAuthorities", "headPolicy", "body", "hands", "preservedAssets"]
                    : ["schemaVersion", "dataRoot", "pluginAuthorities", "head", "body", "hands"]);
                if (root.GetProperty("schemaVersion").GetInt32() != 1 || (!patch.PreservesHead && !patch.PreservedAssets.IsEmpty))
                    throw new InvalidDataException("wholeSkin typed values differ from the versioned bound document contract.");
                if (documentPreserves != patch.PreservesHead ||
                    (patch.PreservesHead && (root.TryGetProperty("head", out _) || patch.Head.Count != 0)) ||
                    (!patch.PreservesHead && (policies.Length != 0 || !root.TryGetProperty("head", out _))))
                    throw new InvalidDataException("wholeSkin typed head policy differs from its bound document.");
                if (new WorkspacePath(root.GetProperty("dataRoot").GetString()!) != patch.DataRoot)
                    throw new InvalidDataException("wholeSkin typed Data root differs from its bound document.");
                var providerRows = root.GetProperty("pluginAuthorities").EnumerateArray().ToArray();
                foreach (JsonElement row in providerRows)
                    RequireExactWholeSkinObject(row, ["plugin", "path", "sha256"]);
                if (providerRows.Length != patch.PluginAuthorities.Length || providerRows.Where((row, index) =>
                    new PluginName(row.GetProperty("plugin").GetString()!) != patch.PluginAuthorities[index].Plugin ||
                    new WorkspacePath(row.GetProperty("path").GetString()!) != patch.PluginAuthorities[index].PluginPath ||
                    new Sha256Hash(row.GetProperty("sha256").GetString()!) != patch.PluginAuthorities[index].ExpectedSha256).Any())
                    throw new InvalidDataException("wholeSkin typed providers differ from their bound document.");
                foreach (var (name, set) in patch.PreservesHead
                    ? new[] { ("body", patch.Body), ("hands", patch.Hands) }
                    : new[] { ("head", patch.Head), ("body", patch.Body), ("hands", patch.Hands) })
                {
                    string[] required = name == "head"
                        ? ["diffuse", "normalOrGloss", "glowOrDetailMap", "backlightMaskOrSpecular", "height"]
                        : ["diffuse", "normalOrGloss", "glowOrDetailMap", "backlightMaskOrSpecular"];
                    if (required.Any(key => !set.ContainsKey(key)))
                        throw new InvalidDataException("wholeSkin typed textures omit a required slot.");
                    JsonElement textureRows = root.GetProperty(name);
                    RequireExactWholeSkinObject(textureRows, set.Keys.ToArray());
                    foreach (JsonProperty row in textureRows.EnumerateObject())
                        RequireExactWholeSkinObject(row.Value, ["path", "sha256"]);
                    if (textureRows.EnumerateObject().Count() != set.Count || set.Any(row =>
                        !textureRows.TryGetProperty(row.Key, out var entry) ||
                        new AssetPath(entry.GetProperty("path").GetString()!) != row.Value.Path ||
                        new Sha256Hash(entry.GetProperty("sha256").GetString()!) != row.Value.Sha256))
                        throw new InvalidDataException("wholeSkin typed textures differ from their bound document.");
                }
                if (patch.PreservesHead)
                {
                    JsonElement rows = root.GetProperty("preservedAssets");
                    JsonElement[] items = rows.EnumerateArray().ToArray();
                    foreach (JsonElement item in items)
                        RequireExactWholeSkinObject(item, ["path", "sha256"]);
                    if (items.Length != patch.PreservedAssets.Length || items.Where((item, index) =>
                        !string.Equals(item.GetProperty("path").GetString(), patch.PreservedAssets[index].Path.Value, StringComparison.Ordinal) ||
                        new Sha256Hash(item.GetProperty("sha256").GetString()!) != patch.PreservedAssets[index].Sha256).Any())
                        throw new InvalidDataException("wholeSkin typed preservedAssets differ from their bound document.");
                }
            }
            if (patch.PluginAuthorities.Length is 0 or > 64 ||
                patch.PluginAuthorities.Select(row => row.Plugin).Distinct().Count() != patch.PluginAuthorities.Length ||
                patch.PluginAuthorities[^1].PluginPath != source)
                throw new InvalidDataException("wholeSkin.pluginAuthorities requires 1-64 unique ordered copied providers with the exact input plugin last.");
            foreach (NpcCreationPluginAuthority authority in patch.PluginAuthorities)
            {
                if (!authority.PluginPath.IsUnder(patch.DataRoot) ||
                    !string.Equals(Path.GetFileName(authority.PluginPath.Value), authority.Plugin.Value, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("wholeSkin provider filename/path does not match its copied Data authority.");
                Check(authority.PluginPath, authority.ExpectedSha256, 512L * 1024 * 1024, "provider");
            }
            foreach (var set in new[] { patch.Head, patch.Body, patch.Hands })
            foreach (NpcWholeSkinTexture texture in set.Values)
            {
                if (!texture.Path.Value.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase) ||
                    !texture.Path.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("wholeSkin textures must be canonical Data-relative Textures/*.dds paths.");
                var path = new WorkspacePath(Path.Combine(patch.DataRoot.Value, texture.Path.Value.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.IsUnder(patch.DataRoot)) throw new InvalidDataException("wholeSkin texture escaped the copied Data root.");
                Check(path, texture.Sha256, 128L * 1024 * 1024, "texture");
                using var input = File.OpenRead(path.Value);
                Span<byte> header = new byte[4];
                if (input.Read(header) != 4 || !header.SequenceEqual("DDS "u8))
                    throw new InvalidDataException("wholeSkin texture is not a DDS file: " + texture.Path);
            }
            if (patch.PreservesHead)
            {
                if (patch.PreservedAssets.Length is < 4 or > 256 ||
                    patch.PreservedAssets.Select(row => row.Path.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != patch.PreservedAssets.Length)
                    throw new InvalidDataException("wholeSkin.preservedAssets requires 4-256 unique canonical paths.");
                string[] paths = patch.PreservedAssets.Select(row => row.Path.Value).ToArray();
                string plugin = Path.GetFileName(source.Value);
                string target = targetFormId.Value.ToString("X8");
                string expectedFaceGeom = $"Meshes/Actors/Character/FaceGenData/FaceGeom/{plugin}/{target}.nif";
                string expectedFaceTint = $"Textures/Actors/Character/FaceGenData/FaceTint/{plugin}/{target}.dds";
                if (!paths.Contains(expectedFaceGeom, StringComparer.OrdinalIgnoreCase) ||
                    !paths.Contains(expectedFaceTint, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "wholeSkin.preservedAssets must bind the exact target NPC FaceGen identity.");
                if (!paths.Any(path => path.StartsWith("Meshes/Actors/Character/FaceGenData/FaceGeom/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) ||
                    !paths.Any(path => path.StartsWith("Textures/Actors/Character/FaceGenData/FaceTint/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)) ||
                    !paths.Any(path => path.EndsWith(".tri", StringComparison.OrdinalIgnoreCase)) ||
                    !paths.Any(path => path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && !path.Contains("/FaceGeom/", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("wholeSkin.preservedAssets must declare accepted FaceGeom NIF, FaceTint DDS, hair NIF, and TRI files.");
                foreach (NpcWholeSkinTexture asset in patch.PreservedAssets)
                {
                    if (!(asset.Path.Value.StartsWith("Meshes/", StringComparison.OrdinalIgnoreCase) || asset.Path.Value.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)) ||
                        !(asset.Path.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
                          asset.Path.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ||
                          asset.Path.Value.EndsWith(".tri", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("wholeSkin.preservedAssets accepts canonical Meshes/Textures NIF, DDS, and TRI files only.");
                    var path = new WorkspacePath(Path.Combine(patch.DataRoot.Value, asset.Path.Value.Replace('/', Path.DirectorySeparatorChar)));
                    if (!path.IsUnder(patch.DataRoot)) throw new InvalidDataException("wholeSkin preserved asset escaped the copied Data root.");
                    Check(path, asset.Sha256, 512L * 1024 * 1024, "preserved asset");
                }
            }
            void Check(WorkspacePath path, Sha256Hash expected, long maximum, string role)
            {
                NpcMutationPathPolicy.AddReparseDiagnostic(diagnostics, path.Value, "wholeSkin." + role);
                if (diagnostics.Any(row => row.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException("wholeSkin authority traverses a reparse point.");
                if (!File.Exists(path.Value) || new FileInfo(path.Value).Length is <= 0 || new FileInfo(path.Value).Length > maximum || ComputeHash(path.Value) != expected)
                    throw new InvalidDataException($"wholeSkin {role} SHA-256/ordinary-file binding failed: {path}.");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            diagnostics.Add(new Diagnostic("npc-whole-skin-authority", DiagnosticSeverity.Error, exception.Message));
        }
    }

    private static void RequireExactWholeSkinObject(JsonElement element, string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("wholeSkin bound document contains a non-object row.");
        string[] actual = element.EnumerateObject().Select(row => row.Name).ToArray();
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            actual.Any(name => !expected.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("wholeSkin typed values do not exactly cover their bound document fields.");
    }
}
