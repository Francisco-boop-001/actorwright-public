# Pinned MediaPipe dependency notice texts

Status: **notice-text preparation only**. The staged texts are exact files fetched from each candidate’s official upstream at its pinned commit or release tag. This does not establish the final DLL’s exact link membership, complete nested third-party notice closure, corresponding source completeness, redistribution clearance, or legal advice.

## Scope and result

The retained MediaPipe build table identifies these 11 target-relevant candidates: Abseil, glog, protobuf, TensorFlow, XNNPACK, cpuinfo, pthreadpool, Eigen, stb_image, zlib, and gflags. The target is link-static, while its exact historical Bazel action/link graph is unavailable. The collection therefore follows the conservative candidate list; it does not claim every listed project is embedded in the shipped DLL.

Sixteen upstream text files are staged under this directory. index.json records, for each file, its official source URL, pinned upstream ref, archive hash from the retained pin table, byte length, and local SHA-256. All requested exact URLs were accessible. GitHub/GitLab root contents were checked at the same refs for separate root-level NOTICE files; no separate root NOTICE was listed for these repositories. That bounded check does not cover nested notices or notices associated with files pulled from submodules.

## License and attribution details to preserve

- Abseil and TensorFlow provide Apache-2.0 root license texts. The TensorFlow Lite transitive notice set remains unverified.
- glog’s COPYING includes the Google BSD-style terms and a separate gettimeofday/Jouni Malinen attribution and terms. Retain the whole file.
- protobuf’s BSD-3-Clause text includes generated-code ownership and support-library language; retain it if protobuf materials are redistributed.
- XNNPACK is BSD-3-Clause; cpuinfo and pthreadpool are BSD-2-Clause.
- Eigen’s COPYING.README states that Eigen is primarily MPL-2.0 and that some files carry third-party BSD or other compatible terms. The collected root COPYING.* texts are preserved, including the Minpack attribution; the exact Eigen files used by the final target were not identified.
- stb’s license offers MIT or Unlicense. Both texts are retained; no license choice is asserted.
- zlib’s license and gflags’ BSD-3-Clause text are retained.

These summaries describe the collected upstream texts; they do not decide which alternative to exercise for stb, determine applicability of file-specific Eigen notices, or close any other nested license obligations.

## Limits and next evidence

The selected target’s source graph and pins make these projects plausible notice candidates, not a verified binary bill of materials. In particular, the current record does not prove TensorFlow Lite’s nested closure, XNNPACK’s FP16 linkage, or the exact Eigen files included. The notice set can be used as a conservative candidate bundle while those facts remain open, subject to review of any additional applicable notices discovered from retained source or a later action record.

During the initial notice-text collection phase, no source archives or full
dependency trees were downloaded, and no build, pin change, or native binary
change was made. This folder remains notice-preparation evidence; it is not a
complete Corresponding Source offer or final redistribution clearance.

## Current target notice copies

The top-level index.json covers the original 16 root notice texts. The hash-
bound r2 target notice set is listed in target-closure/index.json. That index
contains 25 target-root copies, 47 nested archive copies, and one TensorFlow root
source license (73 indexed rows total). This conservative notice coverage does
not prove that every listed nested component is linked. The target record carries
the source hashes and declared-input limits. Source archives are not bundled
in this checkout.
