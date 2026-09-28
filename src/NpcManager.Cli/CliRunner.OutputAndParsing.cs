using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

public sealed partial class CliRunner
{
    private static CommandExitCode ExitCodeForDiagnostics(ImmutableArray<Diagnostic> diagnostics)
    {
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(diagnostics);
    }

    private static bool TryBuildInventoryRequest(ParsedCommand command, out GameInventoryRequest request, out string errorMessage)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        {
            errorMessage = "The command requires --edition fallout4|skyrimse.";
            return false;
        }
        var pluginOption = command.Options.GetValueOrDefault("plugin");
        var dataRootValue = command.Options.GetValueOrDefault("data-root");
        if (dataRootValue is null && pluginOption is not null)
        {
            try
            {
                var pluginPath = new WorkspacePath(pluginOption);
                dataRootValue = Directory.GetParent(pluginPath.Value)?.FullName;
            }
            catch (ArgumentException exception)
            {
                errorMessage = exception.Message;
                return false;
            }
        }
        if (dataRootValue is null)
        {
            errorMessage = "The command requires an explicit --data-root or --plugin path.";
            return false;
        }
        try
        {
            FormId? formId = null;
            if (command.Options.TryGetValue("form-id", out var formIdValue))
            {
                if (!FormId.TryParse(formIdValue, out var parsed))
                {
                    errorMessage = "--form-id must be hexadecimal, for example 0x00000800.";
                    return false;
                }
                formId = parsed;
            }

            var plugins = ImmutableArray<PluginName>.Empty;
            var pluginValue = command.Options.GetValueOrDefault("plugins");
            if (pluginValue is null && pluginOption is not null)
                pluginValue = Path.GetFileName(pluginOption);
            if (!string.IsNullOrWhiteSpace(pluginValue))
            {
                var builder = ImmutableArray.CreateBuilder<PluginName>();
                foreach (var value in pluginValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) builder.Add(new PluginName(value));
                plugins = builder.ToImmutable();
            }
            if (!TryParseCategories(command, out var categories, out errorMessage)) return false;
            var changedOnly = command.Options.TryGetValue("changed-only", out var changedValue) &&
                              string.Equals(changedValue, "true", StringComparison.OrdinalIgnoreCase);
            request = new GameInventoryRequest(edition, new WorkspacePath(dataRootValue),
                command.Options.GetValueOrDefault("search"), formId, false, plugins, categories, changedOnly);
            errorMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static bool TryParseCategories(ParsedCommand command,
        out ImmutableHashSet<NpcCategory>? categories, out string errorMessage)
    {
        categories = null;
        errorMessage = string.Empty;
        var filter = command.Options.GetValueOrDefault("filter") ?? command.Options.GetValueOrDefault("category");
        if (string.IsNullOrWhiteSpace(filter)) return true;
        var builder = ImmutableHashSet.CreateBuilder<NpcCategory>();
        foreach (var value in filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<NpcCategory>(value, true, out var category))
            {
                errorMessage = "--filter must contain only unique,generic,template,unused.";
                return false;
            }
            builder.Add(category);
        }
        categories = builder.ToImmutable();
        return true;
    }

    private CommandExitCode RunGui(ParsedCommand command)
    {
        if (!command.Options.ContainsKey("launch"))
        {
            var response = new DesktopShellResponse("available", "Use --launch --executable <K-local NpcManager.Desktop.exe> to start the shell.");
            Write(response, command.Json, $"gui: {response.Status} — {response.Message}");
            return CommandExitCode.Success;
        }

        if (_desktopLaunchService is null)
            return WriteUsageError(command.Json, "gui launch is unavailable in this runner configuration.");
        var hasWorkflowBundle = command.Options.TryGetValue("workflow-bundle", out var workflowBundle);
        var hasWorkflowBundleSha256 = command.Options.TryGetValue("workflow-bundle-sha256", out var workflowBundleSha256);
        if (hasWorkflowBundle != hasWorkflowBundleSha256)
            return WriteUsageError(command.Json,
                "workflow bundle path and SHA-256 must be supplied together.");
        if (hasWorkflowBundleSha256 && !IsUppercaseSha256(workflowBundleSha256!))
            return WriteUsageError(command.Json,
                "workflow bundle SHA-256 must be 64 uppercase hexadecimal characters.");
        if (!command.Options.TryGetValue("executable", out var executable))
            return WriteUsageError(command.Json, "gui --launch requires --executable <K-local NpcManager.Desktop.exe>.");

        DesktopWorkflowLaunchBinding? workflowReview = null;
        if (hasWorkflowBundle)
        {
            try
            {
                workflowReview = new DesktopWorkflowLaunchBinding(
                    new WorkspacePath(workflowBundle!), workflowBundleSha256!);
            }
            catch (ArgumentException)
            {
                return WriteUsageError(command.Json,
                    "workflow bundle path must be a fully qualified path.");
            }
        }
        var result = _desktopLaunchService.Launch(new DesktopLaunchRequest(
            new WorkspacePath(executable), workflowReview));
        var launchResponse = new DesktopLaunchResponse("1", result.Executable.Value, result.Launched, result.Diagnostics);
        Write(launchResponse, command.Json, result.Launched ? $"gui: launched {result.Executable.Value}" : "gui: REFUSED");
        if (!result.Launched)
            foreach (var diagnostic in result.Diagnostics) error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
        return result.Launched ? CommandExitCode.Success :
            DiagnosticExitCodeClassifier.ClassifyFailure(result.Diagnostics);
    }

    private static bool IsUppercaseSha256(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private CommandExitCode WriteUsageError(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        if (json)
        {
            error.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
        else
        {
            error.WriteLine($"ERROR {response.Code}: {response.Message}");
        }

        return CommandExitCode.UsageError;
    }

    private void Write<T>(T response, bool json, string humanMessage)
        => Write(response, json, humanMessage, output);

    private static void Write<T>(T response, bool json, string humanMessage, TextWriter output)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        }
        else
        {
            output.WriteLine(humanMessage);
        }
    }

    private static void WriteHelp(TextWriter output)
    {
        output.WriteLine("actorwright — typed Actorwright CLI");
        foreach (var descriptor in CommandCatalog.All)
            output.WriteLine($"  {descriptor.Name}");
        output.WriteLine("Workspace preflight accepts --game|--edition, --data-root, and --output-root for copied game roots; add --load-order and optional --selected Plugin.esp,Patch.esp to emit the reviewed Skyrim intake used by the GUI. The strict schema-1 load-order document is {\"schemaVersion\":1,\"edition\":\"skyrimse\",\"plugins\":[{\"name\":\"Skyrim.esm\",\"order\":0,\"enabled\":true}]}. It also accepts --plugin and --asset-index for archive consistency. workspace scan-generated accepts --game|--edition and --data-root for generated plugins and FaceGen sidecars. body sidecar inspect accepts --game|--edition and --file for a read-only .bssliders validation. body sliders resolve accepts --game|--edition, --tri, and --preset for a read-only PIRT BodySlide catalog resolution. body sliders inspect-preset accepts --game skyrimse and --preset-xml <K-local.xml> for read-only BodySlide SliderPreset XML inspection; it reports native percent values and never invokes BodySlide or generates meshes. body weight resolve accepts --game|--edition skyrimse and a K-local --input manifest containing explicit base/twin vertex arrays, gender flags, and weight percentage. body overlay patch accepts --game|--edition, --npc|--form-id, and a strict FO4 template-layer or Skyrim Body/Hands/Feet [OvlN] layer array via --layers JSON or @K-local-file; it returns a deterministic proposal only and never bakes textures or claims runtime pixels. body overlay bake accepts --game skyrimse, a typed raster/layer manifest via --layers JSON or @K-local-file, and a new K-local .dds --output; it folds optional facetint/detail, skee masks, and Face [OvlN] layers with deterministic sRGB/linear boundaries. body transforms apply accepts --game skyrimse, --npc, --preset <K-local .jslot>, --output <new .jslot>, and optional --transforms/--skin-overrides JSON replacements; it preserves unrelated JSON and refuses unsupported RaceMenu keys. body reset accepts --game, --npc, --current, --baseline, --output, and --section weight|morphs|sliders|skin|overlays|transforms|skin-overrides over explicit K-local .body.json snapshots; it replaces only the requested section and never mutates a plugin or runtime state. body patch/npc patch accepts --skin Plugin.esp|0xXXXXXXXX or --clear-skin for Fallout 4 NPC.WNAM; Fallout 4 --regions {head,upperTorso,arms,lowerTorso,legs} or --regions @K-local-file.json writes NPC.MRSV; --preset-skin is intentionally rejected without a trusted LooksMenu template/provider catalog. npc patch also accepts identity, weight, archetype, ACBS statistics/flags, Skyrim DNAM skill/height, ordered KWDA/APPR lists, SNAM faction memberships, CNTO inventory entries, DOFT/SOFT outfits, PRKR perks, SPLO actor effects, and Fallout 4 PRPS properties; use --level|--level-mult, --set-flag|--clear-flag, --keywords|--add-keyword|--remove-keyword, FO4 --appr options, --factions|--add-faction|--update-faction|--remove-faction, --inventory|--add-inventory|--update-inventory|--remove-inventory, --default-outfit|--sleep-outfit, --perks|--add-perk|--update-perk|--remove-perk, --actor-effects|--add-actor-effect|--remove-actor-effect, and --properties|--add-property|--update-property|--remove-property. Inventory commands require --edition fallout4|skyrimse and --data-root or --plugin; --filter unique,generic,template,unused and --changed-only are supported. Forms search requires --type.");
        output.WriteLine("profile scan accepts --edition|--game, --data-root, and optional --load-order; it hashes each copied plugin and produces a deterministic profile fingerprint without inferring a live profile or writing output.");
        output.WriteLine("records list accepts --edition|--game plus --data-root or --plugin, optional --plugins, --signature, and --search; it enumerates typed records from copied plugins and refuses outside-K or reparse inputs.");
        output.WriteLine("preset catalog requires --directory and accepts --filter. Add --data-root, comma-separated ascending --plugins, --race Plugin|0xFormID, and --sex female|male together for hash-bound static RACE/HDPT compatibility; --compatible-only refuses incomplete runtime-proxy authority instead of guessing.");
        output.WriteLine("npc create requires a hash-bound --provider-manifest/--provider-manifest-sha256 that binds the template, FaceGeom, FaceTint, and dependency components; it also accepts their explicit bound paths/hashes, a fresh --output-root, --plugin, --editor-id, --name, --role static-validation, --sex female, and race/voice/class/combat-style/default-outfit Plugin|FormID references. It creates one ordinary Skyrim ESP at local 0x000800, real NIF/DDS assets, a verified dependency contract, and an exact package; the verdict remains STATIC_PASS_RUNTIME_REQUIRED.");
        output.WriteLine("npc create-from-preset requires --request @<K-local-json> and --request-sha256 <SHA256>. The strict schema binds the preset bundle, provider authority, standalone assets, output, identity, traits, references, and stats before the shared transactional builder can create a new Skyrim NPC; unknown or duplicate JSON fields are refused and runtime authority remains false without in-game proof.");
        output.WriteLine("npc create-from-jslot requires the reviewed --request and --request-sha256 plus --preset, --preset-sha256, --data-root, comma-separated ascending --plugins, and an absent --companion-root. Use --preflight-output for read-only review, or the exact --reviewed-preflight/--reviewed-preflight-sha256 pair for stale-checked execution. Recognized unavailable legacy Gate 1 provider bindings use standalone --provider-migration-output review, then either --reviewed-provider-migration/--reviewed-provider-migration-sha256 or --provider-migration/--provider-migration-sha256 acceptance; these modes refuse all build options. Manager alone writes the temporary ESP, native NIF/DDS companions, and final package; static completion never grants runtime authority.");
        output.WriteLine("preset design-propose requires --intake @<schema-1 authoring-intake.json>, --intake-sha256, and an absent --output root. It writes the canonical intake and inference/description/landmark proposal only; reviewed headparts, tints, resource authority, JSlot output, and likeness authority are never fabricated.");
        output.WriteLine("preset create-from-reference requires --proposal/--proposal-sha256, --review/--review-sha256, --resource/--resource-sha256, --jslot-output, and an absent --evidence-root. Without --apply it writes only the deterministic authoring proposal. With --apply it additionally requires --accepted-proposal-sha256 equal to that proposal and writes the verified JSlot at the exact requested path.");
        output.WriteLine("npc create-from-reference requires the same proposal/review/resource hashes, --accepted-proposal-sha256, a reviewed --request/--request-sha256, --data-root, ordered comma-separated --plugins, an absent --transaction-root, and --apply. It passes the verified JSlot only to the existing npc create-from-jslot transaction; all static output remains runtime-required.");
        output.WriteLine("npc patch binds source bytes with --input-sha256; the consumer aliases --input-sha and --expected-sha256 are accepted only when all supplied values agree. npc edit-package requires --edition skyrimse, --input-plugin, --input-sha256, --npc, a fresh --output-root, --plugin, and at least one supported change. Identity uses --editor-id, --name, and --short-name; archetype references use --race, --voice, --class, and --combat-style as Plugin.esp|0xFormID (voice/combat-style also accept none) and must belong to the source master closure. Complete safe Skyrim statistics use --level|--level-mult, ACBS offsets/range/speed/disposition/bleedout, --player-health, --player-magicka, --player-stamina, --skill-values, --skill-offsets, --far-model-distance, --geared-weapons, and supported --set-flag|--clear-flag values. Typed keyword, faction, inventory, outfit, perk, and actor-effect operations use the same options as npc patch. It writes one exact source-owned verified override package; appearance-owned sex/height fields remain excluded so no stale FaceGen can be produced.");
        output.WriteLine("npc follower-finish analyze requires --request, --request-sha256, and a new --proposal. apply additionally requires --proposal-sha256; verify additionally requires --manifest and performs no write. This schema-1 route supports one closed vanilla-framework follower, one simple exterior Sandbox, one placed actor/anchor, optional RGB correction, and ESL flagging. Custom quests, dialogue, combat kits, outfits, navmesh, and runtime proof remain outside the command; successful static output is STATIC_PASS_RUNTIME_REQUIRED.");
        output.WriteLine("npc finish analyze requires --request, --request-sha256, and a new --proposal. Run schema export --command \"npc finish analyze\" --output <new-json-file> --json to obtain the exact request/proposal members and enum values. apply adds --proposal-sha256; analyze/apply accept --validate-all to accumulate safely reachable phase diagnostics through disposable writer/archive readback without creating the requested proposal, package, or archive; verify accepts --manifest and --manifest-sha256. Finish Core is world-clean: it writes no CELL, WRLD, ACHR, or REFR records, emits no placement surface, and remains STATIC_PASS_RUNTIME_REQUIRED with runtimeAuthority=false and visualAuthority=false.");
        output.WriteLine("npc placement interior analyze requires --request, --request-sha256, and a new --proposal. apply adds --proposal-sha256; verify accepts --manifest and --manifest-sha256. The optional patch binds one exact Finish Core manifest and a complete copied provider snapshot, then writes only one EDID-only interior CELL override and one persistent ACHR in a separate ESL; it reports conflict-contained, never conflict-free, and never grants pathing or runtime authority.");
        output.WriteLine("npc follower-finish pair-analyze uses the current hash-bound schema 3, including bounded reviewed companion FaceGeom HairTint and private outfit/inventory finish; legacy schema 2 remains accepted for compatibility. pair-apply additionally requires --proposal-sha256; pair-verify additionally requires --manifest. It preserves the accepted companion package outside those reviewed fields, adds only the reviewed subject outfit/package/placement/pair records, produces one deterministic two-plugin ZIP, and never claims runtime authority.");
        output.WriteLine("facegen hair-regions analyze writes canonical --analysis and Preserve-default --assignment-template documents and reports deterministic <template-stem>.output.nif and <template-stem>.output.manifest.json paths. propose loads exact hash-bound analysis/request bytes and writes one canonical --proposal. preview loads exact request/proposal bytes plus one reviewed --intake, composes the exact candidate and load-order-bound materialized texture providers, invokes the packaged pinned Blender/PyNifly renderer, and atomically publishes one complete verified private bundle. apply requires --output/--manifest to exactly match the documents and writes without overwrite; verify repeats the exact authority chain read-only. Every JSON envelope keeps visualAuthority and runtimeAuthority false.");
        output.WriteLine("plugin verify requires --edition|--game, --before, --after, and --proposal. For an npc edit-package result, pass evidence/npc-edit-proposal.json (artifactKind existing-npc-edit-proposal), not the internal npc-override-apply-proposal.json consumed by the writer.");
        output.WriteLine("assets index accepts --output for a new schema-versioned provider artifact; it never overwrites existing output.");
        output.WriteLine("preview render accepts --visible face,body,hair,outfit,accessory and --morphs bone,vertex,weight,sculpt (or all), plus paired --outfit Plugin.esp|0xID and --variant <id> for manifest-bound semantic variants, optional --camera <id> and --lighting <id> selectors for versioned manifest presets, and --animation <id> with exactly one --frame or --time plus optional --fps/--play. Add --render-headwear true|false with --hair-slots 30,31,32 (FO4; slot 32 culls the FaceGen head) or 31,41 (Skyrim) for the pinned partition-zap rule. Add paired --asset-root <K-local-data-root> and --image-output <new-png> (optional --width/--height 64..2048) for hash-bound Blender/PyNifly import-only rendering; it does not export NIF or claim runtime appearance.");
        output.WriteLine("preview npc requires --intake <reviewed-intake.json>, --plugin <name>, --form <hexadecimal FormID>, and a new --output-root. Add both --package-manifest and --expected-package-sha256 to overlay one exact Manager package as the highest preview-only provider. It writes six 900x900 textured views, role masks, a contact sheet, provider evidence, diagnostics, and hashes; UBE is refused and Skyrim runtime remains authoritative.");
        output.WriteLine("preview reroll accepts --manifest <scene-json>, --npc <form>, --seed <signed-int64>, and --output <new-json>; it selects from validated variants using a documented SplitMix64 mapping and does not render pixels or alter scene inputs.");
        output.WriteLine("preview export-nif accepts --scene <preview-json>, --output <new-*.nif.plan.json>, and --edition|--game; add --asset-root <K-local-root> for an optional hash-bound Blender/PyNifly sandbox NIF export. Neither mode claims runtime or deployment correctness.");
        output.WriteLine("animation list accepts --edition|--game, --manifest <json>, optional --female true|false, --first-person true|false, and --filter <terms>; it applies the pinned picker role/category/gender/perspective filters to manifest metadata and reports deterministic rows, but never executes HKX behavior graphs or claims runtime reachability.");
        output.WriteLine("animation tree accepts the same options and projects the filtered rows into the pinned role/folder/clip and Gestures & Dialogue (IDLE)/category/clip hierarchy; it never executes HKX behavior graphs or claims runtime reachability.");
        output.WriteLine("outfit list accepts --edition|--game, --data-root or --plugin, optional --plugins plugin1,plugin2, and --search; it lists OTFT items with explicit source and override-chain provenance without loading a live profile.");
        output.WriteLine("outfit propose accepts --edition|--game, --plugin, --source, --mode new|override, --items JSON array or comma-separated list of references, and a new *.outfit-proposal.json --output; it writes a hash-bound K-local plan and never mutates a plugin.");
        output.WriteLine("outfit write accepts --edition|--game, --proposal, and a new *.esp --output; it revalidates the hash-bound OTFT plan, writes one ordinary-plugin binary record, reads it back independently, and never overwrites output.");
        output.WriteLine("leveled-list propose accepts --edition|--game, --plugin, --list, --entries JSON, optional chance/flags, and a new *.leveled-list-proposal.json --output; it writes a typed LVLI plan and never mutates a plugin.");
        output.WriteLine("leveled-list write accepts --edition|--game, --proposal, and a new *.esp --output; it revalidates the hash-bound LVLI plan, writes one ordinary-plugin binary record, reads it back independently, and never overwrites output. Skyrim SE maxCount and per-entry chance-none must be zero because those fields are not represented by its record model.");
        output.WriteLine("armor write accepts --edition|--game, --proposal, and a new *.esp --output; it revalidates the hash-bound ARMO plan, writes one ordinary-plugin binary record, reads it back independently, and never overwrites output.");
        output.WriteLine("armor-addon write accepts --edition|--game, --proposal, and a new *.esp --output; it revalidates the hash-bound ARMA plan, writes one ordinary-plugin binary record, reads it back independently, and never overwrites output.");
        output.WriteLine("facegen build-tint accepts --edition|--game, --manifest, --output <semantic-json>, optional --format bgra8|bc3|bc7|uncompressed, --provider-root <K-local-texture-root>, and --dds-output <new-dds>; without --provider-root it uses explicit uniform layer colors, while provider-root sampling decodes relative DDS sources through the hash-pinned DirectXTex boundary before BGRA8/BC3/BC7 output. Runtime appearance remains separate.");
        output.WriteLine("leveled-list resolve accepts --edition|--game, --list <*.leveled-list-proposal.json>, --seed <signed-int64>, and a new *.leveled-list-resolution.json --output; it applies deterministic preview-compatible ChanceNone/UseAll/Count semantics and never mutates a plugin.");
        output.WriteLine("armor propose accepts --edition|--game, --plugin, --source, --patch JSON (or @K-local file), and a new *.armor-proposal.json --output; it writes a typed ARMO plan and never mutates a plugin.");
        output.WriteLine("armor damage-resist accepts --edition fallout4, --plugin, --source, --damage-resist JSON (or @K-local file), and a new *.armor-damage-resist-proposal.json --output; it writes a typed ARMO plan and never mutates a plugin.");
        output.WriteLine("armor-addon propose accepts --edition|--game, --plugin, --source, --patch JSON (or @K-local file), and a new *.armor-addon-proposal.json --output; it writes a typed ARMA plan and never mutates a plugin. Use --models JSON with the same command for ordered ARMO addon-entry proposals ending in *.armor-addon-model-proposal.json.");
        output.WriteLine("material-swap propose accepts --edition fallout4, --plugin, --source, --patch JSON (or @K-local file), and a new *.material-swap-proposal.json --output; it writes a typed MSWP plan and never mutates a plugin.");
        output.WriteLine("material-swap write accepts --edition fallout4, --proposal, and a new *.esp --output; it revalidates the hash-bound MSWP plan, writes one ordinary-plugin binary record, reads it back independently, and never overwrites output.");
        output.WriteLine("object-template propose accepts --edition fallout4, --plugin, --source, --combinations JSON (object with mode/editorId/items), --includes JSON array, and a new *.object-template-proposal.json --output; it writes a typed OBTS plan and never mutates a plugin.");
        output.WriteLine("object-template write accepts --edition fallout4, --proposal, optional --properties, and a new *.esp --output; it materializes the bounded OBTE/OBTF/FULL/OBTS/STOP combination block into one ARMO record and independently checks the written bytes, including bound 24-byte OMOD property rows when supplied. Provider resolution, deployment, and runtime remain outside this writer.");
        output.WriteLine("object-template propose also accepts --properties JSON array and a new *.object-template-properties-proposal.json --output for typed OMOD property rows.");
        output.WriteLine("facegen plan-pack accepts --edition|--game, --data-root, --npc, --plugins, --anchor-plugin, and optional --debug-sandbox/--shared-neutral-detail; it only plans canonical providers. facegen pack accepts the same options plus a new --output-root directory, copies the anchor plugin and present canonical FaceGen providers into a hash-bound Data package, and never overwrites its source or destination. package inspect reads a K-local npcmanager-package.json manifest, package verify re-hashes every declared file and rejects undeclared files; --strict-install-dependencies true additionally requires --data-root <absolute K-local path> and --plugins <complete comma-separated enabled order>, derives the exact persisted target race from the verified output plugin, and performs a second authority check. package build copies an already verified package to a new K-local output root, and package archive creates a new no-wrapper install ZIP from only its verified Data payload and independently reopens every entry. These package routes never overwrite or modify their source and remain staging evidence, not live deployment or runtime proof.");
        output.WriteLine("runtime-script propose accepts --edition|--game, --npc, --appearance, optional --plugin, and a new *.runtime-script-proposal.json --output; runtime-script write accepts --edition|--game, --source, --proposal, and a new ordinary *.esp --output for source-bound VMAD materialization; runtime-script build accepts --edition|--game, --source-root, and a new *.runtime-script-build.json --output for pinned PSC/PEX inspection; runtime-script package accepts --edition|--game, --source-root, and a new --output-root directory, copying only the game-specific apply PEX under Data/Scripts with a hash-bound manifest; runtime-script deploy accepts --edition|--game, --package, and a copied K-local --data-root, installing only the bound apply PEX and refusing conflicting files; runtime-script inspect-vmad accepts --edition|--game, --plugin, --npc, and optional --script for a read-only copied-plugin VMAD script/property inspection; pipeline preset-to-npc accepts optional --runtime-script-package to include that bound apply PEX in the generated package. It never targets the protected live root or claims Papyrus/runtime behavior.");
        output.WriteLine("changes list accepts --edition|--game fallout4|skyrimse and an explicit K-local *.changes-session.json --session; it emits only records with stable field-level differences.");
        output.WriteLine("changes update accepts --edition|--game, --session, --record, --action reset|delete, optional --signature, and a new *.changes-action.json --output; it writes an explicit review proposal and never mutates a plugin.");
        output.WriteLine("records propose accepts --edition|--game, --type, --mode new|template|override, --form-id, --editor-id, optional --source/--name/--masters JSON, and a new *.record-proposal.json --output; it records explicit 24-bit allocation without mutating a plugin.");
        output.WriteLine("Skyrim HDPT proposals accept --model, --tri-race, --tri-chargen, --tri-dialogue, --valid-races, --extra-parts, --flags and --part-type, or --clone-from with --retarget-valid-races. plugin write materializes them; --plugin requires --expected-sha256, and --private-root plus --data-root copies four assets. npc face-patch --headpart-replace old-Plugin|FormID=new-local-FormID changes one PNAM.");
    }

    private static bool TryWriteCommandHelp(ParsedCommand command, TextWriter output)
    {
        AgentCommandContract? registered = AgentCommandRegistry.All.FirstOrDefault(
            item => string.Equals(item.Name, command.Name,
                StringComparison.OrdinalIgnoreCase));
        if (registered is null)
            return false;
        AgentCommandContract contract = AgentCommandRegistry
            .GetLegacyDiscoveryRequired(command.Name);

        if (command.Json)
        {
            output.WriteLine(JsonSerializer.Serialize(contract, JsonOptions));
            return true;
        }

        bool usesContractOptions =
            contract.ContractStatus == AgentContractStatus.Complete ||
            !contract.Options.IsDefaultOrEmpty;
        ImmutableArray<AgentOptionContract> options =
            usesContractOptions
                ? contract.Options
                : LegacyCommandOptionCatalog.For(command.Name)
                    .Select(item => new AgentOptionContract(
                        item.Name,
                        item.Name,
                        AgentValueKind.String,
                        item.Required,
                        item.AcceptedValues,
                        [],
                        false)
                    {
                        ValueSyntax = item.ValueSyntax,
                        Description = item.Description
                    })
                    .ToImmutableArray();
        if (options.IsDefaultOrEmpty &&
            contract.ContractStatus != AgentContractStatus.Complete)
            return false;

        string heading = contract.ContractStatus == AgentContractStatus.Complete &&
                         contract.Readiness == ProtocolReadiness.V2
            ? "command options"
            : "legacy command options";
        output.WriteLine($"{command.Name} — {heading}");
        output.WriteLine($"Usage: actorwright {command.Name} [options]");
        output.WriteLine("Global options: --help, --json.");
        foreach (var option in options)
        {
            var accepted = option.AllowedValues.IsDefaultOrEmpty
                ? string.Empty
                : $" accepted: {string.Join('|', option.AllowedValues)}";
            output.WriteLine(
                $"  --{option.CliName} {option.ValueSyntax} " +
                $"{(option.Required ? "(required)" : "(optional)")}: " +
                $"{option.Description}{accepted}");
        }

        return true;
    }

}
