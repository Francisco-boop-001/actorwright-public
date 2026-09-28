using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferencePresetRulesTests
{
    private static readonly Sha256Hash HashA = Hash('a');
    private static readonly Sha256Hash HashB = Hash('b');
    private static readonly Sha256Hash HashC = Hash('c');
    private static readonly Sha256Hash HashD = Hash('d');

    public static Task TestReferenceIntakeRules()
    {
        ReferencePresetIntake intake = ValidIntake();
        RequireNoErrors(
            ReferencePresetAuthoringRules.ValidateIntake(intake),
            "reference-intake-valid");

        ReferenceImageAuthority front = intake.Images[0];
        ReferencePresetIntake exactLimits = intake with
        {
            Description = string.Concat(
                Enumerable.Repeat(
                    "\U0001F642",
                    ReferencePresetAuthoringRules.MaximumDescriptionScalars)),
            Images = ImmutableArray.Create(front with
            {
                EncodedLength =
                    ReferencePresetAuthoringRules.MaximumEncodedImageBytes
            })
        };
        RequireNoErrors(
            ReferencePresetAuthoringRules.ValidateIntake(exactLimits),
            "reference-intake-exact-limits");

        ReferencePresetIntake nonNormalizedDescription = intake with
        {
            Description = "e\u0301"
        };
        RequireCode(
            ReferencePresetAuthoringRules.ValidateIntake(
                nonNormalizedDescription),
            "reference-intake-description-nfc");

        ReferencePresetIntake tooMany = intake with
        {
            Images = ImmutableArray.Create(
                front,
                front with
                {
                    ImageId = "left-three-quarter",
                    SourcePath = Path("left-three-quarter.png"),
                    ViewRole = ReferenceImageViewRole.LeftThreeQuarter
                },
                front with
                {
                    ImageId = "right-three-quarter",
                    SourcePath = Path("right-three-quarter.png"),
                    ViewRole = ReferenceImageViewRole.RightThreeQuarter
                },
                front with
                {
                    ImageId = "left-profile",
                    SourcePath = Path("left-profile.png"),
                    ViewRole = ReferenceImageViewRole.LeftProfile
                },
                front with
                {
                    ImageId = "right-profile",
                    SourcePath = Path("right-profile.png"),
                    ViewRole = ReferenceImageViewRole.RightProfile
                })
        };
        RequireCode(
            ReferencePresetAuthoringRules.ValidateIntake(tooMany),
            "reference-intake-image-count");

        ReferencePresetIntake oversizedDescription = intake with
        {
            Description = string.Concat(
                Enumerable.Repeat("\U0001F642",
                    ReferencePresetAuthoringRules.MaximumDescriptionScalars + 1))
        };
        RequireCode(
            ReferencePresetAuthoringRules.ValidateIntake(oversizedDescription),
            "reference-intake-description-scalars");

        ReferencePresetIntake duplicateRole = intake with
        {
            Images = ImmutableArray.Create(
                front,
                front with
                {
                    ImageId = "front-two",
                    SourcePath = Path("front-two.png")
                })
        };
        RequireCode(
            ReferencePresetAuthoringRules.ValidateIntake(duplicateRole),
            "reference-intake-view-role-duplicate");

        ReferencePresetIntake duplicatePath = intake with
        {
            Images = ImmutableArray.Create(
                front,
                front with
                {
                    ImageId = "left",
                    ViewRole = ReferenceImageViewRole.LeftThreeQuarter
                })
        };
        RequireCode(
            ReferencePresetAuthoringRules.ValidateIntake(duplicatePath),
            "reference-intake-image-path-duplicate");

        ReferencePresetIntake oversizedImage = intake with
        {
            Images = ImmutableArray.Create(front with
            {
                EncodedLength =
                    ReferencePresetAuthoringRules.MaximumEncodedImageBytes + 1
            })
        };
        RequireCode(
            ReferencePresetAuthoringRules.ValidateIntake(oversizedImage),
            "reference-intake-image-encoded-bytes");
        return Task.CompletedTask;
    }

    public static Task TestReferenceReviewRules()
    {
        LandmarkInferenceProposal validProposal = ValidInferenceProposal();
        RequireNoErrors(
            ReferencePresetAuthoringRules.ValidateInferenceProposal(
                validProposal, HashA, HashB),
            "reference-inference-valid");

        LandmarkInferenceProposal nonFinite = validProposal with
        {
            Images = ImmutableArray.Create(
                validProposal.Images[0] with { DetectorScore = double.NaN })
        };
        RequireCode(
            ReferencePresetAuthoringRules.ValidateInferenceProposal(
                nonFinite, HashA, HashB),
            "reference-inference-score-finite");

        ReviewedReferencePresetDesign review = ValidReviewedDesign();
        RequireNoErrors(
            ReferencePresetAuthoringRules.ValidateReviewedDesign(review, HashC),
            "reference-review-valid");

        ReviewedReferencePresetDesign unreviewed = review with
        {
            Traits = ImmutableArray.Create(review.Traits[0] with
            {
                ReviewState = ReferenceTraitReviewState.Proposed
            }),
            Unknowns = ImmutableArray.Create(
                new ReferenceUnknown(
                    "unmapped",
                    "unsupported-word",
                    ReferenceUnknownReviewState.Unreviewed))
        };
        ImmutableArray<Diagnostic> unreviewedDiagnostics =
            ReferencePresetAuthoringRules.ValidateReviewedDesign(
                unreviewed, HashC);
        RequireCode(unreviewedDiagnostics,
            "reference-review-trait-unreviewed");
        RequireCode(unreviewedDiagnostics,
            "reference-review-unknown-unreviewed");

        ReviewedReferenceView lowConfidence = review.Views[0] with
        {
            DetectorScore = 0.75,
            Anchors = ImmutableArray.Create(
                review.Views[0].Anchors[0] with
                {
                    ReviewState =
                        ReferenceAnchorReviewState.UnknownLowConfidence
                })
        };
        RequireCode(
            ReferencePresetAuthoringRules.ValidateReviewedDesign(
                review with { Views = ImmutableArray.Create(lowConfidence) },
                HashC),
            "reference-review-low-confidence-anchor");

        ReviewedReferencePresetDesign requiredAnchorUnknown = review with
        {
            Views = ImmutableArray.Create(review.Views[0] with
            {
                Anchors = ImmutableArray.Create(
                    review.Views[0].Anchors[0] with
                    {
                        ReviewState =
                            ReferenceAnchorReviewState.UnknownHidden
                    })
            })
        };
        RequireCode(
            ReferencePresetAuthoringRules.CanSolve(
                requiredAnchorUnknown,
                HashC),
            "reference-solve-required-anchor-unknown");

        ReferencePresetAuthoringProposal unacknowledgedLoss =
            ValidAuthoringProposal() with
            {
                Losses = ImmutableArray.Create(new ReferencePresetLoss(
                    "unsupported-detail",
                    ReferencePresetLossKind.UnsupportedDescription,
                    "The detail cannot be represented by RaceMenu.",
                    false))
            };
        RequireCode(
            ReferencePresetAuthoringRules.CanWritePreset(
                unacknowledgedLoss,
                HashD,
                HashD,
                HashC,
                HashB),
            "reference-write-loss-unacknowledged");
        return Task.CompletedTask;
    }

    public static Task TestReferenceMeshBindingRules()
    {
        ReferenceMeshAnchorBinding binding = ValidBinding();
        RequireNoErrors(
            ReferencePresetAuthoringRules.ValidateMeshBindings(
                ImmutableArray.Create(binding)),
            "reference-binding-valid");

        RequireCode(
            ReferencePresetAuthoringRules.ValidateMeshBindings(
                ImmutableArray.Create(binding with
                {
                    Barycentric0 = 0.2,
                    Barycentric1 = 0.2,
                    Barycentric2 = 0.2
                })),
            "reference-binding-barycentric-sum");

        RequireCode(
            ReferencePresetAuthoringRules.ValidateMeshBindings(
                ImmutableArray.Create(binding with
                {
                    Barycentric0 = double.NaN
                })),
            "reference-binding-barycentric-range");

        ReferenceMeshAnchorBinding second = binding with
        {
            Anchor = ReferenceSemanticAnchorKind.Chin,
            TopologySha256 = HashD
        };
        RequireCode(
            ReferencePresetAuthoringRules.ValidateMeshBindings(
                ImmutableArray.Create(binding, second)),
            "reference-binding-topology-drift");

        second = binding with
        {
            Anchor = ReferenceSemanticAnchorKind.Chin,
            CameraSha256 = HashA
        };
        RequireCode(
            ReferencePresetAuthoringRules.ValidateMeshBindings(
                ImmutableArray.Create(binding, second)),
            "reference-binding-camera-drift");
        return Task.CompletedTask;
    }

    public static Task TestReferenceAuthorityTransitions()
    {
        RequireNoErrors(
            ReferencePresetAuthoringRules.ValidateAuthorityTransition(
                ReferencePresetAuthorityKind.Intake,
                ReferencePresetAuthorityKind.InferenceProposal,
                HashA,
                HashA),
            "reference-authority-valid");

        RequireCode(
            ReferencePresetAuthoringRules.ValidateAuthorityTransition(
                ReferencePresetAuthorityKind.InferenceProposal,
                ReferencePresetAuthorityKind.Intake,
                HashA,
                HashA),
            "reference-authority-nonmonotonic");

        RequireCode(
            ReferencePresetAuthoringRules.ValidateAuthorityTransition(
                ReferencePresetAuthorityKind.Intake,
                ReferencePresetAuthorityKind.ReviewedDesign,
                HashA,
                HashA),
            "reference-authority-skip");

        RequireCode(
            ReferencePresetAuthoringRules.ValidateAuthorityTransition(
                ReferencePresetAuthorityKind.Intake,
                ReferencePresetAuthorityKind.InferenceProposal,
                HashA,
                HashB),
            "reference-authority-parent-hash");

        RequireCode(
            ReferencePresetAuthoringRules.ValidateReviewedDesign(
                ValidReviewedDesign(), HashD),
            "reference-review-parent-hash");
        return Task.CompletedTask;
    }

    public static Task TestReferenceSculptRules()
    {
        ReviewedReferencePresetDesign review = ValidReviewedDesign();
        ReferenceSculptEligibilityRequest singleView = new(
            review.Views,
            review.MeshBindings,
            HashB,
            100.0,
            ImmutableArray.Create(
                new ReferenceSculptVertexDelta(10, 0.0, 0.0, 0.5)),
            ImmutableArray<int>.Empty,
            10.0,
            9.0);
        RequireCode(
            ReferencePresetAuthoringRules.CanSculpt(singleView),
            "reference-sculpt-view-separation");

        ReviewedReferenceView side = review.Views[0] with
        {
            ImageId = "left",
            ViewRole = ReferenceImageViewRole.LeftThreeQuarter,
            ReviewedYawDegrees = -40.0
        };
        ReferenceSculptEligibilityRequest neckDrift = singleView with
        {
            Views = ImmutableArray.Create(review.Views[0], side),
            Deltas = ImmutableArray.Create(
                new ReferenceSculptVertexDelta(10, 0.1, 0.0, 0.0)),
            ProtectedNeckRingVertexIndices = ImmutableArray.Create(10)
        };
        RequireCode(
            ReferencePresetAuthoringRules.CanSculpt(neckDrift),
            "reference-sculpt-neck-ring");

        ReferenceSculptEligibilityRequest excessive = neckDrift with
        {
            Deltas = ImmutableArray.Create(
                new ReferenceSculptVertexDelta(11, 1.500001, 0.0, 0.0)),
            ProtectedNeckRingVertexIndices = ImmutableArray<int>.Empty
        };
        RequireCode(
            ReferencePresetAuthoringRules.CanSculpt(excessive),
            "reference-sculpt-displacement-cap");

        ReferenceSculptEligibilityRequest valid = excessive with
        {
            Deltas = ImmutableArray.Create(
                new ReferenceSculptVertexDelta(11, 1.5, 0.0, 0.0))
        };
        RequireNoErrors(
            ReferencePresetAuthoringRules.CanSculpt(valid),
            "reference-sculpt-valid");
        return Task.CompletedTask;
    }

    internal static ReferencePresetIntake ValidIntake()
    {
        var race = new FormReference(
            new PluginName("Skyrim.esm"),
            new FormId(0x00013746));
        var target = new RaceMenuPresetTarget(
            "reviewed-target",
            race,
            NpcSex.Female,
            Path("copied-data"),
            ImmutableArray<SkyrimFaceRecordPluginAuthority>.Empty);
        return new ReferencePresetIntake(
            1,
            "reference-rules",
            "Controlled Reference",
            race,
            NpcSex.Female,
            50.0F,
            "vanilla-head",
            Path("baseline.jslot"),
            HashA,
            "wide cheeks and a narrow nose",
            ImmutableArray.Create(new ReferenceImageAuthority(
                "front",
                Path("front.png"),
                HashB,
                1024,
                ReferenceImageViewRole.Front)),
            target);
    }

    internal static LandmarkInferenceProposal ValidInferenceProposal()
    {
        ImmutableArray<ReferenceFaceLandmark> landmarks =
            Enumerable.Range(0, 478)
                .Select(index => new ReferenceFaceLandmark(
                    index,
                    index / 1000.0,
                    index / 1000.0,
                    0.0,
                    1.0,
                    1.0))
                .ToImmutableArray();
        ImmutableArray<double> transform =
            Enumerable.Range(0, 16)
                .Select(index => index % 5 == 0 ? 1.0 : 0.0)
                .ToImmutableArray();
        var image = new ReferenceImageInference(
            "front",
            ReferenceImageViewRole.Front,
            HashB,
            HashC,
            1024,
            1024,
            0.90,
            landmarks,
            transform,
            0.0,
            HashA,
            HashB,
            HashC);
        return new LandmarkInferenceProposal(
            1,
            ReferencePresetAuthorityKind.InferenceProposal,
            HashA,
            HashB,
            ImmutableArray.Create(image),
            ImmutableArray.Create(new ReferenceDescriptionTrait(
                "wide cheeks",
                ReferenceDescriptionTraitKind.CheekWidth,
                0.6,
                ReferenceTraitChannel.Geometry,
                1.0,
                ReferenceTraitReviewState.Proposed,
                false)),
            ImmutableArray<ReferenceUnknown>.Empty);
    }

    internal static ReviewedReferencePresetDesign ValidReviewedDesign()
    {
        var anchor = new ReferenceSemanticAnchor(
            ReferenceSemanticAnchorKind.ForeheadCenter,
            10,
            0.5,
            0.2,
            0.90,
            true,
            ReferenceAnchorReviewState.Accepted);
        var view = new ReviewedReferenceView(
            "front",
            ReferenceImageViewRole.Front,
            HashB,
            0.90,
            0.0,
            true,
            ImmutableArray.Create(anchor));
        var trait = new ReferenceDescriptionTrait(
            "wide cheeks",
            ReferenceDescriptionTraitKind.CheekWidth,
            0.6,
            ReferenceTraitChannel.Geometry,
            1.0,
            ReferenceTraitReviewState.Accepted,
            false);
        FormReference face = new(
            new PluginName("Skyrim.esm"),
            new FormId(0x0005161E));
        var selection = new ReferencePresetCatalogSelection(
            true,
            face,
            face,
            face,
            face,
            face,
            ImmutableArray<ReferenceTintSelection>.Empty);
        return new ReviewedReferencePresetDesign(
            1,
            ReferencePresetAuthorityKind.ReviewedDesign,
            HashC,
            true,
            ImmutableArray.Create(view),
            ImmutableArray.Create(trait),
            ImmutableArray<ReferenceUnknown>.Empty,
            selection,
            ImmutableArray.Create(ValidBinding()));
    }

    private static ReferenceMeshAnchorBinding ValidBinding() => new(
        ReferenceImageViewRole.Front,
        ReferenceSemanticAnchorKind.ForeheadCenter,
        "head-nif",
        "FemaleHeadNord",
        HashA,
        HashB,
        HashC,
        HashD,
        HashA,
        0,
        0,
        1,
        2,
        0.2,
        0.3,
        0.5);

    private static ReferencePresetAuthoringProposal
        ValidAuthoringProposal()
    {
        var solver = new ReferenceRaceMenuPresetSolverResult(
            ImmutableDictionary<string, double>.Empty,
            ImmutableDictionary<string, double>.Empty,
            ImmutableArray<ReferenceSculptVertexDelta>.Empty,
            ImmutableArray<ReferencePresetResidual>.Empty,
            ImmutableArray<ReferencePresetLoss>.Empty,
            ReferencePresetAuthoringRules.SolverIterations,
            0.0,
            HashA,
            ImmutableArray<Diagnostic>.Empty);
        var comparison = new ReferencePresetComparisonResult(
            ImmutableArray<ReferencePresetComparisonArtifact>.Empty,
            ImmutableArray<ReferencePresetResidual>.Empty,
            ImmutableArray<ReferencePresetLoss>.Empty,
            ImmutableArray<Diagnostic>.Empty);
        return new ReferencePresetAuthoringProposal(
            1,
            ReferencePresetAuthorityKind.AuthoringProposal,
            "reference-rules",
            ValidIntake().Race,
            NpcSex.Female,
            50.0F,
            HashC,
            HashB,
            solver,
            comparison,
            ImmutableArray<ReferencePresetLoss>.Empty,
            ImmutableArray<Diagnostic>.Empty);
    }

    private static WorkspacePath Path(string leaf) =>
        new($@"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work\reference-rules\{leaf}");

    private static Sha256Hash Hash(char value) => new(new string(value, 64));

    private static void RequireCode(
        ImmutableArray<Diagnostic> diagnostics,
        string code)
    {
        if (!diagnostics.Any(item =>
                string.Equals(item.Code, code, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"missing-diagnostic:{code}:{string.Join(',', diagnostics.Select(item => item.Code))}");
        }
    }

    private static void RequireNoErrors(
        ImmutableArray<Diagnostic> diagnostics,
        string context)
    {
        Diagnostic[] errors = diagnostics
            .Where(item => item.Severity == DiagnosticSeverity.Error)
            .ToArray();
        if (errors.Length != 0)
        {
            throw new InvalidOperationException(
                $"{context}:{string.Join(',', errors.Select(item => item.Code))}");
        }
    }
}
