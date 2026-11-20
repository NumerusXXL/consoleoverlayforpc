using System;
using System.Collections.Generic;
using System.Text;

namespace ConsoleOverlay.Steam;

/// <summary>
/// Reads Valve's text KeyValues files (libraryfolders.vdf, appmanifest_*.acf): quoted keys, then either a quoted
/// value or a { block }. Keys are case-insensitive, like Steam treats them.
/// </summary>
internal sealed class Vdf
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Vdf> Blocks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? this[string key] => Values.TryGetValue(key, out var value) ? value : null;

    public Vdf? Block(string key) => Blocks.TryGetValue(key, out var block) ? block : null;

    public static Vdf Parse(string text)
    {
        int position = 0;
        var root = new Vdf();
        ReadInto(root, text, ref position, topLevel: true);
        return root;
    }

    private static void ReadInto(Vdf block, string text, ref int position, bool topLevel)
    {
        while (true)
        {
            var key = NextToken(text, ref position, out bool keyQuoted);
            if (key is null) return;                      // end of file
            if (!keyQuoted && key == "}") { if (!topLevel) return; continue; }

            var value = NextToken(text, ref position, out bool valueQuoted);
            if (value is null) return;
            if (!valueQuoted && value == "{")
            {
                var child = new Vdf();
                ReadInto(child, text, ref position, topLevel: false);
                block.Blocks[key] = child;
            }
            else
            {
                block.Values[key] = value;
            }
        }
    }

    private static string? NextToken(string text, ref int position, out bool quoted)
    {
        quoted = false;
        while (position < text.Length)
        {
            char c = text[position];
            if (char.IsWhiteSpace(c)) { position++; continue; }
            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
                continue;
            }
            break;
        }
        if (position >= text.Length) return null;

        char first = text[position];
        if (first is '{' or '}')
        {
            position++;
            return first.ToString();
        }

        var token = new StringBuilder();
        if (first == '"')
        {
            quoted = true;
            position++;
            while (position < text.Length && text[position] != '"')
            {
                char c = text[position++];
                if (c == '\\' && position < text.Length)
                {
                    char escaped = text[position++];
                    token.Append(escaped switch { 'n' => '\n', 't' => '\t', _ => escaped });
                }
                else
                {
                    token.Append(c);
                }
            }
            position++; // closing quote
            return token.ToString();
        }

        while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not ('{' or '}' or '"'))
            token.Append(text[position++]);
        return token.ToString();
    }
}
