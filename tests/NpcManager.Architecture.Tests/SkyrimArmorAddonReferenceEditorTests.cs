using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimArmorAddonReferenceEditor()
    {
        PluginName source = new("Source.esp");
        FormReference owningRace = new(source, new FormId(0x100));
        FormReference alternateRace = new(source, new FormId(0x101));
        FormReference otherRace = new(source, new FormId(0x102));
        FormReference primaryAddon = new(source, new FormId(0x200));
        FormReference additionalAddon = new(source, new FormId(0x201));
        SkyrimArmorAddonRaceEvidence primaryEvidence =
            Evidence(owningRace, owningRace);
        SkyrimArmorAddonRaceEvidence additionalEvidence =
            Evidence(owningRace, alternateRace, owningRace);

        SkyrimArmorAddonReferenceTransition primary =
            SkyrimArmorAddonReferenceEditorRules.Choose(
                GameEdition.SkyrimSpecialEdition,
                owningRace,
                Candidate(primaryAddon, primaryEvidence));
        SkyrimArmorAddonReferenceTransition additional =
            SkyrimArmorAddonReferenceEditorRules.Choose(
                GameEdition.SkyrimSpecialEdition,
                owningRace,
                Candidate(additionalAddon, additionalEvidence));
        Assert(primary.Accepted && primary.Row?.Reference == primaryAddon &&
               additional.Accepted && additional.Row?.Reference == additionalAddon,
            "Primary and additional-race compatible ARMA candidates were not preserved exactly.");

        Assert(!SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition, owningRace, null).Accepted &&
               !SkyrimArmorAddonReferenceEditorRules.Accept(
                    owningRace, null).Accepted,
            "An empty candidate or row was accepted.");
        Assert(!SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    owningRace,
                    Candidate(new FormReference(source, default),
                        primaryEvidence)).Accepted &&
               !SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    owningRace,
                    Candidate(primaryAddon, primaryEvidence,
                        signature: new RecordSignature("ARMO"))).Accepted,
            "A zero reference or non-ARMA signature was accepted.");
        Assert(!SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    owningRace,
                    Candidate(primaryAddon, primaryEvidence,
                        deleted: true)).Accepted &&
               !SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    owningRace,
                    Candidate(primaryAddon, primaryEvidence,
                        stale: true)).Accepted,
            "A deleted or stale ARMA was accepted.");
        Assert(!SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    owningRace,
                    Candidate(primaryAddon,
                        new SkyrimArmorAddonRaceEvidence(
                            false, owningRace, owningRace, default))).Accepted &&
               !SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    owningRace,
                    Candidate(primaryAddon,
                        Evidence(owningRace, otherRace))).Accepted,
            "Incomplete or incompatible ARMA race evidence was accepted.");
        Assert(!SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    owningRace,
                    Candidate(primaryAddon,
                        Evidence(otherRace, otherRace))).Accepted,
            "Compatibility evidence from a different owning-race context was accepted.");

        SkyrimArmorAddonReferenceRow current = primary.Row ??
            throw new InvalidOperationException("The primary ARMA row is missing.");
        SkyrimArmorAddonReferenceTransition childCancelled =
            SkyrimArmorAddonReferenceEditorRules.ApplyDeepEdit(
                owningRace, current,
                new SkyrimArmorAddonDeepEditResult(false, null));
        Assert(!childCancelled.Accepted && childCancelled.Row is null &&
               current.Reference == primaryAddon,
            "A child Cancel accepted or mutated the caller-owned ARMA row.");
        Assert(!SkyrimArmorAddonReferenceEditorRules.ApplyDeepEdit(
                    owningRace, current, null).Accepted,
            "A missing deep-editor result did not fail closed.");

        ArmorAddonProposalRequest proposal = OverrideProposal(additionalAddon);
        SkyrimArmorAddonReferenceTransition authoredChoice =
            SkyrimArmorAddonReferenceEditorRules.Choose(
                GameEdition.SkyrimSpecialEdition,
                owningRace,
                Candidate(additionalAddon, additionalEvidence) with
                {
                    AuthoredProposal = proposal
                });
        Assert(authoredChoice.Accepted &&
               authoredChoice.Row?.AuthoredProposal == proposal,
            "Choose discarded the nested proposal for an authored ARMA draft.");
        Assert(!SkyrimArmorAddonReferenceEditorRules.Choose(
                    GameEdition.SkyrimSpecialEdition,
                    owningRace,
                    Candidate(additionalAddon, additionalEvidence) with
                    {
                        AuthoredProposal = OverrideProposal(primaryAddon)
                    }).Accepted,
            "Choose accepted an authored ARMA draft with mismatched proposal identity.");
        SkyrimArmorAddonReferenceRow authored = additional.Row! with
        {
            AuthoredProposal = proposal
        };
        SkyrimArmorAddonReferenceTransition childAccepted =
            SkyrimArmorAddonReferenceEditorRules.ApplyDeepEdit(
                owningRace, current,
                new SkyrimArmorAddonDeepEditResult(true, authored));
        Assert(childAccepted.Accepted && childAccepted.AutoAccept &&
               childAccepted.Row?.Reference == additionalAddon &&
               childAccepted.Row.AuthoredProposal == proposal,
            "A valid committed ARMA child did not auto-accept exactly once with its nested proposal.");
        Assert(!SkyrimArmorAddonReferenceEditorRules.ApplyDeepEdit(
                    owningRace, current,
                    new SkyrimArmorAddonDeepEditResult(true,
                        authored with
                        {
                            AuthoredProposal = OverrideProposal(primaryAddon)
                        })).Accepted,
            "A nested ARMA proposal with mismatched identity was accepted.");

        SkyrimArmorEditorDocument armor = ArmorDocument(
            owningRace, primaryAddon);
        SkyrimArmorEditorResult added =
            SkyrimArmorEditorCollectionRules.AddArmorAddonReferenceRow(
                armor, authored);
        Assert(added.Accepted && added.Document is not null &&
               added.Document.ArmorAddons.SequenceEqual(
                   [primaryAddon, additionalAddon]) &&
               added.Document.AuthoredArmorAddons.Single() == authored,
            "Gate 018 could not append an accepted Gate 019 row with its nested Gate 020 proposal.");
        SkyrimArmorEditorResult applied =
            SkyrimArmorEditorCollectionRules.ApplyArmorAddonReferenceRow(
                armor, 0, authored);
        Assert(applied.Accepted && applied.Document is not null &&
               applied.Document.ArmorAddons.SequenceEqual([additionalAddon]) &&
               applied.Document.AuthoredArmorAddons.Single() == authored,
            "Gate 018 lost the accepted Gate 019 row or its nested Gate 020 proposal.");
        SkyrimArmorEditorDocument nested = applied.Document ??
            throw new InvalidOperationException("The nested Armor document is missing.");
        SkyrimArmorProposalAdapterResult adapted =
            SkyrimArmorEditorRules.ToProposal(
                nested,
                new WorkspacePath(
                    "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\gate019.armor-proposal.json"));
        Assert(adapted.Accepted &&
               adapted.AuthoredArmorAddons.SequenceEqual([proposal]),
            "The Armor proposal boundary lost the nested authored ARMA proposal.");
        Assert(!SkyrimArmorEditorRules.Cancel().Accepted &&
               !SkyrimOutfitEditorRules.Cancel().Accepted,
            "Armor or Outfit Cancel accepted a nested ARMA transaction.");

        return Task.CompletedTask;
    }

    private static SkyrimArmorAddonReferenceCandidate Candidate(
        FormReference reference,
        SkyrimArmorAddonRaceEvidence evidence,
        bool deleted = false,
        bool stale = false,
        RecordSignature? signature = null) =>
        new(reference, signature ?? new RecordSignature("ARMA"),
            "Reviewed addon", deleted, stale, evidence);

    private static SkyrimArmorAddonRaceEvidence Evidence(
        FormReference owningRace,
        FormReference primaryRace,
        params FormReference[] additionalRaces) =>
        new(true, owningRace, primaryRace,
            additionalRaces.ToImmutableArray());

    private static SkyrimArmorEditorDocument ArmorDocument(
        FormReference race,
        FormReference addon) =>
        new(
            GameEdition.SkyrimSpecialEdition,
            SkyrimArmorEditorIntent.BlankNew,
            ArmorProposalMode.New,
            new WorkspacePath(
                "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\Source.esp"),
            new FormId(0x300),
            new EditorId("npcm_ARMO_Gate019"),
            new FormId(0x301),
            "Gate 019 armor",
            race,
            null,
            false,
            string.Empty,
            1,
            1,
            1,
            4,
            null,
            null,
            null,
            null,
            new ArmorObjectBounds(0, 0, 0, 0, 0, 0),
            null,
            null,
            null,
            [addon],
            []);

    private static ArmorAddonProposalRequest OverrideProposal(
        FormReference addon) =>
        new(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(
                $"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\{addon.Plugin.Value}"),
            addon.FormId,
            ArmorAddonProposalMode.Override,
            EmptyArmorAddonPatch(),
            new WorkspacePath(
                $"K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work\\{addon.FormId.Value:X6}.armor-addon-proposal.json"));

    private static ArmorAddonProposalPatch EmptyArmorAddonPatch() =>
        new(null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, null, null);
}
