using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ShiftSwitcher.Core;

public readonly record struct TextSpan(int Start, int Length)
{
    public int End => Start + Length;
}

public static class Layout
{
    private const string English = "`qwertyuiop[]asdfghjkl;'zxcvbnm,./~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?@#$^&";
    private const string Russian = "ёйцукенгшщзхъфывапролджэячсмитьбю.ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,\"№;:?";
    public static bool IsRussian(char c) => c is >= '\u0400' and <= '\u04ff';
    public static string? Language(string text)
    {
        foreach (var c in text.Reverse())
        {
            if (IsRussian(c)) return "ru";
            if (char.IsAsciiLetter(c)) return "en";
        }
        return null;
    }
    public static string Convert(string text, string? sourceLanguage = null)
    {
        var output = new StringBuilder();
        foreach (Match token in Regex.Matches(text, @"\s+|\S+"))
        {
            if (char.IsWhiteSpace(token.Value[0])) { output.Append(token.Value); continue; }
            var reverse = (Language(token.Value) ?? sourceLanguage) == "ru";
            var source = reverse ? Russian : English;
            var target = reverse ? English : Russian;
            foreach (var c in token.Value)
            {
                var index = source.IndexOf(c);
                output.Append(index < 0 ? c : target[index]);
            }
        }
        return output.ToString();
    }
    public static bool IsIdentifier(string token) => token.Any(c => char.IsDigit(c) || "@#:/\\=_-".Contains(c));
    public static TextSpan? LastWord(string value, int caret)
    {
        if (caret < 0 || caret > value.Length) return null;
        var end = caret;
        while (end > 0 && char.IsWhiteSpace(value[end - 1])) end--;
        var tokenStart = end;
        while (tokenStart > 0 && !char.IsWhiteSpace(value[tokenStart - 1])) tokenStart--;
        if (IsIdentifier(value[tokenStart..end])) return null;
        var start = end;
        while (start > 0 && (char.IsLetter(value[start - 1]) || "'`[];,.".Contains(value[start - 1]))) start--;
        return end > start ? new(start, end - start) : null;
    }
    public static TextSpan? PreviousCharacter(string value, int caret)
    {
        if (caret <= 0 || caret > value.Length) return null;
        var boundaries = StringInfo.ParseCombiningCharacters(value);
        var start = boundaries.LastOrDefault(i => i < caret);
        if (caret != value.Length && !boundaries.Contains(caret)) return null;
        return new(start, caret - start);
    }
    public static string Replace(string value, TextSpan range, string replacement) =>
        value[..range.Start] + replacement + value[range.End..];
    public static (TextSpan Range, string Text)? WithCaretTail(string value, TextSpan range, string replacement, int caret)
    {
        if (range.Start < 0 || range.Length < 0 || range.Start > value.Length - range.Length || caret < range.End || caret > value.Length) return null;
        // Include unchanged spaces/partial text up to the original caret in the
        // same insertion batch. Otherwise the next word lands before the space.
        return (new(range.Start, caret - range.Start), replacement + value[range.End..caret]);
    }
}

public sealed class DoubleShift
{
    private readonly HashSet<int> pressed = [];
    private double? downAt, lastTap;
    private bool chord;
    public void Reset() { pressed.Clear(); downAt = lastTap = null; chord = false; }
    public void OtherKey() { chord = true; lastTap = null; }
    public bool Shift(int code, bool down, double time, bool otherModifiers)
    {
        if (lastTap > time || downAt > time) Reset();
        if (down)
        {
            if (pressed.Contains(code)) return false;
            if (pressed.Count > 0) { chord = true; lastTap = null; }
            else { downAt = time; chord = otherModifiers; }
            pressed.Add(code); return false;
        }
        if (!pressed.Remove(code)) return false;
        if (pressed.Count != 0 || chord || otherModifiers || downAt is null || time - downAt >= .30)
        { lastTap = null; return false; }
        downAt = null;
        if (lastTap is double previous && time - previous <= .35) { lastTap = null; return true; }
        lastTap = time; return false;
    }
}

public static class AutomaticCorrection
{
    public sealed record Plan(TextSpan Range, string Text);
    private sealed record Word(TextSpan Range, string Before, string After, string Source, bool SourceValid, bool TargetValid)
    { public string Target => Source == "ru" ? "en" : "ru"; }

    public static Plan? GetPlan(string value, int caret, Func<string, string, bool> valid,
        Func<string, bool>? ignored = null, int minimumLocation = 0)
    {
        if (caret <= 0 || caret > value.Length) return null;
        var boundary = caret - 1;
        while (boundary >= 0 && !char.IsWhiteSpace(value[boundary])) boundary--;
        if (boundary < 0) return null;
        var completed = value[..(boundary + 1)];
        var lineStart = completed.LastIndexOf('\n') + 1;
        var start = Math.Max(lineStart, completed.Length - 512);
        var words = new List<Word>();
        foreach (var match in Regex.Matches(completed[start..], @"\S+").Cast<Match>().TakeLast(12))
        {
            if (match.Index == 0 && start > lineStart) continue;
            var token = match.Value;
            if (Layout.IsIdentifier(token) || (token.Length > 1 && token.Any(char.IsLetter) && token == token.ToUpperInvariant())) continue;
            while (token.Length > 0)
            {
                var after = Layout.Convert(token);
                var source = Layout.Language(token) ?? "en";
                var target = source == "ru" ? "en" : "ru";
                if (ignored?.Invoke(token) == true || ignored?.Invoke(after) == true) break;
                var sourceValid = valid(token, source);
                var targetValid = after.Length > 0 && after.All(char.IsLetter) && valid(after, target);
                if (token.All(char.IsLetter) || targetValid)
                { words.Add(new(new(start + match.Index, token.Length), token, after, source, sourceValid, targetValid)); break; }
                if (!".,;:!?…\"»”)}".Contains(token[^1])) break;
                token = token[..^1];
            }
        }
        var votes = new Dictionary<string, int> { ["en"] = 0, ["ru"] = 0 };
        foreach (var word in words.Where(w => w.Before.Length >= 2))
        {
            if (word.SourceValid && !word.TargetValid) votes[word.Source]++;
            if (!word.SourceValid && word.TargetValid) votes[word.Target]++;
        }
        var edits = words.Where(word =>
        {
            if (word.Range.Start < minimumLocation || word.Before == word.After || !word.TargetValid) return false;
            if (word.Before.Length >= 2) return !word.SourceValid;
            var singles = word.Target == "ru" ? "авикосуя" : "ia";
            if (!singles.Contains(word.After.ToLowerInvariant())) return false;
            if (word.After == "о" && words.LastOrDefault(w => w.Range.Start < word.Range.Start) is { } previous)
                return (previous.Source == "ru" && previous.SourceValid && !previous.TargetValid) ||
                    (previous.Target == "ru" && !previous.SourceValid && previous.TargetValid);
            return votes[word.Target] >= 2 && votes[word.Source] == 0;
        }).ToList();
        if (edits.Count == 0) return null;
        var range = new TextSpan(edits[0].Range.Start, edits[^1].Range.End - edits[0].Range.Start);
        var result = value.Substring(range.Start, range.Length);
        foreach (var edit in edits.AsEnumerable().Reverse())
            result = Layout.Replace(result, new(edit.Range.Start - range.Start, edit.Range.Length), edit.After);
        return new(range, result);
    }
}
