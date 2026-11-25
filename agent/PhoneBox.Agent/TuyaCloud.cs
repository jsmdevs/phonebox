using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>A Tuya IoT project (iot.tuya.com) with a Smart Life account linked to it.</summary>
public sealed class TuyaConfig
{
    /// <summary>The project's data center, one of <see cref="TuyaCloud.Regions"/>.</summary>
    public string Region { get; set; } = "us";
    public string? AccessId { get; set; }
    public string? AccessSecret { get; set; }

    internal static TuyaConfig Normalize(TuyaConfig? c)
    {
        c ??= new();
        c.Region = TuyaCloud.Regions.ContainsKey(c.Region ?? "") ? c.Region! : "us";
        c.AccessId = string.IsNullOrWhiteSpace(c.AccessId) ? null : c.AccessId.Trim();
        c.AccessSecret = string.IsNullOrWhiteSpace(c.AccessSecret) ? null : c.AccessSecret.Trim();
        return c;
    }

    /// <summary>Retain the secret redacted from settings GETs when a client sends it back as missing or null.</summary>
    internal static bool PreserveSecret(AgentConfig? proposed, string? stored)
    {
        if (proposed?.Tuya is not { AccessSecret: null } tuya || stored is null) return false;
        tuya.AccessSecret = stored;
        return true;
    }
}

/// <param name="Id">"deviceId/code": the device and the on / off value of it to switch.</param>
public sealed record TuyaSwitch(string Id, string Name, bool Online);

/// <summary>
/// Smart Life / Tuya Smart plugs over the Tuya Cloud API, for the charge guard. The secret only signs requests; it is
/// never sent, logged or shown to the phone.
/// </summary>
public sealed partial class TuyaCloud
{
    public static readonly IReadOnlyDictionary<string, string> Regions = new Dictionary<string, string>
    {
        ["us"] = "https://openapi.tuyaus.com",
        ["us-e"] = "https://openapi-ueaz.tuyaus.com",
        ["eu"] = "https://openapi.tuyaeu.com",
        ["eu-w"] = "https://openapi-weaz.tuyaeu.com",
        ["in"] = "https://openapi.tuyain.com",
        ["sg"] = "https://openapi-sg.iotbing.com",
        ["cn"] = "https://openapi.tuyacn.com",
    };

    private const int MaxPages = 20;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly string? _endpoint;
    private sealed record Token(string Region, string Id, string Secret, string Value, DateTime Until);
    private Token? _token;

    public TuyaCloud() { }
    /// <summary>Tests: every region answers at this address.</summary>
    internal TuyaCloud(string endpoint) => _endpoint = endpoint;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}/[A-Za-z0-9_]{1,64}\\z")]
    private static partial Regex SwitchId();
    [GeneratedRegex("^switch(_[0-9]+|_usb[0-9]+|_led)?\\z")]
    private static partial Regex SwitchCode();

    private sealed class TuyaException(int code, string message) : Exception(message) { public int Code => code; }

    /// <summary>Turns a plug on or off ("deviceId/code").</summary>
    internal async Task<HomeAssistant.PowerSwitchResult> SwitchAsync(string id, bool on, TuyaConfig settings)
    {
        if (!SwitchId().IsMatch(id)) return new("not a Smart Life switch", false);
        var slash = id.IndexOf('/');
        var body = JsonSerializer.Serialize(new { commands = new[] { new { code = id[(slash + 1)..], value = on } } });
        var submitted = false;
        try
        {
            var result = await CallAsync(settings, HttpMethod.Post, $"/v1.0/iot-03/devices/{id[..slash]}/commands", null, body, () => submitted = true);
            return new(result.ValueKind == JsonValueKind.False ? "Tuya did not accept the command" : null, true);
        }
        catch (Exception ex) { return new(Message(ex), submitted); }
    }

    /// <summary>Settings › Load devices: the switches of the linked Smart Life accounts (settings possibly unsaved; local only).</summary>
    public async Task<object> ListAsync(TuyaConfig settings)
    {
        try { return new { error = (string?)null, devices = await DevicesAsync(TuyaConfig.Normalize(settings)) }; }
        catch (Exception ex) { return new { error = (string?)Message(ex), devices = new List<TuyaSwitch>() }; }
    }

    internal async Task<List<TuyaSwitch>> DevicesAsync(TuyaConfig settings)
    {
        var list = new List<TuyaSwitch>();
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal) { ["size"] = "50" };
        for (var page = 0; page < MaxPages; page++)
        {
            var result = await CallAsync(settings, HttpMethod.Get, "/v1.0/iot-01/associated-users/devices", query, "");
            if (result.ValueKind != JsonValueKind.Object) break;
            if (result.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
                foreach (var d in devices.EnumerateArray()) list.AddRange(SwitchesOf(d));
            if (!result.TryGetProperty("has_more", out var more) || more.ValueKind != JsonValueKind.True
                || Str(result, "last_row_key") is not { Length: > 0 } key) break;
            query["last_row_key"] = key;
        }
        return list.DistinctBy(s => s.Id).OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<TuyaSwitch> SwitchesOf(JsonElement d)
    {
        if (Str(d, "id") is not { } id) yield break;
        var codes = new List<string>();
        if (d.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Array)
            foreach (var s in status.EnumerateArray())
                if (Str(s, "code") is { } code && SwitchCode().IsMatch(code)
                    && s.TryGetProperty("value", out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    codes.Add(code);
        // a plug or power strip listed without its values: the usual name of the first outlet
        if (codes.Count == 0 && Str(d, "category") is "cz" or "pc") codes.Add("switch_1");
        var name = Str(d, "name") is { Length: > 0 } n ? n : id;
        var online = d.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.True;
        foreach (var code in codes)
            if (SwitchId().IsMatch($"{id}/{code}"))
                yield return new($"{id}/{code}", codes.Count > 1 ? $"{name} ({code})" : name, online);
    }

    private async Task<JsonElement> CallAsync(TuyaConfig settings, HttpMethod method, string path,
        SortedDictionary<string, string>? query, string body, Action? sending = null)
    {
        var (region, id, secret) = (settings.Region, settings.AccessId, settings.AccessSecret);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret))
            throw new TuyaException(0, "enter the Tuya Access ID and Access Secret");
        for (var retry = false; ; retry = true)
        {
            var token = await TokenAsync(region, id, secret);
            try { return await SendAsync(region, id, secret, token, method, path, query, body, sending); }
            // another program using the same project can make the cached token invalid
            catch (TuyaException ex) when (ex.Code is 1010 or 1011 && !retry) { _token = null; }
        }
    }

    private async Task<string> TokenAsync(string region, string id, string secret)
    {
        if (_token is { } t && t.Region == region && t.Id == id && t.Secret == secret && DateTime.UtcNow < t.Until) return t.Value;
        var result = await SendAsync(region, id, secret, "", HttpMethod.Get, "/v1.0/token",
            new(StringComparer.Ordinal) { ["grant_type"] = "1" }, "", null);
        if (Str(result, "access_token") is not { Length: > 0 } value) throw new TuyaException(0, "Tuya returned no access token");
        var seconds = result.TryGetProperty("expire_time", out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var s) ? s : 0;
        _token = new(region, id, secret, value, DateTime.UtcNow.AddSeconds(Math.Clamp(seconds - 60, 0, 7200)));
        return value;
    }

    private async Task<JsonElement> SendAsync(string region, string id, string secret, string token, HttpMethod method, string path,
        SortedDictionary<string, string>? query, string body, Action? sending)
    {
        if (!Regions.TryGetValue(region, out var host)) throw new TuyaException(0, "unknown Tuya data center");
        // signed before URL encoding, with the names in order
        var plain = query is null ? path : path + "?" + string.Join('&', query.Select(q => $"{q.Key}={q.Value}"));
        var encoded = query is null ? path : path + "?" + string.Join('&', query.Select(q => $"{q.Key}={Uri.EscapeDataString(q.Value)}"));
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        using var req = new HttpRequestMessage(method, (_endpoint ?? host) + encoded);
        req.Headers.TryAddWithoutValidation("client_id", id);
        req.Headers.TryAddWithoutValidation("sign", Sign(id, secret, token, t, method.Method, plain, body));
        req.Headers.TryAddWithoutValidation("t", t);
        req.Headers.TryAddWithoutValidation("sign_method", "HMAC-SHA256");
        if (token != "") req.Headers.TryAddWithoutValidation("access_token", token);
        if (method != HttpMethod.Get) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        sending?.Invoke();
        using var res = await Http.SendAsync(req);
        var text = await res.Content.ReadAsStringAsync();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); }
        catch (JsonException) { throw new TuyaException(0, $"Tuya answered {(int)res.StatusCode}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new TuyaException(0, $"Tuya answered {(int)res.StatusCode}");
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
                return root.TryGetProperty("result", out var result) ? result.Clone() : default;
            var code = root.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? n : 0;
            throw new TuyaException(code, Explain(code, Str(root, "msg") ?? $"answered {(int)res.StatusCode}"));
        }
    }

    internal static string Sign(string id, string secret, string token, string t, string method, string url, string body)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{id}{token}{t}{method}\n{hash}\n\n{url}")));
    }

    private static string Explain(int code, string msg) => (code switch
    {
        1004 => "Tuya rejected the Access Secret",
        1013 => "Tuya rejected the request time: check this computer's clock",
        1106 => "Tuya denied access: check the data center, the linked Smart Life account and the project's API services",
        1114 => "this computer's IP address is not in the Tuya project's list",
        2007 => "the project does not use this data center",
        28841002 or 28841101 or 28841105 => "the Tuya project's IoT Core service is missing or expired: see iot.tuya.com",
        _ => "Tuya",
    }) + $": {msg} ({code})";

    private static string Message(Exception ex) => ex is TaskCanceledException ? "Tuya did not answer" : ex.Message;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
