using System.Collections.Generic;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace Mfr.Launcher;

/// <summary>
/// XAML markup extension that loads (and caches) a skin image by its path
/// under Assets/: Source="{s:SkinImage button/close.png}" or
/// Setter Value="{s:SkinImage button/close.png}".
/// </summary>
public sealed class SkinImageExtension(string path) : MarkupExtension
{
    private static readonly Dictionary<string, IImage> Cache = new();

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (Cache.TryGetValue(path, out var cached))
        {
            return cached;
        }
        var image = Skin.Load(path)
            ?? throw new InvalidOperationException($"Skin asset not found: {path}");
        Cache[path] = image;
        return image;
    }
}
