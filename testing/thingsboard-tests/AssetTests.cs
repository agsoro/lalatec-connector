// AssetTests.cs – Integration tests for ThingsBoard asset CRUD, attributes, and telemetry
using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ThingsBoard.Tests;

public class AssetTests : IAsyncLifetime
{
    private ThingsBoardTestClient _client = null!;
    private string _assetName = null!;

    public Task InitializeAsync()
    {
        TestConfig.Validate();
        _client = new ThingsBoardTestClient(TestConfig.BaseUrl, TestConfig.AdminEmail, TestConfig.AdminPassword);
        _assetName = $"test-asset-{Guid.NewGuid().ToString("N")[..8]}";
        return _client.LoginAsync();
    }

    public Task DisposeAsync()
    {
        _client?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateAsset_ReturnsAssetWithId()
    {
        // Act
        var result = await _client.CreateAssetAsync(_assetName, "Gateway", "Integration test gateway");

        // Assert
        result.ValueKind.Should().NotBe(JsonValueKind.Undefined);
        result.TryGetProperty("id", out _).Should().BeTrue();
        result.GetProperty("name").GetString().Should().Be(_assetName);
        result.GetProperty("type").GetString().Should().Be("Gateway");
        result.GetProperty("description").GetString().Should().Be("Integration test gateway");
        result.GetProperty("publicAsset").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetAsset_ReturnsCorrectAsset()
    {
        // Arrange
        var createResult = await _client.CreateAssetAsync(_assetName, "Sensor", "Test sensor");
        string assetId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        var getResult = await _client.GetAssetAsync(assetId);

        // Assert
        getResult.Should().NotBeNull();
        JsonElement actual = getResult!.Value;
        actual.GetProperty("name").GetString().Should().Be(_assetName);
        actual.GetProperty("type").GetString().Should().Be("Sensor");
    }

    [Fact]
    public async Task ListTenantAssets_ReturnsCreatedAsset()
    {
        // Arrange
        var createResult = await _client.CreateAssetAsync(_assetName, "Monitor", "Test monitor");
        string shortId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        var assets = await _client.ListTenantAssetsAsync(textSearch: _assetName);

        // Assert
        assets.Should().NotBeEmpty();
        assets.Should().Contain(a => ThingsBoardTestClient.GetShortId(a) == shortId);
    }

    [Fact]
    public async Task PostTelemetry_AndRetrieve_ValidTimestampAndValue()
    {
        // Arrange
        var createResult = await _client.CreateAssetAsync(_assetName, "Meter", "Test meter");
        string assetId = ThingsBoardTestClient.GetShortId(createResult);
        string telemetryKey = "voltage";
        double expectedValue = 230.5;
        long expectedTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Act
        await _client.PostAssetTelemetryAsync(assetId, new Dictionary<string, object>
        {
            { telemetryKey, expectedValue }
        }, expectedTs);

        // Wait briefly for the data to propagate
        await Task.Delay(1000);

        // Assert – retrieve the telemetry
        var telemetry = await _client.GetAssetTelemetryAsync(assetId, telemetryKey, expectedTs, expectedTs + 10000);
        telemetry.Should().NotBeNull();
        JsonElement actualTelemetry = telemetry!.Value;
        actualTelemetry.GetProperty(telemetryKey).ValueKind.Should().Be(JsonValueKind.Array);
        var values = actualTelemetry.GetProperty(telemetryKey);
        values[0].GetProperty("ts").GetInt64().Should().Be(expectedTs);
        values[0].GetProperty(telemetryKey).GetDouble().Should().BeApproximately(expectedValue, 0.01);
    }

    [Fact]
    public async Task DeleteAsset_RemovesAsset()
    {
        // Arrange
        var createResult = await _client.CreateAssetAsync(_assetName, "Relay", "Test relay");
        string assetId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        await _client.DeleteAssetAsync(assetId);

        // Assert
        var getResult = await _client.GetAssetAsync(assetId);
        getResult.Should().BeNull();
    }

    [Fact]
    public async Task CreateAssetWithEmptyDescription_Succeeds()
    {
        // Act
        var result = await _client.CreateAssetAsync(_assetName + "-minimal", "Generic");

        // Assert
        result.TryGetProperty("id", out _).Should().BeTrue();
        result.GetProperty("type").GetString().Should().Be("Generic");
    }

    [Fact]
    public async Task CreateAssetWithSpecialCharacters_NamePreserved()
    {
        // Arrange
        var specialName = "test-asset-Ünïcödé";

        // Act
        var result = await _client.CreateAssetAsync(specialName, "Test", "Asset with special characters");

        // Assert
        result.GetProperty("name").GetString().Should().Be(specialName);
    }
}
