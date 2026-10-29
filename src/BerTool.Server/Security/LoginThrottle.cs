using System.Net;

namespace BerTool.Server.Security;

/// <summary>
/// Yönetici girişlerinde kaba kuvvet ve kullanıcı adı keşif denemelerini
/// sınırlar. Hem IP + kullanıcı adı hem de yalnızca IP bazında sayaç tutar.
/// Sunucu yeniden başlatıldığında sayaçlar sıfırlanır; kalıcı bir kilit
/// oluşturmamak için bu davranış bilinçli olarak seçilmiştir.
/// </summary>
internal sealed class LoginThrottle
{
    private const int FailureLimitPerAccount = 5;
    private const int FailureLimitPerIp = 20;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(5);
    private const int MaxTrackedEntries = 5_000;

    private readonly object _sync = new();
    private readonly Dictionary<string, AttemptEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastCleanup = DateTimeOffset.UtcNow;

    public bool IsBlocked(string? ipAddress, string? username, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        var now = DateTimeOffset.UtcNow;
        var ipKey = CreateIpKey(ipAddress);
        var accountKey = CreateAccountKey(ipAddress, username);

        lock (_sync)
        {
            CleanupIfNeeded(now);

            var ipBlockedUntil = GetBlockedUntil(ipKey, now);
            var accountBlockedUntil = GetBlockedUntil(accountKey, now);
            var blockedUntil = ipBlockedUntil > accountBlockedUntil
                ? ipBlockedUntil
                : accountBlockedUntil;

            if (blockedUntil > now)
            {
                retryAfter = blockedUntil - now;
                return true;
            }

            return false;
        }
    }

    public void RecordFailure(string? ipAddress, string? username)
    {
        var now = DateTimeOffset.UtcNow;
        var ipKey = CreateIpKey(ipAddress);
        var accountKey = CreateAccountKey(ipAddress, username);

        lock (_sync)
        {
            CleanupIfNeeded(now);
            Increment(ipKey, FailureLimitPerIp, now);
            Increment(accountKey, FailureLimitPerAccount, now);
        }
    }

    public void Reset(string? ipAddress, string? username)
    {
        lock (_sync)
        {
            _entries.Remove(CreateAccountKey(ipAddress, username));
        }
    }

    private void Increment(string key, int limit, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(key, out var entry) || now - entry.WindowStartedUtc > Window)
        {
            entry = new AttemptEntry { WindowStartedUtc = now };
            _entries[key] = entry;
        }

        entry.Failures++;
        if (entry.Failures >= limit)
        {
            entry.BlockedUntilUtc = now + BlockDuration;
            entry.Failures = 0;
            entry.WindowStartedUtc = now;
        }
    }

    private DateTimeOffset GetBlockedUntil(string key, DateTimeOffset now)
    {
        if (!_entries.TryGetValue(key, out var entry))
        {
            return DateTimeOffset.MinValue;
        }

        if (entry.BlockedUntilUtc <= now)
        {
            entry.BlockedUntilUtc = DateTimeOffset.MinValue;
            return DateTimeOffset.MinValue;
        }

        return entry.BlockedUntilUtc;
    }

    private void CleanupIfNeeded(DateTimeOffset now)
    {
        if (now - _lastCleanup < TimeSpan.FromMinutes(1) && _entries.Count < MaxTrackedEntries)
        {
            return;
        }

        _lastCleanup = now;
        var staleKeys = _entries
            .Where(pair =>
                pair.Value.BlockedUntilUtc <= now &&
                now - pair.Value.WindowStartedUtc > Window)
            .Select(pair => pair.Key)
            .ToList();

        foreach (var key in staleKeys)
        {
            _entries.Remove(key);
        }

        if (_entries.Count > MaxTrackedEntries)
        {
            // Olağan dışı yoğunlukta belleğin sınırsız büyümesini engelle.
            _entries.Clear();
        }
    }

    private static string CreateIpKey(string? ipAddress)
    {
        var normalized = IPAddress.TryParse(ipAddress, out var address)
            ? address.ToString()
            : (ipAddress ?? "unknown").Trim();
        return $"ip:{normalized}";
    }

    private static string CreateAccountKey(string? ipAddress, string? username)
    {
        var normalizedUser = (username ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedUser.Length > 256)
        {
            normalizedUser = normalizedUser[..256];
        }

        return $"{CreateIpKey(ipAddress)}|user:{normalizedUser}";
    }

    private sealed class AttemptEntry
    {
        public int Failures { get; set; }
        public DateTimeOffset WindowStartedUtc { get; set; }
        public DateTimeOffset BlockedUntilUtc { get; set; } = DateTimeOffset.MinValue;
    }
}
