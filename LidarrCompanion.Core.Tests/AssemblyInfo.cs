// LidarrCompanion.Models.AppSettings and LidarrCompanion.Helpers.Logger are static singletons
// (ported as-is from the WPF app to keep the same call signatures). Running test classes in
// parallel would let tests that touch AppSettings.Current/appsettings.json race with each other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
