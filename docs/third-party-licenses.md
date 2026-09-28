> Current publication is source only: application executables and all native DLLs
> are excluded. Binary inventories below describe retained development evidence
> and local build prerequisites, not files shipped by this source snapshot.
> See [source-only publication](source-only-publication.md).

# Third-party licenses and notices

## Scope and status

This inventory records the exact package IDs and versions in the Preview.280
SPDX report and the license declarations in their NuGet package specifications.
The report contains 36 package rows, and every row currently has
licenseDeclared=NOASSERTION and licenseConcluded=NOASSERTION. The table below
is a separately verified metadata inventory; it does not silently rewrite that
SBOM or establish that the Preview.280 ZIP satisfies redistribution duties.

The NuGet license counts are 11 GPL-3.0-only expressions, 17 MIT expressions,
one MIT OR Unlicense expression, six older package specifications without an
SPDX expression (four MIT and two GPL-3.0-only by the versioned upstream
license records), and one package-supplied license file. Thus the package
contents represent 14 GPL-3.0-only packages, 21 MIT-licensed packages, and one
package for which the Unlicense alternative is selected below.

## NuGet package inventory

Each package specification link is the exact NuGet flat-container nuspec for
that ID and version. Source links reflect repository/ref metadata recorded by
the package, with versioned commits supplied where the nuspec provides them.
An author field is not described as a copyright notice unless the source
license or package copyright field says so.

| Package | Version | Declared or source-confirmed license | Attribution recorded | Package specification | Source reference |
| --- | --- | --- | --- | --- | --- |
| BCnEncoder.Net | 2.3.0 | MIT OR Unlicense | NuGet author metadata: Nominom | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/bcnencoder.net/2.3.0/bcnencoder.net.nuspec) | [source revision](https://github.com/Nominom/BCnEncoder.NET/tree/74eda8a330759d81e6307f19aefc547e40687825) |
| CommunityToolkit.HighPerformance | 8.4.0 | MIT | (c) .NET Foundation and Contributors. All rights reserved. | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/communitytoolkit.highperformance/8.4.0/communitytoolkit.highperformance.nuspec) | [source revision](https://github.com/CommunityToolkit/dotnet/tree/638b41dad30dffabb123a39aa38eabc7e3721371) |
| DynamicData | 9.4.1 | MIT | Copyright (c) Roland Pheasant 2011-2025 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/dynamicdata/9.4.1/dynamicdata.nuspec) | [source revision](https://github.com/reactiveui/DynamicData/tree/758ef92ae5b41a78f937add6a9438c9fed39481d) |
| FluentResults | 3.15.2 | MIT | Copyright 2022 (c) Michael Altmann. All rights reserved. | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/fluentresults/3.15.2/fluentresults.nuspec) | [source revision](https://github.com/altmann/FluentResults/tree/8b675de7e8b6e4c0921e95138c99e15fd52cde40) |
| GameFinder.Common | 4.4.0 | GPL-3.0-only | NuGet author metadata: erri120 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/gamefinder.common/4.4.0/gamefinder.common.nuspec) | [source revision](https://github.com/erri120/GameFinder/tree/bb462fbf9babae29375e29ec6833d8b595b3ab27) |
| GameFinder.RegistryUtils | 4.4.0 | GPL-3.0-only | NuGet author metadata: erri120 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/gamefinder.registryutils/4.4.0/gamefinder.registryutils.nuspec) | [source revision](https://github.com/erri120/GameFinder/tree/bb462fbf9babae29375e29ec6833d8b595b3ab27) |
| GameFinder.StoreHandlers.GOG | 4.4.0 | GPL-3.0-only | NuGet author metadata: erri120 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/gamefinder.storehandlers.gog/4.4.0/gamefinder.storehandlers.gog.nuspec) | [source revision](https://github.com/erri120/GameFinder/tree/bb462fbf9babae29375e29ec6833d8b595b3ab27) |
| GameFinder.StoreHandlers.Steam | 4.4.0 | GPL-3.0-only | NuGet author metadata: erri120 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/gamefinder.storehandlers.steam/4.4.0/gamefinder.storehandlers.steam.nuspec) | [source revision](https://github.com/erri120/GameFinder/tree/bb462fbf9babae29375e29ec6833d8b595b3ab27) |
| GameFinder.StoreHandlers.Xbox | 4.4.0 | GPL-3.0-only | NuGet author metadata: erri120 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/gamefinder.storehandlers.xbox/4.4.0/gamefinder.storehandlers.xbox.nuspec) | [source revision](https://github.com/erri120/GameFinder/tree/bb462fbf9babae29375e29ec6833d8b595b3ab27) |
| GameFinder.Wine | 4.4.0 | GPL-3.0-only | NuGet author metadata: erri120 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/gamefinder.wine/4.4.0/gamefinder.wine.nuspec) | [source revision](https://github.com/erri120/GameFinder/tree/bb462fbf9babae29375e29ec6833d8b595b3ab27) |
| ini-parser-netstandard | 2.5.3 | MIT | Ricardo Amores Hernández; exact maintainer-tag LICENSE retained in `licenses/nuget/ini-parser-2.5.3-LICENSE.txt` | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/ini-parser-netstandard/2.5.3/ini-parser-netstandard.nuspec) | [maintainer release 2.5.3 source](https://github.com/nano-byte/ini-parser/tree/b1beea584b23c65f7c9bcafd8ab9be62395f477a) |
| K4os.Compression.LZ4 | 1.3.8 | MIT | Milosz Krajewski | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/k4os.compression.lz4/1.3.8/k4os.compression.lz4.nuspec) | [K4os source tag 1.3.8](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/tree/1.3.8) |
| K4os.Compression.LZ4.Streams | 1.3.8 | MIT | Milosz Krajewski | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/k4os.compression.lz4.streams/1.3.8/k4os.compression.lz4.streams.nuspec) | [K4os source tag 1.3.8](https://github.com/MiloszKrajewski/K4os.Compression.LZ4/tree/1.3.8) |
| K4os.Hash.xxHash | 1.0.8 | MIT | Milosz Krajewski | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/k4os.hash.xxhash/1.0.8/k4os.hash.xxhash.nuspec) | [K4os source tag 1.0.8](https://github.com/MiloszKrajewski/K4os.Hash.xxHash/tree/1.0.8) |
| Loqui | 3.2.0 | GPL-3.0-only | NuGet author metadata: Noggog | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/loqui/3.2.0/loqui.nuspec) | [source revision](https://github.com/Noggog/Loqui/tree/a370205845bfe2e23749f31304a5fc9ee3d30ee0) |
| Microsoft.Extensions.DependencyInjection.Abstractions | 8.0.1 | MIT | © Microsoft Corporation. All rights reserved. | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.dependencyinjection.abstractions/8.0.1/microsoft.extensions.dependencyinjection.abstractions.nuspec) | [source revision](https://github.com/dotnet/runtime/tree/9f4b1f5d664afdfc80e1508ab7ed099dff210fbd) |
| Microsoft.Extensions.Logging.Abstractions | 6.0.1 | MIT | © Microsoft Corporation. All rights reserved. | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/microsoft.extensions.logging.abstractions/6.0.1/microsoft.extensions.logging.abstractions.nuspec) | [source revision](https://github.com/dotnet/runtime/tree/c24d9a9c91c5d04b7b4de71f1a9f33ac35e09663) |
| Mutagen.Bethesda.Core | 0.52.0 | GPL-3.0-only | 2024 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/mutagen.bethesda.core/0.52.0/mutagen.bethesda.core.nuspec) | [source revision](https://github.com/Mutagen-Modding/Mutagen/tree/2fdae6fd580beea74bdb5361e2333dd662b7f393) |
| Mutagen.Bethesda.Fallout4 | 0.52.0 | GPL-3.0-only | 2024 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/mutagen.bethesda.fallout4/0.52.0/mutagen.bethesda.fallout4.nuspec) | [source revision](https://github.com/Mutagen-Modding/Mutagen/tree/2fdae6fd580beea74bdb5361e2333dd662b7f393) |
| Mutagen.Bethesda.Kernel | 0.52.0 | GPL-3.0-only | 2024 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/mutagen.bethesda.kernel/0.52.0/mutagen.bethesda.kernel.nuspec) | [source revision](https://github.com/Mutagen-Modding/Mutagen/tree/2fdae6fd580beea74bdb5361e2333dd662b7f393) |
| Mutagen.Bethesda.Skyrim | 0.52.0 | GPL-3.0-only | 2024 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/mutagen.bethesda.skyrim/0.52.0/mutagen.bethesda.skyrim.nuspec) | [source revision](https://github.com/Mutagen-Modding/Mutagen/tree/2fdae6fd580beea74bdb5361e2333dd662b7f393) |
| NexusMods.Paths | 0.10.0 | GPL-3.0-only | NuGet author metadata: Nexus Mods | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/nexusmods.paths/0.10.0/nexusmods.paths.nuspec) | [source revision](https://github.com/Nexus-Mods/NexusMods.Paths/tree/6c7b575e96d712056e7c75cd366ac17e55925dbf) |
| Noggog.CSharpExt | 3.1.0 | GPL-3.0-only | NuGet author metadata: Noggog | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/noggog.csharpext/3.1.0/noggog.csharpext.nuspec) | [source revision](https://github.com/Noggog/CSharpExt/tree/ba722ca9ced2bada1dcede18beb91ce215f578aa) |
| OneOf | 3.0.271 | MIT | Harry McIntyre | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/oneof/3.0.271/oneof.nuspec) | [source revision](https://github.com/mcintyre321/OneOf/tree/6e02dbe75d0f20f198c640d4c04190a85c5ac9e6) |
| Reloaded.Memory | 9.4.1 | GPLv3 with MIT-origin components identified in package LICENSE.md | NuGet author metadata: Sewer56 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/reloaded.memory/9.4.1/reloaded.memory.nuspec) | [source revision](https://github.com/Reloaded-Project/Reloaded.Memory/tree/2c31a5e84aa06dbe8e22ed3859b79d8729618fb2) |
| SharpZipLib | 1.4.2 | MIT | Copyright © 2000-2022 SharpZipLib Contributors | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/sharpziplib/1.4.2/sharpziplib.nuspec) | [source revision](https://github.com/icsharpcode/SharpZipLib/tree/33f64eb0f28cdd2b084cb822fcc224c7c5aba553) |
| SkiaSharp | 3.119.4 | MIT | © Microsoft Corporation. All rights reserved. | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/skiasharp/3.119.4/skiasharp.nuspec) | [https://go.microsoft.com/fwlink/?linkid=868515](https://go.microsoft.com/fwlink/?linkid=868515) (metadata revision f568ac94dd768ef9a2f593537cfde2dd0d348ef5) |
| SkiaSharp.NativeAssets.macOS | 3.119.4 | MIT | © Microsoft Corporation. All rights reserved. | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/skiasharp.nativeassets.macos/3.119.4/skiasharp.nativeassets.macos.nuspec) | [https://go.microsoft.com/fwlink/?linkid=868515](https://go.microsoft.com/fwlink/?linkid=868515) (metadata revision f568ac94dd768ef9a2f593537cfde2dd0d348ef5) |
| SkiaSharp.NativeAssets.Win32 | 3.119.4 | MIT | © Microsoft Corporation. All rights reserved. | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/skiasharp.nativeassets.win32/3.119.4/skiasharp.nativeassets.win32.nuspec) | [https://go.microsoft.com/fwlink/?linkid=868515](https://go.microsoft.com/fwlink/?linkid=868515) (metadata revision f568ac94dd768ef9a2f593537cfde2dd0d348ef5) |
| StrongInject | 1.4.4 | MIT | Copyright (c) 2020 Yair Halberstadt (exact pinned source LICENSE) | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/stronginject/1.4.4/stronginject.nuspec) | [source revision](https://github.com/YairHalberstadt/stronginject/tree/6f3077c68b1ebd9c56663456efb67ef90886942e) |
| System.IO.Abstractions | 22.0.16 | MIT | Copyright © Tatham Oddie & friends 2010-2025 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/system.io.abstractions/22.0.16/system.io.abstractions.nuspec) | [source revision](https://github.com/TestableIO/System.IO.Abstractions/tree/863b7048305fbdb4d31cf4bb77da98aec7bede78) |
| System.Reactive | 6.0.1 | MIT | Copyright (c) .NET Foundation and Contributors. | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/system.reactive/6.0.1/system.reactive.nuspec) | [source revision](https://github.com/dotnet/reactive/tree/1cfc6465d1c9c6144d5b4e6420240f2767c8f85c) |
| TestableIO.System.IO.Abstractions | 22.0.16 | MIT | Copyright © Tatham Oddie & friends 2010-2025 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/testableio.system.io.abstractions/22.0.16/testableio.system.io.abstractions.nuspec) | [source revision](https://github.com/TestableIO/System.IO.Abstractions/tree/863b7048305fbdb4d31cf4bb77da98aec7bede78) |
| TestableIO.System.IO.Abstractions.Wrappers | 22.0.16 | MIT | Copyright © Tatham Oddie & friends 2010-2025 | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/testableio.system.io.abstractions.wrappers/22.0.16/testableio.system.io.abstractions.wrappers.nuspec) | [source revision](https://github.com/TestableIO/System.IO.Abstractions/tree/863b7048305fbdb4d31cf4bb77da98aec7bede78) |
| Testably.Abstractions.FileSystem.Interface | 9.0.0 | MIT | Copyright (c) 2024- 2025 Testably | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/testably.abstractions.filesystem.interface/9.0.0/testably.abstractions.filesystem.interface.nuspec) | [source revision](https://github.com/Testably/Testably.Abstractions/tree/e3f60b39df247720d64485067b2f30abcb9a63f1) |
| ValveKeyValue | 0.10.0.360 | MIT | Copyright © ValveKeyValue Contributors | [exact package nuspec](https://api.nuget.org/v3-flatcontainer/valvekeyvalue/0.10.0.360/valvekeyvalue.nuspec) | [source revision](https://github.com/ValveResourceFormat/ValveKeyValue/tree/1e95e84c83b32b1263912cdec188064a8fed0bdb) |

## Package-provided SkiaSharp notices

The exact `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` members from
SkiaSharp.NativeAssets.Win32 3.119.4 are retained at
`licenses/nuget/SkiaSharp.NativeAssets.Win32-3.119.4-LICENSE.txt` and
`licenses/nuget/SkiaSharp.NativeAssets.Win32-3.119.4-THIRD-PARTY-NOTICES.txt`.
Both files were compared byte-for-byte with the matching members of the
exact NuGet archive. The bundled notice identifies third-party material
incorporated by SkiaSharp and HarfBuzzSharp; it does not establish which
listed components are linked into the Windows application. The Preview.280
ZIP did not include these source-tree copies. The SBOM inventory also lists
SkiaSharp.NativeAssets.macOS, but no claim is made that macOS native assets
ship in the Windows application.

## License texts

- Actorwright source: GPL-3.0-only, see the repository LICENSE.
- Pinned FO4_NPC_Manager source: exact GPL text in licenses/upstream-fo4-npc-manager-LICENSE.
- GameFinder 4.4.0: licenses/nuget/GameFinder-4.4.0-GPL-3.0-LICENSE.txt.
- Mutagen 0.52.0, Loqui 3.2.0, and Noggog.CSharpExt 3.1.0: their exact pinned
  source license files have identical SHA-256 and are retained as
  licenses/nuget/Noggog-Mutagen-GPL-3.0-LICENSE.txt.
- NexusMods.Paths 0.10.0: licenses/nuget/NexusMods.Paths-0.10.0-GPL-3.0-LICENSE.md.
- Reloaded.Memory 9.4.1: exact package license file at
  licenses/nuget/Reloaded.Memory-9.4.1-LICENSE.md. It explains GPLv3 coverage
  and identifies some Microsoft Community Toolkit-origin components as MIT.
- MIT package terms and package notice strings: licenses/nuget/MIT-LICENSE.txt.
  The exact legacy source texts for K4os, OneOf, and StrongInject are also
  retained beside it. The ini-parser license is from the maintainer's exact
  2.5.3 release at `b1beea584b23c65f7c9bcafd8ab9be62395f477a`, retained as
  `licenses/nuget/ini-parser-2.5.3-LICENSE.txt`. The nuspec itself records no
  source commit; the maintainer tag and matching source versions establish
  this mapping. Byte-identical package reproduction is not claimed.
- BCnEncoder.Net 2.3.0 declares MIT OR Unlicense. The Unlicense alternative is
  used for this inventory; see licenses/nuget/Unlicense.txt and the exact
  package specification. Its package carries no standalone license member.

## Bundled native and framework payloads

The Preview.280 binaries are self-contained single-file .NET applications.
The pinned SDK reports .NET 10.0.9 runtime packs. The exact legal files from
SDK 10.0.301 are retained at licenses/native/dotnet-sdk-10.0.301-LICENSE.txt
and licenses/native/dotnet-sdk-10.0.301-ThirdPartyNotices.txt. The Preview.280
ZIP itself contains neither SDK file.

The restored project assets reference Microsoft.NETCore.App for the CLI, and
Microsoft.NETCore.App plus Microsoft.WindowsDesktop.App.WPF for the desktop
application; they do not reference ASP.NET. The current packaging script requests
self-contained, single-file win-x64 outputs, but these project references and
settings do not establish the exact contents of a future public archive. The
10.0.9 runtime-pack license texts and Core third-party notices are retained at
licenses/native/dotnet-runtime-10.0.9/LICENSE.TXT,
licenses/native/dotnet-runtime-10.0.9/THIRD-PARTY-NOTICES.TXT, and
licenses/native/dotnet-runtime-10.0.9/WINDOWS-DESKTOP-LICENSE.

The exact Core and Windows Desktop runtime-pack specifications declare MIT and
record the dotnet/dotnet Virtual Monolithic Repository source commit
[901ca941248413c79832d2fdbd709da0c4386353](https://github.com/dotnet/dotnet/tree/901ca941248413c79832d2fdbd709da0c4386353).
The [Windows .NET component licensing table](https://github.com/dotnet/core/blob/main/license-information-windows.md)
maps applicable CoreCLR/runtime files and specified WPF files to the [.NET
Library License](https://dotnet.microsoft.com/en-us/dotnet_library_license.htm).
It separately maps D3DCompiler_47_cor3.dll to the
[Windows SDK License](https://learn.microsoft.com/en-us/legal/windows-sdk/license).
The Library License permits object-code redistribution subject to its stated
conditions. Its Excluded License clause concerns terms imposed on Microsoft
source; it is not a blanket prohibition on distributing a GPL-licensed
application. These Microsoft conditions remain separate from Actorwright's GPL
obligations. This inventory does not verify the final public archive's runtime
members or whether a particular distributor satisfies all applicable terms.

The release metadata records embedded MediaPipe, OpenCV, VC runtime DLLs, and
MediaPipe model files. The pinned MediaPipe root Apache-2.0 license and OpenCV
3.4.10 BSD-3-Clause license are retained under licenses/native. The full set
of 27 license and notice files supplied in the upstream OpenCV 3.4.10 binary
distribution is retained under licenses/native/opencv-3.4.10; it includes
notices for optional distribution components, and is not evidence that every
listed component is linked into this build. The model bytes match official
version-1 MediaPipe downloads; their official model cards state Apache-2.0.
The source comparison for the custom MediaPipe build is in
provenance/mediapipe/source-delta.json.

The current runtime tree pins the reviewed OpenCV 3.4.10 DLL built with IPP and IPP IW disabled (SHA-256 98EAF023A55C1A50904C9EBB6AD20348A1654C394C8359ED95EE878D58C31291). Its four non-OS imports are recorded in the runtime verifier; all 13 imports from the current MediaPipe r2 DLL resolved against this exact candidate. The earlier official IPP-enabled binary and its vendor notices remain in the retained upstream distribution inventory; they do not describe the current runtime, and no byte-equivalence is claimed.

The MediaPipe r2 build record and target notice index bind the current DLL to its bounded configured inputs, upstream archive hashes, ordered Glog patches, and conservative notice copies. The source archives are not included in this checkout, so this is not a completed corresponding-source offer. Configured inputs and archive notices do not prove every archive member or header contributed emitted code.

The package also embeds Microsoft VC runtime DLLs. Microsoft limits
redistribution of its VC runtime files to eligible Visual Studio licensees and
specified files. The publisher attested to an eligible Visual Studio license
on 2026-09-28; each redistributor must establish their own eligibility.
See Microsoft's [VC++ redistribution guidance](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files?view=msvc-170).

## User-installed external tools

The following tools are external prerequisites, not files in the Actorwright
release archive. Obtain the named versions from the provider and retain their
licenses with the local installation:

- Blender 4.5.1 Windows x64: Blender GPL-3.0-or-later; see the
  [official license page](https://www.blender.org/about/license/).
- PyNifly 27.4.0: the exact
  [V27.4.0 release](https://github.com/BadDogSkyrim/PyNifly/releases/tag/V27.4.0)
  is GPL-3.0; it is not bundled.
- DirectXTex Texconv 2026.5.7: executable identity is pinned to the Microsoft
  DirectXTex tag may2026. Its exact MIT text is retained at
  licenses/native/DirectXTex-may2026-LICENSE.txt and is available at the
  [versioned upstream license](https://raw.githubusercontent.com/microsoft/DirectXTex/may2026/LICENSE).
  Texconv remains an external installation and is not bundled.

## Papyrus source and privacy status

The NPCM_Manolov_ApplySSE source is modified relative to the pinned
FO4_NPC_Manager source; its upstream attribution and derivation are recorded in
docs/migration/fo4-emitter-provenance.json, with the pinned upstream GPL text
retained in licenses/upstream-fo4-npc-manager-LICENSE.

The sanitized historical ActorwrightFollowerDialogue compile transcript remains
for provenance; it is not the compiler record for the current v2 asset. The
public PEX was compiled from the unchanged PSC with pinned Caprica and
`--anonymize`; the compiler exit code was captured as 0. Independent PEX
inspection reported a valid Skyrim PEX, and API/behavior comparisons against
the replaced asset reported equivalence with zero differences. The PEX debug
identity names the public source snapshot and contains no detected personal
or private staging path markers. This static check is not a game-runtime test,
and the follower-dialogue manifest keeps runtimeAuthority=false.

## Exact Preview.280 ZIP limitation

The inspected ZIP contains only licenses/LICENSE and licenses/NOTICE. It does
not contain this package inventory, the additional license texts, a complete
corresponding-source package, or the .NET/native notices listed above. Its
release record marks it privateOnly=true. This source-tree inventory is not a
claim that the existing ZIP is cleared for public redistribution.