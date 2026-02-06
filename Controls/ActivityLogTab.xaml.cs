using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using CodeMerger.Models;
using CodeMerger.Services;
using Microsoft.Win32;

namespace CodeMerger.Controls
{
    public partial class ActivityLogTab : UserControl
    {
        private const int MaxLogEntries = 1000;
        private readonly ObservableCollection<ActivityLogEntry> _activityLog = new();
        private McpSessionStats? _sessionStats;

        /// <summary>Raised on status messages (e.g. export success/failure).</summary>
        public event EventHandler<string>? StatusUpdate;

        public ActivityLogTab()
        {
            InitializeComponent();
            activityLogListBox.ItemsSource = _activityLog;
        }

        /// <summary>
        /// Inject the shared session stats instance.
        /// </summary>
        public void Initialize(McpSessionStats sessionStats)
        {
            _sessionStats = sessionStats;
        }

        /// <summary>
        /// Add an entry to the activity log. Called from MainWindow on MCP events.
        /// </summary>
        public void AddEntry(ActivityLogType type, string toolName, long durationMs, string message)
        {
            var entry = new ActivityLogEntry
            {
                Timestamp = DateTime.Now,
                Type = type,
                ToolName = toolName,
                DurationMs = durationMs,
                Message = message
            };

            _activityLog.Add(entry);

            // Trim oldest entries if over limit
            while (_activityLog.Count > MaxLogEntries)
                _activityLog.RemoveAt(0);

            // Auto-scroll to bottom
            if (activityLogListBox.Items.Count > 0)
                activityLogListBox.ScrollIntoView(activityLogListBox.Items[activityLogListBox.Items.Count - 1]);
        }

        /// <summary>
        /// Refresh the summary cards, chart, and tool breakdown display.
        /// </summary>
        public void UpdateDisplay()
        {
            if (_sessionStats == null) return;

            // Update summary cards
            logTotalCallsText.Text = _sessionStats.TotalToolCalls.ToString();
            logErrorsText.Text = _sessionStats.TotalErrors.ToString();
            logTimeoutsText.Text = _sessionStats.TotalTimeouts.ToString();

            if (_sessionStats.AverageResponseTime > 0)
                logAvgResponseText.Text = $"{_sessionStats.AverageResponseTime:F0}ms";

            var duration = DateTime.Now - _sessionStats.SessionStartTime;
            logUptimeText.Text = duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}h{duration.Minutes}m"
                : duration.TotalMinutes >= 1
                    ? $"{(int)duration.TotalMinutes}m{duration.Seconds}s"
                    : $"{duration.Seconds}s";

            // Update chart
            RenderResponseTimeChart();

            // Update tool breakdown
            UpdateToolBreakdown();
        }

        private void RenderResponseTimeChart()
        {
            if (_sessionStats == null) return;

            responseTimeChart.Children.Clear();
            var history = _sessionStats.GetCallHistory();
            if (history.Count < 2) return;

            double w = responseTimeChart.ActualWidth;
            double h = responseTimeChart.ActualHeight;
            if (w < 20 || h < 20) return;

            // Show last 60 data points
            var recent = history.Skip(Math.Max(0, history.Count - 60)).ToList();
            if (recent.Count < 2) return;

            double maxMs = recent.Max(r => (double)r.DurationMs);
            if (maxMs < 1) maxMs = 1;
            double padding = 4;

            // Draw grid lines
            for (int i = 0; i <= 4; i++)
            {
                double y = padding + (h - 2 * padding) * i / 4;
                var line = new Line
                {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromRgb(30, 50, 70)),
                    StrokeThickness = 0.5
                };
                responseTimeChart.Children.Add(line);
            }

            // Draw bars for each call
            double barWidth = Math.Max(2, (w - 2 * padding) / recent.Count - 1);
            for (int i = 0; i < recent.Count; i++)
            {
                double x = padding + i * (w - 2 * padding) / recent.Count;
                double barH = (recent[i].DurationMs / maxMs) * (h - 2 * padding);
                double y2 = h - padding - barH;

                var isError = recent[i].DurationMs == 0;
                var color = isError
                    ? Color.FromRgb(233, 69, 96)  // red for errors
                    : recent[i].DurationMs > _sessionStats.AverageResponseTime * 2
                        ? Color.FromRgb(255, 193, 7)  // yellow for slow
                        : Color.FromRgb(0, 217, 165);  // green for normal

                var rect = new Rectangle
                {
                    Width = barWidth,
                    Height = Math.Max(2, barH),
                    Fill = new SolidColorBrush(color),
                    Opacity = 0.8,
                    RadiusX = 1, RadiusY = 1
                };
                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y2);
                responseTimeChart.Children.Add(rect);
            }

            // Y-axis label (max)
            var maxLabel = new TextBlock
            {
                Text = $"{maxMs:F0}ms",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 146, 160))
            };
            Canvas.SetRight(maxLabel, 4);
            Canvas.SetTop(maxLabel, 0);
            responseTimeChart.Children.Add(maxLabel);
        }

        private void UpdateToolBreakdown()
        {
            if (_sessionStats == null) return;

            var breakdown = _sessionStats.GetToolBreakdown();
            if (breakdown.Count == 0) return;

            int maxCount = breakdown.First().Count;
            double maxBarWidth = 150;

            var items = breakdown.Take(8).Select(b => new ToolBreakdownItem
            {
                Name = b.ToolName.Replace("codemerger_", ""),
                CountText = $"{b.Count}× ({b.AvgMs:F0}ms)",
                BarWidth = maxCount > 0 ? (b.Count * maxBarWidth / maxCount) : 0,
                BarBrush = new SolidColorBrush(Color.FromRgb(0, 217, 165))
            }).ToList();

            toolBreakdownList.ItemsSource = items;
        }

        private void ClearActivityLog_Click(object sender, RoutedEventArgs e)
        {
            _activityLog.Clear();
            _sessionStats?.Reset();
            UpdateDisplay();
        }

        private void ExportActivityLog_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "CSV Files|*.csv|Text Files|*.txt",
                FileName = $"activity_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var lines = new List<string> { "Timestamp,Type,Tool,DurationMs,Message" };
                    lines.AddRange(_activityLog.Select(e2 =>
                        $"{e2.Timestamp:yyyy-MM-dd HH:mm:ss},{e2.Type},{e2.ToolName},{e2.DurationMs},\"{e2.Message.Replace("\"", "\"\"")}\""
                    ));
                    File.WriteAllLines(dialog.FileName, lines);
                    StatusUpdate?.Invoke(this, $"Activity log exported to {dialog.FileName}");
                }
                catch (Exception ex)
                {
                    StatusUpdate?.Invoke(this, $"Export failed: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Helper class for tool breakdown display binding.
    /// </summary>
    public class ToolBreakdownItem
    {
        public string Name { get; set; } = string.Empty;
        public string CountText { get; set; } = string.Empty;
        public double BarWidth { get; set; }
        public Brush BarBrush { get; set; } = Brushes.Green;
    }
}
