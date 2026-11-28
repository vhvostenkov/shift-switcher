using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ShiftSwitcher.Windows;

internal sealed class Updater : IDisposable
{
    private const string Feed = "https://vhvost.com/shift-switcher/downloads/update.json";
    private const string PublicKey = "KWJ0pQmLV+QyNTbiGsi/mU4MGt/ungVsYNfegu4Zzpc=";
    private static readonly Version Current = typeof(Updater).Assembly.GetName().Version ?? new(0, 0);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 10000 };
    private DateTimeOffset nextCheck = DateTimeOffset.UtcNow.AddSeconds(30);
    private (Release Release, byte[] Feed)? available;
    private bool busy;
    private bool disposed;
    private readonly string? failedVersion;
    internal bool Automatic { get; set; }
    internal Action<string>? Status { get; init; }
    internal Action? ReadyToExit { get; init; }
    internal Func<bool>? CanRestart { get; init; }

    internal Updater(bool automatic, string? failedVersion)
    {
        Automatic = automatic;
        this.failedVersion = failedVersion;
        timer.Tick += async (_, _) =>
        {
            if (!Automatic || busy || disposed) return;
            if (DateTimeOffset.UtcNow >= nextCheck) await CheckAsync(false);
            if (Automatic && available is { } release && release.Release.Version != failedVersion && CanRestart?.Invoke() == true && Idle())
                await InstallAsync(release.Release, release.Feed, false);
        };
        timer.Start();
    }

    private sealed record Envelope(
        [property: JsonPropertyName("payload")] byte[] Payload,
        [property: JsonPropertyName("signature")] byte[] Signature);
    private sealed record Release(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("windows")] Package Windows);
    private sealed record Package(
        [property: JsonPropertyName("bytes")] int Bytes,
        [property: JsonPropertyName("sha256")] string Sha256);

    private static Release Parse(byte[] feed)
    {
        if (feed.Length > 8192) throw new InvalidDataException("Update feed length");
        var envelope = JsonSerializer.Deserialize<Envelope>(feed);
        if (envelope?.Payload is null || envelope.Signature?.Length != 64)
            throw new InvalidDataException("Update feed");
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(Convert.FromBase64String(PublicKey), 0));
        verifier.BlockUpdate(envelope.Payload, 0, envelope.Payload.Length);
        if (!verifier.VerifySignature(envelope.Signature)) throw new InvalidDataException("Update signature");
        var release = JsonSerializer.Deserialize<Release>(envelope.Payload);
        if (release?.Windows is null || release.Schema != 1 || ParseVersion(release.Version) is null ||
            release.Windows.Bytes is <= 0 or > 160 * 1024 * 1024 || release.Windows.Sha256?.Length != 64)
            throw new InvalidDataException("Update manifest");
        return release;
    }

    private static Version? ParseVersion(string value)
    {
        return Version.TryParse(value, out var version) && version.Build >= 0 && version.Revision < 0 &&
            version.Major <= 999999 && version.Minor <= 999999 && version.Build <= 999999 &&
            version.ToString(3) == value ? new Version(version.Major, version.Minor, version.Build, 0) : null;
    }

    internal async Task CheckAsync(bool manual)
    {
        if (busy || disposed) return;
        busy = true;
        nextCheck = DateTimeOffset.UtcNow.AddHours(6);
        try
        {
            using var output = new MemoryStream();
            await DownloadAsync(new Uri(Feed), output, 8192);
            var feed = output.ToArray();
            var release = Parse(feed);
            available = ParseVersion(release.Version)! > Current ? (release, feed) : null;
            if (disposed) return;
            if (available is null)
            {
                if (manual) Status?.Invoke("У вас актуальная версия Shift Switcher");
                return;
            }
            Status?.Invoke($"Доступно обновление {release.Version}");
        }
        catch (Exception error) when (UpdateFailure(error))
        {
            available = null;
            if (manual && !disposed) Status?.Invoke("Не удалось проверить обновления");
        }
        finally { busy = false; }

        if (manual && !disposed && available is { } update &&
            MessageBox.Show($"Установить Shift Switcher {update.Release.Version} и перезапустить приложение?", "Shift Switcher", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            await InstallAsync(update.Release, update.Feed, true);
    }

    private async Task InstallAsync(Release release, byte[] feed, bool manual)
    {
        if (busy || disposed) return;
        busy = true;
        string? staging = null;
        try
        {
            var target = Environment.ProcessPath ?? throw new IOException("Application path unavailable");
            if (!Path.GetFileName(target).Equals("ShiftSwitcher.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Unsupported installation path");
            staging = Path.Combine(Path.GetDirectoryName(target)!, ".shift-switcher-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var archive = Path.Combine(staging, "update.zip");
            var url = new Uri($"https://github.com/vhvostenkov/shift-switcher/releases/download/v{release.Version}/Shift-Switcher-Windows.zip");
            await using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await DownloadAsync(url, file, release.Windows.Bytes);
            if (disposed || (!manual && !Automatic)) return;
            await File.WriteAllBytesAsync(Path.Combine(staging, "signed-feed.json"), feed);
            await Task.Run(() => File.Copy(target, Path.Combine(staging, "installer.exe")));
            // Do not restart while the user has returned to the computer.
            if (disposed || (!manual && (!Automatic || CanRestart?.Invoke() != true || !Idle())))
            {
                nextCheck = DateTimeOffset.UtcNow.AddMinutes(10);
                return;
            }
            using var parent = Process.GetCurrentProcess();
            var start = new ProcessStartInfo(Path.Combine(staging, "installer.exe")) { UseShellExecute = false, WorkingDirectory = staging };
            var token = Guid.NewGuid().ToString("N");
            using var started = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent(token));
            foreach (var argument in new[] { "--install-update", parent.Id.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString(), target, release.Version, token })
                start.ArgumentList.Add(argument);
            using var helper = Process.Start(start) ?? throw new IOException("Could not start installer");
            if (!await Task.Run(() => started.WaitOne(20000))) throw new IOException("Installer did not start");
            if (disposed || (!manual && (!Automatic || CanRestart?.Invoke() != true || !Idle())))
            {
                // The helper has finished verification and is waiting for this process to exit.
                if (!helper.HasExited) helper.Kill();
                await helper.WaitForExitAsync();
                nextCheck = DateTimeOffset.UtcNow.AddMinutes(10);
                return;
            }
            // The stage is kept until the helper confirms the new process has loaded.
            staging = null;
            ReadyToExit?.Invoke();
        }
        catch (Exception error) when (UpdateFailure(error))
        {
            if (manual && !disposed)
                Status?.Invoke("Не удалось установить обновление. На сайте доступен ZIP для ручной замены.");
        }
        finally
        {
            if (staging is not null) TryCleanup(staging);
            available = null;
            busy = false;
        }
    }

    private static void StageExecutable(string staging, Release release)
    {
        // Keep a read lock on the same ZIP from hashing through extraction.
        using var file = File.OpenRead(Path.Combine(staging, "update.zip"));
        if (file.Length != release.Windows.Bytes || !Convert.ToHexString(SHA256.HashData(file)).Equals(release.Windows.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update archive hash");
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        var entry = zip.Entries.SingleOrDefault(item => item.FullName == "ShiftSwitcher.exe");
        if (entry is null || entry.Length is <= 0 or > 160 * 1024 * 1024) throw new InvalidDataException("Update executable");
        var candidate = Path.Combine(staging, "next.exe");
        entry.ExtractToFile(candidate);
        var product = FileVersionInfo.GetVersionInfo(candidate).ProductVersion?.Split('+')[0];
        if (product != release.Version) throw new InvalidDataException("Update version");
    }

    internal static int Apply(string[] args)
    {
        var staging = Path.GetDirectoryName(Environment.ProcessPath)!;
        try
        {
            if (args.Length != 6 || !Guid.TryParseExact(args[5], "N", out _) || !int.TryParse(args[1], out var pid) || !long.TryParse(args[2], out var started) ||
                ParseVersion(args[4]) is not { } version || version <= Current) return 1;
            var target = Path.GetFullPath(args[3]);
            if (!Path.GetFileName(target).Equals("ShiftSwitcher.exe", StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(staging).StartsWith(".shift-switcher-update-", StringComparison.Ordinal) ||
                !string.Equals(Path.GetDirectoryName(staging), Path.GetDirectoryName(target), StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(staging) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return 1;
            using var parent = Process.GetProcessById(pid);
            if (parent.StartTime.ToUniversalTime().Ticks != started ||
                !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase)) return 1;
            var feed = Path.Combine(staging, "signed-feed.json");
            if (new FileInfo(feed).Length > 8192) return 1;
            var release = Parse(File.ReadAllBytes(feed));
            if (release.Version != args[4]) return 1;
            StageExecutable(staging, release);
            using (var handshake = EventWaitHandle.OpenExisting(ReadyEvent(args[5]))) handshake.Set();
            if (!parent.WaitForExit(20000)) return 1;
            var candidate = Path.Combine(staging, "next.exe");
            var backup = Path.Combine(staging, "previous.exe");
            var receipt = Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyEvent(receipt));
            try { File.Replace(candidate, target, backup); }
            catch { using var unchanged = Launch(target, "--update-failed", release.Version); throw; }
            try
            {
                using var updated = Launch(target, "--update-ready", receipt, Path.GetFileName(staging));
                for (var attempt = 0; attempt < 150; attempt++)
                {
                    if (ready.WaitOne(100))
                    {
                        try { File.WriteAllText(Path.Combine(staging, "completed"), ""); }
                        catch (Exception error) when (UpdateFailure(error)) {}
                        return 0;
                    }
                    if (updated.HasExited) break;
                }
                // Keep the previous EXE if startup is still in progress.
                if (!updated.HasExited) return 2;
            }
            catch (Exception error) when (UpdateFailure(error)) {}
            File.Replace(backup, target, null);
            using var restored = Launch(target, "--update-failed", release.Version);
            return 1;
        }
        catch (Exception error) when (UpdateFailure(error)) { return 1; }
    }

    private static Process Launch(string target, params string[] args)
    {
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        start.ArgumentList.Add("--background");
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("Could not start Shift Switcher");
    }

    internal static void Finish(string[] args)
    {
        var index = Array.IndexOf(args, "--update-ready");
        if (index < 0 || index + 1 >= args.Length || !Guid.TryParseExact(args[index + 1], "N", out _)) return;
        if (!EventWaitHandle.TryOpenExisting(ReadyEvent(args[index + 1]), out var ready)) return;
        using (ready) ready.Set();
        if (args.Length != index + 3) return;
        var name = args[index + 2];
        const string prefix = ".shift-switcher-update-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) return;
        var stage = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, name);
        _ = Task.Run(async () =>
        {
            // The helper still has its EXE open when readiness is signalled.
            await Task.Delay(3000);
            if (File.Exists(Path.Combine(stage, "completed"))) TryCleanup(stage);
        });
    }

    private static string ReadyEvent(string id) => @"Local\VHVost.ShiftSwitcher.Update." + id;

    private static async Task DownloadAsync(Uri url, Stream output, int limit)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri?.Scheme != "https" || response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Update response");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Update download length");
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
    }

    private static bool UpdateFailure(Exception e) => e is IOException or UnauthorizedAccessException or
        InvalidDataException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception
        or HttpRequestException or JsonException or FormatException or CryptographicException or OperationCanceledException;

    private static void TryCleanup(string staging)
    {
        try { Directory.Delete(staging, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {}
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInput { public uint Size; public uint Tick; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInput input);
    private static bool Idle()
    {
        var input = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
        return GetLastInputInfo(ref input) && unchecked((uint)Environment.TickCount - input.Tick) >= 180000;
    }

    public void Dispose()
    {
        disposed = true;
        timer.Dispose();
    }
}
