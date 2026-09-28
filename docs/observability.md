# Local observability

Actorwright exposes the native .NET EventSource provider
`Actorwright-Observability`. CLI collection is opt-in. The desktop enables its
failure-evidence keyword through a built-in listener after workspace admission;
the other keywords still require a diagnostic collector. The provider does not open a log file,
change command output, grant write authority, or replace existing operation
journals.

## Event boundaries

| ID | Event | Meaning |
| --- | --- | --- |
| 1 | Dispatch start | A parsed command enters the Protocol 1, Protocol 2, or unsupported-protocol route. |
| 2 | Dispatch stop | Dispatch returned its existing exit code. This is not independent proof that an artifact is valid. |
| 3 | Dispatch fault | An exception escaped dispatch. The original exception is rethrown; its text is not recorded. |
| 4 | Policy decision | One workspace/output or workspace/read policy evaluation completed. Later domain checks may still refuse the operation. |
| 5 | Workflow outcome | One initial or retained advance attempt completed or failed at the bundle-transition service boundary. Success follows the existing write and readback checks. |
| 6 | Policy shadow comparison | An opt-in independent-model comparison matched, mismatched, or failed for one output or read evaluation; it does not affect authorization. |
| 7 | Desktop subprocess failure | An observed nonzero subprocess exit carries a trace ID, operation and stage, logical tool identity, admitted executable SHA-256, exit code and bounded diagnostic code. |

The dispatch pair carries a newly generated invocation ID, a closed route value,
and a canonical command name or `unknown`. Stop carries the exit code and elapsed
milliseconds; fault carries a closed failure kind. Policy and workflow fields
are closed operation, decision, reason, phase, outcome and failure identifiers.
Policy shadow carries only the policy kind and match, mismatch, or failure status.
Named values live beside the provider in
`src/NpcManager.Application/ActorwrightObservabilityEventSource.cs`.

The invocation ID correlates dispatch events. It does not claim cross-process or
automatic policy/workflow correlation. The provider does not emit raw arguments,
paths, request digests, user correlation values, diagnostic messages, NPC or
reviewer identity, or exception text.

Policy observation covers the existing `KOnlyWorkspacePolicy` checks without
replacing them. Workflow observation covers `WriteInitial` and the three retained
advance entrypoints; delegating wrappers do not add duplicate events. A refused or failed event does not assert that no artifact was written before the failure; use the existing diagnostics and artifact checks to establish that. Other
domain-specific workflows have command-level terminal observation and any
existing scoped journal behavior. This is not a trace of every internal decision
and provides no Skyrim appearance, audio, animation, combat or save proof.

## Default desktop failure records

The desktop records handled blank-NPC and RaceMenu build failures and observed
Blender/texconv subprocess failures without an external tracing tool. Records
live under the admitted workspace's `.actorwright/work/desktop-failure-evidence/`.
The trace ID connects a build's terminal failure to its subprocess records;
these records contain diagnostic codes, not raw arguments, paths, output streams
or exception messages. An executable hash identifies the admitted tool and
does not independently prove the tool's output correct. No exit code is invented
when a process did not start or its exit was not observed.

Crash reports remain under `.actorwright/work/desktop-crash-reports/`. A startup
failure after report initialization uses origin `startup`; the failure message
includes the report path or states that reporting failed. Crash reports retain
their existing exception fields, with bounded text lengths. They can contain
local paths or user data in exception text; inspect them before sharing.

Failure records are limited to 128 files of at most 16 KiB each. Crash reports
are limited to 32 files of at most 64 KiB each. Each directory has a 2 MiB retained
record budget. Retention targets only exact generated record names, preserving
unrelated files. Pinned file operations reject reparse substitution and overlap
with the configured protected root. A workspace-scoped native mutex serializes
retention and publication across desktop processes in the same Windows session;
lock acquisition is bounded. These records support diagnosis after restarting
the desktop; they are not a complete or tamper-proof audit log.

Policy shadow observation is opt-in and compares the independent model's full
ordered diagnostic-code/severity result and allow/refuse result with the existing
policy result, using the same raw filesystem observations. It does not compare or
replace diagnostic message text; the existing policy's messages remain
authoritative. Shadow results are diagnostic evidence only, not a second
permission authority or general proof of filesystem safety. The shadow event
records no paths, messages, or raw diagnostic codes.

## Collect one command locally

First complete the canonical Release build (`tools/build/build.ps1` with the pinned SDK). The recipe requires `src/NpcManager.Cli/bin/Release/net10.0/actorwright.dll` to exist. Run it from the K-local source checkout being examined.
The child PowerShell confines the diagnostic environment settings to this
invocation. The trace directory is an explicit collector output, not an
application-created journal.

```powershell
pwsh -NoProfile -Command {
    $repositoryRoot = (Get-Location).Path
    $traceRoot = Join-Path $repositoryRoot 'artifacts\observability'
    New-Item -ItemType Directory -Force -Path $traceRoot | Out-Null
    $env:ACTORWRIGHT_WORKSPACE_ROOT = $repositoryRoot
    $env:DOTNET_EnableEventPipe = '1'
    $env:DOTNET_EventPipeConfig = 'Actorwright-Observability:7:4'
    $env:DOTNET_EventPipeCircularMB = '10'
    $env:DOTNET_EventPipeOutputStreaming = '0'
    $env:DOTNET_EventPipeOutputPath = Join-Path $traceRoot 'actorwright-{pid}.nettrace'
    & 'K:\Actorwright\artifacts\tools\dotnet-sdk-10.0.301\dotnet.exe' `
        (Join-Path $repositoryRoot 'src\NpcManager.Cli\bin\Release\net10.0\actorwright.dll') `
        version --json
    exit $LASTEXITCODE
}
```

Keyword masks are hexadecimal: `1` selects dispatch, `2` policy, `4` workflow,
`7` the original three, `8` policy shadow, `F` those four, `10` desktop subprocess
failures, and `1F` all five. Level `4` is
Informational. The circular-buffer value `10` is hexadecimal for 16 MiB; it
bounds the runtime buffer, not a promised exact final file size. Collection ends
with the process. Prefer a short reproduction and only the keywords needed,
because policy checks can be frequent and full buffers can drop events. Tools
such as dotnet-trace or PerfView can inspect the trace; no collector dependency
is added to Actorwright.

The sample runs `version`, which emits dispatch events but does not evaluate
workspace policy. Inside that child PowerShell block, keep the other collector
settings, set mask `8`, and replace the `version` invocation with this read-only
Protocol 1 preflight, which evaluates output policy for a K-local workspace:

```powershell
$env:DOTNET_EventPipeConfig = 'Actorwright-Observability:8:4'
& 'K:\Actorwright\artifacts\tools\dotnet-sdk-10.0.301\dotnet.exe' `
    (Join-Path $repositoryRoot 'src\NpcManager.Cli\bin\Release\net10.0\actorwright.dll') `
    workspace preflight --protocol 1 --json `
    --workspace-root $repositoryRoot `
    --output-root $traceRoot
```

The provider's restricted payload does not make the entire runtime trace safe to
publish: a trace container can contain process/runtime metadata. Inspect it before
sharing. EventSource observations are diagnostic evidence, not a complete or
tamper-proof audit log. CLI collection overhead belongs to an explicitly enabled
diagnostic session; the desktop's bounded failure capture is enabled by default.
Reporting failures must not replace primary operation results.

The environment configuration follows Microsoft's
[EventPipe documentation](https://learn.microsoft.com/dotnet/core/diagnostics/eventpipe).
