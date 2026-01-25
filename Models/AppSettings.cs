using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Linq;
using System.Collections.Generic;
using LidarrCompanion.Helpers;

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
        // Sift
        [Setting(typeof(string), "Folder containing tracks to sift through [Local]", "Sift")]
        SiftFolder,
        [Setting(typeof(int), "Default playback volume (0-100)", 50, "Sift")]
        SiftVolume,
        [Setting(typeof(int), "Default playback start position percentage (0, 15, 30, 45, 60, 75, 90)", 0, "Sift")]
        SiftDefaultPosition,

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
        // Library root (where Lidarr stores music) and its local mapping
        [Setting(typeof(string), "Library root path used by Lidarr [Server]", "Import")]
        LidarrLibraryPath,
        [Setting(typeof(string), "Local mapping for Lidarr library root [Local]", "Import")]
        LidarrLibraryPathLocal,

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

        // UI Preferences
        [Setting(typeof(bool), "Enable dark mode theme", false, "UI")]
        DarkMode,

        // Row Highlighting Colors (hex format: #RRGGBB)
        [Setting(typeof(string), "Matched/Import row color", "#90EE90", "Colors")]
        ColorImportMatch,
        [Setting(typeof(string), "Matched/Import row color (dark mode)", "#228B22", "Colors")]
        ColorImportMatchDark,
        [Setting(typeof(string), "Not For Import row color", "#FFA500", "Colors")]
        ColorNotForImport,
        [Setting(typeof(string), "Not For Import row color (dark mode)", "#FF8C00", "Colors")]
        ColorNotForImportDark,
        [Setting(typeof(string), "Defer row color", "#F0E68C", "Colors")]
        ColorDefer,
        [Setting(typeof(string), "Defer row color (dark mode)", "#BDB76B", "Colors")]
        ColorDeferDark,
        [Setting(typeof(string), "Unlink row color", "#FFA07A", "Colors")]
        ColorUnlink,
        [Setting(typeof(string), "Unlink row color (dark mode)", "#E9967A", "Colors")]
        ColorUnlinkDark,
        [Setting(typeof(string), "Delete row color", "#F08080", "Colors")]
        ColorDelete,
        [Setting(typeof(string), "Delete row color (dark mode)", "#CD5C5C", "Colors")]
        ColorDeleteDark,

        // Artist Release Track row colors (list_Artist_Releases)
        [Setting(typeof(string), "Track Has Existing File row color", "#D3D3D3", "Colors")]
        ColorTrackHasFile,
        [Setting(typeof(string), "Track Has Existing File row color (dark mode)", "#505050", "Colors")]
        ColorTrackHasFileDark,
        [Setting(typeof(string), "Release Has Assigned Tracks row color", "#ADD8E6", "Colors")]
        ColorReleaseHasAssigned,
        [Setting(typeof(string), "Release Has Assigned Tracks row color (dark mode)", "#4682B4", "Colors")]
        ColorReleaseHasAssignedDark,
        [Setting(typeof(string), "Track Assigned/Matched row color", "#90EE90", "Colors")]
        ColorTrackAssigned,
        [Setting(typeof(string), "Track Assigned/Matched row color (dark mode)", "#228B22", "Colors")]
        ColorTrackAssignedDark,

        // Window state persistence
        [Setting(typeof(string), "Main window left position", "100", "UI")]
        WindowLeft,
        [Setting(typeof(string), "Main window top position", "100", "UI")]
        WindowTop,
        [Setting(typeof(string), "Main window width", "1278", "UI")]
        WindowWidth,
        [Setting(typeof(string), "Main window height", "966", "UI")]
        WindowHeight,
        [Setting(typeof(bool), "Main window maximized state", false, "UI")]
        WindowMaximized,

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
        public string Value 
        { 
            get => _value; 
            set 
            { 
                if (_value != value) 
                { 
                    _value = value; 
                    OnPropertyChanged(nameof(Value));
                    UpdateValueBrush();
                } 
            } 
        }
        public string Description { get => _description; set { if (_description != value) { _description = value; OnPropertyChanged(nameof(Description)); } } }
        public string Category { get => _category; set { if (_category != value) { _category = value; OnPropertyChanged(nameof(Category)); } } }

        // New: whether the UI should display this setting as highlighted (bold)
        public bool IsHighlighted { get => _isHighlighted; set { if (_isHighlighted != value) { _isHighlighted = value; OnPropertyChanged(nameof(IsHighlighted)); } } }

        // New: for color pairs - indicates if this is a paired color setting
        public bool IsColorPair { get; set; }
        public string? DarkModeValue { get; set; }
        public string? LightModeValue { get; set; }
        public string? PairedSettingName { get; set; }

        // Pre-converted brush for colors to avoid string->brush conversion during scrolling
        public System.Windows.Media.Brush? ValueBrush { get; private set; }

        private void UpdateValueBrush()
        {
            if (Category == "Colors" && !IsColorPair && !string.IsNullOrWhiteSpace(_value))
            {
                try
                {
                    var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(_value);
                    ValueBrush = new System.Windows.Media.SolidColorBrush(color);
                }
                catch
                {
                    ValueBrush = System.Windows.Media.Brushes.Transparent;
                }
            }
            else
            {
                ValueBrush = System.Windows.Media.Brushes.Transparent;
            }
            OnPropertyChanged(nameof(ValueBrush));
        }

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

        // New: collection of import destinations
        [JsonInclude]
        public List<ImportDestination> ImportDestinations { get; set; } = new List<ImportDestination>();

        public AppSettings()
        {
            // Initialize all settings with their default values from attributes
            Settings = new Dictionary<string, object>();
            foreach (SettingKey key in Enum.GetValues(typeof(SettingKey)))
            {
                var keyName = key.ToString();
                var attr = GetAttributeForKey(keyName);
                Settings[keyName] = attr?.DefaultValue ?? string.Empty;
            }
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
            var validKeys = Enum.GetNames(typeof(SettingKey)).ToHashSet();
            var processedKeys = new HashSet<string>();

            var allItems = Settings
                .Where(kvp => validKeys.Contains(kvp.Key))
                .Select(kvp => new SettingItem
                {
                    Name = kvp.Key,
                    Value = ObjectToString(kvp.Value),
                    Description = GetDescriptionForKey(kvp.Key),
                    Category = GetCategoryForKey(kvp.Key)
                })
                .OrderBy(si => si.Category)
                .ThenBy(si => si.Name)
                .ToList();

            foreach (var item in allItems)
            {
                // Skip if already processed as part of a pair
                if (processedKeys.Contains(item.Name))
                    continue;

                // Check if this is a color setting with a Dark variant
                if (item.Category == "Colors" && !item.Name.EndsWith("Dark"))
                {
                    var darkKeyName = item.Name + "Dark";
                    var darkItem = allItems.FirstOrDefault(i => i.Name == darkKeyName);

                    if (darkItem != null)
                    {
                        // Create a paired color item
                        var pairedItem = new SettingItem
                        {
                            Name = item.Name,
                            Description = item.Description.Replace(" row color", "").Replace(" (dark mode)", ""),
                            Category = item.Category,
                            IsColorPair = true,
                            LightModeValue = item.Value,
                            DarkModeValue = darkItem.Value,
                            Value = item.Value, // Display light value by default
                            PairedSettingName = darkKeyName
                        };

                        collection.Add(pairedItem);
                        processedKeys.Add(item.Name);
                        processedKeys.Add(darkKeyName);
                        continue;
                    }
                }

                // Not a color pair - add as normal
                collection.Add(item);
                processedKeys.Add(item.Name);
            }

            return collection;
        }

        public void UpdateFromCollection(ObservableCollection<SettingItem> items)
        {
            foreach (var item in items)
            {
                if (item.IsColorPair)
                {
                    // Update both light and dark mode values
                    if (Settings.ContainsKey(item.Name))
                    {
                        var targetType = GetTypeForKey(item.Name);
                        Settings[item.Name] = ParseStringToType(item.LightModeValue ?? item.Value, targetType);
                    }

                    if (!string.IsNullOrEmpty(item.PairedSettingName) && Settings.ContainsKey(item.PairedSettingName))
                    {
                        var targetType = GetTypeForKey(item.PairedSettingName);
                        Settings[item.PairedSettingName] = ParseStringToType(item.DarkModeValue ?? "", targetType);
                    }
                }
                else if (Settings.ContainsKey(item.Name))
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

        /// <summary>
        /// Gets the server and local path mapping settings for a given path key.
        /// </summary>
        /// <param name="pathKey">The setting key indicating which path mapping to retrieve</param>
        /// <returns>A tuple containing (serverPath, localMapping) or empty strings if not configured</returns>
        public static (string serverPath, string localMapping) GetPathMappingSettings(SettingKey pathKey)
        {
            return pathKey switch
            {
                SettingKey.LidarrImportPath => (
                    GetValue(SettingKey.LidarrImportPath),
                    GetValue(SettingKey.LidarrImportPathLocal)
                ),
                SettingKey.LidarrLibraryPath => (
                    GetValue(SettingKey.LidarrLibraryPath),
                    GetValue(SettingKey.LidarrLibraryPathLocal)
                ),
                _ => (string.Empty, string.Empty)
            };
        }

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

            // Ensure all enum keys exist in Settings dictionary with appropriate defaults
            var validKeys = Enum.GetNames(typeof(SettingKey)).ToHashSet();
            foreach (SettingKey key in Enum.GetValues(typeof(SettingKey)))
            {
                var keyName = key.ToString();
                if (!Current.Settings.ContainsKey(keyName))
                {
                    var attr = GetAttributeForKey(keyName);
                    Current.Settings[keyName] = attr?.DefaultValue ?? string.Empty;
                }
            }

            // Discard any keys not present in the SettingKey enum
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
        }


        public static void Save()
        {
            var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
    }
}