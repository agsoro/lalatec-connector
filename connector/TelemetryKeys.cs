// TelemetryKeys.cs – Centralized constants for ThingsBoard telemetry keys
namespace Connector
{
    public static class TelemetryKeys
    {
        // Standard energy/power keys used across all readers
        public const string PowerKw          = "power_kw";
        public const string EnergyImportKwh  = "energy_import_kwh";
        public const string EnergyExportKwh  = "energy_export_kwh";
        public const string PowerLimitPct    = "power_limit_pct";
    }
}
