using Ecr.Domain.Entities.Configuration;

namespace Ecr.Application.Security;

/// <summary>
/// Що з документа профіль БАЧИТЬ нижче рівня проєкту: таблиці й колонки (S6).
/// </summary>
/// <remarks>
/// ⛔ S6 (enterprise-аудит безпеки, 2026-09-28). До цього читання знало лише
/// рівень проєкту, тож <c>IsDeny</c> на аркуш, таблицю чи колонку сірив
/// редагування, а значення все одно віддавались — зрізом таблиці й
/// порівнянням версій. ФВ-6.6: «<c>IsDeny</c> виграє завжди, на будь-якому рівні».
///
/// ⚠ Рішення по кожному ресурсу приймає <see cref="EditRules.CanRead"/> — те
/// саме правило, що й для запису, з порогом <c>Read</c>. Тут лише обхід
/// структури шаблону й пам'ять уже прийнятих рішень: зріз питає про кожну
/// комірку, а колонок у таблиці десятки.
///
/// ⚠ Невідома знімку колонка чи таблиця — НЕ видима (закрито за замовчуванням):
/// значення, якому структура не знає місця, показувати нема куди і нема чому.
///
/// ⚠ Рішення про проєкт тут не повторюється як окремий крок: <see cref="EditRules.CanRead"/>
/// і так перевіряє заборону й грант проєкту, а викликачі до того вже
/// відмовили невидимому документу (<c>DocumentVisibility</c>).
/// </remarks>
public sealed class DocumentReadScope
{
    private readonly AccessProfile _profile;
    private readonly int _projectId;
    private readonly Dictionary<int, int> _sheetOfTable = [];
    private readonly Dictionary<int, int> _tableOfColumn = [];
    private readonly Dictionary<int, List<int>> _columnsOfTable = [];
    private readonly Dictionary<int, bool> _tables = [];
    private readonly Dictionary<int, bool> _columns = [];

    private DocumentReadScope(AccessProfile profile, int projectId, TemplateVersionSnapshot snapshot)
    {
        _profile = profile;
        _projectId = projectId;

        foreach (var sheet in snapshot.Sheets)
        {
            foreach (var table in sheet.Tables)
            {
                _sheetOfTable[table.Id] = sheet.Id;
                var columns = new List<int>(table.Columns.Count);

                foreach (var column in table.Columns)
                {
                    _tableOfColumn[column.Id] = table.Id;
                    columns.Add(column.Id);
                }

                _columnsOfTable[table.Id] = columns;
            }
        }
    }

    /// <summary>Будує межі читання документа для профілю.</summary>
    /// <param name="profile">Профіль прав.</param>
    /// <param name="projectId">Проєкт документа.</param>
    /// <param name="snapshot">Знімок структури версії шаблону документа.</param>
    public static DocumentReadScope For(AccessProfile profile, int projectId, TemplateVersionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(snapshot);

        return new DocumentReadScope(profile, projectId, snapshot);
    }

    /// <summary>Чи бачить профіль колонку (і, отже, її значення).</summary>
    /// <param name="columnDefId">Колонка версії шаблону.</param>
    public bool CanReadColumn(int columnDefId)
    {
        if (_columns.TryGetValue(columnDefId, out var known))
        {
            return known;
        }

        var readable = _tableOfColumn.TryGetValue(columnDefId, out var tableDefId)
                       && EditRules.CanRead(_profile, _projectId, _sheetOfTable[tableDefId], tableDefId, columnDefId);

        _columns[columnDefId] = readable;
        return readable;
    }

    /// <summary>Чи бачить профіль таблицю цілком.</summary>
    /// <param name="tableDefId">Таблиця версії шаблону.</param>
    /// <remarks>
    /// ⚠ Таблиця видима, коли видима вона сама АБО бодай одна її колонка:
    /// дрібніший грант на колонку може відкрити її під таблицею, де
    /// найдрібніший оголошений рівень — <c>None</c>. Заборона ж на аркуш чи
    /// таблицю закриває і таблицю, і кожну її колонку.
    /// </remarks>
    public bool CanReadTable(int tableDefId)
    {
        if (_tables.TryGetValue(tableDefId, out var known))
        {
            return known;
        }

        var readable = _sheetOfTable.TryGetValue(tableDefId, out var sheetDefId)
                       && (EditRules.CanRead(_profile, _projectId, sheetDefId, tableDefId, columnDefId: 0)
                           || _columnsOfTable[tableDefId].Exists(CanReadColumn));

        _tables[tableDefId] = readable;
        return readable;
    }

    /// <summary>Чи бачить профіль таблицю, якій належить колонка.</summary>
    /// <param name="columnDefId">Колонка версії шаблону.</param>
    /// <remarks>
    /// Для шляхів, що знають лише колонку комірки (зріз подання), а питають про
    /// рядок: рядок належить таблиці, а його ключ — її метадані.
    /// </remarks>
    public bool CanReadTableOf(int columnDefId)
        => _tableOfColumn.TryGetValue(columnDefId, out var tableDefId) && CanReadTable(tableDefId);
}
