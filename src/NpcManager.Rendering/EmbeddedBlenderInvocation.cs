using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace NpcManager.Rendering;

public sealed record EmbeddedBlenderInvocation(
    ImmutableArray<string> Arguments,
    string StandardInput);

public static class EmbeddedBlenderInvocationFactory
{
    public const string Bootstrap =
        "import sys, json, types\n" +
        "p = json.loads(sys.stdin.read())\n" +
        "for name, source in p[\"modules\"].items():\n" +
        "    module = types.ModuleType(name)\n" +
        "    module.__file__ = f\"actorwright-embedded/{name}.py\"\n" +
        "    sys.modules[name] = module\n" +
        "    exec(compile(source, module.__file__, \"exec\"), module.__dict__)\n" +
        "entry_file = f\"actorwright-embedded/{p['entry']}.py\"\n" +
        "entry_globals = {\"__name__\": \"__main__\", \"__file__\": entry_file}\n" +
        "exec(compile(p[\"source\"], entry_file, \"exec\"), entry_globals)";

    public static EmbeddedBlenderInvocation Create(
        string scriptId,
        ImmutableArray<string> rendererArguments)
    {
        EmbeddedBlenderScript script = EmbeddedBlenderScriptBundle.Load(scriptId);
        var modules = script.HelperModules
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToDictionary(
                item => item.Key,
                item => Encoding.UTF8.GetString(item.Value.AsSpan()),
                StringComparer.Ordinal);
        string payload = JsonSerializer.Serialize(new
        {
            entry = script.Id,
            source = Encoding.UTF8.GetString(script.SourceBytes.AsSpan()),
            modules
        });
        return new EmbeddedBlenderInvocation(
            ["--background", "--python-expr", Bootstrap, "--", .. rendererArguments],
            payload);
    }
}
