using Mfr.Core.Exceptions;
using Mfr.Core.Network;
using Mfr.Core.Options;
using Mfr.Core.Services;
using Mfr.Core.Storage;
using Mfr.Protocol.Dto;

namespace Mfr.Core.Tasks;

/// <summary>
/// Composition root for the task layer: wires together options, paths, the
/// repository, both network clients and the game services. One instance per
/// application; tasks are stateless and cheap to create.
/// </summary>
public sealed class LauncherServices : IDisposable
{
    private readonly HttpClient _httpClient = new();

    public LauncherServices(LauncherOptions? options = null, string? databasePath = null, string? gameFolder = null, Mfr.Core.Platform.IPlatformProcessRunner? processRunner = null)
    {
        var external = LoadExternalOptions();
        // адреса продакшн-контура по умолчанию — бинарник работает standalone;
        // launcher.json рядом с exe их переопределяет
        if (string.IsNullOrWhiteSpace(external.Server.Address))
        {
            external = external with
            {
                Server = new ServerOptions
                {
                    Address = EmbeddedDefaults.Address,
                    RuLocationAddress = EmbeddedDefaults.RuLocation,
                    EuLocationAddress = EmbeddedDefaults.EuLocation,
                    TcpPort = external.Server.TcpPort,
                    ConnectionCount = external.Server.ConnectionCount,
                },
            };
        }
        if (options is not null)
        {
            // явные поля вызова (версия из сборки, идентификатор из тестов) поверх адресов из файлов
            options = external with
            {
                Version = options.Version,
                Platform = options.Platform,
                ClientId = options.ClientId,
            };
        }
        else
        {
            options = external;
        }
        var ini = new LauncherIni();
        if (options.ClientId == Guid.Empty)
        {
            options = options with { ClientId = ini.ClientId };
        }
        Options = options;
        Paths = new GamePaths(gameFolder ?? "game");
        Repository = new GameRepository(databasePath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "launcher.db"));
        ImportLegacyH2State(Repository);
        Api = new ApiServerClient(_httpClient, options);
        Downloader = new FileDownloadClient(options);
        Mge = new MgeService(Paths);
        OpenMw = new OpenMwService(Paths);
        Runner = new GameRunner(Paths, processRunner ?? new Mfr.Core.Platform.NullProcessRunner());
    }

    /// <summary>
    /// <summary>
    /// Адреса продакшн-контура, запечённые при сборке.
    /// Значения синхронизируются с addresses.json (production).
    /// </summary>
    public static class EmbeddedDefaults
    {
        public const string Address = "mfr.fullrest.ru";
        public const string RuLocation = "client.mfr.yc.lezenford.com";
        public const string EuLocation = "client.mfr.hz.lezenford.com";
    }

    /// Адреса и параметры: сначала launcher.json рядом с exe (копия секции контура
    /// из файла адресов, кладётся конвейером), затем секция production из addresses.json
    /// рядом с exe. Оба отсутствуют — адресов нет, клиент сообщит о недоступности.
    /// </summary>
    private static LauncherOptions LoadExternalOptions()
    {
        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var overridePath = Path.Combine(AppContext.BaseDirectory, "launcher.json");
        if (File.Exists(overridePath))
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<LauncherOptions>(File.ReadAllText(overridePath), jsonOptions)
                       ?? new LauncherOptions();
            }
            catch (Exception)
            {
                // повреждённая копия не должна ломать клиент — пробуем файл адресов
            }
        }
        var addressesPath = Path.Combine(AppContext.BaseDirectory, "addresses.json");
        if (File.Exists(addressesPath))
        {
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(addressesPath));
                if (document.RootElement.TryGetProperty("production", out var section))
                {
                    var text = section.GetRawText();
                    if (System.Text.Json.JsonSerializer.Deserialize<LauncherOptions>(text, jsonOptions) is { } options)
                    {
                        return options;
                    }
                }
            }
            catch (Exception)
            {
            }
        }
        return new LauncherOptions();
    }

    /// <summary>

    /// to the exe; salvage its PROPERTY rows so line, region, flags and update stamps
    /// survive the switch (the sections/options themselves are re-detected from files
    /// by the FillSchema pass).
    /// </summary>
    private static void ImportLegacyH2State(GameRepository repository)
    {
        try
        {
            if (repository.GetSections().Count > 0 || repository.GetProperty(PropertyKeys.FirstStart) is not null)
            {
                return; // our store already initialized
            }
            var imported = Mfr.Core.Storage.H2Migrator.ReadProperties(Path.Combine(AppContext.BaseDirectory, "launcher.mv.db"));
            if (imported.Count == 0)
            {
                return;
            }
            foreach (var (key, value) in imported)
            {
                repository.SetProperty(key, value);
            }
            // БТ-1.4: очистка папки при переходе разрешена только по импортированному
            // состоянию установленного клиента — никогда по флагу, который писал сам этот клиент
            if (imported.ContainsKey(PropertyKeys.GameInstalled) ||
                imported.ContainsKey(PropertyKeys.NewReleaseInstalled))
            {
                repository.SetProperty(PropertyKeys.ImportedLegacyState, "true");
            }
        }
        catch (Exception)
        {
            // migration is best effort; a fresh state is always functional
        }
    }

    public LauncherOptions Options { get; }
    public GamePaths Paths { get; }
    public GameRepository Repository { get; }
    public ApiServerClient Api { get; }
    public FileDownloadClient Downloader { get; }
    public MgeService Mge { get; }
    public OpenMwService OpenMw { get; }
    public GameRunner Runner { get; }

    public void Dispose()
    {
        Downloader.Dispose();
        Repository.Dispose();
        _httpClient.Dispose();
    }
}

/// <summary>
/// Task base: progress/description reporting with subtask
/// chaining, task failures wrapped into TaskExecuteException.
/// </summary>
public abstract class LauncherTask
{
    private int _progress;
    private string _description = "";

    public int Progress => Volatile.Read(ref _progress);

    public string Description => Volatile.Read(ref _description);

    public event Action<int>? ProgressChanged;
    public event Action<string>? DescriptionChanged;

    protected void Report(int value)
    {
        Volatile.Write(ref _progress, value);
        ProgressChanged?.Invoke(value);
    }

    protected void Report(long current, long max) => Report(max == 0 ? 0 : (int)(current * 100 / max));

    protected void Describe(string description)
    {
        Volatile.Write(ref _description, description);
        DescriptionChanged?.Invoke(description);
    }

    protected async Task<TResult> JoinAsync<TParam, TResult>(
        LauncherTask<TParam, TResult> subtask, TParam parameters, CancellationToken cancellationToken)
    {
        void OnProgress(int value) => Report(value);
        void OnDescription(string description) => Describe(description);
        subtask.ProgressChanged += OnProgress;
        subtask.DescriptionChanged += OnDescription;
        try
        {
            return await subtask.Execute(parameters, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            subtask.ProgressChanged -= OnProgress;
            subtask.DescriptionChanged -= OnDescription;
        }
    }
}

public abstract class LauncherTask<TParam, TResult>(LauncherServices services) : LauncherTask
{
    protected LauncherServices Services { get; } = services;

    public async Task<TResult> Execute(TParam parameters, CancellationToken cancellationToken = default)
    {
        try
        {
            return await Action(parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not TaskExecuteException)
        {
            throw new TaskExecuteException($"Задача {GetType().Name} завершилась с ошибкой", exception);
        }
    }

    protected abstract Task<TResult> Action(TParam parameters, CancellationToken cancellationToken);
}
