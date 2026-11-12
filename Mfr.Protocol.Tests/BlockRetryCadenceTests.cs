using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>
/// БТ-10.3: связь восстанавливается периодической проверкой без перезапуска.
/// Пробе в BLOCK нельзя ждать общего пятиминутного тика: игрок, вернув сеть,
/// видит «Подождите...» и не может отличить повтор от зависания.
/// </summary>
public sealed class BlockRetryCadenceTests
{
    [Fact] // БТ-10.3: недоступный сервер пробуется часто, штатный опрос остаётся редким
    public void BlockProbesOften_RegularPollStaysRare()
    {
        Assert.True(
            Mfr.Launcher.ViewModels.MainViewModel.PollDelay(
                Mfr.Launcher.ViewModels.MainViewModel.UpdateStatus.BLOCK) <= TimeSpan.FromSeconds(30),
            "проба в BLOCK обязана приходить не реже раза в полминуты");
        Assert.Equal(
            TimeSpan.FromMinutes(5),
            Mfr.Launcher.ViewModels.MainViewModel.PollDelay(
                Mfr.Launcher.ViewModels.MainViewModel.UpdateStatus.GAME_INSTALL));
    }
}
