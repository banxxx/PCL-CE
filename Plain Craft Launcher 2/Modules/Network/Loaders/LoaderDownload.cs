using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PCL.Core.Utils;
using PCL.Core.Utils.Threading;

namespace PCL.Network.Loaders;

public class LoaderDownload : ModLoader.LoaderBase
{
    public ModBase.SafeList<PCL.Network.DownloadFile> files;
    private int _fileRemain;
    private readonly object _fileRemainLock = new();
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly AsyncManualResetEvent _pauseGate = new(true);
    // 暂停时取消进行中的下载。继续时更换新实例；旧实例不 Dispose，供中断判断安全读取
    private volatile CancellationTokenSource _pauseCts = new();
    public int FailCount { get; set; }

    /// <summary>
    ///     是否处于暂停状态。暂停会阻止新文件开始下载，并中断正在下载中的文件；
    ///     继续后被中断的文件将重新下载（已下载的部分不保留）。
    /// </summary>
    public bool IsPaused { get; private set; }

    public void Pause()
    {
        if (IsPaused || State > ModBase.LoadState.Loading)
            return;
        IsPaused = true;
        _pauseGate.Reset();
        _pauseCts.Cancel();
        ModBase.Log($"[Download] 下载任务已暂停：{name}");
    }

    public void Resume()
    {
        if (!IsPaused)
            return;
        IsPaused = false;
        _pauseCts = new CancellationTokenSource();
        _pauseGate.Set();
        ModBase.Log($"[Download] 下载任务已继续：{name}");
    }

    public override double Progress
    {
        get => State >= ModBase.LoadState.Finished ? 1 : (files.Any() ? files.Average(file => file.Progress) : 0);
        set => throw new Exception("文件下载不允许指定进度");
    }

    public LoaderDownload(string name, List<PCL.Network.DownloadFile> fileTasks)
    {
        base.name = name;
        files = new ModBase.SafeList<PCL.Network.DownloadFile>(fileTasks ?? new List<PCL.Network.DownloadFile>());
    }

    public void RefreshStat() { }

    public override void Start(object input = null, bool isForceRestart = false)
    {
        if (input is List<PCL.Network.DownloadFile> inputFiles)
            files = new ModBase.SafeList<PCL.Network.DownloadFile>(inputFiles);

        lock (lockState)
        {
            if (State == ModBase.LoadState.Loading)
                return;
            State = ModBase.LoadState.Loading;
        }

        _cancellationTokenSource = new CancellationTokenSource();
        lock (_fileRemainLock)
        {
            _fileRemain = files.Count;
        }

        ModNet.NetManager.Start(this);

        ModBase.RunInNewThread(() => Run(_cancellationTokenSource.Token), $"DL/{Uuid}");
    }

    private void Run(CancellationToken cancellationToken)
    {
        try
        {
            if (!files.Any())
            {
                OnFinish();
                return;
            }

            // 续装优化：先同步扫描本地已存在的文件，命中的直接标记完成，
            // 使总进度与剩余文件数从一开始就反映续装状态
            PreScanReusableFiles(cancellationToken);

            var exceptions = new ConcurrentQueue<Exception>();
            using var semaphore = new SemaphoreSlim(GetMaxParallelFiles());
            var tasks = files.Where(file => file.State < PCL.Network.NetState.Finished)
                .Select(async file =>
                {
                    var entered = false;
                    try
                    {
                        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        entered = true;
                        await ProcessFileAsync(file, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        file.AddError(ex);
                        file.State = PCL.Network.NetState.Interrupted;
                        exceptions.Enqueue(ex);
                        _cancellationTokenSource?.Cancel();
                    }
                    finally
                    {
                        if (entered)
                            semaphore.Release();
                    }
                }).ToList();

            Task.WhenAll(tasks).GetAwaiter().GetResult();
            if (!exceptions.IsEmpty)
                OnFail(exceptions.ToList());
        }
        catch (OperationCanceledException)
        {
            Abort();
        }
        catch (Exception ex)
        {
            OnFail(new List<Exception> { ex });
        }
    }

    private int GetMaxParallelFiles()
    {
        return Math.Max(1, Math.Min(files.Count, Math.Clamp(ModNet.NetTaskThreadLimit, 1, 64)));
    }

    /// <summary>
    ///     下载开始前的本地文件扫描：凡校验通过、可直接复用的文件立即标记完成，
    ///     避免续装时进度与剩余文件数从 0 开始。
    /// </summary>
    private void PreScanReusableFiles(CancellationToken cancellationToken)
    {
        foreach (var file in files.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.State >= PCL.Network.NetState.Finished)
                continue;
            file.RegisterLoader(this);
            if (file.Check?.canUseExistsFile == true && file.Check.Check(file.LocalPath) is null)
                MarkFileFinished(file);
        }
    }

    private void MarkFileFinished(PCL.Network.DownloadFile file)
    {
        file.IsCopy = true;
        file.State = PCL.Network.NetState.Finished;
        try { file.TotalSize = new FileInfo(file.LocalPath).Length; }
        catch (IOException) { file.TotalSize = -1; }
        file.DownloadedBytes = file.TotalSize;
        file.Speed = 0;
        file.ActiveThreads = 0;
        OnFileFinish(file);
    }

    private async Task ProcessFileAsync(PCL.Network.DownloadFile file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        file.RegisterLoader(this);
        await _pauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (State >= ModBase.LoadState.Finished)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(file.LocalPath) ?? throw new IOException("下载路径无效"));
        if (file.Check?.canUseExistsFile == true && file.Check.Check(file.LocalPath) is null)
        {
            MarkFileFinished(file);
            return;
        }

        file.State = PCL.Network.NetState.Connecting;
        var enableParallelChunks = files.Count <= 1;
        for (var retry = 0; retry < 4; retry++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _pauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var pauseCts = _pauseCts;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pauseCts.Token);
            try
            {
                await FileDownloader.DownloadAsync(file.Urls, file.LocalPath, file.UseBrowserUserAgent, file.CustomUserAgent,
                    linkedCts.Token, enableParallelChunks, file).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (pauseCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // 暂停中断了本次下载（部分文件已被清理），等待继续后从头重新下载该文件，不消耗重试次数
                ModBase.Log($"[Download] 下载已中断（暂停）：{file.LocalPath}", ModBase.LogLevel.Debug);
                file.State = PCL.Network.NetState.WaitingToDownload;
                file.Speed = 0;
                file.ActiveThreads = 0;
                retry--;
                await _pauseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (retry < 3)
            {
                ModBase.Log(ex, $"[Download] 重试 {retry + 1}/3：{file.LocalPath}", ModBase.LogLevel.Debug);
                Thread.Sleep(RandomUtils.NextInt(300, 500 + retry * 300));
            }
        }
        try { file.TotalSize = new FileInfo(file.LocalPath).Length; }
        catch (IOException) { file.TotalSize = -1; }
        file.IsUnknownSize = file.TotalSize < 0;
        file.DownloadedBytes = Math.Max(0, file.TotalSize);
        file.Speed = 0;
        file.ActiveThreads = 0;
        file.State = PCL.Network.NetState.Finished;
        OnFileFinish(file);
    }

    public void OnFileFinish(PCL.Network.DownloadFile file)
    {
        lock (_fileRemainLock)
        {
            _fileRemain -= 1;
            if (_fileRemain > 0)
                return;
        }

        OnFinish();
    }

    public void OnFinish()
    {
        RaisePreviewFinish();
        lock (lockState)
        {
            if (State > ModBase.LoadState.Loading)
                return;
            State = ModBase.LoadState.Finished;
        }

        ModNet.NetManager.Finish(this);
    }

    public void OnFileFail(PCL.Network.DownloadFile file)
    {
        var errors = file.Errors;
        OnFail(errors.Count > 0
            ? errors.ToList()
            : new List<Exception> { new Exception($"文件下载失败：{file.LocalPath}") });
    }

    public void OnFail(List<Exception> exList)
    {
        lock (lockState)
        {
            if (State > ModBase.LoadState.Loading)
                return;
            Error = exList.FirstOrDefault() ?? new Exception("未知下载错误");
            State = ModBase.LoadState.Failed;
        }

        FailCount += exList.Count;
        foreach (var file in files.Where(file => file.State < PCL.Network.NetState.Finished))
        {
            file.State = PCL.Network.NetState.Interrupted;
            file.Speed = 0;
            file.ActiveThreads = 0;
            file.AddErrors(exList);
        }

        ModNet.NetManager.Finish(this);
    }

    public override void Abort()
    {
        lock (lockState)
        {
            if (State >= ModBase.LoadState.Finished)
                return;
            State = ModBase.LoadState.Aborted;
        }

        _cancellationTokenSource?.Cancel();
        foreach (var file in files.Where(file => file.State < PCL.Network.NetState.Finished))
        {
            file.State = PCL.Network.NetState.Interrupted;
            file.Speed = 0;
            file.ActiveThreads = 0;
        }

        ModNet.NetManager.Finish(this);
    }
}
