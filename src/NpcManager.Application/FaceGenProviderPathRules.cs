using NpcManager.Domain;

namespace NpcManager.Application;

public static class FaceGenProviderPathRules
{
    /// <summary>Returns the CK/engine FaceGen filename FormID, including the ESL light-plugin rule.</summary>
    public static uint ToFaceGenLocalFormId(FormId formId) =>
        (formId.Value >> 24) == 0xFE ? formId.Value & 0xFFFu : formId.Value & 0xFFFFFFu;
}
