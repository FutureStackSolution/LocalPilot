using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LocalPilot.Chat.ViewModels
{
    public sealed class AgentTurnViewModel : INotifyPropertyChanged
    {
        private string _statusText = "Working...";
        private string _detailText = string.Empty;

        public string StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public string DetailText
        {
            get => _detailText;
            set => SetProperty(ref _detailText, value);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value)) return;
            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
