// tests/Ecr.Infrastructure.Tests/Jobs/RecalculationJobSheetScopeResultsTests.cs
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Errors;
using Ecr.Application.Ports;
using Ecr.Application.Recalculation;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Tests.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// RC14 (приймальна №10, P2-3/P2-4): перерахунок ОБЛАСТІ (аркуш) не торкається результатів методологій
/// ІНШОГО аркуша і не відхиляється через ПОДАНИЙ сусідній аркуш.
/// </summary>
/// <remarks>
/// ⛔ P2-4: прогін області ставав актуальним для ВСЬОГО документа й періоду
/// (<c>SwitchCurrentRunAsync</c>), а писав лише прив'язки свого аркуша — результати сусіднього аркуша
/// лишалися в старому, вже неактуальному прогоні і зникали з <c>ReadCurrentAsync</c>.
/// ⛔ P2-3: гейт <c>RefusedPeriodsAsync</c> рахував подані аркуші ВСЬОГО документа для області одного аркуша.
/// Мутація (локально): прибрати переніс результатів у <c>RecalculationJob</c> / фільтр за
/// <c>SheetDefId</c> у <c>RefusedPeriodsAsync</c> — червоніють відповідні тести.
/// </remarks>
[Collection("SqlServer")]
public sealed class RecalculationJobSheetScopeResultsTests(SqlServerFixture sql)
{
    private static readonly DateTime Now = new(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Перерахунок_аркуша_не_прибирає_результати_методологій_іншого_аркуша()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);

        // Повний прогін документа: обидва аркуші мають результати зі штампом 1000.
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        Assert.Equal(["A|1001", "B|1001"], await ShapeAsync(arranged));

        // Перерахунок ОБЛАСТІ першого аркуша зі штампом 2000.
        await RunJobAsync(arranged, SheetRequest(arranged, arranged.Document.SheetDefId), label: 2_000m);

        // ⛔ ГОЛОВНЕ ТВЕРДЖЕННЯ: A оновлено, B лишився з попереднього прогону. До фіксу тут було лише `A|2001`.
        Assert.Equal(["A|2001", "B|1001"], await ShapeAsync(arranged));

        // Лише один актуальний прогін документа за період, без подвоєних рядків.
        await using var db = arranged.Builder.CreateContext();
        Assert.Equal(1, await db.CalculationRuns.AsNoTracking().CountAsync(r =>
            r.DocumentId == arranged.Document.DocumentId
            && r.PeriodKey == arranged.Document.PeriodKey.Value
            && r.Status == CalculationRun.CurrentStatus));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Повторні_перерахунки_області_не_множать_і_не_гублять_результати_сусіднього_аркуша()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);

        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await RunJobAsync(arranged, SheetRequest(arranged, arranged.Document.SheetDefId), label: 2_000m);
        await RunJobAsync(arranged, SheetRequest(arranged, arranged.Document.SheetDefId), label: 3_000m);

        Assert.Equal(["A|3001", "B|1001"], await ShapeAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.11")]
    public async Task Перерахунок_усього_документа_лишається_повним()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);

        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m);

        Assert.Equal(["A|2001", "B|2001"], await ShapeAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Область_чернеткового_аркуша_рахується_попри_поданий_сусідній_і_не_чіпає_його_результатів()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);

        // Другий аркуш ПОДАНО після першого повного прогону.
        await SubmitSecondSheetAsync(arranged);

        // До фіксу тут летів ECR-CALC-4221 sheetsSubmitted (глухий кут: Submit вимагає перерахунку).
        await RunJobAsync(arranged, SheetRequest(arranged, arranged.Document.SheetDefId), label: 2_000m);

        Assert.Equal(["A|2001", "B|1001"], await ShapeAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Область_поданого_аркуша_відхиляється_а_документ_із_одним_поданим_аркушем_ні()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: true);

        // RC15 (P2-A): сам поданий аркуш — як раніше…
        var scopeThrown = await Record.ExceptionAsync(() =>
            RunJobAsync(arranged, SheetRequest(arranged, arranged.SecondSheetId), label: 2_000m));
        Assert.Equal("ECR-CALC-4221", Assert.IsType<BusinessRuleException>(scopeThrown).ErrorCode);

        // …а перерахунок УСЬОГО документа пропускає поданий аркуш і рахує чернетковий (до фіксу: 4221 на ціле).
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 3_000m);

        Assert.Contains("A|3001", await ShapeAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Документ_де_подані_УСІ_аркуші_відхиляється_і_нічого_не_записує()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: true, submitFirstSheet: true);

        foreach (var sheetDefId in new int?[] { arranged.SecondSheetId, null })
        {
            var thrown = await Record.ExceptionAsync(() =>
                RunJobAsync(arranged, SheetRequest(arranged, sheetDefId), label: 2_000m));

            var error = Assert.IsType<BusinessRuleException>(thrown);
            Assert.Equal("ECR-CALC-4221", error.ErrorCode);
            Assert.Equal("err.ECR-CALC-4221.sheetsSubmitted", error.Details?["messageKey"]);
        }

        // Нічого не записано: результатів немає взагалі.
        Assert.Empty(await ShapeAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Перерахунок_документа_лишає_результати_затвердженого_аркуша_і_оновлює_чернетковий()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        Assert.Equal(["A|1001", "B|1001"], await ShapeAsync(arranged));

        await SubmitSecondSheetAsync(arranged);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m);

        // RC15 (P2-C): B пропущено і ПЕРЕНЕСЕНО з попереднього прогону, а не загублено; A оновлено.
        Assert.Equal(["A|2001", "B|1001"], await ShapeAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Повторні_перерахунки_документа_не_множать_результати_затвердженого_аркуша()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await SubmitSecondSheetAsync(arranged);

        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 3_000m);

        Assert.Equal(["A|3001", "B|1001"], await ShapeAsync(arranged));

        await using var db = arranged.Builder.CreateContext();
        Assert.Equal(1, await db.CalculationRuns.AsNoTracking().CountAsync(r =>
            r.DocumentId == arranged.Document.DocumentId
            && r.PeriodKey == arranged.Document.PeriodKey.Value
            && r.Status == CalculationRun.CurrentStatus));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Методологія_на_двох_аркушах_область_не_губить_результати_другого_аркуша()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false, sharedMethodology: true);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        Assert.Equal(["A|1001", "A|1001", "B|1001"], await ShapeAsync(arranged));

        await SubmitSecondSheetAsync(arranged);
        await RunJobAsync(arranged, SheetRequest(arranged, arranged.Document.SheetDefId), label: 2_000m);

        // Було (RC14, P2-C приймальної): лишався лише A|2001 для першого аркуша - рядок A другого аркуша зникав.
        Assert.Equal(["A|2001", "A|2001", "B|1001"], await ShapeAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Методологія_на_двох_аркушах_документ_із_затвердженим_другим_не_губить_його_результати()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false, sharedMethodology: true);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);

        await SubmitSecondSheetAsync(arranged);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 3_000m);

        Assert.Equal(["A|3001", "A|3001", "B|1001"], await ShapeAsync(arranged));
    }

    /// <summary>
    /// D-557 (D-2 стенда Land): методологія, СПІЛЬНА для чернеткового й ЗАТВЕРДЖЕНОГО аркушів, перераховується на
    /// чернетковому, а числа затвердженого лишаються як були — і для документа, і для області аркуша, і при повторі.
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: у <c>BindingsAsync</c> прибрати <c>.Where(i =&gt; !IsApproved(i))</c> із <c>byPeriod</c> —
    /// затверджений рядок A стає 2001/3001 (D-328), червоні всі три твердження.
    /// </remarks>
    [Theory]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D-2")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Спільна_методологія_не_змінює_числа_затвердженого_аркуша_а_чернетковий_рахує(bool sheetScope)
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false, sharedMethodology: true);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m, stampTables: true);
        Assert.Equal(["A|1001", "A|1001", "B|1001"], await ShapeAsync(arranged));

        await SubmitSecondSheetAsync(arranged);
        var request = SheetRequest(arranged, sheetScope ? arranged.Document.SheetDefId : null);
        await RunJobAsync(arranged, request, label: 2_000m, stampTables: true);

        // Таблиця першого аркуша — нові числа; таблиця затвердженого другого — старі (раніше обидва були 2001).
        Assert.Equal(["A|1001", "A|2001", "B|1001"], await ShapeAsync(arranged));

        // Повтор не множить і не губить перенесене.
        await RunJobAsync(arranged, request, label: 3_000m, stampTables: true);
        Assert.Equal(["A|1001", "A|3001", "B|1001"], await ShapeAsync(arranged));
    }

    /// <summary>
    /// AN-105 / D2-03: аркуш ПОДАНО Й ЗАТВЕРДЖЕНО, поки задача рахувала методології (після рішення «що рахувати»
    /// і до перемикання актуальності). Прогін не стає актуальним, числа затвердженого аркуша — від R1.
    /// ✎ B79: відмовляє саме затвердження (<c>SubmitSecondSheetAsync</c> затверджує); лише подання —
    /// <see cref="Подання_без_затвердження_під_час_нічного_прогону_не_відмовляє_і_позначає_поданий"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати <c>beforeSwitch</c> у виклику <c>CompleteAsync</c> задачі →
    /// винятку немає, а поданий аркуш показує <c>B|2001</c>, червоний. Детерміновано: подання
    /// виконує фейк оркестратора між фазою методологій і завершенням, без таймінгу.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D2-03")]
    public async Task Подання_під_час_методологічної_фази_не_змінює_результатів_поданого_аркуша()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        Assert.Equal(["A|1001", "B|1001"], await ShapeAsync(arranged));

        var thrown = await Record.ExceptionAsync(() => RunJobAsync(
            arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m,
            during: () => SubmitSecondSheetAsync(arranged)));

        var error = Assert.IsType<ConcurrencyConflictException>(thrown);
        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Equal("err.ECR-CALC-0409.recalcStateChanged", error.Details?["messageKey"]);
        Assert.Equal(arranged.SecondSheetId, error.Details?["sheetDefId"]);

        // Попередній прогін лишився актуальним: подавець бачить і підписав саме ці числа.
        Assert.Equal(["A|1001", "B|1001"], await ShapeAsync(arranged));

        // Повтор задачі (конфлікт стану ретраїться) планує вже з новим станом: поданий B переноситься.
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 3_000m);
        Assert.Equal(["A|3001", "B|1001"], await ShapeAsync(arranged));
    }

    /// <summary>
    /// AN-105 / D2-03: період ЗАКРИТО (задача станів) під час методологічної фази —
    /// прогін без погодження S1 у закритий період актуальним не стає.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "D2-03")]
    public async Task Закриття_періоду_під_час_методологічної_фази_не_перемикає_актуальність()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);

        var thrown = await Record.ExceptionAsync(() => RunJobAsync(
            arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m,
            during: () => ClosePeriodAsync(arranged)));

        // ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати перевірку `RecalculationWritePolicy.Check` у
        // `GuardCompletionAsync` → винятку немає, `A|2001` у закритому періоді, червоний.
        var error = Assert.IsType<ConcurrencyConflictException>(thrown);
        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.Null(error.Details?["sheetDefId"]);
        Assert.Equal(["A|1001", "B|1001"], await ShapeAsync(arranged));

        await using var db = arranged.Builder.CreateContext();
        Assert.Equal(1, await db.CalculationRuns.AsNoTracking().CountAsync(r =>
            r.DocumentId == arranged.Document.DocumentId
            && r.PeriodKey == arranged.Document.PeriodKey.Value
            && r.Status == "Failed"));
    }

    /// <summary>
    /// Y1-02: на 2-му аркуші саме йде подання (<c>EnterEditNoWaitAsync</c> → «аркуш подається») у мить
    /// перемикання — повторюється лише коротка транзакція перемикання, методології не перераховуються.
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати цикл повтору навколо <c>CompleteAsync</c> → задача кидає (відкладається),
    /// результати лишаються від R1, червоний. Детерміновано: перші дві спроби взяти аркуш без черги
    /// відмовляють, третя — ні; паузу між повторами обнулено.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Y1-02")]
    public async Task Подання_аркуша_в_мить_перемикання_повторює_лише_перемикання()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);

        var gate = Gate(arranged, busyTimes: 2);
        var runner = await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m, gate: gate);

        Assert.Equal(1, runner.Calls);
        await gate.Received(3).EnterEditNoWaitAsync(
            arranged.Document.DocumentId, arranged.SecondSheetId, arranged.Document.PeriodKey, Arg.Any<CancellationToken>());
        Assert.Equal(["A|2001", "B|2001"], await ShapeAsync(arranged));
    }

    /// <summary>
    /// Y1-02: подання не минуло за всі повтори — задача ВІДКЛАДАЄТЬСЯ (<see cref="JobDeferredException"/>),
    /// а не падає в ретрай (<c>ConcurrencyConflictException</c> → 3 спроби → <c>Failed</c>).
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати перетворення в <c>catch</c> задачі → летить
    /// <c>ConcurrencyConflictException</c>, червоний. Той самий шлях бере й відмова фази формул
    /// (<c>RecalculationService.EnterSheetsAsync</c>).
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "Y1-02")]
    public async Task Тривале_подання_аркуша_відкладає_задачу_а_не_провалює()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);

        var gate = Gate(arranged, busyTimes: int.MaxValue);
        var thrown = await Record.ExceptionAsync(() => RunJobAsync(
            arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m, gate: gate));

        // Відкладення виконавець обробляє ДО `JobRetryPolicy` — спроба не рахується.
        Assert.IsType<JobDeferredException>(thrown);
        Assert.Equal(["A|1001", "B|1001"], await ShapeAsync(arranged));
    }

    /// <summary>
    /// F1 (WW-02): запис комірок не дочекався блокування (<c>ECR-DOC-4091</c>, <c>lockTimeout</c>) — задача
    /// ВІДКЛАДАЄТЬСЯ з паузою <see cref="RecalculationDocumentLock.LockWaitDeferDelay"/>, а не падає в
    /// <c>JobRetryPolicy</c> (3 ретраї → <c>Failed</c>, застарілі числа після переносу версії).
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ: прибрати гілку <c>IsLockWaitTimeout</c> у <c>RecalculationJob</c> — летить
    /// <c>ConcurrencyConflictException</c> (виконавець ретраїть 30/60/120 с), тест червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "F1")]
    public async Task Вичерпане_очікування_блокування_даних_відкладає_задачу_а_не_провалює()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);

        var gate = Gate(arranged, busyTimes: int.MaxValue, messageKey: Ecr.Infrastructure.Persistence.LockWaitGuard.MessageKey);
        var thrown = await Record.ExceptionAsync(() => RunJobAsync(
            arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m, gate: gate));

        var deferred = Assert.IsType<JobDeferredException>(thrown);
        Assert.Equal(RecalculationDocumentLock.LockWaitDeferDelay(arranged.Document.DocumentId), deferred.Delay);
        Assert.True(deferred.Delay > RecalculationDocumentLock.DeferDelay, "повтор довший за паузу зайнятого лока");
        Assert.Equal(["A|1001", "B|1001"], await ShapeAsync(arranged));
    }

    /// <summary>
    /// ✎ UAT-recalc-ux (слово людини 07.11: «тільки погоджені не мають перераховуватися … це також має логуватися»):
    /// ручний перерахунок документа перераховує ПОДАНИЙ аркуш, пише це в журнал змін, і картка документа показує
    /// позначку «перераховано після подання», доки аркуш не подано знову.
    /// </summary>
    /// <remarks>
    /// ⛔ МУТАЦІЙНИЙ ДОКАЗ (локально): у <c>ApprovedSheetsAsync</c> пропускати й <c>Submitted</c> — B лишається 1001,
    /// записів журналу й позначки немає, червоний; прибрати <c>c.ChangedAt &gt; a.SubmittedAt</c> у позначці — повторне
    /// подання її не знімає, червоний. ✎ B79: позначка тут є, бо числа змінились (штамп 1000 → 2000); без зміни —
    /// <see cref="Прогін_без_зміни_чисел_поданому_аркушу_позначки_не_ставить"/>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Поданий_аркуш_перераховується_вручну_пишеться_в_журнал_і_позначається()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);

        await SubmitOnlyAsync(arranged, arranged.SecondSheetId, Now.AddMinutes(-5));

        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m, audit: new AuditWriter(auditDb));
        }

        // Чернетковий A і поданий B перераховано.
        Assert.Equal(["A|2001", "B|2001"], await ShapeAsync(arranged));
        Assert.Equal(1, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditRecalculatedAfterSubmission));
        Assert.Equal(0, await SheetLogCountAsync(arranged, arranged.Document.SheetDefId, RecalculationSkips.AuditRecalculatedAfterSubmission));

        Assert.Equal("-,marked", await MarksAsync(arranged));

        // Повторне подання (пізніше за запис журналу) знімає позначку.
        await ResubmitAsync(arranged, arranged.SecondSheetId, Now.AddMinutes(5));
        Assert.Equal("-,-", await MarksAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    public async Task Затверджений_аркуш_не_перераховується_і_пишеться_в_журнал_як_пропущений()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await SubmitSecondSheetAsync(arranged);

        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged, SheetRequest(arranged, sheetDefId: null), label: 2_000m, audit: new AuditWriter(auditDb));
        }

        // Інший аркуш ТОГО САМОГО документа перераховано, затверджений — ні (значення й результат від R1).
        Assert.Equal(["A|2001", "B|1001"], await ShapeAsync(arranged));
        Assert.Equal(1, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditSkippedApproved));
        Assert.Equal("-,-", await MarksAsync(arranged));
    }

    /// <summary>
    /// ✎ B80 N5 (D-554, рішення людини 19.11): з ЯВНИМ перекриттям адміністратора (<c>IncludeApproved</c>) затверджений аркуш
    /// ПЕРЕРАХОВУЄТЬСЯ (числа й результат нові), у журналі — <c>RecalculatedApproved</c> із автором і причиною, а не
    /// <c>SkippedApproved</c>. Без перекриття — як у сусідньому тесті (пропуск).
    /// </summary>
    /// <remarks>
    /// Мутації: у <c>RecalculationJob</c> замінити <c>request.IncludeApproved</c> на <c>false</c> у <c>ApprovedSheetsAsync</c>-гілці
    /// (методології) — «B|1001» (результат лишився старим); в <c>RecalculationService.Writable</c> прибрати
    /// <c>&amp;&amp; writeApproved</c> — формули затвердженого не пишуться; у <c>SheetLogAsync</c> прибрати
    /// <c>approvedRecalculated</c> — журнал <c>SkippedApproved</c> замість <c>RecalculatedApproved</c>.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Directive", "B80")]
    public async Task Перекриття_адміністратора_перераховує_затверджений_аркуш_із_журналом_хто_і_чому()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await SubmitSecondSheetAsync(arranged);

        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged,
                SheetRequest(arranged, sheetDefId: null) with
                {
                    TriggeredByUserId = null, IncludeApproved = true, OverrideUserId = 9, OverrideReason = "Акт 12: виправлення складу",
                },
                label: 2_000m, audit: new AuditWriter(auditDb));
        }

        // ОБИДВА аркуші перераховано, затверджений теж.
        Assert.Equal(["A|2001", "B|2001"], await ShapeAsync(arranged));
        Assert.Equal(1, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditRecalculatedApproved));
        Assert.Equal(0, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditSkippedApproved));

        // F13: у записі журналу — ХТО (автор перекриття, а не «0»/ініціатор прогону) і ЧОМУ (причина).
        var (changedBy, changeReason, newJson) = await SheetLogRowAsync(
            arranged, arranged.SecondSheetId, RecalculationSkips.AuditRecalculatedApproved);
        Assert.Equal(9, changedBy);
        Assert.Contains("Акт 12: виправлення складу", changeReason, StringComparison.Ordinal);
        Assert.StartsWith("recalc-approved-override: ", changeReason, StringComparison.Ordinal);
        Assert.Contains("approvedOverride", newJson, StringComparison.Ordinal);
    }

    /// <summary>
    /// ✎ B79 (слово людини 16.11: «якщо документ поданий або інші статуси окрім погоджений ми маємо перераховувати його
    /// якщо вхідні дані для нього змінилися»): НІЧНИЙ розклад (без людини й без <c>RecalculateSubmitted</c>) перераховує
    /// поданий аркуш; числа змінились — запис журналу й позначка. Чернетковий аркуш перераховано без позначки.
    /// </summary>
    /// <remarks>
    /// Доти цей тест називався «Автоматичний_прогін_без_людини_поданого_аркуша_не_чіпає» і стверджував <c>B|1001</c>.
    /// ⛔ ДОКАЗ (локально, відкат власної правки): у <c>ApprovedSheetsAsync</c> повернути пропуск <c>Submitted</c> —
    /// B лишається 1001, червоний; у <c>SheetLogAsync</c> не брати рядки <c>Submitted</c> — запису й позначки немає, червоний;
    /// прибрати виклик <c>ResultChangesAsync</c> — запису й позначки немає, червоний.
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    [Trait("Finding", "B79")]
    public async Task Нічний_прогін_перераховує_поданий_аркуш_і_позначає_бо_числа_змінились_а_чернетку_без_позначки()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await SubmitOnlyAsync(arranged, arranged.SecondSheetId, Now.AddMinutes(-5));

        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged, SheetRequest(arranged, sheetDefId: null) with { TriggeredByUserId = null }, label: 2_000m,
                audit: new AuditWriter(auditDb));
        }

        Assert.Equal(["A|2001", "B|2001"], await ShapeAsync(arranged));
        Assert.Equal(1, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditRecalculatedAfterSubmission));
        Assert.Equal(0, await SheetLogCountAsync(arranged, arranged.Document.SheetDefId, RecalculationSkips.AuditRecalculatedAfterSubmission));
        Assert.Equal("-,marked", await MarksAsync(arranged));
    }

    /// <summary>
    /// ✎ B79: прогін, що НЕ змінив жодного числа поданого аркуша (типова ніч над незмінними входами), не лишає йому ні
    /// запису журналу, ні позначки — на кожному шляху задачі: нічний розклад, кнопка, автоперерахунок залежних/збору.
    /// </summary>
    /// <remarks>
    /// ⚠ Для кнопки це зміна поведінки: доти ручний перерахунок позначав кожен поданий аркуш області.
    /// ⛔ ДОКАЗ (локально): у <c>SheetLogAsync</c> прибрати умову <c>changes.Contains</c> — запис і позначка є на всіх
    /// трьох шляхах, червоний; у <c>ChangedResultsSql</c> додати в порівняння <c>CalculationRunId</c> (кожен рядок
    /// «змінений») — червоний.
    /// </remarks>
    [Theory]
    [InlineData(null, false)]
    [InlineData(7, false)]
    [InlineData(null, true)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    [Trait("Finding", "B79")]
    public async Task Прогін_без_зміни_чисел_поданому_аркушу_позначки_не_ставить(int? userId, bool recalculateSubmitted)
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await SubmitOnlyAsync(arranged, arranged.SecondSheetId, Now.AddMinutes(-5));

        // Той самий штамп: прогін рахує заново, але числа ті самі.
        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged,
                SheetRequest(arranged, sheetDefId: null) with { TriggeredByUserId = userId, RecalculateSubmitted = recalculateSubmitted },
                label: 1_000m,
                audit: new AuditWriter(auditDb));
        }

        Assert.Equal(["A|1001", "B|1001"], await ShapeAsync(arranged));
        Assert.Equal(0, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditRecalculatedAfterSubmission));
        Assert.Equal("-,-", await MarksAsync(arranged));

        // Прогін справді відбувся й став актуальним (не «нічого не робив»): актуальний прогін — новий.
        await using var db = arranged.Builder.CreateContext();
        Assert.Equal(2, await db.CalculationRuns.AsNoTracking().CountAsync(r =>
            r.DocumentId == arranged.Document.DocumentId && r.PeriodKey == arranged.Document.PeriodKey.Value));
    }

    /// <summary>
    /// ✎ B79: позначка — на АРКУШ, у якому змінилось число, а не на всі подані аркуші прогону. Обидва аркуші подано;
    /// нічний прогін змінює лише результат першого — позначено його, другий (числа ті самі) — ні. Обидва способи
    /// віднести зміну до аркуша: за екземпляром таблиці результату (D-401) і, без нього, за прив'язкою методології.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): у <c>ResultChangesAsync</c> позначати всі подані аркуші документа, щойно <c>changed</c>
    /// непорожній, — «marked,marked», червоний; у <c>ChangedResultsSql</c> прибрати <c>TableInstanceId</c> з результату
    /// (завжди порожній) при <c>stampTables</c> — лишається зеленим лише завдяки запасному шляху прив'язок, тому окремий
    /// доказ шляху екземпляра: у <c>ResultChangesAsync</c> ігнорувати <c>change.TableInstanceId</c> і зіпсувати запасний
    /// (<c>b.MethodologyId != change.MethodologyId</c>) — «-,marked» лише для <c>stampTables = false</c>.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    [Trait("Finding", "B79")]
    public async Task Нічний_прогін_позначає_лише_той_поданий_аркуш_де_число_змінилось(bool stampTables)
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m, stampTables: stampTables);
        await SubmitOnlyAsync(arranged, arranged.Document.SheetDefId, Now.AddMinutes(-5));
        await SubmitOnlyAsync(arranged, arranged.SecondSheetId, Now.AddMinutes(-5));

        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged, SheetRequest(arranged, sheetDefId: null) with { TriggeredByUserId = null }, label: 2_000m,
                audit: new AuditWriter(auditDb), secondLabel: 1_000m, stampTables: stampTables);
        }

        Assert.Equal(["A|2001", "B|1001"], await ShapeAsync(arranged));
        Assert.Equal(1, await SheetLogCountAsync(arranged, arranged.Document.SheetDefId, RecalculationSkips.AuditRecalculatedAfterSubmission));
        Assert.Equal(0, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditRecalculatedAfterSubmission));
        Assert.Equal("marked,-", await MarksAsync(arranged));
    }

    /// <summary>
    /// ✎ B79: нічний розклад ЗАТВЕРДЖЕНИЙ аркуш не чіпає — числа від попереднього прогону, запис <c>SkippedApproved</c>,
    /// позначки немає (правило пропуску не змінилось, змінилось лише ставлення до поданих).
    /// </summary>
    /// <remarks>⛔ ДОКАЗ (локально, відкат власної правки — фільтр затверджених не мутується): у <c>SheetLogAsync</c>
    /// прибрати гілку <c>approvedRow</c> із запису журналу — <c>SkippedApproved</c> = 0, червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    [Trait("Finding", "B79")]
    public async Task Нічний_прогін_затверджений_аркуш_пропускає_із_журналом_і_без_позначки()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await SubmitSecondSheetAsync(arranged);

        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged, SheetRequest(arranged, sheetDefId: null) with { TriggeredByUserId = null }, label: 2_000m,
                audit: new AuditWriter(auditDb));
        }

        Assert.Equal(["A|2001", "B|1001"], await ShapeAsync(arranged));
        Assert.Equal(1, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditSkippedApproved));
        Assert.Equal(0, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditRecalculatedAfterSubmission));
        Assert.Equal("-,-", await MarksAsync(arranged));
    }

    /// <summary>
    /// ✎ B79: зміну відносять до аркуша за ЕКЗЕМПЛЯРОМ ТАБЛИЦІ результату, коли він є: методологія спільна для двох
    /// поданих аркушів, число змінилось лише в таблиці першого — позначено лише його. Запасний шлях (за прив'язкою
    /// методології) позначив би обидва.
    /// </summary>
    /// <remarks>⛔ ДОКАЗ (локально, мутація 59b): у <c>ResultChangesAsync</c> ігнорувати <c>change.TableInstanceId</c>
    /// (завжди запасний шлях) — «marked,marked», червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "ФВ-9.17")]
    [Trait("Finding", "B79")]
    public async Task Спільна_методологія_число_змінилось_в_одній_таблиці_позначено_лише_її_поданий_аркуш()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false, sharedMethodology: true);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m, stampTables: true);
        await SubmitOnlyAsync(arranged, arranged.Document.SheetDefId, Now.AddMinutes(-5));
        await SubmitOnlyAsync(arranged, arranged.SecondSheetId, Now.AddMinutes(-5));

        // Штамп 2000 — лише рядкам таблиці ПЕРШОГО аркуша; рядки таблиці другого (і A, і B) лишаються 1001.
        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged, SheetRequest(arranged, sheetDefId: null) with { TriggeredByUserId = null }, label: 2_000m,
                audit: new AuditWriter(auditDb), secondLabel: 1_000m, stampTables: true);
        }

        Assert.Equal(["A|1001", "A|2001", "B|1001"], await ShapeAsync(arranged));
        Assert.Equal("marked,-", await MarksAsync(arranged));
    }

    /// <summary>
    /// ✎ B79: порівняння «новий прогін проти актуального» (<c>ChangedResultsAsync</c>) на справжній базі: число, що лише
    /// З'ЯВИЛОСЬ, і число, що лише ЗНИКЛО, — обидва зміна; методологія, якої прогін не рахував (переноситься), зниклою
    /// не вважається; те саме число — не зміна.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): мутація 61a — прибрати напрям «актуальне без нового» — червоне «зникло»; 61b — прибрати
    /// напрям «нове без актуального» — червоне «з'явилось»; 62 — прибрати фільтр перерахованих методологій — червоне
    /// «перенесена не зникла»; 60 — додати в порівняння <c>CalculationRunId</c> — червоне «те саме число».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B79")]
    public async Task Порівняння_результатів_з_явилось_і_зникло_це_зміна_а_перенесена_методологія_і_те_саме_число_ні()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m, stampTables: true);

        var methodologyA = arranged.Versions.Single(v => v.Value.Output == "tonsA").Key;
        var methodologyB = arranged.Versions.Single(v => v.Value.Output == "tonsB").Key;
        var documentId = arranged.Document.DocumentId;
        var sameValue = 1_000m + arranged.Document.PeriodKey.Sequence;

        await using var db = arranged.Builder.CreateContext();
        var store = new CalculationResultStore(db, new TestClock(Now));
        var current = await store.ReadCurrentAsync(documentId, arranged.Document.PeriodKey.Value, default);
        var tableA = current.Single(r => r.OutputCode == "tonsA").TableInstanceId;
        Assert.NotNull(tableA);

        async Task<long> NewRunAsync(params string[] rowKeys)
        {
            var run = new CalculationRun(
                arranged.Document.ProjectId, arranged.Document.PeriodKey.Value, triggeredByUserId: null, Now,
                documentId: documentId);
            db.CalculationRuns.Add(run);
            await db.SaveChangesAsync();

            if (rowKeys.Length > 0)
            {
                await store.WriteResultsAsync(
                    run.Id,
                    [
                        .. rowKeys.Select(rowKey => new CalculationOutput(
                            documentId, rowKey,
                            [new CalculationOutputValue(arranged.Versions[methodologyA].VersionId, null, "tonsA", sameValue, arranged.UnitId)],
                            [], TableInstanceId: tableA)),
                    ],
                    default);
                await db.SaveChangesAsync();
            }

            return run.Id;
        }

        // Те саме число методології A; B прогін не рахував (переноситься) — змін немає.
        var same = await NewRunAsync("R-1");
        Assert.Empty(await store.ChangedResultsAsync(same, documentId, [methodologyA], default));

        // Якби B вважалась перерахованою, її числа «зникли» б — саме тому її в переліку немає.
        var withB = await store.ChangedResultsAsync(same, documentId, [methodologyA, methodologyB], default);
        Assert.Equal(methodologyB, Assert.Single(withB).MethodologyId);

        // Число лише з'явилось (новий рядок R-2 поруч із незмінним R-1).
        var added = await NewRunAsync("R-1", "R-2");
        var appeared = Assert.Single(await store.ChangedResultsAsync(added, documentId, [methodologyA], default));
        Assert.Equal((methodologyA, tableA), (appeared.MethodologyId, appeared.TableInstanceId));

        // Число лише зникло (прогін рахував A, але рядка не дав).
        var gone = await NewRunAsync();
        var vanished = Assert.Single(await store.ChangedResultsAsync(gone, documentId, [methodologyA], default));
        Assert.Equal((methodologyA, tableA), (vanished.MethodologyId, vanished.TableInstanceId));
    }

    /// <summary>
    /// ✎ B79: аркуш ПОДАНО (не затверджено), поки нічний прогін рахував методології. Доти перемикання відмовляло
    /// (<c>ECR-CALC-0409</c>); тепер поданий аркуш перераховує кожен прогін, тож прогін стає актуальним, а аркуш, чиї
    /// числа змінились, отримує позначку (стан читається в транзакції перемикання, після воріт).
    /// </summary>
    /// <remarks>⛔ ДОКАЗ (локально, відкат власної правки): у <c>GuardCompletionAsync</c> повернути відмову для
    /// <c>Submitted</c> — летить <c>ECR-CALC-0409</c>, червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B79")]
    public async Task Подання_без_затвердження_під_час_нічного_прогону_не_відмовляє_і_позначає_поданий()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);

        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged, SheetRequest(arranged, sheetDefId: null) with { TriggeredByUserId = null }, label: 2_000m,
                during: () => SubmitOnlyAsync(arranged, arranged.SecondSheetId, Now.AddMinutes(-5)),
                audit: new AuditWriter(auditDb));
        }

        Assert.Equal(["A|2001", "B|2001"], await ShapeAsync(arranged));
        Assert.Equal("-,marked", await MarksAsync(arranged));
    }

    /// <summary>
    /// ⛔ B63: системний перерахунок ЗАЛЕЖНИХ (RT-25, HSE400 3b, FLERT F6 — <c>RecalculateSubmitted</c>, без людини)
    /// перераховує поданий аркуш тим самим механізмом, що й ручний: журнал <c>RecalculatedAfterSubmission</c> і позначка.
    /// </summary>
    /// <remarks>✎ B79: числа тут змінюються (штамп 1000 → 2000), тому позначка є; без зміни —
    /// <see cref="Прогін_без_зміни_чисел_поданому_аркушу_позначки_не_ставить"/>. ⛔ ДОКАЗ (локально): у
    /// <c>ApprovedSheetsAsync</c> повернути пропуск <c>Submitted</c> — B лишається 1001, червоний.</remarks>
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B63")]
    public async Task Перерахунок_залежних_без_людини_перераховує_поданий_із_журналом_і_позначкою()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await SubmitOnlyAsync(arranged, arranged.SecondSheetId, Now.AddMinutes(-5));

        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged,
                SheetRequest(arranged, sheetDefId: null) with { TriggeredByUserId = null, RecalculateSubmitted = true },
                label: 2_000m,
                audit: new AuditWriter(auditDb));
        }

        Assert.Equal(["A|2001", "B|2001"], await ShapeAsync(arranged));
        Assert.Equal(1, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditRecalculatedAfterSubmission));
        Assert.Equal("-,marked", await MarksAsync(arranged));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B63")]
    public async Task Перерахунок_залежних_без_людини_затверджений_пропускає_із_журналом()
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await SubmitSecondSheetAsync(arranged);

        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(
                arranged,
                SheetRequest(arranged, sheetDefId: null) with { TriggeredByUserId = null, RecalculateSubmitted = true },
                label: 2_000m,
                audit: new AuditWriter(auditDb));
        }

        Assert.Equal(["A|2001", "B|1001"], await ShapeAsync(arranged));
        Assert.Equal(1, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditSkippedApproved));
        Assert.Equal("-,-", await MarksAsync(arranged));
    }

    /// <summary>
    /// ✎ B138 (B75 lane 1b): версія без подання й затвердження (<c>WorkflowMode.None</c>) — старий <c>Approved</c>
    /// (лишився після переносу версії) перерахунок НЕ пропускає й у журнал «пропущено» не пише. Контроль у тому ж
    /// тесті: до перемикання режиму той самий аркуш пропускається.
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): прибрати <c>ExceptWorkflowNone</c> у <c>SubmittedSheetsAsync</c> — B лишається 1001 (значення
    /// першого прогону); прибрати в <c>SheetLogAsync</c> — другий запис «пропущено». Фільтр у <c>SkippedScopesAsync</c>
    /// ловить сусідній тест «…повністю_затверджений_документ…». Період — 2099xx (правило 382).
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B138")]
    public async Task Версія_без_погодження_затверджений_аркуш_перераховується_без_запису_пропущено(bool byHuman)
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false, periodKey: NonePeriod);
        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await SubmitSecondSheetAsync(arranged);
        var request = byHuman
            ? SheetRequest(arranged, sheetDefId: null)
            : SheetRequest(arranged, sheetDefId: null) with { TriggeredByUserId = null };

        // Контроль: режим SubmitApprove — затверджений B пропущено.
        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(arranged, request, label: 2_000m, audit: new AuditWriter(auditDb));
        }

        Assert.Equal(["A|2001", "B|1001"], await ShapeAsync(arranged));
        Assert.Equal(1, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditSkippedApproved));

        await SetWorkflowNoneAsync(arranged);

        await using (var auditDb = arranged.Builder.CreateContext())
        {
            await RunJobAsync(arranged, request, label: 3_000m, audit: new AuditWriter(auditDb));
        }

        Assert.Equal(["A|3001", "B|3001"], await ShapeAsync(arranged));
        Assert.Equal(1, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditSkippedApproved));
        Assert.Equal(0, await SheetLogCountAsync(arranged, arranged.SecondSheetId, RecalculationSkips.AuditRecalculatedAfterSubmission));
    }

    /// <summary>
    /// ✎ B138 (F13): ПОВНІСТЮ затверджений документ (обидва аркуші <c>Approved</c>), перенесений на версію без подання й
    /// затвердження, перераховується — і результати методологій, і значення обох аркушів. Контроль у тому ж тесті: до
    /// перемикання режиму прогін нічого не змінює (усі скоупи пропущено).
    /// </summary>
    /// <remarks>
    /// ⛔ ДОКАЗ (локально): прибрати <c>ExceptWorkflowNone</c> у <c>SkippedScopesAsync</c> — після перемикання результати
    /// лишаються від першого прогону (1001), червоний. Випадки версії — <see cref="WorkflowNoneCase"/>: завжди брати
    /// <c>fn_VersionOf</c> (ігнорувати власний шаблон документа, B74) — червоні <c>ownNone</c> (мусить перерахувати) і
    /// <c>projectNone</c> (документ лишається з погодженням — мусить і далі пропускати).
    /// </remarks>
    [Theory]
    [InlineData(WorkflowNoneCase.Plain)]
    [InlineData(WorkflowNoneCase.SameTemplate)]
    [InlineData(WorkflowNoneCase.OwnNone)]
    [InlineData(WorkflowNoneCase.ProjectNone)]
    [Trait(TestCategories.Stage, TestCategories.Stage5)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Finding", "B138")]
    public async Task Версія_без_погодження_повністю_затверджений_документ_перераховується(string mode)
    {
        var arranged = await ArrangeAsync(submitSecondSheet: false, periodKey: NonePeriod);
        WorkflowNoneCase.Binding binding;
        await using (var bind = arranged.Builder.CreateContext())
        {
            binding = await WorkflowNoneCase.BindAsync(mode, sql.ConnectionString, bind, arranged.Document);
        }

        await RunJobAsync(arranged, SheetRequest(arranged, sheetDefId: null), label: 1_000m);
        await SubmitSheetAsync(arranged, arranged.Document.SheetDefId);
        await SubmitSecondSheetAsync(arranged);

        // Контроль: з погодженням автоматичний прогін повністю затвердженого документа нічого не переписує.
        var automatic = SheetRequest(arranged, sheetDefId: null) with { TriggeredByUserId = null };
        try
        {
            await RunJobAsync(arranged, automatic, label: 2_000m);
        }
        catch (EcrException)
        {
            // «рахувати нічого» може бути й відмовою задачі — головне, що числа не змінилися (нижче).
        }

        Assert.Equal(["A|1001", "B|1001"], await ShapeAsync(arranged));

        await using (var switchDb = arranged.Builder.CreateContext())
        {
            await WorkflowNoneCase.SwitchAsync(switchDb, binding);
        }

        try
        {
            await RunJobAsync(arranged, automatic, label: 3_000m);
        }
        catch (EcrException) when (!binding.DocumentBecomesNone)
        {
            // Документ лишився з погодженням (None лише версія проєкту, не власного шаблону): як у контролі.
        }

        Assert.Equal(binding.DocumentBecomesNone ? RecalculatedShape : FirstRunShape, await ShapeAsync(arranged));
    }

    private static readonly string[] FirstRunShape = ["A|1001", "B|1001"];
    private static readonly string[] RecalculatedShape = ["A|3001", "B|3001"];

    /// <summary>Період нових тестів B138 — далеке майбутнє (правило 382), не перетинається з рештою класу.</summary>
    /// <remarks>⚠ Саме січень: значення прогону — «мітка + номер місяця» (1001, 2001…), як у решті класу.</remarks>
    private const int NonePeriod = 209901;

    private static async Task SetWorkflowNoneAsync(Arranged arranged)
    {
        await using var db = arranged.Builder.CreateContext();
        await db.Database.ExecuteSqlAsync(
            $"UPDATE cfg.TemplateVersion SET WorkflowMode = 1 WHERE Id = {arranged.Document.TemplateVersionId}");
    }

    private static async Task SubmitOnlyAsync(Arranged arranged, int sheetDefId, DateTime at)
    {
        await using var db = arranged.Builder.CreateContext();
        var state = new ApprovalState(arranged.Document.DocumentId, sheetDefId, arranged.Document.PeriodKey.Value);
        state.Submit(userId: 5, at);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
    }

    private static async Task ResubmitAsync(Arranged arranged, int sheetDefId, DateTime at)
    {
        await using var db = arranged.Builder.CreateContext();
        await db.Database.ExecuteSqlAsync($"""
            UPDATE wf.ApprovalState SET SubmittedAt = {at}
             WHERE DocumentId = {arranged.Document.DocumentId} AND SheetDefId = {sheetDefId}
               AND PeriodKey = {arranged.Document.PeriodKey.Value} AND SheetCopyId IS NULL
            """);
    }

    private static async Task<(int ChangedBy, string ChangeReason, string NewJson)> SheetLogRowAsync(
        Arranged arranged, int sheetDefId, string operation)
    {
        await using var db = arranged.Builder.CreateContext();
        var correlation = RecalculationSkips.AuditCorrelation(
            arranged.Document.DocumentId, sheetDefId, arranged.Document.PeriodKey.Value);
        var entityType = RecalculationSkips.AuditEntityType;

        var changedBy = await db.Database
            .SqlQuery<int>($"""
                SELECT CAST(ChangedByUserId AS int) AS Value FROM aud.StructureChange
                 WHERE EntityType = {entityType} AND Operation = {operation} AND CorrelationId = {correlation}
                """)
            .SingleAsync();
        var reason = await db.Database
            .SqlQuery<string>($"""
                SELECT ChangeReason AS Value FROM aud.StructureChange
                 WHERE EntityType = {entityType} AND Operation = {operation} AND CorrelationId = {correlation}
                """)
            .SingleAsync();
        var json = await db.Database
            .SqlQuery<string>($"""
                SELECT NewJson AS Value FROM aud.StructureChange
                 WHERE EntityType = {entityType} AND Operation = {operation} AND CorrelationId = {correlation}
                """)
            .SingleAsync();
        return (changedBy, reason, json);
    }

    private static async Task<int> SheetLogCountAsync(Arranged arranged, int sheetDefId, string operation)
    {
        await using var db = arranged.Builder.CreateContext();
        var correlation = RecalculationSkips.AuditCorrelation(
            arranged.Document.DocumentId, sheetDefId, arranged.Document.PeriodKey.Value);
        var entityType = RecalculationSkips.AuditEntityType;

        return await db.Database
            .SqlQuery<int>($"""
                SELECT COUNT(*) AS Value FROM aud.StructureChange
                 WHERE EntityType = {entityType} AND Operation = {operation} AND CorrelationId = {correlation}
                """)
            .SingleAsync();
    }

    /// <summary>Позначка «перераховано після подання» на картці: [перший аркуш, другий аркуш].</summary>
    private static async Task<string> MarksAsync(Arranged arranged)
    {
        await using var db = arranged.Builder.CreateContext();

        // Склад документа (картка показує стан аркушів складу); будівник документа його не створює.
        if (!await db.DocumentSheets.AnyAsync(s => s.DocumentId == arranged.Document.DocumentId))
        {
            db.DocumentSheets.Add(new DocumentSheet(arranged.Document.DocumentId, arranged.Document.SheetDefId));
            db.DocumentSheets.Add(new DocumentSheet(arranged.Document.DocumentId, arranged.SecondSheetId));
            await db.SaveChangesAsync();
        }

        var card = await new DocumentStore(db).FindAsync(
            arranged.Document.DocumentId, new PeriodKeyFilter(arranged.Document.PeriodKey.Value), CancellationToken.None);
        var sheets = Assert.IsType<DocumentSummary>(card).Sheets ?? [];

        return string.Join(',', sheets.OrderBy(s => s.Code.StartsWith("SHEETRC14", StringComparison.Ordinal) ? 1 : 0)
            .Select(s => s.RecalculatedAfterSubmission ? "marked" : "-"));
    }

    /// <summary>
    /// Фейк воріт аркуша: перший аркуш береться з очікуванням як чернетка, другий без черги
    /// відмовляє «аркуш подається» перші <paramref name="busyTimes"/> разів.
    /// </summary>
    private static ISheetEditGate Gate(
        Arranged arranged, int busyTimes, string messageKey = "err.ECR-DOC-4091.sheetBeingSubmitted")
    {
        var gate = Substitute.For<ISheetEditGate>();
        gate.EnterEditAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(DocumentStatus.Draft);

        var refused = 0;
        gate.EnterEditNoWaitAsync(Arg.Any<long>(), Arg.Any<int>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++refused <= busyTimes
                ? Task.FromException<DocumentStatus>(new ConcurrencyConflictException(
                    "ECR-DOC-4091",
                    $"Аркуш {arranged.SecondSheetId} подається.",
                    new Dictionary<string, object?> { ["messageKey"] = messageKey }))
                : Task.FromResult(DocumentStatus.Draft));

        return gate;
    }

    private static async Task ClosePeriodAsync(Arranged arranged)
    {
        await using var db = arranged.Builder.CreateContext();
        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == arranged.Document.ProjectId && p.PeriodKeyValue == arranged.Document.PeriodKey.Value);
        period.AdvanceTo(PeriodState.Closed, Now);
        await db.SaveChangesAsync();
    }

    private static RecalculationRequest SheetRequest(Arranged arranged, int? sheetDefId)
        => new(
            ProjectId: arranged.Document.ProjectId,
            DocumentId: arranged.Document.DocumentId,
            PeriodKey: arranged.Document.PeriodKey.Value,
            TriggeredByUserId: 7,
            SheetDefId: sheetDefId);

    /// <summary>Форма актуальних результатів: <c>вихід|значення</c>, упорядковано.</summary>
    private static async Task<List<string>> ShapeAsync(Arranged arranged)
    {
        await using var db = arranged.Builder.CreateContext();
        var store = new CalculationResultStore(db, new TestClock(Now));
        var rows = await store.ReadCurrentAsync(
            arranged.Document.DocumentId, arranged.Document.PeriodKey.Value, default);

        return [.. rows.Select(r => $"{r.OutputCode.Replace("tons", string.Empty, StringComparison.Ordinal)}|{r.Value:0.####}")
            .Order(StringComparer.Ordinal)];
    }

    private async Task<Arranged> ArrangeAsync(
        bool submitSecondSheet, bool submitFirstSheet = false, bool sharedMethodology = false, int periodKey = 202601)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: periodKey);

        await using var db = builder.CreateContext();

        var period = await db.Periods.SingleAsync(
            p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == document.PeriodKey.Value);
        period.AdvanceTo(PeriodState.Open, Now);

        var sheet2 = new SheetDef(
            document.TemplateVersionId, EcrCode.Create("SHEETRC14"), Name("Sheet 2"), 2);
        db.SheetDefs.Add(sheet2);
        await db.SaveChangesAsync();

        var table2 = new TableDef(
            sheet2.Id, EcrCode.Create("TBLRC14"), Name("Table 2"), 1,
            TableLayoutKind.PerPeriodInstance, TableRowMode.Fixed);
        db.TableDefs.Add(table2);
        await db.SaveChangesAsync();

        var column2 = new ColumnDef(table2.Id, EcrCode.Create("C1_RC14"), Name("Col 1"), 1, CellDataType.Decimal);
        db.ColumnDefs.Add(column2);
        await db.SaveChangesAsync();

        var loader = new BulkCellLoader(sql.ConnectionString, 1000);
        var instance2Id = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
        db.TableInstances.Add(new TableInstance(
            document.PeriodKey, instance2Id, document.DocumentId, table2.Id, Now));
        await db.SaveChangesAsync();

        var tag = Guid.NewGuid().ToString("N")[..8];
        var versions = new Dictionary<int, (int VersionId, string Output)>();
        foreach (var (key, tableDefId, columnDefId) in new[]
                 {
                     ("A", document.TableDefId, document.ColumnDefIds[1]),
                     ("B", table2.Id, column2.Id),
                 })
        {
            var methodology = new Methodology(EcrCode.Create($"RC14{key}_{tag}"), Name("m"));
            db.Methodologies.Add(methodology);
            await db.SaveChangesAsync();

            var output = "tons" + key;
            db.CalculationBindings.Add(new CalculationBinding(tableDefId, columnDefId, methodology.Id, output, "{}"));

            var version = new MethodologyVersion(methodology.Id, "1.0", CalculationLevel.Configuration, 1, Now);
            db.MethodologyVersions.Add(version);
            await db.SaveChangesAsync();

            versions[methodology.Id] = (version.Id, output);
        }

        if (sharedMethodology)
        {
            // Методологія A прив'язана ще й до таблиці ДРУГОГО аркуша (спільна для обох аркушів).
            var sharedMethodologyId = versions.Keys.First();
            db.CalculationBindings.Add(new CalculationBinding(
                table2.Id, column2.Id, sharedMethodologyId, versions[sharedMethodologyId].Output, "{}"));
            await db.SaveChangesAsync();
        }

        var unitId = await db.Units.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

        var arranged = new Arranged(builder, document, sheet2.Id, versions, unitId, sharedMethodology);
        if (submitSecondSheet)
        {
            await SubmitSecondSheetAsync(arranged);
        }

        if (submitFirstSheet)
        {
            await SubmitSheetAsync(arranged, arranged.Document.SheetDefId);
        }

        return arranged;
    }

    private static Task SubmitSecondSheetAsync(Arranged arranged) => SubmitSheetAsync(arranged, arranged.SecondSheetId);

    private static async Task SubmitSheetAsync(Arranged arranged, int sheetDefId)
    {
        await using var db = arranged.Builder.CreateContext();
        var state = new ApprovalState(
            arranged.Document.DocumentId, sheetDefId, arranged.Document.PeriodKey.Value);
        state.Submit(userId: 5, Now);
        // ✎ UAT-recalc-ux: ручний перерахунок пропускає лише ЗАТВЕРДЖЕНИЙ аркуш (поданий перераховується).
        state.Approve(userId: 6, Now);
        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync();
    }

    private static LocalizedText Name(string value)
        => new(new Dictionary<string, string> { ["en"] = value });

    private static async Task<WritingRunner> RunJobAsync(
        Arranged arranged, RecalculationRequest request, decimal label, Func<Task>? during = null,
        ISheetEditGate? gate = null, IAuditWriter? audit = null, decimal? secondLabel = null, bool stampTables = false)
    {
        await using var db = arranged.Builder.CreateContext();
        var clock = new TestClock(Now);
        var runner = new WritingRunner(arranged, label, during, secondLabel, stampTables);

        var job = new RecalculationJob(
            db,
            runner,
            RunHandler(db, clock),
            FormulaService(),
            clock,
            sheetGate: gate,
            audit: audit)
        {
            CompletionBusyDelay = TimeSpan.Zero,
        };

        await job.ExecuteAsync(request, NoOpProgress.Instance, CancellationToken.None);
        return runner;
    }

    private static RunCalculationHandler RunHandler(EcrDbContext db, TestClock clock)
        => new(
            Substitute.For<IPeriodStore>(),
            Substitute.For<IWorkflowStore>(),
            new CalculationResultStore(db, clock),
            Substitute.For<IBackgroundJobScheduler>(),
            new UnitOfWork(db),
            Substitute.For<Ecr.Application.Security.IAccessDecisionService>(),
            Substitute.For<ICurrentUser>(),
            clock,
            Substitute.For<IAuditWriter>());

    private static RecalculationService FormulaService()
    {
        var rows = Substitute.For<IRowStore>();
        rows.GetTableInstancesAsync(Arg.Any<long>(), Arg.Any<PeriodKey>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableInstanceRef>>([]));

        var units = Substitute.For<IUnitCatalog>();
        units.GetAsync(Arg.Any<CancellationToken>()).Returns(UnitCatalogSnapshot.Empty);

        var header = Substitute.For<IDocumentHeaderStore>();
        header.GetExpressionValuesAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, ExpressionValue>());

        return new(
            Substitute.For<ICellStore>(),
            rows,
            Substitute.For<IPeriodStore>(),
            Substitute.For<IMetadataCache>(),
            Substitute.For<ITemplateVersionStore>(),
            Substitute.For<IFormulaEngine>(),
            units,
            Substitute.For<IRegistryStore>(),
            header,
            Substitute.For<IAuditWriter>(),
            new TestClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Substitute.For<IUnitOfWork>(), Substitute.For<ISheetEditGate>());
    }

    private sealed record Arranged(
        TestDocumentBuilder Builder,
        TestDocument Document,
        int SecondSheetId,
        Dictionary<int, (int VersionId, string Output)> Versions,
        int UnitId,
        bool SharedMethodology = false);

    /// <summary>
    /// Фейк оркестратора: на кожну прив'язку пише ОДИН вихід її методології (<c>tonsA</c>/<c>tonsB</c>)
    /// зі штампом прогону через справжнє сховище, власним контекстом.
    /// </summary>
    /// <param name="arranged">Стенд.</param>
    /// <param name="label">Штамп прогону в значеннях.</param>
    /// <param name="during">
    /// D2-03: дія іншого «користувача» між фазою методологій і завершенням (один раз).
    /// </param>
    /// <param name="secondLabel">✎ B79: окремий штамп для методології ДРУГОГО аркуша (<c>tonsB</c>); <c>null</c> — той самий.</param>
    /// <param name="stampTables">✎ B79: писати результат з екземпляром таблиці (D-401), як справжній оркестратор.</param>
    private sealed class WritingRunner(
        Arranged arranged, decimal label, Func<Task>? during = null, decimal? secondLabel = null, bool stampTables = false)
        : ICalculationRunner
    {
        private Func<Task>? _during = during;

        /// <summary>Скільки разів оркестратор рахував прив'язки (Y1-02: повтор перемикання — без перерахунку).</summary>
        public int Calls { get; private set; }

        public async Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            Calls++;
            if (bindings.Count == 0)
            {
                return new ModuleProfile();
            }

            await using var db = arranged.Builder.CreateContext();
            var store = new CalculationResultStore(db, new TestClock(Now));

            var outputs = bindings
                .DistinctBy(b => arranged.SharedMethodology ? (b.MethodologyId, b.TableInstanceId) : (b.MethodologyId, 0L))
                .Select(b =>
                {
                    var (versionId, output) = arranged.Versions[b.MethodologyId];
                    var rowKey = arranged.SharedMethodology ? $"I{b.TableInstanceId}" : "R-1";
                    // ✎ B79: «другий» штамп — методології другого аркуша і (спільна методологія) рядкам таблиці другого аркуша.
                    var second = output == "tonsB"
                                 || (arranged.SharedMethodology && b.TableInstanceId != arranged.Document.TableInstanceId);
                    var stamp = second ? secondLabel ?? label : label;
                    return new CalculationOutput(documentId, rowKey,
                    [
                        new CalculationOutputValue(versionId, null, output, stamp + periodKey.Sequence, arranged.UnitId),
                    ], [], TableInstanceId: stampTables ? b.TableInstanceId : null);
                })
                .ToList();

            await store.WriteResultsAsync(calculationRunId, outputs, ct);
            await db.SaveChangesAsync(ct);

            if (Interlocked.Exchange(ref _during, null) is { } action)
            {
                await action();
            }

            return new ModuleProfile();
        }
    }

    private sealed class NoOpProgress : IJobProgress
    {
        public static readonly NoOpProgress Instance = new();

        public Task ReportAsync(int percent, string? message, CancellationToken ct) => Task.CompletedTask;
    }
}
