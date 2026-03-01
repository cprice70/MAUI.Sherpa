using System.Diagnostics;
using System.Text.Json;
using MauiSherpa.Core.Interfaces;

namespace MauiSherpa.Core.Services;

public class PhysicalDeviceService : IPhysicalDeviceService
{
    private readonly ILoggingService _logger;
    private readonly IPlatformService _platform;

    public PhysicalDeviceService(ILoggingService logger, IPlatformService platform)
    {
        _logger = logger;
        _platform = platform;
    }

    public bool IsSupported => _platform.IsMacCatalyst || _platform.IsMacOS;

    public async Task<IReadOnlyList<PhysicalDevice>> GetDevicesAsync()
    {
        if (!IsSupported) return [];
        try
        {
            var tempFile = Path.GetTempFileName();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "xcrun",
                    Arguments = $"devicectl list devices --json-output \"{tempFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return [];

                var stdoutTask = process.StandardOutput.ReadToEndAsync();
                var stderrTask = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdoutTask, stderrTask);
                await process.WaitForExitAsync();

                if (!File.Exists(tempFile))
                    return [];

                var json = await File.ReadAllTextAsync(tempFile);
                return ParseDevices(json);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to list physical devices: {ex.Message}", ex);
            return [];
        }
    }

    public async Task<bool> InstallAppAsync(string identifier, string appPath, IProgress<string>? progress = null)
    {
        if (!IsSupported) return false;
        try
        {
            progress?.Report($"Installing app on device...");
            var psi = new ProcessStartInfo
            {
                FileName = "xcrun",
                Arguments = $"devicectl device install app --device {identifier} \"{appPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                progress?.Report("Failed to start devicectl");
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                progress?.Report("App installed successfully");
                return true;
            }

            progress?.Report($"Failed: {error}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to install app: {ex.Message}", ex);
            progress?.Report($"Failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> LaunchAppAsync(string identifier, string bundleId, IProgress<string>? progress = null)
    {
        if (!IsSupported) return false;
        try
        {
            progress?.Report($"Launching {bundleId}...");
            var psi = new ProcessStartInfo
            {
                FileName = "xcrun",
                Arguments = $"devicectl device process launch --device {identifier} {bundleId}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                progress?.Report("Failed to start devicectl");
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                progress?.Report("App launched successfully");
                return true;
            }

            progress?.Report($"Failed: {error}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to launch app: {ex.Message}", ex);
            progress?.Report($"Failed: {ex.Message}");
            return false;
        }
    }

    public async Task<IReadOnlyList<PhysicalDeviceApp>> GetInstalledAppsAsync(string identifier)
    {
        if (!IsSupported) return [];

        var tempFile = Path.GetTempFileName();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "xcrun",
                Arguments = $"devicectl device info apps --device {identifier} --json-output \"{tempFile}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                throw new InvalidOperationException("Failed to start devicectl");

            // Read stdout/stderr concurrently to avoid deadlock
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            if (!File.Exists(tempFile))
                throw new InvalidOperationException("devicectl did not produce output");

            var json = await File.ReadAllTextAsync(tempFile);

            // Check for error response
            var errorMsg = ExtractError(json);
            if (errorMsg != null)
                throw new InvalidOperationException(errorMsg);

            return ParseApps(json);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    public async Task<string?> DownloadAppContainerAsync(string identifier, string bundleId, string outputDir, IProgress<string>? progress = null)
    {
        if (!IsSupported) return null;
        try
        {
            Directory.CreateDirectory(outputDir);
            progress?.Report($"Downloading container for {bundleId}...");

            var psi = new ProcessStartInfo
            {
                FileName = "xcrun",
                Arguments = $"devicectl device copy from --device {identifier} --domain-type appDataContainer --domain-identifier {bundleId} --destination-path \"{outputDir}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                progress?.Report("Failed to start devicectl");
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                progress?.Report("Container downloaded successfully");
                return outputDir;
            }

            progress?.Report($"Failed: {stderrTask.Result}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to download app container: {ex.Message}", ex);
            progress?.Report($"Failed: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> UninstallAppAsync(string identifier, string bundleId, IProgress<string>? progress = null)
    {
        if (!IsSupported) return false;
        try
        {
            progress?.Report($"Uninstalling {bundleId}...");
            var psi = new ProcessStartInfo
            {
                FileName = "xcrun",
                Arguments = $"devicectl device uninstall app --device {identifier} {bundleId}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                progress?.Report("Failed to start devicectl");
                return false;
            }

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                progress?.Report("App uninstalled successfully");
                return true;
            }

            progress?.Report($"Failed: {error}");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to uninstall app: {ex.Message}", ex);
            progress?.Report($"Failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TerminateAppAsync(string identifier, string bundleId)
    {
        if (!IsSupported) return false;
        try
        {
            // First find the PID by listing processes
            var tempFile = Path.GetTempFileName();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "xcrun",
                    Arguments = $"devicectl device info processes --device {identifier} --json-output \"{tempFile}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return false;

                var stdoutTask1 = process.StandardOutput.ReadToEndAsync();
                var stderrTask1 = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdoutTask1, stderrTask1);
                await process.WaitForExitAsync();
                if (!File.Exists(tempFile)) return false;

                var json = await File.ReadAllTextAsync(tempFile);
                var pid = FindPidForBundle(json, bundleId);
                if (pid == null)
                {
                    _logger.LogWarning($"No running process found for {bundleId}");
                    return false;
                }

                // Now terminate the process
                var termPsi = new ProcessStartInfo
                {
                    FileName = "xcrun",
                    Arguments = $"devicectl device process terminate --device {identifier} --pid {pid}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var termProcess = Process.Start(termPsi);
                if (termProcess == null) return false;

                var stdoutTask2 = termProcess.StandardOutput.ReadToEndAsync();
                var stderrTask2 = termProcess.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdoutTask2, stderrTask2);
                await termProcess.WaitForExitAsync();
                return termProcess.ExitCode == 0;
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to terminate {bundleId}: {ex.Message}", ex);
            return false;
        }
    }

    private IReadOnlyList<PhysicalDevice> ParseDevices(string json)
    {
        var devices = new List<PhysicalDevice>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("devices", out var devicesArray))
                return devices;

            foreach (var device in devicesArray.EnumerateArray())
            {
                var dp = device.TryGetProperty("deviceProperties", out var dProps) ? dProps : default;
                var hp = device.TryGetProperty("hardwareProperties", out var hProps) ? hProps : default;
                var cp = device.TryGetProperty("connectionProperties", out var cProps) ? cProps : default;

                var platform = GetStr(hp, "platform") ?? "unknown";
                // Only include iOS devices, skip watchOS etc.
                if (platform != "iOS") continue;

                var identifier = GetStr(device, "identifier") ?? "";
                var udid = GetStr(hp, "udid") ?? identifier;
                var name = GetStr(dp, "name") ?? "Unknown";
                var model = GetStr(hp, "marketingName") ?? GetStr(hp, "productType") ?? "Unknown";
                var osVersion = GetStr(dp, "osVersionNumber") ?? "?";
                var transport = GetStr(cp, "transportType") ?? "unknown";
                var pairingState = GetStr(cp, "pairingState") ?? "unknown";
                var tunnelState = GetStr(cp, "tunnelState") ?? "unknown";

                devices.Add(new PhysicalDevice(
                    identifier, udid, name, model, platform,
                    GetStr(hp, "deviceType") ?? "iPhone",
                    osVersion, transport, pairingState, tunnelState
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to parse devicectl output: {ex.Message}", ex);
        }

        return devices;
    }

    /// <summary>
    /// Extracts a user-friendly error message from devicectl JSON error responses.
    /// Returns null if no error is present.
    /// </summary>
    private static string? ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("error", out var error))
                return null;

            // Check for common error conditions
            if (error.TryGetProperty("userInfo", out var userInfo) &&
                userInfo.TryGetProperty("NSLocalizedDescription", out var desc))
            {
                var message = desc.TryGetProperty("string", out var str) ? str.GetString() : desc.GetString();

                // Look for underlying cause (e.g. "device is locked")
                if (userInfo.TryGetProperty("NSUnderlyingError", out var underlying))
                {
                    var innerMsg = ExtractNestedErrorMessage(underlying);
                    if (innerMsg != null && innerMsg.Contains("locked", StringComparison.OrdinalIgnoreCase))
                        return "Device is locked. Unlock your device and try again.";
                    if (innerMsg != null)
                        return $"{message} ({innerMsg})";
                }

                return message ?? "Unknown device error";
            }

            if (error.TryGetProperty("code", out var code))
                return $"Device error (code {code.GetInt32()})";

            return "Unknown device error";
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractNestedErrorMessage(JsonElement element)
    {
        if (element.TryGetProperty("error", out var inner))
            element = inner;

        if (element.TryGetProperty("userInfo", out var ui) &&
            ui.TryGetProperty("NSLocalizedDescription", out var desc))
        {
            var msg = desc.TryGetProperty("string", out var str) ? str.GetString() : desc.GetString();
            if (msg != null) return msg;
        }

        // Check nested underlying errors recursively
        if (element.TryGetProperty("userInfo", out var ui2) &&
            ui2.TryGetProperty("NSUnderlyingError", out var nested))
            return ExtractNestedErrorMessage(nested);

        return null;
    }

    private static string? GetStr(JsonElement element, string property)
    {
        if (element.ValueKind == JsonValueKind.Undefined) return null;
        return element.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private IReadOnlyList<PhysicalDeviceApp> ParseApps(string json)
    {
        var apps = new List<PhysicalDeviceApp>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("apps", out var appsArray))
                return apps;

            foreach (var app in appsArray.EnumerateArray())
            {
                var bundleId = GetStr(app, "bundleIdentifier") ?? "";
                if (string.IsNullOrEmpty(bundleId)) continue;

                var name = GetStr(app, "name");
                var version = GetStr(app, "bundleVersion");
                var appType = GetStr(app, "appType") ?? "User";
                var isRemovable = app.TryGetProperty("isRemovable", out var rem) && rem.GetBoolean();

                // Normalize type
                var isSystem = appType.Contains("System", StringComparison.OrdinalIgnoreCase) ||
                               appType.Contains("Hidden", StringComparison.OrdinalIgnoreCase);

                apps.Add(new PhysicalDeviceApp(
                    bundleId,
                    name,
                    version,
                    isSystem ? "System" : "User",
                    isRemovable
                ));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to parse apps: {ex.Message}", ex);
        }

        return apps.OrderBy(a => a.AppType == "System").ThenBy(a => a.BundleId).ToList();
    }

    private static int? FindPidForBundle(string json, string bundleId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("runningProcesses", out var procs))
                return null;

            foreach (var proc in procs.EnumerateArray())
            {
                var execPath = proc.TryGetProperty("executable", out var exe)
                    ? exe.GetString() : null;
                var bid = proc.TryGetProperty("bundleIdentifier", out var b)
                    ? b.GetString() : null;

                if (string.Equals(bid, bundleId, StringComparison.OrdinalIgnoreCase))
                {
                    if (proc.TryGetProperty("processIdentifier", out var pidProp))
                        return pidProp.GetInt32();
                }
            }
        }
        catch { }
        return null;
    }

    public async Task<string?> TakeScreenshotAsync(string udid, string outputPath)
    {
        if (!IsSupported) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "idevicescreenshot",
                Arguments = $"-u {udid} \"{outputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && File.Exists(outputPath))
                return outputPath;

            _logger.LogError($"Screenshot failed: {stderrTask.Result}");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Screenshot failed: {ex.Message}", ex);
            return null;
        }
    }

    public async Task<bool> SetLocationAsync(string udid, double latitude, double longitude)
    {
        if (!IsSupported) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "idevicesetlocation",
                Arguments = $"-u {udid} -- {latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)} {longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Set location failed: {ex.Message}", ex);
            return false;
        }
    }

    public async Task<bool> ResetLocationAsync(string udid)
    {
        if (!IsSupported) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "idevicesetlocation",
                Arguments = $"-u {udid} reset",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            await process.WaitForExitAsync();

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError($"Reset location failed: {ex.Message}", ex);
            return false;
        }
    }
}
