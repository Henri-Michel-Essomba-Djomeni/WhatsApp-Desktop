using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WhatsApp.Core.Models;
using WhatsApp.Core.Storage;

namespace WhatsApp.Core.Network;

// Un noeud P2P : écoute en TCP, se fait découvrir par broadcast UDP, échange les messages.
// ATTENTION : les événements arrivent sur des threads de fond (l'interface devra utiliser le Dispatcher).
public sealed class P2PNode : IDisposable
{
    public const int DiscoveryPort = 45454;
    private readonly Database _db;
    private readonly ConcurrentDictionary<string, PeerConnection> _peers = new();
    private readonly ConcurrentDictionary<string, byte> _connecting = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private UdpClient? _udp;

    public Contact Me { get; }
    public int Port { get; private set; }

    public event Action<ChatMessage>? MessageReceived;
    public event Action<string, MessageStatus>? StatusChanged;   // id du message, nouveau statut
    public event Action<string, bool>? PeerPresence;              // numéro, en ligne ou non
    public event Action<string, bool>? Typing;                    // numéro, est en train d'écrire
    public event Action<string>? MessageDeleted;                  // id du message
    public event Action<Contact>? ContactUpdated;

    public P2PNode(Database db, Contact me) { _db = db; Me = me; }

    public bool IsOnline(string phone) => _peers.ContainsKey(phone);

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, 0);   // port libre choisi par Windows
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
        try { StartDiscovery(); } catch { /* sans découverte : connexion manuelle par IP */ }
    }

    // ---------- Découverte (UDP broadcast) ----------
    private void StartDiscovery()
    {
        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _ = Task.Run(ListenLoop);
        _ = Task.Run(AnnounceLoop);
    }

    private async Task AnnounceLoop()
    {
        var data = Encoding.UTF8.GetBytes($"WAC1|{Me.Phone}|{Port}");
        var target = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
        while (!_cts.IsCancellationRequested)
        {
            try { await _udp!.SendAsync(data, target, _cts.Token); } catch { }
            try { await Task.Delay(3000, _cts.Token); } catch { break; }
        }
    }

    private async Task ListenLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp!.ReceiveAsync(_cts.Token);
                var parts = Encoding.UTF8.GetString(r.Buffer).Split('|');
                if (parts.Length != 3 || parts[0] != "WAC1") continue;
                var phone = parts[1];
                if (phone == Me.Phone || !int.TryParse(parts[2], out var port)) continue;
                if (_peers.ContainsKey(phone)) continue;
                // Règle anti-doublon : seul le plus petit numéro initie la connexion
                if (string.CompareOrdinal(Me.Phone, phone) >= 0) continue;
                if (!_connecting.TryAdd(phone, 0)) continue;
                var ip = r.RemoteEndPoint.Address.ToString();
                _ = Task.Run(async () =>
                {
                    try { await ConnectManualAsync(ip, port); }
                    finally { await Task.Delay(5000); _connecting.TryRemove(phone, out _); }
                });
            }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(200); }
        }
    }

    // ---------- Connexions ----------
    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { Attach(new PeerConnection(await _listener!.AcceptTcpClientAsync(_cts.Token))); }
            catch (OperationCanceledException) { break; }
            catch { await Task.Delay(200); }
        }
    }

    // Aussi utilisable à la main (ex. IP Tailscale d'un ami)
    public async Task<bool> ConnectManualAsync(string host, int port)
    {
        try
        {
            var c = new TcpClient();
            using var timeout = new CancellationTokenSource(3000);
            await c.ConnectAsync(host, port, timeout.Token);
            Attach(new PeerConnection(c));
            return true;
        }
        catch { return false; }
    }

    private void Attach(PeerConnection conn)
    {
        conn.PacketReceived += OnPacket;
        conn.Closed += OnClosed;
        conn.StartReading();
        _ = conn.SendAsync(BuildHello());
    }

    private Packet BuildHello()
    {
        var hello = new Packet { Type = PacketType.Hello, From = Me.Phone, Name = Me.Name, About = Me.About };
        if (Me.AvatarPath != null && File.Exists(Me.AvatarPath)) hello.Data = File.ReadAllBytes(Me.AvatarPath);
        return hello;
    }

    private void OnClosed(PeerConnection conn)
    {
        var phone = conn.Phone;
        if (phone == null) return;
        if (_peers.TryRemove(new KeyValuePair<string, PeerConnection>(phone, conn)))
            PeerPresence?.Invoke(phone, false);
    }

    // ---------- Réception ----------
    private void OnPacket(PeerConnection conn, Packet p)
    {
        if (p.Type == PacketType.Hello) { OnHello(conn, p); return; }
        var phone = conn.Phone;
        if (phone == null) return;   // pas encore présenté

        switch (p.Type)
        {
            case PacketType.Message: OnMessage(conn, phone, p); break;
            case PacketType.Delivered:
                _db.SetStatus(p.Id, MessageStatus.Delivered);
                StatusChanged?.Invoke(p.Id, MessageStatus.Delivered); break;
            case PacketType.Read:
                foreach (var id in p.Ids)
                {
                    _db.SetStatus(id, MessageStatus.Read);
                    StatusChanged?.Invoke(id, MessageStatus.Read);
                }
                break;
            case PacketType.Typing: Typing?.Invoke(phone, p.IsTyping); break;
            case PacketType.Delete: _db.DeleteMessage(p.Id); MessageDeleted?.Invoke(p.Id); break;
        }
    }

    private void OnHello(PeerConnection conn, Packet p)
    {
        conn.Phone = p.From;
        var contact = _db.GetContact(p.From) ?? new Contact { Phone = p.From };
        contact.Name = p.Name; contact.About = p.About;
        if (p.Data is { Length: > 0 })
        {
            var path = Path.Combine(_db.MediaFolder, $"avatar_{p.From}.jpg");
            File.WriteAllBytes(path, p.Data);
            contact.AvatarPath = path;
        }
        _db.SaveContact(contact);

        if (_peers.TryGetValue(p.From, out var old) && old != conn) old.Dispose();
        _peers[p.From] = conn;

        ContactUpdated?.Invoke(contact);
        PeerPresence?.Invoke(p.From, true);
        FlushPending(p.From);   // renvoie les messages restés en attente
    }

    private void OnMessage(PeerConnection conn, string phone, Packet p)
    {
        var ack = new Packet { Type = PacketType.Delivered, From = Me.Phone, Id = p.Id };
        if (_db.MessageExists(p.Id)) { _ = conn.SendAsync(ack); return; }   // doublon

        string? media = null;
        if (p.Data is { Length: > 0 })
        {
            media = Path.Combine(_db.MediaFolder, $"{p.Id}_{Path.GetFileName(p.FileName)}");
            File.WriteAllBytes(media, p.Data);
        }
        var m = new ChatMessage
        {
            Id = p.Id, ChatPhone = phone, SenderPhone = phone, Type = p.MsgType, Text = p.Text,
            MediaPath = media, ReplyToId = p.ReplyToId, Status = MessageStatus.Delivered,
            SentAt = DateTimeOffset.FromUnixTimeMilliseconds(p.SentAtMs).LocalDateTime
        };
        _db.AddMessage(m);
        _ = conn.SendAsync(ack);
        MessageReceived?.Invoke(m);
    }

    // ---------- Envoi ----------
    public Task<ChatMessage> SendTextAsync(string toPhone, string text, string? replyToId = null) =>
        SendAsync(new ChatMessage
        {
            ChatPhone = toPhone, SenderPhone = Me.Phone, Type = MessageType.Text,
            Text = text, ReplyToId = replyToId
        });

    // Image, fichier, vocal ou sticker : le fichier est copié dans le dossier media
    public Task<ChatMessage> SendMediaAsync(string toPhone, string filePath, MessageType type, string caption = "")
    {
        var m = new ChatMessage
        {
            ChatPhone = toPhone, SenderPhone = Me.Phone, Type = type, Text = caption
        };
        m.MediaPath = Path.Combine(_db.MediaFolder, $"{m.Id}_{Path.GetFileName(filePath)}");
        File.Copy(filePath, m.MediaPath, true);
        return SendAsync(m);
    }

    private async Task<ChatMessage> SendAsync(ChatMessage m)
    {
        if (_db.GetContact(m.ChatPhone) == null)
            _db.SaveContact(new Contact { Phone = m.ChatPhone, Name = m.ChatPhone });
        _db.AddMessage(m);
        await TrySend(m);   // si le contact est hors ligne, le message reste "Pending"
        return m;
    }

    private async Task<bool> TrySend(ChatMessage m)
    {
        if (!_peers.TryGetValue(m.ChatPhone, out var peer)) return false;
        var pk = new Packet
        {
            Type = PacketType.Message, From = Me.Phone, Id = m.Id, MsgType = m.Type, Text = m.Text,
            ReplyToId = m.ReplyToId, SentAtMs = new DateTimeOffset(m.SentAt).ToUnixTimeMilliseconds()
        };
        if (m.MediaPath != null && File.Exists(m.MediaPath))
        {
            pk.Data = await File.ReadAllBytesAsync(m.MediaPath);
            pk.FileName = Path.GetFileName(m.MediaPath);
        }
        if (!await peer.SendAsync(pk)) return false;
        if (m.Status < MessageStatus.Sent)
        {
            m.Status = MessageStatus.Sent;
            _db.SetStatus(m.Id, MessageStatus.Sent);
            StatusChanged?.Invoke(m.Id, MessageStatus.Sent);
        }
        return true;
    }

    private void FlushPending(string phone) => _ = Task.Run(async () =>
    {
        var pending = _db.GetMessages(phone)
            .Where(x => x.SenderPhone == Me.Phone && x.Status == MessageStatus.Pending && !x.IsDeleted).ToList();
        foreach (var m in pending) await TrySend(m);
    });

    public void SendTyping(string phone, bool typing)
    {
        if (_peers.TryGetValue(phone, out var peer))
            _ = peer.SendAsync(new Packet { Type = PacketType.Typing, From = Me.Phone, IsTyping = typing });
    }

    // À appeler quand l'utilisateur ouvre une discussion : envoie les coches bleues
    public void MarkChatRead(string phone)
    {
        var ids = _db.GetMessages(phone)
            .Where(m => m.SenderPhone == phone && m.Status < MessageStatus.Read && !m.IsDeleted)
            .Select(m => m.Id).ToList();
        if (ids.Count == 0) return;
        _db.MarkChatRead(phone);
        if (_peers.TryGetValue(phone, out var peer))
            _ = peer.SendAsync(new Packet { Type = PacketType.Read, From = Me.Phone, Ids = ids });
    }

    public void DeleteForEveryone(string phone, string messageId)
    {
        _db.DeleteMessage(messageId);
        if (_peers.TryGetValue(phone, out var peer))
            _ = peer.SendAsync(new Packet { Type = PacketType.Delete, From = Me.Phone, Id = messageId });
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _udp?.Close(); } catch { }
        foreach (var p in _peers.Values) p.Dispose();
    }
}