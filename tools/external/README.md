# Local external tools

Only this README belongs in Git beneath tools/external. Install third-party
payloads beneath the active K-local workspace root and retain their upstream
licenses. Do not place mod projects, presets, or game state here. Generated
tools and resources remain ignored. The pinned .NET SDK uses
tools/scripts/bootstrap-dotnet10.ps1 and its separate SDK manifest.

tools/manifests/external-tools.json records renderer release URLs and exact
SHA-256 pins. The bootstrap verifies Blender 4.5.1's complete archive and
embedded executable, the PyNifly 27.4.0 archive and all 76 non-transient
profile files, and the DirectXTex texconv 2026.5.7 release executable before
publishing install directories. It refuses changed bytes, extra PyNifly files,
and occupied partial install targets. It retains the full Blender
distribution, creates the profile's config and data directories, and never
overwrites an existing installation.

Restore or verify pinned downloads from a K-local workspace:

    pwsh -NoProfile -File .\tools\scripts\bootstrap-renderer.ps1

To verify local downloads without installing them, pass BlenderArchivePath,
PyNiflyArchivePath, and TexconvExecutablePath together with VerifyArchivesOnly.
Downloads are cached under ignored artifacts/tools/renderer-downloads.

The product sets BLENDER_USER_CONFIG, BLENDER_USER_SCRIPTS, and
BLENDER_USER_DATA to the profile's config, scripts, and data directories. It
performs its own dependency admission before execution. File verification
alone does not prove rendering or game behavior. See
docs/external-rendering-prerequisites.md for the admission and license
contract.
