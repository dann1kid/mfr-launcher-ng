using System.Diagnostics;
using Mfr.Core.Network;
using Mfr.Core.Options;
using Mfr.Protocol;
using Mfr.Protocol.Cryptography;

// Live smoke checks of the rewritten clients against the production server.
// Usage: dotnet run --project Mfr.Smoke -- rest | file | files <count>

if (args.Length > 0 && args[0] == "h2")
{
    var map = Mfr.Core.Storage.H2Migrator.ReadProperties(System.IO.Path.Combine("H:", "Games", "M[FR]", "launcher.mv.db"));
    foreach (var kv in map) System.Console.WriteLine($"{kv.Key}={kv.Value}");
    return 0;
}
var options = Live();
static LauncherOptions Live() => new() { ClientId = new Mfr.Core.Services.LauncherIni().ClientId };

return args[0] switch
{
    "rest" => RunRest(),
    "file" => RunFile(),
    "files" => RunFiles(args.Length > 1 ? int.Parse(args[1]) : 20),
    "big" => RunBig(args.Length > 1 ? int.Parse(args[1]) : 10),
    "pause" => RunPause(),
    "mini" => RunMini(args.Length > 1 ? int.Parse(args[1]) : 15),
    "pause2" => RunPauseTimings(
        args.Length > 1 ? int.Parse(args[1]) : 500,
        args.Length > 1 ? int.Parse(args[2]) : 2500),
    _ => PrintUsage(),
};

static int PrintUsage()
{
    Console.WriteLine("usage: mfr.smoke <rest|file|files [count]|big [count]|pause>");
    return 2;
}

static int RunRest()
{
    using var http = new HttpClient();
    var api = new ApiServerClient(http, Live());

    var builds = api.GetBuilds().GetAwaiter().GetResult();
    Console.WriteLine($"builds: {builds.Length}, first: id={builds[0].Id} name={builds[0].Name} default={builds[0].Default} lastUpdate={builds[0].LastUpdate:yyyy-MM-dd HH:mm:ss}");

    var launchers = api.GetLauncherVersions().GetAwaiter().GetResult();
    Console.WriteLine($"launchers: {launchers.Length}, first: {launchers[0].System} v{launchers[0].Version} size={launchers[0].Size} md5={Convert.ToBase64String(launchers[0].Md5)}");

    var sw = Stopwatch.StartNew();
    var content = api.GetGameContent(1).GetAwaiter().GetResult();
    var fileCount = content.Categories.Sum(c => c.Items.Sum(i => i.Files.Count));
    var totalSize = content.Categories.Sum(c => c.Items.Sum(i => i.Files.Sum(f => f.Size)));
    sw.Stop();
    Console.WriteLine($"manifest: {fileCount} files, {totalSize / 1024 / 1024} MiB, parsed in {sw.ElapsedMilliseconds} ms");

    var filtered = api.GetGameContent(1, new DateTime(2026, 1, 1)).GetAwaiter().GetResult();
    Console.WriteLine($"filtered (lastUpdate=2026-01-01): {filtered.Categories.Sum(c => c.Items.Sum(i => i.Files.Count))} files");
    return 0;
}

static int RunFile()
{
    // angel.ini: id 68541, small, active — perfect probe file
    return DownloadByIds([68541]).GetAwaiter().GetResult();
}

static int RunFiles(int count)
{
    using var http = new HttpClient();
    var api = new ApiServerClient(http, Live());
    var content = api.GetGameContent(1).GetAwaiter().GetResult();
    var manifest = content.Categories.SelectMany(c => c.Items).SelectMany(i => i.Files)
        .Where(f => f.Active && f.Size > 0 && f.Size < 5 * 1024 * 1024)
        .OrderBy(f => f.Size)
        .Take(count)
        .ToList();
    Console.WriteLine($"picked {manifest.Count} smallest active files, total {manifest.Sum(f => f.Size)} bytes");
    return DownloadByIds(manifest.Select(f => f.Id).ToArray()).GetAwaiter().GetResult();
}

static int RunBig(int count)
{
    // multi-chunk path: every file needs several positioned UploadFileMessage frames
    using var http = new HttpClient();
    var api = new ApiServerClient(http, Live());
    var content = api.GetGameContent(1).GetAwaiter().GetResult();
    var manifest = content.Categories.SelectMany(c => c.Items).SelectMany(i => i.Files)
        .Where(f => f.Active && f.Size > 512 * 1024 && f.Size < 3 * 1024 * 1024)
        .OrderBy(f => f.Size)
        .Take(count)
        .ToList();
    Console.WriteLine($"picked {manifest.Count} multi-chunk files ({manifest.Sum(f => f.Size)} bytes, min {manifest.Min(f => f.Size)}, max {manifest.Max(f => f.Size)})");
    return DownloadByIds(manifest.Select(f => f.Id).ToArray()).GetAwaiter().GetResult();
}

static int RunMini(int count)
{
    // end-to-end core smoke on production data without the 16 GiB install:
    // small MAIN files + real schema.json → DownloadFiles → FillSchema → UpdateGame
    var root = Path.Combine(Path.GetTempPath(), "mfr-mini");
    Directory.CreateDirectory(root);
    Directory.SetCurrentDirectory(root);
    Console.WriteLine($"mini install into {root}");

    using var services = new Mfr.Core.Tasks.LauncherServices(databasePath: "launcher.db", gameFolder: "game");
    var content = services.Api.GetGameContent(1).GetAwaiter().GetResult();
    var main = content.Categories.First(c => c.Type == Mfr.Protocol.Enums.ContentType.MAIN);
    var allMain = main.Items.SelectMany(i => i.Files).ToList();

    var schemaFile = allMain.Single(f => f.Path == "schema.json");
    var files = allMain
        .Where(f => f.Active && f.Size > 0 && f.Size < 256 * 1024)
        .OrderBy(f => f.Size)
        .Take(count)
        .Append(schemaFile)
        .ToList();
    Console.WriteLine($"downloading {files.Count} files ({files.Sum(f => f.Size)} bytes) incl. schema.json ({schemaFile.Size} bytes)");

    var download = new Mfr.Core.Tasks.DownloadFilesTask(services);
    download.ProgressChanged += p => Console.Write($"\r  download: {p,3}%       ");
    download.DescriptionChanged += d => Console.Write($"\r  {d,-40}");
    download.Execute(files).GetAwaiter().GetResult();
    Console.WriteLine();

    var fill = new Mfr.Core.Tasks.FillSchemaTask(services);
    fill.DescriptionChanged += d => Console.WriteLine("  [schema] " + d);
    fill.Execute(null).GetAwaiter().GetResult();

    foreach (var section in services.Repository.GetSections())
    {
        Console.WriteLine($"  section '{section.Name}': {section.Options.Count} options, downloaded={section.Downloaded}");
        foreach (var option in section.Options.Take(3))
        {
            Console.WriteLine($"    option '{option.Name}' applied={option.Applied}, files={option.Files.Count}");
        }
    }
    foreach (var extra in services.Repository.GetExtras().Take(3))
    {
        Console.WriteLine($"  extra '{extra.Name}': {extra.Files.Count} files, downloaded={extra.Downloaded}");
    }

    services.Repository.SetProperty(
        Mfr.Core.Storage.PropertyKeys.LastUpdateDate, DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss"));
    var update = new Mfr.Core.Tasks.UpdateGameTask(services);
    update.DescriptionChanged += d => Console.WriteLine("  [update] " + d);
    update.Execute(1).GetAwaiter().GetResult();
    Console.WriteLine("update task finished clean");
    return 0;
}

static int RunPauseTimings(int pauseDelayMs, int pauseDurationMs)
{
    using var http = new HttpClient();
    var options = Live();
    var api = new ApiServerClient(http, options);
    var content = api.GetGameContent(1).GetAwaiter().GetResult();
    var file = content.Categories.SelectMany(c => c.Items).SelectMany(i => i.Files)
        .Where(f => f.Active && f.Size is > 10 * 1024 * 1024 and < 25 * 1024 * 1024)
        .OrderBy(f => f.Size)
        .First();
    var targetDir = Path.Combine(Path.GetTempPath(), "mfr-smoke");
    Directory.CreateDirectory(targetDir);
    var request = new FileRequest(file.Id, Path.Combine(targetDir, file.Path), file.Size, file.Md5);
    Console.WriteLine($"pause timings test: pause@{pauseDelayMs}ms for {pauseDurationMs}ms, file {file.Size / 1024 / 1024} MiB");

    var received = 0L;
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var downloader = new FileDownloadClient(options);
    downloader.BytesReceived += n => Interlocked.Add(ref received, n);

    _ = Task.Delay(pauseDelayMs).ContinueWith(_ =>
    {
        Console.WriteLine($"  [{watch.Elapsed.TotalMilliseconds,7:F0}ms] PAUSE");
        downloader.Pause();
        return Task.Delay(pauseDurationMs);
    }).Unwrap().ContinueWith(_ =>
    {
        Console.WriteLine($"  [{watch.Elapsed.TotalMilliseconds,7:F0}ms] RESUME");
        downloader.Resume();
    });

    var failed = false;
    try
    {
        downloader.DownloadGameFiles([request]).GetAwaiter().GetResult();
    }
    catch (Exception exception)
    {
        failed = true;
        Console.WriteLine($"  [{watch.Elapsed.TotalMilliseconds,7:F0}ms] FAILED: {exception.Message}");
    }
    Console.WriteLine($"done: {Interlocked.Read(ref received) / 1024} KiB of {request.Size / 1024} KiB in {watch.Elapsed.TotalSeconds:F1}s");

    using (var stream = File.OpenRead(request.Path))
    {
        var ok = Md5.Hash(stream).AsSpan().SequenceEqual(request.Md5);
        Console.WriteLine(ok ? "MD5 OK" : "MD5 FAIL");
        return ok && !failed ? 0 : 1;
    }
}

static int RunPause()
{
    using var http = new HttpClient();
    var options = Live();
    var api = new ApiServerClient(http, options);
    var content = api.GetGameContent(1).GetAwaiter().GetResult();
    var files = content.Categories.SelectMany(c => c.Items).SelectMany(i => i.Files)
        .Where(f => f.Active && f.Size is > 10 * 1024 * 1024 and < 25 * 1024 * 1024)
        .OrderBy(f => f.Size)
        .Take(1)
        .ToList();
    var targetDir = Path.Combine(Path.GetTempPath(), "mfr-smoke");
    Directory.CreateDirectory(targetDir);
    var requests = files.Select(f => new FileRequest(f.Id, Path.Combine(targetDir, f.Path), f.Size, f.Md5)).ToList();
    Console.WriteLine($"pause test: {requests.Count} file, {requests.Sum(r => r.Size) / 1024 / 1024} MiB, single connection");

    var received = 0L;
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var downloader = new FileDownloadClient(options);
    var pausedSent = false;
    var resumedSent = false;
    var died = false;

    using var sampler = new Timer(_ =>
    {
        Console.WriteLine($"  [{watch.Elapsed.TotalSeconds,6:F2}s] {(died ? "DEAD   " : "active ")} {Interlocked.Read(ref received) / 1024,8} KiB");
        if (watch.Elapsed.TotalSeconds > 0.5 && !pausedSent)
        {
            pausedSent = true;
            Console.WriteLine($"  [{watch.Elapsed.TotalSeconds,6:F2}s] sending PAUSE");
            downloader.Pause();
        }
        if (watch.Elapsed.TotalSeconds > 3.0 && pausedSent && !resumedSent)
        {
            resumedSent = true;
            Console.WriteLine($"  [{watch.Elapsed.TotalSeconds,6:F2}s] sending RESUME");
            downloader.Resume();
        }
    }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(250));

    downloader.BytesReceived += n => Interlocked.Add(ref received, n);
    try
    {
        downloader.DownloadGameFiles(requests).GetAwaiter().GetResult();
    }
    catch (Exception exception)
    {
        died = true;
        Console.WriteLine($"  [{watch.Elapsed.TotalSeconds,6:F2}s] FAILED: {exception.GetType().Name}: {exception.Message}");
    }
    watch.Stop();
    Console.WriteLine($"done: {Interlocked.Read(ref received) / 1024} KiB of {requests[0].Size / 1024} KiB in {watch.Elapsed.TotalSeconds:F1}s");

    using (var stream = File.OpenRead(requests[0].Path))
    {
        var ok = Md5.Hash(stream).AsSpan().SequenceEqual(requests[0].Md5);
        Console.WriteLine(ok ? "MD5 OK" : $"MD5 FAIL (file size on disk: {new FileInfo(requests[0].Path).Length})");
        return ok ? 0 : 1;
    }
}

static async Task<int> DownloadByIds(int[] fileIds)
{
    using var http = new HttpClient();
    var options = Live();
    var api = new ApiServerClient(http, options);
    var content = await api.GetGameContent(1);

    var manifestById = content.Categories.SelectMany(c => c.Items).SelectMany(i => i.Files)
        .Where(f => f.Active)
        .ToDictionary(f => f.Id);
    var targetDir = Path.Combine(Path.GetTempPath(), "mfr-smoke");
    Directory.CreateDirectory(targetDir);

    var requests = fileIds.Select(id => manifestById[id]).Select(f =>
        new FileRequest(f.Id, Path.Combine(targetDir, f.Path), f.Size, f.Md5)).ToList();

    var totalBytes = 0L;
    var sw = Stopwatch.StartNew();
    using var downloader = new FileDownloadClient(options);
    downloader.BytesReceived += n => Interlocked.Add(ref totalBytes, n);
    downloader.FileCompleted += (file, ok) => Console.WriteLine($"  done: {Path.GetFileName(file.Path)} md5ok={ok}");
    await downloader.DownloadGameFiles(requests);
    sw.Stop();

    var speed = totalBytes / Math.Max(sw.Elapsed.TotalSeconds, 0.001) / 1024;
    Console.WriteLine($"downloaded {requests.Count} files, {totalBytes} bytes in {sw.Elapsed.TotalSeconds:F1}s ({speed:F0} KiB/s)");

    foreach (var request in requests)
    {
        var actual = Md5.Hash(File.OpenRead(request.Path));
        var ok = actual.AsSpan().SequenceEqual(request.Md5);
        Console.WriteLine($"{(ok ? "OK  " : "FAIL")} id={request.Id} {Path.GetFileName(request.Path)} ({request.Size} bytes)");
        if (!ok)
            return 1;
    }
    return 0;
}
