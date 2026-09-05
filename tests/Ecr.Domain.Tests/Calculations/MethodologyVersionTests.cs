// tests/Ecr.Domain.Tests/Calculations/MethodologyVersionTests.cs
using Ecr.Domain.Abstractions;
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.Enums;
using Ecr.Domain.ValueObjects;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Calculations;

/// <summary>
/// Публікація версії методології — найнебезпечніша операція в системі, бо
/// змінює вже подані числа. Тому правило чотирьох очей перевіряється
/// **системно** (D-40), а не інструкцією.
/// </summary>
public sealed class MethodologyVersionTests
{
    private const int Author = 7;
    private const int Reviewer = 9;

    private static readonly DateTime Now = new(2026, 2, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly From = new(2026, 1, 1);

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_автором_останньої_правки_відхиляється()
    {
        var version = Version();

        var error = Assert.Throws<DomainException>(
            () => version.Publish(Author, "Уточнено коефіцієнт ХСК", From, testsPassed: true, Now));

        // ⛔ Не бюрократія: той, хто писав формулу, дивиться на неї як автор і
        // саме тому не бачить у ній того, що побачить інший. Правило тримається
        // системно — і в базі теж (CK_MV_FourEyes).
        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.False(version.IsPublished);
        Assert.Null(version.PublishedAt);

        // Інший користувач публікує ту саму версію без жодних змін у ній.
        version.Publish(Reviewer, "Уточнено коефіцієнт ХСК", From, testsPassed: true, Now);

        Assert.True(version.IsPublished);
        Assert.Equal(Reviewer, version.PublishedByUserId);
        Assert.Equal(Author, version.CreatedByUserId);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Публікація_без_ChangeReason_неможлива()
    {
        var version = Version();

        var empty = Assert.Throws<DomainException>(
            () => version.Publish(Reviewer, string.Empty, From, testsPassed: true, Now));
        Assert.Equal("ECR-CALC-0422", empty.ErrorCode);

        // ⚠ Пробіли — те саме, що порожньо. Причина потрібна не формі, а тому,
        // хто через півроку звірятиме числа: «оновлення» пояснює рівно нічого,
        // і рядок із пробілів проходив би перевірку на непорожність.
        var blank = Assert.Throws<DomainException>(
            () => version.Publish(Reviewer, "   ", From, testsPassed: true, Now));
        Assert.Equal("ECR-CALC-0422", blank.ErrorCode);

        Assert.False(version.IsPublished);
        Assert.Null(version.ChangeReason);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.12")]
    public void Публікація_без_зеленого_тесту_неможлива()
    {
        var version = Version();

        var error = Assert.Throws<DomainException>(
            () => version.Publish(Reviewer, "Нова методологія", From, testsPassed: false, Now));

        // ⛔ Тести — це дані з очікуваним результатом і допуском (ФВ-13.7).
        // Публікація без них означала б, що правильність чисел перевіряє той,
        // хто відкриє звіт — тобто вже після того, як їх подали.
        Assert.Equal("ECR-CALC-0422", error.ErrorCode);
        Assert.False(version.IsPublished);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    public void Вікна_дії_опублікованих_версій_не_перетинаються()
    {
        var methodology = Methodology();
        var first = Version(methodology, "1.0.0.0");
        var second = Version(methodology, "1.1.0.0");

        methodology.PublishVersion(first, Reviewer, "Базова версія", From, testsPassed: true, Now);

        // ⛔ Друга версія від тієї самої дати. Перетин можливий рівно в один
        // спосіб — кінця вікна не існує, версія чинна до початку наступної, —
        // і саме цей спосіб закритий.
        var error = Assert.Throws<DomainException>(() => methodology.PublishVersion(
            second, Reviewer, "Уточнення", From, testsPassed: true, Now));

        Assert.Equal("ECR-CALC-0409", error.ErrorCode);
        Assert.False(second.IsPublished);

        // Від іншої дати — проходить, і вибір лишається однозначним.
        var later = new DateOnly(2026, 7, 1);
        methodology.PublishVersion(second, Reviewer, "Уточнення", later, testsPassed: true, Now);

        Assert.Same(first, methodology.VersionOn(new DateOnly(2026, 3, 31)));
        Assert.Same(second, methodology.VersionOn(new DateOnly(2026, 7, 1)));
        Assert.Same(second, methodology.VersionOn(new DateOnly(2026, 12, 31)));

        // ⚠ До першої версії не чинна жодна — і це null, а не «візьмемо
        // найранішу». Порахувати період, для якого методології ще не існувало,
        // означало б застосувати до нього правила, ухвалені пізніше.
        Assert.Null(methodology.VersionOn(new DateOnly(2025, 12, 31)));
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.1")]
    public void Опублікована_версія_не_редагується()
    {
        var version = Version();
        version.Publish(Reviewer, "Базова версія", From, testsPassed: true, Now);

        // ⛔ NumericMode і CalendarMode тихо змінюють УСІ числа версії
        // (ФВ-9.9, ФВ-16.11). Правка після публікації означала б, що поданий
        // звіт можна перерахувати інакше, не змінивши жодної формули.
        var modes = Assert.Throws<DomainException>(
            () => version.SetModes(NumericMode.Strict, CalendarMode.Fixed360, TraceLevel.Full));
        Assert.Equal("ECR-CALC-0409", modes.ErrorCode);

        var hash = Assert.Throws<DomainException>(() => version.SetContentHash([1, 2, 3]));
        Assert.Equal("ECR-CALC-0409", hash.ErrorCode);

        // Повторна публікація — теж зміна, і теж відхиляється.
        var again = Assert.Throws<DomainException>(
            () => version.Publish(Reviewer, "Ще раз", From, testsPassed: true, Now));
        Assert.Equal("ECR-CALC-0409", again.ErrorCode);

        Assert.Equal(NumericMode.Legacy, version.NumericMode);
        Assert.Equal(CalendarMode.Actual, version.CalendarMode);
    }

    [Fact] [Trait(TestCategories.Stage, TestCategories.Stage4)]
    [Trait("Requirement", "ФВ-9.9")]
    public void NumericMode_за_замовчуванням_Legacy()
    {
        var version = Version();

        // ⚠ Legacy, а не Strict. Нова версія має рахувати так само, як чинна
        // система, доки хтось свідомо не вирішить інакше: мовчазний перехід на
        // Strict змінив би числа всіх звітів у момент створення версії, яку ще
        // навіть не опублікували (ФВ-9.9).
        Assert.Equal(NumericMode.Legacy, version.NumericMode);

        // Так само з календарем: Actual — фактичні межі періоду, без жодної
        // конвенції, взятої наперед (D-78).
        Assert.Equal(CalendarMode.Actual, version.CalendarMode);
        Assert.Equal(TraceLevel.ErrorsOnly, version.TraceLevel);
        Assert.Equal(TemplateVersionStatus.Draft, version.Status);

        // Змінити режим можна — але свідомо, у чернетці, і це потрапить у diff
        // публікації як обов'язковий пункт.
        version.SetModes(NumericMode.Strict, CalendarMode.Fixed365, TraceLevel.Full);
        Assert.Equal(NumericMode.Strict, version.NumericMode);
    }

    private static Methodology Methodology()
        => new(EcrCode.Create("WATER_DISCHARGE"), Text("Water discharge"));

    private static MethodologyVersion Version(Methodology? owner = null, string number = "1.0.0.0")
    {
        var methodology = owner ?? Methodology();
        var version = new MethodologyVersion(
            methodology.Id, number, CalculationLevel.Configuration, Author, Now);

        methodology.AddVersion(version);
        return version;
    }

    private static LocalizedText Text(string value)
        => new(new Dictionary<string, string> { ["en"] = value });
}
