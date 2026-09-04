using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ecr.Infrastructure.Persistence.Configurations;

/// <summary>
/// Мапінг <see cref="Domain.ValueObjects.LocalizedText"/> на одну колонку <c>…L10n</c>.
/// </summary>
/// <remarks>
/// Локалізований текст зберігається JSON-рядком
/// <c>{"en":"…","ru":"…","kz":"…"}</c>, а не колонкою на мову: додати мову має
/// означати запис у <c>sys.Language</c>, а не <c>ALTER TABLE</c> у двадцяти
/// таблицях (ФВ-2.2).
/// </remarks>
/// <summary>
/// Конвертер <see cref="Domain.ValueObjects.LocalizedText"/> ↔ JSON-рядок.
/// </summary>
/// <remarks>
/// Реєструється <b>глобальною конвенцією</b>, а не для кожної властивості
/// окремо. Без цього EF намагається зробити з <c>LocalizedText</c> окремий
/// тип сутності і падає на відсутності придатного конструктора — причому
/// падає на першій же сутності, яку ще не встигли налаштувати вручну.
/// </remarks>
public sealed class LocalizedTextConverter()
    : ValueConverter<Domain.ValueObjects.LocalizedText, string>(
        v => v.ToJson(),
        v => Domain.ValueObjects.LocalizedText.FromJson(v));

/// <summary>
/// Порівняння локалізованого тексту за <b>вмістом</b>, а не за посиланням.
/// </summary>
/// <remarks>
/// Без цього EF вважав би зміненою кожну прочитану сутність і писав би
/// зайві <c>UPDATE</c> на кожному <c>SaveChanges</c>.
/// </remarks>
public sealed class LocalizedTextComparer()
    : ValueComparer<Domain.ValueObjects.LocalizedText>(
        (a, b) => a!.ToJson() == b!.ToJson(),
        v => v.ToJson().GetHashCode(StringComparison.Ordinal),
        v => Domain.ValueObjects.LocalizedText.FromJson(v.ToJson()));

public static class LocalizedTextMapping
{
    private static readonly ValueConverter<Domain.ValueObjects.LocalizedText, string> Converter =
        new(v => v.ToJson(), v => Domain.ValueObjects.LocalizedText.FromJson(v));

    // Порівняння за вмістом JSON, а не за посиланням: інакше EF вважав би
    // будь-яке читання зміною і писав би зайві UPDATE.
    private static readonly ValueComparer<Domain.ValueObjects.LocalizedText> Comparer =
        new((a, b) => a!.ToJson() == b!.ToJson(),
            v => v.ToJson().GetHashCode(StringComparison.Ordinal),
            v => Domain.ValueObjects.LocalizedText.FromJson(v.ToJson()));

    /// <summary>Налаштовує властивість локалізованого тексту.</summary>
    public static PropertyBuilder<Domain.ValueObjects.LocalizedText> LocalizedText<TEntity>(
        this EntityTypeBuilder<TEntity> builder,
        Expression<Func<TEntity, Domain.ValueObjects.LocalizedText>> property)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Property(property)
                      .HasConversion(Converter, Comparer)
                      .HasColumnType("nvarchar(max)")
                      .IsRequired();
    }
}
