// tests/Ecr.Api.Tests/Security/DenyReadTests.ExportFormulas.cs
using Ecr.TestKit;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// ФВ-4.2 + ФВ-6.6: експорт з <c>includeFormulas</c> не віддає вираз колонки, що називає колонку чи
/// таблицю, закриті для того, хто замовив файл. Наскрізно: справжній SQL, справжня задача в черзі.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>DocumentDataExporter.ColumnFormulas</c> віддавати
/// <c>chosen.Expression</c> без перевірки посилань — червоніють рядки <c>reader</c> (csv, json);
/// регресійні лишаються зеленими. (Вирази без «+»: JSON-кодувальник пише його як u002B.) xlsx — охоронний тест: трансляція в A1 кладе формулу лише коли
/// всі посилання є в координатах книги (прихована колонка → <c>#REF!</c> → комірка значенням).
/// </remarks>
public sealed partial class DenyReadTests
{
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    [InlineData("xlsx")]
    [InlineData("csv")]
    [InlineData("json")]
    public async Task Експорт_з_формулами_не_віддає_вираз_з_посиланням_на_заборонену_колонку(string format)
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var visibleRef = $"[{s.ColumnCodes[1]}] * 2";
        var hiddenRef = $"[{s.ColumnCodes[2]}] - 1";
        await AddColumnFormulasAsync(s, (s.Doc.ColumnDefIds[0], visibleRef), (s.Doc.ColumnDefIds[1], hiddenRef))
            .ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);

        using (var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true))
        {
            var text = await ExportTextAsync(app, reader, s, format, includeFormulas: true).ConfigureAwait(true);

            // Вираз без посилання на заборонене — вивантажується (csv/json — сирим текстом).
            if (format != "xlsx")
            {
                Assert.Contains(visibleRef, text, StringComparison.Ordinal);
            }

            // Вираз із посиланням на заборонене — немає, як і кода забороненої колонки.
            Assert.DoesNotContain(hiddenRef, text, StringComparison.Ordinal);
            Assert.DoesNotContain(s.ColumnCodes[2], text, StringComparison.Ordinal);
        }

        // Регресія: без заборон у файлі обидва вирази.
        if (format != "xlsx")
        {
            using var plain = await SignedInAsync(app, s.Plain).ConfigureAwait(true);
            var text = await ExportTextAsync(app, plain, s, format, includeFormulas: true).ConfigureAwait(true);
            Assert.Contains(visibleRef, text, StringComparison.Ordinal);
            Assert.Contains(hiddenRef, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    [InlineData("csv")]
    [InlineData("json")]
    public async Task Експорт_з_формулами_не_віддає_вираз_з_посиланням_на_заборонену_таблицю(string format)
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var deniedTableSheet = await SheetCodeAsync(s.DeniedTable.SheetDefId).ConfigureAwait(true);
        var viaDeniedTable = $"[{deniedTableSheet}].[{s.DeniedTable.TableCode}].[{s.DeniedTable.ColumnCodes[0]}]";
        const string noRefs = "2 * 3";
        await AddColumnFormulasAsync(s, (s.Doc.ColumnDefIds[0], noRefs), (s.Doc.ColumnDefIds[1], viaDeniedTable))
            .ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var reader = await SignedInAsync(app, s.Reader).ConfigureAwait(true);
        var text = await ExportTextAsync(app, reader, s, format, includeFormulas: true).ConfigureAwait(true);

        Assert.Contains(noRefs, text, StringComparison.Ordinal);
        Assert.DoesNotContain(s.DeniedTable.TableCode, text, StringComparison.Ordinal);
        Assert.DoesNotContain(s.DeniedTable.ColumnCodes[0], text, StringComparison.Ordinal);
    }
}
