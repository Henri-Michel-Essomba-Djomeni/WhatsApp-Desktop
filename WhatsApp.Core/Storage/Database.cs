using Microsoft.Data.Sqlite;
using WhatsApp.Core.Models;

namespace WhatsApp.Core.Storage;

// Une base par numéro : deux instances sur le même PC ne se mélangent pas.
public sealed class Database : IDisposable
{
    private readonly SqliteConnection _c;
    public string Folder { get; }
    public string MediaFolder { get; }

    public Database(string myPhone)
    {
        Folder = Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData), "WhatsAppClone", myPhone);
        MediaFolder = Path.Combine(Folder, "media");
        Directory.CreateDirectory(MediaFolder);

        _c = new SqliteConnection($"Data Source={Path.Combine(Folder, "chat.db")}");
        _c.Open();
        Exec(@"
            CREATE TABLE IF NOT EXISTS contacts(
                phone TEXT PRIMARY KEY, name TEXT, about TEXT, avatar_path TEXT);
            CREATE TABLE IF NOT EXISTS messages(
                id TEXT PRIMARY KEY, chat_phone TEXT, sender_phone TEXT, type INTEGER,
                text TEXT, media_path TEXT, sent_at INTEGER, status INTEGER,
                reply_to TEXT, deleted INTEGER DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_chat ON messages(chat_phone, sent_at);
            CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);");
    }

    private void Exec(string sql, params (string, object?)[] p) { using var c = Cmd(sql, p); c.ExecuteNonQuery(); }

    private SqliteCommand Cmd(string sql, params (string Name, object? Value)[] p)
    {
        var cmd = _c.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in p) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    // ---- Contacts ----
    public void SaveContact(Contact c) => Exec(@"
        INSERT INTO contacts(phone,name,about,avatar_path) VALUES($p,$n,$a,$v)
        ON CONFLICT(phone) DO UPDATE SET name=$n, about=$a, avatar_path=$v",
        ("$p", c.Phone), ("$n", c.Name), ("$a", c.About), ("$v", c.AvatarPath));

    public Contact? GetContact(string phone)
    {
        using var c = Cmd("SELECT phone,name,about,avatar_path FROM contacts WHERE phone=$p", ("$p", phone));
        using var r = c.ExecuteReader();
        return r.Read() ? ReadContact(r, 0) : null;
    }

    private static Contact ReadContact(SqliteDataReader r, int o) => new()
    {
        Phone = r.GetString(o), Name = r.IsDBNull(o + 1) ? "" : r.GetString(o + 1),
        About = r.IsDBNull(o + 2) ? "" : r.GetString(o + 2),
        AvatarPath = r.IsDBNull(o + 3) ? null : r.GetString(o + 3)
    };

    // ---- Messages ----
    public void AddMessage(ChatMessage m) => Exec(@"
        INSERT OR IGNORE INTO messages(id,chat_phone,sender_phone,type,text,media_path,sent_at,status,reply_to,deleted)
        VALUES($id,$cp,$sp,$t,$tx,$mp,$at,$st,$rt,0)",
        ("$id", m.Id), ("$cp", m.ChatPhone), ("$sp", m.SenderPhone), ("$t", (int)m.Type),
        ("$tx", m.Text), ("$mp", m.MediaPath), ("$at", new DateTimeOffset(m.SentAt).ToUnixTimeMilliseconds()),
        ("$st", (int)m.Status), ("$rt", m.ReplyToId));

    public List<ChatMessage> GetMessages(string chatPhone)
    {
        var list = new List<ChatMessage>();
        using var c = Cmd(@"SELECT id,chat_phone,sender_phone,type,text,media_path,sent_at,status,reply_to,deleted
                            FROM messages WHERE chat_phone=$p ORDER BY sent_at", ("$p", chatPhone));
        using var r = c.ExecuteReader();
        while (r.Read()) list.Add(ReadMessage(r));
        return list;
    }

    private static ChatMessage ReadMessage(SqliteDataReader r) => new()
    {
        Id = r.GetString(0), ChatPhone = r.GetString(1), SenderPhone = r.GetString(2),
        Type = (MessageType)r.GetInt32(3), Text = r.IsDBNull(4) ? "" : r.GetString(4),
        MediaPath = r.IsDBNull(5) ? null : r.GetString(5),
        SentAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(6)).LocalDateTime,
        Status = (MessageStatus)r.GetInt32(7), ReplyToId = r.IsDBNull(8) ? null : r.GetString(8),
        IsDeleted = r.GetInt32(9) == 1
    };

    public void SetStatus(string id, MessageStatus s) =>
        Exec("UPDATE messages SET status=$s WHERE id=$id AND status<$s", ("$s", (int)s), ("$id", id));

    // Marque comme lus tous les messages reçus d'un contact
    public void MarkChatRead(string chatPhone) => Exec(
        "UPDATE messages SET status=3 WHERE chat_phone=$p AND sender_phone=$p AND status<3", ("$p", chatPhone));

    public void DeleteMessage(string id) =>
        Exec("UPDATE messages SET deleted=1, text='', media_path=NULL WHERE id=$id", ("$id", id));

    // ---- Liste des discussions (dernier message + non lus) ----
    public List<ChatSummary> GetChats()
    {
        var list = new List<ChatSummary>();
        using var c = Cmd(@"
            SELECT c.phone,c.name,c.about,c.avatar_path,
                   m.id,m.chat_phone,m.sender_phone,m.type,m.text,m.media_path,m.sent_at,m.status,m.reply_to,m.deleted,
                   (SELECT COUNT(*) FROM messages u WHERE u.chat_phone=c.phone
                       AND u.sender_phone=c.phone AND u.status<3 AND u.deleted=0)
            FROM contacts c
            LEFT JOIN messages m ON m.id=(SELECT id FROM messages WHERE chat_phone=c.phone
                                          ORDER BY sent_at DESC LIMIT 1)
            ORDER BY COALESCE(m.sent_at,0) DESC");
        using var r = c.ExecuteReader();
        while (r.Read())
        {
            var contact = ReadContact(r, 0);
            ChatMessage? last = null;
            if (!r.IsDBNull(4))
            {
                last = new ChatMessage
                {
                    Id = r.GetString(4), ChatPhone = r.GetString(5), SenderPhone = r.GetString(6),
                    Type = (MessageType)r.GetInt32(7), Text = r.IsDBNull(8) ? "" : r.GetString(8),
                    MediaPath = r.IsDBNull(9) ? null : r.GetString(9),
                    SentAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(10)).LocalDateTime,
                    Status = (MessageStatus)r.GetInt32(11),
                    ReplyToId = r.IsDBNull(12) ? null : r.GetString(12), IsDeleted = r.GetInt32(13) == 1
                };
            }
            list.Add(new ChatSummary(contact, last, r.GetInt32(14)));
        }
        return list;
    }

    // ---- Paramètres (thème, mon profil, etc.) ----
    public string? GetSetting(string key)
    {
        using var c = Cmd("SELECT value FROM settings WHERE key=$k", ("$k", key));
        return c.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value) => Exec(
        "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v",
        ("$k", key), ("$v", value));

    public void Dispose() => _c.Dispose();
}