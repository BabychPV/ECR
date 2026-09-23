// src/Ecr.Application/Templates/Dto/TemplateStructureDto.cs

using Ecr.Application.Documents.Dto;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates.Dto;

/// <summary>
/// Структура версії — те, що віддається клієнту й кешується за ключем
/// <c>v{id}:r{rev}</c> (`ФВ-2.5`).
/// </summary>
/// <remarks>
/// ⚠ Ім'я лишилося з часів, коли ендпоінт бачив лише опубліковані версії:
/// тепер той самий <c>GET …/structure</c> обслуговує і чернетку — саме на
/// ньому редактор аркушів (<c>PUT …/sheets/{code}</c>) читає поточний склад.
/// </remarks>
/// <param name="TemplateVersionId">Версія шаблону.</param>
/// <param name="PresentationRevision">Ревізія презентаційного шару; частина ключа кешу.</param>
/// <param name="IsEditable">
/// Чи дозволяє стан версії структурну правку (<c>ФВ-7.1</c>). Рахує СЕРВЕР —
/// той самий прапорець, що й <see cref="TableRelationsDto.IsEditable"/>:
/// клієнт, який виводив би його зі статусу самостійно, тримав би другу копію
/// правила «опублікована незмінна».
/// </param>
/// <param name="Sheets">Аркуші в порядку <c>Ordinal</c>.</param>
/// <param name="GroupRules">
/// Правила складу за групами аркушів (<c>ФВ-3.2</c>), директива
/// "live-попередження про порушення SheetGroupRule". Клієнт
/// (<c>CreateDocumentModal.tsx</c>) рахує порушення локально при кожній
/// зміні вибору аркушів, щоб попередити ДО спроби зберегти — сервер
/// лишається останньою лінією правди через
/// <c>IDocumentStore.ValidateCompositionAsync</c>.
/// </param>
public sealed record TemplateStructureDto(
    int TemplateVersionId,
    int PresentationRevision,
    bool IsEditable,
    IReadOnlyList<SheetDto> Sheets,
    IReadOnlyList<SheetGroupRuleDto> GroupRules);

/// <summary>Одне правило складу документа для конкретної групи аркушів (<c>ФВ-3.2</c>).</summary>
/// <param name="SheetGroup">Група, якої стосується правило.</param>
/// <param name="RuleKind">
/// Вид правила: <c>0 RequiresAll</c>, <c>1 RequiresOne</c>, <c>2 Excludes</c>.
/// </param>
/// <param name="TargetGroup">Група-ціль; заповнена лише для <c>Excludes</c>.</param>
/// <remarks>
/// ⚠ Сервер сьогодні перевіряє (<c>DocumentStore.ValidateCompositionAsync</c>)
/// лише <c>RequiresAll</c> і <c>RequiresOne</c> — <c>Excludes</c> віддається
/// тут для повноти опису структури, але жоден шлях запису його ще не
/// застосовує. Клієнтське попередження навмисно дзеркалить рівно ту саму
/// пару правил, що й сервер: попереджати про те, чого сервер не перевіряє,
/// означало б розійтися з ним, а не підстрахувати його.
/// </remarks>
public sealed record SheetGroupRuleDto(string SheetGroup, byte RuleKind, string? TargetGroup);

/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код — ідентичність аркуша й адреса в <c>PUT …/sheets/{code}</c>.</param>
/// <param name="NameL10n">Назва аркуша всіма мовами каталогу.</param>
/// <param name="Ordinal">Порядок відображення; не ідентичність.</param>
/// <param name="SheetGroup">Група для правил складу документа; <c>null</c> — поза групами.</param>
/// <param name="IsMandatory">Чи обов'язковий аркуш для складу документа.</param>
/// <param name="IsVisible">Видимість; презентаційне поле (<c>ФВ-7.2</c>).</param>
/// <param name="Tables">Таблиці аркуша в порядку <c>Ordinal</c>.</param>
public sealed record SheetDto(
    int Id, string Code, LocalizedText NameL10n, int Ordinal,
    string? SheetGroup, bool IsMandatory, bool IsVisible,
    IReadOnlyList<TableDto> Tables);

/// <param name="Id">Ідентифікатор.</param>
/// <param name="Code">Код — ідентичність таблиці й адреса в <c>PUT …/sheets/{sheetCode}/tables/{code}</c>.</param>
/// <param name="NameL10n">
/// Назва таблиці всіма мовами каталогу (`ФВ-2.1`-подібний зріз, `W5.1`). До
/// цього поля тут не було: <c>GET …/structure</c> віддавав таблицю без назви,
/// бо єдиним її творцем був офлайновий генератор тестових даних, якому підпис
/// у веб-формі не був потрібен.
/// </param>
/// <param name="Ordinal">Порядок відображення на аркуші; не ідентичність.</param>
/// <param name="LayoutKind">Розкладка: як періоди лягають на структуру.</param>
/// <param name="RowMode">Спосіб формування рядків.</param>
/// <param name="MaxDynamicRows">Стеля кількості рядків, якщо таблиця приймає додані користувачем.</param>
/// <param name="Columns">Колонки таблиці в порядку <c>Ordinal</c>.</param>
/// <param name="Rows">Рядки таблиці в порядку <c>Ordinal</c>.</param>
public sealed record TableDto(
    int Id, string Code, LocalizedText NameL10n, int Ordinal,
    TableLayoutKind LayoutKind, TableRowMode RowMode,
    int? MaxDynamicRows,
    IReadOnlyList<TemplateColumnDto> Columns,
    IReadOnlyList<TemplateRowDto> Rows);

/// <summary>
/// Колонка в СТРУКТУРІ шаблону — опис для адміністратора.
/// </summary>
/// <remarks>
/// ⚠ Окремий тип від <see cref="ColumnDto"/> з тієї самої причини, що й
/// <see cref="TemplateRowDto"/> від <see cref="RowDto"/> (`Q-012`). Той
/// описує колонку в ЗРІЗІ документа й читається на кожне відкриття таблиці —
/// 60 колонок на зріз, бюджет 400 мс. Тут же потрібен <b>увесь</b>
/// локалізований заголовок: редактор презентаційного шару має показати
/// наявні переклади, інакше правка підпису англійською мовчки стирала б
/// російський і казахський (`ФВ-7.2`). Класти цей об'єкт у зріз означало б
/// платити за нього на найгарячішому шляху системи заради екрана, який
/// відкривають раз на місяць.
/// </remarks>
/// <param name="Id">Ідентифікатор; ним адресується презентаційний патч.</param>
/// <param name="Code">Код колонки — ідентичність, у патчі не змінюється.</param>
/// <param name="HeaderL10n">Заголовок усіма мовами каталогу.</param>
/// <param name="DataType">Тип даних; змінюється лише клоном версії.</param>
/// <param name="Ordinal">Порядок показу; презентаційне поле.</param>
/// <param name="IsReadOnly">Колонка недоступна для введення.</param>
/// <param name="IsRequired">Колонка обов'язкова.</param>
/// <param name="IsHidden">Колонка прихована; презентаційне поле.</param>
/// <param name="DisplayFormat">Формат показу; презентаційне поле.</param>
/// <param name="UnitSymbol">Позначення одиниці, якщо задана.</param>
/// <param name="FormulaExpression">
/// Текст наявної формули колонки, якщо вона є; <c>null</c> — формули ще
/// немає. Додано без нового запиту до бази: <c>GetTemplateStructureHandler</c>
/// читає кешований <c>TemplateVersionSnapshot</c>, який уже вантажить
/// формули (<c>MetadataCache.LoadAsync</c>, «ШОСТИЙ запит — ФОРМУЛИ
/// ШАБЛОНУ») для рушія перерахунку — тут лише додано проєкцію в DTO. До
/// цього поля кнопка "Formula" в редакторі (<c>TemplateVersionPage.tsx</c>)
/// завжди відкривала порожній редактор, навіть коли формула вже збережена:
/// вираз ніде не приходив клієнту без окремого запиту, якого не існувало.
/// </param>
/// <param name="FormulaDialect">
/// Діалект <see cref="FormulaExpression"/>; заповнений лише разом із ним.
/// </param>
public sealed record TemplateColumnDto(
    int Id,
    string Code,
    LocalizedText HeaderL10n,
    string DataType,
    int Ordinal,
    bool IsReadOnly,
    bool IsRequired,
    bool IsHidden,
    string? DisplayFormat,
    string? UnitSymbol,
    string? FormulaExpression,
    ExpressionDialect? FormulaDialect);

/// <summary>
/// Рядок у СТРУКТУРІ шаблону — опис, а не дані.
/// </summary>
/// <remarks>
/// ⚠ Окремий тип від <see cref="RowDto"/> (`Q-012`). Той описує рядок
/// ДОКУМЕНТА і несе <c>Cells</c>, <c>RowVersion</c> та <c>IsOrphaned</c> — усе
/// три належать <c>doc.TableRow</c> і в структурі шаблону не існують:
/// значень там немає, версії рядка немає, а осиротіти може лише посилання в
/// даних. Спільний тип означав би, що половина полів відповіді завжди
/// порожня, і клієнт не міг би відрізнити «немає значення» від «тут значень
/// не буває».
/// </remarks>
/// <param name="RowKey">Стабільна бізнес-ідентичність (`R-B6`).</param>
/// <param name="Ordinal">Порядок відображення; презентаційне поле.</param>
/// <param name="RowKind">Вид рядка: <c>Item</c>, <c>Group</c>, <c>Balance</c>, <c>Note</c>.</param>
/// <param name="Label">Локалізований підпис.</param>
/// <param name="ParentRowKey">Батьківський рядок в ієрархії; <c>null</c> — корінь.</param>
/// <param name="IsReadOnly">Рядок недоступний для введення.</param>
/// <param name="FormulaExpression">
/// Текст наявної формули рядка, якщо вона є; <c>null</c> — формули ще
/// немає. Той самий фікс, що й <see cref="TemplateColumnDto.FormulaExpression"/>.
/// </param>
/// <param name="FormulaDialect">
/// Діалект <see cref="FormulaExpression"/>; заповнений лише разом із ним.
/// </param>
public sealed record TemplateRowDto(
    string RowKey,
    int Ordinal,
    string RowKind,
    string? Label,
    string? ParentRowKey,
    bool IsReadOnly,
    string? FormulaExpression,
    ExpressionDialect? FormulaDialect);
