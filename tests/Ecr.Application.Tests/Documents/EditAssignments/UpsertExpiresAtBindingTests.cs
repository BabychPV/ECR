using System.Text.Json;
using Ecr.Application.Documents.EditAssignments;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Documents.EditAssignments;

/// <summary>
/// EA6-D2: тіло upsert розрізняє «поле відсутнє» (не змінювати), «null» (зняти строк) і значення.
/// </summary>
public sealed class UpsertExpiresAtBindingTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static UpsertEditAssignmentRequest Parse(string json)
        => JsonSerializer.Deserialize<UpsertEditAssignmentRequest>(json, Options)!;

    private const string Base = "\"slice\":{\"sheetCode\":\"F1\"},\"recipientUserIds\":[7]";

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Відсутнє_поле_не_вказане_а_null_і_значення_вказані()
    {
        var absent = Parse("{" + Base + "}");
        Assert.False(absent.ExpiresAtSpecified);
        Assert.Null(absent.ExpiresAt);

        var explicitNull = Parse("{" + Base + ",\"expiresAt\":null}");
        Assert.True(explicitNull.ExpiresAtSpecified);
        Assert.Null(explicitNull.ExpiresAt);

        var value = Parse("{" + Base + ",\"expiresAt\":\"2026-12-01T00:00:00Z\"}");
        Assert.True(value.ExpiresAtSpecified);
        Assert.Equal(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), value.ExpiresAt!.Value.ToUniversalTime());
    }
}
