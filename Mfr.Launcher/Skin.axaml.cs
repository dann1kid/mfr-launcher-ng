using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Mfr.Launcher;

/// <summary>
/// Attached properties for the skinned button theme. Skin.Base/Skin.Hover hold
/// asset paths relative to Assets/; the computed *Image properties feed the
/// control template (mirrors the JavaFX pattern: base PNG + *_hover.png overlay).
/// </summary>
public static class Skin
{
    public static readonly AttachedProperty<string?> BaseProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Base", typeof(Skin));

    public static readonly AttachedProperty<string?> HoverProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Hover", typeof(Skin));

    public static readonly AttachedProperty<string?> OverlayProperty =
        AvaloniaProperty.RegisterAttached<Control, string?>("Overlay", typeof(Skin));

    public static readonly AttachedProperty<IImage?> BaseImageProperty =
        AvaloniaProperty.RegisterAttached<Control, IImage?>("BaseImage", typeof(Skin));

    public static readonly AttachedProperty<IImage?> HoverImageProperty =
        AvaloniaProperty.RegisterAttached<Control, IImage?>("HoverImage", typeof(Skin));

    public static readonly AttachedProperty<IImage?> OverlayImageProperty =
        AvaloniaProperty.RegisterAttached<Control, IImage?>("OverlayImage", typeof(Skin));

    static Skin()
    {
        BaseProperty.Changed.Subscribe(Observer.For<AvaloniaPropertyChangedEventArgs<string?>>(e => ((Control)e.Sender).SetValue(BaseImageProperty, Load(e.NewValue.Value))));
        HoverProperty.Changed.Subscribe(Observer.For<AvaloniaPropertyChangedEventArgs<string?>>(e => ((Control)e.Sender).SetValue(HoverImageProperty, Load(e.NewValue.Value))));
        OverlayProperty.Changed.Subscribe(Observer.For<AvaloniaPropertyChangedEventArgs<string?>>(e => ((Control)e.Sender).SetValue(OverlayImageProperty, Load(e.NewValue.Value))));
    }

    public static string? GetBase(Control element) => element.GetValue(BaseProperty);
    public static void SetBase(Control element, string? value) => element.SetValue(BaseProperty, value);
    public static string? GetHover(Control element) => element.GetValue(HoverProperty);
    public static void SetHover(Control element, string? value) => element.SetValue(HoverProperty, value);
    public static string? GetOverlay(Control element) => element.GetValue(OverlayProperty);
    public static void SetOverlay(Control element, string? value) => element.SetValue(OverlayProperty, value);

    public static IImage? GetBaseImage(Control element) => element.GetValue(BaseImageProperty);
    public static IImage? GetHoverImage(Control element) => element.GetValue(HoverImageProperty);
    public static IImage? GetOverlayImage(Control element) => element.GetValue(OverlayImageProperty);

    public static IImage? Load(string? assetPath)
    {
        if (string.IsNullOrEmpty(assetPath))
        {
            return null;
        }
        var uri = new Uri($"avares://Mfr.Launcher/Assets/{assetPath}");
        using var stream = AssetLoader.Open(uri, baseUri: null);
        return new Bitmap(stream);
    }
}

/// <summary>Adapts an action to System.IObserver (Action-based Subscribe extensions are gone in modern .NET).</summary>
internal static class Observer
{
    public static IObserver<T> For<T>(Action<T> onNext) => new ActionObserver<T>(onNext);

    private sealed class ActionObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
