using System.Numerics;

namespace NpcManager.Application;

/// <summary>
/// Shared deterministic camera math for reviewed reference-preset geometry.
/// Screen coordinates are normalized with (0,0) at the rendered top-left.
/// Skyrim Z is camera-up; reviewed yaw rotates only around Skyrim Z.
/// At zero yaw the camera faces the Skyrim facial side from positive Y,
/// matching the NPC preview renderer. Screen-right is negative Skyrim X.
/// </summary>
public static class ReferenceOrthographicProjection
{
    private const double Epsilon = 0.000000001;

    public static bool TryProject(
        ReferenceOrthographicCamera camera,
        Vector3 position,
        out double x,
        out double y,
        out double depth)
    {
        x = 0;
        y = 0;
        depth = 0;
        if (!TryGetBasis(camera, out Vector3 right, out Vector3 up,
                out Vector3 forward) ||
            !IsFinite(position))
        {
            return false;
        }

        double width = camera.Right - camera.Left;
        double height = camera.Top - camera.Bottom;
        double viewX = Vector3.Dot(position, right);
        double viewY = Vector3.Dot(position, up);
        x = (viewX - camera.Left) / width;
        y = (camera.Top - viewY) / height;
        depth = Vector3.Dot(position, forward);
        return double.IsFinite(x) &&
               double.IsFinite(y) &&
               double.IsFinite(depth);
    }

    public static bool TryCreateRay(
        ReferenceOrthographicCamera camera,
        double normalizedX,
        double normalizedY,
        out Vector3 origin,
        out Vector3 direction,
        out double maximumDistance)
    {
        origin = default;
        direction = default;
        maximumDistance = 0;
        if (!double.IsFinite(normalizedX) ||
            !double.IsFinite(normalizedY) ||
            normalizedX < 0 ||
            normalizedX > 1 ||
            normalizedY < 0 ||
            normalizedY > 1 ||
            !TryGetBasis(camera, out Vector3 right, out Vector3 up,
                out Vector3 forward))
        {
            return false;
        }

        double screenX = camera.Left +
                         normalizedX * (camera.Right - camera.Left);
        double screenY = camera.Top -
                         normalizedY * (camera.Top - camera.Bottom);
        double distance = camera.Far - camera.Near;
        if (!double.IsFinite(screenX) ||
            !double.IsFinite(screenY) ||
            !double.IsFinite(distance) ||
            distance <= Epsilon)
        {
            return false;
        }

        origin = right * (float)screenX +
                 up * (float)screenY +
                 forward * (float)camera.Near;
        direction = forward;
        maximumDistance = distance;
        return IsFinite(origin) && IsFinite(direction);
    }

    public static bool TryGetBasis(
        ReferenceOrthographicCamera camera,
        out Vector3 right,
        out Vector3 up,
        out Vector3 forward)
    {
        right = default;
        up = default;
        forward = default;
        if (camera is null ||
            !double.IsFinite(camera.YawDegrees) ||
            !double.IsFinite(camera.PitchDegrees) ||
            Math.Abs(camera.PitchDegrees) > Epsilon ||
            !double.IsFinite(camera.Left) ||
            !double.IsFinite(camera.Right) ||
            !double.IsFinite(camera.Bottom) ||
            !double.IsFinite(camera.Top) ||
            !double.IsFinite(camera.Near) ||
            !double.IsFinite(camera.Far) ||
            camera.Right - camera.Left <= Epsilon ||
            camera.Top - camera.Bottom <= Epsilon ||
            camera.Far - camera.Near <= Epsilon)
        {
            return false;
        }

        double radians = camera.YawDegrees * Math.PI / 180.0;
        float cosine = (float)Math.Cos(radians);
        float sine = (float)Math.Sin(radians);
        right = new Vector3(-cosine, sine, 0);
        up = Vector3.UnitZ;
        forward = new Vector3(-sine, -cosine, 0);
        return IsFinite(right) && IsFinite(forward);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
