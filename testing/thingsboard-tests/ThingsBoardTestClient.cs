// ThingsBoardTestClient.cs – Standalone ThingsBoard REST API client for integration tests
// NOT shared with the connector; this client validates the external REST API contract.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ThingsBoard.Tests;

/// <summary>
/// Lightweight ThingsBoard REST API client used exclusively by integration tests.
/// Handles authentication, token refresh, and basic CRUD operations for assets,
/// entity views, and alarms.
/// </summary>
public class ThingsBoardTestClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _adminEmail;
    private readonly string _adminPassword;

    private string? _jwt;
    private string? _refreshToken;
    private DateTime _jwtExpiry = DateTime.MinValue;

    public string BaseUrl => _http.BaseAddress!.ToString();

    public ThingsBoardTestClient(string baseUrl, string adminEmail, string adminPassword)
    {
        _adminEmail = adminEmail;
        _adminPassword = adminPassword;

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    #region Authentication

    /// <summary>
    /// Logs in to ThingsBoard and obtains JWT + refresh tokens.
    /// </summary>
    public async Task LoginAsync()
    {
        var resp = await _http.PostAsJsonAsync("/api/auth/login",
            new { username = _adminEmail, password = _adminPassword });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>()!;
        _jwt = body.GetProperty("token").GetString()!;
        _refreshToken = body.GetProperty("refreshToken").GetString()!;
        _jwtExpiry = DateTime.UtcNow.AddHours(2);
        ApplyJwt();
    }

    /// <summary>
    /// Refreshes the JWT if it is close to expiry.
    /// </summary>
    public async Task RefreshIfNeededAsync()
    {
        if (DateTime.UtcNow < _jwtExpiry - TimeSpan.FromMinutes(5))
            return;

        if (string.IsNullOrEmpty(_refreshToken))
            throw new InvalidOperationException("No refresh token available");

        var resp = await _http.PostAsJsonAsync("/api/auth/token",
            new { refreshToken = _refreshToken });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>()!;
        _jwt = body.GetProperty("token").GetString()!;
        _jwtExpiry = DateTime.UtcNow.AddHours(2);
        ApplyJwt();
    }

    private void ApplyJwt()
    {
        if (!string.IsNullOrEmpty(_jwt))
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _jwt);
    }

    #endregion

    #region Asset Operations

    /// <summary>
    /// Creates an asset via POST /api/asset. Returns the created asset JSON.
    /// </summary>
    public async Task<JsonElement> CreateAssetAsync(string name, string type, string? description = null)
    {
        var body = new Dictionary<string, object>
        {
            { "name", name },
            { "type", type },
            { "description", description ?? "" },
            { "publicAsset", false },
        };

        await RefreshIfNeededAsync();
        var resp = await _http.PostAsJsonAsync("/api/asset", body);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>()!;
    }

    /// <summary>
    /// Retrieves an asset by its UUID.
    /// </summary>
    public async Task<JsonElement?> GetAssetAsync(string assetId)
    {
        await RefreshIfNeededAsync();
        var resp = await _http.GetAsync($"/api/asset/{assetId}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Lists tenant assets with pagination support.
    /// </summary>
    public async Task<List<JsonElement>> ListTenantAssetsAsync(int pageSize = 20, int page = 0, string? textSearch = null)
    {
        await RefreshIfNeededAsync();
        var url = $"/api/tenant/assets?pageSize={pageSize}&page={page}";
        if (!string.IsNullOrEmpty(textSearch))
            url += $"&textSearch={Uri.EscapeDataString(textSearch)}";

        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>()!;
        var data = doc.GetProperty("data");
        var list = new List<JsonElement>();
        foreach (var item in data.GetProperty("content").EnumerateArray())
            list.Add(item.Clone());
        return list;
    }

    /// <summary>
    /// Deletes an asset by its UUID.
    /// </summary>
    public async Task DeleteAssetAsync(string assetId)
    {
        await RefreshIfNeededAsync();
        var resp = await _http.DeleteAsync($"/api/asset/{assetId}");
        // 204 or 404 are both acceptable (already deleted)
        if (resp.StatusCode != System.Net.HttpStatusCode.NotFound && !resp.IsSuccessStatusCode)
            resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Posts telemetry data to an asset.
    /// </summary>
    public async Task PostAssetTelemetryAsync(string assetId, Dictionary<string, object> values, long? ts = null)
    {
        await RefreshIfNeededAsync();
        var payload = new Dictionary<string, object>
        {
            { "ts", ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
            { "values", values },
        };
        var resp = await _http.PostAsJsonAsync(
            $"/api/plugins/telemetry/ASSET/{assetId}/timeseries/ANY", payload);
        // 200 or 204 = success
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.NoContent)
            throw new HttpRequestException(
                $"Failed to post telemetry to asset {assetId}: {(int)resp.StatusCode} {resp.ReasonPhrase}");
    }

    /// <summary>
    /// Retrieves telemetry data from an asset.
    /// </summary>
    public async Task<JsonElement?> GetAssetTelemetryAsync(string assetId, string key, long? startTs = null, long? endTs = null)
    {
        await RefreshIfNeededAsync();
        var url = $"/api/plugins/telemetry/ASSET/{assetId}/values/timeseries?keys={Uri.EscapeDataString(key)}";
        if (startTs.HasValue) url += $"&startTs={startTs.Value}";
        if (endTs.HasValue) url += $"&endTs={endTs.Value}";

        var resp = await _http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    #endregion

    #region Entity View Operations

    /// <summary>
    /// Creates an entity view. Returns the created entity view JSON.
    /// </summary>
    public async Task<JsonElement> CreateEntityViewAsync(
        string name, string type, string sourceEntityId, string sourceEntityType,
        string[]? telemetryKeys = null, string[]? serverAttributes = null, string? description = null)
    {
        await RefreshIfNeededAsync();

        var body = new
        {
            name,
            type,
            entityId = new { id = sourceEntityId, entityType = sourceEntityType },
            description = description ?? "",
            keys = new
            {
                timeseries = telemetryKeys ?? Array.Empty<string>(),
                attributes = new { cs = Array.Empty<string>(), ss = serverAttributes ?? Array.Empty<string>(), sh = Array.Empty<string>() },
            },
            startTimeMs = 0L,
            endTimeMs = 0L,
        };

        var resp = await _http.PostAsJsonAsync("/api/entityView", body);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>()!;
    }

    /// <summary>
    /// Retrieves an entity view by its UUID.
    /// </summary>
    public async Task<JsonElement?> GetEntityViewAsync(string entityViewId)
    {
        await RefreshIfNeededAsync();
        var resp = await _http.GetAsync($"/api/entityView/{entityViewId}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Lists entity views for the tenant.
    /// </summary>
    public async Task<List<JsonElement>> ListTenantEntityViewsAsync(int pageSize = 20, int page = 0, string? textSearch = null)
    {
        await RefreshIfNeededAsync();
        var url = $"/api/tenant/entityViews?pageSize={pageSize}&page={page}";
        if (!string.IsNullOrEmpty(textSearch))
            url += $"&textSearch={Uri.EscapeDataString(textSearch!)}";

        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>()!;
        var list = new List<JsonElement>();
        foreach (var item in doc.GetProperty("data").GetProperty("content").EnumerateArray())
            list.Add(item.Clone());
        return list;
    }

    /// <summary>
    /// Deletes an entity view by its UUID.
    /// </summary>
    public async Task DeleteEntityViewAsync(string entityViewId)
    {
        await RefreshIfNeededAsync();
        var resp = await _http.DeleteAsync($"/api/entityView/{entityViewId}");
        if (resp.StatusCode != System.Net.HttpStatusCode.NotFound && !resp.IsSuccessStatusCode)
            resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Sets server-scope attributes on an entity view.
    /// </summary>
    public async Task SetEntityViewAttributesAsync(string entityViewId, Dictionary<string, string> attrs)
    {
        await RefreshIfNeededAsync();
        var resp = await _http.PostAsJsonAsync(
            $"/api/plugins/telemetry/ENTITY_VIEW/{entityViewId}/SERVER_SCOPE", attrs);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Gets attributes from an entity view.
    /// </summary>
    public async Task<JsonElement?> GetEntityViewAttributesAsync(string entityViewId, string scope = "SERVER_SCOPE")
    {
        await RefreshIfNeededAsync();
        var resp = await _http.GetAsync(
            $"/api/plugins/telemetry/ENTITY_VIEW/{entityViewId}/{scope}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    #endregion

    #region Alarm Operations

    /// <summary>
    /// Creates an alarm. Returns the created alarm JSON.
    /// </summary>
    public async Task<JsonElement> CreateAlarmAsync(
        string originatorId, string originatorType, string type,
        string severity, string message, object? details = null)
    {
        await RefreshIfNeededAsync();

        var detailsDict = details as Dictionary<string, object>
            ?? new Dictionary<string, object>();
        if (!detailsDict.ContainsKey("message"))
            detailsDict["message"] = message;

        var alarm = new
        {
            originator = new { id = originatorId, entityType = originatorType },
            type,
            severity,
            status = "ACTIVE_UNACK",
            details = detailsDict,
        };

        var resp = await _http.PostAsJsonAsync("/api/alarm", alarm);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<JsonElement>()!;
    }

    /// <summary>
    /// Gets an alarm by its UUID.
    /// </summary>
    public async Task<JsonElement?> GetAlarmAsync(string alarmId)
    {
        await RefreshIfNeededAsync();
        var resp = await _http.GetAsync($"/api/alarm/{alarmId}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>
    /// Gets alarms filtered by type for a specific entity.
    /// </summary>
    public async Task<List<JsonElement>> GetAlarmsByTypeAsync(string entityId, string entityType, string alarmType, int pageSize = 100, int page = 0)
    {
        await RefreshIfNeededAsync();
        var url = $"/api/alarm?type={Uri.EscapeDataString(alarmType)}&pageSize={pageSize}&page={page}&originatorId={entityId}&originatorType={entityType}";
        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>()!;
        var list = new List<JsonElement>();
        foreach (var item in doc.GetProperty("data").GetProperty("content").EnumerateArray())
            list.Add(item.Clone());
        return list;
    }

    /// <summary>
    /// Lists active (unacknowledged) alarms for an entity.
    /// </summary>
    public async Task<List<JsonElement>> GetActiveAlarmsAsync(string entityId, string entityType = "DEVICE", int pageSize = 100, int page = 0)
    {
        await RefreshIfNeededAsync();
        var url = $"/api/alarm?originatorId={entityId}&originatorType={entityType}&pageSize={pageSize}&page={page}&status=ACTIVE_UNACK";
        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>()!;
        var list = new List<JsonElement>();
        if (doc.TryGetProperty("data", out var data))
        {
            foreach (var item in data.GetProperty("content").EnumerateArray())
                list.Add(item.Clone());
        }
        return list;
    }

    /// <summary>
    /// Acknowledges an alarm by its UUID.
    /// </summary>
    public async Task AcknowledgeAlarmAsync(string alarmId, string? comment = null)
    {
        await RefreshIfNeededAsync();
        var body = new { comment = comment ?? "" };
        var resp = await _http.PostAsJsonAsync($"/api/alarm/{alarmId}/ack", body);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Clears an alarm by its UUID.
    /// </summary>
    public async Task<bool> ClearAlarmAsync(string alarmId)
    {
        await RefreshIfNeededAsync();
        var resp = await _http.PostAsync($"/api/alarm/{alarmId}/clear", null);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Clears and acknowledges an alarm.
    /// </summary>
    public async Task ClearAlarmWithCommentAsync(string alarmId, string comment)
    {
        await RefreshIfNeededAsync();
        var body = new { comment };
        var resp = await _http.PostAsJsonAsync($"/api/alarm/{alarmId}/clear", body);
        resp.EnsureSuccessStatusCode();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Gets the short UUID (first 8 hex chars) from an entity's JSON representation.
    /// </summary>
    public static string GetShortId(JsonElement entity)
    {
        return entity.GetProperty("id").GetProperty("id").GetString()!.Substring(0, 8);
    }

    /// <summary>
    /// Retries an async operation with exponential backoff.
    /// </summary>
    public static async Task<T> RetryAsync<T>(Func<Task<T>> operation, int maxRetries = 5, TimeSpan? initialDelay = null, CancellationToken cancellationToken = default)
    {
        var delay = initialDelay ?? TimeSpan.FromMilliseconds(500);
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                return await operation();
            }
            catch (Exception) when (i < maxRetries - 1)
            {
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromTicks(delay.Ticks * 2);
            }
        }
        return await operation(); // Final attempt, no catch
    }

    public void Dispose()
    {
        _http?.Dispose();
    }

    #endregion
}
