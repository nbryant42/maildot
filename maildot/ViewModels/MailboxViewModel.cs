using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace maildot.ViewModels;

public sealed class MailboxViewModel : INotifyPropertyChanged
{
    private MailFolderViewModel? _selectedFolder;
    private string? _lastSelectedFolderId;
    private string _statusMessage = "Loading folders…";
    private bool _isBusy;
    private string _currentFolderTitle = "Mailbox";
    private string _accountSummary = "Connected to IMAP";
    private string _searchTerm = string.Empty;
    private bool _isSearchActive;
    private SearchNavItemViewModel? _selectedSearchItem;
    private bool _canLoadMore;
    private bool _isRetryVisible;
    private EmailMessageViewModel? _selectedMessage;
    private int? _selectedLabelId;

    public ObservableCollection<MailFolderViewModel> Folders { get; } = new();
    public ObservableCollection<EmailMessageViewModel> Messages { get; } = new();
    public ObservableCollection<SearchNavItemViewModel> SearchItems { get; } = new();
    public ObservableCollection<LabelViewModel> Labels { get; } = new();

    public MailFolderViewModel? SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (_selectedFolder != value)
            {
                _selectedFolder = value;
                if (value != null)
                {
                    _lastSelectedFolderId = value.Id;
                }
                OnPropertyChanged(nameof(SelectedFolder));
            }
        }
    }

    public string? LastSelectedFolderId => _lastSelectedFolderId;

    public string SearchTerm
    {
        get => _searchTerm;
        private set
        {
            if (_searchTerm != value)
            {
                _searchTerm = value;
                OnPropertyChanged(nameof(SearchTerm));
            }
        }
    }

    public bool IsSearchActive
    {
        get => _isSearchActive;
        private set
        {
            if (_isSearchActive != value)
            {
                _isSearchActive = value;
                OnPropertyChanged(nameof(IsSearchActive));
            }
        }
    }

    public SearchNavItemViewModel? SelectedSearchItem
    {
        get => _selectedSearchItem;
        set
        {
            if (_selectedSearchItem != value)
            {
                _selectedSearchItem = value;
                OnPropertyChanged(nameof(SelectedSearchItem));
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
        var selectedId = _selectedLabelId != null ? null : SelectedFolder?.Id ?? _lastSelectedFolderId;
        Folders.Clear();

        foreach (var folder in folders)
        {
            Folders.Add(folder);
        }

        if (IsSearchActive || _selectedLabelId != null)
        {
            return;
        }

        var target = Folders.FirstOrDefault(f => f.Id == selectedId);
        if (!IsSearchActive)
        {
            target ??= Folders.FirstOrDefault();
        }

        SelectedFolder = target;
    }

    public void SetLabels(IEnumerable<LabelViewModel> labels)
    {
        var selectedId = _selectedLabelId;
        Labels.Clear();
        foreach (var label in labels)
        {
            Labels.Add(label);
        }

        if (selectedId != null)
        {
            SelectLabel(selectedId);
        }
    }

    public void SelectLabel(int? labelId)
    {
        _selectedLabelId = labelId;

        foreach (var root in Labels)
        {
            SetLabelSelection(root, labelId);
        }

        if (labelId != null && SelectedFolder != null)
        {
            SelectedFolder = null;
        }
    }

    public int? SelectedLabelId => _selectedLabelId;

    private static void SetLabelSelection(LabelViewModel label, int? targetId)
    {
        label.IsSelected = label.Id == targetId;
        foreach (var child in label.Children)
        {
            SetLabelSelection(child, targetId);
        }
    }

    public void AddLabel(LabelViewModel label, int? parentLabelId)
    {
        if (parentLabelId == null)
        {
            Labels.Add(label);
            return;
        }

        var parent = FindLabel(parentLabelId.Value);
        if (parent != null)
        {
            parent.Children.Add(label);
        }
        else
        {
            Labels.Add(label);
        }

        SelectLabel(label.Id);
    }

    public LabelViewModel? FindLabel(int id)
    {
        foreach (var label in Labels)
        {
            var found = label.FindById(id);
            if (found != null)
            {
                return found;
            }
        }

        return null;
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

    public void EnterSearchMode(string term)
    {
        SearchTerm = term;
        IsSearchActive = true;
        SearchItems.Clear();
        var searchVm = new SearchNavItemViewModel(term);
        SearchItems.Add(searchVm);
        SelectedSearchItem = searchVm;
        if (_selectedFolder != null)
        {
            _selectedFolder = null;
            OnPropertyChanged(nameof(SelectedFolder));
        }
        if (_selectedLabelId != null)
        {
            SelectLabel(null);
        }
    }

    public void ExitSearchMode()
    {
        if (!IsSearchActive)
        {
            return;
        }

        IsSearchActive = false;
        SearchTerm = string.Empty;
        SearchItems.Clear();
        SelectedSearchItem = null;

        if (_selectedFolder == null)
        {
            var target = Folders.FirstOrDefault(f => f.Id == _lastSelectedFolderId) ?? Folders.FirstOrDefault();
            if (target != null)
            {
                SelectedFolder = target;
            }
        }
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

public sealed class EmailMessageViewModel : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString();
    private string _folderId = string.Empty;
    private string _subject = "(No subject)";
    private string _sender = "(Unknown sender)";
    private string _senderAddress = string.Empty;
    private string _senderInitials = string.Empty;
    private Color _senderColor;
    private string _preview = string.Empty;
    private DateTime _received;
    private string _to = string.Empty;
    private string? _cc;
    private string? _bcc;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value ?? string.Empty);
    }

    public string FolderId
    {
        get => _folderId;
        set => SetProperty(ref _folderId, value ?? string.Empty);
    }

    public string Subject
    {
        get => _subject;
        set => SetProperty(ref _subject, value ?? string.Empty);
    }

    public string Sender
    {
        get => _sender;
        set => SetProperty(ref _sender, value ?? string.Empty, nameof(Sender), nameof(FromDisplay));
    }

    public string SenderAddress
    {
        get => _senderAddress;
        set => SetProperty(ref _senderAddress, value ?? string.Empty, nameof(SenderAddress), nameof(FromDisplay));
    }

    public string SenderInitials
    {
        get => _senderInitials;
        set => SetProperty(ref _senderInitials, value ?? string.Empty);
    }

    public Color SenderColor
    {
        get => _senderColor;
        set => SetProperty(ref _senderColor, value);
    }

    public string Preview
    {
        get => _preview;
        set => SetProperty(ref _preview, value ?? string.Empty);
    }

    public DateTime Received
    {
        get => _received;
        set => SetProperty(ref _received, value, nameof(Received), nameof(ReceivedDisplay));
    }

    public string To
    {
        get => _to;
        set => SetProperty(ref _to, value?.Trim() ?? string.Empty, nameof(To), nameof(ToDisplay), nameof(HasTo));
    }

    public string? Cc
    {
        get => _cc;
        set => SetProperty(ref _cc, string.IsNullOrWhiteSpace(value) ? null : value.Trim(), nameof(Cc), nameof(CcDisplay), nameof(HasCc));
    }

    public string? Bcc
    {
        get => _bcc;
        set => SetProperty(ref _bcc, string.IsNullOrWhiteSpace(value) ? null : value.Trim(), nameof(Bcc), nameof(BccDisplay), nameof(HasBcc));
    }

    public string MessageId => Id;

    public string ReceivedDisplay => _received == default
        ? string.Empty
        : _received.ToString("g");

    public string FromDisplay => string.IsNullOrWhiteSpace(Sender) ? string.Empty : $"From: {Sender}";
    public string ToDisplay => string.IsNullOrWhiteSpace(To) ? string.Empty : $"To: {To}";
    public string CcDisplay => string.IsNullOrWhiteSpace(Cc) ? string.Empty : $"Cc: {Cc}";
    public string BccDisplay => string.IsNullOrWhiteSpace(Bcc) ? string.Empty : $"Bcc: {Bcc}";

    public bool HasTo => !string.IsNullOrWhiteSpace(To);
    public bool HasCc => !string.IsNullOrWhiteSpace(Cc);
    public bool HasBcc => !string.IsNullOrWhiteSpace(Bcc);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null, params string[] dependentProperties)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);

        if (dependentProperties != null)
        {
            foreach (var name in dependentProperties)
            {
                OnPropertyChanged(name);
            }
        }

        return true;
    }
}

public sealed class SearchNavItemViewModel
{
    public SearchNavItemViewModel(string term)
    {
        Term = term;
    }

    public string Term { get; }
    public string DisplayName => Term;
}

public sealed class LabelViewModel : INotifyPropertyChanged
{
    private string _name;
    private bool _isSelected;

    public LabelViewModel(int id, string name, int? parentId)
    {
        Id = id;
        _name = name;
        ParentId = parentId;
    }

    public int Id { get; }
    public int? ParentId { get; }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public ObservableCollection<LabelViewModel> Children { get; } = new();

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LabelViewModel? FindById(int id)
    {
        if (Id == id)
        {
            return this;
        }

        foreach (var child in Children)
        {
            var found = child.FindById(id);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
