// src/Ecr.Application/Documents/CalculationYear/CalculationYearDtos.cs
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Documents.CalculationYear;

/// <summary>
/// «Calculation results — усі місяці року»: кожне розраховане число документа за рік, місяці поруч
/// (CALC-RESULTS.md). Один запит на документ × рік.
/// </summary>
/// <remarks>
/// ⚠ Компактна форма: довідкові частини (таблиці, обладнання, методології, речовини, одиниці, колонки формул) —
/// окремими переліками, а рядок посилається на них індексом; значення — масивом з 12 чисел, стани — рядком з 12
/// літер. Для документа Land (десятки тисяч виходів × 12 місяців) об'єкт на кожне значення роздув би відповідь
/// у рази.
/// </remarks>
/// <param name="DocumentId">Документ.</param>
/// <param name="Year">Рік.</param>
/// <param name="Months">Рівно 12 місяців, січень першим.</param>
/// <param name="Tables">Таблиці (з аркушем), на які посилаються рядки.</param>
/// <param name="Equipment">Рядки документа (обладнання), на які посилаються виходи.</param>
/// <param name="Methodologies">Методології (версія — останнього місяця з числом).</param>
/// <param name="Substances">Речовини.</param>
/// <param name="Units">Одиниці з ознакою адитивності.</param>
/// <param name="Columns">Розраховані колонки шаблону (джерело — формула, а не методологія).</param>
/// <param name="Rows">Виходи в порядку показу: аркуш → таблиця → рядок → методологія → вихід.</param>
/// <param name="FreshnessHidden">
/// <c>true</c> — читач звужений (схований аркуш/колонка): «out of date» йому не віддається (Н-2, як у панелі
/// результатів), тож місяці з прогоном показуються як <c>current</c>.
/// </param>
public sealed record CalculationYearViewDto(
    long DocumentId,
    int Year,
    IReadOnlyList<CalculationYearMonthDto> Months,
    IReadOnlyList<CalculationYearTableDto> Tables,
    IReadOnlyList<CalculationYearEquipmentDto> Equipment,
    IReadOnlyList<CalculationYearMethodologyDto> Methodologies,
    IReadOnlyList<CalculationYearSubstanceDto> Substances,
    IReadOnlyList<CalculationYearUnitDto> Units,
    IReadOnlyList<CalculationYearColumnDto> Columns,
    IReadOnlyList<CalculationYearRowDto> Rows,
    bool FreshnessHidden);

/// <summary>Місяць року.</summary>
/// <param name="Month">1…12.</param>
/// <param name="PeriodKey">Ключ періоду (<c>YYYYMM</c>).</param>
/// <param name="PeriodState"><c>open</c> | <c>closed</c> | <c>notOpened</c>.</param>
/// <param name="RunState">
/// <c>current</c> | <c>outOfDate</c> (входи змінено після прогону) | <c>noRun</c> | <c>notOpened</c>.
/// </param>
/// <param name="CalculatedAt">Коли завершився прогін, що дав числа місяця (UTC).</param>
public sealed record CalculationYearMonthDto(
    int Month, int PeriodKey, string PeriodState, string RunState, DateTime? CalculatedAt);

/// <summary>Таблиця документа разом з аркушем.</summary>
/// <param name="SheetDefId">Аркуш.</param>
/// <param name="SheetCode">Код аркуша (для посилання на документ).</param>
/// <param name="SheetNameL10n">Назва аркуша.</param>
/// <param name="SheetCopyId">Копія аркуша; <c>null</c> — оригінал.</param>
/// <param name="TableDefId"><c>0</c> — числа без екземпляра таблиці (прогони до D-401), таблицю не встановлено.</param>
/// <param name="TableCode">Код таблиці (для посилання на документ).</param>
/// <param name="TableNameL10n">Назва таблиці.</param>
public sealed record CalculationYearTableDto(
    int SheetDefId,
    string SheetCode,
    LocalizedText SheetNameL10n,
    long? SheetCopyId,
    int TableDefId,
    string TableCode,
    LocalizedText TableNameL10n);

/// <summary>Рядок документа (обладнання).</summary>
/// <param name="Table">Індекс у <see cref="CalculationYearViewDto.Tables"/>.</param>
/// <param name="RowKey">Ключ рядка; <c>null</c> — число рівня таблиці.</param>
/// <param name="LabelL10n">Підпис фіксованого рядка або назва запису довідника; <c>null</c> — немає.</param>
/// <param name="Name">Текст колонки-назви динамічного рядка; <c>null</c> — немає.</param>
/// <param name="IdCode">Значення колонки бізнес-ключа (IDCode) або код запису довідника.</param>
public sealed record CalculationYearEquipmentDto(
    int Table, string? RowKey, LocalizedText? LabelL10n, string? Name, string? IdCode);

/// <summary>Методологія.</summary>
public sealed record CalculationYearMethodologyDto(int MethodologyId, string Code, LocalizedText NameL10n, string Version);

/// <summary>Речовина (запис довідника).</summary>
public sealed record CalculationYearSubstanceDto(long Id, string Code, LocalizedText NameL10n);

/// <summary>Одиниця.</summary>
/// <param name="Id">Одиниця.</param>
/// <param name="Code">Код (<c>t</c>, <c>kg_per_s</c>).</param>
/// <param name="SymbolL10n">Позначення мовами каталогу; <c>null</c> — немає.</param>
/// <param name="Dimension">Код розмірності (<c>Mass</c>, <c>MassFlow</c>…); порожньо — одиниці немає в довіднику.</param>
/// <param name="Additive">
/// Чи має сенс сума за рік: маса, енергія, об'єм, кількість речовини — так; швидкість, густина, частка, коефіцієнт,
/// відсоток і все невідоме — ні.
/// </param>
public sealed record CalculationYearUnitDto(int Id, string Code, LocalizedText? SymbolL10n, string Dimension, bool Additive);

/// <summary>Розрахована колонка шаблону.</summary>
public sealed record CalculationYearColumnDto(int ColumnDefId, string Code, LocalizedText HeaderL10n);

/// <summary>Вихід: одне розраховане число рядка за 12 місяців.</summary>
/// <param name="Id">Стійкий ключ виходу (для експорту «як відфільтровано»).</param>
/// <param name="Equipment">Індекс у <see cref="CalculationYearViewDto.Equipment"/>.</param>
/// <param name="Methodology">Індекс методології; <c>null</c> — формула шаблону (див. <paramref name="Column"/>).</param>
/// <param name="OutputCode">Код виходу методології або код колонки формули.</param>
/// <param name="Column">Індекс колонки формули; <c>null</c> — вихід методології.</param>
/// <param name="Substance">Індекс речовини; <c>null</c> — без речовини.</param>
/// <param name="Unit">Індекс одиниці; <c>null</c> — одиниця невідома (сума не рахується).</param>
/// <param name="Values">12 значень; <c>null</c> — числа немає.</param>
/// <param name="States">
/// 12 літер: <c>v</c> — значення є; <c>a</c> — у місяці є прогін, а цього числа немає (рядка не було, вхід
/// порожній або методологія не дала виходу); <c>n</c> — у місяці немає прогону або місяць не відкрито.
/// </param>
/// <param name="YearTotal">Сума за рік — лише для адитивної одиниці; інакше <c>null</c>.</param>
/// <param name="MonthsIncluded">Скільки місяців увійшло в суму (з 12).</param>
public sealed record CalculationYearRowDto(
    string Id,
    int Equipment,
    int? Methodology,
    string OutputCode,
    int? Column,
    int? Substance,
    int? Unit,
    IReadOnlyList<decimal?> Values,
    string States,
    decimal? YearTotal,
    int MonthsIncluded);
