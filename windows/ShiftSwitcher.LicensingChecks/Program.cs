using System.Net;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using ShiftSwitcher.Licensing;

int count = 0;
void Check(bool result, string name) { count++; if (!result) throw new Exception(name); }
// Published RFC 8032 test seed. It has no relation to either production signing key.
var signerKey = new Ed25519PrivateKeyParameters(Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"), 0);
var demoSignerKey = new Ed25519PrivateKeyParameters(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), 0);
var keys = new Dictionary<string, string> { ["LIVE:test"] = Convert.ToBase64String(signerKey.GeneratePublicKey().GetEncoded()), ["DEMO:test"] = Convert.ToBase64String(demoSignerKey.GeneratePublicKey().GetEncoded()) };
const long start = 1000000;
long now = start;
string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
string Sign(LicensePayload value, Ed25519PrivateKeyParameters? signingKey = null)
{
    var body = "ss1." + Encode(JsonSerializer.SerializeToUtf8Bytes(value)); var bytes = Encoding.UTF8.GetBytes(body);
    var signer = new Ed25519Signer(); signer.Init(true, signingKey ?? (value.Environment == "DEMO" ? demoSignerKey : signerKey)); signer.BlockUpdate(bytes, 0, bytes.Length);
    return body + "." + Encode(signer.GenerateSignature());
}
LicensePayload Paid(string device, string environment = "LIVE") => new(1, null, LicenseCheck.Product, "test_only", device, now, now + 30L * 86400, "test", environment);
LicensePayload Owner(string device) => Paid(device, "DEMO") with { Kind = "owner", License = "owner_test_only", ValidUntil = now + 365L * 86400 };
var device = new string('a', 64); var good = Paid(device);
Check(LicenseCheck.Verify(Sign(good), device, keys, "LIVE", now) is not null, "valid signed certificate");
Check(LicenseCheck.Verify(Sign(good, demoSignerKey), device, keys, "LIVE", now) is null, "DEMO signer cannot mint LIVE claims");
foreach (var invalid in new[] {
    good with { Schema = 2 }, good with { Product = "another-product" }, good with { Device = new string('b', 64) },
    good with { Environment = "DEMO" }, good with { IssuedAt = now + 301 }, good with { ValidUntil = now },
    good with { ValidUntil = now + 32L * 86400 }, good with { KeyId = "unknown" }, good with { Kind = "admin" },
    good with { Kind = "owner", License = "not_an_owner" }, good with { License = "" } })
    Check(LicenseCheck.Verify(Sign(invalid), device, keys, "LIVE", now) is null, "reject invalid certificate claims");
Check(LicenseCheck.Verify(Sign(good)[..^8] + "AAAAAAAA", device, keys, "LIVE", now) is null, "reject changed signature");
foreach (var value in new[] { "", "ss1.bad.bad", "ss1.+.a", "ss2.a.b", new string('a', 20000) })
    Check(LicenseCheck.Verify(value, device, keys, "LIVE", now) is null, "reject malformed certificate");
Check(LicenseCheck.Verify(Sign(Owner(device)), device, keys, "DEMO", now)?.Kind == "owner", "DEMO owner certificate");
Check(LicenseCheck.Verify(Sign(Owner(device) with { Environment = "LIVE" }), device, keys, "LIVE", now)?.Kind == "owner", "explicit LIVE owner certificate uses LIVE trust key");
Check(LicenseCheck.Verify(Sign(Owner(device)), device, keys, "LIVE", now) is null, "DEMO owner never enters LIVE");
Check(LicenseCheck.Verify(Sign(Owner(device) with { ValidUntil = now + 367L * 86400 }), device, keys, "DEMO", now) is null, "owner term bounded");
Check(LicenseCheck.Verify(Sign(Owner(device) with { License = "normal" }), device, keys, "DEMO", now) is null, "owner prefix required");
using (var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "python-certificate.json"))))
    Check(LicenseCheck.Verify(fixture.RootElement.GetProperty("certificate").GetString()!, device, keys, "LIVE", start) is not null, "Python commerce-format cross-language signature");
var trial = new TrialState(start, start);
Check(trial.DaysRemaining(start) == 14, "new trial");
Check(trial.DaysRemaining(start + 14L * 86400 - 1) == 1, "last trial second");
Check(trial.DaysRemaining(start + 14L * 86400) == 0, "exact trial end");
Check(trial.Observe(start + 1000).DaysRemaining(start + 699) == 0, "trial clock rollback");
Check(new TrialState(start, start - 1).DaysRemaining(start) == 0, "invalid stored trial");
var store = new MemoryStore();
var bank = new BankHandler(async request =>
{
    Check(request.Content!.Headers.ContentLength is > 0 and <= 512, "activation body has bounded Content-Length before serialization by the transport");
    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
    Check(request.RequestUri!.Scheme == "https" && request.RequestUri.Host == "vhvost.com" && request.RequestUri.Query == "", "key uses owned HTTPS endpoint without URL secrets");
    Check(body.RootElement.EnumerateObject().Count() == 2, "activation sends only key and device");
    return BankHandler.Json(new { certificate = Sign(Paid(body.RootElement.GetProperty("device").GetString()!)) });
});
using var http = new HttpClient(bank);
using var client = new LicenseClient(store, keys, "LIVE", () => now, http);
Check(client.Ready && client.HasPro && client.TrialDays == 14, "new persisted Pro trial");
await client.ActivateAsync(new string('k', 32) + "\ntext"); Check(bank.Calls == 0, "multiline clipboard text is rejected before network transmission");
using (var reopened = new LicenseClient(store, keys, "LIVE", () => now, http))
    Check(reopened.Device == client.Device && reopened.TrialDays == 14, "stable installation and trial across reopen");
now += 15L * 86400; client.Refresh();
Check(!client.HasPro && client.TrialDays == 0, "trial ends without resetting stored preference");
await client.ActivateAsync(new string('k', 43));
Check(client.Licensed && client.HasPro && store.State!.Key.Length == 43, "paid activation persists both key and certificate");
using (var reopened = new LicenseClient(store, keys, "LIVE", () => now, http))
    Check(reopened.Licensed && reopened.HasPro, "offline paid reopen");
now += 1000; client.Refresh(); now -= 301; client.Refresh();
Check(!client.HasPro, "clock rollback blocks an otherwise valid paid certificate");
now += 301; client.Refresh(); Check(client.HasPro, "correct clock restores entitlement");
var saved = store.State!.Certificate;
bank.Reply = _ => Task.FromResult(BankHandler.Json(new { certificate = "ss1.invalid.invalid" }));
await client.ActivateAsync(new string('x', 43));
Check(client.Licensed && store.State.Certificate == saved, "bad activation cannot replace existing valid certificate");
bank.Reply = _ => Task.FromResult(BankHandler.Json(new { error = "unavailable" }, HttpStatusCode.ServiceUnavailable));
await client.ActivateAsync(new string('x', 43));
Check(client.Licensed && !client.Busy, "transient server failure preserves current paid access");
bank.Reply = _ => Task.FromException<HttpResponseMessage>(new IOException("test response dropped"));
await client.ActivateAsync(new string('x', 43)); Check(client.Licensed && !client.Busy, "dropped response stream does not crash activation or erase paid access");
bank.Reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', 20000)) });
await client.ActivateAsync(new string('x', 43)); Check(client.Licensed && !client.Busy, "oversized reply rejected without losing old access");
bank.Reply = _ => Task.FromResult(BankHandler.Json(new { error = "license_revoked" }, HttpStatusCode.Forbidden));
await client.ActivateAsync(new string('k', 43));
Check(!client.Licensed && !client.HasPro && store.State.Key == "" && store.State.Certificate == "", "refund revocation clears cached access");
store.FailSave = true;
bank.Reply = _ => Task.FromResult(BankHandler.Json(new { certificate = Sign(Paid(client.Device)) }));
await client.ActivateAsync(new string('k', 43)); Check(!client.Licensed && !client.HasPro, "failed persistence cannot grant paid access");
store.FailSave = false;
await client.ActivateAsync(new string('k', 43)); Check(client.Licensed, "activation after storage recovery");
bank.Reply = request =>
{
    Check(request.Content!.Headers.ContentLength is > 0 and <= 512, "release body has bounded Content-Length before serialization by the transport");
    Check(request.RequestUri!.AbsolutePath == "/pay/api/release-device", "release uses shared device endpoint");
    return Task.FromResult(BankHandler.Json(new { released = true }));
};
await client.ReleaseAsync(); Check(!client.Licensed && !client.HasLicenseData, "paid device release clears local credentials");
using (var failed = new LicenseClient(new MemoryStore { FailSave = true }, keys, "LIVE", () => now))
    Check(!failed.Ready && !failed.HasPro, "no fresh trial on storage failure");
using (var failed = new LicenseClient(new MemoryStore { FailLoad = true }, keys, "LIVE", () => now))
    Check(!failed.Ready && !failed.HasPro, "corrupt store cannot reset trial");
var ownerStore = new MemoryStore(); using var ownerClient = new LicenseClient(ownerStore, keys, "DEMO", () => now, http);
now += 15L * 86400; ownerClient.Refresh();
var envelope = JsonSerializer.Serialize(new { product = LicenseCheck.Product, environment = "DEMO", certificate = Sign(Owner(ownerClient.Device)) });
var calls = bank.Calls; await ownerClient.ImportAsync(envelope);
Check(ownerClient.Owner && ownerClient.HasPro && bank.Calls == calls, "device-bound owner import without network");
using (var reopened = new LicenseClient(ownerStore, keys, "DEMO", () => now, http)) Check(reopened.Owner, "owner licence persists across reopen");
await client.ImportAsync(envelope); Check(!client.HasPro, "LIVE rejects DEMO owner file");
await ownerClient.ReleaseAsync(); Check(!ownerClient.HasPro && bank.Calls == calls, "owner release stays local and keeps expired trial expired");
var delayed = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
bank.Reply = _ => delayed.Task;
var pending = ownerClient.ActivateAsync(new string('k', 43));
await ownerClient.ImportAsync(envelope);
delayed.SetResult(BankHandler.Json(new { certificate = Sign(Paid(ownerClient.Device, "DEMO")) }));
await pending; Check(ownerClient.Owner && !ownerClient.Busy, "stale activation cannot overwrite later owner import");
Console.WriteLine($"PASS: {count} licensing checks");

sealed class MemoryStore : ILicenseStore
{
    internal LicenseState? State;
    internal bool FailLoad, FailSave;
    public LicenseState? Load() => FailLoad ? throw new JsonException() : State;
    public void Save(LicenseState state) { if (FailSave) throw new IOException(); State = state; }
}
sealed class BankHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
{
    internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Reply = reply;
    internal int Calls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { Calls++; return Reply(request); }
    internal static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
