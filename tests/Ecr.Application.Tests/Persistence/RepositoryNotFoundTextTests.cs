using Ecr.Application.Errors;
using Ecr.Domain.Entities.Configuration;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Application.Tests.Persistence;

/// <summary>
/// Узагальнений репозиторій називає СУТНІСТЬ, а не клас .NET.
/// </summary>
/// <remarks>
/// ⛔ Дефект, який цей файл тримає закритим: повідомлення будувалося як
/// <c>$"{typeof(T).Name} з ідентифікатором {id} не знайдено."</c>, тобто до
/// клієнта їхало «TemplateVersion з ідентифікатором 5 не знайдено». У
/// продукті немає сутності «TemplateVersion» — є «версія шаблону». Для
/// оператора це не назва нічого: він не може ані зрозуміти відмову, ані
/// переказати її підтримці інакше, як цитатою внутрішнього імені типу.
///
/// ⚠ Перевіряється саме ВЕРСІЯ ШАБЛОНУ, і це не випадковий вибір: у версії й
/// у самого шаблона код помилки СПІЛЬНИЙ (<c>ECR-TMPL-0404</c>). Якби ключ
/// каталогу був один на код, тут прийшло б «не знайдено шаблон» там, де
/// немає ВЕРСІЇ, — правдоподібно й неправильно, тобто рівно той самий клас
/// дефекту з іншого боку.
///
/// ⚠ Тест дивиться на сам виняток, а не на відповідь HTTP: резолвінг ключа
/// каталогу вже доведений окремо (<c>NotFoundLocalizedErrorTests</c>), і
/// повторювати конвеєр тут означало б перевіряти вдруге те саме, додавши
/// залежність від middleware.
/// </remarks>
[Collection("SqlServer")]
public sealed class RepositoryNotFoundTextTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Відсутня_версія_шаблону_називається_версією_а_не_іменем_класу()
    {
        await using var db = sql.CreateContext();
        var repository = new Repository<TemplateVersion, int>(db);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => repository.GetAsync(int.MaxValue, CancellationToken.None));

        // ⛔ Головне твердження: імені класу .NET у тексті для людини немає.
        Assert.DoesNotContain("TemplateVersion", error.Message, StringComparison.Ordinal);
        Assert.Contains("версію шаблону", error.Message, StringComparison.Ordinal);

        // Контракт не змінився: код і родина ті самі, що й були.
        Assert.Equal("ECR-TMPL-0404", error.ErrorCode);

        // ⚠ Ключ ОКРЕМИЙ для версії — не спільний із шаблоном, хоч код один.
        Assert.NotNull(error.Details);
        Assert.Equal(
            "err.ECR-TMPL-0404.templateVersion",
            Assert.Contains("messageKey", error.Details));

        // ⚠ Ідентифікатор іде РЯДКОМ і під тим іменем, яке вживає шаблон
        // каталогу: `ResolveGenericMessageAsync` підставляє лише `string`, тож
        // число лишилося б незаміненим плейсхолдером просто в тексті.
        Assert.Equal(
            int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assert.Contains("versionId", error.Details));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    public async Task Відсутній_документ_переживає_спільний_ключ_із_іншими_шляхами()
    {
        // ⚠ Зворотний бік: для документа ключ і плейсхолдер узято НАЯВНІ
        // (`err.ECR-DOC-0404.document`, `{documentId}`), а не заведено нові.
        // Той самий факт мусить читатися однаково, яким би шляхом код до нього
        // не дійшов; новий ключ дав би дві різні англійські фрази на одну
        // подію.
        await using var db = sql.CreateContext();
        var repository = new Repository<Ecr.Domain.Entities.Documents.Document, long>(db);

        var error = await Assert.ThrowsAsync<NotFoundException>(
            () => repository.GetAsync(long.MaxValue, CancellationToken.None));

        Assert.Equal("ECR-DOC-0404", error.ErrorCode);
        Assert.NotNull(error.Details);
        Assert.Equal("err.ECR-DOC-0404.document", Assert.Contains("messageKey", error.Details));
        Assert.Equal(
            long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Assert.Contains("documentId", error.Details));
    }
}
