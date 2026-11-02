using AirPdf.Core.Services;
using Xunit;

namespace AirPdf.Core.Tests;

/// <summary>
/// Covers the persistence contract the MSIX packaging work depends on: an existing
/// settings file is overwritten rather than replaced (so a packaged build does not fork
/// into its private AppData cache), and a damaged file falls back to the previous copy.
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"airpdf-settings-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(_directory))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Save_WritesTheFile_WhenNoneExists()
    {
        var service = new SettingsService(_directory);

        service.Save(new AppSettings { Theme = "Dark", Language = "zh-CN" });

        Assert.True(File.Exists(service.SettingsPath));
        Assert.False(File.Exists(service.SettingsPath + ".tmp"), "a temporary file was left behind");

        var reloaded = service.Load();
        Assert.Equal("Dark", reloaded.Theme);
        Assert.Equal("zh-CN", reloaded.Language);
    }

    [Fact]
    public void Save_OverAnExistingFile_KeepsThePreviousCopyAsBackup()
    {
        var service = new SettingsService(_directory);
        service.Save(new AppSettings { Theme = "Light" });
        service.Save(new AppSettings { Theme = "Dark" });

        Assert.True(File.Exists(service.BackupPath));
        Assert.Contains("\"Light\"", File.ReadAllText(service.BackupPath));
        Assert.Contains("\"Dark\"", File.ReadAllText(service.SettingsPath));
        Assert.False(File.Exists(service.SettingsPath + ".tmp"), "a temporary file was left behind");
    }

    [Fact]
    public void Save_OverAnExistingFile_DoesNotReplaceIt()
    {
        var service = new SettingsService(_directory);
        service.Save(new AppSettings { Theme = "Light" });

        // An open-for-read handle makes a replace-by-rename fail on Windows; an
        // in-place overwrite only needs write access, which this share mode still
        // denies, so instead the file's identity is compared through its index.
        var before = GetFileIndex(service.SettingsPath);

        service.Save(new AppSettings { Theme = "Dark" });

        Assert.Equal(before, GetFileIndex(service.SettingsPath));
        Assert.Equal("Dark", service.Load().Theme);
    }

    [Fact]
    public void Load_FallsBackToBackup_WhenTheMainFileIsTruncated()
    {
        var service = new SettingsService(_directory);
        service.Save(new AppSettings { Theme = "Light", Language = "en" });
        service.Save(new AppSettings { Theme = "Dark", Language = "zh-CN" });

        // What an interrupted in-place write leaves behind.
        File.WriteAllText(service.SettingsPath, "{ \"theme\": \"Da");

        var reloaded = service.Load();

        Assert.Equal("Light", reloaded.Theme);
        Assert.Equal("en", reloaded.Language);
    }

    [Fact]
    public void Load_FallsBackToBackup_WhenTheMainFileIsEmpty()
    {
        var service = new SettingsService(_directory);
        service.Save(new AppSettings { Theme = "Light" });
        service.Save(new AppSettings { Theme = "Dark" });

        File.WriteAllText(service.SettingsPath, string.Empty);

        Assert.Equal("Light", service.Load().Theme);
    }

    [Fact]
    public void Load_FallsBackToBackup_WhenTheMainFileIsMissing()
    {
        var service = new SettingsService(_directory);
        service.Save(new AppSettings { Theme = "Light" });
        service.Save(new AppSettings { Theme = "Dark" });

        File.Delete(service.SettingsPath);

        Assert.Equal("Light", service.Load().Theme);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenEveryFileIsDamaged()
    {
        var service = new SettingsService(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(service.SettingsPath, "not json");
        File.WriteAllText(service.BackupPath, "also not json");

        var settings = service.Load();

        Assert.Equal("System", settings.Theme);
        Assert.Empty(settings.RecentFiles);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenNothingWasEverSaved()
    {
        var settings = new SettingsService(_directory).Load();

        Assert.Equal("System", settings.Theme);
        Assert.Equal("en", settings.Language);
        Assert.Empty(settings.Bookmarks);
    }

    [Fact]
    public void Save_SurvivesAReadOnlyFile()
    {
        var service = new SettingsService(_directory);
        service.Save(new AppSettings { Theme = "Light" });

        File.SetAttributes(service.SettingsPath, FileAttributes.ReadOnly);
        try
        {
            // Settings are never allowed to stop a document opening.
            service.Save(new AppSettings { Theme = "Dark" });
        }
        finally
        {
            File.SetAttributes(service.SettingsPath, FileAttributes.Normal);
        }

        Assert.Equal("Light", service.Load().Theme);
        Assert.False(File.Exists(service.SettingsPath + ".tmp"), "a temporary file was left behind");
    }

    [Fact]
    public void Save_ReplacesAStaleTemporaryFile()
    {
        var service = new SettingsService(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(service.SettingsPath + ".tmp", "left over from a crash");

        // No existing settings file, so this goes through the atomic path.
        service.Save(new AppSettings { Theme = "Dark" });

        Assert.Equal("Dark", service.Load().Theme);
        Assert.False(File.Exists(service.SettingsPath + ".tmp"));
    }

    /// <summary>
    /// Stable identity for a file on disk. Replacing a file (write, rename) creates a new
    /// directory entry, so the index changes; overwriting its contents keeps the index.
    /// </summary>
    private static string GetFileIndex(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            // Enough for the assertion on Unix, where rename also swaps the inode.
            return new FileInfo(path).Length.ToString();
        }

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var info = GetFileInformation(handle);
        return $"{info.VolumeSerialNumber:x}-{info.FileIndexHigh:x}{info.FileIndexLow:x}";
    }

    private static NativeFileInfo GetFileInformation(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            throw new InvalidOperationException("GetFileInformationByHandle failed.");
        }

        return info;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        out NativeFileInfo information);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeFileInfo
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
