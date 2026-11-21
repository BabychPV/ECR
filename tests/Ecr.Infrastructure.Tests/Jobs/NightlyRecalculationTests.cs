// tests/Ecr.Infrastructure.Tests/Jobs/NightlyRecalculationTests.cs
using System.Collections.Concurrent;
using System.Text.Json;
using Ecr.Application.Calculations;
using Ecr.Application.Common;
using Ecr.Application.Documents;
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
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// ✎ PSS-15 (<c>D-562</c>, рішення людини 21.11): нічний перерахунок — лише поточний рік і лише документи, входи яких
/// змінились після актуального прогону; кожен вид входу дає перерахунок, власні записи перерахунку — ні.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ Годинники тестів — у ДАЛЕКОМУ майбутньому, різних «слотах», бо частина входів ГЛОБАЛЬНА (будь-який довідник, публікація,
/// журнал структури з <c>TemplateVersionId = 0</c>), а база колекції спільна: інші тести пишуть такі рядки своїм часом (не
/// пізніше 2031), і «незмінний» документ інакше бачив би чужу правку. Слоти: довідник 2041, публікації 2042, глобальна
/// конфігурація 2043 (рядок кожного слота бачать лише тести того самого чи ранішого слота, а причина — найменший код, тож
/// ранніший слот однаково отримує свою), локальні входи 2050, «тихі» тести 2060 (після всіх глобальних рядків). Періоди
/// документів — 2026 (явний <c>Year</c> у завданні), проєкт — чернетка (стан періодів збережений, не від дати).
/// </para>
/// <para>
/// МУТАЦІЇ (локально, див. звіт коміту): (1) <c>NightlyDecisionAsync</c> завжди «рахувати» (без пропуску) — червоні
/// <see cref="Без_змін_документ_пропускається_і_нового_прогону_немає"/> і тихі випадки <see cref="Власні_записи_перерахунку_й_нерелевантні_події_не_будять_документ"/>;
/// (2) дитина без року (<c>Year = null</c>) — червоний <see cref="Дитина_несе_поточний_рік_і_рахує_лише_його_періоди"/>;
/// (3) межа без запасу (<c>NightlyInputsSkew</c> = 0) — червоний випадок «правка за 2 хв до старту»; (4) прибрати гілку
/// відповідного входу з <c>NightlyInputChangeQuery</c> — червоний його випадок <see cref="Зміна_входу_будить_документ"/>.
/// </para>
/// </remarks>
[Collection("SqlServer")]
[Trait(TestCategories.Stage, TestCategories.Stage5)]
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait("Finding", "PSS-15")]
public sealed class NightlyRecalculationTests(SqlServerFixture sql)
{
    private const int Year = 2026;

    private static readonly PeriodKey January = new(202601);

    private static readonly PeriodKey February = new(202602);

    private static readonly DateTime RegistrySlot = new(2041, 6, 15, 1, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime PublicationSlot = new(2042, 6, 15, 1, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime ConfigurationSlot = new(2043, 6, 15, 1, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime ChangeSlot = new(2050, 6, 15, 1, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime QuietSlot = new(2060, 6, 15, 1, 0, 0, DateTimeKind.Utc);

    /// <summary>Вид зміни входу для <see cref="Зміна_входу_будить_документ"/>.</summary>
    public enum InputKind
    {
        UserEdit,
        ImportEdit,
        IntegrationEdit,
        PermitValue,
        EditInOtherPeriod,
        RowAdded,
        InstanceChanged,
        FormulaChanged,
        PeriodReopened,
        DocumentPeriodReopened,
        Unapproved,
        SourceDocumentEdited,
        SourceDocumentRecalculated,
        EditJustBeforeBaseline,
        HeaderEdited,
        SheetCopyDeleted,
    }

    /// <summary>«Тиха» подія для <see cref="Власні_записи_перерахунку_й_нерелевантні_події_не_будять_документ"/>.</summary>
    public enum QuietKind
    {
        RecalculationWrite,
        CalcErrorEvent,
        SheetRecalcJournal,
        ReportSnapshotJournal,
        RowLockJournal,
        Approved,
        OtherTemplateVersionChange,
        ReportPublication,
        OtherDocumentEdit,
        EditLongBeforeBaseline,
    }

    [Theory]
    [InlineData("2026-12-31T18:59:00Z", 2026)]
    [InlineData("2026-12-31T19:00:00Z", 2027)]
    [InlineData("2026-03-10T00:00:00Z", 2026)]
    public void Поточний_рік_береться_в_поясі_проєкту(string utc, int expected)
    {
        // Asia/Atyrau — UTC+5: 19:00 UTC 31 грудня — вже північ 1 січня на майданчику.
        var at = DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal);
        Assert.Equal(expected, RecalculationJob.NightlyYear("Asia/Atyrau", at));
    }

    /// <summary>
    /// Рік — з годинника в поясі проєкту, у момент виконання; дитина несе його числом і рахує лише відкриті періоди ЦЬОГО
    /// року: грудень минулого року документа, хоч і є, не перераховується. Проєкт минулого року нічого не ставить.
    /// </summary>
    [Fact]
    public async Task Дитина_несе_поточний_рік_і_рахує_лише_його_періоди()
    {
        var arranged = await ArrangeAsync();
        var december = new PeriodKey(202512);
        await arranged.Builder.AddPeriodInstanceAsync(arranged.Document, december);

        var at = new DateTime(2026, 3, 10, 0, 30, 0, DateTimeKind.Utc);
        var nightly = await NightlyAsync(arranged, at, year: null);

        var child = Assert.Single(nightly.Children);
        Assert.Equal(Year, child.Year);
        Assert.Null(child.PeriodKey);
        Assert.True(child.Nightly);
        Assert.Equal(arranged.Document.DocumentId, child.DocumentId);

        var runner = await RunChildrenAsync(arranged, nightly.Children, at);
        Assert.Equal([January.Value, February.Value], runner.Periods);

        await using (var db = arranged.Builder.CreateContext())
        {
            Assert.False(await db.CalculationRuns.AnyAsync(r =>
                r.ProjectId == arranged.Document.ProjectId && r.PeriodKey == december.Value));
        }

        // Проєкт 2026 у 2027 (пільговий строк) — жодної дитини, усе пояснено в журналі обслуговування.
        var nextYear = await NightlyAsync(arranged, new DateTime(2027, 2, 1, 0, 30, 0, DateTimeKind.Utc), year: null);
        Assert.Empty(nextYear.Children);
        Assert.Equal(2027, nextYear.Details.GetProperty("year").GetInt32());
        Assert.Equal(0, nextYear.Details.GetProperty("queued").GetInt32());
    }

    /// <summary>
    /// Головне: друга ніч без жодної зміни входу не ставить задачі й не створює прогону; рядок задачі й журнал кажуть
    /// «пропущено». Перша ніч — рахує (прогонів ще немає).
    /// </summary>
    [Fact]
    public async Task Без_змін_документ_пропускається_і_нового_прогону_немає()
    {
        var arranged = await ArrangeAsync();
        var first = await BaselineAsync(arranged, QuietSlot);
        Assert.Equal("noBaseline", Assert.Single(Reasons(first.Details)).Key);

        var runsBefore = await RunCountAsync(arranged);
        Assert.Equal(2, runsBefore);

        var second = await NightlyAsync(arranged, QuietSlot.AddDays(1));

        Assert.Empty(second.Children);
        Assert.Equal(1, second.Details.GetProperty("skippedUnchanged").GetInt32());
        Assert.Equal(0, second.Details.GetProperty("queued").GetInt32());
        Assert.Equal(runsBefore, await RunCountAsync(arranged));

        Assert.True(JobProgressMessageCodec.TryDecode(second.LastMessage, out var envelope));
        Assert.Equal(RecalculationJob.NightlyFannedOutKey, envelope.Key);
        Assert.Equal("0", envelope.Params!["count"]);
        Assert.Equal("1", envelope.Params["skipped"]);
        Assert.Equal("2026", envelope.Params["year"]);
    }

    /// <summary>Кожен вид входу після актуального прогону будить документ — з тією причиною, що в журналі.</summary>
    [Theory]
    [InlineData(InputKind.UserEdit, "cellEdits")]
    [InlineData(InputKind.ImportEdit, "cellEdits")]
    [InlineData(InputKind.IntegrationEdit, "cellEdits")]
    [InlineData(InputKind.PermitValue, "cellEdits")]
    [InlineData(InputKind.EditInOtherPeriod, "cellEdits")]
    [InlineData(InputKind.RowAdded, "cellEdits")]
    [InlineData(InputKind.InstanceChanged, "instances")]
    [InlineData(InputKind.FormulaChanged, "configuration")]
    [InlineData(InputKind.PeriodReopened, "periodState")]
    [InlineData(InputKind.DocumentPeriodReopened, "periodState")]
    [InlineData(InputKind.Unapproved, "unapproved")]
    [InlineData(InputKind.SourceDocumentEdited, "source")]
    [InlineData(InputKind.SourceDocumentRecalculated, "source")]
    [InlineData(InputKind.EditJustBeforeBaseline, "cellEdits")]
    [InlineData(InputKind.HeaderEdited, "document")]
    [InlineData(InputKind.SheetCopyDeleted, "document")]
    public async Task Зміна_входу_будить_документ(InputKind kind, string reason)
    {
        var arranged = await ArrangeAsync();
        TestDocument? source = null;
        if (kind is InputKind.SourceDocumentEdited or InputKind.SourceDocumentRecalculated)
        {
            source = await BindSourceAsync(arranged);
        }

        var baseline = ChangeSlot;
        await BaselineAsync(arranged, baseline);
        var at = kind == InputKind.EditJustBeforeBaseline ? baseline.AddMinutes(-2) : baseline.AddHours(1);

        await using (var db = arranged.Builder.CreateContext())
        {
            var d = arranged.Document;
            switch (kind)
            {
                case InputKind.UserEdit:
                case InputKind.EditJustBeforeBaseline:
                    await CellChangeAsync(db, d, February, CellChangeOrigins.UserEdit, at);
                    break;
                case InputKind.ImportEdit:
                    await CellChangeAsync(db, d, February, CellChangeOrigins.Import, at);
                    break;
                case InputKind.IntegrationEdit:
                    await CellChangeAsync(db, d, February, CellChangeOrigins.Integration, at);
                    break;
                case InputKind.PermitValue:
                    await CellChangeAsync(db, d, February, CellChangeOrigins.Permit, at);
                    break;
                case InputKind.EditInOtherPeriod:
                    // Правка ІНШОГО періоду (минулого грудня) — вхід через [Period:-1] січня.
                    await CellChangeAsync(db, d, new PeriodKey(202512), CellChangeOrigins.UserEdit, at);
                    break;
                case InputKind.RowAdded:
                    await db.Database.ExecuteSqlAsync($"""
                        INSERT INTO aud.RowChange (ChangedAt, DocumentId, PeriodKey, TableInstanceId, TableDefId, TableRowId, RowKey,
                                                   Action, ChangedByUserId, Origin)
                        VALUES ({at}, {d.DocumentId}, {January.Value}, {d.TableInstanceId}, {d.TableDefId}, {d.RowIds[0]}, N'R9',
                                1, 1, N'UserEdit')
                        """);
                    break;
                case InputKind.InstanceChanged:
                    await db.Database.ExecuteSqlAsync(
                        $"UPDATE doc.TableInstance SET ModifiedAt = {at} WHERE PeriodKey = {January.Value} AND Id = {d.TableInstanceId}");
                    break;
                case InputKind.FormulaChanged:
                    await StructureChangeAsync(db, d.TemplateVersionId, "cfg.FormulaDef", at);
                    break;
                case InputKind.PeriodReopened:
                    await db.Database.ExecuteSqlAsync(
                        $"UPDATE doc.Period SET StateChangedAt = {at} WHERE ProjectId = {d.ProjectId} AND PeriodKey = {January.Value}");
                    break;
                case InputKind.DocumentPeriodReopened:
                    db.DocumentPeriodReopens.Add(DocumentPeriodReopen.Open(
                        d.DocumentId, January.Value, at.AddDays(5), "PSS-15 test", await AnyUserAsync(db), at));
                    await db.SaveChangesAsync();
                    break;
                case InputKind.Unapproved:
                    db.ApprovalEvents.Add(new ApprovalEvent(
                        d.DocumentId, d.SheetDefId, January.Value, DocumentStatus.Approved, DocumentStatus.Draft,
                        ApprovalAction.Reopen, byUserId: null, at, reason: "PSS-15 test"));
                    await db.SaveChangesAsync();
                    break;
                case InputKind.SourceDocumentEdited:
                    await CellChangeAsync(db, source!, January, CellChangeOrigins.UserEdit, at);
                    break;
                case InputKind.SourceDocumentRecalculated:
                    var run = new CalculationRun(source!.ProjectId, January.Value, null, at, source.DocumentId);
                    run.Complete("Succeeded", at, null, null);
                    run.MakeCurrent();
                    db.CalculationRuns.Add(run);
                    await db.SaveChangesAsync();
                    break;
                case InputKind.HeaderEdited:
                case InputKind.SheetCopyDeleted:
                    // ✎ Огляд H1: шапка (HDR.*, DocumentHeaderHandlers) і склад копій аркушів (DocumentSheetCopyHandler —
                    // видаляє екземпляри, рядки й комірки) не лишають ні aud.CellChange, ні aud.RowChange — лише
                    // aud.SecurityEvent і дотик документа тим самим портом (обробник копій —
                    // DocumentSheetCopyHandlersTests.Додавання_перейменування_й_видалення_копії_торкаються_документа).
                    await new DocumentStore(db).TouchAsync(d.DocumentId, 1, at, CancellationToken.None);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        var next = await NightlyAsync(arranged, baseline.AddDays(1));

        var child = Assert.Single(next.Children);
        Assert.Equal(arranged.Document.DocumentId, child.DocumentId);
        Assert.Equal(reason, Assert.Single(Reasons(next.Details)).Key);
    }

    /// <summary>Глобальні входи: дані будь-якого довідника, публікація шаблону/методології (і перемикання рушія), прив'язки.</summary>
    [Theory]
    [InlineData("registry")]
    [InlineData("templatePublication")]
    [InlineData("methodologyPublication")]
    [InlineData("bindingChange")]
    public async Task Глобальна_зміна_конфігурації_будить_документ(string kind)
    {
        var baseline = kind switch
        {
            "registry" => RegistrySlot,
            "bindingChange" => ConfigurationSlot,
            _ => PublicationSlot,
        };
        var arranged = await ArrangeAsync();
        await BaselineAsync(arranged, baseline);
        var at = baseline.AddHours(1);

        await using (var db = arranged.Builder.CreateContext())
        {
            switch (kind)
            {
                case "registry":
                    var tag = Guid.NewGuid().ToString("N")[..10];
                    var registry = new RegistryDef(EcrCode.Create($"N15R{tag}"), Name("nightly"), isTemporal: false);
                    registry.MarkDataChanged(at);
                    db.RegistryDefs.Add(registry);
                    await db.SaveChangesAsync();
                    break;
                case "templatePublication":
                    await PublicationAsync(db, "TemplateVersion", at);
                    break;
                case "methodologyPublication":
                    await PublicationAsync(db, "calc.MethodologyVersion", at);
                    break;
                default:
                    await StructureChangeAsync(db, 0, "cfg.CalculationBinding", at);
                    break;
            }
        }

        var next = await NightlyAsync(arranged, baseline.AddDays(1));

        Assert.Single(next.Children);
        var expected = kind switch
        {
            "registry" => "registry",
            "bindingChange" => "configuration",
            _ => "publication",
        };
        Assert.Equal(expected, Assert.Single(Reasons(next.Details)).Key);
    }

    /// <summary>
    /// Записи, які пише сам перерахунок (значення формул, події помилки, журнал аркушів, зріз застарів), блокування рядків,
    /// затвердження (без повернення), чужа версія шаблону, публікація звіту, правка іншого документа і правка задовго до
    /// прогону — НЕ будять документ: інакше нічний прогін сам собі давав би «зміну» щоночі.
    /// </summary>
    [Theory]
    [InlineData(QuietKind.RecalculationWrite)]
    [InlineData(QuietKind.CalcErrorEvent)]
    [InlineData(QuietKind.SheetRecalcJournal)]
    [InlineData(QuietKind.ReportSnapshotJournal)]
    [InlineData(QuietKind.RowLockJournal)]
    [InlineData(QuietKind.Approved)]
    [InlineData(QuietKind.OtherTemplateVersionChange)]
    [InlineData(QuietKind.ReportPublication)]
    [InlineData(QuietKind.OtherDocumentEdit)]
    [InlineData(QuietKind.EditLongBeforeBaseline)]
    public async Task Власні_записи_перерахунку_й_нерелевантні_події_не_будять_документ(QuietKind kind)
    {
        var arranged = await ArrangeAsync();
        var baseline = QuietSlot.AddDays(2);
        await BaselineAsync(arranged, baseline);
        var at = baseline.AddHours(1);

        await using (var db = arranged.Builder.CreateContext())
        {
            var d = arranged.Document;
            switch (kind)
            {
                case QuietKind.RecalculationWrite:
                    await CellChangeAsync(db, d, February, CellChangeOrigins.Recalculation, at);
                    break;
                case QuietKind.CalcErrorEvent:
                    await CellChangeAsync(db, d, February, CellChangeOrigins.CalcError, at);
                    break;
                case QuietKind.SheetRecalcJournal:
                    await StructureChangeAsync(db, d.TemplateVersionId, RecalculationSkips.AuditEntityType, at);
                    break;
                case QuietKind.ReportSnapshotJournal:
                    await StructureChangeAsync(db, d.TemplateVersionId, RunCalculationHandler.SnapshotAuditEntityType, at);
                    break;
                case QuietKind.RowLockJournal:
                    await StructureChangeAsync(
                        db, d.TemplateVersionId, Ecr.Application.Documents.RowLocks.RowLockRules.AuditEntityType, at);
                    break;
                case QuietKind.Approved:
                    db.ApprovalEvents.Add(new ApprovalEvent(
                        d.DocumentId, d.SheetDefId, February.Value, DocumentStatus.Submitted, DocumentStatus.Approved,
                        ApprovalAction.Approve, byUserId: null, at));
                    await db.SaveChangesAsync();
                    break;
                case QuietKind.OtherTemplateVersionChange:
                    await StructureChangeAsync(db, 2_000_000_000, "cfg.FormulaDef", at);
                    break;
                case QuietKind.ReportPublication:
                    await PublicationAsync(db, "ReportVersion", at);
                    break;
                case QuietKind.OtherDocumentEdit:
                    var other = await arranged.Builder.BuildAsync();
                    await CellChangeAsync(db, other, January, CellChangeOrigins.UserEdit, at);
                    break;
                default:
                    // Задовго до прогону (поза запасом NightlyInputsSkew): прогін її вже бачив.
                    await CellChangeAsync(db, d, February, CellChangeOrigins.UserEdit, baseline.AddMinutes(-30));
                    break;
            }
        }

        var next = await NightlyAsync(arranged, baseline.AddDays(1));

        Assert.Empty(next.Children);
        Assert.Equal(1, next.Details.GetProperty("skippedUnchanged").GetInt32());
    }

    /// <summary>
    /// Страховка віком: актуальний прогін старший за тиждень — рахуємо навіть без видимих змін. Вимкнений пропуск
    /// (<c>SkipUnchanged = false</c>) — рахуємо щоночі. Документ без відкритих періодів року — нічого.
    /// </summary>
    [Fact]
    public async Task Вік_прогону_вимкнений_пропуск_і_рік_без_періодів()
    {
        var arranged = await ArrangeAsync();
        var baseline = QuietSlot.AddDays(20);
        await BaselineAsync(arranged, baseline);

        var aged = await NightlyAsync(arranged, baseline + RecalculationJob.NightlyMaxBaselineAge + TimeSpan.FromHours(1));
        Assert.Single(aged.Children);
        Assert.Equal("baselineAged", Assert.Single(Reasons(aged.Details)).Key);

        var forced = await NightlyAsync(arranged, baseline.AddDays(1), skipUnchanged: false);
        Assert.Single(forced.Children);
        Assert.Equal("forced", Assert.Single(Reasons(forced.Details)).Key);
        Assert.False(forced.Details.GetProperty("skipUnchanged").GetBoolean());

        // Документ лише з минулорічним періодом: у 2026 рахувати нічого.
        var old = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: 202511);
        var none = await NightlyAsync(new Arranged(arranged.Builder, old), baseline.AddDays(1));
        Assert.Empty(none.Children);
        Assert.Equal(1, none.Details.GetProperty("noOpenPeriods").GetInt32());
    }

    /// <summary>
    /// Затверджені аркуші (D-493/D-510, D-557): нічна дитина ніколи не несе перекриття адміністратора, навіть якщо його
    /// несло завдання розкладу; період, де все затверджено, у скоуп дитини не входить і без прогону не будить документ;
    /// затвердження не будить, повернення в роботу — будить.
    /// </summary>
    [Fact]
    public async Task Затверджений_аркуш_нічний_не_переписує_а_повернення_в_роботу_будить()
    {
        var arranged = await ArrangeAsync();
        await using (var db = arranged.Builder.CreateContext())
        {
            var state = new ApprovalState(arranged.Document.DocumentId, arranged.Document.SheetDefId, January.Value);
            state.Submit(userId: 5, QuietSlot);
            state.Approve(userId: 6, QuietSlot);
            db.ApprovalStates.Add(state);
            await db.SaveChangesAsync();
        }

        var baseline = QuietSlot.AddDays(40);
        var first = await NightlyAsync(arranged, baseline, overrideApproved: true);
        var child = Assert.Single(first.Children);
        Assert.False(child.IncludeApproved);
        Assert.Null(child.OverrideUserId);
        Assert.Null(child.OverrideReason);

        // Затверджений січень дитина не рахує взагалі — лише лютий.
        var runner = await RunChildrenAsync(arranged, first.Children, baseline);
        Assert.Equal([February.Value], runner.Periods);

        // Затверджений січень без прогону — не «нема з чим порівняти»: друга ніч пропускає.
        var quiet = await NightlyAsync(arranged, baseline.AddDays(1));
        Assert.Empty(quiet.Children);

        // Повернення в роботу (подія з Approved) — будить.
        await using (var db = arranged.Builder.CreateContext())
        {
            db.ApprovalEvents.Add(new ApprovalEvent(
                arranged.Document.DocumentId, arranged.Document.SheetDefId, January.Value, DocumentStatus.Approved,
                DocumentStatus.Draft, ApprovalAction.Reopen, byUserId: null, baseline.AddHours(30), reason: "PSS-15 test"));
            await db.SaveChangesAsync();
        }

        var woken = await NightlyAsync(arranged, baseline.AddDays(2));
        Assert.Single(woken.Children);
        Assert.Equal("unapproved", Assert.Single(Reasons(woken.Details)).Key);
    }

    /// <summary>
    /// ✎ Огляд H2: виняток у рішенні про ОДИН документ не зриває розклад проєкту: той документ рахується (decisionError),
    /// решта — за своїми рішеннями, прогін журналу — Succeeded.
    /// </summary>
    /// <remarks>МУТАЦІЯ: прибрати try/catch навколо NightlyDecisionAsync — виняток летить із задачі, червоний.</remarks>
    [Fact]
    public async Task Збій_рішення_про_документ_рахує_його_і_не_зупиняє_решту()
    {
        var arranged = await ArrangeAsync();
        long other;
        await using (var db = arranged.Builder.CreateContext())
        {
            var extra = new Document(arranged.Document.ProjectId, $"N15E-{Guid.NewGuid():N}"[..20], 1, QuietSlot);
            db.Documents.Add(extra);
            await db.SaveChangesAsync();
            var loader = new BulkCellLoader(sql.ConnectionString, 1000);
            var id = await loader.ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None);
            db.TableInstances.Add(new TableInstance(January, id, extra.Id, arranged.Document.TableDefId, QuietSlot.AddYears(-40)));
            await db.SaveChangesAsync();
            other = extra.Id;
        }

        var failing = arranged.Document.DocumentId;
        var result = await NightlyAsync(
            arranged, QuietSlot.AddDays(60),
            probe: child => child.DocumentId == failing
                ? throw new InvalidOperationException("PSS-15 H2: змодельований збій рішення")
                : Task.CompletedTask);

        Assert.Equal([failing, other], result.Children.Select(c => c.DocumentId).Order());
        var reasons = Reasons(result.Details).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(1, reasons["decisionError"]);
        Assert.Equal(1, reasons["noBaseline"]);
    }

    private static IReadOnlyList<KeyValuePair<string, int>> Reasons(JsonElement details)
        => [.. details.GetProperty("reasons").EnumerateObject().Select(p => new KeyValuePair<string, int>(p.Name, p.Value.GetInt32()))];

    private async Task<Arranged> ArrangeAsync()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: January.Value);
        await builder.AddPeriodInstanceAsync(document, February);
        await builder.AddPeriodAsync(document, new PeriodKey(202512));
        return new Arranged(builder, document);
    }

    /// <summary>Документ-джерело іншого шаблону й зв'язка, де шаблон документа — споживач (B179).</summary>
    private async Task<TestDocument> BindSourceAsync(Arranged arranged)
    {
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(periodKey: January.Value);
        await using var db = arranged.Builder.CreateContext();
        var tag = Guid.NewGuid().ToString("N")[..10];
        var registry = new RegistryDef(EcrCode.Create($"N15S{tag}"), Name("source"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();

        var binding = new DocumentSourceBinding($"N15_{tag}", source.TemplateId, registry.Id);
        binding.AddConsumer(arranged.Document.TemplateId);
        db.DocumentSourceBindings.Add(binding);
        await db.SaveChangesAsync();
        return source;
    }

    private async Task<NightlyResult> BaselineAsync(Arranged arranged, DateTime at)
    {
        var first = await NightlyAsync(arranged, at);
        Assert.Single(first.Children);
        await RunChildrenAsync(arranged, first.Children, at);
        return first;
    }

    private static async Task<NightlyResult> NightlyAsync(
        Arranged arranged, DateTime at, int? year = Year, bool skipUnchanged = true, bool overrideApproved = false,
        Func<RecalculationRequest, Task>? probe = null)
    {
        var scheduler = new RecordingScheduler();
        var progress = new RecordingProgress($"IRecalculationJob-nightly-test-{Guid.NewGuid():N}");
        var request = new RecalculationRequest(
            arranged.Document.ProjectId, 0, PeriodKey: null, TriggeredByUserId: null, Year: year,
            Nightly: true, NightlySkipUnchanged: skipUnchanged);
        if (overrideApproved)
        {
            request = request with { IncludeApproved = true, OverrideUserId = 9, OverrideReason = "не має дійти до дитини" };
        }

        await using (var db = arranged.Builder.CreateContext())
        {
            var job = NightlyJobParts.Job(db, new RecordingRunner(), scheduler.Substitute, at, probe);
            await job.ExecuteAsync(request, progress, CancellationToken.None);
        }

        Assert.All(scheduler.Calls, c =>
        {
            Assert.Equal(RecalculateDocumentHandler.YearTargetOf(c.Request.DocumentId), c.Target);
            Assert.Null(c.CreatedBy);
            Assert.Null(c.Request.TriggeredByUserId);
            Assert.Equal(progress.JobId, c.Request.FanOutParentJobId);
        });

        await using var verify = arranged.Builder.CreateContext();
        var runs = await verify.MaintenanceRuns.AsNoTracking()
            .Where(r => r.JobCode == RecalculationJob.NightlyMaintenanceCode && r.StartedAt == at)
            .OrderByDescending(r => r.Id)
            .Select(r => new { r.Status, r.DetailsJson })
            .Take(50)
            .ToListAsync();
        JsonElement? details = null;
        foreach (var run in runs)
        {
            using var parsed = JsonDocument.Parse(run.DetailsJson!);
            if (parsed.RootElement.GetProperty("projectId").GetInt32() == arranged.Document.ProjectId)
            {
                Assert.Equal("Succeeded", run.Status);
                details = parsed.RootElement.Clone();
                break;
            }
        }

        Assert.NotNull(details);
        return new NightlyResult([.. scheduler.Calls.Select(c => c.Request)], details.Value, progress.Last);
    }

    private static async Task<RecordingRunner> RunChildrenAsync(
        Arranged arranged, IReadOnlyList<RecalculationRequest> children, DateTime at)
    {
        var runner = new RecordingRunner();
        foreach (var child in children)
        {
            await using var db = arranged.Builder.CreateContext();
            await Job(db, runner, Substitute.For<IBackgroundJobScheduler>(), at)
                .ExecuteAsync(child, new RecordingProgress("child"), CancellationToken.None);
        }

        return runner;
    }

    private static async Task<int> RunCountAsync(Arranged arranged)
    {
        await using var db = arranged.Builder.CreateContext();
        return await db.CalculationRuns.AsNoTracking().CountAsync(r => r.DocumentId == arranged.Document.DocumentId);
    }

    private static Task<int> CellChangeAsync(EcrDbContext db, TestDocument document, PeriodKey period, string origin, DateTime at)
        => db.Database.ExecuteSqlAsync($"""
            INSERT INTO aud.CellChange (ChangedAt, PeriodKey, DocumentId, TableRowId, RowKey, ColumnDefId, OldValue, NewValue,
                                        ChangedByUserId, Origin)
            VALUES ({at}, {period.Value}, {document.DocumentId}, {document.RowIds[0]}, N'R1', {document.ColumnDefIds[1]},
                    N'1', N'2', 1, {origin})
            """);

    private static Task<int> StructureChangeAsync(EcrDbContext db, int templateVersionId, string entityType, DateTime at)
        => db.Database.ExecuteSqlAsync($"""
            INSERT INTO aud.StructureChange (ChangedAt, TemplateVersionId, EntityType, EntityId, ChangeClass, Operation,
                                             ChangedByUserId)
            VALUES ({at}, {templateVersionId}, {entityType}, 1, 0, N'Update', 1)
            """);

    private static Task<int> PublicationAsync(EcrDbContext db, string entityType, DateTime at)
        => db.Database.ExecuteSqlAsync($"""
            INSERT INTO aud.PublicationEvent (ChangedAt, EntityType, EntityId, ChangeReason, ChangedByUserId)
            VALUES ({at}, {entityType}, 1, N'PSS-15 test', 1)
            """);

    private static Task<int> AnyUserAsync(EcrDbContext db)
        => db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).FirstAsync();

    private static LocalizedText Name(string value) => new(new Dictionary<string, string> { ["en"] = value });

    private static RecalculationJob Job(EcrDbContext db, ICalculationRunner runner, IBackgroundJobScheduler jobs, DateTime at)
        => NightlyJobParts.Job(db, runner, jobs, at);

    private sealed record Arranged(TestDocumentBuilder Builder, TestDocument Document);

    private sealed record NightlyResult(IReadOnlyList<RecalculationRequest> Children, JsonElement Details, string? LastMessage);

    /// <summary>Планувальник, що запам'ятовує документні постановки.</summary>
    private sealed class RecordingScheduler
    {
        public RecordingScheduler()
        {
            Substitute.EnqueueCoalescedAsync<IRecalculationJob>(
                    Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>(), Arg.Any<int?>())
                .Returns(ci =>
                {
                    lock (Calls)
                    {
                        Calls.Add(new EnqueueCall(
                            ci.ArgAt<string>(0), Assert.IsType<RecalculationRequest>(ci.ArgAt<object?>(1)), ci.ArgAt<int?>(3)));
                    }

                    return Task.FromResult($"IRecalculationJob~{ci.ArgAt<string>(0)}~x");
                });
        }

        public IBackgroundJobScheduler Substitute { get; } = NSubstitute.Substitute.For<IBackgroundJobScheduler>();

        public List<EnqueueCall> Calls { get; } = [];
    }

    private sealed record EnqueueCall(string Target, RecalculationRequest Request, int? CreatedBy);

    /// <summary>Фейк оркестратора: запам'ятовує періоди, нічого не пише.</summary>
    private sealed class RecordingRunner : ICalculationRunner
    {
        private readonly ConcurrentQueue<int> periods = new();

        public IReadOnlyList<int> Periods => [.. periods];

        public Task<ModuleProfile> RunAsync(
            long calculationRunId, long documentId, PeriodKey periodKey,
            IReadOnlyList<CalculationBindingRef> bindings, IJobProgress progress, CancellationToken ct)
        {
            periods.Enqueue(periodKey.Value);
            return Task.FromResult(new ModuleProfile());
        }
    }

    private sealed class RecordingProgress(string jobId) : IJobProgress, IJobIdentity
    {
        public string JobId => jobId;

        public string? Last { get; private set; }

        public Task ReportAsync(int percent, string? message, CancellationToken ct)
        {
            Last = message;
            return Task.CompletedTask;
        }
    }
}

/// <summary>Збірка <see cref="RecalculationJob"/> для тестів нічного розкладу: фаза формул нічого не пише.</summary>
internal static class NightlyJobParts
{
    public static RecalculationJob Job(
        EcrDbContext db, ICalculationRunner runner, IBackgroundJobScheduler jobs, DateTime at,
        Func<RecalculationRequest, Task>? probe = null)
    {
        var clock = new TestClock(at);
        return new RecalculationJob(db, runner, RunHandler(db, clock), FormulaService(), clock, jobs)
        {
            NightlyDecisionProbe = probe,
        };
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

    /// <summary>Фаза формул шаблону нічого не пише — тести про розклад і прогони.</summary>
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
}
