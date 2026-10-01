// src/Ecr.Infrastructure/Persistence/EcrMigrationsSqlGenerator.cs
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Update;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Генератор SQL міграцій: штатний SQL Server плюс передперевірки, які не
/// можна дописати в уже застосовані міграції.
/// </summary>
/// <remarks>
/// ⚠ Єдина точка, через яку проходить КОЖЕН шлях застосування міграцій:
/// <c>Database.MigrateAsync</c> і <c>dotnet ef migrations script</c>
/// (обидва кличуть <c>IMigrationsSqlGenerator.Generate</c> окремо для кожної
/// міграції). Перевірка в одному скрипті розгортання лишила б без захисту
/// решту: пакет інсталятора, <c>setup-dev-db.ps1</c> і режим <c>Migrate</c>.
/// Підключається в <see cref="EcrDbContext"/> (<c>OnConfiguring</c>), тож
/// діє для кожного контексту, а не лише для зареєстрованого в DI.
/// </remarks>
public sealed class EcrMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    ICommandBatchPreparer commandBatchPreparer)
    : SqlServerMigrationsSqlGenerator(dependencies, commandBatchPreparer)
{
    /// <inheritdoc />
    public override IReadOnlyList<MigrationCommand> Generate(
        IReadOnlyList<MigrationOperation> operations,
        IModel? model = null,
        MigrationsSqlGenerationOptions options = MigrationsSqlGenerationOptions.Default)
    {
        ArgumentNullException.ThrowIfNull(operations);

        if (D148ScalePrecheck.Applies(operations))
        {
            operations = [new SqlOperation { Sql = D148ScalePrecheck.Sql }, .. operations];
        }

        if (Q222UniquePrecheck.Applies(operations))
        {
            operations = [new SqlOperation { Sql = Q222UniquePrecheck.Sql }, .. operations];
        }

        return base.Generate(operations, model, options);
    }
}
