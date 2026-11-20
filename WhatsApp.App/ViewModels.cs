using System.ComponentModel;
using WhatsApp.Core.Models;
using System.Windows.Media;

namespace WhatsApp.App;

public class Notifier : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class MessageVM : Notifier
{
    private string _text = "";
    private MessageStatus _status;
    public string Id { get; init; } = "";
    public string Time { get; init; } = "";
    public bool IsMine { get; init; }
    public bool IsFirst { get; init; }
        public string ReplyText { get; init; } = "";
    public bool HasReply => ReplyText.Length > 0;
    public string? MediaPath { get; set; }

    private bool _deleted;
    public bool IsDeleted { get => _deleted; set { _deleted = value; Raise(nameof(IsDeleted)); } }
    private ImageSource? _picture;
    public ImageSource? Picture { get => _picture; set { _picture = value; Raise(nameof(Picture)); } }

    public string Text { get => _text; set { _text = value; Raise(nameof(Text)); } }
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