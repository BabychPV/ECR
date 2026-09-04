// src/Ecr.Domain/ValueObjects/RowKey.cs

using System.Text.RegularExpressions;

namespace Ecr.Domain.ValueObjects;

/// <summary>
/// Стабільна бізнес-ідентичність рядка. Для <c>RowMode = Fixed</c> береться з
/// <c>cfg.RowDef.RowKey</c>, для <c>Dynamic</c> — GUID у форматі "N" (ФВ-2.5).
/// На відміну від <see cref="EcrCode"/> допускає цифрові ключі («7001001»).
/// </summary>
public readonly partial record struct RowKey
{
    public const string Pattern = @"^[A-Za-z0-9_.\-]{1,100}$";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex Validator();

    public string Value { get; }

    private RowKey(string value) => Value = value;

    public static RowKey Create(string value)
        => TryCreate(value, out var key)
            ? key
            : throw new ArgumentException($"RowKey '{value}' не відповідає шаблону {Pattern}.", nameof(value));

    public static bool TryCreate(string? value, out RowKey key)
    {
        if (!string.IsNullOrEmpty(value) && Validator().IsMatch(value))
        {
            key = new RowKey(value);
            return true;
        }
        key = default;
        return false;
    }

    /// <summary>Новий ключ для динамічного рядка.</summary>
    public static RowKey NewDynamic() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
    public static implicit operator string(RowKey k) => k.Value;
}
