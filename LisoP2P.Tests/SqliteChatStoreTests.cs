using LisoP2P.Core;
using LisoP2P.Storage;
using Microsoft.Data.Sqlite;

namespace LisoP2P.Tests;

public class SqliteChatStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteChatStore _store;

    public SqliteChatStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"lisop2p-tests-{Guid.NewGuid():N}.db");
        _store = new SqliteChatStore(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task SaveThenMarkDelivered_UpdatesDeliveredFlag()
    {
        var peer = new PeerId(Guid.NewGuid());
        var message = NewMessage(peer, "oi", DateTimeOffset.UtcNow, delivered: false);

        await _store.SaveMessageAsync(message);
        await _store.MarkDeliveredAsync(message.MessageId);

        var history = await _store.GetHistoryAsync(peer);

        Assert.Single(history);
        Assert.True(history[0].Delivered);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsMessagesOrderedBySentAt()
    {
        var peer = new PeerId(Guid.NewGuid());
        var older = NewMessage(peer, "first", DateTimeOffset.UtcNow.AddMinutes(-5), delivered: true);
        var newer = NewMessage(peer, "second", DateTimeOffset.UtcNow, delivered: true);

        await _store.SaveMessageAsync(newer);
        await _store.SaveMessageAsync(older);

        var history = await _store.GetHistoryAsync(peer);

        Assert.Equal(2, history.Count);
        Assert.Equal("first", history[0].Text);
        Assert.Equal("second", history[1].Text);
    }

    [Fact]
    public async Task SaveMessageAsync_DuplicateMessageId_DoesNotDuplicateRow()
    {
        var peer = new PeerId(Guid.NewGuid());
        var message = NewMessage(peer, "oi", DateTimeOffset.UtcNow, delivered: false);

        await _store.SaveMessageAsync(message);
        await _store.SaveMessageAsync(message);

        var history = await _store.GetHistoryAsync(peer);

        Assert.Single(history);
    }

    [Fact]
    public async Task GetUndeliveredAsync_ReturnsOnlyUndeliveredOutgoingMessages()
    {
        var peer = new PeerId(Guid.NewGuid());
        var pending = NewMessage(peer, "pendente", DateTimeOffset.UtcNow.AddMinutes(-1), delivered: false);
        var delivered = NewMessage(peer, "entregue", DateTimeOffset.UtcNow, delivered: true);
        var incoming = new StoredMessage
        {
            MessageId = Guid.NewGuid(),
            PeerId = peer,
            IsOutgoing = false,
            Text = "dele",
            SentAt = DateTimeOffset.UtcNow,
            Delivered = false,
        };

        await _store.SaveMessageAsync(pending);
        await _store.SaveMessageAsync(delivered);
        await _store.SaveMessageAsync(incoming);

        var undelivered = await _store.GetUndeliveredAsync(peer);

        Assert.Single(undelivered);
        Assert.Equal("pendente", undelivered[0].Text);
    }

    private static StoredMessage NewMessage(PeerId peer, string text, DateTimeOffset sentAt, bool delivered) => new()
    {
        MessageId = Guid.NewGuid(),
        PeerId = peer,
        IsOutgoing = true,
        Text = text,
        SentAt = sentAt,
        Delivered = delivered,
    };
}
