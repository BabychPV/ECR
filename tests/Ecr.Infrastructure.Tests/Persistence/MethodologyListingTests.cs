// tests/Ecr.Infrastructure.Tests/Persistence/MethodologyListingTests.cs
using Ecr.Domain.Entities.Calculations;
using Ecr.Domain.ValueObjects;
using Ecr.Infrastructure.Persistence;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests.Persistence;

/// <summary>
/// <c>IMethodologyDraftStore.ListActiveIdsAsync</c> — перелік усіх активних
/// методологій, а не тільки тих, кого хтось уже назвав ідентифікатором.
/// </summary>
/// <remarks>
/// ⛔ Знайдено живим прогоном (не сценарієм — жоден не проходив без заданих
/// <c>ids</c>): `GET /api/v1/methodologies` без параметра завжди віддавав
/// порожній перелік, навіть коли методологія щойно створена через
/// `POST /api/v1/methodologies` і справді існує в базі. Причина — метод
/// заводили лише для перегляду за ВЖЕ ВІДОМИМИ ідентифікаторами; порожній
/// список `ids` означав «нічого не цікавить», а не «покажи все». Єдине
/// місце, звідки на щойно заведену методологію можна перейти, — цей перелік:
/// без нього вона лишалася недосяжною з інтерфейсу назавжди.
/// </remarks>
[Collection("SqlServer")]
public sealed class MethodologyListingTests(SqlServerFixture sql)
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage7)]
    [Trait(TestCategories.Category, TestCategories.Integration)]
    public async Task Перелік_активних_включає_методологію_без_жодної_версії()
    {
        var builder = new TestDocumentBuilder(sql.ConnectionString);
        var tag = Guid.NewGuid().ToString("N")[..8];

        int withoutVersionsId;
        int withVersionsId;

        await using (var seed = builder.CreateContext())
        {
            var withoutVersions = new Methodology(EcrCode.Create($"LST_A_{tag}"), Name("no versions yet"));
            var withVersions = new Methodology(EcrCode.Create($"LST_B_{tag}"), Name("has a version"));
            seed.Methodologies.AddRange(withoutVersions, withVersions);
            await seed.SaveChangesAsync();

            withoutVersionsId = withoutVersions.Id;
            withVersionsId = withVersions.Id;
        }

        await using var db = builder.CreateContext();
        var store = new MethodologyDraftStore(db);

        var ids = await store.ListActiveIdsAsync(CancellationToken.None);

        // ⛔ Доказ саме цей: методологія БЕЗ ЖОДНОЇ версії (стан щойно
        // заведеної через `POST /methodologies`, до `PUT .../versions`) має
        // з'явитися в переліку так само, як та, що вже має версію — інакше
        // перелік залишається єдиним місцем системи, куди щойно створений
        // запис не потрапляє.
        Assert.Contains(withoutVersionsId, ids);
        Assert.Contains(withVersionsId, ids);
    }

    private static LocalizedText Name(string en)
        => new(new Dictionary<string, string> { ["en"] = en });
}
