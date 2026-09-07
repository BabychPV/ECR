using Ecr.TestKit;
using Xunit;

namespace Ecr.Scenarios.Tests;

/// <summary>
/// Визначення колекції <c>SqlServer</c> для сценарного шару.
/// </summary>
/// <remarks>
/// ⚠ Колекції xUnit живуть у межах збірки (`tests/Ecr.Api.Tests/SqlServerCollection.cs:10-13`),
/// тому визначення з <c>Ecr.Api.Tests</c> сюди не поширюється — потрібне
/// власне. База при цьому фізично одна на прогін: ім'я виводиться з каталогу
/// збірки (<c>EcrTest_Scenarios_...</c>), а <see cref="SqlServerFixture"/>
/// створює її раз на всю збірку й перестворює з нуля.
///
/// ⛔ Усі сценарії цієї збірки навмисно лежать в ОДНІЙ колекції: xUnit не
/// розпаралелює тести всередині колекції, а сценарії ділять ту саму базу і
/// той самий bootstrap-обліковий запис (`ФВ-6.18`, він єдиний фізично).
/// Паралельний вхід двох сценаріїв бутстрапом одночасно змагався б за той
/// самий рядок і давав би плаваючі падіння, які не мають стосунку до
/// дефектів продукту.
/// </remarks>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
