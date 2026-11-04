using System.Windows;
using System.Windows.Controls;
using Cavdeck.Domain.Storage;

namespace Cavdeck.App.Controls;

/// <summary>
/// Decorative provider glyph; generic or unrecognized providers use S3-compatible fallback.
/// </summary>
public sealed class ProviderGlyph : Control
{
    public static readonly DependencyProperty ProviderProperty =
        DependencyProperty.Register(
            nameof(Provider),
            typeof(StorageProviderKind),
            typeof(ProviderGlyph),
            new FrameworkPropertyMetadata(StorageProviderKind.S3Compatible));

    public StorageProviderKind Provider
    {
        get => (StorageProviderKind)GetValue(ProviderProperty);
        set => SetValue(ProviderProperty, value);
    }

    public ProviderGlyph()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }
}
