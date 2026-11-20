// tests/Ecr.Infrastructure.Tests/Persistence/LandHse400CompositionRegistryTests.Script.cs
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions;
using Ecr.Expressions.Evaluation;
using Ecr.Expressions.Functions;
using Ecr.Scripting;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// MS x B80: скриптова версія методології отримує склад газу HSE400 ТАК САМО, як формульна: мова скрипта читає довідник
/// <c>LAND_HSE400_COMPOSITION</c> функціями <c>REGFIND</c>/<c>REGFIELD</c>, а зв'язка <c>cfg.DocumentSourceBinding</c> підміняє його
/// рядки документом HSE400 (<see cref="Ecr.Infrastructure.Persistence.Hse400CompositionSnapshotLoader"/>) - і знімок той самий, який
/// завантажує <c>ScriptCalculationModule</c> (<c>IRegistrySnapshotLoader</c>). Справжній SQL Server.
/// </summary>
/// <remarks>
/// <para>
/// Вираз скрипта - МЕХАНІЧНИЙ переклад мосту моделі Land (<c>T9_04.Density20</c>, той самий текст, що в решті тестів класу):
/// <c>[Period].Year</c> - <c>period.Year</c>, <c>[Period].Sequence</c> - <c>period.Month</c> (так само робить перетворювач
/// <c>ExpressionTranslator</c>), <c>[TypeOfFuel]</c> - <c>row.TypeOfFuel</c>, апострофи - лапки. Результат порівнюється з
/// формульним обчисленням на тому ж знімку.
/// </para>
/// <para>
/// Мутації (локально): ті самі, що й B80 для формул (B80-M1: прибрати перевірку воріт у циклі вводу - лютий дає 0.82 замість
/// #N/A; B80-M3: <c>approvedOnly</c> завжди <c>true</c> - тест про <c>Any</c> червоний): скрипт іде тим самим завантажувачем.
/// </para>
/// </remarks>
public sealed partial class LandHse400CompositionRegistryTests
{
    private static string ScriptDensity20(Fixture f)
    {
        var bridge = f.Retarget(ModelDensity20())
            .Replace("[Period].Year", "period.Year", StringComparison.Ordinal)
            .Replace("[Period].Sequence", "period.Month", StringComparison.Ordinal)
            .Replace("[TypeOfFuel]", "row.TypeOfFuel", StringComparison.Ordinal)
            .Replace((char)39, '"');
        return $"rows from T9_04;{Environment.NewLine}output Density20 : kg = {bridge};";
    }

    private static ExpressionValue EvalScript(Fixture f, IRegistrySnapshot snapshot, string fuelText, int month)
    {
        var compiled = ScriptCompiler.Compile(ScriptDensity20(f), new ScriptCompileOptions(NumericMode.Strict, IsLibrary: false));
        Assert.True(compiled.IsSuccess, string.Join("; ", compiled.Diagnostics.Select(d => d.Message)));
        Assert.Contains(f.CompositionCode, compiled.Script!.RegistryCodes);

        var output = Assert.Single(compiled.Script.Statements.OfType<OutputStatement>());
        var context = new TestEvaluationContext
        {
            Period = new PeriodContext(new DateOnly(2026, month, 1), new DateOnly(2026, month, 28), CalendarMode.Actual, 2026, (byte)month),
        };
        context.Arguments["TypeOfFuel"] = ExpressionValue.Text(fuelText);
        return new Evaluator(new FunctionRegistry())
            .Evaluate(output.Expr, new SnapshotContext(context, snapshot), ExpressionDialect.Methodology);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Скрипт_отримує_склад_з_документа_HSE400_так_само_як_формула_за_Any()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(
            ("FG_Internal", "Density_20", 1, 0.81m),
            ("FG_Internal", "Density_20", 2, 0.82m));
        await AddBindingAsync(f, lastBuilt!.TemplateId, DocumentSourceState.Any);

        var snapshot = await LoadFromDocumentAsync(f, template);

        // Значення документа (не рядка довідника 0.7 / 0.75) і ті самі числа, що дає формула моста на тому ж знімку.
        Assert.Equal(0.81m, EvalScript(f, snapshot, FuelGasInternal, month: 1).AsNumber());
        Assert.Equal(0.82m, EvalScript(f, snapshot, FuelGasInternal, month: 2).AsNumber());
        Assert.Equal(
            Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 1).AsNumber(),
            EvalScript(f, snapshot, FuelGasInternal, month: 1).AsNumber());

        // Потік без запису складу - #N/A, як у формули.
        Assert.Equal(ExpressionErrors.NotAvailable, EvalScript(f, snapshot, "Diesel - Дизель", month: 1).ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Скрипт_за_ApprovedOnly_не_має_даних_незатвердженого_місяця_так_само_як_формула()
    {
        var f = await ArrangeAsync();
        var template = await BuildDocumentAsync(
            ("FG_Internal", "Density_20", 1, 0.81m),
            ("FG_Internal", "Density_20", 2, 0.82m));
        var doc = lastBuilt!;
        await AddBindingAsync(f, doc.TemplateId, DocumentSourceState.ApprovedOnly);
        await AddApprovalAsync(doc.DocumentId, doc.SheetDefId, 202601, DocumentStatus.Approved);
        await AddApprovalAsync(doc.DocumentId, doc.SheetDefId, 202602, DocumentStatus.Submitted);

        var snapshot = await LoadFromDocumentAsync(f, template);

        Assert.Equal(0.81m, EvalScript(f, snapshot, FuelGasInternal, month: 1).AsNumber());

        // Лютий лише поданий: ні вводу документа, ні рядка довідника (0.75) - #N/A, і формула дає те саме.
        Assert.Equal(ExpressionErrors.NotAvailable, EvalScript(f, snapshot, FuelGasInternal, month: 2).ErrorCode);
        Assert.Equal(ExpressionErrors.NotAvailable, Eval(f, snapshot, ModelDensity20(), FuelGasInternal, month: 2).ErrorCode);
    }
}
