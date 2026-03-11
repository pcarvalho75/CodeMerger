using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using CodeMerger.Controls;
using Hardcodet.Wpf.TaskbarNotification;

namespace CodeMerger.Services
{
    /// <summary>
    /// Manages the system tray icon appearance based on connection state.
    /// Subscribes to AppState and updates icon color + tooltip automatically.
    /// Handles activity pulse animation and tray click events.
    /// </summary>
    public class TrayIconService : IDisposable
    {
        private readonly TaskbarIcon _trayIcon;
        private readonly AppState _appState;
        private readonly Dispatcher _dispatcher;

        private Icon? _baseIcon;
        private Icon? _grayIcon;
        private Icon? _greenIcon;
        private Icon? _brightGreenIcon;
        private Icon? _amberIcon;

        // Pulse animation
        private DispatcherTimer? _pulseTimer;
        private bool _pulseToggle;
        private DateTime _lastPulseTime;
        private const int PulseIntervalMs = 400;
        private const int PulseTimeoutMs = 2000;

        private bool _disposed;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public event Action? ShowRequested;
        public event Action? ExitRequested;

        public TrayIconService(TaskbarIcon trayIcon, AppState appState, Dispatcher dispatcher)
        {
            _trayIcon = trayIcon;
            _appState = appState;
            _dispatcher = dispatcher;

            LoadBaseIcon();
            GenerateStateIcons();

            // Set initial state
            ApplyIcon(_grayIcon);
            _trayIcon.ToolTipText = "CodeMerger — Idle";

            // Subscribe to state changes
            _appState.TrayStateChanged += OnTrayStateChanged;

            // Wire tray click events
            _trayIcon.TrayMouseDoubleClick += (s, e) => ShowRequested?.Invoke();
        }

        /// <summary>
        /// Wire context menu items after the tray icon is loaded.
        /// Call from MainWindow_Loaded since ContextMenu isn't available at construction time.
        /// </summary>
        public void WireContextMenu()
        {
            if (_trayIcon.ContextMenu == null) return;

            foreach (var item in _trayIcon.ContextMenu.Items)
            {
                if (item is System.Windows.Controls.MenuItem menuItem)
                {
                    switch (menuItem.Header?.ToString())
                    {
                        case "Open CodeMerger":
                            menuItem.Click += (s, e) => ShowRequested?.Invoke();
                            break;
                        case "Exit":
                            menuItem.Click += (s, e) => ExitRequested?.Invoke();
                            break;
                    }
                }
            }
        }

        private void LoadBaseIcon()
        {
            try
            {
                _baseIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            }
            catch
            {
                // Fallback: no base icon available
            }
        }

        private void GenerateStateIcons()
        {
            if (_baseIcon == null) return;

            _grayIcon = CreateOverlayIcon(_baseIcon, Color.FromArgb(128, 128, 128));
            _greenIcon = CreateOverlayIcon(_baseIcon, Color.FromArgb(76, 175, 80));
            _brightGreenIcon = CreateOverlayIcon(_baseIcon, Color.FromArgb(144, 238, 144));
            _amberIcon = CreateOverlayIcon(_baseIcon, Color.FromArgb(255, 193, 7));
        }

        private static Icon CreateOverlayIcon(Icon baseIcon, Color dotColor)
        {
            var size = baseIcon.Size;
            using var bitmap = new Bitmap(size.Width, size.Height);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.DrawIcon(baseIcon, 0, 0);

                // Draw a colored dot in the bottom-right corner
                int dotSize = Math.Max(6, size.Width / 4);
                int x = size.Width - dotSize - 1;
                int y = size.Height - dotSize - 1;

                // White outline for contrast
                using (var outlineBrush = new SolidBrush(Color.White))
                    g.FillEllipse(outlineBrush, x - 1, y - 1, dotSize + 2, dotSize + 2);

                // Colored dot
                using (var dotBrush = new SolidBrush(dotColor))
                    g.FillEllipse(dotBrush, x, y, dotSize, dotSize);
            }

            // GetHicon creates an unmanaged GDI handle that Icon.FromHandle does NOT own.
            // We must clone the icon (which copies the handle) then destroy the original.
            IntPtr hIcon = bitmap.GetHicon();
            try
            {
                using var temp = Icon.FromHandle(hIcon);
                return (Icon)temp.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }

        private void OnTrayStateChanged()
        {
            _dispatcher.Invoke(() =>
            {
                // Stop pulse when state changes (re-triggered by PulseActivity if still active)
                StopPulse();

                switch (_appState.ClaudeState)
                {
                    case ClaudeState.Connected:
                        ApplyIcon(_greenIcon);
                        _trayIcon.ToolTipText = $"CodeMerger — Connected: {_appState.ClaudeWorkspace}";
                        break;
                    case ClaudeState.Connecting:
                        ApplyIcon(_grayIcon);
                        _trayIcon.ToolTipText = "CodeMerger — Connecting...";
                        break;
                    case ClaudeState.Restarting:
                        ApplyIcon(_grayIcon);
                        _trayIcon.ToolTipText = "CodeMerger — Restarting...";
                        break;
                    case ClaudeState.Error:
                        ApplyIcon(_amberIcon);
                        _trayIcon.ToolTipText = $"CodeMerger — Error: {_appState.ErrorMessage}";
                        break;
                    case ClaudeState.Disconnected:
                    default:
                        ApplyIcon(_grayIcon);
                        _trayIcon.ToolTipText = "CodeMerger — Idle";
                        break;
                }
            });
        }

        /// <summary>
        /// Trigger a visual pulse on the tray icon to indicate activity.
        /// The icon toggles between green and bright-green for 2 seconds after the last call.
        /// Only pulses when connected.
        /// </summary>
        public void PulseActivity()
        {
            if (_appState.ClaudeState != ClaudeState.Connected) return;

            _dispatcher.Invoke(() =>
            {
                _lastPulseTime = DateTime.UtcNow;

                if (_pulseTimer != null) return; // Already pulsing, just extend timeout

                _pulseToggle = false;
                _pulseTimer = new DispatcherTimer(DispatcherPriority.Render, _dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(PulseIntervalMs)
                };
                _pulseTimer.Tick += PulseTimer_Tick;
                _pulseTimer.Start();
            });
        }

        private void PulseTimer_Tick(object? sender, EventArgs e)
        {
            // Auto-stop after timeout with no new activity
            if ((DateTime.UtcNow - _lastPulseTime).TotalMilliseconds > PulseTimeoutMs)
            {
                StopPulse();
                // Restore steady green
                ApplyIcon(_greenIcon);
                return;
            }

            _pulseToggle = !_pulseToggle;
            ApplyIcon(_pulseToggle ? _brightGreenIcon : _greenIcon);
        }

        private void StopPulse()
        {
            if (_pulseTimer == null) return;
            _pulseTimer.Stop();
            _pulseTimer.Tick -= PulseTimer_Tick;
            _pulseTimer = null;
        }

        private void ApplyIcon(Icon? icon)
        {
            if (icon != null)
                _trayIcon.Icon = icon;
        }

        /// <summary>
        /// Re-apply the tray icon for the current state.
        /// Called when explorer.exe restarts and destroys all tray icons.
        /// </summary>
        public void RefreshIcon()
        {
            OnTrayStateChanged();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _appState.TrayStateChanged -= OnTrayStateChanged;
            StopPulse();
            _trayIcon.Dispose();

            _grayIcon?.Dispose();
            _greenIcon?.Dispose();
            _brightGreenIcon?.Dispose();
            _amberIcon?.Dispose();
            _baseIcon?.Dispose();
        }
    }
}
