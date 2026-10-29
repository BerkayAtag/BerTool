using BerTool.Server.Security;
using Xunit;

namespace BerTool.Tests;

public sealed class LoginThrottleTests
{
    [Fact]
    public void BesHataliDenemedenSonra_HesapKilitlenir()
    {
        var throttle = new LoginThrottle();
        const string ip = "192.0.2.10";
        const string user = "admin";

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.False(throttle.IsBlocked(ip, user, out _));
            throttle.RecordFailure(ip, user);
        }

        Assert.True(throttle.IsBlocked(ip, user, out var retryAfter));
        Assert.True(retryAfter > TimeSpan.Zero);

        // Aynı IP'den farklı bir kullanıcı adı henüz IP sınırına takılmamıştır.
        Assert.False(throttle.IsBlocked(ip, "baska-kullanici", out _));
    }

    [Fact]
    public void BasariliGiris_SayaciSifirlar()
    {
        var throttle = new LoginThrottle();
        const string ip = "192.0.2.11";
        const string user = "admin";

        for (var attempt = 0; attempt < 4; attempt++)
        {
            throttle.RecordFailure(ip, user);
        }

        throttle.Reset(ip, user);

        for (var attempt = 0; attempt < 4; attempt++)
        {
            throttle.RecordFailure(ip, user);
        }

        Assert.False(throttle.IsBlocked(ip, user, out _));
    }

    [Fact]
    public void YirmiHataliDenemedenSonra_IpKilitlenir()
    {
        var throttle = new LoginThrottle();
        const string ip = "192.0.2.12";

        for (var attempt = 0; attempt < 20; attempt++)
        {
            throttle.RecordFailure(ip, $"kullanici-{attempt}");
        }

        Assert.True(throttle.IsBlocked(ip, "yeni-kullanici", out _));
        Assert.False(throttle.IsBlocked("192.0.2.13", "yeni-kullanici", out _));
    }
}
