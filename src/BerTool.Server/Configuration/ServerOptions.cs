namespace BerTool.Server.Configuration;

public sealed class ServerOptions
{
    public const string SectionName = "BerTool";

    public string DataDirectory { get; set; } = "data";
    public int SessionHours { get; set; } = 12;
    public string? AdminUsername { get; set; }
    public string? AdminPassword { get; set; }
    public string? AgentApiKey { get; set; }
    public AdOptions Ad { get; set; } = new();
    public string[] AllowedOrigins { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Yerel olmayan istemciler için HTTPS zorunluluğu. Varsayılan olarak
    /// açıktır; yalnızca güvenilir bir ters proxy arkasında HTTP kullanılacaksa
    /// proxy'nin sunucuya loopback üzerinden bağlanması gerekir.
    /// </summary>
    public bool RequireHttpsForRemote { get; set; } = true;

    /// <summary>
    /// İzin verilen ek Host başlığı değerleri. Boş bırakılırsa makine adı,
    /// loopback ve yerel IP adresleri otomatik olarak kabul edilir. Ters proxy
    /// veya genel DNS adı kullanılıyorsa buraya eklenmelidir. "*" tümünü kabul
    /// eder ve üretimde önerilmez.
    /// </summary>
    public string[] AllowedHosts { get; set; } = Array.Empty<string>();

    public string[] TrustedProxies { get; set; } = Array.Empty<string>();
}
