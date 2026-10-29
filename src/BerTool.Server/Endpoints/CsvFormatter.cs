using System.Text;

namespace BerTool.Server.Endpoints;

/// <summary>
/// CSV alanlarını kaçışlar ve formül enjeksiyonunu engeller.
/// Ajan tarafından gönderilen makine adı, kullanıcı, model gibi değerler
/// doğrudan hücreye yazıldığı için "=", "+", "-", "@" gibi önekler
/// elektronik tablo uygulamalarında formül olarak yorumlanabilir.
/// </summary>
internal static class CsvFormatter
{
    public static string Escape(string? value)
    {
        var text = value ?? string.Empty;

        if (text.Length > 0 && IsFormulaPrefix(text[0]))
        {
            // Baştaki tek tırnak, elektronik tablo uygulamasında hücreyi
            // metin olarak işaretler ve formülün çalıştırılmasını engeller.
            text = "'" + text;
        }

        return $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static bool IsFormulaPrefix(char character) =>
        character is '=' or '+' or '-' or '@' or '\t' or '\r';
}
