// tests/Ecr.Api.Tests/DocumentVersionMigrationTests.LookupRemapRowCount.cs
using Ecr.Application.Documents.VersionMigration;
using Ecr.Application.Errors;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// P2-2: SQL запису нових <c>ValueRegistryEntryId</c> звіряє кількість зачеплених рядків з планом; розбіжність —
/// <c>ECR-CELL-0409</c> і відкат переносу, а не мовчки неповна (чи надлишкова) мапа.
/// </summary>
/// <remarks>
/// Мутаційний доказ (червоніють «розбіжність…»): прибрати порівняння <c>actual != plan.LookupRemappedValues</c> у
/// <c>DocumentVersionMigrationStore.ApplyAsync</c> — виняток зникає, перенос «успішний».
/// </remarks>
public sealed partial class DocumentVersionMigrationTests
{
    private static VersionMigrationPlan RemapPlan(LookupSetup l, long oldEntry, long claimedValues)
        => new(
            Sheets: new Dictionary<int, int>(),
            Tables: new Dictionary<int, int>(),
            Columns: [],
            Rows: new Dictionary<int, int>(),
            HeaderFields: new Dictionary<int, int> { [l.OldField] = l.NewField },
            NewRows: [],
            Items: [],
            TransferredValues: 0,
            LostValues: 0,
            GuardedValues: 0)
        {
            LookupRemaps = [new VersionMigrationLookupRemapRow(l.OldField, l.FieldCode, oldEntry, l.GoodNew)],
            LookupRemappedValues = claimedValues,
        };

    private async Task<(Exception? Error, (int FieldId, long? EntryId) InsideTransaction)> ApplyRemapPlanAsync(
        LookupSetup l, VersionMigrationPlan plan)
    {
        await using var db = new EcrDbContext(new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        await using var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(true);

        Exception? error = null;
        try
        {
            await new DocumentVersionMigrationStore(db)
                .ApplyAsync(l.S.Doc.ProjectId, l.S.TargetVersionId, plan, CancellationToken.None).ConfigureAwait(true);
        }
        catch (ConcurrencyConflictException ex)
        {
            error = ex;
        }

        // Стан усередині транзакції; сама вона завжди відкочується: тест не лишає слідів.
        var inside = error is null
            ? await db.DocumentHeaderValues.AsNoTracking()
                .Where(v => v.DocumentId == l.S.Doc.DocumentId)
                .Select(v => new { v.HeaderFieldDefId, v.ValueRegistryEntryId })
                .SingleAsync().ConfigureAwait(true)
            : null;
        await tx.RollbackAsync().ConfigureAwait(true);

        return (error, inside is null ? default : (inside.HeaderFieldDefId, inside.ValueRegistryEntryId));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    public async Task Звірка_рядків_запису_збіг_з_планом_переписує_значення()
    {
        var l = await ArrangeLookupAsync().ConfigureAwait(true);

        var (error, inside) = await ApplyRemapPlanAsync(l, RemapPlan(l, l.OldEntry, claimedValues: 1)).ConfigureAwait(true);

        Assert.Null(error);
        Assert.Equal((l.NewField, (long?)l.GoodNew), inside);
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-7.5")]
    [InlineData("надлишок")]
    [InlineData("недостача")]
    [InlineData("запис_не_знайдено")]
    public async Task Звірка_рядків_запису_розбіжність_з_планом_це_ECR_CELL_0409(string kind)
    {
        var l = await ArrangeLookupAsync().ConfigureAwait(true);

        // надлишок: план обіцяв 2 значення, а в шапці одне; недостача: обіцяв 0, а мапа зачепить 1;
        // запис_не_знайдено: старого запису в шапці нема, а план вважає значення переписаним.
        var plan = kind switch
        {
            "надлишок" => RemapPlan(l, l.OldEntry, claimedValues: 2),
            "недостача" => RemapPlan(l, l.OldEntry, claimedValues: 0),
            _ => RemapPlan(l, l.OtherOldEntry, claimedValues: 1),
        };

        var (error, _) = await ApplyRemapPlanAsync(l, plan).ConfigureAwait(true);

        var conflict = Assert.IsType<ConcurrencyConflictException>(error);
        Assert.Equal("ECR-CELL-0409", conflict.ErrorCode);
        Assert.Equal((l.OldField, (long?)l.OldEntry), await StoredLookupAsync(l).ConfigureAwait(true));
    }
}
