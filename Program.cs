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
            Console.WriteLine("=== Modbus / BACnet → ThingsBoard Connector ===\n");

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

            foreach (var d in cfg.Devices)
            {
                string tbType = DeviceReaderFactory.Get(d.DeviceType).DriverName;
                await tbApi.EnsureDeviceAsync(d.Name, tbType, d.AccessToken);
            }

            // ── MQTT clients (one per device) ─────────────────────────────────
            Console.WriteLine("\n► Connecting MQTT clients…");
            var mqttClients = new Dictionary<DeviceConfig, IManagedMqttClient>();
            foreach (var d in cfg.Devices)
                mqttClients[d] = await BuildMqttClientAsync(d, cfg.ThingsBoard);

            // ── Polling loop ──────────────────────────────────────────────────
            int intervalMs = cfg.Polling.IntervalSeconds * 1000;
            Console.WriteLine($"\n► Polling every {cfg.Polling.IntervalSeconds} s. Ctrl+C to stop.\n");

            while (!cts.Token.IsCancellationRequested)
            {
                var tasks = cfg.Devices
                    .Select(d => PollAndPublishAsync(d, connections[d.ConnectionId], mqttClients[d]))
                    .ToList();

                await Task.WhenAll(tasks);
                await tbApi.RefreshIfNeededAsync();

                try { await Task.Delay(intervalMs, cts.Token); }
                catch (TaskCanceledException) { break; }
            }

            Console.WriteLine("\nShutting down…");
            foreach (var c in mqttClients.Values)
                await c.StopAsync();
        }

        // =====================================================================
        //  Poll one device
        // =====================================================================
        static async Task PollAndPublishAsync(
            DeviceConfig device, ConnectionConfig conn, IManagedMqttClient mqtt)
        {
            try
            {
                var reader = DeviceReaderFactory.Get(device.DeviceType);

                Telemetry  telemetry;
                Attributes attributes = new();

                // BACnet returns both telemetry and attributes
                if (reader is BacnetReader bacnetReader)
                {
                    var result = bacnetReader.ReadFull(conn, device);
                    telemetry  = result.Telemetry;
                    attributes = result.Attributes;
                }
                else
                {
                    telemetry = reader.Read(conn, device);
                }

                // Publish telemetry (time-series)
                if (telemetry.Count > 0)
                    await PublishTelemetryAsync(mqtt, telemetry);

                // Publish attributes (only when non-empty, typically only after discovery)
                if (attributes.Count > 0)
                    await PublishAttributesAsync(mqtt, attributes);

                // Log
                string tvLine = string.Join("  ", telemetry.Select(kv => $"{kv.Key}={kv.Value}"));
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] [{reader.DriverName,-10}] {device.Name,-38} " +
                    $"{(telemetry.Count == 0 ? "(no values)" : tvLine)}");

                if (attributes.Count > 0)
                    Console.WriteLine(
                        $"{"",13}  {"[attrs]",-10}  " +
                        $"{string.Join(", ", attributes.Keys.Take(5))}" +
                        $"{(attributes.Count > 5 ? $" … +{attributes.Count - 5} more" : "")}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] ERROR [{device.DeviceType}] {device.Name}: {ex.Message}");
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
                    .WithClientId($"conn-{d.AccessToken[^10..]}-{Guid.NewGuid():N[..4]}")
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
