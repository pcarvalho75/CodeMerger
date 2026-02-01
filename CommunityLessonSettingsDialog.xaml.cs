using System.Diagnostics;
using System.Threading;
using System.Windows;
using CodeMerger.Models;
using CodeMerger.Services;

namespace CodeMerger
{
    public partial class CommunityLessonSettingsDialog : Window
    {
        private CancellationTokenSource? _pollCts;
        public CommunityLessonSettingsDialog()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            var settings = CommunityLessonSettings.Load();
            enabledCheckBox.IsChecked = settings.CommunityLessonsEnabled;
            repoUrlTextBox.Text = settings.RepoUrl;
            syncIntervalTextBox.Text = settings.SyncIntervalHours.ToString();

            if (!string.IsNullOrEmpty(settings.GitHubUsername))
                ShowSignedIn(settings.GitHubUsername);
        }

        private void ShowSignedIn(string username)
        {
            signedOutPanel.Visibility = Visibility.Collapsed;
            signingInPanel.Visibility = Visibility.Collapsed;
            signedInPanel.Visibility = Visibility.Visible;
            usernameText.Text = username;
        }

        private void ShowSignedOut()
        {
            signedInPanel.Visibility = Visibility.Collapsed;
            signingInPanel.Visibility = Visibility.Collapsed;
            signedOutPanel.Visibility = Visibility.Visible;
        }

        private async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            var service = new GitHubDeviceFlowService();

            if (GitHubDeviceFlowService.ClientId == "REPLACE_WITH_YOUR_CLIENT_ID")
            {
                MessageBox.Show(
                    "GitHub OAuth App not configured yet.\n\n" +
                    "To enable GitHub sign-in:\n" +
                    "1. Go to https://github.com/settings/developers\n" +
                    "2. Click 'New OAuth App'\n" +
                    "3. Set any name and homepage URL\n" +
                    "4. Set callback URL to http://localhost\n" +
                    "5. Enable Device Flow\n" +
                    "6. Copy the Client ID into GitHubDeviceFlowService.cs",
                    "GitHub OAuth Not Configured",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            signedOutPanel.Visibility = Visibility.Collapsed;
            signingInPanel.Visibility = Visibility.Visible;
            signingInStatus.Text = "Requesting device code...";

            var deviceCode = await service.RequestDeviceCodeAsync();
            if (deviceCode == null)
            {
                signingInStatus.Text = "Failed to get device code.";
                return;
            }

            deviceCodeText.Text = deviceCode.UserCode;
            signingInStatus.Text = "Waiting for authorization...";

            try { Process.Start(new ProcessStartInfo(deviceCode.VerificationUri) { UseShellExecute = true }); }
            catch { }

            _pollCts = new CancellationTokenSource();
            var token = await service.PollForTokenAsync(deviceCode, _pollCts.Token);

            if (string.IsNullOrEmpty(token))
            {
                signingInStatus.Text = "Authorization failed or timed out.";
                await System.Threading.Tasks.Task.Delay(2000);
                ShowSignedOut();
                return;
            }

            signingInStatus.Text = "Getting username...";
            var username = await service.GetUsernameAsync(token) ?? "Unknown";

            var s = CommunityLessonSettings.Load();
            s.GitHubToken = token;
            s.GitHubUsername = username;
            s.Save();

            ShowSignedIn(username);
        }

        private void SignOut_Click(object sender, RoutedEventArgs e)
        {
            var settings = CommunityLessonSettings.Load();
            settings.GitHubToken = string.Empty;
            settings.GitHubUsername = string.Empty;
            settings.Save();
            ShowSignedOut();
        }

        private void CopyCode_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(deviceCodeText.Text))
                Clipboard.SetText(deviceCodeText.Text);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(syncIntervalTextBox.Text, out int interval) || interval < 1)
                interval = 24;

            var settings = CommunityLessonSettings.Load();
            settings.CommunityLessonsEnabled = enabledCheckBox.IsChecked == true;
            settings.RepoUrl = repoUrlTextBox.Text.Trim();
            settings.SyncIntervalHours = interval;
            settings.Save();

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _pollCts?.Cancel();
            DialogResult = false;
            Close();
        }
    }
}
