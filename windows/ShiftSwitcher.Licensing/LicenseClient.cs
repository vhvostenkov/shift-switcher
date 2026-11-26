using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShiftSwitcher.Licensing;

public sealed record LicenseState(string Installation, TrialState Trial, string Key = "", string Certificate = "");
public interface ILicenseStore
{
    LicenseState? Load();
    void Save(LicenseState state);
}

// Calls originate on the UI context; HTTP awaits return to that context before changing state.
public sealed class LicenseClient : IDisposable
{
    private static readonly Uri Api = new("https://vhvost.com/pay/api/");
    private readonly ILicenseStore store;
    private readonly IReadOnlyDictionary<string, string> keys;
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly Func<long> clock;
    private LicenseState? state;
    private LicensePayload? payload;
    private long lastSaved, lastRenewal;
    private int generation;
    private bool clockBackwards;
    public string Environment { get; }
    public string Device { get; private set; } = "";
    public bool Ready { get; private set; }
    public bool Busy { get; private set; }
    public string Message { get; private set; } = "";
    public int TrialDays { get; private set; }
    public bool Licensed => payload is { } value && !clockBackwards && value.ValidUntil > clock();
    public bool Owner => Licensed && payload?.Kind == "owner";
    public bool HasLicenseData => state is { } value && (value.Key.Length > 0 || value.Certificate.Length > 0);
    public bool HasPro
    {
        get
        {
            var now = clock();
            return Ready && state is { } value && now >= value.Trial.Latest - 300 &&
                ((payload is { } paid && paid.ValidUntil > now && paid.IssuedAt <= now + 300) || value.Trial.DaysRemaining(now) > 0);
        }
    }
    public string Status => !Ready ? "Данные лицензии недоступны" : clockBackwards ? "Проверьте дату компьютера" : Owner ? "Pro владельца" : Licensed ? "Pro активен" : TrialDays > 0 ? $"Пробный Pro · осталось дней: {TrialDays}" : "Free · ручное исправление";
    public event Action? Changed;

    public LicenseClient(ILicenseStore store, IReadOnlyDictionary<string, string> keys, string environment,
        Func<long>? clock = null, HttpClient? http = null)
    {
        if (environment is not ("DEMO" or "LIVE")) throw new ArgumentException("Unknown licence environment");
        this.store = store; this.keys = keys; Environment = environment;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        ownsHttp = http is null;
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        try
        {
            var loaded = store.Load(); var now = this.clock();
            if (loaded is null)
            {
                loaded = new(Guid.NewGuid().ToString("D"), new(now, now)); store.Save(loaded);
            }
            if (!Guid.TryParseExact(loaded.Installation, "D", out _) || loaded.Trial is null || loaded.Trial.Started < 0 ||
                loaded.Trial.Latest < loaded.Trial.Started || loaded.Key is null || loaded.Key.Length > 100 ||
                loaded.Certificate is null || loaded.Certificate.Length > 16384) throw new JsonException();
            state = loaded; Device = LicenseCheck.Device(loaded.Installation); Ready = true; lastSaved = now;
            Refresh();
        }
        catch (Exception error) when (StorageError(error))
        { Message = "Не удалось прочитать или сохранить лицензию. Ручное исправление доступно."; }
    }
    private static bool StorageError(Exception error) => error is IOException or UnauthorizedAccessException or JsonException or CryptographicException;
    private bool Persist(LicenseState updated)
    {
        try { store.Save(updated); state = updated; lastSaved = clock(); return true; }
        catch (Exception error) when (StorageError(error))
        { Message = "Не удалось сохранить лицензию на этом ПК"; return false; }
    }
    public void Refresh()
    {
        if (state is null || !Ready) return;
        var now = clock(); clockBackwards = now < state.Trial.Latest - 300;
        TrialDays = state.Trial.DaysRemaining(now);
        payload = LicenseCheck.Verify(state.Certificate, Device, keys, Environment, now);
        if (now - lastSaved >= 60 && !Persist(state with { Trial = state.Trial.Observe(now) })) Ready = false;
    }
    private int Begin() { Busy = true; var operation = ++generation; Changed?.Invoke(); return operation; }
    private void End(int operation)
    {
        if (operation != generation) return;
        Busy = false; Refresh(); Changed?.Invoke();
    }
    private async Task<(HttpStatusCode Status, JsonDocument Body)> Post(string endpoint, string key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // The bounded RF API requires Content-Length, rather than a streamed JSON body.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Api, endpoint)) { Content = new StringContent(JsonSerializer.Serialize(new { key, device = Device }), Encoding.UTF8, "application/json") };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.Content.Headers.ContentLength > 16384) throw new JsonException();
        await using var input = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream(); var buffer = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(buffer, timeout.Token);
            if (count == 0) break;
            if (output.Length + count > 16384) throw new JsonException();
            output.Write(buffer, 0, count);
        }
        return (response.StatusCode, JsonDocument.Parse(output.ToArray()));
    }
    public async Task ActivateAsync(string key)
    {
        if (!Ready || state is null) return;
        key = key.Trim();
        if (!LicenseCheck.ValidKey(key)) { Message = "Проверьте ключ лицензии"; Changed?.Invoke(); return; }
        var operation = Begin();
        try
        {
            var (status, body) = await Post("activate", key);
            using (body)
            {
                if (operation != generation) return;
                if (status == HttpStatusCode.Forbidden && body.RootElement.TryGetProperty("error", out var error) && error.GetString() == "license_revoked")
                {
                    if (key == state.Key)
                    {
                        if (!Persist(state with { Key = "", Certificate = "" })) Ready = false;
                        payload = null; Message = "Лицензия отозвана. Доступны Free и оставшийся пробный период.";
                    }
                    else Message = "Этот ключ отозван. Текущая лицензия не изменена.";
                    return;
                }
                if (status != HttpStatusCode.OK || !body.RootElement.TryGetProperty("certificate", out var value) || value.ValueKind != JsonValueKind.String ||
                    LicenseCheck.Verify(value.GetString()!, Device, keys, Environment, clock()) is not { Kind: null })
                { Message = "Активация не выполнена. Проверьте ключ, соединение и лимит двух компьютеров."; return; }
                if (Persist(state with { Key = key, Certificate = value.GetString()! })) Message = "Pro активирован на этом ПК";
            }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
        { if (operation == generation) Message = "Не удалось связаться с сервисом активации. Повторите позже."; }
        finally { End(operation); }
    }
    public async Task ImportAsync(string json)
    {
        if (!Ready || state is null) return;
        try
        {
            if (json.Length > 16384) throw new JsonException();
            using var body = JsonDocument.Parse(json); var root = body.RootElement;
            if (root.GetProperty("product").GetString() != LicenseCheck.Product || root.GetProperty("environment").GetString() != Environment) throw new JsonException();
            if (root.TryGetProperty("certificate", out var certificate) && certificate.ValueKind == JsonValueKind.String &&
                LicenseCheck.Verify(certificate.GetString()!, Device, keys, Environment, clock()) is { Kind: "owner" })
            {
                var operation = Begin();
                try
                {
                    if (Persist(state with { Key = "", Certificate = certificate.GetString()! })) Message = "Лицензия владельца активирована";
                }
                finally { End(operation); }
                return;
            }
            await ActivateAsync(root.GetProperty("license_key").GetString() ?? "");
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
        { Message = "Файл лицензии не подходит этой сборке или этому ПК"; Changed?.Invoke(); }
    }
    public async Task ReleaseAsync()
    {
        if (!Ready || state is null) return;
        var localOnly = state.Key.Length == 0;
        if (localOnly && state.Certificate.Length == 0) return;
        var operation = Begin();
        try
        {
            if (!localOnly)
            {
                var (status, body) = await Post("release-device", state.Key);
                body.Dispose();
                if (operation != generation) return;
                if (status != HttpStatusCode.OK) { Message = "Не удалось освободить ПК. Проверьте соединение."; return; }
            }
            if (Persist(state with { Key = "", Certificate = "" })) Message = "Этот ПК освобождён";
            else { Ready = false; Message = "ПК освобождён, но локальные данные не удалось сохранить"; }
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
        { if (operation == generation) Message = "Не удалось освободить ПК. Проверьте соединение."; }
        finally { End(operation); }
    }
    public async Task RenewIfNeededAsync()
    {
        var now = clock();
        if (!Ready || Busy || Owner || state?.Key.Length is not > 0 || (payload?.ValidUntil ?? 0) >= now + 7L * 86400 || now - lastRenewal < 86400) return;
        lastRenewal = now; await ActivateAsync(state.Key);
    }
    public void Dispose() { generation++; if (ownsHttp) http.Dispose(); }
}
