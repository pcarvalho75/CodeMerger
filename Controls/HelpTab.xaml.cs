using System.Windows.Controls;
using CodeMerger.Models;

namespace CodeMerger.Controls;

public partial class HelpTab : UserControl
{
    private List<TocEntry> _entries = new();

    public HelpTab()
    {
        InitializeComponent();
        LoadTableOfContents();
    }

    private void LoadTableOfContents()
    {
        _entries = HelpManual.GetTableOfContents()
            .Select(t => new TocEntry { Key = t.Key, Title = t.Title })
            .ToList();

        tocListBox.ItemsSource = _entries;

        if (_entries.Count > 0)
            tocListBox.SelectedIndex = 0;
    }

    private void TocListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (tocListBox.SelectedItem is TocEntry entry &&
            HelpManual.Sections.TryGetValue(entry.Key, out var section))
        {
            sectionTitle.Text = section.Title;
            sectionContent.Text = section.Content;
        }
    }

    private class TocEntry
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public override string ToString() => Title;
    }
}
