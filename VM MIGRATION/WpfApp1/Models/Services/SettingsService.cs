using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;
using WpfApp1.Models;

namespace WpfApp1.Services
{
    /// <summary>
    /// Enterprise configuration store managing atomic disk persistence (%AppData%\WinMigratePro\config.json),
    /// system UAC elevation verification, and native Windows CredSSP delegation.
    /// </summary>
    public sealed class SettingsService
    {
        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinMigratePro");

        private static readonly string ConfigFilePath = Path.Combine(AppDataFolder, "config.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        public static string GetConfigPath() => ConfigFilePath;

        public static bool IsRunningAsAdmin()
        {
            if (!OperatingSystem.IsWindows()) return false;

            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>
        /// Loads persistent settings from %AppData%\WinMigratePro\config.json.
        /// Automatically initializes default host templates on initial first run.
        /// </summary>
        public static async Task<AppSettingsModel> LoadSettingsAsync()
        {
            try
            {
                if (!Directory.Exists(AppDataFolder))
                {
                    Directory.CreateDirectory(AppDataFolder);
                }

                if (!File.Exists(ConfigFilePath))
                {
                    var defaults = new AppSettingsModel();
                    defaults.SavedHosts.Add(new ManagedHostEntry
                    {
                        Hostname = "192.168.25.29",
                        IsOnline = true
                    });

                    await SaveSettingsAsync(defaults);
                    return defaults;
                }

                var json = await File.ReadAllTextAsync(ConfigFilePath);
                var settings = JsonSerializer.Deserialize<AppSettingsModel>(json, JsonOptions);
                return settings ?? new AppSettingsModel();
            }
            catch
            {
                return new AppSettingsModel();
            }
        }

        /// <summary>
        /// Atomically saves application settings to disk using a temporary staging swap to prevent corruption.
        /// </summary>
        public static async Task SaveSettingsAsync(AppSettingsModel settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }

            var tempFilePath = Path.Combine(AppDataFolder, $"config_{Guid.NewGuid():N}.tmp");

            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                await File.WriteAllTextAsync(tempFilePath, json);

                // Atomic file swap
                File.Move(tempFilePath, ConfigFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempFilePath))
                {
                    try { File.Delete(tempFilePath); } catch { }
                }
            }
        }

        /// <summary>
        /// Exports the active configuration to an external backup destination.
        /// </summary>
        public static async Task ExportSettingsBackupAsync(string destinationPath, AppSettingsModel settings)
        {
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentNullException(nameof(destinationPath));
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            await File.WriteAllTextAsync(destinationPath, json);
        }

        /// <summary>
        /// Imports and validates an external configuration backup file.
        /// </summary>
        public static async Task<AppSettingsModel?> ImportSettingsBackupAsync(string sourcePath)
        {
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Specified backup file does not exist.", sourcePath);

            var json = await File.ReadAllTextAsync(sourcePath);
            return JsonSerializer.Deserialize<AppSettingsModel>(json, JsonOptions);
        }

        /// <summary>
        /// Configures local and remote CredSSP delegation for multi-hop migration operations
        /// using the native Windows PowerShell subsystem.
        /// </summary>
        public static async Task ConfigureCredSspAsync(string targetHost)
        {
            if (!IsRunningAsAdmin())
            {
                throw new UnauthorizedAccessException("CredSSP delegation configuration requires Administrator privileges. Run WinMigrate as Administrator to apply this change.");
            }

            await Task.Run(() =>
            {
                var delegateTarget = string.IsNullOrWhiteSpace(targetHost) ? "*" : targetHost.Trim();

                // 1. Ensure WinRM service is running
                // 2. Enable CredSSP Client role
                // 3. Set WSMan Client CredSSP auth to True
                var psScript = $@"
                    Start-Service winrm -ErrorAction SilentlyContinue
                    Enable-WSManCredSSP -Role Client -DelegateComputer '{delegateTarget}' -Force
                    Set-Item -Path WSMan:\localhost\Client\Auth\CredSSP -Value $true -Force
                ";

                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{psScript.Replace("\r\n", " ").Replace("\n", " ")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    proc.WaitForExit();
                    var err = proc.StandardError.ReadToEnd();

                    if (proc.ExitCode != 0 && !string.IsNullOrWhiteSpace(err))
                    {
                        throw new InvalidOperationException($"CredSSP configuration failed: {err}");
                    }
                }
                else
                {
                    throw new InvalidOperationException("Failed to launch powershell.exe for CredSSP configuration.");
                }
            });
        }
    }
}