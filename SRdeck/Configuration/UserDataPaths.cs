using System.IO;

namespace SRdeck.Configuration;

public static class UserDataPaths
{
    private const string AppFolderName = "SRdeck";

    public static string UserDataDirectory
    {
        get
        {
            string path = UserDataDirectoryPath;
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static string UserDataDirectoryPath
    {
        get
        {
            var appDataRootPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(appDataRootPath))
            {
                appDataRootPath = AppContext.BaseDirectory;
            }

            return Path.Combine(appDataRootPath, AppFolderName);
        }
    }

    public static string AppSettingsPath => Path.Combine(UserDataDirectoryPath, "appsettings.json");
    public static string HardwareSettingsPath => Path.Combine(UserDataDirectoryPath, "hardware.json");
    public static string LastStatePath => Path.Combine(UserDataDirectoryPath, "last_state.json");
    public static string FftCalibrationPath => Path.Combine(UserDataDirectoryPath, "fft_calibration.json");
    public static string StationsPath => Path.Combine(UserDataDirectory, "stations.json");
    public static string BandPlansPath => Path.Combine(UserDataDirectory, "bandplans.json");
    public static string PluginsDirectory => Path.Combine(UserDataDirectory, "plugins");
}
