using BerTool.TrayAgent.Logging;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;

namespace BerTool.TrayAgent.Reporting;

internal static class MachineIdentityProvider
{
    private static readonly Lazy<string> MachineId = new(CreateMachineId, isThreadSafe: true);

    public static string GetMachineId() => MachineId.Value;

    private static string CreateMachineId()
    {
        var machineGuid = TryReadMachineGuid();
        var source = string.IsNullOrWhiteSpace(machineGuid)
            ? $"{Environment.MachineName}|{Environment.UserDomainName}"
            : machineGuid;

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? TryReadMachineGuid()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var cryptographyKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return cryptographyKey?.GetValue("MachineGuid")?.ToString();
        }
        catch (Exception exception)
        {
            Logger.Warn($"MachineGuid okunamadı, makine kimliği makine adından üretilecek: {exception.Message}");
            return null;
        }
    }
}
