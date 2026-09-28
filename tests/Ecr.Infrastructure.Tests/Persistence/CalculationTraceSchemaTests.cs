// tests/Ecr.Infrastructure.Tests/Persistence/CalculationTraceSchemaTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// Міграція <c>HSE301M3Trace</c> на РЕАЛЬНОМУ SQL Server (FEATURE-HSE301-VIEW §7.1,
/// крок F6, <c>D-175</c>, <c>D-176</c>): область формули, видимість, вихід по
/// речовинах, вид результату.
/// </summary>
/// <remarks>
/// ⛔ Типові значення тут — не зручність, а вся безпечність міграції: наявні рядки
/// отримують DEFAULT схеми, і лише DEFAULT, що дорівнює сьогоднішній поведінці
/// (<c>Substance</c>, невидима, <c>IsPerSubstance = 1</c>, <c>Output</c>), не
/// змінює жодного числа наявних методологій.
///
/// Мутаційний доказ (F6): DEFAULT <c>DF_MF_Scope</c> = 1 (<c>Row</c>) у міграції —
/// червоніють <see cref="Нові_колонки_мають_DEFAULT_сьогоднішньої_поведінки"/> і
/// <see cref="Рядок_вставлений_без_нових_колонок_отримує_сьогоднішню_поведінку"/>.
/// </remarks>
[Collection("SqlServer")]
public sealed class CalculationTraceSchemaTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 9, 28, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F6")]
    [InlineData("calc.MethodologyFormula", "Scope", "DF_MF_Scope", "(CONVERT([tinyint],(0)))")]
    [InlineData("calc.MethodologyFormula", "IsVisible", "DF_MF_Visible", "(CONVERT([bit],(0)))")]
    [InlineData("calc.MethodologyOutput", "IsPerSubstance", "DF_MO_PerSub", "(CONVERT([bit],(1)))")]
    [InlineData("calc.CalculationResult", "Kind", "DF_CRes_Kind", "(CONVERT([tinyint],(0)))")]
    public async Task Нові_колонки_мають_DEFAULT_сьогоднішньої_поведінки(
        string table, string column, string constraint, string definition)
    {
        var actual = await ScalarAsync<string>(
            """
            SELECT dc.name + N' ' + dc.definition + N' ' + CASE c.is_nullable WHEN 1 THEN N'NULL' ELSE N'NOT NULL' END
            FROM sys.columns AS c
            JOIN sys.default_constraints AS dc
              ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            WHERE c.object_id = OBJECT_ID(@table) AND c.name = @column;
            """,
            ("@table", table),
            ("@column", column));

        Assert.Equal($"{constraint} {definition} NOT NULL", actual);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F6")]
    public async Task Рядок_вставлений_без_нових_колонок_отримує_сьогоднішню_поведінку()
    {
        // ⚠ Вставка ПОВЗ EF і без нових колонок — так пишуть рядки, що існували
        // до міграції, і масовий імпорт корпусу методологій: `ALTER TABLE … ADD
        // … NOT NULL DEFAULT` заповнює наявні рядки тим самим DEFAULT.
        var versionId = await NewVersionAsync();
        var unitId = await ScalarAsync<int>("SELECT TOP (1) Id FROM uom.Unit ORDER BY Id;");

        await ScalarAsync<int>(
            """
            INSERT INTO calc.MethodologyFormula (MethodologyVersionId, Code, Expression, ResultType, EvaluationOrder)
            VALUES (@v, N'LEGACY_F', N'1', 0, 0);
            INSERT INTO calc.MethodologyOutput (MethodologyVersionId, Code, UnitId, Ordinal)
            VALUES (@v, N'LEGACY_F', @u, 0);
            SELECT 0;
            """,
            ("@v", versionId),
            ("@u", unitId));

        await using var read = sql.CreateContext();
        var formula = await read.MethodologyFormulas.AsNoTracking()
            .SingleAsync(f => f.MethodologyVersionId == versionId && f.Code == "LEGACY_F");
        var output = await read.MethodologyOutputs.AsNoTracking()
            .SingleAsync(o => o.MethodologyVersionId == versionId && o.Code == "LEGACY_F");

        Assert.Equal((MethodologyFormulaScope.Substance, false, true), (formula.Scope, formula.IsVisible, output.IsPerSubstance));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "HSE301-F6")]
    public async Task Явно_задані_значення_доходять_до_бази_а_не_підміняються_DEFAULT()
    {
        var versionId = await NewVersionAsync();
        var unitId = await ScalarAsync<int>("SELECT TOP (1) Id FROM uom.Unit ORDER BY Id;");

        await using (var db = sql.CreateContext())
        {
            var formula = new MethodologyFormula(versionId, EcrCode.Create("M_T"), "!V_Sm3 * @Rho20");
            formula.SetScope(MethodologyFormulaScope.Row);
            formula.SetVisible(true);

            // ⛔ `false` — CLR-замовчування, а DEFAULT схеми — 1. Без
            // `ValueGeneratedNever` EF не надіслав би його, і Row-вихід ліг би в
            // базу по-речовинним (EF [20601], див. EnumDefaultSentinelTests).
            var output = new MethodologyOutput(versionId, EcrCode.Create("M_T"), unitId);
            output.SetPerSubstance(false);

            db.MethodologyFormulas.Add(formula);
            db.MethodologyOutputs.Add(output);
            await db.SaveChangesAsync();
        }

        await using var read = sql.CreateContext();
        var stored = await read.MethodologyFormulas.AsNoTracking()
            .SingleAsync(f => f.MethodologyVersionId == versionId && f.Code == "M_T");
        var storedOutput = await read.MethodologyOutputs.AsNoTracking()
            .SingleAsync(o => o.MethodologyVersionId == versionId && o.Code == "M_T");

        Assert.Equal((MethodologyFormulaScope.Row, true, false), (stored.Scope, stored.IsVisible, storedOutput.IsPerSubstance));
    }

    private async Task<int> NewVersionAsync()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];

        await using var db = sql.CreateContext();
        var methodology = new Methodology(
            EcrCode.Create($"F6_{tag}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "F6 probe" }));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        return version.Id;
    }

    private async Task<T> ScalarAsync<T>(string commandText, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync().ConfigureAwait(true);

        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (T)(await command.ExecuteScalarAsync().ConfigureAwait(true))!;
    }
}
