using System.Reflection;
using System.Text.Json;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Xunit;

namespace Ecr.Application.Tests.Integration;

/// <summary>
/// D9: перелік сутностей збору читає й користувач лише з Integration.View, тому його
/// відповідь — закритий список полів. Нове поле (особливо endpoint, секрет, ім'я секрету,
/// рядок підключення) червонить цей тест, доки його свідомо не додано сюди.
/// </summary>
public sealed class ListSourceEntitiesResponseShapeTests
{
    private static readonly string[] Allowed =
    [
        "id", "code", "displayName", "entityPath", "transport", "isActive", "lastRun", "oldestGap",
        "dataSourceId", "dataSourceCode", "onMissingInSource", "validFromAttribute", "validToAttribute",
        "validToInclusive", "registryDefId",
    ];

    private static readonly string[] AllowedLastRun = ["finishedAt", "status", "pointsRetrieved"];

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void DTO_переліку_має_лише_дозволені_поля()
    {
        var names = typeof(SourceEntityStatus).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name));

        Assert.Equal(Allowed.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));

        var runNames = typeof(CollectionRunStatus).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name));
        Assert.Equal(AllowedLastRun.Order(StringComparer.Ordinal), runNames.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Серіалізована_відповідь_не_несе_секретів_адреси_і_імені_секрету()
    {
        var row = new SourceEntityStatus(
            1, "Flare01", "Flare", @"\\AF\ECR", "PiWebApi", true,
            new CollectionRunStatus(DateTime.UtcNow, "Succeeded", 5), DateTime.UtcNow,
            7, "PI", RegistryMissingPolicy.MarkOrphaned, null, null, false, 3);

        var json = JsonSerializer.Serialize(new[] { row }, Web);
        using var doc = JsonDocument.Parse(json);

        var keys = doc.RootElement[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal);
        Assert.Equal(Allowed.Order(StringComparer.Ordinal), keys);

        foreach (var forbidden in new[] { "endpoint", "secret", "password", "connectionString", "userinfo", "DataSource." })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }
}
