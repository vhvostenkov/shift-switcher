using System.Runtime.InteropServices;

namespace ShiftSwitcher.Windows;

// Windows spellcheck COM interfaces; only their initial, declared vtable slots
// are used. Missing language packs disable automatic correction for that language.
[ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellCheckerFactory
{
    void Languages([MarshalAs(UnmanagedType.Interface)] out object languages);
    void IsSupported([MarshalAs(UnmanagedType.LPWStr)] string language, [MarshalAs(UnmanagedType.Bool)] out bool supported);
    void CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string language, out ISpellChecker checker);
}
[ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISpellChecker
{
    void LanguageTag([MarshalAs(UnmanagedType.LPWStr)] out string language);
    void Check([MarshalAs(UnmanagedType.LPWStr)] string text, out IEnumSpellingError errors);
}
[ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IEnumSpellingError
{
    [PreserveSig] int Next([MarshalAs(UnmanagedType.Interface)] out object? error);
}
internal sealed class Spelling : IDisposable
{
    private readonly Dictionary<string, ISpellChecker> checkers = [];
    internal Spelling()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"), true)!;
            var factory = (ISpellCheckerFactory)Activator.CreateInstance(type)!;
            try
            {
                foreach (var (language, tag) in new[] { ("ru", "ru-RU"), ("en", "en-US") })
                {
                    factory.IsSupported(tag, out var supported);
                    if (supported) { factory.CreateSpellChecker(tag, out var checker); checkers[language] = checker; }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }
        catch (COMException) { }
    }
    internal bool Ready => checkers.Count == 2;
    internal bool Valid(string word, string language)
    {
        if (word.Length == 0 || !checkers.TryGetValue(language, out var checker)) return false;
        try
        {
            checker.Check(word, out var errors);
            try
            {
                var result = errors.Next(out var error);
                if (error is not null) Marshal.ReleaseComObject(error);
                return result == 1; // S_FALSE: empty enumeration, no spelling errors.
            }
            finally { Marshal.ReleaseComObject(errors); }
        }
        catch (COMException) { return false; }
    }
    public void Dispose() { foreach (var checker in checkers.Values) Marshal.ReleaseComObject(checker); }
}
