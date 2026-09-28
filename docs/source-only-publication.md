# Source-only publication

This repository is a cleaned Preview.281 source snapshot, published by the
owner's explicit choice to omit application binaries. It is not a verified
binary release. The previous private repository, history, installed app,
and candidate executables are not published here.

## Excluded native inputs

No native DLL is included. The existing build/runtime contracts still require
these files locally under `runtime/reference-preset/`:

- `libmediapipe.dll`: build the pinned upstream source and reviewed patches
  using [MediaPipe provenance](../provenance/mediapipe/).
- `opencv_world3410.dll`: use the pinned OpenCV 3.4.10 no-IPP configuration
  recorded in the native provenance and [component inventory](third-party-licenses.md).
- `concrt140.dll`, `msvcp140.dll`, `vcruntime140.dll`, and `vcruntime140_1.dll`:
  obtain the applicable Microsoft x64 runtime files under Microsoft's terms.

The exact admitted versions, lengths, and SHA-256 values remain in
[`runtime-asset-manifest.json`](../runtime/reference-preset/runtime-asset-manifest.json).
A newer or differently built file is not automatically accepted. Installing
Microsoft's system redistributable alone does not satisfy the current app-local
inventory: the current loader requires those exact local files. This publication
does not introduce a system-runtime loader or relax hash checks.

Official providers:

- [Microsoft Visual C++ runtime downloads](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)
- [Microsoft redistribution terms](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files)
- [MediaPipe source](https://github.com/google-ai-edge/mediapipe)
- [OpenCV source](https://github.com/opencv/opencv/tree/3.4.10)

Locally acquired/generated DLLs are ignored by Git. Do not redistribute a local
package merely because it builds: the [binary-release gate](security/public-release-gate.md)
and third-party obligations still apply. Microsoft runtime DLLs are excluded
from this distribution; see the [FSF Windows-runtime guidance](https://www.gnu.org/licenses/gpl-faq.en.html#WindowsRuntimeAndGPL).

## Included assets and other prerequisites

The source retains its licensed model data, deterministic test fixtures, and
two Actorwright Papyrus PEX assets together with their PSC sources and
[compiler/postprocessing instructions](../tools/compilers/caprica/README.md).
These are not an application executable release. The PEX headers have portable
source names and empty build-user/machine fields; their API/behavior comparison
and byte-preserved compiled payload checks passed. No in-game proof is claimed.

Use the pinned .NET SDK and the external tools documented in
[renderer prerequisites](external-rendering-prerequisites.md). Game files,
Creation Kit, SKSE, RaceMenu, appearance providers, and their imported sources
must be acquired separately under their own terms. They are not supplied here.

## Verification limits

Privacy and source-scope review apply to this snapshot. Earlier focused tests
are development evidence, not a complete release result for this final source.
The application was not rebuilt after the final PEX/header changes. Fresh
compatibility capture, clean-clone full build/package checks, NTFS CI, final
SBOM, and binary/source-offer sealing remain pending for any future binary release.

Repository Actions are disabled for this source-only publication to avoid
launching expensive builds with deliberately absent native inputs. The existing
workflow is retained for future use after prerequisites are supplied. This is
not a passing CI result.

Authoring workflows still require a K: workspace. Set
`ACTORWRIGHT_PROTECTED_ROOT` for your own game before use. No Skyrim/Fallout 4
in-game behavior or visual quality is certified by this publication.
