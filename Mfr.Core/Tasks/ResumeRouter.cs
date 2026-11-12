namespace Mfr.Core.Tasks;

/// <summary>Вид последней запущенной задачи — без экранного статуса: ядро
/// не знает, как задача называется на кнопке.</summary>
public enum LauncherTaskKind
{
    None,
    GameUpdate,
    GameInstall,
    LauncherUpdate,
}

/// <summary>Что запускать по «Продолжить» после паузы.</summary>
public enum ResumeRoute
{
    None,
    GameUpdate,
    GameInstall,
    LauncherUpdate,
    Consistency,
}

/// <summary>
/// БТ-3/БТ-1.2: маршрут возобновления решает вид последней задачи. Проверка
/// целостности — тоже задача: её пауза возобновляется самой проверкой, а не
/// теряется в маршруте игровых задач, где прежде «Продолжить» не находил дела.
/// </summary>
public static class ResumeRouter
{
    public static ResumeRoute Route(LauncherTaskKind lastKind, bool lastIsConsistency) =>
        lastIsConsistency
            ? ResumeRoute.Consistency
            : lastKind switch
            {
                LauncherTaskKind.GameUpdate => ResumeRoute.GameUpdate,
                LauncherTaskKind.GameInstall => ResumeRoute.GameInstall,
                LauncherTaskKind.LauncherUpdate => ResumeRoute.LauncherUpdate,
                _ => ResumeRoute.None,
            };
}
