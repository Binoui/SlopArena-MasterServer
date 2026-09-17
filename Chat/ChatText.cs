// MasterServer/Chat/ChatText.cs
using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.SignalR;

namespace MasterServer.Chat;

/// <summary>
/// Literal Unicode validation for chat display names and message bodies, plus
/// the <see cref="ChatPlayer"/> construction helper (deterministic tag from
/// the authenticated player ID). No markup/parsing: message text is preserved
/// verbatim once it passes validation.
/// </summary>
internal static class ChatText
{
    /// <summary>Exclusive upper bound for newly-issued guest IDs: 36^8.</summary>
    internal const long GuestIdLimit = 2821109907456L;

    private const int MaxDisplayNameScalars = 24;
    private const int MaxMessageScalars = 500;
    private const string Base36Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Builds the wire-facing player record for an authenticated identity.</summary>
    internal static ChatPlayer Player(long playerId, string displayName)
        => new(playerId.ToString(CultureInfo.InvariantCulture), displayName, Tag(playerId));

    /// <summary>Trims names; rejects blank, malformed, or control-bearing text.</summary>
    internal static string DisplayName(string text)
    {
        if (text is null)
            throw new HubException("invalid_name");

        var trimmed = text.Trim();
        var span = trimmed.AsSpan();
        var scalarCount = 0;
        var hasContent = false;
        while (!span.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(span, out var rune, out var consumed) != OperationStatus.Done ||
                ++scalarCount > MaxDisplayNameScalars || IsDisallowedNameRune(rune))
                throw new HubException("invalid_name");

            hasContent |= !Rune.IsWhiteSpace(rune) &&
                Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format;
            span = span[consumed..];
        }
        if (!hasContent)
            throw new HubException("invalid_name");
        return trimmed;
    }

    /// <summary>
    /// Validates a message body and returns it unmodified: well-formed UTF-16,
    /// &lt;=500 Unicode scalar values, not empty/whitespace-only. Literal line
    /// breaks/tabs are legal content; nothing is stripped or parsed.
    /// </summary>
    internal static string Message(string text)
    {
        if (text is null)
            throw new HubException("invalid_message");

        var span = text.AsSpan();
        var scalarCount = 0;
        var hasVisibleContent = false;

        while (!span.IsEmpty)
        {
            // Rejects unpaired surrogates (malformed UTF-16) instead of silently
            // substituting U+FFFD the way string.EnumerateRunes() would.
            if (Rune.DecodeFromUtf16(span, out var rune, out var consumed) != OperationStatus.Done)
                throw new HubException("invalid_message");

            if (++scalarCount > MaxMessageScalars)
                throw new HubException("invalid_message");

            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.Control && rune.Value is not ('\r' or '\n' or '\t'))
                throw new HubException("invalid_message");
            if (!Rune.IsWhiteSpace(rune) && category != UnicodeCategory.Format)
                hasVisibleContent = true;

            span = span[consumed..];
        }

        if (!hasVisibleContent)
            throw new HubException("invalid_message");

        return text;
    }

    private static bool IsDisallowedNameRune(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Control => true,
        UnicodeCategory.Format => rune.Value is not (0x200C or 0x200D),
        UnicodeCategory.LineSeparator => true,
        UnicodeCategory.ParagraphSeparator => true,
        _ => false,
    };

    // Base36 of the raw ID, no hashing/truncation. Guest IDs are < 36^8 so they
    // encode to <=8 chars; larger existing IDs simply encode longer (contract
    // explicitly forbids truncating/hashing to force a fixed width).
    private static string Tag(long playerId)
    {
        Span<char> buffer = stackalloc char[13]; // ulong.MaxValue fits in 13 base36 digits
        var index = buffer.Length;
        var value = (ulong)playerId; // player IDs are non-negative SteamIds

        if (value == 0)
            return "0";

        while (value > 0)
        {
            buffer[--index] = Base36Alphabet[(int)(value % 36)];
            value /= 36;
        }

        return new string(buffer[index..]);
    }
}
