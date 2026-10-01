using System.Windows.Forms;

namespace MSSCCforGIT;

/// <summary>
/// A single parsed "git log" entry, used to populate the History form's ListView and the
/// Properties form's "Last Commit" section.
/// </summary>
public sealed record HistoryEntry(string Hash, string Date, string Author, string Message);

/// <summary>
/// A lightweight IWin32Window wrapper so WinForms dialogs can be parented to the raw HWND that EA
/// passes into our Scc* exports, keeping them modal to (and centered on) EA's own window.
/// </summary>
internal sealed class Win32Window : IWin32Window
{
    public IntPtr Handle { get; }

    public Win32Window(IntPtr handle)
    {
        Handle = handle;
    }
}
