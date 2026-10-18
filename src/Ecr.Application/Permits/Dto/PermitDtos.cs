// src/Ecr.Application/Permits/Dto/PermitDtos.cs
namespace Ecr.Application.Permits.Dto;

/// <summary>Прапорці звітних форм, до яких входить дозвіл (поля шапки <c>Form_300</c>…<c>TOx</c>).</summary>
/// <param name="Form300">Входить у форму 300.</param>
/// <param name="Form301">Входить у форму 301.</param>
/// <param name="Form302">Входить у форму 302.</param>
/// <param name="Tox">Входить у TOx.</param>
public sealed record PermitReportingFlags(bool Form300, bool Form301, bool Form302, bool Tox);

/// <summary>Таблиця дозволу: склад з типу й підсумки по рядках.</summary>
/// <param name="Code">Код таблиці в межах типу.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="RegistryCode">Код прихованого реєстру таблиці (<c>PRM_&lt;тип&gt;_&lt;таблиця&gt;</c>): через нього клієнт читає й пише рядки.</param>
/// <param name="RowCount">Скільки живих рядків у цього дозволу.</param>
/// <param name="TotalColumnCode">Головний числовий стовпець (перший обов'язковий <c>Decimal</c> за порядком); <c>null</c> - такого немає.</param>
/// <param name="Total">Σ головного стовпця по рядках дозволу; <c>null</c>, якщо стовпця немає.</param>
public sealed record PermitTableDto(
    string Code,
    Dictionary<string, string> NameL10n,
    string RegistryCode,
    int RowCount,
    string? TotalColumnCode,
    decimal? Total);

/// <summary>Аркуш, що читає дозвіл через привʼязку (з PM-09; до того завжди порожньо).</summary>
/// <param name="BindingCode">Код привʼязки.</param>
/// <param name="SheetName">Назва аркуша.</param>
/// <param name="Mode"><c>Rows</c> або <c>Formulas</c>.</param>
public sealed record PermitBoundSheetDto(string BindingCode, string SheetName, string Mode);

/// <summary>Рядок переліку дозволів.</summary>
/// <param name="Id">Ідентифікатор дозволу (запис шапки).</param>
/// <param name="Number">Номер дозволу.</param>
/// <param name="TypeCode">Код типу.</param>
/// <param name="TypeNameL10n">Назва типу мовами каталогу.</param>
/// <param name="Location">Розташування.</param>
/// <param name="IssueDate">Дата видачі.</param>
/// <param name="ValidFrom">Перший чинний день.</param>
/// <param name="ValidTo">Перший НЕчинний день (виключна межа, півінтервал <c>[from, to)</c>).</param>
/// <param name="ActualEnd">Фактична дата закінчення.</param>
/// <param name="Status"><c>Draft</c>, <c>Active</c> або <c>Expired</c> на дату <c>asOf</c>.</param>
/// <param name="EndsInDays">Днів від <c>asOf</c> до закінчення; лише для <c>Active</c> з відомою датою закінчення.</param>
/// <param name="RowCount">Σ рядків усіх таблиць.</param>
/// <param name="TableCount">Кількість таблиць типу.</param>
/// <param name="BoundSheetCount">Скільки аркушів читають дозвіл; поки привʼязок немає (PM-09) - завжди 0.</param>
/// <param name="UpdatedAt">Остання зміна запису шапки, UTC.</param>
/// <param name="UpdatedByDisplayName">Хто змінив.</param>
public sealed record PermitListItemDto(
    long Id,
    string Number,
    string TypeCode,
    Dictionary<string, string> TypeNameL10n,
    string? Location,
    DateOnly? IssueDate,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    DateOnly? ActualEnd,
    string Status,
    int? EndsInDays,
    int RowCount,
    int TableCount,
    int BoundSheetCount,
    DateTime? UpdatedAt,
    string? UpdatedByDisplayName);

/// <summary>Дозвіл: шапка, життєвий цикл, таблиці й хто його читає.</summary>
/// <param name="Id">Ідентифікатор дозволу (запис шапки).</param>
/// <param name="Number">Номер дозволу.</param>
/// <param name="TypeCode">Код типу.</param>
/// <param name="TypeNameL10n">Назва типу мовами каталогу.</param>
/// <param name="TypeVersion">Опублікована версія типу.</param>
/// <param name="Category">Категорія типу.</param>
/// <param name="Location">Розташування.</param>
/// <param name="IssueDate">Дата видачі.</param>
/// <param name="ValidFrom">Перший чинний день.</param>
/// <param name="ValidTo">Перший НЕчинний день (виключна межа).</param>
/// <param name="ActualEnd">Фактична дата закінчення.</param>
/// <param name="Status"><c>Draft</c>, <c>Active</c> або <c>Expired</c> на дату <c>asOf</c> (за замовчуванням сьогодні, UTC).</param>
/// <param name="EndsInDays">Днів до закінчення; лише для <c>Active</c>.</param>
/// <param name="ReportingFlags">Прапорці звітних форм.</param>
/// <param name="Tables">Таблиці типу з підсумками по цьому дозволу.</param>
/// <param name="BoundSheets">Аркуші, що читають дозвіл (з PM-09; до того порожньо).</param>
/// <param name="RowVersion">Токен для наступного <c>PUT</c>.</param>
/// <param name="UpdatedAt">Остання зміна запису шапки, UTC.</param>
/// <param name="UpdatedByDisplayName">Хто змінив.</param>
public sealed record PermitDto(
    long Id,
    string Number,
    string TypeCode,
    Dictionary<string, string> TypeNameL10n,
    int TypeVersion,
    string Category,
    string? Location,
    DateOnly? IssueDate,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    DateOnly? ActualEnd,
    string Status,
    int? EndsInDays,
    PermitReportingFlags ReportingFlags,
    IReadOnlyList<PermitTableDto> Tables,
    IReadOnlyList<PermitBoundSheetDto> BoundSheets,
    string RowVersion,
    DateTime? UpdatedAt,
    string? UpdatedByDisplayName);

/// <summary>Запит на заведення дозволу (чернеткою).</summary>
/// <param name="TypeCode">Опублікований тип.</param>
/// <param name="Number">Номер дозволу.</param>
/// <param name="Location">Розташування.</param>
/// <param name="IssueDate">Дата видачі.</param>
/// <param name="ValidFrom">Перший чинний день.</param>
/// <param name="ValidTo">Перший НЕчинний день; суворо пізніше за початок.</param>
/// <param name="ReportingFlags">Прапорці звітних форм; <c>null</c> - жодного.</param>
public sealed record CreatePermitRequest(
    string TypeCode,
    string Number,
    string? Location,
    DateOnly? IssueDate,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    PermitReportingFlags? ReportingFlags);

/// <summary>Запит на зміну шапки дозволу (чернетка чи чинний).</summary>
/// <param name="RowVersion">Токен з останнього читання.</param>
/// <param name="Number">Номер дозволу.</param>
/// <param name="Location">Розташування.</param>
/// <param name="IssueDate">Дата видачі.</param>
/// <param name="ValidFrom">Перший чинний день.</param>
/// <param name="ValidTo">Перший НЕчинний день.</param>
/// <param name="ActualEnd">Фактична дата закінчення; <c>null</c> - не задана.</param>
/// <param name="ReportingFlags">Прапорці звітних форм; <c>null</c> - жодного.</param>
public sealed record UpdatePermitRequest(
    string RowVersion,
    string Number,
    string? Location,
    DateOnly? IssueDate,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    DateOnly? ActualEnd,
    PermitReportingFlags? ReportingFlags);

/// <summary>Запит на дострокове завершення чинного дозволу.</summary>
/// <param name="Reason">Причина, не коротша за 10 символів (іде в журнал).</param>
/// <param name="EndDate">Перший нечинний день; <c>null</c> - сьогодні (UTC).</param>
public sealed record EndPermitRequest(string Reason, DateOnly? EndDate);

/// <summary>Необовʼязкові параметри поновлення дозволу.</summary>
/// <param name="ValidTo">Кінець нового дозволу; <c>null</c> - рівно на рік після його початку.</param>
public sealed record RenewPermitRequest(DateOnly? ValidTo);
