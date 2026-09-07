using Ecr.TestKit;
using Xunit;

namespace Ecr.Architecture.Tests;

/// <summary>
/// Перепис вимог мусить сходитися сам із собою.
/// </summary>
/// <remarks>
/// ⛔ Сторож, який звіряв план із заміром, існував — і не бачив нічого, бо
/// звіряв ЧОТИРИ ЧИСЛА з тим самим переписом, який ці числа й породжує.
/// Замір при цьому не сходився в собі: <c>254 · покрито 229 · звільнено 27</c>,
/// тобто <c>229 + 27 = 256</c> при 254 оголошених. Дві вимоги були і покриті,
/// і звільнені водночас, а в знаменнику стояла вимога, якої не існує.
///
/// ⚠ Ці три перевірки — і є «другe джерело», якого сторожеві бракувало. Вони
/// питають не «чи збігається число з числом», а чи внутрішньо несуперечливий
/// сам замір: чи всі згадані вимоги оголошені, чи кошики не перетинаються, чи
/// сходиться сума.
/// </remarks>
public sealed class RequirementCensusTests
{
    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Кожна_згадана_вимога_має_власне_визначення()
    {
        // ⛔ Знаменник рахувався зі ЗГАДОК, і в нього потрапив `ФВ-9.11a` —
        // не вимога, а старий ідентифікатор `B18` із середньої колонки
        // таблиці походження, тобто те, на що цю вимогу колись замінили.
        // Знаменник із неіснуючою вимогою робить відсоток покриття
        // неправдивим у кожному звіті, де він з'являється.
        //
        // ⚠ Перевірка ширша за цей випадок: вона ловить і звичайний одрук у
        // посиланні — «ФВ-9.13» замість «ФВ-9.3» мовчки додало б вимогу.
        var census = RequirementCensus.Take(RepositoryRoot());

        Assert.Empty(census.Phantom);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Жодна_вимога_не_звільнена_і_покрита_водночас()
    {
        // ⛔ Це суперечність, а не подвійний облік: «звільнено» означає
        // «кодом не перевіряється за своєю природою», «покрито» — рівно
        // протилежне. Обрати за людину, який із двох станів правдивий,
        // неможливо, тому сторож падає і називає вимогу поіменно.
        //
        // ⚠ Обидва випадки, які він застав, розв'язалися по-різному, і це
        // доводить, що правила «завжди виграє звільнення» не існує. `ФВ-9.3`
        // («рівень 2 не входить у перший реліз») мала СІМ трейтів на тестах
        // про порядок версій — жоден не про обсяг релізу, трейти прибрані.
        // `ФВ-6.15a` навпаки: звільнення було хибним, бо спостережний
        // наслідок вимоги перевіряється без домену.
        var census = RequirementCensus.Take(RepositoryRoot());

        Assert.Empty(census.Contested);
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage2)]
    [Trait(TestCategories.Category, TestCategories.Architecture)]
    public void Сума_кошиків_дорівнює_оголошеному()
    {
        // ⚠ Тотожність тримається побудовою кошиків, і саме тому її варто
        // стерегти окремо: вона зламається мовчки при першій же зміні
        // `RequirementCensus`, а зведення виглядатиме так само переконливо.
        var census = RequirementCensus.Take(RepositoryRoot());

        Assert.Equal(
            census.Declared,
            census.Covered + census.Exempt + census.Uncovered.Count);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Ecr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Немає Ecr.sln.");
    }
}
