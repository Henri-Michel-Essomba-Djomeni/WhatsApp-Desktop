using System.Windows;

namespace WhatsApp.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var login = new LoginWindow();
        Theme.Apply(Session.Db.GetSetting("theme") != "light");
        if (login.ShowDialog() == true)
        {
            var main = new MainWindow();
            MainWindow = main;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            main.Show();
        }
        else Shutdown();
    }
}