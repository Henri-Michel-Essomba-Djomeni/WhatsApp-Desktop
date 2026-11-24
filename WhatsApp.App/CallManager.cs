using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using WhatsApp.Core.Network;

namespace WhatsApp.App;

public enum CallState { Idle, Calling, Ringing, Active }

// Gère un appel vocal : signalisation via P2PNode (TCP), audio en UDP direct.
// Changed est déclenché sur un thread de fond : l'interface doit passer par le Dispatcher.
public sealed class CallManager : IDisposable
{
    private readonly P2PNode _node;
    private readonly object _gate = new();
    private UdpClient? _udp;
    private IPEndPoint? _remote;
    private WaveIn? _mic;
    private WaveOut? _speaker;
    private BufferedWaveProvider? _buffer;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _timeout;
    private int _remotePort;
    private int _localPort;

    public CallState State { get; private set; } = CallState.Idle;
    public string? Peer { get; private set; }
    public string CallId { get; private set; } = "";
    public bool IsOutgoing { get; private set; }
    public bool Muted { get; private set; }
    public string EndReason { get; private set; } = "";
    public DateTime StartedAt { get; private set; }

    public event Action? Changed;

    public CallManager(P2PNode node)
    {
        _node = node;
        _node.CallSignal += OnSignal;
    }

    // ---------- Actions de l'utilisateur ----------
    public string? StartCall(string phone)
    {
        lock (_gate)
        {
            if (State != CallState.Idle) return "Un appel est déjà en cours.";
            if (!_node.IsOnline(phone)) return "Ce contact est hors ligne.";
            CallId = Guid.NewGuid().ToString();
            Peer = phone; IsOutgoing = true; Muted = false; EndReason = "";
            OpenUdp();
            State = CallState.Calling;
            _node.SendCallSignal(phone, new Packet { Type = PacketType.CallInvite, Id = CallId, Port = _localPort });
            StartTimeout("Sans réponse");
        }
        Changed?.Invoke();
        return null;
    }

    public void Accept()
    {
        lock (_gate)
        {
            if (State != CallState.Ringing) return;
            OpenUdp();
            _node.SendCallSignal(Peer!, new Packet { Type = PacketType.CallAccept, Id = CallId, Port = _localPort });
        }
        BeginAudio();
    }

    public void Decline()
    {
        string? peer; string id;
        lock (_gate)
        {
            if (State != CallState.Ringing) return;
            peer = Peer; id = CallId;
        }
        _node.SendCallSignal(peer!, new Packet { Type = PacketType.CallReject, Id = id });
        Finish("Appel refusé");
    }

    public void Hangup()
    {
        string? peer; string id; CallState s;
        lock (_gate) { s = State; peer = Peer; id = CallId; }
        if (s is not (CallState.Calling or CallState.Active)) return;
        _node.SendCallSignal(peer!, new Packet { Type = PacketType.CallEnd, Id = id });
        Finish("Appel terminé");
    }

    public void ToggleMute() => Muted = !Muted;

    public void PeerLost(string phone)
    {
        if (Peer == phone && State != CallState.Idle) Finish("Connexion perdue");
    }

    // ---------- Signalisation reçue ----------
    private void OnSignal(string phone, Packet p)
    {
        switch (p.Type)
        {
            case PacketType.CallInvite:
                bool busy;
                lock (_gate)
                {
                    busy = State != CallState.Idle;
                    if (!busy)
                    {
                        CallId = p.Id; Peer = phone; IsOutgoing = false; Muted = false; EndReason = "";
                        _remotePort = p.Port;
                        State = CallState.Ringing;
                        StartTimeout("Appel manqué");
                    }
                }
                if (busy) _node.SendCallSignal(phone, new Packet { Type = PacketType.CallReject, Id = p.Id });
                else Changed?.Invoke();
                break;

            case PacketType.CallAccept:
                lock (_gate)
                {
                    if (State != CallState.Calling || p.Id != CallId) return;
                    _remotePort = p.Port;
                }
                BeginAudio();
                break;

            case PacketType.CallReject:
                if (p.Id == CallId) Finish("Appel refusé");
                break;

            case PacketType.CallEnd:
                if (p.Id == CallId) Finish("Appel terminé");
                break;
        }
    }

    // ---------- Audio ----------
    private void OpenUdp()
    {
        _udp?.Dispose();
        _udp = new UdpClient(0);   // port libre choisi par Windows
        _localPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
    }

        private void BeginAudio()
    {
        try
        {
            lock (_gate)
            {
                var ip = _node.GetPeerAddress(Peer!) ?? throw new InvalidOperationException("Contact injoignable");
                _remote = new IPEndPoint(IPAddress.Parse(ip), _remotePort);
                var udp = _udp!;
                var remote = _remote;
                var fmt = new WaveFormat(16000, 16, 1);

                _buffer = new BufferedWaveProvider(fmt) { DiscardOnBufferOverflow = true };
                _speaker = new WaveOut();
                _speaker.Init(_buffer);
                _speaker.Play();

                _mic = new WaveIn { WaveFormat = fmt, BufferMilliseconds = 40 };
                _mic.DataAvailable += (_, a) =>
                {
                    if (Muted) return;
                    try { udp.Send(a.Buffer, a.BytesRecorded, remote); } catch { }
                };
                _mic.StartRecording();

                _cts = new CancellationTokenSource();
                var buf = _buffer; var ct = _cts.Token;
                _ = Task.Run(() => ReceiveLoop(udp, buf, ct));

                _timeout?.Dispose(); _timeout = null;
                State = CallState.Active;
                StartedAt = DateTime.Now;
            }
            Changed?.Invoke();
        }
        catch
        {
            _node.SendCallSignal(Peer!, new Packet { Type = PacketType.CallEnd, Id = CallId });
            Finish("Micro ou audio indisponible");
        }
    }
    private static async Task ReceiveLoop(UdpClient udp, BufferedWaveProvider buf, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                // Plus de 400 ms de retard accumulé : on repart à zéro plutôt que de parler en différé
                if (buf.BufferedDuration > TimeSpan.FromMilliseconds(400)) buf.ClearBuffer();
                buf.AddSamples(r.Buffer, 0, r.Buffer.Length);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(30); }   // erreur ICMP passagère : on continue
        }
    }

    private void StopAudio()
    {
        try { _cts?.Cancel(); } catch { }
        try { _mic?.StopRecording(); } catch { }
        _mic?.Dispose(); _mic = null;
        try { _speaker?.Stop(); } catch { }
        _speaker?.Dispose(); _speaker = null;
        _buffer = null; _cts = null; _remote = null;
    }

    // ---------- Fin d'appel ----------
    private void StartTimeout(string reason)
    {
        _timeout?.Dispose();
        var id = CallId;
        _timeout = new System.Threading.Timer(_ =>
        {
            CallState s; string? peer;
            lock (_gate) { s = State; peer = Peer; }
            if (CallId != id || s is not (CallState.Calling or CallState.Ringing)) return;
            if (s == CallState.Calling)
                _node.SendCallSignal(peer!, new Packet { Type = PacketType.CallEnd, Id = id });
            Finish(reason);
        }, null, 30000, System.Threading.Timeout.Infinite);
    }

    private void Finish(string reason)
    {
        lock (_gate)
        {
            if (State == CallState.Idle) return;
            _timeout?.Dispose(); _timeout = null;
            StopAudio();
            _udp?.Dispose(); _udp = null;
            State = CallState.Idle;
            EndReason = reason;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _node.CallSignal -= OnSignal;
        lock (_gate)
        {
            _timeout?.Dispose();
            StopAudio();
            _udp?.Dispose();
        }
    }
}