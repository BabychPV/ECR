// tests/Ecr.Infrastructure.Tests/Jobs/SourceDependencyWorld.cs
using Ecr.Application.Calculations;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Entities.Workflow;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.Expressions.Evaluation;
using Ecr.Infrastructure.Caching;
using Ecr.Infrastructure.DocumentDependencies;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecr.Infrastructure.Tests.Jobs;

/// <summary>
/// B179 L2: світ «шаблон-джерело наповнює довідник → шаблон-споживач читає його формулою» на справжньому SQL, періоди
/// 2099xx, власні коди на кожен тест (робоча зв'язка HSE400 і чужі дані не зачіпаються).
/// </summary>
/// <param name="sql">Фікстура.</param>
internal sealed class SourceDependencyWorld(SqlServerFixture sql)
{
    /// <summary>Рік світу.</summary>
    public const int Year = 2099;

    private static readonly DateTime Now = new(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _tag = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private int _counter;

    /// <summary>Ключ періоду місяця <paramref name="month"/> року світу.</summary>
    /// <param name="month">Місяць.</param>
    public static int Period(int month) => (Year * 100) + month;

    /// <summary>Новий довідник; повертає (Id, код).</summary>
    public async Task<(int Id, string Code)> RegistryAsync()
    {
        await using var db = sql.CreateContext();
        var registry = new RegistryDef(
            EcrCode.Create($"SD{_tag}{++_counter}"), Text("Source dependency"), isTemporal: false);
        db.RegistryDefs.Add(registry);
        await db.SaveChangesAsync();
        return (registry.Id, registry.Code);
    }

    /// <summary>Документ (свій шаблон) з екземплярами за місяці <paramref name="months"/>; періоди відкриті.</summary>
    /// <param name="months">Місяці (перший — період будівника).</param>
    /// <param name="registryCode">Довідник, який читає формула колонки 2 (за <c>[Period].Year, [Period].Sequence</c>); <c>null</c> — без формули.</param>
    public async Task<TestDocument> DocumentAsync(int[] months, string? registryCode = null)
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var document = await builder.BuildAsync(periodKey: Period(months[0]));
        foreach (var month in months.Skip(1))
        {
            await builder.AddPeriodInstanceAsync(document, new PeriodKey(Period(month)));
        }

        await using var db = sql.CreateContext();
        var periods = await db.Periods.Where(p => p.ProjectId == document.ProjectId).ToListAsync();
        foreach (var period in periods)
        {
            period.AdvanceTo(PeriodState.Open, Now);
        }

        if (registryCode is not null)
        {
            var formula = new FormulaDef(
                document.TableDefId, FormulaScope.Column,
                $"REGFIELD(REGFIND('{registryCode}', [Period].Year, [Period].Sequence, 'S', 'I'), 'VALUE')",
                ExpressionDialect.Template);
            formula.AssignColumn(document.ColumnDefIds[1]);
            db.FormulaDefs.Add(formula);
        }

        await db.SaveChangesAsync();
        return document;
    }

    /// <summary>Зв'язка «шаблон-джерело → довідник» у стані <paramref name="state"/> (біти 1/1); повертає Id.</summary>
    public async Task<int> BindingAsync(int sourceTemplateId, int registryId, DocumentSourceState state = DocumentSourceState.Any)
    {
        await using var db = sql.CreateContext();
        var binding = new DocumentSourceBinding($"B179L2_{_tag}_{++_counter}", sourceTemplateId, registryId);
        db.DocumentSourceBindings.Add(binding);
        await db.SaveChangesAsync();
        await db.DocumentSourceBindings.Where(b => b.Id == binding.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.SourceState, state));
        return binding.Id;
    }

    /// <summary>Біт «перераховувати залежних» зв'язки.</summary>
    public async Task SetRecalculateDependentsAsync(int bindingId, bool value)
    {
        await using var db = sql.CreateContext();
        await db.DocumentSourceBindings.Where(b => b.Id == bindingId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.RecalculateDependents, value));
    }

    /// <summary>Аркуш документа за період — у стан <paramref name="status"/> (подано/затверджено).</summary>
    public async Task SetSheetAsync(TestDocument document, int month, DocumentStatus status)
    {
        await using var db = sql.CreateContext();
        var approval = new ApprovalState(document.DocumentId, document.SheetDefId, Period(month));
        approval.Submit(userId: 5, Now);
        if (status == DocumentStatus.Approved)
        {
            approval.Approve(userId: 6, Now);
        }

        db.ApprovalStates.Add(approval);
        await db.SaveChangesAsync();
    }

    /// <summary>Період документа — закритий.</summary>
    public async Task CloseAsync(TestDocument document, int month)
    {
        await using var db = sql.CreateContext();
        var period = await db.Periods.SingleAsync(p => p.ProjectId == document.ProjectId && p.PeriodKeyValue == Period(month));
        period.TransitionTo(PeriodState.Closed, Now);
        await db.SaveChangesAsync();
    }

    /// <summary>Справжній граф залежностей (без вікна ревізії — свіжі дані тесту видно одразу).</summary>
    public static DependencyGraphProvider Graph(EcrDbContext db)
        => new(
            db, new MemoryCache(new MemoryCacheOptions()), new RealFormulaEngine(), new MethodologyRegistryEdges(db),
            new CacheLifetimes(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30), TimeSpan.Zero));

    /// <summary>Ціль перерахунку залежного документа за місяць.</summary>
    public static string DependentsTarget(TestDocument document, int month)
        => CalculationTrigger.DependentsTargetOf(document.DocumentId, new PeriodKey(Period(month)));

    private static LocalizedText Text(string en) => new(new Dictionary<string, string> { ["en"] = en });
}
