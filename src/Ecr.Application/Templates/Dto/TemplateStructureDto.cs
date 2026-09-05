// src/Ecr.Application/Templates/Dto/TemplateStructureDto.cs

using Ecr.Application.Documents.Dto;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Application.Templates.Dto;

/// <summary>
/// Структура опублікованої версії — те, що віддається клієнту й кешується за
/// ключем <c>v{id}:r{rev}</c> (`ФВ-2.5`).
/// </summary>
public sealed record TemplateStructureDto(
    int TemplateVersionId,
    int PresentationRevision,
    IReadOnlyList<SheetDto> Sheets);

public sealed record SheetDto(
    int Id, string Code, LocalizedText NameL10n, int Ordinal,
    IReadOnlyList<TableDto> Tables);

public sealed record TableDto(
    int Id, string Code, TableLayoutKind LayoutKind, TableRowMode RowMode,
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
    string? UnitSymbol);

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
public sealed record TemplateRowDto(
    string RowKey,
    int Ordinal,
    string RowKind,
    string? Label,
    string? ParentRowKey,
    bool IsReadOnly);
