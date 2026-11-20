using Ecr.Application;
using Ecr.Application.Common;
using Ecr.Application.Ports;
using Ecr.Application.Workflow;
using Ecr.Calculations;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Scripting;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// Версії-скрипти на РЕАЛЬНОМУ SQL Server крізь справжні <c>RecalculationJob</c> → <c>CalculationOrchestrator</c> →
/// <c>ScriptCalculationModule</c> (MS T2.5): рядки з <c>rows … where</c> потрапляють у <c>calc.CalculationResult</c>,
/// решта дають проблеми рядків; методологія-читач бачить результат джерела з ТОГО САМОГО прогону, а правка входу
/// джерела й перерахунок аркуша-джерела оновлюють читача.
/// </summary>
/// <remarks>
/// Скрипт пишеться через домен (<c>SetScript</c>), а виходи, прив'язки й таблиці читання вставлено напряму —
/// матеріалізація (§5.1) належить іншому коду. Періоди — 2099xx. Мутаційні докази (лише цей клас): прибрати
/// <c>ExtendTouchedWithScriptReadersAsync</c> з <c>RecalculationJob.BindingsAsync</c> — червоніє
/// <see cref="Правка_входу_джерела_й_перерахунок_аркуша_оновлює_читача_з_того_самого_прогону"/>; не заповнювати
/// <c>CalculationNode.DependsOn</c> у оркестраторі — читач рахується в одній пачці з джерелом і бачить порожнє.
/// </remarks>
[Collection("SqlServer")]
public sealed class ScriptMethodologyRunSqlTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2099, 1, 10, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Fixture(
        TestDocumentBuilder Builder,
        TestDocument Document,
        string TableCode,
        string C1,
        string C2,
        int OutAColumnId,
        int UnitId,
        string Tag);

    private sealed record Second(
        int SheetDefId, int TableDefId, string TableCode, long InstanceId, int OutBColumnId, int MethodologyBId, int VersionBId);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_скрипт_пише_результати_відібраних_рядків_а_решті_дає_проблеми()
    {
        var f = await ArrangeAsync(209901);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", null), ("oil", 7m));
        await PublishScriptAsync(
            f, "A",
            $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;",
            [("Y", f.OutAColumnId)]);

        await RunJobAsync(f.Document, sheetDefId: null);

        await using var db = f.Builder.CreateContext();
        var rows = await new CalculationResultStore(db, new TestClock(Now))
            .ReadCurrentAsync(f.Document.DocumentId, f.Document.PeriodKey.Value, default);
        var issues = await db.CalculationIssues
            .AsNoTracking()
            .Where(i => i.DocumentId == f.Document.DocumentId && i.PeriodKey == f.Document.PeriodKey.Value)
            .Select(i => new { i.RowNumber, i.Kind })
            .ToListAsync();

        // Рядок 1 (gas, 10) → Y = 20; рядок 2 (gas без C2) — без результату; рядок 3 (oil) не відібраний.
        var result = Assert.Single(rows);
        Assert.Equal(("Y", 20m), (result.OutputCode, result.Value));
        Assert.Equal(f.Document.TableInstanceId, result.TableInstanceId);

        Assert.Contains(issues, i => i is { RowNumber: 2, Kind: CalculationIssue.MissingInputsKind });
        Assert.Contains(issues, i => i is { RowNumber: 3, Kind: CalculationIssue.NoMatchingRuleKind });
        Assert.DoesNotContain(issues, i => i.RowNumber == 1);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_входу_джерела_й_перерахунок_аркуша_оновлює_читача_з_того_самого_прогону()
    {
        var f = await ArrangeAsync(209902);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", 5m), ("oil", 7m));
        var a = await PublishScriptAsync(
            f, "A",
            $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;",
            [("Y", f.OutAColumnId)]);

        // B живе на ІНШОМУ аркуші й читає результат A через колонку Calculated таблиці A (OUTA).
        await ArrangeReaderAsync(f, a);

        // Прогін 1 (увесь документ): B у пізнішій пачці, ніж A, і бачить результат A з цього ж прогону.
        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["Y|10", "Y|20", "Z|30"], await ShapeAsync(f));

        // Правка входу A (10 → 30) і перерахунок АРКУША-ДЖЕРЕЛА: B не прив'язаний до цього аркуша, але читає його
        // таблицю — перераховується разом із ним і бачить свіжі числа A (60 + 10).
        await SetValueAsync(f, rowIndex: 0, 30m);
        await RunJobAsync(f.Document, sheetDefId: f.Document.SheetDefId);

        Assert.Equal(["Y|10", "Y|60", "Z|70"], await ShapeAsync(f));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Застарілість_аркуша_читача_охоплює_таблицю_яку_читає_скрипт_і_таблиці_джерел_результатів()
    {
        var f = await ArrangeAsync(209903);
        await InsertValuesAsync(f, ("gas", 10m));
        var a = await PublishScriptAsync(
            f, "A", $"rows from {f.TableCode};\noutput Y: kg = row.{f.C2};", [("Y", f.OutAColumnId)]);
        var b = await ArrangeReaderAsync(f, a);

        await using var db = f.Builder.CreateContext();
        var store = new ScriptRuntimeStore(db);
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        var snapshot = await scope.ServiceProvider.GetRequiredService<IMetadataCache>()
            .GetAsync(f.Document.TemplateVersionId, default);

        // Аркуш читача (B): власна таблиця B, плюс таблиця A, яку B читає (`table Src = …`).
        var readerScope = new HashSet<int> { b.TableDefId };
        await ScriptFreshnessScope.ExtendAsync(store, snapshot, readerScope, [b.MethodologyBId], default);
        Assert.Equal([f.Document.TableDefId, b.TableDefId], readerScope.Order().ToArray());

        // Аркуш джерела (A) нічого чужого не читає: замикання лишається його таблицею.
        var own = new HashSet<int> { f.Document.TableDefId };
        await ScriptFreshnessScope.ExtendAsync(store, snapshot, own, [a.MethodologyId], default);
        Assert.Equal([f.Document.TableDefId], own.ToArray());

        // Читач результатів без читання таблиць (лише ребро залежності): замикання бере таблиці виходів джерела.
        var c = new HashSet<int> { b.TableDefId };
        db.MethodologyReadTables.RemoveRange(await db.MethodologyReadTables.Where(r => r.MethodologyVersionId == b.VersionBId).ToListAsync());
        await db.SaveChangesAsync();
        await ScriptFreshnessScope.ExtendAsync(store, snapshot, c, [b.MethodologyBId], default);
        Assert.Equal([f.Document.TableDefId, b.TableDefId], c.Order().ToArray());

        // Читачі таблиці й ребра залежностей — те, з чого `RecalculationJob` розширює перелік.
        var readers = await store.GetReadersOfTablesAsync([f.TableCode], default);
        Assert.Empty(readers);
        Assert.Equal([a.MethodologyId, b.MethodologyBId], (await store.GetReaderClosureAsync([a.MethodologyId], default)).Order().ToArray());
    }

    /// <summary>
    /// MS (рецензія F13, D): перегляд скрипта (<c>ScriptPreviewRunner</c>) і справжній прогін після публікації дають ті самі
    /// числа — для джерела й для читача його результату (читач у перегляді бере актуальні результати прогону).
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перегляд_дає_ті_самі_числа_що_прогін_після_публікації_для_джерела_і_читача()
    {
        var f = await ArrangeAsync(209904);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", null), ("oil", 7m));
        var textA = $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;";
        var a = await PublishScriptAsync(f, "A", textA, [("Y", f.OutAColumnId)]);
        var b = await ArrangeReaderAsync(f, a);
        string textB;
        await using (var db = f.Builder.CreateContext())
        {
            textB = $"rows from {b.TableCode};\ntable Src = {f.TableCode};\noutput Z: kg = Sum(Src.{await ColumnCodeAsync(db, f.OutAColumnId)});";
        }

        await RunJobAsync(f.Document, sheetDefId: null);
        var real = await ShapeAsync(f);
        Assert.Equal(["Y|20", "Z|20"], real);

        var previewA = await PreviewAsync(f, a.VersionId, textA);
        var previewB = await PreviewAsync(f, b.VersionBId, textB);

        Assert.Equal(real.Where(r => r.StartsWith("Y|", StringComparison.Ordinal)), OutputsOf(previewA));
        Assert.Equal(real.Where(r => r.StartsWith("Z|", StringComparison.Ordinal)), OutputsOf(previewB));

        // Рядок без числа й невибраний рядок — ті самі причини, що пише прогін (проблеми рядків).
        var rowsA = Assert.Single(previewA.Tables).Rows;
        Assert.Equal("missingInputs", rowsA.Single(r => r.RowNumber == 2).Issue);
        Assert.Equal("notSelected", rowsA.Single(r => r.RowNumber == 3).State);

        // Правка входу: перегляд бачить її одразу, і ті самі числа з'являються після перерахунку.
        await SetValueAsync(f, rowIndex: 0, 30m);
        var editedA = await PreviewAsync(f, a.VersionId, textA);
        await RunJobAsync(f.Document, sheetDefId: f.Document.SheetDefId);
        Assert.Equal(["Y|60", "Z|60"], await ShapeAsync(f));
        Assert.Equal(["Y|60"], OutputsOf(editedA));
        Assert.Equal(["Z|60"], OutputsOf(await PreviewAsync(f, b.VersionBId, textB)));
    }

    /// <summary>
    /// MS (рецензія F13, A): джерело в ЦЬОМУ прогоні нічого не порахувало (вхід зник) — читач не бере числа ПОПЕРЕДНЬОГО
    /// актуального прогону, бо в сітці після прогону джерело теж порожнє.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_не_бачить_числа_попереднього_прогону_якщо_джерело_в_цьому_прогоні_нічого_не_дало()
    {
        var f = await ArrangeAsync(209905);
        await InsertValuesAsync(f, ("gas", 10m), ("oil", 7m));
        var a = await PublishScriptAsync(
            f, "A",
            $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;",
            [("Y", f.OutAColumnId)]);
        await ArrangeReaderAsync(f, a);

        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["Y|20", "Z|20"], await ShapeAsync(f));

        // Вхід джерела зник: A нічого не дає. Без Y читачеві читати нічого — Z не може лишитись 20.
        await using (var db = f.Builder.CreateContext())
        {
            var rowId = f.Document.RowIds[0];
            var column = f.Document.ColumnDefIds[1];
            await db.CellValues
                .Where(c => c.PeriodKeyValue == f.Document.PeriodKey.Value && c.TableRowId == rowId && c.ColumnDefId == column)
                .ExecuteDeleteAsync();
        }

        await RunJobAsync(f.Document, sheetDefId: null);

        Assert.DoesNotContain(await ShapeAsync(f), s => s.StartsWith("Y|", StringComparison.Ordinal));
        Assert.DoesNotContain("Z|20", await ShapeAsync(f));
    }

    /// <summary>
    /// MS (рецензія F13, A): дві методології читають результати одна одної по колу (з'явилося пізнішою прив'язкою) —
    /// прогін відмовляє <c>ECR-CALC-0422 scriptResultCycle</c>, нічого не записавши.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Цикл_читання_результатів_між_методологіями_відмовляє_прогін_і_нічого_не_пише()
    {
        var f = await ArrangeAsync(209906);
        await InsertValuesAsync(f, ("gas", 10m));
        var second = await CreateSecondTableAsync(f);
        string outA;
        await using (var db = f.Builder.CreateContext())
        {
            outA = await ColumnCodeAsync(db, f.OutAColumnId);
        }

        var outB = await ColumnCodeByIdAsync(f, second.OutBColumnId);
        await PublishScriptAsync(
            f, "A", $"rows from {f.TableCode};\ntable Src = {second.TableCode};\noutput Y: kg = Sum(Src.{outB});",
            [("Y", f.OutAColumnId)]);
        await PublishScriptAsync(
            f, "B", $"rows from {second.TableCode};\ntable Src = {f.TableCode};\noutput Z: kg = Sum(Src.{outA});",
            [("Z", second.OutBColumnId)], tableDefId: second.TableDefId);

        var error = await Assert.ThrowsAsync<Ecr.Domain.Abstractions.DomainException>(
            () => RunJobAsync(f.Document, sheetDefId: null));

        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0422.scriptResultCycle", error.Details!["messageKey"]);
        Assert.Empty(await ShapeAsync(f));
    }

    /// <summary>
    /// MS (рецензія 2, п.2): граф залежностей результатів — на рівні (версія, екземпляр таблиці), а не версії. Методологія
    /// A рахує і T1, і T3; на T3 вона читає власну колонку <c>Calculated</c> RES (вихід B), а B (рахує T3) читає колонку
    /// A у T1. Значення не по колу: A@T1 → B@T3 → A@T3. На рівні версій це «A ↔ B» — хибний цикл, прогін відмовляв.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Хибний_цикл_на_рівні_версій_не_відмовляє_а_порядок_дає_обидва_числа_за_один_перерахунок()
    {
        var f = await ArrangeAsync(209909);
        await InsertValuesAsync(f, ("gas", 10m));
        var third = await CreateThirdTableAsync(f);

        // У T1 колонка RES теж є, але нічим не прив'язана: A@T1 нічого не читає з чужих результатів.
        await using (var db = f.Builder.CreateContext())
        {
            db.ColumnDefs.Add(new ColumnDef(f.Document.TableDefId, EcrCode.Create(third.ResCode), Name("Res"), 11, CellDataType.Calculated));
            await db.SaveChangesAsync();
        }

        var aText = $"rows from {f.TableCode}, {third.TableCode};\n"
            + $"output Y: kg = row.{f.C2} * 2 + (row.{third.ResCode}? == null ? 0 : row.{third.ResCode}?);";
        var (a, _) = await PublishScriptAsync(f, "A", aText, [("Y", f.OutAColumnId)]);
        await using (var db = f.Builder.CreateContext())
        {
            db.CalculationBindings.Add(new CalculationBinding(third.TableDefId, third.OutColumnId, a, "Y", "{}"));
            await db.SaveChangesAsync();
        }

        var outA = await ColumnCodeByIdAsync(f, f.OutAColumnId);
        await PublishScriptAsync(
            f, "B", $"rows from {third.TableCode};\ntable Src = {f.TableCode};\noutput Z: kg = Sum(Src.{outA});",
            [("Z", third.ResColumnId)], tableDefId: third.TableDefId);

        await RunJobAsync(f.Document, sheetDefId: null);

        // T1: Y = 10 × 2 = 20; B@T3: Z = 20 (результат A@T1); T3: Y = 7 × 2 + RES (= 20) = 34 — обидва числа правильні.
        Assert.Equal(["Y|20", "Y|34", "Z|20"], await ShapeAsync(f));

        // Той самий порядок після правки входу: одним перерахунком усе доходить (30 → Y=60, Z=60, Y(T3)=14+60).
        await SetValueAsync(f, rowIndex: 0, 30m);
        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["Y|60", "Y|74", "Z|60"], await ShapeAsync(f));
    }

    /// <summary>
    /// MS (рецензія 2, п.2): справжній цикл ЗНАЧЕНЬ лишається відмовою, а повідомлення називає методології й таблиці, що
    /// утворюють його.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Справжній_цикл_відмова_називає_методології_і_таблиці()
    {
        var f = await ArrangeAsync(209910);
        await InsertValuesAsync(f, ("gas", 10m));
        var second = await CreateSecondTableAsync(f);
        var outA = await ColumnCodeByIdAsync(f, f.OutAColumnId);
        var outB = await ColumnCodeByIdAsync(f, second.OutBColumnId);
        var (methodologyA, _) = await PublishScriptAsync(
            f, "A", $"rows from {f.TableCode};\ntable Src = {second.TableCode};\noutput Y: kg = Sum(Src.{outB});",
            [("Y", f.OutAColumnId)]);
        var (methodologyB, _) = await PublishScriptAsync(
            f, "B", $"rows from {second.TableCode};\ntable Src = {f.TableCode};\noutput Z: kg = Sum(Src.{outA});",
            [("Z", second.OutBColumnId)], tableDefId: second.TableDefId);

        var error = await Assert.ThrowsAsync<Ecr.Domain.Abstractions.DomainException>(
            () => RunJobAsync(f.Document, sheetDefId: null));

        Assert.Equal("err.ECR-CALC-0422.scriptResultCycle", error.Details!["messageKey"]);
        Assert.Contains(f.TableCode, error.Message, StringComparison.Ordinal);
        Assert.Contains(second.TableCode, error.Message, StringComparison.Ordinal);
        Assert.Contains($"методологія {methodologyA} ", error.Message, StringComparison.Ordinal);
        Assert.Contains($"методологія {methodologyB} ", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// MS (рецензія 2, п.3а): RL01 на скриптовому шляху. Активне блокування рядка (<c>doc.RowLock</c>) перерахунок «не
    /// бачить» (контракт, Q2): рядок перераховується, як і без блокування, а сам перерахунок не чіпає ні введених
    /// комірок заблокованого рядка, ні самого блокування.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Блокування_рядка_RL01_перерахунок_скрипта_не_бачить_і_не_чіпає_вхід_і_замок()
    {
        var f = await ArrangeAsync(209911);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", 5m), ("oil", 7m));
        var a = await PublishScriptAsync(
            f, "A",
            $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;",
            [("Y", f.OutAColumnId)]);
        await ArrangeReaderAsync(f, a);

        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["Y|10", "Y|20", "Z|30"], await ShapeAsync(f));

        // Рядок 1 блокує ІНША людина, а його вхід тим часом змінюється (збір), і перераховується весь документ.
        await LockRowAsync(f, f.Document.RowIds[0]);
        await SetValueAsync(f, rowIndex: 0, 30m);
        var inputsBefore = await InputsAsync(f);

        await RunJobAsync(f.Document, sheetDefId: null);

        Assert.Equal(["Y|10", "Y|60", "Z|70"], await ShapeAsync(f));
        Assert.Equal(inputsBefore, await InputsAsync(f));
        Assert.Equal(1, await ActiveLocksAsync(f));

        // Той самий результат, що й після зняття блокування: замок на числа перерахунку не діє.
        await using (var db = f.Builder.CreateContext())
        {
            await db.RowLocks.Where(l => l.DocumentId == f.Document.DocumentId).ExecuteDeleteAsync();
        }

        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["Y|10", "Y|60", "Z|70"], await ShapeAsync(f));
    }

    /// <summary>
    /// MS (рецензія 2, п.3б): <c>WorkflowMode = None</c> на скриптовому шляху — як у формульного (<c>B138</c>): затверджений
    /// аркуш (тут — джерело A) при ручному перерахунку пропускається, а після перемикання версії на «без погодження»
    /// перераховується; читач B (інший аркуш) бере свіжі числа джерела.
    /// </summary>
    /// <remarks>
    /// ⚠ Затверджений аркуш тут — ДЖЕРЕЛО (перший); читач (другий аркуш) не затверджений. Затверджений читач
    /// не перераховується ніколи (див. тест про читача на затвердженому аркуші).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Версія_без_погодження_WorkflowMode_None_затверджений_аркуш_скрипта_перераховується()
    {
        var f = await ArrangeAsync(209912);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", 5m), ("oil", 7m));
        var a = await PublishScriptAsync(
            f, "A",
            $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;",
            [("Y", f.OutAColumnId)]);
        await ArrangeReaderAsync(f, a);

        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["Y|10", "Y|20", "Z|30"], await ShapeAsync(f));

        await using (var db = f.Builder.CreateContext())
        {
            var state = new ApprovalState(f.Document.DocumentId, f.Document.SheetDefId, f.Document.PeriodKey.Value);
            state.Submit(userId: 5, Now);
            state.Approve(userId: 6, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        // Контроль (з погодженням): аркуш джерела затверджений — пропускається, Y лишається від першого прогону.
        await SetValueAsync(f, rowIndex: 0, 30m);
        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["Y|10", "Y|20", "Z|30"], await ShapeAsync(f));

        await using (var db = f.Builder.CreateContext())
        {
            await db.Database.ExecuteSqlAsync(
                $"UPDATE cfg.TemplateVersion SET WorkflowMode = 1 WHERE Id = {f.Document.TemplateVersionId}");
        }

        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["Y|10", "Y|60", "Z|70"], await ShapeAsync(f));
    }

    /// <summary>
    /// MS (рецензія 3, п.1): читач (скрипт B) на ЗАТВЕРДЖЕНОМУ аркуші не перераховується разом із джерелом (D-328, D-493, B79:
    /// затверджений аркуш не перераховується; формульний шлях його пропускає). Він лишається зі своїми числами, аркуш
    /// отримує ту саму позначку «пропущено, затверджений» (<c>SheetRecalc</c>/<c>SkippedApproved</c>), що й формульний
    /// залежний (для області аркуша-джерела — теж: тут система знає, що числа читача відстали); після повернення в роботу
    /// перерахунок дає свіжі числа.
    /// </summary>
    /// <remarks>
    /// Мутації: (а) у <c>ExtendTouchedWithScriptReadersAsync</c> прибрати перевірку <c>recalculable.Contains</c> — червоніє
    /// (Z стає 70 при затвердженому читачі); (б) не писати <c>blockedReaders</c> у позначку — червоніє перевірка позначки
    /// для області аркуша.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Читач_на_затвердженому_аркуші_не_перераховується_разом_із_джерелом_а_після_повернення_в_роботу_оновлюється(
        bool areaRun)
    {
        var f = await ArrangeAsync(areaRun ? 209902 : 209901);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", 5m), ("oil", 7m));
        var a = await PublishScriptAsync(
            f, "A",
            $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;",
            [("Y", f.OutAColumnId)]);
        var reader = await ArrangeReaderAsync(f, a);

        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["Y|10", "Y|20", "Z|30"], await ShapeAsync(f));

        // Аркуш читача (другий) затверджено; джерело (перший) — чернетка.
        await using (var db = f.Builder.CreateContext())
        {
            var state = new ApprovalState(f.Document.DocumentId, reader.SheetDefId, f.Document.PeriodKey.Value);
            state.Submit(userId: 5, Now);
            state.Approve(userId: 6, Now);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        // Джерело змінилося (10 → 30) і перераховано: A дає 60 + 10, а затверджений читач лишає 30 (не 70).
        await SetValueAsync(f, rowIndex: 0, 30m);
        await RunJobAsync(f.Document, sheetDefId: areaRun ? f.Document.SheetDefId : null);
        Assert.Equal(["Y|10", "Y|60", "Z|30"], await ShapeAsync(f));

        // Та сама позначка, що й у затвердженого залежного: запис журналу «пропущено» на аркуші читача.
        Assert.Equal(1, await SkippedMarksAsync(f, reader.SheetDefId));

        // Повернення в роботу → перерахунок дає свіжі числа читача.
        await using (var db = f.Builder.CreateContext())
        {
            var state = await db.ApprovalStates.SingleAsync(s =>
                s.DocumentId == f.Document.DocumentId && s.SheetDefId == reader.SheetDefId);
            state.Reopen(userId: 6, "правка", Now);
            await db.SaveChangesAsync();
        }

        await RunJobAsync(f.Document, sheetDefId: areaRun ? f.Document.SheetDefId : null);
        Assert.Equal(["Y|10", "Y|60", "Z|70"], await ShapeAsync(f));
    }

    private async Task<int> SkippedMarksAsync(Fixture f, int sheetDefId)
    {
        await using var db = f.Builder.CreateContext();
        var correlation = Ecr.Application.Calculations.RecalculationSkips.AuditCorrelation(
            f.Document.DocumentId, sheetDefId, f.Document.PeriodKey.Value);
        var entityType = Ecr.Application.Calculations.RecalculationSkips.AuditEntityType;
        var operation = Ecr.Application.Calculations.RecalculationSkips.AuditSkippedApproved;
        return await db.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM aud.StructureChange
                 WHERE EntityType = {entityType} AND Operation = {operation} AND CorrelationId = {correlation}
                """)
            .SingleAsync();
    }

    private async Task LockRowAsync(Fixture f, long rowId)
    {
        await using var db = f.Builder.CreateContext();
        var locker = new User($"rlm_{Guid.NewGuid():N}"[..20], "RL01 locker", AuthProvider.Local);
        locker.SetPassword("not-a-real-hash");
        db.Users.Add(locker);
        await db.SaveChangesAsync();

        var rowKey = await db.TableRows.Where(r => r.Id == rowId).Select(r => r.RowKey).SingleAsync();
        db.RowLocks.Add(RowLock.Create(
            f.Document.DocumentId, f.Document.PeriodKey.Value, null, f.Document.SheetCode, f.TableCode, rowKey.Value,
            Now.AddDays(-1), Now.AddDays(5), locker.Id, "RL01", Now.AddDays(-2)));
        await db.SaveChangesAsync();
    }

    private async Task<int> ActiveLocksAsync(Fixture f)
    {
        await using var db = f.Builder.CreateContext();
        return await db.RowLocks.CountAsync(l => l.DocumentId == f.Document.DocumentId && l.ReleasedAt == null);
    }

    /// <summary>Введені комірки першої таблиці: <c>рядок|колонка|число</c>.</summary>
    private async Task<List<string>> InputsAsync(Fixture f)
    {
        await using var db = f.Builder.CreateContext();
        var cells = await db.CellValues
            .AsNoTracking()
            .Where(c => c.PeriodKeyValue == f.Document.PeriodKey.Value && c.TableDefId == f.Document.TableDefId)
            .Select(c => new { c.TableRowId, c.ColumnDefId, c.ValueNumeric, c.ValueString })
            .ToListAsync();
        return [.. cells
            .Select(c => $"{c.TableRowId}|{c.ColumnDefId}|{c.ValueNumeric:0.####}|{c.ValueString}")
            .Order(StringComparer.Ordinal)];
    }

    private async Task<string> ColumnCodeByIdAsync(Fixture f, int columnId)
    {
        await using var db = f.Builder.CreateContext();
        return await ColumnCodeAsync(db, columnId);
    }

    /// <summary>
    /// MS (рецензія F13, A): читач результатів ЧИТАЧА (C → B → A) без власного читання таблиць перераховується, коли
    /// перераховується аркуш-джерело A: замикання за ребрами <c>calc.MethodologyDependency</c> транзитивне.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Правка_входу_джерела_оновлює_читача_читача_через_транзитивне_замикання_ребер()
    {
        var f = await ArrangeAsync(209907);
        await InsertValuesAsync(f, ("gas", 10m));
        var a = await PublishScriptAsync(
            f, "A", $"rows from {f.TableCode};\noutput Y: kg = row.{f.C2} * 2;", [("Y", f.OutAColumnId)]);
        var b = await ArrangeReaderAsync(f, a);
        var c = await CreateSecondTableAsync(f, "C");
        var outB = await ColumnCodeByIdAsync(f, b.OutBColumnId);
        var (methodologyCId, _) = await PublishScriptAsync(
            f, "C", $"rows from {c.TableCode};\ntable Src = {b.TableCode};\noutput W: kg = Sum(Src.{outB});",
            [("W", c.OutBColumnId)], tableDefId: c.TableDefId);

        // Лише ребро C → B (читання таблиць у C немає): таблицю-джерело C не читає, його тягне замикання.
        await using (var db = f.Builder.CreateContext())
        {
            db.MethodologyDependencies.Add(new MethodologyDependency(methodologyCId, b.MethodologyBId));
            await db.SaveChangesAsync();
        }

        await RunJobAsync(f.Document, sheetDefId: null);
        Assert.Equal(["W|20", "Y|20", "Z|20"], await ShapeAsync(f));

        await SetValueAsync(f, rowIndex: 0, 30m);
        await RunJobAsync(f.Document, sheetDefId: f.Document.SheetDefId);

        Assert.Equal(["W|60", "Y|60", "Z|60"], await ShapeAsync(f));
    }

    /// <summary>
    /// MS (рецензія F13, C): C-5 — значення колонки за замовчуванням для незаповненої комірки діє й на скриптовому шляху
    /// (прогін і перегляд), як у формул і правил: рядок без числа не стає "без входів", а бере дефолт колонки.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Дефолт_колонки_C5_діє_на_скриптовому_шляху_у_прогоні_і_в_перегляді()
    {
        var f = await ArrangeAsync(209908);
        await InsertValuesAsync(f, ("gas", 10m), ("gas", null));
        await using (var db = f.Builder.CreateContext())
        {
            var column = await db.ColumnDefs.SingleAsync(c => c.Id == f.Document.ColumnDefIds[1]);
            column.SetPresentation("4", null, null);
            await db.SaveChangesAsync();
        }

        var text = $"rows from {f.TableCode} where row.{f.C1} == \"gas\";\noutput Y: kg = row.{f.C2} * 2;";
        var a = await PublishScriptAsync(f, "A", text, [("Y", f.OutAColumnId)]);

        await RunJobAsync(f.Document, sheetDefId: null);

        Assert.Equal(["Y|20", "Y|8"], await ShapeAsync(f));
        Assert.Equal(["Y|20", "Y|8"], OutputsOf(await PreviewAsync(f, a.VersionId, text)));
    }

    private async Task<ScriptPreviewResult> PreviewAsync(Fixture f, int versionId, string text)
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IScriptPreviewRunner>().RunAsync(
            new ScriptPreviewRequest(
                versionId, text, f.Document.DocumentId, f.Document.PeriodKey.Value, TableInstanceId: null, MaxRows: 50),
            CancellationToken.None);
    }

    private static List<string> OutputsOf(ScriptPreviewResult preview)
        => [.. preview.Tables
            .SelectMany(t => t.Rows)
            .SelectMany(r => r.Values)
            .Where(v => v.Kind == "output" && v.Error is null)
            .Select(v => $"{v.Name}|{decimal.Parse(v.Text, System.Globalization.CultureInfo.InvariantCulture):0.####}")
            .Order(StringComparer.Ordinal)];

    // ───── стенд ─────

    private async Task<Fixture> ArrangeAsync(int periodKey)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: periodKey, columnCount: 3, rowCount: 3);
        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.AdvanceTo(PeriodState.Open, Now);

        var table = await db.TableDefs.SingleAsync(t => t.Id == document.TableDefId);
        var codes = await db.ColumnDefs
            .Where(c => document.ColumnDefIds.Contains(c.Id))
            .OrderBy(c => c.Ordinal)
            .Select(c => c.Code)
            .ToListAsync();

        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var outA = new ColumnDef(table.Id, EcrCode.Create("OUTA_" + tag), Name("Out A"), 10, CellDataType.Calculated);
        db.ColumnDefs.Add(outA);
        await db.SaveChangesAsync();

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();
        return new Fixture(builder, document, table.Code, codes[0], codes[1], outA.Id, unitId, tag);
    }

    private async Task InsertValuesAsync(Fixture f, params (string Kind, decimal? Value)[] rows)
    {
        await using var db = f.Builder.CreateContext();
        var columns = f.Document.ColumnDefIds;
        for (var i = 0; i < rows.Length; i++)
        {
            db.CellValues.Add(new CellValue(
                new CellAddress(f.Document.PeriodKey, f.Document.RowIds[i], columns[0]),
                f.Document.TableDefId, new CellValueData { ValueString = rows[i].Kind }));
            if (rows[i].Value is { } value)
            {
                db.CellValues.Add(new CellValue(
                    new CellAddress(f.Document.PeriodKey, f.Document.RowIds[i], columns[1]),
                    f.Document.TableDefId, new CellValueData { ValueNumeric = value }));
            }
        }

        await db.SaveChangesAsync();
    }

    private async Task SetValueAsync(Fixture f, int rowIndex, decimal value)
    {
        await using var db = f.Builder.CreateContext();
        var rowId = f.Document.RowIds[rowIndex];
        var column = f.Document.ColumnDefIds[1];
        var cell = await db.CellValues.SingleAsync(c =>
            c.PeriodKeyValue == f.Document.PeriodKey.Value && c.TableRowId == rowId && c.ColumnDefId == column);
        cell.Apply(new CellValueData { ValueNumeric = value });
        await db.SaveChangesAsync();
    }

    /// <summary>Публікує версію-скрипт (текст через домен), вставляє виходи й прив'язки напряму.</summary>
    private async Task<(int MethodologyId, int VersionId)> PublishScriptAsync(
        Fixture f, string key, string text, (string Output, int ColumnId)[] outputs, int? tableDefId = null)
    {
        await using var db = f.Builder.CreateContext();
        var methodology = new Methodology(EcrCode.Create($"SC{key}_{f.Tag}"), Name("script"));
        db.Methodologies.Add(methodology);
        await db.SaveChangesAsync();

        var version = new MethodologyVersion(
            methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now, MethodologyEngine.Script);
        version.SetModes(NumericMode.Strict, CalendarMode.Actual, TraceLevel.Full);
        version.SetScript(text, ScriptCompiler.Hash(text));
        version.Publish(2, "test", new DateOnly(2099, 1, 1), testsPassed: true, Now);
        db.MethodologyVersions.Add(version);
        await db.SaveChangesAsync();

        for (var i = 0; i < outputs.Length; i++)
        {
            var output = new MethodologyOutput(version.Id, EcrCode.Create(outputs[i].Output), f.UnitId, i + 1);
            output.SetPerSubstance(false);
            db.MethodologyOutputs.Add(output);
            db.CalculationBindings.Add(new CalculationBinding(
                tableDefId ?? f.Document.TableDefId, outputs[i].ColumnId, methodology.Id, outputs[i].Output, "{}"));
        }

        await db.SaveChangesAsync();
        return (methodology.Id, version.Id);
    }

    /// <summary>Другий аркуш із таблицею й методологією-читачем B: <c>table Src = A; Sum(Src.OUTA)</c>.</summary>
    private async Task<Second> ArrangeReaderAsync(Fixture f, (int MethodologyId, int VersionId) a)
    {
        var second = await CreateSecondTableAsync(f);
        await using var db = f.Builder.CreateContext();
        var text = $"rows from {second.TableCode};\ntable Src = {f.TableCode};\noutput Z: kg = Sum(Src.{await ColumnCodeAsync(db, f.OutAColumnId)});";
        var (methodologyBId, versionBId) = await PublishScriptAsync(
            f, "B", text, [("Z", second.OutBColumnId)], tableDefId: second.TableDefId);

        await using var extra = f.Builder.CreateContext();
        extra.MethodologyReadTables.Add(new MethodologyReadTable(versionBId, f.TableCode));
        extra.MethodologyDependencies.Add(new MethodologyDependency(methodologyBId, a.MethodologyId));
        await extra.SaveChangesAsync();

        return second with { MethodologyBId = methodologyBId, VersionBId = versionBId };
    }

    /// <summary>Другий аркуш: таблиця, колонка <c>Calculated</c> OUTB і один екземпляр з одним рядком; без методології.</summary>
    private async Task<Second> CreateSecondTableAsync(Fixture f, string suffix = "B")
    {
        await using var db = f.Builder.CreateContext();
        var sheet = new SheetDef(f.Document.TemplateVersionId, EcrCode.Create("SH" + suffix + f.Tag), Name("Sheet " + suffix), 2);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create("TB" + suffix + f.Tag), Name("Table " + suffix), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var outB = new ColumnDef(table.Id, EcrCode.Create("OUT" + suffix + "_" + f.Tag), Name("Out " + suffix), 1, CellDataType.Calculated);
        db.ColumnDefs.Add(outB);
        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(f.Document.PeriodKey, instanceId, f.Document.DocumentId, table.Id, Now));
        db.TableRows.Add(new TableRow(f.Document.PeriodKey, rowId, instanceId, RowKey.Create("R" + suffix + "_" + f.Tag), 1, Now));
        await db.SaveChangesAsync();

        return new Second(sheet.Id, table.Id, table.Code, instanceId, outB.Id, 0, 0);
    }

    private sealed record Third(
        int TableDefId, string TableCode, long InstanceId, int OutColumnId, int ResColumnId, string ResCode);

    /// <summary>
    /// Третя таблиця (окремий аркуш): вхід з тим самим кодом, що в першій (<c>f.C2</c> = 7), вихід A (<c>Calculated</c>) і
    /// колонка RES (<c>Calculated</c>) під вихід B; один екземпляр, один рядок.
    /// </summary>
    private async Task<Third> CreateThirdTableAsync(Fixture f)
    {
        await using var db = f.Builder.CreateContext();
        var sheet = new SheetDef(f.Document.TemplateVersionId, EcrCode.Create("SH3" + f.Tag), Name("Sheet 3"), 3);
        db.SheetDefs.Add(sheet);
        await db.SaveChangesAsync();

        var table = new TableDef(
            sheet.Id, EcrCode.Create("TB3" + f.Tag), Name("Table 3"), 1, TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table);
        await db.SaveChangesAsync();

        var input = new ColumnDef(table.Id, EcrCode.Create(f.C2), Name("Input"), 1, CellDataType.Decimal);
        var outC = new ColumnDef(table.Id, EcrCode.Create("OUTC_" + f.Tag), Name("Out C"), 2, CellDataType.Calculated);
        var res = new ColumnDef(table.Id, EcrCode.Create("RESB"), Name("Res"), 3, CellDataType.Calculated);
        db.ColumnDefs.AddRange(input, outC, res);
        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instanceId = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        var rowId = await loader.ReserveIdsAsync("doc.TableRowSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(f.Document.PeriodKey, instanceId, f.Document.DocumentId, table.Id, Now));
        db.TableRows.Add(new TableRow(f.Document.PeriodKey, rowId, instanceId, RowKey.Create("R3_" + f.Tag), 1, Now));
        await db.SaveChangesAsync();
        db.CellValues.Add(new CellValue(
            new CellAddress(f.Document.PeriodKey, rowId, input.Id), table.Id, new CellValueData { ValueNumeric = 7m }));
        await db.SaveChangesAsync();

        return new Third(table.Id, table.Code, instanceId, outC.Id, res.Id, res.Code);
    }

    private static async Task<string> ColumnCodeAsync(EcrDbContext db, int columnId)
        => await db.ColumnDefs.Where(c => c.Id == columnId).Select(c => c.Code).SingleAsync();

    /// <summary>Форма актуальних результатів: <c>вихід|значення</c>, упорядковано.</summary>
    private async Task<List<string>> ShapeAsync(Fixture f)
    {
        await using var db = f.Builder.CreateContext();
        var rows = await new CalculationResultStore(db, new TestClock(Now))
            .ReadCurrentAsync(f.Document.DocumentId, f.Document.PeriodKey.Value, default);
        return [.. rows.Select(r => $"{r.OutputCode}|{r.Value:0.####}").Order(StringComparer.Ordinal)];
    }

    private async Task RunJobAsync(TestDocument document, int? sheetDefId)
    {
        await using var provider = Provider();
        await using var scope = provider.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<IRecalculationJob>();

        await job.ExecuteAsync(
            new RecalculationRequest(
                ProjectId: document.ProjectId,
                DocumentId: document.DocumentId,
                PeriodKey: document.PeriodKey.Value,
                TriggeredByUserId: 7,
                SheetDefId: sheetDefId),
            new NoOpProgress(),
            CancellationToken.None);
    }

    private ServiceProvider Provider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Ecr"] = sql.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddEcrInfrastructure(configuration);
        services.AddEcrCalculations();
        services.AddEcrApplication();
        services.AddSingleton<ICurrentUser>(Substitute.For<ICurrentUser>());
        services.AddSingleton<Ecr.Domain.Abstractions.IClock>(new TestClock(Now));

        // Задача не має ставити нову задачу в Quartz: у тесті планувальника немає.
        services.AddScoped(_ => Substitute.For<IBackgroundJobScheduler>());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = false });
    }

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private sealed class NoOpProgress : IJobProgress
    {
        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
