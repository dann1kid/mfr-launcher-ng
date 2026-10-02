using System.Text.Json;
using Mfr.Core.Storage;
using Mfr.Protocol;
using Mfr.Protocol.Cryptography;
using Mfr.Protocol.FileExchange;

namespace Mfr.Core.Tasks;

/// <summary>
/// Rebuilds the options database from schema.json when its MD5 changes,
/// preserving downloaded/applied flags, and auto-detects the applied option of
/// sections with none by comparing game files against option MD5s
/// (Kotlin: FillSchemeTask).
/// </summary>
public sealed class FillSchemaTask(LauncherServices services) : LauncherTask<object?, object?>(services)
{
    protected override Task<object?> Action(object? parameters, CancellationToken cancellationToken)
    {
        Describe("Обновление настраиваемых компонентов");
        var schemaPath = Services.Paths.Resolve("schema.json");
        if (!File.Exists(schemaPath))
        {
            throw new ArgumentException("Файл схемы не найден: schema.json");
        }

        var currentSchemaMd5 = Convert.ToHexString(Md5.Hash(File.OpenRead(schemaPath)));
        if (Services.Repository.GetProperty(PropertyKeys.Schema) == currentSchemaMd5)
        {
            return Task.FromResult<object?>(null);
        }

        SchemaFile schema;
        using (var stream = File.OpenRead(schemaPath))
        {
            schema = JsonSerializer.Deserialize<SchemaFile>(stream, ProtocolJson.Options)
                ?? throw new ArgumentException("Файл схемы повреждён: schema.json");
        }

        var savedSections = Services.Repository.GetSections();
        var savedExtras = Services.Repository.GetExtras();

        var sections = schema.Packages.Select(package =>
        {
            var saved = savedSections.FirstOrDefault(s => s.Name == package.Name);
            var section = new Section(0, package.Name, saved?.Downloaded ?? false);
            foreach (var option in package.Options)
            {
                var savedOption = saved?.Options.FirstOrDefault(o => o.Name == option.Name);
                var created = new Option(0, 0, package.Name, option.Name,
                    savedOption?.Applied ?? false, option.Description, option.Image);
                foreach (var item in option.Items)
                {
                    created.Files.Add(new OptionFile(0, 0, item.StoragePath, item.GamePath, item.Md5));
                }
                section.Options.Add(created);
            }
            return section;
        }).ToList();

        var extras = schema.Extra.Select(incoming =>
        {
            var saved = savedExtras.FirstOrDefault(e => e.Name == incoming.Name);
            var created = new Extra(0, incoming.Name, saved?.Downloaded ?? false);
            foreach (var item in incoming.Items)
            {
                created.Files.Add(new ExtraFile(0, 0, item.Path, item.Md5));
            }
            return created;
        }).ToList();

        var notApplied = sections.Where(s => s.Options.All(o => !o.Applied)).ToList();
        if (notApplied.Count > 0)
        {
            Describe("Поиск активной конфигурации");
            var totalCount = notApplied.Sum(s => s.Options.Sum(o => o.Files.Count));
            var currentCount = 0L;
            foreach (var section in notApplied)
            {
                for (var index = 0; index < section.Options.Count; index++)
                {
                    var option = section.Options[index];
                    var matches = true;
                    foreach (var file in option.Files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        currentCount++;
                        Report(currentCount, totalCount);
                        var gamePath = Services.Paths.Resolve(file.GamePath);
                        if (!File.Exists(gamePath))
                        {
                            matches = false;
                            break;
                        }
                        using var stream = File.OpenRead(gamePath);
                        if (!Md5.Hash(stream).AsSpan().SequenceEqual(file.Md5))
                        {
                            matches = false;
                            break;
                        }
                    }
                    if (matches)
                    {
                        section.Options[index] = option with { Applied = true };
                        break;
                    }
                }
            }
        }

        Report(100);
        Describe("Сохранение настроек");
        Services.Repository.ReplaceSchema(sections);
        Services.Repository.ReplaceExtras(extras);
        Services.Repository.SetProperty(PropertyKeys.Schema, currentSchemaMd5);
        return Task.FromResult<object?>(null);
    }
}
