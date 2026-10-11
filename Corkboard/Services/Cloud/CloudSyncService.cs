using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Corkboard.Core;
using Corkboard.Core.Abstraction;
using Corkboard.Core.Abstraction.Services;
using Corkboard.Core.Models;
using Corkboard.Core.Models.SubConfigs.Cloud;
using Corkboard.Core.Services.Cloud;
using Corkboard.Core.Services.Config;
using Corkboard.Services.Auth;
using Corkboard.Shared;
using Microsoft.Extensions.Logging;

namespace Corkboard.Services.Cloud;

/// <summary>
///     云同步：把作业板与设置作为文件备份到思拓创联云存储，并在本机没有改动时自动拉取。
/// </summary>
/// <remarks>
///     <para>
///         为什么不做双向合并：作业板是一份自由文本 + 富文本，两边同时改就必然分叉，自动合并只会把
///         两边的笔记搅在一起。所以这里的规矩是**只备份、敢于自动恢复的前提是本机没动过**——
///         本机有未同步改动时绝不自动拉取，只在界面上提示「云端有更新的备份」，由用户自己决定。
///     </para>
///     <para>
///         防自激：同步成功后要写 <see cref="CloudSyncSettingsConfig.LastSyncedAt" />，而配置一写就触发
///     <see cref="ConfigHandlerBase{T}.Saved" />、又会拉起一次上传。所以批量写这些簿记字段时用
///     <c>_synchronizing</c> 挡住自己的事件。
///     </para>
///     <para>
///         判断「设置有没有改动」用内容摘要而不是文件时间：窗口被拖动这类**被排除同步**的操作也会写
///     配置文件，用文件时间当依据的话界面永远显示本机有改动、再也不敢自动拉取。
///     </para>
/// </remarks>
public sealed class CloudSyncService : ICloudSyncService, IDisposable
{
    /// <summary>
    ///     作业板改动后等这么久再上传。布置 / 修改 / 删除作业都是「一次操作一次落盘」，
    ///     等几秒没人再动就传，用户不用自己去点「立即备份」。
    /// </summary>
    private static readonly TimeSpan BoardAutoUploadDelay = TimeSpan.FromSeconds(5);

    /// <summary>设置改动后等这么久再上传：设置常常一连串地改（拖滑杆、连点开关），攒久一点再传。</summary>
    private static readonly TimeSpan SettingsAutoUploadDelay = TimeSpan.FromSeconds(45);

    private const string ProductName = "Corkboard";
    private const string BoardDirectoryName = "board";
    private const string WorkDirectoryName = "cloud-sync";

    private readonly MainConfigHandler _configHandler;
    private readonly IBoardService _board;
    private readonly ICloudStorageClient _client;
    private readonly ISectlAuthService _auth;
    private readonly ConfigServiceBase _configService;
    private readonly ILogger<CloudSyncService>? _logger;

    private readonly object _gate = new();
    private CloudStorageUsage _usage = CloudStorageUsage.Empty;
    private CloudFileInfo? _remoteBoard;
    private CloudFileInfo? _remoteSettings;
    private DateTimeOffset? _remoteUpdatedAt;
    private bool _remoteIsNewer;
    private bool _isBusy;
    private string? _lastError;

    private DispatcherTimer? _autoUpload;

    /// <summary>
    ///     已经排着的那次自动上传的截止时间。两次改动挨在一起时（作业落盘往往紧跟一次配置落盘），
    ///     按更早的那个算：否则先排的 5 秒会被后到的 45 秒顶掉，作业要等 45 秒才传上去。
    /// </summary>
    private DateTimeOffset? _autoUploadDeadline;
    private bool _synchronizing;
    private bool _autoPullChecked;
    private bool _initialized;
    private bool _disposed;

    public CloudSyncService(MainConfigHandler configHandler,
        IBoardService board,
        ICloudStorageClient client,
        ISectlAuthService auth,
        ConfigServiceBase configService,
        ILogger<CloudSyncService>? logger = null)
    {
        _configHandler = configHandler;
        _board = board;
        _client = client;
        _auth = auth;
        _configService = configService;
        _logger = logger;

        _configHandler.Saved += OnConfigSaved;
        _board.Changed += OnBoardChanged;
        _auth.StateChanged += OnAuthStateChanged;
    }

    public event EventHandler? StateChanged;

    public CloudStorageUsage Usage
    {
        get
        {
            lock (_gate)
            {
                return _usage;
            }
        }
    }

    public CloudSyncScope CurrentScope => ScopeOf(Config);

    public bool HasCloudBackup
    {
        get
        {
            lock (_gate)
            {
                return _remoteBoard is not null || _remoteSettings is not null;
            }
        }
    }

    public bool HasBoardBackup
    {
        get
        {
            lock (_gate)
            {
                return _remoteBoard is not null;
            }
        }
    }

    public bool HasSettingsBackup
    {
        get
        {
            lock (_gate)
            {
                return _remoteSettings is not null;
            }
        }
    }

    public DateTimeOffset? RemoteUpdatedAt
    {
        get
        {
            lock (_gate)
            {
                return _remoteUpdatedAt;
            }
        }
    }

    public bool RemoteIsNewer
    {
        get
        {
            lock (_gate)
            {
                return _remoteIsNewer;
            }
        }
    }

    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _isBusy;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    private CloudSyncSettingsConfig Config => _configHandler.Data.Cloud;

    private static string BoardDirectory => Utils.GetDirectoryPath(BoardDirectoryName);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _initialized = true;
        if (!_auth.IsSignedIn)
            return;

        // 用量和云备份清单都是账号维度的，开关没开也该显示出来（设置页要看剩余空间），
        // 「本机没有改动时自动拉取」才是开关管的事，所以 autoPull 只在开启云同步时才带。
        await SafeCheckAsync(Config.Enabled, cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => CheckAsync(false, false, cancellationToken);

    public async Task BackupAsync(CloudSyncScope scope, CancellationToken cancellationToken = default)
    {
        if (scope == CloudSyncScope.None)
            return;

        EnsureSignedIn();
        SetBusy(true);
        try
        {
            await BackupCoreAsync(scope, cancellationToken);
            await CheckAsync(false, true, cancellationToken);
        }
        catch (Exception exception)
        {
            RecordError(exception);
            throw;
        }
        finally
        {
            SetBusy(false);
        }
    }

    public async Task RestoreAsync(CloudSyncScope scope, CancellationToken cancellationToken = default)
    {
        if (scope == CloudSyncScope.None)
            return;

        EnsureSignedIn();
        SetBusy(true);
        try
        {
            await RestoreCoreAsync(scope, cancellationToken);
            await CheckAsync(false, true, cancellationToken);
        }
        catch (Exception exception)
        {
            RecordError(exception);
            throw;
        }
        finally
        {
            SetBusy(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _configHandler.Saved -= OnConfigSaved;
        _board.Changed -= OnBoardChanged;
        _auth.StateChanged -= OnAuthStateChanged;
        StopAutoUpload();
    }

    private void EnsureSignedIn()
    {
        if (!_auth.IsSignedIn)
            throw new CloudStorageException("unauthorized", "先登录思拓创联账号再使用云同步");
    }

    /// <summary>查一遍云端（用量 + 备份文件）；失败时抛出，由调用方决定怎么显示。</summary>
    private async Task CheckAsync(bool autoPull, bool assumeSynced, CancellationToken cancellationToken)
    {
        SetBusy(true);
        try
        {
            var usage = await _client.GetUsageAsync(false, cancellationToken);
            var files = await _client.ListFilesAsync(cancellationToken);
            ApplyCloudFiles(usage, files, assumeSynced);

            lock (_gate)
            {
                _lastError = null;
            }

            NotifyState();

            if (autoPull)
                await AutoPullAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            RecordError(exception);
            throw;
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>启动期与事件里的查询：失败只记日志，绝不把异常抛到没人接的地方。</summary>
    private async Task SafeCheckAsync(bool autoPull, CancellationToken cancellationToken)
    {
        try
        {
            await CheckAsync(autoPull, false, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogDebug(exception, "查询云存储状态失败。");
        }
    }

    private void ApplyCloudFiles(CloudStorageUsage usage, IReadOnlyList<CloudFileInfo> files, bool assumeSynced)
    {
        var board = CloudBackupFiles.FindBoard(files);
        var settings = CloudBackupFiles.FindSettings(files);
        var scope = CurrentScope;
        var remote = Later(
            scope.HasFlag(CloudSyncScope.Board) ? board?.UpdatedAt : null,
            scope.HasFlag(CloudSyncScope.Settings) ? settings?.UpdatedAt : null);

        lock (_gate)
        {
            _usage = usage;
            _remoteBoard = board;
            _remoteSettings = settings;
            _remoteUpdatedAt = Later(board?.UpdatedAt, settings?.UpdatedAt);
            _remoteIsNewer = !assumeSynced && IsRemoteNewer(remote);
        }

        NotifyState();
    }

    private bool IsRemoteNewer(DateTimeOffset? remote)
        => remote is { } value &&
           (Config.LastSyncedAt is not { } last || value > last + CloudSyncPolicy.Tolerance);

    /// <summary>本机没有未同步改动时自动拉取；只要有可能冲掉用户的数据就不动。</summary>
    private async Task AutoPullAsync(CancellationToken cancellationToken)
    {
        if (_autoPullChecked)
            return;

        _autoPullChecked = true;

        var config = Config;
        if (!config.Enabled || !config.AutoPull || !_auth.IsSignedIn)
            return;

        var scope = ScopeOf(config);
        if (scope == CloudSyncScope.None || RemoteUpdatedAtFor(scope) is not { } remote)
            return;

        var (boardDirty, settingsDirty) = ComputeDirty(scope);
        var dirty = boardDirty || settingsDirty;
        if (!CloudSyncPolicy.ShouldAutoPull(remote, config.LastSyncedAt, dirty ? DateTimeOffset.Now : null))
            return;

        _logger?.LogInformation("本机没有未同步的改动，正在从云端恢复备份。");
        await RestoreCoreAsync(scope, cancellationToken);
    }

    private DateTimeOffset? RemoteUpdatedAtFor(CloudSyncScope scope)
    {
        lock (_gate)
        {
            return Later(
                scope.HasFlag(CloudSyncScope.Board) ? _remoteBoard?.UpdatedAt : null,
                scope.HasFlag(CloudSyncScope.Settings) ? _remoteSettings?.UpdatedAt : null);
        }
    }

    private (bool Board, bool Settings) ComputeDirty(CloudSyncScope scope)
    {
        var lastSyncedAt = Config.LastSyncedAt;
        var boardDirty = scope.HasFlag(CloudSyncScope.Board) && IsBoardDirty(lastSyncedAt);
        var settingsDirty = scope.HasFlag(CloudSyncScope.Settings) && IsSettingsDirty();
        return (boardDirty, settingsDirty);
    }

    private static bool IsBoardDirty(DateTimeOffset? lastSyncedAt)
        => CloudSyncPolicy.NeedsUpload(DirectoryLastWrite(BoardDirectory), lastSyncedAt);

    private bool IsSettingsDirty()
    {
        var hash = ComputeSettingsHash();
        if (hash is null)
            return false;

        var last = Config.LastSettingsHash;
        return last is null || !string.Equals(hash, last, StringComparison.Ordinal);
    }

    /// <summary>本地设置（排除本机专属项之后）的内容摘要；读不出来时返回 <c>null</c>。</summary>
    private string? ComputeSettingsHash()
    {
        try
        {
            var path = _configHandler.Data.ConfigFilePath;
            if (!File.Exists(path))
                return null;

            var stripped = CloudSyncExclusions.StripForUpload(File.ReadAllText(path));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stripped)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger?.LogDebug(exception, "计算设置内容摘要失败。");
            return null;
        }
    }

    private async Task BackupCoreAsync(CloudSyncScope scope, CancellationToken cancellationToken)
    {
        var work = CreateWorkDirectory();
        try
        {
            var existing = await _client.ListFilesAsync(cancellationToken);
            var settingsUploaded = false;

            if (scope.HasFlag(CloudSyncScope.Board))
            {
                var archivePath = Path.Combine(work, CloudBackupFiles.BoardArchiveName);
                var manifest = new CloudBackupManifest(ProductName, GlobalConstants.Version, GlobalConstants.PlatformId,
                    DateTimeOffset.Now);
                CloudBackupArchive.Create(BoardDirectory, archivePath, manifest);

                // 压缩包必须先变成 base64 文本：云存储会把上传的字节按 UTF-8 解码一遍，二进制传上去就坏了。
                var uploadPath = Path.Combine(work, CloudBackupFiles.BoardCloudName);
                await CloudBackupPayload.WriteEncodedAsync(archivePath, uploadPath, cancellationToken)
                    .ConfigureAwait(false);

                var fileId = await _client.UploadAsync(uploadPath, CloudBackupFiles.BoardCloudName, cancellationToken)
                    .ConfigureAwait(false);
                await DeleteReplacedAsync(CloudBackupFiles.AllBoardArchives(existing), fileId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (scope.HasFlag(CloudSyncScope.Settings))
            {
                var path = _configHandler.Data.ConfigFilePath;
                if (File.Exists(path))
                {
                    var stripped = CloudSyncExclusions.StripForUpload(await File.ReadAllTextAsync(path, cancellationToken));
                    var backupPath = Path.Combine(work, CloudBackupFiles.SettingsFileName);
                    await File.WriteAllTextAsync(backupPath, stripped, cancellationToken);

                    var fileId = await _client.UploadAsync(backupPath, CloudBackupFiles.SettingsFileName, cancellationToken);
                    await DeleteReplacedAsync(CloudBackupFiles.AllSettingsFiles(existing), fileId, cancellationToken);
                    settingsUploaded = true;
                }
            }

            CommitSyncState(settingsUploaded);
            _logger?.LogInformation("已上传云备份（{Scope}）。", scope);
        }
        finally
        {
            DeleteDirectory(work);
        }
    }

    private async Task RestoreCoreAsync(CloudSyncScope scope, CancellationToken cancellationToken)
    {
        var files = await _client.ListFilesAsync(cancellationToken);
        var boardFile = CloudBackupFiles.FindBoard(files);
        var settingsFile = CloudBackupFiles.FindSettings(files);

        var wantBoard = scope.HasFlag(CloudSyncScope.Board) && boardFile is not null;
        var wantSettings = scope.HasFlag(CloudSyncScope.Settings) && settingsFile is not null;
        if (!wantBoard && !wantSettings)
            throw new CloudStorageException("file_not_found", "云端还没有可恢复的备份");

        var work = CreateWorkDirectory();
        var settingsRestored = false;
        try
        {
            if (wantBoard)
            {
                // 用自己拼的文件名落盘：云端的 filename 是服务端数据，不能直接拿去拼路径。
                var isBase64 = CloudBackupFiles.IsBase64BoardArchive(boardFile!.FileName);
                var downloadPath = Path.Combine(work, "board-download" + (isBase64 ? CloudBackupFiles.Base64Suffix : ".zip"));
                await _client.DownloadAsync(boardFile.FileId, downloadPath, cancellationToken).ConfigureAwait(false);

                var archivePath = Path.Combine(work, CloudBackupFiles.BoardArchiveName);
                try
                {
                    if (isBase64)
                    {
                        await CloudBackupPayload.WriteDecodedAsync(downloadPath, archivePath, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        // 兼容没加 base64 之前传上去的老件：它多半已经被云端按 UTF-8 弄坏了。
                        File.Move(downloadPath, archivePath, true);
                    }

                    // 先解到临时目录：包是坏的、或者里面没有作业板数据，都还没碰到现有数据。
                    var staging = Path.Combine(work, "board");
                    var count = CloudBackupArchive.Extract(archivePath, staging);
                    if (count == 0 && DirectoryHasFiles(BoardDirectory))
                        throw new CloudStorageException("invalid_backup", "这份云备份里没有作业板数据，已停止恢复");

                    ReplaceDirectory(staging, BoardDirectory);
                    _board.Reload();
                }
                catch (Exception exception) when (exception is InvalidDataException or FormatException or IOException)
                {
                    _logger?.LogWarning(exception, "云端作业板备份解析失败。");
                    throw new CloudStorageException("invalid_backup", "云端的作业板备份读不出来，可能已经损坏");
                }
            }

            if (wantSettings)
            {
                var remotePath = Path.Combine(work, CloudBackupFiles.SettingsFileName);
                await _client.DownloadAsync(settingsFile!.FileId, remotePath, cancellationToken);

                var remote = await File.ReadAllTextAsync(remotePath, cancellationToken);
                var localPath = _configHandler.Data.ConfigFilePath;
                var local = File.Exists(localPath) ? await File.ReadAllTextAsync(localPath, cancellationToken) : "{}";
                var merged = CloudSyncExclusions.MergeRemoteOntoLocal(local, remote);

                var model = JsonSerializer.Deserialize<MainConfigModel>(merged, ConfigServiceBase.JsonOptions)
                            ?? throw new CloudStorageException("invalid_backup", "云端的设置文件读不出来，已停止恢复");

                _synchronizing = true;
                try
                {
                    // 走配置服务落盘再让配置句柄重读，别自己写 settings.json——免得绕过解析与迁移逻辑。
                    _configService.SaveConfig(model);
                    _configHandler.Reload();
                }
                finally
                {
                    _synchronizing = false;
                }

                settingsRestored = true;
            }
        }
        finally
        {
            DeleteDirectory(work);
        }

        CommitSyncState(settingsRestored);
        _logger?.LogInformation("已从云端恢复备份（{Scope}）。", scope);
    }

    /// <summary>同步成功后的簿记：记时间（必要时记设置摘要），并把自己触发的配置事件挡掉。</summary>
    private void CommitSyncState(bool recordSettingsHash)
    {
        _synchronizing = true;
        try
        {
            var config = Config;
            config.LastSyncedAt = DateTimeOffset.Now;
            if (recordSettingsHash)
                config.LastSettingsHash = ComputeSettingsHash();
        }
        finally
        {
            _synchronizing = false;
        }

        lock (_gate)
        {
            _remoteIsNewer = false;
            _lastError = null;
        }

        NotifyState();
    }

    /// <summary>
    ///     删掉被这次上传顶掉的云端旧件。同名可能有好几份（历史上重复上传留下的），一次全清，
    ///     否则空间白占、恢复时也可能挑到旧的那份。
    /// </summary>
    private async Task DeleteReplacedAsync(IEnumerable<CloudFileInfo> previous, string? newFileId,
        CancellationToken cancellationToken)
    {
        if (newFileId is null)
            return;

        foreach (var file in previous)
        {
            if (string.Equals(file.FileId, newFileId, StringComparison.Ordinal))
                continue;

            try
            {
                await _client.DeleteAsync(file.FileId, cancellationToken);
            }
            catch (CloudStorageException exception)
            {
                _logger?.LogDebug(exception, "云端旧备份没删掉，不影响这次备份。");
            }
        }
    }

    private void OnConfigSaved(object? sender, EventArgs e)
    {
        if (_disposed || _synchronizing)
            return;

        if (!Config.Enabled)
        {
            StopAutoUpload();
            return;
        }

        ScheduleAutoUpload(SettingsAutoUploadDelay);

        // 刚打开同步开关：先看一眼云端有没有更新，本机没改动就自动拿下来。
        if (_initialized && !_autoPullChecked && _auth.IsSignedIn)
            _ = SafeCheckAsync(true, CancellationToken.None);
    }

    private void OnBoardChanged(object? sender, EventArgs e)
    {
        if (_disposed || _synchronizing)
            return;

        if (Config.Enabled)
            ScheduleAutoUpload(BoardAutoUploadDelay);
    }

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        if (_disposed || !_initialized)
            return;

        if (!_auth.IsSignedIn)
        {
            _autoPullChecked = false;
            RunOnUiThread(() =>
            {
                StopAutoUpload();
                ResetCloudState();
            });
            return;
        }

        if (Config.Enabled && !_autoPullChecked)
            RunOnUiThread(() => _ = SafeCheckAsync(true, CancellationToken.None));
    }

    private void ScheduleAutoUpload(TimeSpan delay)
    {
        if (_disposed)
            return;

        RunOnUiThread(() =>
        {
            if (_disposed || !Config.Enabled)
                return;

            var now = DateTimeOffset.UtcNow;
            var deadline = CloudSyncPolicy.NextDeadline(_autoUploadDeadline, now, delay);

            // 已经排着一个更早的截止时间就不动它：作业板那 5 秒不能被设置那边的 45 秒顶掉。
            if (_autoUploadDeadline == deadline)
                return;

            // 截止时间可能刚好在这一次改动之前（前一次上传已经到期、计时器还没跑到），
            // 那就立刻再来一次；负的间隔不能交给 DispatcherTimer。
            var interval = deadline - now;
            if (interval < TimeSpan.FromMilliseconds(1))
                interval = TimeSpan.FromMilliseconds(1);

            _autoUpload ??= new DispatcherTimer();
            _autoUpload.Stop();
            _autoUpload.Tick -= OnAutoUploadTick;
            _autoUpload.Tick += OnAutoUploadTick;
            _autoUpload.Interval = interval;
            _autoUploadDeadline = deadline;
            _autoUpload.Start();
        });
    }

    private void StopAutoUpload() => RunOnUiThread(() =>
    {
        _autoUpload?.Stop();
        _autoUploadDeadline = null;
    });

    private void OnAutoUploadTick(object? sender, EventArgs e)
    {
        _autoUpload?.Stop();
        _autoUploadDeadline = null;
        _ = SafeAutoBackupAsync();
    }

    /// <summary>防抖到期后的自动上传：只传真正变过的那部分，失败只记日志。</summary>
    private async Task SafeAutoBackupAsync()
    {
        if (_disposed || IsBusy)
            return;

        var config = Config;
        if (!config.Enabled || !_auth.IsSignedIn)
            return;

        var (boardDirty, settingsDirty) = ComputeDirty(ScopeOf(config));
        var scope = (boardDirty ? CloudSyncScope.Board : CloudSyncScope.None) |
                    (settingsDirty ? CloudSyncScope.Settings : CloudSyncScope.None);
        if (scope == CloudSyncScope.None)
            return;

        SetBusy(true);
        try
        {
            await BackupCoreAsync(scope, CancellationToken.None);
            await CheckAsync(false, true, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            RecordError(exception);
            _logger?.LogWarning(exception, "自动上传云备份失败。");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ResetCloudState()
    {
        lock (_gate)
        {
            _usage = CloudStorageUsage.Empty;
            _remoteBoard = null;
            _remoteSettings = null;
            _remoteUpdatedAt = null;
            _remoteIsNewer = false;
            _lastError = null;
        }

        NotifyState();
    }

    private void SetBusy(bool value)
    {
        lock (_gate)
        {
            if (_isBusy == value)
                return;

            _isBusy = value;
        }

        NotifyState();
    }

    private void RecordError(Exception exception)
    {
        lock (_gate)
        {
            _lastError = exception.Message;
        }

        NotifyState();
    }

    private void NotifyState() => StateChanged?.Invoke(this, EventArgs.Empty);

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }

    private static CloudSyncScope ScopeOf(CloudSyncSettingsConfig config)
    {
        var scope = CloudSyncScope.None;
        if (config.SyncBoard)
            scope |= CloudSyncScope.Board;
        if (config.SyncSettings)
            scope |= CloudSyncScope.Settings;
        return scope;
    }

    private static DateTimeOffset? Later(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
            return right;
        if (right is null)
            return left;
        return left.Value >= right.Value ? left : right;
    }

    private static DateTimeOffset? DirectoryLastWrite(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
                return null;

            DateTimeOffset? latest = null;
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var written = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                if (latest is null || written > latest.Value)
                    latest = written;
            }

            return latest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool DirectoryHasFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory) &&
                   Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>用解出来的备份替换整个目录（先清空再搬，避免留下云端已经没有的旧文件）。</summary>
    private static void ReplaceDirectory(string source, string target)
    {
        if (!Directory.Exists(source))
            return;

        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 单个文件删不掉（被占用）不该中断整次恢复，覆盖同名文件时还会再试一次。
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(target, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                Directory.Delete(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            var folder = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            File.Move(file, destination, true);
        }
    }

    private static string CreateWorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), ProductName, WorkDirectoryName);
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 删不掉就用已存在的目录，反正下面每个文件都是覆盖写。
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 临时目录留在 %TEMP% 里没关系，下次同步会再清一次。
        }
    }
}
