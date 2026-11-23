using System.ComponentModel;
using System.Windows.Media;
using WhatsApp.Core.Models;

namespace WhatsApp.App;

public class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// Une barre de l'onde sonore
public class BarVM : Notifier
{
    private bool _played;
    public double Height { get; init; }
    public bool IsPlayed { get => _played; set { if (_played == value) return; _played = value; Raise(nameof(IsPlayed)); } }
}

public class MessageVM : Notifier
{
    private string _text = "";
    private MessageStatus _status;
    private ImageSource? _picture;
    private bool _deleted, _isVoice, _playing;
    private string _voiceTime = "";

    public string Id { get; init; } = "";
    public string Time { get; init; } = "";
    public bool IsMine { get; init; }
    public bool IsFirst { get; init; }
    public bool IsSticker { get; init; }
    public string ReplyText { get; init; } = "";
    public bool HasReply => ReplyText.Length > 0;
    public string? MediaPath { get; set; }
    public List<BarVM> Bars { get; init; } = new();
    public TimeSpan Duration { get; init; }

    public string Text { get => _text; set { _text = value; Raise(nameof(Text)); } }
    public ImageSource? Picture { get => _picture; set { _picture = value; Raise(nameof(Picture)); } }
    public bool IsDeleted { get => _deleted; set { _deleted = value; Raise(nameof(IsDeleted)); } }
    public bool IsVoice { get => _isVoice; set { _isVoice = value; Raise(nameof(IsVoice)); } }
    public string VoiceTime { get => _voiceTime; set { _voiceTime = value; Raise(nameof(VoiceTime)); } }
    public bool IsPlaying
    {
        get => _playing;
        set { _playing = value; Raise(nameof(IsPlaying)); Raise(nameof(PlayGlyph)); }
    }
    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768";   // pause / lecture (Segoe MDL2)

    public MessageStatus Status
    {
        get => _status;
        set { _status = value; Raise(nameof(Status)); Raise(nameof(Tick)); Raise(nameof(IsRead)); }
    }
    public string Tick => Status switch { MessageStatus.Pending => "○", MessageStatus.Sent => "✓", _ => "✓✓" };
    public bool IsRead => Status == MessageStatus.Read;
}

public class ChatVM
{
    public ImageSource? Avatar { get; init; }
    public string Phone { get; init; } = "";
    public string Name { get; init; } = "";
    public string LastText { get; init; } = "";
    public string Time { get; init; } = "";
    public int Unread { get; init; }
    public bool Online { get; init; }
    public bool Typing { get; init; }
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public string Preview => Typing ? "écrit…" : LastText;
    public bool HasUnread => Unread > 0;
}

public class StatusVM
{
    public string Phone { get; init; } = "";
    public string Name { get; init; } = "";
    public string Sub { get; init; } = "";
    public DateTime Last { get; init; }
    public ImageSource? Avatar { get; init; }
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
}