using System.Runtime.InteropServices;

namespace Ccin.Core;

[ComImport]
[Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
    [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

[ComImport]
[Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
internal class VirtualDesktopManagerClass
{
}

/// <summary>
/// 仮想デスクトップの所属判定。公開 COM API だけを使う。
///
/// 「デスクトップの一覧を取る」「切り替える」は非公開 API でしか出来ず、
/// Windows Update のたびに壊れる。CCIN が必要とするのは
/// 「このウィンドウは自分と同じデスクトップにいるか」だけなので、公開 API で足りる。
/// </summary>
internal sealed class VirtualDesktopService
{
    private readonly IVirtualDesktopManager? _manager;

    public VirtualDesktopService()
    {
        try
        {
            _manager = (IVirtualDesktopManager)new VirtualDesktopManagerClass();
        }
        catch
        {
            _manager = null;
        }
    }

    public bool IsAvailable => _manager is not null;

    /// <summary>
    /// ウィンドウの所属デスクトップ。取得できない場合は null。
    /// Program Manager などデスクトップに属さないウィンドウは TYPE_E_ELEMENTNOTFOUND を返す。
    /// </summary>
    public Guid? GetDesktopId(IntPtr hWnd)
    {
        if (_manager is null) return null;
        int hr = _manager.GetWindowDesktopId(hWnd, out Guid id);
        if (hr != 0) return null;
        if (id == Guid.Empty) return null;   // 未割り当て。送信先候補から外す
        return id;
    }

    public bool IsOnCurrentDesktop(IntPtr hWnd)
    {
        if (_manager is null) return true;   // 判定できないなら弾かない
        int hr = _manager.IsWindowOnCurrentVirtualDesktop(hWnd, out int onCurrent);
        if (hr != 0) return false;
        return onCurrent != 0;
    }
}
