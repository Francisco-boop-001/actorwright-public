using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed partial class MutationCommandHandler
{
    private static bool TryWholeSkin(ParsedCommand command, out NpcWholeSkinPatch? patch, out string error)
    {
        patch = null;
        error = string.Empty;
        bool supplied = command.Options.TryGetValue("whole-skin", out string? value);
        bool hashSupplied = command.Options.TryGetValue("whole-skin-sha256", out string? digest);
        if (!supplied && !hashSupplied) return true;
        try
        {
            if (!supplied || !hashSupplied)
                throw new InvalidDataException("--whole-skin and --whole-skin-sha256 are required together.");
            byte[] bytes;
            if (value!.StartsWith('@'))
            {
                var path = new WorkspacePath(value[1..]);
                if (!path.IsUnder(ActorwrightWorkspace.ResolveRoot()) || HasReparsePath(path.Value) ||
                    !File.Exists(path.Value) || new FileInfo(path.Value).Length > 1024 * 1024)
                    throw new InvalidDataException("--whole-skin requires an ordinary workspace file of at most 1 MiB.");
                bytes = File.ReadAllBytes(path.Value);
            }
            else bytes = Encoding.UTF8.GetBytes(value);
            var expected = new Sha256Hash(digest!);
            if (bytes.Length > 1024 * 1024 || new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))) != expected)
                throw new InvalidDataException("--whole-skin document SHA-256 does not match its exact bytes or exceeds 1 MiB.");
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            JsonElement root = document.RootElement;
            JsonProperty[] policies = root.EnumerateObject().Where(item => item.NameEquals("headPolicy")).ToArray();
            bool preservesHead = policies.Length switch
            {
                0 => false,
                1 when policies[0].Value.ValueKind == JsonValueKind.String && policies[0].Value.GetString() == "preserve" => true,
                1 => throw new InvalidDataException("Unknown wholeSkin headPolicy; admitted value is preserve."),
                _ => throw new InvalidDataException("Unknown or duplicate wholeSkin field: headPolicy")
            };
            Shape(root,
                preservesHead
                    ? ["schemaVersion", "dataRoot", "pluginAuthorities", "headPolicy", "body", "hands", "preservedAssets"]
                    : ["schemaVersion", "dataRoot", "pluginAuthorities", "head", "body", "hands"]);
            if (root.GetProperty("schemaVersion").GetInt32() != 1)
                throw new InvalidDataException("wholeSkin requires schemaVersion 1.");
            var providers = ImmutableArray.CreateBuilder<NpcCreationPluginAuthority>();
            foreach (JsonElement item in root.GetProperty("pluginAuthorities").EnumerateArray())
            {
                Shape(item, ["plugin", "path", "sha256"]);
                providers.Add(new(new PluginName(Text(item, "plugin")), new WorkspacePath(Text(item, "path")), new Sha256Hash(Text(item, "sha256"))));
            }
            var preservedAssets = ImmutableArray.CreateBuilder<NpcWholeSkinTexture>();
            if (preservesHead)
            {
                JsonElement inventory = root.GetProperty("preservedAssets");
                if (inventory.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("wholeSkin.preservedAssets must be an array.");
                foreach (JsonElement item in inventory.EnumerateArray())
                {
                    Shape(item, ["path", "sha256"]);
                    string pathText = Text(item, "path");
                    var assetPath = new AssetPath(pathText);
                    if (!string.Equals(pathText, assetPath.Value, StringComparison.Ordinal))
                        throw new InvalidDataException("wholeSkin.preservedAssets paths must use canonical Data-relative forward-slash spelling.");
                    preservedAssets.Add(new(assetPath, new Sha256Hash(Text(item, "sha256"))));
                }
            }
            patch = new(new WorkspacePath(Text(root, "dataRoot")), providers.ToImmutable(),
                preservesHead ? ImmutableDictionary<string, NpcWholeSkinTexture>.Empty : Textures(root.GetProperty("head"), head: true),
                Textures(root.GetProperty("body"), head: false), Textures(root.GetProperty("hands"), head: false),
                Encoding.UTF8.GetString(bytes), expected)
            {
                HeadPolicy = preservesHead ? NpcWholeSkinHeadPolicy.Preserve : NpcWholeSkinHeadPolicy.Replace,
                PreservedAssets = preservedAssets.ToImmutable()
            };
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            error = "wholeSkin: " + exception.Message;
            return false;
        }
    }

    private static ImmutableDictionary<string, NpcWholeSkinTexture> Textures(JsonElement element, bool head)
    {
        string[] required = head
            ? ["diffuse", "normalOrGloss", "glowOrDetailMap", "backlightMaskOrSpecular", "height"]
            : ["diffuse", "normalOrGloss", "glowOrDetailMap", "backlightMaskOrSpecular"];
        Shape(element, required, ["height", "environmentMaskOrSubsurfaceTint", "environment", "multilayer"]);
        return element.EnumerateObject().ToImmutableDictionary(item => item.Name, item =>
        {
            Shape(item.Value, ["path", "sha256"]);
            return new NpcWholeSkinTexture(new AssetPath(Text(item.Value, "path")), new Sha256Hash(Text(item.Value, "sha256")));
        }, StringComparer.Ordinal);
    }

    private static void Shape(JsonElement element, string[] required, string[]? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected a wholeSkin object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty item in element.EnumerateObject())
            if (!names.Add(item.Name) || (!required.Contains(item.Name, StringComparer.Ordinal) && !(optional ?? []).Contains(item.Name, StringComparer.Ordinal)))
                throw new InvalidDataException("Unknown or duplicate wholeSkin field: " + item.Name);
        if (required.Any(name => !names.Contains(name))) throw new InvalidDataException("Missing required wholeSkin fields: " + string.Join(",", required.Where(name => !names.Contains(name))));
    }

    private static string Text(JsonElement element, string property) =>
        element.GetProperty(property).GetString() is { Length: > 0 } value ? value : throw new InvalidDataException("Empty wholeSkin field: " + property);
}
