using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Mfr.Core.Network;
using Mfr.Core.Options;
using Mfr.Core.Services;
using Mfr.Core.Storage;
using Mfr.Core.Tasks;
using Mfr.Core.V2;
using Mfr.Protocol.Dto;
using Xunit;
using ProtoSchema = Com.Lezenford.Mfr.Schema.V1.Schema;
using ProtoPartition = Com.Lezenford.Mfr.Schema.V1.Partition;
using ProtoContentGroup = Com.Lezenford.Mfr.Schema.V1.ContentGroup;
using ProtoContent = Com.Lezenford.Mfr.Schema.V1.Content;
using SchemaProtoFile = Com.Lezenford.Mfr.Schema.V1.File;
using ProtoVersion = Com.Lezenford.Mfr.Version.V1.Version;
using VersionProtoFile = Com.Lezenford.Mfr.Version.V1.File;

namespace Mfr.Protocol.Tests;

/// <summary>
/// Сценарные тесты загрузки v2 по правилам AGENTS.md: итог — файлы в папке игры
/// и запросы, ушедшие на сервер. БТ-4.2/БТ-2.1/БТ-3.1 — граница пакета проходит
/// по группе манифеста, а не по имени варианта; БТ-1.1/БТ-10.2 — обрыв сжатой копии
/// докачивается, к исходному объекту клиент уходит только при негодной копии.
/// </summary>
public sealed class V2DownloadScenarioTests : IDisposable
{
    private readonly string _gameFolder = Path.Combine(Path.GetTempPath(), $"mfr-v2dl-{Guid.NewGuid():N}");
    private readonly LauncherServices _core;

    public V2DownloadScenarioTests()
    {
        Directory.CreateDirectory(_gameFolder);
        _core = new LauncherServices(
            options: new LauncherOptions { Server = new ServerOptions { Address = "test.invalid" } },
            databasePath: Path.Combine(_gameFolder, "..", $"v2dl-{Guid.NewGuid():N}.db"),
            gameFolder: _gameFolder,
            iniPath: Path.Combine(_gameFolder, "..", $"v2dl-{Guid.NewGuid():N}.ini"));
    }

    public void Dispose() => _core.Dispose();

    private void WriteFile(string relativePath, byte[] content)
    {
        var path = _core.Paths.Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
    }

    private byte[] ReadFile(string relativePath) => File.ReadAllBytes(_core.Paths.Resolve(relativePath));

    private static byte[] Content(string seed) => Encoding.UTF8.GetBytes(seed);

    // ── стенд: сервер выдачи + объектное хранилище одним скриптом ──────

    private sealed class ScriptedServer(Func<HttpRequestMessage, HttpResponseMessage> script) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(script(request));
        }
    }

    private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body))
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
        },
    };

    private static HttpResponseMessage Bytes(byte[] content, HttpStatusCode code = HttpStatusCode.OK) => new(code)
    {
        Content = new ByteArrayContent(content),
    };

    private static string StoragePath(HttpRequestMessage request) => request.RequestUri!.AbsolutePath.TrimStart('/');

    /// <summary>Поток, который отдаёт первые <paramref name="failAfter"/> байт и обрывает связь.</summary>
    private sealed class BreakingStream(byte[] data, int failAfter) : Stream
    {
        private int _position;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
        {
            if (_position >= failAfter)
            {
                throw new IOException("connection reset by peer");
            }
            var chunk = Math.Min(Math.Min(buffer.Length, 16 * 1024), failAfter - _position);
            data.AsMemory(_position, chunk).CopyTo(buffer);
            _position += chunk;
            return new ValueTask<int>(chunk);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Gzip(byte[] content)
    {
        using var packed = new MemoryStream();
        using (var packer = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            packer.Write(content);
        }
        return packed.ToArray();
    }

    // ── мир: группа Heads скачана, группа Voices — нет, в обеих вариант «HD» ──

    private readonly byte[] _baseContent = Content("base-esm-content");
    private readonly byte[] _headsHdOld = Content("heads-hd-5.0.1");
    private readonly byte[] _headsHdNew = Content("heads-hd-5.0.2-changed");
    private readonly byte[] _headsLo = Content("heads-lo");
    private readonly byte[] _voicesOld = Content("voices-hd-5.0.1");
    private readonly byte[] _voicesNew = Content("voices-hd-5.0.2-changed");

    private static SchemaProtoFile SchemaFile(string mainPath, string optionalPath, byte[] content, string? compression = null)
    {
        var file = new SchemaProtoFile
        {
            MainPath = mainPath,
            OptionalPath = optionalPath,
            Sha256 = ByteString.CopyFrom(SHA256.HashData(content)),
            Size = (ulong)content.Length,
        };
        if (compression is not null)
        {
            file.Compression = compression;
        }
        return file;
    }

    private ProtoSchema BuildSchema(string version, byte[] headsHd, byte[] voicesHd, string? headsHdCompression = null, bool voicesRequired = false) => new()
    {
        Version = version,
        Partitions =
        {
            new ProtoPartition { Id = "base", Required = true, Files =
            {
                SchemaFile("Data Files/Morrowind.esm", "Data Files/Morrowind.esm", _baseContent),
            } },
        },
        Options =
        {
            new ProtoContentGroup { Name = "Heads", Contents =
            {
                new ProtoContent { Name = "HD", Partition = new ProtoPartition { Files =
                {
                    SchemaFile("opt/Heads/HD/head.nif", "Data Files/meshes/head.nif", headsHd, headsHdCompression),
                } } },
                new ProtoContent { Name = "LO", Partition = new ProtoPartition { Files =
                {
                    SchemaFile("opt/Heads/LO/head.nif", "Data Files/meshes/head.nif", _headsLo),
                } } },
            } },
            new ProtoContentGroup { Name = "Voices", Contents =
            {
                new ProtoContent { Name = "HD", Partition = new ProtoPartition { Required = voicesRequired, Files =
                {
                    SchemaFile("opt/Voices/HD/voice.lip", "Data Files/voice/voice.lip", voicesHd),
                } } },
            } },
        },
    };

    private ProtoVersion BuildPlan(string? headsHdCompressedKey)
    {
        var headsHd = new VersionProtoFile { Path = "opt/Heads/HD/head.nif", Storage = "heads-hd.obj" };
        if (headsHdCompressedKey is not null)
        {
            headsHd.CompressedStorage = headsHdCompressedKey;
        }
        return new ProtoVersion
        {
            Id = "5.0.2",
            Files =
            {
                new VersionProtoFile { Path = "Data Files/Morrowind.esm", Storage = "base.esm.obj" },
                headsHd,
                new VersionProtoFile { Path = "opt/Heads/LO/head.nif", Storage = "heads-lo.obj" },
                new VersionProtoFile { Path = "opt/Voices/HD/voice.lip", Storage = "voices-hd.obj" },
            },
        };
    }

    private void SeedInstalledState(ProtoSchema installedSchema, byte[] headsHd)
    {
        var heads = new Section(0, "Heads", true);
        heads.Options.Add(new Option(0, 0, "Heads", "HD", false, "", null));
        heads.Options.Add(new Option(0, 0, "Heads", "LO", false, "", null));
        var voices = new Section(0, "Voices", false);
        voices.Options.Add(new Option(0, 0, "Voices", "HD", false, "", null));
        _core.Repository.ReplaceSchema([heads, voices]);
        _core.Repository.SetProperty(PropertyKeys.SelectedBuild, "5.0");

        V2Manifests.SaveSchemaFile(_core, installedSchema);
        WriteFile("Data Files/Morrowind.esm", _baseContent);
        WriteFile("opt/Heads/HD/head.nif", headsHd);
        WriteFile("opt/Heads/LO/head.nif", _headsLo);
    }

    /// <summary>Отмечает вариант применённым, не трогая флаги скачивания групп.</summary>
    private void SeedAppliedOption(string sectionName, string optionName)
    {
        var sections = _core.Repository.GetSections();
        var index = sections.FindIndex(s => s.Name == sectionName);
        var rebuilt = new Section(sections[index].Id, sections[index].Name, sections[index].Downloaded);
        foreach (var option in sections[index].Options)
        {
            rebuilt.Options.Add(option with { Applied = option.Name == optionName });
        }
        sections[index] = rebuilt;
        _core.Repository.ReplaceSchema(sections);
    }

    private (ScriptedServer Server, LauncherServicesV2 V2) Compose(
        ProtoSchema serverSchema, ProtoVersion plan, Func<string, byte[]>? storageObjects = null)
    {
        var server = new ScriptedServer(request =>
        {
            var path = StoragePath(request);
            return path switch
            {
                "v2/game/5.0/version" => Json(new VersionId("5.0.2")),
                "v1/game/files" => Json(new GameSchemaResponse("https://storage.test", "plan.vpb", "schema.vpb", null, null)),
                "schema.vpb" => Bytes(serverSchema.ToByteArray()),
                "plan.vpb" => Bytes(plan.ToByteArray()),
                _ when storageObjects?.Invoke(path) is { } bytes => Bytes(bytes),
                _ => Bytes("no-such-object"u8.ToArray(), HttpStatusCode.NotFound),
            };
        });
        var http = new HttpClient(server);
        return (server, new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http)));
    }

    private static IReadOnlyList<string> ObjectRequests(ScriptedServer server) =>
        server.Requests
            .Where(r => r.RequestUri!.Host == "storage.test")
            .Where(r => StoragePath(r) is not ("schema.vpb" or "plan.vpb"))
            .Select(r => StoragePath(r))
            .ToList();

    // ── БТ-4.2/БТ-2.1: обновление качает только свою группу ────────────

    [Fact] // БТ-4.2/БТ-2.1: пакет — группа манифеста; одинаковое имя варианта в чужой группе чужие гигабайты не тянет
    public async Task Update_VariantNameCollisionInForeignGroup_DownloadsOnlyOwnGroupFiles()
    {
        var oldSchema = BuildSchema("5.0.1", _headsHdOld, _voicesOld);
        var newSchema = BuildSchema("5.0.2", _headsHdNew, _voicesNew);
        SeedInstalledState(oldSchema, _headsHdOld);
        var (server, v2) = Compose(newSchema, BuildPlan(headsHdCompressedKey: null), path => path switch
        {
            "base.esm.obj" => _baseContent,
            "heads-hd.obj" => _headsHdNew,
            "heads-lo.obj" => _headsLo,
            "voices-hd.obj" => _voicesNew,
            _ => null!,
        });

        await new GameUpdateV2Task(v2).Execute(null);

        // перекачан ровно один изменённый файл своей группы
        Assert.Equal(_headsHdNew, ReadFile("opt/Heads/HD/head.nif"));
        Assert.Equal(["heads-hd.obj"], ObjectRequests(server));
        // чужая группа не появилась ни в хранилище, ни в игре
        Assert.False(Directory.Exists(_core.Paths.Resolve("opt/Voices")));
        Assert.False(File.Exists(_core.Paths.Resolve("Data Files/voice/voice.lip")));
    }

    [Fact] // БТ-2.1/БТ-10.2: незнакомый кодек в манифесте — сырой объект, трафик на копию не тратится
    public async Task Update_UnfamiliarCompression_DownloadsRawObjectWithoutCompressedCopy()
    {
        var oldSchema = BuildSchema("5.0.1", _headsHdOld, _voicesOld);
        var newSchema = BuildSchema("5.0.2", _headsHdNew, _voicesNew, headsHdCompression: "zstd");
        SeedInstalledState(oldSchema, _headsHdOld);
        var (server, v2) = Compose(newSchema, BuildPlan("heads-hd.obj.gz"), path => path switch
        {
            "heads-hd.obj" => _headsHdNew,
            "heads-hd.obj.gz" => Gzip(_headsHdNew),
            _ => null!,
        });

        await new GameUpdateV2Task(v2).Execute(null);

        Assert.Equal(_headsHdNew, ReadFile("opt/Heads/HD/head.nif"));
        Assert.Equal(["heads-hd.obj"], ObjectRequests(server)); // копия с чужим кодеком не качалась
    }

    // ── БТ-4.2/БТ-3.1: проверка целостности сверяет скачанные пакеты ────

    [Fact] // БТ-4.2/БТ-3.1: чужая группа с тем же именем варианта не докачивается и в игру не копируется
    public async Task CheckConsistency_VariantNameCollisionInForeignGroup_RestoresOnlyOwnGroup()
    {
        var newSchema = BuildSchema("5.0.2", _headsHdNew, _voicesNew);
        SeedInstalledState(newSchema, Content("corrupted-heads")); // sha не сходится у своей группы
        var (server, v2) = Compose(newSchema, BuildPlan(headsHdCompressedKey: null), path => path switch
        {
            "heads-hd.obj" => _headsHdNew,
            "voices-hd.obj" => _voicesNew,
            _ => null!,
        });

        await new CheckConsistencyV2Task(v2).Execute(null);

        // испорченный файл своей группы восстановлен, копия вернулась в игру
        Assert.Equal(_headsHdNew, ReadFile("opt/Heads/HD/head.nif"));
        Assert.True(File.Exists(_core.Paths.Resolve("Data Files/meshes/head.nif")));
        // чужой пакет не докачан и в игру не подмешан
        Assert.Equal(["heads-hd.obj"], ObjectRequests(server));
        Assert.False(Directory.Exists(_core.Paths.Resolve("opt/Voices")));
        Assert.False(File.Exists(_core.Paths.Resolve("Data Files/voice/voice.lip")));
    }

    [Fact] // БТ-3/пауза: «Продолжить» не пересчитывает свёртое до паузы — как загрузка, БТ-1.2
    public async Task CheckConsistency_Resume_SkipsFilesVerifiedBeforePause()
    {
        var schema = BuildSchema("5.0.2", _headsHdNew, _voicesNew, voicesRequired: true);
        SeedInstalledState(schema, _headsHdNew);
        WriteFile("opt/Voices/HD/voice.lip", _voicesNew); // обязательная партиция на месте и цела
        var (_, v2) = Compose(schema, BuildPlan(null), _ => null!);

        var task = new CheckConsistencyV2Task(v2);
        using var pause = new CancellationTokenSource();
        var seen = 0;
        task.ProgressChanged += _ =>
        {
            if (++seen == 2)
            {
                pause.Cancel(); // «Приостановить» после первого просчитанного файла
            }
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.Execute(null, pause.Token));

        await task.Execute(null, CancellationToken.None); // «Продолжить» — тот же экземпляр задачи

        Assert.True(task.HashSkipped >= 1, "свёртое до паузы не пересчитывается");
    }

    // ── БТ-1/прогресс: готовое на диске — в числителе полосы ──────────

    [Fact] // БТ-1/прогресс: продолжение не начинает полосу с нуля — готовый файл уже учтён
    public async Task DownloadResume_ExistingValidFile_CountedIntoProgress()
    {
        var big = new byte[1000 * 1024];
        new Random(20261112).NextBytes(big); // зерно: тест детерминирован
        var small = Content("tail-file");
        WriteFile("Data Files/big.dat", big); // уже скачан и цел

        var lastProgress = 0;
        var progressAtTailRequest = 0;
        var server = new ScriptedServer(request =>
        {
            if (StoragePath(request) == "tail.obj")
            {
                // фиксируем полосу на момент, когда недостающий файл только запрошен
                progressAtTailRequest = Math.Max(progressAtTailRequest, lastProgress);
            }
            return Bytes(small);
        });
        var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        var task = new DownloadFilesV2Task(v2);
        task.ProgressChanged += percent => lastProgress = Math.Max(lastProgress, percent);

        var plan = new List<V2FilePlan>
        {
            new("Data Files/big.dat", null, "big.obj", null, SHA256.HashData(big), "", (ulong)big.Length),
            new("Data Files/tail.dat", null, "tail.obj", null, SHA256.HashData(small), "", (ulong)small.Length),
        };
        await task.Execute(new V2DownloadParameters("https://storage.test", plan, ApplyOptionalPath: false));

        Assert.True(progressAtTailRequest >= 90,
            $"полоса на момент докачки была {progressAtTailRequest}%, готовый файл должен давать ~99%");
    }

    [Fact] // БТ-3: сверка хеширует сборку вне потока вызвавшего — окно не замирает на всё хеширование
    public async Task CheckConsistency_HashingRunsOffCallerThread()
    {
        var newSchema = BuildSchema("5.0.2", _headsHdNew, _voicesNew);
        SeedInstalledState(newSchema, _headsHdNew); // все файлы на месте и целы
        var (server, v2) = Compose(newSchema, BuildPlan(headsHdCompressedKey: null), _ => null!);

        var task = new CheckConsistencyV2Task(v2);
        var callerThread = Environment.CurrentManagedThreadId;
        var hashingThreads = new ConcurrentBag<int>();
        task.ProgressChanged += percent =>
        {
            if (percent < 100)
            {
                hashingThreads.Add(Environment.CurrentManagedThreadId);
            }
        };

        await task.Execute(null);

        // прогресс сверки идёт пофайлово
        Assert.NotEmpty(hashingThreads);
        // и приходит не с потока вызвавшего задачу (в бою — не с UI-потока)
        Assert.Contains(hashingThreads, thread => thread != callerThread);
        // целые файлы не перекачиваются
        Assert.Empty(ObjectRequests(server));
    }

    // ── БТ-4.4: повторное применение выбранных вариантов ───────────────

    [Fact] // БТ-4.4: обновление перекачало файл выбранного варианта в его пакете — вариант применяется заново
    public async Task Update_RedownloadedPackageFile_AppliedVariantReappliedToGame()
    {
        var oldSchema = BuildSchema("5.0.1", _headsHdOld, _voicesOld);
        var newSchema = BuildSchema("5.0.2", _headsHdNew, _voicesNew);
        SeedInstalledState(oldSchema, _headsHdOld);
        WriteFile("Data Files/meshes/head.nif", _headsHdOld); // применённая копия в игре
        SeedAppliedOption("Heads", "HD");
        var (server, v2) = Compose(newSchema, BuildPlan(headsHdCompressedKey: null), path => path switch
        {
            "heads-hd.obj" => _headsHdNew,
            _ => null!,
        });

        await new GameUpdateV2Task(v2).Execute(null);

        Assert.Equal(_headsHdNew, ReadFile("opt/Heads/HD/head.nif")); // пакет обновлён
        Assert.Equal(_headsHdNew, ReadFile("Data Files/meshes/head.nif")); // и вариант применён заново, а не остался прежним
    }

    [Fact] // БТ-4.4/БТ-2: вариант по умолчанию нескачанной группы выбирается и без пакета — применяется заново
    public async Task Update_DefaultVariantOfNonDownloadedGroup_ReappliedToGame()
    {
        var oldSchema = BuildSchema("5.0.1", _headsHdOld, _voicesOld, voicesRequired: true);
        var newSchema = BuildSchema("5.0.2", _headsHdOld, _voicesNew, voicesRequired: true); // меняется только умолчание
        SeedInstalledState(oldSchema, _headsHdOld);
        WriteFile("opt/Voices/HD/voice.lip", _voicesOld); // умолчание установлено вместе со сборкой
        WriteFile("Data Files/voice/voice.lip", _voicesOld); // и его копия в игре
        SeedAppliedOption("Voices", "HD");
        var (server, v2) = Compose(newSchema, BuildPlan(headsHdCompressedKey: null), path => path switch
        {
            "voices-hd.obj" => _voicesNew,
            _ => null!,
        });

        await new GameUpdateV2Task(v2).Execute(null);

        Assert.Equal(_voicesNew, ReadFile("opt/Voices/HD/voice.lip")); // обязательный файл перекачан
        Assert.Equal(_voicesNew, ReadFile("Data Files/voice/voice.lip")); // и применён заново в игре
    }

    [Fact] // БТ-4.4/БТ-3: перекачанный файл неприменённого варианта не остаётся поверх выбранного
    public async Task CheckConsistency_RedownloadedForeignVariant_AppliedVariantRestored()
    {
        var newSchema = BuildSchema("5.0.2", _headsHdNew, _voicesNew);
        SeedInstalledState(newSchema, Content("corrupted-hd-storage")); // испорчен невыбранный HD
        WriteFile("Data Files/meshes/head.nif", _headsLo); // в игре — выбранный LO
        SeedAppliedOption("Heads", "LO");
        var (server, v2) = Compose(newSchema, BuildPlan(headsHdCompressedKey: null), path => path switch
        {
            "heads-hd.obj" => _headsHdNew,
            _ => null!,
        });

        await new CheckConsistencyV2Task(v2).Execute(null);

        Assert.Equal(_headsHdNew, ReadFile("opt/Heads/HD/head.nif")); // хранилище HD восстановлено
        Assert.Equal(_headsLo, ReadFile("Data Files/meshes/head.nif")); // но в игре — выбранный вариант
    }

    // ── БТ-2: повторное применение переживает сбой ─────────────────────

    [Fact] // БТ-2/опции-повтор: сбой применения не теряет список — повтор задачи доводит его
    public async Task Update_ApplyFailure_PendingSurvivesAndCompletesOnRerun()
    {
        var hdOld = Content("hd-5.0.1");
        var hdNew = Content("hd-5.0.2");
        // цель применения вложена в существующий файл: первая попытка упадёт
        const string gamePath = "Data Files/blocker.dat/head.nif";
        ProtoSchema Build(string version, byte[] hd) => new()
        {
            Version = version,
            Partitions =
            {
                new ProtoPartition { Id = "base", Required = true, Files =
                {
                    SchemaFile("Data Files/Morrowind.esm", "Data Files/Morrowind.esm", _baseContent),
                } },
            },
            Options =
            {
                new ProtoContentGroup { Name = "Heads", Contents =
                {
                    new ProtoContent { Name = "HD", Partition = new ProtoPartition { Files =
                    {
                        SchemaFile("opt/Heads/HD/head.nif", gamePath, hd),
                    } } },
                } },
            },
        };
        var oldSchema = Build("5.0.1", hdOld);
        var newSchema = Build("5.0.2", hdNew);
        SeedInstalledState(oldSchema, hdOld);
        SeedAppliedOption("Heads", "HD");
        WriteFile("Data Files/blocker.dat", Content("blocker")); // цель применения — внутри файла
        var (server, v2) = Compose(newSchema, BuildPlan(headsHdCompressedKey: null), path => path switch
        {
            "heads-hd.obj" => hdNew,
            _ => null!,
        });

        // первый прогон: скачивание прошло, применение упало
        await Assert.ThrowsAsync<Mfr.Core.Exceptions.TaskExecuteException>(
            () => new GameUpdateV2Task(v2).Execute(null));
        Assert.Equal("Heads|HD", _core.Repository.GetProperty(PropertyKeys.PendingReapply));

        // препятствие убрано, изменений на сервере больше нет: повтор доводит применение
        File.Delete(_core.Paths.Resolve("Data Files/blocker.dat"));
        await new GameUpdateV2Task(v2).Execute(null);

        Assert.Equal(hdNew, ReadFile("Data Files/blocker.dat/head.nif"));
        Assert.Null(_core.Repository.GetProperty(PropertyKeys.PendingReapply));
    }

    // ── БТ-1.1/БТ-10.2: сжатая копия ───────────────────────────────────

    private async Task<byte[]> RunSingleFileDownloadAsync(
        Func<HttpRequestMessage, HttpResponseMessage> script, V2FilePlan plan)
    {
        var server = new ScriptedServer(script);
        var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));
        await new DownloadFilesV2Task(v2).Execute(new V2DownloadParameters("https://storage.test", [plan], ApplyOptionalPath: false));
        return ReadFile(plan.MainPath);
    }

    [Fact] // БТ-1.1: обрыв посреди сжатой копии докачивается с места обрыва, сырой объект с нуля не начинается
    public async Task CompressedCopy_BreaksMidCopy_ResumesPackedPart_WithoutRawFallback()
    {
        var content = new byte[512 * 1024];
        new Random(20261111).NextBytes(content); // зерно: тест детерминирован
        var packed = Gzip(content);
        const int breakAfter = 64 * 1024;

        var server = new ScriptedServer(request =>
        {
            var path = StoragePath(request);
            if (path == "big.obj.gz")
            {
                var from = request.Headers.Range?.Ranges.Single().From;
                if (from is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new BreakingStream(packed, breakAfter)),
                    };
                }
                Assert.Equal(breakAfter, from); // докачка именно с места обрыва
                return new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(packed[(int)from.Value..]),
                };
            }
            return Bytes(Content("raw-must-not-be-downloaded"));
        });
        var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));

        var plan = new V2FilePlan("Data Files/big.dat", null, "big.obj", "big.obj.gz",
            SHA256.HashData(content), "gzip", (ulong)content.Length);
        await new DownloadFilesV2Task(v2).Execute(new V2DownloadParameters("https://storage.test", [plan], ApplyOptionalPath: false));

        var target = _core.Paths.Resolve("Data Files/big.dat");
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        // обе попытки — в копию; сырой объект не запрашивался
        var copyRequests = server.Requests.Where(r => StoragePath(r) == "big.obj.gz").ToList();
        Assert.Equal(2, copyRequests.Count);
        Assert.DoesNotContain(server.Requests, r => StoragePath(r) == "big.obj");
        // огрызков в папке игры не осталось
        Assert.False(File.Exists(target + ".packed"));
        Assert.False(File.Exists(target + ".packed.part"));
        Assert.False(File.Exists(target + ".unpacked"));
    }

    [Fact] // БТ-1/прогресс: подпись объёма соответствует значению — 16 ГиБ не «ТБ»
    public void FormatBytes_UnitLabelMatchesValue()
    {
        Assert.Equal("500 Б", FormatBytes2.Format(500));
        // пороги ступеней и подписи: разделитель дроби зависит от культуры, подпись — нет
        Assert.EndsWith("КБ", FormatBytes2.Format(500L * 1024));
        Assert.EndsWith("МБ", FormatBytes2.Format(2L * 1024 * 1024));
        Assert.EndsWith("ГБ", FormatBytes2.Format(16L * 1024 * 1024 * 1024));
        Assert.EndsWith("ТБ", FormatBytes2.Format(3L * 1024 * 1024 * 1024 * 1024));
        // вся сборка ~16 ГиБ — это гигабайты, не терабайты
        Assert.DoesNotContain("ТБ", FormatBytes2.Format(16L * 1024 * 1024 * 1024));
    }

    [Fact] // БТ-10.2: негодная копия — исходный объект, мусорная копия не перекачивается повторно
    public async Task CompressedCopy_NotGzip_FallsBackToRawObjectOnce()
    {
        var content = Content("raw-object-content");
        var server = new ScriptedServer(request => StoragePath(request) switch
        {
            "big.obj.gz" => Bytes(Content("not-a-gzip-copy")),
            "big.obj" => Bytes(content),
            _ => Bytes(Array.Empty<byte>(), HttpStatusCode.NotFound),
        });
        var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));

        var plan = new V2FilePlan("Data Files/big.dat", null, "big.obj", "big.obj.gz",
            SHA256.HashData(content), "gzip", (ulong)content.Length);
        await new DownloadFilesV2Task(v2).Execute(new V2DownloadParameters("https://storage.test", [plan], ApplyOptionalPath: false));

        var target = _core.Paths.Resolve("Data Files/big.dat");
        Assert.Equal(content, await File.ReadAllBytesAsync(target));
        // копия скачана один раз и забракована, дальше — только исходный объект
        Assert.Equal(1, server.Requests.Count(r => StoragePath(r) == "big.obj.gz"));
        Assert.Equal(1, server.Requests.Count(r => StoragePath(r) == "big.obj"));
        Assert.False(File.Exists(target + ".packed"));
        Assert.False(File.Exists(target + ".packed.part"));
        Assert.False(File.Exists(target + ".unpacked"));
    }

    // ── БТ-10.1: сбои связи повторяются с растущей паузой ─────────────

    [Fact] // БТ-10.1: 5xx хранилища у файла повторяется, а не роняет задачу
    public async Task File_ServerError5xx_RetriedWithGrowingPause()
    {
        var content = Content("file-after-retry");
        var calls = 0;
        var server = new ScriptedServer(request =>
        {
            if (StoragePath(request) != "big.obj")
            {
                return Bytes("no-such-object"u8.ToArray(), HttpStatusCode.NotFound);
            }
            calls++;
            return calls == 1
                ? Bytes(Array.Empty<byte>(), HttpStatusCode.ServiceUnavailable)
                : Bytes(content);
        });
        var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));

        var plan = new V2FilePlan("Data Files/big.dat", null, "big.obj", null,
            SHA256.HashData(content), "", (ulong)content.Length);
        await new DownloadFilesV2Task(v2).Execute(new V2DownloadParameters("https://storage.test", [plan], ApplyOptionalPath: false));

        Assert.Equal(content, ReadFile("Data Files/big.dat"));
        Assert.Equal(2, calls); // один 5xx, затем успех
    }

    [Fact] // БТ-10.1: таймаут HTTP — ошибка с повтором, а не пауза игрока
    public async Task File_HttpTimeout_RetriedNotTreatedAsPause()
    {
        var content = Content("file-after-timeout");
        var calls = 0;
        var server = new ScriptedServer(request =>
        {
            if (StoragePath(request) != "big.obj")
            {
                return Bytes(Array.Empty<byte>(), HttpStatusCode.NotFound);
            }
            calls++;
            return calls == 1
                ? Task.FromException<HttpResponseMessage>(
                    new TaskCanceledException("canceled", new TimeoutException())).Result
                : Bytes(content);
        });
        var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));

        var plan = new V2FilePlan("Data Files/big.dat", null, "big.obj", null,
            SHA256.HashData(content), "", (ulong)content.Length);
        await new DownloadFilesV2Task(v2).Execute(new V2DownloadParameters("https://storage.test", [plan], ApplyOptionalPath: false));

        Assert.Equal(content, ReadFile("Data Files/big.dat"));
        Assert.Equal(2, calls);
    }

    [Fact] // БТ-10.1: 4xx — не сетевой сбой, без повторов
    public async Task File_ClientError_FailsWithoutRetries()
    {
        var calls = 0;
        var server = new ScriptedServer(request =>
        {
            if (StoragePath(request) == "big.obj")
            {
                calls++;
                return Bytes("gone"u8.ToArray(), HttpStatusCode.NotFound);
            }
            return Bytes(Array.Empty<byte>(), HttpStatusCode.NotFound);
        });
        var http = new HttpClient(server);
        var v2 = new LauncherServicesV2(_core,
            new ApiClientV2(http, "api.test", "api2.test"), new HttpFileDownloader(http));

        var plan = new V2FilePlan("Data Files/gone.dat", null, "big.obj", null,
            SHA256.HashData(Content("x")), "", 1);
        await Assert.ThrowsAsync<Mfr.Core.Exceptions.TaskExecuteException>(
            () => new DownloadFilesV2Task(v2).Execute(new V2DownloadParameters("https://storage.test", [plan], ApplyOptionalPath: false)));

        Assert.Equal(1, calls); // сразу отказ, пяти попыток не делалось
    }

    [Fact] // БТ-10.1: 5xx у манифеста повторяется до сырой попытки
    public async Task Manifest_ServerError5xx_Retried()
    {
        var manifest = "manifest-bytes"u8.ToArray();
        var calls = 0;
        var server = new ScriptedServer(request =>
        {
            calls++;
            return calls == 1
                ? Bytes(Array.Empty<byte>(), HttpStatusCode.InternalServerError)
                : Bytes(manifest);
        });
        var http = new HttpClient(server);

        var downloaded = await new HttpFileDownloader(http)
            .DownloadManifestAsync("https://storage.test", "plan.vpb", compressedPath: null);

        Assert.Equal(manifest, downloaded);
        Assert.Equal(2, calls);
    }
}
