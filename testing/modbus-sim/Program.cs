// Program.cs – Modbus TCP simulator (Janitza UMG-604 style)
//
// Register map (0-based, float32 big-endian = 2 × uint16 registers):
//   19020  power_w     → connector divides by 1000 → power_kw
//   19060  import_wh   → connector divides by 1000 → import_kwh
//   19062  export_wh   → always 0 (no export on test meter)
//
// Slave ID : 1
// Behaviour: power oscillates on a 5-min sine wave (2–8 kW);
//            import_kwh accumulates monotonically from a random start.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NModbus;

const ushort REG_POWER_W   = 19020;
const ushort REG_IMPORT_WH = 19060;
const ushort REG_EXPORT_WH = 19062;

const double POWER_MIN_W    = 2_000;
const double POWER_MAX_W    = 8_000;
const double POWER_PERIOD_S = 300;          // 5-minute sine cycle

// Random starting energy so the meter doesn't always read 0
var rng       = new Random();
double importWh = rng.NextDouble() * 499_000_000 + 1_000_000;  // 1–500 MWh in Wh

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// ── Build slave ──────────────────────────────────────────────────────────────
var factory  = new ModbusFactory();
var listener = new TcpListener(IPAddress.Any, 502);
listener.Start();

var network = factory.CreateSlaveNetwork(listener);
var slave   = factory.CreateSlave(1);          // slave ID 1
network.AddSlave(slave);

var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
Console.WriteLine($"=== Modbus TCP Simulator v{version?.Major}.{version?.Minor}.{version?.Build} (Janitza UMG-604) ===");
Console.WriteLine($"Listening on 0.0.0.0:502  slave=1");
Console.WriteLine($"Initial import = {importWh / 1000:F1} kWh");
Console.WriteLine($"Power sine  {POWER_MIN_W/1000:F1}–{POWER_MAX_W/1000:F1} kW  period={POWER_PERIOD_S:F0} s");
Console.WriteLine();

// ── Background value updater ─────────────────────────────────────────────────
var t0     = DateTime.UtcNow;
var prevAt = DateTime.UtcNow;

var updater = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        var now = DateTime.UtcNow;
        double dt      = (now - prevAt).TotalSeconds;
        double elapsed = (now - t0).TotalSeconds;
        prevAt = now;

        double phase   = elapsed % POWER_PERIOD_S / POWER_PERIOD_S;
        double powerW  = POWER_MIN_W + (POWER_MAX_W - POWER_MIN_W)
                         * (0.5 + 0.5 * Math.Sin(2 * Math.PI * phase));

        importWh += powerW * dt / 3600.0;   // accumulate energy

        WriteFloat32(slave, REG_POWER_W,   (float)powerW);
        WriteFloat32(slave, REG_IMPORT_WH, (float)importWh);
        WriteFloat32(slave, REG_EXPORT_WH, 0f);

        Console.WriteLine($"[{now:HH:mm:ss}] power={powerW/1000:F3} kW  " +
                          $"import={importWh/1000:F3} kWh");

        await Task.Delay(5_000, cts.Token).ConfigureAwait(false);
    }
}, cts.Token);

// ── Run server ───────────────────────────────────────────────────────────────
var serverTask = network.ListenAsync(cts.Token);
await Task.WhenAny(serverTask, updater);

Console.WriteLine("Shutting down.");
listener.Stop();

// ── Helpers ──────────────────────────────────────────────────────────────────

/// <summary>Encodes a float32 as two big-endian uint16 holding registers
/// (mirrors ModbusHelper.WriteFloat32 in the connector).</summary>
static void WriteFloat32(IModbusSlave slave, ushort address, float value)
{
    var b = BitConverter.GetBytes(value);
    if (BitConverter.IsLittleEndian) Array.Reverse(b);
    ushort hi = (ushort)(b[0] << 8 | b[1]);
    ushort lo = (ushort)(b[2] << 8 | b[3]);
    slave.DataStore.HoldingRegisters.WritePoints(address,     new ushort[] { hi });
    slave.DataStore.HoldingRegisters.WritePoints((ushort)(address + 1), new ushort[] { lo });
}
