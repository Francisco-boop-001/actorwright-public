using System.Collections.Immutable;
using System.Text;
using NpcManager.Domain;

namespace NpcManager.Application;

public static class ReferencePresetAuthoringRules
{
    public const int MaximumImageCount = 4;
    public const long MaximumEncodedImageBytes = 33_554_432;
    public const int MaximumImageDimension = 8_192;
    public const long MaximumDecodedBytes = 536_870_912;
    public const int MaximumDescriptionScalars = 4_096;
    public const double MinimumFaceScore = 0.50;
    public const double LowConfidenceCeiling = 0.75;
    public const int SolverIterations = 256;
    public const double GeneralMorphMagnitudeCap = 0.8;
    public const double MinimumSculptYawSeparation = 35.0;
    public const double MaximumSculptDisplacementRatio = 0.015;

    private const int CurrentSchemaVersion = 1;
    private const double BarycentricTolerance = 0.000001;

    public static ImmutableArray<Diagnostic> ValidateIntake(
        ReferencePresetIntake intake)
    {
        ArgumentNullException.ThrowIfNull(intake);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        if (intake.SchemaVersion != CurrentSchemaVersion)
        {
            diagnostics.Add(Error(
                "reference-intake-schema",
                "The reference intake schema version is unsupported."));
        }

        ValidateBoundedIdentity(
            intake.ProjectId,
            128,
            "reference-intake-project-id",
            "Project identity",
            diagnostics);
        ValidateBoundedIdentity(
            intake.TargetName,
            128,
            "reference-intake-target-name",
            "Target name",
            diagnostics);
        ValidateBoundedIdentity(
            intake.HeadSystemId,
            128,
            "reference-intake-head-system",
            "Head-system identity",
            diagnostics);

        if (!Enum.IsDefined(intake.Sex))
        {
            diagnostics.Add(Error(
                "reference-intake-sex",
                "The requested NPC sex is unsupported."));
        }

        if (!float.IsFinite(intake.Weight) ||
            intake.Weight < 0.0F ||
            intake.Weight > 100.0F)
        {
            diagnostics.Add(Error(
                "reference-intake-weight",
                "Skyrim weight must be finite and within 0 through 100."));
        }

        if (!IsHashBound(intake.BaselineJslotSha256))
        {
            diagnostics.Add(Error(
                "reference-intake-baseline-hash",
                "The baseline JSlot must have an exact SHA-256."));
        }

        int scalarCount;
        string normalizedDescription;
        try
        {
            string description = intake.Description ?? string.Empty;
            normalizedDescription =
                description.Normalize(NormalizationForm.FormC);
            scalarCount = normalizedDescription.EnumerateRunes().Count();
            if (!string.Equals(
                    description,
                    normalizedDescription,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    "reference-intake-description-nfc",
                    "The reference description must already be normalized to Unicode NFC."));
            }
        }
        catch (ArgumentException)
        {
            normalizedDescription = string.Empty;
            scalarCount = MaximumDescriptionScalars + 1;
            diagnostics.Add(Error(
                "reference-intake-description-nfc",
                "The reference description is not valid Unicode NFC text."));
        }

        if (scalarCount > MaximumDescriptionScalars)
        {
            diagnostics.Add(Error(
                "reference-intake-description-scalars",
                $"The normalized description exceeds {MaximumDescriptionScalars} Unicode scalar values."));
        }

        if (intake.Images.IsDefaultOrEmpty ||
            intake.Images.Length > MaximumImageCount)
        {
            diagnostics.Add(Error(
                "reference-intake-image-count",
                $"Reference intake requires one through {MaximumImageCount} images."));
        }

        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        var imagePaths = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var viewRoles = new HashSet<ReferenceImageViewRole>();
        foreach (ReferenceImageAuthority image in intake.Images)
        {
            if (image is null ||
                string.IsNullOrWhiteSpace(image.ImageId) ||
                image.ImageId.Length > 128)
            {
                diagnostics.Add(Error(
                    "reference-intake-image-id",
                    "Every reference image requires a bounded stable identity."));
                continue;
            }

            if (!imageIds.Add(image.ImageId))
            {
                diagnostics.Add(Error(
                    "reference-intake-image-id-duplicate",
                    $"Reference image identity '{image.ImageId}' is duplicated."));
            }

            string path = image.SourcePath.Value ?? string.Empty;
            if (path.Length == 0 || !Path.IsPathFullyQualified(path))
            {
                diagnostics.Add(Error(
                    "reference-intake-image-path",
                    $"Reference image '{image.ImageId}' has no fully qualified path."));
            }
            else if (!imagePaths.Add(path))
            {
                diagnostics.Add(Error(
                    "reference-intake-image-path-duplicate",
                    $"Reference image path '{path}' is duplicated."));
            }

            if (!IsHashBound(image.SourceSha256))
            {
                diagnostics.Add(Error(
                    "reference-intake-image-hash",
                    $"Reference image '{image.ImageId}' has no exact SHA-256."));
            }

            if (image.EncodedLength <= 0 ||
                image.EncodedLength > MaximumEncodedImageBytes)
            {
                diagnostics.Add(Error(
                    "reference-intake-image-encoded-bytes",
                    $"Reference image '{image.ImageId}' exceeds the encoded-byte limit."));
            }

            if (!Enum.IsDefined(image.ViewRole))
            {
                diagnostics.Add(Error(
                    "reference-intake-view-role",
                    $"Reference image '{image.ImageId}' has an unsupported view role."));
            }
            else if (!viewRoles.Add(image.ViewRole))
            {
                diagnostics.Add(Error(
                    "reference-intake-view-role-duplicate",
                    $"View role '{image.ViewRole.ToWireName()}' is duplicated."));
            }
        }

        if (intake.Target is null ||
            intake.Target.Race != intake.Race ||
            intake.Target.Sex != intake.Sex)
        {
            diagnostics.Add(Error(
                "reference-intake-target-identity",
                "The reviewed preset target does not match the requested race and sex."));
        }

        return diagnostics.ToImmutable();
    }

    public static ImmutableArray<Diagnostic> ValidateInferenceProposal(
        LandmarkInferenceProposal proposal,
        Sha256Hash expectedIntakeSha256,
        Sha256Hash expectedRuntimeManifestSha256)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        if (proposal.SchemaVersion != CurrentSchemaVersion)
        {
            diagnostics.Add(Error(
                "reference-inference-schema",
                "The landmark proposal schema version is unsupported."));
        }

        if (proposal.Authority !=
            ReferencePresetAuthorityKind.InferenceProposal)
        {
            diagnostics.Add(Error(
                "reference-inference-authority",
                "Only an inference-proposal document is valid at this stage."));
        }

        if (!HashesEqual(proposal.IntakeSha256, expectedIntakeSha256))
        {
            diagnostics.Add(Error(
                "reference-inference-intake-hash",
                "The landmark proposal is not bound to the expected intake."));
        }

        if (!HashesEqual(
                proposal.RuntimeManifestSha256,
                expectedRuntimeManifestSha256))
        {
            diagnostics.Add(Error(
                "reference-inference-runtime-hash",
                "The landmark proposal is not bound to the expected runtime manifest."));
        }

        if (proposal.Images.IsDefaultOrEmpty ||
            proposal.Images.Length > MaximumImageCount)
        {
            diagnostics.Add(Error(
                "reference-inference-image-count",
                $"Inference requires one through {MaximumImageCount} images."));
        }

        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        var roles = new HashSet<ReferenceImageViewRole>();
        long aggregateDecodedBytes = 0;
        foreach (ReferenceImageInference image in proposal.Images)
        {
            if (image is null || string.IsNullOrWhiteSpace(image.ImageId))
            {
                diagnostics.Add(Error(
                    "reference-inference-image-id",
                    "Every inferred image requires a stable identity."));
                continue;
            }

            if (!imageIds.Add(image.ImageId))
            {
                diagnostics.Add(Error(
                    "reference-inference-image-id-duplicate",
                    $"Inferred image identity '{image.ImageId}' is duplicated."));
            }

            if (!Enum.IsDefined(image.ViewRole) ||
                !roles.Add(image.ViewRole))
            {
                diagnostics.Add(Error(
                    "reference-inference-view-role",
                    $"Inferred view role for '{image.ImageId}' is unsupported or duplicated."));
            }

            if (!double.IsFinite(image.DetectorScore))
            {
                diagnostics.Add(Error(
                    "reference-inference-score-finite",
                    $"Detector score for '{image.ImageId}' must be finite."));
            }
            else if (image.DetectorScore < MinimumFaceScore ||
                     image.DetectorScore > 1.0)
            {
                diagnostics.Add(Error(
                    "reference-inference-score-range",
                    $"Detector score for '{image.ImageId}' is outside the admitted range."));
            }

            if (image.Width <= 0 ||
                image.Height <= 0 ||
                image.Width > MaximumImageDimension ||
                image.Height > MaximumImageDimension)
            {
                diagnostics.Add(Error(
                    "reference-inference-dimensions",
                    $"Canonical dimensions for '{image.ImageId}' are outside the admitted range."));
            }
            else
            {
                try
                {
                    long decodedBytes = checked(
                        checked((long)image.Width * image.Height) * 4L);
                    aggregateDecodedBytes = checked(
                        aggregateDecodedBytes + decodedBytes);
                }
                catch (OverflowException)
                {
                    aggregateDecodedBytes = long.MaxValue;
                }
            }

            if (image.Landmarks.IsDefault ||
                image.Landmarks.Length != 478)
            {
                diagnostics.Add(Error(
                    "reference-inference-landmark-count",
                    $"Image '{image.ImageId}' must retain exactly 478 landmarks."));
            }
            else
            {
                for (var index = 0; index < image.Landmarks.Length; index++)
                {
                    ReferenceFaceLandmark landmark = image.Landmarks[index];
                    if (landmark.Index != index)
                    {
                        diagnostics.Add(Error(
                            "reference-inference-landmark-order",
                            $"Image '{image.ImageId}' has unstable landmark ordering."));
                        break;
                    }

                    if (!double.IsFinite(landmark.X) ||
                        !double.IsFinite(landmark.Y) ||
                        !double.IsFinite(landmark.Z))
                    {
                        diagnostics.Add(Error(
                            "reference-inference-landmark-finite",
                            $"Image '{image.ImageId}' has a non-finite landmark."));
                        break;
                    }

                    if (landmark.X < 0.0 || landmark.X > 1.0 ||
                        landmark.Y < 0.0 || landmark.Y > 1.0)
                    {
                        diagnostics.Add(Error(
                            "reference-inference-landmark-range",
                            $"Image '{image.ImageId}' has an out-of-range normalized landmark."));
                        break;
                    }

                    if (!IsOptionalUnitInterval(landmark.Presence) ||
                        !IsOptionalUnitInterval(landmark.Visibility))
                    {
                        diagnostics.Add(Error(
                            "reference-inference-landmark-confidence",
                            $"Image '{image.ImageId}' has an invalid landmark confidence."));
                        break;
                    }
                }
            }

            if (image.TransformationMatrix.IsDefault ||
                image.TransformationMatrix.Length != 16)
            {
                diagnostics.Add(Error(
                    "reference-inference-transform-count",
                    $"Image '{image.ImageId}' must retain one 4x4 transformation matrix."));
            }
            else if (image.TransformationMatrix.Any(
                         value => !double.IsFinite(value)))
            {
                diagnostics.Add(Error(
                    "reference-inference-transform-finite",
                    $"Image '{image.ImageId}' has a non-finite transformation matrix."));
            }

            if (!double.IsFinite(image.AdvisoryYawDegrees) ||
                image.AdvisoryYawDegrees < -90.0 ||
                image.AdvisoryYawDegrees > 90.0)
            {
                diagnostics.Add(Error(
                    "reference-inference-yaw",
                    $"Image '{image.ImageId}' has an invalid advisory yaw."));
            }

            if (!IsHashBound(image.SourceSha256) ||
                !IsHashBound(image.CanonicalRgbaSha256) ||
                !IsHashBound(image.NativeLibrarySha256) ||
                !IsHashBound(image.DetectorModelSha256) ||
                !IsHashBound(image.LandmarkerModelSha256))
            {
                diagnostics.Add(Error(
                    "reference-inference-hash",
                    $"Image '{image.ImageId}' has incomplete source or runtime authority."));
            }
        }

        if (aggregateDecodedBytes > MaximumDecodedBytes)
        {
            diagnostics.Add(Error(
                "reference-inference-decoded-budget",
                "The canonical image set exceeds the decoded-memory budget."));
        }

        foreach (ReferenceDescriptionTrait trait in proposal.Traits)
        {
            ValidateTrait(trait, diagnostics, "reference-inference");
        }

        foreach (ReferenceUnknown unknown in proposal.Unknowns)
        {
            if (unknown is null ||
                string.IsNullOrWhiteSpace(unknown.Text) ||
                string.IsNullOrWhiteSpace(unknown.Reason))
            {
                diagnostics.Add(Error(
                    "reference-inference-unknown",
                    "Every unresolved description token requires text and reason."));
            }
        }

        return diagnostics.ToImmutable();
    }

    public static ImmutableArray<Diagnostic> ValidateReviewedDesign(
        ReviewedReferencePresetDesign design,
        Sha256Hash expectedProposalSha256)
    {
        ArgumentNullException.ThrowIfNull(design);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        if (design.SchemaVersion != CurrentSchemaVersion)
        {
            diagnostics.Add(Error(
                "reference-review-schema",
                "The reviewed-design schema version is unsupported."));
        }

        if (design.Authority != ReferencePresetAuthorityKind.ReviewedDesign)
        {
            diagnostics.Add(Error(
                "reference-review-authority",
                "Only a reviewed-design document is valid at this stage."));
        }

        if (!HashesEqual(design.ProposalSha256, expectedProposalSha256))
        {
            diagnostics.Add(Error(
                "reference-review-parent-hash",
                "The reviewed design is not bound to the expected inference proposal."));
        }

        if (!design.ReviewAccepted)
        {
            diagnostics.Add(Error(
                "reference-review-not-accepted",
                "The reviewed design requires explicit acceptance."));
        }

        if (design.Views.IsDefaultOrEmpty ||
            design.Views.Length > MaximumImageCount)
        {
            diagnostics.Add(Error(
                "reference-review-view-count",
                $"Review requires one through {MaximumImageCount} views."));
        }

        var imageIds = new HashSet<string>(StringComparer.Ordinal);
        var roles = new HashSet<ReferenceImageViewRole>();
        foreach (ReviewedReferenceView view in design.Views)
        {
            if (view is null ||
                string.IsNullOrWhiteSpace(view.ImageId) ||
                !imageIds.Add(view.ImageId))
            {
                diagnostics.Add(Error(
                    "reference-review-view-id",
                    "Every reviewed view requires a distinct stable identity."));
                continue;
            }

            if (!Enum.IsDefined(view.ViewRole) ||
                !roles.Add(view.ViewRole))
            {
                diagnostics.Add(Error(
                    "reference-review-view-role",
                    $"Reviewed view role for '{view.ImageId}' is unsupported or duplicated."));
            }

            if (!view.ReviewAccepted)
            {
                diagnostics.Add(Error(
                    "reference-review-view-unaccepted",
                    $"View '{view.ImageId}' has not been accepted."));
            }

            if (!double.IsFinite(view.DetectorScore) ||
                view.DetectorScore < MinimumFaceScore ||
                view.DetectorScore > 1.0)
            {
                diagnostics.Add(Error(
                    "reference-review-score",
                    $"View '{view.ImageId}' has an invalid detector score."));
            }

            if (!double.IsFinite(view.ReviewedYawDegrees) ||
                view.ReviewedYawDegrees < -90.0 ||
                view.ReviewedYawDegrees > 90.0)
            {
                diagnostics.Add(Error(
                    "reference-review-yaw",
                    $"View '{view.ImageId}' has an invalid reviewed yaw."));
            }

            if (view.Anchors.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error(
                    "reference-review-anchor-count",
                    $"View '{view.ImageId}' has no semantic anchors."));
                continue;
            }

            var anchors = new HashSet<ReferenceSemanticAnchorKind>();
            foreach (ReferenceSemanticAnchor anchor in view.Anchors)
            {
                if (anchor is null ||
                    !Enum.IsDefined(anchor.Anchor) ||
                    !anchors.Add(anchor.Anchor))
                {
                    diagnostics.Add(Error(
                        "reference-review-anchor-duplicate",
                        $"View '{view.ImageId}' has an unsupported or duplicate anchor."));
                    continue;
                }

                if (anchor.SourceLandmarkIndex < 0 ||
                    anchor.SourceLandmarkIndex >= 478)
                {
                    diagnostics.Add(Error(
                        "reference-review-anchor-landmark-index",
                        $"View '{view.ImageId}' has an invalid source landmark index."));
                }

                if (!IsUnitInterval(anchor.X) ||
                    !IsUnitInterval(anchor.Y) ||
                    !IsUnitInterval(anchor.Confidence))
                {
                    diagnostics.Add(Error(
                        "reference-review-anchor-coordinate",
                        $"View '{view.ImageId}' has a non-finite or out-of-range anchor."));
                }

                if (!Enum.IsDefined(anchor.ReviewState))
                {
                    diagnostics.Add(Error(
                        "reference-review-anchor-state",
                        $"View '{view.ImageId}' has an unsupported anchor state."));
                }

                if (view.DetectorScore <= LowConfidenceCeiling &&
                    anchor.ReviewState ==
                    ReferenceAnchorReviewState.UnknownLowConfidence)
                {
                    diagnostics.Add(Error(
                        "reference-review-low-confidence-anchor",
                        $"Low-confidence view '{view.ImageId}' still has an unconfirmed visible anchor."));
                }
            }
        }

        foreach (ReferenceDescriptionTrait trait in design.Traits)
        {
            ValidateTrait(trait, diagnostics, "reference-review");
            if (trait.ReviewState ==
                ReferenceTraitReviewState.Proposed)
            {
                diagnostics.Add(Error(
                    "reference-review-trait-unreviewed",
                    $"Description trait '{trait.SourcePhrase}' has no review decision."));
            }
        }

        foreach (ReferenceUnknown unknown in design.Unknowns)
        {
            if (unknown is null ||
                unknown.ReviewState ==
                ReferenceUnknownReviewState.Unreviewed)
            {
                diagnostics.Add(Error(
                    "reference-review-unknown-unreviewed",
                    "Every unresolved description token requires a review decision."));
            }
        }

        if (design.CatalogSelection is null ||
            !design.CatalogSelection.ReviewAccepted)
        {
            diagnostics.Add(Error(
                "reference-review-catalog-unaccepted",
                "The compatible headpart and tint selection requires explicit review."));
        }

        diagnostics.AddRange(ValidateMeshBindings(design.MeshBindings));
        return diagnostics.ToImmutable();
    }

    public static ImmutableArray<Diagnostic> ValidateMeshBindings(
        ImmutableArray<ReferenceMeshAnchorBinding> bindings)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (bindings.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                "reference-binding-empty",
                "At least one reviewed mesh-anchor binding is required."));
            return diagnostics.ToImmutable();
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        var shapes = new Dictionary<
            string,
            (Sha256Hash Nif, Sha256Hash Topology, Sha256Hash RestPositions)>(
            StringComparer.Ordinal);
        var views = new Dictionary<
            ReferenceImageViewRole,
            (Sha256Hash Camera, Sha256Hash Render)>();

        foreach (ReferenceMeshAnchorBinding binding in bindings)
        {
            if (binding is null ||
                !Enum.IsDefined(binding.ViewRole) ||
                !Enum.IsDefined(binding.Anchor))
            {
                diagnostics.Add(Error(
                    "reference-binding-identity",
                    "Every mesh binding requires a supported view and anchor."));
                continue;
            }

            string identity =
                $"{binding.ViewRole.ToWireName()}\0{binding.Anchor.ToWireName()}";
            if (!identities.Add(identity))
            {
                diagnostics.Add(Error(
                    "reference-binding-duplicate",
                    "A view and semantic anchor may have only one mesh binding."));
            }

            if (string.IsNullOrWhiteSpace(binding.NifIdentity) ||
                string.IsNullOrWhiteSpace(binding.ShapeIdentity) ||
                !IsHashBound(binding.NifSha256) ||
                !IsHashBound(binding.TopologySha256) ||
                !IsHashBound(binding.RestPositionsSha256) ||
                !IsHashBound(binding.CameraSha256) ||
                !IsHashBound(binding.RenderSha256))
            {
                diagnostics.Add(Error(
                    "reference-binding-authority",
                    "A mesh binding has incomplete NIF, shape, topology, rest-position, camera, or render authority."));
            }

            if (binding.TriangleOrdinal < 0 ||
                binding.VertexIndex0 < 0 ||
                binding.VertexIndex1 < 0 ||
                binding.VertexIndex2 < 0 ||
                binding.VertexIndex0 == binding.VertexIndex1 ||
                binding.VertexIndex0 == binding.VertexIndex2 ||
                binding.VertexIndex1 == binding.VertexIndex2)
            {
                diagnostics.Add(Error(
                    "reference-binding-triangle",
                    "A mesh binding has an invalid triangle identity."));
            }

            double sum = binding.Barycentric0 +
                         binding.Barycentric1 +
                         binding.Barycentric2;
            if (!IsUnitInterval(binding.Barycentric0) ||
                !IsUnitInterval(binding.Barycentric1) ||
                !IsUnitInterval(binding.Barycentric2))
            {
                diagnostics.Add(Error(
                    "reference-binding-barycentric-range",
                    "Barycentric weights must be finite and within zero through one."));
            }
            else if (Math.Abs(sum - 1.0) > BarycentricTolerance)
            {
                diagnostics.Add(Error(
                    "reference-binding-barycentric-sum",
                    "Barycentric weights must sum to one within 1e-6."));
            }

            string shapeKey =
                $"{binding.NifIdentity}\0{binding.ShapeIdentity}";
            var shapeAuthority = (
                Nif: binding.NifSha256,
                Topology: binding.TopologySha256,
                RestPositions: binding.RestPositionsSha256);
            if (shapes.TryGetValue(shapeKey, out var priorShape))
            {
                if (!HashesEqual(
                        priorShape.Nif, shapeAuthority.Nif))
                {
                    diagnostics.Add(Error(
                        "reference-binding-nif-drift",
                        $"NIF identity '{binding.NifIdentity}' changed within one reviewed binding set."));
                }

                if (!HashesEqual(
                        priorShape.Topology, shapeAuthority.Topology))
                {
                    diagnostics.Add(Error(
                        "reference-binding-topology-drift",
                        $"Shape '{binding.ShapeIdentity}' changed topology within one reviewed binding set."));
                }

                if (!HashesEqual(
                        priorShape.RestPositions,
                        shapeAuthority.RestPositions))
                {
                    diagnostics.Add(Error(
                        "reference-binding-rest-position-drift",
                        $"Shape '{binding.ShapeIdentity}' changed rest positions within one reviewed binding set."));
                }
            }
            else
            {
                shapes.Add(shapeKey, shapeAuthority);
            }

            var viewAuthority = (
                binding.CameraSha256,
                binding.RenderSha256);
            if (views.TryGetValue(binding.ViewRole, out var priorView))
            {
                if (!HashesEqual(
                        priorView.Camera, viewAuthority.CameraSha256))
                {
                    diagnostics.Add(Error(
                        "reference-binding-camera-drift",
                        $"View '{binding.ViewRole.ToWireName()}' changed camera authority within one binding set."));
                }

                if (!HashesEqual(
                        priorView.Render, viewAuthority.RenderSha256))
                {
                    diagnostics.Add(Error(
                        "reference-binding-render-drift",
                        $"View '{binding.ViewRole.ToWireName()}' changed render authority within one binding set."));
                }
            }
            else
            {
                views.Add(binding.ViewRole, viewAuthority);
            }
        }

        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// The first resource/render pass necessarily precedes reviewed baseline
    /// mesh clicks. It admits an explicitly empty binding array while retaining
    /// every other reviewed-design rule. Once any binding exists, the complete
    /// binding closure is validated normally.
    /// </summary>
    public static ImmutableArray<Diagnostic>
        ValidateReviewedDesignForResourceSnapshot(
        ReviewedReferencePresetDesign design,
        Sha256Hash expectedProposalSha256)
    {
        ImmutableArray<Diagnostic> diagnostics =
            ValidateReviewedDesign(
                design, expectedProposalSha256);
        if (!design.MeshBindings.IsEmpty)
        {
            return diagnostics;
        }
        return diagnostics
            .Where(item =>
                item.Code != "reference-binding-empty")
            .ToImmutableArray();
    }

    public static ImmutableArray<Diagnostic> CanSolve(
        ReviewedReferencePresetDesign design,
        Sha256Hash expectedProposalSha256)
    {
        ImmutableArray<Diagnostic>.Builder diagnostics =
            ValidateReviewedDesign(design, expectedProposalSha256)
                .ToBuilder();
        var bindingKeys = new HashSet<string>(
            design.MeshBindings.Select(binding =>
                $"{binding.ViewRole.ToWireName()}\0{binding.Anchor.ToWireName()}"),
            StringComparer.Ordinal);

        foreach (ReviewedReferenceView view in design.Views)
        {
            foreach (ReferenceSemanticAnchor anchor in view.Anchors)
            {
                if (!anchor.Required)
                {
                    continue;
                }

                if (anchor.ReviewState is not
                    (ReferenceAnchorReviewState.Accepted or
                     ReferenceAnchorReviewState.Corrected))
                {
                    diagnostics.Add(Error(
                        "reference-solve-required-anchor-unknown",
                        $"Required anchor '{anchor.Anchor.ToWireName()}' in view '{view.ViewRole.ToWireName()}' is explicitly unknown."));
                    continue;
                }

                string key =
                    $"{view.ViewRole.ToWireName()}\0{anchor.Anchor.ToWireName()}";
                if (!bindingKeys.Contains(key))
                {
                    diagnostics.Add(Error(
                        "reference-solve-binding-missing",
                        $"Required anchor '{anchor.Anchor.ToWireName()}' in view '{view.ViewRole.ToWireName()}' has no reviewed mesh binding."));
                }
            }
        }

        return diagnostics.ToImmutable();
    }

    public static ImmutableArray<Diagnostic> CanSculpt(
        ReferenceSculptEligibilityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        double maximumSeparation = 0.0;
        ReviewedReferenceView[] acceptedViews = request.Views
            .Where(view => view is not null && view.ReviewAccepted)
            .ToArray();
        for (var left = 0; left < acceptedViews.Length; left++)
        {
            for (var right = left + 1;
                 right < acceptedViews.Length;
                 right++)
            {
                double separation = Math.Abs(
                    acceptedViews[left].ReviewedYawDegrees -
                    acceptedViews[right].ReviewedYawDegrees);
                if (double.IsFinite(separation))
                {
                    maximumSeparation = Math.Max(
                        maximumSeparation, separation);
                }
            }
        }

        if (maximumSeparation < MinimumSculptYawSeparation)
        {
            diagnostics.Add(Error(
                "reference-sculpt-view-separation",
                $"Sculpt requires two reviewed views separated by at least {MinimumSculptYawSeparation} degrees."));
        }

        if (!IsHashBound(request.TopologySha256) ||
            request.MeshBindings.Any(binding =>
                !HashesEqual(
                    binding.TopologySha256,
                    request.TopologySha256)))
        {
            diagnostics.Add(Error(
                "reference-sculpt-topology-drift",
                "Every sculpt binding must use the same exact topology."));
        }

        if (!double.IsFinite(request.HeadBoundingBoxDiagonal) ||
            request.HeadBoundingBoxDiagonal <= 0.0)
        {
            diagnostics.Add(Error(
                "reference-sculpt-head-bounds",
                "The reviewed head bounding-box diagonal must be finite and positive."));
        }

        double displacementCap =
            request.HeadBoundingBoxDiagonal *
            MaximumSculptDisplacementRatio;
        var vertexIndices = new HashSet<int>();
        var neckRing = request.ProtectedNeckRingVertexIndices
            .ToHashSet();
        foreach (ReferenceSculptVertexDelta delta in request.Deltas)
        {
            if (delta is null ||
                delta.VertexIndex < 0 ||
                !vertexIndices.Add(delta.VertexIndex) ||
                !double.IsFinite(delta.X) ||
                !double.IsFinite(delta.Y) ||
                !double.IsFinite(delta.Z))
            {
                diagnostics.Add(Error(
                    "reference-sculpt-delta",
                    "Every sculpt delta requires one distinct vertex and finite coordinates."));
                continue;
            }

            double magnitude = Math.Sqrt(
                delta.X * delta.X +
                delta.Y * delta.Y +
                delta.Z * delta.Z);
            if (double.IsFinite(displacementCap) &&
                magnitude > displacementCap)
            {
                diagnostics.Add(Error(
                    "reference-sculpt-displacement-cap",
                    $"Sculpt vertex {delta.VertexIndex} exceeds the 1.5 percent displacement cap."));
            }

            if (neckRing.Contains(delta.VertexIndex) &&
                (delta.X != 0.0 || delta.Y != 0.0 || delta.Z != 0.0))
            {
                diagnostics.Add(Error(
                    "reference-sculpt-neck-ring",
                    $"Protected neck-ring vertex {delta.VertexIndex} changed."));
            }
        }

        if (!double.IsFinite(request.ObjectiveBefore) ||
            !double.IsFinite(request.ObjectiveAfter) ||
            request.ObjectiveAfter >= request.ObjectiveBefore)
        {
            diagnostics.Add(Error(
                "reference-sculpt-objective",
                "Sculpt must strictly improve the declared finite objective."));
        }

        return diagnostics.ToImmutable();
    }

    public static ImmutableArray<Diagnostic> ValidateAuthorityTransition(
        ReferencePresetAuthorityKind current,
        ReferencePresetAuthorityKind next,
        Sha256Hash actualParentSha256,
        Sha256Hash expectedParentSha256)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!Enum.IsDefined(current) || !Enum.IsDefined(next))
        {
            diagnostics.Add(Error(
                "reference-authority-kind",
                "The authority transition contains an unsupported state."));
            return diagnostics.ToImmutable();
        }

        int difference = (int)next - (int)current;
        if (difference <= 0)
        {
            diagnostics.Add(Error(
                "reference-authority-nonmonotonic",
                "Reference authority cannot move backward or remain unchanged."));
        }
        else if (difference != 1)
        {
            diagnostics.Add(Error(
                "reference-authority-skip",
                "Reference authority cannot skip an intermediate reviewed state."));
        }

        if (!HashesEqual(actualParentSha256, expectedParentSha256))
        {
            diagnostics.Add(Error(
                "reference-authority-parent-hash",
                "The next authority document is not bound to the expected parent hash."));
        }

        return diagnostics.ToImmutable();
    }

    public static ImmutableArray<Diagnostic> CanWritePreset(
        ReferencePresetAuthoringProposal proposal,
        Sha256Hash actualProposalSha256,
        Sha256Hash acceptedProposalSha256,
        Sha256Hash expectedReviewedDesignSha256,
        Sha256Hash expectedResourceSnapshotSha256)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(ValidateAuthorityTransition(
            ReferencePresetAuthorityKind.ReviewedDesign,
            proposal.Authority,
            proposal.ReviewedDesignSha256,
            expectedReviewedDesignSha256));

        if (!HashesEqual(
                proposal.ResourceSnapshotSha256,
                expectedResourceSnapshotSha256))
        {
            diagnostics.Add(Error(
                "reference-write-resource-hash",
                "The authoring proposal is not bound to the expected resource snapshot."));
        }

        if (!HashesEqual(actualProposalSha256, acceptedProposalSha256))
        {
            diagnostics.Add(Error(
                "reference-write-accepted-proposal-hash",
                "The accepted proposal hash does not match the reopened authoring proposal."));
        }

        if (proposal.Losses.Any(loss => !loss.Acknowledged))
        {
            diagnostics.Add(Error(
                "reference-write-loss-unacknowledged",
                "Every retained authoring loss requires explicit acknowledgement."));
        }

        if (proposal.Diagnostics.Any(
                item => item.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(Error(
                "reference-write-proposal-errors",
                "An authoring proposal with errors cannot write a JSlot."));
        }

        return diagnostics.ToImmutable();
    }

    public static ImmutableArray<Diagnostic> CanBuildNpc(
        VerifiedReferencePreset preset,
        Sha256Hash actualVerifiedPresetDocumentSha256,
        VerifiedReferenceNpcHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(handoff);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(ValidateAuthorityTransition(
            ReferencePresetAuthorityKind.VerifiedPreset,
            handoff.Authority,
            handoff.VerifiedPresetDocumentSha256,
            actualVerifiedPresetDocumentSha256));

        if (preset.Authority !=
            ReferencePresetAuthorityKind.VerifiedPreset)
        {
            diagnostics.Add(Error(
                "reference-npc-preset-authority",
                "NPC handoff requires a verified-reference-preset authority."));
        }

        if (handoff.PresetPath != preset.PresetPath ||
            !HashesEqual(handoff.PresetSha256, preset.PresetSha256) ||
            handoff.Race != preset.Race ||
            handoff.Sex != preset.Sex ||
            handoff.Weight != preset.Weight)
        {
            diagnostics.Add(Error(
                "reference-npc-handoff-identity",
                "The NPC handoff does not preserve the exact verified JSlot and target identity."));
        }

        if (preset.Diagnostics.Any(
                item => item.Severity == DiagnosticSeverity.Error) ||
            handoff.Diagnostics.Any(
                item => item.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(Error(
                "reference-npc-handoff-errors",
                "An authority document with errors cannot enter the NPC build."));
        }

        return diagnostics.ToImmutable();
    }

    private static void ValidateTrait(
        ReferenceDescriptionTrait trait,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string prefix)
    {
        if (trait is null ||
            string.IsNullOrWhiteSpace(trait.SourcePhrase) ||
            !Enum.IsDefined(trait.Kind) ||
            !Enum.IsDefined(trait.Channel) ||
            !Enum.IsDefined(trait.ReviewState))
        {
            diagnostics.Add(Error(
                $"{prefix}-trait",
                "Every description trait requires a typed source phrase and state."));
            return;
        }

        if (!double.IsFinite(trait.Strength) ||
            trait.Strength < -1.0 ||
            trait.Strength > 1.0)
        {
            diagnostics.Add(Error(
                $"{prefix}-trait-strength",
                $"Description trait '{trait.SourcePhrase}' has an invalid strength."));
        }

        if (!IsUnitInterval(trait.Confidence))
        {
            diagnostics.Add(Error(
                $"{prefix}-trait-confidence",
                $"Description trait '{trait.SourcePhrase}' has an invalid confidence."));
        }
    }

    private static void ValidateBoundedIdentity(
        string value,
        int maximumLength,
        string code,
        string label,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength)
        {
            diagnostics.Add(Error(
                code,
                $"{label} must be non-empty and at most {maximumLength} characters."));
        }
    }

    private static bool IsHashBound(Sha256Hash hash) =>
        hash.Value is { Length: 64 };

    private static bool HashesEqual(Sha256Hash left, Sha256Hash right) =>
        IsHashBound(left) &&
        IsHashBound(right) &&
        string.Equals(
            left.Value,
            right.Value,
            StringComparison.Ordinal);

    private static bool IsUnitInterval(double value) =>
        double.IsFinite(value) &&
        value >= 0.0 &&
        value <= 1.0;

    private static bool IsOptionalUnitInterval(double? value) =>
        value is null || IsUnitInterval(value.Value);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
