using System.Text.RegularExpressions;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// RS (копії аркушів, рішення людини 03.11): кожне місце, що читає чи пише <c>doc.TableInstance</c>,
/// <c>wf.ApprovalState</c>, <c>wf.ApprovalEvent</c> або <c>calc.SubmissionSnapshot</c>, ПЕРЕГЛЯНУТЕ на копії аркушів.
/// </summary>
/// <remarks>
/// ⛔ Предмет. Після RS1 ці таблиці мають <c>SheetCopyId</c> (<c>NULL</c> — оригінал), і запит, написаний як «один
/// рядок на (документ, таблицю, період)» чи «(документ, аркуш, період)», мовчки зливає копію з оригіналом
/// (<c>FirstOrDefault</c> бере рядок копії) або падає на дублі ключа (<c>ToDictionary</c>). Сторож тримає перелік
/// закритим: НОВИЙ файл, що звертається до цих таблиць (EF <c>db.TableInstances</c>… або сирий SQL
/// <c>doc.TableInstance</c>…), червоний, доки автор не вирішить, що він робить із копією, і не впише файл сюди.
/// Перевірка в обидва боки: файл, що більше не звертається, теж червоний — перелік не гниє.
///
/// ⚠ «Pending review» — місця, які ще НЕ перероблено під копії (коміти RS 5–8). Звідти файл переходить у
/// <see cref="Reviewed"/> разом із правкою, що його робить копієвідомим.
///
/// ⚠ Коментарі (<c>//</c>, <c>/* */</c>, SQL <c>--</c>) знімаються до пошуку: згадка таблиці в поясненні — не запит.
///
/// Мутаційний доказ (локально, виконано): додати у файл поза переліком (<c>Persistence/SheetCopyQueries.cs</c>) метод
/// <c>db.ApprovalStates.Count()</c> — червоніє <see cref="Нове_місце_запиту_до_таблиць_копій_потребує_рішення_про_копію"/>.
/// </remarks>
public sealed partial class SheetCopyQuerySitesTests
{
    /// <summary>Переглянуті місця: що файл робить із копією.</summary>
    private static readonly Dictionary<string, string> Reviewed = new(StringComparer.Ordinal)
    {
        // ── копієвідомі (RS 1–4) ──
        ["src/Ecr.Infrastructure/Persistence/RowStore.cs"] =
            "цілі (таблиця, копія) у кожному періоді; Q-17 (рік, дзеркала якоря) — лише оригінал",
        ["src/Ecr.Infrastructure/Persistence/ArchiveAwareCellReader.cs"] =
            "архівні екземпляри несуть SheetCopyId; решта — за Id екземпляра",
        ["src/Ecr.Infrastructure/Persistence/SheetEditGate.cs"] = "applock :c{copy}, стан своєї копії",
        ["src/Ecr.Infrastructure/Security/AccessDecisionService.cs"] = "стан і крок маршруту — своєї копії (SheetKey)",
        ["src/Ecr.Infrastructure/Persistence/WorkflowStore.cs"] =
            "стан, зрізи, журнал — за копією; календар подання — оригінал (копія — коміт 8)",
        ["src/Ecr.Infrastructure/Persistence/RegistryStore.cs"] =
            "NAE purge: «де вживається» id запису в зрізах подання — навмисно в усіх копіях і оригіналі (будь-який зріз блокує)",
        ["src/Ecr.Infrastructure/Persistence/StepApproverStore.cs"] = "AN-59: подані стани оригіналів І копій документа за період — кожен несе SheetCopyId у SubmittedStepRef",
        ["src/Ecr.Infrastructure/Persistence/ApprovalQueueStore.cs"] = "кандидати несуть SheetCopyId; склад і журнал — своєї копії",
        ["src/Ecr.Infrastructure/Jobs/SubmissionSnapshotRetentionJob.cs"] = "ранжування й зрізи погодження — на (аркуш, копія)",
        ["src/Ecr.Infrastructure/Persistence/CalculationIssueStore.cs"] =
            "AN-69: прохід замінює проблеми на (документ, таблиця, копія); читання — за sheetCopyId, null — оригінал",

        // ── лише оригінал (Q5: інтеграція, збір, події джерел, дозволи) ──
        ["src/Ecr.Infrastructure/Integration/IntegrationCellPatcher.cs"] = "запис у екземпляр копії відхиляється",
        ["src/Ecr.Infrastructure/Jobs/MaterializationScheduler.cs"] = "OriginalOnly",
        ["src/Ecr.Infrastructure/Persistence/TemplateVersionDiscardStore.cs"] =
            "лише факт існування рядків за аркушем/таблицею версії — і копія, і оригінал однаково блокують відкидання",
        ["src/Ecr.Infrastructure/Jobs/MaterializationTargets.cs"] = "OriginalOnly (адресати й відсутні екземпляри)",
        ["src/Ecr.Infrastructure/Jobs/MaterializeCollectedDataJob.cs"] = "OriginalOnly: задача на копію мапінгів не має",
        ["src/Ecr.Infrastructure/Jobs/SourceEventSyncJob.cs"] = "OriginalOnly (екземпляри, стан блокування, сирий SQL)",
        ["src/Ecr.Infrastructure/Jobs/RowWindowFetchJob.cs"] = "OriginalOnly",
        ["src/Ecr.Infrastructure/Persistence/RowWindowMapStore.cs"] = "OriginalOnly",
        ["src/Ecr.Infrastructure/Persistence/SourceEventViewStore.cs"] = "OriginalOnly (стан блокування)",
        ["src/Ecr.Infrastructure/Persistence/PermitBindingService.cs"] = "OriginalOnly: рядки з дозволу — лише оригіналу",

        // ── байдужі до копії: адресація за Id екземпляра/рядка або агрегат без ключа таблиці ──
        ["src/Ecr.Infrastructure/Persistence/PermitStore.cs"] = "«де вжито» за Id екземпляра рядка",
        ["src/Ecr.Infrastructure/Persistence/NormalizedCellStore.cs"] = "за Id екземпляра",
        ["src/Ecr.Infrastructure/Persistence/OrphanScanner.cs"] = "рядок → екземпляр за Id",
        ["src/Ecr.Infrastructure/Persistence/ConsistencyIssueReader.cs"] = "рядок → екземпляр за Id",
        ["src/Ecr.Infrastructure/Jobs/ConsistencyCheckJob.cs"] = "анти-з'єднання рядок → екземпляр за Id",
        ["src/Ecr.Infrastructure/Persistence/CollectionStore.cs"] = "проєкти мапінгу (без ключа таблиці)",
        ["src/Ecr.Infrastructure/Persistence/TableDiscussionStore.cs"] = "перелік періодів документа (Distinct); чат — окремий на копію: ключ SheetCopyKey в усіх запитах чату (TC02)",
        ["src/Ecr.Infrastructure/Persistence/RuleCoverageReader.cs"] = "лічильники рядків і документів (копії — теж рядки)",

        // ── перерахунок і Q-17 (RS5): «вид» копії, каскад усередині копії ──
        ["src/Ecr.Infrastructure/Jobs/RecalculationJob.cs"] =
            "формули — усі «види» (RecalculationService); методології, стани подання й гейти — лише оригінал",
        ["src/Ecr.Infrastructure/Jobs/FormulaRecalculationJob.cs"] = "насіння розкладається за видами (оригінал, кожна копія)",
        ["src/Ecr.Infrastructure/Jobs/CrossPeriodFanOut.cs"] =
            "читачі за (таблиця, колонка); вид розрізняє прогін періоду-читача; Distinct за періодами",
        ["src/Ecr.Infrastructure/Persistence/FormulaOverrideStore.cs"] = "позначки ручних значень — за Id рядка; лічильник за TableDefId",
        ["src/Ecr.Infrastructure/Persistence/MonthColumnRouter.cs"] = "Q-17 для копій не діє: екземпляр копії не «маршрутизований»",
        ["src/Ecr.Infrastructure/Persistence/MonthRoutedCellStore.cs"] = "Q-17 — лише оригінали (OriginalOnly)",
        ["src/Ecr.Infrastructure/Persistence/TableFillStore.cs"] = "заповненість — лише оригінали (OriginalOnly), копії не входять",
        ["src/Ecr.Infrastructure/Persistence/PeriodStore.cs"] = "HasReopenedSheetAsync — оригінал; HasReopenedSheetCopyAsync — своя копія",
        ["src/Ecr.Infrastructure/Persistence/CellSourceStore.cs"] = "останній запис журналу — екземпляра своєї копії (@copy)",

        // ── звітність і зведення (RS7): копія — окремий аркуш складу зі СВОЇМ станом ──
        ["src/Ecr.Infrastructure/Reporting/ReportSnapshotBuilder.cs"] =
            "статус зрізу: стан рядка складу (копія — своя); Approved лише коли затверджені оригінал і всі копії",
        ["src/Ecr.Infrastructure/Persistence/CampaignSummaryStore.cs"] =
            "підсумки кампанії: стан СВОГО рядка складу (a.SheetCopyId = s.Id або обидва NULL)",
        ["src/Ecr.Infrastructure/Persistence/DocumentListSummaryStore.cs"] =
            "смуга переліку: копія — окремий аркуш складу зі своїм станом",

        // ── контракт (RS8): відповідь API знає копію ──
        ["src/Ecr.Infrastructure/Persistence/DocumentStore.cs"] =
            "SheetStates/Sheets: ключ КОД#sheetCopyId, стан СВОГО рядка складу; WhereState — те саме; Distinct id/кодів аркушів",
        ["src/Ecr.Infrastructure/Persistence/SheetChangeStore.cs"] =
            "опитування змін і стан аркуша — екземплярів і стану своєї копії (@copy)",
        ["src/Ecr.Infrastructure/Persistence/AuditReader.cs"] = "журнал переходів несе SheetCopyId події",

        // ── склад документа, видалення, перенос версії (RS6) ──
        ["src/Ecr.Infrastructure/Persistence/DocumentSheetCopyStore.cs"] =
            "додає/перейменовує/видаляє копії; видалення — по періодах за SheetCopyId, потім перенумерація",
        ["src/Ecr.Infrastructure/Persistence/DocumentDeletionStore.cs"] =
            "видалення документа забирає ВСІ екземпляри, стани й склад (копії теж); стани копій входять у «чернетка»",
        ["src/Ecr.Infrastructure/Persistence/DocumentVersionMigrationStore.cs"] =
            "перенос версії міняє TableDefId/SheetDefId на місці — SheetCopyId лишається; значення копій входять у «втрату» (Q4)",
        ["src/Ecr.Infrastructure/Persistence/DocumentVersionStore.cs"] =
            "перелік і порівняння зрізів — за Id зрізу/рядка; рядки копій мають власні Id",
    };

    /// <summary>
    /// Pending review (RS commits 5–7): місця, ще НЕ перероблені під копії. Порожньо — усі 8 комітів RS завершено;
    /// словник лишено, щоб наступна зміна могла знову відкласти місце до рішення, а не мовчки його пропустити.
    /// </summary>
    private static readonly Dictionary<string, string> PendingReviewCommits5To7 = new(StringComparer.Ordinal);

    /// <summary>Pending review (RS commit 8 — контракт/UI): порожньо (див. <see cref="PendingReviewCommits5To7"/>).</summary>
    private static readonly Dictionary<string, string> PendingReviewCommit8 = new(StringComparer.Ordinal);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait(TestCategories.Check, TestCategories.Static)]
    [Trait("Decision", "RS-1103")]
    public void Нове_місце_запиту_до_таблиць_копій_потребує_рішення_про_копію()
    {
        var querying = SourceTree.Production()
            .Where(f => !f.Path.Contains("/Configurations/", StringComparison.Ordinal)
                        && QueriesCopyTables(f.Text))
            .Select(f => f.Path)
            .ToHashSet(StringComparer.Ordinal);

        var known = Reviewed.Keys.Concat(PendingReviewCommits5To7.Keys).Concat(PendingReviewCommit8.Keys).ToList();

        var fresh = querying.Except(known, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var gone = known.Except(querying, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.NotEmpty(querying);
        Assert.True(
            fresh.Count == 0,
            "Файл звертається до doc.TableInstance / wf.ApprovalState / wf.ApprovalEvent / calc.SubmissionSnapshot, "
            + "але не переглянутий на копії аркушів (RS): врахуй SheetCopyId (NULL — оригінал; OriginalOnly() для "
            + "інтеграції) і впиши файл у SheetCopyQuerySitesTests.Reviewed з причиною:"
            + Environment.NewLine + string.Join(Environment.NewLine, fresh));
        Assert.True(
            gone.Count == 0,
            "Файл більше не звертається до таблиць копій — прибери його з SheetCopyQuerySitesTests:"
            + Environment.NewLine + string.Join(Environment.NewLine, gone));
        Assert.Empty(Reviewed.Keys.Intersect(PendingReviewCommits5To7.Keys.Concat(PendingReviewCommit8.Keys)));
    }

    /// <summary>Сито впізнає обидві форми звернення й не бачить коментарів і схожих імен.</summary>
    [Theory]
    [InlineData("var x = db.TableInstances.Where(t => t.Id == 1);", true)]
    [InlineData("await db.ApprovalStates\n    .AsNoTracking()", true)]
    [InlineData("from e in db.ApprovalEvents.AsNoTracking()", true)]
    [InlineData("_db.SubmissionSnapshots.Add(entity);", true)]
    [InlineData("var sql = \"\"\"\n SELECT 1 FROM doc.TableInstance AS i\n\"\"\";", true)]
    [InlineData("const string Q = \"SELECT Status FROM [wf].[ApprovalState] a\";", true)]
    [InlineData("FROM calc.SubmissionSnapshot AS s", true)]
    [InlineData("INSERT INTO arc.TableInstance (PeriodKey)", true)]
    [InlineData("// db.TableInstances.Where(...) — пояснення", false)]
    [InlineData("/// <c>wf.ApprovalState</c> — єдине джерело істини", false)]
    [InlineData("/* calc.SubmissionSnapshot */ var a = 1;", false)]
    [InlineData("  -- doc.TableInstance truncate'ить разом із партицією", false)]
    [InlineData("await loader.ReserveIdsAsync(\"doc.TableInstanceSeq\", 1, ct);", false)]
    [InlineData("var t = new TableInstanceRef(1, 2, 3, 4, 5);", false)]
    [InlineData("db.TableInstanceCopies.Count();", false)]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Decision", "RS-1103")]
    public void Сито_впізнає_EF_і_сирий_SQL_без_коментарів(string code, bool expected)
        => Assert.Equal(expected, QueriesCopyTables(code));

    private static bool QueriesCopyTables(string text)
    {
        var code = SqlLineComment().Replace(LineComment().Replace(BlockComment().Replace(text, " "), " "), " ");
        return EfQuery().IsMatch(code) || SqlQuery().IsMatch(code);
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"//[^\n]*", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LineComment();

    [GeneratedRegex(@"--[^\n]*", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SqlLineComment();

    /// <summary>DbSet у ланцюгу EF: <c>.TableInstances</c>, <c>.ApprovalStates</c>, <c>.ApprovalEvents</c>, <c>.SubmissionSnapshots</c>.</summary>
    [GeneratedRegex(@"\.\s*(?:TableInstances|ApprovalStates|ApprovalEvents|SubmissionSnapshots)\b", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex EfQuery();

    /// <summary>Сирий SQL: схема + таблиця, з дужками чи без; <c>doc.TableInstanceSeq</c> — не таблиця.</summary>
    [GeneratedRegex(
        @"\b(?:doc|wf|calc|arc)\]?\.\[?(?:TableInstance|ApprovalState|ApprovalEvent|SubmissionSnapshot)\b(?!\w)",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex SqlQuery();
}
