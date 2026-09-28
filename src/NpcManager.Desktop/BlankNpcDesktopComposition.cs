using System.IO;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Desktop;

internal sealed record BlankNpcDesktopContext(
    IBlankNpcBuildService Service,
    BlankNpcProviderService ProviderSelector,
    IQualifiedFaceGeomCarrierService FaceGeomCarrierService,
    BlankNpcBuildRequest InitialRequest);

/// <summary>Small desktop composition root for the shared blank-NPC workflow.</summary>
internal static class BlankNpcDesktopComposition
{
    private const string TexconvSha256 =
        "DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06";

    public static BlankNpcDesktopContext Create(IWorkspacePolicy policy, WorkspacePath labRoot)
    {
        var texconv = new WorkspacePath(Path.Combine(labRoot.Value,
            "tools", "external", "directxtex-texconv-2026.5.7", "texconv.exe"));
        var encoder = new TexconvFaceTintTextureEncoder(
            texconv, labRoot, new Sha256Hash(TexconvSha256));
        var decoder = new InProcessDdsTextureDecoder(labRoot);
        var faceTint = new FaceTintBuildService(policy, labRoot, encoder, decoder);
        var packageReader = new PackageManifestReader(policy, labRoot);
        var packageVerifier = new PackageVerifyService(packageReader);
        var faceGeomCarrier = new QualifiedFaceGeomCarrierService(policy, labRoot);
        var providerService = new BlankNpcProviderService(policy, labRoot);
        var service = new BlankNpcBuildService(
            NpcCreationComposition.Create(policy, labRoot),
            providerService,
            faceGeomCarrier,
            faceTint,
            decoder,
            packageVerifier,
            policy,
            labRoot);
        return new BlankNpcDesktopContext(
            service, providerService, faceGeomCarrier, CreateInitialRequest(labRoot));
    }

    private static BlankNpcBuildRequest CreateInitialRequest(WorkspacePath labRoot)
    {
        var project = Path.Combine(labRoot.Value, ".actorwright");
        var providerRoot = Path.Combine(project, "providers", "unselected");
        var skyrim = new PluginName("Skyrim.esm");
        return new BlankNpcBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(Path.Combine(providerRoot, "provider-manifest.json")),
            new Sha256Hash(new string('0', 64)),
            new WorkspacePath(Path.Combine(providerRoot, "Data", "Template.esp")),
            new Sha256Hash(new string('0', 64)),
            new FormId(0x00000800),
            new WorkspacePath(Path.Combine(providerRoot, "Data", "meshes", "FaceGeom", "00000800.nif")),
            new Sha256Hash(new string('0', 64)),
            new WorkspacePath(Path.Combine(providerRoot, "facetint-manifest.json")),
            new WorkspacePath(Path.Combine(providerRoot, "Data")),
            new WorkspacePath(Path.Combine(providerRoot, "dependency-manifest.json")),
            CreateFreshOutputRoot(project),
            new PluginName("NpcManagerGuiBlank.esp"),
            new NpcCreationIdentity(new EditorId("NPCM_Gate1GuiBlank"),
                new NpcName("NPC Manager GUI Blank")),
            new SkyrimNpcCreationTraits(NpcSex.Female, NpcCreationRole.StaticValidation,
                true, false, false, false, true),
            new SkyrimNpcCreationReferences(
                new FormReference(skyrim, new FormId(0x00013746)),
                new FormReference(skyrim, new FormId(0x00013ADC)),
                new FormReference(skyrim, new FormId(0x00013181)),
                new FormReference(skyrim, new FormId(0x0003BE1D)),
                new FormReference(skyrim, new FormId(0x0001DC10))),
            TemplateCarrierNpcAppearanceSource.Instance,
            new SkyrimNpcCreationStats(
                new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                0, 0, 0, 1, 1, 100, 35, 0, 50, 50, 50, 1f, 0f, 255));
    }

    private static WorkspacePath CreateFreshOutputRoot(string project)
    {
        var parent = Path.Combine(project, "03-builds", "work", "gate1-blank-npc-walking-product");
        Directory.CreateDirectory(parent);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        return new WorkspacePath(Path.Combine(parent, "gui-run-" + stamp));
    }
}
