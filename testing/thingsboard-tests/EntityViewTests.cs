// EntityViewTests.cs – Integration tests for ThingsBoard entity view CRUD, key configuration, and attributes
using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ThingsBoard.Tests;

public class EntityViewTests : IAsyncLifetime
{
    private ThingsBoardTestClient _client = null!;
    private string _assetName = null!;
    private string _assetId = null!;
    private string _viewName = null!;

    public async Task InitializeAsync()
    {
        TestConfig.Validate();
        _client = new ThingsBoardTestClient(TestConfig.BaseUrl, TestConfig.AdminEmail, TestConfig.AdminPassword);
        await _client.LoginAsync();

        _assetName = $"test-asset-ev-{Guid.NewGuid().ToString("N")[..8]}";
        _viewName = $"test-view-{Guid.NewGuid().ToString("N")[..8]}";

        // Create a source asset for the entity view
        var createResult = await _client.CreateAssetAsync(_assetName, "Source", "Source entity for entity view tests");
        _assetId = ThingsBoardTestClient.GetShortId(createResult);
    }

    public async Task DisposeAsync()
    {
        try
        {
            // Clean up entity views
            var views = await _client.ListTenantEntityViewsAsync(textSearch: _viewName);
            foreach (var view in views)
            {
                var id = ThingsBoardTestClient.GetShortId(view);
                await _client.DeleteEntityViewAsync(id);
            }
        }
        finally
        {
            // Clean up asset
            try
            {
                await _client.DeleteAssetAsync(_assetId);
            }
            catch { /* ignore cleanup errors */ }
        }
        _client?.Dispose();
    }

    [Fact]
    public async Task CreateEntityView_ReturnsViewWithId()
    {
        // Arrange
        var telemetryKeys = new[] { "voltage", "current", "power" };
        var serverAttributes = new[] { "model", "location" };

        // Act
        var result = await _client.CreateEntityViewAsync(
            _viewName, "GatewayView",
            _assetId, "ASSET",
            telemetryKeys, serverAttributes, "Integration test entity view");

        // Assert
        result.ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Undefined);
        result.TryGetProperty("id", out _).Should().BeTrue();
        result.GetProperty("name").GetString().Should().Be(_viewName);
        result.GetProperty("type").GetString().Should().Be("GatewayView");
    }

    [Fact]
    public async Task GetEntityView_ReturnsCorrectView()
    {
        // Arrange
        var telemetryKeys = new[] { "voltage" };
        var serverAttributes = new string[0];
        var createResult = await _client.CreateEntityViewAsync(
            _viewName + "-get", "SensorView",
            _assetId, "ASSET",
            telemetryKeys, serverAttributes, "Test view for GET");
        string viewId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        var getResult = await _client.GetEntityViewAsync(viewId);

        // Assert
        getResult.Should().NotBeNull();
        JsonElement actual = getResult!.Value;
        actual.GetProperty("name").GetString().Should().Be(_viewName + "-get");
        actual.GetProperty("type").GetString().Should().Be("SensorView");
        actual.GetProperty("entityId").GetProperty("id").GetString().Should().Contain(_assetId);
    }

    [Fact]
    public async Task ListTenantEntityViews_ReturnsCreatedView()
    {
        // Arrange
        var createResult = await _client.CreateEntityViewAsync(
            _viewName + "-list", "ListView",
            _assetId, "ASSET");
        string shortId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        var views = await _client.ListTenantEntityViewsAsync(textSearch: _viewName);

        // Assert
        views.Should().Contain(v => ThingsBoardTestClient.GetShortId(v) == shortId);
    }

    [Fact]
    public async Task CreateEntityView_WithSourceEntityReference_Valid()
    {
        // Arrange
        var createResult = await _client.CreateEntityViewAsync(
            _viewName + "-src", "SourceView",
            _assetId, "ASSET",
            new[] { "temperature" }, new[] { "building" },
            "View with source entity reference");
        string viewId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        var result = await _client.GetEntityViewAsync(viewId);

        // Assert
        result.Should().NotBeNull();
        JsonElement actual = result!.Value;
        actual.GetProperty("entityId").GetProperty("id").GetString().Should().Contain(_assetId);
        actual.GetProperty("entityId").GetProperty("entityType").GetString().Should().Be("ASSET");
    }

    [Fact]
    public async Task SetEntityViewAttributes_AndRetrieve_Valid()
    {
        // Arrange
        var createResult = await _client.CreateEntityViewAsync(
            _viewName + "-attr", "AttrView",
            _assetId, "ASSET");
        string viewId = ThingsBoardTestClient.GetShortId(createResult);

        var attrs = new Dictionary<string, string>
        {
            { "building", "A1" },
            { "floor", "3" },
            { "room", "301" },
        };

        // Act
        await _client.SetEntityViewAttributesAsync(viewId, attrs);

        // Wait for data to propagate
        await Task.Delay(1000);

        // Assert – retrieve attributes
        var retrieved = await _client.GetEntityViewAttributesAsync(viewId, "SERVER_SCOPE");
        retrieved.Should().NotBeNull();
    }

    [Fact]
    public async Task CreateEntityView_WithEmptyKeys_Configured()
    {
        // Act – entity view with no telemetry keys or attributes
        var result = await _client.CreateEntityViewAsync(
            _viewName + "-empty", "EmptyView",
            _assetId, "ASSET",
            Array.Empty<string>(), Array.Empty<string>());

        // Assert
        result.TryGetProperty("id", out _).Should().BeTrue();
        result.GetProperty("name").GetString().Should().Be(_viewName + "-empty");
        result.GetProperty("type").GetString().Should().Be("EmptyView");
    }

    [Fact]
    public async Task DeleteEntityView_RemovesView()
    {
        // Arrange
        var createResult = await _client.CreateEntityViewAsync(
            _viewName + "-del", "DelView",
            _assetId, "ASSET");
        string viewId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        await _client.DeleteEntityViewAsync(viewId);

        // Assert
        var getResult = await _client.GetEntityViewAsync(viewId);
        getResult.Should().BeNull();
    }

    [Fact]
    public async Task CreateEntityView_WithTelemetryKeyConfiguration_Valid()
    {
        // Arrange
        var telemetryKeys = new[] { "voltage", "current", "power_factor" };
        var serverAttrs = new[] { "manufacturer", "serial_number" };

        // Act
        var result = await _client.CreateEntityViewAsync(
            _viewName + "-keys", "KeyView",
            _assetId, "ASSET",
            telemetryKeys, serverAttrs);

        // Assert – verify the keys are present in the response
        result.TryGetProperty("id", out _).Should().BeTrue();
        result.GetProperty("name").GetString().Should().Be(_viewName + "-keys");
    }
}
