// src/Ecr.Application/Ports/ICalculationBindingStore.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Ports;

/// <summary>
/// Прив'язки результатів методології до колонок документа
/// (<c>cfg.CalculationBinding</c>, <c>D-69</c>).
/// </summary>
/// <remarks>
/// ⛔ Порт окремий від <see cref="IMethodologyDraftStore"/> навмисно, і межа тут
/// не за зручністю, а за ВЛАСНИКОМ. Усе, що вміє той порт, належить ВЕРСІЇ
/// методології і живе в схемі <c>calc</c>; прив'язка належить ШАБЛОНУ — вона
/// посилається на <c>cfg.ColumnDef</c>, переживає всі версії методології
/// одразу (ключ <c>MethodologyId</c>, не <c>MethodologyVersionId</c>) і
/// клонуванням версії не копіюється взагалі.
///
/// ⛔ Без цього порту прив'язку не створювало НІЩО — ні обробник, ні тест, —
/// і <c>RecalculationJob.BindingsAsync</c> тихо повертав порожній перелік:
/// перерахунок документа завершувався успіхом, не порахувавши жодного числа
/// (директива №09, <c>W6</c> §3).
/// </remarks>
public interface ICalculationBindingStore
{
    /// <summary>
    /// Прив'язка за своєю адресою — трійкою <c>UQ_CalculationBinding</c>;
    /// відстежувана, <c>null</c> — такої ще немає.
    /// </summary>
    /// <param name="columnDefId">Колонка-приймач.</param>
    /// <param name="methodologyId">Методологія-джерело.</param>
    /// <param name="outputCode">Який саме вихід методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Прив'язка або <c>null</c>.</returns>
    public Task<CalculationBinding?> FindAsync(
        int columnDefId, int methodologyId, string outputCode, CancellationToken ct);

    /// <summary>Усі прив'язки методології, включно з вимкненими.</summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Прив'язки в порядку колонки й коду виходу.</returns>
    /// <remarks>
    /// ⚠ Вимкнені теж: невидима в редакторі прив'язка — це та сама відповідь
    /// «розрахунок нічого не дав», яку неможливо пояснити.
    /// </remarks>
    public Task<IReadOnlyList<CalculationBinding>> ListAsync(
        int methodologyId, CancellationToken ct);

    /// <summary>
    /// Колонка-приймач так, як її бачить прив'язка; <c>null</c> — колонки
    /// немає або її видалено.
    /// </summary>
    /// <param name="columnDefId">Колонка-приймач.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Таблиця, код і тип колонки — або <c>null</c>.</returns>
    /// <remarks>
    /// ⛔ <c>TableDefId</c> прив'язки НЕ приймається ззовні, а виводиться тут.
    /// Обидва поля вже є в рядку, і розійтися вони можуть лише мовчки:
    /// <c>RecalculationJob.BindingsAsync</c> шукає екземпляри таблиці за
    /// <c>TableDefId</c>, а значення кладе в колонку — прив'язка з чужим
    /// <c>TableDefId</c> просто не спрацювала б, не давши жодної помилки.
    ///
    /// ⚠ Метод віддає РЯДОК, а не самий <c>TableDefId</c> (як робив
    /// <c>FindTableOfColumnAsync</c> до <c>ECR-TMPL-4227</c>): тип колонки
    /// потрібен на тому самому шляху й у той самий момент, а другий запит про
    /// ту саму колонку був би другою правдою про неї.
    /// </remarks>
    public Task<BoundColumnRef?> FindColumnAsync(int columnDefId, CancellationToken ct);

    /// <summary>
    /// Коди колонок кожної з названих таблиць — для публікаційної перевірки
    /// «аргумент методології відповідає колонці таблиці, до якої вона
    /// прив'язана» (<c>ECR-CALC-0438</c>).
    /// </summary>
    /// <param name="tableDefIds">Таблиці, чиї колонки цікавлять.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// <c>TableDefId</c> → коди його НЕ видалених колонок; таблиця без жодної
    /// колонки в результаті не з'являється взагалі.
    /// </returns>
    /// <remarks>
    /// ⚠ Видалені колонки виключені з тієї самої причини, що й у
    /// <see cref="FindColumnAsync"/>: <c>CalculationInputBuilder</c>
    /// будує аргументи з живого знімка структури, і м'яко видалену колонку
    /// туди не візьме.
    /// </remarks>
    public Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> ListColumnCodesAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct);

    /// <summary>
    /// Колонки ВЕРСІЇ шаблону, до яких прив'язаний активний вихід методології —
    /// для публікаційної перевірки «обчислювана колонка має джерело»
    /// (<c>ECR-TMPL-4226</c>).
    /// </summary>
    /// <param name="templateVersionId">Версія, що публікується.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатори колонок; порожня множина — прив'язок немає.</returns>
    /// <remarks>
    /// ⛔ Питання ставиться саме ВЕРСІЇ, а не методології: публікація не знає
    /// наперед, які методології хтось прив'язав до її колонок, і перебирати їх
    /// через <see cref="ListAsync"/> означало б спершу дізнатися перелік
    /// методологій — якого нізвідки взяти.
    ///
    /// ⚠ Лише <c>IsActive</c>: вимкнена прив'язка нічого не рахує
    /// (<c>RecalculationJob</c> її не бере), тож для колонки вона — не джерело.
    ///
    /// ⚠ <c>cfg.CalculationBinding</c> не має власного <c>TemplateVersionId</c>:
    /// версія дістається через <c>ColumnDef → TableDef → SheetDef</c> — так
    /// само, як для <c>cfg.TableRelationDef</c>.
    /// </remarks>
    public Task<IReadOnlySet<int>> ListBoundColumnIdsAsync(
        int templateVersionId, CancellationToken ct);

    /// <summary>Ставить прив'язку в чергу на вставку; зберігає <c>IUnitOfWork</c>.</summary>
    /// <param name="binding">Нова прив'язка.</param>
    public void Add(CalculationBinding binding);
}

/// <summary>Колонка-приймач прив'язки: усе, що про неї треба знати на запису.</summary>
/// <param name="TableDefId">Таблиця, якій належить колонка.</param>
/// <param name="Code">Код колонки — для діагностики, яка називає винуватця.</param>
/// <param name="DataType">
/// Тип колонки. Прив'язати вихід методології можна лише до ОБЧИСЛЮВАНОЇ
/// (<c>ColumnDef.IsComputed</c>) — інакше <c>ECR-TMPL-4227</c>.
/// </param>
public sealed record BoundColumnRef(int TableDefId, string Code, Ecr.Domain.Enums.CellDataType DataType);
