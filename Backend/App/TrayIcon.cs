using System.Drawing;
using Serilog;

namespace TretioDLSS.App;

/// <summary>Notification area icon with Show, Start, Stop and Exit, on its own UI thread.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Thread _thread;
    private NotifyIcon? _icon;
    private System.Windows.Forms.ApplicationContext? _context;

    public TrayIcon(Action show, Action start, Action stop, Action exit, Func<bool> running)
    {
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try
            {
                var menu = new ContextMenuStrip();
                var startItem = new ToolStripMenuItem("Start", null, (_, _) => start());
                var stopItem = new ToolStripMenuItem("Stop", null, (_, _) => stop());
                menu.Items.Add(new ToolStripMenuItem("Show " + AppInfo.Name, null, (_, _) => show()));
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(startItem);
                menu.Items.Add(stopItem);
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => exit()));
                menu.Opening += (_, _) =>
                {
                    bool live = running();
                    startItem.Enabled = !live;
                    stopItem.Enabled = live;
                };
                _icon = new NotifyIcon
                {
                    Icon = LoadIcon(),
                    Text = AppInfo.Name,
                    ContextMenuStrip = menu,
                    Visible = true,
                };
                _icon.DoubleClick += (_, _) => show();
                _context = new System.Windows.Forms.ApplicationContext();
                ready.Set();
                System.Windows.Forms.Application.Run(_context);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Tray icon failed");
                ready.Set();
            }
        }) { IsBackground = true, Name = "Tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(3000);
    }

    private static Icon LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Resources", "icon.ico");
        if (File.Exists(path)) return new Icon(path, SystemInformation.SmallIconSize);
        return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
    }

    public void Dispose()
    {
        var icon = _icon;
        if (icon == null) return;
        try
        {
            icon.ContextMenuStrip?.Invoke(() =>
            {
                icon.Visible = false;
                icon.Dispose();
                _context?.ExitThread();
            });
        }
        catch { }
        _icon = null;
    }
}
