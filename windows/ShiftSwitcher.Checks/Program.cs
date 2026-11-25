using ShiftSwitcher.Core;
int count = 0;
void Check(bool result, string name) { count++; if (!result) throw new Exception(name); }
foreach (var (before, after) in new[] {
    ("ghbdtn", "привет"), ("руддщ", "hello"), ("Ghbdtn Vbh", "Привет Мир"),
    ("копbz", "копия"), ("helдщ", "hello"), ("mesh wifi копbz", "ьуыр цшаш копия"),
    ("тестю", "ntcn."), ("123\n🙂", "123\n🙂") })
    Check(Layout.Convert(before) == after, "conversion " + before);
foreach (var value in new[] { "Hello Мир", "привет, мир!", "😀 ghbdtn" })
    Check(Layout.Convert(Layout.Convert(value)) == value, "round trip " + value);
Check(Layout.Convert(Layout.Convert("@#$^&", "en"), "ru") == "@#$^&", "punctuation direction uses active language");
foreach (var value in new[] { "mesh wifi копbz", "mesh wifi копbz ", "mesh wifi копbz  " })
{
    var span = Layout.LastWord(value, value.Length)!.Value;
    Check(Layout.Replace(value, span, Layout.Convert(value.Substring(span.Start, span.Length))) == value.Replace("копbz", "копия"), "last word only");
}
foreach (var value in new[] { "DA-10", "mail@example.com", "https://site.ru", "абв123" })
    Check(Layout.LastWord(value, value.Length) is null, "preserve identifier " + value);
Check(Layout.LastWord("hello", 99) is null, "invalid caret");
foreach (var value in new[] { "😀", "a\u0301", "👨‍👩‍👧" })
    Check(Layout.PreviousCharacter("test" + value, 4 + value.Length) == new TextSpan(4, value.Length), "whole composed character");
Check(Layout.PreviousCharacter("😀", 1) is null, "reject split surrogate");
foreach (var (value, range, replacement, caret, expected) in new[] {
    ("руддщ ", new TextSpan(0, 5), "hello", 6, "hello "),
    ("руддщ  ", new TextSpan(0, 5), "hello", 7, "hello  "),
    ("руддщ typing", new TextSpan(0, 5), "hello", 12, "hello typing"),
    ("руддщ tail suffix", new TextSpan(0, 5), "hello", 10, "hello tail suffix"),
    ("mesh wifi копbz \t", new TextSpan(10, 5), "копия", 17, "mesh wifi копия \t"),
    ("x ", new TextSpan(0, 1), "hello", 2, "hello ") })
{
    var insertion = Layout.WithCaretTail(value, range, replacement, caret)!.Value;
    Check(Layout.Replace(value, insertion.Range, insertion.Text) == expected, "preserve caret tail text");
    Check(insertion.Range.Start + insertion.Text.Length == caret + replacement.Length - range.Length, "preserve caret position");
}
var firstWord = Layout.WithCaretTail("руддщ ", new(0, 5), "hello", 6)!.Value;
var firstValue = Layout.Replace("руддщ ", firstWord.Range, firstWord.Text);
var firstCaret = firstWord.Range.Start + firstWord.Text.Length;
var secondValue = firstValue.Insert(firstCaret, "цщкдв ");
var secondWord = Layout.WithCaretTail(secondValue, new(firstCaret, 5), "world", firstCaret + 6)!.Value;
Check(Layout.Replace(secondValue, secondWord.Range, secondWord.Text) == "hello world ", "successive corrected words stay separated");
Check(Layout.WithCaretTail("word", new(0, 4), "слово", 3) is null, "reject caret inside replacement");
Check(Layout.WithCaretTail("word", new(0, 5), "слово", 5) is null, "reject invalid replacement bounds");
var dictionary = new Dictionary<string, HashSet<string>> {
    ["ru"] = ["привет", "мир", "как", "дела", "или", "отзывы", "о", "копия", "и", "это"],
    ["en"] = ["hello", "world", "voron", "mesh", "wifi", "i", "a", "b"] };
bool Valid(string word, string lang) => dictionary[lang].Contains(word.ToLowerInvariant());
string Auto(string value, int minimum = 0)
{
    var plan = AutomaticCorrection.GetPlan(value, value.Length, Valid, minimumLocation: minimum);
    return plan is null ? value : Layout.Replace(value, plan.Range, plan.Text);
}
foreach (var (before, after) in new[] {
    ("bkb vbh ", "или мир "), ("ghbdtn rfr ", "привет как "),
    ("руддщ цщкдв ", "hello world "), ("руддщ руддщ ", "hello hello "),
    ("отзывы j мщкщт 400 ", "отзывы о voron 400 "), ("mesh wifi копbz ", "mesh wifi копия "),
    ("hello world ", "hello world "), ("привет hello ", "привет hello "),
    ("b ", "b "), ("DA-10 ", "DA-10 "), ("ghbdtn@example.com ", "ghbdtn@example.com "),
    ("ghbdtn", "ghbdtn"), ("ghbdtn typing", "привет typing"), ("😀 ghbdtn ", "😀 привет ") })
    Check(Auto(before) == after, "automatic " + before);
Check(Auto("ghbdtn rfr ", 6) == "ghbdtn как ", "protect undo prefix");
var shift = new DoubleShift();
Check(!shift.Shift(160, true, 0, false), "first down");
Check(!shift.Shift(160, false, .05, false), "first up");
Check(!shift.Shift(160, true, .12, false), "second down");
Check(shift.Shift(160, false, .17, false), "double Shift on release");
shift.Reset(); shift.Shift(160, true, 1, false); shift.OtherKey(); shift.Shift(160, false, 1.1, false);
shift.Shift(160, true, 1.2, false);
Check(!shift.Shift(160, false, 1.25, false), "Shift typing is not double Shift");
shift.Reset(); shift.Shift(160, true, 2, false); shift.Shift(160, false, 2.4, false); shift.Shift(160, true, 2.5, false);
Check(!shift.Shift(160, false, 2.55, false), "held Shift rejected");
Console.WriteLine($"PASS: {count} portable Windows-core checks");
