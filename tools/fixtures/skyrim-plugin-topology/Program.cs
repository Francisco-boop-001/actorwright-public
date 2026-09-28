using Actorwright.PublicFixtures;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: topology-generator <output-directory>");
    return 2;
}

(string repaired, string orphan) = SyntheticSkyrimPluginTopology.Generate(args[0]);
Console.WriteLine($"Generated {Path.GetFileName(repaired)} and {Path.GetFileName(orphan)}.");
return 0;