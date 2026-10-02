namespace Mfr.Core.Services;

/// <summary>
/// launcher.ini next to the executable: simple "key=value" lines
/// (Kotlin: Launcher.initExternalConfiguration). Keeps the client UUID stable
/// so server-side statistics survive updates.
/// </summary>
public sealed class LauncherIni
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public LauncherIni(string path = "launcher.ini")
    {
        _path = path;
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var separator = line.IndexOf('=');
                if (separator > 0)
                {
                    _values[line[..separator]] = line[(separator + 1)..];
                }
            }
        }
        if (!_values.TryGetValue("application.clientId", out var clientId))
        {
            clientId = Guid.NewGuid().ToString("D");
            _values["application.clientId"] = clientId;
            Save();
        }
        ClientId = Guid.Parse(clientId);
    }

    public Guid ClientId { get; }

    public string? Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public void Set(string key, string value)
    {
        _values[key] = value;
        Save();
    }

    private void Save() =>
        File.WriteAllLines(_path, _values.Select(pair => $"{pair.Key}={pair.Value}"));
}
