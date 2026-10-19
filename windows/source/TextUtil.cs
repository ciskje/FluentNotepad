using System;

namespace FluentNotepad;

/// <summary>
/// UI-free text math shared by the editor paths (kept here so it stays
/// unit-testable without spinning up WinUI controls).
/// </summary>
internal static class TextUtil
{
    // Caret's line as (start, length) on \n text. Empty lines collapse to one
    // char (the break, or the previous one at EOF) so a line flash stays visible.
    internal static bool TryGetLineRange(string text, int caret, out int start, out int length)
    {
        start = 0; length = 0;
        if (text.Length == 0) return false;
        int ls = Math.Clamp(caret, 0, text.Length);
        while (ls > 0 && text[ls - 1] != '\n') ls--;
        int le = Math.Clamp(caret, 0, text.Length);
        while (le < text.Length && text[le] != '\n') le++;
        int len = le - ls;
        if (len <= 0)
        {
            if (ls + 1 <= text.Length) len = 1;
            else if (ls > 0) { ls--; len = 1; }
            else return false;
        }
        start = ls; length = len;
        return true;
    }
}
