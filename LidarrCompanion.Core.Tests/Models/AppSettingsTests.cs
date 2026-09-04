using LidarrCompanion.Models;

namespace LidarrCompanion.Core.Tests.Models
{
    // AppSettings.Load()/Save() read/write a fixed relative "appsettings.json" path, so these
    // tests run against an isolated temp working directory and restore state afterwards.
    public class AppSettingsTests : IDisposable
    {
        private readonly string _originalCwd;
        private readonly string _tempDir;
        private readonly AppSettings _originalCurrent;

        public AppSettingsTests()
        {
            _originalCwd = Directory.GetCurrentDirectory();
            _originalCurrent = AppSettings.Current;
            _tempDir = Path.Combine(Path.GetTempPath(), "LidarrCompanionSettingsTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            Directory.SetCurrentDirectory(_tempDir);
        }

        public void Dispose()
        {
            Directory.SetCurrentDirectory(_originalCwd);
            AppSettings.Current = _originalCurrent;
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }

        [Fact]
        public void NewAppSettings_PopulatesAllKeysWithDefaults()
        {
            var settings = new AppSettings();

            Assert.Equal(50, settings.GetTyped<int>(SettingKey.SiftVolume));
            Assert.False(settings.GetTyped<bool>(SettingKey.DarkMode));
            Assert.Equal("/mnt/Music/Albums", settings.Get(SettingKey.DefaultArtistRootFolder));
        }

        [Fact]
        public void Load_NoFileOnDisk_UsesDefaults()
        {
            AppSettings.Load();

            Assert.Equal(30, AppSettings.Current.GetTyped<int>(SettingKey.LidarrHttpTimeout));
        }

        [Fact]
        public void SaveThenLoad_RoundTripsScalarAndImportDestinations()
        {
            AppSettings.Current = new AppSettings();
            AppSettings.Current.Settings[SettingKey.LidarrURL.ToString()] = "http://lidarr.example:8686";
            AppSettings.Current.Settings[SettingKey.DarkMode.ToString()] = true;
            AppSettings.Current.Settings[SettingKey.LidarrHttpTimeout.ToString()] = 45;
            AppSettings.Current.ImportDestinations.Add(new ImportDestination
            {
                Name = "Singles",
                DestinationPath = "/mnt/library/Singles",
                RequireArtwork = true
            });

            AppSettings.Save();

            // Simulate a fresh process picking the file back up.
            AppSettings.Current = new AppSettings();
            AppSettings.Load();

            Assert.Equal("http://lidarr.example:8686", AppSettings.Current.Get(SettingKey.LidarrURL));
            Assert.True(AppSettings.Current.GetTyped<bool>(SettingKey.DarkMode));
            Assert.Equal(45, AppSettings.Current.GetTyped<int>(SettingKey.LidarrHttpTimeout));

            var dest = Assert.Single(AppSettings.Current.ImportDestinations);
            Assert.Equal("Singles", dest.Name);
            Assert.Equal("/mnt/library/Singles", dest.DestinationPath);
            Assert.True(dest.RequireArtwork);
        }

        [Fact]
        public void ToCollection_PairsLightAndDarkColorSettings()
        {
            AppSettings.Current = new AppSettings();

            var collection = AppSettings.Current.ToCollection();

            var importMatch = Assert.Single(collection, i => i.Name == SettingKey.ColorImportMatch.ToString());
            Assert.True(importMatch.IsColorPair);
            Assert.Equal(SettingKey.ColorImportMatchDark.ToString(), importMatch.PairedSettingName);
            Assert.DoesNotContain(collection, i => i.Name == SettingKey.ColorImportMatchDark.ToString());
        }

        [Fact]
        public void GetPathMappingSettings_ReturnsConfiguredServerAndLocalPaths()
        {
            AppSettings.Current = new AppSettings();
            AppSettings.Current.Settings[SettingKey.ImportPathLidarr.ToString()] = "/data/import";
            AppSettings.Current.Settings[SettingKey.ImportPathCompanion.ToString()] = "/mnt/local/import";

            var (serverPath, localMapping) = AppSettings.GetPathMappingSettings(SettingKey.ImportPathLidarr);

            Assert.Equal("/data/import", serverPath);
            Assert.Equal("/mnt/local/import", localMapping);
        }
    }
}
