using System.Text;

namespace LisoP2P.Core;

public static class NicknameRules
{
    public const int MaxLength = 24;

    private const char ZeroWidthSpace = '​';
    private const char ByteOrderMark = '﻿';

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var character in value)
        {
            if (char.IsControl(character) || character == ZeroWidthSpace || character == ByteOrderMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        var text = builder.ToString();

        if (text.Length <= MaxLength)
        {
            return text;
        }

        var cut = MaxLength;

        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut].TrimEnd();
    }

    public static bool IsValid(string? value) => Sanitize(value).Length > 0;

    public static string FallbackFor(PeerId id) => "user-" + id.Value.ToString("N")[..4];
}
