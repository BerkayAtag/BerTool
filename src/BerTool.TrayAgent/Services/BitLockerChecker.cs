using BerTool.Shared.Products;
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
    private static readonly Regex ProtectionRegex = new(
        @"^(Protection Status|Koruma Durumu)\s*:\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ConversionRegex = new(
        @"^(Conversion Status|Dönüştürme Durumu|Dönüşüm Durumu)\s*:\s*(.+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // WMI hizmeti askıda kalırsa sorgu varsayılan olarak sonsuza kadar bekler;
    // yenileme döngüsü kilitlenir ve eski durum "güncel" diye raporlanırdı.
    private static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(20);

    // SYSTEM görevi cache'i saatlik ve oturum açılışında yeniler. Bundan eski
    // bir cache gerçeği yansıtmayabilir; değeri "okunamadı" sayılır.
    private static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(7);

    private static bool _accessDeniedLogged;
    private static bool _staleCacheLogged;

    public IReadOnlyList<BitLockerVolumeInfo> GetVolumeStatuses()
    {
        var directVolumes = QueryVolumeStatuses();

        // Cache yalnızca doğrudan okunamayan (Unknown) birimler için kullanılır.
        // Eskiden WMI'nin gerçek "şifresiz" (N/A) sonucu da cache'teki eski
        // XTS-AES-256 değeriyle eziliyordu; şifresi çözülen disk sağlıklı görünüyordu.
        if (directVolumes.Count == 0 || directVolumes.Any(IsUnknown))
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
                UpdatedAtUtc = DateTimeOffset.UtcNow,
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

        // Başlangıç değeri "Unknown"dur: okunamayan birim "şifresiz" (N/A) diye
        // raporlanmaz. Eskiden standart kullanıcıda cache yoksa her disk N/A
        // görünüyor, "BitLocker bilgisi okunamadı" yerine gerçekten şifresiz bir
        // diskle aynı "XTS-AES-256 dışındaki şifreleme" riski üretiliyordu.
        var readings = driveLetters.ToDictionary(
            letter => letter,
            _ => new VolumeReading("Unknown", null, string.Empty),
            StringComparer.OrdinalIgnoreCase);

        try
        {
            // Birincil kaynak: WMI'den yöntem, koruma ve dönüştürme durumu.
            foreach (var item in QueryWithWmi())
            {
                if (readings.ContainsKey(item.Key))
                {
                    readings[item.Key] = item.Value;
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

        foreach (var driveLetter in driveLetters.Where(letter => NeedsManageBdeFallback(readings[letter].Method)))
        {
            if (!CanUseManageBde())
            {
                // Standart kullanıcı manage-bde çalıştıramaz; SYSTEM cache'i kullanılacak.
                break;
            }

            try
            {
                // WMI erişilemediğinde veya değer dönmediğinde manage-bde ile fallback.
                readings[driveLetter] = QueryWithManageBde(driveLetter);
            }
            catch (Exception exception)
            {
                Logger.Error($"manage-bde sorgusu başarısız oldu: {driveLetter}", exception);
            }
        }

        return driveLetters
            .Select(driveLetter => CreateVolume(driveLetter, readings[driveLetter]))
            .ToList();
    }

    private static BitLockerVolumeInfo CreateVolume(string driveLetter, VolumeReading reading)
    {
        var method = NormalizeMethod(reading.Method);
        return new BitLockerVolumeInfo
        {
            DriveLetter = driveLetter,
            EncryptionMethod = method,
            ProtectionOn = reading.ProtectionOn,
            ConversionStatus = reading.ConversionStatus,
            IsHealthy = BitLockerRules.IsHealthy(method, reading.ProtectionOn, reading.ConversionStatus)
        };
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

            var updatedAtUtc = cache.UpdatedAtUtc ??
                (cache.UpdatedAt == default ? (DateTimeOffset?)null : new DateTimeOffset(cache.UpdatedAt));
            var age = updatedAtUtc is { } value ? DateTimeOffset.UtcNow - value : TimeSpan.MaxValue;
            if (age > MaxCacheAge || age < TimeSpan.FromDays(-1))
            {
                if (!_staleCacheLogged)
                {
                    _staleCacheLogged = true;
                    Logger.Warn(
                        $"BitLocker cache çok eski veya geçersiz tarihli ({updatedAtUtc?.ToString("u") ?? "tarih yok"}); " +
                        "kullanılmıyor. 'BerTool BitLocker Cache' görevini kontrol edin.");
                }

                return Array.Empty<BitLockerVolumeInfo>();
            }

            // Sağlık bayrağı dosyadan alınmaz, kayıtlı değerlerden yeniden
            // hesaplanır. Aynı sürücü birden çok kez yazılmışsa ilki kullanılır;
            // eskiden yinelenen sürücü harfi her döngüde istisnaya yol açıyordu.
            return cache.Volumes
                .Where(volume => volume is not null &&
                    !string.IsNullOrWhiteSpace(volume.DriveLetter) &&
                    !IsUnknown(volume))
                .GroupBy(volume => NormalizeDriveLetter(volume.DriveLetter), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var volume = group.First();
                    return CreateVolume(
                        group.Key,
                        new VolumeReading(volume.EncryptionMethod, volume.ProtectionOn, volume.ConversionStatus ?? string.Empty));
                })
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

        var cacheByDrive = cachedVolumes
            .GroupBy(volume => volume.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return directVolumes
            .Select(volume => IsUnknown(volume) && cacheByDrive.TryGetValue(volume.DriveLetter, out var cachedVolume)
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
        return BitLockerRules.IsUnknownMethod(volume.EncryptionMethod);
    }

    private static IReadOnlyDictionary<string, VolumeReading> QueryWithWmi()
    {
        var output = new Dictionary<string, VolumeReading>(StringComparer.OrdinalIgnoreCase);
        var scope = new ManagementScope(
            @"\\.\root\CIMV2\Security\MicrosoftVolumeEncryption",
            new ConnectionOptions { Timeout = WmiTimeout });
        scope.Connect();

        // Yalnızca birkaç sütun seçilirse WMI nesnesi yolsuz (kısmi) döner ve
        // GetEncryptionMethod çağrısı "Operation is not valid due to the current
        // state of the object" hatasıyla başarısız olur. Tam nesne gerekir.
        using var searcher = new ManagementObjectSearcher(
            scope,
            new ObjectQuery("SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter IS NOT NULL"),
            new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = true, Rewindable = false });
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

                var method = TryInvokeUInt(volume, "GetEncryptionMethod", "EncryptionMethod", out var methodCode)
                    ? MapEncryptionMethodCode(methodCode)
                    : MapEncryptionMethodCode(volume["EncryptionMethod"]);

                // ProtectionStatus: 0 kapalı/askıda, 1 açık, 2 bilinmiyor (kilitli birim).
                bool? protectionOn = null;
                if (TryInvokeUInt(volume, "GetProtectionStatus", "ProtectionStatus", out var protectionCode) ||
                    TryReadUInt(volume, "ProtectionStatus", out protectionCode))
                {
                    protectionOn = protectionCode switch
                    {
                        0 => false,
                        1 => true,
                        _ => null
                    };
                }

                var conversion = TryInvokeUInt(volume, "GetConversionStatus", "ConversionStatus", out var conversionCode) ||
                    TryReadUInt(volume, "ConversionStatus", out conversionCode)
                        ? BitLockerRules.MapConversionStatus(conversionCode)
                        : string.Empty;

                output[NormalizeDriveLetter(driveLetter)] = new VolumeReading(method, protectionOn, conversion);
            }
        }

        return output;
    }

    private static bool TryInvokeUInt(ManagementObject volume, string methodName, string outputName, out uint value)
    {
        value = 0;
        try
        {
            using var outParams = volume.InvokeMethod(
                methodName,
                null,
                new InvokeMethodOptions(null, WmiTimeout)) as ManagementBaseObject;
            if (outParams is null)
            {
                return false;
            }

            if (!uint.TryParse(outParams["ReturnValue"]?.ToString(), out var returnValue) || returnValue != 0)
            {
                return false;
            }

            return uint.TryParse(outParams[outputName]?.ToString(), out value);
        }
        catch (Exception exception)
        {
            Logger.Warn($"{methodName} çağrısı başarısız: {exception.Message}");
            return false;
        }
    }

    private static bool TryReadUInt(ManagementObject volume, string propertyName, out uint value)
    {
        value = 0;
        try
        {
            return uint.TryParse(volume[propertyName]?.ToString(), out value);
        }
        catch (ManagementException)
        {
            // Özellik eski Windows sürümlerinde bulunmayabilir.
            return false;
        }
    }

    private static VolumeReading QueryWithManageBde(string driveLetter)
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
            return new VolumeReading("Unknown", null, string.Empty);
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

            return new VolumeReading("Unknown", null, string.Empty);
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

        var (protectionOn, conversion) = ParseManageBdeStatus(output);
        return new VolumeReading(parsedMethod, protectionOn, conversion);
    }

    /// <summary>
    /// manage-bde çıktısından koruma ve dönüştürme durumunu okur (İngilizce ve
    /// Türkçe Windows). Tanınmayan dilde değerler boş kalır; bilinmeyen durum
    /// sağlık değerlendirmesinde cezalandırılmaz.
    /// </summary>
    internal static (bool? ProtectionOn, string ConversionStatus) ParseManageBdeStatus(string output)
    {
        bool? protectionOn = null;
        var conversion = string.Empty;

        foreach (var rawLine in (output ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            var protectionMatch = ProtectionRegex.Match(line);
            if (protectionMatch.Success)
            {
                var value = FoldForMatch(protectionMatch.Groups[2].Value);
                if (value.Contains("OFF") || value.Contains("KAPALI") || value.Contains("ASKI"))
                {
                    protectionOn = false;
                }
                else if (value.Contains(" ON") || value == "ON" || value.Contains("ACIK"))
                {
                    protectionOn = true;
                }

                continue;
            }

            var conversionMatch = ConversionRegex.Match(line);
            if (conversionMatch.Success)
            {
                conversion = MapManageBdeConversion(conversionMatch.Groups[2].Value);
            }
        }

        return (protectionOn, conversion);
    }

    private static string MapManageBdeConversion(string value)
    {
        var text = FoldForMatch(value);
        var decrypt = text.Contains("DECRYPT") || text.Contains("COZ") || text.Contains("SIFRESIZ");

        if (text.Contains("PROGRESS") || text.Contains("DEVAM") || text.Contains("SURUYOR"))
        {
            return decrypt ? "DecryptionInProgress" : "EncryptionInProgress";
        }

        if (text.Contains("PAUSED") || text.Contains("DURAKLA"))
        {
            return decrypt ? "DecryptionPaused" : "EncryptionPaused";
        }

        if (decrypt)
        {
            return "FullyDecrypted";
        }

        if (text.Contains("ENCRYPTED") || text.Contains("SIFRELI") || text.Contains("SIFRELENMIS"))
        {
            return BitLockerRules.FullyEncrypted;
        }

        return string.Empty;
    }

    /// <summary>Türkçe karakterleri ASCII büyük harfe indirger (İ/ı → I, Ş → S ...).</summary>
    private static string FoldForMatch(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(char.ToUpperInvariant(character) switch
            {
                'İ' or 'I' or 'ı' => 'I',
                'Ş' => 'S',
                'Ç' => 'C',
                'Ö' => 'O',
                'Ü' => 'U',
                'Ğ' => 'G',
                var other => other
            });
        }

        return builder.ToString();
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

        /// <summary>Saat dilimi değişikliklerinden etkilenmeyen yazılma zamanı (1.6.0+).</summary>
        public DateTimeOffset? UpdatedAtUtc { get; init; }

        public IReadOnlyList<BitLockerVolumeInfo> Volumes { get; init; } = Array.Empty<BitLockerVolumeInfo>();
    }

    private sealed record VolumeReading(string Method, bool? ProtectionOn, string ConversionStatus);
}
