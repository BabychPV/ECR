// src/Ecr.Application/Calculations/Dto/MethodologyDraftDtos.cs
using Ecr.Domain.Enums;

namespace Ecr.Application.Calculations.Dto;

/// <summary>
/// Версія методології в **конфігураторі** — на відміну від
/// <see cref="MethodologyVersionDto"/>, тут є і чернетки.
/// </summary>
/// <remarks>
/// ⛔ Окремий тип, а не додаткове поле в наявному. У
/// <see cref="MethodologyVersionDto"/> <c>EffectiveFrom</c> обов'язковий, бо
/// той перелік описує ЧИННІ версії й обчислює межу вікна з початку наступної.
/// Чернетка вікна не має взагалі — зробити поле нульовним означало б, що
/// перелік для розрахунку теж почав би допускати версію без дати, тобто
/// версію, для якої <c>VersionOn</c> не має відповіді (ФВ-13.3).
/// </remarks>
/// <param name="Id">Ідентифікатор версії.</param>
/// <param name="VersionNumber">Номер версії.</param>
/// <param name="Status">Чернетка, опублікована чи виведена з обігу.</param>
/// <param name="Level">Рівень драбини виразності.</param>
/// <param name="EffectiveFrom">Початок вікна дії; <c>null</c> — чернетка.</param>
/// <param name="NumericMode">Арифметика; переноситься в клон незмінною.</param>
/// <param name="CalendarMode">Джерело тривалості періоду.</param>
/// <param name="TraceLevel">Обсяг журналу.</param>
/// <param name="IsEditable">
/// Чи можна правити вміст. Відповідь дає СЕРВЕР, а не форма: клієнт, який
/// вирішує це сам за <c>status</c>, розійдеться з доменом на першому ж новому
/// стані версії.
/// </param>
/// <param name="CreatedByUserId">Автор; публікувати власну правку він не зможе (D-40).</param>
public sealed record MethodologyDraftVersionDto(
    int Id,
    string VersionNumber,
    TemplateVersionStatus Status,
    CalculationLevel Level,
    DateOnly? EffectiveFrom,
    NumericMode NumericMode,
    CalendarMode CalendarMode,
    TraceLevel TraceLevel,
    bool IsEditable,
    int CreatedByUserId);

/// <summary>Формула версії методології, як її бачить конфігуратор.</summary>
/// <remarks>
/// ⚠ <paramref name="EvaluationOrder"/> віддається, але не приймається:
/// порядок топологічний і рахується при публікації (ФВ-9.4). Показувати його
/// варто — він пояснює, чому формула бачить результат сусідньої; приймати
/// його від клієнта означало б дозволити людині зсунути обчислення так, що
/// помилка стане числом у звіті, а не помилкою публікації.
/// </remarks>
/// <param name="Id">Ідентифікатор формули.</param>
/// <param name="Code">Код — те, на що посилається <c>!Name</c>.</param>
/// <param name="Expression">Вираз діалекту методологій.</param>
/// <param name="ResultType">Число чи текст.</param>
/// <param name="OutputUnitId">Одиниця результату; <c>null</c> — безрозмірна або текст.</param>
/// <param name="EvaluationOrder">Позиція в топологічному порядку; нуль до публікації.</param>
/// <param name="ArgumentsCsv">
/// Оголошені аргументи — <c>;</c>-список, як у <c>FInfo_Arguments</c>;
/// <c>null</c> — списку немає. ⛔ Не те саме, що порожній рядок: <c>null</c>
/// глушить звірку пастки 2 (<c>ECR-CALC-0432</c>), а порожній оголошує «нуль
/// аргументів», і тоді будь-який токен у виразі є порушенням.
/// </param>
public sealed record MethodologyFormulaDto(
    int Id,
    string Code,
    string Expression,
    FormulaResultType ResultType,
    int? OutputUnitId,
    int EvaluationOrder,
    string? ArgumentsCsv);

/// <summary>
/// Методологія-контейнер, як її бачить конфігуратор — <b>без</b> версій.
/// </summary>
/// <remarks>
/// ⛔ Окремий тип, а не <see cref="MethodologyDto"/>. Той віддає перелік для
/// РОЗРАХУНКУ і за побудовою містить лише методології, що мають бодай одну
/// опубліковану версію: щойно створена методологія в ньому не з'являється
/// взагалі. Повернути її тим типом означало б віддати клієнтові запис, якого
/// той самий клієнт не побачить у переліку через секунду.
/// </remarks>
/// <param name="Id">Ідентифікатор методології.</param>
/// <param name="Code">Код — те, чим на неї посилаються імпорти й прив'язки.</param>
/// <param name="NameL10n">Назва мовами каталогу.</param>
/// <param name="Kind">Природа: обирається правилом, зашита в модуль або бібліотека.</param>
/// <param name="Group">Група в переліку; <c>null</c> — поза групами.</param>
/// <param name="IsActive">Чи бере методологія участь у розрахунку.</param>
public sealed record MethodologySummaryDto(
    int Id,
    string Code,
    IReadOnlyDictionary<string, string> NameL10n,
    MethodologyKind Kind,
    string? Group,
    bool IsActive);

/// <summary>Константа версії методології (ФВ-16.1, ФВ-16.5).</summary>
/// <remarks>
/// ⚠ Число і текст віддаються ОБИДВА, і обидва нульовні: константа не завжди
/// число (поправка 2-біс директиви ПК-1 №05), а числова константа з
/// нерозібраним рядком джерела має <c>value = null</c> при
/// <c>kind = Numeric</c> — саме це й показує <paramref name="IsResolved"/>.
/// </remarks>
/// <param name="Id">Ідентифікатор константи.</param>
/// <param name="Code">Код — те, що стоїть після <c>CST.</c>.</param>
/// <param name="Kind">Число, текст або мітка категорії.</param>
/// <param name="Value">Число; <c>null</c> — нечислова або нерозібрана.</param>
/// <param name="TextValue">Текст або сирий рядок джерела.</param>
/// <param name="UnitId">Одиниця; <c>null</c> — нечислова.</param>
/// <param name="ValidFrom">Перший чинний день; <c>null</c> — від початку.</param>
/// <param name="ValidTo">Перший НЕчинний день (виключно); <c>null</c> — без межі.</param>
/// <param name="Category">Категорія звуження; <c>null</c> — спільна.</param>
/// <param name="Source">Звідки взято значення.</param>
/// <param name="IsResolved">Чи придатна константа до підстановки у вираз.</param>
public sealed record MethodologyConstantDto(
    int Id,
    string Code,
    ConstantKind Kind,
    decimal? Value,
    string? TextValue,
    int? UnitId,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    string? Category,
    string? Source,
    bool IsResolved);

/// <summary>Правило відбору рядків документа (ФВ-13.3, ФВ-13.4).</summary>
/// <param name="Id">Ідентифікатор правила.</param>
/// <param name="Code">Код, унікальний у межах версії.</param>
/// <param name="MatchJson">Структурований предикат; <c>{}</c> — уся таблиця.</param>
/// <param name="Priority">Менше значення — вищий пріоритет; перший збіг виграє.</param>
/// <param name="IsActive">Вимкнене правило не бере участі в зіставленні.</param>
public sealed record MethodologyRuleDto(
    int Id,
    string Code,
    string MatchJson,
    int Priority,
    bool IsActive);

/// <summary>Оголошений вихід версії — те, що методологія повертає (ФВ-16.6).</summary>
/// <param name="Id">Ідентифікатор виходу.</param>
/// <param name="Code">Код виходу — адреса, на яку посилається прив'язка.</param>
/// <param name="UnitId">Одиниця результату; обов'язкова.</param>
/// <param name="Ordinal">Порядок у переліку.</param>
public sealed record MethodologyOutputDto(int Id, string Code, int UnitId, int Ordinal);

/// <summary>Тест золотого набору версії (ФВ-13.7, ФВ-9.12).</summary>
/// <remarks>
/// ⛔ Порожній набір — не зелений, і публікація без тестів відхиляється
/// (<c>GoldenSet.IsGreen</c>). Тому екран версії показує тести поруч із
/// формулами, а не в окремому кутку: без них кнопка публікації не спрацює
/// жодного разу.
/// </remarks>
/// <param name="Id">Ідентифікатор тесту.</param>
/// <param name="Code">Код тесту — те, що потрапляє в повідомлення про провал.</param>
/// <param name="InputJson">Вхід прогону у формі <c>CalculationInput</c>.</param>
/// <param name="ExpectedJson">Очікувані виходи: код виходу → число.</param>
/// <param name="Tolerance">Допуск порівняння; нуль — точна рівність.</param>
public sealed record MethodologyTestCaseDto(
    int Id,
    string Code,
    string InputJson,
    string ExpectedJson,
    decimal Tolerance);

/// <summary>
/// Прив'язка виходу методології до колонки документа (<c>D-69</c>).
/// </summary>
/// <remarks>
/// ⚠ <paramref name="TableDefId"/> віддається, але не приймається: він
/// виводиться з колонки. Пара, у якій вони розійшлися, не має симптому —
/// прив'язка просто не спрацьовує.
/// </remarks>
/// <param name="Id">Ідентифікатор прив'язки.</param>
/// <param name="TableDefId">Таблиця колонки-приймача.</param>
/// <param name="ColumnDefId">Колонка-приймач.</param>
/// <param name="MethodologyId">Методологія-джерело.</param>
/// <param name="OutputCode">Який вихід методології лягає в колонку.</param>
/// <param name="MatchJson">Як звузити рядки таблиці; <c>{}</c> — усі.</param>
/// <param name="IsActive">Вимкнена прив'язка не бере участі в прогоні.</param>
public sealed record CalculationBindingDto(
    int Id,
    int TableDefId,
    int ColumnDefId,
    int MethodologyId,
    string OutputCode,
    string MatchJson,
    bool IsActive);

/// <summary>
/// Число, яке дав актуальний прогін розрахунку на документі.
/// </summary>
/// <remarks>
/// ⛔ Результат методології **не** лежить у <c>doc.CellValue</c> (<c>D-69</c>):
/// у документ він приходить посиланням через <c>cfg.CalculationBinding</c>.
/// Тому це окреме читання, а не поле зрізу таблиці.
/// </remarks>
/// <param name="MethodologyVersionId">Версія, що дала число.</param>
/// <param name="SourceRowKey">Рядок документа; <c>null</c> — рівень таблиці.</param>
/// <param name="OutputCode">Код виходу методології.</param>
/// <param name="Value">Значення.</param>
/// <param name="UnitId">Одиниця результату.</param>
/// <param name="SubstanceEntryId">Речовина; <c>null</c> — вихід без речовини.</param>
public sealed record CalculationResultDto(
    int MethodologyVersionId,
    string? SourceRowKey,
    string OutputCode,
    decimal Value,
    int UnitId,
    long? SubstanceEntryId);
