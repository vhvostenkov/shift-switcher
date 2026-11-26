using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShiftSwitcher.Licensing;
using ShiftSwitcher.Windows;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native DPAPI checks require Windows");
string Fingerprint(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
var realPath = WindowsLicenseStore.DefaultPath;
var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
if (args is ["--device"])
{
    var value = new WindowsLicenseStore(realPath).Load() ?? throw new IOException("Launch the candidate once before reading its installation ID");
    Console.WriteLine(JsonSerializer.Serialize(new { device = LicenseCheck.Device(value.Installation), state_sha256 = Fingerprint(realPath) })); return;
}
if (args is ["--expire-trial-qa", var expectedDevice])
{
    if (System.Diagnostics.Process.GetProcessesByName("ShiftSwitcher").Length != 0) throw new InvalidOperationException("Stop the candidate before changing its QA fixture");
    var store = new WindowsLicenseStore(realPath); var original = store.Load() ?? throw new IOException("Missing state");
    if (LicenseCheck.Device(original.Installation) != expectedDevice || original.Key != "" || original.Certificate != "" || original.Trial.DaysRemaining(now) == 0)
        throw new InvalidOperationException("Only the expected fresh trial, without any licence, may be used as this QA fixture");
    var backup = realPath + ".pre-free-qa-" + Guid.NewGuid().ToString("N");
    File.Copy(realPath, backup, false);
    var fixtureStarted = now - 15L * 86400;
    store.Save(original with { Trial = new(fixtureStarted, now) });
    Console.WriteLine(JsonSerializer.Serialize(new { device = expectedDevice, backup, original_sha256 = Fingerprint(backup), fixture_started = fixtureStarted, fixture_sha256 = Fingerprint(realPath) })); return;
}
if (args is ["--restore-trial-qa", var backupPath, var expectedDeviceId, var started])
{
    if (System.Diagnostics.Process.GetProcessesByName("ShiftSwitcher").Length != 0) throw new InvalidOperationException("Stop the candidate before restoring its QA fixture");
    if (!backupPath.StartsWith(realPath + ".pre-free-qa-", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(Path.GetDirectoryName(Path.GetFullPath(backupPath)), Path.GetDirectoryName(realPath), StringComparison.OrdinalIgnoreCase)) throw new IOException("Expected the recorded private QA backup");
    var original = new WindowsLicenseStore(backupPath).Load() ?? throw new IOException("Invalid backup");
    var current = new WindowsLicenseStore(realPath).Load() ?? throw new IOException("Invalid fixture");
    if (LicenseCheck.Device(original.Installation) != expectedDeviceId || LicenseCheck.Device(current.Installation) != expectedDeviceId ||
        current.Trial.Started != long.Parse(started) || current.Key != "" || current.Certificate != "")
        throw new InvalidOperationException("The QA state changed; do not overwrite a newly activated licence");
    var restore = realPath + ".restore-" + Guid.NewGuid().ToString("N");
    File.Copy(backupPath, restore, false); File.Move(restore, realPath, true);
    if (Fingerprint(realPath) != Fingerprint(backupPath)) throw new IOException("Restore mismatch");
    Console.WriteLine(JsonSerializer.Serialize(new { restored = true, state_sha256 = Fingerprint(realPath) })); return;
}
if (args.Length != 0) throw new ArgumentException("Unknown native licensing QA operation");
var folder = Path.Combine(Path.GetTempPath(), "shift-licensing-qa-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder); var path = Path.Combine(folder, "state.dat");
var local = new WindowsLicenseStore(path);
var state = new LicenseState(Guid.NewGuid().ToString("D"), new(now, now), "test-only-not-a-real-key", "test-only-certificate");
int checks = 0;
void Check(bool result, string name) { checks++; if (!result) throw new Exception(name); }
Check(local.Load() is null, "new store is absent");
local.Save(state); var bytes = File.ReadAllBytes(path);
Check(!Encoding.UTF8.GetString(bytes).Contains(state.Key, StringComparison.Ordinal), "key is encrypted at rest");
Check(!Encoding.UTF8.GetString(bytes).Contains(state.Installation, StringComparison.Ordinal), "installation ID is encrypted at rest");
Check(local.Load() == state, "current-user DPAPI round trip");
Check(new WindowsLicenseStore(path).Load() == state, "DPAPI survives reopening the store");
File.WriteAllBytes(path, [1, 2, 3]);
bool rejected = false;
try { local.Load(); } catch (Exception error) when (error is CryptographicException or JsonException) { rejected = true; }
Check(rejected, "corrupt store does not create a fresh trial");
local.Save(state); Check(local.Load() == state, "atomic replacement restores valid protected state");
Console.WriteLine(JsonSerializer.Serialize(new { passed = checks, folder, current_user_dpapi = true, dpapi_ui_forbidden = true }));
