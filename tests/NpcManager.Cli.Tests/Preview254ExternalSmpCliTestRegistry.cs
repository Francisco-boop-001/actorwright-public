using System.Collections.Immutable;
using System.Reflection;

namespace NpcManager.Cli.Tests;

internal interface IPreview254ExternalSmpCliScenario
{
    string Selector { get; }

    ValueTask RunAsync(CancellationToken cancellationToken);
}

internal static class Preview254ExternalSmpCliTestRegistry
{
    private static readonly ImmutableArray<string> ReservedSelectors =
        [
            "--test-package-verify-external-install-cli",
            "--test-finish-core-external-smp-cli",
            "--test-protocol-v2-external-smp"
        ];

    internal static ImmutableArray<string> Selectors => ReservedSelectors;

    internal static async ValueTask<int?> TryRunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args.Length != 1 || !ReservedSelectors.Contains(args[0],
                StringComparer.Ordinal))
            return null;

        string requested = args[0];
        try
        {
            var scenarios = typeof(IPreview254ExternalSmpCliScenario)
                .Assembly
                .GetTypes()
                .Where(type => type is { IsClass: true, IsAbstract: false } &&
                    typeof(IPreview254ExternalSmpCliScenario)
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
                    "CLI scenario registry contains an invalid selector set.");

            var matching = scenarios.Where(scenario =>
                scenario.Selector.Equals(requested, StringComparison.Ordinal))
                .ToArray();
            if (matching.Length != 1)
                throw new InvalidDataException(
                    $"CLI scenario selector '{requested}' is not unique.");

            await matching[0].RunAsync(cancellationToken);
            Console.WriteLine($"PASS {requested}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {requested} {StableReason(exception)}");
            return 1;
        }
    }

    private static IPreview254ExternalSmpCliScenario CreateScenario(Type type)
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
                $"CLI scenario type '{type.FullName}' violates the closed class contract.");

        if (type.GetFields(BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic).Any(field => !field.IsLiteral &&
                !field.IsInitOnly))
            throw new InvalidDataException(
                $"CLI scenario type '{type.FullName}' has mutable static state.");

        return (IPreview254ExternalSmpCliScenario)Activator.CreateInstance(
            type, nonPublic: true)!;
    }

    private static string StableReason(Exception exception) =>
        exception is AggregateException aggregate
            ? string.Join("; ", aggregate.Flatten().InnerExceptions
                .Select(inner => inner.Message))
            : exception.Message;
}
