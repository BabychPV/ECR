using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Відкат версії шаблону (<c>ФВ-7.8</c>): <c>Deprecated</c>, але не видалення.
/// </summary>
/// <remarks>
/// ⛔ Стан <c>Deprecated</c> існував від Етапу 1 і був <b>недосяжним</b>:
/// <c>IsStructurallyFrozen</c> його враховував, текст відмови публікації на
/// нього посилався, а перевести версію в цей стан не міг ніхто. Відкат
/// опублікованої версії був неможливий у принципі, і єдиним «відкатом»
/// лишалося видалення — рівно те, що вимога забороняє.
/// </remarks>
public sealed class TemplateVersionDeprecationTests
{
    private static readonly DateTime Now = new(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc);

    private static TemplateVersion Published()
    {
        var version = new TemplateVersion(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);
        version.Publish(publishedByUserId: 8, utcNow: Now);

        return version;
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-7.8")]
    public void Опублікована_версія_виводиться_з_обігу_і_лишається_в_системі()
    {
        var version = Published();

        version.Deprecate(deprecatedByUserId: 9, utcNow: Now.AddDays(1));

        Assert.Equal(TemplateVersionStatus.Deprecated, version.Status);
        Assert.Equal(Now.AddDays(1), version.DeprecatedAt);
        Assert.Equal(9, version.DeprecatedByUserId);

        // ⛔ Дані публікації НЕ стираються: на них посилаються подані форми, і
        // «коли ця версія була в обігу» — питання, на яке аудит має відповісти
        // через роки.
        Assert.Equal(Now, version.PublishedAt);
        Assert.Equal(8, version.PublishedByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    [Trait("Requirement", "ФВ-7.8")]
    public void Виведена_з_обігу_версія_лишається_структурно_замороженою()
    {
        var version = Published();
        version.Deprecate(9, Now);

        // ⚠ Відкат не «розморожує» структуру: документи, заповнені за цією
        // версією, існують і далі, і зміна опису під ними змінила б зміст уже
        // поданих чисел.
        Assert.True(version.IsStructurallyFrozen);
        Assert.Throws<DomainException>(version.EnsureStructurallyMutable);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Чернетку_виводити_з_обігу_нема_від_чого()
    {
        var draft = new TemplateVersion(1, "1.0.0.0", 7, Now);

        var ex = Assert.Throws<DomainException>(() => draft.Deprecate(9, Now));

        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Повторне_виведення_відхиляється()
    {
        var version = Published();
        version.Deprecate(9, Now);

        // ⚠ Друге виведення перезаписало б автора і момент першого — тобто
        // сказало б, що версію вивів не той, хто це зробив насправді.
        Assert.Throws<DomainException>(() => version.Deprecate(10, Now.AddDays(1)));
        Assert.Equal(9, version.DeprecatedByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Виведену_з_обігу_версію_не_можна_опублікувати_знову()
    {
        var version = Published();
        version.Deprecate(9, Now);

        // ⛔ Повернення в обіг — це НОВА версія клоном, а не «розкат назад»:
        // між виведенням і поверненням структура могла розійтися з даними, і
        // мовчазне повернення зробило б це невидимим.
        Assert.Throws<DomainException>(() => version.Publish(8, Now.AddDays(2)));
    }
}
