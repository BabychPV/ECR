using Ecr.TestKit;
using Xunit;

namespace Ecr.Infrastructure.Tests;

/// <summary>
/// Визначення колекції <c>SqlServer</c> для інтеграційних тестів.
/// </summary>
/// <remarks>
/// ⚠ У пакеті цього визначення немає (<c>Q-019</c>), хоча десять тестових
/// класів позначені <c>[Collection("SqlServer")]</c> і приймають
/// <see cref="SqlServerFixture"/> у конструкторі. Без визначення в **цій самій**
/// збірці xUnit не може підставити фікстуру, а аналізатор дає <c>xUnit1041</c>.
/// Один контейнер SQL Server на всю збірку — інакше кожен клас піднімав би
/// власний, і прогін інтеграційних тестів став би непідйомним за часом.
/// </remarks>
[CollectionDefinition("SqlServer")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
}
