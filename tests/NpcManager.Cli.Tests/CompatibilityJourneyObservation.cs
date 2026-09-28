using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NpcManager.Cli.Tests;

internal sealed record CompatibilityStepObservation(
    string Command,
    int ExitCode,
    string ExitMeaning,
    ImmutableArray<string> SchemaIds);

internal sealed record CompatibilityArtifactObservation(
    string Path,
    string Role,
    string IdentityKind,
    long Length,
    string? Sha256,
    string? SemanticSha256,
    JsonNode? Projection,
    string? RawSha256 = null,
    JsonArray? Relationships = null);

internal sealed record CompatibilityJourneyObservation(
    string Id,
    ImmutableArray<CompatibilityStepObservation> Steps,
    ImmutableArray<CompatibilityArtifactObservation> Artifacts,
    JsonObject Semantics,
    ImmutableArray<CompatibilityTransientSourceIdentity> TransientSourceIdentities = default);

internal sealed record CompatibilityTransientSourceIdentity(
    string Role, string Path, long Length, string RawSha256, string CaptureStep);

internal interface ICompatibilityJourneySink
{
    void Capture(CompatibilityJourneyObservation observation);
}

internal sealed class CompatibilityJourneyFileSink : ICompatibilityJourneySink
{
    private static readonly JsonSerializerOptions DocumentOptions =
        new() { WriteIndented = true };
    private const string OutputVariable =
        "ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT";
    private const string CaptureRootVariable =
        "ACTORWRIGHT_COMPATIBILITY_CAPTURE_ROOT";
    private const string CaptureParentVariable =
        "ACTORWRIGHT_COMPATIBILITY_CAPTURE_PARENT";
    private const string CaptureTokenVariable =
        "ACTORWRIGHT_COMPATIBILITY_CAPTURE_TOKEN";
    private readonly string output;
    private readonly string workspaceRoot;
    private readonly string journeyOutputRoot;
    private readonly string operationId;

    private CompatibilityJourneyFileSink(
        string output,
        string workspaceRoot,
        string journeyOutputRoot,
        string operationId)
    {
        this.output = output;
        this.workspaceRoot = workspaceRoot;
        this.journeyOutputRoot = journeyOutputRoot;
        this.operationId = operationId;
    }

    internal static ICompatibilityJourneySink? FromEnvironment(
        string workspaceRoot,
        string journeyOutputRoot,
        string operationId)
    {
        string? configured = Environment.GetEnvironmentVariable(OutputVariable);
        if (string.IsNullOrWhiteSpace(configured))
            return null;
        string captureRoot = RequireOwnedCaptureRoot();
        string token = Environment.GetEnvironmentVariable(CaptureTokenVariable)!;
        if (!Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException($"{OutputVariable} must be an absolute path.");
        string canonical = Path.GetFullPath(configured);
        string prefix = Path.GetFileName(Path.GetFullPath(workspaceRoot)).StartsWith("c-", StringComparison.Ordinal)
            ? "create-to-package.json" : "wave-b-v1.json";
        string expected = Path.Combine(captureRoot, "observations", prefix);
        if (!string.Equals(canonical, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{OutputVariable} must name the invocation-owned observation file.");
        if (File.Exists(canonical))
            throw new InvalidOperationException($"{OutputVariable} must be fresh.");
        if (!string.Equals(operationId, token, StringComparison.Ordinal))
            throw new InvalidOperationException("Compatibility operation identity does not match its owner token.");
        return new CompatibilityJourneyFileSink(
            canonical,
            Path.GetFullPath(workspaceRoot),
            Path.GetFullPath(journeyOutputRoot),
            operationId);
    }

    public void Capture(CompatibilityJourneyObservation observation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var document = new JsonObject
        {
            ["formatVersion"] = 1,
            ["id"] = observation.Id,
            ["workspaceRoot"] = workspaceRoot,
            ["outputRoot"] = journeyOutputRoot,
            ["operationId"] = operationId,
            ["generatedAtUtc"] = DateTime.UtcNow.ToString(
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture),
            ["steps"] = new JsonArray(observation.Steps.Select((step, index) =>
                (JsonNode)new JsonObject
                {
                    ["position"] = index,
                    ["command"] = step.Command,
                    ["exitCode"] = step.ExitCode,
                    ["exitMeaning"] = step.ExitMeaning,
                    ["schemaIds"] = new JsonArray(step.SchemaIds.Select(value =>
                        (JsonNode)JsonValue.Create(value)!).ToArray())
                }).ToArray()),
            ["artifacts"] = new JsonArray(observation.Artifacts.Select(artifact =>
                (JsonNode)CompatibilityJourneySupport.ArtifactJson(artifact)).ToArray()),
            ["transientSourceIdentities"] = new JsonArray(
                observation.TransientSourceIdentities.Select(identity => (JsonNode)new JsonObject
                {
                    ["role"] = identity.Role,
                    ["path"] = identity.Path,
                    ["length"] = identity.Length,
                    ["rawSha256"] = identity.RawSha256,
                    ["captureStep"] = identity.CaptureStep
                }).ToArray()),
            ["semantics"] = observation.Semantics.DeepClone(),
            ["runtimeAuthority"] = false,
            ["visualAuthority"] = false
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, DocumentOptions);
        using FileStream stream = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    internal static string RequireOwnedCaptureRoot()
    {
        string? configured = Environment.GetEnvironmentVariable(CaptureRootVariable);
        string? token = Environment.GetEnvironmentVariable(CaptureTokenVariable);
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(token) ||
            !Regex.IsMatch(token, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Compatibility capture ownership is incomplete.");
        string root = Path.GetFullPath(configured);
        string expectedParent = GetCaptureParent();
        if (!string.Equals(Path.GetDirectoryName(root), expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(root), "j-" + token, StringComparison.Ordinal))
            throw new InvalidOperationException("Compatibility capture root is not the token-owned K-local root.");
        string marker = Path.Combine(root, ".compatibility-firewall-owner.json");
        string ownerLock = Path.Combine(expectedParent, "j-" + token + ".owner.lock");
        if (!File.Exists(marker) || !File.Exists(ownerLock) ||
            File.ReadAllText(ownerLock, Encoding.ASCII) != token)
            throw new InvalidOperationException("Compatibility capture owner marker or lock is missing.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(marker));
        JsonElement value = document.RootElement;
        if (!value.TryGetProperty("formatVersion", out JsonElement formatVersion) ||
            !value.TryGetProperty("kind", out JsonElement kind) ||
            !value.TryGetProperty("token", out JsonElement ownerToken) ||
            formatVersion.ValueKind != JsonValueKind.Number || formatVersion.GetInt32() != 1 ||
            kind.GetString() != "journey-capture" ||
            ownerToken.GetString() != token ||
            value.EnumerateObject().Count() != 3)
            throw new InvalidOperationException("Compatibility capture owner marker mismatch.");
        return root;
    }

    internal static string GetCaptureParent()
    {
        string? configured = Environment.GetEnvironmentVariable(CaptureParentVariable);
        string parent = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(FindRepositoryRoot(), "artifacts", "test-work")
            : Path.GetFullPath(configured);
        if (!Path.IsPathFullyQualified(parent) ||
            !string.Equals(Path.GetPathRoot(parent), @"K:\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Compatibility capture parent must be K-local.");
        return Path.GetFullPath(parent);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Actorwright.sln")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Compatibility capture repository root was not found.");
    }
}

internal sealed class CompatibilityTransientSourceWatcher : IAsyncDisposable
{
    private static readonly Regex SourcePath = new(
        @"^companion/selection/racemenu-selection-[0-9a-f]{16}-[0-9a-f]{32}/(record-authority|runtime-routes)\.json$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly string root;
    private readonly FileSystemWatcher watcher;
    private readonly Dictionary<string, (long Length, string Hash)> captured =
        new(StringComparer.Ordinal);
    private readonly object gate = new();
    private readonly HashSet<string> pending = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource complete = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource retryCancellation = new();
    private readonly Task retryTask;
    private string? error;
    private bool stopped;

    internal CompatibilityTransientSourceWatcher(string root)
    {
        this.root = Path.GetFullPath(root);
        if (!Directory.Exists(this.root))
            throw new InvalidOperationException("Transient source watcher requires the owned journey root.");
        watcher = new FileSystemWatcher(this.root, "*.json")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite |
                NotifyFilters.Size | NotifyFilters.DirectoryName
        };
        watcher.Created += Changed;
        watcher.Changed += Changed;
        watcher.Renamed += Renamed;
        watcher.Error += (_, args) =>
        {
            lock (gate) error = "Transient source watcher failed: " + args.GetException().Message;
        };
        watcher.EnableRaisingEvents = true;
        retryTask = RetryPendingAsync();
    }

    private void Changed(object sender, FileSystemEventArgs args) => Observe(args.FullPath);
    private void Renamed(object sender, RenamedEventArgs args) => Observe(args.FullPath);

    private void Observe(string fullPath)
    {
        string relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (!SourcePath.IsMatch(relative)) return;
        try
        {
            byte[] bytes = File.ReadAllBytes(fullPath);
            if (bytes.Length == 0)
            {
                Retry(fullPath);
                return;
            }
            using JsonDocument document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Retry(fullPath);
                return;
            }
            var identity = (bytes.LongLength,
                Convert.ToHexString(SHA256.HashData(bytes)));
            lock (gate)
            {
                pending.Remove(fullPath);
                if (captured.TryGetValue(relative, out var previous) &&
                    previous != identity)
                    error = "Transient source changed after a complete capture: " + relative;
                else
                    captured[relative] = identity;
                if (captured.Count >= 2) complete.TrySetResult();
            }
        }
        catch (IOException) { Retry(fullPath); }
        catch (UnauthorizedAccessException) { Retry(fullPath); }
        catch (JsonException) { Retry(fullPath); }
    }

    private void Retry(string fullPath)
    {
        lock (gate) pending.Add(fullPath);
    }

    private async Task RetryPendingAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(retryCancellation.Token))
            {
                string[] retry;
                lock (gate) retry = pending.ToArray();
                foreach (string path in retry) Observe(path);
            }
        }
        catch (OperationCanceledException) { }
    }

    internal async Task StopAsync()
    {
        if (stopped) return;
        stopped = true;
        await Task.WhenAny(complete.Task, Task.Delay(TimeSpan.FromMilliseconds(250)));
        watcher.EnableRaisingEvents = false;
        retryCancellation.Cancel();
        await retryTask;
        watcher.Dispose();
        retryCancellation.Dispose();
    }

    internal bool HasCapture(string relative)
    {
        lock (gate) return captured.ContainsKey(relative);
    }

    internal bool HasError
    {
        get { lock (gate) return error is not null; }
    }

    internal ImmutableArray<CompatibilityTransientSourceIdentity> Seal(
        string retainedBundle,
        string retainedEvidenceRoot)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(retainedBundle));
        var identities = ImmutableArray.CreateBuilder<CompatibilityTransientSourceIdentity>();
        foreach ((string role, string evidenceFile) in new[]
                 {
                     ("recordAuthority", "record-authority.json"),
                     ("runtimeRoutes", "runtime-routes.json")
                 })
        {
            JsonElement authority = document.RootElement.GetProperty(role);
            string path = authority.GetProperty("manifestPath").GetString() ?? "";
            if (!SourcePath.IsMatch(path) ||
                !path.EndsWith("/" + evidenceFile, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "RaceMenu bundle did not bind an exact transient source path.");
            string digest = authority.GetProperty("manifestSha256").GetString() ?? "";
            lock (gate)
            {
                if (error is not null)
                    throw new InvalidOperationException(error);
                if (!captured.TryGetValue(path, out var source))
                    throw new InvalidOperationException(
                        "Transient selection source was not captured before cleanup: " + path);
                string retainedPath = Path.Combine(retainedEvidenceRoot, evidenceFile);
                byte[] retained = File.ReadAllBytes(retainedPath);
                if (!string.Equals(source.Hash, digest, StringComparison.OrdinalIgnoreCase) ||
                    source.Length != retained.LongLength ||
                    !string.Equals(source.Hash,
                        Convert.ToHexString(SHA256.HashData(retained)),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "Transient selection source, bundle digest, and retained copy diverged.");
                identities.Add(new(role, path, source.Length, source.Hash,
                    "npc create-from-jslot:selection-before-promotion"));
            }
        }
        if (identities.Select(identity => Path.GetDirectoryName(identity.Path))
                .Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidOperationException(
                "Transient selection roles did not share one candidate transaction.");
        return identities.ToImmutable();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}

internal static class CompatibilityJourneySupport
{
    private const string CliVariable = "ACTORWRIGHT_COMPATIBILITY_TEST_CLI";
    private const string HashVariable =
        "ACTORWRIGHT_COMPATIBILITY_TEST_CLI_SHA256";
    private static readonly JsonSerializerOptions CanonicalProjectionOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private const string DerivedDigest = "derived-digest";
    private const string ManagerJslotId = "manager-jslot-id";
    private const string SelectionId = "selection-id";
    private const string RacemenuSelectionPath = "racemenu-selection-path";
    private const string RootPath = "owned-root-path";
    private const string ProviderPath = "owned-provider-path";
    private const string DiagnosticPath = "owned-diagnostic-path";
    private static readonly Dictionary<string, IReadOnlyDictionary<string, string>>
        ExactProjectionRules = BuildExactProjectionRules();

    internal static void RunBoundaryRegression()
    {
        JsonNode canonicalProbe = Normalize(new JsonObject
        {
            ["members"] = new JsonArray(new JsonObject
            {
                ["path"] = "x", ["length"] = 1, ["identityKind"] = "raw",
                ["digest"] = new string('A', 64)
            })
        }, @"K:\unused");
        if (Convert.ToHexString(SHA256.HashData(CanonicalProjectionBytes(canonicalProbe))) !=
            "F7CA69533FE27DB9C00D2A8F817AEEA5DCE6663DE968018D251CB0E76CE9E8F3")
            throw new InvalidOperationException("Compatibility semantic projection is not Python-canonical JSON.");
        JsonObject semanticProbe = (JsonObject)NormalizeWithRules(new JsonObject
        {
            ["targetAuthorityId"] = "manager-jslot-0123456789abcdef01234567",
            ["adjacentSha256"] = new string('B', 64),
            ["other"] = new JsonObject
            {
                ["targetAuthorityId"] = "manager-jslot-89abcdef0123456789abcdef"
            }
        }, @"K:\unused", new Dictionary<string, string>
        {
            ["/targetAuthorityId"] = ManagerJslotId
        });
        if (semanticProbe["targetAuthorityId"]?.GetValue<string>() !=
                "manager-jslot-<semantic-id>" ||
            semanticProbe["adjacentSha256"]?.GetValue<string>() != new string('B', 64) ||
            semanticProbe["other"]?["targetAuthorityId"]?.GetValue<string>() !=
                "manager-jslot-89abcdef0123456789abcdef")
            throw new InvalidOperationException(
                "Compatibility semantic projection normalized beyond its exact pointer.");
        JsonObject unrelated = (JsonObject)NormalizeWithRules(new JsonObject
        {
            ["generatedAtUtc"] = "2026-09-23T12:00:00Z",
            ["path"] = @"K:\unused\adjacent.json",
            ["sameValue"] = "manager-jslot-0123456789abcdef01234567",
            ["nullable"] = null
        }, @"K:\unused", new Dictionary<string, string>());
        if (unrelated["generatedAtUtc"]?.GetValue<string>() != "2026-09-23T12:00:00Z" ||
            unrelated["path"]?.GetValue<string>() != @"K:\unused\adjacent.json" ||
            !unrelated.ContainsKey("nullable") ||
            unrelated["sameValue"]?.GetValue<string>() !=
                "manager-jslot-0123456789abcdef01234567")
            throw new InvalidOperationException(
                "Compatibility projection changed fields without exact pointer rules.");
        JsonObject ownedPath = (JsonObject)NormalizeWithRules(new JsonObject
        {
            ["path"] = @"K:\unused\adjacent.json"
        }, @"K:\unused", new Dictionary<string, string>
        {
            ["/path"] = RootPath
        });
        if (ownedPath["path"]?.GetValue<string>() != @"<workspace-root>\adjacent.json")
            throw new InvalidOperationException("Exact owned path was not canonicalized.");
        JsonObject providerPath = (JsonObject)NormalizeWithRules(new JsonObject
        {
            ["sourcePlugin"] = @"K:\unused\companion\.actorwright-provider-inputs-0123456789abcdef0123456789abcdef\Data\Provider.esp"
        }, @"K:\unused", new Dictionary<string, string>
        {
            ["/sourcePlugin"] = ProviderPath
        });
        if (providerPath["sourcePlugin"]?.GetValue<string>() !=
            @"<workspace-root>\companion\.actorwright-provider-inputs-<token>\Data\Provider.esp")
            throw new InvalidOperationException("Exact provider input path was not canonicalized.");
        JsonObject workspaceProviderPath = (JsonObject)NormalizeWithRules(new JsonObject
        {
            ["sourcePlugin"] = @"K:\unused\npc-preflight\workspace-provider\Data\ActorwrightBlankNpcProvider.esp"
        }, @"K:\unused", new Dictionary<string, string>
        {
            ["/sourcePlugin"] = ProviderPath
        });
        if (workspaceProviderPath["sourcePlugin"]?.GetValue<string>() !=
            @"<workspace-root>\npc-preflight\workspace-provider\Data\ActorwrightBlankNpcProvider.esp")
            throw new InvalidOperationException(
                "Exact workspace provider path was not canonicalized.");
        RequireProjectionRuleRefusal(new JsonObject
        {
            ["sourcePlugin"] = @"K:\unused\npc-preflight\workspace-provider\Data\Other.esp"
        }, new Dictionary<string, string>
        {
            ["/sourcePlugin"] = ProviderPath
        }, "owned provider path");
        RequireProjectionRuleRefusal(new JsonObject
        {
            ["sourcePlugin"] = @"K:\outside\npc-preflight\workspace-provider\Data\ActorwrightBlankNpcProvider.esp"
        }, new Dictionary<string, string>
        {
            ["/sourcePlugin"] = ProviderPath
        }, "owned root path");
        JsonObject diagnostic = (JsonObject)NormalizeWithRules(new JsonObject
        {
            ["detail"] = @"Blender is missing or not an ordinary file at 'K:\unused\tools\external\blender.exe'."
        }, @"K:\unused", new Dictionary<string, string>
        {
            ["/detail"] = DiagnosticPath
        });
        if (diagnostic["detail"]?.GetValue<string>() !=
            @"Blender is missing or not an ordinary file at '<workspace-root>\tools\external\blender.exe'.")
            throw new InvalidOperationException("Exact diagnostic path was not canonicalized.");
        RequireProjectionRuleRefusal(new JsonObject(), new Dictionary<string, string>
        {
            ["/targetAuthorityId"] = ManagerJslotId
        }, "unused exact pointer");
        RequireProjectionRuleRefusal(new JsonObject
        {
            ["targetAuthorityId"] = "manager-jslot-0123456789abcdef01234567"
        }, new Dictionary<string, string>
        {
            ["/targetAuthorityId"] = "unknown"
        }, "unknown normalizer");
        RequireProjectionRuleRefusal(new JsonObject
        {
            ["targetAuthorityId"] = "manager-jslot-0123456789abcdef01234567"
        }, new Dictionary<string, string>
        {
            ["/*"] = ManagerJslotId
        }, "exact full JSON Pointer");
        string captureParent = CompatibilityJourneyFileSink.GetCaptureParent();
        Directory.CreateDirectory(captureParent);
        string collisionToken = Guid.NewGuid().ToString("N");
        string collisionRoot = Path.Combine(captureParent, "j-" + collisionToken);
        Directory.CreateDirectory(collisionRoot);
        File.WriteAllText(Path.Combine(collisionRoot, "foreign.txt"), "foreign");
        try
        {
            try
            {
                _ = ReserveBoundaryRoot(collisionToken);
                throw new InvalidOperationException("Expected preexisting root refusal.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("preexisting", StringComparison.OrdinalIgnoreCase))
            {
            }
            if (!File.Exists(Path.Combine(collisionRoot, "foreign.txt")))
                throw new InvalidOperationException("Collision handling deleted a foreign root.");
        }
        finally
        {
            File.Delete(Path.Combine(collisionRoot, "foreign.txt"));
            Directory.Delete(collisionRoot);
        }
        string racedToken = Guid.NewGuid().ToString("N");
        string racedRoot = Path.Combine(captureParent, "j-" + racedToken);
        string racedLock = racedRoot + ".owner.lock";
        try
        {
            try
            {
                _ = ReserveBoundaryRoot(racedToken, () =>
                {
                    Directory.CreateDirectory(racedRoot);
                    File.WriteAllText(Path.Combine(racedRoot, "foreign.txt"), "foreign");
                });
                throw new InvalidOperationException("Expected raced root refusal.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("preexisting", StringComparison.OrdinalIgnoreCase)) { }
            if (File.ReadAllText(Path.Combine(racedRoot, "foreign.txt")) != "foreign")
                throw new InvalidOperationException("Raced foreign root was changed.");
        }
        finally
        {
            File.Delete(Path.Combine(racedRoot, "foreign.txt"));
            File.Delete(Path.Combine(racedRoot, ".compatibility-firewall-owner.json"));
            if (Directory.Exists(racedRoot)) Directory.Delete(racedRoot);
            if (File.Exists(racedLock) && File.ReadAllText(racedLock, Encoding.ASCII) == racedToken)
                File.Delete(racedLock);
        }
        string lockCollisionToken = Guid.NewGuid().ToString("N");
        string foreignLock = Path.Combine(captureParent,
            "j-" + lockCollisionToken + ".owner.lock");
        File.WriteAllText(foreignLock, "foreign", Encoding.ASCII);
        try
        {
            try
            {
                _ = ReserveBoundaryRoot(lockCollisionToken);
                throw new InvalidOperationException("Expected foreign lock refusal.");
            }
            catch (IOException)
            {
            }
            if (File.ReadAllText(foreignLock, Encoding.ASCII) != "foreign")
                throw new InvalidOperationException("Collision handling changed a foreign lock.");
        }
        finally
        {
            File.Delete(foreignLock);
        }
        string token = Guid.NewGuid().ToString("N");
        (string root, string ownerLock) = ReserveBoundaryRoot(token);
        string probe = Path.Combine(root, "probe.json");
        byte[] probeBytes = Encoding.UTF8.GetBytes("{\"value\":1}");
        File.WriteAllBytes(probe, probeBytes);
        JsonObject probeArtifact = ArtifactJson(Artifact(root, "probe.json", "synthetic"));
        if (probeArtifact["rawSha256"]?.GetValue<string>() !=
                Convert.ToHexString(SHA256.HashData(probeBytes)) ||
            probeArtifact["relationships"] is not JsonArray)
            throw new InvalidOperationException("Semantic artifact omitted raw identity or relationship evidence.");
        string selectionRelative = "companion/selection/racemenu-selection-" +
            new string('a', 16) + "-" + new string('b', 32);
        string selectionRoot = Path.Combine(root,
            selectionRelative.Replace('/', Path.DirectorySeparatorChar));
        string recordRelative = selectionRelative + "/record-authority.json";
        string routesRelative = selectionRelative + "/runtime-routes.json";
        string recordPath = Path.Combine(selectionRoot, "record-authority.json");
        string routesPath = Path.Combine(selectionRoot, "runtime-routes.json");
        byte[] recordBytes = Encoding.UTF8.GetBytes("{\"record\":1}");
        byte[] routesBytes = Encoding.UTF8.GetBytes("{\"routes\":1}");
        string retained = Path.Combine(root, "retained-selection");
        Directory.CreateDirectory(retained);
        File.WriteAllBytes(Path.Combine(retained, "record-authority.json"), recordBytes);
        File.WriteAllBytes(Path.Combine(retained, "runtime-routes.json"), routesBytes);
        string bundle = Path.Combine(root, "selection-bundle.json");
        File.WriteAllText(bundle, new JsonObject
        {
            ["recordAuthority"] = new JsonObject
            {
                ["manifestPath"] = recordRelative,
                ["manifestSha256"] = Convert.ToHexString(SHA256.HashData(recordBytes))
            },
            ["runtimeRoutes"] = new JsonObject
            {
                ["manifestPath"] = routesRelative,
                ["manifestSha256"] = Convert.ToHexString(SHA256.HashData(routesBytes))
            }
        }.ToJsonString());
        var transient = new CompatibilityTransientSourceWatcher(root);
        try
        {
            try
            {
                _ = transient.Seal(bundle, retained);
                throw new InvalidOperationException("Missing transient source was accepted.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("not captured", StringComparison.Ordinal)) { }
            Directory.CreateDirectory(selectionRoot);
            File.WriteAllText(recordPath, "{");
            File.WriteAllBytes(routesPath, routesBytes);
            File.WriteAllBytes(recordPath, recordBytes);
            if (!SpinWait.SpinUntil(() => transient.HasCapture(recordRelative) &&
                    transient.HasCapture(routesRelative), TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException(
                    "Transient watcher missed coalesced create/change or partial-write retry.");
            if (transient.Seal(bundle, retained).Length != 2)
                throw new InvalidOperationException("Transient source identity closure was incomplete.");
            File.WriteAllText(Path.Combine(retained, "runtime-routes.json"), "{}");
            try
            {
                _ = transient.Seal(bundle, retained);
                throw new InvalidOperationException("Divergent retained copy was accepted.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("diverged", StringComparison.Ordinal)) { }
            File.WriteAllBytes(Path.Combine(retained, "runtime-routes.json"), routesBytes);
            File.WriteAllText(recordPath, "{\"record\":2}");
            if (!SpinWait.SpinUntil(() => transient.HasError, TimeSpan.FromSeconds(3)))
                throw new InvalidOperationException("Conflicting duplicate transient source was accepted.");
        }
        finally
        {
            transient.StopAsync().GetAwaiter().GetResult();
        }
        string workspace = Path.Combine(root, "c-" + token);
        string observations = Path.Combine(root, "observations");
        string output = Path.Combine(observations, "create-to-package.json");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(observations);
        string? priorRoot = Environment.GetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_CAPTURE_ROOT");
        string? priorToken = Environment.GetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_CAPTURE_TOKEN");
        string? priorOutput = Environment.GetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT");
        bool cleanupSucceeded = false;
        try
        {
            Environment.SetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_CAPTURE_ROOT", root);
            Environment.SetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_CAPTURE_TOKEN", token);
            Environment.SetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT", output);
            ICompatibilityJourneySink? ownedSink = CompatibilityJourneyFileSink.FromEnvironment(
                workspace, Path.Combine(workspace, "finished"), token);
            if (ownedSink is null)
                throw new InvalidOperationException("Owned fresh observation destination was not admitted.");
            string defaultWorkspace = Path.Combine(root, "default-cleanup-probe");
            Directory.CreateDirectory(defaultWorkspace);
            CompatibilityJourneySupport.CleanupCreateToFinishWorkspace(defaultWorkspace, null);
            if (Directory.Exists(defaultWorkspace))
                throw new InvalidOperationException("Default selector workspace cleanup changed.");
            CompatibilityJourneySupport.CleanupCreateToFinishWorkspace(workspace, ownedSink);
            if (!Directory.Exists(workspace))
                throw new InvalidOperationException("Compatibility workspace vanished before graph verification.");
            string foreignWorkspace = Path.Combine(root, "foreign-workspace");
            Directory.CreateDirectory(foreignWorkspace);
            try
            {
                CompatibilityJourneySupport.CleanupCreateToFinishWorkspace(foreignWorkspace, ownedSink);
                throw new InvalidOperationException("Foreign compatibility workspace was retained.");
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("invocation-owned", StringComparison.Ordinal)) { }
            if (!Directory.Exists(foreignWorkspace))
                throw new InvalidOperationException("Foreign workspace was deleted.");
            File.WriteAllText(output, "occupied");
            RequireBoundaryRefusal(workspace, token, "fresh");
            File.Delete(output);
            Environment.SetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT",
                Path.Combine(captureParent, "arbitrary.json"));
            RequireBoundaryRefusal(workspace, token, "invocation-owned");
            Environment.SetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT", output);
            File.WriteAllText(Path.Combine(root, ".compatibility-firewall-owner.json"), "{}");
            RequireBoundaryRefusal(workspace, token, "marker");
            if (TryCleanupOwnedBoundaryRoot(root, ownerLock, token))
                throw new InvalidOperationException("Tampered marker authorized cleanup.");
            File.Delete(Path.Combine(root, ".compatibility-firewall-owner.json"));
            WriteOwnerMarker(root, token);
            File.WriteAllText(ownerLock, new string('0', 32), Encoding.ASCII);
            if (TryCleanupOwnedBoundaryRoot(root, ownerLock, token))
                throw new InvalidOperationException("Wrong owner lock authorized cleanup.");
            File.WriteAllText(ownerLock, token, Encoding.ASCII);
        }
        finally
        {
            cleanupSucceeded = TryCleanupOwnedBoundaryRoot(root, ownerLock, token);
            Environment.SetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_CAPTURE_ROOT", priorRoot);
            Environment.SetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_CAPTURE_TOKEN", priorToken);
            Environment.SetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT", priorOutput);
        }
        if (!cleanupSucceeded)
            throw new InvalidOperationException("Owned boundary root cleanup was refused.");
        Console.WriteLine("PASS compatibility journey ownership and sink boundaries");
    }

    private static (string Root, string Lock) ReserveBoundaryRoot(
        string token, Action? afterPresenceCheck = null)
    {
        string parent = CompatibilityJourneyFileSink.GetCaptureParent();
        string root = Path.Combine(parent, "j-" + token);
        string ownerLock = Path.Combine(parent, "j-" + token + ".owner.lock");
        using (var stream = new FileStream(
                   ownerLock, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.Write(Encoding.ASCII.GetBytes(token));
        try
        {
            if (Directory.Exists(root))
                throw new InvalidOperationException(
                    "Compatibility capture root is preexisting.");
            afterPresenceCheck?.Invoke();
            if (!CreateDirectoryExclusive(root, 0))
            {
                int error = Marshal.GetLastPInvokeError();
                if (error == 183)
                    throw new InvalidOperationException(
                        "Compatibility capture root is preexisting.");
                throw new IOException(
                    "Compatibility capture root could not be reserved: Windows error " + error);
            }
            WriteOwnerMarker(root, token);
            if (!File.Exists(Path.Combine(root, ".compatibility-firewall-owner.json")) ||
                File.ReadAllText(ownerLock, Encoding.ASCII) != token)
                throw new InvalidOperationException("Compatibility capture ownership changed during reservation.");
            return (root, ownerLock);
        }
        catch
        {
            if (File.Exists(ownerLock) &&
                File.ReadAllText(ownerLock, Encoding.ASCII) == token)
                File.Delete(ownerLock);
            throw;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW",
        SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryExclusive(string path, nint securityAttributes);

    private static void WriteOwnerMarker(string root, string token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["formatVersion"] = 1, ["kind"] = "journey-capture", ["token"] = token
        });
        using var stream = new FileStream(
            Path.Combine(root, ".compatibility-firewall-owner.json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    private static bool TryCleanupOwnedBoundaryRoot(
        string root, string ownerLock, string token)
    {
        try
        {
            string expectedRoot = Path.Combine(
                CompatibilityJourneyFileSink.GetCaptureParent(), "j-" + token);
            if (!string.Equals(Path.GetFullPath(root), expectedRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(ownerLock), expectedRoot + ".owner.lock",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            Environment.SetEnvironmentVariable(
                "ACTORWRIGHT_COMPATIBILITY_CAPTURE_ROOT", root);
            Environment.SetEnvironmentVariable(
                "ACTORWRIGHT_COMPATIBILITY_CAPTURE_TOKEN", token);
            _ = CompatibilityJourneyFileSink.RequireOwnedCaptureRoot();
            if (File.ReadAllText(ownerLock, Encoding.ASCII) != token)
                return false;
            Directory.Delete(root, recursive: true);
            File.Delete(ownerLock);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void RequireBoundaryRefusal(string workspace, string token, string messagePart)
    {
        try
        {
            CompatibilityJourneyFileSink.FromEnvironment(
                workspace, Path.Combine(workspace, "finished"), token);
            throw new InvalidOperationException("Expected compatibility boundary refusal.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(messagePart, StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    internal static string CreateCreateToFinishRoot(string repositoryRoot)
    {
        string? observation = Environment.GetEnvironmentVariable(
            "ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT");
        if (string.IsNullOrWhiteSpace(observation))
            return Path.Combine(repositoryRoot, "artifacts", "test-work",
                "create-finish-" + Guid.NewGuid().ToString("N"));
        return CompatibilityRootFromObservation(
            observation, "c-");
    }

    internal static void CleanupCreateToFinishWorkspace(
        string workspaceRoot, ICompatibilityJourneySink? sink)
    {
        if (sink is null)
        {
            Directory.Delete(workspaceRoot, recursive: true);
            return;
        }
        // Defer only the exact invocation-owned child. Python verifies its raw
        // bytes and then removes the parent with the matching marker and token.
        string captureRoot = CompatibilityJourneyFileSink.RequireOwnedCaptureRoot();
        string token = Environment.GetEnvironmentVariable(
            "ACTORWRIGHT_COMPATIBILITY_CAPTURE_TOKEN")!;
        string expected = Path.Combine(captureRoot, "c-" + token);
        if (!string.Equals(Path.GetFullPath(workspaceRoot), expected,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Only the invocation-owned create workspace may be retained.");
    }

    internal static string CreateWaveBRoot(string repositoryRoot)
    {
        string? observation = Environment.GetEnvironmentVariable(
            "ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT");
        if (string.IsNullOrWhiteSpace(observation))
            return Path.Combine(repositoryRoot, "artifacts", "task38",
                "run-" + Guid.NewGuid().ToString("N"));
        return CompatibilityRootFromObservation(
            observation, "w-");
    }

    private static string CompatibilityRootFromObservation(
        string observation,
        string prefix)
    {
        string captureRoot = CompatibilityJourneyFileSink.RequireOwnedCaptureRoot();
        string operationId = Environment.GetEnvironmentVariable(
            "ACTORWRIGHT_COMPATIBILITY_CAPTURE_TOKEN")!;
        string root = Path.Combine(captureRoot, prefix + operationId);
        if (Directory.Exists(root))
            throw new InvalidOperationException(
                "The compatibility journey workspace must be fresh: " + root);
        return root;
    }

    internal static (string Path, bool UseDotNet) ResolveCli(string defaultAssembly)
    {
        string? configured = Environment.GetEnvironmentVariable(CliVariable);
        string? expectedHash = Environment.GetEnvironmentVariable(HashVariable);
        if (string.IsNullOrWhiteSpace(configured) &&
            string.IsNullOrWhiteSpace(expectedHash))
            return (defaultAssembly, true);
        if (string.IsNullOrWhiteSpace(configured) ||
            string.IsNullOrWhiteSpace(expectedHash))
            throw new InvalidOperationException(
                $"{CliVariable} and {HashVariable} must be set together.");
        if (!Path.IsPathFullyQualified(configured))
            throw new InvalidOperationException($"{CliVariable} must be absolute.");
        string canonical = Path.GetFullPath(configured);
        if (!string.Equals(Path.GetPathRoot(canonical), @"K:\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{CliVariable} must be K-local; F: and other roots are forbidden.");
        string suffix = Path.GetExtension(canonical);
        if (!string.Equals(suffix, ".dll", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(suffix, ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{CliVariable} must name a supported .dll or .exe.");
        if (!File.Exists(canonical))
            throw new InvalidOperationException($"{CliVariable} does not exist.");
        if (expectedHash.Length != 64 || expectedHash.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException($"{HashVariable} is malformed.");
        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(canonical)));
        if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{CliVariable} SHA-256 mismatch.");
        return (canonical, string.Equals(suffix, ".dll", StringComparison.OrdinalIgnoreCase));
    }

    internal static string ExitMeaning(int exitCode) => exitCode switch
    {
        0 => "Success",
        1 => "General failure",
        2 => "Usage or schema error",
        3 => "Security refusal",
        4 => "Domain validation failure",
        5 => "Cancellation",
        _ => throw new InvalidOperationException(
            $"The journey observed unsupported exit code {exitCode}.")
    };

    internal static ImmutableArray<string> SchemaIds(JsonElement root)
    {
        var values = new SortedSet<string>(StringComparer.Ordinal);
        Visit(root);
        return values.ToImmutableArray();

        void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        property.Name.Contains("schema", StringComparison.OrdinalIgnoreCase))
                    {
                        string? value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                            values.Add(value);
                    }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (JsonElement child in element.EnumerateArray())
                    Visit(child);
        }
    }

    internal static CompatibilityArtifactObservation Artifact(
        string root,
        string relative,
        string role)
    {
        string path = Path.Combine(
            root,
            relative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"Required journey artifact is missing: {relative}");
        byte[] bytes = File.ReadAllBytes(path);
        if (string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase))
            return SemanticJsonArtifact(root, relative, role, bytes);
        if (string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            return SemanticZipArtifact(root, relative, role, path, bytes.LongLength);
        return new CompatibilityArtifactObservation(relative, role, "raw", bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)), null, null);
    }

    internal static JsonObject ArtifactJson(CompatibilityArtifactObservation artifact)
    {
        var value = new JsonObject
        {
            ["path"] = artifact.Path,
            ["role"] = artifact.Role,
            ["identityKind"] = artifact.IdentityKind,
            ["length"] = artifact.Length
        };
        if (artifact.Sha256 is not null) value["sha256"] = artifact.Sha256;
        if (artifact.SemanticSha256 is not null) value["semanticSha256"] = artifact.SemanticSha256;
        if (artifact.Projection is not null) value["projection"] = artifact.Projection.DeepClone();
        if (artifact.RawSha256 is not null) value["rawSha256"] = artifact.RawSha256;
        if (artifact.Relationships is not null) value["relationships"] = artifact.Relationships.DeepClone();
        return value;
    }

    private static CompatibilityArtifactObservation SemanticJsonArtifact(
        string root, string relative, string role, byte[] bytes)
    {
        JsonNode projection = Normalize(
            JsonNode.Parse(bytes)!, Path.GetFullPath(root), relative);
        string hash = Convert.ToHexString(SHA256.HashData(CanonicalProjectionBytes(projection)));
        return new(relative, role, "semantic-json", bytes.LongLength, null, hash, projection,
            Convert.ToHexString(SHA256.HashData(bytes)), new JsonArray());
    }

    private static CompatibilityArtifactObservation SemanticZipArtifact(
        string root, string relative, string role, string path, long length)
    {
        var members = new JsonArray();
        using ZipArchive archive = ZipFile.OpenRead(path);
        foreach (ZipArchiveEntry entry in archive.Entries
                     .Where(item => !string.IsNullOrEmpty(item.Name))
                     .OrderBy(item => item.FullName, StringComparer.Ordinal))
        {
            using Stream stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            byte[] bytes = memory.ToArray();
            var member = new JsonObject
            {
                ["path"] = entry.FullName.Replace('\\', '/'),
                ["length"] = bytes.LongLength
            };
            if (string.Equals(Path.GetExtension(entry.Name), ".json", StringComparison.OrdinalIgnoreCase))
            {
                string memberIdentity = Path.GetFileNameWithoutExtension(relative) + "/" +
                    entry.FullName.Replace('\\', '/');
                JsonNode projection = Normalize(
                    JsonNode.Parse(bytes)!, Path.GetFullPath(root), memberIdentity);
                member["identityKind"] = "semantic-json";
                member["semanticSha256"] = Convert.ToHexString(SHA256.HashData(
                    CanonicalProjectionBytes(projection)));
                member["projection"] = projection;
                member["rawSha256"] = Convert.ToHexString(SHA256.HashData(bytes));
                member["relationships"] = new JsonArray();
            }
            else
            {
                member["identityKind"] = "raw";
                member["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
            }
            members.Add(member);
        }
        JsonNode zipProjection = Normalize(
            new JsonObject { ["members"] = members }, Path.GetFullPath(root));
        string hash = Convert.ToHexString(SHA256.HashData(
            CanonicalProjectionBytes(zipProjection)));
        return new(relative, role, "semantic-zip", length, null, hash, zipProjection,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), new JsonArray());
    }

    private static JsonNode Normalize(
        JsonNode node,
        string root,
        string? sourceIdentity = null)
    {
        string? identity = sourceIdentity?.Replace('\\', '/');
        IReadOnlyDictionary<string, string> rules = identity is not null &&
            ExactProjectionRules.TryGetValue(identity, out var found)
                ? found
                : new Dictionary<string, string>();
        if (Path.GetFileName(root).StartsWith("w-", StringComparison.Ordinal) &&
            identity is "planned-npc-output/evidence/npc-creation-proposal.json" or
                "finished/evidence/npc-creation-proposal.json" or
                "finished/Data/evidence/npc-creation-proposal.json")
        {
            var waveRules = new Dictionary<string, string>(rules, StringComparer.Ordinal)
            {
                ["/pluginAuthorities/2/pluginPath"] = RootPath
            };
            rules = waveRules;
        }
        return NormalizeWithRules(node, root, rules);
    }

    private static JsonNode NormalizeWithRules(
        JsonNode node,
        string root,
        IReadOnlyDictionary<string, string> rules)
    {
        foreach ((string pointer, string normalizer) in rules)
        {
            if (!pointer.StartsWith('/') ||
                pointer.Length == 1 || pointer.Contains('*') ||
                pointer.EndsWith('/'))
                throw new InvalidOperationException(
                    "Compatibility projection rule must use an exact full JSON Pointer.");
            if (normalizer is not (DerivedDigest or ManagerJslotId or SelectionId or RacemenuSelectionPath or RootPath or ProviderPath or DiagnosticPath))
                throw new InvalidOperationException(
                    "Compatibility projection rule has an unknown normalizer.");
        }
        var unused = rules.Keys.ToHashSet(StringComparer.Ordinal);
        JsonNode normalized = NormalizeNode(node, root, "", rules, unused);
        if (unused.Count != 0)
            throw new InvalidOperationException(
                "Compatibility projection rule has an unused exact pointer: " +
                unused.Order(StringComparer.Ordinal).First());
        return normalized;
    }

    private static JsonNode NormalizeNode(
        JsonNode node,
        string root,
        string pointer,
        IReadOnlyDictionary<string, string> rules,
        HashSet<string> unused)
    {
        if (node is JsonObject input)
        {
            var output = new JsonObject();
            foreach ((string key, JsonNode? value) in input.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                string childPointer = pointer + "/" +
                    key.Replace("~", "~0", StringComparison.Ordinal)
                        .Replace("/", "~1", StringComparison.Ordinal);
                if (value is not null)
                    output[key] = NormalizeNode(
                        value, root, childPointer, rules, unused);
                else
                    output[key] = null;
            }
            return output;
        }
        if (node is JsonArray array)
            return new JsonArray(array.Select((value, index) => value is null
                ? null
                : NormalizeNode(
                    value, root, pointer + "/" + index, rules, unused)).ToArray());
        if (node is JsonValue scalar && scalar.TryGetValue<string>(out string? text))
        {
            if (rules.TryGetValue(pointer, out string? normalizer))
            {
                unused.Remove(pointer);
                return NormalizeExactScalar(pointer, normalizer, text, root);
            }
            return scalar.DeepClone();
        }
        return node.DeepClone();
    }

    private static JsonValue NormalizeExactScalar(
        string pointer,
        string normalizer,
        string value,
        string root)
    {
        if (normalizer == RootPath)
            return JsonValue.Create(CanonicalizeOwnedPath(pointer, value, root))!;
        if (normalizer == ProviderPath)
        {
            string canonical = CanonicalizeOwnedPath(pointer, value, root);
            const string workspaceProvider =
                @"<workspace-root>\npc-preflight\workspace-provider\Data\ActorwrightBlankNpcProvider.esp";
            if (canonical.Equals(workspaceProvider, StringComparison.OrdinalIgnoreCase))
                return JsonValue.Create(workspaceProvider)!;
            const string prefix = @"<workspace-root>\companion\.actorwright-provider-inputs-";
            if (!canonical.StartsWith(prefix, StringComparison.Ordinal) ||
                canonical.Length <= prefix.Length + 33 ||
                !canonical.AsSpan(prefix.Length, 32).ToString().All(Uri.IsHexDigit) ||
                canonical[prefix.Length + 32] != Path.DirectorySeparatorChar)
                throw new InvalidOperationException(
                    $"Compatibility projection rule {pointer} requires an owned provider path.");
            return JsonValue.Create(prefix + "<token>" + canonical[(prefix.Length + 32)..])!;
        }
        if (normalizer == DiagnosticPath)
        {
            string[] prefixes =
            [
                "Blender is missing or not an ordinary file at '",
                "Blender profile is missing or not an ordinary directory at '",
                "Texconv is missing or not an ordinary file at '"
            ];
            string? prefix = prefixes.FirstOrDefault(value.StartsWith);
            if (prefix is null || !value.EndsWith("'.", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Compatibility projection rule {pointer} requires a known diagnostic path.");
            string path = value[prefix.Length..^2];
            string canonical = CanonicalizeOwnedPath(pointer, path, root);
            if (!canonical.StartsWith(@"<workspace-root>\tools\external\", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Compatibility projection rule {pointer} requires a tool diagnostic path.");
            return JsonValue.Create(prefix + canonical + "'.")!;
        }
        if (normalizer == DerivedDigest &&
            value.Length == 64 && value.All(Uri.IsHexDigit))
            return JsonValue.Create(value)!;
        if (normalizer == ManagerJslotId &&
            Regex.IsMatch(value, "^manager-jslot-[0-9a-f]{24}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return JsonValue.Create("manager-jslot-<semantic-id>")!;
        if (normalizer == SelectionId &&
            Regex.IsMatch(value, "^selection-(?:bundle|record|routes|assets)-[0-9a-f]{24}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return JsonValue.Create(Regex.Replace(
                value, "[0-9a-f]{24}$", "<semantic-id>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))!;
        if (normalizer == RacemenuSelectionPath &&
            Regex.IsMatch(value, "racemenu-selection-[0-9a-f]{16}-[0-9a-f]{32}/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return JsonValue.Create(Regex.Replace(
                value, "(?<=racemenu-selection-[0-9a-f]{16}-)[0-9a-f]{32}", "<token>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))!;
        throw new InvalidOperationException(
            $"Compatibility projection rule {pointer} did not match its exact value shape.");
    }

    private static string CanonicalizeOwnedPath(string pointer, string value, string root)
    {
        string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (value.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase))
            return "<workspace-root>";
        if (!value.StartsWith(canonicalRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            value.Split(Path.DirectorySeparatorChar).Contains("..", StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Compatibility projection rule {pointer} requires an owned root path.");
        return "<workspace-root>" + value[canonicalRoot.Length..];
    }

    private static void RequireProjectionRuleRefusal(
        JsonNode node,
        IReadOnlyDictionary<string, string> rules,
        string expected)
    {
        try
        {
            _ = NormalizeWithRules(node, @"K:\unused", rules);
            throw new InvalidOperationException(
                "Expected compatibility projection rule refusal.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>>
        BuildExactProjectionRules()
    {
        static IReadOnlyDictionary<string, string> Hashes(params string[] pointers) =>
            pointers.ToDictionary(pointer => pointer, _ => DerivedDigest, StringComparer.Ordinal);
        static IReadOnlyDictionary<string, string> Rules(
            params (string Pointer, string Normalizer)[] values) =>
            values.ToDictionary(value => value.Pointer, value => value.Normalizer, StringComparer.Ordinal);
        static string[] PackageManifestHashes() =>
        [
            "/artifacts/3/sha256", "/artifacts/10/sha256",
            "/artifacts/12/sha256", "/artifacts/14/sha256",
            "/artifacts/15/sha256", "/sourcePresetSha256"
        ];
        static string[] FinishProposalHashes() =>
        [
            "/proposalSha256", "/request/source/packageManifestSha256",
            "/request/source/packageTreeSha256", "/requestSha256"
        ];
        static string[] FinishManifestHashes() =>
        [
            "/evidence/files/0/sha256", "/evidence/files/1/sha256",
            "/evidence/packageTreeSha256", "/evidence/sourcePackageTreeSha256",
            "/packageTreeSha256", "/proposalSha256", "/requestSha256",
            "/sourcePackageTreeSha256"
        ];
        static IReadOnlyDictionary<string, string> RacemenuBundleRules() => Rules(
            ("/bundleId", SelectionId),
            ("/recordAuthority/manifestPath", RacemenuSelectionPath),
            ("/recordAuthority/manifestSha256", DerivedDigest),
            ("/runtimeRoutes/manifestPath", RacemenuSelectionPath),
            ("/runtimeRoutes/manifestSha256", DerivedDigest));
        static IReadOnlyDictionary<string, string> RuntimeRoutesRules() => Rules(
            ("/bundleId", SelectionId), ("/routeId", SelectionId));

        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.Ordinal)
        {
            ["evidence/19-response.json"] = Hashes("/sha256"),
            ["evidence/20-response.json"] = Rules(
                ("/targetAuthorityId", ManagerJslotId)),
            ["evidence/24-response.json"] = Hashes("/sha256"),
            ["evidence/25-response.json"] = Rules(
                ("/manifestSha256", DerivedDigest),
                ("/targetAuthorityId", ManagerJslotId)),
            ["evidence/27-response.json"] = Hashes("/proposalSha256"),
            ["evidence/29-response.json"] = Hashes(
                "/verification/archiveSha256/value",
                "/verification/packageTreeSha256/value",
                "/verification/sourcePackageTreeSha256/value"),
            ["evidence/30-response.json"] = Hashes("/proposalSha256"),
            ["evidence/create-before-finish-fixture/npcmanager-package.json"] =
                Hashes(PackageManifestHashes()),
            ["evidence/finish-proposal.json"] = Hashes(FinishProposalHashes()),
            ["evidence/finish-request.json"] = Hashes(
                "/source/packageManifestSha256", "/source/packageTreeSha256"),
            ["evidence/finish-request-canonical.json"] = Hashes(
                "/source/packageManifestSha256", "/source/packageTreeSha256"),
            ["evidence/placement-proposal.json"] = Hashes(
                "/proposalSha256", "/requestSha256"),
            ["evidence/placement-request.json"] = Hashes(
                "/finishCore/manifestSha256"),
            ["finished/Data/NPCManager/Evidence/racemenu-bundle.json"] =
                RacemenuBundleRules(),
            ["finished/Data/NPCManager/Evidence/record-authority.json"] = Rules(
                ("/authorityId", SelectionId)),
            ["finished/Data/NPCManager/Evidence/runtime-routes.json"] =
                RuntimeRoutesRules(),
            ["finished/Data/NPCManager/Evidence/standalone-assets.json"] = Rules(
                ("/assetSetId", SelectionId)),
            ["finished/NPCManager/Evidence/finish-core-manifest.json"] =
                Hashes(FinishManifestHashes()),
            ["finished/NPCManager/Evidence/finish-core-proposal.json"] =
                Hashes(FinishProposalHashes()),
            ["finished/NPCManager/Evidence/finish-core-request.json"] = Hashes(
                "/source/packageManifestSha256", "/source/packageTreeSha256"),
            ["finished/NPCManager/Evidence/finish-core-verification.json"] = Hashes(
                "/packageTreeSha256", "/sourcePackageTreeSha256"),
            ["finished/NPCManager/finish-core-source-package-manifest.json"] =
                Hashes(PackageManifestHashes()),
            ["finished/Data/NPCManager/finish-core-source-package-manifest.json"] =
                Hashes(PackageManifestHashes()),
            ["finished/npcmanager-package.json"] = Hashes(
                "/artifacts/1/sha256", "/artifacts/2/sha256",
                "/artifacts/3/sha256", "/artifacts/6/sha256",
                "/artifacts/14/sha256", "/artifacts/15/sha256",
                "/artifacts/16/sha256", "/artifacts/17/sha256",
                "/artifacts/19/sha256", "/artifacts/24/sha256",
                "/sourcePresetSha256"),
            ["finished/Data/npcmanager-package.json"] = Hashes(
                "/artifacts/1/sha256", "/artifacts/2/sha256",
                "/artifacts/3/sha256", "/artifacts/6/sha256",
                "/artifacts/14/sha256", "/artifacts/15/sha256",
                "/artifacts/16/sha256", "/artifacts/17/sha256",
                "/artifacts/19/sha256", "/artifacts/24/sha256",
                "/sourcePresetSha256"),
            ["placement/interior-placement.manifest.json"] = Hashes(
                "/archiveSha256", "/finishCoreManifestSha256",
                "/proposalSha256", "/requestSha256"),
            ["planned-npc-output/Data/NPCManager/Evidence/racemenu-bundle.json"] =
                RacemenuBundleRules(),
            ["planned-npc-output/Data/NPCManager/Evidence/record-authority.json"] = Rules(
                ("/authorityId", SelectionId)),
            ["planned-npc-output/Data/NPCManager/Evidence/runtime-routes.json"] =
                RuntimeRoutesRules(),
            ["planned-npc-output/Data/NPCManager/Evidence/standalone-assets.json"] = Rules(
                ("/assetSetId", SelectionId)),
            ["planned-npc-output/npcmanager-package.json"] =
                Hashes(PackageManifestHashes())
        };
        void Add(string identity, string normalizer, params string[] pointers)
        {
            var rules = result.TryGetValue(identity, out var existing)
                ? new Dictionary<string, string>(existing, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pointer in pointers)
                if (!rules.TryAdd(pointer, normalizer))
                    throw new InvalidOperationException(
                        $"Duplicate exact projection rule {identity}{pointer}.");
            result[identity] = rules;
        }

        string[] diagnostics =
        [
            "/optionalPreview/2/detail", "/optionalPreview/3/detail",
            "/optionalPreview/4/detail"
        ];
        Add("evidence/17-response.json", RootPath, "/workspaceRoot", "/outputRoot");
        foreach (string identity in new[] { "evidence/19-response.json", "evidence/24-response.json" })
        {
            Add(identity, RootPath, "/path");
            Add(identity, DiagnosticPath, diagnostics);
        }
        Add("evidence/22-response.json", RootPath, "/output", "/proposal");
        Add("evidence/23-response.json", RootPath, "/output", "/plugin");
        Add("evidence/25-response.json", RootPath, "/companionFaceGeom",
            "/companionFaceTint", "/companionPreset", "/manifest", "/plugin");
        Add("evidence/27-response.json", RootPath, "/proposalPath");
        Add("evidence/28-response.json", RootPath, "/archive", "/outputRoot");
        Add("evidence/30-response.json", RootPath, "/proposalPath");
        Add("evidence/31-response.json", RootPath, "/archive", "/manifest", "/outputRoot");

        string[] preflightPaths =
        [
            "/authorities/0/identity", "/authorities/1/identity",
            "/authorities/2/identity", "/authorities/3/identity",
            "/authorities/4/identity", "/authorities/5/identity",
            "/authorities/6/identity", "/authorities/7/identity",
            "/authorities/8/identity", "/authorities/9/identity",
            "/authorities/10/identity", "/authorities/11/identity",
            "/authorities/12/identity", "/authorities/13/identity",
            "/authorities/14/identity",
            "/finalDependencyClosure/0/identity",
            "/finalDependencyClosure/1/identity",
            "/finalDependencyClosure/2/identity",
            "/finalDependencyClosure/3/identity",
            "/plannedOutputs/0/path", "/plannedOutputs/1/path",
            "/plannedOutputs/2/path", "/plannedOutputs/3/path",
            "/plannedOutputs/4/path", "/plannedOutputs/5/path",
            "/plannedOutputs/6/path", "/plannedOutputs/7/path",
            "/plannedOutputs/8/path", "/plannedOutputs/9/path",
            "/sourceRequest"
        ];
        foreach (string identity in new[] { "evidence/initial-preflight.json", "evidence/repaired-preflight.json" })
        {
            Add(identity, RootPath, preflightPaths);
            Add(identity, DiagnosticPath, diagnostics);
        }
        foreach (string identity in new[]
                 {
                     "evidence/create-before-finish-fixture/npcmanager-package.json",
                     "planned-npc-output/npcmanager-package.json",
                     "finished/NPCManager/finish-core-source-package-manifest.json",
                     "finished/Data/NPCManager/finish-core-source-package-manifest.json",
                     "finished/npcmanager-package.json",
                     "finished/Data/npcmanager-package.json"
                 })
            Add(identity, ProviderPath, "/sourcePlugin");
        foreach (string identity in new[]
                 {
                     "planned-npc-output/evidence/npc-creation-proposal.json",
                     "finished/evidence/npc-creation-proposal.json",
                     "finished/Data/evidence/npc-creation-proposal.json"
                 })
        {
            Add(identity, RootPath, "/output", "/proposal",
                "/pluginAuthorities/0/pluginPath", "/pluginAuthorities/1/pluginPath");
            Add(identity, ProviderPath, "/templatePlugin");
        }
        return result;
    }

    private static byte[] CanonicalProjectionBytes(JsonNode projection) =>
        Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(projection, CanonicalProjectionOptions) + "\n");
}
