// src/Ecr.Application/Ports/IMethodologyStore.cs

using Ecr.Domain.Entities.Calculations;

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

    /// <summary>Речовини версії: для кожної рахуються власні виходи.</summary>
    public Task<IReadOnlyList<MethodologySubstance>> GetSubstancesAsync(int methodologyVersionId, CancellationToken ct);

    /// <summary>Оголошені виходи версії — з обов'язковими одиницями (ФВ-16.6).</summary>
    public Task<IReadOnlyList<MethodologyOutput>> GetOutputsAsync(int methodologyVersionId, CancellationToken ct);

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
}

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
