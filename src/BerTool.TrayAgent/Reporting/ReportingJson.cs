using System.Text.Json;
using System.Text.Json.Serialization;

namespace BerTool.TrayAgent.Reporting;

internal static class ReportingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
