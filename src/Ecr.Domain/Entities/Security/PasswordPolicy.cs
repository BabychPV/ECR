// src/Ecr.Domain/Entities/Security/PasswordPolicy.cs
using Ecr.Domain.Abstractions;

namespace Ecr.Domain.Entities.Security;

/// <summary>
/// Політика паролів для локальних облікових записів (ФВ-6.4).
/// </summary>
/// <remarks>
/// У першому релізі діє **базова** політика (ФВ-6.4a, D-104): довжина і
/// блокування після N невдач. Складність, історія і строк дії лишаються
/// полями, але не вмикаються, доки ІБ не дасть формулювання (`P-1`).
/// </remarks>
public sealed class PasswordPolicy : Entity<int>
{
    private PasswordPolicy() { }

    public PasswordPolicy(string code, int minLength, int maxFailedAttempts)
    {
        Code = code;
        MinLength = minLength;
        MaxFailedAttempts = maxFailedAttempts;
    }

    public string Code { get; private set; } = null!;
    public int MinLength { get; private set; }
    public int MaxFailedAttempts { get; private set; }
    public int LockoutMinutes { get; private set; }

    // ⚠ Складність — це ТРИ окремі прапорці, а не один RequireComplexity
    // (`Q-040`). Різниця не косметична: «складний пароль» без розкладки на
    // вимоги неможливо ні показати користувачеві, ні перевірити однозначно.
    public bool RequireUpper { get; private set; }
    public bool RequireDigit { get; private set; }
    public bool RequireSpecial { get; private set; }

    /// <summary>Строк дії в днях; <c>null</c> — не діє (ФВ-6.4a, `P-1`).</summary>
    public int? ExpirationDays { get; private set; }
}
