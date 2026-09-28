using System.Collections.Immutable;

namespace NpcManager.Application;

public static class BuildInfo
{
    public const string ProductName = "Actorwright";
    public const string ProductVersion = "1.0.0-preview.281";
    public const string SourceLine = "preview.281-public";
    public const string ProtocolVersion = "1";
    public const string LatestProtocolVersion = "2";
    public static ImmutableArray<string> SupportedProtocolVersions { get; } =
        ["1", "2"];
    public const string TargetFramework = "net10.0-windows";
}
