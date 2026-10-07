// src/Ecr.Application/Calculations/Dto/MethodologyPackageDtos.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations.Dto;

/// <summary>
/// Пакет <c>ecr-methodology-package</c> v1 — вихід <c>tools/Ecr.MethodologyImport export</c>
/// (FEATURE-HSE301-VIEW §11.6), вхід <c>POST /api/v1/methodologies/import</c>.
/// </summary>
/// <remarks>
/// ⚠ Дзеркало <c>MethodologyPackage</c> інструмента, а не посилання на нього: застосунок не
/// залежить від інструмента міграції, а формат між ними — контракт (§11.6). Дати — рядки AF
/// як є (ISO 8601 в UTC); пояс і півінтервал вирішує імпортер.
/// </remarks>
/// <param name="Format">Має бути <c>ecr-methodology-package</c>.</param>
/// <param name="Version">Версія формату; приймається лише 1.</param>
/// <param name="Library">Методологія-бібліотека, куди резолвляться чужі посилання (<c>Common</c>).</param>
/// <param name="Methodologies">Методології з версіями.</param>
/// <param name="Blockers">Блокери, знайдені експортером; непорожній — імпорт відмовляє.</param>
public sealed record MethodologyPackageDto(
    string Format,
    int Version,
    string Library,
    IReadOnlyList<MethodologyPackageMethodologyDto> Methodologies,
    IReadOnlyList<string>? Blockers);

/// <summary>Методологія пакета.</summary>
/// <param name="Name">Ім'я в AF — стає кодом методології.</param>
/// <param name="Versions">Версії методології.</param>
public sealed record MethodologyPackageMethodologyDto(
    string Name, IReadOnlyList<MethodologyPackageVersionDto> Versions);

/// <summary>Версія методології пакета.</summary>
/// <param name="Version">Номер версії в AF.</param>
/// <param name="Formulas">Формули (усі версії формул AF).</param>
/// <param name="Constants">Константи зі значеннями.</param>
/// <param name="CategoryRule">
/// ✎ L-2: правило категорії константи версії (<c>calc.CategoryRule</c>). Необов'язкове: пакет без вузла
/// читається як і раніше (формат v1 зворотно сумісний). Експортер AF правило сам НЕ виводить — у AF
/// категорію вибирає C#-клас методології, а не дані, — тож вузол дописує людина чи хмарна лінія.
/// </param>
public sealed record MethodologyPackageVersionDto(
    string Version,
    IReadOnlyList<MethodologyPackageFormulaDto> Formulas,
    IReadOnlyList<MethodologyPackageConstantDto> Constants,
    MethodologyPackageCategoryRuleDto? CategoryRule = null);

/// <summary>Правило категорії константи версії пакета (L-2).</summary>
/// <param name="Expression">
/// Вираз діалекту Methodology над рядком документа, що дає ключ категорії (текст):
/// <c>!ECW_Category</c>, <c>if(@Land_TypeFuel = 'Diesel - Дизель', 'Diesel', …)</c>.
/// </param>
public sealed record MethodologyPackageCategoryRuleDto(string? Expression);

/// <summary>Формула пакета — одна версія формули AF.</summary>
/// <param name="Name">Ім'я — стає кодом формули.</param>
/// <param name="Version">Версія формули AF.</param>
/// <param name="Arguments"><c>;</c>-список <c>FInfo_Arguments</c>, як є.</param>
/// <param name="Text">Вираз.</param>
/// <param name="StartDate">Початок дії (рядок AF).</param>
/// <param name="EndDate">Кінець дії (рядок AF).</param>
/// <param name="IsAvailable">Прапорець AF.</param>
/// <param name="Report">Позначка звіту AF.</param>
public sealed record MethodologyPackageFormulaDto(
    string Name,
    string Version,
    string? Arguments,
    string? Text,
    string? StartDate,
    string? EndDate,
    bool IsAvailable,
    string? Report);

/// <summary>Константа пакета.</summary>
/// <param name="Name">Ім'я — те, що стоїть після <c>CST.</c>.</param>
/// <param name="Parameter">Параметр AF (опис).</param>
/// <param name="Unit">Одиниця AF; шукається в каталозі за кодом.</param>
/// <param name="Values">Значення за категоріями й версіями.</param>
public sealed record MethodologyPackageConstantDto(
    string Name,
    string? Parameter,
    string? Unit,
    IReadOnlyList<MethodologyPackageConstantValueDto> Values);

/// <summary>Значення константи пакета.</summary>
/// <param name="Category">Категорія (Location/набір); порожня — спільна.</param>
/// <param name="Version">Версія значення AF.</param>
/// <param name="Value">Сирий рядок <c>CInfo_Value</c>.</param>
/// <param name="StartDate">Початок чинності (рядок AF).</param>
/// <param name="EndDate">Кінець чинності (рядок AF).</param>
public sealed record MethodologyPackageConstantValueDto(
    string? Category,
    string? Version,
    string? Value,
    string? StartDate,
    string? EndDate);

/// <summary>Звіт імпорту пакета методологій — однаковий для сухого прогону й запису.</summary>
/// <param name="DryRun">Чи був це сухий прогін.</param>
/// <param name="Applied">Чи записано хоч щось.</param>
/// <param name="Outcome">
/// <c>created</c> — є що створити (або створено); <c>unchanged</c> — усе вже є, без змін;
/// <c>blocked</c> — блокери; <c>conflict</c> — розбіжність із наявними версіями.
/// </param>
/// <param name="Methodologies">План по методологіях.</param>
/// <param name="Blockers">Блокери: нерезолвні посилання, недопустимі коди, блокери експортера.</param>
/// <param name="Conflicts">Версії, які вже є з іншим вмістом.</param>
/// <param name="Warnings">Попередження, що не зупиняють імпорт.</param>
/// <param name="Totals">Підсумки.</param>
public sealed record MethodologyImportReportDto(
    bool DryRun,
    bool Applied,
    string Outcome,
    IReadOnlyList<MethodologyImportMethodologyDto> Methodologies,
    IReadOnlyList<MethodologyImportIssueDto> Blockers,
    IReadOnlyList<MethodologyImportIssueDto> Conflicts,
    IReadOnlyList<MethodologyImportIssueDto> Warnings,
    MethodologyImportTotalsDto Totals);

/// <summary>Методологія у звіті імпорту.</summary>
/// <param name="Code">Код.</param>
/// <param name="Kind">Природа методології.</param>
/// <param name="Action"><c>create</c> або <c>existing</c>.</param>
/// <param name="MethodologyId">Ідентифікатор; <c>null</c> — ще не створено.</param>
/// <param name="Versions">Версії.</param>
public sealed record MethodologyImportMethodologyDto(
    string Code,
    MethodologyKind Kind,
    string Action,
    int? MethodologyId,
    IReadOnlyList<MethodologyImportVersionDto> Versions);

/// <summary>Версія у звіті імпорту.</summary>
/// <param name="Version">Номер версії.</param>
/// <param name="Action"><c>create</c>, <c>unchanged</c> або <c>conflict</c>.</param>
/// <param name="VersionId">Ідентифікатор; <c>null</c> — ще не створено.</param>
/// <param name="Formulas">Скільки формул.</param>
/// <param name="Constants">Скільки рядків констант (значень).</param>
/// <param name="ConstantsFromLibrary">Скільки рядків констант скопійовано з бібліотеки.</param>
/// <param name="Imports">Методології, чиї формули версія імпортує (<c>!</c>).</param>
/// <param name="CategoryRule">
/// L-2: що пакет робить із правилом категорії версії — <c>added</c> (створюється разом з чернеткою),
/// <c>unchanged</c> (версія вже є з тим самим правилом), <c>conflict</c> (версія є з іншим вмістом);
/// <c>null</c> — вузла <c>categoryRule</c> у пакеті немає.
/// </param>
public sealed record MethodologyImportVersionDto(
    string Version,
    string Action,
    int? VersionId,
    int Formulas,
    int Constants,
    int ConstantsFromLibrary,
    IReadOnlyList<string> Imports,
    string? CategoryRule = null);

/// <summary>Рядок звіту: блокер, конфлікт або попередження.</summary>
/// <param name="Kind">Вид (стабільний ключ, напр. <c>unresolvedFormula</c>).</param>
/// <param name="Methodology">Методологія; <c>null</c> — пакет цілком.</param>
/// <param name="Version">Версія методології.</param>
/// <param name="Subject">Формула чи константа, де знайдено.</param>
/// <param name="Detail">Посилання, значення або пояснення.</param>
public sealed record MethodologyImportIssueDto(
    string Kind,
    string? Methodology,
    string? Version,
    string? Subject,
    string? Detail);

/// <summary>Підсумки імпорту.</summary>
/// <param name="MethodologiesToCreate">Нових методологій.</param>
/// <param name="VersionsToCreate">Нових версій-чернеток.</param>
/// <param name="VersionsUnchanged">Версій, що вже є з тим самим вмістом.</param>
/// <param name="Formulas">Формул у нових версіях.</param>
/// <param name="Constants">Рядків констант у нових версіях.</param>
/// <param name="Imports">Імпортів між методологіями в нових версіях.</param>
public sealed record MethodologyImportTotalsDto(
    int MethodologiesToCreate,
    int VersionsToCreate,
    int VersionsUnchanged,
    int Formulas,
    int Constants,
    int Imports);
