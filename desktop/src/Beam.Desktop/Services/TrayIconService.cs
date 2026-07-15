using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace Beam.Desktop.Services;

/// <summary>Ícone na bandeja do sistema — indicador de execução e acesso rápido.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Window _window;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private TrayApplicationContext? _context;
    private bool _disposed;

    public TrayIconService(Window window)
    {
        _window = window;
        StartupLog.Write("Creating tray thread.");

        _thread = new Thread(RunTrayMessageLoop)
        {
            IsBackground = true,
            Name = "Beam Tray Icon",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        StartupLog.Write("Tray thread initialized.");
    }

    public event EventHandler? ExitRequested;

    public void SetPairingCode(string pairingCode)
    {
        InvokeOnTray(() =>
        {
            if (_context is not null)
            {
                _context.PairingCode = pairingCode;
            }
        });
    }

    public void Show()
    {
        InvokeOnTray(() => _context?.ShowIcon());
    }

    private void RunTrayMessageLoop()
    {
        try
        {
            _context = new TrayApplicationContext(_window, OnExitRequested);
            StartupLog.Write("NotifyIcon created.");
            _ready.Set();
            System.Windows.Forms.Application.Run(_context);
        }
        catch (Exception ex)
        {
            StartupLog.Write($"Tray thread failed: {ex}");
            _ready.Set();
        }
    }

    private void OnExitRequested()
    {
        _window.Dispatcher.BeginInvoke(() => ExitRequested?.Invoke(this, EventArgs.Empty));
    }

    private void InvokeOnTray(Action action)
    {
        if (_disposed || !_thread.IsAlive)
        {
            return;
        }

        _context?.Invoke(action);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _context?.StopTrayThread();
        _ready.Dispose();
    }

    private sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly NotifyIcon _notifyIcon;
        private readonly Window _window;
        private readonly Action _exitRequested;

        public TrayApplicationContext(Window window, Action exitRequested)
        {
            _window = window;
            _exitRequested = exitRequested;

            Icon icon;
            try
            {
                icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
                    ?? SystemIcons.Application;
            }
            catch (Exception)
            {
                icon = SystemIcons.Application;
            }

            _notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "Beam",
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Abrir Beam", null, (_, _) => ShowWindow());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, (_, _) => ExitApplication());
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (_, _) => ShowWindow();
            _notifyIcon.Visible = true;
        }

        public string PairingCode
        {
            set => _notifyIcon.Text = $"Beam - Código: {value}";
        }

        public void ShowIcon()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Visible = true;
        }

        public void Invoke(Action action)
        {
            action();
        }

        public void StopTrayThread()
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            ExitThreadCore();
        }

        private void ShowWindow()
        {
            _window.Dispatcher.BeginInvoke(() =>
            {
                _window.Show();
                _window.WindowState = WindowState.Normal;
                _window.Activate();
            });
        }

        private void ExitApplication()
        {
            _notifyIcon.Visible = false;
            _exitRequested();
        }
    }
}
