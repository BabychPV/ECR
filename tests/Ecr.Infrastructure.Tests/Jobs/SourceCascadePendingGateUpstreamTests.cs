// tests/Ecr.Infrastructure.Tests/Jobs/SourceCascadePendingGateUpstreamTests.cs
using System.Globalization;
using Ecr.Application.DocumentDependencies;
using Ecr.Application.Ports;
using Ecr.Domain.Enums;
using Ecr.Expressions.Binding;
using Ecr.Infrastructure.Jobs;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// F5 (огляд координатора, п. 1): місяць споживача чекає й ДО постановки каскаду — поки жива ланка ланцюжка документа-
/// джерела зв'язки, споживачем якої граф називає шаблон документа: задача впливу <c>source-change-b{з}-y{р}[-m{м}]</c>,
/// власний перерахунок джерела <c>doc{id}-p{період}-auto</c>, задача формул джерела <c>doc{id}-formula</c>. Парно —
/// шаблон-не-споживач (той самий рядок черги) не зачеплений.
/// </summary>
/// <remarks>
/// Мутації (локально, не комітяться): <c>UpstreamPends</c> → <c>false</c> (ланки вимкнено) — червоні «чекає»;
/// <c>UpstreamPends</c> без перевірки шаблону-споживача (усі ребра графа) — червоний парний «не-споживач».
/// </remarks>
[Collection("SqlServer")]
public sealed class SourceCascadePendingGateUpstreamTests(SqlServerFixture sql)
{
    private static int _binding = 1_900_000_000 - (Environment.ProcessId % 100_000 * 100);

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Задача_впливу_зв_язки_тримає_місяць_споживача_а_не_споживача_ні()
    {
        var w = await ArrangeAsync();

        // Без ланок — нічого не чекає.
        Assert.False(await PendingAsync(w, w.Consumer));

        var target = Impact(RegistryImpactRecalculationTarget.ForSourceChange(w.BindingId, w.Year, 3));
        await InsertAsync(target, "Queued", documentId: null, payload: "{}");
        try
        {
            Assert.True(await PendingAsync(w, w.Consumer));
            Assert.Equal(CascadePendingReason.SourceCascade, await ReasonAsync(w, w.Consumer));
            Assert.False(await PendingAsync(w, w.Other)); // парно: шаблон-не-споживач

            // Виконується — так само.
            await SetStateAsync(target, "Running");
            Assert.True(await PendingAsync(w, w.Consumer));

            // Закінчилась — уже ні.
            await SetStateAsync(target, "Succeeded");
            Assert.False(await PendingAsync(w, w.Consumer));
        }
        finally
        {
            await SetStateAsync(target, "Succeeded");
        }

        // «З затвердженими» переписує й затверджені — не тримає; інший рік — не тримає.
        var approved = Impact(RegistryImpactRecalculationTarget.ForSourceChange(w.BindingId, w.Year, null, includeApproved: true));
        var otherYear = Impact(RegistryImpactRecalculationTarget.ForSourceChange(w.BindingId, w.Year + 1, null));
        await InsertAsync(approved, "Queued", documentId: null, payload: "{}");
        await InsertAsync(otherYear, "Queued", documentId: null, payload: "{}");
        try
        {
            Assert.False(await PendingAsync(w, w.Consumer));
        }
        finally
        {
            await SetStateAsync(approved, "Succeeded");
            await SetStateAsync(otherYear, "Succeeded");
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Власний_перерахунок_і_задача_формул_джерела_тримають_споживача_лише_за_зв_язки_що_ставить_каскад()
    {
        var w = await ArrangeAsync();
        var auto = DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>($"doc{w.Source.DocumentId.ToString(CultureInfo.InvariantCulture)}-p{w.Source.PeriodKey.Value.ToString(CultureInfo.InvariantCulture)}-auto");
        var payload = $$"""{"projectId":0,"documentId":{{w.Source.DocumentId}},"periodKey":{{w.Source.PeriodKey.Value}}}""";
        await InsertAsync(auto, "Running", w.Source.DocumentId, payload);
        try
        {
            Assert.True(await PendingAsync(w, w.Consumer));
            // ✎ b11: власний перерахунок ДЖЕРЕЛА для споживача — ланка джерела (ключ D-567), не власний перерахунок споживача.
            Assert.Equal(CascadePendingReason.SourceCascade, await ReasonAsync(w, w.Consumer));
            Assert.False(await PendingAsync(w, w.Other)); // парно

            // Зв'язка не ставить каскаду з -auto (ApprovedOnly чи читання з документа вимкнено) — не тримає.
            Assert.False(await PendingAsync(w, w.Consumer, autoBindings: []));
        }
        finally
        {
            await SetStateAsync(auto, "Succeeded");
        }

        var formula = DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>($"doc{w.Source.DocumentId.ToString(CultureInfo.InvariantCulture)}-formula-u{Random.Shared.Next(1_000_000, 9_000_000)}");
        await InsertAsync(formula, "Queued", w.Source.DocumentId, payload);
        try
        {
            Assert.True(await PendingAsync(w, w.Consumer));
            Assert.False(await PendingAsync(w, w.Other));
        }
        finally
        {
            await SetStateAsync(formula, "Succeeded");
        }
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Без_ребер_у_графі_ворота_як_доти()
    {
        var w = await ArrangeAsync();
        var target = Impact(RegistryImpactRecalculationTarget.ForSourceChange(w.BindingId, w.Year, null));
        await InsertAsync(target, "Queued", documentId: null, payload: "{}");
        try
        {
            Assert.False(await PendingAsync(w, w.Consumer, edges: []));
        }
        finally
        {
            await SetStateAsync(target, "Succeeded");
        }
    }

    [Theory]
    [InlineData(PeriodScopeKind.Month, 0, 2026, 3, 202603, true)]   // період-власник місяця
    [InlineData(PeriodScopeKind.Month, 0, 2026, 3, 202605, true)]   // пізніший — каскад допише читачів
    [InlineData(PeriodScopeKind.Month, 0, 2026, 3, 202602, true)]   // раніший теж (огляд b11: прогін будить раніших читачів, якір)
    [InlineData(PeriodScopeKind.Month, 0, 2026, null, 202601, true)] // місяць невідомий — увесь рік
    [InlineData(PeriodScopeKind.Month, 0, 2025, 3, 202603, false)]  // інший рік
    [InlineData(PeriodScopeKind.Year, 0, 2026, 3, 202601, true)]
    [InlineData(PeriodScopeKind.Year, 1 << 5, 2026, 3, 202601, false)] // літеральний травень, зміна в березні
    [InlineData(PeriodScopeKind.Year, 1 << 3, 2026, 3, 202601, true)]
    [InlineData(PeriodScopeKind.All, 0, 2024, 3, 202601, true)]
    [Trait("Requirement", "F5")]
    public void Affects_те_саме_відображення_що_задача_впливу(
        PeriodScopeKind kind, int mask, int sourceYear, int? sourceMonth, int periodKey, bool expected)
        => Assert.Equal(
            expected,
            SourceCascadePendingGate.Affects(new PeriodScope(kind, mask), sourceYear, sourceMonth, periodKey, PeriodKind.Monthly));

    [Theory]
    [InlineData("source-change-b12-y2026", 12, 2026, null, false)]
    [InlineData("source-change-b12-y2026-m4", 12, 2026, 4, false)]
    [InlineData("source-change-b12-y2026-m11-approved", 12, 2026, 11, true)]
    [Trait("Requirement", "F5")]
    public void Ціль_задачі_впливу_розбирається(string target, int binding, int year, int? month, bool approved)
    {
        Assert.True(SourceChangeKey.TryParse(target, out var b, out var y, out var m, out var a));
        Assert.Equal((binding, year, month, approved), (b, y, m, a));
        Assert.False(SourceChangeKey.TryParse("registry-impact-r1-y2026", out _, out _, out _, out _));
    }

    /// <summary>
    /// ✎ Огляд b11-2 (B), ланцюжок X→A→B: споживач B чекає, поки жива БУДЬ-ЯКА задача документа-предка A (каскад року A,
    /// залежні A, натискання — черга в базі й дзеркало Quartz) чи задача впливу зв'язки X→A; інший рік — ні.
    /// </summary>
    /// <remarks>
    /// Мутації: (1) предки — лише прямі джерела (одна ланка) — червоний «документ X»; (2) документні ланки — лише
    /// <c>-auto</c>/формули (як доти) — червоні «каскад року A» і «Quartz залежні A».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Ланцюжок_двох_ланок_тримає_кінцевого_споживача_поки_жива_задача_предка()
    {
        var w = await ArrangeAsync();
        var x = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(w.Consumer.PeriodKey.Value);
        var xTemplate = await TemplateOfAsync(x);
        var xToA = Interlocked.Decrement(ref _binding);
        var aToB = w.BindingId;
        IReadOnlyList<TemplateDependency> edges =
        [
            new(xTemplate, w.SourceTemplateId, xToA, 1, PeriodScope.Month, true, false),
            new(w.SourceTemplateId, w.ConsumerTemplateId, aToB, 1, PeriodScope.Month, true, false),
        ];
        IReadOnlyCollection<int> auto = [xToA, aToB];
        var a = w.Source.DocumentId.ToString(CultureInfo.InvariantCulture);
        var chosen = new Dictionary<(int, int), long> { [(aToB, w.Year)] = w.Source.DocumentId, [(xToA, w.Year)] = x.DocumentId };
        Task<bool> PendingChainAsync(TestDocument document) => PendingAsync(w, document, auto, edges, chosen);

        Assert.False(await PendingChainAsync(w.Consumer));

        // Задача впливу X→A (споживач A — предок B).
        var impact = Impact(RegistryImpactRecalculationTarget.ForSourceChange(xToA, w.Year, 3));
        await InsertAsync(impact, "Queued", documentId: null, payload: "{}");
        Assert.True(await PendingChainAsync(w.Consumer));
        Assert.False(await PendingChainAsync(w.Other));
        await SetStateAsync(impact, "Succeeded");

        // Каскад року A (черга в базі) — той самий рік тримає, інший — ні.
        var cascade = DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>($"doc{a}-y{w.Year.ToString(CultureInfo.InvariantCulture)}-dependents");
        await InsertAsync(cascade, "Running", w.Source.DocumentId, $$"""{"documentId":{{a}},"year":{{w.Year}}}""");
        Assert.True(await PendingChainAsync(w.Consumer));
        await SetStateAsync(cascade, "Succeeded");

        var otherYear = DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>($"doc{a}-y{(w.Year + 1).ToString(CultureInfo.InvariantCulture)}-dependents");
        await InsertAsync(otherYear, "Queued", w.Source.DocumentId, $$"""{"documentId":{{a}},"year":{{w.Year + 1}}}""");
        Assert.False(await PendingChainAsync(w.Consumer));
        await SetStateAsync(otherYear, "Succeeded");

        // Документ X (предок через дві ланки): будь-яка задача — теж тримає B.
        var xAuto = DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>(
            $"doc{x.DocumentId.ToString(CultureInfo.InvariantCulture)}-p{x.PeriodKey.Value.ToString(CultureInfo.InvariantCulture)}-auto");
        await InsertAsync(xAuto, "Queued", x.DocumentId, $$"""{"documentId":{{x.DocumentId}},"periodKey":{{x.PeriodKey.Value}}}""");
        Assert.True(await PendingChainAsync(w.Consumer));
        await SetStateAsync(xAuto, "Succeeded");

        // Дзеркало Quartz: залежні A за період (ціль — з JobId, без TargetKey і payload).
        var period = w.Source.PeriodKey.Value.ToString(CultureInfo.InvariantCulture);
        var quartzId = $"{nameof(IRecalculationJob)}~doc{a}-p{period}-dependents~{Guid.NewGuid():N}";
        await InsertQuartzAsync(quartzId, w.Source.DocumentId);
        Assert.True(await PendingChainAsync(w.Consumer));
        await SetQuartzStateAsync(quartzId, "Succeeded");
        Assert.False(await PendingChainAsync(w.Consumer));
    }
    /// <summary>
    /// ✎ Огляд b11 №3 (MEDIUM): тримає задача лише СПРАВЖНЬОГО документа-джерела B (документ, обраний за рік зв'язки), а не
    /// будь-якого документа шаблону-предка (інший проєкт/об'єкт); лише каскадна зв'язка (ApprovedOnly — ні); лише рік, що
    /// перетинається: перерахунок іншого року й формули людини з комірками іншого року не тримають, свого — тримають.
    /// </summary>
    /// <remarks>
    /// Мутації: (1) <c>ResolvedPends</c> без звірки документа — червоний «не обраний документ»; (2) без звірки року — червоні
    /// «інший рік»; (3) <c>ActualSourcesAsync</c> бере всі ребра графа, не лише каскадні — червоний «ApprovedOnly».
    /// </remarks>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    [Trait("Requirement", "F5")]
    public async Task Тримає_лише_справжнє_джерело_каскадною_зв_язкою_за_свій_рік()
    {
        var w = await ArrangeAsync();
        var otherTemplate = await TemplateOfAsync(w.Other);
        var otherBinding = Interlocked.Decrement(ref _binding);
        IReadOnlyList<TemplateDependency> edges =
        [
            new(w.SourceTemplateId, w.ConsumerTemplateId, w.BindingId, 1, PeriodScope.Year, true, false),
            new(otherTemplate, w.ConsumerTemplateId, otherBinding, 1, PeriodScope.Year, true, false),
        ];

        // Документ шаблону-предка «Other» — НЕ обраний джерелом зв'язки за рік (обрано документ іншого проєкту): не тримає.
        var chosen = new Dictionary<(int, int), long> { [(w.BindingId, w.Year)] = w.Source.DocumentId };
        IReadOnlyCollection<int> auto = [w.BindingId, otherBinding];
        var src = w.Source.DocumentId.ToString(CultureInfo.InvariantCulture);
        var oth = w.Other.DocumentId.ToString(CultureInfo.InvariantCulture);
        var year = w.Year.ToString(CultureInfo.InvariantCulture);
        var next = (w.Year + 1).ToString(CultureInfo.InvariantCulture);

        var foreign = DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>($"doc{oth}-y{year}-dependents");
        await InsertAsync(foreign, "Running", w.Other.DocumentId, $$"""{"documentId":{{oth}},"year":{{w.Year}}}""");
        try
        {
            Assert.False(await PendingAsync(w, w.Consumer, auto, edges, chosen));
        }
        finally
        {
            await SetStateAsync(foreign, "Succeeded");
        }

        // Справжнє джерело — тримає; лише за ApprovedOnly (зв'язка не каскадна) — ні.
        var real = DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>($"doc{src}-y{year}-dependents");
        await InsertAsync(real, "Running", w.Source.DocumentId, $$"""{"documentId":{{src}},"year":{{w.Year}}}""");
        try
        {
            Assert.True(await PendingAsync(w, w.Consumer, auto, edges, chosen));
            Assert.False(await PendingAsync(w, w.Consumer, [otherBinding], edges, chosen));
        }
        finally
        {
            await SetStateAsync(real, "Succeeded");
        }

        // Справжнє джерело, інший рік — не тримає.
        var later = DbBackgroundJobScheduler.TargetKeyOf<IRecalculationJob>($"doc{src}-y{next}-dependents");
        await InsertAsync(later, "Queued", w.Source.DocumentId, $$"""{"documentId":{{src}},"year":{{w.Year + 1}}}""");
        try
        {
            Assert.False(await PendingAsync(w, w.Consumer, auto, edges, chosen));
        }
        finally
        {
            await SetStateAsync(later, "Succeeded");
        }

        // Формули людини на справжньому джерелі: комірки іншого року — ні; свого — так.
        foreach (var (cellYear, expected) in new[] { (w.Year + 1, false), (w.Year, true) })
        {
            var formula = DbBackgroundJobScheduler.TargetKeyOf<IFormulaRecalculationJob>($"doc{src}-formula-u{Random.Shared.Next(1_000_000, 9_000_000)}");
            var cellPeriod = ((cellYear * 100) + 3).ToString(CultureInfo.InvariantCulture);
            await InsertAsync(formula, "Queued", w.Source.DocumentId, $$"""{"documentId":{{src}},"cells":[{"periodKey":{{cellPeriod}}}]}""");
            try
            {
                Assert.Equal(expected, await PendingAsync(w, w.Consumer, auto, edges, chosen));
            }
            finally
            {
                await SetStateAsync(formula, "Succeeded");
            }
        }
    }

    // ───────────────────────────── допоміжне ─────────────────────────────

    /// <summary>Ціль задачі впливу так, як її пише черга в базі (`IRegistryImpactRecalculationJob~…`, огляд b9).</summary>
    private static string Impact(string target) => DbBackgroundJobScheduler.TargetKeyOf<IRegistryImpactRecalculationJob>(target);

    private sealed record World(TestDocument Consumer, TestDocument Other, TestDocument Source, int BindingId, int ConsumerTemplateId, int SourceTemplateId, int Year);

    private async Task<World> ArrangeAsync()
    {
        var consumer = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync();
        var other = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(consumer.PeriodKey.Value);
        var source = await new TestDocumentBuilder(sql.ConnectionString).BuildAsync(consumer.PeriodKey.Value);
        var consumerTemplate = await TemplateOfAsync(consumer);
        var otherTemplate = await TemplateOfAsync(other);
        var sourceTemplate = await TemplateOfAsync(source);
        Assert.NotEqual(consumerTemplate, otherTemplate);
        Assert.NotEqual(consumerTemplate, sourceTemplate);

        return new World(
            consumer, other, source, Interlocked.Decrement(ref _binding), consumerTemplate, sourceTemplate,
            consumer.PeriodKey.Value / 100);
    }

    private async Task<bool> PendingAsync(
        World w, TestDocument document, IReadOnlyCollection<int>? autoBindings = null, IReadOnlyList<TemplateDependency>? edges = null,
        IReadOnlyDictionary<(int Binding, int Year), long>? chosen = null)
        => await ReasonAsync(w, document, autoBindings, edges, chosen) != CascadePendingReason.None;

    /// <summary>✎ b11 (огляд координатора 24.11): причина утримання — ланка джерела дає ключ D-567, не «власний перерахунок».</summary>
    private async Task<CascadePendingReason> ReasonAsync(
        World w, TestDocument document, IReadOnlyCollection<int>? autoBindings = null, IReadOnlyList<TemplateDependency>? edges = null,
        IReadOnlyDictionary<(int Binding, int Year), long>? chosen = null)
    {
        // ✎ Огляд b11 №3: справжнє джерело зв'язки за рік (вибір документа року); за замовчуванням — документ-джерело світу.
        chosen ??= new Dictionary<(int, int), long> { [(w.BindingId, w.Year)] = w.Source.DocumentId };
        var graph = Substitute.For<IDependencyGraphProvider>();
        graph.GetAsync(Arg.Any<CancellationToken>()).Returns(new DependencyGraphSnapshot(
            "f5", [], [],
            edges ?? [new TemplateDependency(w.SourceTemplateId, w.ConsumerTemplateId, w.BindingId, 1, new PeriodScope(PeriodScopeKind.Year, 0), true, false)],
            [], [], []));

        await using var db = new EcrDbContext(
            new DbContextOptionsBuilder<EcrDbContext>().UseSqlServer(sql.ConnectionString).Options);
        var bindings = autoBindings ?? [w.BindingId];
        var gate = new SourceCascadePendingGate(db, gateCache: null, graph)
        {
            AutoBindingsProbe = _ => Task.FromResult(bindings),
            ChosenSourcesProbe = (_, _) => Task.FromResult<IReadOnlyDictionary<(int Binding, int Year), long>>(
                chosen.ToDictionary(p => (p.Key.Binding, p.Key.Year), p => p.Value)),
        };
        return await gate.GetPendingReasonAsync(document.DocumentId, document.PeriodKey.Value, CancellationToken.None);
    }

    /// <summary>Шаблон документа за період — те саме правило, що у воротах (власний шаблон чи версії проєкту за період).</summary>
    private async Task<int> TemplateOfAsync(TestDocument document)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(d.TemplateId, v.TemplateId)
              FROM doc.Document AS d
              LEFT JOIN cfg.TemplateVersion AS v ON v.Id = CASE WHEN d.TemplateId IS NULL THEN doc.fn_VersionOf(d.ProjectId, @period) ELSE doc.fn_TemplateVersionOf(d.ProjectId, d.TemplateId, @period) END
             WHERE d.Id = @doc;
            """;
        command.Parameters.AddWithValue("@doc", document.DocumentId);
        command.Parameters.AddWithValue("@period", document.PeriodKey.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task InsertAsync(string target, string state, long? documentId, string payload)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO itg.JobProgress
                (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt, DocumentId, TargetKey, Payload)
            VALUES (@id, N'Ecr.Test.F5Upstream', @state, 0, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(),
                    @doc, @target, @payload);
            """;
        command.Parameters.AddWithValue("@id", $"f5u-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@doc", (object?)documentId ?? DBNull.Value);
        command.Parameters.AddWithValue("@target", target);
        command.Parameters.AddWithValue("@payload", payload);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Рядок-дзеркало Quartz: без TargetKey, Lane і payload; ціль — у JobId.</summary>
    private async Task InsertQuartzAsync(string jobId, long documentId)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO itg.JobProgress
                (JobId, JobCode, [State], [Percent], StartedAt, UpdatedAt, HeartbeatAt, CreatedAt, DocumentId)
            VALUES (@id, @code, N'Queued', 0, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), @doc);
            """;
        command.Parameters.AddWithValue("@id", jobId);
        command.Parameters.AddWithValue("@code", JobListKinds.RecalculationJobCode);
        command.Parameters.AddWithValue("@doc", documentId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetQuartzStateAsync(string jobId, string state)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE itg.JobProgress SET [State] = @state WHERE JobId = @id;";
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@id", jobId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SetStateAsync(string target, string state)
    {
        await using var connection = new SqlConnection(sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE itg.JobProgress SET [State] = @state WHERE TargetKey = @target AND [State] IN ('Queued', 'Running');";
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@target", target);
        await command.ExecuteNonQueryAsync();
    }
}
