# License and distribution status

Current publication is [source only](source-only-publication.md). No native DLLs
or application executable packages are distributed. Historical binary findings
below remain context for a future binary release, not claims about this snapshot.

Actorwright source is licensed under GPL-3.0-only. The repository LICENSE is
the project license. NOTICE records the pinned MANOLOV02/FO4_NPC_Manager
baseline and the exact upstream GPL text retained at
licenses/upstream-fo4-npc-manager-LICENSE. The derived Papyrus emitter record
is docs/migration/fo4-emitter-provenance.json.

## Preview.280 evidence

The inspected Preview.280 ZIP contains 36 NuGet packages in its SPDX report.
All package licenseDeclared and licenseConcluded fields are NOASSERTION. Exact
NuGet nuspec records establish the license declarations summarized in
[third-party licenses](third-party-licenses.md), but that audit has not updated
the SBOM. The ZIP includes only licenses/LICENSE and licenses/NOTICE; it does
not include the package inventory or the component licenses and notices listed
in the source tree. The release record explicitly marks the ZIP privateOnly.

The private binary packaging route produces self-contained executables, embedding .NET
10.0.9 and the native reference payload. Exact legal files from the pinned
10.0.301 SDK are now retained under licenses/native.

The current runtime tree pins the reviewed OpenCV 3.4.10 build with IPP and IPP IW disabled (SHA-256 98EAF023A55C1A50904C9EBB6AD20348A1654C394C8359ED95EE878D58C31291). The hash-bound MediaPipe r2 candidate passed exact export/import checks and isolated native loader/inference checks. See [third-party licenses](third-party-licenses.md) for source and redistribution limits.

## ini-parser source-version note

The ini-parser-netstandard 2.5.3 nuspec declares MIT but records no source
commit. The maintainer's nano-byte/ini-parser release tag 2.5.3 resolves to
`b1beea584b23c65f7c9bcafd8ab9be62395f477a`; its source and assembly versions
match the package. The exact release license is retained at
`licenses/nuget/ini-parser-2.5.3-LICENSE.txt`. The source project uses package
ID `ini-parser`; producing the distributed ID requires a `PackageId`
override. This identifies the release source without claiming the historical
packing command or byte-identical package reproduction.

## Conditions still open

- GPL-licensed NuGet components have exact version/source/license evidence,
  but the inspected ZIP has no corresponding-source arrangement for those
  dependencies and no complete per-component notice set.
- The MediaPipe r2 build record binds its configured link libraries, compile-input roots, upstream archive hashes, patches, and conservative notice set. The source archives are not in this checkout, so a complete corresponding-source offer remains pending. The current OpenCV no-IPP build is hash-bound in the runtime manifest; source and notice obligations remain. See [third-party licenses](third-party-licenses.md) for the limits and open redistribution qualifications.
- The bundled VC runtime DLLs require the distributor to qualify under
  Microsoft's redistribution terms. The publisher attested to an eligible
  Visual Studio license on 2026-09-28. Exact-file redistribution and notice
  obligations remain part of the candidate's release receipt.
- Both bundled Skyrim PEX files use portable source names and empty build-user
  and machine fields. The documented header postprocess is required because
  the pinned compiler's `--anonymize` option does not clear those fields.
  Postprocessing preserves the compiled payload bytes; inspection and API/
  behavior comparisons passed with zero differences. This is static validation,
  not game-runtime proof; the manifests keep `runtimeAuthority=false`.

Blender, PyNifly, and Texconv remain external user installations. Their exact
supported-version licensing evidence and official provider links are in
[third-party licenses](third-party-licenses.md).
## Optional Caprica source build

The public snapshot includes a source-only build packet for Orvid/Caprica, pinned to the upstream commit and patched for Actorwright's Skyrim Papyrus sources. See the [Caprica source-build packet](../tools/compilers/caprica/README.md) for its license, patches, toolchain, and user-supplied input requirements.

This packet does not include the private hash-pinned runtime-script inspection distribution or its manifests. It is not a drop-in replacement for that tool, so the runtime-script build command remains unavailable in this snapshot. Core application builds do not use Caprica, and runtime-script package copies a supplied precompiled PEX without invoking it.

A license declaration establishes the terms recorded by that package; it does not by itself establish that a particular compiled package includes required notices, source, attribution, or distributor qualification. The existing Preview.280 ZIP is not cleared for public redistribution by this document.