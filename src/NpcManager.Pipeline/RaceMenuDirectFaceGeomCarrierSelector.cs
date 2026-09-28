using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

internal sealed record RaceMenuDirectFaceGeomCarrierSelection(
    WorkspacePath SourceNif,
    Sha256Hash SourceSha256,
    bool RequiresOwnedCopy)
{
    public QualifiedFaceGeomCarrierProfile QualificationProfile { get; init; } =
        RequiresOwnedCopy
            ? QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete
            : QualifiedFaceGeomCarrierProfile.ProviderPinnedSevenShape;
}

/// <summary>
/// Keeps generic schema-6 provider authority separate from the stronger,
/// transaction-local proof emitted by the Manager's native JSlot bake.
/// </summary>
internal static class RaceMenuDirectFaceGeomCarrierSelector
{
    public static RaceMenuDirectFaceGeomCarrierSelection Select(
        WorkspacePath charGenNif,
        Sha256Hash charGenSha256,
        WorkspacePath providerCarrierNif,
        Sha256Hash providerCarrierSha256,
        RaceMenuManagerOwnedFaceGeomCarrierAuthority? managerAuthority,
        RaceMenuNpcExternalCharGenExportAuthority? externalAuthority = null)
    {
        if (managerAuthority is not null && externalAuthority is not null)
            throw new InvalidDataException(
                "Manager-owned and external RaceMenu-export carrier authorities are mutually exclusive.");

        if (externalAuthority is not null)
        {
            if (!externalAuthority.UserConfirmedVisualMatch ||
                externalAuthority.RuntimeAuthority ||
                externalAuthority.FaceGeomSha256 != charGenSha256)
            {
                throw new InvalidDataException(
                    "External RaceMenu-export carrier authority must be user-confirmed, non-runtime, and match the exact selected CharGen NIF SHA-256.");
            }

            return new RaceMenuDirectFaceGeomCarrierSelection(
                charGenNif,
                charGenSha256,
                RequiresOwnedCopy: true)
            {
                QualificationProfile =
                    QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete
            };
        }

        if (managerAuthority is null)
        {
            return new RaceMenuDirectFaceGeomCarrierSelection(
                providerCarrierNif,
                providerCarrierSha256,
                RequiresOwnedCopy: false);
        }

        if (!string.Equals(
                managerAuthority.FaceGeom.Value,
                charGenNif.Value,
                StringComparison.OrdinalIgnoreCase) ||
            managerAuthority.FaceGeomSha256 != charGenSha256)
        {
            throw new InvalidDataException(
                "Manager-owned FaceGeom carrier authority must match the exact " +
                "selected CharGen NIF path and SHA-256.");
        }

        return new RaceMenuDirectFaceGeomCarrierSelection(
            charGenNif,
            charGenSha256,
            RequiresOwnedCopy: true);
    }
}
