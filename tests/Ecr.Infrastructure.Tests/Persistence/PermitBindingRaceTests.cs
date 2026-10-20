// tests/Ecr.Infrastructure.Tests/Persistence/PermitBindingRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Гонка двох збережень прив'язки аркуша до дозволу (аудит, прохід A, PA-A-11): програвший дістає 409 <c>ECR-PRM-4101</c>,
/// а не голий 500 <c>ECR-SYS-0500</c> з <c>SqlException</c> на <c>UQ_PermitSheetBinding_Sheet</c>.
/// </summary>
/// <remarks>
/// Мутаційний доказ: прибрати обидва арми <c>UQ_PermitSheetBinding_*</c> у <c>UnitOfWork.TryMapDuplicateKey</c> - червоніють
/// «Другий_запис_…» (замість 4101 - <c>DbUpdateException</c>). Гонка обробників недетермінована за побудовою (друга спроба
/// може дістати 4101 і від перевірки наперед), тож доказом мапінгу служать детерміновані тести, а гонка - кінцевий результат.
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitBindingRaceTests(SqlServerFixture sql)
{
    private const int Rounds = 20;
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Другий_запис_прив_язки_тієї_самої_таблиці_після_перевірки_дає_409_4101_а_не_помилку_провайдера()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");
        await using (var first = await h.NextRequestAsync())
        {
            await first.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", Request(sheet), default);
        }

        // «Друга» сесія вже пройшла перевірку наперед (її тут немає) і лише вставляє: так виглядає програш гонки.
        await using var loser = await h.NextRequestAsync();
        var winner = await loser.Db.PermitSheetBindings.AsNoTracking().SingleAsync(b => b.Code == $"B{_tag}");
        loser.Db.PermitSheetBindings.Add(Binding($"C{_tag}", winner.TemplateVersionId, winner.SheetTableDefId, winner));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => new UnitOfWork(loser.Db).SaveChangesAsync(default));

        Assert.Equal(("ECR-PRM-4101", "err.ECR-PRM-4101.sheetConcurrent"), (error.ErrorCode, error.Details!["messageKey"]));
        Assert.Equal(winner.SheetTableDefId.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Details["tableDefId"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Другий_запис_з_тим_самим_кодом_в_іншій_версії_дає_409_4101_code()
    {
        var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        var other = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");
        await using (var first = await h.NextRequestAsync())
        {
            await first.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{_tag}", Request(sheet), default);
        }

        await using var loser = await h.NextRequestAsync();
        var winner = await loser.Db.PermitSheetBindings.AsNoTracking().SingleAsync(b => b.Code == $"B{_tag}");
        loser.Db.PermitSheetBindings.Add(Binding($"B{_tag}", other.Doc.TemplateVersionId, other.Doc.TableDefId, winner));

        var error = await Assert.ThrowsAsync<BusinessRuleException>(() => new UnitOfWork(loser.Db).SaveChangesAsync(default));

        Assert.Equal(("ECR-PRM-4101", "err.ECR-PRM-4101.code"), (error.ErrorCode, error.Details!["messageKey"]));
        Assert.Equal($"B{_tag}", error.Details["bindingCode"]);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Два_одночасні_збереження_різних_кодів_на_одну_таблицю_дають_одну_прив_язку_і_409_4101()
    {
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");

        for (var round = 0; round < Rounds; round++)
        {
            var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
            await using var a = await h.NextRequestAsync();
            await using var b = await h.NextRequestAsync();
            var start = new TaskCompletionSource();

            async Task<Exception?> TryAsync(PermitBindingHarness x, string code)
            {
                await start.Task;
                try
                {
                    await x.Save.HandleAsync(sheet.Doc.TemplateVersionId, code, Request(sheet), default);
                    return null;
                }
                catch (BusinessRuleException ex)
                {
                    return ex;
                }
            }

            var first = TryAsync(a, $"A{round}{_tag}");
            var second = TryAsync(b, $"Z{round}{_tag}");
            start.SetResult();
            var results = await Task.WhenAll(first, second);

            Assert.Single(results, r => r is null);
            var failure = Assert.IsType<BusinessRuleException>(Assert.Single(results, r => r is not null));
            Assert.Equal("ECR-PRM-4101", failure.ErrorCode);

            await using var read = await h.NextRequestAsync();
            Assert.Single((await read.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).Items);
        }
    }

    private static PermitSheetBinding Binding(string code, int versionId, int sheetTableId, PermitSheetBinding like)
        => new(EcrCode.Create(code), versionId, sheetTableId, like.PermitTypeDefId, like.PermitTypeTableId, like.Mode, like.SelectionRule, like.HeaderFieldDefId);

    private SavePermitBindingRequest Request(PreparedSheet sheet) => new(
        sheet.Doc.TableDefId,
        $"AIR{_tag}",
        "Emissions",
        "Rows",
        "ByFacilityPeriodEnd",
        null,
        true,
        [
            new PermitBindingColumnRequest("RowKey", "Substance", sheet.Doc.ColumnDefIds[0]),
            new PermitBindingColumnRequest("Limit", "Limit", sheet.Doc.ColumnDefIds[1]),
            new PermitBindingColumnRequest("Info", "Note", sheet.NoteColumnId),
        ]);
}
