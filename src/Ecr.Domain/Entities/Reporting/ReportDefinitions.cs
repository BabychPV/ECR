// src/Ecr.Domain/Entities/Reporting/ReportDefinitions.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;

namespace Ecr.Domain.Entities.Reporting;

/// <summary>Опис звіту (<c>rpt.ReportDef</c>).</summary>
/// <remarks>
/// <see cref="IsRegulatory"/> — не позначка «важливий». Регуляторна вʼюха
/// віддає **лише** <c>Approved</c> і <c>Submitted</c> (D-65), і саме цей
/// прапорець вирішує, який фільтр піде у вʼюху.
/// </remarks>
public sealed class ReportDef : Entity<int>
{
    private ReportDef() { }

    /// <summary>Створює опис звіту.</summary>
    /// <param name="code">Код звіту.</param>
    /// <param name="name">Назва мовами каталогу.</param>
    /// <param name="isRegulatory">Чи звіт іде регулятору.</param>
    public ReportDef(EcrCode code, LocalizedText name, bool isRegulatory)
    {
        Code = code.Value;
        NameL10n = name;
        IsRegulatory = isRegulatory;
        IsActive = true;
    }

    public string Code { get; private set; } = null!;
    public LocalizedText NameL10n { get; private set; } = null!;

    /// <summary>Чи звіт іде регулятору: від цього залежить фільтр статусів (D-65).</summary>
    public bool IsRegulatory { get; private set; }

    public bool IsActive { get; private set; }

    /// <summary>Виводить звіт з обігу; описи не видаляються.</summary>
    public void Deactivate() => IsActive = false;
}

/// <summary>Версія звіту (<c>rpt.ReportVersion</c>).</summary>
/// <remarks>
/// Колонки і правила — **дані** (<see cref="ColumnsJson"/>,
/// <see cref="RulesJson"/>), а не код: звіт має змінюватися конфігуратором,
/// а не релізом.
/// </remarks>
public sealed class ReportVersion : Entity<int>
{
    private ReportVersion() { }

    /// <summary>Створює версію звіту.</summary>
    /// <param name="reportDefId">Опис звіту.</param>
    /// <param name="version">Номер версії.</param>
    /// <param name="columnsJson">Опис колонок.</param>
    /// <param name="rulesJson">Правила відбору рядків.</param>
    /// <param name="utcNow">Час створення в UTC.</param>
    public ReportVersion(
        int reportDefId, string version, string columnsJson, string rulesJson, DateTime utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        ReportDefId = reportDefId;
        Version = version;
        ColumnsJson = columnsJson;
        RulesJson = rulesJson;
        CreatedAt = utcNow;
        Status = TemplateVersionStatus.Draft;
    }

    public int ReportDefId { get; private set; }
    public string Version { get; private set; } = null!;
    public TemplateVersionStatus Status { get; private set; }
    public string ColumnsJson { get; private set; } = null!;
    public string RulesJson { get; private set; } = null!;
    public DateTime CreatedAt { get; private set; }

    /// <summary>Публікує версію звіту.</summary>
    /// <exception cref="DomainException">Версія вже не чернетка.</exception>
    public void Publish()
    {
        if (Status != TemplateVersionStatus.Draft)
        {
            throw new DomainException(
                "ECR-RPT-0409", $"Версія звіту {Version} у стані {Status}: публікувати нічого.");
        }

        Status = TemplateVersionStatus.Published;
    }
}

/// <summary>
/// Побудований зріз звіту (<c>rpt.ReportSnapshot</c>).
/// </summary>
/// <remarks>
/// ⚠ **Статус успадковується від ДАНИХ** (D-65), а не задається окремо:
/// зріз не може бути «затвердженим», якщо затверджені не всі його аркуші.
/// Окреме поле статусу зрізу стало б другим джерелом істини і рано чи пізно
/// показало б регулятору <c>Approved</c> на чернетці.
/// </remarks>
public sealed class ReportSnapshot : Entity<long>
{
    private ReportSnapshot() { }

    /// <summary>Створює зріз.</summary>
    /// <param name="reportVersionId">Версія звіту.</param>
    /// <param name="projectId">Проєкт.</param>
    /// <param name="periodKey">Період; <c>null</c> — увесь рік.</param>
    /// <param name="status">Статус, успадкований від даних.</param>
    /// <param name="utcNow">Час побудови в UTC.</param>
    /// <param name="builtByUserId">Хто побудував; <c>null</c> — за розкладом.</param>
    public ReportSnapshot(
        int reportVersionId,
        int projectId,
        int? periodKey,
        DocumentStatus status,
        DateTime utcNow,
        int? builtByUserId)
    {
        ReportVersionId = reportVersionId;
        ProjectId = projectId;
        PeriodKey = periodKey;
        Status = status;
        BuiltAt = utcNow;
        BuiltByUserId = builtByUserId;
    }

    public int ReportVersionId { get; private set; }
    public int ProjectId { get; private set; }
    public int? PeriodKey { get; private set; }
    public string? ParametersJson { get; private set; }
    public long? CalculationRunId { get; private set; }

    /// <summary>Статус ДАНИХ зрізу; регуляторна вʼюха бере лише два (D-65).</summary>
    public DocumentStatus Status { get; private set; }

    /// <summary>Чи це поточний зріз для пари «версія × проєкт × період».</summary>
    public bool IsCurrent { get; private set; }

    /// <summary>Скільки рядків у зрізі.</summary>
    public int RowCount { get; private set; }

    public byte[]? ContentHash { get; private set; }
    public DateTime BuiltAt { get; private set; }
    public int? BuiltByUserId { get; private set; }

    /// <summary>Фіксує підсумок побудови.</summary>
    /// <param name="rowCount">Скільки рядків.</param>
    /// <param name="contentHash">Контрольна сума вмісту.</param>
    /// <param name="calculationRunId">Прогін, з якого взяті числа.</param>
    /// <param name="parametersJson">Параметри побудови.</param>
    public void Complete(int rowCount, byte[]? contentHash, long? calculationRunId, string? parametersJson)
    {
        RowCount = rowCount;
        ContentHash = contentHash;
        CalculationRunId = calculationRunId;
        ParametersJson = parametersJson;
    }

    /// <summary>Робить зріз поточним.</summary>
    public void MakeCurrent() => IsCurrent = true;

    /// <summary>Знімає поточність — попередній зріз лишається читабельним.</summary>
    /// <remarks>
    /// ⛔ Зрізи **накопичуються**, а не перезаписуються: поданий зріз не
    /// перебудовується ніколи (ФВ-9.17), і старий має лишатися доказом того,
    /// що саме бачив регулятор.
    /// </remarks>
    public void Supersede() => IsCurrent = false;
}

/// <summary>Рядок зрізу звіту (<c>rpt.ReportRow</c>).</summary>
/// <remarks>
/// Зберігається «довгим» форматом (<c>рядок × колонка</c>), а не широким:
/// склад колонок задає версія звіту і змінюється конфігуратором, а таблиця з
/// фіксованими колонками вимагала б міграції на кожну правку звіту.
/// </remarks>
public sealed class ReportRow
{
    private ReportRow() { }

    /// <summary>Створює комірку зрізу.</summary>
    /// <param name="snapshotId">Зріз.</param>
    /// <param name="rowNo">Номер рядка.</param>
    /// <param name="columnCode">Код колонки.</param>
    public ReportRow(long snapshotId, int rowNo, string columnCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnCode);

        SnapshotId = snapshotId;
        RowNo = rowNo;
        ColumnCode = columnCode;
    }

    public long SnapshotId { get; private set; }
    public int RowNo { get; private set; }
    public string ColumnCode { get; private set; } = null!;

    public string? ValueString { get; private set; }

    /// <summary><c>decimal(28,10)</c>; <c>float</c> заборонений (D-30).</summary>
    public decimal? ValueNumeric { get; private set; }

    public DateTime? ValueDate { get; private set; }

    /// <summary>Записує значення комірки зрізу.</summary>
    /// <param name="valueString">Текст.</param>
    /// <param name="valueNumeric">Число.</param>
    /// <param name="valueDate">Дата.</param>
    public void SetValue(string? valueString, decimal? valueNumeric, DateTime? valueDate)
    {
        ValueString = valueString;
        ValueNumeric = valueNumeric;
        ValueDate = valueDate;
    }
}
