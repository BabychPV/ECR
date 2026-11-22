// src/Ecr.Application/Ports/IMethodologyStore.cs

using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;

namespace Ecr.Application.Ports;

/// <summary>
/// Читання конфігурації методологій зі сховища.
/// </summary>
/// <remarks>
/// ⚠ <b>Порт уведений за рішенням Q-018 (варіант B).</b> До цього
/// <c>Ecr.Calculations.MethodologyResolver</c> був типізований напряму на
/// <c>Ecr.Infrastructure.Persistence.EcrDbContext</c>, чого не передбачає
/// <c>05-skeleton.md</c> §4. Порт лишає <b>логіку</b> підбору версії і
/// зіставлення рядків у <c>Ecr.Calculations</c>, а сховище — в
/// <c>Ecr.Infrastructure</c>: інакше проєкт, у якому живуть числа викидів,
/// неможливо було б протестувати без бази.
/// </remarks>
public interface IMethodologyStore
{
    /// <summary>
    /// Опубліковані версії методології. Вибір чинної на дату робить викликач:
    /// правило «максимальний <c>EffectiveFrom</c> ≤ дата» — це домен, не сховище.
    /// </summary>
    public Task<IReadOnlyList<MethodologyVersion>> GetPublishedVersionsAsync(int methodologyId, CancellationToken ct);

    /// <summary>Активні правила прив'язки версії, впорядковані за <c>Priority</c>.</summary>
    public Task<IReadOnlyList<MethodologyRule>> GetRulesAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>Формули версії в порядку обчислення.</summary>
    public Task<IReadOnlyList<MethodologyFormula>> GetFormulasAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>Константи версії — усі, включно з текстовими й мітками категорій.</summary>
    /// <remarks>
    /// ⛔ Саме всі. Перевірка публікації «мітка категорії у виразі» неможлива,
    /// якщо сховище віддає лише числові: відфільтрувати нечислові тут означало б
    /// зробити помилку, яку ця перевірка ловить, невидимою для неї
    /// (директива ПК-1 №05, поправка 2-біс).
    /// </remarks>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Константи версії.</returns>
    public Task<IReadOnlyList<MethodologyConstant>> GetConstantsAsync(
        int methodologyVersionId, CancellationToken ct);

    /// <summary>
    /// Вираз правила категорії константи версії (L-2, <c>calc.CategoryRule</c>); <c>null</c> — правила
    /// немає, і рушій резолвить константи без категорії, як і до L-2.
    /// </summary>
    /// <remarks>
    /// ⛔ Читається тим самим <c>PrepareAsync</c>, що й константи: раз на прив'язку, а не на рядок.
    /// </remarks>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Текст виразу діалекту Methodology або <c>null</c>.</returns>
    public Task<string?> GetCategoryRuleAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>
    /// Коди переліку категорій констант версії (<c>calc.MethodologyCategory</c>, включно з виведеними з ужитку).
    /// </summary>
    /// <remarks>Читає лише публікація (попередження про літерали правила без категорії); прогін — ні.</remarks>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Коди в порядку переліку.</returns>
    public Task<IReadOnlyList<string>> GetCategoryCodesAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>
    /// Бібліотеки, видимі виразам версії: оголошені імпорти з версією, чинною
    /// **на дату**, і кодами їхніх формул.
    /// </summary>
    /// <remarks>
    /// ⚠ Дата — параметр, а не «сьогодні»: версія бібліотеки вибирається тим
    /// самим правилом «останній <c>EffectiveFrom</c> ≤ дата» (ФВ-9.3), що й
    /// версія будь-якої іншої методології. Інакше перерахунок минулого періоду
    /// брав би сьогоднішню редакцію <c>Common</c>.
    ///
    /// ⚠ Оголошений імпорт без чинної версії на дату повертається з
    /// <c>MethodologyVersionId = null</c>, а не пропускається: «бібліотеки не
    /// видно» і «бібліотеки не оголошено» — різні проблеми, і плутати їх у
    /// повідомленні публікації означає шукати не там.
    /// </remarks>
    /// <param name="methodologyVersionId">Версія, що оголосила імпорти.</param>
    /// <param name="onDate">Дата, на яку добирається версія бібліотеки.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Оголошені імпорти в порядку коду методології.</returns>
    public Task<IReadOnlyList<MethodologyLibrary>> ResolveImportsAsync(
        int methodologyVersionId, DateOnly onDate, CancellationToken ct);

    /// <summary>
    /// Те саме, що <see cref="ResolveImportsAsync"/>, але разом зі СКЛАДОМ обраної версії
    /// кожної бібліотеки: формули, константи й режими (HSE301 L).
    /// </summary>
    /// <remarks>
    /// ⛔ Версія бібліотеки — рівно та, яку дає <see cref="ResolveImportsAsync"/> на ту саму
    /// дату: «остання з <c>EffectiveFrom ≤ дата</c>». Друге правило вибору тут розійшлося б
    /// із публікацією на першому ж перевиданні <c>Common</c>, і прогін рахував би не ту
    /// редакцію, яку перевірив золотий набір.
    ///
    /// ⚠ Лише один рівень — імпорти ЦІЄЇ версії. Транзитивне замикання будує викликач
    /// (<c>MethodologyLibraryClosure</c>): він знає, які формули справді потрібні, і
    /// ходить за імпортами бібліотеки лише тоді, коли потрібна формула туди посилається.
    /// </remarks>
    /// <param name="methodologyVersionId">Версія, що оголосила імпорти.</param>
    /// <param name="onDate">Бізнес-дата: кінець періоду в прогоні, дата чинності в публікації.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Імпорти в порядку коду методології; без чинної версії — з порожнім складом.</returns>
    public Task<IReadOnlyList<MethodologyLibraryContent>> GetLibraryContentsAsync(
        int methodologyVersionId, DateOnly onDate, CancellationToken ct);

    /// <summary>
    /// Замінює ребра <c>calc.MethodologyDependency</c>, що виходять із методології.
    /// </summary>
    /// <remarks>
    /// ⛔ Саме заміна, а не додавання. Прибране з версії посилання має прибрати
    /// й ребро: інакше граф накопичує залежності, яких у виразах уже немає, і
    /// перерахунок щоразу тягне за собою методологію, з якою давно розв'язався.
    ///
    /// ⚠ Ребро будується і для посилань у бібліотеку (поправка 10). ✎ HSE301 L:
    /// записаний результат <c>Common</c> викликач не читає — формулу бібліотеки
    /// модуль обчислює в контексті його рядка, тож на порядок прогону ребро не
    /// впливає. Воно потрібне для інвалідації: перевидана <c>Common</c> мусить
    /// потягнути перерахунок усіх, хто на неї посилається.
    /// </remarks>
    /// <param name="fromMethodologyId">Методологія, що посилається.</param>
    /// <param name="toMethodologyIds">Методології, на які вона посилається.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Задача заміни; збереження робить <c>IUnitOfWork</c>.</returns>
    public Task ReplaceDependenciesAsync(
        int fromMethodologyId, IReadOnlyCollection<int> toMethodologyIds, CancellationToken ct);

    /// <summary>Речовини версії: для кожної рахуються власні виходи.</summary>
    public Task<IReadOnlyList<MethodologySubstance>> GetSubstancesAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>
    /// Коди записів довідника для речовин версії: <c>SubstanceEntryId</c> → <c>RegistryEntry.Code</c>.
    /// </summary>
    /// <remarks>
    /// LP-07, форма B: категорія константи може мати суфікс коду речовини (<c>{ключ}_{код}</c>), і резолвер
    /// спершу пробує його. Речовини без запису в довіднику в словник не потрапляють.
    /// </remarks>
    public Task<IReadOnlyDictionary<long, string>> GetSubstanceCodesAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>Оголошені виходи версії — з обов'язковими одиницями (ФВ-16.6).</summary>
    public Task<IReadOnlyList<MethodologyOutput>> GetOutputsAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>
    /// Текст скрипта версії й усе, що потрібне для його компіляції (MS, план 5.3).
    /// </summary>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// <c>null</c> — версії немає або в неї немає тексту скрипта (порожній текст — теж «немає»).
    /// Текст читається незалежно від <c>Engine</c>: «тіньовий» скрипт формульної версії теж видно.
    /// </returns>
    public Task<MethodologyScript?> GetScriptAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>
    /// Повністю замінює перелік таблиць, які читає скрипт версії (<c>calc.MethodologyReadTable</c>).
    /// </summary>
    /// <param name="versionId">Версія методології.</param>
    /// <param name="tableCodes">Коди таблиць; дублікати без урахування регістру зливаються.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Задача заміни; збереження робить <c>IUnitOfWork</c> — у транзакції матеріалізації.</returns>
    public Task ReplaceScriptReadTablesAsync(int versionId, IReadOnlyList<string> tableCodes, CancellationToken ct);

    /// <summary>
    /// ✎ D-568: повністю замінює перелік полів ВЛАСНОЇ таблиці, які читає скрипт версії (<c>calc.MethodologyReadField</c>),
    /// разом зі службовим рядком «перелік записано».
    /// </summary>
    /// <param name="versionId">Версія методології.</param>
    /// <param name="fields">Поля (<see cref="Calculations.Scripting.ScriptReadFields.Of"/>); дублікати без регістру зливаються.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Задача заміни; збереження робить <c>IUnitOfWork</c> — у транзакції матеріалізації.</returns>
    public Task ReplaceScriptReadFieldsAsync(
        int versionId, IReadOnlyList<Calculations.Scripting.ScriptReadField> fields, CancellationToken ct);

    /// <summary>
    /// Повністю замінює виходи версії-скрипта (<c>calc.MethodologyOutput</c>) — рядок на кожен <c>output</c> (MS, план 5.1).
    /// </summary>
    /// <param name="versionId">Версія методології.</param>
    /// <param name="rows">Виходи; коди без урахування регістру унікальні.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Задача заміни; збереження робить <c>IUnitOfWork</c>.</returns>
    /// <remarks>Рядок, у якого код, одиниця, порядок і ознака «по речовині» не змінилися, лишається як є.</remarks>
    public Task ReplaceScriptOutputsAsync(int versionId, IReadOnlyList<ScriptOutputRow> rows, CancellationToken ct);

    /// <summary>
    /// Повністю замінює речовини версії-скрипта (<c>calc.MethodologySubstance</c>); порядок — порядок переліку.
    /// </summary>
    /// <param name="versionId">Версія методології.</param>
    /// <param name="substanceEntryIds">Id записів довідника речовин; дублікати зливаються.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Задача заміни; збереження робить <c>IUnitOfWork</c>.</returns>
    public Task ReplaceScriptSubstancesAsync(int versionId, IReadOnlyList<long> substanceEntryIds, CancellationToken ct);

    /// <summary>
    /// Повністю замінює імпорти версії-скрипта (<c>calc.MethodologyImport</c>).
    /// </summary>
    /// <param name="versionId">Версія методології-власника.</param>
    /// <param name="importedMethodologyIds">Імпортовані методології (бібліотеки); дублікати зливаються.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Задача заміни; збереження робить <c>IUnitOfWork</c>.</returns>
    public Task ReplaceScriptImportsAsync(int versionId, IReadOnlyList<int> importedMethodologyIds, CancellationToken ct);

    /// <summary>
    /// Id записів довідника речовин за кодами речовин (те саме джерело, що <see cref="GetSubstanceCodesAsync"/>).
    /// </summary>
    /// <param name="codes">Коди речовин («301», «NOx»).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Код (як у запиті) → Id запису; невідомих кодів у словнику немає.</returns>
    /// <remarks>
    /// Код речовини — значення поля <c>SUBSTANCE_CODE</c> запису довідника, інакше код самого запису; шукаються лише
    /// записи довідників, що мають таке поле. Збіг неточний за регістром, якщо так порівнює база. Кілька записів з одним
    /// кодом — береться запис із найменшим Id.
    /// </remarks>
    public Task<IReadOnlyDictionary<string, long>> FindSubstanceEntryIdsAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct);

    /// <summary>
    /// «Живі» таблиці з цим кодом: таблиця останньої опублікованої версії кожного шаблону, разом із колонками й
    /// псевдонімами місячних входів.
    /// </summary>
    /// <param name="tableCode">Код таблиці.</param>
    /// <param name="templateCode">Код шаблону; <c>null</c> — будь-який шаблон.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Таблиці за кодом шаблону; порожньо — такої таблиці немає ні в одному опублікованому шаблоні.</returns>
    public Task<IReadOnlyList<LiveTable>> FindLiveTablesByCodeAsync(
        string tableCode, string? templateCode, CancellationToken ct);

    /// <summary>
    /// Залежності методологій від методологій-джерел (<c>calc.MethodologyDependency</c>): «хто → від кого».
    /// </summary>
    /// <param name="fromMethodologyIds">Методології, чиї ребра потрібні.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Методологія → ідентифікатори тих, від кого вона залежить; методології без ребер у словнику немає.</returns>
    public Task<IReadOnlyDictionary<int, IReadOnlyList<int>>> GetMethodologyDependenciesAsync(
        IReadOnlyCollection<int> fromMethodologyIds, CancellationToken ct);

    /// <summary>
    /// Методології, що мають ОПУБЛІКОВАНУ або ВИВЕДЕНУ З ОБІГУ версію, яка читає бодай одну з
    /// названих таблиць.
    /// </summary>
    /// <param name="tableCodes">Коди таблиць (порівняння без урахування регістру).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Різні ідентифікатори методологій за зростанням; порожньо — ніхто не читає.</returns>
    /// <remarks>
    /// ⚠ Для перерахунку і позначки «результати застаріли»: правка в чужій таблиці-джерелі
    /// мусить потягнути методології-читачі. Чернетки не враховуються — вони нічого не рахували.
    /// </remarks>
    public Task<IReadOnlyList<int>> GetMethodologyIdsReadingTablesAsync(
        IReadOnlyCollection<string> tableCodes, CancellationToken ct);

    /// <summary>
    /// Обов'язкові вхідні колонки версії — gate перед збереженням клітинки
    /// (директива «обов'язкові вхідні колонки методології»).
    /// </summary>
    public Task<IReadOnlyList<MethodologyRequiredInput>> GetRequiredInputsAsync(
        int methodologyVersionId, CancellationToken ct);

    /// <summary>
    /// <see cref="GetPublishedVersionsAsync(int, CancellationToken)"/> для кількох методологій — одним зверненням.
    /// </summary>
    /// <remarks>
    /// ⚠ Потрібно gate-у обов'язкових входів <c>PatchCellsHandler</c>: там версії питались поштучно по кожній
    /// прив'язаній методології на КОЖЕН запис комірки (Land, T3_00: 5 методологій × 3 запити). Вибір чинної на
    /// дату робить викликач, як і для поштучного методу.
    /// </remarks>
    /// <param name="methodologyIds">Методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Опубліковані версії всіх названих методологій (групує викликач за <c>MethodologyId</c>).</returns>
    public Task<IReadOnlyList<MethodologyVersion>> GetPublishedVersionsOfAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct);

    /// <summary>
    /// <see cref="GetRulesAsync(int, CancellationToken)"/> для кількох версій — одним зверненням.
    /// </summary>
    /// <param name="methodologyVersionIds">Версії методологій.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Активні правила, впорядковані за версією, далі за <c>Priority</c> і <c>Id</c> — у межах версії той самий
    /// порядок «перший збіг виграє», що й у поштучного методу.
    /// </returns>
    public Task<IReadOnlyList<MethodologyRule>> GetRulesOfVersionsAsync(
        IReadOnlyCollection<int> methodologyVersionIds, CancellationToken ct);

    /// <summary>
    /// <see cref="GetRequiredInputsAsync(int, CancellationToken)"/> для кількох версій — одним зверненням.
    /// </summary>
    /// <param name="methodologyVersionIds">Версії методологій.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Обов'язкові входи, впорядковані за версією, далі за <c>Id</c>.</returns>
    public Task<IReadOnlyList<MethodologyRequiredInput>> GetRequiredInputsOfVersionsAsync(
        IReadOnlyCollection<int> methodologyVersionIds, CancellationToken ct);

    /// <summary>
    /// Методології, активно прив'язані бодай однією колонкою до цієї таблиці
    /// (<c>cfg.CalculationBinding</c>).
    /// </summary>
    /// <remarks>
    /// ⚠ Потрібно gate-у обов'язкових вхідних колонок у
    /// <c>PatchCellsHandler</c>: щоб перевірити, чи в рядка є визначена
    /// методологія, спершу треба знати, яка методологія взагалі МОЖЕ
    /// стосуватися цієї таблиці — так само, як <c>RecalculationJob</c> це
    /// робить перед прогоном, лише прицільно по одній таблиці, а не по
    /// всьому документу.
    /// </remarks>
    public Task<IReadOnlyList<int>> GetMethodologyIdsBoundToTableAsync(
        int tableDefId, CancellationToken ct);

    /// <summary>
    /// Методології, активно прив'язані бодай однією колонкою до БУДЬ-ЯКОЇ з
    /// названих таблиць (<c>cfg.CalculationBinding</c>) — одним зверненням.
    /// </summary>
    /// <param name="tableDefIds">Таблиці (наприклад, усі таблиці аркуша).</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>
    /// Різні ідентифікатори за зростанням — об'єднання того, що дав би
    /// <see cref="GetMethodologyIdsBoundToTableAsync"/> по кожній таблиці;
    /// порожньо — жодна з таблиць не прив'язана.
    /// </returns>
    /// <remarks>
    /// ⚠ Потрібно поданню аркуша (<c>SubmitSheetHandler</c>): прив'язки питались
    /// поштучно по таблицях аркуша під винятковим блокуванням подання — N
    /// звернень на аркуш із N таблиць (<c>SubmitSheetQueryCountTests</c>).
    /// </remarks>
    public Task<IReadOnlyList<int>> GetMethodologyIdsBoundToTablesAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct);

    /// <summary>
    /// Активні прив'язки виходів методологій до колонок названих таблиць —
    /// разом з усіма версіями кожної методології (F-02).
    /// </summary>
    /// <param name="tableDefIds">Таблиці документа, що читаються.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Прив'язки; порожньо — жодна колонка цих таблиць не отримує результату.</returns>
    public Task<IReadOnlyList<ColumnResultBinding>> GetColumnResultBindingsAsync(
        IReadOnlyCollection<int> tableDefIds, CancellationToken ct);

    /// <summary>
    /// Чи змінилися входи документа після прогону, що дав його актуальні числа (F-05).
    /// </summary>
    /// <param name="documentId">Документ.</param>
    /// <param name="periodKey">Період.</param>
    /// <param name="tableDefIds">
    /// Лише зміни входів у колонках цих таблиць рахуються зміною входів;
    /// <c>null</c> — увесь документ (дисплей F-05). Порожній перелік — жодна
    /// зміна не рахується. ⚠ Потрібно поданню аркуша: застарілість прив'язаної
    /// таблиці ІНШОГО аркуша того самого документа+періоду не мусить блокувати
    /// цей аркуш.
    /// </param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Свіжість; <c>CalculatedAt = null</c> — актуальних чисел немає.</returns>
    public Task<CalculationFreshness> GetCalculationFreshnessAsync(
        long documentId, int periodKey, IReadOnlyCollection<int>? tableDefIds, CancellationToken ct);

    /// <summary>Код методології й номер версії — для підпису числа (F-21).</summary>
    /// <param name="methodologyVersionIds">Версії.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Підписи; невідомої версії в результаті немає.</returns>
    public Task<IReadOnlyDictionary<int, MethodologyVersionLabel>> GetVersionLabelsAsync(
        IReadOnlyCollection<int> methodologyVersionIds, CancellationToken ct);

    /// <summary>Журнал публікацій версій методології, найновіші першими (F-16).</summary>
    /// <param name="methodologyId">Методологія.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Записи журналу.</returns>
    public Task<IReadOnlyList<MethodologyPublicationEntry>> ListPublicationsAsync(
        int methodologyId, CancellationToken ct);

    /// <summary>
    /// Методологія-контейнер разом з усіма своїми версіями; <c>null</c> — версії немає.
    /// </summary>
    /// <remarks>
    /// ⚠ Саме агрегат, а не окрема версія: перевірку «вікна дії не
    /// перетинаються» неможливо зробити, не бачачи сусідів (ФВ-13.3), а
    /// збирати їх у застосунку означало б повторити правило вибору версії
    /// втретє.
    /// </remarks>
    public Task<Methodology?> FindByVersionAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>
    /// Золотий набір версії: входи з очікуваними числами і допуском (ФВ-13.7).
    /// </summary>
    /// <remarks>
    /// ⛔ Порожній набір означає «зеленого тесту немає», і публікація
    /// відхиляється (ФВ-9.12). Це не формальність: без очікуваних чисел
    /// правильність результату перевіряє той, хто відкриє звіт — тобто вже
    /// після того, як його подали.
    /// </remarks>
    public Task<IReadOnlyList<MethodologyTestCase>> GetTestCasesAsync(
        int methodologyVersionId, CancellationToken ct);

    /// <summary>
    /// Таблиця символів версії: на що взагалі можуть посилатися її вирази.
    /// </summary>
    /// <remarks>
    /// ⚠ Один метод на три переліки, бо це один предмет: `CST.`, `!` і `@` —
    /// три способи назвати щось, що ІСНУЄ в цій версії, і питання «що тут
    /// можна написати» має один відповідач. Три окремі виклики означали б три
    /// походи в базу на кожне відкриття редактора (`ФВ-9.15a`).
    ///
    /// ⛔ Аргументи (`@`) — це коди КОЛОНОК таблиці, до якої прив'язана
    /// методологія (`CalculationInputBuilder`: «ім'я аргументу — це код
    /// колонки»). Тому їх не можна перелічити з самої методології: потрібна
    /// прив'язка `cfg.CalculationBinding`. Без неї перелік порожній — і це
    /// правда про стан, а не збій: непри'вязана методологія справді не має
    /// аргументів, які можна назвати.
    /// </remarks>
    /// <param name="methodologyVersionId">Версія методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Константи, формули й аргументи версії.</returns>
    public Task<MethodologySymbols> GetSymbolsAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>
    /// Чи має хоч одна з версій хоч один рядок результату — у <c>calc.CalculationResult</c>
    /// або в архіві <c>arc.CalculationResult</c>.
    /// </summary>
    /// <remarks>
    /// ✎ <c>D-560</c>: у <c>calc.CalculationResult</c> лишаються рядки лише актуальних (і ще працюючих) прогонів —
    /// витіснені й невдалі прибираються, тож версія, порахована лише в витіснених прогонах, після прибирання
    /// «не використана» (гард <c>strictBackdated</c> і гард видалення версії її не бачать; рішення людини, D-460/D-560).
    /// </remarks>
    /// <remarks>
    /// ⚠ Єдиний слід «версією щось порахували»: зрізи подання (<c>SubmissionSnapshot</c>) версій
    /// методологій не фіксують (Q-155), тож використання версії видно лише з результатів.
    /// Потрібне гарду <c>strictBackdated</c> (ФВ-9.9): заднє число небезпечне, лише якщо є
    /// порахований результат, який воно змінить.
    /// </remarks>
    /// <param name="methodologyVersionIds">Версії методології.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns><c>true</c>, якщо хоч для однієї з версій є результат.</returns>
    public Task<bool> AnyCalculationResultsAsync(
        IReadOnlyCollection<int> methodologyVersionIds, CancellationToken ct);

    /// <summary>
    /// ✎ D-565 (PERF-03): які з названих методологій мають увімкнений прапор «трасувати проміжні кроки»
    /// (<c>calc.Methodology.TraceSteps</c>) — одним зверненням на прогін.
    /// </summary>
    /// <param name="methodologyIds">Методології прогону.</param>
    /// <param name="ct">Токен скасування.</param>
    /// <returns>Ідентифікатори методологій із прапором; порожньо — жодна кроків не пише.</returns>
    public Task<IReadOnlyList<int>> GetTraceStepMethodologyIdsAsync(
        IReadOnlyCollection<int> methodologyIds, CancellationToken ct);
}

/// <summary>
/// Оголошений імпорт, розв'язаний на дату: чиї формули видно і які саме.
/// </summary>
/// <remarks>
/// ⚠ Тип називає бібліотекою будь-яку імпортовану методологію, а не лише
/// <c>Kind = Library</c>: <c>ECW_C09_02_01</c> — звичайна <c>DataDriven</c>
/// методологія з однією формулою, на яку посилаються п'ять інших, і жодних
/// воріт для неї не існує (директива ПК-1 №05, поправка 6).
/// </remarks>
/// <param name="MethodologyId">Імпортована методологія.</param>
/// <param name="MethodologyCode">Її код — те, що потрапляє в повідомлення про неоднозначність.</param>
/// <param name="MethodologyVersionId">
/// Версія, чинна на дату; <c>null</c> — на цю дату жодна не чинна.
/// </param>
/// <param name="FormulaCodes">Коди формул цієї версії — те, на що можна послатися.</param>
public sealed record MethodologyLibrary(
    int MethodologyId,
    string MethodologyCode,
    int? MethodologyVersionId,
    IReadOnlyList<string> FormulaCodes);

/// <summary>Скрипт версії методології з режимами, потрібними для компіляції (MS, план 5.3).</summary>
/// <param name="Text">Текст скрипта.</param>
/// <param name="Hash">SHA-256 тексту; <c>null</c> — ще не порахований.</param>
/// <param name="Engine">Рушій версії.</param>
/// <param name="IsLibrary">Методологія має вид <c>Library</c>.</param>
/// <param name="NumericMode">Арифметичний режим версії.</param>
public sealed record MethodologyScript(
    string Text,
    byte[]? Hash,
    MethodologyEngine Engine,
    bool IsLibrary,
    NumericMode NumericMode);

/// <summary>
/// Імпорт, розв'язаний на дату, разом зі складом обраної версії бібліотеки (HSE301 L).
/// </summary>
/// <param name="Library">Імпорт: методологія, обрана версія, коди формул.</param>
/// <param name="NumericMode">Арифметика обраної версії; <c>null</c> — версії немає.</param>
/// <param name="CalendarMode">Календар обраної версії; <c>null</c> — версії немає.</param>
/// <param name="Formulas">Формули обраної версії в порядку <c>EvaluationOrder</c>.</param>
/// <param name="Constants">Усі константи обраної версії — кандидати для вибору на речовину й дату.</param>
public sealed record MethodologyLibraryContent(
    MethodologyLibrary Library,
    Domain.Enums.NumericMode? NumericMode,
    Domain.Enums.CalendarMode? CalendarMode,
    IReadOnlyList<MethodologyFormula> Formulas,
    IReadOnlyList<MethodologyConstant> Constants);

/// <summary>Символи, видимі виразам версії методології.</summary>
/// <param name="Constants">Константи — префікс <c>CST.</c>.</param>
/// <param name="Formulas">Формули тієї самої версії — префікс <c>!</c>.</param>
/// <param name="Arguments">Поля рядка джерела — префікс <c>@</c>.</param>
public sealed record MethodologySymbols(
    IReadOnlyList<MethodologySymbol> Constants,
    IReadOnlyList<MethodologySymbol> Formulas,
    IReadOnlyList<MethodologySymbol> Arguments);

/// <summary>Один символ: ім'я, одиниця і пояснення.</summary>
/// <remarks>
/// ⚠ Одиниця віддається ІДЕНТИФІКАТОРОМ, а не позначенням. Розв'язати його в
/// текст — робота застосунку, у якого є каталог одиниць; сховище, яке саме
/// підставляє позначення, стало б другим місцем, де живе це знання.
/// </remarks>
/// <param name="Name">Ім'я символу без префікса.</param>
/// <param name="UnitId">Одиниця; <c>null</c> — безрозмірний або невідомо.</param>
/// <param name="Note">Коротке пояснення: категорія, тип даних.</param>
public sealed record MethodologySymbol(string Name, int? UnitId, string? Note);

/// <summary>
/// Тест методології: вхід, очікувані виходи і допуск.
/// </summary>
/// <remarks>
/// ⚠ Тип оголошений тут, а не як сутність, бо таблиці <c>calc.TestCase</c> у
/// схемі **немає** — при тому, що ФВ-13.7 прямо на неї посилається. Розбіжність
/// записана як <c>P-08</c>; порт віддає ту форму, яка потрібна публікації, і
/// зміна сховища її не зачепить.
/// </remarks>
/// <param name="Code">Код тесту — те, що потрапляє в повідомлення про провал.</param>
/// <param name="Input">Вхід розрахунку.</param>
/// <param name="Expected">Очікувані значення: код виходу → число.</param>
/// <param name="Tolerance">Допуск порівняння; нуль означає точний збіг.</param>
public sealed record MethodologyTestCase(
    string Code,
    CalculationInput Input,
    IReadOnlyDictionary<string, decimal> Expected,
    decimal Tolerance);

/// <summary>
/// Активна прив'язка виходу методології до колонки — у формі, потрібній
/// накладанню результатів на документ (F-02).
/// </summary>
/// <param name="TableDefId">Таблиця колонки.</param>
/// <param name="ColumnDefId">Колонка-приймач.</param>
/// <param name="MethodologyId">Методологія-джерело.</param>
/// <param name="OutputCode">Вихід.</param>
/// <param name="MatchJson">Предикат рядків; <c>{}</c> — уся таблиця.</param>
/// <param name="VersionIds">
/// Усі версії методології — будь-яка з них могла дати число цього періоду
/// (версія резолвиться за датою періоду, ФВ-9.3).
/// </param>
public sealed record ColumnResultBinding(
    int TableDefId,
    int ColumnDefId,
    int MethodologyId,
    string OutputCode,
    string MatchJson,
    IReadOnlyList<int> VersionIds);

/// <summary>Свіжість результатів методологій документа за період (F-05).</summary>
/// <param name="CalculatedAt">Коли завершився прогін, що дав актуальні числа.</param>
/// <param name="InputsChangedAt">
/// Остання зміна ВХОДІВ (ручний запис, імпорт) або довідника, який читають методології цього
/// документа, після початку цього прогону (RT-25); <c>null</c> — числа відповідають даним.
/// </param>
/// <param name="ChangedRegistries">
/// Коди довідників, змінених після прогону (RT-25, ФВ-9.19); порожньо — правка довідників не причина.
/// </param>
public sealed record CalculationFreshness(
    DateTime? CalculatedAt, DateTime? InputsChangedAt, IReadOnlyList<string>? ChangedRegistries = null)
{
    /// <summary>Довідники, змінені після прогону; ніколи не <c>null</c>.</summary>
    public IReadOnlyList<string> ChangedRegistryCodes => ChangedRegistries ?? [];

    /// <summary>Чи змінилися входи після розрахунку — числа застарілі.</summary>
    public bool IsStale => InputsChangedAt is not null;
}

/// <summary>Підпис версії методології для людини: код методології й номер версії (F-21).</summary>
/// <param name="MethodologyVersionId">Версія.</param>
/// <param name="MethodologyCode">Код методології.</param>
/// <param name="Version">Номер версії («1.2.0»).</param>
/// <param name="MethodologyNameL10n">Назва методології мовами каталогу; <c>null</c> — не прочитана (UAT 08.11 b3: людині — назва, не код).</param>
public sealed record MethodologyVersionLabel(
    int MethodologyVersionId, string MethodologyCode, string Version, Ecr.Domain.ValueObjects.LocalizedText? MethodologyNameL10n = null);

/// <summary>Одна публікація версії методології з журналу <c>aud.PublicationEvent</c> (F-16).</summary>
/// <param name="Id">Запис журналу.</param>
/// <param name="ChangedAt">Коли опубліковано (UTC).</param>
/// <param name="MethodologyVersionId">Опублікована версія.</param>
/// <param name="Version">Номер версії.</param>
/// <param name="ChangeReason">Причина зміни (ФВ-14.7).</param>
/// <param name="ChangedByUserId">Хто опублікував.</param>
/// <param name="ChangedByName">Ім'я того, хто опублікував; <c>null</c> — облікового запису вже немає.</param>
/// <param name="ResultDiffJson">Diff результатів на золотому наборі (ФВ-9.6), як записано.</param>
public sealed record MethodologyPublicationEntry(
    long Id,
    DateTime ChangedAt,
    int MethodologyVersionId,
    string Version,
    string? ChangeReason,
    int ChangedByUserId,
    string? ChangedByName,
    string? ResultDiffJson);

/// <summary>Вихід версії-скрипта для матеріалізації (MS, план 5.3).</summary>
/// <param name="Code">Код виходу.</param>
/// <param name="UnitId">Одиниця виходу.</param>
/// <param name="Ordinal">Порядок у тексті з 1.</param>
/// <param name="IsPerSubstance">Вихід по речовині.</param>
public sealed record ScriptOutputRow(string Code, int UnitId, int Ordinal, bool IsPerSubstance);

/// <summary>Колонка «живої» таблиці.</summary>
/// <param name="Id">Ідентифікатор колонки.</param>
/// <param name="Code">Код колонки.</param>
/// <param name="DataType">Тип даних.</param>
/// <param name="IsMonthColumn">Місячна колонка.</param>
/// <param name="MonthNumber">Номер місяця 1..12 для місячної колонки.</param>
/// <param name="UnitId">Одиниця колонки.</param>
public sealed record LiveColumn(
    int Id, string Code, Ecr.Domain.Enums.CellDataType DataType, bool IsMonthColumn, byte? MonthNumber, int? UnitId);

/// <summary>Таблиця з останньої опублікованої версії шаблону (MS, план 5.3).</summary>
/// <param name="TemplateId">Шаблон.</param>
/// <param name="TemplateCode">Код шаблону.</param>
/// <param name="TemplateVersionId">Остання опублікована версія шаблону.</param>
/// <param name="TableDefId">Таблиця.</param>
/// <param name="TableCode">Код таблиці.</param>
/// <param name="Columns">Колонки (без видалених).</param>
/// <param name="MonthAliases">Псевдоніми місячних входів таблиці (<c>cfg.MonthInputAlias</c>), без повторів.</param>
public sealed record LiveTable(
    int TemplateId,
    string TemplateCode,
    int TemplateVersionId,
    int TableDefId,
    string TableCode,
    IReadOnlyList<LiveColumn> Columns,
    IReadOnlyList<string> MonthAliases);
