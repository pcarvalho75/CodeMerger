using System.ComponentModel;

namespace CodeMerger.Models
{
    public class SelectableItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        private string _path = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public string Path
        {
            get => _path;
            set
            {
                if (_path != value)
                {
                    _path = value;
                    OnPropertyChanged(nameof(Path));
                }
            }
        }

        public SelectableItem() { }

        public SelectableItem(string path, bool isSelected = true)
        {
            Path = path;
            IsSelected = isSelected;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
