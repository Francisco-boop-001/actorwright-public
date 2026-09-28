using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Opaque frozen authority for the exact copied-master package admitted by
/// <see cref="BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission"/>.
/// </summary>
public sealed class BethesdaSkyrimFollowerFinishSandboxAuthority
{
    private readonly Package _template;

    internal BethesdaSkyrimFollowerFinishSandboxAuthority(
        PluginName sourcePlugin,
        FormReference templateForm,
        Sha256Hash pluginFileHash,
        Sha256Hash rawRecordDigest,
        EditorId templateEditorId,
        string procedureType,
        Package templateRecord)
    {
        SourcePlugin = sourcePlugin;
        TemplateForm = templateForm;
        PluginFileHash = pluginFileHash;
        RawRecordDigest = rawRecordDigest;
        TemplateEditorId = templateEditorId;
        ProcedureType = procedureType;
        ScheduleMonth = templateRecord.ScheduleMonth;
        ScheduleDayOfWeek = templateRecord.ScheduleDayOfWeek;
        ScheduleDate = templateRecord.ScheduleDate;
        ScheduleHour = templateRecord.ScheduleHour;
        ScheduleMinute = templateRecord.ScheduleMinute;
        ScheduleDurationInMinutes =
            templateRecord.ScheduleDurationInMinutes;
        _template = templateRecord.DeepCopy();
    }

    public PluginName SourcePlugin { get; }

    public FormReference TemplateForm { get; }

    public Sha256Hash PluginFileHash { get; }

    public Sha256Hash RawRecordDigest { get; }

    public EditorId TemplateEditorId { get; }

    public string ProcedureType { get; }

    internal sbyte ScheduleMonth { get; }

    internal Package.DayOfWeek ScheduleDayOfWeek { get; }

    internal byte ScheduleDate { get; }

    internal sbyte ScheduleHour { get; }

    internal sbyte ScheduleMinute { get; }

    internal int ScheduleDurationInMinutes { get; }

    internal Package CreateTemplateCopy() => _template.DeepCopy();
}

/// <summary>
/// Admits one exact package from one policy-approved K-local copied master.
/// Hashing, raw-record binding, typed readback, and defensive freezing all use
/// a single locked source handle and its private snapshot.
/// </summary>
public sealed class BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission(
    IWorkspacePolicy policy,
    WorkspacePath workspaceRoot)
{
    private static readonly FormReference CanonicalTemplateForm =
        new(
            new PluginName("Skyrim.esm"),
            new FormId(0x0001B217));

    private static readonly EditorId CanonicalTemplateEditorId =
        new("DefaultSandboxEditorLocation512");

    internal static readonly FormReference
        CanonicalSandboxProcedureTemplateForm =
            new(
                new PluginName("Skyrim.esm"),
                new FormId(0x0001C254));

    private static readonly Sha256Hash CanonicalTemplateRawDigest =
        new(
            "fba3cca0eff98528da3985962ff9058ee" +
            "7662ea944c250f3ffb90a0490d52685");

    public BethesdaSkyrimFollowerFinishSandboxAuthority Admit(
        WorkspacePath copiedMaster,
        Sha256Hash expectedPluginFileHash)
    {
        ImmutableArray<Diagnostic> pathDiagnostics =
            policy.EvaluateReadRoot(workspaceRoot, copiedMaster);
        if (pathDiagnostics.Any(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error))
            BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                "follower-finish-core-template-path",
                "The copied-master template path failed workspace read policy: " +
                string.Join(
                    " | ",
                    pathDiagnostics.Select(diagnostic =>
                        $"{diagnostic.Code}: {diagnostic.Message}")));
        return AdmitBound(copiedMaster, expectedPluginFileHash);
    }

    internal static BethesdaSkyrimFollowerFinishSandboxAuthority AdmitBound(
        WorkspacePath copiedMaster,
        Sha256Hash expectedPluginFileHash,
        string templateCountDiagnosticCode = "follower-finish-core-template-form")
    {
        if (!File.Exists(copiedMaster.Value) ||
            Directory.Exists(copiedMaster.Value))
            BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                "follower-finish-core-template-path",
                "The copied-master template path is not an ordinary file.");
        if (!string.Equals(
                Path.GetFileName(copiedMaster.Value),
                CanonicalTemplateForm.Plugin.Value,
                StringComparison.OrdinalIgnoreCase))
            BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                "follower-finish-core-template-form",
                "The copied-master filename differs from the expected template plugin identity.");

        string directory = Path.GetDirectoryName(
            copiedMaster.Value) ??
            throw new InvalidDataException(
                "The copied-master template has no parent directory.");
        string snapshotPath = Path.Combine(
            directory,
            $".{Path.GetFileName(copiedMaster.Value)}." +
            $"{Guid.NewGuid():N}.authority-snapshot");
        try
        {
            Sha256Hash actualFileHash;
            using (var source = new FileStream(
                       copiedMaster.Value,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       1024 * 1024,
                       FileOptions.SequentialScan))
            using (var snapshot = new FileStream(
                       snapshotPath,
                       FileMode.CreateNew,
                       FileAccess.ReadWrite,
                       FileShare.Read,
                       1024 * 1024,
                       FileOptions.SequentialScan))
            {
                source.CopyTo(snapshot);
                snapshot.Flush(flushToDisk: true);
                snapshot.Position = 0;
                actualFileHash = new Sha256Hash(
                    Convert.ToHexString(SHA256.HashData(snapshot)));
            }
            if (actualFileHash != expectedPluginFileHash)
                BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                    "follower-finish-core-template-file-hash",
                    "The copied-master bytes differ from the expected plugin file hash.");

            int templateCount = BethesdaSkyrimNpcFinishCoreRaw.CountRecords(
                File.ReadAllBytes(snapshotPath),
                "PACK",
                CanonicalTemplateForm.FormId.Value);
            if (templateCount != 1)
            {
                string observed = templateCount == 0
                    ? "0"
                    : "at least 2";
                BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                    templateCountDiagnosticCode,
                    $"PACK 0x0001B217 requires exactly 1 record; observed {observed} " +
                    $"(expected=1; observed={observed}).");
            }

            IReadOnlyDictionary<
                (uint FormId, string Signature),
                string> digests =
                BethesdaRawRecordDigestReader.Read(snapshotPath);
            string[] rawMatches = digests
                .Where(entry =>
                    entry.Key.Signature == "PACK" &&
                    (entry.Key.FormId & 0x00FF_FFFFu) ==
                    CanonicalTemplateForm.FormId.Value)
                .Select(entry => entry.Value)
                .ToArray();
            if (rawMatches.Length != 1)
                BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                    "follower-finish-core-template-form",
                    "The copied master raw digest inventory omitted its admitted PACK.");
            var actualRawDigest = new Sha256Hash(rawMatches[0]);

            ModKey expectedModKey = ModKey.FromNameAndExtension(
                CanonicalTemplateForm.Plugin.Value);
            using var overlay = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    expectedModKey,
                    new FilePath(snapshotPath)),
                SkyrimRelease.SkyrimSE);
            IPackageGetter? getter = overlay.Packages
                .SingleOrDefault(package =>
                    package.FormKey ==
                    BethesdaSkyrimFollowerFinishCoreWriter.ToFormKey(
                        CanonicalTemplateForm));
            if (getter is null ||
                !string.Equals(
                    getter.EditorID,
                    CanonicalTemplateEditorId.Value,
                    StringComparison.Ordinal))
                BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                    "follower-finish-core-template-editor-id",
                    "The copied-master PACK FormKey/EditorID differs from the request-bound authority.");
            Package template = getter.DeepCopy();
            string procedureType = ValidateShape(template);
            if (actualRawDigest != CanonicalTemplateRawDigest)
                BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                    "follower-finish-core-template-record-digest",
                    "The copied-master PACK raw digest differs from the fixed production capability.");
            return new BethesdaSkyrimFollowerFinishSandboxAuthority(
                CanonicalTemplateForm.Plugin,
                CanonicalTemplateForm,
                actualFileHash,
                actualRawDigest,
                CanonicalTemplateEditorId,
                procedureType,
                template);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        finally
        {
            try
            {
                if (File.Exists(snapshotPath))
                    File.Delete(snapshotPath);
            }
            catch (IOException)
            {
                // A locked antivirus/indexer can delay cleanup; the unique
                // hidden snapshot is never treated as authority evidence.
            }
        }
    }

    private static string ValidateShape(Package template)
    {
        string? semanticProcedure =
            template.PackageTemplate.FormKey ==
                BethesdaSkyrimFollowerFinishCoreWriter.ToFormKey(
                    CanonicalSandboxProcedureTemplateForm) &&
            template.ProcedureTree.Count == 0
                ? "Sandbox"
                : template.ProcedureTree.Count == 1 &&
                  !string.IsNullOrWhiteSpace(
                      template.ProcedureTree[0].ProcedureType)
                    ? template.ProcedureTree[0].ProcedureType
                    : null;
        if (semanticProcedure is not null &&
            !string.Equals(
                semanticProcedure,
                "Sandbox",
                StringComparison.Ordinal))
            BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                "follower-finish-core-template-procedure",
                "The copied-master PACK resolves to procedure " +
                $"'{semanticProcedure}', but the fixed production capability admits only Sandbox.");
        if (template.IsDeleted ||
            template.VirtualMachineAdapter is not null ||
            template.OwnerQuest.FormKeyNullable is not null ||
            !IsCanonicalEmptyVanillaEventBlock(
                template.OnBegin) ||
            !IsCanonicalEmptyVanillaEventBlock(
                template.OnChange) ||
            !IsCanonicalEmptyVanillaEventBlock(
                template.OnEnd) ||
            template.Conditions.Count != 0 ||
            template.Data.Values
                .OfType<PackageDataLocation>()
                .Count() != 1 ||
            template.PackageTemplate.FormKey !=
            BethesdaSkyrimFollowerFinishCoreWriter.ToFormKey(
                CanonicalSandboxProcedureTemplateForm) ||
            template.ProcedureTree.Count != 0)
            BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                "follower-finish-core-template-shape",
                "The copied-master PACK is not the fixed canonical Sandbox template/event shape: " +
                $"deleted={template.IsDeleted}, " +
                $"vmad={template.VirtualMachineAdapter is not null}, " +
                $"owner={template.OwnerQuest.FormKeyNullable is not null}, " +
                $"begin={template.OnBegin is not null}, " +
                $"change={template.OnChange is not null}, " +
                $"end={template.OnEnd is not null}, " +
                $"conditions={template.Conditions.Count}, " +
                $"locations={template.Data.Values.OfType<PackageDataLocation>().Count()}, " +
                $"package-template={template.PackageTemplate.FormKey}, " +
                $"branches={template.ProcedureTree.Count}, " +
                $"procedures={string.Join(",", template.ProcedureTree.Select(branch => branch.ProcedureType))}.");
        if (!IsContinuous(template))
            BethesdaSkyrimFollowerFinishCoreWriter.Refuse(
                "follower-finish-core-template-schedule",
                "The copied-master PACK does not use the admitted typed continuous schedule: " +
                $"month={template.ScheduleMonth}, " +
                $"day={template.ScheduleDayOfWeek}, " +
                $"date={template.ScheduleDate}, " +
                $"hour={template.ScheduleHour}, " +
                $"minute={template.ScheduleMinute}, " +
                $"duration={template.ScheduleDurationInMinutes}.");
        return semanticProcedure!;
    }

    private static bool IsCanonicalEmptyVanillaEventBlock(
        PackageEvent? packageEvent) =>
        packageEvent is not null &&
        packageEvent.Topics.Count == 1 &&
        packageEvent.Topics[0] is TopicReference topic &&
        topic.Reference.FormKey.IsNull &&
        packageEvent.Idle.IsNull &&
        packageEvent.SCHR is null &&
        packageEvent.SCDA is null &&
        packageEvent.SCTX is null &&
        packageEvent.QNAM is null &&
        packageEvent.TNAM is null;

    private static bool IsContinuous(Package package) =>
        package.ScheduleMonth == -1 &&
        package.ScheduleDayOfWeek == Package.DayOfWeek.Any &&
        package.ScheduleDate == 0 &&
        package.ScheduleHour == -1 &&
        package.ScheduleMinute == -1 &&
        package.ScheduleDurationInMinutes == 0;
}

/// <summary>
/// Applies only the model-level core of the closed follower-finish proposal:
/// TES4 ESL mechanics, one CLFM RGB, one NPC package link, and one PACK.
/// World/reference authoring and filesystem promotion remain later stages.
/// </summary>
public sealed class BethesdaSkyrimFollowerFinishCoreWriter
{
    private static readonly Regex ConditionPattern = new(
        "^GetFactionRank\\((?<plugin>[^|()]+)\\|0x(?<id>[0-9A-Fa-f]{8})\\) < 0$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public SkyrimMod Write(
        SkyrimMod source,
        SkyrimFollowerFinishProposal proposal,
        BethesdaSkyrimFollowerFinishSandboxAuthority authority)
    {
        _ = GetType();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(authority);

        SkyrimFollowerFinishRequest request = proposal.Request;
        ValidateProposalEnvelope(proposal, request);
        if (!string.Equals(
                source.ModKey.ToString(),
                request.Source.Plugin.Value,
                StringComparison.OrdinalIgnoreCase))
            Refuse(
                "follower-finish-core-source-plugin",
                "The mutable source plugin identity differs from the admitted request.");

        if (source.IsSmallMaster)
            Refuse(
                "follower-finish-core-source-light",
                "The admitted source must not already carry the ESL flag.");
        if (source.ModHeader.Stats.NextFormID !=
            request.Allocation.Package.Value)
            Refuse(
                "follower-finish-core-next-form-id",
                "The source NextFormID does not equal the first proposal allocation.");

        Npc? npc = source.Npcs.SingleOrDefault(record =>
            record.FormKey.ModKey == source.ModKey &&
            record.FormKey.ID == request.NpcFormId.Value);
        if (npc is null)
            Refuse(
                "follower-finish-core-npc-form-id",
                "The proposal NPC FormID does not resolve to one self-owned NPC.");

        uint[] actualOwned = source.EnumerateMajorRecords()
            .Where(record => record.FormKey.ModKey == source.ModKey)
            .Select(record => record.FormKey.ID)
            .Order()
            .ToArray();
        uint[] admittedOccupied = request.OccupiedLocalFormIds
            .Select(formId => formId.Value)
            .Order()
            .ToArray();
        if (!actualOwned.SequenceEqual(admittedOccupied) ||
            !SnapshotInventoryIds(proposal.SourceSnapshot)
                .Order()
                .SequenceEqual(admittedOccupied))
            Refuse(
                "follower-finish-core-occupied-ids",
                "The self-owned record map differs from the admitted occupied-ID map.");

        ColorRecord? color = source.Colors.SingleOrDefault(record =>
            record.FormKey.ModKey == source.ModKey &&
            record.FormKey.ID == request.Hair.ColorFormId.Value);
        if (color is null ||
            PackRgb(color.Color) != request.Hair.OldPackedRgb.Value ||
            proposal.SourceSnapshot.HairPackedRgb !=
            request.Hair.OldPackedRgb)
            Refuse(
                "follower-finish-core-old-rgb",
                "The source CLFM RGB differs from the admitted old value.");

        FormKey expectedHair = new(
            source.ModKey,
            request.Hair.ColorFormId.Value);
        if (npc!.HairColor.FormKeyNullable != expectedHair ||
            proposal.SourceSnapshot.ActorHairColor !=
            new FormReference(
                request.Source.Plugin,
                request.Hair.ColorFormId))
            Refuse(
                "follower-finish-core-hclf",
                "The source NPC HCLF does not point to the admitted self-owned CLFM.");

        ImmutableArray<NpcFactionEntry> actualFactions = npc.Factions
            .Select(entry => new NpcFactionEntry(
                ToReference(entry.Faction.FormKey),
                checked((sbyte)entry.Rank)))
            .ToImmutableArray();
        if (!actualFactions.SequenceEqual(request.ExpectedFactionRanks) ||
            !proposal.SourceSnapshot.FactionRanks.SequenceEqual(
                request.ExpectedFactionRanks))
            Refuse(
                "follower-finish-core-factions",
                "The source NPC faction ranks differ from the admitted follower ranks.");

        Relationship? relationship =
            source.Relationships.SingleOrDefault(record =>
                record.FormKey.ModKey == source.ModKey &&
                record.FormKey.ID ==
                request.RelationshipFormId.Value);
        if (relationship is null ||
            !string.Equals(
                relationship.Rank.ToString(),
                request.ExpectedRelationshipRank,
                StringComparison.Ordinal) ||
            checked((byte)(short)relationship.Rank) !=
            request.ExpectedRelationshipRankRawDiscriminator ||
            proposal.SourceSnapshot.RelationshipRank !=
            request.ExpectedRelationshipRank ||
            proposal.SourceSnapshot
                .RelationshipRankRawDiscriminator !=
            request.ExpectedRelationshipRankRawDiscriminator)
            Refuse(
                "follower-finish-core-relationship-rank",
                "The source RELA typed/raw rank differs from the admitted value.");

        bool defaultOutfitNull =
            npc.DefaultOutfit.FormKeyNullable is null;
        if (defaultOutfitNull != request.ExpectedDefaultOutfitNull ||
            proposal.SourceSnapshot.DefaultOutfitNull !=
            request.ExpectedDefaultOutfitNull)
            Refuse(
                "follower-finish-core-outfit",
                "The source NPC default outfit null state differs from the proposal.");

        ValidateSandbox(request.Sandbox, authority);
        if (npc.Packages.Count != 0 || source.Packages.Count != 0)
            Refuse(
                "follower-finish-core-source-package",
                "The admitted source must contain no package records or NPC package links.");
        SkyrimFollowerFinishAllocation allocation = request.Allocation;
        if (allocation.Package.Value is < 0x800 or > 0xFFC ||
            allocation.Anchor.Value != allocation.Package.Value + 1 ||
            allocation.Actor.Value != allocation.Package.Value + 2 ||
            allocation.NextFormId.Value != allocation.Package.Value + 3 ||
            actualOwned.Distinct().Count() != actualOwned.Length ||
            actualOwned.Any(id => id < 0x800 || id >= allocation.Package.Value))
            Refuse(
                "follower-finish-core-allocation",
                "Occupied local IDs must precede the four contiguous allocation IDs inside 0x800..0xFFF.");

        (FormKey conditionFaction, ConditionFloat condition) =
            BuildCondition(request.Sandbox.Condition);
        if (conditionFaction.ModKey == source.ModKey)
            Refuse(
                "follower-finish-core-condition",
                "The follower-rank condition must target a qualified external faction.");

        var mutable = (SkyrimMod)source.DeepCopy();
        mutable.IsSmallMaster = true;
        mutable.ModHeader.Stats.NextFormID =
            proposal.NextFormId.Value;

        ColorRecord mutableColor = mutable.Colors.Single(record =>
            record.FormKey.ModKey == mutable.ModKey &&
            record.FormKey.ID == request.Hair.ColorFormId.Value);
        uint packedRgb = request.Hair.NewPackedRgb.Value;
        mutableColor.Color = Color.FromArgb(
            255,
            (int)((packedRgb >> 16) & 0xFFu),
            (int)((packedRgb >> 8) & 0xFFu),
            (int)(packedRgb & 0xFFu));

        FormKey packageKey = new(
            mutable.ModKey,
            request.Allocation.Package.Value);
        Package package = mutable.Packages.DuplicateInAsNewRecord(
            authority.CreateTemplateCopy(),
            packageKey);
        package.EditorID = BuildPackageEditorId(
            request.NpcEditorId.Value);
        PackageDataLocation location = package.Data.Values
            .OfType<PackageDataLocation>()
            .Single();
        location.Location = new LocationTargetRadius
        {
            Target = new LocationTarget
            {
                Link = new FormLink<IPlacedGetter>(
                    ToFormKey(request.Sandbox.Target))
            },
            Radius = checked((uint)request.Sandbox.Radius)
        };
        package.Conditions.Clear();
        package.Conditions.Add(condition);

        Npc mutableNpc = mutable.Npcs.Single(record =>
            record.FormKey.ModKey == mutable.ModKey &&
            record.FormKey.ID == request.NpcFormId.Value);
        mutableNpc.Packages.Clear();
        mutableNpc.Packages.Add(
            new FormLink<IPackageGetter>(packageKey));
        return mutable;
    }

    private static void ValidateProposalEnvelope(
        SkyrimFollowerFinishProposal proposal,
        SkyrimFollowerFinishRequest request)
    {
        if (!proposal.SourceSnapshot.Valid ||
            proposal.SourceSnapshot.Plugin !=
            request.Source.Plugin ||
            proposal.SourceSnapshot.PluginSha256 !=
            request.Source.PluginSha256)
            Refuse(
                "follower-finish-core-proposal-source",
                "The proposal is not bound to the admitted source snapshot.");
        if (!request.SetEslFlag || request.CompactFormIds)
            Refuse(
                "follower-finish-core-esl-contract",
                "The core requires ESL flagging without FormID compaction.");
        if (proposal.NextFormId != request.Allocation.NextFormId ||
            proposal.SourceSnapshot.NextFormId !=
            request.Allocation.Package)
            Refuse(
                "follower-finish-core-next-form-id",
                "Proposal and snapshot NextFormID mechanics disagree with the allocation.");
        if (!proposal.ExistingRecordChanges.SequenceEqual(
                request.AllowedExistingRecordChanges) ||
            !proposal.NewRecords.SequenceEqual(
                request.AllowedNewRecords))
            Refuse(
                "follower-finish-core-change-surface",
                "The proposal widened its admitted existing/new record surfaces.");
    }

    private static void ValidateSandbox(
        SkyrimFollowerFinishSandbox sandbox,
        BethesdaSkyrimFollowerFinishSandboxAuthority authority)
    {
        if (!string.Equals(
                sandbox.Procedure,
                authority.ProcedureType,
                StringComparison.Ordinal))
            Refuse(
                "follower-finish-core-procedure",
                "The proposal procedure differs from the actual admitted template procedure.");
        if (!string.Equals(
                sandbox.Schedule,
                "continuous",
                StringComparison.OrdinalIgnoreCase))
            Refuse(
                "follower-finish-core-schedule",
                "The proposal does not request the typed continuous schedule admitted from the template.");
    }

    private static (FormKey Faction, ConditionFloat Condition)
        BuildCondition(string text)
    {
        Match match = ConditionPattern.Match(text);
        uint localFormId = 0;
        if (!match.Success ||
            !uint.TryParse(
                match.Groups["id"].Value,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out localFormId))
            Refuse(
                "follower-finish-core-condition",
                "The admitted package condition is not one exact GetFactionRank qualified-form comparison.");
        ModKey modKey;
        try
        {
            modKey = ModKey.FromNameAndExtension(
                match.Groups["plugin"].Value);
        }
        catch (ArgumentException)
        {
            Refuse(
                "follower-finish-core-condition",
                "The admitted condition faction plugin name is invalid.");
            throw;
        }

        var faction = new FormKey(modKey, localFormId);
        var data = new GetFactionRankConditionData
        {
            RunOnType = Condition.RunOnType.Subject
        };
        data.Faction.Link.SetTo(faction);
        return (
            faction,
            new ConditionFloat
            {
                CompareOperator = CompareOperator.LessThan,
                ComparisonValue = 0f,
                Data = data
            });
    }

    private static IEnumerable<uint> SnapshotInventoryIds(
        SkyrimFollowerFinishPluginSnapshot snapshot)
    {
        foreach (string entry in snapshot.RecordInventory)
        {
            int marker = entry.LastIndexOf("0x", StringComparison.Ordinal);
            uint value = 0;
            if (marker < 0 ||
                !uint.TryParse(
                    entry.AsSpan(marker + 2),
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out value))
                Refuse(
                    "follower-finish-core-occupied-ids",
                    "The source snapshot inventory contains a malformed FormID.");
            yield return value;
        }
    }

    private static uint PackRgb(Color color) =>
        ((uint)color.R << 16) |
        ((uint)color.G << 8) |
        color.B;

    private static string BuildPackageEditorId(string npcEditorId)
    {
        const string suffix = "_FollowerSandbox";
        int prefixLength = Math.Min(
            npcEditorId.Length,
            64 - suffix.Length);
        return npcEditorId[..prefixLength] + suffix;
    }

    internal static FormKey ToFormKey(FormReference reference) =>
        new(
            ModKey.FromNameAndExtension(reference.Plugin.Value),
            reference.FormId.Value);

    private static FormReference ToReference(FormKey key) =>
        new(
            new PluginName(key.ModKey.ToString()),
            new FormId(key.ID));

    [DoesNotReturn]
    internal static void Refuse(string code, string message) =>
        throw new InvalidDataException($"{code}: {message}");

}
