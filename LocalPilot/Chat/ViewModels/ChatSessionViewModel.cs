using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LocalPilot.Chat.ViewModels
{
    public sealed class ChatSessionViewModel : INotifyPropertyChanged
    {
        private bool _isStreaming;
        private bool _isInputEnabled = true;
        private double _inputOpacity = 1.0;
        private string _currentAction = string.Empty;

        public ChatSessionViewModel()
        {
            AgentTurn = new AgentTurnViewModel();
        }

        public AgentTurnViewModel AgentTurn { get; }

        public bool IsStreaming
        {
            get => _isStreaming;
            set => SetProperty(ref _isStreaming, value);
        }

        public bool IsInputEnabled
        {
            get => _isInputEnabled;
            set => SetProperty(ref _isInputEnabled, value);
        }

        public double InputOpacity
        {
            get => _inputOpacity;
            set => SetProperty(ref _inputOpacity, value);
        }

        public string CurrentAction
        {
            get => _currentAction;
            set => SetProperty(ref _currentAction, value);
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
