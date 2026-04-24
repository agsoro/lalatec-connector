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
            string? deviceId = await FindDeviceIdAsync(deviceName);

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

        async Task<string?> FindDeviceIdAsync(string name)
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

        async Task SetAccessTokenAsync(string deviceId, string accessToken)
        {
            var getResp = await _http.GetAsync($"/api/device/{deviceId}/credentials");
            getResp.EnsureSuccessStatusCode();
            var existing = await getResp.Content.ReadFromJsonAsync<JsonElement>();
            string credId = existing.GetProperty("id").GetProperty("id").GetString()!;

            var putResp = await _http.PutAsJsonAsync("/api/device/credentials", new
            {
                id              = new { id = credId,   entityType = "DEVICE_CREDENTIALS" },
                deviceId        = new { id = deviceId, entityType = "DEVICE" },
                credentialsType = "ACCESS_TOKEN",
                credentialsId   = accessToken,
            });
            putResp.EnsureSuccessStatusCode();
        }
    }
}
