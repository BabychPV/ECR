using Ecr.Application.Ports;

namespace Ecr.Adapters.PiAf;

/// <summary>
/// Виконує збір: ідемпотентно, з catch-up і журналом покриття (ФВ-11.3).
/// </summary>
/// <remarks>
/// Обслуговування AF відбуватиметься незалежно від нашої згоди, тому простій
/// джерела має бути **затримкою, а не втратою**. Ознака здоров'я — журнал
/// покриття, а не тиша: система, яка «нічого не повідомляє», і система, яка
/// «нічого не зібрала», ззовні виглядають однаково.
/// </remarks>
public sealed class CollectionRunner(
    IEnumerable<IExternalDataSource> sources,
    SourceUnitConverter unitConverter,
    CatchUpPlanner catchUp,
    Ecr.Infrastructure.Persistence.EcrDbContext db)
{
    /// <summary>Виконує збір для сутності джерела.</summary>
    public Task RunAsync(int sourceEntityId, DateTime from, DateTime to, IJobProgress progress, CancellationToken ct)
        => throw new NotImplementedException(
            "TODO:\n" +
            "1) створити itg.CollectionRun;\n" +
            "2) обрати адаптер за Transport джерела — вибір транспорту це НАЛАШТУВАННЯ, " +
            "   не гілка коду (ФВ-11.2);\n" +
            "3) читати діапазон; upsert у ext.RawDataPoint за природним ключем " +
            "   (SourceEntityId, SourcePath, Timestamp) — повторний запуск не дублює;\n" +
            "4) ⚠ сирі дані зберігати В ОДИНИЦІ ДЖЕРЕЛА; конверсія — на межі, через " +
            "   unitConverter, із записом у журнал (ФВ-16.9). Інакше повторний перерахунок " +
            "   з архіву дасть інший результат;\n" +
            "5) записати itg.CollectionCoverage — за які інтервали дані є;\n" +
            "6) при відмові джерела: зафіксувати ECR-INT-0503, поставити діапазон у catch-up " +
            "   і ЗАВЕРШИТИСЯ успішно — це затримка, не збій.");
}
