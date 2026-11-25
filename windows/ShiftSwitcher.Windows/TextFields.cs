using System.Globalization;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using ShiftSwitcher.Core;

namespace ShiftSwitcher.Windows;

internal sealed record FieldTarget(AutomationElement Element, int Process, nint Window)
{
    internal bool Same(FieldTarget other)
    {
        try { return Window == other.Window && Process == other.Process && Automation.Compare(Element, other.Element); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ElementNotAvailableException or InvalidOperationException) { return false; }
    }
}
internal sealed record FieldState(FieldTarget Target, TextPattern Pattern, string Value, TextSpan Selection, long Revision);

internal static class TextFields
{
    internal static FieldTarget? Capture()
    {
        try
        {
            var window = Native.GetForegroundWindow();
            if (window == 0) return null;
            Native.GetWindowThreadProcessId(window, out var process);
            if (process == Environment.ProcessId) return null;
            var element = AutomationElement.FocusedElement;
            if (element is null || element.Current.ProcessId != process || element.Current.IsPassword || !element.Current.IsEnabled) return null;
            if (element.Current.ControlType != ControlType.Edit && element.Current.ControlType != ControlType.Document) return null;
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern)) return null;
            if (((TextPattern)textPattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not false) return null;
            return new(element, (int)process, window);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ElementNotAvailableException or InvalidOperationException) { return null; }
    }
    internal static FieldState? Read(FieldTarget target)
    {
        try
        {
            var revision = Native.PhysicalRevision;
            var current = Capture();
            if (current is null || !target.Same(current)) return null;
            var pattern = (TextPattern)current.Element.GetCurrentPattern(TextPattern.Pattern);
            var document = pattern.DocumentRange;
            var value = document.GetText(32769);
            if (value.Length > 32768) return null;
            var selections = pattern.GetSelection();
            if (selections.Length != 1) return null;
            var before = document.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selections[0], TextPatternRangeEndpoint.Start);
            var start = before.GetText(32769).Length;
            var selected = selections[0].GetText(32769);
            if (start + selected.Length > value.Length || value.Substring(start, selected.Length) != selected) return null;
            // Reject a snapshot whose provider changed text while we read its caret.
            if (pattern.DocumentRange.GetText(32769) != value || Native.PhysicalRevision != revision) return null;
            return new(current, pattern, value, new(start, selected.Length), revision);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ElementNotAvailableException or InvalidOperationException or ArgumentException) { return null; }
    }
    private static int CharacterCount(string text) => StringInfo.ParseCombiningCharacters(text).Length;
    internal static bool Select(FieldState field, TextSpan span)
    {
        try
        {
            var current = Read(field.Target);
            if (current is null || current.Value != field.Value || current.Revision != field.Revision || span.Start < 0 || span.Length < 0 || span.End > field.Value.Length) return false;
            var range = current.Pattern.DocumentRange.Clone();
            range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, CharacterCount(field.Value[..span.Start]));
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, CharacterCount(field.Value.Substring(span.Start, span.Length)));
            var prefix = current.Pattern.DocumentRange.Clone();
            prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
            // Providers differ in what a character unit means. Never select an
            // unverified span, including text after emoji or combining characters.
            if (range.GetText(32769) != field.Value.Substring(span.Start, span.Length) || prefix.GetText(32769) != field.Value[..span.Start]) return false;
            range.Select();
            for (var i = 0; i < 20; i++)
            {
                var selected = Read(field.Target);
                if (selected is null) { Thread.Sleep(5); continue; }
                if (selected.Value != field.Value || selected.Revision != field.Revision) return false;
                if (selected.Selection == span) return true;
                Thread.Sleep(5);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ElementNotAvailableException or InvalidOperationException or ArgumentException) { }
        return false;
    }
    internal static bool Replace(FieldState field, TextSpan span, string text)
    {
        if (field.Selection.Length == 0)
        {
            var insertion = Layout.WithCaretTail(field.Value, span, text, field.Selection.Start);
            if (insertion is null) return false;
            (span, text) = insertion.Value;
        }
        if (text.Length is 0 or > 256 || !Select(field, span)) return false;
        var selected = Read(field.Target);
        if (selected is null || selected.Value != field.Value || selected.Selection != span || selected.Revision != field.Revision) return false;
        // One Unicode path, never a clipboard operation or a second write path.
        if (Native.PhysicalRevision != field.Revision || Native.ModifiersDown() || !Native.Unicode(text)) return false;
        var expected = Layout.Replace(field.Value, span, text);
        for (var i = 0; i < 60; i++)
        {
            var current = Read(field.Target);
            if (current is not null && current.Value == expected && current.Selection == new TextSpan(span.Start + text.Length, 0)) return true;
            if (Native.GetForegroundWindow() != field.Target.Window) return false;
            Thread.Sleep(5);
        }
        return false;
    }
}
