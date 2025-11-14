using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using maildot.Models;
using maildot.ViewModels;
using Microsoft.UI.Dispatching;
using MimeKit;

namespace maildot.Services;

public sealed class ImapSyncService : IAsyncDisposable
{
    private const int PageSize = 40;

    private readonly MailboxViewModel _viewModel;
    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Dictionary<string, IMailFolder> _folderCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _folderNextEndIndex = new(StringComparer.OrdinalIgnoreCase);

    private ImapClient? _client;

    public ImapSyncService(MailboxViewModel viewModel, DispatcherQueue dispatcher)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
    }

    public async Task StartAsync(AccountSettings settings, string password)
    {
        await _semaphore.WaitAsync(_cts.Token);
        try
        {
            await ReportStatusAsync("Connecting to IMAP…", true);

            _client = new ImapClient();
            await _client.ConnectAsync(settings.Server, settings.Port, settings.UseSsl, _cts.Token);
            await _client.AuthenticateAsync(settings.Username, password, _cts.Token);

            await ReportStatusAsync("Loading folders…", true);
            var folders = await LoadFoldersAsync(_cts.Token);
            await EnqueueAsync(() => _viewModel.SetFolders(folders));
        }
        catch (OperationCanceledException)
        {
            // App is closing or user reconfigured the account.
        }
        catch (Exception ex)
        {
            await ReportStatusAsync($"IMAP error: {ex.Message}", false);
        }
        finally
        {
            _semaphore.Release();
        }

        if (_viewModel.SelectedFolder is { } initialFolder)
        {
            await LoadFolderAsync(initialFolder.Id);
        }
    }

    public async Task LoadFolderAsync(string folderId)
    {
        if (string.IsNullOrEmpty(folderId))
        {
            return;
        }

        if (_client == null)
        {
            await ReportStatusAsync("IMAP client is not connected.", false);
            return;
        }

        if (!_folderCache.TryGetValue(folderId, out var folder))
        {
            await ReportStatusAsync("Folder could not be found on the server.", false);
            return;
        }

        await _semaphore.WaitAsync(_cts.Token);
        try
        {
            var folderDisplay = string.IsNullOrEmpty(folder.Name) ? folderId : folder.Name;
            await ReportStatusAsync($"Loading {folderDisplay}…", true);
            if (!folder.IsOpen)
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, _cts.Token);
            }

            var messageCount = folder.Count;
            if (messageCount == 0)
            {
                _folderNextEndIndex[folderId] = -1;
                await EnqueueAsync(() =>
                {
                    _viewModel.SetMessages(folderDisplay, Array.Empty<EmailMessageViewModel>());
                    _viewModel.SetStatus("Folder is empty.", false);
                    _viewModel.SetLoadMoreAvailability(false);
                });
                return;
            }

            var endIndex = messageCount - 1;
            var startIndex = Math.Max(0, endIndex - PageSize + 1);
            var emailItems = await FetchMessagesAsync(folder, startIndex, endIndex);

            _folderNextEndIndex[folderId] = startIndex - 1;

            await EnqueueAsync(() =>
            {
                _viewModel.SetMessages(folderDisplay, emailItems);
                _viewModel.SetStatus("Mailbox is up to date.", false);
                _viewModel.SetLoadMoreAvailability(startIndex > 0);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await ReportStatusAsync($"Unable to load messages: {ex.Message}", false);
        }
        finally
        {
            try
            {
                if (folder.IsOpen)
                {
                    await folder.CloseAsync(false, _cts.Token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }
    }

    public async Task LoadOlderMessagesAsync(string folderId)
    {
        if (string.IsNullOrEmpty(folderId))
        {
            return;
        }

        if (_client == null)
        {
            return;
        }

        if (!_folderCache.TryGetValue(folderId, out var folder))
        {
            return;
        }

        if (!_folderNextEndIndex.TryGetValue(folderId, out var nextEndIndex) || nextEndIndex < 0)
        {
            await ReportStatusAsync("No more messages to load.", false);
            await EnqueueAsync(() => _viewModel.SetLoadMoreAvailability(false));
            return;
        }

        await _semaphore.WaitAsync(_cts.Token);
        try
        {
            var folderDisplay = string.IsNullOrEmpty(folder.Name) ? folderId : folder.Name;
            await ReportStatusAsync($"Loading older messages for {folderDisplay}…", true);
            if (!folder.IsOpen)
            {
                await folder.OpenAsync(FolderAccess.ReadOnly, _cts.Token);
            }

            var endIndex = nextEndIndex;
            var startIndex = Math.Max(0, endIndex - PageSize + 1);
            var emailItems = await FetchMessagesAsync(folder, startIndex, endIndex);

            _folderNextEndIndex[folderId] = startIndex - 1;

            await EnqueueAsync(() =>
            {
                _viewModel.AppendMessages(emailItems);
                _viewModel.SetStatus("Mailbox is up to date.", false);
                _viewModel.SetLoadMoreAvailability(startIndex > 0);
            });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await ReportStatusAsync($"Unable to load earlier messages: {ex.Message}", false);
        }
        finally
        {
            try
            {
                if (folder.IsOpen)
                {
                    await folder.CloseAsync(false, _cts.Token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }
    }

    private async Task<IReadOnlyList<MailFolderViewModel>> LoadFoldersAsync(CancellationToken token)
    {
        var folders = new List<MailFolderViewModel>();
        if (_client == null)
        {
            return folders;
        }

        _folderCache.Clear();

        async Task AddFolderAsync(IMailFolder folder)
        {
            await folder.StatusAsync(StatusItems.Unread | StatusItems.Count, token);
            _folderCache[folder.FullName] = folder;

            var folderVm = new MailFolderViewModel(folder.FullName, folder.Name ?? folder.FullName)
            {
                UnreadCount = folder.Unread
            };

            folders.Add(folderVm);
        }

        await AddFolderAsync(_client.Inbox);

        var personalRoot = _client.PersonalNamespaces.Count > 0
            ? _client.GetFolder(_client.PersonalNamespaces[0])
            : null;

        if (personalRoot != null)
        {
            var subfolders = await personalRoot.GetSubfoldersAsync(false, token);
            foreach (var folder in subfolders.Where(f => !f.Attributes.HasFlag(FolderAttributes.NonExistent)))
            {
                await AddFolderAsync(folder);
            }
        }

        return folders;
    }

    private async Task<List<EmailMessageViewModel>> FetchMessagesAsync(IMailFolder folder, int startIndex, int endIndex)
    {
        var summaries = await folder.FetchAsync(startIndex, endIndex,
            MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate,
            _cts.Token);

        return summaries
            .OrderByDescending(s => s.InternalDate?.UtcDateTime ?? DateTime.MinValue)
            .Select(summary =>
            {
                var mailbox = summary.Envelope?.From?.OfType<MailboxAddress>().FirstOrDefault();
                var senderName = mailbox?.Name;
                var senderAddress = mailbox?.Address;
                var senderDisplay = !string.IsNullOrWhiteSpace(senderName)
                    ? senderName!
                    : senderAddress ?? "(Unknown sender)";

                return new EmailMessageViewModel
                {
                    Id = summary.UniqueId.Id.ToString(),
                    Subject = summary.Envelope?.Subject ?? "(No subject)",
                    Sender = senderDisplay,
                    SenderInitials = SenderInitialsHelper.From(senderName, senderAddress),
                    Preview = summary.Envelope?.Subject ?? string.Empty,
                    Received = summary.InternalDate?.DateTime ?? DateTime.UtcNow
                };
            })
            .ToList();
    }

    private Task ReportStatusAsync(string message, bool busy) =>
        EnqueueAsync(() => _viewModel.SetStatus(message, busy));

    private Task EnqueueAsync(Action action)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = _dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                completion.SetResult(true);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        if (!enqueued)
        {
            completion.SetResult(true);
        }

        return completion.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        _semaphore.Dispose();
        if (_client != null)
        {
            try
            {
                if (_client.IsConnected)
                {
                    await _client.DisconnectAsync(true);
                }
            }
            catch
            {
            }

            _client.Dispose();
            _client = null;
        }

        _cts.Dispose();
    }
}
