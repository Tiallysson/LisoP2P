using LisoP2P.Media;

namespace LisoP2P.Tests;

public class LatestFrameSlotTests
{
    [Fact]
    public void TryTake_ReturnsMostRecentItem()
    {
        var slot = new LatestFrameSlot<int>();

        slot.Publish(1);
        slot.Publish(2);
        slot.Publish(3);

        Assert.True(slot.TryTake(0, out var item));
        Assert.Equal(3, item);
    }

    [Fact]
    public void Publish_NeverQueues()
    {
        var slot = new LatestFrameSlot<int>();

        slot.Publish(1);
        slot.Publish(2);

        Assert.True(slot.TryTake(0, out _));
        Assert.False(slot.TryTake(0, out _));
    }

    [Fact]
    public void Publish_CountsAndReportsDroppedItems()
    {
        var dropped = new List<int>();
        var slot = new LatestFrameSlot<int>(dropped.Add);

        slot.Publish(1);
        slot.Publish(2);
        slot.Publish(3);

        Assert.Equal(2, slot.DroppedCount);
        Assert.Equal([1, 2], dropped);
    }

    [Fact]
    public void FastProducerSlowConsumer_KeepsOnlyLatest()
    {
        var dropped = new List<int>();
        var slot = new LatestFrameSlot<int>(dropped.Add);
        var consumed = new List<int>();

        for (var i = 1; i <= 100; i++)
        {
            slot.Publish(i);

            if (i % 10 == 0 && slot.TryTake(0, out var item))
            {
                consumed.Add(item);
            }
        }

        Assert.Equal(10, consumed.Count);
        Assert.Equal([10, 20, 30, 40, 50, 60, 70, 80, 90, 100], consumed);
        Assert.Equal(90, slot.DroppedCount);
        Assert.Equal(90, dropped.Count);
    }

    [Fact]
    public void TryTake_ReturnsFalseWhenEmpty()
    {
        var slot = new LatestFrameSlot<int>();

        Assert.False(slot.TryTake(10, out _));
    }

    [Fact]
    public void Close_ReleasesPendingItem()
    {
        var dropped = new List<int>();
        var slot = new LatestFrameSlot<int>(dropped.Add);

        slot.Publish(7);
        slot.Close();

        Assert.Equal([7], dropped);
        Assert.False(slot.TryTake(0, out _));
    }
}
