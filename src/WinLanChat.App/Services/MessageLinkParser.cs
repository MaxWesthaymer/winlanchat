using System.Text.RegularExpressions;

namespace WinLanChat.App.Services;

public static class MessageLinkParser
{
    private static readonly Regex Links = new(
        @"\b(?:https?://|www\.)[^\s<>""'«»\p{Cc}]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static IEnumerable<(int Start, int Length, Uri Address)> FindLinks(string text)
    {
        foreach (Match match in Links.Matches(text))
        {
            string candidate = TrimPunctuation(match.Value);
            bool hasScheme = !candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
            string address = hasScheme ? candidate : "https://" + candidate;
            if (Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
                && BrowserLauncher.IsWebAddress(uri)
                && (hasScheme || uri.Host.Length > 4))
                yield return (match.Index, candidate.Length, uri);
        }
    }

    private static string TrimPunctuation(string text)
    {
        // Preserve balanced parentheses in URLs such as Wikipedia article addresses.
        int parentheses = 0, brackets = 0, braces = 0;
        foreach (char character in text)
        {
            switch (character)
            {
                case '(': parentheses++; break;
                case ')': parentheses--; break;
                case '[': brackets++; break;
                case ']': brackets--; break;
                case '{': braces++; break;
                case '}': braces--; break;
            }
        }

        int length = text.Length;
        while (length > 0)
        {
            char last = text[length - 1];
            if (last is '.' or ',' or ';' or ':' or '!' or '?' or '…')
                length--;
            else if (last == ')' && parentheses < 0)
            {
                length--;
                parentheses++;
            }
            else if (last == ']' && brackets < 0)
            {
                length--;
                brackets++;
            }
            else if (last == '}' && braces < 0)
            {
                length--;
                braces++;
            }
            else
                break;
        }
        return text[..length];
    }
}
