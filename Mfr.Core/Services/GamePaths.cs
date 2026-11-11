namespace Mfr.Core.Services;

/// <summary>
/// All game-related paths, mirroring application.yml + PropertiesBeanPostProcessor:
/// everything is resolved against the game folder; OpenMW configs live under
/// the user's Documents folder.
/// </summary>
public sealed class GamePaths
{
    public GamePaths(string gameFolder = "game", string? openMwConfigFolder = null,
        string[]? openMwRootCandidates = null)
    {
        Root = Path.GetFullPath(gameFolder, AppContext.BaseDirectory);
        // на Linux backslash — обычный символ: без нормализации OpenMW\OpenMW_Config\mid_PC
        // превращается в одно имя каталога, и ни один пресет не находится
        var resolve = (string relative) => Path.GetFullPath(
            OperatingSystem.IsWindows() ? relative : relative.Replace('\\', '/'), Root);

        OptionalFolder = resolve("Optional");
        VersionFile = resolve(@"Optional\version");

        ClassicApplication = resolve("Morrowind.exe");
        ClassicLauncher = resolve("Morrowind Launcher.exe");
        McpApplication = resolve("Morrowind Code Patch.exe");

        MgeApplication = resolve("MGEXEgui.exe");
        MgeConfig = resolve(@"mge3\MGE.ini");
        MgeConfigBackup = resolve(@"mge3\backup\MGE.ini");
        MgeTemplates = new Templates(
            resolve(@"Optional\MGE\top_PC\MGE.ini"),
            resolve(@"Optional\MGE\mid_PC\MGE.ini"),
            resolve(@"Optional\MGE\low_PC\MGE.ini"),
            resolve(@"Optional\MGE\necro_PC\MGE.ini"));

        // сторедж автора (git-репа) не допускает два файла на одном пути:
        // линуксовые бинари приезжают отдельным каталогом OpenMW_linux_x64, а
        // OpenMW/ остаётся виндовым и общим (пресеты OpenMW_Config — оттуда).
        // На Linux предпочитаем OpenMW_linux_x64, фолбэк — вручную разложенный OpenMW.
        var openMwRoot = (openMwRootCandidates ?? DefaultOpenMwRootCandidates())
            .Select(resolve)
            .FirstOrDefault(Directory.Exists)
            ?? resolve("OpenMW");
        var openMwNative = Path.Combine(openMwRoot, "openmw");
        var openMwExe = Path.Combine(openMwRoot, "openmw.exe");
        var openMwLauncherNative = Path.Combine(openMwRoot, "openmw-launcher");
        var openMwLauncherExe = Path.Combine(openMwRoot, "openmw-launcher.exe");
        OpenMwApplication = File.Exists(openMwNative) ? openMwNative : openMwExe;
        OpenMwLauncher = File.Exists(openMwLauncherNative) ? openMwLauncherNative : openMwLauncherExe;
        OpenMwConfigBackupFolder = resolve(@"OpenMW\OpenMW_Config\backup");
        OpenMwTemplates = new Templates(
            resolve(@"OpenMW\OpenMW_Config\top_PC"),
            resolve(@"OpenMW\OpenMW_Config\mid_PC"),
            resolve(@"OpenMW\OpenMW_Config\low_PC"),
            resolve(@"OpenMW\OpenMW_Config\necro_PC"));

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        OpenMwConfigFolder = openMwConfigFolder ?? (OperatingSystem.IsWindows()
            ? Path.Combine(documents, "My Games", "OpenMW")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openmw"));
        Directory.CreateDirectory(OpenMwConfigFolder);
    }

    public string Root { get; }

    /// <summary>Папка данных игры — каноническое значение data= для openmw.cfg.</summary>
    public string DataFilesFolder => Resolve("Data Files");

    public string OptionalFolder { get; }

    public string VersionFile { get; }

    /// <summary>Modpack version from Optional\version (first non-empty line).</summary>
    public string GameVersion =>
        File.Exists(VersionFile)
            ? File.ReadLines(VersionFile).FirstOrDefault(line => line.Length > 0) ?? ""
            : "";

    public string ClassicApplication { get; }
    public string ClassicLauncher { get; }
    public string McpApplication { get; }
    public bool ClassicExists =>
        File.Exists(ClassicApplication) && File.Exists(ClassicLauncher) &&
        File.Exists(McpApplication) && File.Exists(MgeApplication);

    public string MgeApplication { get; }
    public string MgeConfig { get; }
    public string MgeConfigBackup { get; }
    public Templates MgeTemplates { get; }

    public string OpenMwApplication { get; }
    public string OpenMwLauncher { get; }
    public bool OpenMwExists => File.Exists(OpenMwApplication) && File.Exists(OpenMwLauncher);
    public string OpenMwConfigFolder { get; }
    public string OpenMwConfigBackupFolder { get; }
    public Templates OpenMwTemplates { get; }

    /// <summary>Placeholder inside openmw_template.cfg replaced with the game folder path
    ///.</summary>
    public const string OpenMwConfigChangeValue = @"E:\M[FR]_Git";

    /// <summary>Каталоги с бинарями OpenMW в порядке приоритета: линуксовый набор
    /// приезжает отдельным каталогом, виндовый остаётся в OpenMW.</summary>
    internal static string[] DefaultOpenMwRootCandidates() =>
        OperatingSystem.IsWindows() ? ["OpenMW"] : ["OpenMW_linux_x64", "OpenMW"];

    public string Resolve(string relative)
    {
        // на Linux backslash — обычный символ, не разделитель; нормализуем
        if (!OperatingSystem.IsWindows())
        {
            relative = relative.Replace('\\', '/');
        }
        return Path.GetFullPath(relative, Root);
    }

    public sealed record Templates(string High, string Middle, string Low, string Basic);
}
