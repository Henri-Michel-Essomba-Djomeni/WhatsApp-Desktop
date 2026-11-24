using System.Net;
using System.Net.Sockets;
using NAudio.Wave;
using OpenCvSharp;
using WhatsApp.Core.Network;

namespace WhatsApp.App;

public enum CallState { Idle, Calling, Ringing, Active }

// Gère un appel vocal ou vidéo : signalisation via P2PNode (TCP), audio et vidéo en UDP direct.
// Les événements sont déclenchés sur des threads de fond : l'interface doit passer par le Dispatcher.
public sealed class CallManager : IDisposable
{
    private readonly P2PNode _node;
    private readonly object _gate = new();
    private UdpClient? _udp;
    private UdpClient? _videoUdp;
    private IPEndPoint? _remote;
    private WaveIn? _mic;
    private WaveOut? _speaker;
    private BufferedWaveProvider? _buffer;
    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _timeout;
    private int _remotePort, _localPort;
    private int _remoteVideoPort, _localVideoPort;

    public CallState State { get; private set; } = CallState.Idle;
    public string? Peer { get; private set; }
    public string CallId { get; private set; } = "";
    public bool IsOutgoing { get; private set; }
    public bool Video { get; private set; }
    public bool Muted { get; private set; }
    public bool CameraOn { get; set; } = true;
    public string EndReason { get; private set; } = "";
    public DateTime StartedAt { get; private set; }

    public event Action? Changed;
    public event Action<byte[]>? FrameReceived;   // image JPEG de l'autre personne
    public event Action<byte[]>? LocalFrame;      // ma propre image (aperçu)
    public event Action? CameraError;

    public CallManager(P2PNode node)
    {
        _node = node;
        _node.CallSignal += OnSignal;
    }

    // ---------- Actions de l'utilisateur ----------
    public string? StartCall(string phone, bool video = false)
    {
        lock (_gate)
        {
            if (State != CallState.Idle) return "Un appel est déjà en cours.";
            if (!_node.IsOnline(phone)) return "Ce contact est hors ligne.";
            CallId = Guid.NewGuid().ToString();
            Peer = phone; IsOutgoing = true; Video = video; Muted = false; CameraOn = true; EndReason = "";
            OpenUdp();
            State = CallState.Calling;
            _node.SendCallSignal(phone, new Packet
            {
                Type = PacketType.CallInvite, Id = CallId, Port = _localPort,
                Video = video, VideoPort = _localVideoPort
            });
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
            _node.SendCallSignal(Peer!, new Packet
            {
                Type = PacketType.CallAccept, Id = CallId, Port = _localPort,
                Video = Video, VideoPort = _localVideoPort
            });
        }
        BeginMedia();
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
    public void ToggleCamera() => CameraOn = !CameraOn;

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
                        CallId = p.Id; Peer = phone; IsOutgoing = false; Video = p.Video;
                        Muted = false; CameraOn = true; EndReason = "";
                        _remotePort = p.Port; _remoteVideoPort = p.VideoPort;
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
                    _remotePort = p.Port; _remoteVideoPort = p.VideoPort;
                }
                BeginMedia();
                break;

            case PacketType.CallReject:
                if (p.Id == CallId) Finish("Appel refusé");
                break;

            case PacketType.CallEnd:
                if (p.Id == CallId) Finish("Appel terminé");
                break;
        }
    }

    // ---------- Audio et vidéo ----------
    private void OpenUdp()
    {
        _udp?.Dispose();
        _udp = new UdpClient(0);   // port libre choisi par Windows
        _localPort = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

        _videoUdp?.Dispose(); _videoUdp = null;
        if (Video)
        {
            _videoUdp = new UdpClient(0);
            _localVideoPort = ((IPEndPoint)_videoUdp.Client.LocalEndPoint!).Port;
        }
    }

    private void BeginMedia()
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

                if (Video && _videoUdp != null)
                {
                    var vUdp = _videoUdp;
                    var vRemote = new IPEndPoint(IPAddress.Parse(ip), _remoteVideoPort);
                    _ = Task.Run(() => VideoReceiveLoop(vUdp, ct));
                    StartCamera(vUdp, vRemote, ct);
                }

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
                buf.AddSamples(r.Buffer, 0, r.Buffer.Length);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(30); }   // erreur ICMP passagère : on continue
        }
    }

    private async Task VideoReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                FrameReceived?.Invoke(r.Buffer);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { await Task.Delay(30); }
        }
    }

    // Lit la webcam, réduit l'image, l'encode en JPEG et l'envoie en UDP
    private void StartCamera(UdpClient udp, IPEndPoint remote, CancellationToken ct)
    {
        _ = Task.Run(() =>
        {
            VideoCapture? cap = null;
            try
            {
                cap = new VideoCapture(0, VideoCaptureAPIs.DSHOW);
                if (!cap.IsOpened()) { cap.Dispose(); cap = new VideoCapture(0); }
                if (!cap.IsOpened()) { CameraError?.Invoke(); return; }

                using var frame = new Mat();
                using var small = new Mat();
                while (!ct.IsCancellationRequested)
                {
                    if (!cap.Read(frame) || frame.Empty()) { Thread.Sleep(30); continue; }
                    if (!CameraOn) { Thread.Sleep(100); continue; }

                    int w = 480, h = Math.Max(1, frame.Height * w / frame.Width);
                    Cv2.Resize(frame, small, new OpenCvSharp.Size(w, h));
                    Cv2.ImEncode(".jpg", small, out var jpg, new ImageEncodingParam(ImwriteFlags.JpegQuality, 55));

                    LocalFrame?.Invoke(jpg);
                    if (jpg.Length < 60000)   // un paquet UDP ne dépasse pas ~64 Ko
                        try { udp.Send(jpg, jpg.Length, remote); } catch { }
                    Thread.Sleep(60);
                }
            }
            catch { CameraError?.Invoke(); }
            finally { cap?.Dispose(); }
        });
    }

    private void StopMedia()
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
            StopMedia();
            _udp?.Dispose(); _udp = null;
            _videoUdp?.Dispose(); _videoUdp = null;
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
            StopMedia();
            _udp?.Dispose();
            _videoUdp?.Dispose();
        }
    }
}