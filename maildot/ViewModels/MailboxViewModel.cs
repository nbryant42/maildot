using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace maildot.ViewModels;

public sealed class MailboxViewModel : INotifyPropertyChanged
{
    private MailFolderViewModel? _selectedFolder;
    private string _statusMessage = "Loading folders…";
    private bool _isBusy;
    private string _currentFolderTitle = "Mailbox";
    private string _accountSummary = "Connected to IMAP";
    private bool _canLoadMore;
    private bool _isRetryVisible;
    private EmailMessageViewModel? _selectedMessage;

    public ObservableCollection<MailFolderViewModel> Folders { get; } = new();
    public ObservableCollection<EmailMessageViewModel> Messages { get; } = new();

    public MailFolderViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (_selectedFolder != value)
            {
                _selectedFolder = value;
                OnPropertyChanged(nameof(SelectedFolder));
            }
        }
    }

    public EmailMessageViewModel? SelectedMessage
    {
        get => _selectedMessage;
        set
        {
            if (_selectedMessage != value)
            {
                _selectedMessage = value;
                OnPropertyChanged(nameof(SelectedMessage));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                OnPropertyChanged(nameof(StatusMessage));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy != value)
            {
                _isBusy = value;
                OnPropertyChanged(nameof(IsBusy));
            }
        }
    }

    public string CurrentFolderTitle
    {
        get => _currentFolderTitle;
        private set
        {
            if (_currentFolderTitle != value)
            {
                _currentFolderTitle = value;
                OnPropertyChanged(nameof(CurrentFolderTitle));
            }
        }
    }

    public string AccountSummary
    {
        get => _accountSummary;
        private set
        {
            if (_accountSummary != value)
            {
                _accountSummary = value;
                OnPropertyChanged(nameof(AccountSummary));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetFolders(IEnumerable<MailFolderViewModel> folders)
    {
        var selectedId = SelectedFolder?.Id;
        Folders.Clear();

        foreach (var folder in folders)
        {
            Folders.Add(folder);
        }

        SelectedFolder = Folders.FirstOrDefault(f => f.Id == selectedId) ?? Folders.FirstOrDefault();
    }

    public void SetMessages(string folderTitle, IEnumerable<EmailMessageViewModel> messages)
    {
        CurrentFolderTitle = folderTitle;

        Messages.Clear();
        foreach (var message in messages)
        {
            Messages.Add(message);
        }
    }

    public bool CanLoadMore
    {
        get => _canLoadMore;
        private set
        {
            if (_canLoadMore != value)
            {
                _canLoadMore = value;
                OnPropertyChanged(nameof(CanLoadMore));
            }
        }
    }

    public bool IsRetryVisible
    {
        get => _isRetryVisible;
        private set
        {
            if (_isRetryVisible != value)
            {
                _isRetryVisible = value;
                OnPropertyChanged(nameof(IsRetryVisible));
                OnPropertyChanged(nameof(RetryVisibility));
            }
        }
    }

    public Visibility RetryVisibility => _isRetryVisible ? Visibility.Visible : Visibility.Collapsed;

    public void SetStatus(string message, bool isBusy)
    {
        StatusMessage = message;
        IsBusy = isBusy;
    }

    public void SetAccountSummary(string summary)
    {
        AccountSummary = summary;
    }

    public void SetLoadMoreAvailability(bool canLoadMore)
    {
        CanLoadMore = canLoadMore;
    }

    public void AppendMessages(IEnumerable<EmailMessageViewModel> messages)
    {
        foreach (var message in messages)
        {
            Messages.Add(message);
        }
    }

    public void SetRetryVisible(bool isVisible)
    {
        IsRetryVisible = isVisible;
    }

    public void UpdateFolderCounts(string folderId, int unreadCount)
    {
        var folder = Folders.FirstOrDefault(f => f.Id == folderId);
        if (folder != null)
        {
            folder.UnreadCount = unreadCount;
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MailFolderViewModel : INotifyPropertyChanged
{
    private int _unreadCount;

    public MailFolderViewModel(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public string Id { get; }
    public string DisplayName { get; }

    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (_unreadCount != value)
            {
                _unreadCount = value;
                OnPropertyChanged(nameof(UnreadCount));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class EmailMessageViewModel
{
    public string Id { get; init; } = Guid.NewGuid().ToString();
    public string FolderId { get; init; } = string.Empty;
    public string Subject { get; init; } = "(No subject)";
    public string Sender { get; init; } = "(Unknown sender)";
    public string SenderAddress { get; init; } = string.Empty;
    public string SenderInitials { get; init; } = string.Empty;
    public Color SenderColor { get; init; }
    public string Preview { get; init; } = string.Empty;
    public DateTime Received { get; init; }
    public string MessageId => Id;

    public string ReceivedDisplay => Received == default
        ? string.Empty
        : Received.ToString("g");
}
