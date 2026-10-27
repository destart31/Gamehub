using System.Text;

namespace GameHub.Services;

/// <summary>
/// Parser mínimo do formato VDF (Valve KeyValues) — o suficiente para ler
/// libraryfolders.vdf e os appmanifest_*.acf. Não é uma implementação
/// completa da spec, só cobre o que os arquivos do Steam realmente usam.
/// </summary>
public static class VdfParser
{
    public static Dictionary<string, object> Parse(string text)
    {
        var pos = 0;
        return ParseObject(text, ref pos);
    }

    private static Dictionary<string, object> ParseObject(string text, ref int pos)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        while (pos < text.Length)
        {
            SkipWhitespaceAndComments(text, ref pos);
            if (pos >= text.Length) break;

            if (text[pos] == '}')
            {
                pos++;
                break;
            }

            var key = ReadQuotedString(text, ref pos);
            if (key == null) break;

            SkipWhitespaceAndComments(text, ref pos);

            if (pos < text.Length && text[pos] == '{')
            {
                pos++;
                var child = ParseObject(text, ref pos);
                result[key] = child;
            }
            else
            {
                var value = ReadQuotedString(text, ref pos) ?? "";
                result[key] = value;
            }
        }

        return result;
    }

    private static string? ReadQuotedString(string text, ref int pos)
    {
        SkipWhitespaceAndComments(text, ref pos);
        if (pos >= text.Length || text[pos] != '"') return null;

        pos++; // pula a aspa de abertura
        var sb = new StringBuilder();
        while (pos < text.Length && text[pos] != '"')
        {
            if (text[pos] == '\\' && pos + 1 < text.Length)
            {
                pos++;
                sb.Append(text[pos]);
            }
            else
            {
                sb.Append(text[pos]);
            }
            pos++;
        }
        pos++; // pula a aspa de fechamento
        return sb.ToString();
    }

    private static void SkipWhitespaceAndComments(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos]))
            {
                pos++;
            }
            else if (text[pos] == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
            }
            else
            {
                break;
            }
        }
    }
}
