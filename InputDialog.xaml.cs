using System.Windows;

namespace CodeMerger
{
    public partial class InputDialog : Window
    {
        public string ResponseText => responseTextBox.Text;

        public InputDialog(string title, string prompt, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            promptText.Text = prompt;
            responseTextBox.Text = defaultValue;
            responseTextBox.SelectAll();
            responseTextBox.Focus();
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
