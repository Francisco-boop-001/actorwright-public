using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class ReferencePresetSessionService
{
    public async ValueTask<ReferencePresetSessionWriteResult> WriteIntakeTemplateAsync(
        WorkspacePath destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateDestination(destinationPath, diagnostics);
        if (HasErrors(diagnostics)) return new(false, null, diagnostics.ToImmutable());
        // Placeholders deliberately stay JSON strings until the user supplies real authorities.
        using JsonDocument template = JsonDocument.Parse("""
            {
              "kind": 0,
              "intake": {
                "schemaVersion": 1,
                "projectId": "<project-id>",
                "targetName": "<target-name>",
                "race": "<plugin-name>|<8-hex-form-id>",
                "sex": 1,
                "weight": 50,
                "headSystemId": "<head-system-id>",
                "baselineJslot": "<absolute-K-local-path>",
                "baselineJslotSha256": "<64-hex-sha256>",
                "description": "<description>",
                "images": [{"imageId":"<image-id>","sourcePath":"<absolute-K-local-path>","sourceSha256":"<64-hex-sha256>","encodedLength":1,"viewRole":0}],
                "target": {"authorityId":"<reviewed-authority-id>","race":"<plugin-name>|<8-hex-form-id>","sex":1,"dataRoot":"<absolute-K-local-path>","pluginOrder":[]}
              },
              "inferenceProposal": null,
              "reviewedDesign": null,
              "resourceSnapshot": null,
              "authoringProposal": null,
              "verifiedPreset": null,
              "verifiedNpcHandoff": null
            }
            """);
        try
        {
            return await WriteBytesAsync(destinationPath, SerializeCanonicalJson(template.RootElement), typed: false,
                diagnostics, cancellationToken).ConfigureAwait(false);
        }
        catch (ReferenceSessionSizeLimitException exception)
        {
            diagnostics.Add(Error("reference-session-size-limit", exception.Message));
            return new(false, null, diagnostics.ToImmutable());
        }
    }
}
