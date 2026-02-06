using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeMerger.Models;

namespace CodeMerger.Controls
{
    public partial class ActivityStrip : UserControl
    {
        private static readonly Brush YellowBrush = new SolidColorBrush(Color.FromRgb(255, 193, 7));
        private static readonly Brush GreenBrush = new SolidColorBrush(Color.FromRgb(0, 217, 165));
        private static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(233, 69, 96));
        private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(255, 152, 0));
        private static readonly Brush GrayBrush = new SolidColorBrush(Color.FromRgb(136, 146, 160));

        private System.Windows.Threading.DispatcherTimer? _claudeResponseTimer;
        private DateTime _lastCompletedTime;
        private bool _timeoutRecorded;
        private int _timeoutThresholdSeconds = 45;
        private AppState? _appState;

        /// <summary>Raised when the reset button is clicked.</summary>
        public event EventHandler? ResetStatsClicked;

        /// <summary>Raised when Claude response timeout is first detected.</summary>
        public event EventHandler<int>? TimeoutDetected;

        public ActivityStrip()
        {
            InitializeComponent();

            _claudeResponseTimer = new System.Windows.Threading.DispatcherTimer();
            _claudeResponseTimer.Interval = TimeSpan.FromSeconds(1);
            _claudeResponseTimer.Tick += ClaudeResponseTimer_Tick;
        }

        /// <summary>Subscribe to AppState for disconnect detection.</summary>
        public void Initialize(AppState appState)
        {
            _appState = appState;
            _appState.ConnectionStateChanged += () => Dispatcher.Invoke(OnConnectionStateChanged);
        }

        private void OnConnectionStateChanged()
        {
            if (_appState?.ClaudeState == ClaudeState.Disconnected)
            {
                StopTimer();
                SetIdle();
            }
        }

        // --- Public API ---

        /// <summary>Update the timeout threshold (from settings).</summary>
        public int TimeoutThresholdSeconds
        {
            get => _timeoutThresholdSeconds;
            set => _timeoutThresholdSeconds = value;
        }

        /// <summary>Show processing state (yellow).</summary>
        public void SetProcessing(string toolName)
        {
            activityIndicator.Fill = YellowBrush;
            activityText.Text = $"🔧 Processing {toolName}...";
            activityText.Foreground = YellowBrush;
            activityBorder.BorderBrush = YellowBrush;
            activityBorder.BorderThickness = new Thickness(1);

            // Stop timeout timer - server is working
            _claudeResponseTimer?.Stop();
            _timeoutRecorded = false;
        }

        /// <summary>Show completed state (green). Starts the Claude response timer.</summary>
        public void SetCompleted(string toolName, string details)
        {
            activityIndicator.Fill = GreenBrush;
            activityText.Text = $"✅ {toolName} done ({details}) — waiting for Claude...";
            activityText.Foreground = GreenBrush;
            activityBorder.BorderBrush = GreenBrush;
            activityBorder.BorderThickness = new Thickness(1);

            // Start waiting for Claude's next request
            _lastCompletedTime = DateTime.Now;
            _timeoutRecorded = false;
            _claudeResponseTimer?.Start();
        }

        /// <summary>Show error state (red). Clears after 10 seconds.</summary>
        public void SetError(string toolName, string details)
        {
            activityIndicator.Fill = RedBrush;
            activityText.Text = $"❌ {toolName} failed: {details}";
            activityText.Foreground = RedBrush;
            activityBorder.BorderBrush = RedBrush;
            activityBorder.BorderThickness = new Thickness(1);

            // Stop timeout timer
            _claudeResponseTimer?.Stop();
            _timeoutRecorded = false;

            // Auto-clear error after 10 seconds
            var clearTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
            clearTimer.Tick += (s, e) =>
            {
                clearTimer.Stop();
                if (activityText.Text.StartsWith("❌"))
                {
                    SetIdle();
                }
            };
            clearTimer.Start();
        }

        /// <summary>Show idle state (gray).</summary>
        public void SetIdle()
        {
            activityIndicator.Fill = GrayBrush;
            activityText.Text = "Idle";
            activityText.Foreground = GrayBrush;
            activityBorder.BorderBrush = Brushes.Transparent;
            activityBorder.BorderThickness = new Thickness(0);
        }

        /// <summary>Update the stats text from session statistics.</summary>
        public void UpdateStatsDisplay(McpSessionStats stats)
        {
            var summary = stats.GetSummary();
            var duration = stats.GetSessionDuration();

            if (stats.TotalToolCalls > 0 || stats.TotalErrors > 0 || stats.TotalTimeouts > 0)
            {
                statsText.Text = $"{summary} | Session: {duration}";
            }
            else
            {
                statsText.Text = $"No activity this session | Session: {duration}";
            }

            // Build detailed tooltip
            var tooltipText = new System.Text.StringBuilder();
            tooltipText.AppendLine($"Session Duration: {duration}");
            tooltipText.AppendLine($"Total Calls: {stats.TotalToolCalls}");

            if (stats.TotalToolCalls > 0)
            {
                tooltipText.AppendLine($"Average Response: {stats.AverageResponseTime:F1}ms");
            }

            var slowest = stats.GetSlowestTool();
            if (slowest.HasValue)
            {
                tooltipText.AppendLine($"Slowest Tool: {slowest.Value.ToolName} ({slowest.Value.DurationMs}ms)");
            }

            if (stats.TotalTimeouts > 0)
            {
                tooltipText.AppendLine($"Timeouts: {stats.TotalTimeouts}");
            }

            if (stats.TotalErrors > 0)
            {
                tooltipText.AppendLine($"Errors: {stats.TotalErrors}");
            }

            tooltipText.AppendLine();
            tooltipText.AppendLine("Click 🔄 to reset statistics");

            statsText.ToolTip = tooltipText.ToString().TrimEnd();

            // Color-code based on health
            if (stats.TotalErrors > 0)
            {
                statsText.Foreground = RedBrush;
            }
            else if (stats.TotalTimeouts > 0)
            {
                statsText.Foreground = YellowBrush;
            }
            else if (stats.TotalToolCalls > 0)
            {
                statsText.Foreground = GreenBrush;
            }
            else
            {
                statsText.Foreground = GrayBrush;
            }
        }

        /// <summary>Stop the timeout timer (e.g., on disconnect).</summary>
        public void StopTimer()
        {
            _claudeResponseTimer?.Stop();
        }

        // --- Internal ---

        private void ClaudeResponseTimer_Tick(object? sender, EventArgs e)
        {
            var elapsed = (DateTime.Now - _lastCompletedTime).TotalSeconds;

            if (elapsed >= _timeoutThresholdSeconds)
            {
                activityIndicator.Fill = OrangeBrush;
                activityText.Text = $"⏳ Claude hasn't responded in {(int)elapsed}s (timeout threshold: {_timeoutThresholdSeconds}s)";
                activityText.Foreground = OrangeBrush;
                activityBorder.BorderBrush = OrangeBrush;
                activityBorder.BorderThickness = new Thickness(2);

                if (!_timeoutRecorded)
                {
                    _timeoutRecorded = true;
                    TimeoutDetected?.Invoke(this, (int)elapsed);
                }
            }
            else
            {
                activityText.Text = $"✅ Tool completed — waiting for Claude... ({(int)elapsed}s)";
            }
        }

        private void ResetStats_Click(object sender, RoutedEventArgs e)
        {
            ResetStatsClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
