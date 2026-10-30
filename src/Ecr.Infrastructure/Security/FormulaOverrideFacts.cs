using Ecr.Application.Documents.FormulaOverride;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Security;

/// <summary>
/// ✎ D-461: факти зрізу для рішення «чи можна замінити формулу ручним значенням»: політика формул таблиці
/// (<see cref="FormulaOverridePolicy"/>) і комірки, що вже мають ручне значення (<c>doc.CellFormulaOverride</c>).
/// </summary>
/// <param name="Policy">Формули колонки/комірки таблиці.</param>
/// <param name="Overridden">Комірки з ручним значенням: <c>(RowKey, ColumnDefId)</c>.</param>
internal sealed record FormulaOverrideFacts(
    FormulaOverridePolicy Policy,
    IReadOnlySet<(string RowKey, int ColumnDefId)> Overridden)
{
    private static readonly IReadOnlySet<(string, int)> NoCells = new HashSet<(string, int)>();

    /// <summary>Таблиця без формул колонки/комірки: замінювати нічого.</summary>
    public static readonly FormulaOverrideFacts None = new(FormulaOverridePolicy.None, NoCells);

    /// <summary>
    /// Формула комірки дозволяє ручне значення, або комірка вже ручна (її можна повернути до формули й після вимкнення
    /// прапорця). Право <c>Document.OverrideFormula</c> тут НЕ перевіряється — це робить <c>EditRules.CanEdit</c>.
    /// </summary>
    public bool Overridable(string rowKey, ColumnDef column)
        => Policy.HasTargets
           && Policy.FormulaOf(rowKey, column) is { } formula
           && (formula.AllowManualOverride || Overridden.Contains((rowKey, column.Id)));

    /// <summary>
    /// Факти для екземплярів однієї групи «документ × період» — ОДИН запит позначок на групу і лише для таблиць, де є
    /// формули колонки/комірки.
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, FormulaOverrideFacts>> LoadAsync(
        EcrDbContext db,
        TemplateVersionSnapshot snapshot,
        IReadOnlyList<(long InstanceId, int TableDefId)> instances,
        PeriodKey periodKey,
        CancellationToken ct)
    {
        var policies = new Dictionary<long, FormulaOverridePolicy>();
        foreach (var (instanceId, tableDefId) in instances)
        {
            var table = snapshot.Sheets.SelectMany(s => s.Tables).FirstOrDefault(t => t.Id == tableDefId);
            if (table is not null && FormulaOverridePolicy.For(table) is { HasTargets: true } policy)
            {
                policies[instanceId] = policy;
            }
        }

        var result = new Dictionary<long, FormulaOverrideFacts>(policies.Count);
        if (policies.Count == 0)
        {
            return result;
        }

        var key = periodKey.Value;
        var ids = policies.Keys.ToList();

        // ⚠ Рівність за PeriodKey на ОБОХ таблицях — відсічка партицій; IX_CellFormulaOverride_Instance — пошук.
        var marks = await db.CellFormulaOverrides.AsNoTracking()
            .Where(o => o.PeriodKeyValue == key && ids.Contains(o.TableInstanceId))
            .Join(
                db.TableRows.Where(r => r.PeriodKeyValue == key && !r.IsDeleted),
                o => new { o.PeriodKeyValue, o.TableRowId },
                r => new { r.PeriodKeyValue, TableRowId = r.Id },
                (o, r) => new { o.TableInstanceId, r.RowKeyValue, o.ColumnDefId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var byInstance = marks
            .GroupBy(m => m.TableInstanceId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<(string, int)>)g.Select(m => (m.RowKeyValue, m.ColumnDefId)).ToHashSet());

        foreach (var (instanceId, policy) in policies)
        {
            result[instanceId] = new FormulaOverrideFacts(policy, byInstance.GetValueOrDefault(instanceId) ?? NoCells);
        }

        return result;
    }
}
