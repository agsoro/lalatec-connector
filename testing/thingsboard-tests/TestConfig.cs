// TestConfig.cs – Loads ThingsBoard connection settings from environment variables
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace ThingsBoard.Tests;

/// <summary>
/// Central configuration for all ThingsBoard integration tests.
/// </summary>
public static class TestConfig
{
    public static readonly string Host =
        Environment.GetEnvironmentVariable("TB_HOST") ?? "localhost";

    public static readonly int HttpPort =
        int.Parse(Environment.GetEnvironmentVariable("TB_HTTP_PORT") ?? "8080");

    public static readonly string AdminEmail =
        Environment.GetEnvironmentVariable("TB_ADMIN_EMAIL") ?? "sysadmin@thingsboard.org";

    public static readonly string AdminPassword =
        Environment.GetEnvironmentVariable("TB_ADMIN_PASSWORD") ?? "sysadmin";

    public static string BaseUrl => $"http://{Host}:{HttpPort}";

    /// <summary>
    /// Ensures all required configuration values are present.
    /// Throws if any required value is missing or empty.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host))
            throw new InvalidOperationException("TB_HOST is not set");
        if (HttpPort <= 0 || HttpPort > 65535)
            throw new InvalidOperationException($"Invalid TB_HTTP_PORT: {HttpPort}");
        if (string.IsNullOrWhiteSpace(AdminEmail))
            throw new InvalidOperationException("TB_ADMIN_EMAIL is not set");
        if (string.IsNullOrWhiteSpace(AdminPassword))
            throw new InvalidOperationException("TB_ADMIN_PASSWORD is not set");
    }
}
