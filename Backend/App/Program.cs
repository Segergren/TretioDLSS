using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Photino.NET;
using Serilog;
using Serilog.Events;
using TretioDLSS.Capture;
using TretioDLSS.Core;
using TretioDLSS.Diagnostics;
using TretioDLSS.Nvidia;
using TretioDLSS.Platform;

namespace TretioDLSS.App;

public static class Program
{
    private static Mutex? _mutex;
    private static PhotinoWindow? _window;
    private static PhotinoApplication? _app;
    private static AppHost? _host;
    private static Serilog.Core.LoggingLevelSwitch _level = new(Environment.GetEnvironmentVariable("TRETIO_DEBUG") == "1" ? LogEventLevel.Debug : LogEventLevel.Information);

    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack install and update hooks; they exit on their own arguments and touch no graphics.
        Velopack.VelopackApp.Build().Run();
        var cli = CommandLine.Parse(args);
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        WaitForPredecessor(args);
        ConfigureLogging(cli);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Warning(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        if (cli.PipelineHost != null) return PipelineChild.Run(cli.PipelineHost);
        if (cli.DecodeDumps != null) return Tools.DecodeDumps(cli.DecodeDumps, cli.Size);
        if (cli.AudioTestSeconds > 0) return Tools.AudioTest(cli.AudioTestSeconds, cli.AudioIn, cli.Report);

        var settings = new SettingsService(AppInfo.SettingsFile);
        bool firstRun = !settings.Load();
        if (firstRun && !cli.IsToolMode) settings.Current.Ui.Onboarded = false;

        if (cli.Headless || !cli.IsToolMode) Engines.SafeMode.CheckAtStartup();

        try
        {
            if (cli.Headless) return HeadlessRunner.Run(cli);
            if (cli.StreamHostSeconds > 0) return StreamHostTool.Run(cli, cli.StreamHostSeconds);
            if (cli.TvScan)
            {
                foreach (var tv in Streaming.TvDiscovery.ScanAsync().GetAwaiter().GetResult())
                    Console.WriteLine($"{tv.Name} | model {tv.Model} | {tv.Manufacturer} | {tv.Address} | id {tv.Id}");
                return 0;
            }
            if (cli.PatternWindow != null) return PatternWindow.Run(cli.PatternWindow);
            if (cli.CaptureInfo) return Tools.CaptureInfo();
            if (cli.RecordRaw is { } rec) return Tools.RecordRaw(cli, rec.Seconds, rec.Dir);
            if (cli.LatencyTest) return Tools.LatencyTest(settings.Current, cli.Source);
            if (cli.Install is string id) return Tools.Install(id, cli.InstallTo);
        }
        finally
        {
            if (cli.IsToolMode) Log.CloseAndFlush();
        }

        // Windowed app: single instance, later launches forward their command to it.
        _mutex = new Mutex(true, AppInfo.MutexName, out bool createdNew);
        if (!createdNew)
        {
            ForwardToRunningInstance(cli);
            return 0;
        }
        try
        {
            return RunApp(cli, settings, firstRun);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "App crashed");
            return 1;
        }
        finally
        {
            // A cleanup step that hangs (a device, a driver) must not keep an invisible process alive.
            _shutdownWatchdog = new Timer(_ => { Log.Warning("Shutdown took over 5 s, ending the process"); Log.CloseAndFlush(); Environment.Exit(0); }, null, 5000, Timeout.Infinite);
            _host?.Dispose();
            Log.Information("Exited");
            Log.CloseAndFlush();
            try { _mutex.ReleaseMutex(); } catch { }
        }
    }

    private static void ConfigureLogging(CommandLine cli)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppInfo.LogFile)!);
        string file = cli.PipelineHost != null ? Path.Combine(Path.GetDirectoryName(AppInfo.LogFile)!, "picture.log")
            : cli.IsToolMode ? Path.Combine(Path.GetDirectoryName(AppInfo.LogFile)!, "tools.log") : AppInfo.LogFile;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(_level)
            .WriteTo.Debug()
            .WriteTo.File(file, fileSizeLimitBytes: 10 * 1024 * 1024, rollOnFileSizeLimit: true, retainedFileCountLimit: 3, shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        Log.Information("TretioDLSS {Version} starting, args: {Args}", AppInfo.Version, string.Join(' ', Environment.GetCommandLineArgs().Skip(1)));
    }

    private static void WaitForPredecessor(string[] args)
    {
        int i = Array.IndexOf(args, "--await-pid");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out int pid)) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.WaitForExit(15000)) p.Kill();
        }
        catch
        {
        }
    }

    private static void ForwardToRunningInstance(CommandLine cli)
    {
        string command = cli.Exit ? "EXIT" : cli.Stop ? "STOP" : cli.Start ? "START" + (cli.Profile != null ? "|" + cli.Profile : "") : "SHOW";
        try
        {
            using var pipe = new NamedPipeClientStream(".", AppInfo.PipeName, PipeDirection.Out);
            pipe.Connect(3000);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(command);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not reach the running instance");
        }
    }

    private static void StartPipeServer()
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(AppInfo.PipeName, PipeDirection.In);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    string? line = reader.ReadLine();
                    Log.Information("Pipe command {Command}", line);
                    if (line == null) continue;
                    if (line == "SHOW") ShowWindow();
                    else if (line == "EXIT") ExitApp();
                    else if (line == "STOP") Task.Run(() => _host?.StopPipeline());
                    else if (line.StartsWith("START"))
                    {
                        var parts = line.Split('|', 2);
                        Task.Run(() =>
                        {
                            if (parts.Length > 1) _host?.Messages.Dispatch(JsonSerializer.Serialize(new { Method = "SelectProfile", Parameters = new { name = parts[1] } }));
                            Thread.Sleep(200);
                            _host?.StartPipeline();
                        });
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Pipe server error");
                    Thread.Sleep(500);
                }
            }
        }) { IsBackground = true, Name = "InstancePipe" };
        t.Start();
    }

    private static int RunApp(CommandLine cli, SettingsService settings, bool firstRun)
    {
        _level.MinimumLevel = settings.Current.Advanced.LogLevel == "debug" ? LogEventLevel.Debug : LogEventLevel.Information;
        settings.Changed += s => _level.MinimumLevel = s.Advanced.LogLevel == "debug" ? LogEventLevel.Debug : LogEventLevel.Information;
        StartPipeServer();

        bool dev = Debugger.IsAttached || Environment.GetEnvironmentVariable("TRETIO_DEV") == "1";
        // TRETIO_TOKEN fixes the launch token for UI test runs in a browser; normal runs use a random one.
        var messages = new MessageService(AppInfo.WebSocketPort, Environment.GetEnvironmentVariable("TRETIO_TOKEN"));
        // In dev mode the UI comes from Vite (hot reload). F5 starts Vite alongside the app, so wait for it;
        // if it never answers, use the built-in copy instead of showing an empty window.
        if (dev && !WaitForPort(AppInfo.ViteDevPort, TimeSpan.FromSeconds(30)))
        {
            Log.Warning("Vite did not answer on port {Port} within 30 s; using the built-in frontend", AppInfo.ViteDevPort);
            dev = false;
        }
        string baseUrl = dev ? $"http://localhost:{AppInfo.ViteDevPort}" : FrontendServer.Start(AppInfo.FrontendPortStart);
        if (dev) Log.Information("Frontend from Vite at {Url} (hot reload)", baseUrl);
        messages.AllowOrigin(baseUrl);
        if (dev) messages.AllowOrigin($"http://localhost:{AppInfo.ViteDevPort}");
        messages.Start();

        _host = new AppHost(settings, messages);
        _host.Start();
        if (firstRun) Log.Information("First run");
        if (cli.Start) Task.Run(async () =>
        {
            await Task.Delay(800);
            if (cli.Profile != null) messages.Dispatch(JsonSerializer.Serialize(new { Method = "SelectProfile", Parameters = new { name = cli.Profile } }));
            _host.StartPipeline();
        });

        string url = $"{baseUrl}/index.html?token={messages.Token}&wsPort={messages.Port}&v={Uri.EscapeDataString(AppInfo.Version)}";
        _app = new PhotinoApplication { ShutdownMode = PhotinoShutdownMode.OnMainWindowClose };
        _app.Startup += (_, _) => CreateWindow(url, cli.Minimized);
        using var tray = new TrayIcon(
            show: ShowWindow,
            start: () => Task.Run(() => _host?.StartPipeline()),
            stop: () => Task.Run(() => _host?.StopPipeline()),
            exit: ExitApp,
            running: () => _host?.PipelineRunning == true);
        _app.Run(null);
        Log.Information("Window closed, shutting down");
        return 0;
    }

    private static bool WaitForPort(int port, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        bool logged = false;
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                if (client.ConnectAsync("localhost", port).Wait(500) && client.Connected) return true;
            }
            catch { }
            if (!logged)
            {
                Log.Information("Waiting for Vite on port {Port}", port);
                logged = true;
            }
            Thread.Sleep(250);
        }
        return false;
    }

    private static void CreateWindow(string url, bool minimized)
    {
        string icon = Path.Combine(AppContext.BaseDirectory, "Resources", "icon.ico");
        var window = new PhotinoWindow()
            .SetTitle(AppInfo.Name)
            .SetUseOsDefaultSize(false)
            .SetSize(new Size(1180, 740))
            .SetMinSize(960, 600)
            .SetResizable(true)
            .Center()
            .SetUserDataFolder(Path.Combine(AppInfo.DataFolder, "webview"))
            .SetBrowserAcceleratorKeysEnabled(Environment.GetEnvironmentVariable("TRETIO_DEV") == "1")
            .SetGeneralAutofillEnabled(false)
            .SetReputationCheckingRequired(false);
        if (File.Exists(icon)) window = window.SetIconFile(icon);
        string browserArgs = "--no-proxy-server";
        if (int.TryParse(Environment.GetEnvironmentVariable("TRETIO_DEVTOOLS_PORT"), out int port)) browserArgs += $" --remote-debugging-port={port}";
        window = window.SetBrowserControlInitParameters(browserArgs);
        window.RegisterWebMessageReceivedHandler((_, e) => _host?.Messages.Dispatch(e.Message));
        window.RegisterCreatedHandler((_, _) => TintTitleBar(window.WindowHandle));
        window.RegisterClosingHandler((_, e) =>
        {
            if (_exiting || _host?.Settings.Current.Ui.CloseToTray != true) return;
            // Close to tray: the window hides, the pipeline keeps running.
            e.Cancel = true;
            Native.ShowWindow(window.WindowHandle, Native.SW_HIDE);
            Log.Information("Window hidden to the tray");
        });
        window.Load(url);
        _window = window;
        _app!.MainWindow = window;
        window.Show();
        if (minimized)
        {
            window.SetMinimized(true);
            return;
        }
        // Windows applies the launcher's show mode to the first show call. A debugger starts the app
        // hidden, so the window stayed invisible under F5; a second, explicit show is honored.
        Native.ShowWindow(window.WindowHandle, Native.SW_SHOW);
        Native.ForceForeground(window.WindowHandle);
    }

    /// <summary>Night title bar on Windows 11 (DESIGN, Layout).</summary>
    private static void TintTitleBar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        int dark = 1;
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
        int caption = 0x001A1512; // COLORREF 0x00BBGGRR for #12151A
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_CAPTION_COLOR, ref caption, 4);
        int text = 0x00ECE8E6;    // #E6E8EC
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_TEXT_COLOR, ref text, 4);
        int border = 0x00241D19;  // #191D24
        Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_BORDER_COLOR, ref border, 4);
    }

    private static void ShowWindow()
    {
        if (_window == null || _app == null) return;
        _app.Dispatcher.BeginInvoke(() =>
        {
            Native.ShowWindow(_window.WindowHandle, Native.SW_SHOW);
            _window.SetMinimized(false);
            _window.BringToFront();
        });
    }

    private static volatile bool _exiting;
    private static Timer? _shutdownWatchdog;

    private static void ExitApp()
    {
        _exiting = true;
        Log.Information("Exit requested");
        if (_app == null) Environment.Exit(0);
        _app.Dispatcher.BeginInvoke(() => _app.Shutdown(0, force: true));
    }

}
