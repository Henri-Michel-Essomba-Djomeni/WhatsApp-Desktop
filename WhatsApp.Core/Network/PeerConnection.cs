using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace WhatsApp.Core.Network;

// Une connexion TCP avec un contact. Chaque paquet = 4 octets de longueur + JSON.
public sealed class PeerConnection : IDisposable
{
    private const int MaxFrame = 64 * 1024 * 1024;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string? Phone { get; set; }
    public event Action<PeerConnection, Packet>? PacketReceived;
    public event Action<PeerConnection>? Closed;

    public string RemoteAddress { get; }

    public PeerConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        var ip = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address;
        RemoteAddress = ip == null ? "" : (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }

    public void StartReading() => _ = Task.Run(ReadLoop);

    private async Task ReadLoop()
    {
        try
        {
            var lenBuf = new byte[4];
            while (true)
            {
                await ReadExact(lenBuf, 4);
                int len = BitConverter.ToInt32(lenBuf);
                if (len <= 0 || len > MaxFrame) break;
                var buf = new byte[len];
                await ReadExact(buf, len);
                var packet = JsonSerializer.Deserialize<Packet>(buf);
                if (packet != null) PacketReceived?.Invoke(this, packet);
            }
        }
        catch { /* connexion coupée */ }
        finally { Closed?.Invoke(this); Dispose(); }
    }

    private async Task ReadExact(byte[] buf, int count)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await _stream.ReadAsync(buf.AsMemory(offset, count - offset));
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    public async Task<bool> SendAsync(Packet p)
    {
        try
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(p);
            await _writeLock.WaitAsync();
            try
            {
                await _stream.WriteAsync(BitConverter.GetBytes(json.Length));
                await _stream.WriteAsync(json);
            }
            finally { _writeLock.Release(); }
            return true;
        }
        catch { return false; }
    }

    public void Dispose() { try { _tcp.Close(); } catch { } }
}