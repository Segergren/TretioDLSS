using System.Reflection;

namespace TretioDLSS.App;

public static class AppInfo
{
    public const string Name = "TretioDLSS";
    public const string ExeName = "TretioDLSS.exe";
    public const int WebSocketPort = 44130;
    public const int FrontendPortStart = 44140;
    public const int ViteDevPort = 2992;
    public const string MutexName = "TretioDLSS_SingleInstance";
    public const string PipeName = "TretioDLSS_Pipe";
    public const string UpdateSource = "https://github.com/Segergren/TretioDLSS";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static string? _dataFolder;

    /// <summary>%APPDATA%\TretioDLSS, overridable with TRETIO_DATA for tests.</summary>
    public static string DataFolder
    {
        get
        {
            if (_dataFolder != null) return _dataFolder;
            var overridden = Environment.GetEnvironmentVariable("TRETIO_DATA");
            _dataFolder = string.IsNullOrEmpty(overridden)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name)
                : overridden;
            Directory.CreateDirectory(_dataFolder);
            return _dataFolder;
        }
        set => _dataFolder = value;
    }

    public static string LogFile => Path.Combine(DataFolder, "logs", "tretio.log");
    public static string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public static string ShaderCache => Path.Combine(DataFolder, "shadercache");
    public static string SafeModeMarker => Path.Combine(DataFolder, "engine-running.marker");
    public static string ModelsFolder => Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>Repo root when running from a dev build, used to find ThirdParty and TestMedia.</summary>
    public static string? RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TretioDLSS.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
