// tests/Ecr.Api.Tests/Security/DenyReadTests.ColumnExpression.cs
using System.Net;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests.Security;

/// <summary>
/// UI-25 (B4): <c>ColumnDto.expression</c> віддається лише коли читач бачить КОЖНЕ посилання
/// виразу; інакше <c>null</c>. Наскрізно: справжній SQL, справжній вхід, HTTP.
/// </summary>
/// <remarks>
/// ⛔ МУТАЦІЙНИЙ ДОКАЗ: <c>ColumnExpressionVisibility.Visible</c> повертає
/// <c>formula.Expression</c> без перевірки посилань — червоніють тести з <c>reader</c>
/// (<c>null</c> замість прихованого виразу), регресійні й контрольні лишаються зеленими.
/// </remarks>
public sealed partial class DenyReadTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Вираз_колонки_з_посиланням_на_заборонену_колонку_приходить_null_з_видимим_на_місці()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var visibleRef = $"[{s.ColumnCodes[1]}] * 2";
        var hiddenRef = $"[{s.ColumnCodes[2]}] + 1";
        await AddColumnFormulasAsync(s, (s.Doc.ColumnDefIds[0], visibleRef), (s.Doc.ColumnDefIds[1], hiddenRef))
            .ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        var (status, body) = await GetAsync(client, Slice(s, s.Doc.TableInstanceId)).ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

        var expressions = ExpressionsByCode(body);
        Assert.Equal(visibleRef, expressions[s.ColumnCodes[0]]);
        Assert.Null(expressions[s.ColumnCodes[1]]);

        // Текст прихованого виразу (і код забороненої колонки) не лишається ніде у відповіді.
        Assert.DoesNotContain(hiddenRef, body, StringComparison.Ordinal);
        Assert.DoesNotContain(s.ColumnCodes[2], body, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-6.6")]
    public async Task Вираз_з_посиланням_на_заборонену_таблицю_й_аркуш_приходить_null_без_посилань_лишається()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);

        var deniedTableSheet = await SheetCodeAsync(s.DeniedTable.SheetDefId).ConfigureAwait(true);
        var deniedSheetSheet = await SheetCodeAsync(s.DeniedSheetTable.SheetDefId).ConfigureAwait(true);

        var viaDeniedTable = $"[{deniedTableSheet}].[{s.DeniedTable.TableCode}].[{s.DeniedTable.ColumnCodes[0]}]";
        var viaDeniedSheet = $"[{deniedSheetSheet}].[{s.DeniedSheetTable.TableCode}].[{s.DeniedSheetTable.ColumnCodes[0]}]";
        const string noRefs = "2 + 2";

        await AddColumnFormulasAsync(
            s,
            (s.Doc.ColumnDefIds[0], noRefs),
            (s.Doc.ColumnDefIds[1], viaDeniedTable)).ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Reader).ConfigureAwait(true);

        var (status, body) = await GetAsync(client, Slice(s, s.Doc.TableInstanceId)).ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

        var expressions = ExpressionsByCode(body);
        Assert.Equal(noRefs, expressions[s.ColumnCodes[0]]);
        Assert.Null(expressions[s.ColumnCodes[1]]);
        Assert.DoesNotContain(s.DeniedTable.TableCode, body, StringComparison.Ordinal);

        // Те саме для аркуша під забороною: друга формула на тій самій колонці замінює першу.
        await SetExpressionAsync(s.Doc.ColumnDefIds[1], viaDeniedSheet).ConfigureAwait(true);

        using var app2 = new EcrApiFactory(sql);
        using var client2 = await SignedInAsync(app2, s.Reader).ConfigureAwait(true);
        var (status2, body2) = await GetAsync(client2, Slice(s, s.Doc.TableInstanceId)).ConfigureAwait(true);
        Assert.True(status2 == HttpStatusCode.OK, $"{status2}: {body2}\n{app2.ErrorsText}");
        Assert.Null(ExpressionsByCode(body2)[s.ColumnCodes[1]]);
        Assert.DoesNotContain(s.DeniedSheetTable.TableCode, body2, StringComparison.Ordinal);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Без_заборон_вираз_колонки_віддається_як_є_регресія()
    {
        var s = await ArrangeAsync().ConfigureAwait(true);
        var onVisible = $"[{s.ColumnCodes[1]}] * 2";
        var onDeniedFor = $"[{s.ColumnCodes[2]}] + 1";
        await AddColumnFormulasAsync(s, (s.Doc.ColumnDefIds[0], onVisible), (s.Doc.ColumnDefIds[1], onDeniedFor))
            .ConfigureAwait(true);

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.Plain).ConfigureAwait(true);

        var (status, body) = await GetAsync(client, Slice(s, s.Doc.TableInstanceId)).ConfigureAwait(true);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}\n{app.ErrorsText}");

        var expressions = ExpressionsByCode(body);
        Assert.Equal(onVisible, expressions[s.ColumnCodes[0]]);
        Assert.Equal(onDeniedFor, expressions[s.ColumnCodes[1]]);
    }

    private async Task<string> SheetCodeAsync(int sheetDefId)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        return await db.SheetDefs.AsNoTracking().Where(x => x.Id == sheetDefId).Select(x => x.Code)
            .SingleAsync().ConfigureAwait(false);
    }

    private async Task SetExpressionAsync(int columnDefId, string expression)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();
        var formula = await db.FormulaDefs.SingleAsync(f => f.ColumnDefId == columnDefId).ConfigureAwait(false);
        formula.SetExpression(expression);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private static Dictionary<string, string?> ExpressionsByCode(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("columns").EnumerateArray()
            .ToDictionary(
                c => c.GetProperty("code").GetString()!,
                c => c.TryGetProperty("expression", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null,
                StringComparer.Ordinal);

    private async Task AddColumnFormulasAsync(Scenario s, params (int ColumnDefId, string Expression)[] formulas)
    {
        await using var db = new TestDocumentBuilder(sql.ConnectionString).CreateContext();

        foreach (var (columnDefId, expression) in formulas)
        {
            var formula = new FormulaDef(s.Doc.TableDefId, FormulaScope.Column, expression, ExpressionDialect.Template);
            formula.AssignColumn(columnDefId);
            db.FormulaDefs.Add(formula);
        }

        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
