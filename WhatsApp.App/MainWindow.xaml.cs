using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WhatsApp.Core.Models;
using NAudio.Wave;

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
    private MessageVM? _replyTo;
    private WaveIn? _wave;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _playTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private MessageVM? _playing;
    private WaveFileWriter? _writer;
    private string? _recPath;
    private string? _recChat;

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
        BuildEmojiPanel();
        LoadMyProfile();
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
                _playTimer.Tick += PlayTick;
        _player.MediaEnded += (_, _) => StopPlayback();

        RefreshChats();
        ShowChat(null);
    }

    // ---------- Colonne d'icônes et filtres ----------
    private void Rail_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton rb) return;
        var tag = rb.Tag as string;
        ChatsPanel.Visibility = tag == "chats" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        OtherPanel.Visibility = tag is "calls" or "status" ? Visibility.Visible : Visibility.Collapsed;
        switch (tag)
        {
            case "calls": OtherTitle.Text = "Appels"; OtherText.Text = "Les appels vocaux et vidéo arrivent bientôt."; break;
            case "status": OtherTitle.Text = "Statuts"; OtherText.Text = "Les statuts arrivent bientôt."; break;
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
                Typing = _typing.Contains(s.Contact.Phone),
                Avatar = LoadImage(s.Contact.AvatarPath)
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

        // ---------- Paramètres ----------
    private void LoadMyProfile()
    {
        var me = Session.Me;
        var img = LoadImage(me.AvatarPath);
        ProfilePhoto.ImageSource = img;
        SetPhoto.ImageSource = img;
        var initial = me.Name.Length > 0 ? me.Name[..1].ToUpperInvariant() : "?";
        ProfileInitial.Text = initial;
        SetInitial.Text = initial;
        SetName.Text = me.Name;
        SetAbout.Text = me.About;
        SetPhone.Text = me.Phone;
        var light = Session.Db.GetSetting("theme") == "light";
        ThemeDark.IsChecked = !light;
        ThemeLight.IsChecked = light;
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = SetName.Text.Trim();
        if (name.Length == 0) { SaveInfo.Text = "Le nom est obligatoire."; return; }
        Session.Me.Name = name;
        Session.Me.About = SetAbout.Text.Trim();
        Session.Db.SetSetting("name", Session.Me.Name);
        Session.Db.SetSetting("about", Session.Me.About);
        Session.Node.BroadcastProfile();
        LoadMyProfile();
        SaveInfo.Text = "Enregistré ✓";
    }

    private void ChangePhoto_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Photo de profil", Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp" };
        if (dlg.ShowDialog(this) != true) return;
        if (new FileInfo(dlg.FileName).Length > 5 * 1024 * 1024)
        {
            MessageBox.Show(this, "La photo dépasse 5 Mo.", "Photo trop lourde");
            return;
        }
        var path = Path.Combine(Session.Db.MediaFolder, $"me_avatar_{DateTime.Now.Ticks}{Path.GetExtension(dlg.FileName)}");
        File.Copy(dlg.FileName, path, true);
        Session.Me.AvatarPath = path;
        Session.Db.SetSetting("avatar", path);
        Session.Node.BroadcastProfile();
        LoadMyProfile();
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton rb) return;
        var dark = (rb.Tag as string) == "dark";
        Theme.Apply(dark);
        Session.Db.SetSetting("theme", dark ? "dark" : "light");
    }

    // ---------- Conversation ----------
    private void ShowChat(string? phone)
    {
        SetMyTyping(false);
        StopPlayback();
        ClearReply();
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
        ChatPhoto.ImageSource = LoadImage(c?.AvatarPath);
        ChatStatus.Text = _typing.Contains(_current) ? "écrit…"
            : Session.Node.IsOnline(_current) ? "en ligne" : "hors ligne";
    }

        private MessageVM ToVM(ChatMessage m, bool? prevMine)
    {
        var mine = m.SenderPhone == Session.Me.Phone;
        var pic = m.Type == MessageType.Image && !m.IsDeleted ? LoadImage(m.MediaPath) : null;
        var voice = m.Type == MessageType.Voice && !m.IsDeleted;
        var (bars, dur) = voice ? AnalyzeWav(m.MediaPath) : (new List<BarVM>(), TimeSpan.Zero);
        var reply = "";
        if (m.ReplyToId != null)
        {
            var r = Session.Db.GetMessage(m.ReplyToId);
            if (r != null)
                reply = (r.SenderPhone == Session.Me.Phone ? "Vous : " : "") + Preview(r);
        }
        return new MessageVM
        {
            Id = m.Id,
            IsMine = mine,
            IsFirst = prevMine != mine,
            Time = m.SentAt.ToString("HH:mm"),
            Text = m.IsDeleted ? "🚫 Ce message a été supprimé" : voice ? "" : pic != null ? m.Text : Preview(m),
            Status = m.Status,
            Picture = pic,
            ReplyText = reply,
            MediaPath = m.IsDeleted ? null : m.MediaPath,
            IsDeleted = m.IsDeleted,
            IsVoice = voice,
            Bars = bars,
            Duration = dur,
            VoiceTime = Fmt(dur)
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
        if (vm != null) MarkDeleted(vm);
        RefreshChats();
    }

    private static void MarkDeleted(MessageVM vm)
    {
        vm.Text = "🚫 Ce message a été supprimé";
        vm.Picture = null;
        vm.MediaPath = null;
        vm.IsVoice = false;
        vm.IsDeleted = true;
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
        var replyId = _replyTo?.Id;
        ClearReply();
        var m = await Session.Node.SendTextAsync(_current, text, replyId);
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

    // ---------- Images, fichiers, emojis ----------
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    private static ImageSource? LoadImage(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;   // ne verrouille pas le fichier
            bi.DecodePixelWidth = 400;
            bi.CreateOptions = BitmapCreateOptions.IgnoreImageCache;   // la photo change, le chemin reste
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var dlg = new OpenFileDialog
        {
            Title = "Joindre",
            Filter = "Images|*.jpg;*.jpeg;*.png;*.gif;*.bmp|Tous les fichiers|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames)
        {
            if (new FileInfo(f).Length > 30 * 1024 * 1024)
            {
                MessageBox.Show(this, $"{Path.GetFileName(f)} dépasse 30 Mo.", "Fichier trop volumineux");
                continue;
            }
            var type = ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant())
                ? MessageType.Image : MessageType.File;
            var m = await Session.Node.SendMediaAsync(_current, f, type);
            if (m.ChatPhone == _current)
            {
                _messages.Add(ToVM(m, _messages.LastOrDefault()?.IsMine));
                ScrollToEnd();
            }
        }
        RefreshChats();
    }

    private static readonly string[] Emojis =
        ("😀 😃 😄 😁 😆 😅 😂 🤣 😊 😇 🙂 😉 😍 🥰 😘 😋 😛 😜 🤪 😎 🤩 🥳 😏 😒 😞 😔 😟 😕 🙁 😣 😢 😭 😤 😠 😡 🤯 😳 🥵 🥶 😱 😨 😰 🤗 🤔 🤭 🤫 😐 😶 🙄 😴 🤤 😷 🤒 🤕 🥴 🤠 " +
         "👍 👎 👌 ✌️ 🤞 🤟 🤘 👏 🙌 🙏 💪 👋 🤝 ✍️ 🫶 " +
         "❤️ 🧡 💛 💚 💙 💜 🖤 🤍 💔 💕 💖 💯 🔥 ✨ ⭐ 🎉 🎂 🎁 " +
         "🐶 🐱 🦁 🐵 🌹 🌞 🌙 ☀️ 🌧️ 🍕 🍔 🍟 ☕ 🍺 ⚽ 🏀 🎵 📚 💻 📱 🚗 ✈️ 🏠 ✅ ❌ ❓ ❗").Split(' ');

    private void BuildEmojiPanel()
    {
        foreach (var emoji in Emojis)
        {
            var b = new Button
            {
                Content = emoji, Style = (Style)FindResource("IconButton"),
                FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 22, Width = 38, Height = 38,
                Foreground = (Brush)FindResource("TextMain")
            };
            b.Click += (_, _) =>
            {
                var i = Input.CaretIndex;
                Input.Text = Input.Text.Insert(i, emoji);
                Input.CaretIndex = i + emoji.Length;
                Input.Focus();
            };
            EmojiPanel.Children.Add(b);
        }
    }

    private void Emoji_Click(object sender, RoutedEventArgs e) => EmojiPopup.IsOpen = !EmojiPopup.IsOpen;

    // ---------- Menu contextuel : répondre, copier, supprimer ----------
    private static MessageVM? VmOf(object sender) => (sender as FrameworkElement)?.DataContext as MessageVM;

    private void Reply_Click(object sender, RoutedEventArgs e)
    {
        var vm = VmOf(sender);
        if (vm == null || vm.IsDeleted) return;
        _replyTo = vm;
        ReplyBarText.Text = (vm.IsMine ? "Vous : " : "") + (vm.IsVoice ? "🎤 Message vocal" : vm.Text);
        ReplyBar.Visibility = Visibility.Visible;
        Input.Focus();
    }

    private void CancelReply_Click(object sender, RoutedEventArgs e) => ClearReply();

    private void ClearReply()
    {
        _replyTo = null;
        ReplyBar.Visibility = Visibility.Collapsed;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var vm = VmOf(sender);
        if (vm != null && !vm.IsDeleted && vm.Text.Length > 0) Clipboard.SetText(vm.Text);
    }

    // Mon message : supprimé chez les deux. Message reçu : supprimé chez moi seulement.
    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var vm = VmOf(sender);
        if (vm == null || vm.IsDeleted || _current == null) return;
        if (vm.IsMine) Session.Node.DeleteForEveryone(_current, vm.Id);
        else Session.Db.DeleteMessage(vm.Id);
        MarkDeleted(vm);
        RefreshChats();
    }

    // Double-clic sur une bulle : ouvre l'image ou le fichier
    private void Bubble_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || VmOf(sender) is not { IsVoice: false, MediaPath: { } path }) return;
        if (!File.Exists(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { MessageBox.Show(this, "Impossible d'ouvrir ce fichier.", "Erreur"); }
    }

        // ---------- Messages vocaux ----------
    private void Mic_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        if (_wave == null) StartRecording(); else _wave.StopRecording();
    }

    private void StartRecording()
    {
        try
        {
            _recChat = _current;
            _recPath = Path.Combine(Path.GetTempPath(), $"vocal_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            _wave = new WaveIn { WaveFormat = new WaveFormat(16000, 16, 1) };
            _writer = new WaveFileWriter(_recPath, _wave.WaveFormat);
            _wave.DataAvailable += (_, a) => _writer?.Write(a.Buffer, 0, a.BytesRecorded);
            _wave.RecordingStopped += (_, _) => Dispatcher.InvokeAsync(FinishRecording);
            _wave.StartRecording();
            MicBtn.Foreground = Brushes.IndianRed;
            MicBtn.ToolTip = "Cliquez pour arrêter et envoyer";
        }
        catch (Exception ex)
        {
            _wave?.Dispose(); _writer?.Dispose(); _wave = null; _writer = null;
            MessageBox.Show(this, "Micro indisponible : " + ex.Message, "Message vocal");
        }
    }

    private async void FinishRecording()
    {
        _writer?.Dispose(); _writer = null;
        _wave?.Dispose(); _wave = null;
        MicBtn.ClearValue(Control.ForegroundProperty);
        MicBtn.ToolTip = "Enregistrer un message vocal";
        if (_recPath == null || _recChat == null || !File.Exists(_recPath)) return;
        var m = await Session.Node.SendMediaAsync(_recChat, _recPath, MessageType.Voice);
        if (m.ChatPhone == _current)
        {
            _messages.Add(ToVM(m, _messages.LastOrDefault()?.IsMine));
            ScrollToEnd();
        }
        RefreshChats();
    }   

        // ---------- Lecture des vocaux + ondes ----------
    private static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

    // Calcule la hauteur de 40 barres à partir des pics du fichier WAV
    private static (List<BarVM>, TimeSpan) AnalyzeWav(string? path, int count = 40)
    {
        var peaks = new double[count];
        var dur = TimeSpan.Zero;
        var ok = false;
        try
        {
            if (path != null && File.Exists(path))
            {
                using var r = new WaveFileReader(path);
                dur = r.TotalTime;
                if (r.WaveFormat.BitsPerSample == 16)
                {
                    var data = new byte[r.Length];
                    var samples = r.Read(data, 0, data.Length) / 2;
                    for (int i = 0; i < samples; i++)
                    {
                        var v = Math.Abs((int)BitConverter.ToInt16(data, i * 2)) / 32768.0;
                        var b = Math.Min(count - 1, (int)((long)i * count / samples));
                        if (v > peaks[b]) peaks[b] = v;
                    }
                    ok = samples > 0;
                }
            }
        }
        catch { /* fichier illisible : on affiche une onde par défaut */ }

        var max = peaks.Max();
        var bars = new List<BarVM>();
        for (int i = 0; i < count; i++)
        {
            var h = ok && max > 0 ? 4 + Math.Sqrt(peaks[i] / max) * 24 : 4 + (i * 7 % 11) * 2;
            bars.Add(new BarVM { Height = h });
        }
        return (bars, dur);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var vm = VmOf(sender);
        if (vm?.MediaPath == null || !File.Exists(vm.MediaPath)) return;

        if (_playing == vm)   // même message : pause / reprise
        {
            if (vm.IsPlaying) { _player.Pause(); _playTimer.Stop(); vm.IsPlaying = false; }
            else { _player.Play(); _playTimer.Start(); vm.IsPlaying = true; }
            return;
        }
        StopPlayback();
        _playing = vm;
        _player.Open(new Uri(vm.MediaPath));
        _player.Play();
        vm.IsPlaying = true;
        _playTimer.Start();
    }

    private void PlayTick(object? s, EventArgs e)
    {
        if (_playing == null) return;
        var total = _playing.Duration.TotalSeconds;
        if (total <= 0) return;
        var n = (int)Math.Round(_player.Position.TotalSeconds / total * _playing.Bars.Count);
        for (int i = 0; i < _playing.Bars.Count; i++) _playing.Bars[i].IsPlayed = i < n;
        _playing.VoiceTime = Fmt(_player.Position);
    }

    private void StopPlayback()
    {
        _playTimer.Stop();
        _player.Stop();
        if (_playing == null) return;
        _playing.IsPlaying = false;
        foreach (var b in _playing.Bars) b.IsPlayed = false;
        _playing.VoiceTime = Fmt(_playing.Duration);
        _playing = null;
    }

    private void SetMyTyping(bool typing)
    {
        if (_current == null || _iTyping == typing) return;
        _iTyping = typing;
        Session.Node.SendTyping(_current, typing);
    }
}