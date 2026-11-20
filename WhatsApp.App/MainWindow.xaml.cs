using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using WhatsApp.Core.Models;

namespace WhatsApp.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ChatVM> _chats = new();
    private readonly ObservableCollection<MessageVM> _messages = new();
    private readonly HashSet<string> _typing = new();
    private readonly DispatcherTimer _typingTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _current;
    private bool _refreshing;
    private bool _iTyping;
    private bool _unreadOnly;

    // Barre de titre sombre (Windows 10/11)
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int on = 1;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
    }

    public MainWindow()
    {
        InitializeComponent();
        ChatList.ItemsSource = _chats;
        MessageList.ItemsSource = _messages;
        ProfileInitial.Text = Session.Me.Name.Length > 0 ? Session.Me.Name[..1].ToUpperInvariant() : "?";

        var node = Session.Node;   // ses événements arrivent sur des threads de fond
        node.MessageReceived += m => Dispatcher.InvokeAsync(() => OnIncoming(m));
        node.StatusChanged += (id, s) => Dispatcher.InvokeAsync(() => OnStatus(id, s));
        node.PeerPresence += (p, on) => Dispatcher.InvokeAsync(() => { RefreshChats(); UpdateHeader(); });
        node.ContactUpdated += c => Dispatcher.InvokeAsync(() => { RefreshChats(); UpdateHeader(); });
        node.Typing += (p, t) => Dispatcher.InvokeAsync(() => OnTyping(p, t));
        node.MessageDeleted += id => Dispatcher.InvokeAsync(() => OnDeleted(id));

        _typingTimer.Tick += (_, _) => { _typingTimer.Stop(); SetMyTyping(false); };
        Closed += (_, _) => Session.Stop();

        RefreshChats();
        ShowChat(null);
    }

    // ---------- Colonne d'icônes et filtres ----------
    private void Rail_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton rb) return;
        var tag = rb.Tag as string;
        ChatsPanel.Visibility = tag == "chats" ? Visibility.Visible : Visibility.Collapsed;
        OtherPanel.Visibility = tag == "chats" ? Visibility.Collapsed : Visibility.Visible;
        switch (tag)
        {
            case "calls": OtherTitle.Text = "Appels"; OtherText.Text = "Les appels vocaux et vidéo arrivent bientôt."; break;
            case "status": OtherTitle.Text = "Statuts"; OtherText.Text = "Les statuts arrivent bientôt."; break;
            case "settings": OtherTitle.Text = "Paramètres"; OtherText.Text = "Les paramètres arrivent bientôt."; break;
        }
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton rb) return;
        _unreadOnly = (rb.Tag as string) == "unread";
        RefreshChats();
    }

    // ---------- Liste des discussions ----------
    private void RefreshChats()
    {
        _refreshing = true;
        var filter = SearchBox.Text.Trim().ToLowerInvariant();
        var all = Session.Db.GetChats();

        var total = all.Sum(s => s.Unread);
        RailBadge.Visibility = total > 0 ? Visibility.Visible : Visibility.Collapsed;
        RailBadgeText.Text = total > 99 ? "99+" : total.ToString();

        _chats.Clear();
        foreach (var s in all)
        {
            if (_unreadOnly && s.Unread == 0) continue;
            if (filter.Length > 0 &&
                !(s.Contact.Name.ToLowerInvariant().Contains(filter) || s.Contact.Phone.Contains(filter))) continue;
            _chats.Add(new ChatVM
            {
                Phone = s.Contact.Phone,
                Name = string.IsNullOrWhiteSpace(s.Contact.Name) ? s.Contact.Phone : s.Contact.Name,
                LastText = s.LastMessage == null ? "" : Preview(s.LastMessage),
                Time = s.LastMessage == null ? "" : ShortTime(s.LastMessage.SentAt),
                Unread = s.Unread,
                Online = Session.Node.IsOnline(s.Contact.Phone),
                Typing = _typing.Contains(s.Contact.Phone)
            });
        }
        ChatList.SelectedItem = _chats.FirstOrDefault(c => c.Phone == _current);
        _refreshing = false;
    }

    private void ChatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing) return;
        if (ChatList.SelectedItem is ChatVM vm && vm.Phone != _current) ShowChat(vm.Phone);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshChats();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e)
    {
        var input = Dialogs.Ask(this, "Nouvelle discussion", "Numéro de téléphone du contact");
        if (input == null) return;
        var phone = new string(input.Where(char.IsDigit).ToArray());
        if (phone.Length < 6 || phone == Session.Me.Phone) return;
        if (Session.Db.GetContact(phone) == null)
            Session.Db.SaveContact(new Contact { Phone = phone, Name = phone });
        ShowChat(phone);
    }

    // ---------- Conversation ----------
    private void ShowChat(string? phone)
    {
        SetMyTyping(false);
        _current = phone;
        _messages.Clear();
        ChatPanel.Visibility = phone != null ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = phone != null ? Visibility.Collapsed : Visibility.Visible;
        if (phone != null)
        {
            bool? prev = null;
            foreach (var m in Session.Db.GetMessages(phone))
            {
                var vm = ToVM(m, prev);
                prev = vm.IsMine;
                _messages.Add(vm);
            }
            Session.Node.MarkChatRead(phone);
            UpdateHeader();
            ScrollToEnd();
            Input.Focus();
        }
        RefreshChats();
    }

    private void UpdateHeader()
    {
        if (_current == null) return;
        var c = Session.Db.GetContact(_current);
        var name = string.IsNullOrWhiteSpace(c?.Name) ? _current : c!.Name;
        ChatName.Text = name;
        ChatInitial.Text = name[..1].ToUpperInvariant();
        ChatStatus.Text = _typing.Contains(_current) ? "écrit…"
            : Session.Node.IsOnline(_current) ? "en ligne" : "hors ligne";
    }

    private MessageVM ToVM(ChatMessage m, bool? prevMine)
    {
        var mine = m.SenderPhone == Session.Me.Phone;
        return new MessageVM
        {
            Id = m.Id,
            IsMine = mine,
            IsFirst = prevMine != mine,
            Time = m.SentAt.ToString("HH:mm"),
            Text = m.IsDeleted ? "🚫 Ce message a été supprimé" : Preview(m),
            Status = m.Status
        };
    }

    private static string Preview(ChatMessage m) => m.IsDeleted ? "🚫 Message supprimé" : m.Type switch
    {
        MessageType.Image => "📷 Photo" + (m.Text.Length > 0 ? " " + m.Text : ""),
        MessageType.Voice => "🎤 Message vocal",
        MessageType.File => "📎 " + Path.GetFileName(m.MediaPath ?? "Fichier"),
        MessageType.Sticker => "Sticker",
        _ => m.Text
    };

    private static string ShortTime(DateTime t) =>
        t.Date == DateTime.Today ? t.ToString("HH:mm")
        : t.Date == DateTime.Today.AddDays(-1) ? "Hier" : t.ToString("dd/MM/yyyy");

    private void ScrollToEnd() =>
        Dispatcher.InvokeAsync(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);

    // ---------- Événements réseau ----------
    private void OnIncoming(ChatMessage m)
    {
        if (m.ChatPhone == _current)
        {
            _messages.Add(ToVM(m, _messages.LastOrDefault()?.IsMine));
            Session.Node.MarkChatRead(m.ChatPhone);
            ScrollToEnd();
        }
        else System.Media.SystemSounds.Asterisk.Play();
        RefreshChats();
    }

    private void OnStatus(string id, MessageStatus s)
    {
        var vm = _messages.FirstOrDefault(x => x.Id == id);
        if (vm != null && s > vm.Status) vm.Status = s;
    }

    private void OnDeleted(string id)
    {
        var vm = _messages.FirstOrDefault(x => x.Id == id);
        if (vm != null) vm.Text = "🚫 Ce message a été supprimé";
        RefreshChats();
    }

    private void OnTyping(string phone, bool typing)
    {
        if (typing) _typing.Add(phone); else _typing.Remove(phone);
        RefreshChats();
        UpdateHeader();
    }

    // ---------- Envoi ----------
    private void Send_Click(object sender, RoutedEventArgs e) => Send();

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Send(); }
    }

    private async void Send()
    {
        var text = Input.Text.Trim();
        if (text.Length == 0 || _current == null) return;
        Input.Clear();
        SetMyTyping(false);
        var m = await Session.Node.SendTextAsync(_current, text);
        if (m.ChatPhone == _current)
        {
            _messages.Add(ToVM(m, _messages.LastOrDefault()?.IsMine));
            ScrollToEnd();
        }
        RefreshChats();
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        var empty = Input.Text.Length == 0;
        Placeholder.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        SendBtn.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        MicBtn.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) { SetMyTyping(true); _typingTimer.Stop(); _typingTimer.Start(); }
    }

    private void SetMyTyping(bool typing)
    {
        if (_current == null || _iTyping == typing) return;
        _iTyping = typing;
        Session.Node.SendTyping(_current, typing);
    }
}