using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Configuration;
using Ecr.Domain.Enums;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Configuration;

/// <summary>
/// Інваріанти публікації — найважливіші в системі (ФВ-7.1, ФВ-7.2).
/// </summary>
public sealed class TemplateVersionTests
{
    private static readonly DateTime Now = new(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc);

    private static TemplateVersion Draft() => new(templateId: 1, version: "1.0.0.0", createdByUserId: 7, utcNow: Now);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Нова_версія_створюється_у_стані_Draft_з_нульовою_ревізією()
    {
        var version = Draft();

        Assert.Equal(TemplateVersionStatus.Draft, version.Status);
        Assert.Equal(0, version.PresentationRevision);
        Assert.False(version.IsStructurallyFrozen);
        Assert.Null(version.PublishedAt);
        Assert.Null(version.PublishedByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Публікація_фіксує_автора_і_момент()
    {
        var version = Draft();
        var publishedAt = Now.AddDays(3);

        version.Publish(publishedByUserId: 42, utcNow: publishedAt);

        Assert.Equal(TemplateVersionStatus.Published, version.Status);
        Assert.Equal(42, version.PublishedByUserId);
        Assert.Equal(publishedAt, version.PublishedAt);

        // Публікація не є презентаційною правкою: ключ кешу свіжоопублікованої
        // версії має лишитися r0, інакше клієнти вважали б структуру зміненою.
        Assert.Equal(0, version.PresentationRevision);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Повторна_публікація_відхиляється()
    {
        var version = Draft();
        version.Publish(publishedByUserId: 42, utcNow: Now);

        var ex = Assert.Throws<DomainException>(() => version.Publish(publishedByUserId: 43, utcNow: Now.AddDays(1)));
        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);

        // Автор і момент першої публікації не перезаписані спробою другої.
        Assert.Equal(42, version.PublishedByUserId);
        Assert.Equal(Now, version.PublishedAt);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Опублікована_версія_структурно_заморожена()
    {
        var version = Draft();
        Assert.False(version.IsStructurallyFrozen);
        version.EnsureStructurallyMutable();   // у Draft не кидає

        version.Publish(publishedByUserId: 42, utcNow: Now);

        Assert.True(version.IsStructurallyFrozen);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Структурна_зміна_опублікованої_версії_кидає_ECR_TMPL_0409()
    {
        var version = Draft();
        version.Publish(publishedByUserId: 42, utcNow: Now);

        var ex = Assert.Throws<DomainException>(version.EnsureStructurallyMutable);
        Assert.Equal("ECR-TMPL-0409", ex.ErrorCode);

        // Презентаційна правка при цьому лишається дозволеною — саме це
        // розрізнення робить ключ кешу v{id}:r{rev} осмисленим.
        version.ApplyPresentationRevision(1);
        Assert.Equal(1, version.PresentationRevision);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Ключ_кешу_містить_і_версію_і_ревізію_презентації()
    {
        var version = Draft();
        Assert.Equal($"v{version.Id}:r0", version.CacheKey);

        version.Publish(publishedByUserId: 42, utcNow: Now);
        version.ApplyPresentationRevision(1);

        Assert.Equal($"v{version.Id}:r1", version.CacheKey);

        // Ключ мусить змінитися після презентаційної правки — саме тому
        // інвалідація кешу не потрібна: правка створює НОВИЙ ключ (D-16).
        var before = version.CacheKey;
        version.ApplyPresentationRevision(2);
        Assert.NotEqual(before, version.CacheKey);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage1)]
    public void Презентаційна_ревізія_приймається_лише_як_наступна_за_поточною()
    {
        var version = Draft();
        version.Publish(publishedByUserId: 42, utcNow: Now);

        // Перескок уперед означає, що між читанням і записом хтось інший уже
        // інкрементував ревізію, і цю правку ми не бачили.
        Assert.Equal("ECR-TMPL-0409", Assert.Throws<DomainException>(() => version.ApplyPresentationRevision(2)).ErrorCode);

        // Те саме значення — теж розбіжність, а не «нічого не змінилося».
        Assert.Throws<DomainException>(() => version.ApplyPresentationRevision(0));

        // Крок назад неприпустимий і поспіль.
        version.ApplyPresentationRevision(1);
        Assert.Throws<DomainException>(() => version.ApplyPresentationRevision(1));
        Assert.Equal(1, version.PresentationRevision);
    }
}
