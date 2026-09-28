using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

internal sealed class FaceGeomHairRegionCardViewModel :
    NotifyViewModel
{
    private readonly Action<
        FaceGeomHairRegionCardViewModel,
        FaceGeomHairRegionRole>? roleChangeRequested;
    private FaceGeomHairRegionRole role;
    private string targetColor;
    private WorkspacePath? thumbnailPath;
    private WorkspacePath? maskPath;

    public FaceGeomHairRegionCardViewModel(
        FaceGeomHairRegionsRegion region,
        Action<
            FaceGeomHairRegionCardViewModel,
            FaceGeomHairRegionRole>? roleChangeRequested = null)
    {
        Region = region ??
            throw new ArgumentNullException(nameof(region));
        this.roleChangeRequested = roleChangeRequested;
        role = region.DefaultRole;
        targetColor = region.CurrentColor;
    }

    public FaceGeomHairRegionsRegion Region { get; }

    public string StructuralId => Region.StructuralId;

    public string Name => Region.Name;

    public string SharedShaderGroupId =>
        Region.SharedShaderGroupId;

    public string CurrentColor =>
        Region.CurrentColor;

    public string TargetColor =>
        targetColor;

    public WorkspacePath? ThumbnailPath =>
        thumbnailPath;

    public WorkspacePath? MaskPath =>
        maskPath;

    public bool HasPreviewArtifacts =>
        thumbnailPath is not null &&
        maskPath is not null;

    public string TextureRoutesText =>
        Region.TextureRoutes.IsDefaultOrEmpty
            ? "No texture route declared"
            : string.Join(
                " · ",
                Region.TextureRoutes);

    public FaceGeomHairRegionRole Role
    {
        get => role;
        set
        {
            if (role == value)
            {
                return;
            }

            if (roleChangeRequested is null)
            {
                ApplyLinkedRole(value);
                return;
            }

            roleChangeRequested(this, value);
        }
    }

    internal bool ApplyLinkedRole(
        FaceGeomHairRegionRole value) =>
        Set(ref role, value);

    internal void UpdateTargetColor(
        string primary,
        string accent)
    {
        string value = Role switch
        {
            FaceGeomHairRegionRole.Primary =>
                primary,
            FaceGeomHairRegionRole.Accent =>
                accent,
            _ => CurrentColor
        };
        Set(
            ref targetColor,
            value,
            nameof(TargetColor));
    }

    internal void SetPreviewArtifacts(
        WorkspacePath? thumbnail,
        WorkspacePath? mask)
    {
        bool thumbnailChanged =
            Set(
                ref thumbnailPath,
                thumbnail,
                nameof(ThumbnailPath));
        bool maskChanged =
            Set(
                ref maskPath,
                mask,
                nameof(MaskPath));
        if (thumbnailChanged ||
            maskChanged)
        {
            Raise(nameof(HasPreviewArtifacts));
        }
    }
}
