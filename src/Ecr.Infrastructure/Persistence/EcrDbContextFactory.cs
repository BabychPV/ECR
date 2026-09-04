using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecr.Infrastructure.Persistence;

/// <summary>
/// Фабрика для <c>dotnet ef migrations</c>. Потрібна, бо інструмент не може
/// підняти повний host застосунку.
/// </summary>
public sealed class EcrDbContextFactory : IDesignTimeDbContextFactory<EcrDbContext>
{
    /// <inheritdoc />
    public EcrDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("ECR_ConnectionStrings__Ecr")
                         ?? "Server=localhost;Database=Ecr;Trusted_Connection=True;TrustServerCertificate=True";

        var options = new DbContextOptionsBuilder<EcrDbContext>()
            .UseSqlServer(connection, o => o.MigrationsHistoryTable("__EFMigrationsHistory", "dbo"))
            .Options;

        return new EcrDbContext(options);
    }
}
