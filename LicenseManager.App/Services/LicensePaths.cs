using System.IO;

namespace LicenseManager.Services;

internal static class LicensePaths
{
    public static string ResolveDataDirectory(string? storageDir = null)
    {
        var directory = storageDir
            ?? Environment.GetEnvironmentVariable("KANBAN_DATA_DIR")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Kanban");

        Directory.CreateDirectory(directory);
        return directory;
    }
}