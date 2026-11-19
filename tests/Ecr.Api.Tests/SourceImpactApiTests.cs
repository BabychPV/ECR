// tests/Ecr.Api.Tests/SourceImpactApiTests.cs
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Security;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.Infrastructure.Security;
using Ecr.Domain.Entities.Documents;
using Ecr.Domain.Entities.Workflow;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Api.Tests;

/// <summary>
/// B80 N5 (D-554): вплив затвердження й повернення із затвердженого аркуша документа-джерела на залежні документи —
/// справжній HTTP на справжній базі, з ТОЧНИМИ координатами: джерело (документ, аркуш, березень 2099) → Land (документ,
/// березень 2099, затверджений). Сервер сам вимагає <c>confirmedImpact</c> за політикою зв'язки.
/// </summary>
/// <remarks>
/// Мутації (локально, не в коміті): у <c>SourceImpactCheck</c> прибрати звірку <c>confirmation.Hash == impact.Hash</c> →
/// червоніє «застарілий відбиток»; прибрати гілку <c>Block</c> → червоніє «Block»; прибрати вимогу причини →
/// червоніє «ConfirmWithReason без причини»; у <c>SourceImpactAssessor</c> прибрати <c>Approved</c>-фільтр залежних
/// (<c>group.All(...)</c> → <c>true</c>) → червоніє «незатверджений Land не вимагає підтвердження».
/// </remarks>
[Collection("SqlServer")]
public sealed class SourceImpactApiTests(SqlServerFixture sql)
{
    private const string Password = "Api-Source-Impact-2026!";
    private const int March = ImpactWorld.March;
    private static readonly DateTime T0 = new(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = new(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-554")]
    public async Task Reopen_джерела_із_затвердженим_Land_ConfirmWithReason_вимагає_відбиток_і_причину_і_пише_аудит()
    {
        var s = await ArrangeAsync(DocumentSourcePolicy.ConfirmWithReason, sourceApproved: true, landApproved: true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.User).ConfigureAwait(true);

        // Точні координати впливу: Land (документ, березень 2099) затверджений.
        var impact = await ImpactAsync(app, client, s, "reopen").ConfigureAwait(true);
        Assert.True(impact.GetProperty("required").GetBoolean());
        Assert.Equal("ConfirmWithReason", impact.GetProperty("policy").GetString());
        Assert.True(impact.GetProperty("reasonRequired").GetBoolean());
        Assert.Equal(1, impact.GetProperty("approvedCount").GetInt32());
        Assert.Equal(1, impact.GetProperty("dependentCount").GetInt32());
        Assert.Equal([March], impact.GetProperty("sourceMonths").EnumerateArray().Select(m => m.GetInt32()).ToArray());
        var consumer = Assert.Single(impact.GetProperty("consumers").EnumerateArray());
        Assert.Equal(s.World.Land.DocumentId, consumer.GetProperty("documentId").GetInt64());
        Assert.Equal(March, consumer.GetProperty("periodKey").GetInt32());
        Assert.True(consumer.GetProperty("approved").GetBoolean());
        var hash = impact.GetProperty("impactHash").GetString()!;
        Assert.NotEmpty(hash);

        // Без підтвердження — 409, аркуш НЕ змінено.
        var none = await PostAsync(client, s, "reopen", new { sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка" }).ConfigureAwait(true);
        await AssertRefusedAsync(none, HttpStatusCode.Conflict, "ECR-SRC-0409", "err.ECR-SRC-0409.impactConfirmationRequired").ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await SourceStatusAsync(s).ConfigureAwait(true));

        // Застарілий (чужий) відбиток — 409 impactChanged, у тілі актуальний відбиток.
        var stale = await PostAsync(client, s, "reopen", new
        {
            sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка",
            confirmedImpact = new { hash = "deadbeef", reason = "x" },
        }).ConfigureAwait(true);
        var staleBody = await AssertRefusedAsync(stale, HttpStatusCode.Conflict, "ECR-SRC-0409", "err.ECR-SRC-0409.impactChanged").ConfigureAwait(true);
        Assert.Equal(hash, staleBody.GetProperty("impactHash").GetString());
        Assert.Equal(DocumentStatus.Approved, await SourceStatusAsync(s).ConfigureAwait(true));

        // Правильний відбиток без причини — 422.
        var noReason = await PostAsync(client, s, "reopen", new
        {
            sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка",
            confirmedImpact = new { hash, reason = "  " },
        }).ConfigureAwait(true);
        await AssertRefusedAsync(noReason, HttpStatusCode.UnprocessableEntity, "ECR-SRC-0422", "err.ECR-SRC-0422.impactReasonRequired").ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await SourceStatusAsync(s).ConfigureAwait(true));

        // Відбиток + причина — 204, аркуш у Draft, підтвердження в журналі безпеки.
        var ok = await PostAsync(client, s, "reopen", new
        {
            sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка",
            confirmedImpact = new { hash, reason = "Виправлення складу газу за актом 12" },
        }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(DocumentStatus.Draft, await SourceStatusAsync(s).ConfigureAwait(true));
        var details = await ConfirmedEventAsync(s).ConfigureAwait(true);
        var audit = JsonDocument.Parse(details).RootElement;
        Assert.Equal("Виправлення складу газу за актом 12", audit.GetProperty("reason").GetString());
        Assert.Equal(hash, audit.GetProperty("impactHash").GetString());
        Assert.Equal("reopen", audit.GetProperty("action").GetString());
        Assert.Equal(1, audit.GetProperty("approvedDependents").GetInt32());
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-554")]
    public async Task Block_відмовляє_доки_Land_затверджений_і_пускає_після_його_повернення()
    {
        var s = await ArrangeAsync(DocumentSourcePolicy.Block, sourceApproved: true, landApproved: true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.User).ConfigureAwait(true);

        var impact = await ImpactAsync(app, client, s, "reopen").ConfigureAwait(true);
        Assert.True(impact.GetProperty("blocked").GetBoolean());

        // Навіть з відбитком — 422: Block не підтверджується.
        var blocked = await PostAsync(client, s, "reopen", new
        {
            sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка",
            confirmedImpact = new { hash = impact.GetProperty("impactHash").GetString(), reason = "дуже треба" },
        }).ConfigureAwait(true);
        await AssertRefusedAsync(blocked, HttpStatusCode.UnprocessableEntity, "ECR-SRC-0422", "err.ECR-SRC-0422.sourceImpactBlocked").ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await SourceStatusAsync(s).ConfigureAwait(true));

        // Land повернули в роботу — затвердженого залежного немає, Block не діє.
        await SetStatusAsync(s.World.Land.DocumentId, s.World.Land.SheetDefId, DocumentStatus.Draft).ConfigureAwait(true);
        var open = await PostAsync(client, s, "reopen", new { sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, open.StatusCode);
        Assert.Equal(DocumentStatus.Draft, await SourceStatusAsync(s).ConfigureAwait(true));
    }

    [Theory]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-554")]
    [InlineData(DocumentSourcePolicy.None, false)]
    [InlineData(DocumentSourcePolicy.Warn, true)]
    public async Task None_без_запиту_Warn_лише_відбиток_без_причини(DocumentSourcePolicy policy, bool needsConfirmation)
    {
        var s = await ArrangeAsync(policy, sourceApproved: true, landApproved: true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.User).ConfigureAwait(true);
        var impact = await ImpactAsync(app, client, s, "reopen").ConfigureAwait(true);
        Assert.Equal(needsConfirmation, impact.GetProperty("required").GetBoolean());
        Assert.False(impact.GetProperty("reasonRequired").GetBoolean());

        var request = needsConfirmation
            ? (object)new
            {
                sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка",
                confirmedImpact = new { hash = impact.GetProperty("impactHash").GetString() },
            }
            : new { sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка" };
        if (needsConfirmation)
        {
            var without = await PostAsync(client, s, "reopen", new { sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка" }).ConfigureAwait(true);
            await AssertRefusedAsync(without, HttpStatusCode.Conflict, "ECR-SRC-0409", "err.ECR-SRC-0409.impactConfirmationRequired").ConfigureAwait(true);
        }

        var response = await PostAsync(client, s, "reopen", request).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(DocumentStatus.Draft, await SourceStatusAsync(s).ConfigureAwait(true));
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-554")]
    public async Task Незатверджений_Land_підтвердження_не_вимагає_а_Approve_джерела_із_затвердженим_Land_вимагає()
    {
        // (а) Land не затверджено — перерахується сам; Reopen і підтвердження не потрібні.
        var free = await ArrangeAsync(DocumentSourcePolicy.ConfirmWithReason, sourceApproved: true, landApproved: false);
        using var app = new EcrApiFactory(sql);
        using var freeClient = await SignedInAsync(app, free.User).ConfigureAwait(true);
        var freeImpact = await ImpactAsync(app, freeClient, free, "reopen").ConfigureAwait(true);
        Assert.False(freeImpact.GetProperty("required").GetBoolean());
        Assert.Equal(1, freeImpact.GetProperty("dependentCount").GetInt32());
        Assert.Equal(0, freeImpact.GetProperty("approvedCount").GetInt32());
        var freeReopen = await PostAsync(freeClient, free, "reopen", new { sheetDefId = free.World.Source.SheetDefId, periodKey = March, reason = "правка" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, freeReopen.StatusCode);

        // (б) Approve джерела, чий Land уже затверджений, — повторне затвердження зачіпає його (reapproved).
        var s = await ArrangeAsync(DocumentSourcePolicy.Warn, sourceApproved: false, landApproved: true);
        using var app2 = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app2, s.User).ConfigureAwait(true);
        var impact = await ImpactAsync(app2, client, s, "approve").ConfigureAwait(true);
        Assert.True(impact.GetProperty("required").GetBoolean());
        Assert.Equal(s.World.Land.DocumentId, Assert.Single(impact.GetProperty("consumers").EnumerateArray()).GetProperty("documentId").GetInt64());

        var without = await PostAsync(client, s, "approve", new { sheetDefId = s.World.Source.SheetDefId, periodKey = March, approved = true }).ConfigureAwait(true);
        await AssertRefusedAsync(without, HttpStatusCode.Conflict, "ECR-SRC-0409", "err.ECR-SRC-0409.impactConfirmationRequired").ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Submitted, await SourceStatusAsync(s).ConfigureAwait(true));

        var ok = await PostAsync(client, s, "approve", new
        {
            sheetDefId = s.World.Source.SheetDefId, periodKey = March, approved = true,
            confirmedImpact = new { hash = impact.GetProperty("impactHash").GetString() },
        }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(DocumentStatus.Approved, await SourceStatusAsync(s).ConfigureAwait(true));

        // Відхилення (approved = false) джерела впливу не має: підтвердження не потрібне.
        var rejectWorld = await ArrangeAsync(DocumentSourcePolicy.Block, sourceApproved: false, landApproved: true);
        using var app3 = new EcrApiFactory(sql);
        using var rejectClient = await SignedInAsync(app3, rejectWorld.User).ConfigureAwait(true);
        var reject = await PostAsync(rejectClient, rejectWorld, "approve", new
        {
            sheetDefId = rejectWorld.World.Source.SheetDefId, periodKey = March, approved = false, reason = "Є помилки",
        }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, reject.StatusCode);
    }

    /// <summary>
    /// Споживачі читають лише документ, який обрав би завантажувач за рік (найбільший id): повернення ІНШОГО (старшого)
    /// документа того ж шаблону-джерела залежних не зачіпає — підтвердження не потрібне.
    /// </summary>
    /// <remarks>Мутація: прибрати в <c>SourceImpactAssessor</c> перевірку <c>chosen?.DocumentId != documentId</c> — червоніє.</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-554")]
    public async Task Не_обраний_документ_джерело_залежних_не_зачіпає_а_обраний_зачіпає()
    {
        var s = await ArrangeAsync(DocumentSourcePolicy.Block, sourceApproved: true, landApproved: true).ConfigureAwait(true);

        // Новіший документ-джерело (більший id) із таблицею потоку за березень 2099 перебирає вибір завантажувача.
        long newerId;
        await using (var db = sql.CreateContext())
        {
            var newer = new Ecr.Domain.Entities.Documents.Document(
                s.World.Source.ProjectId, $"NEWER{Guid.NewGuid():N}"[..20], 1, T0);
            db.Documents.Add(newer);
            await db.SaveChangesAsync().ConfigureAwait(true);
            newerId = newer.Id;
            var streamId = s.World.StreamTableId;
            var instanceId = await new BulkCellLoader(sql.ConnectionString, 1000)
                .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(true);
            db.TableInstances.Add(new Ecr.Domain.Entities.Documents.TableInstance(new PeriodKey(March), instanceId, newerId, streamId, T0));
            await db.SaveChangesAsync().ConfigureAwait(true);
        }

        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.User).ConfigureAwait(true);

        // Старший (початковий) документ вже не обраний — Block на нього не діє.
        var older = await ImpactAsync(app, client, s, "reopen").ConfigureAwait(true);
        Assert.False(older.GetProperty("required").GetBoolean());
        Assert.Equal(0, older.GetProperty("dependentCount").GetInt32());

        // Обраний (новіший) — вплив є: його аркуш затверджено, Land березня 2099 затверджений, політика Block.
        await ImpactWorld.AddStateAsync(sql, newerId, s.World.Source.SheetDefId, March, T0, T0).ConfigureAwait(true);
        using var response = await client.GetAsync(new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"/api/v1/documents/{newerId}/source-impact?sheetDefId={s.World.Source.SheetDefId}&periodKey={March}&action=reopen"),
            UriKind.Relative)).ConfigureAwait(true);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}");
        var chosen = JsonDocument.Parse(body).RootElement;
        Assert.True(chosen.GetProperty("blocked").GetBoolean());
        Assert.Equal(
            s.World.Land.DocumentId,
            Assert.Single(chosen.GetProperty("consumers").EnumerateArray()).GetProperty("documentId").GetInt64());
    }

    /// <summary>Джерело, чиї залежні — інший документ того ж шаблону-споживача, не зачіпає чужий Land (інший документ-джерело).</summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-554")]
    public async Task Стан_Any_зв_язки_впливу_не_має()
    {
        var s = await ArrangeAsync(DocumentSourcePolicy.Block, sourceApproved: true, landApproved: true, DocumentSourceState.Any);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.User).ConfigureAwait(true);
        var impact = await ImpactAsync(app, client, s, "reopen").ConfigureAwait(true);
        Assert.False(impact.GetProperty("required").GetBoolean());
        Assert.Equal(0, impact.GetProperty("dependentCount").GetInt32());
        var response = await PostAsync(client, s, "reopen", new { sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка" }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }


    /// <summary>Залежний документ, до якого читач не має доступу, показується метаданими (рішення 19.11): документ, період, статус; без даних.</summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-554")]
    public async Task Невидимий_читачу_Land_показується_метаданими_і_вимога_діє()
    {
        var s = await ArrangeAsync(DocumentSourcePolicy.Warn, sourceApproved: true, landApproved: true, landVisible: false);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.User).ConfigureAwait(true);
        var impact = await ImpactAsync(app, client, s, "reopen").ConfigureAwait(true);
        Assert.True(impact.GetProperty("required").GetBoolean());
        var seen = Assert.Single(impact.GetProperty("consumers").EnumerateArray());
        Assert.Equal(s.World.Land.DocumentId, seen.GetProperty("documentId").GetInt64());
        Assert.False(seen.GetProperty("willRecalculate").GetBoolean());
        Assert.Equal("approved", seen.GetProperty("skipReason").GetString());
        Assert.Equal(1, impact.GetProperty("approvedCount").GetInt32());
    }
    // ===== Рішення людини 19.11: «перерахувати також затверджені» (право Calculation.RecalculateApprovedDependents) =====

    /// <summary>
    /// Reopen із <c>recalculateApproved</c>: без права — 403 (аркуш не змінено); з правом без причини — 422; з правом,
    /// причиною й відбитком — 204, а в журналі безпеки підтвердження з <c>recalculateApproved = true</c> і причиною.
    /// </summary>
    /// <remarks>Мутації: прибрати перевірку права в <c>SourceImpactCheck</c> — червоніє 403; прибрати вимогу причини — червоніє 422.</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-554")]
    public async Task Reopen_з_перекриттям_затверджених_вимагає_право_і_причину_і_пише_аудит()
    {
        var denied = await ArrangeAsync(DocumentSourcePolicy.Warn, sourceApproved: true, landApproved: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var deniedClient = await SignedInAsync(app, denied.User).ConfigureAwait(true);
        var deniedImpact = await ImpactAsync(app, deniedClient, denied, "reopen").ConfigureAwait(true);
        Assert.False(deniedImpact.GetProperty("canRecalculateApproved").GetBoolean());

        var forbidden = await PostAsync(deniedClient, denied, "reopen", new
        {
            sheetDefId = denied.World.Source.SheetDefId, periodKey = March, reason = "правка",
            confirmedImpact = new { hash = deniedImpact.GetProperty("impactHash").GetString(), reason = "Акт", recalculateApproved = true },
        }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(DocumentStatus.Approved, await SourceStatusAsync(denied).ConfigureAwait(true));

        var s = await ArrangeAsync(DocumentSourcePolicy.Warn, sourceApproved: true, landApproved: true, recalculateApprovedPermission: true)
            .ConfigureAwait(true);
        using var app2 = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app2, s.User).ConfigureAwait(true);
        var impact = await ImpactAsync(app2, client, s, "reopen").ConfigureAwait(true);
        Assert.True(impact.GetProperty("canRecalculateApproved").GetBoolean());
        var hash = impact.GetProperty("impactHash").GetString();

        var noReason = await PostAsync(client, s, "reopen", new
        {
            sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка",
            confirmedImpact = new { hash, reason = " ", recalculateApproved = true },
        }).ConfigureAwait(true);
        await AssertRefusedAsync(noReason, HttpStatusCode.UnprocessableEntity, "ECR-SRC-0422", "err.ECR-SRC-0422.recalculateApprovedReasonRequired")
            .ConfigureAwait(true);
        Assert.Equal(DocumentStatus.Approved, await SourceStatusAsync(s).ConfigureAwait(true));

        var ok = await PostAsync(client, s, "reopen", new
        {
            sheetDefId = s.World.Source.SheetDefId, periodKey = March, reason = "правка",
            confirmedImpact = new { hash, reason = "Акт 12: виправлення складу", recalculateApproved = true },
        }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        Assert.Equal(DocumentStatus.Draft, await SourceStatusAsync(s).ConfigureAwait(true));
        var audit = JsonDocument.Parse(await ConfirmedEventAsync(s).ConfigureAwait(true)).RootElement;
        Assert.True(audit.GetProperty("recalculateApproved").GetBoolean());
        Assert.Equal("Акт 12: виправлення складу", audit.GetProperty("reason").GetString());
    }

    /// <summary>
    /// <c>action=edit</c> (правка комірки джерела) за зв'язки <c>any</c>: лише відомості — затверджений Land позначено
    /// «не буде перераховано» (<c>skipReason = approved</c>), підтвердження сервер не вимагає, <c>canRecalculateApproved</c> — за правом.
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-554")]
    public async Task Правка_комірки_джерела_any_показує_хто_буде_перераховано_а_хто_ні_без_вимоги_підтвердження()
    {
        var s = await ArrangeAsync(DocumentSourcePolicy.Block, sourceApproved: false, landApproved: true, DocumentSourceState.Any,
            recalculateApprovedPermission: true).ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app, s.User).ConfigureAwait(true);

        var impact = await ImpactAsync(app, client, s, "edit").ConfigureAwait(true);

        Assert.Equal("edit", impact.GetProperty("action").GetString());
        Assert.False(impact.GetProperty("required").GetBoolean());
        Assert.False(impact.GetProperty("blocked").GetBoolean());
        Assert.True(impact.GetProperty("canRecalculateApproved").GetBoolean());
        var consumer = Assert.Single(impact.GetProperty("consumers").EnumerateArray());
        Assert.Equal(s.World.Land.DocumentId, consumer.GetProperty("documentId").GetInt64());
        Assert.Equal(March, consumer.GetProperty("periodKey").GetInt32());
        Assert.False(consumer.GetProperty("willRecalculate").GetBoolean());
        Assert.Equal("approved", consumer.GetProperty("skipReason").GetString());
    }

    /// <summary>
    /// <c>POST …/source-impact/recalculate-approved</c>: без права 403; без причини 422; застарілий відбиток 409; з усім — 200
    /// із числом затверджених і поставлених задач, у журналі безпеки подія з причиною й затвердженими залежними.
    /// </summary>
    /// <remarks>Мутації: прибрати перевірку права в обробнику — червоніє 403; прибрати звірку відбитка — червоніє 409.</remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Decision", "D-554")]
    public async Task Явний_перерахунок_затверджених_залежних_право_причина_відбиток_і_аудит()
    {
        var plain = await ArrangeAsync(DocumentSourcePolicy.Warn, sourceApproved: false, landApproved: true, DocumentSourceState.Any)
            .ConfigureAwait(true);
        using var app = new EcrApiFactory(sql);
        using var plainClient = await SignedInAsync(app, plain.User).ConfigureAwait(true);
        var plainImpact = await ImpactAsync(app, plainClient, plain, "edit").ConfigureAwait(true);
        var denied = await PostAsync(plainClient, plain, "source-impact/recalculate-approved", new
        {
            sheetDefId = plain.World.Source.SheetDefId, periodKey = March, hash = plainImpact.GetProperty("impactHash").GetString(), reason = "Акт",
        }).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var s = await ArrangeAsync(DocumentSourcePolicy.Warn, sourceApproved: false, landApproved: true, DocumentSourceState.Any,
            recalculateApprovedPermission: true).ConfigureAwait(true);
        using var app2 = new EcrApiFactory(sql);
        using var client = await SignedInAsync(app2, s.User).ConfigureAwait(true);
        var hash = (await ImpactAsync(app2, client, s, "edit").ConfigureAwait(true)).GetProperty("impactHash").GetString();

        var noReason = await PostAsync(client, s, "source-impact/recalculate-approved", new
        {
            sheetDefId = s.World.Source.SheetDefId, periodKey = March, hash, reason = "  ",
        }).ConfigureAwait(true);
        await AssertRefusedAsync(noReason, HttpStatusCode.UnprocessableEntity, "ECR-SRC-0422", "err.ECR-SRC-0422.recalculateApprovedReasonRequired")
            .ConfigureAwait(true);

        var stale = await PostAsync(client, s, "source-impact/recalculate-approved", new
        {
            sheetDefId = s.World.Source.SheetDefId, periodKey = March, hash = "deadbeef", reason = "Акт 12",
        }).ConfigureAwait(true);
        await AssertRefusedAsync(stale, HttpStatusCode.Conflict, "ECR-SRC-0409", "err.ECR-SRC-0409.impactChanged").ConfigureAwait(true);

        var ok = await PostAsync(client, s, "source-impact/recalculate-approved", new
        {
            sheetDefId = s.World.Source.SheetDefId, periodKey = March, hash, reason = "Акт 12",
        }).ConfigureAwait(true);
        var text = await ok.Content.ReadAsStringAsync().ConfigureAwait(true);
        Assert.True(ok.StatusCode == HttpStatusCode.OK, $"{ok.StatusCode}: {text}");
        var result = JsonDocument.Parse(text).RootElement;
        Assert.Equal(1, result.GetProperty("approvedDependents").GetInt32());
        Assert.Equal(1, result.GetProperty("queued").GetInt32());

        await using var db = sql.CreateContext();
        var needle = string.Create(CultureInfo.InvariantCulture, $"\"sourceDocumentId\":{s.World.Source.DocumentId},");
        var rows = await db.Database.SqlQuery<string>(
            $"SELECT DetailsJson AS Value FROM aud.SecurityEvent WHERE EventType = 'ApprovedDependentsRecalculationRequested'")
            .ToListAsync().ConfigureAwait(true);
        var audit = JsonDocument.Parse(Assert.Single(rows, r => r.Contains(needle, StringComparison.Ordinal))).RootElement;
        Assert.Equal("Акт 12", audit.GetProperty("reason").GetString());
        var dependent = Assert.Single(audit.GetProperty("approvedDependents").EnumerateArray());
        Assert.Equal(s.World.Land.DocumentId, dependent.GetProperty("DocumentId").GetInt64());
    }

    private sealed record Scenario(ImpactWorld.World World, string User);

    private async Task<Scenario> ArrangeAsync(
        DocumentSourcePolicy policy, bool sourceApproved, bool landApproved, DocumentSourceState state = DocumentSourceState.ApprovedOnly,
        bool landVisible = true, bool recalculateApprovedPermission = false)
    {
        var world = await ImpactWorld.ArrangeAsync(sql, state).ConfigureAwait(false);
        await using (var db = sql.CreateContext())
        {
            await db.DocumentSourceBindings.Where(b => b.Id == world.BindingId)
                .ExecuteUpdateAsync(b => b.SetProperty(x => x.Policy, policy)).ConfigureAwait(false);
        }

        var builder = new TestDocumentBuilder(sql.ConnectionString);
        await builder.OpenPeriodAsync(world.Source).ConfigureAwait(false);
        await builder.OpenPeriodAsync(world.Land).ConfigureAwait(false);
        await ImpactWorld.AddStateAsync(
            sql, world.Source.DocumentId, world.Source.SheetDefId, March, T0, sourceApproved ? T0 : null).ConfigureAwait(false);
        await ImpactWorld.AddStateAsync(
            sql, world.Land.DocumentId, world.Land.SheetDefId, March, T1, landApproved ? T1 : null).ConfigureAwait(false);

        // Користувач: Manage на проєкт джерела + право повернення (Document.Reopen) і перегляду.
        await using var ctx = builder.CreateContext();
        var userName = $"si_{Guid.NewGuid():N}"[..20];
        var user = new User(userName, userName, AuthProvider.Local);
        user.SetPassword(new PasswordHasher().Hash(Password));
        ctx.Users.Add(user);
        var role = new Role(
            EcrCode.Create($"SI_{Guid.NewGuid():N}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Source impact test" }));
        ctx.Roles.Add(role);
        await ctx.SaveChangesAsync().ConfigureAwait(false);
        ctx.RolePermissions.Add(new RolePermission(role.Id, "Document.View"));
        ctx.RolePermissions.Add(new RolePermission(role.Id, "Document.Reopen"));
        if (recalculateApprovedPermission)
        {
            ctx.RolePermissions.Add(new RolePermission(role.Id, "Calculation.RecalculateApprovedDependents"));
        }

        ctx.RoleAssignments.Add(new RoleAssignment(role.Id, user.Id, null));
        ctx.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, world.Source.ProjectId, GrantLevel.Manage));
        if (landVisible)
        {
            ctx.ResourceGrants.Add(new ResourceGrant(role.Id, ResourceKind.Project, world.Land.ProjectId, GrantLevel.Read));
        }

        await ctx.SaveChangesAsync().ConfigureAwait(false);
        return new Scenario(world, userName);
    }

    private static async Task<HttpClient> SignedInAsync(EcrApiFactory app, string userName)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(
            new Uri("/api/v1/login/local", UriKind.Relative), new { userName, password = Password }).ConfigureAwait(false);
        Assert.True(login.IsSuccessStatusCode, $"Вхід {userName}: {login.StatusCode}: {app.ErrorsText}");
        return client;
    }

    private static async Task<JsonElement> ImpactAsync(EcrApiFactory app, HttpClient client, Scenario s, string action)
    {
        using var response = await client.GetAsync(new Uri(
            string.Create(
                CultureInfo.InvariantCulture,
                $"/api/v1/documents/{s.World.Source.DocumentId}/source-impact?sheetDefId={s.World.Source.SheetDefId}&periodKey={March}&action={action}"),
            UriKind.Relative)).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {body}\n{app.ErrorsText}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Scenario s, string route, object body)
        => client.PostAsJsonAsync(
            new Uri(string.Create(CultureInfo.InvariantCulture, $"/api/v1/documents/{s.World.Source.DocumentId}/{route}"), UriKind.Relative),
            body);

    private static async Task<JsonElement> AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string code, string messageKey)
    {
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        Assert.True(response.StatusCode == status, $"{response.StatusCode}: {text}");
        var json = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal(code, json.GetProperty("errorCode").GetString());
        Assert.Equal(messageKey, json.GetProperty("messageKey").GetString());
        return json;
    }

    private async Task<DocumentStatus> SourceStatusAsync(Scenario s)
    {
        await using var db = sql.CreateContext();
        return await db.ApprovalStates.AsNoTracking()
            .Where(x => x.DocumentId == s.World.Source.DocumentId && x.SheetDefId == s.World.Source.SheetDefId
                        && x.PeriodKey == March && x.SheetCopyId == null)
            .Select(x => x.Status).SingleAsync().ConfigureAwait(false);
    }

    private async Task SetStatusAsync(long documentId, int sheetDefId, DocumentStatus status)
    {
        await using var db = sql.CreateContext();
        var state = await db.ApprovalStates.SingleAsync(x => x.DocumentId == documentId && x.SheetDefId == sheetDefId && x.PeriodKey == March)
            .ConfigureAwait(false);
        Assert.Equal(DocumentStatus.Draft, status);
        state.Reopen(userId: 7, "B80 N5: тест", DateTime.UtcNow);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task<string> ConfirmedEventAsync(Scenario s)
    {
        await using var db = sql.CreateContext();
        var needle = string.Create(CultureInfo.InvariantCulture, $"\"sourceDocumentId\":{s.World.Source.DocumentId},");
        var rows = await db.Database.SqlQuery<string>(
            $"SELECT DetailsJson AS Value FROM aud.SecurityEvent WHERE EventType = 'SourceImpactConfirmed'")
            .ToListAsync().ConfigureAwait(false);
        return Assert.Single(rows, r => r.Contains(needle, StringComparison.Ordinal));
    }
}

/// <summary>Світ тестів B80 N5: документ-джерело (таблиця <c>FG_Internal</c>, екземпляр за березень 2099), зв'язка, документ Land (власне все).</summary>
internal static class ImpactWorld
{
    /// <summary>Березень 2099.</summary>
    public const int March = 209903;

    private static readonly DateTime Stamp = new(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc);

    /// <summary>Стан оригіналу аркуша: подано, за потреби затверджено.</summary>
    public static async Task<long> AddStateAsync(
        SqlServerFixture fixture, long documentId, int sheetDefId, int periodKey, DateTime submittedAt, DateTime? approvedAt = null)
    {
        await using var db = fixture.CreateContext();
        var state = new ApprovalState(documentId, sheetDefId, periodKey);
        state.Submit(userId: 5, submittedAt);
        if (approvedAt is { } at)
        {
            state.Approve(userId: 6, at);
        }

        db.ApprovalStates.Add(state);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return state.Id;
    }

    /// <summary>Джерело, довідник, зв'язка в заданому стані і Land-споживач.</summary>
    public static async Task<World> ArrangeAsync(SqlServerFixture sql, DocumentSourceState state)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var source = await builder.BuildAsync(periodKey: March).ConfigureAwait(false);
        var land = await builder.BuildAsync(periodKey: March).ConfigureAwait(false);

        await using var db = sql.CreateContext();
        var stream = new TableDef(
            source.SheetDefId, EcrCode.Create("FG_Internal"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "FG_Internal" }), 50,
            TableLayoutKind.MonthsInColumns, TableRowMode.Fixed);
        db.TableDefs.Add(stream);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var instanceId = await new BulkCellLoader(sql.ConnectionString, 1000)
            .ReserveIdsAsync("doc.TableInstanceSeq", 1, CancellationToken.None).ConfigureAwait(false);
        db.TableInstances.Add(new TableInstance(new PeriodKey(March), instanceId, source.DocumentId, stream.Id, Stamp));

        var registry = new RegistryDef(
            EcrCode.Create($"HN{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}"),
            new LocalizedText(new Dictionary<string, string> { ["en"] = "Composition" }),
            isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var binding = new DocumentSourceBinding($"B80N5_{registry.Code}", source.TemplateId, registry.Id);
        db.DocumentSourceBindings.Add(binding);
        await db.SaveChangesAsync().ConfigureAwait(false);
        await DependencyGraphTestKit.MakeConsumerAsync(db, land, registry.Code).ConfigureAwait(false);
        await db.DocumentSourceBindings.Where(b => b.Id == binding.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.SourceState, state)).ConfigureAwait(false);
        return new World(source, land, binding.Id, stream.Id);
    }

    /// <summary>Світ тесту.</summary>
    /// <param name="Source">Документ-джерело.</param>
    /// <param name="Land">Документ-споживач.</param>
    /// <param name="BindingId">Зв'язка.</param>
    /// <param name="StreamTableId">Таблиця потоку джерела.</param>
    public sealed record World(TestDocument Source, TestDocument Land, int BindingId, int StreamTableId);
}
