// MonitoringServer.cs – Embedded Kestrel server + single-page dashboard UI
// The dashboard HTML is served from wwwroot/dashboard.html as an embedded resource.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Connector
{
    /// <summary>
    /// Embedded Kestrel server that serves a read-only monitoring dashboard.
    /// No authentication, no security hardening. Intended for controlled/internal use only.
    /// </summary>
    internal sealed class MonitoringServer : IDisposable
    {
        private readonly WebApplication _app;
        private readonly LogBuffer _logBuffer;
        private readonly AppConfig _config;
        private readonly ThingsBoardApi? _tbApi;
        private readonly HashSet<string> _offlineDevices;
        private readonly Dictionary<string, DateTime> _lastPolledAtMap;
        private readonly string _version;
        private readonly Stopwatch _uptime;
        private readonly string _dashboardHtml;

        // Cache for ThingsBoard alarms
        private List<AlarmDto>? _cachedAlarms;
        private DateTime _alarmCacheTime = DateTime.MinValue;
        private readonly object _alarmLock = new object();

        // Cache for device status
        private DeviceStatusDto? _cachedStatus;
        private DateTime _statusCacheTime = DateTime.MinValue;
        private readonly object _statusLock = new object();

        internal MonitoringServer(
            LogBuffer logBuffer,
            AppConfig config,
            ThingsBoardApi? tbApi,
            HashSet<string> offlineDevices,
            Dictionary<string, DateTime> lastPolledAtByName,
            int port = 5000)
        {
            _logBuffer = logBuffer;
            _config = config;
            _tbApi = tbApi;
            _offlineDevices = offlineDevices;
            _lastPolledAtMap = lastPolledAtByName;
            _version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            _uptime = Stopwatch.StartNew();

            // Load dashboard HTML from embedded resource
            _dashboardHtml = LoadEmbeddedDashboardHtml();

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions());
            builder.Services.AddSingleton<IStartupFilter>(new RequestLoggingStartupFilter());
            builder.Services.AddSingleton(_logBuffer);
            builder.Services.AddSingleton(config);

            _app = builder.Build();

            ConfigureMiddleware();
            ConfigureRoutes();
        }

        private string LoadEmbeddedDashboardHtml()
        {
            try
            {
                // Try loading from wwwroot first (development)
                var exePath = AppContext.BaseDirectory;
                var wwwRootPath = Path.Combine(exePath, "wwwroot", "dashboard.html");
                if (File.Exists(wwwRootPath))
                    return File.ReadAllText(wwwRootPath);

                // Fallback: try embedded resource
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("Connector.wwwroot.dashboard.html");
                if (stream != null)
                {
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                // Swallow - will use fallback below
            }

            // Ultimate fallback
            return "<html><body><h1>Dashboard unavailable</h1></body></html>";
        }

        internal void UpdateOfflineDevices(HashSet<string> offlineDevices)
        {
            lock (offlineDevices) { /* reference stays the same */ }
        }

        internal void UpdateLastPolledAt(Dictionary<string, DateTime> lastPolledAt)
        {
            lock (lastPolledAt) { }
        }

        private void ConfigureMiddleware()
        {
            // No auth, no security headers needed per requirements
            _app.UseExceptionHandler();
            _app.UseRouting();
        }

        private void ConfigureRoutes()
        {
            var logBuffer = _logBuffer;

            // ── Main Dashboard Page ─────────────────────────────────
            _app.MapGet("/", (HttpContext ctx) =>
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
                ctx.Response.Headers.Pragma = "no-cache";
                return Results.Text(_dashboardHtml, "text/html; charset=utf-8");
            });

            // ── API: System Status Overview ─────────────────────────
            _app.MapGet("/api/status", async (HttpContext ctx) =>
            {
                lock (_statusLock)
                {
                    if (_cachedStatus != null && DateTime.UtcNow - _statusCacheTime < TimeSpan.FromSeconds(10))
                    {
                        ctx.Response.ContentType = "application/json";
                        return Results.Json(_cachedStatus, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                    }
                }

                int totalDevices = _config.Devices.Count;
                int offlineCount = 0;
                lock (_offlineDevices) offlineCount = _offlineDevices.Count;
                int onlineDevices = totalDevices - offlineCount;

                int alarmCount = 0;
                try
                {
                    var alarms = await GetCachedAlarmsAsync();
                    alarmCount = alarms?.Count ?? 0;
                }
                catch
                {
                    alarmCount = -1;
                }

                var status = new DeviceStatusDto
                {
                    TotalDevices = totalDevices,
                    OnlineDevices = onlineDevices,
                    OfflineDevices = offlineCount,
                    ActiveAlarms = alarmCount,
                    ConnectorVersion = _version,
                    UptimeSeconds = (long)_uptime.Elapsed.TotalSeconds,
                    LogBufferSize = logBuffer.Count,
                    LogBufferCapacity = logBuffer.Capacity,
                    Timestamp = DateTime.UtcNow.ToString("o")
                };

                lock (_statusLock)
                {
                    _cachedStatus = status;
                    _statusCacheTime = DateTime.UtcNow;
                }

                ctx.Response.ContentType = "application/json";
                return Results.Json(status, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            });

            // ── API: Device List ────────────────────────────────────
            _app.MapGet("/api/devices", (HttpContext ctx) =>
            {
                var devices = new List<DeviceDto>();

                foreach (var d in _config.Devices)
                {
                    bool isOffline = false;
                    lock (_offlineDevices)
                        isOffline = _offlineDevices.Contains(d.Name);

                    DateTime lastPolled = DateTime.MinValue;
                    lock (_lastPolledAtMap)
                        _lastPolledAtMap.TryGetValue(d.Name, out lastPolled);

                    var lastSeen = isOffline ? DateTime.MinValue : lastPolled;
                    string status = isOffline ? "offline" : "online";
                    string color = isOffline ? "#dc3545" : "#28a745";

                    var connCfg = _config.Connections.FirstOrDefault(c => c.Id == d.ConnectionId);
                    devices.Add(new DeviceDto
                    {
                        Name = d.Name,
                        Type = d.DeviceType,
                        ConnectionId = d.ConnectionId,
                        Status = status,
                        StatusColor = color,
                        LastSeen = lastSeen == DateTime.MinValue ? "Never" : lastSeen.ToString("yyyy-MM-dd HH:mm:ss UTC"),
                        Connection = connCfg?.Host ?? "unknown",
                        Port = connCfg?.Port ?? 0
                    });
                }

                ctx.Response.ContentType = "application/json";
                return Results.Json(devices, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            });

            // ── API: Logs ───────────────────────────────────────────
            _app.MapGet("/api/logs", (HttpContext ctx) =>
            {
                int count = 200;
                var qs = ctx.Request.QueryString;
                if (qs.HasValue)
                {
                    // Simple query string parser (avoids System.Web.HttpUtility
                    // which is not available in .NET 8 without an extra package).
                    var raw = qs.Value.TrimStart('?');
                    foreach (var pair in raw.Split('&'))
                    {
                        var eqIdx = pair.IndexOf('=');
                        if (eqIdx < 0) continue;
                        var key = pair.Substring(0, eqIdx);
                        var val = eqIdx + 1 < pair.Length ? pair.Substring(eqIdx + 1) : "";
                        if (string.Equals(key, "count", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(val, out int c) && c > 0)
                                count = Math.Min(c, 5000);
                            break;
                        }
                    }
                }

                var logs = logBuffer.GetLatest(count).Select(l => new LogEntryDto
                {
                    Timestamp = l.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Severity = l.Severity.ToString().ToLowerInvariant(),
                    Message = l.Message,
                    Source = l.Source
                }).ToList();

                ctx.Response.ContentType = "application/json";
                return Results.Json(logs, options: new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                });
            });

            // ── API: Active Alarms ──────────────────────────────────
            _app.MapGet("/api/alarm", async (HttpContext ctx) =>
            {
                var alarms = await GetCachedAlarmsAsync() ?? new List<AlarmDto>();

                var dtos = alarms.Select(a => new AlarmDisplayDto
                {
                    Type = a.type,
                    Severity = a.severity,
                    Status = a.status,
                    Message = a.details is JsonElement det && det.TryGetProperty("message", out var m)
                        ? m.GetString() ?? ""
                        : "",
                    Originator = a.originator,
                    Time = a.created
                }).ToList();

                ctx.Response.ContentType = "application/json";
                return Results.Json(dtos, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            });

            // ── API: Config ─────────────────────────────────────────
            _app.MapGet("/api/config", (HttpContext ctx) =>
            {
                int intervalSec = _config.Polling?.IntervalSeconds ?? 30;
                return Results.Json(new
                {
                    pollingIntervalSeconds = intervalSec,
                    logBufferSize = _logBuffer.Capacity,
                    monitoringPort = ctx.Connection.LocalPort.ToString()
                }, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            });
        }

        private async Task<List<AlarmDto>?> GetCachedAlarmsAsync()
        {
            if (_tbApi == null) return new List<AlarmDto>();

            lock (_alarmLock)
            {
                if (_cachedAlarms != null && DateTime.UtcNow - _alarmCacheTime < TimeSpan.FromSeconds(10))
                    return _cachedAlarms;
            }

            try
            {
                var allAlarms = new List<AlarmDto>();

                foreach (var d in _config.Devices)
                {
                    var tbDeviceId = await _tbApi.FindDeviceIdAsync(d.Name);
                    if (tbDeviceId != null)
                    {
                        try
                        {
                            var deviceAlarms = await _tbApi.GetActiveAlarmsAsync(tbDeviceId);
                            foreach (var alarm in deviceAlarms)
                            {
                                var dto = new AlarmDto
                                {
                                    type = alarm.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                                    severity = alarm.TryGetProperty("severity", out var s) ? s.GetString() ?? "" : "",
                                    status = alarm.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                                    created = alarm.TryGetProperty("created", out var cr) ? cr.ToString() : "",
                                    originator = alarm.TryGetProperty("originator", out var o) && o.TryGetProperty("name", out var n)
                                        ? n.GetString() ?? ""
                                        : d.Name,
                                    details = alarm.TryGetProperty("details", out var det) ? det : default
                                };
                                allAlarms.Add(dto);
                            }
                        }
                        catch { /* Ignore per-device alarm fetch errors */ }
                    }
                }

                lock (_alarmLock)
                {
                    _cachedAlarms = allAlarms;
                    _alarmCacheTime = DateTime.UtcNow;
                }

                return _cachedAlarms;
            }
            catch
            {
                return new List<AlarmDto>();
            }
        }

        /// <summary>
        /// Starts the Kestrel server on the configured port.
        /// </summary>
        internal async Task RunAsync(CancellationToken cancellationToken)
        {
            var port = _config.Monitoring?.Port ?? 5000;
            _app.Urls.Clear();
            _app.Urls.Add($"http://+:{port}");

            Console.WriteLine($"  [Monitoring] Dashboard starting on port {port}...");
            await _app.RunAsync();
        }

        public void Dispose()
        {
            // WebApplication in ASP.NET Core 8 doesn't have a public Dispose.
            // We rely on the hosting lifetime to shut it down.
            _uptime?.Stop();
        }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────

    public class DeviceStatusDto
    {
        [JsonPropertyName("totalDevices")]      public int    TotalDevices;
        [JsonPropertyName("onlineDevices")]     public int    OnlineDevices;
        [JsonPropertyName("offlineDevices")]    public int    OfflineDevices;
        [JsonPropertyName("activeAlarms")]      public int    ActiveAlarms;
        [JsonPropertyName("connectorVersion")]  public string ConnectorVersion = "";
        [JsonPropertyName("uptimeSeconds")]     public long   UptimeSeconds;
        [JsonPropertyName("logBufferSize")]     public int    LogBufferSize;
        [JsonPropertyName("logBufferCapacity")] public int    LogBufferCapacity;
        [JsonPropertyName("timestamp")]         public string Timestamp = "";
    }

    public class DeviceDto
    {
        [JsonPropertyName("name")]         public string Name = "";
        [JsonPropertyName("type")]         public string Type = "";
        [JsonPropertyName("status")]       public string Status = "";
        [JsonPropertyName("statusColor")]  public string StatusColor = "";
        [JsonPropertyName("lastSeen")]     public string LastSeen = "";
        [JsonPropertyName("connection")]   public string Connection = "";
        [JsonPropertyName("port")]         public int    Port;
        [JsonPropertyName("connectionId")] public string ConnectionId = "";
    }

    public class LogEntryDto
    {
        [JsonPropertyName("timestamp")] public string Timestamp = "";
        [JsonPropertyName("severity")]  public string Severity = "";
        [JsonPropertyName("message")]   public string Message = "";
        [JsonPropertyName("source")]    public string Source = "";
    }

    /// <summary>Internal alarm DTO used before display mapping.</summary>
    public class AlarmDto
    {
        public string type = "";
        public string severity = "";
        public string status = "";
        public string created = "";
        public string originator = "";
        public JsonElement details;
    }

    /// <summary>Alarm DTO for dashboard display.</summary>
    public class AlarmDisplayDto
    {
        [JsonPropertyName("type")]     public string Type     = "";
        [JsonPropertyName("severity")] public string Severity = "";
        [JsonPropertyName("status")]   public string Status   = "";
        [JsonPropertyName("message")]  public string Message  = "";
        [JsonPropertyName("originator")] public string Originator = "";
        [JsonPropertyName("time")]     public string Time     = "";
    }

    // ── Request logging startup filter ──────────────────────────

    /// <summary>
    /// Adds simple request logging to the middleware pipeline.
    /// </summary>
    internal class RequestLoggingStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (ctx, next) =>
                {
                    var sw = Stopwatch.StartNew();
                    await next();
                    sw.Stop();
                    Console.WriteLine($"  [HTTP] {ctx.Request.Method} {ctx.Request.Path} {ctx.Response.StatusCode} {sw.ElapsedMilliseconds}ms");
                });
                next(app);
            };
        }
    }
}
