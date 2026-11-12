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
/// MGE.ini template handling. Templates are plain copies;
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
        // БТ-5: дескрипторы конфига и шаблонов освобождаются сразу — пока GC не
        // собрал поток, File.Copy поверх MGE.ini падает нарушением совместного доступа
        var current = HashFile(paths.MgeConfig);
        var candidates = new (string Path, MgeConfiguration Configuration)[]
        {
            (paths.MgeTemplates.High, MgeConfiguration.HIGH),
            (paths.MgeTemplates.Middle, MgeConfiguration.MIDDLE),
            (paths.MgeTemplates.Low, MgeConfiguration.LOW),
            (paths.MgeTemplates.Basic, MgeConfiguration.BASIC),
        };
        foreach (var (template, configuration) in candidates)
        {
            if (File.Exists(template) && HashFile(template).AsSpan().SequenceEqual(current))
            {
                return configuration;
            }
        }
        return MgeConfiguration.CUSTOM;
    }

    private static byte[] HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Md5.Hash(stream);
    }
}

/// <summary>
/// OpenMW configuration folder handling. Configs are
/// four files (openmw.cfg, settings.cfg, input_v3.xml, launcher.cfg); templates
/// generate openmw.cfg from openmw_template.cfg by substituting the game path.
/// </summary>
public sealed partial class OpenMwService(GamePaths paths)
{
    private static readonly string[] ConfigFiles = ["input_v3.xml", "launcher.cfg", "openmw.cfg", "settings.cfg"];
    private const string TemplateConfigFileName = "openmw_template.cfg";

    /// <summary>Источник openmw.cfg пресета: шаблон с плейсхолдером автора предпочтительнее готового файла.</summary>
    private static string? PresetConfigSource(string folder)
    {
        var template = Path.Combine(folder, TemplateConfigFileName);
        if (File.Exists(template))
        {
            return template;
        }
        var ready = Path.Combine(folder, "openmw.cfg");
        return File.Exists(ready) ? ready : null;
    }

    /// <summary>Подстановка путей под эту машину: плейсхолдер автора и мёртвые data-строки пресета.</summary>
    private IEnumerable<string> LocalizeConfig(IEnumerable<string> lines) => lines
        .Select(line => line.Replace(GamePaths.OpenMwConfigChangeValue, paths.Root))
        .Select(line => DataLineRegex().Replace(line, _ => $"data=\"{paths.DataFilesFolder}\""));

    /// <summary>Каноническая форма для сравнения: конфиг сравнивается с ЛОКАЛИЗОВАННЫМ
    /// пресетом (как его пишет ApplyConfig), а path-строки выбрасываются с обеих
    /// сторон — их значение зависит от машины, а не от пресета.</summary>
    private IEnumerable<string> ComparableConfig(string sourcePath) =>
        LocalizeConfig(File.ReadLines(sourcePath)).Where(line => !PathLineRegex().IsMatch(line));

    public void ApplyConfig(OpenMwConfiguration configuration, bool backup)
    {
        CopyBackup(backup);

        var templateFolder = configuration switch
        {
            OpenMwConfiguration.HIGH => paths.OpenMwTemplates.High,
            OpenMwConfiguration.MIDDLE => paths.OpenMwTemplates.Middle,
            OpenMwConfiguration.LOW => paths.OpenMwTemplates.Low,
            OpenMwConfiguration.BASIC => paths.OpenMwTemplates.Basic,
            OpenMwConfiguration.CUSTOM => paths.OpenMwConfigBackupFolder,
            _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
        };
        var isCustom = configuration == OpenMwConfiguration.CUSTOM;
        var source = isCustom ? Path.Combine(templateFolder, "openmw.cfg") : PresetConfigSource(templateFolder);
        if (!File.Exists(source))
        {
            return; // применять нечего: в пресете нет openmw.cfg
        }

        foreach (var file in Directory.EnumerateFiles(paths.OpenMwConfigFolder))
        {
            if (ConfigFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }
        Directory.CreateDirectory(paths.OpenMwConfigFolder);
        // бэкап хранит уже локальный конфиг игрока — его не перекладываем
        var lines = isCustom ? File.ReadLines(source) : LocalizeConfig(File.ReadLines(source));
        File.WriteAllLines(Path.Combine(paths.OpenMwConfigFolder, "openmw.cfg"), lines);
        foreach (var file in ConfigFiles)
        {
            var companion = Path.Combine(templateFolder, file);
            if (file != "openmw.cfg" && File.Exists(companion))
            {
                File.Copy(companion, Path.Combine(paths.OpenMwConfigFolder, file), overwrite: true);
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
            File.WriteAllLines(Path.Combine(folder, "openmw.cfg"), LocalizeConfig(File.ReadLines(template)));
        }
    }

    /// <summary>
    /// Автонастройка OpenMW без визарда (Linux/Steam Deck): кладёт пресет
    /// в папку конфига и подставляет фактический путь данных. Ничего не
    /// делает, когда существующий конфиг уже ссылается на существующую папку.
    /// </summary>
    public void EnsureAutoConfig()
    {
        var configPath = Path.Combine(paths.OpenMwConfigFolder, "openmw.cfg");
        if (File.Exists(configPath) && HasExistingDataDir(configPath))
        {
            return; // конфиг рабочий — пользователь мог настроить под себя
        }

        var dataDir = paths.DataFilesFolder;
        foreach (var folder in new[] { paths.OpenMwTemplates.Middle, paths.OpenMwTemplates.High, paths.OpenMwTemplates.Low, paths.OpenMwTemplates.Basic })
        {
            var source = PresetConfigSource(folder);
            if (source is null)
            {
                continue;
            }
            Directory.CreateDirectory(paths.OpenMwConfigFolder);
            File.WriteAllLines(configPath, LocalizeConfig(File.ReadLines(source)));
            foreach (var file in ConfigFiles)
            {
                var companion = Path.Combine(folder, file);
                if (file != "openmw.cfg" && File.Exists(companion))
                {
                    File.Copy(companion, Path.Combine(paths.OpenMwConfigFolder, file), overwrite: true);
                }
            }
            return;
        }

        // пресетов нет — минимальный конфиг, чтобы визард не запускался
        if (!File.Exists(configPath) && Directory.Exists(dataDir))
        {
            File.WriteAllText(configPath, $"data=\"{dataDir}\"\n");
        }
    }

    /// <summary>Конфиг валиден, когда хоть одна строка data="..." указывает на существующую папку.</summary>
    private static bool HasExistingDataDir(string configPath)
    {
        return File.ReadLines(configPath).Any(line =>
        {
            var match = DataLineRegex().Match(line);
            return match.Success && Directory.Exists(match.Groups[1].Value);
        });
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*data=""(.+?)""")]
    private static partial System.Text.RegularExpressions.Regex DataLineRegex();

    /// <summary>data= и data-local= — строки путей: для сравнения пресетов не важны.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s*data(-local)?=""")]
    private static partial System.Text.RegularExpressions.Regex PathLineRegex();

    /// <summary>
    /// Активный пресет. openmw.cfg сравнивается без path-строк (пути зависят от
    /// машины), а различаются пресеты настройки в settings.cfg — они сравниваются
    /// по значениям: OpenMW перезаписывает файл своим форматированием и добавляет
    /// ключи по умолчанию, поэтому действует правило «ключи пресета присутствуют
    /// и равны», лишние ключи активного файла не мешают.
    /// </summary>
    public OpenMwConfiguration? FindActiveConfig()
    {
        var activeConfig = Path.Combine(paths.OpenMwConfigFolder, "openmw.cfg");
        if (!File.Exists(activeConfig))
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
            var source = PresetConfigSource(folder);
            if (source is null
                || !SettingsMatch(folder)
                || CompanionDiffers(folder, "input_v3.xml")
                || CompanionDiffers(folder, "launcher.cfg"))
            {
                continue;
            }
            var expected = ComparableConfig(source).ToList();
            var actual = File.ReadLines(activeConfig).Where(line => !PathLineRegex().IsMatch(line));
            // пресет из одних path-строк нельзя отличить от другого такого же — пропускаем
            if (expected.Count == 0 || !expected.SequenceEqual(actual))
            {
                continue;
            }
            return configuration;
        }
        return OpenMwConfiguration.CUSTOM;

        bool CompanionDiffers(string folder, string file)
        {
            var preset = Path.Combine(folder, file);
            if (!File.Exists(preset))
            {
                return false; // пресет этим файлом не выражен — не мешает совпадению
            }
            var active = Path.Combine(paths.OpenMwConfigFolder, file);
            return !File.Exists(active) || !SameFile(active, preset);
        }

        bool SettingsMatch(string folder)
        {
            var preset = Path.Combine(folder, "settings.cfg");
            if (!File.Exists(preset))
            {
                return true; // пресет не выражен настройками — различает openmw.cfg
            }
            var active = Path.Combine(paths.OpenMwConfigFolder, "settings.cfg");
            if (!File.Exists(active))
            {
                return false;
            }
            var activeSettings = ParseSettings(active);
            return ParseSettings(preset).All(kv => activeSettings.TryGetValue(kv.Key, out var value) && value == kv.Value);
        }
    }

    /// <summary>settings.cfg как плоский словарь «секция|ключ=значение»: порядок
    /// строк и форматирование OpenMW не важны, важны значения.</summary>
    private static Dictionary<string, string> ParseSettings(string path)
    {
        var map = new Dictionary<string, string>();
        var section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line;
                continue;
            }
            map[$"{section}|{line}"] = line;
        }
        return map;
    }

    /// <summary>
    /// БТ-5.3: копия «Своих настроек». Детекция пресета сравнивает только ключи
    /// пресета и не видит правок игрока в остальных (fov и подобные): слот
    /// заполняется и переходом со «штатного» пресета, пока пуст, — данные дороже
    /// догадки о чистом пресете. Занятый слот пресетным содержимым не затирается.
    /// </summary>
    private void CopyBackup(bool requested)
    {
        var hasSlot = File.Exists(Path.Combine(paths.OpenMwConfigBackupFolder, "openmw.cfg"));
        if (hasSlot && (!requested || FindActiveConfig() is not (OpenMwConfiguration.CUSTOM or null)))
        {
            return; // слот уже хранит свои настройки — пресетным состоянием не перезаписываем
        }
        Directory.CreateDirectory(paths.OpenMwConfigBackupFolder);
        foreach (var file in ConfigFiles)
        {
            var active = Path.Combine(paths.OpenMwConfigFolder, file);
            if (File.Exists(active))
            {
                File.Copy(active, Path.Combine(paths.OpenMwConfigBackupFolder, file), overwrite: true);
            }
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
