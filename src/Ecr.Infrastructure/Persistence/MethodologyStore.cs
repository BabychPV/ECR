using Ecr.Application.Ports;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>Реалізація <see cref="IMethodologyStore"/> над <see cref="EcrDbContext"/>.</summary>
public sealed class MethodologyStore(EcrDbContext db) : IMethodologyStore
{
    /// <summary>
    /// Стеля вибірки дочірніх записів версії.
    /// </summary>
    /// <remarks>
    /// Формул у методології — десятки, речовин — одиниці. Межа є не тому, що
    /// їх може бути багато, а тому, що помилка в даних без неї виглядала б як
    /// повільність, а не як помилка.
    /// </remarks>
    private const int MaxChildren = 10_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyVersion>> GetPublishedVersionsAsync(
        int methodologyId, CancellationToken ct)
        => await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => v.MethodologyId == methodologyId
                        && v.Status == TemplateVersionStatus.Published)
            .OrderByDescending(v => v.EffectiveFrom)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyRule>> GetRulesAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyRules
            .AsNoTracking()
            .Where(r => r.MethodologyVersionId == methodologyVersionId && r.IsActive)

            // Порядок за Priority — це і є правило «перший збіг виграє»
            // (ФВ-13.4). Сортувати в пам'яті означало б покластися на те, що
            // база поверне рядки як їй зручно.
            .OrderBy(r => r.Priority)
            .ThenBy(r => r.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyFormula>> GetFormulasAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyFormulas
            .Where(f => f.MethodologyVersionId == methodologyVersionId)
            .OrderBy(f => f.EvaluationOrder)
            .ThenBy(f => f.Id)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologySubstance>> GetSubstancesAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologySubstances
            .AsNoTracking()
            .Where(s => s.MethodologyVersionId == methodologyVersionId)
            .OrderBy(s => s.Ordinal)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyOutput>> GetOutputsAsync(
        int methodologyVersionId, CancellationToken ct)
        => await db.MethodologyOutputs
            .AsNoTracking()
            .Where(o => o.MethodologyVersionId == methodologyVersionId)
            .OrderBy(o => o.Ordinal)
            .Take(MaxChildren)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <inheritdoc />
    /// <remarks>
    /// ⚠ Версії ЗАВАНТАЖУЮТЬСЯ разом із методологією і відстежуються: агрегат
    /// потрібен, щоб перевірити перетин вікон і опублікувати версію в одній
    /// транзакції. <c>AsNoTracking</c> тут зробив би публікацію
    /// беззмістовною — зміни нікуди не збереглися б.
    /// </remarks>
    public async Task<Methodology?> FindByVersionAsync(int methodologyVersionId, CancellationToken ct)
    {
        var methodologyId = await db.MethodologyVersions
            .AsNoTracking()
            .Where(v => v.Id == methodologyVersionId)
            .Select(v => (int?)v.MethodologyId)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (methodologyId is not { } id)
        {
            return null;
        }

        return await db.Methodologies
            .Include(m => m.Versions)
            .FirstOrDefaultAsync(m => m.Id == id, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ⛔ Таблиці <c>calc.TestCase</c> у схемі немає, хоч ФВ-13.7 прямо на неї
    /// посилається — це <c>P-08</c>. Поки її немає, набір порожній, і
    /// публікація відхиляється як «без зеленого тесту» (ФВ-9.12). Це не
    /// заглушка, що мовчить: відмова гучна, і причина названа.
    /// </remarks>
    public Task<IReadOnlyList<MethodologyTestCase>> GetTestCasesAsync(
        int methodologyVersionId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MethodologyTestCase>>([]);
}

/// <summary>Реалізація <see cref="IConstantStore"/> над <see cref="EcrDbContext"/>.</summary>
/// <remarks>
/// Порт віддає **кандидатів**, а не готове значення: звуження за категорією і
/// речовиною та вибір темпорального інтервалу — правила предметної області
/// (ФВ-16.5), і живуть вони в <c>ConstantResolver</c>. Зокрема правило «кілька
/// кандидатів на одну дату — помилка конфігурації» неможливо перевірити, якщо
/// сховище вже вибрало один запис.
/// </remarks>
public sealed class ConstantStore(EcrDbContext db) : IConstantStore
{
    /// <summary>Стеля: варіантів однієї константи — одиниці, не тисячі.</summary>
    private const int MaxCandidates = 1_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MethodologyConstant>> GetCandidatesAsync(
        int methodologyVersionId, string code, CancellationToken ct)
        => await db.MethodologyConstants
            .AsNoTracking()
            .Where(c => c.MethodologyVersionId == methodologyVersionId && c.Code == code)
            .OrderBy(c => c.Id)
            .Take(MaxCandidates)
            .ToListAsync(ct)
            .ConfigureAwait(false);
}
