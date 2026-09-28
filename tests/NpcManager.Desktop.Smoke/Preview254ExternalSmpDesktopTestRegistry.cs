using System.Collections.Immutable;
using System.IO;
using System.Reflection;

namespace NpcManager.Desktop.Smoke;

internal interface IPreview254ExternalSmpDesktopScenario
{
    string Selector { get; }

    void Run();
}

internal static class Preview254ExternalSmpDesktopTestRegistry
{
    private static readonly ImmutableArray<string> ReservedSelectors =
        [
            "--test-desktop-external-smp-composition",
            "--test-npc-finish-wizard-external-install"
        ];

    internal static ImmutableArray<string> Selectors => ReservedSelectors;

    internal static int? TryRun(string[] args)
    {
        if (args.Length != 1 || !ReservedSelectors.Contains(args[0],
                StringComparer.Ordinal))
            return null;

        string requested = args[0];
        try
        {
            var scenarios = typeof(IPreview254ExternalSmpDesktopScenario)
                .Assembly
                .GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } &&
                    typeof(IPreview254ExternalSmpDesktopScenario)
                        .IsAssignableFrom(type))
                .Select(CreateScenario)
                .OrderBy(scenario => scenario.Selector,
                    StringComparer.Ordinal)
                .ToArray();

            if (scenarios.Select(scenario => scenario.Selector)
                    .Distinct(StringComparer.Ordinal).Count() != scenarios.Length ||
                scenarios.Any(scenario =>
                    string.IsNullOrWhiteSpace(scenario.Selector) ||
                    !ReservedSelectors.Contains(scenario.Selector,
                        StringComparer.Ordinal)))
                throw new InvalidDataException(
                    "Desktop scenario registry contains an invalid selector set.");

            var matching = scenarios.Where(scenario =>
                scenario.Selector.Equals(requested, StringComparison.Ordinal))
                .ToArray();
            if (matching.Length != 1)
                throw new InvalidDataException(
                    $"Desktop scenario selector '{requested}' is not unique.");

            matching[0].Run();
            Console.WriteLine($"PASS {requested}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {requested} {StableReason(exception)}");
            return 1;
        }
    }

    private static IPreview254ExternalSmpDesktopScenario CreateScenario(
        Type type)
    {
        if (!type.IsSealed || type.IsNested || type.ContainsGenericParameters ||
            type.GetConstructors(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic).Length != 1 ||
            type.GetConstructor(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic, binder: null, Type.EmptyTypes,
                modifiers: null) is null ||
            type.GetProperties(BindingFlags.Instance | BindingFlags.Public |
                BindingFlags.NonPublic).SingleOrDefault(property =>
                property.Name.Equals("Selector", StringComparison.Ordinal) &&
                property.PropertyType == typeof(string) &&
                property.GetMethod is not null) is null)
            throw new InvalidDataException(
                $"Desktop scenario type '{type.FullName}' violates the closed class contract.");

        if (type.GetFields(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic).Any(field => !field.IsLiteral &&
                !field.IsInitOnly))
            throw new InvalidDataException(
                $"Desktop scenario type '{type.FullName}' has mutable static state.");

        return (IPreview254ExternalSmpDesktopScenario)Activator.CreateInstance(
            type, nonPublic: true)!;
    }

    private static string StableReason(Exception exception) =>
        exception is AggregateException aggregate
            ? string.Join("; ", aggregate.Flatten().InnerExceptions
                .Select(inner => inner.Message))
            : exception.Message;
}

internal sealed class Preview254ExternalSmpDesktopScenario
    : IPreview254ExternalSmpDesktopScenario
{
    public string Selector => "--test-npc-finish-wizard-external-install";

    public void Run() =>
        SkyrimNpcFinishWizardTests.RunExternalInstallAsync(
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
}

internal sealed class DesktopExternalSmpCompositionScenario
    : IPreview254ExternalSmpDesktopScenario
{
    public string Selector => "--test-desktop-external-smp-composition";

    public void Run() => DesktopExternalSmpCompositionTests.Run();
}
