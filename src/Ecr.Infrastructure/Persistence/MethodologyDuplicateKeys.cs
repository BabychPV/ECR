// src/Ecr.Infrastructure/Persistence/MethodologyDuplicateKeys.cs
using System.Globalization;
using Ecr.Application.Errors;
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Microsoft.EntityFrameworkCore;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// ⛔ B158 (a): програш гонки «перевір, потім встав» у конфігураторі методологій — у ту саму відмову, що й
/// послідовний шлях, а не сирий <see cref="DbUpdateException"/> (500 <c>ECR-SYS-0500</c>).
/// </summary>
/// <remarks>
/// Обробники методологій перевіряють «чи є вже» без блокування (<c>FindByCodeAsync</c>, <c>Find…Async</c> складу
/// версії, агрегат із номерами версій), тож два однакові запити обидва бачать «немає», і другого відбиває лише
/// унікальний індекс. Мапиться за ІМЕНЕМ індексу в лапках (без лапок <c>UQ_Methodology</c> збігся б і з
/// <c>UQ_MethodologyVersion</c>) і лише для відомих індексів; решту викликач перекидає як є.
/// <para>
/// Спільне для <see cref="UnitOfWork"/> і <see cref="MethodologyDraftStore.SaveDraftAsync"/>: останній зберігає
/// напряму через контекст, і його виняток до одиниці роботи не доходить.
/// </para>
/// </remarks>
internal static class MethodologyDuplicateKeys
{
    /// <summary>
    /// Індекси складу версії, які пишуть upsert-обробники (формули, константи, правила, обов'язкові входи, виходи,
    /// тести, категорії, правило категорії, речовини). Послідовний повтор такого запиту — оновлення без помилки,
    /// тож програш гонки — загальний конфлікт «дані змінилися» (той самий, що дає
    /// <see cref="DbUpdateConcurrencyException"/> в <see cref="UnitOfWork"/>): повторний запит уже оновить рядок.
    /// </summary>
    private static readonly string[] UpsertIndexes =
    [
        "'UQ_MethodologyFormula'",
        "'UQ_MethodologyConstant'",
        "'UQ_MethodologyRule'",
        "'UQ_MethodologyRequiredInput'",
        "'UQ_MethodologyOutput'",
        "'UQ_TestCase'",
        "'UQ_MethodologyCategory'",
        "'UQ_CategoryRule_Version'",
        "'UQ_MethodologySubstance'",
    ];

    /// <summary>Відмова для відомого індексу методологій або <c>null</c>.</summary>
    /// <param name="db">Контекст, на якому впало збереження.</param>
    /// <param name="ex">Виняток збереження (порушення унікальності).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Виняток, який треба кинути замість <paramref name="ex"/>, або <c>null</c>.</returns>
    public static async Task<Exception?> TryMapAsync(EcrDbContext db, DbUpdateException ex, CancellationToken ct)
    {
        // a1/a2: два `POST /methodologies` (або два імпорти пакета) з тим самим кодом. Відмова — та сама, що
        // в `CreateMethodologyHandler` (`409 ECR-CALC-0409 codeTaken` з `{code, existingId}`); ідентифікатор
        // переможця читається одним запитом — ключ каталогу вимагає `{existingId}`, і той самий користувач
        // бачить його в послідовному шляху. Переможця не знайдено (відкотився) — загальний конфлікт.
        if (SqlConflict.ViolatesIndex(ex, "'UQ_Methodology'")
            && ex.Entries.FirstOrDefault(e => e.State == EntityState.Added && e.Entity is Methodology)
                is { Entity: Methodology methodology })
        {
            var existingId = await db.Methodologies
                .AsNoTracking()
                .Where(m => m.Code == methodology.Code)
                .Select(m => (int?)m.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            return existingId is { } id
                ? new BusinessRuleException(
                    "ECR-CALC-0409",
                    $"Методологія «{methodology.Code}» уже існує (ідентифікатор {id}).",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-CALC-0409.codeTaken",
                        ["code"] = methodology.Code,
                        ["existingId"] = id.ToString(CultureInfo.InvariantCulture),
                    })
                : ConcurrentChange();
        }

        // a3: два `POST /methodologies/{id}/versions` з тим самим номером. Номер перевіряє агрегат
        // (`Methodology.AddVersion`) на знімку без версії переможця — та сама `DomainException`
        // (`ECR-CALC-0409 versionNumberTaken` з `{version, code}`).
        if (SqlConflict.ViolatesIndex(ex, "'UQ_MethodologyVersion'")
            && ex.Entries.FirstOrDefault(e => e.State == EntityState.Added && e.Entity is MethodologyVersion)
                is { Entity: MethodologyVersion version })
        {
            var code = db.Methodologies.Local.FirstOrDefault(m => m.Id == version.MethodologyId)?.Code
                       ?? await db.Methodologies
                           .AsNoTracking()
                           .Where(m => m.Id == version.MethodologyId)
                           .Select(m => m.Code)
                           .FirstOrDefaultAsync(ct)
                           .ConfigureAwait(false);

            return code is { } methodologyCode
                ? new DomainException(
                    "ECR-CALC-0409",
                    $"Версія {version.Version} методології {methodologyCode} уже існує.",
                    new Dictionary<string, object?>
                    {
                        ["messageKey"] = "err.ECR-CALC-0409.versionNumberTaken",
                        ["version"] = version.Version,
                        ["code"] = methodologyCode,
                    })
                : ConcurrentChange();
        }

        // a4–a12: upsert складу версії.
        foreach (var index in UpsertIndexes)
        {
            if (SqlConflict.ViolatesIndex(ex, index))
            {
                return ConcurrentChange();
            }
        }

        return null;
    }

    private static ConcurrencyConflictException ConcurrentChange()
        => new(
            "ECR-CELL-0409",
            "Методологію щойно змінив інший запит: перечитайте її й повторіть дію.",
            new Dictionary<string, object?>
            {
                ["messageKey"] = "err.ECR-CELL-0409.concurrentChange",
            });
}
