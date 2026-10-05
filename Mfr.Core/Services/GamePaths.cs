namespace Mfr.Core.Services;

/// <summary>
/// All game-related paths, mirroring application.yml + PropertiesBeanPostProcessor:
/// everything is resolved against the game folder; OpenMW configs live under
/// the user's Documents folder.
/// </summary>
public sealed class GamePaths
{
    public GamePaths(string gameFolder = "game")
    {
        Root = Path.GetFullPath(gameFolder, AppContext.BaseDirectory);
        var resolve = (string relative) => Path.GetFullPath(relative, Root);

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

        OpenMwApplication = resolve(@"OpenMW\openmw.exe");
        OpenMwLauncher = resolve(@"OpenMW\openmw-launcher.exe");
        OpenMwConfigBackupFolder = resolve(@"OpenMW\OpenMW_Config\backup");
        OpenMwTemplates = new Templates(
            resolve(@"OpenMW\OpenMW_Config\top_PC"),
            resolve(@"OpenMW\OpenMW_Config\mid_PC"),
            resolve(@"OpenMW\OpenMW_Config\low_PC"),
            resolve(@"OpenMW\OpenMW_Config\necro_PC"));

        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        OpenMwConfigFolder = Path.Combine(documents, "My Games", "OpenMW");
        Directory.CreateDirectory(OpenMwConfigFolder);
    }

    public string Root { get; }

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
    /// (dev application.yml value; the 2021 master value no longer matches shipped templates).</summary>
    public const string OpenMwConfigChangeValue = @"E:\M[FR]_Git";

    public string Resolve(string relative) => Path.GetFullPath(relative, Root);

    public sealed record Templates(string High, string Middle, string Low, string Basic);
}
