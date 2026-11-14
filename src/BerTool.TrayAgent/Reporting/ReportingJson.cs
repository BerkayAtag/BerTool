using System.Text.Json;
using System.Text.Json.Serialization;

namespace BerTool.TrayAgent.Reporting;

internal static class ReportingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>
    /// Elle düzenlenen ajan ayar dosyası için hoşgörülü okuma: yorum satırları
    /// ve sondaki virgüller kabul edilir. Eskiden bunlardan biri dosyanın
    /// tamamen yok sayılmasına ve raporlamanın sessizce kapanmasına yol açıyordu.
    /// </summary>
    public static readonly JsonSerializerOptions SettingsFileOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
