using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal static class NpcInheritedDefaultsAudit
{
    internal static (ImmutableArray<string>? Fields, ImmutableArray<Diagnostic> Diagnostics) Read(
        GameEdition edition, WorkspacePath workspaceRoot, WorkspacePath plugin, FormReference actor, CancellationToken cancellationToken,
        Sha256Hash? expectedPluginHash = null)
    {
        if (edition != GameEdition.SkyrimSpecialEdition) return (null, []);
        try
        {
            if (!plugin.IsUnder(workspaceRoot)) throw new InvalidDataException("The inspected plugin is outside the workspace.");
            var registry = new ApplicationProviderResourceRegistry(new ApplicationResourcePath(AppContext.BaseDirectory));
            if (!registry.TryGetDefaultBlankNpcFixture(
                    out ProductFixtureBundleReference? product) ||
                product is null)
                return (null, [new Diagnostic(
                    "inherited-defaults-unavailable",
                    DiagnosticSeverity.Warning,
                    "Fixture equality is unavailable because the optional bundled product provider is not installed (product-provider-unavailable).")]);
            var admitted = registry.Admit(product, new FormId(0x800), GameEdition.SkyrimSpecialEdition, NpcSex.Female);
            var authority = admitted.Accepted ? admitted.Authority! : throw new InvalidDataException(
                string.Join(" | ", admitted.Diagnostics.Select(item => item.Message)));
            byte[] template = File.ReadAllBytes(authority.TemplatePlugin.PhysicalPath);
            if (new Sha256Hash(Convert.ToHexString(SHA256.HashData(template))) != authority.TemplatePlugin.ExpectedSha256)
                throw new InvalidDataException("The authenticated blank NPC template changed before comparison.");
            byte[] current = File.ReadAllBytes(plugin.Value);
            if (expectedPluginHash is { } expected && new Sha256Hash(Convert.ToHexString(SHA256.HashData(current))) != expected)
                throw new InvalidDataException("The produced plugin changed before its fixture comparison.");
            var fields = BethesdaPluginVerifier.CompareBlankNpcDefaults(current, new PluginName(Path.GetFileName(plugin.Value)), actor,
                template, new PluginName(Path.GetFileName(authority.TemplatePlugin.PhysicalPath)), authority.TemplateNpcFormId, cancellationToken);
            return (fields, []);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return (null, [new Diagnostic("inherited-defaults-unavailable", DiagnosticSeverity.Warning,
                "Fixture equality could not be established: " + exception.Message)]);
        }
    }
}
