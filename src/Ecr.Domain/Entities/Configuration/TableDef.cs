using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Configuration;

/// <summary>Таблиця на аркуші.</summary>
public sealed class TableDef : Entity<int>
{
    private readonly List<ColumnDef> _columns = [];
    private readonly List<RowDef> _rows = [];
    private readonly List<FormulaDef> _formulas = [];
    private readonly List<ValidationRule> _validationRules = [];

    private TableDef() { }

    public TableDef(int sheetDefId, EcrCode code, LocalizedText name, int ordinal,
                    TableLayoutKind layoutKind, TableRowMode rowMode)
    {
        SheetDefId = sheetDefId;
        Code = code.Value;
        NameL10n = name;
        Ordinal = ordinal;
        LayoutKind = layoutKind;
        RowMode = rowMode;
        StorageMode = CellStorageMode.Normalized;
    }

    public int SheetDefId { get; private set; }
    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;
    public int Ordinal { get; private set; }
    public TableLayoutKind LayoutKind { get; private set; }
    public TableRowMode RowMode { get; private set; }
    public int? MaxDynamicRows { get; private set; }
    public int? HeaderStyleId { get; private set; }

    /// <summary>
    /// Фізична модель зберігання комірок саме цієї таблиці. Перехід на гібрид
    /// **вибірковий** — глобальний перехід не потрібен і надто дорогий (D-21).
    /// </summary>
    public CellStorageMode StorageMode { get; private set; }

    public bool IsDeleted { get; private set; }
    public DateTime? DeletedAt { get; private set; }
    public int? DeletedByUserId { get; private set; }

    public IReadOnlyList<ColumnDef> Columns => _columns;
    public IReadOnlyList<RowDef> Rows => _rows;

    /// <summary>Чи може користувач додавати рядки.</summary>
    public bool AllowsDynamicRows => RowMode is TableRowMode.Dynamic or TableRowMode.Mixed;

    /// <summary>Перемикає модель зберігання за результатом гейта Етапу 0.</summary>
    public void SwitchStorage(CellStorageMode mode) => StorageMode = mode;

    /// <summary>
    /// Змінює назву таблиці.
    /// </summary>
    /// <remarks>
    /// ⚠ <c>NameL10n</c> — поле презентаційного шару
    /// (<see cref="Services.ChangeClassifier.PresentationFields"/> уже містить
    /// <c>TableDef.NameL10n</c> — саме на це посилання цей сеттер і чекав).
    /// Той самий метод, а не дві копії, покликаний обслуговувати і
    /// <c>PUT …/tables/{code}</c> (структурна правка чернетки), і майбутній
    /// <c>PATCH …/presentation</c> (опублікована версія) — за зразком
    /// <see cref="SheetDef.Rename"/>.
    /// </remarks>
    public void Rename(LocalizedText name)
    {
        ArgumentNullException.ThrowIfNull(name);
        NameL10n = name;
    }

    /// <summary>Змінює порядок — та сама презентаційна операція, що й <see cref="SheetDef.Reorder"/>.</summary>
    public void Reorder(int ordinal) => Ordinal = ordinal;

    /// <summary>
    /// Змінює розкладку таблиці.
    /// </summary>
    /// <remarks>
    /// ⛔ Дозволена в чернетці без окремого узгодження з <see cref="RowMode"/>:
    /// обидва поля структурні, але жодне не є ідентичністю
    /// (<see cref="Services.ChangeClassifier.IdentityFields"/> їх не містить) —
    /// тобто зміна сама собою не Breaking за ФВ-7.4. Чи руйнівна вона з
    /// документами на версії, вирішує загальна класифікація зміни в
    /// обробнику (<c>ChangeClassifier.Classify</c>), а не ця сутність.
    /// </remarks>
    public void SetLayout(TableLayoutKind layoutKind) => LayoutKind = layoutKind;

    /// <summary>
    /// Змінює спосіб формування рядків.
    /// </summary>
    /// <remarks>
    /// ⚠ Не звіряє сумісність із наявними <see cref="Rows"/>: колонки й рядки
    /// заводить наступний зріз (<c>ColumnDef</c>/<c>RowDef</c>), тож на момент,
    /// коли цей сеттер узагалі можна викликати повторно, рядків ще немає.
    /// <c>MaxDynamicRows</c> перевіряється окремо в
    /// <see cref="SetMaxDynamicRows"/> — обробник викликає її одразу після
    /// цього сеттера з фінальним <see cref="RowMode"/>, а не покладається на
    /// побічну перевірку тут.
    /// </remarks>
    public void SetRowMode(TableRowMode rowMode) => RowMode = rowMode;

    /// <summary>Логічне видалення: фізично запис лишається, бо на нього посилаються дані (ФВ-7.6).</summary>
    public void SoftDelete(int userId, DateTime utcNow)
    {
        IsDeleted = true;
        DeletedAt = utcNow;
        DeletedByUserId = userId;
    }

    /// <summary>
    /// Стеля кількості динамічних рядків.
    /// </summary>
    /// <remarks>
    /// Не формальність: зріз на 500×60 має бюджет 1.5 с, і таблиця, яка
    /// непомітно виросла до тисяч рядків, вибиває його для всіх.
    /// </remarks>
    /// <exception cref="DomainException">Таблиця не динамічна або межа не додатна.</exception>
    public void SetMaxDynamicRows(int? max)
    {
        if (max is { } m && m <= 0)
        {
            throw new DomainException("ECR-TMPL-0422", $"MaxDynamicRows має бути додатним; отримано {m}.");
        }

        // ⚠ Стеля має сенс скрізь, де рядки додає користувач, — тобто і в
        // `Mixed`. Саме там вона потрібна найбільше: до динамічних рядків
        // додаються ще й фіксовані, і бюджет читання зрізу вибирається швидше.
        if (max is not null && !AllowsDynamicRows)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"MaxDynamicRows має сенс лише там, де рядки додає користувач; у таблиці {Code} режим {RowMode}.");
        }

        MaxDynamicRows = max;
    }

    /// <summary>Додає колонку.</summary>
    /// <exception cref="DomainException">Колонка з таким кодом уже є.</exception>
    public void AddColumn(ColumnDef column)
    {
        ArgumentNullException.ThrowIfNull(column);

        if (_columns.Any(c => string.Equals(c.Code, column.Code, StringComparison.Ordinal)))
        {
            throw new DomainException(
                "ECR-TMPL-0409", $"Колонка з кодом {column.Code} у таблиці {Code} уже існує.");
        }

        _columns.Add(column);
    }

    /// <summary>Формули таблиці.</summary>
    /// <remarks>
    /// ⚠ Формули належать ТАБЛИЦІ, а не аркушу чи версії: саме так їх адресує
    /// схема (<c>cfg.FormulaDef.TableDefId</c>), і саме таблиця дає контекст
    /// скороченим формам посилань — <c>[Jan]</c> без коду таблиці означає
    /// «колонка цієї таблиці» (02b §3.1).
    /// </remarks>
    public IReadOnlyList<FormulaDef> Formulas => _formulas;

    /// <summary>Додає формулу.</summary>
    /// <exception cref="DomainException">Таблиця вже опублікована.</exception>
    public void AddFormula(FormulaDef formula)
    {
        ArgumentNullException.ThrowIfNull(formula);
        _formulas.Add(formula);
    }

    /// <summary>Правила валідації таблиці.</summary>
    /// <remarks>
    /// Належать таблиці за схемою (<c>cfg.ValidationRule.TableDefId</c>).
    /// Правило рівня комірки додатково вказує <c>ColumnDefId</c>.
    /// </remarks>
    public IReadOnlyList<ValidationRule> ValidationRules => _validationRules;

    /// <summary>Додає правило валідації.</summary>
    public void AddValidationRule(ValidationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        _validationRules.Add(rule);
    }

    /// <summary>Додає рядок фіксованої таблиці.</summary>
    /// <exception cref="DomainException">Рядок із таким ключем уже є, або таблиця динамічна.</exception>
    public void AddRow(RowDef row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (RowMode == TableRowMode.Dynamic)
        {
            throw new DomainException(
                "ECR-TMPL-0422",
                $"Таблиця {Code} динамічна: її рядки створюються під час роботи, а не в шаблоні.");
        }

        if (_rows.Any(r => string.Equals(r.RowKeyValue, row.RowKeyValue, StringComparison.Ordinal)))
        {
            throw new DomainException(
                "ECR-TMPL-0409", $"Рядок із ключем {row.RowKeyValue} у таблиці {Code} уже існує.");
        }

        _rows.Add(row);
    }
}
