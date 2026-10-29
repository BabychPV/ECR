using Ecr.Application.Ports;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Recalculation;

/// <summary>
/// L2: спільні читання ОДНОГО проходу перерахунку документа (усі періоди під одним локом): екземпляри таблиць за період
/// і залежності формул за версію шаблону читаються раз, а не раз на період × ланку ланцюжка.
/// </summary>
/// <remarks>
/// ⛔ Час життя — один прохід (<c>FormulaRecalculationJob.ExecuteAsync</c>), НЕ сервіс і не запит: екземпляри не
/// створюються й не видаляються під час перерахунку (рядки й екземпляри пише PATCH/імпорт до постановки задачі),
/// а залежності версії незмінні. Не потокобезпечний навмисно: прохід послідовний.
/// </remarks>
public sealed class RecalculationSharedLoad(
    IRowStore rowStore, ITemplateVersionStore versions, IOwnerReadSession? ownerReads = null) : IDisposable
{
    // L6: сесія читання комірок власників (Q-17.4) живе весь прохід, а не один період: 12 читачів «місяці в колонках»
    // читали ТІ САМІ введені комірки власників 12 разів (кожне читання — скан ~124 тис. сторінок doc.CellValue).
    // Введені комірки під час проходу не пишуться (перерахунок пише лише похідні), тож кеш залишається чинним.
    private readonly IDisposable? _ownerSession = ownerReads?.BeginOwnerReadSession();

    private readonly Dictionary<(long DocumentId, int Period), IReadOnlyList<TableInstanceRef>> _instances = [];
    private readonly Dictionary<int, IReadOnlyList<FormulaDependency>> _dependencies = [];

    /// <summary>Скільки разів справді ходили в сховище рядків (для тесту «читається раз»).</summary>
    public int InstanceReads { get; private set; }

    /// <summary>Скільки разів справді читали залежності (для тесту «читається раз»).</summary>
    public int DependencyReads { get; private set; }

    /// <summary>Екземпляри таблиць документа за період.</summary>
    public async Task<IReadOnlyList<TableInstanceRef>> InstancesAsync(long documentId, PeriodKey period, CancellationToken ct)
    {
        if (_instances.TryGetValue((documentId, period.Value), out var known))
        {
            return known;
        }

        InstanceReads++;
        var loaded = await rowStore.GetTableInstancesAsync(documentId, period, ct).ConfigureAwait(false);
        _instances[(documentId, period.Value)] = loaded;

        return loaded;
    }

    /// <inheritdoc />
    public void Dispose() => _ownerSession?.Dispose();

    /// <summary>Розкриті залежності формул версії шаблону.</summary>
    public async Task<IReadOnlyList<FormulaDependency>> DependenciesAsync(int templateVersionId, CancellationToken ct)
    {
        if (_dependencies.TryGetValue(templateVersionId, out var known))
        {
            return known;
        }

        DependencyReads++;
        var loaded = await versions.ListFormulaDependenciesAsync(templateVersionId, ct).ConfigureAwait(false);
        _dependencies[templateVersionId] = loaded;

        return loaded;
    }
}
