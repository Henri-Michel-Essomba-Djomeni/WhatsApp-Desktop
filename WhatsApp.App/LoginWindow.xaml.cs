using System.Windows;

namespace WhatsApp.App;

public partial class LoginWindow : Window
{
    private string _code = "";
    private string _phone = "";

    public LoginWindow() { InitializeComponent(); PhoneBox.Focus(); }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        _phone = new string(PhoneBox.Text.Where(char.IsDigit).ToArray());
        if (_phone.Length < 6) { Error1.Text = "Numéro invalide."; return; }
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { Error1.Text = "Entrez votre nom."; return; }
        _code = Random.Shared.Next(100000, 999999).ToString();
        MessageBox.Show(this, $"Votre code de vérification WhatsApp est : {_code}", "Code de vérification");
        Step1.Visibility = Visibility.Collapsed;
        Step2.Visibility = Visibility.Visible;
        CodeBox.Focus();
    }

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        if (CodeBox.Text.Trim() != _code) { Error2.Text = "Code incorrect."; return; }
        Session.Start(_phone, NameBox.Text.Trim());
        DialogResult = true;
    }
}