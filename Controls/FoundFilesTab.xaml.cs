using System.Collections;
using System.Windows.Controls;

namespace CodeMerger.Controls
{
    public partial class FoundFilesTab : UserControl
    {
        public FoundFilesTab()
        {
            InitializeComponent();
        }

        /// <summary>Bind the file list source.</summary>
        public void SetSource(IEnumerable source)
        {
            fileListBox.ItemsSource = source;
        }

        /// <summary>Update the tab header with file count. Null = no count.</summary>
        public void UpdateHeader(int? count)
        {
            // TabItem.Header is set by the parent TabControl via the TabItem wrapper.
            // We need to walk up to find our TabItem parent.
            if (Parent is TabItem tabItem)
            {
                tabItem.Header = count.HasValue
                    ? $"📄 Found Files ({count.Value})"
                    : "📄 Found Files";
            }
        }
    }
}
