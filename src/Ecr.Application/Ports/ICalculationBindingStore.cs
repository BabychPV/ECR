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
    /// Код колонки → код довідника, на який вона вказує (<c>Lookup</c>), у названих таблицях — для
    /// типізації аргументів <c>@Col</c> у перевірках довідників публікації (RC14, L2-4/P3).
    /// </summary>
    /// <param name="tableDefIds">Таблиці прив'язки методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Лише однозначні відповіді: код, що веде на різні довідники в різних таблицях, пропущено.</returns>
    public Task<IReadOnlyDictionary<string, string>> ListLookupRegistryCodesAsync(
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

    /// <summary>
    /// Активні прив'язки колонок ВЕРСІЇ шаблону до методологій, що не мають жодної
    /// опублікованої версії — для публікаційної перевірки (D-R2, <c>ECR-CALC-0422</c>).
    /// </summary>
    /// <param name="templateVersionId">Версія, що публікується.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Прив'язки-порушники; порожній перелік — усе гаразд.</returns>
    /// <remarks>
    /// ⛔ Така прив'язка валить ВЕСЬ перерахунок документа
    /// (<c>MethodologyResolver.ResolveVersionAsync</c>: «bound to a table but has no
    /// published version»), разом з методологіями, що опубліковані. Лише активні:
    /// вимкнена прив'язка нічого не рахує (<c>RecalculationJob</c> її не бере), тож
    /// вимкнення — спосіб зняти відмову. Видалені колонки не рахуються.
    /// </remarks>
    public Task<IReadOnlyList<UnpublishedMethodologyBinding>> ListBindingsToUnpublishedMethodologiesAsync(
        int templateVersionId, CancellationToken ct);

    /// <summary>
    /// Код виходу методології → масштаб колонки, у яку цей вихід потрапляє
    /// (<c>cfg.ColumnDef.Scale</c>).
    /// </summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Коди активно прив'язаних виходів; значення <c>null</c> — колонка
    /// масштабу не оголошує. Виходу без жодної активної прив'язки в результаті
    /// немає взагалі.
    /// </returns>
    /// <remarks>
    /// ⚠ Питання ставиться саме сховищу, а не знімку метаданих: скільки знаків
    /// несе результат — це конфігурація колонки-приймача (рішення людини
    /// 2026-09-20), а зв'язок «вихід → колонка» живе лише в
    /// <c>cfg.CalculationBinding</c>, якого в <c>TemplateVersionSnapshot</c>
    /// немає.
    ///
    /// ⛔ Один вихід може бути прив'язаний до КІЛЬКОХ колонок (різні таблиці,
    /// різні шаблони). Округлювати за найвужчою з них означало б, що колонка з
    /// двома знаками мовчки ріже число і для тієї, яка просила шістнадцять, —
    /// тому береться найширший масштаб, а колонка без оголошеного масштабу
    /// (тобто «усі знаки») перемагає будь-яке число.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, byte?>> ListOutputScalesAsync(
        int methodologyId, CancellationToken ct);

    /// <summary>
    /// Код і назва кожної з названих таблиць — щоб перелік прив'язок показував
    /// таблицю людською назвою, а не сирим <c>TableDefId</c>.
    /// </summary>
    /// <param name="tableDefIds">Таблиці прив'язок.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>TableDefId</c> → код і назва; невідомої таблиці немає в результаті.</returns>
    /// <remarks>Один запит на весь перелік, не по запиту на прив'язку.</remarks>
    public Task<IReadOnlyDictionary<int, BoundTableName>> ListTableNamesAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct);

    /// <summary>
    /// Довідники <c>Lookup</c>-колонок таблиць, до яких методологію активно прив'язано, —
    /// звідки беруться записи аргументів <c>@Arg</c> (аудит L7-06).
    /// </summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатори довідників без повторів; порожньо — підстановок немає.</returns>
    /// <remarks>
    /// ⛔ <c>REGFIELD(@Stream, 'NAME')</c> коду довідника літералом не має: без цього
    /// переліку знімок прив'язки його не містив, і кожен рядок давав <c>#REF</c>.
    /// </remarks>
    public Task<IReadOnlyList<int>> ListLookupRegistryIdsAsync(int methodologyId, CancellationToken ct);

    /// <summary>Усі активні прив'язки колонок ВЕРСІЇ шаблону (P2-1): для перевірки дубля при публікації.</summary>
    /// <remarks>
    /// Перевірка лише на публікації, не на PUT: заміна джерела робиться двома PUT («прив'язати нове →
    /// відв'язати старе», D-215), і між ними дубль законний.
    /// </remarks>
    /// <param name="templateVersionId">Версія, що публікується.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Прив'язки за зростанням <c>Id</c>; видалені колонки не рахуються.</returns>
    public Task<IReadOnlyList<ActiveColumnBinding>> ListActiveBindingsAsync(int templateVersionId, CancellationToken ct);

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

/// <summary>Активна прив'язка колонки до методології без опублікованої версії.</summary>
/// <param name="MethodologyId">Методологія.</param>
/// <param name="MethodologyCode">Її код — для відмови, що називає винуватця.</param>
/// <param name="TableCode">Код таблиці колонки.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="OutputCode">Прив'язаний вихід.</param>
public sealed record UnpublishedMethodologyBinding(
    int MethodologyId, string MethodologyCode, string TableCode, string ColumnCode, string OutputCode);

/// <summary>Активна прив'язка колонки версії шаблону — для перевірки дубля на публікації.</summary>
/// <param name="ColumnDefId">Колонка-приймач.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="ColumnCode">Код колонки.</param>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="OutputCode">Вихід.</param>
/// <param name="MatchJson">Предикат рядків.</param>
/// <param name="HasSelectionRules">
/// Опублікована версія методології має активні <c>MethodologyRule</c> або <c>MethodologyCategoryRule</c>:
/// вибір методології для рядка робиться правилами (RC14, Land).
/// </param>
public sealed record ActiveColumnBinding(
    int ColumnDefId, string TableCode, string ColumnCode, string MethodologyCode, string OutputCode, string MatchJson,
    bool HasSelectionRules = false);

/// <summary>Таблиця прив'язки так, як її називає конфігуратор.</summary>
/// <param name="Code">Код таблиці.</param>
/// <param name="NameL10n">Назва таблиці мовами каталогу.</param>
public sealed record BoundTableName(string Code, Ecr.Domain.ValueObjects.LocalizedText NameL10n);
