using Mfr.Core.Options;
using Mfr.Core.Tasks;
using Xunit;

namespace Mfr.Protocol.Tests;

/// <summary>Адреса, встроенные в сборку, берутся из файла адресов, а не из кода.</summary>
public sealed class AddressTests
{
    private static string AddressFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "addresses.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException("addresses.json не найден выше каталога тестов");
    }

    [Fact]
    public void EmbeddedAddresses_AreTheProductionSectionOfAddressFile()
    {
        using var file = File.OpenRead(AddressFile());
        var expected = EmbeddedAddresses.Read(file, "production");

        var embedded = EmbeddedAddresses.Current();

        Assert.NotNull(expected);
        Assert.NotNull(embedded);
        Assert.False(string.IsNullOrWhiteSpace(embedded.Address));
        Assert.Equal(expected.Address, embedded.Address);
        Assert.Equal(expected.RuLocationAddress, embedded.RuLocationAddress);
        Assert.Equal(expected.EuLocationAddress, embedded.EuLocationAddress);
    }

    [Fact]
    public void ContourMissingFromAddressFile_GivesNoAddresses()
    {
        using var file = File.OpenRead(AddressFile());

        Assert.Null(EmbeddedAddresses.Read(file, "no-such-contour"));
    }

    [Fact]
    public void ClientWithoutAddressFilesNextToExe_UsesEmbeddedAddresses()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mfr-addr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            using var services = new LauncherServices(
                options: new LauncherOptions { ClientId = Guid.NewGuid() },
                databasePath: Path.Combine(folder, "launcher.db"),
                gameFolder: Path.Combine(folder, "game"));

            var embedded = EmbeddedAddresses.Current()!;
            Assert.Equal(embedded.Address, services.Options.Server.Address);
            Assert.Equal(embedded.RuLocationAddress, services.Options.Server.RuLocationAddress);
            Assert.Equal(embedded.EuLocationAddress, services.Options.Server.EuLocationAddress);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
