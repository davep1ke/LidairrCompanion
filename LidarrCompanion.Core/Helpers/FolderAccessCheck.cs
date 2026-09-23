namespace LidarrCompanion.Helpers
{
    // A real permission failure on one of these paths (CIFS/NAS shares, in practice) is easy to
    // miss until it breaks something mid-import - Directory.Exists/a plain listing can look fine
    // (a nounix CIFS mount shows a cosmetic, always-succeeding "0755" regardless of the real
    // server-side permissions - see CLAUDE.md) right up until an actual write is attempted. This
    // runs the same kind of probe used to diagnose that live: list the directory for real, and for
    // anything the app writes to, actually create and remove a small temp file.
    public static class FolderAccessCheck
    {
        public record Result(string Label, string Path, bool Ok, string? Error);

        // A blank/unconfigured path isn't a failure - there's nothing to check.
        public static Result Check(string label, string? path, bool requireWrite)
        {
            if (string.IsNullOrWhiteSpace(path))
                return new Result(label, string.Empty, true, null);

            try
            {
                if (!Directory.Exists(path))
                    return new Result(label, path, false, "folder not found");

                // Forces a real read, not just a stat.
                _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();

                if (requireWrite)
                {
                    var probePath = Path.Combine(path, $".lidarrcompanion-access-check-{Guid.NewGuid():N}");
                    File.WriteAllBytes(probePath, Array.Empty<byte>());
                    File.Delete(probePath);
                }

                return new Result(label, path, true, null);
            }
            catch (Exception ex)
            {
                return new Result(label, path, false, ex.Message);
            }
        }
    }
}
