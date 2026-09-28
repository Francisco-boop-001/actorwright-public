using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class SchemaExportService(IWorkspacePolicy policy, WorkspacePath labRoot) : ISchemaExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static readonly JsonElement PathConventions = JsonSerializer.SerializeToElement(new
    {
        cli = "CLI path options use absolute K-local filesystem paths under the admitted workspace.",
        documents = "JSON document paths use workspace-relative or explicitly project-relative forward-slash paths. Follow the base and exceptions declared by each document schema.",
        assets = "Asset fields use their declared Data-relative, textures-relative, or meshes-relative paths. Overlay omission comparison and package-destination admission normalize one leading separator, casing and slashes. Rooted active overlays, UNC/device paths, drives and traversal remain refused; immutable source documents and other AssetPath values retain their bytes and casing."
    });

    public SchemaExportResult Export(SchemaExportRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var output = request.Output;
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(new Diagnostic("schema-output-parent-missing", DiagnosticSeverity.Error,
                "The schema output directory must already exist."));
        }
        else
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        }

        if (File.Exists(output.Value))
        {
            diagnostics.Add(new Diagnostic("schema-output-exists", DiagnosticSeverity.Error,
                "Schema export refuses to overwrite an existing file."));
        }

        CommandDescriptor[] commands;
        if (string.IsNullOrWhiteSpace(request.CommandName))
        {
            commands = CommandCatalog.All.ToArray();
        }
        else
        {
            var command = CommandCatalog.All.FirstOrDefault(item =>
                string.Equals(item.Name, request.CommandName, StringComparison.OrdinalIgnoreCase));
            if (command is null)
            {
                diagnostics.Add(new Diagnostic("schema-command-unknown", DiagnosticSeverity.Error,
                    $"Unknown command '{request.CommandName}'."));
                commands = [];
            }
            else
            {
                commands = [command];
            }
        }

        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            return new SchemaExportResult(false, output, request.CommandName, diagnostics.ToImmutable());
        }

        var bytes = SerializeDocument(request.CommandName, commands);
        var temporary = output.Value + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            File.Move(temporary, output.Value);
            return new SchemaExportResult(true, output, request.CommandName, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temporary);
            diagnostics.Add(new Diagnostic("schema-output-write-failed", DiagnosticSeverity.Error,
                $"Schema export failed: {exception.Message}"));
            return new SchemaExportResult(false, output, request.CommandName, diagnostics.ToImmutable());
        }
    }

    public static byte[] SerializeDocument(string? commandName)
    {
        CommandDescriptor[] commands = string.IsNullOrWhiteSpace(commandName)
            ? CommandCatalog.All.ToArray()
            : CommandCatalog.All.Where(command => string.Equals(
                command.Name, commandName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (commands.Length == 0)
            throw new ArgumentException($"Unknown command '{commandName}'.", nameof(commandName));
        return SerializeDocument(commandName, commands);
    }

    private static byte[] SerializeDocument(string? commandName, CommandDescriptor[] commands)
    {
        var document = new SchemaDocument(
            "1",
            BuildInfo.ProtocolVersion,
            commandName,
            PathConventions,
            commands.Select(command => new SchemaCommand(
                command.Name,
                command.Description,
                command.SchemaVersion,
                command.Mutates,
                command.SupportedGames.Select(game => game.ToWireName()).ToImmutableArray(),
                command.Limitations,
                CommandCatalog.LedgerIdsFor(command.Name),
                CommandDocumentSchemaCatalog.For(command.Name),
                LegacyCommandOptionCatalog.For(command.Name) is { IsDefaultOrEmpty: false } options
                    ? options
                    : null)).ToImmutableArray(),
            [
                new SchemaExitCode((int)CommandExitCode.Success, nameof(CommandExitCode.Success), "The command completed."),
                new SchemaExitCode((int)CommandExitCode.GeneralFailure, nameof(CommandExitCode.GeneralFailure), "An unexpected failure occurred."),
                new SchemaExitCode((int)CommandExitCode.UsageError, nameof(CommandExitCode.UsageError), "Input or command syntax was invalid."),
                new SchemaExitCode((int)CommandExitCode.SecurityRefusal, nameof(CommandExitCode.SecurityRefusal), "A workspace or safety policy refused the request."),
                new SchemaExitCode((int)CommandExitCode.ValidationFailure, nameof(CommandExitCode.ValidationFailure), "Typed validation failed."),
                new SchemaExitCode((int)CommandExitCode.Cancelled, nameof(CommandExitCode.Cancelled), "The operation was cancelled.")
            ]);

        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private sealed record SchemaDocument(string SchemaVersion, string ProtocolVersion, string? Command, JsonElement PathConventions,
        ImmutableArray<SchemaCommand> Commands, ImmutableArray<SchemaExitCode> ExitCodes);

    private sealed record SchemaCommand(string Name, string Description, string SchemaVersion, bool Mutates,
        ImmutableArray<string> SupportedGames, ImmutableArray<string> Limitations,
        ImmutableArray<string> LedgerIds,
        ImmutableArray<CommandDocumentSchemaDefinition> DocumentSchemas,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ImmutableArray<LegacyCommandOption>? Options);

    private sealed record SchemaExitCode(int Value, string Name, string Meaning);
}
