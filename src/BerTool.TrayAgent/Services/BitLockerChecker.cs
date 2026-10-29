using BerTool.TrayAgent.Logging;
using BerTool.TrayAgent.Models;
using System.Diagnostics;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BerTool.TrayAgent.Services;

internal sealed class BitLockerChecker
{
    private const int ManageBdeTimeoutMilliseconds = 5000;
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BerTool");
    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "bitlocker-cache.json");
    private static readonly Regex MethodRegex = new(
        @"^(Encryption Method|Şifreleme Yöntemi)\s*:\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex XtsAes256Regex = new(@"XTS[\s\-]?AES[\s\-]?256", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex XtsAes128Regex = new(@"XTS[\s\-]?AES[\s\-]?128", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Aes256Regex = new(@"AES[\s\-]?256", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Aes128Regex = new(@"AES[\s\-]?128", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static bool _accessDeniedLogged;

    public IReadOnlyList<BitLockerVolumeInfo> GetVolumeStatuses()
    {
        var directVolumes = QueryVolumeStatuses();
        if (directVolumes.Count == 0 || directVolumes.Any(ShouldUseCacheFor))
        {
            var cachedVolumes = TryReadCache();
            if (cachedVolumes.Count > 0)
            {
                return MergeWithCache(directVolumes, cachedVolumes);
            }
        }

        return directVolumes;
    }

    public bool RefreshCache()
    {
        if (!CanUseManageBde())
        {
            Logger.Warn("BitLocker cache güncellemesi için yönetici yetkisi gerekli.");
            return false;
        }

        var volumes = QueryVolumeStatuses()
            .Where(volume => !IsUnknown(volume))
            .ToList();

        if (volumes.Count == 0)
        {
            Logger.Warn("BitLocker cache güncellenemedi: yazılabilir sağlıklı veri bulunamadı.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(CacheDirectory);
            HardenCacheDirectory();

            var cache = new BitLockerCacheFile
            {
                UpdatedAt = DateTime.Now,
                Volumes = volumes
            };
            var json = JsonSerializer.Serialize(cache);
            var tempFilePath = Path.Combine(
                CacheDirectory,
                $"bitlocker-cache-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");

            try
            {
                File.WriteAllText(tempFilePath, json);
                // Aynı anda çalışan görevlerin yarım dosya bırakmasını engellemek için atomik taşıma.
                File.Move(tempFilePath, CacheFilePath, overwrite: true);
            }
            finally
            {
                DeleteFileIfExists(tempFilePath);
            }

            Logger.Info($"BitLocker cache güncellendi: {CacheFilePath}");
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error("BitLocker cache yazılamadı.", exception);
            return false;
        }
    }

    private static IReadOnlyList<BitLockerVolumeInfo> QueryVolumeStatuses()
    {
        var driveLetters = DriveInfo.GetDrives()
            .Where(drive => drive.DriveType == DriveType.Fixed)
            .Select(drive => NormalizeDriveLetter(drive.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(letter => letter, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (driveLetters.Count == 0)
        {
            return new List<BitLockerVolumeInfo>();
        }

        var methods = driveLetters.ToDictionary(letter => letter, _ => "N/A", StringComparer.OrdinalIgnoreCase);

        try
        {
            // Birincil kaynak: WMI'den doğrudan EncryptionMethod kodlarını okuma.
            foreach (var item in QueryWithWmi())
            {
                if (methods.ContainsKey(item.Key))
                {
                    methods[item.Key] = item.Value;
                }
            }
        }
        catch (Exception exception) when (IsAccessDenied(exception))
        {
            // Standart kullanıcı BitLocker WMI ad alanına erişemez; bu beklenen
            // durumdur ve SYSTEM görevinin yazdığı cache kullanılır. Her 45
            // saniyede yığın izi yazmamak için yalnızca bir kez bilgi verilir.
            if (!_accessDeniedLogged)
            {
                _accessDeniedLogged = true;
                Logger.Info("BitLocker WMI erişimi yönetici yetkisi gerektiriyor; BitLocker cache kullanılacak.");
            }
        }
        catch (Exception exception)
        {
            Logger.Error("BitLocker WMI sorgusu başarısız oldu.", exception);
        }

        foreach (var driveLetter in driveLetters.Where(letter => NeedsManageBdeFallback(methods[letter])))
        {
            if (!CanUseManageBde())
            {
                // Standart kullanıcı manage-bde çalıştıramaz; SYSTEM cache'i kullanılacak.
                break;
            }

            try
            {
                // WMI erişilemediğinde veya değer dönmediğinde manage-bde ile fallback.
                var fallbackMethod = QueryWithManageBde(driveLetter);
                methods[driveLetter] = fallbackMethod;
            }
            catch (Exception exception)
            {
                Logger.Error($"manage-bde sorgusu başarısız oldu: {driveLetter}", exception);
            }
        }

        return driveLetters
            .Select(driveLetter =>
            {
                var method = NormalizeMethod(methods[driveLetter]);
                return new BitLockerVolumeInfo
                {
                    DriveLetter = driveLetter,
                    EncryptionMethod = method,
                    IsHealthy = string.Equals(method, "XTS-AES-256", StringComparison.OrdinalIgnoreCase)
                };
            })
            .ToList();
    }

    private static IReadOnlyList<BitLockerVolumeInfo> TryReadCache()
    {
        try
        {
            if (!File.Exists(CacheFilePath))
            {
                return Array.Empty<BitLockerVolumeInfo>();
            }

            var cache = JsonSerializer.Deserialize<BitLockerCacheFile>(File.ReadAllText(CacheFilePath));
            if (cache?.Volumes is null)
            {
                return Array.Empty<BitLockerVolumeInfo>();
            }

            return cache.Volumes
                .Where(volume => !IsUnknown(volume))
                .ToList();
        }
        catch (Exception exception)
        {
            Logger.Warn($"BitLocker cache okunamadı: {exception.Message}");
            return Array.Empty<BitLockerVolumeInfo>();
        }
    }

    private static IReadOnlyList<BitLockerVolumeInfo> MergeWithCache(
        IReadOnlyList<BitLockerVolumeInfo> directVolumes,
        IReadOnlyList<BitLockerVolumeInfo> cachedVolumes)
    {
        if (directVolumes.Count == 0)
        {
            return cachedVolumes;
        }

        var cacheByDrive = cachedVolumes.ToDictionary(volume => volume.DriveLetter, StringComparer.OrdinalIgnoreCase);
        return directVolumes
            .Select(volume => ShouldUseCacheFor(volume) && cacheByDrive.TryGetValue(volume.DriveLetter, out var cachedVolume)
                ? cachedVolume
                : volume)
            .ToList();
    }

    private static bool IsAccessDenied(Exception exception)
    {
        return exception is UnauthorizedAccessException ||
            exception is ManagementException { ErrorCode: ManagementStatus.AccessDenied };
    }

    private static bool IsUnknown(BitLockerVolumeInfo volume)
    {
        return string.Equals(volume.EncryptionMethod, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldUseCacheFor(BitLockerVolumeInfo volume)
    {
        return IsUnknown(volume) ||
            string.Equals(volume.EncryptionMethod, "N/A", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> QueryWithWmi()
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scope = new ManagementScope(@"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption");
        scope.Connect();

        // Yalnızca birkaç sütun seçilirse WMI nesnesi yolsuz (kısmi) döner ve
        // GetEncryptionMethod çağrısı "Operation is not valid due to the current
        // state of the object" hatasıyla başarısız olur. Tam nesne gerekir.
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter IS NOT NULL"));
        using var results = searcher.Get();

        foreach (ManagementObject volume in results)
        {
            using (volume)
            {
                var driveLetter = volume["DriveLetter"]?.ToString();
                if (string.IsNullOrWhiteSpace(driveLetter))
                {
                    continue;
                }

                var normalizedDriveLetter = NormalizeDriveLetter(driveLetter);

                if (TryGetMethodFromWmiCall(volume, out var methodFromCall))
                {
                    output[normalizedDriveLetter] = methodFromCall;
                    continue;
                }

                output[normalizedDriveLetter] = MapEncryptionMethodCode(volume["EncryptionMethod"]);
            }
        }

        return output;
    }

    private static bool TryGetMethodFromWmiCall(ManagementObject volume, out string method)
    {
        method = "Unknown";
        try
        {
            using var outParams = volume.InvokeMethod("GetEncryptionMethod", null, null) as ManagementBaseObject;
            if (outParams is null)
            {
                return false;
            }

            if (!uint.TryParse(outParams["ReturnValue"]?.ToString(), out var returnValue) || returnValue != 0)
            {
                return false;
            }

            method = MapEncryptionMethodCode(outParams["EncryptionMethod"]);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Warn($"GetEncryptionMethod çağrısı başarısız: {exception.Message}");
            return false;
        }
    }

    private static string QueryWithManageBde(string driveLetter)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "manage-bde.exe"),
            Arguments = $"-status {driveLetter}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(processStartInfo);
        if (process is null)
        {
            return "N/A";
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(ManageBdeTimeoutMilliseconds))
        {
            Logger.Warn($"manage-bde zaman aşımına uğradı ({driveLetter}).");
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
            catch (Exception exception)
            {
                Logger.Warn($"manage-bde süreci kapatılamadı ({driveLetter}): {exception.Message}");
            }

            try
            {
                Task.WaitAll(new Task[] { outputTask, errorTask }, 1000);
            }
            catch (AggregateException)
            {
                // Zaman aşımı sonrası akış hataları yalnızca loglanır.
            }

            return "Unknown";
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            Logger.Warn($"manage-bde çıkış kodu ({driveLetter}): {process.ExitCode}");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            Logger.Warn($"manage-bde çıktısı ({driveLetter}): {error.Trim()}");
        }

        var parsedMethod = ParseManageBdeOutput(output);
        if (string.Equals(parsedMethod, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn($"manage-bde şifreleme yöntemi ayrıştırılamadı ({driveLetter}).");
        }

        return parsedMethod;
    }

    private static string ParseManageBdeOutput(string output)
    {
        foreach (var line in output.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries))
        {
            var match = MethodRegex.Match(line.Trim());
            if (match.Success)
            {
                return NormalizeMethod(match.Groups[2].Value);
            }
        }

        if (XtsAes256Regex.IsMatch(output))
        {
            return "XTS-AES-256";
        }

        if (XtsAes128Regex.IsMatch(output))
        {
            return "XTS-AES-128";
        }

        if (Aes256Regex.IsMatch(output))
        {
            return "AES-CBC-256";
        }

        if (Aes128Regex.IsMatch(output))
        {
            return "AES-CBC-128";
        }

        var normalized = Regex.Replace(output.ToUpperInvariant(), @"\s+", string.Empty);
        if (normalized.Contains("HARDWARE") || normalized.Contains("DONANIM"))
        {
            return "Donanım Şifreleme";
        }

        if (normalized.Contains("NOTENCRYPTED") ||
            normalized.Contains("FULLYDECRYPTED") ||
            normalized.Contains("NONE") ||
            normalized.Contains("YOK"))
        {
            return "N/A";
        }

        return "Unknown";
    }

    private static string MapEncryptionMethodCode(object? value)
    {
        if (!uint.TryParse(value?.ToString(), out var code))
        {
            return "Unknown";
        }

        return code switch
        {
            0 => "N/A",
            1 => "AES-CBC-128 (Diffuser)",
            2 => "AES-CBC-256 (Diffuser)",
            3 => "AES-CBC-128",
            4 => "AES-CBC-256",
            5 => "Donanım Şifreleme",
            6 => "XTS-AES-128",
            7 => "XTS-AES-256",
            _ => "Unknown"
        };
    }

    private static string NormalizeMethod(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return "N/A";
        }

        var normalized = method.Trim().ToUpperInvariant().Replace(" ", string.Empty);

        if (normalized.Contains("XTSAES256"))
        {
            return "XTS-AES-256";
        }

        if (normalized.Contains("XTSAES128"))
        {
            return "XTS-AES-128";
        }

        if (normalized is "NONE" or "N/A" || normalized.Contains("YOK"))
        {
            return "N/A";
        }

        if (normalized.Contains("AESCBC256") || normalized.Contains("AES256"))
        {
            return "AES-CBC-256";
        }

        if (normalized.Contains("AESCBC128") || normalized.Contains("AES128"))
        {
            return "AES-CBC-128";
        }

        if (normalized.Contains("UNKNOWN"))
        {
            return "Unknown";
        }

        return method.Trim();
    }

    private static bool NeedsManageBdeFallback(string method)
    {
        return string.IsNullOrWhiteSpace(method) ||
               string.Equals(method, "N/A", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(method, "Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDriveLetter(string drive)
    {
        var cleaned = drive.Trim().TrimEnd('\\');
        if (cleaned.Length >= 2 && cleaned[1] == ':')
        {
            return cleaned[..2].ToUpperInvariant();
        }

        return cleaned.ToUpperInvariant();
    }

    private static bool CanUseManageBde()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (identity is null)
            {
                return false;
            }

            // Zamanlanmış görev SYSTEM olarak çalıştığı için SYSTEM kimliği de yetkili sayılır.
            if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true)
            {
                return true;
            }

            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception)
        {
            Logger.Warn($"Yönetici yetkisi kontrol edilemedi: {exception.Message}");
            return false;
        }
    }

    private static void HardenCacheDirectory()
    {
        try
        {
            // Cache, standart kullanıcılar tarafından okunabilir; ancak yalnızca
            // SYSTEM ve Administrators tarafından değiştirilebilir olmalıdır.
            var directoryInfo = new DirectoryInfo(CacheDirectory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute,
                inheritance,
                PropagationFlags.None,
                AccessControlType.Allow));

            directoryInfo.SetAccessControl(security);
        }
        catch (Exception exception)
        {
            Logger.Warn($"BitLocker cache klasörü izinleri ayarlanamadı: {exception.Message}");
        }
    }

    private static void DeleteFileIfExists(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
        catch (Exception exception)
        {
            Logger.Warn($"Geçici dosya silinemedi: {filePath} - {exception.Message}");
        }
    }

    private sealed class BitLockerCacheFile
    {
        public DateTime UpdatedAt { get; init; }
        public IReadOnlyList<BitLockerVolumeInfo> Volumes { get; init; } = Array.Empty<BitLockerVolumeInfo>();
    }
}
