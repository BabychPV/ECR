// tests/Ecr.Infrastructure.Tests/Persistence/PermitSheetGuardRaceTests.cs
using Ecr.Application.Errors;
using Ecr.Application.Permits.Bindings;
using Ecr.Application.Ports;
using Ecr.Application.Templates;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Гонка «збереження прив'язки» проти «правки цілі прив'язки» (PM-09b2, знахідка Аналізу ч.1): охоронець прив'язок і
/// перевірка цілі в збереженні прив'язки йдуть ПІД блоком рядка версії в одній транзакції із записом - або виграє
/// прив'язка (тоді правка цілі дістає <c>409 ECR-PRM-4102</c>), або правка цілі (тоді збереження прив'язки дістає
/// <c>422</c> «таблиці немає»). Обидві не виграють ніколи.
/// </summary>
/// <remarks>
/// <para>
/// Детермінованість: пауза стоїть у <c>ITemplateVersionStore.LockVersionForUpdateAsync</c> - рівно ПЕРЕД блоком версії,
/// тобто між «охоронець/перевірка» (у старому коді вони йшли до транзакції) і «коміт». Поки перша сторона стоїть на паузі,
/// друга виконується ЦІЛКОМ і комітить; далі пауза знімається. Нічого не залежить від таймінгу.
/// </para>
/// <para>
/// Мутаційні докази (запущені): (1) повернути ВСІ п'ять охоронців (Table/Sheet/Column/SaveTable) ДО транзакції -
/// червоніє «Правка_цілі…» і називає усі п'ять правок (обидві сторони перемогли); (2) повернути читання цілі в
/// <c>SavePermitBindingHandler</c> ДО блоку версії - червоніє «Збереження_прив_язки…» (прив'язка на видаленій таблиці).
/// </para>
/// </remarks>
[Collection("SqlServer")]
public sealed class PermitSheetGuardRaceTests(SqlServerFixture sql)
{
    private const int Rounds = 20;
    private readonly string _tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_цілі_що_стартувала_до_прив_язки_але_блок_взяла_після_неї_дістає_4102_а_ціль_не_змінена()
    {
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");

        var operations = new (string Name, string MessageKey, Func<PermitBindingHarness, PreparedSheet, Task> Run, Func<PermitBindingHarness, PreparedSheet, Task<bool>> Intact)[]
        {
            (
                "Таблиця_видалення",
                "err.ECR-PRM-4102.table",
                async (x, s) => await x.DeleteTable.HandleAsync(s.Doc.TemplateVersionId, s.Doc.SheetCode, await TableCodeAsync(x, s), default),
                async (x, s) => !(await x.Db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == s.Doc.TableDefId)).IsDeleted),
            (
                "Аркуш_видалення",
                "err.ECR-PRM-4102.sheet",
                (x, s) => x.DeleteSheet.HandleAsync(s.Doc.TemplateVersionId, s.Doc.SheetCode, default),
                async (x, s) => !(await x.Db.SheetDefs.AsNoTracking().SingleAsync(t => t.Id == s.Doc.SheetDefId)).IsDeleted),
            (
                "Колонка_видалення",
                "err.ECR-PRM-4102.column",
                async (x, s) => await x.DeleteColumn.HandleAsync(
                    s.Doc.TemplateVersionId, s.Doc.TableDefId, await ColumnCodeAsync(x, s.Doc.ColumnDefIds[0]), default),
                async (x, s) => !(await x.Db.ColumnDefs.AsNoTracking().SingleAsync(c => c.Id == s.Doc.ColumnDefIds[0])).IsDeleted),
            (
                "Режим_рядків_Fixed",
                "err.ECR-PRM-4102.rowMode",
                async (x, s) => await x.SaveTable.HandleAsync(
                    s.Doc.TemplateVersionId, s.Doc.SheetCode, await TableCodeAsync(x, s), Command(TableRowMode.Fixed), default),
                async (x, s) => (await x.Db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == s.Doc.TableDefId)).RowMode == TableRowMode.Dynamic),
            (
                "Стеля_рядків_знижена",
                "err.ECR-PRM-4102.maxRows",
                async (x, s) => await x.SaveTable.HandleAsync(
                    s.Doc.TemplateVersionId, s.Doc.SheetCode, await TableCodeAsync(x, s), Command(TableRowMode.Dynamic, 5), default),
                async (x, s) => (await x.Db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == s.Doc.TableDefId)).MaxDynamicRows == 10),
        };

        var failedOperations = new List<string>();
        foreach (var (operation, index) in operations.Select((o, i) => (o, i)))
        {
            for (var round = 0; round < Rounds; round++)
            {
                var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
                await SetMaxAsync(sheet, 10);

                var gate = new LockGate();
                await using var editor = await h.NextRequestAsync(gate.Wrap);
                var edit = Task.Run(() => operation.Run(editor, sheet));

                // Правка цілі дійшла до блоку версії (у старому коді охоронець уже пройдений) і стоїть.
                await gate.ReachedAsync();

                // Прив'язка зберігається й комітиться ЦІЛКОМ, поки правка стоїть.
                await using (var saver = await h.NextRequestAsync())
                {
                    await saver.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{index}x{round}{_tag}", Request(sheet), default);
                }

                gate.Release();

                // Виграти мала прив'язка: правка цілі відмовлена 409 4102, ціль не змінена, прив'язка одна. Провали збираються по
                // правках (а не зупиняють цикл на першій), щоб мутація «усі охоронці повернуті до транзакції» назвала всі п'ять.
                var outcome = await Record.ExceptionAsync(() => edit);
                await using var read = await h.NextRequestAsync();
                var intact = await operation.Intact(read, sheet);
                var bindingCount = (await read.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).Items.Count;

                var refused = outcome is BusinessRuleException { ErrorCode: "ECR-PRM-4102" } rule
                    && (string?)rule.Details!["messageKey"] == operation.MessageKey;
                if (!refused || !intact || bindingCount != 1)
                {
                    failedOperations.Add($"{operation.Name} (раунд {round}: відмова={refused}, ціль_ціла={intact}, прив'язок={bindingCount}, результат={outcome?.GetType().Name ?? "успіх"})");
                    break;
                }
            }
        }

        Assert.True(failedOperations.Count == 0, "Прив'язка не виграла чисто (правка цілі пройшла під прив'язкою або ціль змінена): " + string.Join("; ", failedOperations));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Збереження_прив_язки_що_стартувало_до_видалення_цілі_але_блок_взяло_після_нього_дістає_422_і_прив_язки_немає()
    {
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");

        for (var round = 0; round < Rounds; round++)
        {
            var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);

            var gate = new LockGate();
            await using var saver = await h.NextRequestAsync(gate.Wrap);
            var save = Task.Run(() => saver.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"B{round}{_tag}", Request(sheet), default));

            // Збереження прив'язки дійшло до блоку версії (у старому коді перевірку цілі вже пройдено) і стоїть.
            await gate.ReachedAsync();

            // Таблиця видаляється й комітиться ЦІЛКОМ: прив'язки ще немає, охоронець пропускає.
            await using (var deleter = await h.NextRequestAsync())
            {
                await deleter.DeleteTable.HandleAsync(
                    sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, await TableCodeAsync(deleter, sheet), default);
            }

            gate.Release();

            var error = await Assert.ThrowsAsync<BusinessRuleException>(() => save);
            Assert.True(error.ErrorCode == "ECR-PRM-0422", $"раунд {round}: {error.ErrorCode} {error.Details!["messageKey"]}");

            await using var read = await h.NextRequestAsync();
            Assert.Empty((await read.List.HandleAsync(sheet.Doc.TemplateVersionId, default)).Items);
            Assert.True((await read.Db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == sheet.Doc.TableDefId)).IsDeleted);
        }
    }

    /// <summary>
    /// «Старе» значення для порівняння береться з бази ПІД блоком версії, а не з графа, прочитаного до нього: друга з двох
    /// паралельних правок порівнюється з уже зміненим станом переможця. Перший запит (10 → 15) стоїть перед блоком, другий
    /// піднімає стелю 10 → 20 і комітить; перший після блоку бачить 20 і ЗНИЖУЄ її до 15 - відмова з <c>previousMax = 20</c>
    /// (за застарілим 10 це «підняття», і стеля тихо впала б до 15). Мутація (запущена): порівнювати зі значенням графа
    /// (<c>existing.MaxDynamicRows</c>) - червоніє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Друга_паралельна_правка_стелі_порівнюється_з_уже_зміненою_стелею_а_не_із_застарілою()
    {
        await using var h = await PermitBindingHarness.CreateAsync(sql, PermitBindingHarness.Everything);
        await h.PublishEmissionsTypeAsync($"AIR{_tag}");

        for (var round = 0; round < Rounds; round++)
        {
            var sheet = await PermitBindingHarness.PrepareSheetAsync(sql, withDocument: false);
            await SetMaxAsync(sheet, 10);
            await using (var binder = await h.NextRequestAsync())
            {
                await binder.Save.HandleAsync(sheet.Doc.TemplateVersionId, $"M{round}{_tag}", Request(sheet), default);
            }

            var gate = new LockGate();
            await using var lowering = await h.NextRequestAsync(gate.Wrap);
            var tableCode = await TableCodeAsync(lowering, sheet);
            var lower = Task.Run(() => lowering.SaveTable.HandleAsync(
                sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, tableCode, Command(TableRowMode.Dynamic, 15), default));
            await gate.ReachedAsync();

            await using (var raising = await h.NextRequestAsync())
            {
                await raising.SaveTable.HandleAsync(
                    sheet.Doc.TemplateVersionId, sheet.Doc.SheetCode, tableCode, Command(TableRowMode.Dynamic, 20), default);
            }

            gate.Release();

            var outcome = await Record.ExceptionAsync(() => lower);
            await using var read = await h.NextRequestAsync();
            var max = (await read.Db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == sheet.Doc.TableDefId)).MaxDynamicRows;

            var refused = outcome is BusinessRuleException { ErrorCode: "ECR-PRM-4102" } rule
                && (string?)rule.Details!["messageKey"] == "err.ECR-PRM-4102.maxRows"
                && (string?)rule.Details["previousMax"] == "20";
            Assert.True(
                refused && max == 20,
                $"Раунд {round}: відмова зі старою стелею 20={refused}, стеля в базі={max}, результат={outcome?.GetType().Name ?? "успіх"}.");
        }
    }

    private static SaveTableDefCommand Command(TableRowMode mode, int? maxDynamicRows = null)
        => new(new Dictionary<string, string> { ["en"] = "Table" }, null, TableLayoutKind.PerPeriodInstance, mode, maxDynamicRows);

    private async Task SetMaxAsync(PreparedSheet sheet, int max)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var table = await db.TableDefs.SingleAsync(t => t.Id == sheet.Doc.TableDefId);
        table.SetMaxDynamicRows(max);
        await db.SaveChangesAsync();
    }

    private static Task<string> TableCodeAsync(PermitBindingHarness h, PreparedSheet sheet)
        => h.Db.TableDefs.AsNoTracking().Where(t => t.Id == sheet.Doc.TableDefId).Select(t => t.Code).SingleAsync();

    private static Task<string> ColumnCodeAsync(PermitBindingHarness h, int columnId)
        => h.Db.ColumnDefs.AsNoTracking().Where(c => c.Id == columnId).Select(c => c.Code).SingleAsync();

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

    /// <summary>
    /// Пауза перед блоком версії: перший виклик <c>LockVersionForUpdateAsync</c> сигналить «дійшов» і чекає
    /// <see cref="Release"/>. Решту портів передає справжньому сховищу.
    /// </summary>
    private sealed class LockGate
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ReachedAsync() => _reached.Task.WaitAsync(TimeSpan.FromSeconds(60));

        public void Release() => _release.TrySetResult();

        public ITemplateVersionStore Wrap(ITemplateVersionStore real)
        {
            var gated = Substitute.For<ITemplateVersionStore>();
            gated.GetWithStructureAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => real.GetWithStructureAsync(call.ArgAt<int>(0), call.ArgAt<CancellationToken>(1)));
            gated.HasDocumentsAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call => real.HasDocumentsAsync(call.ArgAt<int>(0), call.ArgAt<CancellationToken>(1)));
            gated.LockVersionForUpdateAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(async call =>
                {
                    _reached.TrySetResult();
                    await _release.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                    return await real.LockVersionForUpdateAsync(call.ArgAt<int>(0), call.ArgAt<CancellationToken>(1)).ConfigureAwait(false);
                });

            return gated;
        }
    }
}
