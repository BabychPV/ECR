using Ecr.Domain.Entities.Documents;
using Xunit;

namespace Ecr.Domain.Tests;

/// <summary>Чат таблиці: вікно правки (15 хв, межа включно), м'яке видалення, монотонне «прочитано», ревізія зведення.</summary>
public sealed class TableDiscussionMessageTests
{
    private static readonly DateTime T0 = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Вікно_правки_закривається_рівно_через_15_хвилин_межа_включно()
    {
        var message = new TableDiscussionMessage(1, "T1", 202610, 7, "текст", T0);

        Assert.True(message.IsWithinEditWindow(T0));
        Assert.True(message.IsWithinEditWindow(T0 + TimeSpan.FromMinutes(15)));
        Assert.False(message.IsWithinEditWindow(T0 + TimeSpan.FromMinutes(15) + TimeSpan.FromMilliseconds(1)));
        Assert.Equal(T0.AddMinutes(15), message.EditableUntil);
    }

    [Fact]
    public void Правка_ставить_позначку_а_видалення_м_яке_і_зберігає_текст()
    {
        var message = new TableDiscussionMessage(1, "T1", 202610, 7, "текст", T0);

        message.Edit("новий", T0.AddMinutes(1));
        Assert.Equal("новий", message.Body);
        Assert.Equal(T0.AddMinutes(1), message.EditedAt);

        message.SoftDelete(9, T0.AddMinutes(2));
        Assert.True(message.IsDeleted);
        Assert.Equal(9, message.DeletedByUserId);
        Assert.Equal("новий", message.Body);
    }

    [Fact]
    public void Позначка_прочитано_лише_зростає()
    {
        var read = new TableDiscussionRead(1, 2, "T1", 10);

        read.Advance(5);
        Assert.Equal(10, read.LastReadMessageId);
        read.Advance(12);
        Assert.Equal(12, read.LastReadMessageId);
    }

    [Fact]
    public void Ревізія_зведення_росте_на_кожну_зміну_а_лічильник_лише_на_живі()
    {
        var state = new TableDiscussionState(1, "T1");

        state.OnPosted(5, T0);
        state.OnPosted(6, T0.AddMinutes(1));
        Assert.Equal(2, state.MessageCount);
        Assert.Equal(6, state.LastMessageId);
        Assert.Equal(2, state.Revision);

        state.OnEdited();
        Assert.Equal(3, state.Revision);
        Assert.Equal(2, state.MessageCount);

        state.OnDeleted(T0);
        Assert.Equal(4, state.Revision);
        Assert.Equal(1, state.MessageCount);
        Assert.Equal(T0, state.LastMessageAt);
        Assert.Equal(6, state.LastMessageId);
    }
}
