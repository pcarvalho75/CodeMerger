using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodeMerger.Controls
{
    public partial class StatusBar : UserControl
    {
        public StatusBar()
        {
            InitializeComponent();
        }

        /// <summary>Update the status bar message and color.</summary>
        public void UpdateStatus(string message, Brush color)
        {
            statusText.Text = message;
            statusText.Foreground = color;
        }

        /// <summary>Get the current status text (for tray tooltip etc.).</summary>
        public string StatusMessage => statusText.Text;

        /// <summary>Set progress bar visibility and value.</summary>
        public void SetProgress(double value, bool visible)
        {
            progressBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            progressBar.Value = value;
        }

        /// <summary>Set the file status label text.</summary>
        public void SetFileStatus(string text)
        {
            fileStatusLabel.Text = text;
        }

        /// <summary>Set UI state for scanning operations.</summary>
        public void SetScanningState(bool isEnabled)
        {
            progressBar.Visibility = isEnabled ? Visibility.Collapsed : Visibility.Visible;
            if (isEnabled)
            {
                fileStatusLabel.Text = "";
                progressBar.Value = 0;
            }
        }
    }
}
