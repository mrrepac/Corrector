using System.Globalization;

namespace Corrector;

internal sealed record ReviewSpan(string Text, bool Changed);

internal static class ReviewDiff
{
    // Bound the work on rewritten drafts and the number of WPF runs. Outside
    // these limits the unresolved middle is highlighted as a single change.
    const int MaxMiddleChars = 300_000;
    const int MaxTokens = 32_768;
    const int MaxEdits = 128;

    internal static (IReadOnlyList<ReviewSpan> Before, IReadOnlyList<ReviewSpan> After)
        Compare(string original, string corrected)
    {
        if (original == corrected)
        {
            ReviewSpan[] same = original.Length == 0 ? [] : [new(original, false)];
            return (same, same);
        }

        int prefix = 0;
        while (prefix < original.Length && prefix < corrected.Length)
        {
            int next = NextToken(original, prefix);
            if (next != NextToken(corrected, prefix) ||
                !original.AsSpan(prefix, next - prefix).SequenceEqual(corrected.AsSpan(prefix, next - prefix)))
                break;
            prefix = next;
        }

        var (beforeEnd, afterEnd) = FindSuffix(original, corrected, prefix);
        var before = new Spans(original);
        var after = new Spans(corrected);
        before.Through(prefix, false);
        after.Through(prefix, false);
        if (beforeEnd > prefix && afterEnd > prefix &&
            Math.Max(beforeEnd, afterEnd) - prefix <= MaxMiddleChars)
        {
            var left = TokenBoundaries(original, prefix, beforeEnd);
            var right = TokenBoundaries(corrected, prefix, afterEnd);
            if (left != null && right != null)
            {
                foreach (var (x, y) in Matches(original, left, corrected, right))
                {
                    before.Through(left[x], true);
                    after.Through(right[y], true);
                    before.Through(left[x + 1], false);
                    after.Through(right[y + 1], false);
                }
            }
        }
        before.Through(beforeEnd, true);
        after.Through(afterEnd, true);
        before.Through(original.Length, false);
        after.Through(corrected.Length, false);
        return (before.Finish(), after.Finish());
    }

    // Work in text elements so that an accent, surrogate pair, CRLF or joined
    // emoji cannot be split between two runs. Words and spaces form tokens.
    static int NextToken(string text, int start)
    {
        int end = start + StringInfo.GetNextTextElementLength(text.AsSpan(start));
        int group = Group(text, start);
        if (group != 0)
            while (end < text.Length && Group(text, end) == group)
                end += StringInfo.GetNextTextElementLength(text.AsSpan(end));
        return end;
    }

    static int Group(string text, int index)
    {
        if (char.IsLetterOrDigit(text, index) || text[index] == '_') return 1;
        if (char.IsWhiteSpace(text, index) && text[index] is not ('\r' or '\n' or '\f' or '\u0085' or '\u2028' or '\u2029')) return 2;
        return 0;
    }

    static (int, int) FindSuffix(string original, string corrected, int prefix)
    {
        int length = 0, limit = Math.Min(original.Length, corrected.Length) - prefix;
        while (length < limit && original[original.Length - length - 1] == corrected[corrected.Length - length - 1])
            length++;
        if (length == 0) return (original.Length, corrected.Length);

        // The first equal character might be inside a word or text element on
        // either side. Only accept a suffix starting at a boundary on both.
        int left = prefix, right = prefix;
        while (left < original.Length - length) left = NextToken(original, left);
        while (right < corrected.Length - length) right = NextToken(corrected, right);
        while (original.Length - left != corrected.Length - right)
        {
            if (original.Length - left > corrected.Length - right) left = NextToken(original, left);
            else right = NextToken(corrected, right);
        }
        return (left, right);
    }

    static List<int>? TokenBoundaries(string text, int start, int end)
    {
        var boundaries = new List<int> { start };
        while (start < end)
        {
            if (boundaries.Count > MaxTokens) return null;
            start = NextToken(text, start);
            boundaries.Add(start);
        }
        return boundaries;
    }

    // Myers' shortest edit path. Keep only a bounded frontier per edit depth.
    static List<(int X, int Y)> Matches(string original, List<int> left, string corrected, List<int> right)
    {
        int n = left.Count - 1, m = right.Count - 1;
        if (Math.Abs(n - m) > MaxEdits) return [];
        int limit = Math.Min(MaxEdits, n + m), offset = limit + 1;
        var frontier = new int[2 * limit + 3];
        var history = new List<int[]>();
        for (int depth = 0; depth <= limit; depth++)
        {
            for (int diagonal = -depth; diagonal <= depth; diagonal += 2)
            {
                int i = offset + diagonal;
                bool insert = diagonal == -depth || (diagonal != depth && frontier[i - 1] < frontier[i + 1]);
                int x = insert ? frontier[i + 1] : frontier[i - 1] + 1;
                int y = x - diagonal;
                while (x < n && y < m &&
                    original.AsSpan(left[x], left[x + 1] - left[x]).SequenceEqual(
                        corrected.AsSpan(right[y], right[y + 1] - right[y])))
                {
                    x++;
                    y++;
                }
                frontier[i] = x;
                if (x == n && y == m) return Backtrack(history, offset, x, y);
            }
            history.Add((int[])frontier.Clone());
        }
        return [];
    }

    static List<(int, int)> Backtrack(List<int[]> history, int offset, int x, int y)
    {
        var matches = new List<(int, int)>();
        for (int depth = history.Count; depth > 0; depth--)
        {
            int diagonal = x - y;
            var previous = history[depth - 1];
            int i = offset + diagonal;
            bool insert = diagonal == -depth || (diagonal != depth && previous[i - 1] < previous[i + 1]);
            int previousDiagonal = diagonal + (insert ? 1 : -1);
            int previousX = previous[offset + previousDiagonal];
            int previousY = previousX - previousDiagonal;
            while (x > previousX && y > previousY) matches.Add((--x, --y));
            x = previousX;
            y = previousY;
        }
        while (x > 0 && y > 0) matches.Add((--x, --y));
        matches.Reverse();
        return matches;
    }

    sealed class Spans(string source)
    {
        readonly List<ReviewSpan> spans = [];
        int start, end;
        bool changed;

        public void Through(int next, bool highlight)
        {
            if (next == end) return;
            if (end > start && highlight != changed)
            {
                spans.Add(new(source[start..end], changed));
                start = end;
            }
            end = next;
            changed = highlight;
        }

        public IReadOnlyList<ReviewSpan> Finish()
        {
            if (end > start) spans.Add(new(source[start..end], changed));
            return spans;
        }
    }
}
