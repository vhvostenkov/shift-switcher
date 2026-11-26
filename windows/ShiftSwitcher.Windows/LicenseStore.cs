using System.Runtime.InteropServices;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ShiftSwitcher.Licensing;

namespace ShiftSwitcher.Windows;

internal sealed class WindowsLicenseStore : ILicenseStore
{
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VHVost", "ShiftSwitcher", "license-state-v1.dat");
    private readonly string path;
    internal WindowsLicenseStore(string path) => this.path = path;
    public LicenseState? Load()
    {
        if (!File.Exists(path)) return null;
        using var input = File.OpenRead(path);
        if (input.Length > 131072) throw new IOException("Invalid licence state size");
        var encrypted = new byte[(int)input.Length]; input.ReadExactly(encrypted);
        var plain = Transform(encrypted, false);
        try { return JsonSerializer.Deserialize<LicenseState>(plain) ?? throw new JsonException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Save(LicenseState state)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(state);
        byte[] encrypted;
        try { encrypted = Transform(plain, true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { stream.Write(encrypted); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
    private static byte[] Transform(byte[] data, bool protect)
    {
        var source = new Blob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(data, 0, source.Data, data.Length);
            // Current-user DPAPI, without UI or machine-wide protection.
            var success = protect ? CryptProtectData(ref source, null, 0, 0, 0, 1, out output)
                : CryptUnprotectData(ref source, 0, 0, 0, 0, 1, out output);
            if (!success) throw new CryptographicException(Marshal.GetLastWin32Error());
            if (output.Size is < 1 or > 131072) throw new CryptographicException("Invalid protected state");
            var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Marshal.Copy(new byte[data.Length], 0, source.Data, data.Length); Marshal.FreeHGlobal(source.Data);
            if (output.Data != 0)
            {
                if (!protect && output.Size is > 0 and <= 131072) Marshal.Copy(new byte[output.Size], 0, output.Data, output.Size);
                LocalFree(output.Data);
            }
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { internal int Size; internal nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint memory);
}
