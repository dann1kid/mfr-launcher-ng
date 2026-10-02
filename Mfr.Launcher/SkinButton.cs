using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Mfr.Launcher;

/// <summary>
/// Button with skin images. The string properties are set from styles;
/// images are loaded in code (no XAML bindings involved — reflection bindings
/// silently fail in single-file Release builds, so the template binds to the
/// IImage properties with plain TemplateBinding, which always resolves).
/// </summary>
public class SkinButton : Button
{
    public static readonly StyledProperty<string?> BaseProperty =
        AvaloniaProperty.Register<SkinButton, string?>("Base");

    public static readonly StyledProperty<string?> HoverProperty =
        AvaloniaProperty.Register<SkinButton, string?>("Hover");

    public static readonly StyledProperty<string?> OverlayProperty =
        AvaloniaProperty.Register<SkinButton, string?>("Overlay");

    public static readonly StyledProperty<IImage?> BaseImageProperty =
        AvaloniaProperty.Register<SkinButton, IImage?>("BaseImage");

    public static readonly StyledProperty<IImage?> HoverImageProperty =
        AvaloniaProperty.Register<SkinButton, IImage?>("HoverImage");

    public static readonly StyledProperty<IImage?> OverlayImageProperty =
        AvaloniaProperty.Register<SkinButton, IImage?>("OverlayImage");

    public string? Base
    {
        get => GetValue(BaseProperty);
        set => SetValue(BaseProperty, value);
    }

    public string? Hover
    {
        get => GetValue(HoverProperty);
        set => SetValue(HoverProperty, value);
    }

    public string? Overlay
    {
        get => GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    public IImage? BaseImage
    {
        get => GetValue(BaseImageProperty);
        set => SetValue(BaseImageProperty, value);
    }

    public IImage? HoverImage
    {
        get => GetValue(HoverImageProperty);
        set => SetValue(HoverImageProperty, value);
    }

    public IImage? OverlayImage
    {
        get => GetValue(OverlayImageProperty);
        set => SetValue(OverlayImageProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BaseProperty)
        {
            BaseImage = Skin.Load(Base);
        }
        else if (change.Property == HoverProperty)
        {
            HoverImage = Skin.Load(Hover);
        }
        else if (change.Property == OverlayProperty)
        {
            OverlayImage = Skin.Load(Overlay);
        }
    }
}
