using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WhatsApp.App;

public static class Dialogs
{
    public static string? Ask(Window owner, string title, string label)
    {
        var app = Application.Current;
        var box = new TextBox { Style = (Style)app.FindResource("Field"), Margin = new Thickness(0, 8, 0, 14) };
        var ok = new Button
        {
            Content = "OK", IsDefault = true, Style = (Style)app.FindResource("GreenButton"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = (Brush)app.FindResource("TextMain") });
        panel.Children.Add(box);
        panel.Children.Add(ok);
        var w = new Window
        {
            Title = title, Content = panel, Width = 360, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner,
            ResizeMode = ResizeMode.NoResize, Background = (Brush)app.FindResource("BgHeader")
        };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; w.DialogResult = true; };
        w.Loaded += (_, _) => box.Focus();
        w.ShowDialog();
        return result;
    }
}