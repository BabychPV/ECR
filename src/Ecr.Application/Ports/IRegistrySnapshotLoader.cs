using Ecr.Expressions.Evaluation;

namespace Ecr.Application.Ports;

/// <summary>
/// Завантажує знімок довідників для обчислення (RT-22, FEATURE-REGISTRY-TABLES §5.7,
/// рішення <c>D-158</c>, <c>D-162</c>).
/// </summary>
/// <remarks>
/// ⛔ Дві осі часу розділені (<c>R-9</c>, §3.6):
/// <list type="bullet">
/// <item><b>бізнес-дата</b> — останній день періоду: запис видимий, лише якщо його вікно
/// чинності містить цю дату (та сама умова, що в пікері <c>Lookup</c>,
/// <c>RegistryResolver.IsSelectable</c>);</item>
/// <item><b>системний момент</b> — <c>CalculationRun.RegistryAsOfUtc</c>: усі довідники
/// читаються <c>FOR SYSTEM_TIME AS OF</c> одного моменту, тож повтор прогону бачить ті
/// самі дані, хоч би що змінили в довіднику після нього.</item>
/// </list>
///
/// ⚠ Знімок вантажиться ДО обчислення й далі лише читається (<c>D-162</c>): жодного
/// звернення до БД під час обчислення. Кількість запитів не залежить ні від кількості
/// довідників, ні від кількості записів.
/// </remarks>
public interface IRegistrySnapshotLoader
{
    /// <summary>Читає довідники й повертає незмінний знімок із уже застосованою видимістю.</summary>
    /// <param name="registryDefIds">
    /// Довідники, які читають формули прогону (<c>cfg.RegistryUse</c>). До них завантажувач
    /// сам додає цілі <c>Lookup</c>-полів (транзитивно): без них не пройде шлях
    /// <c>ROW.COMPONENT.MW</c> і не перевіриться видимість батька композиції.
    /// </param>
    /// <param name="businessDate">Бізнес-дата знімка — останній день періоду.</param>
    /// <param name="registryAsOfUtc">
    /// Системний момент <c>AS OF</c> (UTC). <c>null</c> — прогін до міграції
    /// <c>RK04RegistryUseAndRunAsOf</c> (системної історії ще не було): читаються поточні
    /// дані (§3.5).
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Знімок; для порожнього переліку — порожній знімок без звернень до БД.</returns>
    public Task<IRegistrySnapshot> LoadAsync(
        IReadOnlyCollection<int> registryDefIds,
        DateOnly businessDate,
        DateTime? registryAsOfUtc,
        CancellationToken ct);

    /// <summary>
    /// Читає ті самі дані з БД, але знімка на дату не будує: його будує
    /// <see cref="IRegistrySnapshotSource.Build"/> — стільки разів і на стільки дат, скільки треба.
    /// </summary>
    /// <param name="registryDefIds">Довідники — як у <see cref="LoadAsync"/>.</param>
    /// <param name="registryAsOfUtc">Системний момент <c>AS OF</c> — як у <see cref="LoadAsync"/>.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Джерело знімків; для порожнього переліку — порожнє, без звернень до БД.
    /// </returns>
    /// <remarks>
    /// ⛔ L5-12. Для пакета, якому потрібні знімки на КІЛЬКА дат (правила довідника: кожна <c>ValidFrom</c>
    /// темпорального запису — окрема дата). Бізнес-дата впливає лише на видимість, яку
    /// <see cref="IRegistrySnapshotSource.Build"/> застосовує в пам'яті, а читання з БД від неї не
    /// залежить, тож повторювати його на кожну дату — марно: п'ять запитів над усіма записами довідників.
    /// <c>LoadAsync(ids, date, asOf)</c> ≡ <c>(await LoadSourceAsync(ids, asOf)).Build(date)</c>.
    /// </remarks>
    public Task<IRegistrySnapshotSource> LoadSourceAsync(
        IReadOnlyCollection<int> registryDefIds,
        DateTime? registryAsOfUtc,
        CancellationToken ct);
}
