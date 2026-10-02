using Mfr.Protocol.Cryptography;

namespace Mfr.Core.Services;

public enum MgeConfiguration
{
    HIGH,
    MIDDLE,
    LOW,
    BASIC,
    CUSTOM,
}

public enum OpenMwConfiguration
{
    HIGH,
    MIDDLE,
    LOW,
    BASIC,
    CUSTOM,
}

/// <summary>
/// MGE.ini template handling (Kotlin: MgeService). Templates are plain copies;
/// the active configuration is detected by comparing MD5 against templates.
/// </summary>
public sealed class MgeService(GamePaths paths)
{
    public void ApplyConfig(MgeConfiguration configuration, bool backup)
    {
        if (backup)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(paths.MgeConfigBackup)!);
            File.Copy(paths.MgeConfig, paths.MgeConfigBackup, overwrite: true);
        }

        var template = configuration switch
        {
            MgeConfiguration.HIGH => paths.MgeTemplates.High,
            MgeConfiguration.MIDDLE => paths.MgeTemplates.Middle,
            MgeConfiguration.LOW => paths.MgeTemplates.Low,
            MgeConfiguration.BASIC => paths.MgeTemplates.Basic,
            MgeConfiguration.CUSTOM => paths.MgeConfigBackup,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        };
        if (File.Exists(template))
        {
            File.Copy(template, paths.MgeConfig, overwrite: true);
        }
    }

    public MgeConfiguration FindActiveConfig()
    {
        if (!File.Exists(paths.MgeConfig))
        {
            return MgeConfiguration.CUSTOM;
        }
        var current = Md5.Hash(File.OpenRead(paths.MgeConfig));
        var candidates = new (string Path, MgeConfiguration Configuration)[]
        {
            (paths.MgeTemplates.High, MgeConfiguration.HIGH),
            (paths.MgeTemplates.Middle, MgeConfiguration.MIDDLE),
            (paths.MgeTemplates.Low, MgeConfiguration.LOW),
            (paths.MgeTemplates.Basic, MgeConfiguration.BASIC),
        };
        foreach (var (template, configuration) in candidates)
        {
            if (File.Exists(template) && Md5.Hash(File.OpenRead(template)).AsSpan().SequenceEqual(current))
            {
                return configuration;
            }
        }
        return MgeConfiguration.CUSTOM;
    }
}

/// <summary>
/// OpenMW configuration folder handling (Kotlin: OpenMwService). Configs are
/// four files (openmw.cfg, settings.cfg, input_v3.xml, launcher.cfg); templates
/// generate openmw.cfg from openmw_template.cfg by substituting the game path.
/// </summary>
public sealed class OpenMwService(GamePaths paths)
{
    private static readonly string[] ConfigFiles = ["input_v3.xml", "launcher.cfg", "openmw.cfg", "settings.cfg"];
    private const string TemplateConfigFileName = "openmw_template.cfg";

    public void ApplyConfig(OpenMwConfiguration configuration, bool backup)
    {
        if (backup)
        {
            CopyBackup();
        }

        var templateFolder = configuration switch
        {
            OpenMwConfiguration.HIGH => paths.OpenMwTemplates.High,
            OpenMwConfiguration.MIDDLE => paths.OpenMwTemplates.Middle,
            OpenMwConfiguration.LOW => paths.OpenMwTemplates.Low,
            OpenMwConfiguration.BASIC => paths.OpenMwTemplates.Basic,
            OpenMwConfiguration.CUSTOM => paths.OpenMwConfigBackupFolder,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        };
        var templateFiles = ConfigFiles.Select(file => Path.Combine(templateFolder, file)).ToList();
        if (templateFiles.All(File.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(paths.OpenMwConfigFolder))
            {
                if (ConfigFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
            foreach (var template in templateFiles)
            {
                File.Copy(template, Path.Combine(paths.OpenMwConfigFolder, Path.GetFileName(template)), overwrite: true);
            }
        }
    }

    /// <summary>Generates openmw.cfg in each template folder from openmw_template.cfg.</summary>
    public void PrepareTemplates()
    {
        foreach (var folder in new[] { paths.OpenMwTemplates.Basic, paths.OpenMwTemplates.Low, paths.OpenMwTemplates.Middle, paths.OpenMwTemplates.High })
        {
            var template = Path.Combine(folder, TemplateConfigFileName);
            if (!File.Exists(template))
            {
                continue;
            }
            var lines = File.ReadLines(template)
                .Select(line => line.Replace(GamePaths.OpenMwConfigChangeValue, paths.Root));
            File.WriteAllLines(Path.Combine(folder, "openmw.cfg"), lines);
        }
    }

    public OpenMwConfiguration? FindActiveConfig()
    {
        if (!ConfigFiles.All(file => File.Exists(Path.Combine(paths.OpenMwConfigFolder, file))))
        {
            return null;
        }
        var candidates = new (string Folder, OpenMwConfiguration Configuration)[]
        {
            (paths.OpenMwTemplates.High, OpenMwConfiguration.HIGH),
            (paths.OpenMwTemplates.Middle, OpenMwConfiguration.MIDDLE),
            (paths.OpenMwTemplates.Low, OpenMwConfiguration.LOW),
            (paths.OpenMwTemplates.Basic, OpenMwConfiguration.BASIC),
        };
        foreach (var (folder, configuration) in candidates)
        {
            if (ConfigFiles.All(file => SameFile(
                    Path.Combine(paths.OpenMwConfigFolder, file),
                    Path.Combine(folder, file))))
            {
                return configuration;
            }
        }
        return OpenMwConfiguration.CUSTOM;
    }

    private void CopyBackup()
    {
        var templates = new[] { paths.OpenMwTemplates.High, paths.OpenMwTemplates.Middle, paths.OpenMwTemplates.Low, paths.OpenMwTemplates.Basic };
        if (templates.Any(folder => ConfigFiles.All(file => SameFile(
                Path.Combine(paths.OpenMwConfigFolder, file), Path.Combine(folder, file)))))
        {
            return; // active config is a stock template, nothing user-made to back up
        }
        if (!ConfigFiles.All(file => File.Exists(Path.Combine(paths.OpenMwConfigFolder, file))))
        {
            return;
        }
        Directory.CreateDirectory(paths.OpenMwConfigBackupFolder);
        foreach (var file in ConfigFiles)
        {
            File.Copy(
                Path.Combine(paths.OpenMwConfigFolder, file),
                Path.Combine(paths.OpenMwConfigBackupFolder, file), overwrite: true);
        }
    }

    private static bool SameFile(string first, string second)
    {
        if (!File.Exists(first) || !File.Exists(second))
        {
            return false;
        }
        using var firstStream = File.OpenRead(first);
        using var secondStream = File.OpenRead(second);
        return Md5.Hash(firstStream).AsSpan().SequenceEqual(Md5.Hash(secondStream));
    }
}
