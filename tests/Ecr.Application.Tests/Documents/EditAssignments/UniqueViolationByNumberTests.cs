// tests/Ecr.Application.Tests/Documents/EditAssignments/UniqueViolationByNumberTests.cs
using System.Reflection;
using Ecr.Application.Documents.EditAssignments;
using Ecr.TestKit;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Ecr.Application.Tests.Documents.EditAssignments;

/// <summary>
/// <see cref="EditAssignmentAccess.IsUniqueViolation"/> розпізнає дубль за НОМЕРОМ помилки SQL Server (2601/2627), а не
/// за англійським текстом «duplicate key»: на інстансі з російською мовою повідомлень дубль ставав би 500.
/// </summary>
/// <remarks>Мутація: повернути перевірку <c>Message.Contains("duplicate key")</c> — червоніють випадки з російським текстом.</remarks>
public sealed class UniqueViolationByNumberTests
{
    private const string RussianDuplicate =
        "Не удается вставить повторяющуюся строку ключа в объект \"doc.EditAssignment\" с уникальным индексом \"UX_EditAssignment\".";

    [Theory]
    [InlineData(2627, true)]
    [InlineData(2601, true)]
    [InlineData(547, false)] // FK
    [InlineData(1205, false)] // deadlock
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Номер_2601_2627_дубль_незалежно_від_мови_тексту(int number, bool expected)
    {
        var sql = Sql(number, RussianDuplicate);

        Assert.Equal(expected, EditAssignmentAccess.IsUniqueViolation(sql));
        Assert.Equal(expected, EditAssignmentAccess.IsUniqueViolation(new DbUpdateException("save failed", sql)));
        Assert.Equal(expected, EditAssignmentAccess.IsUniqueViolation(
            new InvalidOperationException("outer", new DbUpdateException("save failed", sql))));
    }

    [Fact]
    [Trait(TestCategories.Stage, TestCategories.Stage8)]
    public void Англійський_текст_без_номера_дубля_не_дубль()
    {
        Assert.False(EditAssignmentAccess.IsUniqueViolation(Sql(50000, "Cannot insert duplicate key row (raised by a trigger).")));
        Assert.False(EditAssignmentAccess.IsUniqueViolation(new InvalidOperationException("duplicate key")));
    }

    /// <summary><see cref="SqlException"/> із номером і текстом: публічного конструктора немає — рефлексією (як в AuditReaderArchiveTests).</summary>
    private static SqlException Sql(int number, string message)
    {
        var errorCtor = typeof(SqlError)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        var args = errorCtor.GetParameters()
            .Select(p => p.Name switch
            {
                "infoNumber" => number,
                "errorMessage" => message,
                _ => p.ParameterType == typeof(string) ? "test"
                    : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
            })
            .ToArray();
        var error = (SqlError)errorCtor.Invoke(args);

        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(errors, [error]);

        var create = typeof(SqlException)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .First(m => m.Name == "CreateException"
                        && m.GetParameters().Select(p => p.ParameterType)
                            .SequenceEqual([typeof(SqlErrorCollection), typeof(string)]));

        var exception = (SqlException)create.Invoke(null, [errors, "16.0"])!;
        Assert.Equal(number, exception.Number);
        Assert.Contains(message, exception.Message, StringComparison.Ordinal);
        return exception;
    }
}
