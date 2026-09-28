using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private const string ExpectedSchema3DependencyId =
        "selected-preset-dependencies-c668d45e1a3f1be034590b1d";
    private const string ExpectedSchema3CanonicalBase64 =
        "ew0KICAic2NoZW1hVmVyc2lvbiI6IDMsDQogICJpZCI6ICJzZWxlY3RlZC1wcmVzZXQtZGVwZW5kZW5jaWVzLWM2NjhkNDVlMWEzZjFiZTAzNDU5MGIxZCIsDQogICJwcmVzZXRTaGEyNTYiOiAiOTkwOWVjODMxZTJjZjZkMGM3M2ZiNTQ4MGYzMTk0NWE4MDk4N2ExM2ZhZWUwMDU3MDQxNjZjYjUzYTI2Y2VjYSIsDQogICJoZWFkUGFydHMiOiBbDQogICAgew0KICAgICAgImZvcm1LZXkiOiAiUHJvdmlkZXIuZXNwfDAwMDEyMyIsDQogICAgICAidHlwZSI6ICJmYWNlIiwNCiAgICAgICJwbHVnaW5FdmlkZW5jZSI6ICJhcnRpZmFjdHMvc2VsZWN0ZWQtZGVwZW5kZW5jeS1zY2hlbWEtZ29sZGVuL1Byb3ZpZGVyLmVzcCIsDQogICAgICAicGx1Z2luU2hhMjU2IjogIjAzOTA1OGM2ZjJjMGNiNDkyYzUzM2IwYTRkMTRlZjc3Y2MwZjc4YWJjY2NlZDUyODdkODRhMWEyMDExY2ZiODEiDQogICAgfQ0KICBdLA0KICAibG9vc2VBc3NldHMiOiBbDQogICAgew0KICAgICAgImdhbWVQYXRoIjogIm1lc2hlcy9hY3RvcndyaWdodC9kZXBlbmRlbmN5Lm5pZiIsDQogICAgICAicHJvdmlkZXIiOiAibG9vc2UiLA0KICAgICAgImV2aWRlbmNlUGF0aCI6ICJhcnRpZmFjdHMvc2VsZWN0ZWQtZGVwZW5kZW5jeS1zY2hlbWEtZ29sZGVuL21lc2hlcy9hY3RvcndyaWdodC9kZXBlbmRlbmN5Lm5pZiIsDQogICAgICAic2hhMjU2IjogIjc4N2M3OThlMzlhNWJjMTkxMDM1NWJhZTZkMGNkODdhMzZiMmUxMGZkMDIwMmE4M2UzYmI2YjAwNWRhODM0NzIiLA0KICAgICAgImJ5dGVMZW5ndGgiOiAzDQogICAgfQ0KICBdLA0KICAiYXJjaGl2ZXMiOiBbDQogICAgew0KICAgICAgInByb3ZpZGVyIjogIk9yY2hpZEFkb3JubWVudC5ic2EiLA0KICAgICAgImV2aWRlbmNlUGF0aCI6ICJhcnRpZmFjdHMvc2VsZWN0ZWQtZGVwZW5kZW5jeS1zY2hlbWEtZ29sZGVuL09yY2hpZEFkb3JubWVudC5ic2EiLA0KICAgICAgInNoYTI1NiI6ICJiMjM1NDlkZGExNTc4MDE1MzNkMWQyNzJkYTVmZjg4NjgzYmYxZmJlNmVlNDZkZWIzMDY2YmY1NWY3ZDA1NTA3IiwNCiAgICAgICJieXRlTGVuZ3RoIjogNCwNCiAgICAgICJtZW1iZXJzIjogWw0KICAgICAgICB7DQogICAgICAgICAgImdhbWVQYXRoIjogIm1lc2hlcy9hY3RvcndyaWdodC9hcmNoaXZlLWRlcGVuZGVuY3kubmlmIiwNCiAgICAgICAgICAic2hhMjU2IjogIjQzZmNhZDRkMTA3OWIxZGZhZmVmOTIxYmFhN2Q1M2M5ZDBlYmU3NGMzMzVhMjRmMmQ0YTBjNTUwZTA1OWFlNGUiLA0KICAgICAgICAgICJieXRlTGVuZ3RoIjogNA0KICAgICAgICB9LA0KICAgICAgICB7DQogICAgICAgICAgImdhbWVQYXRoIjogInRleHR1cmVzL2FjdG9yd3JpZ2h0L2FyY2hpdmUtZGVwZW5kZW5jeS5kZHMiLA0KICAgICAgICAgICJzaGEyNTYiOiAiMGVmODdiYjFlMjI2MDgwMGRjNGRlMDc5ZjQ2M2FhYjZkODA2NTRmMTgyMThmYTRkYzNhMDNkODlkNTYyZTA1MSIsDQogICAgICAgICAgImJ5dGVMZW5ndGgiOiA1DQogICAgICAgIH0NCiAgICAgIF0NCiAgICB9DQogIF0sDQogICJleHRlcm5hbFByb3ZpZGVyU2lkZWNhcnMiOiBbDQogICAgew0KICAgICAgImdhbWVQYXRoIjogIm1lc2hlcy9hY3RvcndyaWdodC9kZXBlbmRlbmN5LnhtbCIsDQogICAgICAicHJvdmlkZXIiOiAibG9vc2UiLA0KICAgICAgImV2aWRlbmNlUGF0aCI6ICJhcnRpZmFjdHMvc2VsZWN0ZWQtZGVwZW5kZW5jeS1zY2hlbWEtZ29sZGVuL21lc2hlcy9hY3RvcndyaWdodC9kZXBlbmRlbmN5LnhtbCIsDQogICAgICAic2hhMjU2IjogIjY2YTY3NTcxNTFmOGVlNTVkYjEyNzcxNmM3ZTNkY2UwYmU4MDc0YjY0ZTIwZWRhNTQyZTVjMWU0NmNhOWM0MWUiLA0KICAgICAgImJ5dGVMZW5ndGgiOiAzDQogICAgfQ0KICBdLA0KICAiZXh0ZXJuYWxJbnN0YWxsRGVwZW5kZW5jaWVzIjogWw0KICAgIHsNCiAgICAgICJkZXNjcmlwdG9ySWQiOiAiMDdmYTJhNDcyZjA3ZThjZjI5YmQ1NGQ4ZDBmZmM1MjhiOTllYTkxMWU2YWJmMDg5ZjJkNmIzYjNlMWVjMjIzOSIsDQogICAgICAiZGVzY3JpcHRvciI6IHsic2NoZW1hSWRlbnRpZmllciI6Im5wYy5leHRlcm5hbC1oZWFkcGFydC1kZXBlbmRlbmN5LnYxIiwiZGVzY3JpcHRvcklkIjoiMDdmYTJhNDcyZjA3ZThjZjI5YmQ1NGQ4ZDBmZmM1MjhiOTllYTkxMWU2YWJmMDg5ZjJkNmIzYjNlMWVjMjIzOSIsImRpc3Bvc2l0aW9uIjoicmVjb3JkLW9ubHktZXh0ZXJuYWwiLCJyb290U291cmNlRm9ybSI6IlByb3ZpZGVyLmVzcHwweDAwMDAwODAwIiwicm9vdFdpbm5pbmdGb3JtIjoiUHJvdmlkZXIuZXNwfDB4MDAwMDA4MDAiLCJyb290VHlwZSI6ImhhaXIiLCJncmFwaFNoYTI1NiI6IjVjOGI1YzU2OTY1NTVmMzg0Mjk1YzM2MGM1NDdhNDhiMTAxYzViMWU0ODA3MTA3YzI1YjRlNTI5Nzg4ODM1YzYiLCJwcm92aWRlciI6eyJwbHVnaW4iOiJQcm92aWRlci5lc3AiLCJwbHVnaW5TaGEyNTYiOiIwMzkwNThjNmYyYzBjYjQ5MmM1MzNiMGE0ZDE0ZWY3N2NjMGY3OGFiY2NjZWQ1Mjg3ZDg0YTFhMjAxMWNmYjgxIiwicGx1Z2luQnl0ZUxlbmd0aCI6MywicmVkaXN0cmlidXRpb25Nb2RlIjoiZXh0ZXJuYWwtcHJvdmlkZXItcmVxdWlyZWQifSwibWVtYmVycyI6W3sib3JpZ2luRm9ybSI6IlByb3ZpZGVyLmVzcHwweDAwMDAwODAwIiwicmVxdWlyZWRPdXRwdXRNYXN0ZXIiOiJQcm92aWRlci5lc3AiLCJ3aW5uaW5nRm9ybSI6IlByb3ZpZGVyLmVzcHwweDAwMDAwODAwIiwid2lubmluZ1BsdWdpbiI6IlByb3ZpZGVyLmVzcCIsIndpbm5pbmdQbHVnaW5TaGEyNTYiOiIwMzkwNThjNmYyYzBjYjQ5MmM1MzNiMGE0ZDE0ZWY3N2NjMGY3OGFiY2NjZWQ1Mjg3ZDg0YTFhMjAxMWNmYjgxIiwid2lubmluZ1BsdWdpbkJ5dGVMZW5ndGgiOjMsIndpbm5pbmdSZWNvcmRTaGEyNTYiOiIxZTMzOTM1NDkyZmYxOWU3OGJhNjE4ZGIyZDdmZjMyMmQ3ZGYxNzNiZGI5YWZmZmU2NzI2MTY1YWJlZmEyZDFiIiwiZWRpdG9ySWQiOiJPcmNoaWRIYWlyIiwiZGVjbGFyZWRUeXBlIjoiaGFpciIsImVmZmVjdGl2ZVR5cGUiOiJoYWlyIiwibW9kZWxOaWYiOiJtZXNoZXMvYWN0b3J3cmlnaHQvZGVwZW5kZW5jeS5uaWYiLCJ0cmlSb3V0ZXMiOltdLCJobmFtRWRnZXMiOltdLCJwYXJlbnQiOm51bGwsImRlcHRoIjowLCJyb3V0ZU9yZGVyIjowLCJhcHBsaWVzVG9TZXgiOiJtYWxlIiwidmFsaWRSYWNlIjpudWxsfV0sInBoeXNpY3MiOnsibW9kZSI6ImRpcmVjdC1uaWYtZXh0cmEtZGF0YSIsInNoYXBlcyI6W3sibWVtYmVyRm9ybSI6IlByb3ZpZGVyLmVzcHwweDAwMDAwODAwIiwibW9kZWxOaWYiOiJtZXNoZXMvYWN0b3J3cmlnaHQvZGVwZW5kZW5jeS5uaWYiLCJzaGFwZU5hbWUiOiJPcmNoaWRIYWlyIiwieG1sUGF0aCI6Im1lc2hlcy9hY3RvcndyaWdodC9kZXBlbmRlbmN5LnhtbCIsInhtbFNoYTI1NiI6IjY2YTY3NTcxNTFmOGVlNTVkYjEyNzcxNmM3ZTNkY2UwYmU4MDc0YjY0ZTIwZWRhNTQyZTVjMWU0NmNhOWM0MWUiLCJ4bWxCeXRlTGVuZ3RoIjozfV0sIm1hcHBpbmdBdXRob3JpdHkiOm51bGx9LCJhc3NldHMiOlt7InBhdGgiOiJtZXNoZXMvYWN0b3J3cmlnaHQvYXJjaGl2ZS1kZXBlbmRlbmN5Lm5pZiIsInNoYTI1NiI6IjQzZmNhZDRkMTA3OWIxZGZhZmVmOTIxYmFhN2Q1M2M5ZDBlYmU3NGMzMzVhMjRmMmQ0YTBjNTUwZTA1OWFlNGUiLCJieXRlTGVuZ3RoIjo0LCJwcm92aWRlclBsdWdpbiI6IlByb3ZpZGVyLmVzcCIsInByb3ZpZGVyUGx1Z2luU2hhMjU2IjoiMDM5MDU4YzZmMmMwY2I0OTJjNTMzYjBhNGQxNGVmNzdjYzBmNzhhYmNjY2VkNTI4N2Q4NGExYTIwMTFjZmI4MSIsImFyY2hpdmVNZW1iZXIiOnsiYXJjaGl2ZVBhdGgiOiJPcmNoaWRBZG9ybm1lbnQuYnNhIiwiYXJjaGl2ZVNoYTI1NiI6ImIyMzU0OWRkYTE1NzgwMTUzM2QxZDI3MmRhNWZmODg2ODNiZjFmYmU2ZWU0NmRlYjMwNjZiZjU1ZjdkMDU1MDciLCJhcmNoaXZlQnl0ZUxlbmd0aCI6NCwibWVtYmVyUGF0aCI6Im1lc2hlcy9hY3RvcndyaWdodC9hcmNoaXZlLWRlcGVuZGVuY3kubmlmIiwibWVtYmVyU2hhMjU2IjoiNDNmY2FkNGQxMDc5YjFkZmFmZWY5MjFiYWE3ZDUzYzlkMGViZTc0YzMzNWEyNGYyZDRhMGM1NTBlMDU5YWU0ZSIsIm1lbWJlckJ5dGVMZW5ndGgiOjR9fSx7InBhdGgiOiJtZXNoZXMvYWN0b3J3cmlnaHQvZGVwZW5kZW5jeS5uaWYiLCJzaGEyNTYiOiI3ODdjNzk4ZTM5YTViYzE5MTAzNTViYWU2ZDBjZDg3YTM2YjJlMTBmZDAyMDJhODNlM2JiNmIwMDVkYTgzNDcyIiwiYnl0ZUxlbmd0aCI6MywicHJvdmlkZXJQbHVnaW4iOiJQcm92aWRlci5lc3AiLCJwcm92aWRlclBsdWdpblNoYTI1NiI6IjAzOTA1OGM2ZjJjMGNiNDkyYzUzM2IwYTRkMTRlZjc3Y2MwZjc4YWJjY2NlZDUyODdkODRhMWEyMDExY2ZiODEiLCJhcmNoaXZlTWVtYmVyIjpudWxsfSx7InBhdGgiOiJ0ZXh0dXJlcy9hY3RvcndyaWdodC9hcmNoaXZlLWRlcGVuZGVuY3kuZGRzIiwic2hhMjU2IjoiMGVmODdiYjFlMjI2MDgwMGRjNGRlMDc5ZjQ2M2FhYjZkODA2NTRmMTgyMThmYTRkYzNhMDNkODlkNTYyZTA1MSIsImJ5dGVMZW5ndGgiOjUsInByb3ZpZGVyUGx1Z2luIjoiUHJvdmlkZXIuZXNwIiwicHJvdmlkZXJQbHVnaW5TaGEyNTYiOiIwMzkwNThjNmYyYzBjYjQ5MmM1MzNiMGE0ZDE0ZWY3N2NjMGY3OGFiY2NjZWQ1Mjg3ZDg0YTFhMjAxMWNmYjgxIiwiYXJjaGl2ZU1lbWJlciI6eyJhcmNoaXZlUGF0aCI6Ik9yY2hpZEFkb3JubWVudC5ic2EiLCJhcmNoaXZlU2hhMjU2IjoiYjIzNTQ5ZGRhMTU3ODAxNTMzZDFkMjcyZGE1ZmY4ODY4M2JmMWZiZTZlZTQ2ZGViMzA2NmJmNTVmN2QwNTUwNyIsImFyY2hpdmVCeXRlTGVuZ3RoIjo0LCJtZW1iZXJQYXRoIjoidGV4dHVyZXMvYWN0b3J3cmlnaHQvYXJjaGl2ZS1kZXBlbmRlbmN5LmRkcyIsIm1lbWJlclNoYTI1NiI6IjBlZjg3YmIxZTIyNjA4MDBkYzRkZTA3OWY0NjNhYWI2ZDgwNjU0ZjE4MjE4ZmE0ZGMzYTAzZDg5ZDU2MmUwNTEiLCJtZW1iZXJCeXRlTGVuZ3RoIjo1fX1dLCJydW50aW1lUHJlcmVxdWlzaXRlcyI6W119LA0KICAgICAgImF0dGVzdGF0aW9uIjogeyJzY2hlbWFJZGVudGlmaWVyIjoibnBjLmV4dGVybmFsLWhlYWRwYXJ0LWZhY2VnZW9tLWV4Y2x1c2lvbi52MSIsImF0dGVzdGF0aW9uU2hhMjU2IjoiYjU4Yjk3MDMzNDc5ODYwZmExZmVkNTExMTJlZGI3Y2UzMzhiODYwMGEwMmQ5MGFlYjViYzhmZjUyMDJlMjZmNiIsImRlc2NyaXB0b3JJZCI6IjA3ZmEyYTQ3MmYwN2U4Y2YyOWJkNTRkOGQwZmZjNTI4Yjk5ZWE5MTFlNmFiZjA4OWYyZDZiM2IzZTFlYzIyMzkiLCJvdXRwdXRGYWNlR2VvbVBhdGgiOiJEYXRhL05QQ01hbmFnZXIvRmFjZUdlb20vZXh0ZXJuYWwubmlmIiwib3V0cHV0RmFjZUdlb21TaGEyNTYiOiJiNzE5MWFmZmYzMjNkZmZkYWZiYTc5Y2FhNTJkYjBkOTQ5NGIxNDZmZTA2M2Q4M2VlM2MwZjY3NjNjNTM5MmRhIiwib3V0cHV0RmFjZUdlb21CeXRlTGVuZ3RoIjoyNTYsImluY2x1ZGVkT3JkaW5hcnlTaGFwZXMiOltdLCJleGNsdWRlZFNoYXBlcyI6W3sicHJvdmlkZXJNb2RlbCI6Im1lc2hlcy9hY3RvcndyaWdodC9kZXBlbmRlbmN5Lm5pZiIsInNoYXBlTmFtZSI6Ik9yY2hpZEhhaXIifV0sImV4Y2x1ZGVkTWV0YWRhdGEiOlt7ImtpbmQiOiJwaHlzaWNzLWxvY2F0b3IiLCJwb3J0YWJsZVZhbHVlIjoiSERUIFNraW5uZWQgTWVzaCBQaHlzaWNzIE9iamVjdCJ9XSwidmVyaWZpZXJWZXJzaW9uIjoidGFzazQtdGVzdCJ9LA0KICAgICAgIm91dHB1dFBsdWdpbiI6IHsNCiAgICAgICAgInBsdWdpbiI6ICJOcGNNYW5hZ2VyT3V0cHV0LmVzcCIsDQogICAgICAgICJzaGEyNTYiOiAiZWE4ZjliYjRhMTg1M2VkODYzNmI3MzM4NWFjNGIzNGQwNjhhYWE0OTVmOWE5MzU4MzllODdkY2NhM2U4ZDlhNyIsDQogICAgICAgICJieXRlTGVuZ3RoIjogMTI4LA0KICAgICAgICAibWFzdGVycyI6IFsNCiAgICAgICAgICAiUHJvdmlkZXIuZXNwIg0KICAgICAgICBdLA0KICAgICAgICAicG5hbSI6IFsNCiAgICAgICAgICAiUHJvdmlkZXIuZXNwfDB4MDAwMDA4MDAiDQogICAgICAgIF0NCiAgICAgIH0sDQogICAgICAiZmFjZUdlb20iOiB7DQogICAgICAgICJwYXRoIjogIkRhdGEvTlBDTWFuYWdlci9GYWNlR2VvbS9leHRlcm5hbC5uaWYiLA0KICAgICAgICAic2hhMjU2IjogImI3MTkxYWZmZjMyM2RmZmRhZmJhNzljYWE1MmRiMGQ5NDk0YjE0NmZlMDYzZDgzZWUzYzBmNjc2M2M1MzkyZGEiLA0KICAgICAgICAiYnl0ZUxlbmd0aCI6IDI1Ng0KICAgICAgfQ0KICAgIH0NCiAgXQ0KfQ==";
    private static readonly JsonSerializerOptions MutationJsonOptions = new()
    {
        WriteIndented = true
    };

    private static async Task TestSelectedDependencyManifestSchema()
    {
        string root = Path.Combine(
            Directory.GetCurrentDirectory(),
            "artifacts",
            "selected-dependency-schema-golden");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        try
        {
            var labRoot = new WorkspacePath(Directory.GetCurrentDirectory());
            var policy = new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath(@"F:\ExampleGame"));
            var writer = new RaceMenuSelectedDependencyManifestWriter(policy, labRoot);
            WorkspacePath providerPlugin = Write(root, "Provider.esp", [1, 2, 3]);
            WorkspacePath dependency = Write(root,
                "meshes/actorwright/dependency.nif", [4, 5, 6]);
            WorkspacePath sidecar = Write(root,
                "meshes/actorwright/dependency.xml", [7, 8, 9]);
            Sha256Hash presetHash = Hash([10, 11, 12]);
            Sha256Hash providerHash = HashFile(providerPlugin);
            Sha256Hash dependencyHash = HashFile(dependency);
            Sha256Hash sidecarHash = HashFile(sidecar);
            var reference = new FormReference(
                new PluginName("Provider.esp"), new FormId(0x123));
            var binding = new RaceMenuNpcFormBinding(
                new RecordSignature("HDPT"), reference, reference,
                new PluginName("Provider.esp"), providerPlugin, providerHash,
                NpcHeadPartType.Face);
            var draft = new RaceMenuPresetRecordAuthorityDraft(
                "selected-dependency-schema",
                new PresetDocument(
                    PresetFormat.RaceMenuJslot,
                    GameEdition.SkyrimSpecialEdition,
                    null!,
                    presetHash,
                    []),
                null!,
                null!,
                [new RaceMenuPresetHeadPartAuthority(null!, binding)],
                null!,
                null!,
                0,
                new FormId(0x800),
                null!,
                RuntimeAuthority: false);
            var looseDependency = new SkyrimAssetAuthority(
                "loose", AssetProviderKind.Loose, dependency, dependencyHash,
                new AssetPath("meshes/actorwright/dependency.nif"),
                new FileInfo(dependency.Value).Length, dependencyHash);
            var providerSidecar = new SkyrimAssetAuthority(
                "loose", AssetProviderKind.Loose, sidecar, sidecarHash,
                new AssetPath("meshes/actorwright/dependency.xml"),
                new FileInfo(sidecar.Value).Length, sidecarHash);

            WorkspacePath v1Path = new(Path.Combine(root, "manifest-v1.json"));
            RaceMenuSelectedDependencyManifestWriteResult v1Result =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft, [looseDependency], [], v1Path),
                    CancellationToken.None);
            Assert(v1Result.Written, "The zero-sidecar manifest was refused.");

            WorkspacePath v2Path = new(Path.Combine(root, "manifest-v2.json"));
            RaceMenuSelectedDependencyManifestWriteResult v2Result =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft, [looseDependency], [], v2Path)
                    {
                        ExternalProviderSidecars = [providerSidecar]
                    },
                    CancellationToken.None);
            Assert(v2Result.Written, "The sidecar manifest was refused.");

            using JsonDocument v1 = JsonDocument.Parse(await File.ReadAllBytesAsync(
                v1Path.Value));
            Require(v1.RootElement.GetProperty("schemaVersion").GetInt32() == 1);
            Require(v1.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet()
                .SetEquals(["schemaVersion", "id", "headParts", "looseAssets", "archives"]));
            Require(!v1.RootElement.TryGetProperty("externalProviderSidecars", out _));

            using JsonDocument v2 = JsonDocument.Parse(await File.ReadAllBytesAsync(
                v2Path.Value));
            Require(v2.RootElement.GetProperty("schemaVersion").GetInt32() == 2);
            Require(v2.RootElement.GetProperty("externalProviderSidecars").GetArrayLength() == 1);
            Require(v2.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet()
                .SetEquals(["schemaVersion", "id", "headParts", "looseAssets", "archives",
                    "externalProviderSidecars"]));
            byte[] v1Golden = Convert.FromBase64String(
                "ew0KICAic2NoZW1hVmVyc2lvbiI6IDEsDQogICJpZCI6ICJzZWxlY3RlZC1wcmVzZXQtZGVwZW5kZW5jaWVzLTg4MzRjZjA5YjhmZTRmZjNmMzEwNjc4NiIsDQogICJoZWFkUGFydHMiOiBbDQogICAgew0KICAgICAgImZvcm1LZXkiOiAiUHJvdmlkZXIuZXNwfDAwMDEyMyIsDQogICAgICAidHlwZSI6ICJmYWNlIiwNCiAgICAgICJwbHVnaW5FdmlkZW5jZSI6ICJhcnRpZmFjdHMvc2VsZWN0ZWQtZGVwZW5kZW5jeS1zY2hlbWEtZ29sZGVuL1Byb3ZpZGVyLmVzcCIsDQogICAgICAicGx1Z2luU2hhMjU2IjogIjAzOTA1OGM2ZjJjMGNiNDkyYzUzM2IwYTRkMTRlZjc3Y2MwZjc4YWJjY2NlZDUyODdkODRhMWEyMDExY2ZiODEiDQogICAgfQ0KICBdLA0KICAibG9vc2VBc3NldHMiOiBbDQogICAgew0KICAgICAgImdhbWVQYXRoIjogIm1lc2hlcy9hY3RvcndyaWdodC9kZXBlbmRlbmN5Lm5pZiIsDQogICAgICAicHJvdmlkZXIiOiAibG9vc2UiLA0KICAgICAgImV2aWRlbmNlUGF0aCI6ICJhcnRpZmFjdHMvc2VsZWN0ZWQtZGVwZW5kZW5jeS1zY2hlbWEtZ29sZGVuL21lc2hlcy9hY3RvcndyaWdodC9kZXBlbmRlbmN5Lm5pZiIsDQogICAgICAic2hhMjU2IjogIjc4N2M3OThlMzlhNWJjMTkxMDM1NWJhZTZkMGNkODdhMzZiMmUxMGZkMDIwMmE4M2UzYmI2YjAwNWRhODM0NzIiDQogICAgfQ0KICBdLA0KICAiYXJjaGl2ZXMiOiBbXQ0KfQ==");
            byte[] v2Golden = Convert.FromBase64String(
                "ew0KICAic2NoZW1hVmVyc2lvbiI6IDIsDQogICJpZCI6ICJzZWxlY3RlZC1wcmVzZXQtZGVwZW5kZW5jaWVzLWYxMmIzOGE5YTI4NmRhY2RiZGQxNDU5NiIsDQogICJoZWFkUGFydHMiOiBbDQogICAgew0KICAgICAgImZvcm1LZXkiOiAiUHJvdmlkZXIuZXNwfDAwMDEyMyIsDQogICAgICAidHlwZSI6ICJmYWNlIiwNCiAgICAgICJwbHVnaW5FdmlkZW5jZSI6ICJhcnRpZmFjdHMvc2VsZWN0ZWQtZGVwZW5kZW5jeS1zY2hlbWEtZ29sZGVuL1Byb3ZpZGVyLmVzcCIsDQogICAgICAicGx1Z2luU2hhMjU2IjogIjAzOTA1OGM2ZjJjMGNiNDkyYzUzM2IwYTRkMTRlZjc3Y2MwZjc4YWJjY2NlZDUyODdkODRhMWEyMDExY2ZiODEiDQogICAgfQ0KICBdLA0KICAibG9vc2VBc3NldHMiOiBbDQogICAgew0KICAgICAgImdhbWVQYXRoIjogIm1lc2hlcy9hY3RvcndyaWdodC9kZXBlbmRlbmN5Lm5pZiIsDQogICAgICAicHJvdmlkZXIiOiAibG9vc2UiLA0KICAgICAgImV2aWRlbmNlUGF0aCI6ICJhcnRpZmFjdHMvc2VsZWN0ZWQtZGVwZW5kZW5jeS1zY2hlbWEtZ29sZGVuL21lc2hlcy9hY3RvcndyaWdodC9kZXBlbmRlbmN5Lm5pZiIsDQogICAgICAic2hhMjU2IjogIjc4N2M3OThlMzlhNWJjMTkxMDM1NWJhZTZkMGNkODdhMzZiMmUxMGZkMDIwMmE4M2UzYmI2YjAwNWRhODM0NzIiDQogICAgfQ0KICBdLA0KICAiYXJjaGl2ZXMiOiBbXSwNCiAgImV4dGVybmFsUHJvdmlkZXJTaWRlY2Fyc yI6IFsNCiAgICB7DQogICAgICAiZ2FtZVBhdGgiOiAibWVzaGVzL2FjdG9yd3JpZ2h0L2RlcGVuZGVuY3kueG1sIiwNCiAgICAgICJwcm92aWRlciI6ICJsb29zZSIsDQogICAgICAiZXZpZGVuY2VQYXRoIjogImFydGlmYWN0cy9zZWxlY3RlZC1kZXBlbmRlbmN5LXNjaGVtYS1nb2xkZW4vbWVzaGVzL2FjdG9yd3JpZ2h0L2RlcGVuZGVuY3kueG1sIiwNCiAgICAgICJzaGEyNTYiOiAiNjZhNjc1NzE1MWY4ZWU1NWRiMTI3NzE2YzdlM2RjZTBiZTgwNzRiNjRlMjBlZGE1NDJlNWMxZTQ2Y2E5YzQxZSINCiAgICB9DQogIF0NCn0=");
            Require((await File.ReadAllBytesAsync(v1Path.Value)).SequenceEqual(v1Golden));
            Require((await File.ReadAllBytesAsync(v2Path.Value)).SequenceEqual(v2Golden));

            var overlappingLooseTexture = new RaceMenuNpcExternalTextureAuthority(
                looseDependency.AssetPath,
                "loose",
                dependency,
                dependencyHash,
                dependencyHash);
            WorkspacePath legacyOverlapV1Path = new(Path.Combine(
                root, "manifest-v1-loose-overlap.json"));
            RaceMenuSelectedDependencyManifestWriteResult legacyOverlapV1 =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft, [looseDependency],
                        [overlappingLooseTexture], legacyOverlapV1Path),
                    CancellationToken.None);
            Require(legacyOverlapV1.Written &&
                    (await File.ReadAllBytesAsync(legacyOverlapV1Path.Value))
                        .SequenceEqual(v1Golden));

            WorkspacePath legacyOverlapV2Path = new(Path.Combine(
                root, "manifest-v2-loose-overlap.json"));
            RaceMenuSelectedDependencyManifestWriteResult legacyOverlapV2 =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft, [looseDependency],
                        [overlappingLooseTexture], legacyOverlapV2Path)
                    {
                        ExternalProviderSidecars = [providerSidecar]
                    },
                    CancellationToken.None);
            Require(legacyOverlapV2.Written &&
                    (await File.ReadAllBytesAsync(legacyOverlapV2Path.Value))
                        .SequenceEqual(v2Golden));

            RaceMenuSelectedDependencyManifestWriteResult staleLoose =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft,
                        [looseDependency with
                        {
                            ContentLength = looseDependency.ContentLength + 1
                        }], [],
                        new WorkspacePath(Path.Combine(root, "stale-loose.json"))),
                    CancellationToken.None);
            Require(!staleLoose.Written);
            RaceMenuSelectedDependencyManifestWriteResult staleSidecar =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft, [looseDependency], [],
                        new WorkspacePath(Path.Combine(root, "stale-sidecar.json")))
                    {
                        ExternalProviderSidecars = [providerSidecar with
                        {
                            ContentSha256 = Hash([99, 98, 97])
                        }]
                    },
                    CancellationToken.None);
            Require(!staleSidecar.Written);

            Require(typeof(RaceMenuSelectedDependencyManifestWriteRequest)
                .GetProperty("ExternalInstallDependencies") is not null);

            WorkspacePath archive = Write(root, "OrchidAdornment.bsa",
                [10, 11, 12, 13]);
            Sha256Hash archiveHash = HashFile(archive);
            WorkspacePath archiveDependencyPath = Write(root,
                "meshes/actorwright/archive-dependency.nif", [14, 15, 16, 17]);
            Sha256Hash archiveDependencyHash = HashFile(archiveDependencyPath);
            var archiveDependency = new SkyrimAssetAuthority(
                "OrchidAdornment.bsa",
                AssetProviderKind.Archive,
                archive,
                archiveHash,
                new AssetPath("meshes/actorwright/archive-dependency.nif"),
                new FileInfo(archiveDependencyPath.Value).Length,
                archiveDependencyHash);
            WorkspacePath archiveTexturePath = Write(root,
                "textures/actorwright/archive-dependency.dds",
                [40, 41, 42, 43, 44]);
            Sha256Hash archiveTextureHash = HashFile(archiveTexturePath);
            var archiveTextureDependency = new SkyrimAssetAuthority(
                "OrchidAdornment.bsa",
                AssetProviderKind.Archive,
                archive,
                archiveHash,
                new AssetPath("textures/actorwright/archive-dependency.dds"),
                new FileInfo(archiveTexturePath.Value).Length,
                archiveTextureHash);
            var archiveTexture = new RaceMenuNpcExternalTextureAuthority(
                new AssetPath("textures/actorwright/archive-dependency.dds"),
                "OrchidAdornment.bsa", archive, archiveHash, archiveTextureHash);
            ExternalHeadPartDependencyDescriptor descriptor =
                CreateExternalDescriptor(
                    providerPlugin, providerHash, dependency, dependencyHash,
                    sidecar, sidecarHash, archiveDependencyHash, archiveHash,
                    archive, archiveDependencyPath, archiveTexturePath,
                    archiveTextureHash);
            ExternalHeadPartFaceGeomExclusionAttestation attestation =
                CreateExternalAttestation(descriptor);
            var external = new RaceMenuSelectedDependencyManifestExternalInstallDependency(
                descriptor,
                attestation,
                new RaceMenuSelectedDependencyManifestOutputPluginBinding(
                    new PluginName("NpcManagerOutput.esp"),
                    Hash([18, 19, 20]),
                    128,
                    [new PluginName("Provider.esp")],
                    [new FormReference(
                        new PluginName("Provider.esp"), new FormId(0x800))]),
                attestation.OutputFaceGeomPath,
                attestation.OutputFaceGeomSha256,
                attestation.OutputFaceGeomByteLength);

            string missingSidecarRoot = Path.Combine(root, "schema3-missing-sidecar");
            string missingSidecarEvidence = Path.Combine(
                missingSidecarRoot, "Data", "NPCManager", "Evidence");
            Directory.CreateDirectory(missingSidecarEvidence);
            RaceMenuSelectedDependencyManifestWriteResult missingSidecarResult =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft,
                        [looseDependency, archiveDependency, archiveTextureDependency],
                        [archiveTexture],
                        new WorkspacePath(Path.Combine(
                            missingSidecarEvidence,
                            "selected-preset-dependencies.json")))
                    {
                        ExternalInstallDependencies = [external]
                    },
                    CancellationToken.None);
            Require(!missingSidecarResult.Written);

            WorkspacePath extraSidecarPath = Write(root,
                "meshes/actorwright/extra.xml", [41, 42, 43]);
            var extraSidecar = new SkyrimAssetAuthority(
                "loose", AssetProviderKind.Loose, extraSidecarPath,
                HashFile(extraSidecarPath),
                new AssetPath("meshes/actorwright/extra.xml"),
                new FileInfo(extraSidecarPath.Value).Length,
                HashFile(extraSidecarPath));
            string extraSidecarRoot = Path.Combine(root, "schema3-extra-sidecar");
            string extraSidecarEvidence = Path.Combine(
                extraSidecarRoot, "Data", "NPCManager", "Evidence");
            Directory.CreateDirectory(extraSidecarEvidence);
            RaceMenuSelectedDependencyManifestWriteResult extraSidecarResult =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft,
                        [looseDependency, archiveDependency, archiveTextureDependency],
                        [archiveTexture],
                        new WorkspacePath(Path.Combine(
                            extraSidecarEvidence,
                            "selected-preset-dependencies.json")))
                    {
                        ExternalProviderSidecars = [providerSidecar, extraSidecar],
                        ExternalInstallDependencies = [external]
                    },
                    CancellationToken.None);
            Require(!extraSidecarResult.Written);
            string packageRoot = Path.Combine(root, "schema3-package");
            string evidenceDirectory = Path.Combine(
                packageRoot, "Data", "NPCManager", "Evidence");
            Directory.CreateDirectory(evidenceDirectory);
            WorkspacePath schema3Path = new(Path.Combine(
                evidenceDirectory, "selected-preset-dependencies.json"));
            RaceMenuSelectedDependencyManifestWriteResult schema3Result =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft,
                        [looseDependency, archiveDependency,
                            archiveTextureDependency],
                        [archiveTexture], schema3Path)
                    {
                        ExternalProviderSidecars = [providerSidecar],
                        ExternalInstallDependencies = [external]
                    },
                    CancellationToken.None);
            Require(schema3Result.Written && schema3Result.Artifact is not null);
            RaceMenuSelectedDependencyManifestArtifact schema3Artifact =
                schema3Result.Artifact!;
            Require(schema3Artifact.SchemaVersion == 3 &&
                    schema3Artifact.PresetSha256 == presetHash &&
                    schema3Artifact.DependencyId == ExpectedSchema3DependencyId &&
                    schema3Artifact.ExternalInstallDependencies.Length == 1);
            byte[] schema3Bytes = await File.ReadAllBytesAsync(schema3Path.Value);
            Require(Convert.ToBase64String(schema3Bytes) ==
                    ExpectedSchema3CanonicalBase64);
            using JsonDocument schema3 = JsonDocument.Parse(schema3Bytes);
            Require(schema3.RootElement.GetProperty("schemaVersion").GetInt32() == 3);
            Require(schema3.RootElement.EnumerateObject().Select(p => p.Name)
                .SequenceEqual([
                    "schemaVersion", "id", "presetSha256", "headParts", "looseAssets", "archives",
                    "externalProviderSidecars", "externalInstallDependencies"]));
            Require(schema3.RootElement.GetProperty("presetSha256").GetString() ==
                    presetHash.Value);
            JsonElement schema3Group = schema3.RootElement
                .GetProperty("externalInstallDependencies")[0];
            Require(schema3Group.GetProperty("descriptorId").GetString() ==
                    descriptor.DescriptorId.Value &&
                    schema3Group.GetProperty("attestation")
                        .GetProperty("descriptorId").GetString() ==
                    descriptor.DescriptorId.Value);
            Require(schema3.RootElement.GetProperty("archives")[0]
                .GetProperty("members")[0].GetProperty("byteLength").GetInt64() ==
                archiveDependency.ContentLength);
            Require(schema3.RootElement.GetProperty("looseAssets")[0]
                .GetProperty("byteLength").GetInt64() == looseDependency.ContentLength);
            Require(schema3.RootElement.GetProperty("archives")[0]
                .GetProperty("byteLength").GetInt64() ==
                new FileInfo(archive.Value).Length);
            var reader = new RaceMenuSelectedDependencyManifestReader();
            RaceMenuSelectedDependencyManifestReadResult reopened =
                await reader.ReadAsync(
                    schema3Path,
                    schema3Artifact.ManifestSha256,
                    new WorkspacePath(packageRoot),
                    CancellationToken.None);
            if (reopened.Artifact is null)
                throw new InvalidOperationException(string.Join(
                    " | ", reopened.Diagnostics.Select(item => item.Message)));
            Require(reopened.Artifact is not null &&
                    reopened.Artifact.SchemaVersion == 3 &&
                    reopened.Artifact.PresetSha256 == presetHash &&
                    reopened.Artifact.ExternalInstallDependencies.Length == 1 &&
                    reopened.Artifact.ExternalInstallDependencies[0].Descriptor
                        .DescriptorId == descriptor.DescriptorId &&
                    reopened.Artifact.ExternalInstallDependencies[0].Attestation
                        .AttestationSha256 == attestation.AttestationSha256);
            byte[] reopenedSchema3Bytes = await File.ReadAllBytesAsync(
                schema3Path.Value);
            Require(schema3Bytes.SequenceEqual(reopenedSchema3Bytes) &&
                    schema3Artifact.ManifestSha256 == HashFile(schema3Path));
            string schema3Text = Encoding.UTF8.GetString(schema3Bytes);
            Require(!schema3Text.Contains(
                        Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase));
            Require(Directory.EnumerateFiles(
                        packageRoot, "*", SearchOption.AllDirectories).Count() == 1);

            ExternalHeadPartDependencyDescriptor sharedVanillaDescriptor =
                CreateSharedVanillaDescriptor(descriptor);
            ExternalHeadPartFaceGeomExclusionAttestation sharedVanillaAttestation =
                CreateExternalAttestation(sharedVanillaDescriptor);
            var sharedVanillaExternal = external with
            {
                Descriptor = sharedVanillaDescriptor,
                Attestation = sharedVanillaAttestation,
                OutputPlugin = external.OutputPlugin with
                {
                    Masters = [
                        new PluginName("Provider.esp"),
                        new PluginName("Skyrim.esm")],
                    PnamBindings = sharedVanillaDescriptor.Members
                        .Select(item => item.WinningForm).ToImmutableArray()
                },
                FaceGeomSha256 = sharedVanillaAttestation.OutputFaceGeomSha256,
                FaceGeomByteLength = sharedVanillaAttestation.OutputFaceGeomByteLength
            };
            string sharedVanillaRoot = Path.Combine(
                root, "schema3-shared-vanilla");
            string sharedVanillaEvidence = Path.Combine(
                sharedVanillaRoot, "Data", "NPCManager", "Evidence");
            Directory.CreateDirectory(sharedVanillaEvidence);
            WorkspacePath sharedVanillaPath = new(Path.Combine(
                sharedVanillaEvidence, "selected-preset-dependencies.json"));
            RaceMenuSelectedDependencyManifestWriteResult sharedVanillaWrite =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft,
                        [looseDependency, archiveDependency,
                            archiveTextureDependency],
                        [archiveTexture], sharedVanillaPath)
                    {
                        ExternalProviderSidecars = [providerSidecar],
                        ExternalInstallDependencies = [sharedVanillaExternal]
                    },
                    CancellationToken.None);
            if (!sharedVanillaWrite.Written || sharedVanillaWrite.Artifact is null)
                throw new InvalidOperationException(string.Join(
                    " | ", sharedVanillaWrite.Diagnostics.Select(item => item.Message)));
            RaceMenuSelectedDependencyManifestReadResult sharedVanillaRead =
                await reader.ReadAsync(
                    sharedVanillaPath,
                    sharedVanillaWrite.Artifact!.ManifestSha256,
                    new WorkspacePath(sharedVanillaRoot),
                    CancellationToken.None);
            Require(sharedVanillaRead.Artifact is not null &&
                    sharedVanillaRead.Artifact.ExternalInstallDependencies.Length == 1 &&
                    sharedVanillaRead.Artifact.ExternalInstallDependencies[0]
                        .Descriptor.Members.Count(item =>
                            ExternalHeadPartMemberAuthority.IsSelfOwnedVanillaMember(item)) == 2);
            byte[] sharedVanillaBytes = await File.ReadAllBytesAsync(
                sharedVanillaPath.Value);
            JsonObject sharedVanillaJson = JsonNode.Parse(
                sharedVanillaBytes)!.AsObject();
            JsonObject mixedMember = sharedVanillaJson[
                "externalInstallDependencies"]![0]!["descriptor"]!["members"]![2]!
                .AsObject();
            mixedMember["originForm"] = "Dawnguard.esm|0x000122";
            mixedMember["requiredOutputMaster"] = "Dawnguard.esm";
            mixedMember["winningForm"] = "Dawnguard.esm|0x000122";
            mixedMember["winningPlugin"] = "Dawnguard.esm";
            byte[] mixedManifestBytes = Encoding.UTF8.GetBytes(
                sharedVanillaJson.ToJsonString());
            await File.WriteAllBytesAsync(
                sharedVanillaPath.Value, mixedManifestBytes);
            RaceMenuSelectedDependencyManifestReadResult mixedRead =
                await reader.ReadAsync(
                    sharedVanillaPath,
                    Hash(mixedManifestBytes),
                    new WorkspacePath(sharedVanillaRoot),
                    CancellationToken.None);
            Require(mixedRead.Artifact is null);

            string secondPackageRoot = Path.Combine(root, "schema3-package-2");
            string secondEvidenceDirectory = Path.Combine(
                secondPackageRoot, "Data", "NPCManager", "Evidence");
            Directory.CreateDirectory(secondEvidenceDirectory);
            WorkspacePath secondSchema3Path = new(Path.Combine(
                secondEvidenceDirectory, "selected-preset-dependencies.json"));
            RaceMenuSelectedDependencyManifestWriteResult secondSchema3Result =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft,
                        [looseDependency, archiveDependency,
                            archiveTextureDependency],
                        [archiveTexture], secondSchema3Path)
                    {
                        ExternalProviderSidecars = [providerSidecar],
                        ExternalInstallDependencies = [external]
                    },
                    CancellationToken.None);
            byte[] secondSchema3Bytes = await File.ReadAllBytesAsync(
                secondSchema3Path.Value);
            Require(secondSchema3Result.Written &&
                    secondSchema3Result.Artifact is not null &&
                    secondSchema3Result.Artifact.DependencyId ==
                    schema3Artifact.DependencyId &&
                    schema3Bytes.SequenceEqual(secondSchema3Bytes));

            string schema3LooseOverlapRoot = Path.Combine(
                root, "schema3-loose-overlap");
            string schema3LooseOverlapEvidence = Path.Combine(
                schema3LooseOverlapRoot, "Data", "NPCManager", "Evidence");
            Directory.CreateDirectory(schema3LooseOverlapEvidence);
            WorkspacePath schema3LooseOverlapPath = new(Path.Combine(
                schema3LooseOverlapEvidence,
                "selected-preset-dependencies.json"));
            RaceMenuSelectedDependencyManifestWriteResult schema3LooseOverlap =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash, draft,
                        [looseDependency, archiveDependency,
                            archiveTextureDependency],
                        [overlappingLooseTexture, archiveTexture],
                        schema3LooseOverlapPath)
                    {
                        ExternalProviderSidecars = [providerSidecar],
                        ExternalInstallDependencies = [external]
                    },
                    CancellationToken.None);
            byte[] schema3LooseOverlapBytes = await File.ReadAllBytesAsync(
                schema3LooseOverlapPath.Value);
            Require(schema3LooseOverlap.Written &&
                    schema3Bytes.SequenceEqual(schema3LooseOverlapBytes));

            string archiveOnlySourceRoot = Path.Combine(
                root, "archive-only-source");
            Directory.CreateDirectory(archiveOnlySourceRoot);
            WorkspacePath archiveOnlyNif = Write(
                root,
                "archive-only-source/meshes/actorwright/archive-dependency.nif",
                [14, 15, 16, 17]);
            WorkspacePath archiveOnlyDds = Write(
                root,
                "archive-only-source/textures/actorwright/archive-dependency.dds",
                [40, 41, 42, 43, 44]);
            string archiveOnlyDirectory = Path.Combine(root, "archive-only");
            Directory.CreateDirectory(archiveOnlyDirectory);
            WorkspacePath archiveOnlyPath = new(Path.Combine(
                archiveOnlyDirectory, "OrchidAdornment.bsa"));
            SkyrimBsaBuildResult archiveOnlyBuild =
                await new BethesdaSkyrimBsaService(policy, labRoot).BuildAsync(
                    new SkyrimBsaBuildRequest(
                        new WorkspacePath(archiveOnlySourceRoot),
                        archiveOnlyPath,
                        [
                            new AssetPath("meshes/actorwright/archive-dependency.nif"),
                            new AssetPath("textures/actorwright/archive-dependency.dds")
                        ]),
                    CancellationToken.None);
            Require(archiveOnlyBuild.Written && archiveOnlyBuild.Artifact is not null);
            Sha256Hash archiveOnlyHash = HashFile(archiveOnlyPath);
            Sha256Hash archiveOnlyNifHash = HashFile(archiveOnlyNif);
            Sha256Hash archiveOnlyDdsHash = HashFile(archiveOnlyDds);
            var archiveOnlyDependency = new SkyrimAssetAuthority(
                "OrchidAdornment.bsa",
                AssetProviderKind.Archive,
                archiveOnlyPath,
                archiveOnlyHash,
                new AssetPath("meshes/actorwright/archive-dependency.nif"),
                new FileInfo(archiveOnlyNif.Value).Length,
                archiveOnlyNifHash);
            var archiveOnlyTexture = new RaceMenuNpcExternalTextureAuthority(
                new AssetPath("textures/actorwright/archive-dependency.dds"),
                "OrchidAdornment.bsa",
                archiveOnlyPath,
                archiveOnlyHash,
                archiveOnlyDdsHash);
            ExternalHeadPartDependencyDescriptor archiveOnlyDescriptor =
                CreateExternalDescriptor(
                    providerPlugin, providerHash, dependency,
                    dependencyHash, sidecar, sidecarHash,
                    archiveOnlyNifHash, archiveOnlyHash, archiveOnlyPath,
                    archiveOnlyNif, archiveOnlyDds, archiveOnlyDdsHash);
            ExternalHeadPartFaceGeomExclusionAttestation archiveOnlyAttestation =
                CreateExternalAttestation(archiveOnlyDescriptor);
            var archiveOnlyExternal =
                new RaceMenuSelectedDependencyManifestExternalInstallDependency(
                    archiveOnlyDescriptor,
                    archiveOnlyAttestation,
                    external.OutputPlugin,
                    archiveOnlyAttestation.OutputFaceGeomPath,
                    archiveOnlyAttestation.OutputFaceGeomSha256,
                    archiveOnlyAttestation.OutputFaceGeomByteLength);
            string archiveOnlyPackageRoot = Path.Combine(
                root, "archive-only-package");
            string archiveOnlyEvidence = Path.Combine(
                archiveOnlyPackageRoot, "Data", "NPCManager", "Evidence");
            Directory.CreateDirectory(archiveOnlyEvidence);
            WorkspacePath archiveOnlyManifestPath = new(Path.Combine(
                archiveOnlyEvidence, "selected-preset-dependencies.json"));
            RaceMenuSelectedDependencyManifestWriteResult archiveOnlyResult =
                await writer.WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash,
                        draft,
                        [looseDependency, archiveOnlyDependency],
                        [archiveOnlyTexture],
                        archiveOnlyManifestPath)
                    {
                        ExternalProviderSidecars = [providerSidecar],
                        ExternalInstallDependencies = [archiveOnlyExternal]
                    },
                    CancellationToken.None);
            Require(archiveOnlyResult.Written &&
                    archiveOnlyResult.Artifact is not null);
            using JsonDocument archiveOnlyDocument = JsonDocument.Parse(
                await File.ReadAllBytesAsync(archiveOnlyManifestPath.Value));
            Require(archiveOnlyDocument.RootElement.GetProperty("archives")[0]
                .GetProperty("members").EnumerateArray()
                .Single(item => string.Equals(
                    item.GetProperty("gamePath").GetString(),
                    archiveOnlyTexture.DataRelativePath.Value,
                    StringComparison.OrdinalIgnoreCase))
                .GetProperty("byteLength").GetInt64() ==
                new FileInfo(archiveOnlyDds.Value).Length);

            var commaMasterGroup = external with
            {
                OutputPlugin = external.OutputPlugin with
                {
                    Masters = [
                        new PluginName("Provider.esp"),
                        new PluginName("A.esp,B.esp")]
                }
            };
            var splitMasterGroup = external with
            {
                OutputPlugin = external.OutputPlugin with
                {
                    Masters = [
                        new PluginName("Provider.esp"),
                        new PluginName("A.esp"),
                        new PluginName("B.esp")]
                }
            };
            RaceMenuSelectedDependencyManifestWriteResult commaMasterResult =
                await WriteSchema3VariantAsync(
                    writer, root, "schema3-comma-master", presetHash, draft,
                    looseDependency, archiveDependency, archiveTextureDependency,
                    archiveTexture, providerSidecar,
                    commaMasterGroup);
            RaceMenuSelectedDependencyManifestWriteResult splitMasterResult =
                await WriteSchema3VariantAsync(
                    writer, root, "schema3-split-master", presetHash, draft,
                    looseDependency, archiveDependency, archiveTextureDependency,
                    archiveTexture, providerSidecar,
                    splitMasterGroup);
            Require(commaMasterResult.Written && splitMasterResult.Written &&
                    commaMasterResult.Artifact is not null &&
                    splitMasterResult.Artifact is not null &&
                    commaMasterResult.Artifact.DependencyId !=
                    splitMasterResult.Artifact.DependencyId);

            WorkspacePath alternatePath = Write(
                packageRoot,
                "Data/NPCManager/Evidence/alternate.json",
                schema3Bytes);
            RaceMenuSelectedDependencyManifestReadResult alternateLocator =
                await reader.ReadAsync(
                    alternatePath,
                    schema3Artifact.ManifestSha256,
                    new WorkspacePath(packageRoot),
                    CancellationToken.None);
            Require(alternateLocator.Artifact is null);

            string archiveEvidencePath = schema3.RootElement
                .GetProperty("archives")[0].GetProperty("evidencePath")
                .GetString()!;
            await RequireReaderAcceptsRawAsync(
                reader, root, schema3Bytes, "alternate-evidence-path",
                json => json.Replace(
                    $"\"evidencePath\": \"{archiveEvidencePath}\"",
                    "\"evidencePath\": \"provenance/alternate-provider.bsa\"",
                    StringComparison.Ordinal));
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "unknown-member",
                manifest =>
                {
                    JsonObject member = manifest["archives"]![0]!
                        .AsObject()["members"]![0]!.AsObject();
                    member["gamePath"] = "meshes/actorwright/unknown.nif";
                });
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "archive-hash-mutation",
                manifest =>
                {
                    JsonObject member = manifest["archives"]![0]!
                        .AsObject()["members"]![0]!.AsObject();
                    member["sha256"] = new string('0', 64);
                });
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "archive-length-mutation",
                manifest =>
                {
                    JsonObject member = manifest["archives"]![0]!
                        .AsObject()["members"]![0]!.AsObject();
                    member["byteLength"] = 999;
                });
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "loose-length-mutation",
                manifest => manifest["looseAssets"]![0]!
                    .AsObject()["byteLength"] = 999);
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "archive-provider-length-mutation",
                manifest => manifest["archives"]![0]!
                    .AsObject()["byteLength"] = 999);
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "preset-hash-mutation",
                manifest => manifest["presetSha256"] = new string('0', 64));
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "headpart-type-mutation",
                manifest => manifest["headParts"]![0]!
                    .AsObject()["type"] = "hair");
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "wrong-descriptor-id",
                manifest => manifest["externalInstallDependencies"]![0]!
                    .AsObject()["descriptorId"] = new string('0', 64));
            await RequireReaderRefusesRawAsync(
                reader, root, schema3Bytes, "wrong-attestation-id",
                json => ReplaceAttestationDescriptorId(
                    json, descriptor.DescriptorId.Value));
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "missing-output-master",
                manifest => manifest["externalInstallDependencies"]![0]!
                    .AsObject()["outputPlugin"]!.AsObject()["masters"] =
                    new JsonArray());
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "duplicate-asset",
                manifest =>
                {
                    JsonArray loose = manifest["looseAssets"]!.AsArray();
                    JsonObject duplicate = JsonNode.Parse(
                        loose[0]!.ToJsonString())!.AsObject();
                    duplicate["gamePath"] = duplicate["gamePath"]!
                        .GetValue<string>().ToUpperInvariant();
                    loose.Add(duplicate);
                });
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "unknown-top-level-member",
                manifest => manifest["unknownMember"] = true);
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "unknown-external-member",
                manifest => manifest["externalInstallDependencies"]![0]!
                    .AsObject()["unknownMember"] = true);
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "unknown-output-plugin-member",
                manifest => manifest["externalInstallDependencies"]![0]!
                    .AsObject()["outputPlugin"]!.AsObject()["unknownMember"] = true);
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "unknown-facegeom-member",
                manifest => manifest["externalInstallDependencies"]![0]!
                    .AsObject()["faceGeom"]!.AsObject()["unknownMember"] = true);
            await RequireReaderRefusesRawAsync(
                reader, root, schema3Bytes, "unknown-descriptor-member",
                json => AddUnknownNestedMember(json, "descriptor"));
            await RequireReaderRefusesRawAsync(
                reader, root, schema3Bytes, "unknown-attestation-member",
                json => AddUnknownNestedMember(json, "attestation"));
            await RequireReaderRefusesRawAsync(
                reader, root, schema3Bytes, "empty-descriptor-assets",
                ReplaceDescriptorAssetsWithEmpty);
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "unsupported-asset-path",
                manifest => manifest["looseAssets"]![0]!
                    .AsObject()["gamePath"] = "scripts/unsupported.txt");
            await RequireReaderRefusesRawAsync(
                reader, root, schema3Bytes, "noncanonical-whitespace",
                json => json.Replace("\r\n", "\n",
                    StringComparison.Ordinal));
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "host-facegeom-path",
                manifest => manifest["externalInstallDependencies"]![0]!
                    .AsObject()["faceGeom"]!.AsObject()["path"] =
                    "C:/absolute/external.nif");
            await RequireReaderRefusesAsync(
                reader, root, schema3Bytes, "traversal-facegeom-path",
                manifest => manifest["externalInstallDependencies"]![0]!
                    .AsObject()["faceGeom"]!.AsObject()["path"] =
                    "../escape.nif");
            RaceMenuSelectedDependencyManifestReadResult wrongHash =
                await reader.ReadAsync(
                    schema3Path, Hash([21, 22, 23]), new WorkspacePath(packageRoot),
                    CancellationToken.None);
            Require(wrongHash.Artifact is null);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<
        RaceMenuSelectedDependencyManifestWriteResult>
        WriteSchema3VariantAsync(
            RaceMenuSelectedDependencyManifestWriter writer,
            string root,
            string label,
            Sha256Hash presetHash,
            RaceMenuPresetRecordAuthorityDraft draft,
            SkyrimAssetAuthority looseDependency,
            SkyrimAssetAuthority archiveDependency,
            SkyrimAssetAuthority archiveTextureDependency,
            RaceMenuNpcExternalTextureAuthority archiveTexture,
            SkyrimAssetAuthority providerSidecar,
            RaceMenuSelectedDependencyManifestExternalInstallDependency external)
    {
        string packageRoot = Path.Combine(root, label);
        string evidenceDirectory = Path.Combine(
            packageRoot, "Data", "NPCManager", "Evidence");
        Directory.CreateDirectory(evidenceDirectory);
        return await writer.WriteAsync(
            new RaceMenuSelectedDependencyManifestWriteRequest(
                presetHash, draft,
                [looseDependency, archiveDependency, archiveTextureDependency],
                [archiveTexture],
                new WorkspacePath(Path.Combine(
                    evidenceDirectory,
                    "selected-preset-dependencies.json")))
            {
                ExternalProviderSidecars = [providerSidecar],
                ExternalInstallDependencies = [external]
            },
            CancellationToken.None);
    }

    private static async Task RequireReaderRefusesAsync(
        RaceMenuSelectedDependencyManifestReader reader,
        string root,
        byte[] baseline,
        string label,
        Action<JsonObject> mutate)
    {
        RaceMenuSelectedDependencyManifestReadResult result =
            await ReadMutatedAsync(reader, root, baseline, label, mutate);
        Require(result.Artifact is null);
    }

    private static string ReplaceAttestationDescriptorId(
        string json, string descriptorId)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        string raw = document.RootElement
            .GetProperty("externalInstallDependencies")[0]
            .GetProperty("attestation").GetRawText();
        string replacement = new string('0', 64);
        string changedRaw = raw.Replace(
            $"\"descriptorId\": \"{descriptorId}\"",
            $"\"descriptorId\": \"{replacement}\"",
            StringComparison.Ordinal);
        if (changedRaw == raw)
        {
            changedRaw = raw.Replace(
                $"\"descriptorId\":\"{descriptorId}\"",
                $"\"descriptorId\":\"{replacement}\"",
                StringComparison.Ordinal);
        }
        Require(changedRaw != raw);
        string changed = json.Replace(raw, changedRaw, StringComparison.Ordinal);
        Require(changed != json);
        return changed;
    }

    private static string AddUnknownNestedMember(string json, string property)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        string raw = document.RootElement
            .GetProperty("externalInstallDependencies")[0]
            .GetProperty(property).GetRawText();
        int close = raw.LastIndexOf('}');
        Require(close > 0);
        string changedRaw = raw.Insert(close, ",\"unknownMember\":true");
        string changed = json.Replace(raw, changedRaw, StringComparison.Ordinal);
        Require(changed != json);
        return changed;
    }

    private static string ReplaceDescriptorAssetsWithEmpty(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        string raw = document.RootElement
            .GetProperty("externalInstallDependencies")[0]
            .GetProperty("descriptor").GetRawText();
        JsonObject descriptor = JsonNode.Parse(raw)!.AsObject();
        descriptor["assets"] = new JsonArray();
        string changedRaw = descriptor.ToJsonString();
        string changed = json.Replace(raw, changedRaw, StringComparison.Ordinal);
        Require(changed != json);
        return changed;
    }

    private static async Task RequireReaderAcceptsRawAsync(
        RaceMenuSelectedDependencyManifestReader reader,
        string root,
        byte[] baseline,
        string label,
        Func<string, string> mutate)
    {
        string json = Encoding.UTF8.GetString(baseline);
        byte[] bytes = Encoding.UTF8.GetBytes(mutate(json));
        RaceMenuSelectedDependencyManifestReadResult result =
            await ReadBytesAsync(reader, root, bytes, label);
        if (result.Artifact is null)
            throw new InvalidOperationException(string.Join(
                " | ", result.Diagnostics.Select(item => item.Message)));
    }

    private static async Task RequireReaderRefusesRawAsync(
        RaceMenuSelectedDependencyManifestReader reader,
        string root,
        byte[] baseline,
        string label,
        Func<string, string> mutate)
    {
        string json = Encoding.UTF8.GetString(baseline);
        byte[] bytes = Encoding.UTF8.GetBytes(mutate(json));
        RaceMenuSelectedDependencyManifestReadResult result =
            await ReadBytesAsync(reader, root, bytes, label);
        Require(result.Artifact is null);
    }

    private static async Task<
        RaceMenuSelectedDependencyManifestReadResult> ReadMutatedAsync(
        RaceMenuSelectedDependencyManifestReader reader,
        string root,
        byte[] baseline,
        string label,
        Action<JsonObject> mutate)
    {
        JsonObject manifest = JsonNode.Parse(baseline)!.AsObject();
        mutate(manifest);
        byte[] bytes = SerializeMutation(manifest, baseline);
        return await ReadBytesAsync(reader, root, bytes, label);
    }

    private static byte[] SerializeMutation(
        JsonObject manifest, byte[] baseline)
    {
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            manifest,
            MutationJsonOptions);
        using JsonDocument original = JsonDocument.Parse(baseline);
        using JsonDocument changed = JsonDocument.Parse(serialized);
        JsonElement originalGroup = original.RootElement
            .GetProperty("externalInstallDependencies")[0];
        string descriptor = originalGroup.GetProperty("descriptor").GetRawText();
        string attestation = originalGroup.GetProperty("attestation").GetRawText();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in changed.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.Name == "externalInstallDependencies")
                {
                    writer.WriteStartArray();
                    foreach (JsonElement group in property.Value.EnumerateArray())
                    {
                        writer.WriteStartObject();
                        foreach (JsonProperty groupProperty in group.EnumerateObject())
                        {
                            writer.WritePropertyName(groupProperty.Name);
                            if (groupProperty.Name == "descriptor")
                                writer.WriteRawValue(descriptor, true);
                            else if (groupProperty.Name == "attestation")
                                writer.WriteRawValue(attestation, true);
                            else
                                groupProperty.Value.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                    property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static async Task<
        RaceMenuSelectedDependencyManifestReadResult> ReadBytesAsync(
        RaceMenuSelectedDependencyManifestReader reader,
        string root,
        byte[] bytes,
        string label)
    {
        string packageRoot = Path.Combine(root, "mutations", label);
        string evidenceDirectory = Path.Combine(
            packageRoot, "Data", "NPCManager", "Evidence");
        Directory.CreateDirectory(evidenceDirectory);
        WorkspacePath path = new(Path.Combine(
            evidenceDirectory, "selected-preset-dependencies.json"));
        await File.WriteAllBytesAsync(path.Value, bytes);
        return await reader.ReadAsync(
            path,
            HashFile(path),
            new WorkspacePath(packageRoot),
            CancellationToken.None);
    }

    private static WorkspacePath Write(string root, string relative, byte[] bytes)
    {
        string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return new WorkspacePath(path);
    }

    private static Sha256Hash HashFile(WorkspacePath path) =>
        new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path.Value))));

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static ExternalHeadPartDependencyDescriptor CreateExternalDescriptor(
        WorkspacePath providerPlugin,
        Sha256Hash providerHash,
        WorkspacePath dependency,
        Sha256Hash dependencyHash,
        WorkspacePath sidecar,
        Sha256Hash sidecarHash,
        Sha256Hash archiveMemberHash,
        Sha256Hash archiveHash,
        WorkspacePath archiveProvider,
        WorkspacePath archiveDependency,
        WorkspacePath? archiveTextureDependency,
        Sha256Hash? archiveTextureHash)
    {
        var provider = new PluginName("Provider.esp");
        var root = new FormReference(provider, new FormId(0x800));
        var model = new AssetPath("meshes/actorwright/dependency.nif");
        var xml = new AssetPath("meshes/actorwright/dependency.xml");
        var archivePath = new AssetPath("OrchidAdornment.bsa");
        var archiveMemberPath = new AssetPath(
            "meshes/actorwright/archive-dependency.nif");
        var member = new ExternalHeadPartRecordDependency(
            root,
            provider,
            root,
            provider,
            providerHash,
            new FileInfo(providerPlugin.Value).Length,
            Hash([24, 25, 26]),
            "OrchidHair",
            NpcHeadPartType.Hair,
            NpcHeadPartType.Hair,
            model,
            [],
            [],
            null,
            0,
            0,
            NpcSex.Male,
            null);
        var physics = new ExternalHeadPartPhysicsBinding(
            ExternalHeadPartPhysicsBindingMode.DirectNifExtraData,
            [new ExternalHeadPartPhysicsShapeBinding(
                root,
                model,
                "OrchidHair",
                xml,
                sidecarHash,
                new FileInfo(sidecar.Value).Length)],
            null);
        var assets = new List<ExternalHeadPartAssetDependency>
        {
            new ExternalHeadPartAssetDependency(
                model,
                dependencyHash,
                new FileInfo(dependency.Value).Length,
                provider,
                providerHash,
                null),
            new ExternalHeadPartAssetDependency(
                archiveMemberPath,
                archiveMemberHash,
                new FileInfo(archiveDependency.Value).Length,
                provider,
                providerHash,
                new ExternalHeadPartArchiveMemberAuthority(
                    archivePath,
                    archiveHash,
                    new FileInfo(archiveProvider.Value).Length,
                    archiveMemberPath,
                    archiveMemberHash,
                    new FileInfo(archiveDependency.Value).Length))
        };
        if (archiveTextureDependency is { } texturePath &&
            archiveTextureHash is { } textureHash)
        {
            var textureAsset = new AssetPath(
                "textures/actorwright/archive-dependency.dds");
            assets.Add(new ExternalHeadPartAssetDependency(
                textureAsset,
                textureHash,
                new FileInfo(texturePath.Value).Length,
                provider,
                providerHash,
                new ExternalHeadPartArchiveMemberAuthority(
                    archivePath,
                    archiveHash,
                    new FileInfo(archiveProvider.Value).Length,
                    textureAsset,
                    textureHash,
                    new FileInfo(texturePath.Value).Length)));
        }
        var draft = new ExternalHeadPartDependencyDescriptor(
            ExternalHeadPartSchemaIdentifiers.Descriptor,
            Hash([27, 28, 29]),
            ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
            root,
            root,
            NpcHeadPartType.Hair,
            Hash([30, 31, 32]),
            new ExternalHeadPartProviderIdentity(
                provider,
                providerHash,
                new FileInfo(providerPlugin.Value).Length,
                ExternalHeadPartRedistributionMode.ExternalProviderRequired),
            [member],
            physics,
            assets.ToImmutableArray(),
            []);
        return draft with
        {
            DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(draft)
        };
    }

    private static ExternalHeadPartDependencyDescriptor
        CreateSharedVanillaDescriptor(
            ExternalHeadPartDependencyDescriptor descriptor)
    {
        var vanilla = new PluginName("Skyrim.esm");
        Sha256Hash vanillaHash = Hash([91, 92, 93]);
        var firstForm = new FormReference(vanilla, new FormId(0x900));
        var secondForm = new FormReference(vanilla, new FormId(0x901));
        ExternalHeadPartRecordDependency first = descriptor.Members[0] with
        {
            OriginForm = firstForm,
            RequiredOutputMaster = vanilla,
            WinningForm = firstForm,
            WinningPlugin = vanilla,
            WinningPluginSha256 = vanillaHash,
            WinningPluginByteLength = 64,
            Parent = descriptor.RootSourceForm,
            Depth = 1,
            RouteOrder = 1,
            HnamEdges = []
        };
        ExternalHeadPartRecordDependency second = first with
        {
            OriginForm = secondForm,
            WinningForm = secondForm,
            RouteOrder = 2
        };
        ExternalHeadPartRecordDependency root = descriptor.Members[0] with
        {
            HnamEdges = [firstForm, secondForm]
        };
        AssetPath sharedPath = first.ModelNif!.Value;
        ImmutableArray<ExternalHeadPartAssetDependency> assets =
            descriptor.Assets.Select(asset => asset.Path == sharedPath
                ? asset with
                {
                    ProviderPlugin = vanilla,
                    ProviderPluginSha256 = vanillaHash
                }
                : asset).ToImmutableArray();
        ExternalHeadPartPhysicsBinding physics = descriptor.Physics with
        {
            Shapes = descriptor.Physics.Shapes.Select(shape => shape with
            {
                MemberForm = firstForm
            }).ToImmutableArray()
        };
        ExternalHeadPartDependencyDescriptor draft = descriptor with
        {
            Members = [root, first, second],
            Assets = assets,
            Physics = physics,
            DescriptorId = default
        };
        return draft with
        {
            DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(draft)
        };
    }

    private static ExternalHeadPartFaceGeomExclusionAttestation
        CreateExternalAttestation(
            ExternalHeadPartDependencyDescriptor descriptor)
    {
        var attestation = new ExternalHeadPartFaceGeomExclusionAttestation(
            ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
            Hash([33, 34, 35]),
            descriptor.DescriptorId,
            new AssetPath("Data/NPCManager/FaceGeom/external.nif"),
            Hash([36, 37, 38]),
            256,
            [],
            [new ExternalHeadPartExcludedShapeEvidence(
                new AssetPath("meshes/actorwright/dependency.nif"),
                "OrchidHair")],
            [new ExternalHeadPartExcludedMetadataEvidence(
                "physics-locator", "HDT Skinned Mesh Physics Object")],
            "task4-test");
        return attestation with
        {
            AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                .ComputeAttestationHash(attestation)
        };
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Selected dependency manifest schema changed.");
    }
}
