using System.Text;

namespace BerTool.Server.Security;

/// <summary>
/// LDAP arama filtrelerinde kullanıcı girdisini güvenli hale getirir.
/// RFC 4515 özel karakter kaçışlarını uygular.
/// </summary>
internal static class LdapFilter
{
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\\':
                    builder.Append("\\5c");
                    break;
                case '*':
                    builder.Append("\\2a");
                    break;
                case '(':
                    builder.Append("\\28");
                    break;
                case ')':
                    builder.Append("\\29");
                    break;
                case '\0':
                    builder.Append("\\00");
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }
}
