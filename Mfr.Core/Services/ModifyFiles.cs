namespace Mfr.Core.Services;

/// <summary>
/// Fixed last-modified stamps for the core esm/esp files (Kotlin: ModifyFiles):
/// Morrowind orders plugins by file date, so the repack pins them to force the
/// intended load order. Applied whenever the installed state fires.
/// </summary>
public static class ModifyFiles
{
    private const string DataDirectory = "Data Files";

    private static readonly (string Name, int Year, int Month, int Day)[] Stamps =
    [
        ("Morrowind.esm", 2002, 1, 1),
        ("Tribunal.esm", 2003, 1, 1),
        ("Bloodmoon.esm", 2004, 1, 1),
        ("Tamriel_Data.esm", 2005, 1, 1),
        ("MFR.esm", 2006, 1, 1),
        ("Cyr_Main.esm", 2011, 1, 1),
        ("Sky_Main.esm", 2012, 1, 1),
        ("TR_Mainland.esm", 2020, 1, 1),
        ("TR_Factions.esp", 2030, 1, 1),
        ("MFR_TR_patch.esp", 2050, 1, 1),
    ];

    public static void ApplyLoadOrderDates(string gameRoot)
    {
        foreach (var (name, year, month, day) in Stamps)
        {
            var path = Path.Combine(gameRoot, DataDirectory, name);
            if (File.Exists(path))
            {
                File.SetLastWriteTime(path, new DateTime(year, month, day));
            }
        }
    }
}
