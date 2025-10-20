using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace LidairrCompanion.Models
{
    public enum SettingKey
    {
        LidarrURL,
        LidarrAPIKey,
        OllamaURL,
        LidarrImportPath,
        LidarrImportPathRemote,
        OllamaModel
    }



    public class SettingItem
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    public class AppSettings
    {
        private const string FilePath = "appsettings.json";

        public static AppSettings Current { get; set; } = new AppSettings();

        [JsonInclude]
        public Dictionary<string, string> Settings { get; private set; }

        public AppSettings()
        {
            Settings = Enum.GetNames(typeof(SettingKey))
                .ToDictionary(key => key, key => string.Empty);
        }






        public ObservableCollection<SettingItem> ToCollection()
        {
            var collection = new ObservableCollection<SettingItem>();
            foreach (var kvp in Settings)
            {
                collection.Add(new SettingItem { Name = kvp.Key, Value = kvp.Value });
            }
            return collection;
        }

        public void UpdateFromCollection(ObservableCollection<SettingItem> items)
        {
            foreach (var item in items)
            {
                if (Settings.ContainsKey(item.Name))
                    Settings[item.Name] = item.Value;
            }
        }

        public string Get(SettingKey key)
        {
            return Settings.TryGetValue(key.ToString(), out var value) ? value : string.Empty;
        }

        // Static helper for shorter calls
        public static string GetValue(SettingKey key) => Current.Get(key);

        public static void Load()
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loadedSettings = JsonSerializer.Deserialize<AppSettings>(json);
                if (loadedSettings != null)
                {
                    Current = loadedSettings;
                }
            }
            else
            {
                Current = new AppSettings();
            }
            Current.EnsureAllKeys();
        }



        public void EnsureAllKeys()
        {
            foreach (var key in Enum.GetNames(typeof(SettingKey)))
            {
                if (!Settings.ContainsKey(key))
                    Settings[key] = string.Empty;
            }
        }

        public static void Save()
        {
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }
}