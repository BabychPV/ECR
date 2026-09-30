// tests/Ecr.Worker.Tests/SqlServerCollection.cs

using Ecr.TestKit;
using Xunit;

namespace Ecr.Worker.Tests;

/// <summary>Колекція <c>SqlServer</c>: одна тестова база на збірку, класи — послідовно.</summary>
/// <remarks>
/// ⚠ Послідовно ще й тому, що дочірній воркер (<c>--child</c>) бере БУДЬ-ЯКУ задачу
/// лейну recalc у базі: паралельний клас побачив би свою задачу виконаною чужим процесом.
/// </remarks>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
