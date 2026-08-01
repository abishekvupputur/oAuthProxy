using System;
using System.IO;
using System.Text.Json;

namespace RavensPort.Core.Vault;

public class LocalSettingsData
{
    public string OnePasswordAccountName { get; set; } = "";
}

public static class LocalSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RavensPort",
        "local_settings.json");

    private static LocalSettingsData? _current;

    public static LocalSettingsData Current
    {
        get
        {
            if (_current == null)
            {
                Load();
            }
            return _current!;
        }
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                _current = JsonSerializer.Deserialize<LocalSettingsData>(json) ?? new LocalSettingsData();
            }
            else
            {
                _current = new LocalSettingsData();
            }
        }
        catch
        {
            _current = new LocalSettingsData();
        }
    }

    public static void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory!);
            }

            var json = JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Ignore write failures for local non-critical UI settings
        }
    }
}
