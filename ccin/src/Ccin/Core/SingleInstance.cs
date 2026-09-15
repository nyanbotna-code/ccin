namespace Ccin.Core;

/// <summary>
/// 多重起動制御。プロセスは 1 個に集約し、2 個目以降は既存プロセスへ合図して自分は消える。
///
/// ユーザーから見た要求は「仮想デスクトップごとに 1 個」だが、プロセスを分けると
/// 表示名と番号を全ウィンドウで共有する仕組みを別途作ることになる。
/// そこで「1 プロセス + デスクトップごとに窓 1 枚」に倒す。窓の管理は UI 層の責務。
///
/// 名前空間を Local\ にしているのは、ログオンセッションごとに独立させるため。
/// Global\ にすると別ユーザーの CCIN と衝突する。
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\CcinSingleInstance";
    private const string ActivateEventName = @"Local\CcinActivate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly ManualResetEvent _stop = new(false);
    private Thread? _listener;
    private bool _disposed;

    /// <summary>常駐プロセスとして動くか。false なら既存プロセスへ合図して終了する。</summary>
    public bool IsPrimary { get; }

    /// <summary>
    /// 別プロセスから起動要求が来た。**ワーカースレッドから呼ばれる**ので、
    /// UI に触るなら受け手側でマーシャリングすること。
    /// </summary>
    public event Action? ActivationRequested;

    private SingleInstance(Mutex mutex, bool isPrimary, EventWaitHandle activate)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
        _activate = activate;
    }

    public static SingleInstance Acquire()
    {
        // 生成と取得を 1 回の呼び出しで済ませる。別々にすると 2 プロセスが
        // 同時起動したときに両方が primary になりうる
        var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        return new SingleInstance(mutex, createdNew, activate);
    }

    /// <summary>2 個目以降のプロセスが、常駐プロセスへ「窓を出せ」と合図する。</summary>
    public void SignalPrimary() => _activate.Set();

    /// <summary>常駐プロセスが合図の待ち受けを始める。</summary>
    public void StartListening()
    {
        if (!IsPrimary || _listener is not null) return;

        _listener = new Thread(Listen)
        {
            IsBackground = true,
            Name = "ccin-activate-listener",
        };
        _listener.Start();
    }

    private void Listen()
    {
        WaitHandle[] handles = [_activate, _stop];
        while (true)
        {
            int index = WaitHandle.WaitAny(handles);
            if (index == 1) return;      // 停止要求
            ActivationRequested?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stop.Set();
        _listener?.Join(1000);

        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }

        _activate.Dispose();
        _mutex.Dispose();
        _stop.Dispose();
    }
}
