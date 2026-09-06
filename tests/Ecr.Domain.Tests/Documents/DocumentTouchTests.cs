// tests/Ecr.Domain.Tests/Documents/DocumentTouchTests.cs
using Ecr.Domain.Entities.Documents;
using Ecr.TestKit;
using Xunit;

namespace Ecr.Domain.Tests.Documents;

/// <summary>
/// Дата й автор останньої зміни документа (<c>H-23d</c>).
/// </summary>
/// <remarks>
/// ⛔ <c>ModifiedAt</c> і <c>ModifiedByUserId</c> існували від першого дня і
/// назавжди лишалися моментом СТВОРЕННЯ: <c>Touch</c> не кликав ніхто. За
/// документом неможливо було ані відсортувати зміни, ані сказати, хто торкався
/// його останнім, — при тому, що обидві колонки видно в переліку кожному
/// користувачеві.
/// </remarks>
public sealed class DocumentTouchTests
{
    private static readonly DateTime Created = new(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Edited = new(2026, 2, 3, 14, 30, 0, DateTimeKind.Utc);

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23d")]
    public void Новий_документ_має_дату_зміни_рівну_даті_створення()
    {
        // ⚠ Це не «порожньо», а свідома рівність: документ, якого ще не
        // правили, змінений тоді ж, коли й створений. `null` тут змусив би
        // кожен екран вигадувати, що показувати замість дати.
        var document = new Document(projectId: 3, "P3-V2-0001", createdByUserId: 9, Created);

        Assert.Equal(Created, document.ModifiedAt);
        Assert.Equal(9, document.ModifiedByUserId);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait("Finding", "H-23d")]
    public void Дотик_переносить_і_дату_і_автора()
    {
        // ⛔ Регресія: `Touch` знову перестає щось міняти — і дата створення
        // видає себе за дату зміни. Це підриває довіру не лише до себе:
        // користувач, який один раз побачив колонку, що бреше, перестає
        // вірити й сусіднім датам на тому самому екрані.
        var document = new Document(projectId: 3, "P3-V2-0001", createdByUserId: 9, Created);

        document.Touch(userId: 42, Edited);

        Assert.Equal(Edited, document.ModifiedAt);
        Assert.Equal(42, document.ModifiedByUserId);

        // Дата створення при цьому НЕ рухається: вона доказова.
        Assert.Equal(Created, document.CreatedAt);
        Assert.Equal(9, document.CreatedByUserId);
    }
}
