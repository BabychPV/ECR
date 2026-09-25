using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// `V-13`: кожна <see cref="DateTime"/>-колонка моделі класифікована рівно
/// одним способом — момент UTC або календарна дата.
/// </summary>
/// <remarks>
/// ⛔ Предмет — ПОБУДОВАНА модель, а не джерело: правило живе в циклі
/// <c>UtcDateTimeColumns.Apply</c>, і лише модель показує, чи дійшов він до
/// кожної властивості. Мутація: прибрати виклик з
/// <c>EcrDbContext.OnModelCreating</c> — і перший тест назве всі ~70 моментів
/// поіменно.
///
/// ⚠ Наскрізну половину (JSON справді несе «Z» на справжньому SQL Server)
/// тримає <c>UtcInstantsInResponsesTests</c> у <c>Ecr.Api.Tests</c>; тут —
/// лише те, що перевіряється без бази.
/// </remarks>
public sealed class UtcDateTimeColumnsTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Directive", "V-13")]
    public void Кожен_момент_часу_читається_з_Kind_Utc_а_календарна_дата_без_зони()
    {
        using var db = Context();

        var offenders = new List<string>();
        var instants = 0;

        foreach (var (entity, property) in DateTimeProperties(db.Model))
        {
            var calendar = UtcDateTimeColumns.CalendarDates.Contains((entity.ClrType, property.Name));
            var converter = property.GetValueConverter();

            if (calendar && converter is not null)
            {
                offenders.Add($"{entity.ClrType.Name}.{property.Name}: календарна дата, але має конвертер {converter.GetType().Name}");
            }
            else if (!calendar && !ReferenceEquals(converter, UtcDateTimeColumns.Converter))
            {
                offenders.Add($"{entity.ClrType.Name}.{property.Name}: момент часу без UTC-конвертера — піде в JSON без «Z»");
            }
            else if (!calendar)
            {
                instants++;
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders.Order(StringComparer.Ordinal)));

        // Порожня модель дала б «жодного порушення» так само.
        Assert.True(instants > 50, $"моментів часу в моделі лише {instants} — модель не зібралася?");
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Directive", "V-13")]
    public void Перелік_календарних_дат_не_містить_застарілих_назв_і_не_пропускає_ValueDate()
    {
        using var db = Context();

        var all = DateTimeProperties(db.Model)
            .Select(x => (x.Entity.ClrType, x.Property.Name))
            .ToHashSet();

        // Перейменована властивість лишила б у переліку мертвий рядок, а сама
        // тихо стала б «моментом UTC».
        var stale = UtcDateTimeColumns.CalendarDates
            .Where(c => !all.Contains(c))
            .Select(c => $"{c.Entity.Name}.{c.Property}")
            .ToList();

        Assert.Empty(stale);

        // `ValueDate` — значення типу `Date` у будь-якій сутності. Нова сутність
        // із таким полем, не внесена в перелік, отримала б «Z» і зсув на добу.
        var unlisted = all
            .Where(x => x.Name == "ValueDate" && !UtcDateTimeColumns.CalendarDates.Contains(x))
            .Select(x => $"{x.ClrType.Name}.{x.Name}")
            .ToList();

        Assert.Empty(unlisted);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    [Trait("Directive", "V-13")]
    public void Конвертер_ставить_Utc_на_читанні_і_не_змінює_значення_на_записі()
    {
        var stored = new DateTime(2026, 9, 24, 12, 48, 47, 85, DateTimeKind.Unspecified);

        var read = (DateTime)UtcDateTimeColumns.Converter.ConvertFromProvider(stored)!;
        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(stored.Ticks, read.Ticks);

        // ⚠ Запис тотожний: зсув «Local → UTC» тут змінив би дані в базі, а
        // домен і так пише `IClock.UtcNow`.
        var utc = new DateTime(2026, 9, 24, 12, 48, 47, DateTimeKind.Utc);
        var written = (DateTime)UtcDateTimeColumns.Converter.ConvertToProvider(utc)!;
        Assert.Equal(utc.Ticks, written.Ticks);

        // І головне, заради чого все: JSON пише такий момент із «Z».
        Assert.EndsWith("Z\"", System.Text.Json.JsonSerializer.Serialize(read), StringComparison.Ordinal);
    }

    private static IEnumerable<(IEntityType Entity, IProperty Property)> DateTimeProperties(IModel model)
        => model.GetEntityTypes()
            .SelectMany(e => e.GetDeclaredProperties().Select(p => (Entity: e, Property: p)))
            .Where(x => (Nullable.GetUnderlyingType(x.Property.ClrType) ?? x.Property.ClrType) == typeof(DateTime))
            .OrderBy(x => x.Entity.ClrType.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Property.Name, StringComparer.Ordinal);

    /// <summary>Контекст без бази: модель будується, з'єднання не відкривається.</summary>
    private static EcrDbContext Context()
        => new(new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer("Server=(guard-does-not-connect);Database=Ecr;Trusted_Connection=True")
            .Options);
}
