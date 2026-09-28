using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SchemaExportRequest(string? CommandName, WorkspacePath Output);

public sealed record SchemaExportResult(
    bool Written,
    WorkspacePath Output,
    string? CommandName,
    ImmutableArray<Diagnostic> Diagnostics);

public interface ISchemaExportService
{
    SchemaExportResult Export(SchemaExportRequest request);
}
