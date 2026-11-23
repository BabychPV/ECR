// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyStoreReadFieldsTests.cs
using Ecr.Application.Calculations.Scripting;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// ✎ D-568 (огляд b9, п. 4): <see cref="MethodologyStore.ReplaceScriptReadFieldsAsync"/> переписує перелік полів читання
/// версії і тоді, коли рядків понад стелю завантаження (<c>MaxChildren</c> = 10 000): частковий перелік давав вставку поля,
/// що вже лежить за стелею, — порушення <c>UQ_MethodologyReadField</c> і провал публікації.
/// </summary>
/// <remarks>
/// Шляхи: перелік у межах стелі (порівняння, як доти; та сама назва з іншою ознакою «інший місяць» — заміна) і понад стелю
/// (видалення одним оператором і вставка). МУТАЦІЯ (локально): гілку «понад стелю» вимкнено — червоний тест 1.
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Decision", "D-568")]
public sealed class MethodologyStoreReadFieldsTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 11, 23, 8, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(10_001)] // на межі: усі рядки завантажено (стеля + 1), порівняння
    [InlineData(10_002)] // понад межу: видалення одним оператором і вставка
    public async Task Понад_стелю_рядків_перелік_переписується_без_порушення_унікальності(int rows)
    {
        var versionId = await VersionAsync();
        await SeedRowsAsync(versionId, rows);
        var last = "F" + (rows - 1).ToString("00000", System.Globalization.CultureInfo.InvariantCulture);

        await using (var db = sql.CreateContext())
        {
            await new MethodologyStore(db).ReplaceScriptReadFieldsAsync(
                versionId, [new ScriptReadField(last, false), new ScriptReadField("NEWX", true)], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            [(MethodologyReadField.RecordedMarker, false), (last, false), ("NEWX", true)],
            await RowsAsync(versionId));
    }

    [Fact]
    public async Task У_межах_стелі_перелік_порівнюється_і_замінюється()
    {
        var versionId = await VersionAsync();
        await using (var db = sql.CreateContext())
        {
            db.MethodologyReadFields.AddRange(
                new MethodologyReadField(versionId, MethodologyReadField.RecordedMarker, otherMonths: false),
                new MethodologyReadField(versionId, "A", otherMonths: false),
                new MethodologyReadField(versionId, "B", otherMonths: false));
            await db.SaveChangesAsync();
        }

        await using (var db = sql.CreateContext())
        {
            await new MethodologyStore(db).ReplaceScriptReadFieldsAsync(
                versionId, [new ScriptReadField("B", true), new ScriptReadField("C", false)], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        Assert.Equal(
            [(MethodologyReadField.RecordedMarker, false), ("B", true), ("C", false)],
            await RowsAsync(versionId));
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    private async Task<int> VersionAsync()
    {
        await using var db = sql.CreateContext();
        var methodology = new Methodology(
            EcrCode.Create("RF4_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "read fields" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();
        return version.Id;
    }

    /// <summary>Маркер запису + <paramref name="count"/> − 1 полів <c>F00001…</c> одним оператором.</summary>
    private async Task SeedRowsAsync(int versionId, int count)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = """
            INSERT INTO calc.MethodologyReadField (MethodologyVersionId, FieldCode, OtherMonths)
            VALUES (@v, @marker, 0);
            INSERT INTO calc.MethodologyReadField (MethodologyVersionId, FieldCode, OtherMonths)
            SELECT TOP (@n) @v, N'F' + RIGHT(N'00000' + CAST(ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS nvarchar(10)), 5), 0
              FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
             ORDER BY ROW_NUMBER() OVER (ORDER BY (SELECT 1));
            """;
        command.Parameters.AddWithValue("@v", versionId);
        command.Parameters.AddWithValue("@marker", MethodologyReadField.RecordedMarker);
        command.Parameters.AddWithValue("@n", count - 1);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<(string Code, bool OtherMonths)>> RowsAsync(int versionId)
    {
        await using var db = sql.CreateContext();
        var rows = await db.MethodologyReadFields.AsNoTracking()
            .Where(f => f.MethodologyVersionId == versionId)
            .Select(f => new { f.FieldCode, f.OtherMonths })
            .ToListAsync();
        return [.. rows.Select(r => (r.FieldCode, r.OtherMonths)).OrderBy(r => r.FieldCode == MethodologyReadField.RecordedMarker ? 0 : 1).ThenBy(r => r.FieldCode, StringComparer.Ordinal)];
    }
}
