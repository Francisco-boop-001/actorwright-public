using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using NpcManager.Application;

namespace NpcManager.Infrastructure;

public sealed class XttsRequestException(string code, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed class XttsApiServerClient : IDisposable
{
    private static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)];
    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public XttsApiServerClient(HttpClient http, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _delay = delay ?? Task.Delay;
    }

    public async ValueTask<(byte[] Bytes, SkyrimVoiceSampleAudio Audio, double ElapsedSeconds)> SynthesizeAsync(
        Uri endpoint,
        string text,
        string speakerWav,
        string language,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!IsLoopbackHttp(endpoint))
            throw new XttsRequestException(SkyrimNpcVoiceDiagnosticCodes.ServiceRequestRejected, "XTTS endpoints must use HTTP on loopback.");
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                    timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(180, Math.Max(60, words * 15))));
                    var stopwatch = Stopwatch.StartNew();
                    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/tts_to_audio/"))
                    {
                        Content = JsonContent.Create(new { text, speaker_wav = speakerWav, language })
                    };
                    request.Headers.Accept.ParseAdd("audio/wav");
                    using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    byte[] bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        string message = bytes.Length == 0 ? response.ReasonPhrase ?? "XTTS request failed." : System.Text.Encoding.UTF8.GetString(bytes);
                        if ((int)response.StatusCode < 500)
                            throw new XttsRequestException(SkyrimNpcVoiceDiagnosticCodes.ServiceRequestRejected, message);
                        throw new HttpRequestException(message, null, response.StatusCode);
                    }
                    if (bytes.Length == 0)
                        throw new XttsRequestException(SkyrimNpcVoiceDiagnosticCodes.AudioEmpty, "XTTS returned an empty body.");
                    if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "audio/wav", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(response.Content.Headers.ContentType?.MediaType, "audio/x-wav", StringComparison.OrdinalIgnoreCase))
                        throw new XttsRequestException(SkyrimNpcVoiceDiagnosticCodes.AudioShape, "XTTS must return an audio/wav response.");
                    WavDecodedAudio decoded;
                    try { decoded = WavSampleCodec.Decode(bytes); }
                    catch (InvalidDataException exception)
                    {
                        throw new XttsRequestException(SkyrimNpcVoiceDiagnosticCodes.AudioShape, "XTTS did not return a decodable WAV.", exception);
                    }
                    SkyrimVoiceSampleAudio audio = WavSampleCodec.Measure(decoded);
                    if (audio.SampleRate != 24_000 || audio.Channels != 1 || audio.PeakDbfs <= -90)
                        throw new XttsRequestException(SkyrimNpcVoiceDiagnosticCodes.AudioShape, "XTTS WAV must be non-silent 24 kHz mono 16-bit PCM.");
                    if (audio is { BitsPerSample: 32, Format: "float" })
                    {
                        bytes = WavSampleCodec.WritePcm16(decoded.Samples, 24_000);
                        audio = WavSampleCodec.Measure(WavSampleCodec.Decode(bytes));
                    }
                    else if (audio.BitsPerSample != 16 || audio.Format != "pcm")
                    {
                        throw new XttsRequestException(SkyrimNpcVoiceDiagnosticCodes.AudioShape, "XTTS WAV must be non-silent 24 kHz mono 16-bit PCM.");
                    }
                    if (audio.PeakDbfs <= -90)
                        throw new XttsRequestException(SkyrimNpcVoiceDiagnosticCodes.AudioShape, "XTTS WAV must be non-silent 24 kHz mono 16-bit PCM.");
                    return (bytes, audio, stopwatch.Elapsed.TotalSeconds);
                }
                catch (XttsRequestException) { throw; }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (OperationCanceledException exception)
                {
                    if (attempt >= RetryDelays.Length)
                        throw new XttsRequestException(SkyrimNpcVoiceDiagnosticCodes.ServiceBusyTimeout, "XTTS did not answer within the bounded read timeout.", exception);
                    await _delay(RetryDelays[attempt], cancellationToken);
                }
                catch (HttpRequestException exception)
                {
                    if (attempt >= RetryDelays.Length)
                        throw new XttsRequestException(
                            exception.StatusCode is null ? SkyrimNpcVoiceDiagnosticCodes.ServiceUnreachable : SkyrimNpcVoiceDiagnosticCodes.ServiceError,
                            exception.Message,
                            exception);
                    await _delay(RetryDelays[attempt], cancellationToken);
                }
            }
        }
        finally { _requestGate.Release(); }
    }

    internal static bool IsLoopbackHttp(Uri endpoint) =>
        endpoint.Scheme == Uri.UriSchemeHttp &&
        (endpoint.IsLoopback || IPAddress.TryParse(endpoint.Host, out IPAddress? address) && IPAddress.IsLoopback(address));

    public void Dispose() => _requestGate.Dispose();
}
