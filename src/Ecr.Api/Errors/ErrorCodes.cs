namespace Ecr.Api.Errors;

/// <summary>
/// Каталог кодів помилок. Код **стабільний**: клієнт і тести покладаються на
/// нього, тому текст змінювати можна, код — ні.
/// </summary>
public static class ErrorCodes
{
    // Автентифікація і доступ
    public const string Unauthorized = "ECR-AUTH-0401";
    public const string Forbidden = "ECR-AUTH-0403";
    public const string AccountLocked = "ECR-AUTH-0423";
    public const string AccessDenied = "ECR-ACCS-0403";

    // Шаблони і схема
    public const string TemplateNotFound = "ECR-TMPL-0404";
    public const string TemplateFrozen = "ECR-TMPL-0409";
    public const string TemplateInvalid = "ECR-TMPL-0422";
    public const string FormulaCycle = "ECR-TMPL-4221";
    public const string ReferenceUnresolved = "ECR-TMPL-4222";
    public const string UnitMismatch = "ECR-TMPL-4223";
    public const string BreakingChange = "ECR-SCHM-0409";
    public const string GuardedChangeWithoutStrategy = "ECR-SCHM-0422";

    // Документи, рядки, комірки
    public const string DocumentNotFound = "ECR-DOC-0404";
    public const string DocumentSubmitted = "ECR-DOC-0409";
    public const string DocumentCompositionInvalid = "ECR-DOC-0422";
    public const string RowNotFound = "ECR-ROW-0404";
    public const string RowDuplicate = "ECR-ROW-0409";
    public const string CellConflict = "ECR-CELL-0409";
    public const string CellInvalid = "ECR-CELL-0422";
    public const string CellComputed = "ECR-CELL-4221";
    public const string CellOutOfRange = "ECR-CELL-4222";

    // Періоди
    public const string PeriodClosed = "ECR-PRD-0409";
    public const string PeriodOutOfProject = "ECR-PRD-0422";
    public const string ReopenBlockedByPeriod = "ECR-PRD-4223";

    // Реєстри і одиниці
    public const string RegistryEntryNotFound = "ECR-REG-0404";
    public const string RegistryEntryInUse = "ECR-REG-0409";
    public const string RegistrySwitchInOpenPeriod = "ECR-REG-0422";
    public const string UnitDimensionMismatch = "ECR-UOM-0422";
    public const string UnitContextualCoefficient = "ECR-UOM-4221";

    // Розрахунки
    public const string MethodologyFourEyes = "ECR-CALC-0409";
    public const string MethodologyNoGreenTest = "ECR-CALC-0422";
    public const string RecalculateClosedPeriod = "ECR-CALC-4221";

    // Імпорт та інтеграція
    public const string ImportStructureMismatch = "ECR-IMP-0422";
    public const string SourceUnavailable = "ECR-INT-0503";
    public const string SourceUnitChanged = "ECR-INT-0422";

    // Система
    public const string Internal = "ECR-SYS-0500";
    public const string Archiving = "ECR-SYS-0503";
}
