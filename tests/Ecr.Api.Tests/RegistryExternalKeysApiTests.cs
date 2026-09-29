// tests/Ecr.Api.Tests/RegistryExternalKeysApiTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Dictionaries;
using Ecr.Domain.Entities.External;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Зовнішні ідентифікатори записів довідника на HTTP (<c>ФВ-8.10</c>, FEATURE-REGISTRY-SYNC S2).
/// </summary>
/// <remarks>
/// ⛔ Доказ саме на HTTP: дубль мапить у <c>409</c> окремий арм
/// <c>ExceptionHandlingMiddleware</c> (<c>BusinessRuleException</c> з <c>ECR-REG-0409</c>) —
/// обробниковий тест лишався б зеленим, поки клієнт бачить <c>422</c>.
/// <para>
/// ⚠ Синк (<c>RegistrySyncJob.LinksAsync</c>) читає рівно рядок <c>dic.RegistryExternalKey</c>
/// (запис, джерело, <c>ExternalId</c>) — його й звіряє перший тест. Окремого прогону синку тут
/// немає: стенд задачі (каталог, джерело, мапінг) живе в <c>RegistrySyncJobTests</c>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class RegistryExternalKeysApiTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Прив_язка_201_дубль_409_перелік_і_відв_язка_204()
    {
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.EditData", "Registry.View").ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);
        var guid = Guid.NewGuid().ToString("D");

        var created = await client.PostAsJsonAsync(
            Uri(stand.Code), new { entryId = stand.EntryId, dataSourceId = stand.DataSourceId, externalId = guid });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await JsonAsync(created).ConfigureAwait(true);
        var id = dto.GetProperty("id").GetInt64();
        Assert.Equal(stand.EntryId, dto.GetProperty("registryEntryId").GetInt64());
        Assert.Equal(stand.SourceCode, dto.GetProperty("dataSourceCode").GetString());

        // Рядок у базі — той самий, що читає синк: запис × джерело × ExternalId.
        var row = await KeyAsync(id).ConfigureAwait(true);
        Assert.NotNull(row);
        Assert.Equal((stand.EntryId, stand.DataSourceId, guid), (row.RegistryEntryId, row.DataSourceId, row.ExternalId));
        Assert.Equal(1, await AuditCountAsync(id, "Bind").ConfigureAwait(true));

        // Той самий елемент джерела на інший запис — 409, а не другий рядок.
        var duplicate = await client.PostAsJsonAsync(
            Uri(stand.Code), new { entryId = stand.OtherEntryId, dataSourceId = stand.DataSourceId, externalId = guid });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        var problem = await JsonAsync(duplicate).ConfigureAwait(true);
        Assert.Equal("ECR-REG-0409", problem.GetProperty("errorCode").GetString());
        Assert.Equal("err.ECR-REG-0409.externalKeyTaken", problem.GetProperty("messageKey").GetString());

        var list = await JsonAsync(
            await client.GetAsync(new Uri($"{Uri(stand.Code)}?entryId={stand.EntryId}", UriKind.Relative)).ConfigureAwait(true))
            .ConfigureAwait(true);
        var item = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(guid, item.GetProperty("externalId").GetString());

        var deleted = await client.DeleteAsync(new Uri($"{Uri(stand.Code)}/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(await KeyAsync(id).ConfigureAwait(true));
        Assert.Equal(1, await AuditCountAsync(id, "Unbind").ConfigureAwait(true));

        var again = await client.DeleteAsync(new Uri($"{Uri(stand.Code)}/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Без_права_на_дані_довідника_прив_язка_і_відв_язка_дають_403()
    {
        // ⚠ Автентифікований зі СТОРОННІМИ правами: Integration.Manage не рахується (S2).
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, "Registry.View", "Integration.Manage").ConfigureAwait(true);
        var stand = await StandAsync().ConfigureAwait(true);
        var keyId = await SeedKeyAsync(stand).ConfigureAwait(true);

        var bind = await client.PostAsJsonAsync(
            Uri(stand.Code), new { entryId = stand.EntryId, dataSourceId = stand.DataSourceId, externalId = "DENIED" });
        Assert.Equal(HttpStatusCode.Forbidden, bind.StatusCode);

        var unbind = await client.DeleteAsync(new Uri($"{Uri(stand.Code)}/{keyId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Forbidden, unbind.StatusCode);

        Assert.NotNull(await KeyAsync(keyId).ConfigureAwait(true));
        Assert.Equal(1, await CountForEntryAsync(stand.EntryId).ConfigureAwait(true));

        // Перелік — лише Registry.View, і він доступний.
        var list = await client.GetAsync(Uri(stand.Code));
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-8.10")]
    public async Task Гонка_за_парою_в_сховищі_дає_ту_саму_відмову_409_а_не_збій_бази()
    {
        // Обидві прив'язки пройшли перевірку обробника; розводить їх лише UQ_RegistryExternalKey.
        var stand = await StandAsync().ConfigureAwait(true);
        var guid = Guid.NewGuid().ToString("D");

        await using (var first = Context())
        {
            await new RegistryExternalKeyStore(first)
                .AddAsync(new RegistryExternalKey(stand.EntryId, stand.DataSourceId, guid), CancellationToken.None)
                .ConfigureAwait(true);
        }

        await using var second = Context();
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => new RegistryExternalKeyStore(second).AddAsync(
                new RegistryExternalKey(stand.OtherEntryId, stand.DataSourceId, guid), CancellationToken.None));

        Assert.Equal("ECR-REG-0409", ex.ErrorCode);
        Assert.Equal("err.ECR-REG-0409.externalKeyTakenConcurrently", ex.Details!["messageKey"]);
        Assert.Equal(0, await CountForEntryAsync(stand.OtherEntryId).ConfigureAwait(true));
    }

    private static Uri Uri(string code) => new($"/api/v1/registries/{code}/external-keys", UriKind.Relative);

    private sealed record Stand(string Code, long EntryId, long OtherEntryId, int DataSourceId, string SourceCode);

    private async Task<Stand> StandAsync()
    {
        await using var db = Context();
        var tag = $"{Guid.NewGuid():N}"[..8];

        var source = new DataSource(
            EcrCode.Create($"XK{tag}"), Name("PI"), ExternalTransport.PiWebApi, "https://example.test", "secret");
        var registry = new RegistryDef(EcrCode.Create($"XKR{tag}"), Name("Flares"), isTemporal: false);
        db.DataSources.Add(source);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var entry = new RegistryEntry(registry.Id, EcrCode.Create("FL01"), Name("FL01"));
        var other = new RegistryEntry(registry.Id, EcrCode.Create("FL02"), Name("FL02"));
        db.RegistryEntries.AddRange(entry, other);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new Stand(registry.Code, entry.Id, other.Id, source.Id, source.Code);
    }

    private async Task<long> SeedKeyAsync(Stand stand)
    {
        await using var db = Context();
        var key = new RegistryExternalKey(stand.EntryId, stand.DataSourceId, Guid.NewGuid().ToString("D"));
        db.RegistryExternalKeys.Add(key);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return key.Id;
    }

    private async Task<RegistryExternalKey?> KeyAsync(long id)
    {
        await using var db = Context();
        return await db.RegistryExternalKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id).ConfigureAwait(false);
    }

    private async Task<int> CountForEntryAsync(long entryId)
    {
        await using var db = Context();
        return await db.RegistryExternalKeys.AsNoTracking().CountAsync(k => k.RegistryEntryId == entryId).ConfigureAwait(false);
    }

    private async Task<int> AuditCountAsync(long keyId, string operation)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM aud.StructureChange "
            + "WHERE EntityType = N'dic.RegistryExternalKey' AND EntityId = @id AND Operation = @op;";
        command.Parameters.AddWithValue("@id", checked((int)keyId));
        command.Parameters.AddWithValue("@op", operation);

        return (int)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
        => JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;

    private EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private Task<HttpClient> SignedInAsync(EcrApiFactory app, params string[] permissions)
        => SystemHealthControllerTests.SignedInAsync(sql, app, permissions);
}
