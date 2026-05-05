// AlarmTests.cs – Integration tests for ThingsBoard alarm lifecycle (create, retrieve, update, clear)
using System;
using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace ThingsBoard.Tests;

public class AlarmTests : IAsyncLifetime
{
    private ThingsBoardTestClient _client = null!;
    private string _assetName = null!;
    private string _assetId = null!;
    private string _alarmType = null!;

    public async Task InitializeAsync()
    {
        TestConfig.Validate();
        _client = new ThingsBoardTestClient(TestConfig.BaseUrl, TestConfig.AdminEmail, TestConfig.AdminPassword);
        await _client.LoginAsync();

        _assetName = $"test-asset-alarm-{Guid.NewGuid().ToString("N")[..8]}";
        _alarmType = $"test-alarm-{Guid.NewGuid().ToString("N")[..8]}";

        // Create a source asset to associate alarms with
        var createResult = await _client.CreateAssetAsync(_assetName, "AlarmTarget", "Asset for alarm tests");
        _assetId = ThingsBoardTestClient.GetShortId(createResult);
    }

    public async Task DisposeAsync()
    {
        try
        {
            // Clean up any remaining alarms
            var alarms = await _client.GetActiveAlarmsAsync(_assetId, "ASSET");
            foreach (var alarm in alarms)
            {
                var id = ThingsBoardTestClient.GetShortId(alarm);
                try { await _client.ClearAlarmAsync(id); } catch { }
            }
        }
        finally
        {
            try
            {
                await _client.DeleteAssetAsync(_assetId);
            }
            catch { /* ignore cleanup errors */ }
        }
        _client?.Dispose();
    }

    [Fact]
    public async Task CreateAlarm_ReturnsAlarmWithId()
    {
        // Act
        var result = await _client.CreateAlarmAsync(
            _assetId, "ASSET",
            _alarmType,
            "WARNING",
            "Test alarm message",
            new Dictionary<string, object> { { "source", "integration-test" } });

        // Assert
        result.ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Undefined);
        result.TryGetProperty("id", out _).Should().BeTrue();
        result.GetProperty("type").GetString().Should().Be(_alarmType);
        result.GetProperty("severity").GetString().Should().Be("WARNING");
        result.GetProperty("status").GetString().Should().Be("ACTIVE_UNACK");
        result.GetProperty("details").GetProperty("message").GetString().Should().Be("Test alarm message");
        result.GetProperty("originator").GetProperty("id").GetString().Should().Contain(_assetId);
        result.GetProperty("originator").GetProperty("entityType").GetString().Should().Be("ASSET");
    }

    [Fact]
    public async Task CreateAlarm_DifferentSeverities_Valid()
    {
        // Arrange
        var severities = new[] { "CRITICAL", "MAJOR", "MINOR", "WARNING", "INDETERMINATE" };

        try
        {
            // Act – create alarms with each severity
            foreach (var severity in severities)
            {
                var alarmName = $"{_alarmType}-{severity}";
                var result = await _client.CreateAlarmAsync(
                    _assetId, "ASSET",
                    alarmName,
                    severity,
                    $"Test {severity} alarm",
                    new Dictionary<string, object>());

                // Assert
                result.TryGetProperty("id", out _).Should().BeTrue();
                result.GetProperty("severity").GetString().Should().Be(severity);
                result.GetProperty("status").GetString().Should().Be("ACTIVE_UNACK");
            }
        }
        finally
        {
            // Clean up all test alarms
            await CleanupAlarmByName(_alarmType + "-CRITICAL");
            await CleanupAlarmByName(_alarmType + "-MAJOR");
            await CleanupAlarmByName(_alarmType + "-MINOR");
            await CleanupAlarmByName(_alarmType + "-WARNING");
            await CleanupAlarmByName(_alarmType + "-INDETERMINATE");
        }
    }

    [Fact]
    public async Task GetAlarm_ReturnsCreatedAlarm()
    {
        // Arrange
        var createResult = await _client.CreateAlarmAsync(
            _assetId, "ASSET",
            _alarmType + "-get",
            "WARNING", "Test alarm for GET");
        string alarmId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        var getResult = await _client.GetAlarmAsync(alarmId);

        // Assert
        getResult.Should().NotBeNull();
        JsonElement actual = getResult!.Value;
        actual.GetProperty("type").GetString().Should().Be(_alarmType + "-get");
        actual.GetProperty("severity").GetString().Should().Be("WARNING");
        actual.GetProperty("originator").GetProperty("id").GetString().Should().Contain(_assetId);
    }

    [Fact]
    public async Task GetActiveAlarms_ReturnsCreatedAlarms()
    {
        // Arrange
        await _client.CreateAlarmAsync(_assetId, "ASSET", _alarmType + "-list1", "CRITICAL", "Critical alarm 1");
        await _client.CreateAlarmAsync(_assetId, "ASSET", _alarmType + "-list2", "MAJOR", "Major alarm 2");

        // Act
        var activeAlarms = await _client.GetActiveAlarmsAsync(_assetId, "ASSET");

        // Assert
        activeAlarms.Should().NotBeEmpty();
        activeAlarms.Should().HaveCount(2);
        activeAlarms.Should().Contain(a => a.GetProperty("severity").GetString() == "CRITICAL");
        activeAlarms.Should().Contain(a => a.GetProperty("severity").GetString() == "MAJOR");
    }

    [Fact]
    public async Task AcknowledgeAlarm_ChangesStatus()
    {
        // Arrange
        var createResult = await _client.CreateAlarmAsync(
            _assetId, "ASSET",
            _alarmType + "-ack",
            "WARNING", "Alarm to acknowledge");
        string alarmId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        await _client.AcknowledgeAlarmAsync(alarmId, "Acknowledged by integration test");

        // Wait for status update to propagate
        await Task.Delay(1000);

        // Assert
        var result = await _client.GetAlarmAsync(alarmId);
        result.Should().NotBeNull();
        JsonElement actual = result!.Value;
        actual.GetProperty("status").GetString().Should().StartWith("ACTIVE_ACK");
    }

    [Fact]
    public async Task ClearAlarm_ChangesStatus()
    {
        // Arrange
        var createResult = await _client.CreateAlarmAsync(
            _assetId, "ASSET",
            _alarmType + "-clear",
            "MINOR", "Alarm to clear");
        string alarmId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        var cleared = await _client.ClearAlarmAsync(alarmId);

        // Wait for status update to propagate
        await Task.Delay(1000);

        // Assert
        cleared.Should().BeTrue();
        var result = await _client.GetAlarmAsync(alarmId);
        result.Should().NotBeNull();
        JsonElement actual = result!.Value;
        actual.GetProperty("status").GetString().Should().StartWith("CLEARED");
    }

    [Fact]
    public async Task ClearAlarm_WithComment_ChangesStatus()
    {
        // Arrange
        var createResult = await _client.CreateAlarmAsync(
            _assetId, "ASSET",
            _alarmType + "-comment",
            "MAJOR", "Alarm with clear comment");
        string alarmId = ThingsBoardTestClient.GetShortId(createResult);

        // Act
        await _client.ClearAlarmWithCommentAsync(alarmId, "Cleared during integration test");

        // Wait for status update
        await Task.Delay(1000);

        // Assert
        var result = await _client.GetAlarmAsync(alarmId);
        result.Should().NotBeNull();
        JsonElement actual = result!.Value;
        actual.GetProperty("status").GetString().Should().StartWith("CLEARED");
    }

    [Fact]
    public async Task AlarmLifecycle_Create_Ack_Clear_FullCycle()
    {
        // Arrange
        var createResult = await _client.CreateAlarmAsync(
            _assetId, "ASSET",
            _alarmType + "-cycle",
            "CRITICAL", "Full lifecycle alarm");
        string alarmId = ThingsBoardTestClient.GetShortId(createResult);

        // Step 1: Verify initially UNACK
        var initial = await _client.GetAlarmAsync(alarmId);
        JsonElement actualInitial = initial!.Value;
        actualInitial.GetProperty("status").GetString().Should().Be("ACTIVE_UNACK");

        // Step 2: Acknowledge
        await _client.AcknowledgeAlarmAsync(alarmId, "Acknowledged");
        await Task.Delay(500);
        var acknowledged = await _client.GetAlarmAsync(alarmId);
        JsonElement actualAcknowledged = acknowledged!.Value;
        actualAcknowledged.GetProperty("status").GetString().Should().StartWith("ACTIVE_ACK");

        // Step 3: Clear
        await _client.ClearAlarmAsync(alarmId);
        await Task.Delay(500);
        var cleared = await _client.GetAlarmAsync(alarmId);
        JsonElement actualCleared = cleared!.Value;
        actualCleared.GetProperty("status").GetString().Should().StartWith("CLEARED");
    }

    [Fact]
    public async Task CreateAlarm_WithDetails_Valid()
    {
        // Arrange
        var details = new Dictionary<string, object>
        {
            { "message", "Detailed alarm description" },
            { "equipment", "Pump-1" },
            { "action_required", "Inspect valve" },
        };

        // Act
        var result = await _client.CreateAlarmAsync(
            _assetId, "ASSET",
            _alarmType + "-details",
            "WARNING", "Test with details",
            details);

        // Assert
        result.TryGetProperty("id", out _).Should().BeTrue();
        result.GetProperty("details").GetProperty("equipment").GetString().Should().Be("Pump-1");
        result.GetProperty("details").GetProperty("action_required").GetString().Should().Be("Inspect valve");
    }

    [Fact]
    public async Task GetAlarmsByType_ReturnsFilteredAlarms()
    {
        // Arrange
        var type1 = _alarmType + "-bytype-a";
        var type2 = _alarmType + "-bytype-b";
        
        await _client.CreateAlarmAsync(_assetId, "ASSET", type1, "CRITICAL", "Type A alarm");
        await _client.CreateAlarmAsync(_assetId, "ASSET", type2, "WARNING", "Type B alarm");

        // Act
        var type1Alarms = await _client.GetAlarmsByTypeAsync(_assetId, "ASSET", type1);

        // Assert
        type1Alarms.Should().ContainSingle();
        type1Alarms[0].GetProperty("type").GetString().Should().Be(type1);
    }

    private async Task CleanupAlarmByName(string alarmType)
    {
        try
        {
            var alarms = await _client.GetAlarmsByTypeAsync(_assetId, "ASSET", alarmType);
            foreach (var alarm in alarms)
            {
                var id = ThingsBoardTestClient.GetShortId(alarm);
                try { await _client.ClearAlarmAsync(id); } catch { }
            }
        }
        catch { /* ignore cleanup errors */ }
    }
}
