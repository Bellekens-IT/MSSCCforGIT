using System.Windows.Forms;

namespace MSSCCforGIT;

/// <summary>
/// Shows the "git log" history for a single file as a ListView, replacing the plain MessageBoxW
/// output that SccHistory previously used.
/// </summary>
public partial class HistoryForm : Form
{
    private readonly string _fullPath;

    public HistoryForm(string fullPath, string relativePath, IReadOnlyList<HistoryEntry> entries)
    {
        _fullPath = fullPath;

        InitializeComponent();

        Text = "File History";
        headerLabel.Text = System.IO.Path.GetFileName(relativePath);

        PopulateHistoryListView(entries);
    }

    private void PopulateHistoryListView(IReadOnlyList<HistoryEntry> entries)
    {
        foreach (HistoryEntry entry in entries)
        {
            var item = new ListViewItem(entry.Hash);
            item.SubItems.Add(entry.Date);
            item.SubItems.Add(entry.Author);
            item.SubItems.Add(entry.Message);
            historyListView.Items.Add(item);
        }

        if (entries.Count == 0)
        {
            historyListView.Items.Add(new ListViewItem(new[] { "", "", "", "(no history available)" }));
        }
    }

    private void openExplorerButton_Click(object sender, EventArgs e)
    {
        OpenInExplorer(_fullPath);
    }

    /// <summary>
    /// Opens Windows Explorer with the given file pre-selected, using the standard
    /// "explorer.exe /select,&lt;path&gt;" convention.
    /// </summary>
    private static void OpenInExplorer(string fullPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{fullPath}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Best-effort only; not critical if Explorer can't be launched.
        }
    }

    /// <summary>
    /// Convenience helper used by MsscciExports so callers don't need to know about the form's
    /// constructor/ShowDialog details.
    /// </summary>
    public static void ShowHistory(IntPtr owner, string fullPath, string relativePath, IReadOnlyList<HistoryEntry> entries)
    {
        using var form = new HistoryForm(fullPath, relativePath, entries);
        form.ShowDialog(new Win32Window(owner));
    }
}
