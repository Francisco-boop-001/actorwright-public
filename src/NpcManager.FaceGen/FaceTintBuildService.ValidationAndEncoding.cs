using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

public sealed partial class FaceTintBuildService
{
    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("facetint-build-output-outside-lab",
            DiagnosticSeverity.Error, "FaceTint build outputs must remain under the K-only lab root."));
        if (!string.Equals(Path.GetExtension(output.Value), ".json", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facetint-build-output-extension", DiagnosticSeverity.Error,
                "The semantic FaceTint build artifact must use the .json extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("facetint-build-output-exists",
            DiagnosticSeverity.Error, "FaceTint build outputs never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("facetint-build-output-parent-missing",
            DiagnosticSeverity.Error, "FaceTint build output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private ImmutableArray<Diagnostic> ValidateTextureDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("facetint-dds-output-outside-lab",
            DiagnosticSeverity.Error, "FaceTint DDS outputs must remain under the K-only lab root."));
        if (!output.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("facetint-dds-output-extension", DiagnosticSeverity.Error,
                "FaceTint DDS outputs must use the .dds extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("facetint-dds-output-exists",
            DiagnosticSeverity.Error, "FaceTint DDS outputs never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("facetint-dds-output-parent-missing",
            DiagnosticSeverity.Error, "The FaceTint DDS output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private ImmutableArray<Diagnostic> ValidateProviderRoot(WorkspacePath providerRoot)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!providerRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("facetint-provider-root-outside-lab", DiagnosticSeverity.Error,
                "FaceTint provider roots must remain under the K-only lab root."));
        if (!Directory.Exists(providerRoot.Value))
            diagnostics.Add(new Diagnostic("facetint-provider-root-missing", DiagnosticSeverity.Error,
                "The FaceTint provider root must already exist."));
        else
        {
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, providerRoot));
            try
            {
                if (File.GetAttributes(providerRoot.Value).HasFlag(FileAttributes.ReparsePoint))
                    diagnostics.Add(new Diagnostic("facetint-provider-root-reparse", DiagnosticSeverity.Error,
                        "The FaceTint provider root may not be a reparse point."));
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("facetint-provider-root-stat-failed", DiagnosticSeverity.Error,
                    exception.Message));
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("facetint-provider-root-stat-denied", DiagnosticSeverity.Error,
                    exception.Message));
            }
        }
        return diagnostics.ToImmutable();
    }

    private static byte[] EncodeBgra8(int width, int height, byte[] pixels)
    {
        var payload = checked(width * height * 4);
        if (pixels.Length != payload) throw new ArgumentException("The raster byte count does not match the dimensions.");
        var bytes = new byte[128 + payload];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), 0x2053_4444);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), 0x0002_100F);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12, 4), checked((uint)height));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16, 4), checked((uint)width));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20, 4), checked((uint)(width * 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(76, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80, 4), 0x41);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(88, 4), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92, 4), 0x00FF_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96, 4), 0x0000_FF00);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(100, 4), 0x0000_00FF);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(104, 4), 0xFF00_0000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(108, 4), 0x1000);
        Buffer.BlockCopy(pixels, 0, bytes, 128, payload);
        return bytes;
    }

    private static byte Quantize(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255d, MidpointRounding.ToEven), 0d, 255d);

    private static FaceTintOutputFormat ParseFormat(string? value, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("bgra8", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("uncompressed", StringComparison.OrdinalIgnoreCase)) return FaceTintOutputFormat.Bgra8;
        if (value.Equals("bc3", StringComparison.OrdinalIgnoreCase)) return FaceTintOutputFormat.Bc3;
        if (value.Equals("bc7", StringComparison.OrdinalIgnoreCase)) return FaceTintOutputFormat.Bc7;
        diagnostics.Add(new Diagnostic("facetint-build-format-unsupported", DiagnosticSeverity.Error,
            "FaceTint format must be bgra8, bc3, or bc7."));
        return FaceTintOutputFormat.Bgra8;
    }

    private static FaceTintAlphaMode ParseAlpha(string? value, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value)) return FaceTintAlphaMode.Preserve;
        if (Enum.TryParse<FaceTintAlphaMode>(value, true, out var alpha)) return alpha;
        diagnostics.Add(new Diagnostic("facetint-build-alpha-unsupported", DiagnosticSeverity.Error,
            "alphaMode must be preserve or opaque."));
        return FaceTintAlphaMode.Preserve;
    }
}
