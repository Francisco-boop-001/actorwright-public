# MediaPipe native source delta

source-delta.json identifies the upstream source and indexes the adjacent
patch. The patch is for MediaPipe commit
f8ef212d5c962c0e853db7e59d217056b187084b; the upstream project is
[google-ai-edge/mediapipe](https://github.com/google-ai-edge/mediapipe).
The compared upstream source archive has SHA-256
dc18a99bc9021ffdc5ec4134df623f0d3b62f824edb44d9d326a708807475676.

To recreate the reviewed source delta:

1. Check out that exact commit with line-ending conversion disabled for each
   checkout/apply operation:

       git -c core.autocrlf=false clone --no-checkout https://github.com/google-ai-edge/mediapipe.git mediapipe-source
       git -C mediapipe-source -c core.autocrlf=false checkout --detach f8ef212d5c962c0e853db7e59d217056b187084b
       git -C mediapipe-source -c core.autocrlf=false apply --check <path-to-this-directory>/source-delta.patch
       git -C mediapipe-source -c core.autocrlf=false apply <path-to-this-directory>/source-delta.patch

2. Verify the five modified files against
   comparison.modifiedFiles[*].reviewedBuildTreeSha256 and the added export
   definition against comparison.addedFiles[*].reviewedBuildTreeSha256 in
   source-delta.json. The adjacent manifest also binds the patch SHA-256.
   The reviewed build tree omitted three upstream Android resource symlinks,
   listed in the manifest; this patch does not recreate them.

This is the reviewed source delta, not a complete MediaPipe source package or
a reproducible native build recipe. The full source archive, generated build
tree, original build logs, and complete static dependency/notice closure are
not included here. See ../../docs/third-party-licenses.md for the remaining
native licensing limitations.
## Current native candidate record

The current MediaPipe and no-IPP OpenCV runtime bytes, r2 build controls, ordered Glog patches, declared target source inputs, and isolated native check results are bound in native-build-r2.json. The separate glog-static-noexports.patch follows the two Glog patches in the pinned MediaPipe source tree; the build script checks the resulting bazel/glog.bzl hash. This is not a claim of equivalence to earlier DLL bytes or a complete public corresponding-source offer.
