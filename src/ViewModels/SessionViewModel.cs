using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NoBSSftp.Models;
using NoBSSftp.Services;
using System.Threading;
using System.Threading.Tasks;

namespace NoBSSftp.ViewModels;

public partial class SessionViewModel : ViewModelBase
{
    private const int TransferRetryAttempts = 3;
    private const long HashVerificationThresholdBytes = 64L * 1024 * 1024;
    private const int RecursiveSearchDirectoryLimit = 10000;
    private const int RecursiveSearchDebounceMs = 300;
    private const string ExplorerViewListOption = "📄 List";
    private const string ExplorerViewTreeOption = "🌳 Tree";
    private const string AtomicUploadTempPrefix = ".nobssftp-upload";
    private static readonly TimeSpan VerificationTimestampTolerance = TimeSpan.FromMinutes(2);

    private readonly ISftpService _sftpService;
    private readonly IDialogService _dialogService;
    private readonly IProfileManager _profileManager;
    private readonly IUserVerificationService _userVerificationService;
    private CancellationTokenSource? _transferCts;
    private readonly Queue<QueuedTransfer> _transferQueue = new();
    private readonly Dictionary<Guid, Func<TransferWorkDefinition>> _retryFactories = new();
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _treeLoadCts;
    private int _searchRevision;
    private bool _isProcessingTransferQueue;
    private DateTime _lastExternalTerminalLaunchUtc = DateTime.MinValue;
    private DateTime _lastSuggestionConnectUtc = DateTime.MinValue;

    [ObservableProperty]
    private string _header = "New Server";

    [ObservableProperty]
    private ServerProfile _profile;

    [ObservableProperty]
    private string _currentPath = "/";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenExternalTerminalCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isConnecting;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private FileEntry? _selectedFile;

    [ObservableProperty]
    private System.Collections.IList? _selectedFiles;

    [ObservableProperty]
    private bool _isTransferring;

    [ObservableProperty]
    private double _transferProgress;

    [ObservableProperty]
    private string _transferTitle = "";

    [ObservableProperty]
    private bool _showHiddenFiles;

    [ObservableProperty]
    private bool _showTransferQueue = true;

    [ObservableProperty]
    private bool _verifyTransfersAfterTransfer;

    [ObservableProperty]
    private bool _isTreeView;

    [ObservableProperty]
    private bool _isTreeLoading;

    [ObservableProperty]
    private string _selectedExplorerView = ExplorerViewListOption;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _searchRecursive;

    [ObservableProperty]
    private bool _isSearchInProgress;

    [ObservableProperty]
    private int _connectFailureFocusRequestId;

    [ObservableProperty]
    private TransferJob? _selectedTransferJob;

    [ObservableProperty]
    private RemoteTreeNode? _selectedTreeNode;

    private class ClipboardItem
    {
        public string Path { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
        public bool IsSymbolicLink { get; init; }
        public string SymbolicLinkTarget { get; set; } = string.Empty;
        public bool SymbolicLinkTargetIsDirectory { get; set; }
        public long Size { get; init; }
        public DateTime LastWriteTime { get; init; }
    }

    private sealed class UploadItem
    {
        public string LocalPath { get; init; } = string.Empty;
        public string RemotePath { get; set; } = string.Empty;
        public string? AtomicTempRemotePath { get; set; }
        public string DisplayName { get; init; } = string.Empty;
        public long Size { get; init; }
    }

    private sealed class UploadSourceTarget
    {
        public string LocalPath { get; init; } = string.Empty;
        public string RemoteRootPath { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
        public string DisplayRootName { get; init; } = string.Empty;
    }

    private sealed class UploadPlan
    {
        public List<string> Directories { get; } = [];
        public List<UploadItem> Files { get; } = [];
    }

    private sealed class DownloadFilePlanItem
    {
        public string RemotePath { get; init; } = string.Empty;
        public string LocalPath { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public long Size { get; init; }
    }

    private sealed class DownloadPlan
    {
        public List<string> Directories { get; } = [];
        public List<DownloadFilePlanItem> Files { get; } = [];
    }

    private sealed class DeletePlanItem
    {
        public string RemotePath { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
    }

    private sealed class AttributeApplyTarget
    {
        public string RemotePath { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
    }

    private enum VerificationMode
    {
        Hash,
        SizeAndTimeFallback
    }

    private sealed class DeleteTargetSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public string RemotePath { get; init; } = "/";
        public bool IsDirectory { get; init; }
        public bool IsSymbolicLink { get; init; }
        public bool FollowSymlinkTarget { get; init; }
    }

    private sealed class DeleteJobRequest
    {
        public string BaseRemotePath { get; init; } = "/";
        public IReadOnlyList<DeleteTargetSnapshot> Targets { get; init; } = [];
    }

    private sealed class UploadJobRequest
    {
        public string DestinationPath { get; init; } = "/";
        public IReadOnlyList<string> LocalPaths { get; init; } = [];
    }

    private sealed class DownloadJobRequest
    {
        public string Name { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public DateTime LastWriteTime { get; init; }
        public string RemotePath { get; init; } = "/";
        public string LocalFolder { get; init; } = string.Empty;
        public string? ResolvedLocalRootPath { get; set; }
    }

    private sealed class TransferWorkDefinition
    {
        public TransferJobType Type { get; init; }
        public string Title { get; init; } = string.Empty;
        public Func<TransferJob, CancellationToken, Task> ExecuteAsync { get; init; } = (_, _) => Task.CompletedTask;
        public Func<TransferWorkDefinition>? RetryFactory { get; init; }
    }

    private sealed class QueuedTransfer
    {
        public TransferJob Job { get; init; } = null!;
        public TransferWorkDefinition Definition { get; init; } = null!;
    }

    private List<ClipboardItem> _clipboardItems = new();
    private bool _clipboardIsCut;
    private List<FileEntry> _recursiveSearchResults = [];

    partial void OnShowHiddenFilesChanged(bool value)
    {
        _ = RefreshSearchResultsAsync(debounce: false);
        if (IsTreeView)
            RebuildTreeRootFromCurrentList();
    }

    partial void OnSearchQueryChanged(string value)
    {
        _ = RefreshSearchResultsAsync(debounce: true);
    }

    partial void OnSearchRecursiveChanged(bool value)
    {
        _ = RefreshSearchResultsAsync(debounce: false);
    }

    partial void OnSelectedExplorerViewChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        IsTreeView = IsTreeExplorerOption(value);
    }

    partial void OnIsTreeViewChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListView));
        var expected = value ? ExplorerViewTreeOption : ExplorerViewListOption;
        if (!string.Equals(SelectedExplorerView, expected, StringComparison.Ordinal))
            SelectedExplorerView = expected;
        if (value)
            SelectedFiles = null;
        if (value)
            RebuildTreeRootFromCurrentList();
    }

    partial void OnSelectedTreeNodeChanged(RemoteTreeNode? value)
    {
        if (value is null || value.IsPlaceholder)
            return;

        SelectedFiles = null;
        SelectedFile = value.Entry;
    }

    public event Action<SessionViewModel>? CloseRequested;

    public ObservableCollection<FileEntry> Files { get; } = [];
    public ObservableCollection<RemoteTreeNode> TreeItems { get; } = [];
    public ObservableCollection<TransferJob> TransferJobs { get; } = [];

    public bool HasTransferJobs => TransferJobs.Count > 0;

    public bool HasRetryableTransfers => TransferJobs.Any(job => job.IsRetryable);

    public bool IsTransferQueueVisible => ShowTransferQueue;

    public bool IsListView => !IsTreeView;

    public ObservableCollection<string> ExplorerViewOptions { get; } = new()
    {
        ExplorerViewListOption,
        ExplorerViewTreeOption
    };

    public string TransferQueueSummary
    {
        get
        {
            var pending = TransferJobs.Count(job => job.State == TransferJobState.Pending);
            var running = TransferJobs.Count(job => job.State == TransferJobState.Running);
            var failed = TransferJobs.Count(job => job.State == TransferJobState.Failed);
            var completed = TransferJobs.Count(job => job.State == TransferJobState.Completed);
            return $"Queue: {pending} pending, {running} running, {failed} failed, {completed} completed";
        }
    }

    [ObservableProperty]
    private ServerProfile? _suggestedServerPrimary;

    [ObservableProperty]
    private ServerProfile? _suggestedServerSecondary;

    public bool HasSuggestedServerPrimary => SuggestedServerPrimary is not null;
    public bool HasSuggestedServerSecondary => SuggestedServerSecondary is not null;
    public bool HasSingleSuggestedServer =>
        SuggestedServerPrimary is not null && SuggestedServerSecondary is null;
    public bool HasTwoSuggestedServers =>
        SuggestedServerPrimary is not null && SuggestedServerSecondary is not null;

    private List<FileEntry> _allFiles = [];

    public SessionViewModel() : this(new ServerProfile())
    {
    }

    public SessionViewModel(ServerProfile profile)
    {
        _sftpService = new SftpService();
        _dialogService = new DialogService();
        _profileManager = new ProfileManager();
        _userVerificationService = new UserVerificationService();
        _profile = profile;
        TransferJobs.CollectionChanged += OnTransferJobsCollectionChanged;
        Header = GetDisplayName();
    }

    partial void OnSelectedTransferJobChanged(TransferJob? value)
    {
        RetryTransferCommand.NotifyCanExecuteChanged();
    }

    private void OnTransferJobsCollectionChanged(object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<TransferJob>())
                item.PropertyChanged -= OnTransferJobPropertyChanged;
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<TransferJob>())
                item.PropertyChanged += OnTransferJobPropertyChanged;
        }

        NotifyTransferQueueChanged();
    }

    private void OnTransferJobPropertyChanged(object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TransferJob.State) or nameof(TransferJob.IsRetryable))
            NotifyTransferQueueChanged();
    }

    private void NotifyTransferQueueChanged()
    {
        OnPropertyChanged(nameof(HasTransferJobs));
        OnPropertyChanged(nameof(HasRetryableTransfers));
        OnPropertyChanged(nameof(IsTransferQueueVisible));
        OnPropertyChanged(nameof(TransferQueueSummary));
        RetryTransferCommand.NotifyCanExecuteChanged();
    }

    partial void OnShowTransferQueueChanged(bool value)
    {
        OnPropertyChanged(nameof(IsTransferQueueVisible));
    }

    private bool CanRetryTransfer(TransferJob? job)
    {
        var candidate = job ?? SelectedTransferJob;
        return candidate is not null &&
               candidate.IsRetryable &&
               _retryFactories.ContainsKey(candidate.Id);
    }

    [RelayCommand(CanExecute = nameof(CanRetryTransfer))]
    private void RetryTransfer(TransferJob? job)
    {
        var candidate = job ?? SelectedTransferJob;
        if (candidate is null)
            return;

        if (!_retryFactories.TryGetValue(candidate.Id, out var retryFactory))
            return;

        EnqueueTransfer(retryFactory());
    }

    private void EnqueueTransfer(TransferWorkDefinition definition)
    {
        var job = new TransferJob(definition.Type, definition.Title);
        TransferJobs.Add(job);

        _transferQueue.Enqueue(
            new QueuedTransfer
            {
                Job = job,
                Definition = definition
            });

        StatusMessage = $"Queued: {definition.Title}";
        NotifyTransferQueueChanged();
        _ = ProcessTransferQueueAsync();
    }

    private async Task ProcessTransferQueueAsync()
    {
        if (_isProcessingTransferQueue)
            return;

        _isProcessingTransferQueue = true;
        try
        {
            while (_transferQueue.Count > 0)
            {
                var queued = _transferQueue.Dequeue();
                await ExecuteQueuedTransferAsync(queued);
            }
        }
        finally
        {
            _isProcessingTransferQueue = false;
        }
    }

    private async Task ExecuteQueuedTransferAsync(QueuedTransfer queued)
    {
        var job = queued.Job;
        _transferCts = new CancellationTokenSource();
        IsTransferring = true;
        TransferProgress = 0;
        TransferTitle = job.Title;
        job.State = TransferJobState.Running;
        job.Status = "Starting...";
        job.Progress = 0;
        job.ErrorMessage = string.Empty;
        job.IsRetryable = false;
        _retryFactories.Remove(job.Id);
        NotifyTransferQueueChanged();

        try
        {
            await queued.Definition.ExecuteAsync(job, _transferCts.Token);
            job.State = TransferJobState.Completed;
            job.Progress = 100;
            if (string.IsNullOrWhiteSpace(job.Status))
                job.Status = "Completed";
            job.IsRetryable = false;
            _retryFactories.Remove(job.Id);
            TransferTitle = $"{job.Title} complete";
        }
        catch (OperationCanceledException)
        {
            job.State = TransferJobState.Cancelled;
            job.Status = "Cancelled";
            job.IsRetryable = false;
            _retryFactories.Remove(job.Id);
            TransferTitle = $"{job.Title} cancelled";
            StatusMessage = $"{job.Title} cancelled";
        }
        catch (Exception ex)
        {
            LoggingService.Error($"{job.JobType} transfer failed", ex);
            job.State = TransferJobState.Failed;
            job.ErrorMessage = ex.Message;
            job.Status = $"Failed: {ex.Message}";

            var retryFactory = queued.Definition.RetryFactory;
            if (retryFactory is not null)
            {
                _retryFactories[job.Id] = retryFactory;
                job.IsRetryable = true;
            }
            else
            {
                job.IsRetryable = false;
                _retryFactories.Remove(job.Id);
            }

            TransferTitle = $"{job.Title} failed";
            StatusMessage = $"{job.Title} failed: {ex.Message}";
        }
        finally
        {
            IsTransferring = false;
            _transferCts.Dispose();
            _transferCts = null;
            NotifyTransferQueueChanged();
        }
    }

    public void SetConnectionSuggestions(IEnumerable<ServerProfile> suggestions)
    {
        var topSuggestions = suggestions
            .Where(s => s is not null)
            .Take(2)
            .ToArray();

        SuggestedServerPrimary = topSuggestions.Length > 0
            ? topSuggestions[0]
            : null;

        SuggestedServerSecondary = topSuggestions.Length > 1
            ? topSuggestions[1]
            : null;
    }

    [RelayCommand]
    private async Task ConnectSuggestion(ServerProfile? suggestion)
    {
        if (suggestion is null) return;
        if (IsConnecting) return;

        var now = DateTime.UtcNow;
        if (now - _lastSuggestionConnectUtc < TimeSpan.FromMilliseconds(600))
            return;

        _lastSuggestionConnectUtc = now;

        var credentials = await _dialogService.ShowConnectDialogAsync("Connect", suggestion.Host, suggestion);
        if (credentials is null)
            return;

        var resolvedAuthOrder = AuthPreferenceOrder.Normalize(credentials.AuthPreferenceOrder, suggestion.UsePrivateKey);
        var resolvedPassword = credentials.Password ?? string.Empty;
        var resolvedKeyPassphrase = credentials.PrivateKeyPassphrase ?? string.Empty;
        var resolvedKeyPath = string.IsNullOrWhiteSpace(credentials.PrivateKeyPath)
            ? suggestion.PrivateKeyPath
            : credentials.PrivateKeyPath;
        var needsSavedPassword = string.IsNullOrEmpty(resolvedPassword) &&
                                 resolvedAuthOrder.Contains(AuthMethodPreference.Password);
        var needsSavedKeyPassphrase = string.IsNullOrEmpty(resolvedKeyPassphrase) &&
                                      resolvedAuthOrder.Contains(AuthMethodPreference.PrivateKey) &&
                                      !string.IsNullOrWhiteSpace(resolvedKeyPath);

        if (needsSavedPassword || needsSavedKeyPassphrase)
        {
            var verified = await _userVerificationService.VerifyForConnectionAsync(suggestion);
            if (verified)
            {
                var savedSecrets = await _profileManager.LoadCredentialsAsync(suggestion.Id) ?? new CredentialSecrets();
                if (string.IsNullOrEmpty(resolvedPassword))
                    resolvedPassword = savedSecrets.Password ?? string.Empty;

                if (string.IsNullOrEmpty(resolvedKeyPassphrase))
                    resolvedKeyPassphrase = savedSecrets.PrivateKeyPassphrase ?? string.Empty;
            }
        }

        if (!string.IsNullOrEmpty(credentials.Password) || !string.IsNullOrEmpty(credentials.PrivateKeyPassphrase))
        {
            var existingSecrets = await _profileManager.LoadCredentialsAsync(suggestion.Id) ?? new CredentialSecrets();
            var passwordToPersist =
                !string.IsNullOrEmpty(credentials.Password)
                    ? credentials.Password
                    : existingSecrets.Password ?? string.Empty;
            var keyPassphraseToPersist =
                !string.IsNullOrEmpty(credentials.PrivateKeyPassphrase)
                    ? credentials.PrivateKeyPassphrase
                    : existingSecrets.PrivateKeyPassphrase ?? string.Empty;
            await _profileManager.SaveCredentialsAsync(suggestion.Id, passwordToPersist, keyPassphraseToPersist);
        }

        suggestion.Username = credentials.Username;
        suggestion.AuthPreferenceOrder = new List<AuthMethodPreference>(resolvedAuthOrder);
        suggestion.UsePrivateKey = resolvedAuthOrder.Count > 0 &&
                                   resolvedAuthOrder[0] == AuthMethodPreference.PrivateKey;
        suggestion.PrivateKeyPath = resolvedKeyPath;
        suggestion.Password = string.Empty;
        suggestion.PrivateKeyPassphrase = string.Empty;

        var resolved =
            new ConnectInfo
            {
                Username = credentials.Username,
                AuthPreferenceOrder = resolvedAuthOrder,
                Password = resolvedPassword,
                PrivateKeyPath = resolvedKeyPath,
                PrivateKeyPassphrase = resolvedKeyPassphrase
            };

        ApplyConnectionSuggestion(suggestion, resolved);
        await Connect();
    }

    private void ApplyConnectionSuggestion(ServerProfile suggestion, ConnectInfo credentials)
    {
        Profile.Name = suggestion.Name;
        Profile.Host = suggestion.Host;
        Profile.Port = suggestion.Port;
        Profile.ConnectionTimeoutSeconds = suggestion.ConnectionTimeoutSeconds;
        Profile.KeepAliveIntervalSeconds = suggestion.KeepAliveIntervalSeconds;
        Profile.ReconnectStrategy = suggestion.ReconnectStrategy;
        Profile.ReconnectAttempts = suggestion.ReconnectAttempts;
        Profile.ReconnectDelaySeconds = suggestion.ReconnectDelaySeconds;
        Profile.Username = credentials.Username;
        Profile.AuthPreferenceOrder = AuthPreferenceOrder.Normalize(credentials.AuthPreferenceOrder, suggestion.UsePrivateKey);
        Profile.Password = credentials.Password;
        Profile.UsePrivateKey = Profile.AuthPreferenceOrder.Count > 0 &&
                                Profile.AuthPreferenceOrder[0] == AuthMethodPreference.PrivateKey;
        Profile.PrivateKeyPath = credentials.PrivateKeyPath;
        Profile.PrivateKeyPassphrase = credentials.PrivateKeyPassphrase;

        OnPropertyChanged(nameof(Profile));
        Header = GetDisplayName();
    }

    partial void OnSuggestedServerPrimaryChanged(ServerProfile? value)
    {
        OnPropertyChanged(nameof(HasSuggestedServerPrimary));
        OnPropertyChanged(nameof(HasSingleSuggestedServer));
        OnPropertyChanged(nameof(HasTwoSuggestedServers));
    }

    partial void OnSuggestedServerSecondaryChanged(ServerProfile? value)
    {
        OnPropertyChanged(nameof(HasSuggestedServerSecondary));
        OnPropertyChanged(nameof(HasSingleSuggestedServer));
        OnPropertyChanged(nameof(HasTwoSuggestedServers));
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void OpenExternalTerminal()
    {
        var now = DateTime.UtcNow;
        if (now - _lastExternalTerminalLaunchUtc < TimeSpan.FromMilliseconds(800))
            return;

        _lastExternalTerminalLaunchUtc = now;

        try
        {
            ExternalTerminalService.OpenSshSession(Profile);
            StatusMessage = "Opened terminal session";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Open external terminal failed", ex);
            StatusMessage = $"Terminal Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this);
    }

    [RelayCommand]
    public async Task Connect()
    {
        if (IsConnecting)
            return;

        var attemptedHost = Profile.Host;
        var attemptedUsername = Profile.Username;

        try
        {
            IsConnecting = true;
            StatusMessage = "Connecting...";
            await _sftpService.ConnectAsync(Profile);

            var startPath = "/";
            try
            {
                startPath = NormalizeRemotePath(await _sftpService.GetCurrentDirectoryAsync());
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"Could not determine remote home directory; falling back to '/'. {ex.Message}");
            }

            CurrentPath = startPath;
            IsConnected = true;
            Header = GetDisplayName(preferHostWhenDefaultName: true);
            StatusMessage = "Connected";
            await RefreshFileList();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Connect failed", ex);
            IsConnected = false;
            Profile.Host = attemptedHost;
            Profile.Username = attemptedUsername;
            OnPropertyChanged(nameof(Profile));
            ConnectFailureFocusRequestId++;
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private string GetDisplayName(bool preferHostWhenDefaultName = false)
    {
        var name = Profile.Name;
        if (preferHostWhenDefaultName && string.Equals(name, "New Server", StringComparison.OrdinalIgnoreCase))
            name = string.Empty;

        if (string.IsNullOrWhiteSpace(name))
            name = Profile.Host;

        return string.IsNullOrWhiteSpace(name) ? "New Server" : name;
    }

    private static string NormalizeRemotePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        return path.Trim();
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        CancelSearch();
        CancelTreeLoad();
        await _sftpService.DisconnectAsync();
        IsConnected = false;
        IsSearchInProgress = false;
        IsTreeLoading = false;
        _allFiles.Clear();
        _recursiveSearchResults.Clear();
        TreeItems.Clear();
        SelectedTreeNode = null;
        Files.Clear();
        StatusMessage = "Disconnected";
    }

    [RelayCommand]
    public async Task Navigate(string path)
    {
        if (!IsConnected) return;

        string newPath;
        if (path == "..")
        {
            var parent = System.IO.Path.GetDirectoryName(CurrentPath);
            newPath = string.IsNullOrEmpty(parent) ? "/" : parent;
            if (CurrentPath == "/" && path == "..") newPath = "/";
        }
        else if (path.StartsWith('/'))
        {
            newPath = path;
        }
        else
        {
            newPath = CurrentPath.EndsWith('/') ? CurrentPath + path : CurrentPath + "/" + path;
        }

        try
        {
            StatusMessage = "Listing directory...";
            var files = await _sftpService.ListDirectoryAsync(newPath);
            CurrentPath = newPath;
            _allFiles = MapEntriesToCurrentPath(files, newPath);
            await RefreshSearchResultsAsync(debounce: false);
            if (IsTreeView)
                RebuildTreeRootFromCurrentList();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Navigate failed", ex);
            StatusMessage = $"Navigation Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshFileList()
    {
        try
        {
            StatusMessage = "Listing directory...";
            _allFiles = MapEntriesToCurrentPath(await _sftpService.ListDirectoryAsync(CurrentPath), CurrentPath);
            await RefreshSearchResultsAsync(debounce: false);
            if (IsTreeView)
                RebuildTreeRootFromCurrentList();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Refresh file list failed", ex);
            StatusMessage = $"List Error: {ex.Message}";
        }
    }

    private async Task RefreshSearchResultsAsync(bool debounce)
    {
        CancelSearch();

        var query = SearchQuery.Trim();
        var useRecursiveSearch = IsConnected && SearchRecursive && !string.IsNullOrWhiteSpace(query);
        if (!useRecursiveSearch)
        {
            _recursiveSearchResults.Clear();
            IsSearchInProgress = false;
            ApplyFileFilter();
            return;
        }

        var revision = ++_searchRevision;
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        IsSearchInProgress = true;
        _recursiveSearchResults.Clear();
        ApplyFileFilter();

        try
        {
            if (debounce)
                await Task.Delay(RecursiveSearchDebounceMs, cts.Token);

            var results = await SearchRemoteTreeAsync(CurrentPath, query, cts.Token);
            if (cts.IsCancellationRequested || revision != _searchRevision)
                return;

            _recursiveSearchResults = results;
            ApplyFileFilter();
        }
        catch (OperationCanceledException)
        {
            // New search request superseded the previous one.
        }
        catch (Exception ex)
        {
            LoggingService.Error("Recursive search failed", ex);
            StatusMessage = $"Search Error: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                IsSearchInProgress = false;
                ApplyFileFilter();
            }

            cts.Dispose();
        }
    }

    private async Task<List<FileEntry>> SearchRemoteTreeAsync(string rootPath,
        string query,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = NormalizeRemoteAbsolutePath(rootPath);
        var pendingDirectories = new Queue<string>();
        var visitedDirectories = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<FileEntry>();
        pendingDirectories.Enqueue(normalizedRoot);

        var visitedCount = 0;
        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var directoryPath = pendingDirectories.Dequeue();
            if (!visitedDirectories.Add(directoryPath))
                continue;

            visitedCount++;
            if (visitedCount > RecursiveSearchDirectoryLimit)
                throw new InvalidOperationException($"Search stopped after {RecursiveSearchDirectoryLimit} directories.");

            if (visitedCount % 12 == 0)
                StatusMessage = $"Searching '{query}'... {visitedCount} folders scanned, {matches.Count} matches";

            List<FileEntry> entries;
            try
            {
                entries = await _sftpService.ListDirectoryAsync(directoryPath);
            }
            catch (Exception ex)
            {
                LoggingService.Warn($"Search skipped '{directoryPath}': {ex.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!ShowHiddenFiles && entry.IsHidden)
                    continue;

                var entryRemotePath = NormalizeRemoteAbsolutePath(CombineRemotePath(directoryPath, entry.Name));
                var relativePath = BuildRelativePath(normalizedRoot, entryRemotePath);
                var sourceText = string.IsNullOrEmpty(relativePath) ? entry.Name : relativePath;

                if (ContainsText(sourceText, query) || ContainsText(entry.Name, query))
                {
                    matches.Add(
                        new FileEntry
                        {
                            Name = sourceText,
                            RemotePath = entryRemotePath,
                            IsDirectory = entry.IsDirectory,
                            IsSymbolicLink = entry.IsSymbolicLink,
                            SymbolicLinkTarget = entry.SymbolicLinkTarget,
                            SymbolicLinkTargetIsDirectory = entry.SymbolicLinkTargetIsDirectory,
                            Size = entry.Size,
                            Permissions = entry.Permissions,
                            LastWriteTime = entry.LastWriteTime
                        });
                }

                if (entry.IsDirectory && !entry.IsSymbolicLink)
                    pendingDirectories.Enqueue(entryRemotePath);
            }
        }

        return matches
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ContainsText(string value,
        string query)
    {
        return value.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRelativePath(string rootPath,
        string fullPath)
    {
        var normalizedRoot = NormalizeRemoteAbsolutePath(rootPath);
        var normalizedFull = NormalizeRemoteAbsolutePath(fullPath);

        if (normalizedRoot == "/")
            return normalizedFull.TrimStart('/');

        var prefix = normalizedRoot + "/";
        if (normalizedFull.StartsWith(prefix, StringComparison.Ordinal))
            return normalizedFull[prefix.Length..];

        if (string.Equals(normalizedFull, normalizedRoot, StringComparison.Ordinal))
            return Path.GetFileName(normalizedFull);

        return normalizedFull.TrimStart('/');
    }

    private static bool IsTreeExplorerOption(string value)
    {
        return value.Contains("Tree", StringComparison.OrdinalIgnoreCase);
    }

    private void CancelSearch()
    {
        if (_searchCts is null)
            return;

        try
        {
            _searchCts.Cancel();
            _searchCts.Dispose();
        }
        catch
        {
            // Ignore cancellation races.
        }
        finally
        {
            _searchCts = null;
        }
    }

    private void CancelTreeLoad()
    {
        if (_treeLoadCts is null)
            return;

        try
        {
            _treeLoadCts.Cancel();
            _treeLoadCts.Dispose();
        }
        catch
        {
            // Ignore cancellation races.
        }
        finally
        {
            _treeLoadCts = null;
        }
    }

    private static List<FileEntry> MapEntriesToCurrentPath(IEnumerable<FileEntry> files,
        string basePath)
    {
        var normalizedBasePath = NormalizeRemoteAbsolutePath(basePath);
        return files
            .Select(
                file =>
                    new FileEntry
                    {
                        Name = file.Name,
                        RemotePath =
                            string.IsNullOrWhiteSpace(file.RemotePath)
                                ? CombineRemotePath(normalizedBasePath, file.Name)
                                : NormalizeRemoteAbsolutePath(file.RemotePath),
                        IsDirectory = file.IsDirectory,
                        IsSymbolicLink = file.IsSymbolicLink,
                        SymbolicLinkTarget = file.SymbolicLinkTarget,
                        SymbolicLinkTargetIsDirectory = file.SymbolicLinkTargetIsDirectory,
                        Size = file.Size,
                        Permissions = file.Permissions,
                        LastWriteTime = file.LastWriteTime
                    })
            .ToList();
    }

    private List<RemoteTreeNode> BuildTreeNodes(IEnumerable<FileEntry> entries,
        string parentPath)
    {
        return entries
            .Where(file => ShowHiddenFiles || !file.IsHidden)
            .OrderByDescending(file => file.IsDirectory)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(
                file =>
                {
                    var remotePath =
                        string.IsNullOrWhiteSpace(file.RemotePath)
                            ? CombineRemotePath(parentPath, file.Name)
                            : NormalizeRemoteAbsolutePath(file.RemotePath);
                    var mappedEntry =
                        new FileEntry
                        {
                            Name = file.Name,
                            RemotePath = remotePath,
                            IsDirectory = file.IsDirectory,
                            IsSymbolicLink = file.IsSymbolicLink,
                            SymbolicLinkTarget = file.SymbolicLinkTarget,
                            SymbolicLinkTargetIsDirectory = file.SymbolicLinkTargetIsDirectory,
                            Size = file.Size,
                            Permissions = file.Permissions,
                            LastWriteTime = file.LastWriteTime
                        };
                    return new RemoteTreeNode(mappedEntry);
                })
            .ToList();
    }

    private void RebuildTreeRootFromCurrentList()
    {
        CancelTreeLoad();
        _treeLoadCts = new CancellationTokenSource();
        IsTreeLoading = false;
        SelectedTreeNode = null;
        TreeItems.Clear();

        foreach (var node in BuildTreeNodes(_allFiles, CurrentPath))
            TreeItems.Add(node);
    }

    public async Task ExpandTreeNodeAsync(RemoteTreeNode? node)
    {
        if (!IsConnected || node is null || node.IsPlaceholder || !node.IsDirectory || node.IsSymbolicLink)
            return;
        if (node.IsLoaded || node.IsLoading)
            return;

        var token = _treeLoadCts?.Token ?? CancellationToken.None;
        node.IsLoading = true;
        IsTreeLoading = true;

        try
        {
            token.ThrowIfCancellationRequested();
            var entries = await _sftpService.ListDirectoryAsync(node.RemotePath);
            token.ThrowIfCancellationRequested();
            var mapped = MapEntriesToCurrentPath(entries, node.RemotePath);
            var children = BuildTreeNodes(mapped, node.RemotePath);
            node.Children.Clear();
            foreach (var child in children)
                node.Children.Add(child);
            node.IsLoaded = true;
        }
        catch (OperationCanceledException)
        {
            // Tree was refreshed/cancelled.
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Tree expand failed for '{node.RemotePath}': {ex.Message}");
            node.Children.Clear();
            node.IsLoaded = true;
        }
        finally
        {
            node.IsLoading = false;
            IsTreeLoading = false;
        }
    }

    private bool IsRecursiveSearchActive =>
        IsConnected && SearchRecursive && !string.IsNullOrWhiteSpace(SearchQuery);

    private bool IsFlatFilterActive =>
        !string.IsNullOrWhiteSpace(SearchQuery) && !SearchRecursive;

    private void ApplyFileFilter()
    {
        Files.Clear();

        var source = IsRecursiveSearchActive ? _recursiveSearchResults : _allFiles;
        IEnumerable<FileEntry> filtered = source.Where(file => ShowHiddenFiles || !file.IsHidden);

        if (IsFlatFilterActive)
        {
            var query = SearchQuery.Trim();
            filtered =
                filtered.Where(
                    file =>
                        ContainsText(file.Name, query) ||
                        ContainsText(file.RemotePath, query));
        }

        if (!IsRecursiveSearchActive && CurrentPath != "/")
        {
            Files.Add(new FileEntry { Name = "..", IsDirectory = true });
        }

        foreach (var file in filtered)
        {
            Files.Add(file);
        }

        var visibleCount = Files.Count(entry => entry.Name != "..");
        if (IsRecursiveSearchActive)
        {
            StatusMessage =
                IsSearchInProgress
                    ? $"Searching '{SearchQuery}'... {visibleCount} matches so far"
                    : $"Recursive search '{SearchQuery}': {visibleCount} matches";
            return;
        }

        if (IsFlatFilterActive)
        {
            StatusMessage = $"Filtered '{SearchQuery}': {visibleCount} matches";
            return;
        }

        StatusMessage = $"Showing {visibleCount} items";
    }

    private string GetEntryRemotePath(FileEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.RemotePath))
            return NormalizeRemoteAbsolutePath(entry.RemotePath);

        return CombineRemotePath(CurrentPath, entry.Name);
    }

    public async Task OpenItem(FileEntry entry)
    {
        if (entry.IsSymbolicLink)
        {
            var entryPath = GetEntryRemotePath(entry);
            var decision = await _dialogService.ConfirmSymlinkBehaviorAsync(
                "Open symbolic link",
                $"'{entry.Name}' is a symbolic link. Choose how to open it.",
                BuildSymlinkActionDetails(entry, entryPath),
                allowApplyToAll: false);

            if (decision.Choice == SymlinkBehaviorChoice.Cancel)
                return;

            if (decision.Choice == SymlinkBehaviorChoice.OperateOnLinkEntry)
            {
                StatusMessage = $"Selected symbolic link: {entryPath}";
                return;
            }

            var followPath = await ResolveSymbolicLinkFollowPathAsync(entryPath, CancellationToken.None);
            var followInfo = await _sftpService.GetEntryInfoAsync(followPath);
            if (followInfo.IsDirectory)
            {
                await Navigate(followPath);
            }
            else
            {
                StatusMessage = $"Link target file: {followPath}";
            }

            return;
        }

        if (entry.IsDirectory)
        {
            var targetPath = entry.Name == ".." ? ".." : GetEntryRemotePath(entry);
            await Navigate(targetPath);
        }
        else
        {
            StatusMessage = $"Selected file: {GetEntryRemotePath(entry)}";
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
    }

    [RelayCommand]
    private async Task CreateFolder()
    {
        if (!IsConnected) return;
        var name = await _dialogService.PromptAsync("New Folder", "Enter folder name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var path = CurrentPath.EndsWith('/') ? CurrentPath + name : $"{CurrentPath}/{name}";
            if (await _sftpService.PathExistsAsync(path))
            {
                StatusMessage = $"Folder already exists: {name}";
                return;
            }

            await _sftpService.CreateDirectoryAsync(path);
            await RefreshFileList();
            StatusMessage = $"Created folder: {name}";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Create folder failed", ex);
            StatusMessage = $"Error creating folder: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CreateFile()
    {
        if (!IsConnected) return;
        var name = await _dialogService.PromptAsync("New File", "Enter file name:");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var path = CurrentPath.EndsWith('/') ? CurrentPath + name : $"{CurrentPath}/{name}";
            if (await _sftpService.PathExistsAsync(path))
            {
                StatusMessage = $"File already exists: {name}";
                return;
            }

            await _sftpService.CreateFileAsync(path);
            await RefreshFileList();
            StatusMessage = $"Created file: {name}";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Create file failed", ex);
            StatusMessage = $"Error creating file: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Rename(FileEntry? file)
    {
        var target = file ?? SelectedFile;
        if (!IsConnected || target is null || target.Name == "..") return;

        var newName = await _dialogService.PromptAsync("Rename", "Enter new name:", target.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName == target.Name) return;

        try
        {
            var oldPath = GetEntryRemotePath(target);
            var parentPath = GetRemoteParentPath(oldPath);
            var newPath = CombineRemotePath(parentPath, newName);

            await _sftpService.RenameFileAsync(oldPath, newPath);
            await RefreshFileList();
            StatusMessage = $"Renamed to: {newName}";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Rename failed", ex);
            StatusMessage = $"Rename Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ShowProperties(FileEntry? file)
    {
        if (!IsConnected) return;

        var target = file ?? SelectedFile ?? SelectedTreeNode?.Entry;
        if (target is null || target.Name == "..")
            return;

        var targetPath = GetEntryRemotePath(target);
        try
        {
            var current = await _sftpService.GetPosixAttributesAsync(targetPath);
            var request =
                new RemotePropertiesDialogRequest
                {
                    Name = target.Name,
                    RemotePath = targetPath,
                    IsDirectory = current.IsDirectory,
                    Size = current.Size,
                    LastWriteTime = current.LastWriteTime,
                    SymbolicPermissions = current.SymbolicPermissions,
                    OctalPermissions = current.OctalPermissions,
                    UserId = current.UserId,
                    OwnerName = current.OwnerName,
                    GroupId = current.GroupId,
                    GroupName = current.GroupName
                };

            var result = await _dialogService.ShowRemotePropertiesDialogAsync(request);
            if (result is null)
                return;

            if (!result.ApplyPermissions && !result.ApplyOwnerId && !result.ApplyGroupId)
            {
                StatusMessage = "No property changes selected";
                return;
            }

            short? applyPermissions = result.ApplyPermissions ? result.PermissionMode : null;
            int? applyOwner = result.ApplyOwnerId ? result.OwnerId : null;
            int? applyGroup = result.ApplyGroupId ? result.GroupId : null;
            var applyRecursive = result.ApplyRecursively && current.IsDirectory;

            var targets =
                await BuildAttributeApplyTargetsAsync(
                    targetPath,
                    applyRecursive,
                    CancellationToken.None);
            if (targets.Count == 0)
            {
                StatusMessage = "Nothing to update";
                return;
            }

            var orderedTargets =
                applyRecursive
                    ? targets
                        .OrderBy(t => t.IsDirectory ? 1 : 0)
                        .ThenByDescending(t => GetRemotePathDepth(t.RemotePath))
                        .ToList()
                    : targets;

            for (var i = 0; i < orderedTargets.Count; i++)
            {
                var applyTarget = orderedTargets[i];
                StatusMessage = $"Applying properties {i + 1}/{orderedTargets.Count}: {applyTarget.DisplayName}";
                await _sftpService.SetPosixAttributesAsync(
                    applyTarget.RemotePath,
                    applyPermissions,
                    applyOwner,
                    applyGroup);
            }

            await RefreshFileList();
            StatusMessage =
                $"Updated properties for {orderedTargets.Count} item{(orderedTargets.Count == 1 ? "" : "s")}";
        }
        catch (Exception ex)
        {
            LoggingService.Error("Update properties failed", ex);
            StatusMessage = $"Properties Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Delete(FileEntry? file)
    {
        if (!IsConnected) return;
        var rawTargets = GetSelectedTargets(file);
        if (rawTargets.Count is 0) return;
        var targets = FilterDeleteTargets(rawTargets);
        if (targets.Count is 0) return;

        var confirm =
            targets.Count is 1
                ? await _dialogService.ConfirmAsync("Delete", $"Are you sure you want to delete '{targets[0].Name}'?")
                : await _dialogService.ConfirmAsync("Delete",
                    $"Are you sure you want to delete {targets.Count} items?");
        if (!confirm) return;

        var symlinkChoices = await ResolveDeleteSymlinkChoicesAsync(targets);
        if (symlinkChoices.Cancelled)
        {
            StatusMessage = "Delete cancelled";
            return;
        }

        var request =
            new DeleteJobRequest
            {
                BaseRemotePath = CurrentPath,
                Targets =
                    targets.Select(t =>
                    {
                        var remotePath = GetEntryRemotePath(t);
                        var followTarget =
                            symlinkChoices.FollowTargetByPath.TryGetValue(remotePath, out var followChoice) &&
                            followChoice;

                        return new DeleteTargetSnapshot
                        {
                            Name = t.Name,
                            RemotePath = remotePath,
                            IsDirectory = t.IsDirectory,
                            IsSymbolicLink = t.IsSymbolicLink,
                            FollowSymlinkTarget = followTarget
                        };
                    }).ToList()
            };

        EnqueueTransfer(CreateDeleteTransferDefinition(request));
    }

    [RelayCommand]
    private void Copy(FileEntry? file)
    {
        var targets = GetSelectedTargets(file);
        if (targets.Count is 0) return;

        _clipboardItems =
            targets.Select(t =>
                new ClipboardItem
                {
                    Name = t.Name,
                    Path = GetEntryRemotePath(t),
                    IsDirectory = t.IsDirectory,
                    IsSymbolicLink = t.IsSymbolicLink,
                    SymbolicLinkTarget = t.SymbolicLinkTarget,
                    SymbolicLinkTargetIsDirectory = t.SymbolicLinkTargetIsDirectory,
                    Size = t.Size,
                    LastWriteTime = t.LastWriteTime
                }).ToList();
        _clipboardIsCut = false;
        LoggingService.Info($"Copy set: {_clipboardItems.Count} item(s)");
        StatusMessage = _clipboardItems.Count == 1 ? $"Copied: {targets[0].Name}" : $"Copied {targets.Count} items";
    }

    private List<FileEntry> FilterDeleteTargets(IReadOnlyList<FileEntry> targets)
    {
        if (targets.Count <= 1)
            return targets.ToList();

        var uniqueTargetsByPath = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            var targetPath = GetEntryRemotePath(target);
            if (!uniqueTargetsByPath.ContainsKey(targetPath))
                uniqueTargetsByPath[targetPath] = target;
        }

        var directoryRoots = uniqueTargetsByPath
            .Where(kvp => kvp.Value.IsDirectory && !kvp.Value.IsSymbolicLink)
            .Select(kvp => kvp.Key)
            .ToList();

        var filtered = new List<FileEntry>();
        foreach (var (path, target) in uniqueTargetsByPath)
        {
            var isNestedUnderSelectedDirectory =
                directoryRoots.Any(root =>
                    !path.Equals(root, StringComparison.Ordinal) &&
                    path.StartsWith(root + "/", StringComparison.Ordinal));

            if (!isNestedUnderSelectedDirectory)
                filtered.Add(target);
        }

        return filtered;
    }

    private async Task<(bool Cancelled, Dictionary<string, bool> FollowTargetByPath)> ResolveDeleteSymlinkChoicesAsync(
        IReadOnlyList<FileEntry> targets)
    {
        var symlinkTargets = targets.Where(target => target.IsSymbolicLink).ToList();
        var followTargetByPath = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (symlinkTargets.Count == 0)
            return (false, followTargetByPath);

        SymlinkBehaviorChoice? applyChoiceToAll = null;

        foreach (var symlinkTarget in symlinkTargets)
        {
            var path = GetEntryRemotePath(symlinkTarget);
            SymlinkBehaviorChoice choice;
            if (applyChoiceToAll is { } allChoice)
            {
                choice = allChoice;
            }
            else
            {
                var details = BuildSymlinkActionDetails(symlinkTarget, path);
                var decision = await _dialogService.ConfirmSymlinkBehaviorAsync(
                    "Delete symbolic link",
                    $"'{symlinkTarget.Name}' is a symbolic link. Choose delete behavior.",
                    details,
                    allowApplyToAll: symlinkTargets.Count > 1);
                choice = decision.Choice;

                if (decision.ApplyToAll && choice != SymlinkBehaviorChoice.Cancel)
                    applyChoiceToAll = choice;
            }

            if (choice == SymlinkBehaviorChoice.Cancel)
                return (true, followTargetByPath);

            followTargetByPath[path] = choice == SymlinkBehaviorChoice.FollowLinkTarget;
        }

        return (false, followTargetByPath);
    }

    private static string BuildSymlinkActionDetails(FileEntry symlinkEntry,
        string remotePath)
    {
        var targetText = string.IsNullOrWhiteSpace(symlinkEntry.SymbolicLinkTarget)
            ? "Target: unresolved"
            : $"Target: {symlinkEntry.SymbolicLinkTarget}";
        var targetType = symlinkEntry.SymbolicLinkTargetIsDirectory ? "directory" : "file/unknown";
        return
            $"Path: {remotePath}{Environment.NewLine}{targetText}{Environment.NewLine}Follow target type: {targetType}";
    }

    private static string BuildSymlinkActionDetails(ClipboardItem symlinkItem,
        string remotePath)
    {
        var targetText = string.IsNullOrWhiteSpace(symlinkItem.SymbolicLinkTarget)
            ? "Target: unresolved"
            : $"Target: {symlinkItem.SymbolicLinkTarget}";
        var targetType = symlinkItem.SymbolicLinkTargetIsDirectory ? "directory" : "file/unknown";
        return
            $"Path: {remotePath}{Environment.NewLine}{targetText}{Environment.NewLine}Follow target type: {targetType}";
    }

    private async Task<string> EnsureClipboardSymlinkTargetAsync(ClipboardItem symlinkItem)
    {
        if (!string.IsNullOrWhiteSpace(symlinkItem.SymbolicLinkTarget))
            return symlinkItem.SymbolicLinkTarget;

        var target = await _sftpService.GetSymbolicLinkTargetAsync(symlinkItem.Path);
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException($"Unable to resolve symbolic link target for '{symlinkItem.Path}'.");

        symlinkItem.SymbolicLinkTarget = target;
        return target;
    }

    private async Task<bool> ResolveClipboardSymlinkTargetIsDirectoryAsync(ClipboardItem symlinkItem)
    {
        if (symlinkItem.SymbolicLinkTargetIsDirectory)
            return true;

        var targetInfo = await _sftpService.GetEntryInfoAsync(symlinkItem.Path);
        symlinkItem.SymbolicLinkTargetIsDirectory = targetInfo.IsDirectory;
        return targetInfo.IsDirectory;
    }

    private async Task<string> ResolveSymbolicLinkFollowPathAsync(string symbolicLinkPath,
        CancellationToken cancellationToken)
    {
        var rawTargetPath = await _sftpService.GetSymbolicLinkTargetAsync(symbolicLinkPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(rawTargetPath))
            throw new InvalidOperationException($"Unable to resolve symbolic link target for '{symbolicLinkPath}'.");

        var normalizedRawTarget = rawTargetPath.Trim();
        if (normalizedRawTarget.StartsWith('/'))
            return NormalizeRemotePathWithDotSegments(normalizedRawTarget);

        var parentPath = GetRemoteParentPath(symbolicLinkPath);
        return NormalizeRemotePathWithDotSegments(CombineRemotePath(parentPath, normalizedRawTarget));
    }

    private async Task<List<DeletePlanItem>> BuildDeletePlanAsync(IReadOnlyList<DeleteTargetSnapshot> targets,
        string basePath,
        CancellationToken cancellationToken)
    {
        var plan = new List<DeletePlanItem>();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetPath = NormalizeRemoteAbsolutePath(target.RemotePath);
            if (target.IsSymbolicLink)
            {
                if (!target.FollowSymlinkTarget)
                {
                    plan.Add(
                        new DeletePlanItem
                        {
                            RemotePath = targetPath,
                            DisplayName = $"{BuildDeleteDisplayName(targetPath, basePath)} (link)",
                            IsDirectory = false
                        });
                    continue;
                }

                var followPath = await ResolveSymbolicLinkFollowPathAsync(targetPath, cancellationToken);
                var followInfo = await _sftpService.GetEntryInfoAsync(followPath);
                if (followInfo.IsDirectory)
                {
                    await AppendDirectoryDeletePlanAsync(followPath, basePath, plan, cancellationToken);
                }
                else
                {
                    plan.Add(
                        new DeletePlanItem
                        {
                            RemotePath = followPath,
                            DisplayName = $"{BuildDeleteDisplayName(followPath, basePath)} (via link)",
                            IsDirectory = false
                        });
                }

                continue;
            }

            if (target.IsDirectory)
                await AppendDirectoryDeletePlanAsync(targetPath, basePath, plan, cancellationToken);
            else
                plan.Add(
                    new DeletePlanItem
                    {
                        RemotePath = targetPath,
                        DisplayName = BuildDeleteDisplayName(targetPath, basePath),
                        IsDirectory = false
                    });
        }

        return plan;
    }

    private async Task AppendDirectoryDeletePlanAsync(string directoryPath,
        string basePath,
        ICollection<DeletePlanItem> plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _sftpService.ListDirectoryAsync(directoryPath);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryPath = NormalizeRemoteAbsolutePath(CombineRemotePath(directoryPath, entry.Name));
            if (entry.IsDirectory && !entry.IsSymbolicLink)
            {
                await AppendDirectoryDeletePlanAsync(entryPath, basePath, plan, cancellationToken);
            }
            else
            {
                plan.Add(
                    new DeletePlanItem
                    {
                        RemotePath = entryPath,
                        DisplayName =
                            entry.IsSymbolicLink
                                ? $"{BuildDeleteDisplayName(entryPath, basePath)} (link)"
                                : BuildDeleteDisplayName(entryPath, basePath),
                        IsDirectory = false
                    });
            }
        }

        plan.Add(
            new DeletePlanItem
            {
                RemotePath = directoryPath,
                DisplayName = $"{BuildDeleteDisplayName(directoryPath, basePath)}/",
                IsDirectory = true
            });
    }

    private static string BuildDeleteDisplayName(string remotePath,
        string basePath)
    {
        var normalizedCurrentPath = NormalizeRemoteAbsolutePath(basePath);
        var normalizedRemotePath = NormalizeRemoteAbsolutePath(remotePath);

        if (normalizedCurrentPath == "/")
            return normalizedRemotePath.TrimStart('/');

        var prefix = normalizedCurrentPath + "/";
        if (normalizedRemotePath.StartsWith(prefix, StringComparison.Ordinal))
            return normalizedRemotePath[prefix.Length..];

        return normalizedRemotePath;
    }

    private async Task<List<AttributeApplyTarget>> BuildAttributeApplyTargetsAsync(string rootPath,
        bool recursive,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRoot = NormalizeRemoteAbsolutePath(rootPath);
        var rootInfo = await _sftpService.GetEntryInfoAsync(normalizedRoot);
        var basePath = GetRemoteParentPath(normalizedRoot);
        var targets =
            new List<AttributeApplyTarget>
            {
                new()
                {
                    RemotePath = normalizedRoot,
                    DisplayName = BuildDeleteDisplayName(normalizedRoot, basePath),
                    IsDirectory = rootInfo.IsDirectory
                }
            };

        if (!recursive || !rootInfo.IsDirectory)
            return targets;

        await AppendAttributeTargetsRecursiveAsync(normalizedRoot, basePath, targets, cancellationToken);
        return targets;
    }

    private async Task AppendAttributeTargetsRecursiveAsync(string directoryPath,
        string basePath,
        ICollection<AttributeApplyTarget> targets,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entries = await _sftpService.ListDirectoryAsync(directoryPath);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryPath = NormalizeRemoteAbsolutePath(CombineRemotePath(directoryPath, entry.Name));
            targets.Add(
                new AttributeApplyTarget
                {
                    RemotePath = entryPath,
                    DisplayName = BuildDeleteDisplayName(entryPath, basePath),
                    IsDirectory = entry.IsDirectory
                });

            if (entry.IsDirectory && !entry.IsSymbolicLink)
                await AppendAttributeTargetsRecursiveAsync(entryPath, basePath, targets, cancellationToken);
        }
    }

    private static DeleteJobRequest CloneDeleteJobRequest(DeleteJobRequest request)
    {
        return new DeleteJobRequest
        {
            BaseRemotePath = request.BaseRemotePath,
            Targets =
                request.Targets
                    .Select(t =>
                        new DeleteTargetSnapshot
                        {
                            Name = t.Name,
                            RemotePath = t.RemotePath,
                            IsDirectory = t.IsDirectory,
                            IsSymbolicLink = t.IsSymbolicLink,
                            FollowSymlinkTarget = t.FollowSymlinkTarget
                        })
                    .ToList()
        };
    }

    private static UploadJobRequest CloneUploadJobRequest(UploadJobRequest request)
    {
        return new UploadJobRequest
        {
            DestinationPath = request.DestinationPath,
            LocalPaths = request.LocalPaths.ToList()
        };
    }

    private static DownloadJobRequest CloneDownloadJobRequest(DownloadJobRequest request)
    {
        return new DownloadJobRequest
        {
            Name = request.Name,
            IsDirectory = request.IsDirectory,
            Size = request.Size,
            LastWriteTime = request.LastWriteTime,
            RemotePath = request.RemotePath,
            LocalFolder = request.LocalFolder,
            ResolvedLocalRootPath = request.ResolvedLocalRootPath
        };
    }

    private TransferWorkDefinition CreateDeleteTransferDefinition(DeleteJobRequest request)
    {
        var snapshot = CloneDeleteJobRequest(request);
        var title =
            snapshot.Targets.Count == 1
                ? $"Delete {snapshot.Targets[0].Name}"
                : $"Delete {snapshot.Targets.Count} items";

        return new TransferWorkDefinition
        {
            Type = TransferJobType.Delete,
            Title = title,
            ExecuteAsync = (job, token) => ExecuteDeleteJobAsync(job, snapshot, token),
            RetryFactory = () => CreateDeleteTransferDefinition(CloneDeleteJobRequest(snapshot))
        };
    }

    private TransferWorkDefinition CreateUploadTransferDefinition(UploadJobRequest request)
    {
        var snapshot = CloneUploadJobRequest(request);
        var title =
            snapshot.LocalPaths.Count == 1
                ? $"Upload {Path.GetFileName(snapshot.LocalPaths[0])}"
                : $"Upload {snapshot.LocalPaths.Count} items";

        return new TransferWorkDefinition
        {
            Type = TransferJobType.Upload,
            Title = title,
            ExecuteAsync = (job, token) => ExecuteUploadJobAsync(job, snapshot, token),
            RetryFactory = () => CreateUploadTransferDefinition(CloneUploadJobRequest(snapshot))
        };
    }

    private TransferWorkDefinition CreateDownloadTransferDefinition(DownloadJobRequest request)
    {
        var snapshot = CloneDownloadJobRequest(request);
        var title = $"Download {snapshot.Name}";

        return new TransferWorkDefinition
        {
            Type = TransferJobType.Download,
            Title = title,
            ExecuteAsync = (job, token) => ExecuteDownloadJobAsync(job, snapshot, token),
            RetryFactory = () => CreateDownloadTransferDefinition(CloneDownloadJobRequest(snapshot))
        };
    }

    private void UpdateTransferUi(TransferJob job,
        string title,
        string status,
        double? progress = null)
    {
        TransferTitle = title;
        StatusMessage = status;
        job.Status = status;

        if (!progress.HasValue)
            return;

        var clamped = Math.Clamp(progress.Value, 0, 100);
        TransferProgress = clamped;
        job.Progress = clamped;
    }

    private async Task TryRefreshFileListAfterTransferAsync(string operation)
    {
        if (!IsConnected)
            return;

        try
        {
            await RefreshFileList();
        }
        catch (Exception ex)
        {
            LoggingService.Warn($"Could not refresh file list after {operation}: {ex.Message}");
        }
    }

    private async Task ExecuteDeleteJobAsync(TransferJob job,
        DeleteJobRequest request,
        CancellationToken cancellationToken)
    {
        var shouldRefresh = false;
        try
        {
            UpdateTransferUi(job, "Preparing delete plan...", "Preparing delete plan...", 0);
            var deletePlan = await BuildDeletePlanAsync(request.Targets, request.BaseRemotePath, cancellationToken);
            if (deletePlan.Count == 0)
            {
                UpdateTransferUi(job, "Nothing to delete", "Nothing to delete", 100);
                return;
            }

            shouldRefresh = true;
            var totalSteps = deletePlan.Count;
            var completedSteps = 0;

            foreach (var step in deletePlan)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stepNumber = completedSteps + 1;
                var title = $"Deleting {stepNumber}/{totalSteps}: {step.DisplayName}";
                UpdateTransferUi(job, title, title, completedSteps * 100.0 / totalSteps);

                if (step.IsDirectory)
                    await _sftpService.DeleteDirectoryAsync(step.RemotePath);
                else
                    await _sftpService.DeleteFileAsync(step.RemotePath);

                completedSteps++;
                UpdateTransferUi(job, title, title, completedSteps * 100.0 / totalSteps);
            }

            var doneText =
                request.Targets.Count == 1
                    ? $"Deleted: {request.Targets[0].Name}"
                    : $"Deleted {request.Targets.Count} items";
            UpdateTransferUi(job, doneText, doneText, 100);
        }
        finally
        {
            if (shouldRefresh)
                await TryRefreshFileListAfterTransferAsync("delete");
        }
    }

    private async Task ExecuteDownloadJobAsync(TransferJob job,
        DownloadJobRequest request,
        CancellationToken cancellationToken)
    {
        var shouldCleanupCurrentPartialFile = false;
        string? currentLocalPath = null;

        try
        {
            UpdateTransferUi(job, $"Preparing download: {request.Name}", $"Preparing download: {request.Name}", 0);

            var sourceInfo = (request.IsDirectory, request.Size, request.LastWriteTime);
            string resolvedLocalRoot;
            if (!string.IsNullOrWhiteSpace(request.ResolvedLocalRootPath))
            {
                resolvedLocalRoot = request.ResolvedLocalRootPath;
            }
            else
            {
                var localRootPath = Path.Combine(request.LocalFolder, request.Name);
                var resolved = await ResolveLocalConflictAsync(request.Name, sourceInfo, localRootPath);
                if (resolved is null)
                    throw new OperationCanceledException();

                request.ResolvedLocalRootPath = resolved;
                resolvedLocalRoot = resolved;
            }

            var rootEntry =
                new FileEntry
                {
                    Name = request.Name,
                    IsDirectory = request.IsDirectory,
                    Size = request.Size,
                    LastWriteTime = request.LastWriteTime
                };

            var plan = await BuildDownloadPlanAsync(rootEntry, request.RemotePath, resolvedLocalRoot, cancellationToken);
            if (plan.Files.Count == 0 && plan.Directories.Count == 0)
            {
                UpdateTransferUi(job, "Nothing to download", "Nothing to download", 100);
                return;
            }

            foreach (var directory in plan.Directories.OrderBy(GetLocalPathDepth))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
            }

            var totalFiles = plan.Files.Count;
            var totalBytes = plan.Files.Sum(item => Math.Max(0, item.Size));
            var shouldVerify = VerifyTransfersAfterTransfer;
            var hashVerifiedFiles = 0;
            var fallbackVerifiedFiles = 0;
            long downloadedBytesBeforeCurrent = 0;
            var downloadedFiles = 0;

            foreach (var item in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileSize = Math.Max(0, item.Size);
                var fileNumber = downloadedFiles + 1;
                var title = $"Downloading {fileNumber}/{totalFiles}: {item.DisplayName}";
                UpdateTransferUi(job, title, title);

                Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
                currentLocalPath = item.LocalPath;
                shouldCleanupCurrentPartialFile = true;

                var progress =
                    new Progress<double>(p =>
                    {
                        var filePercent = Math.Clamp(p, 0, 100);
                        double overallPercent;

                        if (totalBytes > 0)
                        {
                            var downloadedInCurrentFile = (long)Math.Round(fileSize * filePercent / 100.0);
                            overallPercent =
                                (downloadedBytesBeforeCurrent + downloadedInCurrentFile) * 100.0 / totalBytes;
                        }
                        else
                        {
                            overallPercent = (downloadedFiles + filePercent / 100.0) * 100.0 / Math.Max(totalFiles, 1);
                        }

                        UpdateTransferUi(
                            job,
                            title,
                            $"Downloading {item.DisplayName}: {filePercent:F0}% ({overallPercent:F0}% total)",
                            overallPercent);
                    });

                await DownloadFileWithRetriesAsync(job, item, progress, cancellationToken);

                shouldCleanupCurrentPartialFile = false;
                currentLocalPath = null;
                downloadedBytesBeforeCurrent += fileSize;
                downloadedFiles++;

                var progressValue =
                    totalBytes > 0
                        ? downloadedBytesBeforeCurrent * 100.0 / totalBytes
                        : downloadedFiles * 100.0 / Math.Max(totalFiles, 1);
                UpdateTransferUi(job, title, job.Status, progressValue);

                if (shouldVerify)
                {
                    var verificationMode =
                        await VerifyDownloadedFileAsync(job, item, fileNumber, totalFiles, progressValue, cancellationToken);
                    if (verificationMode == VerificationMode.Hash)
                        hashVerifiedFiles++;
                    else
                        fallbackVerifiedFiles++;
                }
            }

            var doneText =
                request.IsDirectory
                    ? $"Download complete ({downloadedFiles} file{(downloadedFiles == 1 ? "" : "s")})"
                    : $"Downloaded: {Path.GetFileName(resolvedLocalRoot)}";
            if (shouldVerify && downloadedFiles > 0)
                doneText = $"{doneText}, {BuildVerificationSummary(hashVerifiedFiles, fallbackVerifiedFiles)}";
            UpdateTransferUi(job, doneText, doneText, 100);
        }
        catch (OperationCanceledException)
        {
            if (shouldCleanupCurrentPartialFile && !string.IsNullOrWhiteSpace(currentLocalPath))
                TryDeleteLocalPath(currentLocalPath);
            throw;
        }
    }

    private async Task ExecuteUploadJobAsync(TransferJob job,
        UploadJobRequest request,
        CancellationToken cancellationToken)
    {
        var shouldRefreshFileList = false;
        try
        {
            UpdateTransferUi(job, "Checking upload paths", "Resolving upload sources...", 0);

            var sourceTargets =
                await ResolveUploadTargetsAsync(request.LocalPaths, request.DestinationPath, cancellationToken);
            if (sourceTargets is null)
                throw new OperationCanceledException();

            var plan = BuildUploadPlan(sourceTargets, cancellationToken);
            var totalFiles = plan.Files.Count;
            var totalDirectories = plan.Directories.Count;

            if (totalFiles == 0 && totalDirectories == 0)
            {
                UpdateTransferUi(job, "Nothing to upload", "Nothing to upload", 100);
                return;
            }

            shouldRefreshFileList = true;

            UpdateTransferUi(job, "Checking upload conflicts", "Scanning destination...", 0);
            var shouldContinue = await ResolveUploadFileConflictsAsync(job, plan, cancellationToken);
            if (!shouldContinue)
                throw new OperationCanceledException();

            var preparedDirectories = 0;
            foreach (var remoteDirectory in plan.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                preparedDirectories++;
                var title = $"Preparing folder {preparedDirectories}/{totalDirectories}";
                var status = $"Preparing {remoteDirectory}";
                UpdateTransferUi(
                    job,
                    title,
                    status,
                    totalFiles == 0 && totalDirectories > 0 ? (preparedDirectories - 1) * 100.0 / totalDirectories : null);

                var exists = await _sftpService.PathExistsAsync(remoteDirectory);
                if (!exists)
                {
                    await _sftpService.CreateDirectoryAsync(remoteDirectory);
                }
                else if (!await _sftpService.IsDirectoryAsync(remoteDirectory))
                {
                    throw new InvalidOperationException(
                        $"Cannot create directory '{remoteDirectory}' because a file already exists at that path.");
                }

                if (totalFiles == 0 && totalDirectories > 0)
                    UpdateTransferUi(job, title, status, preparedDirectories * 100.0 / totalDirectories);
            }

            var totalBytes = plan.Files.Sum(f => Math.Max(0, f.Size));
            var shouldVerify = VerifyTransfersAfterTransfer;
            var hashVerifiedFiles = 0;
            var fallbackVerifiedFiles = 0;
            long uploadedBytesBeforeCurrent = 0;
            var uploadedFiles = 0;

            foreach (var item in plan.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileSize = Math.Max(0, item.Size);
                var fileNumber = uploadedFiles + 1;
                var title = $"Uploading {fileNumber}/{totalFiles}: {item.DisplayName}";
                UpdateTransferUi(job, title, title);
                var uploadRemotePath = item.AtomicTempRemotePath ?? item.RemotePath;

                var progress =
                    new Progress<double>(p =>
                    {
                        var filePercent = Math.Clamp(p, 0, 100);
                        double overallPercent;

                        if (totalBytes > 0)
                        {
                            var uploadedInCurrentFile = (long)Math.Round(fileSize * filePercent / 100.0);
                            overallPercent = (uploadedBytesBeforeCurrent + uploadedInCurrentFile) * 100.0 / totalBytes;
                        }
                        else
                        {
                            overallPercent = (uploadedFiles + filePercent / 100.0) * 100.0 / Math.Max(totalFiles, 1);
                        }

                        UpdateTransferUi(
                            job,
                            title,
                            $"Uploading {item.DisplayName}: {filePercent:F0}% ({overallPercent:F0}% total)",
                            overallPercent);
                    });

                try
                {
                    await UploadFileWithRetriesAsync(job, item, uploadRemotePath, progress, cancellationToken);

                    uploadedBytesBeforeCurrent += fileSize;
                    uploadedFiles++;
                    var overall =
                        totalBytes > 0
                            ? uploadedBytesBeforeCurrent * 100.0 / totalBytes
                            : uploadedFiles * 100.0 / Math.Max(totalFiles, 1);
                    UpdateTransferUi(job, title, job.Status, overall);

                    if (shouldVerify)
                    {
                        var verificationMode =
                            await VerifyUploadedFileAsync(
                                job,
                                item,
                                uploadRemotePath,
                                fileNumber,
                                totalFiles,
                                overall,
                                cancellationToken);
                        if (verificationMode == VerificationMode.Hash)
                            hashVerifiedFiles++;
                        else
                            fallbackVerifiedFiles++;
                    }

                    if (!string.IsNullOrWhiteSpace(item.AtomicTempRemotePath))
                    {
                        await FinalizeAtomicUploadAsync(job, item, fileNumber, totalFiles, overall, cancellationToken);
                    }
                }
                catch
                {
                    if (!string.IsNullOrWhiteSpace(item.AtomicTempRemotePath))
                        await TryDeleteRemoteFileQuietAsync(item.AtomicTempRemotePath);
                    throw;
                }
            }

            var doneText =
                $"Upload complete ({uploadedFiles} file{(uploadedFiles == 1 ? "" : "s")}, {totalDirectories} folder{(totalDirectories == 1 ? "" : "s")})";
            if (shouldVerify && uploadedFiles > 0)
                doneText = $"{doneText}, {BuildVerificationSummary(hashVerifiedFiles, fallbackVerifiedFiles)}";
            UpdateTransferUi(job, "Upload complete", doneText, 100);
        }
        finally
        {
            if (shouldRefreshFileList)
                await TryRefreshFileListAfterTransferAsync("upload");
        }
    }

    private async Task UploadFileWithRetriesAsync(TransferJob job,
        UploadItem item,
        string uploadRemotePath,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= TransferRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _sftpService.UploadFileAsync(
                    item.LocalPath,
                    uploadRemotePath,
                    progress,
                    cancellationToken,
                    resume: true);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < TransferRetryAttempts)
            {
                lastException = ex;
                var retryStatus =
                    $"Upload interrupted for {item.DisplayName}. Retrying ({attempt + 1}/{TransferRetryAttempts})...";
                UpdateTransferUi(job, TransferTitle, retryStatus, TransferProgress);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(4, attempt)), cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        throw lastException ?? new InvalidOperationException($"Upload failed for {item.DisplayName}.");
    }

    private async Task DownloadFileWithRetriesAsync(TransferJob job,
        DownloadFilePlanItem item,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= TransferRetryAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _sftpService.DownloadFileAsync(
                    item.RemotePath,
                    item.LocalPath,
                    progress,
                    cancellationToken,
                    resume: true);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < TransferRetryAttempts)
            {
                lastException = ex;
                var retryStatus =
                    $"Download interrupted for {item.DisplayName}. Retrying ({attempt + 1}/{TransferRetryAttempts})...";
                UpdateTransferUi(job, TransferTitle, retryStatus, TransferProgress);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(4, attempt)), cancellationToken);
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        throw lastException ?? new InvalidOperationException($"Download failed for {item.DisplayName}.");
    }

    private async Task FinalizeAtomicUploadAsync(TransferJob job,
        UploadItem item,
        int fileNumber,
        int totalFiles,
        double progressValue,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.AtomicTempRemotePath))
            return;

        var title = $"Finalizing {fileNumber}/{totalFiles}: {item.DisplayName}";
        UpdateTransferUi(job, title, $"Finalizing atomic replace for {item.DisplayName}...", progressValue);
        await _sftpService.PromoteUploadedFileAtomicallyAsync(
            item.AtomicTempRemotePath,
            item.RemotePath,
            cancellationToken);
        item.AtomicTempRemotePath = null;
        UpdateTransferUi(job, title, $"Finalized {item.DisplayName}", progressValue);
    }

    private async Task<VerificationMode> VerifyUploadedFileAsync(TransferJob job,
        UploadItem item,
        string uploadedRemotePath,
        int fileNumber,
        int totalFiles,
        double progressValue,
        CancellationToken cancellationToken)
    {
        var title = $"Verifying {fileNumber}/{totalFiles}: {item.DisplayName}";
        UpdateTransferUi(job, title, $"Verifying {item.DisplayName}...", progressValue);

        var localInfo = new FileInfo(item.LocalPath);
        if (!localInfo.Exists)
            throw new InvalidOperationException(
                $"Integrity verification failed for '{item.DisplayName}': local file is missing.");

        var remoteInfo = await _sftpService.GetEntryInfoAsync(uploadedRemotePath);
        if (remoteInfo.IsDirectory)
            throw new InvalidOperationException(
                $"Integrity verification failed for '{item.DisplayName}': remote path is a directory.");

        if (remoteInfo.Size != localInfo.Length)
        {
            throw new InvalidOperationException(
                $"Integrity verification failed for '{item.DisplayName}': size mismatch (local {localInfo.Length:N0} bytes, remote {remoteInfo.Size:N0} bytes).");
        }

        if (localInfo.Length <= HashVerificationThresholdBytes)
        {
            var localHash = await ComputeLocalFileSha256Async(item.LocalPath, cancellationToken);
            var remoteHash = await _sftpService.ComputeRemoteFileSha256Async(uploadedRemotePath, cancellationToken);
            if (!string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Integrity verification failed for '{item.DisplayName}': SHA-256 mismatch.");
            }

            UpdateTransferUi(job, title, $"Verified {item.DisplayName}: SHA-256 OK", progressValue);
            return VerificationMode.Hash;
        }

        var fallbackStatus =
            FormatSizeAndTimeVerificationStatus(
                localInfo.LastWriteTimeUtc,
                remoteInfo.LastWriteTime.ToUniversalTime());
        UpdateTransferUi(job, title, $"Verified {item.DisplayName}: {fallbackStatus}", progressValue);
        return VerificationMode.SizeAndTimeFallback;
    }

    private async Task<VerificationMode> VerifyDownloadedFileAsync(TransferJob job,
        DownloadFilePlanItem item,
        int fileNumber,
        int totalFiles,
        double progressValue,
        CancellationToken cancellationToken)
    {
        var title = $"Verifying {fileNumber}/{totalFiles}: {item.DisplayName}";
        UpdateTransferUi(job, title, $"Verifying {item.DisplayName}...", progressValue);

        var remoteInfo = await _sftpService.GetEntryInfoAsync(item.RemotePath);
        if (remoteInfo.IsDirectory)
            throw new InvalidOperationException(
                $"Integrity verification failed for '{item.DisplayName}': remote path is a directory.");

        var localInfo = new FileInfo(item.LocalPath);
        if (!localInfo.Exists)
            throw new InvalidOperationException(
                $"Integrity verification failed for '{item.DisplayName}': downloaded file is missing.");

        if (remoteInfo.Size != localInfo.Length)
        {
            TryDeleteLocalPath(item.LocalPath);
            throw new InvalidOperationException(
                $"Integrity verification failed for '{item.DisplayName}': size mismatch (remote {remoteInfo.Size:N0} bytes, local {localInfo.Length:N0} bytes).");
        }

        if (remoteInfo.Size <= HashVerificationThresholdBytes)
        {
            var localHash = await ComputeLocalFileSha256Async(item.LocalPath, cancellationToken);
            var remoteHash = await _sftpService.ComputeRemoteFileSha256Async(item.RemotePath, cancellationToken);
            if (!string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteLocalPath(item.LocalPath);
                throw new InvalidOperationException(
                    $"Integrity verification failed for '{item.DisplayName}': SHA-256 mismatch.");
            }

            UpdateTransferUi(job, title, $"Verified {item.DisplayName}: SHA-256 OK", progressValue);
            return VerificationMode.Hash;
        }

        var fallbackStatus =
            FormatSizeAndTimeVerificationStatus(
                remoteInfo.LastWriteTime.ToUniversalTime(),
                localInfo.LastWriteTimeUtc);
        UpdateTransferUi(job, title, $"Verified {item.DisplayName}: {fallbackStatus}", progressValue);
        return VerificationMode.SizeAndTimeFallback;
    }

    private static async Task<string> ComputeLocalFileSha256Async(string localPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string FormatSizeAndTimeVerificationStatus(DateTime sourceUtc,
        DateTime targetUtc)
    {
        if (sourceUtc == DateTime.MinValue || targetUtc == DateTime.MinValue)
            return "size/time fallback OK (timestamp unavailable)";

        var delta = (sourceUtc - targetUtc).Duration();
        if (delta <= VerificationTimestampTolerance)
            return "size/time fallback OK";

        return $"size/time fallback OK (timestamp delta {delta.TotalMinutes:F1} min)";
    }

    private static string BuildVerificationSummary(int hashVerifiedFiles,
        int fallbackVerifiedFiles)
    {
        var totalVerified = hashVerifiedFiles + fallbackVerifiedFiles;
        if (totalVerified <= 0)
            return "verification skipped";

        if (hashVerifiedFiles > 0 && fallbackVerifiedFiles > 0)
            return $"verification: {totalVerified} OK ({hashVerifiedFiles} hash, {fallbackVerifiedFiles} size/time)";

        if (hashVerifiedFiles > 0)
            return $"verification: {hashVerifiedFiles} hash";

        return $"verification: {fallbackVerifiedFiles} size/time";
    }

    [RelayCommand]
    private void Cut(FileEntry? file)
    {
        var targets = GetSelectedTargets(file);
        if (targets.Count == 0) return;

        _clipboardItems =
            targets.Select(t =>
                new ClipboardItem
                {
                    Name = t.Name,
                    Path = GetEntryRemotePath(t),
                    IsDirectory = t.IsDirectory,
                    IsSymbolicLink = t.IsSymbolicLink,
                    SymbolicLinkTarget = t.SymbolicLinkTarget,
                    SymbolicLinkTargetIsDirectory = t.SymbolicLinkTargetIsDirectory,
                    Size = t.Size,
                    LastWriteTime = t.LastWriteTime
                }).ToList();
        _clipboardIsCut = true;
        LoggingService.Info($"Cut set: {_clipboardItems.Count} item(s)");
        StatusMessage = _clipboardItems.Count == 1 ? $"Cut: {targets[0].Name}" : $"Cut {targets.Count} items";
    }

    [RelayCommand]
    private async Task Paste()
    {
        await PasteInto(null);
    }

    [RelayCommand]
    private async Task PasteInto(FileEntry? targetFolder)
    {
        if (_clipboardItems.Count == 0 || !IsConnected) return;

        var destinationFolder = CurrentPath;
        if (targetFolder is { IsDirectory: true } && targetFolder.Name != "..")
        {
            destinationFolder = GetEntryRemotePath(targetFolder);
        }

        try
        {
            SymlinkBehaviorChoice? copySymlinkChoiceToAll = null;

            foreach (var item in _clipboardItems)
            {
                var fileName = item.Name;
                var destPath =
                    destinationFolder.EndsWith('/') ? destinationFolder + fileName : $"{destinationFolder}/{fileName}";

                var copyAsLinkEntry = false;
                var sourceIsDirectory = item.IsDirectory;
                if (!_clipboardIsCut && item.IsSymbolicLink)
                {
                    SymlinkBehaviorChoice behaviorChoice;
                    if (copySymlinkChoiceToAll is { } presetChoice)
                    {
                        behaviorChoice = presetChoice;
                    }
                    else
                    {
                        var details = BuildSymlinkActionDetails(item, item.Path);
                        var decision = await _dialogService.ConfirmSymlinkBehaviorAsync(
                            "Copy symbolic link",
                            $"'{item.Name}' is a symbolic link. Choose copy behavior.",
                            details,
                            allowApplyToAll: _clipboardItems.Count(clipboardItem => clipboardItem.IsSymbolicLink) > 1);
                        behaviorChoice = decision.Choice;

                        if (decision.ApplyToAll && behaviorChoice != SymlinkBehaviorChoice.Cancel)
                            copySymlinkChoiceToAll = behaviorChoice;
                    }

                    if (behaviorChoice == SymlinkBehaviorChoice.Cancel)
                    {
                        StatusMessage = "Paste cancelled";
                        return;
                    }

                    if (behaviorChoice == SymlinkBehaviorChoice.OperateOnLinkEntry)
                    {
                        copyAsLinkEntry = true;
                        sourceIsDirectory = false;
                    }
                    else
                    {
                        sourceIsDirectory = await ResolveClipboardSymlinkTargetIsDirectoryAsync(item);
                    }
                }

                if (item.Path == destPath) continue;

                LoggingService.Info(
                    $"Paste start: source={item.Path} dest={destPath} cut={_clipboardIsCut} isDir={sourceIsDirectory} isSymlink={item.IsSymbolicLink} asLink={copyAsLinkEntry}");

                if (await _sftpService.PathExistsAsync(destPath))
                {
                    var sourceInfo = (sourceIsDirectory, item.Size, item.LastWriteTime);
                    var choice = await PromptConflictAsync(item.Name, sourceInfo, destPath);

                    if (choice == ConflictChoice.Cancel)
                    {
                        StatusMessage = "Paste cancelled";
                        return;
                    }

                    if (choice == ConflictChoice.Duplicate)
                    {
                        destPath = await GetUniqueDestinationPathAsync(destinationFolder, fileName);
                        fileName = Path.GetFileName(destPath);
                    }
                    else if (choice == ConflictChoice.Overwrite)
                    {
                        var destIsDir = await _sftpService.IsDirectoryAsync(destPath);
                        if (destIsDir)
                            await _sftpService.DeleteDirectoryAsync(destPath);
                        else
                            await _sftpService.DeleteFileAsync(destPath);
                    }
                }

                if (_clipboardIsCut)
                {
                    await _sftpService.RenameFileAsync(item.Path, destPath);
                    StatusMessage = $"Moved to: {fileName}";
                }
                else
                {
                    StatusMessage = $"Copying {fileName}...";
                    if (copyAsLinkEntry)
                    {
                        var linkTarget = await EnsureClipboardSymlinkTargetAsync(item);
                        await _sftpService.CreateSymbolicLinkAsync(linkTarget, destPath);
                    }
                    else if (sourceIsDirectory)
                    {
                        await _sftpService.CopyDirectoryAsync(item.Path, destPath);
                    }
                    else
                    {
                        await _sftpService.CopyFileAsync(item.Path, destPath);
                    }

                    StatusMessage = $"Copied to: {fileName}";
                }
            }

            if (_clipboardIsCut)
            {
                _clipboardItems.Clear();
            }

            await RefreshFileList();
        }
        catch (Exception ex)
        {
            LoggingService.Error("Paste failed", ex);
            StatusMessage = $"Paste Error: {ex.Message}";
        }
    }

    private List<FileEntry> GetSelectedTargets(FileEntry? file)
    {
        var list = new List<FileEntry>();
        if (SelectedFiles is { Count: > 0 })
        {
            foreach (var item in SelectedFiles)
            {
                if (item is FileEntry entry && entry.Name != "..")
                    list.Add(entry);
            }
        }
        else if (file is not null && file.Name != "..")
        {
            list.Add(file);
        }
        else if (SelectedFile is not null && SelectedFile.Name != "..")
        {
            list.Add(SelectedFile);
        }

        return list;
    }

    private Task<ConflictChoice> PromptConflictAsync(ClipboardItem sourceItem,
        string destPath)
    {
        var sourceInfo = (sourceItem.IsDirectory, sourceItem.Size, sourceItem.LastWriteTime);
        return PromptConflictAsync(sourceItem.Name, sourceInfo, destPath);
    }

    private async Task<ConflictChoice> PromptConflictAsync(string name,
        (bool IsDirectory, long Size, DateTime LastWriteTime) sourceInfo,
        string destPath,
        (bool IsDirectory, long Size, DateTime LastWriteTime)? destinationInfo = null)
    {
        var result = await PromptConflictWithScopeAsync(
            name,
            sourceInfo,
            destPath,
            allowApplyToAll: false,
            destinationInfo: destinationInfo);
        return result.Choice;
    }

    private async Task<ConflictDialogResult> PromptConflictWithScopeAsync(string name,
        (bool IsDirectory, long Size, DateTime LastWriteTime) sourceInfo,
        string destPath,
        bool allowApplyToAll,
        (bool IsDirectory, long Size, DateTime LastWriteTime)? destinationInfo = null,
        string? message = null)
    {
        var destInfo = destinationInfo ?? await _sftpService.GetEntryInfoAsync(destPath);
        var sourceDetails = FormatEntryDetails(sourceInfo.IsDirectory, sourceInfo.Size, sourceInfo.LastWriteTime);
        var destDetails = FormatEntryDetails(destInfo.IsDirectory, destInfo.Size, destInfo.LastWriteTime);

        return await _dialogService.ConfirmConflictWithScopeAsync(
            "Name conflict",
            message ?? $"'{name}' already exists in the destination. What do you want to do?",
            sourceDetails,
            destDetails,
            allowApplyToAll);
    }

    private async Task<string> GetUniqueDestinationPathAsync(string destinationFolder,
        string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var index = 1;

        while (true)
        {
            var candidateName =
                ext.Length > 0
                    ? $"{nameWithoutExt} ({index}){ext}"
                    : $"{nameWithoutExt} ({index})";
            var candidatePath =
                destinationFolder.EndsWith('/')
                    ? destinationFolder + candidateName
                    : $"{destinationFolder}/{candidateName}";

            if (!await _sftpService.PathExistsAsync(candidatePath))
                return candidatePath;

            index++;
        }
    }

    private async Task<string> BuildUniqueAtomicUploadTempPathAsync(string destinationPath,
        CancellationToken cancellationToken)
    {
        var normalizedDestination = NormalizeRemoteAbsolutePath(destinationPath);
        var parentPath = GetRemoteParentPath(normalizedDestination);
        var destinationFileName = Path.GetFileName(normalizedDestination.TrimEnd('/'));

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidateName =
                $"{AtomicUploadTempPrefix}-{destinationFileName}.{Guid.NewGuid():N}.part";
            var candidatePath = CombineRemotePath(parentPath, candidateName);
            if (!await _sftpService.PathExistsAsync(candidatePath))
                return candidatePath;
        }
    }

    private static string GetUniqueLocalDestinationPath(string destinationFolder,
        string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var index = 1;

        while (true)
        {
            var candidateName =
                ext.Length > 0
                    ? $"{nameWithoutExt} ({index}){ext}"
                    : $"{nameWithoutExt} ({index})";
            var candidatePath = Path.Combine(destinationFolder, candidateName);

            if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
                return candidatePath;

            index++;
        }
    }

    private static (bool IsDirectory, long Size, DateTime LastWriteTime) GetLocalEntryInfo(string path)
    {
        if (Directory.Exists(path))
        {
            var dirInfo = new DirectoryInfo(path);
            return (true, 0, dirInfo.LastWriteTime);
        }

        if (File.Exists(path))
        {
            var fileInfo = new FileInfo(path);
            return (false, fileInfo.Length, fileInfo.LastWriteTime);
        }

        return (false, 0, DateTime.MinValue);
    }

    private static int GetLocalPathDepth(string localPath)
    {
        var normalized = Path.GetFullPath(localPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (normalized.Length == 0)
            return 0;

        return normalized.Count(
            ch => ch == Path.DirectorySeparatorChar || ch == Path.AltDirectorySeparatorChar);
    }

    private static void TryDeleteLocalPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return;
            }

            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch
        {
            // Best-effort cleanup for overwritten/canceled local downloads.
        }
    }

    private async Task TryDeleteRemoteFileQuietAsync(string remotePath)
    {
        try
        {
            if (await _sftpService.PathExistsAsync(remotePath))
                await _sftpService.DeleteFileAsync(remotePath);
        }
        catch
        {
            // Best effort cleanup for temporary upload artifacts.
        }
    }

    private static string FormatEntryDetails(bool isDirectory,
        long size,
        DateTime lastWriteTime)
    {
        if (isDirectory)
        {
            var dateText = lastWriteTime == DateTime.MinValue ? "Unknown" : lastWriteTime.ToString("yyyy-MM-dd HH:mm");
            return $"Directory, Modified: {dateText}";
        }

        var sizeText = $"{size / 1024.0:F2} KB";
        var modText = lastWriteTime == DateTime.MinValue ? "Unknown" : lastWriteTime.ToString("yyyy-MM-dd HH:mm");
        return $"File, Size: {sizeText}, Modified: {modText}";
    }

    // For Drag and Drop internal move
    public async Task MoveItem(string sourcePath,
        string destFolderPath)
    {
        if (!IsConnected) return;

        var fileName = System.IO.Path.GetFileName(sourcePath);
        var destPath = destFolderPath.EndsWith('/') ? destFolderPath + fileName : $"{destFolderPath}/{fileName}";

        if (sourcePath == destPath) return;

        try
        {
            LoggingService.Info($"Move start: source={sourcePath} destFolder={destFolderPath}");
            if (await _sftpService.PathExistsAsync(destPath))
            {
                var sourceInfo = await _sftpService.GetEntryInfoAsync(sourcePath);
                var choice = await PromptConflictAsync(fileName, sourceInfo, destPath);

                if (choice == ConflictChoice.Cancel)
                {
                    StatusMessage = "Move cancelled";
                    return;
                }

                if (choice == ConflictChoice.Duplicate)
                {
                    destPath = await GetUniqueDestinationPathAsync(destFolderPath, fileName);
                    fileName = Path.GetFileName(destPath);
                }
                else if (choice == ConflictChoice.Overwrite)
                {
                    var destIsDir = await _sftpService.IsDirectoryAsync(destPath);
                    if (destIsDir)
                        await _sftpService.DeleteDirectoryAsync(destPath);
                    else
                        await _sftpService.DeleteFileAsync(destPath);
                }
            }

            await _sftpService.RenameFileAsync(sourcePath, destPath);
            await RefreshFileList();
            StatusMessage = $"Moved {fileName} to {System.IO.Path.GetFileName(destFolderPath)}";
        }
        catch (Exception ex)
        {
            LoggingService.Error($"Move failed: source={sourcePath} destFolder={destFolderPath}", ex);
            StatusMessage = $"Move Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelTransfer()
    {
        _transferCts?.Cancel();
    }

    [RelayCommand]
    private async Task DownloadTo(FileEntry? file)
    {
        if (!IsConnected) return;

        var targets = GetSelectedTargets(file);
        if (targets.Count is 0) return;
        if (targets.Count > 1)
        {
            StatusMessage = "Download supports one target at a time";
            return;
        }

        var target = targets[0];
        var targetRemotePath = GetEntryRemotePath(target);
        var targetName = Path.GetFileName(targetRemotePath.TrimEnd('/'));
        if (string.IsNullOrWhiteSpace(targetName))
            targetName = target.Name;

        var localFolder = await _dialogService.PickFolderAsync();
        if (string.IsNullOrEmpty(localFolder)) return;

        var request =
            new DownloadJobRequest
            {
                Name = targetName,
                IsDirectory = target.IsDirectory,
                Size = target.Size,
                LastWriteTime = target.LastWriteTime,
                RemotePath = targetRemotePath,
                LocalFolder = localFolder
            };

        EnqueueTransfer(CreateDownloadTransferDefinition(request));
    }

    private async Task<string?> ResolveLocalConflictAsync(string name,
        (bool IsDirectory, long Size, DateTime LastWriteTime) sourceInfo,
        string localPath)
    {
        if (!File.Exists(localPath) && !Directory.Exists(localPath))
            return localPath;

        var destInfo = GetLocalEntryInfo(localPath);
        var sourceDetails = FormatEntryDetails(sourceInfo.IsDirectory, sourceInfo.Size, sourceInfo.LastWriteTime);
        var destDetails = FormatEntryDetails(destInfo.IsDirectory, destInfo.Size, destInfo.LastWriteTime);

        var choice = await _dialogService.ConfirmConflictAsync(
            "Name conflict",
            $"'{name}' already exists in the destination. What do you want to do?",
            sourceDetails,
            destDetails);

        if (choice == ConflictChoice.Cancel)
            return null;

        if (choice == ConflictChoice.Duplicate)
            return GetUniqueLocalDestinationPath(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath));

        TryDeleteLocalPath(localPath);
        return localPath;
    }

    private async Task<DownloadPlan> BuildDownloadPlanAsync(FileEntry rootEntry,
        string remoteRootPath,
        string localRootPath,
        CancellationToken cancellationToken)
    {
        var plan = new DownloadPlan();

        if (rootEntry.IsDirectory)
        {
            await AppendDirectoryDownloadPlanAsync(
                remoteRootPath,
                localRootPath,
                rootEntry.Name,
                plan,
                cancellationToken);
            return plan;
        }

        plan.Files.Add(
            new DownloadFilePlanItem
            {
                RemotePath = remoteRootPath,
                LocalPath = localRootPath,
                DisplayName = rootEntry.Name,
                Size = rootEntry.Size
            });
        return plan;
    }

    private async Task AppendDirectoryDownloadPlanAsync(string remoteDirectoryPath,
        string localDirectoryPath,
        string displayPrefix,
        DownloadPlan plan,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        plan.Directories.Add(localDirectoryPath);

        var entries = await _sftpService.ListDirectoryAsync(remoteDirectoryPath);
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteEntryPath = CombineRemotePath(remoteDirectoryPath, entry.Name);
            var localEntryPath = Path.Combine(localDirectoryPath, entry.Name);
            var displayName = $"{displayPrefix}/{entry.Name}";

            if (entry.IsDirectory && !entry.IsSymbolicLink)
            {
                await AppendDirectoryDownloadPlanAsync(
                    remoteEntryPath,
                    localEntryPath,
                    displayName,
                    plan,
                    cancellationToken);
                continue;
            }

            plan.Files.Add(
                new DownloadFilePlanItem
                {
                    RemotePath = remoteEntryPath,
                    LocalPath = localEntryPath,
                    DisplayName = displayName,
                    Size = entry.Size
                });
        }
    }

    public async Task UploadFiles(IEnumerable<string> localPaths)
    {
        if (!IsConnected) return;
        var pathList = localPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .ToList();
        if (pathList.Count == 0) return;

        var request =
            new UploadJobRequest
            {
                DestinationPath = CurrentPath,
                LocalPaths = pathList
            };

        EnqueueTransfer(CreateUploadTransferDefinition(request));
    }

    private async Task<List<UploadSourceTarget>?> ResolveUploadTargetsAsync(IEnumerable<string> localPaths,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var targets = new List<UploadSourceTarget>();
        var seenLocalPaths = new HashSet<string>(GetPathComparerForCurrentPlatform());

        foreach (var rawPath in localPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            string localPath;
            try
            {
                localPath = Path.GetFullPath(rawPath);
            }
            catch
            {
                continue;
            }

            if (!seenLocalPaths.Add(localPath))
                continue;

            if (File.Exists(localPath))
            {
                var fileInfo = new FileInfo(localPath);
                var fileName = fileInfo.Name;
                var resolvedTargetPath = NormalizeRemoteAbsolutePath(CombineRemotePath(destinationPath, fileName));

                targets.Add(
                    new UploadSourceTarget
                    {
                        LocalPath = localPath,
                        RemoteRootPath = resolvedTargetPath,
                        IsDirectory = false,
                        DisplayRootName = Path.GetFileName(resolvedTargetPath)
                    });
                continue;
            }

            if (Directory.Exists(localPath))
            {
                var directoryInfo = new DirectoryInfo(localPath);
                var folderName = directoryInfo.Name;
                if (string.IsNullOrWhiteSpace(folderName))
                    continue;

                var remoteTargetPath = CombineRemotePath(destinationPath, folderName);
                var sourceInfo = (IsDirectory: true, Size: 0L, LastWriteTime: directoryInfo.LastWriteTime);
                var resolvedTargetPath =
                    await ResolveUploadConflictAsync(
                        folderName,
                        sourceInfo,
                        remoteTargetPath,
                        destinationPath,
                        cancellationToken);
                if (resolvedTargetPath is null)
                    return null;

                targets.Add(
                    new UploadSourceTarget
                    {
                        LocalPath = localPath,
                        RemoteRootPath = resolvedTargetPath,
                        IsDirectory = true,
                        DisplayRootName = Path.GetFileName(resolvedTargetPath.TrimEnd('/'))
                    });
                continue;
            }

            LoggingService.Warn($"Upload skipped path that does not exist: {localPath}");
        }

        return targets;
    }

    private async Task<string?> ResolveUploadConflictAsync(string name,
        (bool IsDirectory, long Size, DateTime LastWriteTime) sourceInfo,
        string remoteTargetPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedTargetPath = NormalizeRemoteAbsolutePath(remoteTargetPath);
        var targetExists = await _sftpService.PathExistsAsync(normalizedTargetPath);
        (bool IsDirectory, long Size, DateTime LastWriteTime)? destinationInfo = null;

        // Some servers report false negatives for directory existence checks.
        // Fall back to listing the parent folder so repeated directory uploads still
        // trigger conflict resolution instead of silently merging/overwriting.
        if (!targetExists && sourceInfo.IsDirectory)
        {
            var parentPath = NormalizeRemoteAbsolutePath(destinationPath);
            var existingEntry = await TryFindRemoteChildEntryAsync(parentPath, name, cancellationToken);
            if (existingEntry is not null)
            {
                targetExists = true;
                destinationInfo = (existingEntry.IsDirectory, existingEntry.Size, existingEntry.LastWriteTime);
            }
        }

        if (!targetExists)
            return normalizedTargetPath;

        var destinationIsDirectory = destinationInfo is { } info
            ? info.IsDirectory
            : await _sftpService.IsDirectoryAsync(normalizedTargetPath);

        var isDirectoryMerge = sourceInfo.IsDirectory && destinationIsDirectory;
        var message =
            isDirectoryMerge
                ? $"Folder '{name}' already exists in destination. Overwrite will replace conflicting files during upload; duplicate will create a new folder name."
                : $"'{name}' already exists in the destination. What do you want to do?";

        var choice = (
                await PromptConflictWithScopeAsync(
                    name,
                    sourceInfo,
                    normalizedTargetPath,
                    allowApplyToAll: false,
                    destinationInfo: destinationInfo,
                    message: message))
            .Choice;
        if (choice == ConflictChoice.Cancel)
            return null;

        if (choice == ConflictChoice.Duplicate)
            return await GetUniqueDestinationPathAsync(destinationPath, name);

        if (isDirectoryMerge)
            return normalizedTargetPath;

        if (destinationIsDirectory)
            await _sftpService.DeleteDirectoryAsync(normalizedTargetPath);
        else
            await _sftpService.DeleteFileAsync(normalizedTargetPath);

        return normalizedTargetPath;
    }

    private async Task<FileEntry?> TryFindRemoteChildEntryAsync(string parentPath,
        string childName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var entries = await _sftpService.ListDirectoryAsync(parentPath);
            return entries.FirstOrDefault(entry => string.Equals(entry.Name, childName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            LoggingService.Warn(
                $"Could not verify remote upload conflict for '{childName}' in '{parentPath}'. {ex.Message}");
            return null;
        }
    }

    private async Task<bool> ResolveUploadFileConflictsAsync(TransferJob job,
        UploadPlan plan,
        CancellationToken cancellationToken)
    {
        if (plan.Files.Count == 0)
            return true;

        ConflictChoice? applyChoiceToAll = null;

        for (var i = 0; i < plan.Files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = plan.Files[i];
            item.AtomicTempRemotePath = null;
            var checkTitle = $"Checking conflicts {i + 1}/{plan.Files.Count}";
            UpdateTransferUi(job, checkTitle, $"Checking {item.DisplayName}");

            if (!await _sftpService.PathExistsAsync(item.RemotePath))
                continue;

            var sourceInfo = GetLocalEntryInfo(item.LocalPath);
            var destinationInfo = await _sftpService.GetEntryInfoAsync(item.RemotePath);

            ConflictChoice choice;
            if (applyChoiceToAll is { } allChoice)
            {
                choice = allChoice;
            }
            else
            {
                var decision = await PromptConflictWithScopeAsync(
                    Path.GetFileName(item.RemotePath),
                    sourceInfo,
                    item.RemotePath,
                    allowApplyToAll: true,
                    destinationInfo: destinationInfo);
                choice = decision.Choice;

                if (choice == ConflictChoice.Cancel)
                    return false;

                if (decision.ApplyToAll)
                    applyChoiceToAll = choice;
            }

            if (choice == ConflictChoice.Cancel)
                return false;

            if (choice == ConflictChoice.Duplicate)
            {
                var destinationFolder = GetRemoteParentPath(item.RemotePath);
                var fileName = Path.GetFileName(item.RemotePath);
                item.RemotePath = await GetUniqueDestinationPathAsync(destinationFolder, fileName);
                item.AtomicTempRemotePath = null;
                continue;
            }

            UpdateTransferUi(job, checkTitle, $"Replacing {item.DisplayName}");
            if (destinationInfo.IsDirectory)
            {
                await _sftpService.DeleteDirectoryAsync(item.RemotePath);
                continue;
            }

            item.AtomicTempRemotePath =
                await BuildUniqueAtomicUploadTempPathAsync(item.RemotePath, cancellationToken);
        }

        return true;
    }

    private UploadPlan BuildUploadPlan(IEnumerable<UploadSourceTarget> sourceTargets,
        CancellationToken cancellationToken)
    {
        var plan = new UploadPlan();
        var seenRemoteDirectories = new HashSet<string>(StringComparer.Ordinal);
        var seenLocalFiles = new HashSet<string>(GetPathComparerForCurrentPlatform());

        foreach (var sourceTarget in sourceTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (sourceTarget.IsDirectory)
            {
                AddDirectoryTreeToUploadPlan(
                    plan,
                    seenRemoteDirectories,
                    seenLocalFiles,
                    sourceTarget.LocalPath,
                    sourceTarget.RemoteRootPath,
                    sourceTarget.DisplayRootName,
                    cancellationToken);
                continue;
            }

            AddFileToUploadPlan(
                plan,
                seenLocalFiles,
                sourceTarget.LocalPath,
                sourceTarget.RemoteRootPath,
                sourceTarget.DisplayRootName);
        }

        plan.Directories.Sort(
            (left, right) => GetRemotePathDepth(left).CompareTo(GetRemotePathDepth(right)));

        return plan;
    }

    private void AddDirectoryTreeToUploadPlan(UploadPlan plan,
        HashSet<string> seenRemoteDirectories,
        HashSet<string> seenLocalFiles,
        string localRootPath,
        string remoteRootPath,
        string displayRootName,
        CancellationToken cancellationToken)
    {
        AddDirectoryToUploadPlan(plan, seenRemoteDirectories, remoteRootPath);

        foreach (var directoryPath in Directory.EnumerateDirectories(localRootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativeDirectoryPath = Path.GetRelativePath(localRootPath, directoryPath);
            var remoteDirectoryPath = CombineRemotePath(remoteRootPath, ToRemoteRelativePath(relativeDirectoryPath));
            AddDirectoryToUploadPlan(plan, seenRemoteDirectories, remoteDirectoryPath);
        }

        foreach (var filePath in Directory.EnumerateFiles(localRootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativeFilePath = Path.GetRelativePath(localRootPath, filePath);
            var remoteFilePath = CombineRemotePath(remoteRootPath, ToRemoteRelativePath(relativeFilePath));
            var displayName = $"{displayRootName}/{ToRemoteRelativePath(relativeFilePath)}";
            AddFileToUploadPlan(plan, seenLocalFiles, filePath, remoteFilePath, displayName);
        }
    }

    private static void AddDirectoryToUploadPlan(UploadPlan plan,
        HashSet<string> seenRemoteDirectories,
        string remoteDirectoryPath)
    {
        var normalizedRemoteDirectory = NormalizeRemoteAbsolutePath(remoteDirectoryPath);
        if (normalizedRemoteDirectory.Length == 0 || normalizedRemoteDirectory == "/")
            return;

        if (seenRemoteDirectories.Add(normalizedRemoteDirectory))
            plan.Directories.Add(normalizedRemoteDirectory);
    }

    private static void AddFileToUploadPlan(UploadPlan plan,
        HashSet<string> seenLocalFiles,
        string localFilePath,
        string remoteFilePath,
        string? displayName = null)
    {
        if (!seenLocalFiles.Add(localFilePath))
            return;

        var fileInfo = new FileInfo(localFilePath);
        plan.Files.Add(
            new UploadItem
            {
                LocalPath = localFilePath,
                RemotePath = NormalizeRemoteAbsolutePath(remoteFilePath),
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? fileInfo.Name : displayName,
                Size = fileInfo.Exists ? fileInfo.Length : 0
            });
    }

    private static int GetRemotePathDepth(string remotePath)
    {
        return NormalizeRemoteAbsolutePath(remotePath).Count(ch => ch == '/');
    }

    private static string CombineRemotePath(string basePath,
        string childPath)
    {
        var normalizedBase = NormalizeRemoteAbsolutePath(basePath);
        var normalizedChild = childPath.Replace('\\', '/').Trim('/');
        if (normalizedChild.Length == 0)
            return normalizedBase;

        return normalizedBase == "/"
            ? $"/{normalizedChild}"
            : $"{normalizedBase}/{normalizedChild}";
    }

    private static string ToRemoteRelativePath(string localRelativePath)
    {
        return localRelativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Trim('/');
    }

    private static string GetRemoteParentPath(string remotePath)
    {
        var normalized = NormalizeRemoteAbsolutePath(remotePath);
        if (normalized == "/")
            return "/";

        var separatorIndex = normalized.LastIndexOf('/');
        if (separatorIndex <= 0)
            return "/";

        return normalized[..separatorIndex];
    }

    private static string NormalizeRemoteAbsolutePath(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
            return "/";

        var normalized = remotePath.Trim().Replace('\\', '/');
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);

        if (normalized.Length > 1)
            normalized = normalized.TrimEnd('/');

        return normalized;
    }

    private static string NormalizeRemotePathWithDotSegments(string remotePath)
    {
        var normalized = NormalizeRemoteAbsolutePath(remotePath);
        if (normalized == "/")
            return normalized;

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new Stack<string>();
        foreach (var segment in segments)
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (stack.Count > 0)
                    stack.Pop();
                continue;
            }

            stack.Push(segment);
        }

        if (stack.Count == 0)
            return "/";

        return "/" + string.Join("/", stack.Reverse());
    }

    private static StringComparer GetPathComparerForCurrentPlatform()
    {
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }
}
