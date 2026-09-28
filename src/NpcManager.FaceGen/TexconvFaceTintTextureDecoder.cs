using System.Buffers.Binary;
using System.ComponentModel;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using DesktopFailureOperationId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopFailureOperationId;

namespace NpcManager.FaceGen;

/// <summary>
/// Decodes a K-local DDS provider source through the pinned DirectXTex
/// executable into a single-mip BGRA8 raster.
/// </summary>
/// <remarks>
/// The source, executable, their existing ancestry, and the private conversion
/// directories remain identity-pinned while DirectXTex runs. Returned pixels
/// come from a no-follow output handle and are independently validated.
/// </remarks>
public sealed class TexconvFaceTintTextureDecoder(
    WorkspacePath executablePath,
    WorkspacePath labRoot,
    Sha256Hash expectedExecutableSha256) : IFaceTintTextureDecoder
{
    private const long MaxSourceBytes = 64L * 1024 * 1024;
    private const long MaxOutputBytes = 64L * 1024 * 1024;
    private const uint DdsMagic = 0x2053_4444;
    private const uint DdsHeaderSize = 124;
    private const uint Bgra8PixelFormatFlags = 0x41;
    private const uint Bgra8RgbBitCount = 32;
    private const uint Bgra8RedMask = 0x00FF_0000;
    private const uint Bgra8GreenMask = 0x0000_FF00;
    private const uint Bgra8BlueMask = 0x0000_00FF;
    private const uint Bgra8AlphaMask = 0xFF00_0000;
    private const uint MaxDimension = 8192;

    public async ValueTask<FaceTintTextureDecodeResult> DecodeAsync(
        WorkspacePath sourceDds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!executablePath.IsUnder(labRoot) || !File.Exists(executablePath.Value))
        {
            diagnostics.Add(new Diagnostic("facetint-decoder-tool-invalid", DiagnosticSeverity.Error,
                "The pinned DirectXTex executable must exist under the K-only lab root."));
            return Refused(diagnostics);
        }
        if (!sourceDds.IsUnder(labRoot) || !File.Exists(sourceDds.Value) ||
            !sourceDds.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("facetint-decoder-source-invalid", DiagnosticSeverity.Error,
                "The provider source must be an existing K-local DDS file."));
            return Refused(diagnostics);
        }

        TexconvCodecSession? session = null;
        uint width = 0;
        uint height = 0;
        Sha256Hash? sourceHash = null;
        try
        {
            var opened = await TexconvCodecSession.OpenAsync(
                executablePath, sourceDds, labRoot, expectedExecutableSha256,
                "facetint-decoder", MaxSourceBytes, cancellationToken);
            diagnostics.AddRange(opened.Diagnostics);
            session = opened.Session;
            if (session is null) return Refused(diagnostics);

            sourceHash = session.SourceSha256;
            byte[]? pixels = null;
            if (!TryReadDimensions(session.SourceBytes, out width, out height))
            {
                diagnostics.Add(new Diagnostic("facetint-decoder-source-dds-invalid",
                    DiagnosticSeverity.Error, "The provider DDS header is invalid."));
            }
            else
            {
                var process = await TexconvProcessRunner.RunAsync(
                    session,
                    "B8G8R8A8_UNORM",
                    "facetint-decoder",
                    DesktopFailureOperationId.FaceTintDecode,
                    expectedExecutableSha256,
                    cancellationToken);
                diagnostics.AddRange(process.Diagnostics);
                if (process.Completed)
                {
                    if (await session.HashExecutableAsync(cancellationToken) != expectedExecutableSha256)
                    {
                        diagnostics.Add(new Diagnostic("facetint-decoder-tool-hash-mismatch",
                            DiagnosticSeverity.Error,
                            "The pinned DirectXTex executable changed while the decode was running."));
                    }
                    else
                    {
                        var output = await session.ReadOutputAsync(MaxOutputBytes, cancellationToken);
                        diagnostics.AddRange(output.Diagnostics);
                        if (output.Bytes is { } decoded)
                        {
                            if (ValidateBgra8Dds(decoded, width, height, out var error))
                                pixels = decoded.AsSpan(128).ToArray();
                            else
                                diagnostics.Add(new Diagnostic("facetint-decoder-output-invalid",
                                    DiagnosticSeverity.Error, error ?? "The decoded DDS is invalid."));
                        }
                    }
                }
            }

            var cleanup = session.Cleanup();
            session = null;
            diagnostics.AddRange(cleanup.Diagnostics);
            return pixels is not null && cleanup.Completed &&
                   diagnostics.All(item => item.Severity != DiagnosticSeverity.Error)
                ? new FaceTintTextureDecodeResult(true, checked((int)width), checked((int)height),
                    pixels, sourceHash, diagnostics.ToImmutable())
                : Refused(diagnostics, width, height, sourceHash);
        }
        catch (OperationCanceledException)
        {
            AddCleanupDiagnostics(session, diagnostics);
            session = null;
            if (cancellationToken.IsCancellationRequested) throw;
            diagnostics.Add(new Diagnostic("facetint-decoder-timeout", DiagnosticSeverity.Error,
                "The DirectXTex decoder exceeded its execution budget."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddCleanupDiagnostics(session, diagnostics);
            session = null;
            diagnostics.Add(new Diagnostic("facetint-decoder-io-failed", DiagnosticSeverity.Error,
                exception.Message));
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            AddCleanupDiagnostics(session, diagnostics);
            session = null;
            diagnostics.Add(new Diagnostic("facetint-decoder-launch-failed", DiagnosticSeverity.Error,
                exception.Message));
        }
        finally
        {
            session?.Dispose();
        }
        return Refused(diagnostics, width, height, sourceHash);
    }

    private static bool TryReadDimensions(byte[] bytes, out uint width, out uint height)
    {
        width = height = 0;
        if (bytes.Length < 128 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) != DdsMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) != DdsHeaderSize)
            return false;
        height = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
        width = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4));
        return width > 0 && height > 0 && width <= MaxDimension && height <= MaxDimension &&
            (long)width * height * 4 <= MaxOutputBytes;
    }

    private static bool ValidateBgra8Dds(byte[] bytes, uint width, uint height, out string? error)
    {
        error = null;
        if (!TryReadDimensions(bytes, out var actualWidth, out var actualHeight) ||
            actualWidth != width || actualHeight != height)
        {
            error = "The decoded DDS dimensions do not match the provider source.";
            return false;
        }
        if (bytes.Length != 128L + (long)width * height * 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(80, 4)) != Bgra8PixelFormatFlags ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(88, 4)) != Bgra8RgbBitCount ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(92, 4)) != Bgra8RedMask ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(96, 4)) != Bgra8GreenMask ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(100, 4)) != Bgra8BlueMask ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(104, 4)) != Bgra8AlphaMask)
        {
            error = "The decoded DDS is not a single-mip BGRA8 raster.";
            return false;
        }
        return true;
    }

    private static void AddCleanupDiagnostics(
        TexconvCodecSession? session,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (session is not null) diagnostics.AddRange(session.Cleanup().Diagnostics);
    }

    private static FaceTintTextureDecodeResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        uint width = 0,
        uint height = 0,
        Sha256Hash? sourceHash = null) =>
        new(false, width > int.MaxValue ? 0 : (int)width,
            height > int.MaxValue ? 0 : (int)height, null, sourceHash, diagnostics.ToImmutable());
}
