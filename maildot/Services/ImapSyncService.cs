using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using maildot.Data;
using maildot.Models;
using maildot.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.UI.Dispatching;
using MimeKit;
using Windows.UI;

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
    private MailDbContext? _db;
    private AccountSettings? _settings;
    private string? _password;
    private bool _dbReady;

    public ImapSyncService(MailboxViewModel viewModel, DispatcherQueue dispatcher)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
    }

    public async Task StartAsync(AccountSettings settings, string password)
    {
        _settings = settings;
        _password = password;

        if (!await ConnectAsync("Connecting to IMAP…"))
        {
            return;
        }

        if (_viewModel.SelectedFolder is { } initialFolder)
        {
            await LoadFolderAsync(initialFolder.Id);
        }
    }

    public Task LoadFolderAsync(string folderId) => LoadFolderInternalAsync(folderId, allowReconnect: true);

    private async Task LoadFolderInternalAsync(string folderId, bool allowReconnect)
    {
        if (string.IsNullOrEmpty(folderId))
        {
            return;
        }

        IMailFolder? folder = null;
        string folderDisplay = folderId;
        Exception? failure = null;
        var shouldRetry = false;

        await _semaphore.WaitAsync(_cts.Token);
        try
        {
            if (_client == null)
            {
                throw new ServiceNotConnectedException();
            }

            if (!_folderCache.TryGetValue(folderId, out folder))
            {
                throw new InvalidOperationException("Folder could not be found on the server.");
            }

            folderDisplay = string.IsNullOrEmpty(folder.Name) ? folderId : folder.Name;
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
                    _viewModel.SetRetryVisible(false);
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
                _viewModel.SetRetryVisible(false);
            });
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
            shouldRetry = allowReconnect && IsRecoverable(ex);
        }
        finally
        {
            try
            {
                if (folder?.IsOpen == true)
                {
                    await folder.CloseAsync(false, _cts.Token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }

        if (shouldRetry && await TryReconnectAsync())
        {
            await LoadFolderInternalAsync(folderId, false);
            return;
        }

        if (failure != null)
        {
            await ReportStatusAsync($"Unable to load messages: {failure.Message}", false);
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
        }
    }

    public Task LoadOlderMessagesAsync(string folderId) => LoadOlderMessagesInternalAsync(folderId, allowReconnect: true);

    private async Task LoadOlderMessagesInternalAsync(string folderId, bool allowReconnect)
    {
        if (string.IsNullOrEmpty(folderId))
        {
            return;
        }

        IMailFolder? folder = null;
        Exception? failure = null;
        var shouldRetry = false;

        await _semaphore.WaitAsync(_cts.Token);
        try
        {
            if (_client == null)
            {
                throw new ServiceNotConnectedException();
            }

            if (!_folderCache.TryGetValue(folderId, out folder))
            {
                throw new InvalidOperationException("Folder could not be found on the server.");
            }

            if (!_folderNextEndIndex.TryGetValue(folderId, out var nextEndIndex) || nextEndIndex < 0)
            {
                await ReportStatusAsync("No more messages to load.", false);
                await EnqueueAsync(() =>
                {
                    _viewModel.SetLoadMoreAvailability(false);
                    _viewModel.SetRetryVisible(false);
                });
                return;
            }

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
                _viewModel.SetRetryVisible(false);
            });
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            failure = ex;
            shouldRetry = allowReconnect && IsRecoverable(ex);
        }
        finally
        {
            try
            {
                if (folder?.IsOpen == true)
                {
                    await folder.CloseAsync(false, _cts.Token);
                }
            }
            catch
            {
            }

            _semaphore.Release();
        }

        if (shouldRetry && await TryReconnectAsync())
        {
            await LoadOlderMessagesInternalAsync(folderId, false);
            return;
        }

        if (failure != null)
        {
            await ReportStatusAsync($"Unable to load earlier messages: {failure.Message}", false);
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
        }
    }

    private async Task<bool> ConnectAsync(string statusMessage)
    {
        if (_settings == null || _password == null)
        {
            return false;
        }

        try
        {
            await ReportStatusAsync(statusMessage, true);

            _client?.Dispose();
            _client = new ImapClient();
            await _client.ConnectAsync(_settings.Server, _settings.Port, _settings.UseSsl, _cts.Token);
            await _client.AuthenticateAsync(_settings.Username, _password, _cts.Token);

            _folderCache.Clear();
            _folderNextEndIndex.Clear();

            await ReportStatusAsync("Loading folders…", true);
            var folders = await LoadFoldersAsync(_cts.Token);
            await EnqueueAsync(() =>
            {
                _viewModel.SetFolders(folders);
                _viewModel.SetRetryVisible(false);
            });

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            await ReportStatusAsync($"IMAP connection failed: {ex.Message}", false);
            await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
            return false;
        }
    }

    private Task<bool> TryReconnectAsync() => ConnectAsync("Reconnecting to IMAP…");

    private static bool IsRecoverable(Exception ex) =>
        ex is ServiceNotConnectedException ||
        ex is ImapProtocolException ||
        ex is ImapCommandException ||
        ex is IOException;

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

        await PersistMessagesAsync(folder, summaries);

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

                var colorComponents = SenderColorHelper.GetColor(senderName, senderAddress);
                var messageColor = new Color
                {
                    A = 255,
                    R = colorComponents.R,
                    G = colorComponents.G,
                    B = colorComponents.B
                };

                return new EmailMessageViewModel
                {
                    Id = summary.UniqueId.Id.ToString(),
                    Subject = summary.Envelope?.Subject ?? "(No subject)",
                    Sender = senderDisplay,
                    SenderAddress = senderAddress ?? string.Empty,
                    SenderInitials = SenderInitialsHelper.From(senderName, senderAddress),
                    SenderColor = messageColor,
                    Preview = summary.Envelope?.Subject ?? string.Empty,
                    Received = summary.InternalDate?.DateTime ?? DateTime.UtcNow
                };
            })
            .ToList();
    }

    private async Task PersistMessagesAsync(IMailFolder folder, IList<IMessageSummary> summaries)
    {
        if (_settings == null || summaries.Count == 0)
        {
            return;
        }

        if (!_dbReady && !await EnsureDbAsync())
        {
            return;
        }

        if (_db == null)
        {
            return;
        }

        var folderEntity = await _db.ImapFolders
            .FirstOrDefaultAsync(f => f.AccountId == _settings.Id && f.FullName == folder.FullName, _cts.Token);

        if (folderEntity == null)
        {
            folderEntity = new Models.ImapFolder
            {
                AccountId = _settings.Id,
                FullName = folder.FullName,
                DisplayName = folder.Name ?? folder.FullName
            };

            _db.ImapFolders.Add(folderEntity);
            await _db.SaveChangesAsync(_cts.Token);
        }

        var existingUids = await _db.ImapMessages
            .Where(m => m.FolderId == folderEntity.Id)
            .Select(m => m.ImapUid)
            .ToHashSetAsync(_cts.Token);

        foreach (var summary in summaries)
        {
            var uid = (long)summary.UniqueId.Id;
            if (existingUids.Contains(uid))
            {
                continue;
            }

            var mailbox = summary.Envelope?.From?.OfType<MailboxAddress>().FirstOrDefault();
            var senderName = mailbox?.Name ?? string.Empty;
            var senderAddress = mailbox?.Address ?? string.Empty;

            var messageId = summary.Envelope?.MessageId;
            if (string.IsNullOrWhiteSpace(messageId))
            {
                messageId = $"uid:{uid}@{_settings.Server}";
            }

            var received = summary.InternalDate?.ToUniversalTime() ?? DateTimeOffset.UtcNow;

            _db.ImapMessages.Add(new ImapMessage
            {
                FolderId = folderEntity.Id,
                ImapUid = uid,
                MessageId = messageId,
                Subject = summary.Envelope?.Subject ?? string.Empty,
                FromName = senderName,
                FromAddress = senderAddress,
                ReceivedUtc = received,
                Hash = $"{messageId}:{uid}"
            });
        }

        await _db.SaveChangesAsync(_cts.Token);
    }

    private async Task<bool> EnsureDbAsync()
    {
        if (_dbReady)
        {
            return true;
        }

        try
        {
            var pg = PostgresSettingsStore.Load();
            var passwordResponse = await CredentialManager.RequestPostgresPasswordAsync(pg);
            if (passwordResponse.Result != CredentialAccessResult.Success || string.IsNullOrWhiteSpace(passwordResponse.Password))
            {
                return false;
            }

            _db = MailDbContextFactory.CreateDbContext(pg, passwordResponse.Password);
            _dbReady = true;
            return true;
        }
        catch
        {
            _dbReady = false;
            _db?.Dispose();
            _db = null;
            return false;
        }
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
        _db?.Dispose();
        _db = null;
        _dbReady = false;
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

        _settings = null;
        _password = null;
        _cts.Dispose();
    }

    public Task<string?> LoadMessageBodyAsync(string folderId, string messageId) =>
        LoadMessageBodyInternalAsync(folderId, messageId, true);

    private async Task<string?> LoadMessageBodyInternalAsync(string folderId, string messageId, bool allowReconnect)
    {
        if (string.IsNullOrEmpty(folderId) || string.IsNullOrEmpty(messageId))
        {
            return null;
        }

        var canRetry = allowReconnect;

        while (true)
        {
            IMailFolder? folder = null;
            await _semaphore.WaitAsync(_cts.Token);
            try
            {
                if (_client == null)
                {
                    throw new ServiceNotConnectedException();
                }

                if (!_folderCache.TryGetValue(folderId, out folder))
                {
                    return null;
                }

                if (!uint.TryParse(messageId, out var idValue))
                {
                    return null;
                }

                if (!folder.IsOpen)
                {
                    await folder.OpenAsync(FolderAccess.ReadOnly, _cts.Token);
                }

                var uniqueId = new UniqueId(idValue);
                var message = await folder.GetMessageAsync(uniqueId, _cts.Token);

                var html = message.HtmlBody;
                var plain = message.TextBody;

                string htmlToRender;
                if (!string.IsNullOrWhiteSpace(html))
                {
                    htmlToRender = HtmlSanitizer.Sanitize(html).Html;
                }
                else if (!string.IsNullOrWhiteSpace(plain))
                {
                    htmlToRender = $"<html><body><pre>{System.Net.WebUtility.HtmlEncode(plain)}</pre></body></html>";
                }
                else
                {
                    htmlToRender = "<html><body></body></html>";
                }

                return htmlToRender;
            }
            catch (Exception ex)
            {
                if (canRetry && IsRecoverable(ex) && await TryReconnectAsync())
                {
                    canRetry = false;
                    continue;
                }

                await ReportStatusAsync($"Unable to load message: {ex.Message}", false);
                await EnqueueAsync(() => _viewModel.SetRetryVisible(true));
                return null;
            }
            finally
            {
                try
                {
                    if (folder?.IsOpen == true)
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
    }
}
