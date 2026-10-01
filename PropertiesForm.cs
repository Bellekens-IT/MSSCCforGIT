using System.Drawing;
using System.Windows.Forms;

namespace MSSCCforGIT;

/// <summary>
/// Shows file properties (path, repository, status, and last commit info) for a single file,
/// replacing the plain MessageBoxW output that SccProperties previously used.
/// </summary>
public partial class PropertiesForm : Form
{
    private readonly string _fullPath;

    public PropertiesForm(
        string fullPath,
        string relativePath,
        string repoPath,
        string status,
        HistoryEntry? lastCommit)
    {
        _fullPath = fullPath;

        InitializeComponent();

        fileNameLabel.Text = System.IO.Path.GetFileName(relativePath);
        Text = $"{fileNameLabel.Text} Properties";
        LoadFileIcon(fullPath);
        repositoryValueLabel.Text = repoPath;
        statusValueLabel.Text = status;

        if (lastCommit != null)
        {
            commitValueLabel.Text = lastCommit.Hash;
            dateValueLabel.Text = lastCommit.Date;
            authorValueLabel.Text = lastCommit.Author;
            messageValueLabel.Text = lastCommit.Message;
        }
        else
        {
            commitValueLabel.Text = "(no commits yet)";
            dateValueLabel.Text = string.Empty;
            authorValueLabel.Text = string.Empty;
            messageValueLabel.Text = string.Empty;
        }
    }

    private void openExplorerButton_Click(object sender, EventArgs e)
    {
        OpenInExplorer(_fullPath);
    }

    /// <summary>
    /// Loads the file's associated shell icon, falling back to a generic file icon
    /// if the file no longer exists or the icon can't be extracted.
    /// </summary>
    private void LoadFileIcon(string fullPath)
    {
        try
        {
            using var icon = System.IO.File.Exists(fullPath)
                ? Icon.ExtractAssociatedIcon(fullPath)
                : SystemIcons.WinLogo;
            if (icon != null)
            {
                fileIconPictureBox.Image = icon.ToBitmap();
            }
        }
        catch
        {
            // Best-effort only; the dialog is still useful without an icon.
        }
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
    public static void ShowProperties(
        IntPtr owner,
        string fullPath,
        string relativePath,
        string repoPath,
        string status,
        HistoryEntry? lastCommit)
    {
        using var form = new PropertiesForm(fullPath, relativePath, repoPath, status, lastCommit);
        form.ShowDialog(new Win32Window(owner));
    }
}
