using LisoP2P.Core;
using Microsoft.Data.Sqlite;

namespace LisoP2P.Storage;

public sealed class SqliteChatStore : IChatStore
{
    private static readonly string DefaultDbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LisoP2P", "chat.db");

    // Indexed by (user_version - 1); each entry migrates from that version to the next.
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE IF NOT EXISTS peers (
            peer_id     TEXT PRIMARY KEY,
            nickname    TEXT NOT NULL,
            last_seen   INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS messages (
            message_id  TEXT PRIMARY KEY,
            peer_id     TEXT NOT NULL,
            is_outgoing INTEGER NOT NULL,
            text        TEXT NOT NULL,
            sent_at     INTEGER NOT NULL,
            delivered   INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_messages_peer_time ON messages(peer_id, sent_at);
        """,
        """
        ALTER TABLE messages ADD COLUMN room_id TEXT NULL;

        CREATE INDEX IF NOT EXISTS ix_messages_room_time ON messages(room_id, sent_at);
        """,
    ];

    private readonly string _connectionString;

    public SqliteChatStore() : this(DefaultDbPath)
    {
    }

    public SqliteChatStore(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
        Initialize();
    }

    public async Task SaveMessageAsync(StoredMessage message)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO messages (message_id, peer_id, is_outgoing, text, sent_at, delivered, room_id)
            VALUES ($messageId, $peerId, $isOutgoing, $text, $sentAt, $delivered, $roomId);
            """;
        command.Parameters.AddWithValue("$messageId", message.MessageId.ToString());
        command.Parameters.AddWithValue("$peerId", message.PeerId.Value.ToString());
        command.Parameters.AddWithValue("$isOutgoing", message.IsOutgoing ? 1 : 0);
        command.Parameters.AddWithValue("$text", message.Text);
        command.Parameters.AddWithValue("$sentAt", message.SentAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$delivered", message.Delivered ? 1 : 0);
        command.Parameters.AddWithValue(
            "$roomId",
            message.RoomId is { } room ? room.Value.ToString() : DBNull.Value);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task MarkDeliveredAsync(Guid messageId)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET delivered = 1 WHERE message_id = $messageId;";
        command.Parameters.AddWithValue("$messageId", messageId.ToString());
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredMessage>> GetHistoryAsync(PeerId peer, int limit = 100)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, peer_id, is_outgoing, text, sent_at, delivered, room_id
            FROM messages
            WHERE peer_id = $peerId AND room_id IS NULL
            ORDER BY sent_at ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$peerId", peer.Value.ToString());
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadAllAsync(command).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredMessage>> GetRoomHistoryAsync(RoomId room, int limit = 100)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, peer_id, is_outgoing, text, sent_at, delivered, room_id
            FROM messages
            WHERE room_id = $roomId
            ORDER BY sent_at ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$roomId", room.Value.ToString());
        command.Parameters.AddWithValue("$limit", limit);

        return await ReadAllAsync(command).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<StoredMessage>> GetUndeliveredAsync(PeerId peer)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id, peer_id, is_outgoing, text, sent_at, delivered, room_id
            FROM messages
            WHERE peer_id = $peerId AND is_outgoing = 1 AND delivered = 0 AND room_id IS NULL
            ORDER BY sent_at ASC;
            """;
        command.Parameters.AddWithValue("$peerId", peer.Value.ToString());

        return await ReadAllAsync(command).ConfigureAwait(false);
    }

    public async Task UpsertPeerAsync(PeerId id, string nickname)
    {
        await using var connection = await OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO peers (peer_id, nickname, last_seen)
            VALUES ($peerId, $nickname, $lastSeen)
            ON CONFLICT(peer_id) DO UPDATE SET nickname = excluded.nickname, last_seen = excluded.last_seen;
            """;
        command.Parameters.AddWithValue("$peerId", id.Value.ToString());
        command.Parameters.AddWithValue("$nickname", nickname);
        command.Parameters.AddWithValue("$lastSeen", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<StoredMessage>> ReadAllAsync(SqliteCommand command)
    {
        var results = new List<StoredMessage>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            results.Add(new StoredMessage
            {
                MessageId = Guid.Parse(reader.GetString(0)),
                PeerId = new PeerId(Guid.Parse(reader.GetString(1))),
                IsOutgoing = reader.GetInt64(2) != 0,
                Text = reader.GetString(3),
                SentAt = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                Delivered = reader.GetInt64(5) != 0,
                RoomId = reader.IsDBNull(6) ? null : new RoomId(Guid.Parse(reader.GetString(6))),
            });
        }

        return results;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        int currentVersion;
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA user_version;";
            currentVersion = Convert.ToInt32(pragma.ExecuteScalar());
        }

        for (var version = currentVersion; version < Migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = Migrations[version];
                command.ExecuteNonQuery();
            }

            using (var pragma = connection.CreateCommand())
            {
                pragma.Transaction = transaction;
                pragma.CommandText = $"PRAGMA user_version = {version + 1};";
                pragma.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
