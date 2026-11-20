using System.Globalization;
using Ecr.Calculations.Scripting.Parity;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.MethodologyScriptMigration.Cli;
using Ecr.MethodologyScriptMigration.Conversion;
using Ecr.MethodologyScriptMigration.Loading;
using Ecr.Scripting;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ecr.MethodologyScriptMigration.Tests;

/// <summary>
/// Паритет рівня <c>Module</c> (HSE400) на реальному SQL Server (MS, план §8.4, етап 7): версія рівня Module з формулами
/// <c>X400_&lt;таблиця&gt;_*</c> → конвертер → тіньовий текст → <see cref="ParityRunner"/> порівнює старий і скриптовий шлях
/// <c>MonthColumnArgumentsModule</c> на одному документі: 0 розбіжностей; зміна однієї константи в тексті — розбіжності;
/// порожній відбір — не «гаразд». Період 2099xx, документ — місячні колонки M1..M12, рядки вводу й <c>F_…</c>.
/// </summary>
[Collection("SqlServer")]
public sealed class ParityModuleDbTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 1, 10, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Scene(string MethodologyCode, string Table, long DocumentId, int VersionId, int Period);

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Module_перетворена_версія_дає_нуль_розбіжностей_а_змінена_константа_дає_розбіжності()
    {
        var scene = await ArrangeAsync(209911);
        await ShadowAsync(scene);

        var equal = await RunAsync(scene);

        Assert.True(equal.IsEqual, Describe(equal));
        Assert.Equal(1, equal.Targets);
        Assert.Equal(9, equal.RowsCompared); // усі рядки таблиці: 3 входи й 6 рядків F_…
        Assert.True(equal.ValuesCompared >= 8, $"values compared: {equal.ValuesCompared}");

        // Контроль чутливості: константа Factor 1.5 → 1.6 лише в тексті (старий шлях читає константи з бази).
        await ChangeScriptAsync(scene, text => text.Replace("1.5", "1.6", StringComparison.Ordinal));

        var different = await RunAsync(scene);

        Assert.False(different.IsEqual);
        Assert.Contains(different.Mismatches, m => m.Aspect == ParityAspect.ValueNumber && m.RowKey == "F_Scaled");
        Assert.DoesNotContain(different.Mismatches, m => m.Aspect is ParityAspect.RowSelected or ParityAspect.ValueMissing);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Module_кроки_причини_порівнюються_замаскований_нуль_і_нульова_молекулярна_вага_збігаються()
    {
        var scene = await ArrangeAsync(209912);
        await ShadowAsync(scene);

        // Повний збіг на даних, де є і замаскований нуль (M2: Methane = 0), і нульова молекулярна вага (M3: Gas = 0).
        var equal = await RunAsync(scene);
        Assert.True(equal.IsEqual, Describe(equal));

        // Ідентичність кроків не порожня: у документі є рядки з кроками-причинами (M2 для F_Ratio і F_Scaled).
        await using var provider = ParityHost.Build(sql.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        var comparer = scope.ServiceProvider.GetRequiredService<ParityComparer>();
        await using var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: true);
        var instance = await db.TableInstances.AsNoTracking().OriginalOnly()
            .SingleAsync(i => i.DocumentId == scene.DocumentId && i.PeriodKeyValue == scene.Period);
        var version = await db.MethodologyVersions.AsNoTracking().SingleAsync(v => v.Id == scene.VersionId);

        // Підміна тексту: ділення на Methane → множення. Кроків-причин у скрипта на M2 більше немає, у старого шляху вони є.
        var report = await comparer.CompareAsync(
            scene.VersionId, version.ScriptText!.Replace("/ row.GCV", "* row.GCV", StringComparison.Ordinal),
            scene.DocumentId, new PeriodKey(scene.Period), instance.Id, default);

        Assert.False(report.IsEqual);
        Assert.Contains(report.Mismatches, m => m.Aspect == ParityAspect.TraceSteps);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Module_команда_parity_пише_звіт_і_порожній_відбір_не_гаразд()
    {
        var scene = await ArrangeAsync(209910);
        await ShadowAsync(scene);
        var directory = Path.Combine(Path.GetTempPath(), "ecr-ms-parity-" + Guid.NewGuid().ToString("N"));
        var report = Path.Combine(directory, "parity.json");

        var ok = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", scene.MethodologyCode,
            "--document", scene.DocumentId.ToString(CultureInfo.InvariantCulture), "--out", report);

        Assert.Equal(MigrationCli.Ok, ok.Code);
        Assert.Contains("розбіжностей 0", ok.Output, StringComparison.Ordinal);
        Assert.Contains("\"mismatches\": []", await File.ReadAllTextAsync(report), StringComparison.Ordinal);

        await ChangeScriptAsync(scene, text => text.Replace("1.5", "1.6", StringComparison.Ordinal));
        var failed = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", scene.MethodologyCode, "--out", report);
        Assert.Equal(MigrationCli.Failures, failed.Code);
        Assert.Contains("value.number", failed.Output, StringComparison.Ordinal);

        // «Нічого не порівняно» — не зелений результат: код методології, якої немає.
        var empty = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", "NO_SUCH_" + scene.MethodologyCode, "--out", report);
        Assert.Equal(MigrationCli.Failures, empty.Code);
        Assert.Contains("НІЧОГО ПОРІВНЮВАТИ", empty.Output, StringComparison.Ordinal);

        // …і документ без екземплярів методології (документа немає) — теж.
        var noDocument = await CliAsync("parity", "--connection", sql.ConnectionString, "--methodology", scene.MethodologyCode,
            "--document", "999999999", "--out", report);
        Assert.Equal(MigrationCli.Failures, noDocument.Code);
        Assert.Contains("НІЧОГО ПОРІВНЮВАТИ", noDocument.Output, StringComparison.Ordinal);
    }

    // ───── стенд ─────

    private static string Describe(ParityRunReport report)
        => string.Join("\n", report.Mismatches.Select(m => $"{m.RowKey} {m.Aspect} {m.Old} / {m.New}"));

    private async Task<ParityRunReport> RunAsync(Scene scene)
    {
        await using var provider = ParityHost.Build(sql.ConnectionString);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ParityRunner>().RunAsync(scene.MethodologyCode, scene.DocumentId, default);
    }

    private static async Task<(int Code, string Output, string Error)> CliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await MigrationCli.RunAsync(args, output, error, CancellationToken.None);
        return (code, output.ToString(), error.ToString());
    }

    private async Task ChangeScriptAsync(Scene scene, Func<string, string> change)
    {
        await using var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false);
        var version = await db.MethodologyVersions.SingleAsync(v => v.Id == scene.VersionId);
        var changed = change(version.ScriptText!);
        Assert.NotEqual(version.ScriptText, changed);
        version.SetScript(changed, ScriptCompiler.Hash(changed));
        await db.SaveChangesAsync();
    }

    /// <summary>Перетворює версію справжнім конвертером і записує тіньовий текст.</summary>
    private async Task ShadowAsync(Scene scene)
    {
        string text;
        await using (var read = MigrationCli.CreateContext(sql.ConnectionString, readOnly: true))
        {
            var loaded = await new VersionSourceLoader(read, MigrationCli.CreateFormulaEngine()).LoadAsync(scene.VersionId, default);
            Assert.False(loaded.HasErrors, string.Join("; ", loaded.Issues.Select(i => i.Code + ": " + i.Message)));
            var converted = ScriptConverter.Convert(loaded.Source);
            Assert.False(converted.HasErrors, string.Join("; ", converted.Issues.Select(i => i.Code + ": " + i.Message)));
            text = converted.Text!;
        }

        await using var db = MigrationCli.CreateContext(sql.ConnectionString, readOnly: false);
        var version = await db.MethodologyVersions.SingleAsync(v => v.Id == scene.VersionId);
        version.SetScript(text, ScriptCompiler.Hash(text));
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Документ з однією таблицею «місяці в колонках» (M1..M12): вхід <c>Methane/Nitrogen/Gas</c>, розрахункові
    /// <c>F_Sum/F_Ratio/F_Scaled/F_MolecularWeight/F_Inverse</c>; версія Module з правилом {} і активною прив'язкою.
    /// </summary>
    private async Task<Scene> ArrangeAsync(int periodKey)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(
            periodKey: periodKey, columnCount: 0, rowCount: 0, layoutKind: TableLayoutKind.MonthsInColumns, monthColumns: 12);
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        await using var db = builder.CreateContext();
        var table = await db.TableDefs.AsNoTracking().SingleAsync(t => t.Id == document.TableDefId);
        var t = table.Code;
        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.AdvanceTo(PeriodState.Open, Now);
        await db.SaveChangesAsync();
        await ExecuteAsync($"UPDATE doc.Project SET PeriodKind = {(int)PeriodKind.Yearly} WHERE Id = {document.ProjectId};");

        // Рядки: вхід і розрахункові.
        string[] keys = ["Methane", "Nitrogen", "Gas", "F_Sum", "F_Ratio", "F_Scaled", "F_MolecularWeight", "F_Inverse", "F_Mass"];
        var first = await new BulkCellLoader(sql.ConnectionString, 1000).ReserveIdsAsync("doc.TableRowSeq", keys.Length, default);
        var rowIds = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Length; i++)
        {
            db.TableRows.Add(new TableRow(document.PeriodKey, first + i, document.TableInstanceId, RowKey.Create(keys[i]), i + 1, Now));
            rowIds[keys[i]] = first + i;
        }

        await db.SaveChangesAsync();

        // Ввід: M1 — звичайний; M2 — Methane = 0 (замаскований нуль у діленні); M3 — Gas = 0 (нульова молекулярна вага);
        // M4 — Gas = 2; M5 — порожній (нічого не введено); M6 — лише Methane (неповний вхід).
        var inputs = new (int Month, string Row, decimal Value)[]
        {
            (1, "Methane", 10m), (1, "Nitrogen", 4m), (1, "Gas", 3m),
            (2, "Methane", 0m), (2, "Nitrogen", 2.5m), (2, "Gas", 5m),
            (3, "Methane", 7m), (3, "Nitrogen", 1m), (3, "Gas", 0m),
            (4, "Methane", 8m), (4, "Nitrogen", 2m), (4, "Gas", 2m),
            (6, "Methane", 9m),
            (7, "Methane", 6m), (7, "Nitrogen", 1m), (7, "Gas", 14.4721m),
        };
        var statements = inputs.Select(v =>
            "INSERT INTO doc.CellValue (PeriodKey, TableRowId, ColumnDefId, TableDefId, ValueNumeric, IsCalculated, IsEmpty) VALUES " +
            $"({periodKey}, {rowIds[v.Row]}, {document.MonthColumnDefIds[v.Month - 1]}, {document.TableDefId}, " +
            $"{v.Value.ToString(CultureInfo.InvariantCulture)}, 0, 0);");
        await ExecuteAsync(string.Join(Environment.NewLine, statements));

        var unitOne = (await db.Units.FirstAsync(u => u.Code == "one")).Id;
        var code = "PMOD_" + tag;
        var methodology = new Methodology(EcrCode.Create(code), Name("module"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(methodology.Id, "V1", CalculationLevel.Module, 1, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        (string Code, string Expression)[] formulas =
        [
            ("Common_K", "CST.Factor * 2"),
            ($"X400_{t}_Sum", $"@GCV.X400_{t}_Methane + @GCV.X400_{t}_Nitrogen"),
            ($"X400_{t}_Ratio", $"Round(!X400_{t}_Sum / @GCV.X400_{t}_Methane, 2)"),
            ($"X400_{t}_Scaled", $"Round(!X400_{t}_Sum * !Common_K, 1)"),
            ($"X400_{t}_MolecularWeight", $"@GCV.X400_{t}_Gas * 2"),
            ($"X400_{t}_Inverse", $"1 / !X400_{t}_MolecularWeight"),

            // Регресія (HSE400 doc 3, F_H2S M09): константи з БД мають scale 16, і (double)34.0760002136230000m не дорівнює
            // (double)34.076000213623m. Текст константи в скрипті мусить давати той самий double, що й значення з БД.
            ($"X400_{t}_Mass", $"(CST.K1*CST.K2*@GCV.X400_{t}_Gas*CST.K3)/(CST.MW*CST.K4)"),
        ];
        for (var i = 0; i < formulas.Length; i++)
        {
            var formula = new MethodologyFormula(version.Id, EcrCode.Create(formulas[i].Code), formulas[i].Expression);
            formula.SetEvaluationOrder(i + 1);
            db.MethodologyFormulas.Add(formula);
        }

        var constant = new MethodologyConstant(version.Id, EcrCode.Create("Factor"), 1.5m, unitOne);
        db.MethodologyConstants.Add(constant);
        foreach (var (constantCode, constantValue) in new[]
                 { ("K1", 0.8313m), ("K2", 273.15m), ("K3", 1000m), ("K4", 101325m), ("MW", 34.076000213623m) })
        {
            db.MethodologyConstants.Add(new MethodologyConstant(version.Id, EcrCode.Create(constantCode), constantValue, unitOne));
        }

        db.MethodologyOutputs.Add(new MethodologyOutput(version.Id, EcrCode.Create($"X400_{t}_Sum"), unitOne, 1));
        db.MethodologyRules.Add(new MethodologyRule(version.Id, EcrCode.Create("R_ALL"), "{}", 10));
        await db.SaveChangesAsync();

        db.CalculationBindings.Add(new CalculationBinding(
            document.TableDefId, document.MonthColumnDefIds[0], methodology.Id, $"X400_{t}_Sum", "{}"));
        version.Publish(1, "seed", new DateOnly(2099, 1, 1), true, Now);
        await db.SaveChangesAsync();

        return new Scene(code, t, document.DocumentId, version.Id, periodKey);
    }

    private async Task ExecuteAsync(string statement)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync();
    }
}
