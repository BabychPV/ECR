// src/Ecr.Application/Documents/Dto/TableSliceDto.cs
using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Documents.Dto;

/// <summary>
/// Зріз таблиці для grid. Порожні комірки не передаються — клієнт бере
/// <c>DefaultValue</c> з опису колонки (ФВ-3.8).
/// Бюджет усієї операції: p95 1.5 с на 500×60 (tz/08 §8.2).
/// </summary>
/// <param name="TableInstanceId">Екземпляр таблиці.</param>
/// <param name="PeriodKey">Період екземпляра.</param>
/// <param name="Columns">Опис колонок таблиці.</param>
/// <param name="Rows">Рядки зі значеннями.</param>
/// <param name="CellPermissions">
/// Компактна мапа заборон: ключ — <c>"{rowKey}:{columnCode}"</c>, значення —
/// назва <see cref="Ecr.Domain.Enums.EditDenyReason"/>. Комірка, якої тут
/// немає, дозволена.
/// </param>
/// <param name="CellConfirmations">
/// Комірки, дозволені лише після ЯВНОГО підтвердження оператора
/// (<c>ФВ-2.16</c>, <c>AllowWithConfirmation</c>, <c>#43</c>). Ключ — той
/// самий формат, що й у <see cref="CellPermissions"/>
/// (<c>"{rowKey}:{columnCode}"</c>); значення — пояснення для діалогу
/// підтвердження. Комірка, якої тут немає, підтвердження не потребує.
/// </param>
/// <param name="CellFormats">
/// Умовне форматування (ФВ-2.6/2.7): ключ — той самий
/// <c>"{rowKey}:{columnCode}"</c>; значення — колір/жирність першого
/// спрацьованого правила колонки. Комірки, якої тут немає, правила не
/// зачіпають. Обчислено сервером тією самою функцією, що й Excel-експорт.
/// </param>
/// <param name="OutOfWindowCells">
/// Комірки, чия остання зміна — правка за політикою <c>Warn</c> поза вікном
/// доступу (<c>ФВ-2.16</c>, <c>D-239</c>); формат — той самий
/// <c>"{rowKey}:{columnCode}"</c>, що й <c>PatchCellsResponse.OutOfWindow</c>.
/// Порожній — таких немає. Читається з <c>aud.CellChange.IsOutOfWindow</c>
/// одним запитом на зріз, тож значок на сітці переживає перезавантаження.
/// </param>
/// <param name="CellStyles">
/// Користувацьке оформлення комірок (заливка, колір тексту, жирність;
/// <c>PATCH …/tables/{id}/cell-styles</c>): ключ — той самий
/// <c>"{rowKey}:{columnCode}"</c>; значення — <see cref="Ecr.Application.Templates.CellFormatDto"/>
/// (<c>BackgroundHex</c>/<c>ForegroundHex</c> у формі <c>#rrggbb</c>, <c>null</c> — поле не задано).
/// Лише комірки з оформленням і лише ВИДИМІ читачеві колонки (нерозкриття: схованої колонки
/// тут немає, як і в <see cref="Columns"/>). Шар у сітці — над стилем колонки, під умовним
/// форматуванням (<see cref="CellFormats"/>). <c>null</c>/порожній — оформлення немає.
/// </param>
/// <param name="HeaderStyle">
/// Стиль шапки таблиці, заданий автором шаблону (<c>TableDef.HeaderStyleId</c> → <c>StyleDef</c>); <c>null</c> —
/// стиль не задано (або посилання висить). Це дані самої таблиці: таблиця під забороною/схована
/// не віддає зрізу взагалі (404), тож стиль шапки окремо розкрити не може.
/// </param>
/// <param name="StyleAccess">
/// «Можна фарбувати» (CELL-STYLE P1A): те саме рішення, що приймає <c>PATCH …/cell-styles</c>; див.
/// <c>CellStyleAccessDto</c>. <c>null</c> — лише в тестах, що конструюють відповідь вручну.
/// </param>
public sealed record TableSliceDto(
    long TableInstanceId,
    int PeriodKey,
    IReadOnlyList<ColumnDto> Columns,
    IReadOnlyList<RowDto> Rows,
    IReadOnlyDictionary<string, string> CellPermissions,
    IReadOnlyDictionary<string, string> CellConfirmations,
    IReadOnlyDictionary<string, Ecr.Application.Templates.CellFormatDto>? CellFormats = null,
    IReadOnlyList<string>? OutOfWindowCells = null,
    IReadOnlyDictionary<string, Ecr.Application.Templates.CellFormatDto>? CellStyles = null,
    CellStyleDto? HeaderStyle = null,
    CellStyleAccessDto? StyleAccess = null)
{
    /// <summary>
    /// Групи заголовків видимих колонок (плоский список). Лише групи, у піддереві яких є хоч одна видима
    /// колонка зрізу (прихована/заборонена колонка випадає зі спану, група без видимих — не віддається).
    /// Порожній — груп немає.
    /// </summary>
    public IReadOnlyList<ColumnHeaderGroupDto> HeaderGroups { get; init; } = [];

    /// <summary>Максимальна глибина дерева груп (3).</summary>
    public int MaxHeaderGroupDepth { get; init; } = ColumnHeaderGroupDef.MaxDepth;
}

/// <summary>Група заголовків у зрізі: підпис уже обрано за мовою запиту (fallback — як у заголовка колонки).</summary>
/// <param name="Id">Ідентифікатор групи.</param>
/// <param name="Code">Код групи.</param>
/// <param name="ParentId">Батько; <c>null</c> — корінь.</param>
/// <param name="Ordinal">Порядок серед сусідів.</param>
/// <param name="Label">Підпис.</param>
public sealed record ColumnHeaderGroupDto(int Id, string Code, int? ParentId, int Ordinal, string Label);

/// <summary>
/// Чи можна фарбувати комірки таблиці (CELL-STYLE P1A): те, що клієнт має знати, щоб не вмикати «Заливку»/«Колір
/// тексту» там, де <c>PATCH …/cell-styles</c> однаково відмовить 403.
/// </summary>
/// <param name="CanStyle">
/// <c>true</c> — хоч одну ВИДИМУ комірку зрізу можна фарбувати (те саме рішення, що приймає запис). Ознаки колонки
/// «обчислювана»/«лише читання» тут НЕ заважають: колір — оформлення, а не дані.
/// </param>
/// <param name="DeniedReason">
/// Коли <c>canStyle</c> = <c>false</c>: стабільне ім'я <c>EditDenyReason</c> (те саме, що значення
/// <c>CellPermissions</c>; текст — ключ каталогу <c>deny.{reason}</c>) — найчастіша причина серед комірок. <c>null</c> — коли
/// <c>canStyle</c> = <c>true</c> або комірок у зрізі немає (фарбувати нічого).
/// </param>
/// <param name="StyleDeniedRowKeys">
/// Рядки, у яких не можна фарбувати жодну видиму комірку (напр. рядок «лише читання»). Лише коли
/// <c>canStyle</c> = <c>true</c>.
/// </param>
/// <param name="StyleDeniedColumnCodes">
/// Видимі колонки, у яких не можна фарбувати жодну комірку (напр. заборона/«лише читання» на колонку за грантом).
/// Лише коли <c>canStyle</c> = <c>true</c>. Схованих/заборонених читачеві колонок тут немає (нерозкриття, S6).
/// </param>
/// <param name="StyleDeniedCells">
/// Решта заборонених комірок (не покриті повністю рядком чи колонкою): ключ <c>"{rowKey}:{columnCode}"</c> →
/// назва <c>EditDenyReason</c>. Лише коли <c>canStyle</c> = <c>true</c>.
/// </param>
/// <param name="Truncated">
/// <c>true</c> — переліки обрізано до ліміту (<c>CellStyleAccessPolicy.MaxKeys</c> ключів усього): комірки, яких тут
/// немає, можуть бути заборонені; остаточне рішення — за сервером (403 на запис).
/// </param>
public sealed record CellStyleAccessDto(
    bool CanStyle,
    string? DeniedReason,
    IReadOnlyList<string> StyleDeniedRowKeys,
    IReadOnlyList<string> StyleDeniedColumnCodes,
    IReadOnlyDictionary<string, string> StyleDeniedCells,
    bool Truncated);

/// <summary>Опис колонки для клієнта.</summary>
/// <remarks>
/// ⚠ <see cref="Scale"/> і <see cref="Precision"/> потрібні клієнтові не для
/// краси: за <c>ФВ-9.16c</c> вставка з Excel <b>округлює</b> зайві знаки і
/// показує це, а ручне введення — ні. Округлити на клієнті можна лише знаючи
/// масштаб колонки; без цих полів вставка з реального аркуша Excel
/// відхилялася б цілком (<c>ECR-CELL-0422</c>), тобто головний шлях введення
/// не працював би.
///
/// ⚠ <see cref="IsRequiredByMethodology"/> — колонка є обов'язковим входом
/// (<c>Domain.Entities.Calculations.MethodologyRequiredInput</c>) бодай
/// однієї методології, чинної зараз для цієї таблиці. Це ІНША вісь, ніж
/// <see cref="IsRequired"/> (те саме розрізнення документує сам
/// <c>MethodologyRequiredInput</c>), але з погляду grid обидва означають
/// одне: «заповни це до подання» — тому позначаються на екрані однаково
/// (`*` у заголовку), без розрізнення джерела вимоги.
///
/// Рахується на РІВНІ КОЛОНКИ, а не рядка: чи справді методологія
/// застосовна до КОНКРЕТНОГО рядка, залежить від значень інших колонок
/// цього рядка (<c>MethodologyRuleMatcher</c>) — а зірочка в заголовку
/// показується один раз на всю таблицю, до того, як рядки взагалі
/// заповнені. Тому ознака — «ця колонка МОЖЕ бути обов'язковою за чинною
/// методологією таблиці», а не «обов'язкова для КОЖНОГО рядка»: та сама
/// точність, що вже прийнята для <see cref="IsRequired"/> (він теж не
/// знає про рядки).
///
/// ⚠ <see cref="Style"/> — оформлення, задане автором шаблону (директива
/// registry-lookup / cell-style, PR B2); <c>null</c> —
/// <see cref="ColumnDef.StyleId"/> не задано, комірка виглядає як завжди.
///
/// ⛔ <see cref="Expression"/> (UI-25, B4) — вираз формули колонки; <c>null</c>, коли формули немає АБО
/// хоч одне посилання в ній читачеві не видне (інша колонка, таблиця, аркуш під забороною), або вираз не
/// розібрався. Різницю «немає» / «сховано» клієнтові свідомо не видно.
/// </remarks>
public sealed record ColumnDto(
    int Id,
    string Code,
    string Header,
    string DataType,
    int Ordinal,
    bool IsReadOnly,
    bool IsRequired,
    string? DisplayFormat,
    string? DefaultValue,
    int? LookupRegistryDefId,
    int? UnitId,
    string? UnitSymbol,
    byte? Precision = null,
    byte? Scale = null,
    bool IsRequiredByMethodology = false,
    CellStyleDto? Style = null,
    int? WidthPx = null,
    string? Expression = null,
    int? HeaderGroupId = null);

/// <summary>
/// Підмножина <c>StyleDef</c>, потрібна ЖИВІЙ сітці (директива registry-
/// lookup / cell-style, PR B2) — рамки/формат числа тут НЕ несуться:
/// перші читає лише Excel-експорт (`StyleMapper.cs`), другий сітка вже має
/// свій (`ColumnDef.DisplayFormat`), а IsBold/кольори/вирівнювання/перенос —
/// саме те, що змінює вигляд НЕ значення, а КОМІРКИ, тобто прямий аналог
/// CSS (`font-weight`, `color`, `background-color`, `text-align`,
/// `white-space`).
/// </summary>
public sealed record CellStyleDto(
    bool IsBold,
    bool IsItalic,
    int? ForegroundArgb,
    int? BackgroundArgb,
    byte? HorizontalAlign,
    byte? VerticalAlign,
    bool WrapText);

/// <summary>Рядок зі значеннями. Ключ у <paramref name="Cells"/> — код колонки.</summary>
/// <param name="RowKey">Ідентичність рядка.</param>
/// <param name="Ordinal">Позиція.</param>
/// <param name="RowKind">Режим рядків таблиці.</param>
/// <param name="Label">Підпис для фіксованих рядків.</param>
/// <param name="RowVersion">Версія для оптимістичного блокування.</param>
/// <param name="Cells">Значення; ключ — код колонки. Присутній ключ зі значенням
/// <c>null</c> означає <b>явну порожнечу</b>, відсутній ключ — «не заповнювали» (R-B4).</param>
/// <param name="IsOrphaned">
/// Рядок посилається на запис реєстру, що втратив чинність (ФВ-8.13).
/// ⚠ Читається зі збереженого поля <c>doc.TableRow.IsOrphaned</c>, а не
/// обчислюється при читанні: перерахунок на кожен зріз не вкладається в
/// бюджет 400 мс. Читання не блокує, <c>Submit</c> блокує.
/// </param>
/// <param name="Style">
/// Стиль рядка шаблону (<c>RowDef.StyleId</c> → <c>StyleDef</c>); <c>null</c> — стилю рядка немає (для
/// динамічного рядка, якого немає в шаблоні, — завжди <c>null</c>). Порядок шарів у сітці: стиль колонки, стиль
/// рядка, користувацьке оформлення, умовний формат.
/// </param>
public sealed record RowDto(
    string RowKey,
    int Ordinal,
    string RowKind,
    string? Label,
    string RowVersion,
    IReadOnlyDictionary<string, object?> Cells,
    bool IsOrphaned = false,
    CellStyleDto? Style = null);
