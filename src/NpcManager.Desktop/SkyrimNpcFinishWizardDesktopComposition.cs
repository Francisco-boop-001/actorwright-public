using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Desktop;

public sealed record SkyrimNpcFinishWizardDesktopContext(
    SkyrimNpcFinishWizardViewModel ViewModel,
    SkyrimNpcFinishWizardTransaction Transaction);

public static class SkyrimNpcFinishWizardDesktopComposition
{
    public static SkyrimNpcFinishWizardDesktopContext Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        Func<ReviewedGameIntake, FormReference?>? targetRaceResolver = null)
    {
        var manifestReader = new PackageManifestReader(policy, labRoot);
        var packageVerifier = new PackageVerifyService(manifestReader);
        var sourceReader = new SkyrimNpcFinishCoreSourcePackageReader(
            labRoot,
            policy,
            manifestReader,
            packageVerifier,
            new BethesdaSkyrimNpcFinishCoreSourceReader());
        return CreateCore(
            policy,
            labRoot,
            targetRaceResolver,
            sourceReader.InspectAsync,
            serviceDecorator: null);
    }

    internal static SkyrimNpcFinishWizardDesktopContext CreateForTest(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        Func<ReviewedGameIntake, FormReference?> targetRaceResolver,
        Func<ISkyrimNpcFinishCoreService, ISkyrimNpcFinishCoreService>
            serviceDecorator)
    {
        ArgumentNullException.ThrowIfNull(targetRaceResolver);
        ArgumentNullException.ThrowIfNull(serviceDecorator);
        var manifestReader = new PackageManifestReader(policy, labRoot);
        var packageVerifier = new PackageVerifyService(manifestReader);
        var sourceReader = new SkyrimNpcFinishCoreSourcePackageReader(
            labRoot,
            policy,
            manifestReader,
            packageVerifier,
            new BethesdaSkyrimNpcFinishCoreSourceReader());
        return CreateCore(
            policy,
            labRoot,
            targetRaceResolver,
            sourceReader.InspectAsync,
            serviceDecorator);
    }

    private static SkyrimNpcFinishWizardDesktopContext CreateCore(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        Func<ReviewedGameIntake, FormReference?>? targetRaceResolver,
        Func<
            SkyrimNpcFinishCoreRequest,
            CancellationToken,
            ValueTask<SkyrimNpcFinishCoreSourceReadResult>> inspectSource,
        Func<ISkyrimNpcFinishCoreService, ISkyrimNpcFinishCoreService>?
            serviceDecorator)
    {
        var externalInstallVerifier = new BethesdaExternalHeadPartInstallVerifier(
            new RaceMenuSelectedDependencyManifestReader(),
            new ExternalHeadPartPhysicsBindingResolver(policy, labRoot));
        ISkyrimNpcFinishCoreService service = new SkyrimNpcFinishCoreService(
            inspectSource,
            labRoot,
            externalInstallVerifier);
        service = serviceDecorator?.Invoke(service) ?? service;
        var transaction = new SkyrimNpcFinishWizardTransaction(service, labRoot);
        return new SkyrimNpcFinishWizardDesktopContext(
            new SkyrimNpcFinishWizardViewModel(transaction, targetRaceResolver),
            transaction);
    }
}
