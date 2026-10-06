using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.ValueObjects;

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
    private readonly PeriodKey? _period;
    private readonly TemplateVersionSnapshot _snapshot;
    private readonly Dictionary<int, string> _sheetCodes = [];
    private readonly Dictionary<int, int> _sheetOfTable = [];
    private readonly Dictionary<int, List<int>> _tablesOfSheet = [];
    private readonly Dictionary<int, int> _tableOfColumn = [];
    private readonly Dictionary<int, List<int>> _columnsOfTable = [];
    private readonly Dictionary<int, bool> _tables = [];
    private readonly Dictionary<int, bool> _columns = [];
    private readonly Dictionary<(int TableDefId, string Code), int> _columnByCode = [];

    private DocumentReadScope(AccessProfile profile, int projectId, TemplateVersionSnapshot snapshot, PeriodKey? period)
    {
        _profile = profile;
        _projectId = projectId;
        _period = period;
        _snapshot = snapshot;

        foreach (var sheet in snapshot.Sheets)
        {
            _sheetCodes[sheet.Id] = sheet.Code;
            _tablesOfSheet[sheet.Id] = [.. sheet.Tables.Select(t => t.Id)];

            foreach (var table in sheet.Tables)
            {
                _sheetOfTable[table.Id] = sheet.Id;
                var columns = new List<int>(table.Columns.Count);

                foreach (var column in table.Columns)
                {
                    _tableOfColumn[column.Id] = table.Id;
                    _columnByCode[(table.Id, column.Code)] = column.Id;
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
    /// <remarks>
    /// ⚠ Межі без періоду: роль, звужена періодами (D-214), у них нижче рівня
    /// документа не відкриває нічого — період називає <see cref="InPeriod"/>.
    /// </remarks>
    public static DocumentReadScope For(AccessProfile profile, int projectId, TemplateVersionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(snapshot);

        return new DocumentReadScope(profile, projectId, snapshot, period: null);
    }

    /// <summary>Ті самі межі, але про дані конкретного періоду (D-214).</summary>
    /// <param name="period">Період, про дані якого питають.</param>
    /// <returns>Нові межі; ці лишаються незмінними.</returns>
    /// <remarks>
    /// ⚠ Окремий крок, а не параметр служби: межі без періоду — закриті для
    /// ролі, звуженої періодами, тож шлях, що період знає, мусить його назвати.
    /// Шлях, що не знає (історія комірки, порівняння версій), лишається
    /// закритим — безпечний бік.
    /// </remarks>
    public DocumentReadScope InPeriod(PeriodKey period)
        => new(_profile, _projectId, _snapshot, period);

    /// <summary>Чи бачить профіль ресурс аркуша — з кодом аркуша й періодом (D-214).</summary>
    private bool Readable(int sheetDefId, int tableDefId, int columnDefId)
        => EditRules.CanReadIn(
            _profile, _projectId, sheetDefId, _sheetCodes.GetValueOrDefault(sheetDefId), tableDefId, columnDefId, _period);

    /// <summary>Чи бачить профіль колонку (і, отже, її значення).</summary>
    /// <param name="columnDefId">Колонка версії шаблону.</param>
    public bool CanReadColumn(int columnDefId)
    {
        if (_columns.TryGetValue(columnDefId, out var known))
        {
            return known;
        }

        var readable = _tableOfColumn.TryGetValue(columnDefId, out var tableDefId)
                       && Readable(_sheetOfTable[tableDefId], tableDefId, columnDefId);

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
                       && (Readable(sheetDefId, tableDefId, columnDefId: 0)
                           || _columnsOfTable[tableDefId].Exists(CanReadColumn));

        _tables[tableDefId] = readable;
        return readable;
    }

    /// <summary>
    /// Чи бачить профіль аркуш: перегляд, а не дані — версії, події й журнал, що називають
    /// аркуш (його код, автора, час), показуються лише тому, хто його бачить (L1-18).
    /// </summary>
    /// <param name="sheetDefId">Аркуш версії шаблону.</param>
    /// <remarks>
    /// ⚠ Та сама семантика, що в таблиці: <c>Deny</c> на аркуш (чи проєкт) закриває аркуш; аркуш,
    /// усі таблиці якого закриті (заборона на кожну або на всі їхні колонки), теж закритий — нічого
    /// з його вмісту питальний не бачить. Аркуш без таблиць видимий, коли видимий сам.
    /// Аркуш, невідомий знімку (версія з іншого шаблону), вирішується лише за власною забороною
    /// (код невідомий — звужена ролі область його не відкриває).
    /// </remarks>
    public bool CanReadSheet(int sheetDefId)
    {
        if (!Readable(sheetDefId, tableDefId: 0, columnDefId: 0))
        {
            return false;
        }

        return !_tablesOfSheet.TryGetValue(sheetDefId, out var tables)
               || tables.Count == 0
               || tables.Exists(CanReadTable);
    }

    /// <summary>Те саме за кодом аркуша (журнал погоджень знає код, а не ідентифікатор).</summary>
    /// <param name="sheetCode">Код аркуша.</param>
    /// <remarks>Код, якого знімок не знає, — невидимий (закрито за замовчуванням).</remarks>
    public bool CanReadSheetCode(string sheetCode)
    {
        foreach (var (id, code) in _sheetCodes)
        {
            if (string.Equals(code, sheetCode, StringComparison.Ordinal))
            {
                return CanReadSheet(id);
            }
        }

        return false;
    }

    /// <summary>
    /// Чи бачить профіль місце, про яке говорить повідомлення: таблицю і, коли
    /// названо, колонку за її кодом (так адресують повідомлення валідації).
    /// </summary>
    /// <param name="tableDefId">Таблиця.</param>
    /// <param name="columnCode">Код колонки; <c>null</c> — рівень рядка чи таблиці.</param>
    /// <remarks>Невідомий у таблиці код — невидимий (закрито за замовчуванням).</remarks>
    public bool CanReadAt(int tableDefId, string? columnCode)
        => CanReadTable(tableDefId)
           && (columnCode is null
               || (_columnByCode.TryGetValue((tableDefId, columnCode), out var columnDefId) && CanReadColumn(columnDefId)));

    /// <summary>Коди аркушів структури, яких профіль НЕ бачить, — за зростанням (зведення переліку).</summary>
    public IReadOnlyList<string> HiddenSheetCodes()
        => [.. _sheetCodes.Where(s => !CanReadSheet(s.Key)).Select(s => s.Value).Order(StringComparer.Ordinal)];

    /// <summary>Таблиці структури, яких профіль НЕ бачить, — за зростанням.</summary>
    /// <remarks>
    /// Для шляхів, що рахують межі тут, а застосовують деінде: задача експорту
    /// в черзі не знає користувача, тож межі їдуть у її завданні списком.
    /// </remarks>
    public IReadOnlyList<int> HiddenTableIds()
        => [.. _sheetOfTable.Keys.Where(t => !CanReadTable(t)).Order()];

    /// <summary>Колонки структури, яких профіль НЕ бачить, — за зростанням.</summary>
    public IReadOnlyList<int> HiddenColumnIds()
        => [.. _tableOfColumn.Keys.Where(c => !CanReadColumn(c)).Order()];

    /// <summary>Чи бачить профіль таблицю, якій належить колонка.</summary>
    /// <param name="columnDefId">Колонка версії шаблону.</param>
    /// <remarks>
    /// Для шляхів, що знають лише колонку комірки (зріз подання), а питають про
    /// рядок: рядок належить таблиці, а його ключ — її метадані.
    /// </remarks>
    public bool CanReadTableOf(int columnDefId)
        => _tableOfColumn.TryGetValue(columnDefId, out var tableDefId) && CanReadTable(tableDefId);
}
