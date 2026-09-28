using System.Windows;
using System.Windows.Media;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimLightingPreviewControl : FrameworkElement
{
    public static readonly DependencyProperty PresetProperty =
        DependencyProperty.Register(
            nameof(Preset),
            typeof(PreviewLightingPreset),
            typeof(SkyrimLightingPreviewControl),
            new FrameworkPropertyMetadata(
                SkyrimLightingRules.DefaultPreset,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public PreviewLightingPreset Preset
    {
        get => (PreviewLightingPreset)GetValue(PresetProperty);
        set => SetValue(PresetProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Rect bounds = new(0, 0, ActualWidth, ActualHeight);
        drawingContext.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(15, 24, 35)),
            new Pen(new SolidColorBrush(Color.FromRgb(43, 61, 82)), 1),
            bounds,
            12,
            12);
        if (ActualWidth < 80 || ActualHeight < 80) return;

        double radius = Math.Min(ActualWidth, ActualHeight) * 0.27;
        Point center = new(ActualWidth * 0.5, ActualHeight * 0.48);
        byte ambient = ToByte(Math.Clamp(Preset.AmbientIntensity / 1.5f, 0.03f, 0.65f));
        drawingContext.DrawEllipse(
            new SolidColorBrush(Color.FromRgb(ambient, ambient, ambient)),
            new Pen(new SolidColorBrush(Color.FromRgb(104, 217, 195)), 1.25),
            center,
            radius,
            radius);

        foreach (PreviewLightSource light in Preset.Lights)
        {
            double azimuth = light.AzimuthDegrees * Math.PI / 180.0;
            double elevation = light.ElevationDegrees * Math.PI / 180.0;
            double x = Math.Cos(azimuth) * Math.Cos(elevation);
            double y = -Math.Sin(elevation);
            Point origin = new(
                center.X + x * radius * 0.72,
                center.Y + y * radius * 0.72);
            Color color = Color.FromRgb(
                ToByte(light.Red),
                ToByte(light.Green),
                ToByte(light.Blue));
            byte alpha = ToByte(Math.Clamp(light.Intensity / 4f, 0, 0.85f));
            var glow = new RadialGradientBrush
            {
                Center = new Point(0.5 + x * 0.3, 0.5 + y * 0.3),
                GradientOrigin = new Point(0.5 + x * 0.3, 0.5 + y * 0.3),
                RadiusX = 0.7,
                RadiusY = 0.7
            };
            glow.GradientStops.Add(new GradientStop(
                Color.FromArgb(alpha, color.R, color.G, color.B), 0));
            glow.GradientStops.Add(new GradientStop(
                Color.FromArgb(0, color.R, color.G, color.B), 1));
            drawingContext.DrawEllipse(glow, null, center, radius, radius);

            Point marker = new(
                center.X + x * radius * 1.43,
                center.Y + y * radius * 1.43);
            drawingContext.DrawLine(new Pen(new SolidColorBrush(color), 1), marker, origin);
            drawingContext.DrawEllipse(
                new SolidColorBrush(color),
                new Pen(Brushes.White, 1),
                marker,
                6,
                6);
        }

        drawingContext.DrawEllipse(
            new RadialGradientBrush(
                Color.FromArgb(105, 255, 255, 255),
                Color.FromArgb(0, 255, 255, 255))
            {
                GradientOrigin = new Point(0.35, 0.3),
                Center = new Point(0.35, 0.3),
                RadiusX = 0.75,
                RadiusY = 0.75
            },
            null,
            center,
            radius,
            radius);
    }

    private static byte ToByte(float value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);
}
