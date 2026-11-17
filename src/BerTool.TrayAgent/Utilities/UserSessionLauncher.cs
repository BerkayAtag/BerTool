using BerTool.TrayAgent.Logging;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace BerTool.TrayAgent.Utilities;

/// <summary>
/// Kurulum SYSTEM olarak çalıştığında (SCCM, Intune, BigFix) tray ajanını açık
/// kullanıcı oturumlarının her birinde, o oturumun kullanıcısı olarak başlatır.
/// Kurulum güncelleme öncesinde tüm oturumlardaki ajanı kapatır; eskiden SYSTEM
/// kurulumunda hiçbiri yeniden başlatılmıyor, makineler kullanıcılar oturumu
/// kapatıp açana kadar konsolda çevrimdışı görünüyordu.
/// </summary>
internal static class UserSessionLauncher
{
    private const uint CreateUnicodeEnvironment = 0x00000400;

    private enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        public int SessionId;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string WinStationName;

        public WtsConnectState State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WTSEnumerateSessionsW")]
    private static extern bool WTSEnumerateSessions(
        IntPtr server,
        int reserved,
        int version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateProcessAsUserW")]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <returns>0: en az bir oturumda başlatıldı veya açık oturum yok; 1: hata.</returns>
    public static int StartInActiveSessions(string executablePath)
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (!identity.IsSystem)
            {
                // Kullanıcı tokenı yalnızca SYSTEM (SeTcbPrivilege) ile alınabilir.
                Logger.Warn("Kullanıcı oturumlarında başlatma yalnızca SYSTEM olarak çalışır; atlandı.");
                return 0;
            }
        }

        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessions, out var count))
        {
            Logger.Error("Oturumlar listelenemedi.", new Win32Exception(Marshal.GetLastWin32Error()));
            return 1;
        }

        var started = 0;
        var failed = 0;
        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var index = 0; index < count; index++)
            {
                var session = Marshal.PtrToStructure<WtsSessionInfo>(sessions + (index * size));

                // Oturum 0 hizmetlere aittir; yalnızca kullanıcısı olan etkin ve
                // bağlantısı kesilmiş (geri bağlanılabilir) oturumlar hedeflenir.
                if (session.SessionId == 0 ||
                    session.State is not (WtsConnectState.Active or WtsConnectState.Disconnected))
                {
                    continue;
                }

                if (TryStartInSession(session.SessionId, executablePath))
                {
                    started++;
                }
                else
                {
                    failed++;
                }
            }
        }
        finally
        {
            WTSFreeMemory(sessions);
        }

        Logger.Info($"Tray ajanı kullanıcı oturumlarında başlatıldı: {started} başarılı, {failed} başarısız.");
        return failed > 0 && started == 0 ? 1 : 0;
    }

    private static bool TryStartInSession(int sessionId, string executablePath)
    {
        if (!WTSQueryUserToken(sessionId, out var token))
        {
            // Oturumda kullanıcı yoksa (ör. oturum açma ekranı) beklenen durumdur.
            Logger.Info($"Oturum {sessionId} için kullanıcı tokenı alınamadı (Win32 {Marshal.GetLastWin32Error()}); atlandı.");
            return false;
        }

        var environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false))
            {
                environment = IntPtr.Zero;
            }

            var startupInfo = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = @"winsta0\default"
            };

            var commandLine = new StringBuilder($"\"{executablePath}\"");
            if (!CreateProcessAsUser(
                    token,
                    executablePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: false,
                    environment == IntPtr.Zero ? 0 : CreateUnicodeEnvironment,
                    environment,
                    Path.GetDirectoryName(executablePath),
                    ref startupInfo,
                    out var processInformation))
            {
                Logger.Error(
                    $"Tray ajanı oturum {sessionId} içinde başlatılamadı.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
                return false;
            }

            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);
            Logger.Info($"Tray ajanı oturum {sessionId} içinde başlatıldı (PID {processInformation.dwProcessId}).");
            return true;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            CloseHandle(token);
        }
    }
}
