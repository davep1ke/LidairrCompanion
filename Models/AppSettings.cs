using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;

namespace LidarrCompanion.Models
{
    // Attribute to attach metadata to settings enum members
    [AttributeUsage(AttributeTargets.Field)]
    public class SettingAttribute : Attribute
    {
        public Type Type { get; }
        public string Description { get; }
        public object DefaultValue { get; }
        public string Category { get; }

        public SettingAttribute(Type type, string description, string category = "")
        {
            Type = type;
            Description = description;
            Category = string.IsNullOrWhiteSpace(category) ? "General" : category;
            if (type == typeof(bool)) DefaultValue = false;
            else if (type == typeof(int)) DefaultValue =0;
            else DefaultValue = string.Empty;
        }

        public SettingAttribute(Type type, string description, object defaultValue, string category = "")
        {
            Type = type;
            Description = description;
            Category = string.IsNullOrWhiteSpace(category) ? "General" : category;
            DefaultValue = defaultValue ?? (type == typeof(bool) ? (object)false : type == typeof(int) ? (object)0 : string.Empty);
        }
    }

    public enum SettingKey
    {
        // Lidarr
        [Setting(typeof(string), "Base URL for your Lidarr instance", "Lidarr")]
        LidarrURL,
        [Setting(typeof(string), "API key for Lidarr", "Lidarr")]
        LidarrAPIKey,
        [Setting(typeof(int), "HTTP timeout in seconds for Lidarr calls",30, "Lidarr")]
        LidarrHttpTimeout,

        // Import
        [Setting(typeof(string), "Import path used by Lidarr [Server]", "Import")]
        LidarrImportPath,
        [Setting(typeof(string), "Local mapping for Lidarr import path [Local]", "Import")]
        LidarrImportPathLocal,

        //Ollama
        [Setting(typeof(string), "URL for Ollama server", "Ollama")]
        OllamaURL,
        [Setting(typeof(string), "Ollama model to use", "Ollama")]
        OllamaModel,

        //Backups and Moving
        [Setting(typeof(bool), "Create a backup of files before importing", "Backup")]
        BackupFilesBeforeImport,
        [Setting(typeof(string), "Root folder where backups will be stored [Local]", "Backup")]
        BackupRootFolder,
        [Setting(typeof(bool), "Also copy imported files to a separate location", "Copy")]
        CopyImportedFiles,
        [Setting(typeof(string), "Destination path for copied imported files [Local]", "Copy")]
        CopyImportedFilesPath,

        //Not Selected
        [Setting(typeof(string), "Path to move files that are marked 'Not Selected' before import [Local]", "NotSelectedFiles")]
        NotSelectedPath,
        [Setting(typeof(bool), "Also backup files that are moved to Not-Selected", "NotSelectedFiles")]
        BackupNotSelectedFiles,
        [Setting(typeof(bool), "Also copy files that are moved to Not-Selected", "NotSelectedFiles")]
        CopyNotSelectedFiles,

        // Defer options
        [Setting(typeof(string), "Destination path for deferred files [Local]", "Defer")]
        DeferDestinationPath,
        [Setting(typeof(bool), "Also backup files that have been deferred", "Defer")]
        BackupDeferredFiles,
        [Setting(typeof(bool), "Also copy files that have been deferred", "Defer")]
        CopyDeferredFiles,


        // Match scoring configuration (max points) - Weights
        [Setting(typeof(int), "Direct match max score (words match irrespective of order)",15, "Weights")]
        Direct,
        [Setting(typeof(int), "Exact match max score (full string match)",10, "Weights")]
        Exact,
        [Setting(typeof(int), "Clean match max score (cleaned words percentage)",8, "Weights")]
        Clean,
        [Setting(typeof(int), "Minimal match max score (minimalized words percentage)",6, "Weights")]
        Minimal,
        [Setting(typeof(int), "MinClean match max score (minimal+clean overlap percentage)",4, "Weights")]
        MinClean,
        [Setting(typeof(int), "Release-level boost when other files are assigned",10, "Weights")]
        ReleaseBoost,
    }


    public class SettingItem : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _value = string.Empty;
        private string _description = string.Empty;
        private string _category = string.Empty;
        private bool _isHighlighted = false;

        public string Name { get => _name; set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } } }
        public string Value { get => _value; set { if (_value != value) { _value = value; OnPropertyChanged(nameof(Value)); } } }
        public string Description { get => _description; set { if (_description != value) { _description = value; OnPropertyChanged(nameof(Description)); } } }
        public string Category { get => _category; set { if (_category != value) { _category = value; OnPropertyChanged(nameof(Category)); } } }

        // New: whether the UI should display this setting as highlighted (bold)
        public bool IsHighlighted { get => _isHighlighted; set { if (_isHighlighted != value) { _isHighlighted = value; OnPropertyChanged(nameof(IsHighlighted)); } } }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propName));
        }
    }

    public class AppSettings
    {
        private const string FilePath = "appsettings.json";

        public static AppSettings Current { get; set; } = new AppSettings();

        // Values are stored as boxed objects so booleans/ints/strings are preserved in JSON
        [JsonInclude]
        public Dictionary<string, object> Settings { get; private set; }

        public AppSettings()
        {
            Settings = Enum.GetNames(typeof(SettingKey))
                .ToDictionary(key => key, key => (object)string.Empty);
        }

        private static SettingAttribute? GetAttributeForKey(string name)
        {
            if (!Enum.TryParse<SettingKey>(name, out var k)) return null;
            var mem = typeof(SettingKey).GetMember(k.ToString());
            if (mem != null && mem.Length >0)
            {
                return mem[0].GetCustomAttribute<SettingAttribute>();
            }
            return null;
        }

        private static Type GetTypeForKey(string name)
        {
            var attr = GetAttributeForKey(name);
            if (attr != null) return attr.Type;
            return typeof(string);
        }

        private static string GetDescriptionForKey(string name)
        {
            var attr = GetAttributeForKey(name);
            if (attr != null) return attr.Description;
            return string.Empty;
        }

        private static string GetCategoryForKey(string name)
        {
            var attr = GetAttributeForKey(name);
            if (attr != null) return attr.Category;
            return "General";
        }

        private static object DefaultValueForType(Type t)
        {
            if (t == typeof(bool)) return false;
            if (t == typeof(int)) return 0;
            return string.Empty;
        }

        private static object ParseStringToType(string str, Type t)
        {
            if (t == typeof(bool))
            {
                if (bool.TryParse(str, out var b)) return b;
                // Accept common truthy values
                if (!string.IsNullOrEmpty(str))
                {
                    var lowered = str.Trim().ToLowerInvariant();
                    if (lowered == "1" || lowered == "yes" || lowered == "y" || lowered == "true") return true;
                    if (lowered == "0" || lowered == "no" || lowered == "n" || lowered == "false") return false;
                }
                return false;
            }
            if (t == typeof(int))
            {
                if (int.TryParse(str, out var i)) return i;
                return 0;
            }
            return str ?? string.Empty;
        }

        private static string ObjectToString(object o)
        {
            if (o == null) return string.Empty;
            if (o is JsonElement je)
            {
                switch (je.ValueKind)
                {
                    case JsonValueKind.String: return je.GetString() ?? string.Empty;
                    case JsonValueKind.Number: return je.GetRawText();
                    case JsonValueKind.True: return "True";
                    case JsonValueKind.False: return "False";
                    default: return je.GetRawText();
                }
            }
            return o.ToString();
        }

        public ObservableCollection<SettingItem> ToCollection()
        {
            var collection = new ObservableCollection<SettingItem>();
            // Only include keys that are defined in the enum; get their category and description
            var validKeys = Enum.GetNames(typeof(SettingKey)).ToHashSet();
            var items = Settings
                .Where(kvp => validKeys.Contains(kvp.Key))
                .Select(kvp => new SettingItem
                {
                    Name = kvp.Key,
                    Value = ObjectToString(kvp.Value),
                    Description = GetDescriptionForKey(kvp.Key),
                    Category = GetCategoryForKey(kvp.Key)
                })
                .OrderBy(si => si.Category)
                .ThenBy(si => si.Name);

            foreach (var it in items) collection.Add(it);
            return collection;
        }

        public void UpdateFromCollection(ObservableCollection<SettingItem> items)
        {
            foreach (var item in items)
            {
                if (Settings.ContainsKey(item.Name))
                {
                    var targetType = GetTypeForKey(item.Name);
                    Settings[item.Name] = ParseStringToType(item.Value, targetType);
                }
            }
        }

        // Return string representation (keeps compatibility with existing callers)
        public string Get(SettingKey key)
        {
            var name = key.ToString();
            if (!Settings.TryGetValue(name, out var value)) return string.Empty;
            return ObjectToString(value);
        }

        // Return raw typed value
        public T GetTyped<T>(SettingKey key)
        {
            var name = key.ToString();
            if (!Settings.TryGetValue(name, out var value)) return default!;
            if (value is JsonElement je)
            {
                try
                {
                    if (typeof(T) == typeof(string)) return (T)(object)(je.GetString() ?? string.Empty);
                    if (typeof(T) == typeof(bool)) return (T)(object)je.GetBoolean();
                    if (typeof(T) == typeof(int)) return (T)(object)je.GetInt32();
                }
                catch
                {
                    return default!;
                }
            }
            if (value is T tVal) return tVal;
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return default!;
            }
        }

        // Static helper for shorter calls (keeps compatibility)
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

            // Discard any keys not present in the SettingKey enum
            var validKeys = Enum.GetNames(typeof(SettingKey)).ToHashSet();
            Current.Settings = Current.Settings
                .Where(kvp => validKeys.Contains(kvp.Key))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

            // Convert any JsonElement values to appropriate CLR types based on the enum attributes
            var keys = Current.Settings.Keys.ToList();
            foreach (var k in keys)
            {
                var t = GetTypeForKey(k);
                var val = Current.Settings[k];
                if (val is JsonElement je)
                {
                    object converted;
                    try
                    {
                        if (t == typeof(bool) && (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False))
                            converted = je.GetBoolean();
                        else if (t == typeof(int) && je.ValueKind == JsonValueKind.Number)
                            converted = je.GetInt32();
                        else if (je.ValueKind == JsonValueKind.String)
                            converted = je.GetString() ?? string.Empty;
                        else if (je.ValueKind == JsonValueKind.Number)
                        {
                            // fallback - try int then raw
                            if (je.TryGetInt32(out var i)) converted = i;
                            else converted = je.GetRawText();
                        }
                        else converted = je.GetRawText();
                    }
                    catch
                    {
                        converted = DefaultValueForType(t);
                    }

                    Current.Settings[k] = converted;
                }
                else if (val == null)
                {
                    Current.Settings[k] = DefaultValueForType(t);
                }
            }

            Current.EnsureAllKeys();
        }

        public void EnsureAllKeys()
        {
            // Ensure all known enum keys are present and initialized with typed defaults (using attribute default when provided)
            foreach (var key in Enum.GetValues(typeof(SettingKey)).Cast<SettingKey>())
            {
                var keyName = key.ToString();
                if (!Settings.ContainsKey(keyName))
                {
                    var attr = GetAttributeForKey(keyName);
                    Settings[keyName] = attr != null ? attr.DefaultValue : DefaultValueForType(typeof(string));
                }
                else
                {
                    // If present but null, set default
                    if (Settings[keyName] == null)
                    {
                        var attr = GetAttributeForKey(keyName);
                        Settings[keyName] = attr != null ? attr.DefaultValue : DefaultValueForType(typeof(string));
                    }
                }
            }

            // Also ensure any keys present (but without attributes) at least have string defaults
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