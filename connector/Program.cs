// Program.cs – entry point; wires everything together
//
//  ThingsBoard publish topics:
//    Telemetry   →  v1/devices/me/telemetry    (timestamped time-series)
//    Attributes  →  v1/devices/me/attributes   (key-value device attributes, no timestamp)
//
//  BACnet devices return both; Modbus devices return telemetry only.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Extensions.ManagedClient;

namespace Connector
{
    using Telemetry  = Dictionary<string, double>;
    using Attributes = Dictionary<string, string>;

    class Program
    {
        static async Task Main()
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            Console.WriteLine($"=== Modbus / BACnet → ThingsBoard Connector v{version?.Major}.{version?.Minor}.{version?.Build} ===\n");

            // ── Load config ───────────────────────────────────────────────────
            string configPath = Path.Combine(AppContext.BaseDirectory, "connector.json");
            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"Config file not found: {configPath}");
                return;
            }

            var cfg = JsonSerializer.Deserialize<AppConfig>(
                          await File.ReadAllTextAsync(configPath),
                          new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip })
                      ?? throw new Exception("Failed to parse connector.json");

            var connections = cfg.Connections.ToDictionary(c => c.Id);

            // Validate all devices reference a known connection
            foreach (var d in cfg.Devices)
                if (!connections.ContainsKey(d.ConnectionId))
                    throw new Exception(
                        $"Device '{d.Name}' references unknown connectionId '{d.ConnectionId}'.");

            // ── Print summary ─────────────────────────────────────────────────
            Console.WriteLine($"Connections ({cfg.Connections.Count}):");
            foreach (var c in cfg.Connections)
                Console.WriteLine($"  [{c.Id,-22}] {c.Type,-12} {c.Host}:{c.Port}");

            Console.WriteLine($"\nDevices ({cfg.Devices.Count}):");
            foreach (var d in cfg.Devices)
            {
                var conn = connections[d.ConnectionId];
                Console.WriteLine($"  [{d.DeviceType,-15}] {d.Name,-38} → {conn.Host}:{conn.Port}");
            }

            // ── Ctrl+C ────────────────────────────────────────────────────────
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            // ── ThingsBoard provisioning ──────────────────────────────────────
            Console.WriteLine("\n► Provisioning devices in ThingsBoard…");
            var tbApi = new ThingsBoardApi(cfg.ThingsBoard);
            await tbApi.LoginAsync();

            var deviceIds = new Dictionary<string, string>();
            foreach (var d in cfg.Devices)
            {
                string tbType = DeviceReaderFactory.Get(d.DeviceType).DriverName;
                deviceIds[d.Name] = await tbApi.EnsureDeviceAsync(d.Name, tbType, d.AccessToken);
            }

            // ── MQTT clients (one per device) ─────────────────────────────────
            Console.WriteLine("\n► Connecting MQTT clients…");
            var mqttClients = new Dictionary<DeviceConfig, IManagedMqttClient>();
            foreach (var d in cfg.Devices)
            {
                var mqttClient = await BuildMqttClientAsync(d, cfg.ThingsBoard);
                mqttClients[d] = mqttClient;

                // ── Write-back subscriptions (opt-in per device) ──────────────
                if (d.Writeback is { } wb)
                {
                    var writer = DeviceReaderFactory.GetWriter(d.DeviceType);
                    if (writer is null)
                    {
                        Console.WriteLine(
                            $"  [WB] WARNING: '{d.Name}' has writeback enabled but " +
                            $"driver '{d.DeviceType}' does not implement IDeviceWriter.");
                    }
                    else
                    {
                        var conn = connections[d.ConnectionId];

                        if (wb.SharedAttributes)
                        {
                            await mqttClient.SubscribeAsync("v1/devices/me/attributes");
                            Console.WriteLine($"  [WB] {d.Name} → subscribed to shared-attribute updates.");
                        }

                        if (wb.Rpc)
                        {
                            await mqttClient.SubscribeAsync("v1/devices/me/rpc/request/+");
                            Console.WriteLine($"  [WB] {d.Name} → subscribed to server-side RPC.");
                        }

                        // Capture loop variables for the closure
                        var capturedDevice = d;
                        var capturedConn   = conn;
                        var capturedWriter = writer;
                        var capturedClient = mqttClient;
                        mqttClient.ApplicationMessageReceivedAsync += args =>
                            HandleWriteBackAsync(args, capturedDevice, capturedConn,
                                                 capturedWriter, capturedClient);
                    }
                }
            }

            // ── Hierarchy background provisioning (Deziko Structured Views) ─────
            // Fires once after startup as a background Task for each BACnet device
            // that has hierarchy.enabled = true.  It waits for the first discovery
            // (signalled by BacnetReadResult.HierarchyDirty) then provisions assets.
            var bacnetReader = (BacnetReader)DeviceReaderFactory.Get("bacnet-generic");

            // Per-device leaf asset maps (keyPrefix → TB asset UUID)
            // Populated by the background hierarchy provisioner after discovery; the
            // poll loop reads this (under lock) to route telemetry to individual assets.
            var leafMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in cfg.Devices)
            {
                if (d.Bacnet?.Hierarchy?.Enabled != true) continue;

                var capturedDevice = d;
                var capturedConn   = connections[d.ConnectionId];
                _ = Task.Run(async () =>
                {
                    Console.WriteLine(
                        $"  [Hierarchy] Background job started for '{capturedDevice.Name}'.");
                    try
                    {
                        // Wait until the first ReadFull() sets HierarchyDirty, which
                        // happens on the first poll cycle.  Poll every 5 s here.
                        DezikoTree? tree = null;
                        while (!cts.Token.IsCancellationRequested)
                        {
                            tree = bacnetReader.GetDiscoveredTree(capturedDevice.Name);
                            if (tree is not null) break;
                            await Task.Delay(5_000, cts.Token);
                        }

                        if (tree is null || cts.Token.IsCancellationRequested) return;

                        // Refresh JWT in case it expired while waiting for discovery
                        await tbApi.RefreshIfNeededAsync();

                        // Look up the TB Device UUID for this BACnet device
                        string? tbDeviceId = await tbApi.FindDeviceIdAsync(capturedDevice.Name);
                        if (tbDeviceId is null)
                        {
                            Console.Error.WriteLine(
                                $"  [Hierarchy] ERROR: TB device '{capturedDevice.Name}' not found in ThingsBoard. " +
                                "Ensure the device was provisioned before the hierarchy job runs.");
                            return;
                        }

                        Console.WriteLine(
                            $"  [Hierarchy] Provisioning asset tree for '{capturedDevice.Name}' " +
                            $"(device UUID={tbDeviceId}, roots={tree.Roots.Count})…");
                        var provisioner = new DezikoProvisioner();
                        var leafMap = await provisioner.ProvisionAsync(
                            tree,
                            bacnetReader.GetDiscoveredObjects(capturedDevice.Name),
                            tbApi, tbDeviceId,
                            capturedDevice.Bacnet!.Hierarchy!.AssetType,
                            cts.Token);

                        // Store the leaf map so the poll loop can route telemetry to assets
                        lock (leafMaps)
                            leafMaps[capturedDevice.Name] = leafMap;

                        // Also push the mapping into the BacnetReader instance so it can route alarms to assets
                        bacnetReader.UpdateAssetIdMap(capturedDevice.Name, leafMap, tbApi, capturedDevice, tbDeviceId);

                        Console.WriteLine(
                            $"  [Hierarchy] Leaf map: {leafMap.Count} data-point assets registered for telemetry and alarm routing.");
                    }
                    catch (OperationCanceledException) { /* shutting down */ }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(
                            $"  [Hierarchy] ERROR '{capturedDevice.Name}': {ex.GetType().Name}: {ex.Message}");
                        if (ex.InnerException is not null)
                            Console.Error.WriteLine(
                                $"  [Hierarchy]   Inner: {ex.InnerException.Message}");
                    }
                }, cts.Token);
            }

            // ── Per-device poll-interval tracking ─────────────────────────────
            var lastPolledAt = new Dictionary<DeviceConfig, DateTime>();
            foreach (var d in cfg.Devices)
                lastPolledAt[d] = DateTime.MinValue;

            int globalIntervalMs = cfg.Polling.IntervalSeconds * 1000;

            // Tracks devices with active "Communication Loss" alarms
            var offlineDevices = new HashSet<string>();

            // ── COV init: set up long-lived clients for COV-enabled BACnet devices ──
            foreach (var d in cfg.Devices)
            {
                if (d.Bacnet?.Cov is not { Enabled: true }) continue;

                var capturedDevice = d;
                var capturedConn   = connections[d.ConnectionId];
                var capturedMqtt   = mqttClients[d];

                Console.WriteLine($"  [COV] Initialising COV mode for '{d.Name}'…");
                bacnetReader.InitCovMode(
                    capturedConn,
                    capturedDevice,
                    tbApi,
                    tel  => PublishTelemetryAsync(capturedMqtt, tel),
                    attr => PublishAttributesAsync(capturedMqtt, attr));

                // COV devices don't need the polling loop to read their values;
                // set LastPolledAt so ServiceCovDevice runs every tick.
                lastPolledAt[d] = DateTime.UtcNow;
            }

            // ── Polling loop (1 s tick) ───────────────────────────────────────
            Console.WriteLine($"\n► Running. Ctrl+C to stop.\n");

            while (!cts.Token.IsCancellationRequested)
            {
                var tasks = cfg.Devices
                    .Select(d => PollAndPublishAsync(
                        d, connections[d.ConnectionId], mqttClients[d],
                        lastPolledAt,
                        d.PollIntervalSeconds.HasValue
                            ? d.PollIntervalSeconds.Value * 1000
                            : globalIntervalMs,
                        tbApi, deviceIds, offlineDevices, leafMaps))
                    .ToList();

                await Task.WhenAll(tasks);
                await tbApi.RefreshIfNeededAsync();

                try { await Task.Delay(1_000, cts.Token); }
                catch (TaskCanceledException) { break; }
            }

            Console.WriteLine("\nShutting down…");
            // Gracefully cancel COV subscriptions
            foreach (var d in cfg.Devices)
                if (d.Bacnet?.Cov is { Enabled: true })
                    bacnetReader.DisposeCovClient(d.Name);
            foreach (var c in mqttClients.Values)
                await c.StopAsync();
        }

        // =====================================================================
        //  Poll one device
        // =====================================================================
        static async Task PollAndPublishAsync(
            DeviceConfig device, ConnectionConfig conn, IManagedMqttClient mqtt,
            Dictionary<DeviceConfig, DateTime> lastPolledAt,
            int deviceIntervalMs,
            ThingsBoardApi tbApi,
            Dictionary<string, string> deviceIds,
            HashSet<string> offlineDevices,
            Dictionary<string, Dictionary<string, string>> leafMaps)
        {
            try
            {
                var reader = DeviceReaderFactory.Get(device.DeviceType);

                Telemetry  telemetry;
                Attributes attributes = new();

                // ── BACnet COV mode ───────────────────────────────────────────
                if (reader is BacnetReader bacnetReader && device.Bacnet?.Cov is { Enabled: true })
                {
                    // ServiceCovDevice runs every tick: renews subs, drip-polls attrs,
                    // reads fallback-polled objects. COV telemetry is published
                    // event-driven directly from the OnCOVNotification handler.
                    var result = bacnetReader.ServiceCovDevice(conn, device);
                    attributes = result.Attributes;
                    telemetry  = result.Telemetry;  // fallback-poll objects only

                    if (attributes.Count > 0)
                        await PublishAttributesAsync(mqtt, attributes);
                    if (telemetry.Count > 0)
                        await PublishTelemetryAsync(mqtt, telemetry);

                    if (attributes.Count > 0 || telemetry.Count > 0)
                        Console.WriteLine(
                            $"[{DateTime.Now:HH:mm:ss}] [{reader.DriverName,-10}] {device.Name,-38} " +
                            $"[COV] attrs={attributes.Count} fallback-tel={telemetry.Count}");
                    return;
                }

                // ── Polling mode (non-COV BACnet / Modbus) ────────────────────
                var now = DateTime.UtcNow;
                if (now - lastPolledAt[device] < TimeSpan.FromMilliseconds(deviceIntervalMs))
                    return;   // not yet time for this device

                lastPolledAt[device] = now;

                if (reader is BacnetReader br)
                {
                    var result = br.ReadFull(conn, device, tbApi);
                    telemetry  = result.Telemetry;
                    attributes = result.Attributes;
                }
                else
                {
                    telemetry = reader.Read(conn, device);
                }

                if (telemetry.Count > 0)
                {
                    await PublishTelemetryAsync(mqtt, telemetry);

                    // Post to Assets via REST (so Entity Views pointing to Assets have data)
                    if (leafMaps.TryGetValue(device.Name, out var leafMap))
                    {
                        // Group telemetry by asset
                        var perAsset = new Dictionary<string, Dictionary<string, double>>();
                        foreach (var kv in telemetry)
                        {
                            // Key is e.g. "ai_1_value". Prefix is the technical key.
                            // We find the prefix by stripping the property suffix.
                            int lastUnderscore = kv.Key.LastIndexOf('_');
                            if (lastUnderscore <= 0) continue;
                            string prefix = kv.Key.Substring(0, lastUnderscore);
                            
                            if (leafMap.TryGetValue(prefix, out string? assetId))
                            {
                                if (!perAsset.ContainsKey(assetId)) perAsset[assetId] = new();
                                perAsset[assetId][kv.Key] = kv.Value;
                            }
                        }

                        foreach (var entry in perAsset)
                            _ = tbApi.PostAssetTelemetryBatchAsync(entry.Key, entry.Value);
                    }
                }

                if (attributes.Count > 0)
                    await PublishAttributesAsync(mqtt, attributes);

                string tvLine = string.Join("  ", telemetry.Select(kv => $"{kv.Key}={kv.Value}"));
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [{reader.DriverName,-10}] {device.Name,-38} " +
                    $"{(telemetry.Count == 0 ? "(no values)" : tvLine)}");

                if (attributes.Count > 0)
                    Console.WriteLine(
                        $"{"",13}  {"[attrs]",-10}  " +
                        $"{string.Join(", ", attributes.Keys.Take(5))}" +
                        $"{(attributes.Count > 5 ? $" … +{attributes.Count - 5} more" : "")}");

                // ── Clear Communication Loss Alarm on success ─────────────────
                if (offlineDevices.Remove(device.Name))
                {
                    _ = tbApi.ClearAlarmAsync(deviceIds[device.Name], "Communication Loss");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ERROR [{device.DeviceType}] {device.Name}: {ex.Message}");

                // ── Communication Loss Alarm ──────────────────────────────────
                if (offlineDevices.Add(device.Name))
                {
                    _ = tbApi.CreateOrUpdateAlarmAsync(
                        deviceIds[device.Name], "Communication Loss", "CRITICAL",
                        $"Device {device.Name} is not responding: {ex.Message}");
                }
            }
        }


        // =====================================================================
        //  MQTT publish helpers
        // =====================================================================

        /// <summary>Publishes time-series data to v1/devices/me/telemetry.</summary>
        static async Task PublishTelemetryAsync(IManagedMqttClient client, Telemetry values)
        {
            var payload = JsonSerializer.Serialize(new
            {
                ts     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                values,
            });
            await Enqueue(client, "v1/devices/me/telemetry", payload);
        }

        /// <summary>Publishes static key-value pairs to v1/devices/me/attributes.</summary>
        static async Task PublishAttributesAsync(IManagedMqttClient client, Attributes attrs)
        {
            // Attributes topic expects a flat JSON object, no timestamp
            var payload = JsonSerializer.Serialize(attrs);
            await Enqueue(client, "v1/devices/me/attributes", payload);
        }

        static async Task Enqueue(IManagedMqttClient client, string topic, string json)
        {
            await client.EnqueueAsync(
                new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Encoding.UTF8.GetBytes(json))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build());
        }

        static MqttApplicationMessage BuildMqttMessage(string topic, string json) =>
            new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(Encoding.UTF8.GetBytes(json))
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                .Build();

        // =====================================================================
        //  Write-back handler – called by the per-device MQTT ApplicationMessageReceivedAsync
        // =====================================================================
        static async Task HandleWriteBackAsync(
            MqttApplicationMessageReceivedEventArgs args,
            DeviceConfig device, ConnectionConfig conn,
            IDeviceWriter writer, IManagedMqttClient mqttClient)
        {
            string topic   = args.ApplicationMessage.Topic;
            var    seg     = args.ApplicationMessage.PayloadSegment;
            string payload = seg.Count > 0
                ? Encoding.UTF8.GetString(seg.Array!, seg.Offset, seg.Count)
                : "{}";

            try
            {
                // ── Shared Attribute update ────────────────────────────────────
                //    Payload: { "key1": value1, "key2": value2, … }
                if (topic == "v1/devices/me/attributes")
                {
                    using var doc = JsonDocument.Parse(payload);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind != JsonValueKind.Number) continue;

                        double val = prop.Value.GetDouble();
                        Console.WriteLine(
                            $"  [WB←TB] {device.Name}  attr  {prop.Name} = {val}");
                        writer.Write(conn, device, prop.Name, val);
                    }
                    return;
                }

                // ── Server-side RPC ────────────────────────────────────────────
                //    Topic:   v1/devices/me/rpc/request/{requestId}
                //    Payload: { "method": "setValue", "params": { "key": "…", "value": … } }
                if (topic.StartsWith("v1/devices/me/rpc/request/"))
                {
                    string requestId  = topic[(topic.LastIndexOf('/') + 1)..];
                    string respTopic  = $"v1/devices/me/rpc/response/{requestId}";

                    using var doc  = JsonDocument.Parse(payload);
                    string method  = doc.RootElement.TryGetProperty("method", out var mProp)
                        ? mProp.GetString() ?? ""
                        : "";

                    if (method == "setValue")
                    {
                        var rpcParams = doc.RootElement.GetProperty("params");
                        string key = rpcParams.GetProperty("key").GetString()
                            ?? throw new ArgumentException("RPC params.key is null.");
                        double val = rpcParams.GetProperty("value").GetDouble();

                        Console.WriteLine(
                            $"  [WB\u2190TB] {device.Name}  rpc   {key} = {val}  (reqId={requestId})");
                        writer.Write(conn, device, key, val);

                        await mqttClient.EnqueueAsync(
                            BuildMqttMessage(respTopic,
                                JsonSerializer.Serialize(new { success = true })));
                    }
                    else
                    {
                        Console.WriteLine(
                            $"  [WB\u2190TB] {device.Name}  rpc   unknown method '{method}' \u2013 ignored.");
                        await mqttClient.EnqueueAsync(
                            BuildMqttMessage(respTopic,
                                JsonSerializer.Serialize(new { error = $"Unknown method '{method}'" })));
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"  [WB\u2190TB] ERROR {device.Name}: {ex.GetType().Name}: {ex.Message}");

                // Send error response for RPC so the caller doesn't time out
                if (topic.StartsWith("v1/devices/me/rpc/request/"))
                {
                    string requestId = topic[(topic.LastIndexOf('/') + 1)..];
                    try
                    {
                        await mqttClient.EnqueueAsync(
                            BuildMqttMessage(
                                $"v1/devices/me/rpc/response/{requestId}",
                                JsonSerializer.Serialize(new { error = ex.Message })));
                    }
                    catch { /* swallow \u2013 best effort */ }
                }
            }
        }

        // =====================================================================
        //  Managed MQTT client (auto-reconnects)
        // =====================================================================
        static async Task<IManagedMqttClient> BuildMqttClientAsync(DeviceConfig d, TbConfig tb)
        {
            var options = new ManagedMqttClientOptionsBuilder()
                .WithAutoReconnectDelay(TimeSpan.FromSeconds(5))
                .WithClientOptions(new MqttClientOptionsBuilder()
                    .WithTcpServer(tb.Host, tb.MqttPort)
                    .WithCredentials(d.AccessToken, "")
                    .WithClientId($"conn-{d.AccessToken[^10..]}-{Guid.NewGuid().ToString("N")[..4]}")
                    .WithCleanSession()
                    .Build())
                .Build();

            var client = new MqttFactory().CreateManagedMqttClient();
            client.ConnectedAsync    += _ => { Console.WriteLine($"    [MQTT ✓] {d.Name}"); return Task.CompletedTask; };
            client.DisconnectedAsync += a => { Console.WriteLine($"    [MQTT ✗] {d.Name}: {a.ReasonString} – reconnecting…"); return Task.CompletedTask; };

            await client.StartAsync(options);

            for (int i = 0; i < 16 && !client.IsConnected; i++)
                await Task.Delay(500);

            if (!client.IsConnected)
                throw new Exception($"MQTT connect timeout for '{d.Name}' (token: {d.AccessToken})");

            return client;
        }
    }
}
