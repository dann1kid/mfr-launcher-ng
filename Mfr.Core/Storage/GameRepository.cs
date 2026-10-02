using Mfr.Protocol;

namespace Mfr.Core.Storage;

/// <summary>
/// Persistent launcher state in SQLite (replacement for the old embedded H2
/// database ./launcher.mv.db). Mirrors the old Liquibase schema: sections,
/// options, items, extra, extra_files, property — so the semantics of
/// FillScheme/ApplyOptions tasks carry over unchanged.
/// </summary>
public sealed class GameRepository : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

    public GameRepository(string databasePath = "launcher.db")
    {
        _connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        Migrate();
    }

    public void Dispose() => _connection.Dispose();

    private void Migrate()
    {
        using var command = _connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS sections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                downloaded INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS options (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                section_id INTEGER NOT NULL REFERENCES sections(id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                applied INTEGER NOT NULL DEFAULT 0,
                description TEXT NOT NULL DEFAULT '',
                image_path TEXT,
                UNIQUE(section_id, name)
            );
            CREATE TABLE IF NOT EXISTS items (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                option_id INTEGER NOT NULL REFERENCES options(id) ON DELETE CASCADE,
                storage_path TEXT NOT NULL,
                game_path TEXT NOT NULL,
                md5 BLOB NOT NULL,
                UNIQUE(option_id, storage_path)
            );
            CREATE TABLE IF NOT EXISTS extra (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                downloaded INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS extra_files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                extra_id INTEGER NOT NULL REFERENCES extra(id) ON DELETE CASCADE,
                path TEXT NOT NULL,
                md5 BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS property (
                key TEXT PRIMARY KEY,
                value TEXT
            );
            """;
        command.ExecuteNonQuery();
    }

    // ---- sections / options / option files ----

    public List<Section> GetSections()
    {
        var sections = new Dictionary<int, Section>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, downloaded FROM sections ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                sections[id] = new Section(id, reader.GetString(1), reader.GetBoolean(2));
            }
        }

        var options = new Dictionary<int, Option>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT o.id, o.section_id, o.name, o.applied, o.description, o.image_path, s.name
                FROM options o JOIN sections s ON s.id = o.section_id
                ORDER BY o.id
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                var option = new Option(
                    id, reader.GetInt32(1), reader.GetString(6), reader.GetString(2), reader.GetBoolean(3),
                    reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
                options[id] = option;
                sections[option.SectionId].Options.Add(option);
            }
        }

        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, option_id, storage_path, game_path, md5 FROM items ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                var file = new OptionFile(
                    id, reader.GetInt32(1), reader.GetString(2), reader.GetString(3), GetBlob(reader, 4));
                options[file.OptionId].Files.Add(file);
            }
        }
        return [.. sections.Values];
    }

    /// <summary>Replaces the whole options tree from schema.json (Kotlin: FillSchemeTask).</summary>
    public void ReplaceSchema(IReadOnlyList<Section> sections)
    {
        using var transaction = _connection.BeginTransaction();
        try
        {
            ExecuteTx(transaction, "DELETE FROM sections");
            foreach (var section in sections)
            {
                var sectionId = Insert(transaction,
                    "INSERT INTO sections (name, downloaded) VALUES (@name, @downloaded); SELECT last_insert_rowid();",
                    [("name", section.Name), ("downloaded", section.Downloaded)]);
                foreach (var option in section.Options)
                {
                    var optionId = Insert(transaction,
                        "INSERT INTO options (section_id, name, applied, description, image_path) VALUES (@section, @name, @applied, @description, @image); SELECT last_insert_rowid();",
                        [("section", sectionId), ("name", option.Name), ("applied", option.Applied),
                         ("description", option.Description), ("image", (object?)option.ImagePath ?? DBNull.Value)]);
                    foreach (var file in option.Files)
                    {
                        ExecuteTx(transaction,
                            "INSERT INTO items (option_id, storage_path, game_path, md5) VALUES (@option, @storage, @game, @md5)",
                            [("option", optionId), ("storage", file.StoragePath), ("game", file.GamePath), ("md5", file.Md5)]);
                    }
                }
            }
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public void SetOptionApplied(int optionId, bool applied) =>
        Execute("UPDATE options SET applied = @applied WHERE id = @id", [("applied", applied), ("id", optionId)]);

    public void SetSectionDownloaded(int sectionId, bool downloaded) =>
        Execute("UPDATE sections SET downloaded = @downloaded WHERE id = @id", [("downloaded", downloaded), ("id", sectionId)]);

    // ---- extra content ----

    public List<Extra> GetExtras()
    {
        var extras = new Dictionary<int, Extra>();
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, downloaded FROM extra ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var id = reader.GetInt32(0);
                extras[id] = new Extra(id, reader.GetString(1), reader.GetBoolean(2));
            }
        }
        using (var command = _connection.CreateCommand())
        {
            command.CommandText = "SELECT id, extra_id, path, md5 FROM extra_files ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var file = new ExtraFile(reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2), GetBlob(reader, 3));
                extras[file.ExtraId].Files.Add(file);
            }
        }
        return [.. extras.Values];
    }

    public void ReplaceExtras(IReadOnlyList<Extra> extras)
    {
        using var transaction = _connection.BeginTransaction();
        ExecuteTx(transaction, "DELETE FROM extra");
        foreach (var extra in extras)
        {
            var extraId = Insert(transaction,
                "INSERT INTO extra (name, downloaded) VALUES (@name, @downloaded); SELECT last_insert_rowid();",
                [("name", extra.Name), ("downloaded", extra.Downloaded)]);
            foreach (var file in extra.Files)
            {
                ExecuteTx(transaction,
                    "INSERT INTO extra_files (extra_id, path, md5) VALUES (@extra, @path, @md5)",
                    [("extra", extraId), ("path", file.Path), ("md5", file.Md5)]);
            }
        }
        transaction.Commit();
    }

    public void SetExtraDownloaded(int extraId, bool downloaded) =>
        Execute("UPDATE extra SET downloaded = @downloaded WHERE id = @id", [("downloaded", downloaded), ("id", extraId)]);

    // ---- key/value properties ----

    public string? GetProperty(string key)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM property WHERE key = @key";
        command.Parameters.AddWithValue("key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetProperty(string key, string? value)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO property (key, value) VALUES (@key, @value) ON CONFLICT(key) DO UPDATE SET value = @value";
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("value", (object?)value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    // ---- helpers ----

    private void Execute(string sql, params (string Name, object?)[] parameters)
    {
        using var transaction = _connection.BeginTransaction();
        ExecuteTx(transaction, sql, parameters);
        transaction.Commit();
    }

    private void ExecuteTx(Microsoft.Data.Sqlite.SqliteTransaction transaction, string sql, params (string Name, object?)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private int Insert(Microsoft.Data.Sqlite.SqliteTransaction transaction, string sql, params (string Name, object?)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return Convert.ToInt32(command.ExecuteScalar()!);
    }

    private static byte[] GetBlob(Microsoft.Data.Sqlite.SqliteDataReader reader, int ordinal)
    {
        using var stream = reader.GetStream(ordinal);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

/// <summary>Section → options → files tree from schema.json, mirrored from the old JPA entities.</summary>
public sealed record Section(int Id, string Name, bool Downloaded)
{
    public List<Option> Options { get; } = [];
}

public sealed record Option(int Id, int SectionId, string SectionName, string Name, bool Applied, string Description, string? ImagePath)
{
    public List<OptionFile> Files { get; } = [];
}

public sealed record OptionFile(int Id, int OptionId, string StoragePath, string GamePath, byte[] Md5);

public sealed record Extra(int Id, string Name, bool Downloaded)
{
    public List<ExtraFile> Files { get; } = [];
}

public sealed record ExtraFile(int Id, int ExtraId, string Path, byte[] Md5);

/// <summary>Property keys of the old Properties.Key enum.</summary>
public static class PropertyKeys
{
    public const string Schema = "SCHEMA";
    public const string FirstStart = "FIRST_START";
    public const string SelectedBuild = "SELECTED_BUILD";
    public const string LastUpdateDate = "LAST_UPDATE_DATE";
    public const string GameInstalled = "GAME_INSTALLED";
    public const string OnlineMode = "ONLINE_MODE";
    public const string SpeedLimit = "SPEED_LIMIT";
    public const string MinimizeToTray = "MINIMIZE_TO_TRAY";
}
