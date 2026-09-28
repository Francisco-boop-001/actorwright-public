using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal sealed class Preview254ExternalSmpFinishContractScenario :
    IPreview254ExternalSmpArchitectureScenario
{
    public string Selector => "--test-npc-finish-core-external-contracts";

    public ValueTask RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SkyrimNpcFinishCoreExternalContractTests.Run();
        return ValueTask.CompletedTask;
    }
}

internal static class SkyrimNpcFinishCoreExternalContractTests
{
    public static void Run()
    {
        WorkspacePath projectRoot = new(@"K:\ExampleWorkspace");

        ExternalHeadPartDependencyDescriptor descriptor = CreateDescriptor();
        ExternalHeadPartFaceGeomExclusionAttestation attestation =
            CreateAttestation(descriptor);
        Sha256Hash selectedManifestHash = Hash("selected-manifest");
        var binding = new SkyrimNpcFinishCoreExternalHeadPartBinding(
            descriptor.DescriptorId,
            attestation.AttestationSha256);
        var authority = new SkyrimNpcFinishCoreExternalHeadPartAuthority(
            new AssetPath(SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath),
            selectedManifestHash,
            [binding]);
        ExternalHeadPartInstallVerificationArtifact declared =
            CreateDeclaredArtifact(descriptor.DescriptorId);

        JsonObject requestNode = CreateRequestNode(
            SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier,
            descriptor.DescriptorId,
            attestation.AttestationSha256,
            selectedManifestHash);
        byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(requestNode);
        SkyrimNpcFinishCoreRequest request =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(requestBytes, projectRoot);
        byte[] canonicalRequest = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
            request, projectRoot);
        SkyrimNpcFinishCoreRequest requestReplay =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(canonicalRequest, projectRoot);
        Require(requestReplay.Schema == SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier,
            "Finish Core request v3 schema changed during round trip.");
        Require(ExternalAuthorityEquals(requestReplay.Authorities.ExternalHeadParts, authority),
            $"Finish Core request v3 did not retain canonical external authority. Expected {authority}; actual {requestReplay.Authorities.ExternalHeadParts}.");
        Require(canonicalRequest.SequenceEqual(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                        requestReplay, projectRoot)),
            "Finish Core request v3 bytes were not stable across a round trip.");

        var proposal = new SkyrimNpcFinishCoreProposal
        {
            Schema = SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier,
            RequestSha256 = Hash("request"),
            ProposalSha256 = Hash("proposal"),
            Request = request,
            Status = SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired,
            ExternalHeadParts = new SkyrimNpcFinishCoreExternalHeadPartProposalAuthority(
                authority,
                declared,
                null)
        };
        byte[] proposalBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal, projectRoot);
        SkyrimNpcFinishCoreProposal proposalReplay =
            SkyrimNpcFinishCoreDocumentCodec.ParseProposal(proposalBytes, projectRoot);
        Require(proposalReplay.Status ==
                    SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired &&
                proposalReplay.ExternalHeadParts?.ContextFingerprint is null &&
                ExternalAuthorityEquals(proposalReplay.ExternalHeadParts!.Authority, authority) &&
                proposalBytes.SequenceEqual(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                        proposalReplay, projectRoot)),
            "Finish Core proposal v3 did not preserve its context-free dependency gate.");

        ExternalHeadPartInstallContextFingerprint fingerprint =
            CreateFingerprint();
        ExternalHeadPartVerifiedInstallSnapshot snapshot =
            new(selectedManifestHash, [descriptor.DescriptorId], fingerprint);
        var manifest = new SkyrimNpcFinishCoreManifest
        {
            Schema = SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier,
            Plugin = new PluginName("Test.esp"),
            PluginSha256 = Hash("plugin"),
            BaseNpc = new FormReference(new PluginName("Test.esp"), new FormId(0x800)),
            RequestSha256 = Hash("request"),
            ProposalSha256 = Hash("proposal"),
            PackageRoot = new WorkspacePath(@"K:\ExampleWorkspace\package"),
            Archive = new WorkspacePath(@"K:\ExampleWorkspace\package.zip"),
            ArchiveSha256 = Hash("archive"),
            SourcePackageTreeSha256 = Hash("source-tree"),
            PackageTreeSha256 = Hash("package-tree"),
            ExternalHeadParts = new SkyrimNpcFinishCoreExternalHeadPartManifestAuthority(
                authority.SelectedManifestPath,
                authority.SelectedManifestSha256,
                [descriptor],
                [attestation],
                snapshot)
            {
                PromotedOutputBindingPath = new AssetPath(
                    SkyrimNpcFinishCoreDocumentCodec.CanonicalPromotedOutputBindingPath),
                PromotedOutputBindingSha256 = Hash("promoted-binding")
            }
        };
        byte[] manifestBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
            manifest, projectRoot);
        SkyrimNpcFinishCoreManifest manifestReplay =
            SkyrimNpcFinishCoreDocumentCodec.ParseManifest(manifestBytes, projectRoot);
        Require(manifestReplay.ExternalHeadParts?.Descriptors.Single().DescriptorId ==
                    descriptor.DescriptorId &&
                manifestBytes.SequenceEqual(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                        manifestReplay, projectRoot)),
            "Finish Core manifest v2 did not preserve descriptor/snapshot closure.");

        ExternalHeadPartInstallVerificationArtifact verified =
            CreateVerifiedArtifact(descriptor.DescriptorId, selectedManifestHash, snapshot);
        var verification = new SkyrimNpcFinishCoreVerification
        {
            Schema = SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier,
            Status = SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired,
            Verified = true,
            PluginSha256 = Hash("plugin"),
            PackageTreeSha256 = Hash("package-tree"),
            SourcePackageTreeSha256 = Hash("source-tree"),
            ArchiveSha256 = Hash("archive"),
            ExternalHeadParts = new SkyrimNpcFinishCoreExternalHeadPartVerification(
                verified)
        };
        byte[] verificationBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeVerification(
            verification, projectRoot);
        SkyrimNpcFinishCoreVerification verificationReplay =
            SkyrimNpcFinishCoreDocumentCodec.ParseVerification(verificationBytes);
        Require(verificationReplay.ExternalHeadParts?.Verification.HistoricalSnapshotValid == true &&
                verificationBytes.SequenceEqual(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeVerification(
                        verificationReplay, projectRoot)),
            "Finish Core verification v2 did not preserve independent install facts.");

        AssertClosedWireRefusals(
            requestNode,
            proposal,
            proposalBytes,
            manifest,
            manifestBytes,
            verification,
            projectRoot);
        AssertPromotedBindingBoundary(descriptor, attestation, selectedManifestHash);
        AssertLegacyCompatibility(request, projectRoot);
        AssertSchemaExport();
    }

    private static JsonObject CreateRequestNode(
        string schema,
        Sha256Hash descriptorId,
        Sha256Hash attestationHash,
        Sha256Hash selectedManifestHash) => new()
    {
        ["schema"] = schema,
        ["source"] = new JsonObject
        {
            ["packageRoot"] = "projects/NpcManagerReimplementation",
            ["packageManifest"] = "projects/NpcManagerReimplementation/PROJECT_MANIFEST.json",
            ["packageManifestSha256"] = new string('a', 64),
            ["packageTreeSha256"] = new string('b', 64),
            ["pluginPath"] = "projects/NpcManagerReimplementation/Test.esp",
            ["plugin"] = "Test.esp",
            ["pluginSha256"] = new string('c', 64)
        },
        ["actor"] = new JsonObject
        {
            ["editorId"] = "TestActor",
            ["formId"] = "0x00000800"
        },
        ["authorities"] = new JsonObject
        {
            ["bodyRoute"] = "Cbbe3Ba",
            ["providers"] = new JsonArray(),
            ["additionalMasters"] = new JsonArray(),
            ["actorAssemblySha256"] = null,
            ["bodyOwnerSha256"] = null,
            ["protectedAppearanceTreeSha256"] = null,
            ["externalHeadParts"] = new JsonObject
            {
                ["selectedManifestPath"] =
                    "Data/NPCManager/Evidence/selected-preset-dependencies.json",
                ["selectedManifestSha256"] = selectedManifestHash.Value,
                ["bindings"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["descriptorId"] = descriptorId.Value,
                        ["faceGeomExclusionAttestationSha256"] = attestationHash.Value
                    }
                }
            }
        },
        ["followerPolicy"] = new JsonObject
        {
            ["recruitable"] = true,
            ["defensiveOnly"] = true,
            ["potentialFollowerFaction"] = "Skyrim.esm|0x0005C84D",
            ["currentFollowerFaction"] = "Skyrim.esm|0x0005C84E",
            ["relationshipRank"] = "Ally"
        },
        ["aiPolicy"] = new JsonObject
        {
            ["aggression"] = "Unaggressive",
            ["confidence"] = "Brave",
            ["energy"] = 50,
            ["morality"] = "NoCrime",
            ["assistance"] = "HelpsFriendsAndAllies",
            ["mood"] = "Neutral"
        },
        ["outfitPolicy"] = new JsonObject
        {
            ["policy"] = "ExistingOutfit",
            ["existingOutfit"] = null,
            ["armorItems"] = new JsonArray()
        },
        ["inventoryPolicy"] = new JsonObject
        {
            ["policy"] = "PreserveInventory",
            ["expectedSourceItems"] = new JsonArray(),
            ["desiredItems"] = new JsonArray()
        },
        ["sandboxAuthority"] = new JsonObject
        {
            ["copiedMaster"] = "projects/NpcManagerReimplementation/Skyrim.esm",
            ["copiedMasterSha256"] = new string('1', 64),
            ["template"] = "Skyrim.esm|0x0001B217",
            ["templateEditorId"] = "DefaultSandboxEditorLocation512",
            ["rawRecordDigest"] = new string('2', 64)
        },
        ["output"] = new JsonObject
        {
            ["root"] = "projects/NpcManagerReimplementation/output",
            ["archive"] = "projects/NpcManagerReimplementation/output.zip",
            ["pluginFileName"] = "Test.esp"
        }
    };

    private static ExternalHeadPartDependencyDescriptor CreateDescriptor()
    {
        PluginName provider = new("OrchidAdornment.esp");
        FormReference rootForm = new(provider, new FormId(0x800));
        AssetPath model = new("meshes/actors/character/hair/orchid-root.nif");
        AssetPath xml = new("SKSE/Plugins/hdtSkinnedMeshConfigs/orchid-root.xml");
        var member = new ExternalHeadPartRecordDependency(
            rootForm,
            provider,
            rootForm,
            provider,
            Hash("provider-plugin"),
            1120,
            Hash("root-record"),
            "OrchidRootHair",
            NpcHeadPartType.Hair,
            NpcHeadPartType.Hair,
            model,
            [],
            [],
            null,
            0,
            0,
            NpcSex.Female,
            null);
        var descriptor = new ExternalHeadPartDependencyDescriptor(
            ExternalHeadPartSchemaIdentifiers.Descriptor,
            Hash("placeholder"),
            ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
            rootForm,
            rootForm,
            NpcHeadPartType.Hair,
            Hash("graph"),
            new ExternalHeadPartProviderIdentity(
                provider,
                Hash("provider-plugin"),
                1120,
                ExternalHeadPartRedistributionMode.ExternalProviderRequired),
            [member],
            new ExternalHeadPartPhysicsBinding(
                ExternalHeadPartPhysicsBindingMode.DirectNifExtraData,
                [new ExternalHeadPartPhysicsShapeBinding(
                    rootForm, model, "OrchidRoot", xml, Hash("root-xml"), 231)],
                null),
            [new ExternalHeadPartAssetDependency(
                model,
                Hash("root-nif"),
                4096,
                provider,
                Hash("provider-plugin"),
                null)],
            [new ExternalHeadPartRuntimePrerequisite(
                "skse-plugin", "FSMP present", "SKSE/Plugins/hdtSMP64.dll")]);
        return descriptor with
        {
            DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(descriptor)
        };
    }

    private static ExternalHeadPartFaceGeomExclusionAttestation CreateAttestation(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        var attestation = new ExternalHeadPartFaceGeomExclusionAttestation(
            ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
            Hash("placeholder"),
            descriptor.DescriptorId,
            new AssetPath("Data/NPCManager/FaceGeom/orchid.nif"),
            Hash("facegeom"),
            4096,
            [],
            [new ExternalHeadPartExcludedShapeEvidence(
                descriptor.Members[0].ModelNif!.Value,
                "OrchidRoot")],
            [new ExternalHeadPartExcludedMetadataEvidence(
                "physics-locator", "HDT Skinned Mesh Physics Object")],
            "preview254-exclusion-v1");
        return attestation with
        {
            AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                .ComputeAttestationHash(attestation)
        };
    }

    private static ExternalHeadPartInstallVerificationArtifact CreateDeclaredArtifact(
        Sha256Hash descriptorId)
    {
        var observation = new ExternalHeadPartInstallProviderObservation(
            new PluginName("OrchidAdornment.esp"),
            Hash("provider-plugin"),
            null,
            null);
        return new ExternalHeadPartInstallVerificationArtifact(
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
            [observation],
            [new ExternalHeadPartInstallPrerequisite(
                ExternalHeadPartDiagnosticCodes.ProviderMissing,
                "OrchidAdornment.esp",
                Hash("provider-plugin"),
                null,
                null,
                "Install and enable the provider plugin.")]);
    }

    private static ExternalHeadPartInstallContextFingerprint CreateFingerprint()
    {
        var observations = ImmutableArray.Create(
            new ExternalHeadPartInstallObservation(
                "plugin", "OrchidAdornment.esp", Hash("provider-plugin"), 1120, 0),
            new ExternalHeadPartInstallObservation(
                "record", "OrchidAdornment.esp|0x00000800",
                Hash("root-record"), 180, 1));
        return new ExternalHeadPartInstallContextFingerprint(
            ExternalHeadPartDependencyDescriptorCodec
                .ComputeInstallContextFingerprintHash(observations),
            observations);
    }

    private static ExternalHeadPartInstallVerificationArtifact CreateVerifiedArtifact(
        Sha256Hash descriptorId,
        Sha256Hash selectedManifestHash,
        ExternalHeadPartVerifiedInstallSnapshot snapshot)
    {
        return new ExternalHeadPartInstallVerificationArtifact(
            ExternalHeadPartSchemaIdentifiers.InstallVerification,
            true,
            true,
            true,
            [descriptorId],
            snapshot with { SelectedManifestSha256 = selectedManifestHash },
            ExternalInstallDependencyState.Verified,
            true,
            true,
            false,
            false,
            [new ExternalHeadPartInstallProviderObservation(
                new PluginName("OrchidAdornment.esp"),
                Hash("provider-plugin"),
                Hash("provider-plugin"),
                true)],
            []);
    }

    private static void AssertClosedWireRefusals(
        JsonObject requestNode,
        SkyrimNpcFinishCoreProposal proposal,
        byte[] proposalBytes,
        SkyrimNpcFinishCoreManifest manifest,
        byte[] manifestBytes,
        SkyrimNpcFinishCoreVerification verification,
        WorkspacePath projectRoot)
    {
        JsonObject unknown = requestNode.DeepClone().AsObject();
        unknown["timestamp"] = "2026-08-21T00:00:00Z";
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
            JsonSerializer.SerializeToUtf8Bytes(unknown), projectRoot),
            "unknown request member");

        JsonObject absolutePath = requestNode.DeepClone().AsObject();
        absolutePath["authorities"]!["externalHeadParts"]!["selectedManifestPath"] =
            @"K:\Data\NPCManager\Evidence\selected-preset-dependencies.json";
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
            JsonSerializer.SerializeToUtf8Bytes(absolutePath), projectRoot),
            "absolute selected manifest path");

        JsonObject timestamp = requestNode.DeepClone().AsObject();
        timestamp["authorities"]!["externalHeadParts"]!["timestamp"] =
            "2026-08-21T00:00:00Z";
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
            JsonSerializer.SerializeToUtf8Bytes(timestamp), projectRoot),
            "external timestamp");

        JsonObject providerRow = requestNode.DeepClone().AsObject();
        providerRow["authorities"]!["providers"] = new JsonArray
        {
            new JsonObject
            {
                ["plugin"] = "OrchidAdornment.esp",
                ["path"] = "Data/NPCManager/Evidence/selected-preset-dependencies.json",
                ["sha256"] = new string('a', 64),
                ["byteLength"] = 1
            }
        };
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
            JsonSerializer.SerializeToUtf8Bytes(providerRow), projectRoot),
            "external authority placed in Authorities.Providers");

        var legacyStatus = proposal with
        {
            Schema = SkyrimNpcFinishCoreProposal.SchemaIdentifier,
            Request = proposal.Request! with
            {
                Schema = SkyrimNpcFinishCoreRequest.SchemaIdentifier,
                Authorities = proposal.Request.Authorities with
                {
                    ExternalHeadParts = null
                }
            },
            Status = SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired,
            ExternalHeadParts = null
        };
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            legacyStatus, projectRoot), "new status in legacy proposal");

        var staleFingerprint = CreateFingerprint() with { Sha256 = Hash("wrong") };
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal with
            {
                Status = SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite,
                ExternalHeadParts = proposal.ExternalHeadParts! with
                {
                    ContextFingerprint = staleFingerprint
                }
            }, projectRoot), "mismatched proposal fingerprint");

        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal with
            {
                Status = SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite,
                ExternalHeadParts = proposal.ExternalHeadParts! with
                {
                    ContextFingerprint = CreateFingerprint()
                }
            }, projectRoot), "ready proposal with declared-unverified install state");

        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal with
            {
                RuntimeAuthority = true
            }, projectRoot), "external proposal root runtime authority");

        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal with
            {
                ExternalHeadParts = proposal.ExternalHeadParts! with
                {
                    Verification = proposal.ExternalHeadParts.Verification with
                    {
                        PackageIntegrity = false
                    }
                }
            }, projectRoot), "static proposal with failed package closure");

        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal with
            {
                ExternalHeadParts = proposal.ExternalHeadParts! with
                {
                    Verification = proposal.ExternalHeadParts.Verification with
                    {
                        RuntimeAuthority = true
                    }
                }
            }, projectRoot), "external proposal runtime authority");

        JsonObject readyDeclaredWire = JsonNode.Parse(
            Encoding.UTF8.GetString(proposalBytes))!.AsObject();
        readyDeclaredWire["status"] =
            SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite.ToString();
        readyDeclaredWire["externalHeadParts"]!.AsObject()["contextFingerprint"] =
            CreateFingerprintNode(CreateFingerprint());
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
            JsonSerializer.SerializeToUtf8Bytes(readyDeclaredWire), projectRoot),
            "parsed ready proposal with declared-unverified install state");

        JsonObject staticFailedWire = JsonNode.Parse(
            Encoding.UTF8.GetString(proposalBytes))!.AsObject();
        staticFailedWire["externalHeadParts"]!["verification"]!["packageIntegrity"] = false;
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
            JsonSerializer.SerializeToUtf8Bytes(staticFailedWire), projectRoot),
            "parsed static proposal with failed package closure");

        JsonObject proposalRuntimeWire = JsonNode.Parse(
            Encoding.UTF8.GetString(proposalBytes))!.AsObject();
        proposalRuntimeWire["externalHeadParts"]!["verification"]!["runtimeAuthority"] = true;
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
            JsonSerializer.SerializeToUtf8Bytes(proposalRuntimeWire), projectRoot),
            "parsed external proposal runtime authority");

        JsonObject proposalRootRuntimeWire = JsonNode.Parse(
            Encoding.UTF8.GetString(proposalBytes))!.AsObject();
        proposalRootRuntimeWire["runtimeAuthority"] = true;
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
            JsonSerializer.SerializeToUtf8Bytes(proposalRootRuntimeWire), projectRoot),
            "parsed external proposal root runtime authority");

        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
            manifest with
            {
                ExternalHeadParts = manifest.ExternalHeadParts! with
                {
                    SelectedManifestPath = new AssetPath("Data/other.json")
                }
            }, projectRoot), "wrong selected manifest path");

        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
            manifest with
            {
                RuntimeAuthority = true,
                VisualAuthority = true
            }, projectRoot), "external manifest runtime/visual authority");

        JsonObject manifestAuthorityWire = JsonNode.Parse(
            Encoding.UTF8.GetString(manifestBytes))!.AsObject();
        manifestAuthorityWire["runtimeAuthority"] = true;
        manifestAuthorityWire["visualAuthority"] = true;
        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.ParseManifest(
            JsonSerializer.SerializeToUtf8Bytes(manifestAuthorityWire), projectRoot),
            "parsed external manifest runtime/visual authority");

        ExpectInvalid(() => SkyrimNpcFinishCoreDocumentCodec.SerializeVerification(
            verification with
            {
                ExternalHeadParts = verification.ExternalHeadParts! with
                {
                    Verification = verification.ExternalHeadParts.Verification with
                    {
                        HistoricalSnapshotValid = null,
                        VerifiedInstallSnapshot = null
                    }
                }
            }, projectRoot), "missing verification historical validity");
    }

    private static void AssertPromotedBindingBoundary(
        ExternalHeadPartDependencyDescriptor descriptor,
        ExternalHeadPartFaceGeomExclusionAttestation attestation,
        Sha256Hash selectedManifestHash)
    {
        PluginName outputPlugin = new("Test.esp");
        ImmutableArray<PluginName> masters = [new PluginName("Skyrim.esm")];
        ImmutableArray<FormReference> pnam =
            [new FormReference(new PluginName("OrchidAdornment.esp"), new FormId(0x800))];
        var group = new SkyrimNpcFinishCoreExternalHeadPartPromotedOutputGroupBinding(
            descriptor.DescriptorId,
            attestation.AttestationSha256,
            outputPlugin,
            Hash("plugin"),
            1,
            masters,
            pnam);
        var binding = new SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding(
            SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding.SchemaIdentifierValue,
            new AssetPath(SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath),
            selectedManifestHash,
            [group],
            outputPlugin,
            Hash("plugin"),
            1,
            masters,
            pnam);
        byte[] canonical = SkyrimNpcFinishCorePromotedOutputBindingCodec.Serialize(binding);
        JsonObject wrongShape = JsonNode.Parse(Encoding.UTF8.GetString(canonical))!
            .AsObject();
        wrongShape["groups"] = "not-an-array";
        ExpectInvalid(() => SkyrimNpcFinishCorePromotedOutputBindingCodec.Parse(
                JsonSerializer.SerializeToUtf8Bytes(wrongShape)),
            "promoted binding wrong array shape");

        JsonObject invalidHash = JsonNode.Parse(Encoding.UTF8.GetString(canonical))!
            .AsObject();
        invalidHash["sourceSelectedManifestSha256"] = "bad";
        ExpectInvalid(() => SkyrimNpcFinishCorePromotedOutputBindingCodec.Parse(
                JsonSerializer.SerializeToUtf8Bytes(invalidHash)),
            "promoted binding invalid hash");

        JsonObject oversizedPnam = JsonNode.Parse(Encoding.UTF8.GetString(canonical))!
            .AsObject();
        var pnamArray = new JsonArray();
        for (int index = 0; index < 257; index++)
            pnamArray.Add(pnam[0].ToString());
        oversizedPnam["declaredExternalPnam"] = pnamArray;
        ExpectInvalid(() => SkyrimNpcFinishCorePromotedOutputBindingCodec.Parse(
                JsonSerializer.SerializeToUtf8Bytes(oversizedPnam)),
            "promoted binding oversized PNAM array");

        ExpectInvalid(() => SkyrimNpcFinishCorePromotedOutputBindingCodec.Serialize(
                binding with { Groups = default }),
            "promoted binding default groups");
    }

    private static JsonObject CreateFingerprintNode(
        ExternalHeadPartInstallContextFingerprint fingerprint) => new()
    {
        ["sha256"] = fingerprint.Sha256.Value,
        ["observations"] = new JsonArray(fingerprint.Observations.Select(observation =>
            (JsonNode)new JsonObject
            {
                ["kind"] = observation.Kind,
                ["portableIdentity"] = observation.PortableIdentity,
                ["sha256"] = observation.Sha256.Value,
                ["byteLength"] = observation.ByteLength,
                ["order"] = observation.Order
            }).ToArray())
    };

    private static void AssertLegacyCompatibility(
        SkyrimNpcFinishCoreRequest externalRequest,
        WorkspacePath projectRoot)
    {
        SkyrimNpcFinishCoreRequest legacyRequest = externalRequest with
        {
            Schema = SkyrimNpcFinishCoreRequest.SchemaIdentifier,
            Authorities = externalRequest.Authorities with
            {
                ExternalHeadParts = null
            }
        };
        byte[] requestBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
            legacyRequest, projectRoot);
        Require(!Encoding.UTF8.GetString(requestBytes).Contains(
                    "externalHeadParts", StringComparison.Ordinal),
            "Ordinary v2 request bytes gained an external group.");
        Require(requestBytes.SequenceEqual(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                        SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                            requestBytes, projectRoot), projectRoot)),
            "Ordinary v2 request bytes changed across a parse/serialize round trip.");

    }

    private static void AssertSchemaExport()
    {
        JsonElement analyze = ProtocolV2SchemaService.RenderInline("npc finish analyze");
        string[] analyzeIds = analyze.GetProperty("documentSchemas")
            .EnumerateArray()
            .Select(item => item.GetProperty("schemaIdentifier").GetString()!)
            .ToArray();
        Require(analyzeIds.Contains(SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier) &&
                analyzeIds.Contains(SkyrimNpcFinishCoreRequest.SchemaIdentifier) &&
                analyzeIds.Contains(SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier) &&
                analyzeIds.Contains(SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier) &&
                analyzeIds.Contains(SkyrimNpcFinishCoreProposal.SchemaIdentifier) &&
                analyzeIds.Contains(SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier),
            "Finish Core analyze schema export omitted a request/proposal version.");

        JsonElement apply = ProtocolV2SchemaService.RenderInline("npc finish apply");
        string[] applyIds = apply.GetProperty("documentSchemas")
            .EnumerateArray()
            .Select(item => item.GetProperty("schemaIdentifier").GetString()!)
            .ToArray();
        Require(analyzeIds.SequenceEqual(applyIds),
            "Finish Core analyze/apply schema exports drifted apart.");

        JsonElement verify = ProtocolV2SchemaService.RenderInline("npc finish verify");
        string[] verifyIds = verify.GetProperty("documentSchemas")
            .EnumerateArray()
            .Select(item => item.GetProperty("schemaIdentifier").GetString()!)
            .ToArray();
        Require(verifyIds.Contains(SkyrimNpcFinishCoreManifest.SchemaIdentifier) &&
                verifyIds.Contains(SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier) &&
                verifyIds.Contains(SkyrimNpcFinishCoreVerification.SchemaIdentifier) &&
                verifyIds.Contains(SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier),
            "Finish Core verify schema export omitted a manifest/verification version.");

        JsonElement legacyProposalSchema = FindDocumentSchema(
            analyze,
            SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier);
        JsonElement currentProposalSchema = FindDocumentSchema(
            analyze,
            SkyrimNpcFinishCoreProposal.SchemaIdentifier);
        JsonElement externalProposalSchema = FindDocumentSchema(
            analyze,
            SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier);
        Require(HasBooleanType(
                    legacyProposalSchema,
                    "runtimeAuthority") &&
                HasBooleanType(currentProposalSchema, "runtimeAuthority") &&
                HasConst(externalProposalSchema, "runtimeAuthority") &&
                externalProposalSchema
                    .GetProperty("jsonSchema")
                    .GetProperty("properties")
                    .GetProperty("runtimeAuthority")
                    .GetProperty("const")
                    .GetBoolean() == false,
            "Finish Core proposal schema authority flags did not preserve permissive v1/v2 schemas and const false for external v3.");

        JsonElement legacyManifestSchema = FindDocumentSchema(
            verify,
            SkyrimNpcFinishCoreManifest.SchemaIdentifier);
        JsonElement externalManifestSchema = FindDocumentSchema(
            verify,
            SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier);
        Require(HasBooleanType(legacyManifestSchema, "runtimeAuthority") &&
                HasBooleanType(legacyManifestSchema, "visualAuthority") &&
                HasConst(externalManifestSchema, "runtimeAuthority") &&
                HasConst(externalManifestSchema, "visualAuthority") &&
                externalManifestSchema
                    .GetProperty("jsonSchema")
                    .GetProperty("properties")
                    .GetProperty("runtimeAuthority")
                    .GetProperty("const")
                    .GetBoolean() == false &&
                externalManifestSchema
                    .GetProperty("jsonSchema")
                    .GetProperty("properties")
                    .GetProperty("visualAuthority")
                    .GetProperty("const")
                    .GetBoolean() == false,
            "Finish Core manifest schema authority flags did not preserve permissive v1 and const false for external v2.");
    }

    private static JsonElement FindDocumentSchema(
        JsonElement commandSchema,
        string schemaIdentifier) => commandSchema
        .GetProperty("documentSchemas")
        .EnumerateArray()
        .Single(item => item.GetProperty("schemaIdentifier").GetString() == schemaIdentifier);

    private static bool HasBooleanType(
        JsonElement documentSchema,
        string propertyName)
    {
        JsonElement property = documentSchema
            .GetProperty("jsonSchema")
            .GetProperty("properties")
            .GetProperty(propertyName);
        return property.GetProperty("type").GetString() == "boolean";
    }

    private static bool HasConst(
        JsonElement documentSchema,
        string propertyName) => documentSchema
        .GetProperty("jsonSchema")
        .GetProperty("properties")
        .GetProperty(propertyName)
        .TryGetProperty("const", out _);

    private static bool ExternalAuthorityEquals(
        SkyrimNpcFinishCoreExternalHeadPartAuthority? actual,
        SkyrimNpcFinishCoreExternalHeadPartAuthority expected) =>
        actual is not null &&
        actual.SelectedManifestPath == expected.SelectedManifestPath &&
        actual.SelectedManifestSha256 == expected.SelectedManifestSha256 &&
        actual.Bindings.SequenceEqual(expected.Bindings);

    private static Sha256Hash Hash(string value) =>
        new(Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))));

    private static void ExpectInvalid(Action action, string label)
    {
        try
        {
            action();
        }
        catch (Exception exception) when (exception is InvalidDataException or
            ArgumentException or JsonException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected the Finish Core external codec to refuse {label}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

}
