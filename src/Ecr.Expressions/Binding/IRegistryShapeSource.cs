using Ecr.Domain.Enums;

namespace Ecr.Expressions.Binding;

/// <summary>
/// Форми довідників для статичної перевірки виразів: поля, типи, одиниці,
/// ключі (FEATURE-REGISTRY-TABLES §5.9).
/// </summary>
/// <remarks>
/// ⛔ Форма, а не дані. Перевірки 15–21 (§5.5) питають «чи є такий довідник,
/// поле, ключ і якого вони типу», і жодна не питає «що в записі». Тому
/// джерело форм і знімок даних (<see cref="Evaluation.IRegistrySnapshot"/>) —
/// два різні інтерфейси: публікація й клієнтська перевірка не повинні
/// вантажити тисячі записів, щоб перевірити одну описку в коді поля.
///
/// ⚠ Два джерела, одна форма: у публікації — з БД, у клієнтській перевірці —
/// з <c>GET /expressions/metadata</c> (§5.11). Саме тому тут лише прості
/// записи з кодами, без сутностей домену: обидва джерела мусять віддавати те
/// саме, і сутність із лінивими навігаціями тут була б пасткою.
///
/// ⚠ Коди порівнюються без урахування регістру
/// (<see cref="StringComparer.OrdinalIgnoreCase"/>) — так само, як у знімку даних.
/// </remarks>
public interface IRegistryShapeSource
{
    /// <summary>Форма довідника за кодом.</summary>
    /// <param name="registryCode">Код довідника (рядковий літерал виразу).</param>
    /// <returns>
    /// Форма; <c>null</c> — такого довідника немає (<c>expr.registryUnknown</c>,
    /// перевірка 15).
    /// </returns>
    public RegistryShape? FindRegistry(string registryCode);
}

/// <summary>Форма довідника: поля й активні ключі.</summary>
/// <param name="Code">Код довідника.</param>
/// <param name="Fields">Поля в порядку <c>Ordinal</c>.</param>
/// <param name="Keys">Активні ключі; первинний — не більше одного.</param>
public sealed record RegistryShape(
    string Code,
    IReadOnlyList<RegistryFieldShape> Fields,
    IReadOnlyList<RegistryKeyShape> Keys)
{
    /// <summary>Поле за кодом; <c>null</c> — немає (перевірка 16).</summary>
    /// <param name="fieldCode">Код поля.</param>
    public RegistryFieldShape? FindField(string fieldCode)
        => Fields.FirstOrDefault(f => string.Equals(f.Code, fieldCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Первинний ключ; <c>null</c> — його немає, і <c>REGFIND</c> шукає за
    /// <c>Code</c> запису однією частиною (§5.4).
    /// </summary>
    public RegistryKeyShape? PrimaryKey => Keys.FirstOrDefault(k => k.IsPrimary);
}

/// <summary>Форма поля довідника.</summary>
/// <param name="Code">Код поля.</param>
/// <param name="DataType">
/// Тип поля; від нього статичний тип значення (§5.3): <c>Lookup</c> →
/// <c>EntryRef</c> довідника <paramref name="LookupRegistryCode"/>.
/// </param>
/// <param name="UnitId">Одиниця значення; <c>null</c> — безрозмірне (перевірка 20).</param>
/// <param name="LookupRegistryCode">
/// Ціль <c>Lookup</c>-поля; для решти типів — <c>null</c>. Проміжні сегменти
/// шляху <c>ROW.a.b</c> мусять мати ціль (перевірка 16).
/// </param>
public sealed record RegistryFieldShape(
    string Code,
    CellDataType DataType,
    int? UnitId,
    string? LookupRegistryCode);

/// <summary>Форма ключа довідника.</summary>
/// <param name="Code">Код ключа (<c>cfg.RegistryKeyDef.Code</c>).</param>
/// <param name="IsPrimary">Чи первинний — саме його використовує <c>REGFIND</c>.</param>
/// <param name="FieldCodes">
/// Коди полів ключа в порядку <c>Ordinal</c> — це й порядок частин
/// <c>REGFIND</c> (перевірка 17: кількість і типи частин).
/// </param>
public sealed record RegistryKeyShape(
    string Code,
    bool IsPrimary,
    IReadOnlyList<string> FieldCodes);
