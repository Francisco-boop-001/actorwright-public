using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal sealed class Preview254ExternalSmpContractScenario :
    IPreview254ExternalSmpArchitectureScenario
{
    public string Selector => "--test-external-headpart-contracts";

    public ValueTask RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ExternalHeadPartDependencyContractTests.Run();
        return ValueTask.CompletedTask;
    }
}

internal static class ExternalHeadPartDependencyContractTests
{
    private static readonly PluginName ProviderPlugin =
        new("OrchidAdornment.esp");
    private static readonly PluginName OutputMaster =
        new("OrchidAdornment.esp");
    private static readonly FormReference RootForm =
        new(ProviderPlugin, new FormId(0x800));
    private static readonly FormReference ChildForm =
        new(ProviderPlugin, new FormId(0x801));

    public static void Run()
    {
        var descriptor = CreateDescriptor();
        var descriptorId = ExternalHeadPartDependencyDescriptorCodec
            .ComputeDescriptorId(descriptor);
        descriptor = descriptor with { DescriptorId = descriptorId };

        byte[] first = ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor);
        var parsed = ExternalHeadPartDependencyDescriptorCodec
            .ParseDescriptor(first);
        byte[] second = ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(parsed);

        Require(first.SequenceEqual(second),
            "Descriptor bytes were not stable across a parse/serialize round trip.");
        Require(parsed.DescriptorId == descriptorId && descriptorId.Value.Length == 64,
            "Descriptor ID was not canonical and non-empty.");
        Require(parsed.Members.Select(member => member.RouteOrder)
            .SequenceEqual([0, 1]), "HNAM member route order changed.");
        Require(parsed.Members[0].HnamEdges.SequenceEqual([ChildForm]),
            "HNAM edge order changed.");
        Require(Convert.ToHexString(SHA256.HashData(first)) == "E45C57C3E814B3AF7B39B5D4DC8A67F7994DFBF746C3FEBD7B9CCCF6EEA7129A",
            "Optional physics fields changed retained canonical descriptor bytes.");
        AssertInheritedPhysicsEvidence(descriptor);

        AssertIdentityMutationsChangeId(descriptor);
        AssertAttestationIsIndependent(descriptor);
        AssertClosedWires(first);
        AssertRefusals(descriptor, first);
        AssertPortableTokenBoundaries(descriptor);
        AssertInstallArtifacts(descriptorId);

        string wire = Encoding.UTF8.GetString(first);
        Require(!wire.Contains("K:\\", StringComparison.OrdinalIgnoreCase) &&
                !wire.Contains("worktree", StringComparison.OrdinalIgnoreCase) &&
                !wire.Contains("2026-08-20", StringComparison.Ordinal) &&
                !wire.Contains("install-state", StringComparison.OrdinalIgnoreCase),
            "Portable descriptor serialized environment or install-state data.");
    }

    private static void AssertInheritedPhysicsEvidence(ExternalHeadPartDependencyDescriptor descriptor)
    {
        var root = descriptor.Physics.Shapes.Single(shape => shape.MemberForm == RootForm) with
        { Origin = ExternalHeadPartPhysicsBindingOrigin.Direct };
        var inherited = descriptor.Physics.Shapes.Single(shape => shape.MemberForm == ChildForm) with
        {
            XmlPath = root.XmlPath, XmlSha256 = root.XmlSha256, XmlByteLength = root.XmlByteLength,
            Origin = ExternalHeadPartPhysicsBindingOrigin.InheritedFromRoot,
            InheritedFromRoot = new(root.MemberForm, root.ModelNif, root.ShapeName)
        };
        var reviewed = descriptor with
        {
            PhysicsBinding = ExternalHeadPartPhysicsBindingDisposition.InheritRoot,
            Physics = descriptor.Physics with { Shapes = [root, inherited] }
        };
        reviewed = reviewed with { DescriptorId = ExternalHeadPartDependencyDescriptorCodec.ComputeDescriptorId(reviewed) };
        var bytes = ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(reviewed);
        var parsed = ExternalHeadPartDependencyDescriptorCodec.ParseDescriptor(bytes);
        Require(parsed.PhysicsBinding == ExternalHeadPartPhysicsBindingDisposition.InheritRoot &&
                parsed.Physics.Shapes.Single(shape => shape.MemberForm == ChildForm) == inherited,
            "Reviewed inherit-root disposition and exact root provenance were lost from canonical descriptor bytes.");
        Require(parsed.DescriptorId != descriptor.DescriptorId,
            "Inherited physics evidence did not bind descriptor identity.");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec.ParseDescriptor(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("inherit-root", "inherit-global", StringComparison.Ordinal))),
            "Unknown physics inheritance disposition must refuse.");
        RequireDescriptorRefusal(reviewed with { Physics = reviewed.Physics with
            { Shapes = [root, inherited with { InheritedFromRoot = new(root.MemberForm, root.ModelNif, "missing-root") }] } },
            "Missing root shape must refuse inherited physics evidence.");
        RequireDescriptorRefusal(reviewed with { Physics = reviewed.Physics with
            { Shapes = [root, inherited with { XmlSha256 = Hash("different-xml") }] } },
            "Inherited physics hash drift must refuse.");
    }

    internal static ExternalHeadPartDependencyDescriptor CreateDescriptor()
    {
        var childModel = new AssetPath(
            "meshes/actors/character/character assets/hair/orchid-child.nif");
        var rootModel = new AssetPath(
            "meshes/actors/character/character assets/hair/orchid-root.nif");
        var childXml = new AssetPath(
            "SKSE/Plugins/hdtSkinnedMeshConfigs/orchid-child.xml");
        var rootXml = new AssetPath(
            "SKSE/Plugins/hdtSkinnedMeshConfigs/orchid-root.xml");
        var archivePath = new AssetPath("OrchidAdornment.bsa");
        var archiveMember = new AssetPath(
            "meshes/actors/character/character assets/hair/orchid-child.nif");

        var child = new ExternalHeadPartRecordDependency(
            ChildForm,
            OutputMaster,
            ChildForm,
            ProviderPlugin,
            Hash("provider-plugin"),
            1120,
            Hash("child-record"),
            "OrchidChildHair",
            NpcHeadPartType.Hair,
            NpcHeadPartType.Hair,
            childModel,
            [
                new SkyrimHdptTriRoute(
                    SkyrimHdptTriRole.RaceMorph,
                    new AssetPath(
                        "meshes/actors/character/character assets/hair/orchid-child.tri")),
                new SkyrimHdptTriRoute(
                    SkyrimHdptTriRole.Mesh,
                    childModel)
            ],
            [],
            RootForm,
            1,
            1,
            NpcSex.Male,
            new FormReference(new PluginName("Skyrim.esm"), new FormId(0x13746)));

        var root = new ExternalHeadPartRecordDependency(
            RootForm,
            OutputMaster,
            RootForm,
            ProviderPlugin,
            Hash("provider-plugin"),
            1120,
            Hash("root-record"),
            "OrchidRootHair",
            NpcHeadPartType.Hair,
            NpcHeadPartType.Hair,
            rootModel,
            [
                new SkyrimHdptTriRoute(
                    SkyrimHdptTriRole.RaceMorph,
                    new AssetPath(
                        "meshes/actors/character/character assets/hair/orchid-root.tri")),
                new SkyrimHdptTriRoute(SkyrimHdptTriRole.Mesh, rootModel)
            ],
            [ChildForm],
            null,
            0,
            0,
            NpcSex.Male,
            null);

        var physics = new ExternalHeadPartPhysicsBinding(
            ExternalHeadPartPhysicsBindingMode.DirectNifExtraData,
            [
                new ExternalHeadPartPhysicsShapeBinding(
                    ChildForm,
                    childModel,
                    "OrchidChild",
                    childXml,
                    Hash("child-xml"),
                    230),
                new ExternalHeadPartPhysicsShapeBinding(
                    RootForm,
                    rootModel,
                    "OrchidRoot",
                    rootXml,
                    Hash("root-xml"),
                    231)
            ],
            null);

        var assets = ImmutableArray.Create(
            new ExternalHeadPartAssetDependency(
                new AssetPath(
                    "textures/actors/character/hair/orchid-child.dds"),
                Hash("child-dds"),
                2048,
                ProviderPlugin,
                Hash("provider-plugin"),
                null),
            new ExternalHeadPartAssetDependency(
                childModel,
                Hash("child-nif"),
                4096,
                ProviderPlugin,
                Hash("provider-plugin"),
                new ExternalHeadPartArchiveMemberAuthority(
                    archivePath,
                    Hash("archive"),
                    8192,
                    archiveMember,
                    Hash("child-nif"),
                    4096)),
            new ExternalHeadPartAssetDependency(
                new AssetPath(
                    "meshes/actors/character/character assets/hair/orchid-child.tri"),
                Hash("child-tri"),
                1024,
                ProviderPlugin,
                Hash("provider-plugin"),
                null));

        return new ExternalHeadPartDependencyDescriptor(
            ExternalHeadPartSchemaIdentifiers.Descriptor,
            Hash("placeholder"),
            ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
            RootForm,
            RootForm,
            NpcHeadPartType.Hair,
            Hash("graph"),
            new ExternalHeadPartProviderIdentity(
                ProviderPlugin,
                Hash("provider-plugin"),
                1120,
                ExternalHeadPartRedistributionMode.ExternalProviderRequired),
            [root, child],
            physics,
            assets,
            [
                new ExternalHeadPartRuntimePrerequisite(
                    "skse-plugin",
                    "FSMP present",
                    "SKSE/Plugins/hdtSMP64.dll"),
                new ExternalHeadPartRuntimePrerequisite(
                    "skeleton",
                    "SMP-compatible skeleton",
                    "skeleton.nif")
            ]);
    }

    private static void AssertIdentityMutationsChangeId(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        var mutations = new (string Name,
            Func<ExternalHeadPartDependencyDescriptor,
                ExternalHeadPartDependencyDescriptor> Mutate)[]
        {
            ("provider name", value => value with
            {
                Provider = value.Provider with
                {
                    Plugin = new PluginName("OtherProvider.esp")
                }
            }),
            ("provider hash", value => value with
            {
                Provider = value.Provider with
                {
                    PluginSha256 = Hash("other-provider")
                }
            }),
            ("provider length", value => value with
            {
                Provider = value.Provider with { PluginByteLength = 1121 }
            }),
            ("source form", value => value with
            {
                RootSourceForm = new FormReference(
                    ProviderPlugin,
                    new FormId(0x802))
            }),
            ("winning form", value => value with
            {
                RootWinningForm = new FormReference(
                    ProviderPlugin,
                    new FormId(0x803))
            }),
            ("record digest", value => value with
            {
                Members = value.Members.SetItem(
                    0,
                    value.Members[0] with
                    {
                        WinningRecordSha256 = Hash("other-record")
                    })
            }),
            ("route order", value => value with
            {
                Members = value.Members.SetItem(
                    1,
                    value.Members[1] with { RouteOrder = 2 })
            }),
            ("model path", value => value with
            {
                Members = value.Members.SetItem(
                    1,
                    value.Members[1] with
                    {
                        ModelNif = new AssetPath(
                            "meshes/actors/character/character assets/hair/other.nif")
                    })
            }),
            ("physics key", value => value with
            {
                Physics = value.Physics with
                {
                    Shapes = value.Physics.Shapes.SetItem(
                        0,
                        value.Physics.Shapes[0] with
                        {
                            ShapeName = "OtherShape"
                        })
                }
            }),
            ("xml hash", value => value with
            {
                Physics = value.Physics with
                {
                    Shapes = value.Physics.Shapes.SetItem(
                        0,
                        value.Physics.Shapes[0] with
                        {
                            XmlSha256 = Hash("other-xml")
                        })
                }
            }),
            ("archive hash", value => value with
            {
                Assets = value.Assets.SetItem(
                    1,
                    value.Assets[1] with
                    {
                        ArchiveMember = value.Assets[1].ArchiveMember! with
                        {
                            ArchiveSha256 = Hash("other-archive")
                        }
                    })
            })
        };

        Sha256Hash baseline = ExternalHeadPartDependencyDescriptorCodec
            .ComputeDescriptorId(descriptor);
        foreach (var (name, mutate) in mutations)
        {
            Sha256Hash changed = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(mutate(descriptor));
            Require(changed != baseline,
                $"Descriptor identity did not change for {name}.");
        }

        var reordered = descriptor with
        {
            Members = [descriptor.Members[1], descriptor.Members[0]]
        };
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(reordered),
            "reordered ordered members");
        var reorderedEdges = descriptor with
        {
            Members = descriptor.Members.SetItem(
                0,
                descriptor.Members[0] with { HnamEdges = [RootForm] })
        };
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(reorderedEdges),
            "changed ordered HNAM edges");
    }

    private static void AssertAttestationIsIndependent(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        var attestation = new ExternalHeadPartFaceGeomExclusionAttestation(
            ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
            Hash("placeholder-attestation"),
            descriptor.DescriptorId,
            new AssetPath("meshes/actors/character/facegeom/output.nif"),
            Hash("output-facegeom"),
            4096,
            [
                new SkyrimNativeFaceGeomShapeEvidence(
                    new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x5)),
                    NpcHeadPartType.Face,
                    new AssetPath("meshes/actors/character/head.nif"),
                    Hash("head-model"),
                    "Head",
                    "Head",
                    16,
                    Hash("topology"),
                    Hash("base"),
                    Hash("final"),
                    [],
                    false)
            ],
            [
                new ExternalHeadPartExcludedShapeEvidence(
                    descriptor.Members[1].ModelNif!.Value,
                    "OrchidChild")
            ],
            [
                new ExternalHeadPartExcludedMetadataEvidence(
                    "physics-locator",
                    "HDT Skinned Mesh Physics Object")
            ],
            "preview254-v1");

        Sha256Hash descriptorId = ExternalHeadPartDependencyDescriptorCodec
            .ComputeDescriptorId(descriptor);
        Sha256Hash firstHash = ExternalHeadPartDependencyDescriptorCodec
            .ComputeAttestationHash(attestation);
        Sha256Hash changedHash = ExternalHeadPartDependencyDescriptorCodec
            .ComputeAttestationHash(attestation with
            {
                OutputFaceGeomSha256 = Hash("different-facegeom")
            });
        Require(firstHash != changedHash,
            "FaceGeom output mutation did not change attestation hash.");
        Require(descriptorId == descriptor.DescriptorId,
            "FaceGeom attestation calculation altered descriptor identity.");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .ComputeAttestationHash(attestation with
            {
                OutputFaceGeomPath = new AssetPath(
                    "meshes/actors/character/facegeom/CON.nif")
            }), "reserved output FaceGeom device path");

        var withAttestation = attestation with { AttestationSha256 = firstHash };
        byte[] bytes = ExternalHeadPartDependencyDescriptorCodec
            .SerializeAttestation(withAttestation);
        var parsed = ExternalHeadPartDependencyDescriptorCodec
            .ParseAttestation(bytes);
        Require(parsed.DescriptorId == descriptor.DescriptorId &&
                parsed.AttestationSha256 == firstHash,
            "FaceGeom attestation did not round-trip.");
    }

    private static void AssertClosedWires(byte[] descriptorBytes)
    {
        string wire = Encoding.UTF8.GetString(descriptorBytes);
        Require(wire.Contains("record-only-external", StringComparison.Ordinal),
            "The descriptor did not use its exact closed disposition token.");
        Require(!wire.Contains("RecordOnlyExternal", StringComparison.Ordinal),
            "The descriptor leaked a CLR enum token.");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .ParseDescriptor(Encoding.UTF8.GetBytes(
                wire.Replace("record-only-external", "RecordOnlyExternal",
                    StringComparison.Ordinal))),
            "undefined disposition token");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .ParseDescriptor(Encoding.UTF8.GetBytes(
                wire.Replace(
                    ExternalHeadPartSchemaIdentifiers.Descriptor,
                    "npc.external-headpart-dependency.v2",
                    StringComparison.Ordinal))),
            "future descriptor schema");
    }

    private static void AssertRefusals(
        ExternalHeadPartDependencyDescriptor descriptor,
        byte[] bytes)
    {
        string wire = Encoding.UTF8.GetString(bytes);
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .ParseDescriptor(Encoding.UTF8.GetBytes(
                wire.Replace(
                    "\"schemaIdentifier\":\"",
                    "\"unknown\":true,\"schemaIdentifier\":\"",
                    StringComparison.Ordinal))),
            "unknown JSON member");

        int providerIndex = wire.IndexOf(
            "\"provider\":{", StringComparison.Ordinal);
        Require(providerIndex >= 0, "Provider was not serialized.");
        string duplicate = wire.Insert(
            providerIndex + "\"provider\":{".Length,
            "\"plugin\":\"Duplicate.esp\",");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .ParseDescriptor(Encoding.UTF8.GetBytes(duplicate)),
            "duplicate JSON member");

        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor with
            {
                Members = []
            }), "empty graph");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor with
            {
                Assets = descriptor.Assets.Add(
                    descriptor.Assets[0] with
                    {
                        Path = new AssetPath(
                            "textures/actors/character/hair/orchid-child.DDS")
                    })
            }), "case-colliding path");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor with
            {
                RuntimePrerequisites = [
                    new ExternalHeadPartRuntimePrerequisite(
                        "2026-08-20T12:00:00Z", "FSMP", "fixture")
                ]
            }), "timestamp-shaped portable token");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor with
            {
                RuntimePrerequisites = [
                    new ExternalHeadPartRuntimePrerequisite(
                        "install-state", "Verified", "fixture")
                ]
            }), "install-state portable token");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor with
            {
                RuntimePrerequisites = [
                    new ExternalHeadPartRuntimePrerequisite(
                        "physics", "FSMP", "SKSE/Plugins/../hdtSMP64.dll")
                ]
            }), "traversal portable path");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor with
            {
                RuntimePrerequisites = [
                    new ExternalHeadPartRuntimePrerequisite(
                        "physics", "FSMP", "SKSE/Plugins/hdtSMP64.dll:Zone.Identifier")
                ]
            }), "ADS portable path");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor with
            {
                Members = descriptor.Members.SetItem(
                    0,
                    descriptor.Members[0] with
                    {
                        EditorId = "C:\\worktree\\unsafe"
                    })
            }), "rooted portable token");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor with
            {
                Members = descriptor.Members.SetItem(
                    0,
                    descriptor.Members[0] with
                    {
                        HnamEdges = []
                    }).SetItem(
                    1,
                    descriptor.Members[1] with
                    {
                        Parent = null
                    })
            }), "disconnected HNAM graph");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor with
            {
                Members = descriptor.Members.SetItem(
                    1,
                    descriptor.Members[1] with
                    {
                        Depth = 2
                    })
            }), "inconsistent HNAM depth");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(descriptor with
            {
                Assets = descriptor.Assets.SetItem(
                    0,
                    descriptor.Assets[0] with { ByteLength = 0 })
            }), "non-positive length");

        var mapping = new ExternalHeadPartPhysicsMappingAuthority(
            new AssetPath("SKSE/Plugins/hdtSkinnedMeshConfigs/defaultBBPs.xml"),
            Hash("default-bbp"),
            512);
        RequireDescriptorRefusal(descriptor with
        {
            Physics = descriptor.Physics with { MappingAuthority = mapping }
        }, "direct NIF mapping authority");
        RequireDescriptorRefusal(descriptor with
        {
            Physics = descriptor.Physics with
            {
                Mode = ExternalHeadPartPhysicsBindingMode.DefaultBbpMap,
                MappingAuthority = null
            }
        }, "missing default-BBP mapping authority");
        RequireDescriptorRefusal(descriptor with
        {
            Members = descriptor.Members.SetItem(
                0,
                descriptor.Members[0] with
                {
                    ModelNif = new AssetPath(
                        "meshes/actors/character/character assets/hair/other.nif")
                })
        }, "physics member model mismatch");
        var reservedModel = new AssetPath(
            "meshes/actors/character/character assets/hair/CON.nif");
        RequireDescriptorRefusal(descriptor with
        {
            Members = descriptor.Members.SetItem(
                0,
                descriptor.Members[0] with { ModelNif = reservedModel }),
            Physics = descriptor.Physics with
            {
                Shapes = descriptor.Physics.Shapes.SetItem(
                    1,
                    descriptor.Physics.Shapes[1] with
                    {
                        MemberForm = RootForm,
                        ModelNif = reservedModel
                    })
            }
        }, "reserved member model device path");
        RequireDescriptorRefusal(descriptor with
        {
            Physics = descriptor.Physics with { Shapes = [] }
        }, "direct physics without shape binding");
        RequireDescriptorRefusal(descriptor with
        {
            Physics = descriptor.Physics with
            {
                Mode = ExternalHeadPartPhysicsBindingMode.DefaultBbpMap,
                Shapes = [],
                MappingAuthority = mapping
            }
        }, "default-BBP physics without shape binding");
        RequireDescriptorRefusal(descriptor with
        {
            Assets = descriptor.Assets.SetItem(
                1,
                descriptor.Assets[1] with
                {
                    ArchiveMember = descriptor.Assets[1].ArchiveMember! with
                    {
                        MemberPath = new AssetPath(
                            "meshes/actors/character/character assets/hair/other.nif")
                    }
                })
        }, "archive member path mismatch");
        RequireDescriptorRefusal(descriptor with
        {
            Physics = descriptor.Physics with
            {
                Shapes = descriptor.Physics.Shapes.SetItem(
                    0,
                    descriptor.Physics.Shapes[0] with
                    {
                        XmlPath = new AssetPath("SKSE/Plugins/NUL.xml")
                    })
            }
        }, "reserved physics XML device path");
        RequireDescriptorRefusal(descriptor with
        {
            Assets = descriptor.Assets.SetItem(
                1,
                descriptor.Assets[1] with
                {
                    ArchiveMember = descriptor.Assets[1].ArchiveMember! with
                    {
                        ArchivePath = new AssetPath("NUL.bsa")
                    }
                })
        }, "reserved archive device path");

        var duplicateShapeName = descriptor with
        {
            Physics = descriptor.Physics with
            {
                Shapes = descriptor.Physics.Shapes.SetItem(
                    1,
                    descriptor.Physics.Shapes[1] with
                    {
                        ShapeName = descriptor.Physics.Shapes[0].ShapeName
                    })
            }
        };
        _ = ExternalHeadPartDependencyDescriptorCodec
            .SerializeDescriptor(duplicateShapeName with
            {
                DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(duplicateShapeName)
            });
    }

    private static void AssertInstallArtifacts(Sha256Hash descriptorId)
    {
        var observations = ImmutableArray.Create(
            new ExternalHeadPartInstallObservation(
                "plugin", "OrchidAdornment.esp", Hash("provider-plugin"),
                1120, 1),
            new ExternalHeadPartInstallObservation(
                "record", "OrchidAdornment.esp|0x00000800",
                Hash("root-record"), 180, 2));
        var fingerprint = new ExternalHeadPartInstallContextFingerprint(
            ExternalHeadPartDependencyDescriptorCodec
                .ComputeInstallContextFingerprintHash(observations),
            observations);
        var providerObservations = ImmutableArray.Create(
            new ExternalHeadPartInstallProviderObservation(
                new PluginName("OrchidAdornment.esp"),
                Hash("provider-plugin"),
                Hash("provider-plugin"),
                false));
        var artifact = new ExternalHeadPartInstallVerificationArtifact(
            ExternalHeadPartSchemaIdentifiers.InstallVerification,
            true,
            true,
            null,
            [descriptorId],
            null,
            ExternalInstallDependencyState.DeclaredUnverified,
            false,
            false,
            false,
            false,
            providerObservations,
            [
                new ExternalHeadPartInstallPrerequisite(
                    ExternalHeadPartDiagnosticCodes.ProviderDisabled,
                    "OrchidAdornment.esp",
                    Hash("provider-plugin"),
                    Hash("provider-plugin"),
                    false,
                    "Enable the provider plugin.")
            ]);
        byte[] bytes = ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact);
        var parsed = ExternalHeadPartDependencyDescriptorCodec
            .ParseInstallVerificationArtifact(bytes);
        Require(parsed.HistoricalSnapshotValid is null &&
                parsed.DescriptorIds.SequenceEqual([descriptorId]) &&
                !parsed.RuntimeAuthority && !parsed.VisualAuthority,
            "Context-free install artifact did not preserve authority truth.");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact with
            {
                MissingPrerequisites = [
                    artifact.MissingPrerequisites[0] with { CurrentSha256 = null }
                ]
            }), "provider prerequisite observation mismatch");
        var missingObservation = new ExternalHeadPartInstallProviderObservation(
            new PluginName("OrchidAdornment.esp"),
            Hash("provider-plugin"),
            null,
            null);
        var disabledObservation = new ExternalHeadPartInstallProviderObservation(
            new PluginName("OrchidAdornment.esp"),
            Hash("provider-plugin"),
            Hash("provider-plugin"),
            false);
        var driftedObservation = new ExternalHeadPartInstallProviderObservation(
            new PluginName("OrchidAdornment.esp"),
            Hash("provider-plugin"),
            Hash("drifted-provider"),
            true);
        foreach (var observation in new[]
                 {
                     missingObservation,
                     disabledObservation,
                     driftedObservation
                 })
        {
            var diagnosticCode = observation.CurrentSha256 is null
                ? ExternalHeadPartDiagnosticCodes.ProviderMissing
                : observation.Enabled == false
                    ? ExternalHeadPartDiagnosticCodes.ProviderDisabled
                    : ExternalHeadPartDiagnosticCodes.AssetDrift;
            byte[] projectionBytes = ExternalHeadPartDependencyDescriptorCodec
                .SerializeInstallVerificationArtifact(artifact with
                {
                    ProviderObservations = [observation],
                    MissingPrerequisites = [
                        new ExternalHeadPartInstallPrerequisite(
                            diagnosticCode,
                            observation.ProviderPlugin.Value,
                            observation.ExpectedSha256,
                            observation.CurrentSha256,
                            observation.Enabled,
                            "Resolve the provider dependency.")
                    ]
                });
            var projection = ExternalHeadPartDependencyDescriptorCodec
                .ParseInstallVerificationArtifact(projectionBytes);
            Require(projection.CurrentInstallDependencyState ==
                    ExternalInstallDependencyState.DeclaredUnverified,
                    "Declared-unverified provider projection did not round-trip.");
        }
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact with
            {
                ProviderObservations = [missingObservation],
                MissingPrerequisites = [
                    new ExternalHeadPartInstallPrerequisite(
                        ExternalHeadPartDiagnosticCodes.ProviderDisabled,
                        missingObservation.ProviderPlugin.Value,
                        missingObservation.ExpectedSha256,
                        missingObservation.CurrentSha256,
                        missingObservation.Enabled,
                        "Resolve the provider dependency.")
                ]
            }), "ProviderDisabled code for an absent provider");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact with
            {
                ProviderObservations = [disabledObservation],
                MissingPrerequisites = [
                    new ExternalHeadPartInstallPrerequisite(
                        ExternalHeadPartDiagnosticCodes.ProviderMissing,
                        disabledObservation.ProviderPlugin.Value,
                        disabledObservation.ExpectedSha256,
                        disabledObservation.CurrentSha256,
                        disabledObservation.Enabled,
                        "Resolve the provider dependency.")
                ]
            }), "ProviderMissing code for a disabled provider");
        string contextFreeWire = Encoding.UTF8.GetString(bytes);
        Require(!contextFreeWire.Contains("verifiedInstallSnapshot",
                    StringComparison.Ordinal),
            "Null verified install snapshot was not suppressed on the wire.");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .ParseInstallVerificationArtifact(Encoding.UTF8.GetBytes(
                contextFreeWire.Insert(
                    contextFreeWire.IndexOf("\"currentInstallDependencyState\":",
                        StringComparison.Ordinal),
                    "\"verifiedInstallSnapshot\":null,"))),
            "explicit null verified install snapshot");

        var verified = artifact with
        {
            ProviderObservations = [
                providerObservations[0] with { Enabled = true }
            ],
            HistoricalSnapshotValid = true,
            VerifiedInstallSnapshot = new ExternalHeadPartVerifiedInstallSnapshot(
                Hash("selected-manifest"),
                [descriptorId],
                fingerprint),
            CurrentInstallDependencyState = ExternalInstallDependencyState.Verified,
            InstallReady = true,
            InstallDependencyAuthority = true,
            MissingPrerequisites = []
        };
        byte[] verifiedBytes = ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(verified);
        var verifiedParsed = ExternalHeadPartDependencyDescriptorCodec
            .ParseInstallVerificationArtifact(verifiedBytes);
        Require(verifiedParsed.HistoricalSnapshotValid == true &&
                verifiedParsed.InstallReady &&
                verifiedParsed.InstallDependencyAuthority,
            "Finish verification install artifact did not round-trip.");

        var stale = verified with
        {
            HistoricalSnapshotValid = false,
            CurrentInstallDependencyState = ExternalInstallDependencyState.DeclaredUnverified,
            InstallReady = false,
            InstallDependencyAuthority = false,
            MissingPrerequisites = [
                new ExternalHeadPartInstallPrerequisite(
                    ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch,
                    "selected-manifest",
                    Hash("selected-manifest"),
                    Hash("stale-manifest"),
                    null,
                    "Re-run install verification.")
            ]
        };
        byte[] staleBytes = ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(stale);
        var staleParsed = ExternalHeadPartDependencyDescriptorCodec
            .ParseInstallVerificationArtifact(staleBytes);
        Require(staleParsed.HistoricalSnapshotValid == false &&
                staleParsed.VerifiedInstallSnapshot is not null &&
                !staleParsed.InstallReady &&
                !staleParsed.InstallDependencyAuthority,
            "Invalid Finish snapshot did not round-trip as non-authoritative.");

        string wire = Encoding.UTF8.GetString(bytes);
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .ParseInstallVerificationArtifact(Encoding.UTF8.GetBytes(
                wire.Replace(
                    "\"packageIntegrity\":true",
                    "\"packageIntegrity\":true,\"unknown\":true",
                    StringComparison.Ordinal))),
            "unknown install artifact member");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact with
            {
                CurrentInstallDependencyState = ExternalInstallDependencyState.Verified,
                InstallReady = false
            }), "inconsistent install state/readiness");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact with
            {
                HistoricalSnapshotValid = false,
                VerifiedInstallSnapshot = null
            }), "inconsistent historical snapshot");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(verified with
            {
                PackageIntegrity = false
            }), "verified state with invalid package integrity");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(verified with
            {
                HistoricalSnapshotValid = false
            }), "invalid snapshot with install authority");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(verified with
            {
                VerifiedInstallSnapshot = verified.VerifiedInstallSnapshot! with
                {
                    ContextFingerprint = fingerprint with
                    {
                        Observations = observations.SetItem(
                            0,
                            observations[0] with { ByteLength = 1121 })
                    }
                }
            }), "mismatched context fingerprint hash");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact with
            {
                ProviderObservations = [
                    providerObservations[0] with
                    {
                        CurrentSha256 = null,
                        Enabled = true
                    }
                ]
            }), "provider enabled without current hash");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact with
            {
                ProviderObservations = [
                    providerObservations[0] with { Enabled = null }
                ]
            }), "provider current hash without enabled state");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact with
            {
                CurrentInstallDependencyState = ExternalInstallDependencyState.NotRequired,
                InstallReady = false
            }), "not-required artifact without ready package semantics");
        var notRequired = artifact with
        {
            CurrentInstallDependencyState = ExternalInstallDependencyState.NotRequired,
            InstallReady = true,
            ProviderObservations = [],
            MissingPrerequisites = []
        };
        byte[] notRequiredBytes = ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(notRequired);
        var notRequiredParsed = ExternalHeadPartDependencyDescriptorCodec
            .ParseInstallVerificationArtifact(notRequiredBytes);
        Require(notRequiredParsed.InstallReady &&
                !notRequiredParsed.InstallDependencyAuthority,
            "Not-required artifact did not preserve ready package semantics.");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(notRequired with
            {
                ProviderObservations = [
                    providerObservations[0] with { Enabled = true }
                ]
            }), "not-required artifact with provider observations");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(artifact with
            {
                MissingPrerequisites = []
            }), "declared-unverified artifact without prerequisites");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(verified with
            {
                ProviderObservations = []
            }), "verified artifact without provider observations");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(verified with
            {
                ProviderObservations = [
                    providerObservations[0] with { Enabled = false }
                ]
            }), "verified artifact with disabled provider");
        RequireThrows(() => ExternalHeadPartDependencyDescriptorCodec
            .SerializeInstallVerificationArtifact(verified with
            {
                ProviderObservations = [
                    providerObservations[0] with
                    {
                        CurrentSha256 = Hash("drifted-provider")
                    }
                ]
            }), "verified artifact with drifted provider");
    }

    private static void AssertPortableTokenBoundaries(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        var valid = descriptor with
        {
            RuntimePrerequisites = descriptor.RuntimePrerequisites.SetItem(
                0,
                descriptor.RuntimePrerequisites[0] with
                {
                    EvidenceSource = "SKSE/Plugins/hdtSMP64.dll"
                })
        };
        _ = ExternalHeadPartDependencyDescriptorCodec.ComputeDescriptorId(valid);

        foreach (string value in new[]
                 {
                     "../escape",
                     "..\\escape",
                     "/rooted",
                     "C:relative",
                     "safe:ads",
                     "CON",
                     "NUL",
                     "COM1",
                     "LPT9"
                 })
        {
            var invalid = descriptor with
            {
                RuntimePrerequisites = descriptor.RuntimePrerequisites.SetItem(
                    0,
                    descriptor.RuntimePrerequisites[0] with
                    {
                        EvidenceSource = value
                    })
            };
            RequireThrows(
                () => ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(invalid),
                $"portable token '{value}'");
        }
    }

    private static Sha256Hash Hash(string seed) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))));

    private static void RequireDescriptorRefusal(
        ExternalHeadPartDependencyDescriptor descriptor,
        string label)
    {
        RequireThrows(() =>
        {
            var bound = descriptor with
            {
                DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(descriptor)
            };
            _ = ExternalHeadPartDependencyDescriptorCodec
                .SerializeDescriptor(bound);
        }, label);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireThrows(Action action, string label)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidDataException or JsonException or OverflowException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected the codec to refuse {label}.");
    }
}
