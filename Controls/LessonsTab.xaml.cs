using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeMerger.Models;
using CodeMerger.Services;

namespace CodeMerger.Controls
{
    public partial class LessonsTab : UserControl
    {
        private bool _isLessonSelectionChanging;
        private Func<Window>? _getOwnerWindow;

        /// <summary>Raised on status messages.</summary>
        public event EventHandler<string>? StatusUpdate;

        public LessonsTab()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Inject the owner window provider (needed for CommunityLessonSettingsDialog).
        /// </summary>
        public void Initialize(Func<Window> getOwnerWindow)
        {
            _getOwnerWindow = getOwnerWindow;
        }

        /// <summary>
        /// Refresh the lessons lists from disk. Called from MainWindow startup sync and internally.
        /// </summary>
        public void RefreshLessons()
        {
            try
            {
                var service = new LessonService();
                var local = service.GetLocalLessons();
                var community = service.GetCommunityLessons();

                localLessonsListBox.ItemsSource = local;
                communityLessonsListBox.ItemsSource = community;
                localLessonCountText.Text = $"({local.Count}/100)";
                communityLessonCountText.Text = $"({community.Count})";

                // Reset button states
                deleteLessonButton.IsEnabled = false;
                submitLessonButton.IsEnabled = false;
                lessonDetailPanel.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                StatusUpdate?.Invoke(this, $"Failed to load lessons: {ex.Message}");
            }
        }

        private void ShowLessonDetail(Lesson lesson)
        {
            detailTypeText.Text = lesson.Type;
            detailComponentText.Text = lesson.Component;
            detailContributorText.Text = lesson.ContributedBy ?? "";

            var content = $"Observation:\n{lesson.Observation}\n\nProposal:\n{lesson.Proposal}";
            if (!string.IsNullOrEmpty(lesson.SuggestedCode))
                content += $"\n\nSuggested Code:\n{lesson.SuggestedCode}";

            detailContentText.Text = content;
            lessonDetailPanel.Visibility = Visibility.Visible;
        }

        private void LocalLessonListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLessonSelectionChanging) return;
            _isLessonSelectionChanging = true;

            communityLessonsListBox.SelectedItem = null;
            if (localLessonsListBox.SelectedItem is Lesson lesson)
            {
                deleteLessonButton.IsEnabled = true;
                submitLessonButton.IsEnabled = true;
                ShowLessonDetail(lesson);
            }
            else
            {
                deleteLessonButton.IsEnabled = false;
                submitLessonButton.IsEnabled = false;
                lessonDetailPanel.Visibility = Visibility.Collapsed;
            }

            _isLessonSelectionChanging = false;
        }

        private void CommunityLessonListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLessonSelectionChanging) return;
            _isLessonSelectionChanging = true;

            localLessonsListBox.SelectedItem = null;
            deleteLessonButton.IsEnabled = false;
            submitLessonButton.IsEnabled = false;
            if (communityLessonsListBox.SelectedItem is Lesson lesson)
            {
                ShowLessonDetail(lesson);
            }
            else
            {
                lessonDetailPanel.Visibility = Visibility.Collapsed;
            }

            _isLessonSelectionChanging = false;
        }

        private async void SyncLessonsNow_Click(object sender, RoutedEventArgs e)
        {
            syncLessonsButton.IsEnabled = false;
            lessonSyncStatusText.Text = "⏳ Syncing...";
            lessonSyncStatusText.Foreground = Brushes.LightBlue;

            try
            {
                var lessonService = new LessonService();
                var syncService = new CommunityLessonSyncService(lessonService);
                var (synced, count, message) = await syncService.ForceSyncAsync();

                lessonSyncStatusText.Text = synced ? $"✅ Synced {count} lessons" : $"ℹ️ {message}";
                lessonSyncStatusText.Foreground = synced ? Brushes.LightGreen : Brushes.Orange;
                RefreshLessons();
            }
            catch (Exception ex)
            {
                lessonSyncStatusText.Text = $"❌ {ex.Message}";
                lessonSyncStatusText.Foreground = Brushes.OrangeRed;
            }
            finally
            {
                syncLessonsButton.IsEnabled = true;
            }
        }

        private void DeleteLesson_Click(object sender, RoutedEventArgs e)
        {
            if (localLessonsListBox.SelectedItem is not Lesson lesson) return;

            var result = MessageBox.Show($"Delete lesson '{lesson.Component}'?\n\n{lesson.Observation}",
                "Delete Lesson", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            var service = new LessonService();
            // Find the lesson's index among all lessons (local come first)
            var all = service.GetLessons();
            var match = all.FindIndex(l => l.Timestamp == lesson.Timestamp && l.Observation == lesson.Observation);
            if (match >= 0)
            {
                var (success, message) = service.DeleteLesson(match + 1);
                lessonSyncStatusText.Text = success ? $"✅ {message}" : $"❌ {message}";
                lessonSyncStatusText.Foreground = success ? Brushes.LightGreen : Brushes.OrangeRed;
            }

            lessonDetailPanel.Visibility = Visibility.Collapsed;
            RefreshLessons();
        }

        private async void SubmitLesson_Click(object sender, RoutedEventArgs e)
        {
            if (localLessonsListBox.SelectedItem is not Lesson lesson) return;

            var settings = CommunityLessonSettings.Load();
            if (string.IsNullOrEmpty(settings.GitHubToken))
            {
                lessonSyncStatusText.Text = "❌ GitHub sign-in required — open Settings";
                lessonSyncStatusText.Foreground = Brushes.OrangeRed;
                return;
            }

            var confirm = MessageBox.Show($"Submit lesson '{lesson.Component}' to GitHub as an issue?\n\n{lesson.Observation}",
                "Submit Lesson", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            submitLessonButton.IsEnabled = false;
            lessonSyncStatusText.Text = "⏳ Submitting to GitHub...";
            lessonSyncStatusText.Foreground = Brushes.LightBlue;

            try
            {
                var repoOwner = "pcarvalho75";
                var repoName = "CodeMerger";

                if (!string.IsNullOrEmpty(settings.RepoUrl))
                {
                    var uri = settings.RepoUrl.TrimEnd('/');
                    var parts = uri.Split('/');
                    if (parts.Length >= 2)
                    {
                        repoOwner = parts[parts.Length - 2];
                        repoName = parts[parts.Length - 1];
                    }
                }

                var contributor = !string.IsNullOrEmpty(settings.GitHubUsername)
                    ? $"@{settings.GitHubUsername}" : "Anonymous";

                var title = $"[Lesson] {lesson.Type}: {lesson.Component}";
                var body = $"## Observation\n{lesson.Observation}\n\n" +
                           $"## Proposal\n{lesson.Proposal}\n\n" +
                           $"**Type:** {lesson.Type}\n" +
                           $"**Component:** {lesson.Component}\n" +
                           $"**Contributed by:** {contributor}\n" +
                           $"**Logged:** {lesson.Timestamp:yyyy-MM-dd HH:mm}\n";

                if (!string.IsNullOrEmpty(lesson.SuggestedCode))
                    body += $"\n## Suggested Code\n```csharp\n{lesson.SuggestedCode}\n```\n";

                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Add("Authorization", $"token {settings.GitHubToken}");
                client.DefaultRequestHeaders.Add("User-Agent", "CodeMerger");
                client.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");

                var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body, labels = new[] { "lesson", lesson.Type } });
                var content = new System.Net.Http.StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"https://api.github.com/repos/{repoOwner}/{repoName}/issues", content);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    using var doc = System.Text.Json.JsonDocument.Parse(responseJson);
                    var issueUrl = doc.RootElement.GetProperty("html_url").GetString();
                    lessonSyncStatusText.Text = $"✅ Submitted — {issueUrl}";
                    lessonSyncStatusText.Foreground = Brushes.LightGreen;
                }
                else
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    lessonSyncStatusText.Text = $"❌ HTTP {(int)response.StatusCode}";
                    lessonSyncStatusText.Foreground = Brushes.OrangeRed;
                }
            }
            catch (Exception ex)
            {
                lessonSyncStatusText.Text = $"❌ {ex.Message}";
                lessonSyncStatusText.Foreground = Brushes.OrangeRed;
            }
            finally
            {
                submitLessonButton.IsEnabled = true;
            }
        }

        private void ClearLocalLessons_Click(object sender, RoutedEventArgs e)
        {
            var service = new LessonService();
            var count = service.GetLessonCount();
            if (count == 0)
            {
                lessonSyncStatusText.Text = "ℹ️ No local lessons to clear";
                lessonSyncStatusText.Foreground = Brushes.Orange;
                return;
            }

            var result = MessageBox.Show($"Delete all {count} local lessons? This cannot be undone.",
                "Clear Local Lessons", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            service.ClearAllLessons();
            lessonSyncStatusText.Text = $"✅ Cleared {count} local lessons";
            lessonSyncStatusText.Foreground = Brushes.LightGreen;
            lessonDetailPanel.Visibility = Visibility.Collapsed;
            RefreshLessons();
        }

        private void CommunityLessonsSettings_Click(object sender, RoutedEventArgs e)
        {
            var owner = _getOwnerWindow?.Invoke();
            var dialog = new CommunityLessonSettingsDialog { Owner = owner };
            dialog.ShowDialog();
            RefreshLessons();
        }
    }
}
