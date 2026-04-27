// ThingsBoardApi.cs – ThingsBoard REST API helper (login, provision, JWT refresh)
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Web;

namespace Connector
{
    class ThingsBoardApi
    {
        readonly HttpClient _http;
        readonly TbConfig   _cfg;
        string   _jwt          = "";
        string   _refreshToken = "";
        DateTime _jwtExpiry    = DateTime.MinValue;

        public ThingsBoardApi(TbConfig cfg)
        {
            _cfg  = cfg;
            _http = new HttpClient
            {
                BaseAddress = new Uri($"http://{cfg.Host}:{cfg.HttpPort}"),
                Timeout     = TimeSpan.FromSeconds(15),
            };
        }

        // ── Auth ──────────────────────────────────────────────────────────────

        public async Task LoginAsync()
        {
            Console.WriteLine($"  TB REST → logging in as {_cfg.AdminEmail}…");
            var resp = await _http.PostAsJsonAsync("/api/auth/login",
                           new { username = _cfg.AdminEmail, password = _cfg.AdminPassword });
            resp.EnsureSuccessStatusCode();

            var body      = await resp.Content.ReadFromJsonAsync<JsonElement>();
            _jwt          = body.GetProperty("token").GetString()!;
            _refreshToken = body.GetProperty("refreshToken").GetString()!;
            _jwtExpiry    = DateTime.UtcNow.AddHours(2);
            ApplyJwt();
            Console.WriteLine("  ✓ JWT obtained.");
        }

        public async Task RefreshIfNeededAsync()
        {
            if (DateTime.UtcNow < _jwtExpiry - TimeSpan.FromMinutes(5)) return;

            Console.WriteLine("  Refreshing ThingsBoard JWT…");
            var resp = await _http.PostAsJsonAsync("/api/auth/token",
                           new { refreshToken = _refreshToken });
            resp.EnsureSuccessStatusCode();

            var body   = await resp.Content.ReadFromJsonAsync<JsonElement>();
            _jwt       = body.GetProperty("token").GetString()!;
            _jwtExpiry = DateTime.UtcNow.AddHours(2);
            ApplyJwt();
            Console.WriteLine("  ✓ JWT refreshed.");
        }

        void ApplyJwt() =>
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _jwt);

        // ── Device provisioning ───────────────────────────────────────────────

        /// <summary>Creates the device if it doesn't exist yet, then (re-)applies the access token. Idempotent.</summary>
        public async Task EnsureDeviceAsync(string deviceName, string tbDeviceType, string accessToken)
        {
            string? deviceId = await FindDeviceIdInternalAsync(deviceName);

            if (deviceId is null)
            {
                var resp = await _http.PostAsJsonAsync("/api/device", new
                {
                    name = deviceName,
                    type = tbDeviceType,
                });
                resp.EnsureSuccessStatusCode();

                var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
                deviceId = body.GetProperty("id").GetProperty("id").GetString()!;
                Console.WriteLine($"  [TB] Created  → {deviceName}  ({tbDeviceType})  token={accessToken}");
            }
            else
            {
                Console.WriteLine($"  [TB] Existing → {deviceName}");
            }

            await SetAccessTokenAsync(deviceId, accessToken);
        }

        async Task<string?> FindDeviceIdInternalAsync(string name)
        {
            var resp = await _http.GetAsync(
                $"/api/tenant/devices?pageSize=10&page=0&textSearch={HttpUtility.UrlEncode(name)}");
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var d in body.GetProperty("data").EnumerateArray())
                if (d.GetProperty("name").GetString() == name)
                    return d.GetProperty("id").GetProperty("id").GetString();

            return null;
        }

        /// <summary>Returns the UUID of a ThingsBoard Device by its name. Returns null when not found.</summary>
        public Task<string?> FindDeviceIdAsync(string deviceName)
            => FindDeviceIdInternalAsync(deviceName);

        async Task SetAccessTokenAsync(string deviceId, string accessToken)
        {
            var getResp = await _http.GetAsync($"/api/device/{deviceId}/credentials");
            getResp.EnsureSuccessStatusCode();
            var existing = await getResp.Content.ReadFromJsonAsync<JsonElement>();
            string credId = existing.GetProperty("id").GetProperty("id").GetString()!;

            var putResp = await _http.PostAsJsonAsync("/api/device/credentials", new
            {
                id              = new { id = credId,   entityType = "DEVICE_CREDENTIALS" },
                deviceId        = new { id = deviceId, entityType = "DEVICE" },
                credentialsType = "ACCESS_TOKEN",
                credentialsId   = accessToken,
            });
            putResp.EnsureSuccessStatusCode();
        }

        // ── Asset management ──────────────────────────────────────────────────

        /// <summary>
        /// Creates a ThingsBoard Asset with the given name and type if it does not exist yet.
        /// Returns the asset UUID.  Idempotent.
        /// </summary>
        public async Task<string> EnsureAssetAsync(string assetName, string assetType)
        {
            string? existing = await FindAssetIdAsync(assetName);
            if (existing is not null)
                return existing;

            var resp = await _http.PostAsJsonAsync("/api/asset", new
            {
                name = assetName,
                type = assetType,
            });
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            string id = body.GetProperty("id").GetProperty("id").GetString()!;
            Console.WriteLine($"  [TB] Asset created  → {assetName}  ({assetType})");
            return id;
        }

        async Task<string?> FindAssetIdAsync(string name)
        {
            // textSearch does a prefix/contains match; we verify exact name in the results
            var resp = await _http.GetAsync(
                $"/api/tenant/assets?pageSize=20&page=0&textSearch={System.Web.HttpUtility.UrlEncode(name)}");
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var a in body.GetProperty("data").EnumerateArray())
                if (a.GetProperty("name").GetString() == name)
                    return a.GetProperty("id").GetProperty("id").GetString();

            return null;
        }



        // ── Relations ─────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a directed relation between two entities if it does not exist yet.
        /// Idempotent.  Typical usage: "Contains" from a parent Asset to a child Asset or Device.
        /// </summary>
        public async Task EnsureRelationAsync(
            string fromId, string fromType,
            string toId,   string toType,
            string relationType = "Contains")
        {
            // Check whether the relation already exists
            var checkResp = await _http.GetAsync(
                $"/api/relations?fromId={fromId}&fromType={fromType}&relationType={System.Web.HttpUtility.UrlEncode(relationType)}&relationTypeGroup=COMMON");

            if (checkResp.IsSuccessStatusCode)
            {
                var existing = await checkResp.Content.ReadFromJsonAsync<JsonElement>();
                foreach (var rel in existing.EnumerateArray())
                {
                    var to = rel.GetProperty("to");
                    if (to.GetProperty("id").GetString()   == toId &&
                        to.GetProperty("entityType").GetString() == toType)
                        return; // already exists
                }
            }

            var resp = await _http.PostAsJsonAsync("/api/relation", new
            {
                from          = new { id = fromId, entityType = fromType },
                to            = new { id = toId,   entityType = toType   },
                type          = relationType,
                typeGroup     = "COMMON",
            });
            resp.EnsureSuccessStatusCode();
        }

        // ── Asset attributes ──────────────────────────────────────────────────

        /// <summary>
        /// Posts a flat key-value dictionary as SERVER_SCOPE attributes on an Asset.
        /// </summary>
        public async Task SetAssetAttributesAsync(string assetId, Dictionary<string, string> attrs)
        {
            var resp = await _http.PostAsJsonAsync(
                $"/api/plugins/telemetry/ASSET/{assetId}/SERVER_SCOPE", attrs);
            resp.EnsureSuccessStatusCode();
        }

        /// <summary>
        /// Posts a single numeric value as a timestamped time-series data point on an Asset.
        /// Uses the ThingsBoard telemetry API: POST /api/plugins/telemetry/ASSET/{id}/timeseries/ANY
        /// </summary>
        public async Task PostAssetTelemetryAsync(string assetId, string key, double value)
        {
            var payload = new
            {
                ts     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                values = new Dictionary<string, double> { [key] = value },
            };
            var resp = await _http.PostAsJsonAsync(
                $"/api/plugins/telemetry/ASSET/{assetId}/timeseries/ANY", payload);
            // 200 or 204 both indicate success; swallow non-critical failures
            if (!resp.IsSuccessStatusCode)
                Console.Error.WriteLine(
                    $"  [TB] WARN: asset telemetry POST failed ({(int)resp.StatusCode}) for asset {assetId} key={key}");
        }

        /// <summary>
        /// Posts a batch of key-value telemetry values to an Asset in a single request.
        /// </summary>
        public async Task PostAssetTelemetryBatchAsync(string assetId, Dictionary<string, double> values)
        {
            if (values.Count == 0) return;
            var payload = new
            {
                ts     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                values,
            };
            var resp = await _http.PostAsJsonAsync(
                $"/api/plugins/telemetry/ASSET/{assetId}/timeseries/ANY", payload);
            if (!resp.IsSuccessStatusCode)
                Console.Error.WriteLine(
                    $"  [TB] WARN: asset telemetry batch POST failed ({(int)resp.StatusCode}) for asset {assetId}");
        }

        // ── Entity Views ──────────────────────────────────────────────────────

        /// <summary>
        /// Creates (or finds) a ThingsBoard Entity View for the given source entity.
        /// The view exposes <paramref name="telemetryKeys"/> as time-series and
        /// <paramref name="serverAttributes"/> as server-scope attributes.
        /// Idempotent: returns the existing UUID when an Entity View with the same name exists.
        /// </summary>
        public async Task<string> EnsureEntityViewAsync(
            string   viewName,
            string   viewType,
            string   sourceEntityId,
            string   sourceEntityType,
            string[] telemetryKeys,
            string[] serverAttributes)
        {
            string? existing = await FindEntityViewIdAsync(viewName);
            if (existing is not null)
                return existing;

            var body = new
            {
                name     = viewName,
                type     = viewType,
                entityId = new { id = sourceEntityId, entityType = sourceEntityType },
                keys     = new
                {
                    timeseries = telemetryKeys,
                    attributes = new
                    {
                        cs = Array.Empty<string>(),
                        ss = serverAttributes,
                        sh = Array.Empty<string>(),
                    },
                },
                startTimeMs = 0L,
                endTimeMs   = 0L,
            };

            var resp = await _http.PostAsJsonAsync("/api/entityView", body);
            resp.EnsureSuccessStatusCode();

            var result = await resp.Content.ReadFromJsonAsync<JsonElement>();
            string id  = result.GetProperty("id").GetProperty("id").GetString()!;
            Console.WriteLine($"  [TB] EntityView created → {viewName}");
            return id;
        }

        async Task<string?> FindEntityViewIdAsync(string name)
        {
            var resp = await _http.GetAsync(
                $"/api/tenant/entityViews?pageSize=20&page=0&textSearch={System.Web.HttpUtility.UrlEncode(name)}");
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
            foreach (var ev in body.GetProperty("data").EnumerateArray())
                if (ev.GetProperty("name").GetString() == name)
                    return ev.GetProperty("id").GetProperty("id").GetString();

            return null;
        }
    }
}
