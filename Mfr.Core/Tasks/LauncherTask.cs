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

    public LauncherServices(LauncherOptions? options = null, string? databasePath = null, string? gameFolder = null)
    {
        options ??= new LauncherOptions();
        var ini = new LauncherIni();
        if (options.ClientId == Guid.Empty)
        {
            options = options with { ClientId = ini.ClientId };
        }
        Options = options;
        Paths = new GamePaths(gameFolder ?? "game");
        Repository = new GameRepository(databasePath ?? "launcher.db");
        ImportLegacyH2State(Repository);
        Api = new ApiServerClient(_httpClient, options);
        Downloader = new FileDownloadClient(options);
        Mge = new MgeService(Paths);
        OpenMw = new OpenMwService(Paths);
        Runner = new GameRunner(Paths);
    }

    /// <summary>
    /// First run over an old client's folder: the H2 database (launcher.mv.db) sits next
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
            foreach (var (key, value) in imported)
            {
                repository.SetProperty(key, value);
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
/// Task base (Kotlin: task.Task): progress/description reporting with subtask
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
