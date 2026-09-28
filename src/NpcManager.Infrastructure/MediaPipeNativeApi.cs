using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class MediaPipeNativeApi
    : IReferenceFaceInferenceNativeApi, IDisposable
{
    private static readonly Sha256Hash EmptyHash =
        new(new string('0', 64));

    private static readonly (string Path, string Role)[] ExpectedAssets =
    [
        ("libmediapipe.dll", "native-c-api"),
        ("opencv_world3410.dll", "opencv-runtime"),
        ("concrt140.dll", "vc-runtime-concurrency"),
        ("msvcp140.dll", "vc-runtime-cpp"),
        ("vcruntime140.dll", "vc-runtime-core"),
        ("vcruntime140_1.dll", "vc-runtime-core-1"),
        ("blaze_face_short_range.tflite", "face-detector-model"),
        ("face_landmarker.task", "face-landmarker-model")
    ];

    private static readonly string[] NativeDependencyLoadOrder =
    [
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "msvcp140.dll",
        "concrt140.dll",
        "opencv_world3410.dll",
        "libmediapipe.dll"
    ];

    private readonly WorkspacePath _labRoot;
    private readonly WorkspacePath _runtimeRoot;
    private readonly ApplicationResourceRuntimeAuthority?
        _applicationRuntimeAuthority;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private int _disposed;

    private bool IsDisposed =>
        Volatile.Read(ref _disposed) != 0;

    public MediaPipeNativeApi(
        WorkspacePath labRoot,
        WorkspacePath runtimeRoot)
    {
        if (!runtimeRoot.IsUnder(labRoot))
        {
            throw new ArgumentException(
                "The MediaPipe runtime must remain under the admitted lab root.",
                nameof(runtimeRoot));
        }

        _labRoot = labRoot;
        _runtimeRoot = runtimeRoot;
    }

    public MediaPipeNativeApi(
        ApplicationResourceRuntimeAuthority runtimeAuthority)
    {
        ArgumentNullException.ThrowIfNull(runtimeAuthority);
        _labRoot = new WorkspacePath(
            runtimeAuthority.ResourceBase.Value);
        _runtimeRoot = new WorkspacePath(
            runtimeAuthority.RuntimeRoot.Value);
        _applicationRuntimeAuthority = runtimeAuthority;
    }

    public async ValueTask<ReferenceFaceNativeInferenceResult> InferAsync(
        ReferenceFaceInferenceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            ReferenceFaceNativeInferenceResult result =
                InferCore(request, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _serial.Release();
        }
    }

    public ReferencePresetRuntimeAdmissionResult AdmitRuntime()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        RuntimeAuthorityResult result = VerifyRuntimeAuthority();
        return new ReferencePresetRuntimeAdmissionResult(
            result.Authority?.ManifestSha256,
            result.Diagnostics);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    private ReferenceFaceNativeInferenceResult InferCore(
        ReferenceFaceInferenceRequest request,
        CancellationToken cancellationToken)
    {
        RuntimeAuthorityResult authorityResult = VerifyRuntimeAuthority();
        if (authorityResult.Authority is null)
        {
            return EmptyResult(authorityResult.Diagnostics);
        }

        RuntimeAuthority authority = authorityResult.Authority;
        if (request.RuntimeManifestSha256 != authority.ManifestSha256)
        {
            return EmptyResult(ImmutableArray.Create(Error(
                "reference-native-runtime-manifest-hash",
                "The requested MediaPipe runtime manifest hash does not match the admitted runtime bytes.")));
        }

        if (!TryValidateImage(request.Image, out Diagnostic? imageError))
        {
            return EmptyResult(ImmutableArray.Create(imageError!));
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using NativeBindings native = NativeBindings.Load(
                authority.NativeLibraries);
            MediaPipeNativeInteropLayout.ValidateWindowsX64();
            return RunInference(
                native,
                authority,
                request.Image,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (NativeRuntimeLoadException exception)
        {
            return EmptyResult(ImmutableArray.Create(Error(
                exception.DiagnosticCode,
                exception.Message)));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
                InvalidDataException or
                IOException or
                UnauthorizedAccessException or
                BadImageFormatException or
                EntryPointNotFoundException or
                SEHException)
        {
            return EmptyResult(ImmutableArray.Create(Error(
                "reference-native-runtime",
                $"The admitted MediaPipe runtime could not complete inference: {exception.Message}")));
        }
    }

    private static ReferenceFaceNativeInferenceResult RunInference(
        NativeBindings native,
        RuntimeAuthority authority,
        DecodedReferenceImage image,
        CancellationToken cancellationToken)
    {
        byte[] pixels = image.CanonicalRgba.ToArray();
        GCHandle pinnedPixels = default;
        try
        {
            pinnedPixels = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            int imageStatus = native.ImageCreate(
                2,
                image.Width,
                image.Height,
                pinnedPixels.AddrOfPinnedObject(),
                pixels.Length,
                out nint imagePointer,
                out nint imageError);
            string? imageErrorText = native.ConsumeError(imageError);
            if (imageStatus != 0 || imagePointer == nint.Zero)
            {
                return EmptyResult(ImmutableArray.Create(NativeError(
                    "reference-native-image-create",
                    imageStatus,
                    imageErrorText)));
            }

            using var nativeImage = new MediaPipeImageSafeHandle(
                imagePointer,
                native.ImageFree);
            cancellationToken.ThrowIfCancellationRequested();
            return DetectAndLandmark(
                native,
                authority,
                nativeImage,
                pixels,
                image.Width,
                image.Height,
                cancellationToken);
        }
        finally
        {
            if (pinnedPixels.IsAllocated)
            {
                pinnedPixels.Free();
            }
        }
    }

    private static ReferenceFaceNativeInferenceResult DetectAndLandmark(
        NativeBindings native,
        RuntimeAuthority authority,
        MediaPipeImageSafeHandle image,
        byte[] canonicalRgba,
        int imageWidth,
        int imageHeight,
        CancellationToken cancellationToken)
    {
        using var detectorModelPath =
            new Utf8CoTaskMemSafeHandle(authority.DetectorModelPath);
        FaceDetectorOptionsNative detectorOptions = new()
        {
            BaseOptions = BaseOptionsNative.ForModel(
                detectorModelPath.DangerousGetHandle()),
            RunningMode = 1,
            MinDetectionConfidence =
                (float)ReferencePresetAuthoringRules.MinimumFaceScore,
            MinSuppressionThreshold = 0.5F,
            ResultCallback = nint.Zero
        };
        int createStatus = native.FaceDetectorCreate(
            ref detectorOptions,
            out nint detectorPointer,
            out nint createError);
        string? createErrorText = native.ConsumeError(createError);
        if (createStatus != 0 || detectorPointer == nint.Zero)
        {
            return EmptyResult(ImmutableArray.Create(NativeError(
                "reference-native-detector-create",
                createStatus,
                createErrorText)));
        }

        using var detector = new MediaPipeTaskSafeHandle(
            detectorPointer,
            native.FaceDetectorClose,
            native.ErrorFree);
        cancellationToken.ThrowIfCancellationRequested();

        DetectionResultNative detectionResult = default;
        var detectionResultOwned = false;
        try
        {
            int detectionStatus = native.FaceDetectorDetectImage(
                detector.DangerousGetHandle(),
                image.DangerousGetHandle(),
                nint.Zero,
                out detectionResult,
                out nint detectionError);
            string? detectionErrorText =
                native.ConsumeError(detectionError);
            if (detectionStatus != 0)
            {
                return EmptyResult(ImmutableArray.Create(NativeError(
                    "reference-native-detector-run",
                    detectionStatus,
                    detectionErrorText)));
            }

            detectionResultOwned = true;
            if (detectionResult.DetectionsCount > int.MaxValue)
            {
                return EmptyResult(ImmutableArray.Create(Error(
                    "reference-native-detector-count",
                    "The native detector returned an impossible face count.")));
            }

            int detectedFaceCount =
                checked((int)detectionResult.DetectionsCount);
            if (detectedFaceCount != 1)
            {
                return Result(
                    detectedFaceCount,
                    0.0,
                    ImmutableArray<ReferenceFaceLandmark>.Empty,
                    ImmutableArray<double>.Empty,
                    0.0,
                    authority,
                    ImmutableArray<Diagnostic>.Empty);
            }

            if (detectionResult.Detections == nint.Zero)
            {
                return Result(
                    1,
                    0.0,
                    ImmutableArray<ReferenceFaceLandmark>.Empty,
                    ImmutableArray<double>.Empty,
                    0.0,
                    authority,
                    ImmutableArray.Create(Error(
                        "reference-native-detector-result",
                        "The native detector returned a null detection array.")));
            }

            DetectionNative detection =
                Marshal.PtrToStructure<DetectionNative>(
                    detectionResult.Detections);
            if (detection.Categories == nint.Zero ||
                detection.CategoriesCount == 0)
            {
                return Result(
                    1,
                    0.0,
                    ImmutableArray<ReferenceFaceLandmark>.Empty,
                    ImmutableArray<double>.Empty,
                    0.0,
                    authority,
                    ImmutableArray.Create(Error(
                        "reference-native-detector-score",
                        "The native detector returned no face score.")));
            }

            CategoryNative category =
                Marshal.PtrToStructure<CategoryNative>(
                    detection.Categories);
            double detectorScore = category.Score;
            cancellationToken.ThrowIfCancellationRequested();
            return RunLandmarker(
                native,
                authority,
                detection,
                canonicalRgba,
                imageWidth,
                imageHeight,
                detectorScore,
                cancellationToken);
        }
        finally
        {
            if (detectionResultOwned)
            {
                native.FaceDetectorCloseResult(ref detectionResult);
            }
        }
    }

    private static ReferenceFaceNativeInferenceResult RunLandmarker(
        NativeBindings native,
        RuntimeAuthority authority,
        DetectionNative detection,
        byte[] canonicalRgba,
        int imageWidth,
        int imageHeight,
        double detectorScore,
        CancellationToken cancellationToken)
    {
        using var landmarkerModelPath =
            new Utf8CoTaskMemSafeHandle(authority.LandmarkerModelPath);
        FaceLandmarkerOptionsNative landmarkerOptions = new()
        {
            BaseOptions = BaseOptionsNative.ForModel(
                landmarkerModelPath.DangerousGetHandle()),
            RunningMode = 1,
            NumFaces = 2,
            MinFaceDetectionConfidence =
                0.1F,
            MinFacePresenceConfidence = 0.1F,
            MinTrackingConfidence = 0.1F,
            OutputFaceBlendshapes = false,
            OutputFacialTransformationMatrices = true,
            ResultCallback = nint.Zero
        };
        int createStatus = native.FaceLandmarkerCreate(
            ref landmarkerOptions,
            out nint landmarkerPointer,
            out nint createError);
        string? createErrorText = native.ConsumeError(createError);
        if (createStatus != 0 || landmarkerPointer == nint.Zero)
        {
            return Result(
                1,
                detectorScore,
                ImmutableArray<ReferenceFaceLandmark>.Empty,
                ImmutableArray<double>.Empty,
                0.0,
                authority,
                ImmutableArray.Create(NativeError(
                    "reference-native-landmarker-create",
                    createStatus,
                    createErrorText)));
        }

        using var landmarker = new MediaPipeTaskSafeHandle(
            landmarkerPointer,
            native.FaceLandmarkerClose,
            native.ErrorFree);
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryCreateLandmarkerCrop(
                detection,
                canonicalRgba,
                imageWidth,
                imageHeight,
                out LandmarkerCrop? crop,
                out Diagnostic? cropError))
        {
            return Result(
                1,
                detectorScore,
                ImmutableArray<ReferenceFaceLandmark>.Empty,
                ImmutableArray<double>.Empty,
                0.0,
                authority,
                ImmutableArray.Create(cropError!));
        }

        LandmarkerCrop admittedCrop = crop!;
        GCHandle pinnedCrop = default;
        try
        {
            pinnedCrop = GCHandle.Alloc(
                admittedCrop.Rgba,
                GCHandleType.Pinned);
            int imageStatus = native.ImageCreate(
                2,
                admittedCrop.Width,
                admittedCrop.Height,
                pinnedCrop.AddrOfPinnedObject(),
                admittedCrop.Rgba.Length,
                out nint cropImagePointer,
                out nint cropImageError);
            string? cropImageErrorText =
                native.ConsumeError(cropImageError);
            if (imageStatus != 0 ||
                cropImagePointer == nint.Zero)
            {
                return Result(
                    1,
                    detectorScore,
                    ImmutableArray<ReferenceFaceLandmark>.Empty,
                    ImmutableArray<double>.Empty,
                    0.0,
                    authority,
                    ImmutableArray.Create(NativeError(
                        "reference-native-landmarker-image",
                        imageStatus,
                        cropImageErrorText)));
            }

            using var cropImage = new MediaPipeImageSafeHandle(
                cropImagePointer,
                native.ImageFree);
            FaceLandmarkerResultNative landmarkerResult = default;
            var landmarkerResultOwned = false;
            try
            {
                int landmarkStatus =
                    native.FaceLandmarkerDetectImage(
                        landmarker.DangerousGetHandle(),
                        cropImage.DangerousGetHandle(),
                        nint.Zero,
                        out landmarkerResult,
                        out nint landmarkError);
                string? landmarkErrorText =
                    native.ConsumeError(landmarkError);
                if (landmarkStatus != 0)
                {
                    return Result(
                        1,
                        detectorScore,
                        ImmutableArray<ReferenceFaceLandmark>.Empty,
                        ImmutableArray<double>.Empty,
                        0.0,
                        authority,
                        ImmutableArray.Create(NativeError(
                            "reference-native-landmarker-run",
                            landmarkStatus,
                            landmarkErrorText)));
                }

                landmarkerResultOwned = true;
                return ReadLandmarkerResult(
                    authority,
                    detectorScore,
                    landmarkerResult,
                    admittedCrop,
                    imageWidth,
                    imageHeight);
            }
            finally
            {
                if (landmarkerResultOwned)
                {
                    native.FaceLandmarkerCloseResult(
                        ref landmarkerResult);
                }
            }
        }
        finally
        {
            if (pinnedCrop.IsAllocated)
            {
                pinnedCrop.Free();
            }
        }
    }

    private static bool TryCreateLandmarkerCrop(
        DetectionNative detection,
        byte[] canonicalRgba,
        int imageWidth,
        int imageHeight,
        out LandmarkerCrop? crop,
        out Diagnostic? error)
    {
        int boxWidth =
            detection.BoundingBoxRight - detection.BoundingBoxLeft;
        int boxHeight =
            detection.BoundingBoxBottom - detection.BoundingBoxTop;
        if (boxWidth <= 0 ||
            boxHeight <= 0 ||
            imageWidth <= 0 ||
            imageHeight <= 0 ||
            canonicalRgba.Length !=
                checked(imageWidth * imageHeight * 4))
        {
            crop = null;
            error = Error(
                "reference-native-detector-bounds",
                "The admitted face detection has invalid image bounds.");
            return false;
        }

        const double margin = 0.25;
        int left = Math.Clamp(
            (int)Math.Floor(
                detection.BoundingBoxLeft -
                boxWidth * margin),
            0,
            imageWidth - 1);
        int top = Math.Clamp(
            (int)Math.Floor(
                detection.BoundingBoxTop -
                boxHeight * margin),
            0,
            imageHeight - 1);
        int right = Math.Clamp(
            (int)Math.Ceiling(
                detection.BoundingBoxRight +
                boxWidth * margin),
            left + 1,
            imageWidth);
        int bottom = Math.Clamp(
            (int)Math.Ceiling(
                detection.BoundingBoxBottom +
                boxHeight * margin),
            top + 1,
            imageHeight);
        int cropWidth = right - left;
        int cropHeight = bottom - top;
        if (cropWidth <= 0 || cropHeight <= 0)
        {
            crop = null;
            error = Error(
                "reference-native-detector-bounds",
                "The admitted face detection cannot produce a bounded crop.");
            return false;
        }

        var cropRgba = new byte[checked(
            cropWidth * cropHeight * 4)];
        int sourceStride = checked(imageWidth * 4);
        int cropStride = checked(cropWidth * 4);
        for (var row = 0; row < cropHeight; row++)
        {
            Buffer.BlockCopy(
                canonicalRgba,
                checked((top + row) * sourceStride + left * 4),
                cropRgba,
                checked(row * cropStride),
                cropStride);
        }

        crop = new LandmarkerCrop(
            cropRgba,
            left,
            top,
            cropWidth,
            cropHeight);
        error = null;
        return true;
    }

    private static ReferenceFaceNativeInferenceResult ReadLandmarkerResult(
        RuntimeAuthority authority,
        double detectorScore,
        FaceLandmarkerResultNative result,
        LandmarkerCrop crop,
        int imageWidth,
        int imageHeight)
    {
        if (result.FaceLandmarksCount != 1 ||
            result.FaceLandmarks == nint.Zero)
        {
            return Result(
                1,
                detectorScore,
                ImmutableArray<ReferenceFaceLandmark>.Empty,
                ImmutableArray<double>.Empty,
                0.0,
                authority,
                ImmutableArray.Create(Error(
                    "reference-native-landmarker-face-count",
                    $"The native landmarker returned {result.FaceLandmarksCount} faces; exactly one is required.")));
        }

        NormalizedLandmarksNative collection =
            Marshal.PtrToStructure<NormalizedLandmarksNative>(
                result.FaceLandmarks);
        if (collection.LandmarksCount != 478 ||
            collection.Landmarks == nint.Zero)
        {
            return Result(
                1,
                detectorScore,
                ImmutableArray<ReferenceFaceLandmark>.Empty,
                ImmutableArray<double>.Empty,
                0.0,
                authority,
                ImmutableArray.Create(Error(
                    "reference-native-landmark-count",
                    $"The native landmarker returned {collection.LandmarksCount} landmarks; exactly 478 are required.")));
        }

        var landmarks =
            ImmutableArray.CreateBuilder<ReferenceFaceLandmark>(478);
        int landmarkSize =
            Marshal.SizeOf<NormalizedLandmarkNative>();
        for (var index = 0; index < 478; index++)
        {
            nint address = nint.Add(
                collection.Landmarks,
                checked(index * landmarkSize));
            NormalizedLandmarkNative landmark =
                Marshal.PtrToStructure<NormalizedLandmarkNative>(address);
            landmarks.Add(new ReferenceFaceLandmark(
                index,
                (crop.Left + landmark.X * crop.Width) /
                imageWidth,
                (crop.Top + landmark.Y * crop.Height) /
                imageHeight,
                landmark.Z * crop.Width / imageWidth,
                landmark.HasPresence ? landmark.Presence : null,
                landmark.HasVisibility ? landmark.Visibility : null));
        }

        if (result.FacialTransformationMatricesCount != 1 ||
            result.FacialTransformationMatrices == nint.Zero)
        {
            return Result(
                1,
                detectorScore,
                landmarks.MoveToImmutable(),
                ImmutableArray<double>.Empty,
                0.0,
                authority,
                ImmutableArray.Create(Error(
                    "reference-native-transform-count",
                    $"The native landmarker returned {result.FacialTransformationMatricesCount} transformation matrices; exactly one is required.")));
        }

        MatrixNative matrix =
            Marshal.PtrToStructure<MatrixNative>(
                result.FacialTransformationMatrices);
        if (matrix.Rows != 4 ||
            matrix.Columns != 4 ||
            matrix.Data == nint.Zero)
        {
            return Result(
                1,
                detectorScore,
                landmarks.MoveToImmutable(),
                ImmutableArray<double>.Empty,
                0.0,
                authority,
                ImmutableArray.Create(Error(
                    "reference-native-transform-shape",
                    $"The native transformation matrix is {matrix.Rows}x{matrix.Columns}; 4x4 is required.")));
        }

        var matrixValues = new float[16];
        Marshal.Copy(matrix.Data, matrixValues, 0, matrixValues.Length);
        ImmutableArray<double> canonicalMatrix = matrixValues
            .Select(value => (double)value)
            .ToImmutableArray();
        double yawDegrees = Math.Atan2(
                canonicalMatrix[8],
                canonicalMatrix[10]) *
            180.0 /
            Math.PI;
        return Result(
            1,
            detectorScore,
            landmarks.MoveToImmutable(),
            canonicalMatrix,
            yawDegrees,
            authority,
            ImmutableArray<Diagnostic>.Empty);
    }

    private RuntimeAuthorityResult VerifyRuntimeAuthority()
    {
        try
        {
            bool pathAccepted =
                _applicationRuntimeAuthority is null
                    ? _runtimeRoot.IsUnder(_labRoot) &&
                      !HasReparseAncestor(_runtimeRoot, _labRoot)
                    : _applicationRuntimeAuthority.RuntimeRoot.IsUnder(
                          _applicationRuntimeAuthority.ResourceBase) &&
                      !HasReparseAncestor(
                          _runtimeRoot,
                          new WorkspacePath(
                              _applicationRuntimeAuthority.ResourceBase.Value));
            if (!Directory.Exists(_runtimeRoot.Value) ||
                !pathAccepted)
            {
                return AuthorityError(
                    "reference-native-runtime-path",
                    "The MediaPipe runtime root is absent, outside its admitted authority root, or crosses a reparse point.");
            }

            string manifestPath = Path.Combine(
                _runtimeRoot.Value,
                "runtime-asset-manifest.json");
            if (!File.Exists(manifestPath) ||
                IsReparsePoint(manifestPath))
            {
                return AuthorityError(
                    "reference-native-runtime-manifest",
                    "The MediaPipe runtime manifest is absent or is a reparse point.");
            }

            byte[] manifestBytes = File.ReadAllBytes(manifestPath);
            Sha256Hash manifestHash = HashBytes(manifestBytes);
            if (_applicationRuntimeAuthority is not null &&
                manifestHash !=
                _applicationRuntimeAuthority.ManifestSha256)
            {
                return AuthorityError(
                    "reference-native-runtime-manifest-hash",
                    "The MediaPipe runtime manifest drifted after application-resource admission.");
            }
            using JsonDocument document = JsonDocument.Parse(
                manifestBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            JsonElement root = document.RootElement;
            EnsureNoDuplicateProperties(root, "$");
            if (root.ValueKind != JsonValueKind.Object ||
                root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("runtimeArchitecture").GetString() !=
                    "windows-x64" ||
                !root.GetProperty("offline").GetBoolean() ||
                !root.GetProperty("cpuOnly").GetBoolean())
            {
                return AuthorityError(
                    "reference-native-runtime-manifest",
                    "The MediaPipe runtime manifest contract is not admitted.");
            }

            JsonElement assets = root.GetProperty("assets");
            if (assets.ValueKind != JsonValueKind.Array ||
                assets.GetArrayLength() != ExpectedAssets.Length)
            {
                return AuthorityError(
                    "reference-native-runtime-inventory",
                    "The MediaPipe runtime manifest inventory is not exact.");
            }

            var hashes =
                new Dictionary<string, Sha256Hash>(StringComparer.Ordinal);
            for (var index = 0;
                 index < ExpectedAssets.Length;
                 index++)
            {
                JsonElement row = assets[index];
                string? relativePath =
                    row.GetProperty("path").GetString();
                string? role = row.GetProperty("role").GetString();
                if (relativePath != ExpectedAssets[index].Path ||
                    role != ExpectedAssets[index].Role ||
                    relativePath != Path.GetFileName(relativePath) ||
                    Path.IsPathRooted(relativePath) ||
                    relativePath.Contains(':', StringComparison.Ordinal))
                {
                    return AuthorityError(
                        "reference-native-runtime-inventory",
                        $"Runtime asset row {index} is not admitted.");
                }

                string assetPath = Path.Combine(
                    _runtimeRoot.Value,
                    relativePath);
                if (!File.Exists(assetPath) ||
                    IsReparsePoint(assetPath))
                {
                    return AuthorityError(
                        "reference-native-runtime-asset",
                        $"Runtime asset '{relativePath}' is absent or is a reparse point.");
                }

                FileInfo info = new(assetPath);
                long declaredLength =
                    row.GetProperty("length").GetInt64();
                string? declaredHash =
                    row.GetProperty("sha256").GetString();
                if (info.Length != declaredLength ||
                    declaredHash is null)
                {
                    return AuthorityError(
                        "reference-native-runtime-asset",
                        $"Runtime asset '{relativePath}' does not match its declared length.");
                }

                Sha256Hash measuredHash = HashFile(assetPath);
                Sha256Hash expectedHash = new(declaredHash);
                if (measuredHash != expectedHash)
                {
                    return AuthorityError(
                        "reference-native-runtime-asset-hash",
                        $"Runtime asset '{relativePath}' does not match its declared SHA-256.");
                }

                if (_applicationRuntimeAuthority is not null)
                {
                    ApplicationResourceAuthority admitted =
                        _applicationRuntimeAuthority.Assets[index];
                    if (!string.Equals(
                            admitted.Role,
                            role,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            admitted.Path.Value,
                            Path.GetFullPath(assetPath),
                            StringComparison.OrdinalIgnoreCase) ||
                        admitted.ByteLength != info.Length ||
                        admitted.Sha256 != measuredHash ||
                        admitted.ManifestSha256 != manifestHash)
                    {
                        return AuthorityError(
                            "reference-native-runtime-admission-drift",
                            $"Runtime asset '{relativePath}' drifted after application-resource admission.");
                    }
                }

                hashes.Add(relativePath, measuredHash);
            }

            string[] actualFiles = Directory.EnumerateFiles(
                    _runtimeRoot.Value,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] expectedFiles = ExpectedAssets
                .Select(item => item.Path)
                .Append("runtime-asset-manifest.json")
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!actualFiles.SequenceEqual(
                    expectedFiles,
                    StringComparer.Ordinal) ||
                Directory.EnumerateDirectories(
                    _runtimeRoot.Value,
                    "*",
                    SearchOption.TopDirectoryOnly).Any())
            {
                return AuthorityError(
                    "reference-native-runtime-inventory",
                    "The MediaPipe runtime root contains undeclared files or directories.");
            }

            ImmutableArray<NativeRuntimeAssetAuthority> nativeLibraries =
                NativeDependencyLoadOrder
                    .Select(fileName =>
                        new NativeRuntimeAssetAuthority(
                            fileName,
                            Path.Combine(
                                _runtimeRoot.Value,
                                fileName),
                            hashes[fileName],
                            fileName == "libmediapipe.dll"))
                    .ToImmutableArray();

            return new RuntimeAuthorityResult(
                new RuntimeAuthority(
                    manifestHash,
                    nativeLibraries,
                    Path.Combine(
                        _runtimeRoot.Value,
                        "blaze_face_short_range.tflite"),
                    Path.Combine(
                        _runtimeRoot.Value,
                        "face_landmarker.task"),
                    hashes["libmediapipe.dll"],
                    hashes["blaze_face_short_range.tflite"],
                    hashes["face_landmarker.task"]),
                ImmutableArray<Diagnostic>.Empty);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                JsonException or
                KeyNotFoundException or
                InvalidOperationException or
                ArgumentException)
        {
            return AuthorityError(
                "reference-native-runtime-manifest",
                $"The MediaPipe runtime authority could not be verified: {exception.Message}");
        }
    }

    private static bool TryValidateImage(
        DecodedReferenceImage image,
        out Diagnostic? error)
    {
        if (image.Width <= 0 ||
            image.Height <= 0 ||
            image.Stride != checked(image.Width * 4) ||
            image.CanonicalRgba.IsDefault ||
            image.CanonicalRgba.Length !=
                checked(image.Stride * image.Height))
        {
            error = Error(
                "reference-native-image-shape",
                "The decoded image is not tightly packed canonical RGBA.");
            return false;
        }

        var hashInput = new byte[checked(8 + image.CanonicalRgba.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(
            hashInput.AsSpan(0, 4),
            image.Width);
        BinaryPrimitives.WriteInt32LittleEndian(
            hashInput.AsSpan(4, 4),
            image.Height);
        image.CanonicalRgba.AsSpan().CopyTo(hashInput.AsSpan(8));
        Sha256Hash measuredHash = HashBytes(hashInput);
        if (measuredHash != image.CanonicalRgbaSha256)
        {
            error = Error(
                "reference-native-image-hash",
                "The canonical RGBA bytes do not match their admitted SHA-256.");
            return false;
        }

        error = null;
        return true;
    }

    private static bool HasReparseAncestor(
        WorkspacePath path,
        WorkspacePath root)
    {
        DirectoryInfo? current = new(path.Value);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (string.Equals(
                    current.FullName.TrimEnd(
                        Path.DirectorySeparatorChar),
                    root.Value.TrimEnd(
                        Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            current = current.Parent;
        }

        return true;
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static Sha256Hash HashFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(stream)));
    }

    private static Sha256Hash HashBytes(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void EnsureNoDuplicateProperties(
        JsonElement element,
        string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in
                     element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException(
                        $"Duplicate property '{path}.{property.Name}'.");
                }

                EnsureNoDuplicateProperties(
                    property.Value,
                    $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                EnsureNoDuplicateProperties(
                    item,
                    $"{path}[{index}]");
                index++;
            }
        }
    }

    private static RuntimeAuthorityResult AuthorityError(
        string code,
        string message) =>
        new(
            null,
            ImmutableArray.Create(Error(code, message)));

    private static ReferenceFaceNativeInferenceResult EmptyResult(
        ImmutableArray<Diagnostic> diagnostics) =>
        new(
            0,
            0.0,
            ImmutableArray<ReferenceFaceLandmark>.Empty,
            ImmutableArray<double>.Empty,
            0.0,
            EmptyHash,
            EmptyHash,
            EmptyHash,
            diagnostics);

    private static ReferenceFaceNativeInferenceResult Result(
        int detectedFaceCount,
        double detectorScore,
        ImmutableArray<ReferenceFaceLandmark> landmarks,
        ImmutableArray<double> matrix,
        double yawDegrees,
        RuntimeAuthority authority,
        ImmutableArray<Diagnostic> diagnostics) =>
        new(
            detectedFaceCount,
            detectorScore,
            landmarks,
            matrix,
            yawDegrees,
            authority.NativeLibrarySha256,
            authority.DetectorModelSha256,
            authority.LandmarkerModelSha256,
            diagnostics);

    private static Diagnostic NativeError(
        string code,
        int status,
        string? nativeMessage) =>
        Error(
            code,
            string.IsNullOrWhiteSpace(nativeMessage)
                ? $"MediaPipe returned status {status}."
                : $"MediaPipe returned status {status}: {nativeMessage}");

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record RuntimeAuthority(
        Sha256Hash ManifestSha256,
        ImmutableArray<NativeRuntimeAssetAuthority> NativeLibraries,
        string DetectorModelPath,
        string LandmarkerModelPath,
        Sha256Hash NativeLibrarySha256,
        Sha256Hash DetectorModelSha256,
        Sha256Hash LandmarkerModelSha256);

    private sealed record RuntimeAuthorityResult(
        RuntimeAuthority? Authority,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record LandmarkerCrop(
        byte[] Rgba,
        int Left,
        int Top,
        int Width,
        int Height);
}

internal static class MediaPipeNativeInteropLayout
{
    public static void ValidateWindowsX64()
    {
        if (!OperatingSystem.IsWindows() ||
            RuntimeInformation.ProcessArchitecture !=
                Architecture.X64 ||
            Marshal.SizeOf<BaseOptionsNative>() != 56 ||
            Marshal.SizeOf<FaceDetectorOptionsNative>() != 80 ||
            Marshal.SizeOf<FaceLandmarkerOptionsNative>() != 88 ||
            Marshal.SizeOf<DetectionResultNative>() != 16 ||
            Marshal.SizeOf<DetectionNative>() != 48 ||
            Marshal.SizeOf<CategoryNative>() != 24 ||
            Marshal.SizeOf<FaceLandmarkerResultNative>() != 48 ||
            Marshal.SizeOf<NormalizedLandmarksNative>() != 16 ||
            Marshal.SizeOf<NormalizedLandmarkNative>() != 40 ||
            Marshal.SizeOf<MatrixNative>() != 16)
        {
            throw new PlatformNotSupportedException(
                "The MediaPipe C ABI is admitted only for the verified Windows x64 layouts.");
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct BaseOptionsNative
{
    public nint ModelAssetBuffer;
    public uint ModelAssetBufferCount;
    public nint ModelAssetPath;
    public int Delegate;
    public int HostEnvironment;
    public int HostSystem;
    public nint HostVersion;
    public nint CaBundlePath;

    public static BaseOptionsNative ForModel(nint modelPath) => new()
    {
        ModelAssetBuffer = nint.Zero,
        ModelAssetBufferCount = 0,
        ModelAssetPath = modelPath,
        Delegate = 0,
        HostEnvironment = 0,
        HostSystem = 3,
        HostVersion = nint.Zero,
        CaBundlePath = nint.Zero
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct FaceDetectorOptionsNative
{
    public BaseOptionsNative BaseOptions;
    public int RunningMode;
    public float MinDetectionConfidence;
    public float MinSuppressionThreshold;
    public nint ResultCallback;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FaceLandmarkerOptionsNative
{
    public BaseOptionsNative BaseOptions;
    public int RunningMode;
    public int NumFaces;
    public float MinFaceDetectionConfidence;
    public float MinFacePresenceConfidence;
    public float MinTrackingConfidence;

    [MarshalAs(UnmanagedType.I1)]
    public bool OutputFaceBlendshapes;

    [MarshalAs(UnmanagedType.I1)]
    public bool OutputFacialTransformationMatrices;

    public nint ResultCallback;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DetectionResultNative
{
    public nint Detections;
    public uint DetectionsCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DetectionNative
{
    public nint Categories;
    public uint CategoriesCount;
    public int BoundingBoxLeft;
    public int BoundingBoxTop;
    public int BoundingBoxBottom;
    public int BoundingBoxRight;
    public nint Keypoints;
    public uint KeypointsCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CategoryNative
{
    public int Index;
    public float Score;
    public nint CategoryName;
    public nint DisplayName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FaceLandmarkerResultNative
{
    public nint FaceLandmarks;
    public uint FaceLandmarksCount;
    public nint FaceBlendshapes;
    public uint FaceBlendshapesCount;
    public nint FacialTransformationMatrices;
    public uint FacialTransformationMatricesCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NormalizedLandmarksNative
{
    public nint Landmarks;
    public uint LandmarksCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NormalizedLandmarkNative
{
    public float X;
    public float Y;
    public float Z;

    [MarshalAs(UnmanagedType.I1)]
    public bool HasVisibility;

    public float Visibility;

    [MarshalAs(UnmanagedType.I1)]
    public bool HasPresence;

    public float Presence;
    public nint Name;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MatrixNative
{
    public uint Rows;
    public uint Columns;
    public nint Data;
}

internal sealed class NativeBindings : IDisposable
{
    private readonly AdmittedNativeRuntime _runtime;

    private NativeBindings(AdmittedNativeRuntime runtime)
    {
        _runtime = runtime;
        ErrorFree = Export<MediaPipeErrorFreeDelegate>("MpErrorFree");
        ImageCreate = Export<MpImageCreateDelegate>(
            "MpImageCreateFromUint8Data");
        ImageFree = Export<MediaPipeImageFreeDelegate>("MpImageFree");
        FaceDetectorCreate = Export<MpFaceDetectorCreateDelegate>(
            "MpFaceDetectorCreate");
        FaceDetectorDetectImage =
            Export<MpFaceDetectorDetectImageDelegate>(
                "MpFaceDetectorDetectImage");
        FaceDetectorCloseResult =
            Export<MpFaceDetectorCloseResultDelegate>(
                "MpFaceDetectorCloseResult");
        FaceDetectorClose = Export<MediaPipeTaskCloseDelegate>(
            "MpFaceDetectorClose");
        FaceLandmarkerCreate =
            Export<MpFaceLandmarkerCreateDelegate>(
                "MpFaceLandmarkerCreate");
        FaceLandmarkerDetectImage =
            Export<MpFaceLandmarkerDetectImageDelegate>(
                "MpFaceLandmarkerDetectImage");
        FaceLandmarkerCloseResult =
            Export<MpFaceLandmarkerCloseResultDelegate>(
                "MpFaceLandmarkerCloseResult");
        FaceLandmarkerClose = Export<MediaPipeTaskCloseDelegate>(
            "MpFaceLandmarkerClose");
    }

    public MediaPipeErrorFreeDelegate ErrorFree { get; }

    public MpImageCreateDelegate ImageCreate { get; }

    public MediaPipeImageFreeDelegate ImageFree { get; }

    public MpFaceDetectorCreateDelegate FaceDetectorCreate { get; }

    public MpFaceDetectorDetectImageDelegate FaceDetectorDetectImage
    {
        get;
    }

    public MpFaceDetectorCloseResultDelegate FaceDetectorCloseResult
    {
        get;
    }

    public MediaPipeTaskCloseDelegate FaceDetectorClose { get; }

    public MpFaceLandmarkerCreateDelegate FaceLandmarkerCreate { get; }

    public MpFaceLandmarkerDetectImageDelegate FaceLandmarkerDetectImage
    {
        get;
    }

    public MpFaceLandmarkerCloseResultDelegate FaceLandmarkerCloseResult
    {
        get;
    }

    public MediaPipeTaskCloseDelegate FaceLandmarkerClose { get; }

    public static NativeBindings Load(
        ImmutableArray<NativeRuntimeAssetAuthority> authorities) =>
        new(WindowsAdmittedNativeRuntimeLoader.Load(authorities));

    public string? ConsumeError(nint errorMessage)
    {
        if (errorMessage == nint.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUTF8(errorMessage);
        }
        finally
        {
            ErrorFree(errorMessage);
        }
    }

    public void Dispose() => _runtime.Dispose();

    private T Export<T>(string name)
        where T : Delegate
    {
        nint address = NativeLibrary.GetExport(
            _runtime.PrimaryHandle,
            name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MpImageCreateDelegate(
    int format,
    int width,
    int height,
    nint pixelData,
    int pixelDataSize,
    out nint image,
    out nint errorMessage);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MpFaceDetectorCreateDelegate(
    ref FaceDetectorOptionsNative options,
    out nint detector,
    out nint errorMessage);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MpFaceDetectorDetectImageDelegate(
    nint detector,
    nint image,
    nint imageProcessingOptions,
    out DetectionResultNative result,
    out nint errorMessage);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MpFaceDetectorCloseResultDelegate(
    ref DetectionResultNative result);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MpFaceLandmarkerCreateDelegate(
    ref FaceLandmarkerOptionsNative options,
    out nint landmarker,
    out nint errorMessage);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MpFaceLandmarkerDetectImageDelegate(
    nint landmarker,
    nint image,
    nint imageProcessingOptions,
    out FaceLandmarkerResultNative result,
    out nint errorMessage);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MpFaceLandmarkerCloseResultDelegate(
    ref FaceLandmarkerResultNative result);
