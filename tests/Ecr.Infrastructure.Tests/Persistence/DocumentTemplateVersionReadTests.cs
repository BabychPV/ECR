// tests/Ecr.Infrastructure.Tests/Persistence/DocumentTemplateVersionReadTests.cs
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// B74 P2 (пункт 1): гарячі читання екземплярів документа беруть версію шкали ШАБЛОНУ документа
/// (<c>doc.fn_TemplateVersionOf(d.ProjectId, d.TemplateId, …)</c>) напряму, а не через вкладену <c>doc.fn_VersionOf</c>,
/// яка відповідає за ОСНОВНИЙ шаблон проєкту.
/// </summary>
/// <remarks>
/// Проєкт із двома шаблонами: A (основний — шаблон <c>Project.TemplateVersionId</c>) зі шкалою A, B — приєднаний сирим SQL
/// (<c>doc.ProjectTemplate</c> + шкала B, чинна версія B — інша, ніж у шкалі за період). Документ B мусить отримати
/// версію шкали B за період; <c>fn_VersionOf</c> дала б версію A — тобто тест червоний до правки (доказ — прогін на
/// cb178a970 з цим тестом). <c>TemplateId = NULL</c> (рядок старої збірки до ремонту) — запасна гілка: основний шаблон,
/// рівно як досі. Документ A (основний шаблон) — та сама версія і з NULL, і з явним TemplateId.
/// ⚠ Рік 2031: періоди, яких не архівують/відновлюють інші тести (<c>ArchiveJobTests</c> 2027, <c>RestoreYearTests</c>).
/// </remarks>
[Collection("SqlServer")]
public sealed class DocumentTemplateVersionReadTests(SqlServerFixture sql)
{
    private const int Hot = 203104;
    private const int Archived = 203105;

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B74")]
    public async Task Гарячі_читання_дають_документу_шаблону_B_версію_шкали_B()
    {
        var s = await SeedTwoTemplateProjectAsync();
        try
        {
            // Передумова: стара функція відповідає за основний шаблон — інакше тест нічого не доводив би.
            Assert.Equal(s.A.TemplateVersionId, await ScalarAsync($"SELECT doc.fn_VersionOf({s.A.ProjectId}, {Hot})"));
            Assert.NotEqual(s.B.TemplateVersionId, s.CurrentB);

            Assert.Equal(s.B.TemplateVersionId, (await ReadAllAsync(s.B, s.ArchivedB)).Distinct().Single());
            Assert.Equal(s.A.TemplateVersionId, (await ReadAllAsync(s.A, archivedInstance: null)).Distinct().Single());

            // Явний TemplateId основного шаблону — та сама версія, що й запасна гілка NULL.
            await ExecuteAsync($"UPDATE doc.Document SET TemplateId = {s.A.TemplateId} WHERE Id = {s.A.DocumentId};");
            Assert.Equal(s.A.TemplateVersionId, (await ReadAllAsync(s.A, archivedInstance: null)).Distinct().Single());
        }
        finally
        {
            await CleanupAsync(s);
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "B74")]
    public async Task TemplateId_NULL_дає_версію_основного_шаблону_як_досі()
    {
        var s = await SeedTwoTemplateProjectAsync();
        try
        {
            // Рядок старої збірки у вікні відкату: шаблону немає — відповідь та сама, що в doc.fn_VersionOf (основний A).
            await ExecuteAsync($"UPDATE doc.Document SET TemplateId = NULL WHERE Id = {s.B.DocumentId};");
            var expected = await ScalarAsync($"SELECT doc.fn_VersionOf({s.B.ProjectId}, {Hot})");

            Assert.Equal(s.A.TemplateVersionId, expected);
            Assert.Equal(expected, (await ReadAllAsync(s.B, s.ArchivedB)).Distinct().Single());
        }
        finally
        {
            await CleanupAsync(s);
        }
    }

    /// <summary>Версії з кожного зміненого читання екземплярів документа (гарячі, рік, архів, версія за період).</summary>
    private async Task<List<int>> ReadAllAsync(TestDocument doc, long? archivedInstance)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await using var db = builder.CreateContext();
        var clock = new TestClock(new DateTime(2031, 4, 15, 10, 0, 0, DateTimeKind.Utc));
        var rows = new RowStore(db, new BulkCellLoader(sql.ConnectionString, 1000), clock);
        var versions = new List<int>();

        var year = await new CalculationResultStore(db, clock).ReadTableInstancesAsync(doc.DocumentId, [Hot], CancellationToken.None);
        versions.Add(Assert.Single(year).TemplateVersionId);

        var hot = await rows.GetTableInstancesAsync(doc.DocumentId, new PeriodKey(Hot), CancellationToken.None);
        versions.Add(Assert.Single(hot).TemplateVersionId);

        var resolved = await rows.ResolveTableInstancesAsync([doc.TableInstanceId], CancellationToken.None);
        versions.Add(resolved[doc.TableInstanceId].TemplateVersionId);

        var ofYear = await rows.GetTableInstancesOfTableInYearAsync(doc.DocumentId, doc.TableDefId, 2031, CancellationToken.None);
        versions.Add(Assert.Single(ofYear).TemplateVersionId);

        versions.Add(await new DocumentStore(db).GetTemplateVersionIdForPeriodAsync(doc.DocumentId, Hot, CancellationToken.None));

        if (archivedInstance is { } id)
        {
            var archived = await new ArchiveAwareCellReader(db)
                .ReadArchivedTableInstancesAsync(doc.DocumentId, new PeriodKey(Archived), CancellationToken.None);
            var instance = Assert.Single(archived);
            Assert.Equal(id, instance.TableInstanceId);
            versions.Add(instance.TemplateVersionId);
        }

        Assert.Equal(archivedInstance is null ? 5 : 6, versions.Count);
        return versions;
    }

    /// <summary>Засів: проєкт документа A з двома шаблонами; документ B переноситься в нього з шаблоном B.</summary>
    private sealed record Seed(TestDocument A, TestDocument B, int CurrentB, long ArchivedB, int SpareProjectId);

    private async Task<Seed> SeedTwoTemplateProjectAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var a = await builder.BuildAsync(periodKey: Hot, ct: CancellationToken.None);
        var built = await builder.BuildAsync(periodKey: Hot, ct: CancellationToken.None);
        var archivedId = await new BulkCellLoader(sql.ConnectionString, 1000)
            .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);

        // Друга версія шаблону B — чинна (ProjectTemplate), але НЕ версія шкали за 2031-04: читання мусить брати шкалу.
        var currentB = await ScalarAsync($"""
            SET NOCOUNT ON;
            INSERT cfg.TemplateVersion (TemplateId, Version, Status, PublishedAt, PublishedByUserId, CreatedAt, CreatedByUserId)
            VALUES ({built.TemplateId}, N'9.9.{built.TemplateVersionId}', 1, '2026-01-01', 1, '2026-01-01', 1);
            SELECT CAST(SCOPE_IDENTITY() AS int);
            """);

        await ExecuteAsync($"""
            UPDATE doc.Document SET ProjectId = {a.ProjectId}, TemplateId = {built.TemplateId} WHERE Id = {built.DocumentId};

            INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId)
            SELECT {a.ProjectId}, {a.TemplateId}, {a.TemplateVersionId}
            WHERE NOT EXISTS (SELECT 1 FROM doc.ProjectTemplate WHERE ProjectId = {a.ProjectId} AND TemplateId = {a.TemplateId});
            INSERT doc.ProjectTemplate (ProjectId, TemplateId, CurrentTemplateVersionId)
            VALUES ({a.ProjectId}, {built.TemplateId}, {currentB});

            INSERT doc.ProjectTemplateVersion (ProjectId, TemplateId, FromPeriodKey, TemplateVersionId)
            VALUES ({a.ProjectId}, {a.TemplateId}, 203101, {a.TemplateVersionId}),
                   ({a.ProjectId}, {built.TemplateId}, 203101, {built.TemplateVersionId}),
                   ({a.ProjectId}, {built.TemplateId}, 203107, {currentB});

            INSERT arc.TableInstance (PeriodKey, Id, DocumentId, TableDefId, CreatedAt, ModifiedAt)
            VALUES ({Archived}, {archivedId}, {built.DocumentId}, {built.TableDefId}, '2031-05-01', '2031-05-01');
            """);

        return new Seed(a, built with { ProjectId = a.ProjectId }, currentB, archivedId, built.ProjectId);
    }

    /// <summary>Прибирає те, що засів додав поза будівником (спільна база колекції).</summary>
    private Task CleanupAsync(Seed s) => ExecuteAsync($"""
        DELETE FROM arc.TableInstance WHERE PeriodKey = {Archived} AND Id = {s.ArchivedB};
        DELETE FROM doc.ProjectTemplateVersion WHERE ProjectId = {s.A.ProjectId};
        DELETE FROM doc.ProjectTemplate WHERE ProjectId = {s.A.ProjectId} AND TemplateId = {s.B.TemplateId};
        UPDATE doc.Document SET ProjectId = {s.SpareProjectId}, TemplateId = NULL WHERE Id = {s.B.DocumentId};
        """);

    private async Task ExecuteAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarAsync(string text)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = text;
        return Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
