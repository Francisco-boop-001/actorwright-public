using System.Buffers.Binary;
using System.ComponentModel;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using DesktopFailureOperationId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopFailureOperationId;

namespace NpcManager.FaceGen;

/// <summary>
/// Encodes a K-local BGRA8 DDS through the pinned DirectXTex executable.
/// </summary>
/// <remarks>
/// The source, executable, their existing ancestry, and the private conversion
/// directories remain identity-pinned while DirectXTex runs. Returned bytes
/// come from a no-follow output handle and are independently validated.
/// </remarks>
public sealed class TexconvFaceTintTextureEncoder(
    WorkspacePath executablePath,
    WorkspacePath labRoot,
    Sha256Hash expectedExecutableSha256) : IFaceTintTextureEncoder
{
    private const long MaxSourceBytes = 64L * 1024 * 1024 + 128;
    private const long MaxOutputBytes = 64L * 1024 * 1024;
    private const uint DdsMagic = 0x2053_4444;
    private const uint DdsHeaderSize = 124;
    private const uint Dxt5FourCc = 0x3554_5844;
    private const uint Dx10FourCc = 0x3031_5844;
    private const uint Bc7UnormDxgi = 98;

    public async ValueTask<FaceTintTextureEncodeResult> EncodeAsync(
        FaceTintOutputFormat format,
        WorkspacePath sourceDds,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (format is not (FaceTintOutputFormat.Bc3 or FaceTintOutputFormat.Bc7))
        {
            diagnostics.Add(new Diagnostic("facetint-codec-format", DiagnosticSeverity.Error,
                "The external FaceTint encoder supports only BC3 or BC7."));
            return Refused(diagnostics);
        }
        if (!executablePath.IsUnder(labRoot) || !File.Exists(executablePath.Value))
        {
            diagnostics.Add(new Diagnostic("facetint-codec-tool-invalid", DiagnosticSeverity.Error,
                "The pinned DirectXTex executable must exist under the K-only lab root."));
            return Refused(diagnostics);
        }
        if (!sourceDds.IsUnder(labRoot) || !File.Exists(sourceDds.Value))
        {
            diagnostics.Add(new Diagnostic("facetint-codec-source-invalid", DiagnosticSeverity.Error,
                "The temporary source DDS must be an ordinary K-local file."));
            return Refused(diagnostics);
        }

        TexconvCodecSession? session = null;
        try
        {
            var opened = await TexconvCodecSession.OpenAsync(
                executablePath, sourceDds, labRoot, expectedExecutableSha256,
                "facetint-codec", MaxSourceBytes, cancellationToken);
            diagnostics.AddRange(opened.Diagnostics);
            session = opened.Session;
            if (session is null) return Refused(diagnostics);

            byte[]? encoded = null;
            if (!TryReadDimensions(session.SourceBytes, out var width, out var height))
            {
                diagnostics.Add(new Diagnostic("facetint-codec-source-dds-invalid", DiagnosticSeverity.Error,
                    "The temporary source DDS header is invalid."));
            }
            else
            {
                var pixelFormat = format == FaceTintOutputFormat.Bc3 ? "BC3_UNORM" : "BC7_UNORM";
                var process = await TexconvProcessRunner.RunAsync(
                    session,
                    pixelFormat,
                    "facetint-codec",
                    DesktopFailureOperationId.FaceTintEncode,
                    expectedExecutableSha256,
                    cancellationToken);
                diagnostics.AddRange(process.Diagnostics);
                if (process.Completed)
                {
                    if (await session.HashExecutableAsync(cancellationToken) != expectedExecutableSha256)
                    {
                        diagnostics.Add(new Diagnostic("facetint-codec-tool-hash-mismatch",
                            DiagnosticSeverity.Error,
                            "The pinned DirectXTex executable changed while the encode was running."));
                    }
                    else
                    {
                        var output = await session.ReadOutputAsync(MaxOutputBytes, cancellationToken);
                        diagnostics.AddRange(output.Diagnostics);
                        if (output.Bytes is { } bytes)
                        {
                            var expectedFourCc = format == FaceTintOutputFormat.Bc3 ? Dxt5FourCc : Dx10FourCc;
                            uint? expectedDxgi = format == FaceTintOutputFormat.Bc3 ? null : Bc7UnormDxgi;
                            if (ValidateCompressedDds(bytes, width, height, expectedFourCc, expectedDxgi,
                                    out var error))
                                encoded = bytes;
                            else
                                diagnostics.Add(new Diagnostic("facetint-codec-output-invalid",
                                    DiagnosticSeverity.Error, error));
                        }
                    }
                }
            }

            var cleanup = session.Cleanup();
            session = null;
            diagnostics.AddRange(cleanup.Diagnostics);
            return encoded is not null && cleanup.Completed &&
                   diagnostics.All(item => item.Severity != DiagnosticSeverity.Error)
                ? new FaceTintTextureEncodeResult(true, encoded, diagnostics.ToImmutable())
                : Refused(diagnostics);
        }
        catch (OperationCanceledException)
        {
            AddCleanupDiagnostics(session, diagnostics);
            session = null;
            if (cancellationToken.IsCancellationRequested) throw;
            diagnostics.Add(new Diagnostic("facetint-codec-timeout", DiagnosticSeverity.Error,
                "The DirectXTex encoder exceeded its execution budget."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AddCleanupDiagnostics(session, diagnostics);
            session = null;
            diagnostics.Add(new Diagnostic("facetint-codec-io-failed", DiagnosticSeverity.Error,
                exception.Message));
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            AddCleanupDiagnostics(session, diagnostics);
            session = null;
            diagnostics.Add(new Diagnostic("facetint-codec-launch-failed", DiagnosticSeverity.Error,
                exception.Message));
        }
        finally
        {
            session?.Dispose();
        }
        return Refused(diagnostics);
    }

    private static bool TryReadDimensions(byte[] bytes, out uint width, out uint height)
    {
        width = height = 0;
        return bytes.Length >= 128 &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4)) == DdsMagic &&
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)) == DdsHeaderSize &&
            (height = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4))) > 0 &&
            (width = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16, 4))) > 0;
    }

    private static bool ValidateCompressedDds(
        byte[] bytes,
        uint width,
        uint height,
        uint expectedFourCc,
        uint? expectedDxgi,
        out string error)
    {
        error = string.Empty;
        if (!TryReadDimensions(bytes, out var actualWidth, out var actualHeight) ||
            actualWidth != width || actualHeight != height)
        {
            error = "The compressed DDS dimensions do not match the source raster.";
            return false;
        }
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(84, 4)) != expectedFourCc)
        {
            error = "The compressed DDS pixel format does not match the requested BCn format.";
            return false;
        }
        if (expectedDxgi is not null &&
            (bytes.Length < 148 ||
             BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(128, 4)) != expectedDxgi.Value))
        {
            error = "The BC7 DDS does not declare DXGI_FORMAT_BC7_UNORM.";
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

    private static FaceTintTextureEncodeResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
