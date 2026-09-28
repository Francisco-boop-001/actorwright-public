// The linked Gate1 emitter relies on its project's implicit System.IO using.
global using System.IO;

namespace NpcManager.Gate1.Tests;

internal static partial class Program
{
    internal static Task EmitDesktopSmokeProviderFixtureAsync(string outputRoot) =>
        EmitProductProviderFixtureAsync(outputRoot);
}
