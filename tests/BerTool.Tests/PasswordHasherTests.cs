using BerTool.Server.Security;
using Xunit;

namespace BerTool.Tests;

public sealed class PasswordHasherTests
{
    [Fact]
    public void HashVeVerify_DogruSifreyiKabulEder()
    {
        var record = PasswordHasher.Hash("Guclu-Sifre-123");

        Assert.True(PasswordHasher.Verify("Guclu-Sifre-123", record));
        Assert.False(PasswordHasher.Verify("yanlis-sifre", record));
    }

    [Fact]
    public void Verify_NullVeyaBosKayittaFalseDoner()
    {
        Assert.False(PasswordHasher.Verify("sifre", null));
        Assert.False(PasswordHasher.Verify("sifre", new PasswordHashRecord()));
        Assert.False(PasswordHasher.Verify(string.Empty, PasswordHasher.Hash("sifre")));
    }

    [Fact]
    public void Verify_BozukBase64KayittaFalseDoner()
    {
        var record = new PasswordHashRecord
        {
            Iterations = 210_000,
            Salt = "***",
            Hash = "***"
        };

        Assert.False(PasswordHasher.Verify("sifre", record));
    }

    [Fact]
    public void Verify_AsiriIterasyonDegeriniReddeder()
    {
        var record = PasswordHasher.Hash("Guclu-Sifre-123");
        record.Iterations = int.MaxValue;

        Assert.False(PasswordHasher.Verify("Guclu-Sifre-123", record));
    }

    [Fact]
    public void AjanAnahtari_OzetiDogruDogrular()
    {
        var hash = PasswordHasher.HashAgentApiKey("ajan-anahtari");

        Assert.True(PasswordHasher.VerifyAgentApiKey("ajan-anahtari", hash));
        Assert.False(PasswordHasher.VerifyAgentApiKey("baska-anahtar", hash));
        Assert.False(PasswordHasher.VerifyAgentApiKey(string.Empty, hash));
        Assert.False(PasswordHasher.VerifyAgentApiKey("ajan-anahtari", string.Empty));
    }
}
