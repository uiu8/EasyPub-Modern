using EasyPub.Desktop;

namespace EasyPub.Desktop.Tests;

public sealed class BoundedHistoryTests
{
    [Fact]
    public void History_discards_the_oldest_item_when_capacity_is_reached()
    {
        var history = new BoundedHistory<int>(3);
        history.Push(1);
        history.Push(2);
        history.Push(3);
        history.Push(4);

        Assert.Equal(3, history.Count);
        Assert.True(history.TryPop(out var latest));
        Assert.Equal(4, latest);
        Assert.True(history.TryPop(out var middle));
        Assert.Equal(3, middle);
        Assert.True(history.TryPop(out var oldest));
        Assert.Equal(2, oldest);
        Assert.False(history.TryPop(out _));
    }
}
