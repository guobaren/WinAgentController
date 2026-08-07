using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Rc.Contracts;

namespace Rc.UiAgent;

public static class DesktopClipboardController
{
    private const string TextFormat = "text/plain";
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public static UiClipboardReadResponse Read(UiClipboardReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTextFormat(request.Format);
        return StaThreadDispatcher.Run(() => new UiClipboardReadResponse(Encoding.UTF8.GetBytes(Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty), TextFormat));
    }

    public static UiClipboardWriteResponse Write(UiClipboardWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTextFormat(request.Format);
        return StaThreadDispatcher.Run(() =>
        {
            var data = request.Data;
            if (data.Length == 0)
            {
                ClearNativeClipboard();
            }
            else
            {
                Clipboard.SetText(Encoding.UTF8.GetString(data));
            }
            return new UiClipboardWriteResponse();
        });
    }

    private static void ClearNativeClipboard()
    {
        // Clipboard.Clear() 以及仅调用 EmptyClipboard() 都曾在该交互会话中留下旧的可读文本。
        // 因此将文本剪贴板替换成一个零长度 CF_UNICODETEXT 值：对本协议仍是零字节，
        // 同时明确通知 Windows 和剪贴板监听程序“最新值为空”，避免旧数据重新出现。
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (!WriteEmptyUnicodeText())
            {
                Thread.Sleep(25);
                continue;
            }

            // CloseClipboard 后再通过 WinForms 验证，避免在已打开的剪贴板上重入 OpenClipboard。
            if (!Clipboard.ContainsText() || Clipboard.GetText().Length == 0)
            {
                return;
            }

            Thread.Sleep(25);
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to replace the Windows clipboard with empty text.");
    }

    private static bool WriteEmptyUnicodeText()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to empty the Windows clipboard.");
            }

            var memory = GlobalAlloc(GmemMoveable, (UIntPtr)sizeof(char));
            if (memory == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to allocate empty clipboard text.");
            }

            var ownershipTransferred = false;
            try
            {
                var text = GlobalLock(memory);
                if (text == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to access empty clipboard text.");
                }

                try
                {
                    Marshal.WriteInt16(text, 0);
                }
                finally
                {
                    GlobalUnlock(memory);
                }

                if (SetClipboardData(CfUnicodeText, memory) == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to set empty clipboard text.");
                }

                ownershipTransferred = true;
                return true;
            }
            finally
            {
                if (!ownershipTransferred)
                {
                    GlobalFree(memory);
                }
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static void EnsureTextFormat(string format)
    {
        if (!string.Equals(format, TextFormat, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Only text/plain clipboard content is supported.", nameof(format));
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);

}
