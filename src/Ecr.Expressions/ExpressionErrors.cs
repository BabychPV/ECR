namespace Ecr.Expressions;

/// <summary>
/// Коди помилок, які повертає рушій виразів.
/// </summary>
/// <remarks>
/// ⚠ Значення продубльовані з каталогу <c>Ecr.Api.Errors.ErrorCodes</c>
/// навмисно: <c>Ecr.Expressions</c> не має права посилатися на шар API
/// (архітектурне правило «залежності лише всередину»). За збігом стежить
/// перевірка унікальності кодів у <c>ContractIntegrityTests</c>.
/// </remarks>
public static class ExpressionErrors
{
    /// <summary>Синтаксис, невідома функція, невірна кількість аргументів.</summary>
    public const string Syntax = "ECR-TMPL-0422";

    /// <summary>Цикл у графі залежностей формул.</summary>
    public const string Cycle = "ECR-TMPL-4221";

    /// <summary>Посилання не резолвиться або тип несумісний.</summary>
    public const string Unresolved = "ECR-TMPL-4222";

    /// <summary>Несумісні одиниці без явного <c>CONVERT</c>.</summary>
    public const string UnitMismatch = "ECR-TMPL-4223";

    // ——— помилки-ЗНАЧЕННЯ (02b §6.4): вони живуть у комірці, а не в діагностиці ———

    /// <summary>Ділення на нуль або на <c>null</c>.</summary>
    public const string DivideByZero = "#DIV/0";

    /// <summary>Посилання не резолвиться в рантаймі (рядок видалено).</summary>
    public const string BadReference = "#REF";

    /// <summary>Несумісні типи, які не вдалося відсіяти при публікації.</summary>
    public const string BadValue = "#VALUE";

    /// <summary>Несумісні одиниці в рантаймі.</summary>
    public const string BadUnit = "#UNIT";

    /// <summary>Цикл, виявлений у рантаймі — аварійний випадок.</summary>
    public const string RuntimeCycle = "#CYCLE";
}
