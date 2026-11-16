using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ipx2Mqtt.Ipx;

/// <summary>
/// REST access to the IPX800 V5: secure login (AuthToken), full state snapshot, commands.
/// If an ApiKey is configured it is used for REST calls instead of a login token.
/// </summary>
public sealed class IpxHttpApi
{
    private readonly HttpClient _http;
    private readonly IpxOptions _options;
    private readonly IpxDiagnostics _diagnostics;
    private readonly ILogger<IpxHttpApi> _logger;
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private string? _token;

    public IpxHttpApi(HttpClient http, IOptions<IpxOptions> options, IpxDiagnostics diagnostics, ILogger<IpxHttpApi> logger)
    {
        _http = http;
        _options = options.Value;
        _diagnostics = diagnostics;
        _logger = logger;

        var baseUrl = _options.BaseUrl.TrimEnd('/') + "/";
        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds));
    }

    private bool UseApiKey => !string.IsNullOrWhiteSpace(_options.ApiKey);

    /// <summary>Returns a login token, logging in when needed (or when <paramref name="forceRefresh"/>).</summary>
    public async Task<string> GetAuthTokenAsync(CancellationToken ct, bool forceRefresh = false)
    {
        await _loginGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _token is not null)
            {
                return _token;
            }

            _token = await LoginAsync(ct).ConfigureAwait(false);
            return _token;
        }
        finally
        {
            _loginGate.Release();
        }
    }

    /// <summary>ws://host/websocket/dashboard/open?AuthToken=... (wss for https).</summary>
    public Uri BuildWebSocketUri(string token)
    {
        var builder = new UriBuilder(_http.BaseAddress!)
        {
            Scheme = _http.BaseAddress!.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = "websocket/dashboard/open",
            Query = "AuthToken=" + Uri.EscapeDataString(token),
        };
        return builder.Uri;
    }

    /// <summary>Reads all io and ana objects (relays, inputs, analog values...).</summary>
    public async Task<IReadOnlyList<IpxObject>> GetAllAsync(CancellationToken ct)
    {
        var io = await SendAsync(HttpMethod.Get, "api/core/io", null, ct).ConfigureAwait(false);
        var ana = await SendAsync(HttpMethod.Get, "api/core/ana", null, ct).ConfigureAwait(false);

        var result = new List<IpxObject>();
        result.AddRange(IpxStateParser.ParseIoObjects(io, _diagnostics, _logger));
        result.AddRange(IpxStateParser.ParseAnaObjects(ana, _diagnostics, _logger));
        return result;
    }

    /// <summary>
    /// Lists every io/ana object (name and owning extension) and the extensions that carry analog outputs.
    /// Extension kinds missing from the firmware (404) are skipped.
    /// </summary>
    public async Task<Mapping.IpxInventory> GetInventoryAsync(CancellationToken ct)
    {
        var objects = new List<Mapping.IpxListedObject>();
        foreach (var (path, isAna) in new[] { ("api/core/io", false), ("api/core/ana", true) })
        {
            var array = await SendAsync(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
            if (array.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var e in array.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("_id", out var id) && id.TryGetInt64(out var idValue))
                {
                    var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                    var link = e.TryGetProperty("link1", out var l) && l.TryGetInt64(out var lv) ? lv : 0;
                    var link0 = e.TryGetProperty("link0", out var l0) && l0.TryGetInt64(out var l0v) ? l0v : 0;
                    objects.Add(new Mapping.IpxListedObject(idValue, name, link, isAna, link0));
                }
            }
        }

        var extensions = new List<Mapping.IpxExtension>();
        foreach (var kind in Mapping.IpxEntityDiscovery.AnalogOutputKinds)
        {
            try
            {
                var list = await SendAsync(HttpMethod.Get, $"api/ebx/{kind}", null, ct).ConfigureAwait(false);
                if (list.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var e in list.EnumerateArray())
                {
                    if (e.TryGetProperty("_id", out var id) && id.TryGetInt64(out var idValue))
                    {
                        var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? kind : kind;
                        extensions.Add(new Mapping.IpxExtension(kind, idValue, name));
                    }
                }
            }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound)
            {
                _logger.LogDebug("IPX has no {Kind} extension endpoint", kind);
            }
        }

        return new Mapping.IpxInventory(objects, extensions);
    }

    /// <summary>PUT /api/core/io/{id} {"on":bool} or PUT /api/core/ana/{id} {"value":n}.</summary>
    public async Task SetAsync(long id, object value, CancellationToken ct)
    {
        var kind = IpxIdRanges.KindOf(id) ?? throw new ArgumentException($"Unknown IPX object id {id}.", nameof(id));
        switch (kind, value)
        {
            case (IpxObjectKind.Io, bool on):
                await SendAsync(HttpMethod.Put, $"api/core/io/{id}", new { on }, ct).ConfigureAwait(false);
                break;
            case (IpxObjectKind.Ana, double or int or long):
                await SendAsync(HttpMethod.Put, $"api/core/ana/{id}", new { value = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture) }, ct)
                    .ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"Cannot write a {value.GetType().Name} to {kind} object {id}.", nameof(value));
        }
    }

    /// <summary>
    /// Version information exposed by the documented API (/api/system/ipx/info: apiVersion, objVersion).
    /// The API does not document a firmware version endpoint.
    /// </summary>
    public async Task<string?> GetSystemInfoAsync(CancellationToken ct)
    {
        var info = await SendAsync(HttpMethod.Get, "api/system/ipx/info", null, ct).ConfigureAwait(false);
        var api = info.TryGetProperty("apiVersion", out var a) ? a.ToString() : "?";
        var obj = info.TryGetProperty("objVersion", out var o) ? o.ToString() : "?";
        return $"apiVersion={api}, objVersion={obj}";
    }

    private async Task<string> LoginAsync(CancellationToken ct)
    {
        var login = Uri.EscapeDataString(_options.Login);

        using var keyResponse = await _http.PostAsync($"api/auth/secure/{login}", new StringContent(string.Empty), ct).ConfigureAwait(false);
        keyResponse.EnsureSuccessStatusCode();
        var keyJson = await keyResponse.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        var secureKey = keyJson.TryGetProperty("secureKey", out var k) ? k.GetString() : null;
        if (string.IsNullOrEmpty(secureKey))
        {
            throw new InvalidOperationException("IPX secure login: no secureKey in response.");
        }

        var secureContent = IpxCrypto.EncryptLogin(_options.Login, _options.Password, secureKey);
        using var loginResponse = await _http.PostAsJsonAsync($"api/auth/login/{login}?secure=true", new { secureContent }, ct).ConfigureAwait(false);
        loginResponse.EnsureSuccessStatusCode();
        var loginJson = await loginResponse.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
        var token = loginJson.TryGetProperty("AuthToken", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("IPX secure login failed: no AuthToken in response (wrong login or password?).");
        }

        _logger.LogInformation("Logged in to the IPX as '{Login}'", _options.Login);
        return token;
    }

    private async Task<JsonElement> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var auth = UseApiKey
                ? "ApiKey=" + Uri.EscapeDataString(_options.ApiKey!)
                : "AuthToken=" + Uri.EscapeDataString(await GetAuthTokenAsync(ct, forceRefresh: attempt > 0).ConfigureAwait(false));

            using var request = new HttpRequestMessage(method, $"{path}{(path.Contains('?') ? '&' : '?')}{auth}");
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);

            if (!UseApiKey && attempt == 0 && response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                _logger.LogDebug("IPX answered {Status} to {Method} {Path}; logging in again", (int)response.StatusCode, method, path);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException($"IPX {method} {path} failed with {(int)response.StatusCode}: {text}", null, response.StatusCode);
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return doc.RootElement.Clone();
        }
    }
}
