using System.IO;
using System.Windows;

namespace WhatsApp.App;

public partial class App : Application
{
    private static void Log(string where, Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "crash.log"),
                $"[{DateTime.Now}] {where}\n{ex}\n\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, a) =>
        {
            Log("Interface", a.Exception);
            MessageBox.Show(a.Exception.ToString(), "Erreur");
            a.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log("Domaine", (Exception)a.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log("Tâche", a.Exception); a.SetObserved(); };

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var login = new LoginWindow();
            if (login.ShowDialog() == true)
            {
                Theme.Apply(Session.Db.GetSetting("theme") != "light");
                var main = new MainWindow();
                MainWindow = main;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                main.Show();
            }
            else Shutdown();
        }
        catch (Exception ex)
        {
            Log("Démarrage", ex);
            MessageBox.Show(ex.ToString(), "Erreur au démarrage");
            Shutdown();
        }
    }
}