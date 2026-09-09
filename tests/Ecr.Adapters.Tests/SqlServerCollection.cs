using Ecr.TestKit;
using Xunit;

namespace Ecr.Adapters.Tests;

/// <summary>
/// Визначення колекції <c>SqlServer</c> для інтеграційних тестів цього пакета
/// (Q-168) — той самий прийом, що й в інших тестових проєктах: без визначення
/// в цій самій збірці xUnit не може підставити <see cref="SqlServerFixture"/>.
/// </summary>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
