using System;
using System.ComponentModel;

namespace ApibotWarZ.UI.Models
{
    public class RegisteredAccount : INotifyPropertyChanged
    {
        private int _index = 1;
        private bool _isSelected = true;
        private string _username = string.Empty;
        private string _password = string.Empty;
        private string _status = "⏳ รอคิว";
        private string _registeredAt = "-";
        private string _resultDetail = "อยู่ในคิวรอทำงาน";

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }
        }

        public bool IsCompleted => Status != null && (Status.Contains("สำเร็จ") || Status.Contains("เคยรับ"));

        public int Index
        {
            get => _index;
            set { if (_index != value) { _index = value; OnPropertyChanged(nameof(Index)); } }
        }

        public string Username
        {
            get => _username;
            set { if (_username != value) { _username = value; OnPropertyChanged(nameof(Username)); } }
        }

        public string Password
        {
            get => _password;
            set { if (_password != value) { _password = value; OnPropertyChanged(nameof(Password)); } }
        }

        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; OnPropertyChanged(nameof(Status)); } }
        }

        public string RegisteredAt
        {
            get => _registeredAt;
            set { if (_registeredAt != value) { _registeredAt = value; OnPropertyChanged(nameof(RegisteredAt)); } }
        }

        public string ResultDetail
        {
            get => _resultDetail;
            set { if (_resultDetail != value) { _resultDetail = value; OnPropertyChanged(nameof(ResultDetail)); } }
        }

        private string _sessionStatus = "⚪ ไม่มี";
        public string SessionStatus
        {
            get => _sessionStatus;
            set { if (_sessionStatus != value) { _sessionStatus = value; OnPropertyChanged(nameof(SessionStatus)); } }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class AccountStateItem
    {
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Status { get; set; } = "⏳ รอคิว";
        public string RegisteredAt { get; set; } = "-";
        public string ResultDetail { get; set; } = "";
        public string SessionStatus { get; set; } = "⚪ ยังไม่ได้ล็อกอิน";
        public bool IsSelected { get; set; } = true;
    }
}

