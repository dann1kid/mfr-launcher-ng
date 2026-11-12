using Mfr.Core.Tasks;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-3/БТ-1.2: пауза проверки целостности возобновляется самой проверкой.
/// Прежде вид задачи нигде не запоминался: «Продолжить» после паузы проверки
/// не находил дела — маршрут решает ядро, окно только исполняет его.
/// </summary>
public sealed class ResumeRouteTests
{
    [Fact] // БТ-3/БТ-1.2: последняя задача — проверка: возобновляется она сама
    public void PausedConsistency_ResumesAsConsistency()
    {
        Assert.Equal(ResumeRoute.Consistency,
            ResumeRouter.Route(LauncherTaskKind.None, lastIsConsistency: true));
        // прежде вид проверки нигде не хранился и маршрут уходил в «ничего не делать»
        Assert.Equal(ResumeRoute.Consistency,
            ResumeRouter.Route(LauncherTaskKind.GameUpdate, lastIsConsistency: true));
    }

    [Fact] // БТ-3/БТ-1.2: игровые и лаунчерные задачи остаются собой
    public void GameTasks_ResumeAsThemselves()
    {
        Assert.Equal(ResumeRoute.GameUpdate, ResumeRouter.Route(LauncherTaskKind.GameUpdate, false));
        Assert.Equal(ResumeRoute.GameInstall, ResumeRouter.Route(LauncherTaskKind.GameInstall, false));
        Assert.Equal(ResumeRoute.LauncherUpdate, ResumeRouter.Route(LauncherTaskKind.LauncherUpdate, false));
    }

    [Fact] // БТ-1.2: без последней задачи «Продолжить» честно ничего не делает
    public void NoLastTask_ResumesToNone()
    {
        Assert.Equal(ResumeRoute.None, ResumeRouter.Route(LauncherTaskKind.None, false));
    }
}
