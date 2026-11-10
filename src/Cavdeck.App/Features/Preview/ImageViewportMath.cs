namespace Cavdeck.App.Features.Preview;

/// <summary>
/// Pixel-independent fit, cursor-anchored zoom and clamped pan for a WPF image viewport.
/// All dimensions and offsets use device-independent pixels.
/// </summary>
public static class ImageViewportMath
{
    public const double MinimumScale = 0.05;
    public const double MaximumScale = 8.0;

    public readonly record struct View(double Scale, double X, double Y);

    public static View Fit(double imageWidth, double imageHeight, double viewportWidth, double viewportHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
            return new View(1, 0, 0);

        var scale = Math.Min(1, Math.Min(viewportWidth / imageWidth, viewportHeight / imageHeight));
        return CenterOrClamp(new View(scale, 0, 0),
            imageWidth, imageHeight, viewportWidth, viewportHeight);
    }

    public static View Zoom(View current, double requestedScale, double anchorX, double anchorY,
        double imageWidth, double imageHeight, double viewportWidth, double viewportHeight)
    {
        if (current.Scale <= 0 || !double.IsFinite(requestedScale))
            return current;
        var nextScale = Math.Clamp(requestedScale, MinimumScale, MaximumScale);
        var ratio = nextScale / current.Scale;
        return CenterOrClamp(new View(
                nextScale,
                anchorX - (anchorX - current.X) * ratio,
                anchorY - (anchorY - current.Y) * ratio),
            imageWidth, imageHeight, viewportWidth, viewportHeight);
    }

    public static View Pan(View current, double deltaX, double deltaY,
        double imageWidth, double imageHeight, double viewportWidth, double viewportHeight) =>
        CenterOrClamp(current with { X = current.X + deltaX, Y = current.Y + deltaY },
            imageWidth, imageHeight, viewportWidth, viewportHeight);

    public static View CenterOrClamp(View current,
        double imageWidth, double imageHeight, double viewportWidth, double viewportHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
            return current;

        static double Limit(double offset, double rendered, double available) =>
            rendered <= available ? (available - rendered) / 2 : Math.Clamp(offset, available - rendered, 0);

        return current with
        {
            X = Limit(current.X, imageWidth * current.Scale, viewportWidth),
            Y = Limit(current.Y, imageHeight * current.Scale, viewportHeight)
        };
    }
}
