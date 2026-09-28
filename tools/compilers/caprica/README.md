# Building Caprica for Actorwright Papyrus sources

This source-only packet applies three small patches to Orvid/Caprica commit e4dee0860914d75e770d3f9ab374f7aba474b701:

1. 0001-skyrim-native-declarations.patch permits Skyrim native declarations while retaining the upstream restriction for other games.
2. 0002-pex-object-length-endian.patch corrects the Skyrim big-endian PEX object length. The upstream little-endian extent is unchanged.
3. 0003-vcpkg-pugixml-target-build-only.patch uses the pugixml::pugixml target exported by vcpkg; it changes build integration only.

Caprica is MIT-licensed; the upstream license and copyright are included as LICENSE.Caprica. The patches target only the listed upstream commit. They do not include the private PEX inspection/analysis extensions and are not a drop-in replacement for the private hash-pinned runtime-script build tool.

## Build

Use an x64 Native Tools Command Prompt for Visual Studio 2022. The locally verified toolchain was Visual Studio 2022 17.10.4 / MSVC 19.40.33812, CMake 3.28.3, Ninja 1.11.0, and vcpkg 2024-03-14 (7d353e869753e5609a1f1a057df3db8fd356e49d). The verified x64 package cache had Boost 1.83.0, fmt 9.1.0, and pugixml 1.13.0; the pinned Caprica checkout's vcpkg.json is the dependency manifest and source of its package baseline.

Set VCPKG_ROOT to a vcpkg checkout and ACTORWRIGHT_PACKET to this packet directory. In that same x64 Native Tools Command Prompt, run:

~~~cmd
set "VCPKG_ROOT=C:\path\to\vcpkg"
set "ACTORWRIGHT_PACKET=C:\path\to\Actorwright\tools\compilers\caprica"
git clone https://github.com/Orvid/Caprica.git Caprica
if errorlevel 1 exit /b 1
cd /d Caprica
if errorlevel 1 exit /b 1
git checkout e4dee0860914d75e770d3f9ab374f7aba474b701
if errorlevel 1 exit /b 1
git apply "%ACTORWRIGHT_PACKET%\patches\0001-skyrim-native-declarations.patch"
if errorlevel 1 exit /b 1
git apply "%ACTORWRIGHT_PACKET%\patches\0002-pex-object-length-endian.patch"
if errorlevel 1 exit /b 1
git apply "%ACTORWRIGHT_PACKET%\patches\0003-vcpkg-pugixml-target-build-only.patch"
if errorlevel 1 exit /b 1
git diff --check
if errorlevel 1 exit /b 1

cmake -S . -B build -G Ninja ^
  "-DCMAKE_TOOLCHAIN_FILE=%VCPKG_ROOT%\scripts\buildsystems\vcpkg.cmake" ^
  -DCMAKE_BUILD_TYPE=Release ^
  -DVCPKG_TARGET_TRIPLET=x64-windows
if errorlevel 1 exit /b 1
cmake --build build --config Release --target Caprica --parallel 1
if errorlevel 1 exit /b 1
~~~

The pinned upstream vcpkg.json supplies the dependency baseline. The command above uses the pinned manifest for a fresh package install, but that fresh-install path was not exercised in the local validation. The local validation used an already populated x64 package cache with manifest mode disabled. Both routes link the exported pugixml::pugixml target. The local validation built only the Caprica target with one Ninja worker. It did not build or require a private inspector.

## Compile Actorwright scripts

Obtain the external inputs from their original sources and install them locally:

- Skyrim Special Edition: [Steam store page](https://store.steampowered.com/app/489830/SKYRIM_SPECIAL_EDITION/).
- Creation Kit: install the [Skyrim Special Edition Creation Kit on Steam](https://store.steampowered.com/app/1946180/Skyrim_Special_Edition_Creation_Kit/). [Bethesda Support](https://help.bethesda.net/app/answers/detail/a_id/55366/) documents that it is available in Steam under Software.
- SKSE64: download the build matching the installed Skyrim runtime from the [official SKSE site](https://skse.silverlock.org/); source is also published in the [official repository](https://github.com/ianpatt/skse64).
- RaceMenu and its NiOverride Modders Package: use the [RaceMenu files page on Nexus Mods](https://www.nexusmods.com/skyrimspecialedition/mods/19080?tab=files). Use the package's local Papyrus source folder containing NiOverride.psc, subject to the author's current terms.

The public source release includes two compiled Actorwright runtime PEX files. This Caprica packet does not include Skyrim, the Creation Kit, SKSE, RaceMenu, their flags or imported PSC sources, the private PEX analysis tool, or compiled implementations of imported external scripts. These commands compile Actorwright's own PSC sources into a separate scratch directory, then sanitize only the shipped PEX header metadata; they do not add those outputs to the release.

Caprica writes the local user and computer names into Skyrim PEX headers even when `--anonymize` is supplied. After each compile, run the repository sanitizer shown below. It validates the Skyrim v3.2 header and expected script basename, sets the source field to a repository-relative path, clears the two local identity fields, and preserves every byte from the string-table count onward. It does not alter the PSC source or recompile the script.

Set the paths below to your local installations. Point VANILLA_SCRIPTS and SKSE_VANILLA_SCRIPTS at folders containing their respective PSC declarations, and RACEMENU_SCRIPTS at the Modders Package folder containing NiOverride.psc. Use new output paths; the commands refuse to reuse existing ones. Caprica resolves duplicate imported script names in argument order, so list SKSE before vanilla for the NPCM script to prefer SKSE's augmented declarations.

~~~cmd
set "ACTORWRIGHT_ROOT=C:\path\to\Actorwright"
set "CAPRICA=C:\path\to\Caprica\build\Caprica\Caprica.exe"
set "FLAGS=C:\path\to\TESV_Papyrus_Flags.flg"
set "VANILLA_SCRIPTS=C:\path\to\vanilla\Scripts"
set "SKSE_VANILLA_SCRIPTS=C:\path\to\skse\scripts\vanilla"
set "RACEMENU_SCRIPTS=C:\path\to\RaceMenu\ModdersPackage\Scripts"
set "OUT_ROOT=C:\temp\actorwright-psc"

if not exist "%OUT_ROOT%" mkdir "%OUT_ROOT%"
if errorlevel 1 exit /b 1
if exist "%OUT_ROOT%\dialogue" (echo Choose a fresh output path. & exit /b 1)
mkdir "%OUT_ROOT%\dialogue"
if errorlevel 1 exit /b 1
"%CAPRICA%" --game=skyrim "--flags=%FLAGS%" "--output=%OUT_ROOT%\dialogue" --ignorecwd --async-write=0 --anonymize "--import=%VANILLA_SCRIPTS%" "%ACTORWRIGHT_ROOT%\runtime\skyrimse\Source\Scripts\ActorwrightFollowerDialogue.psc"
if errorlevel 1 exit /b 1
python "%ACTORWRIGHT_ROOT%\tools\compilers\caprica\sanitize_pex_metadata.py" "%OUT_ROOT%\dialogue\ActorwrightFollowerDialogue.pex"
if errorlevel 1 exit /b 1

if exist "%OUT_ROOT%\npcm" (echo Choose a fresh output path. & exit /b 1)
mkdir "%OUT_ROOT%\npcm"
if errorlevel 1 exit /b 1
"%CAPRICA%" --game=skyrim "--flags=%FLAGS%" "--output=%OUT_ROOT%\npcm" --ignorecwd --async-write=0 --anonymize "--import=%SKSE_VANILLA_SCRIPTS%" "--import=%VANILLA_SCRIPTS%" "--import=%RACEMENU_SCRIPTS%" "%ACTORWRIGHT_ROOT%\runtime\skyrimse\Source\Scripts\NPCM_Manolov_ApplySSE.psc"
if errorlevel 1 exit /b 1
python "%ACTORWRIGHT_ROOT%\tools\compilers\caprica\sanitize_pex_metadata.py" "%OUT_ROOT%\npcm\NPCM_Manolov_ApplySSE.pex"
if errorlevel 1 exit /b 1
~~~

The retained local compatibility smoke used a Papyrus test-source checkout and local SKSE declarations, not verified official Creation Kit or RaceMenu packages. It establishes compatibility only with those exact local inputs; official-provider compatibility and in-game behavior remain unverified. The smoke's private evidence records exact input and output hashes. The private PEX analyzer used for that comparison is not part of this source-build route.

## Review and verification boundary

- Confirmed: the exact base commit and all three patches apply cleanly; the Release Caprica target builds from pinned source with one Ninja worker; both public PSCs compile in Skyrim mode with no diagnostics; the authenticated local PEX parser reports one expected Skyrim object per output with the expected object and parent, and API/behavior comparison reports equivalence to the shipped PEX.
- Not verified: compilation against official Creation Kit, SKSE, or RaceMenu source packages; in-game behavior; a non-Skyrim object-length convention. The local imports are from Papyrus test-source inputs and prove compatibility only with those exact inputs. The little-endian writer extent remains byte-for-byte equivalent to pinned upstream; the four-byte-inclusive correction is only on the Skyrim big-endian path.
- The local PEX analyzer does not validate the encoded object-length field. Champollion's [public parser](https://github.com/Orvid/Champollion/blob/main/Pex/FileReader.cpp) recognizes little-endian PEX but discards the length while reading an object. No little-endian PEX fixture was found in the local bounded corpus; its 24 PEX samples are Skyrim big-endian. The current output-length value was therefore not independently reread in this bounded run.
