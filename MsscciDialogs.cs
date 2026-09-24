using System.Drawing;
using System.Windows.Forms;

namespace MSSCCforGIT;

/// <summary>
/// A single parsed "git log" entry, used to populate the History dialog's ListView.
/// </summary>
internal sealed record HistoryEntry(string Hash, string Date, string Author, string Message);

/// <summary>
/// A lightweight IWin32Window wrapper so WinForms dialogs can be parented to the raw HWND that EA
/// passes into our Scc* exports, keeping them modal to (and centered on) EA's own window.
/// </summary>
internal sealed class Win32Window : IWin32Window
{
    public IntPtr Handle { get; }
    public Win32Window(IntPtr handle) => Handle = handle;
}

/// <summary>
/// WinForms-based replacements for the plain MessageBoxW dialogs previously used by SccHistory and
/// SccProperties, giving a proper ListView (history) and a labeled summary panel (properties)
/// instead of a flat wall of text.
/// </summary>
internal static class MsscciDialogs
{
    public static void ShowHistory(IntPtr owner, string relativePath, IReadOnlyList<HistoryEntry> entries)
    {
        using var form = new Form
        {
            Text = $"History - {relativePath}",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(760, 480),
            MinimumSize = new Size(480, 300),
            Icon = null,
            ShowIcon = false,
        };

        var header = new Label
        {
            Text = relativePath,
            Dock = DockStyle.Top,
            Font = new Font(form.Font.FontFamily, 11f, FontStyle.Bold),
            Padding = new Padding(10, 10, 10, 6),
            AutoSize = false,
            Height = 34,
        };

        var listView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            HideSelection = false,
        };
        listView.Columns.Add("Commit", 80);
        listView.Columns.Add("Date", 90);
        listView.Columns.Add("Author", 140);
        listView.Columns.Add("Message", 420);

        foreach (HistoryEntry entry in entries)
        {
            var item = new ListViewItem(entry.Hash);
            item.SubItems.Add(entry.Date);
            item.SubItems.Add(entry.Author);
            item.SubItems.Add(entry.Message);
            listView.Items.Add(item);
        }

        if (entries.Count == 0)
        {
            listView.Items.Add(new ListViewItem(new[] { "", "", "", "(no history available)" }));
        }

        var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 44 };
        var closeButton = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 28),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        closeButton.Location = new Point(buttonPanel.Width - closeButton.Width - 12, 8);
        buttonPanel.Controls.Add(closeButton);
        buttonPanel.Resize += (_, _) =>
            closeButton.Location = new Point(buttonPanel.Width - closeButton.Width - 12, 8);

        form.Controls.Add(listView);
        form.Controls.Add(header);
        form.Controls.Add(buttonPanel);
        form.AcceptButton = closeButton;
        form.CancelButton = closeButton;

        form.ShowDialog(new Win32Window(owner));
    }

    public static void ShowProperties(
        IntPtr owner,
        string fullPath,
        string relativePath,
        string repoPath,
        string status,
        HistoryEntry? lastCommit)
    {
        using var form = new Form
        {
            Text = "File Properties",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(560, 420),
            MinimumSize = new Size(460, 320),
            FormBorderStyle = FormBorderStyle.Sizable,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowIcon = false,
            AutoScaleMode = AutoScaleMode.Dpi,
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(14),
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddRow(string label, string value, bool bold = false)
        {
            var labelControl = new Label
            {
                Text = label,
                Font = new Font(form.Font, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 4, 8, 4),
            };
            var valueControl = new Label
            {
                Text = value,
                AutoSize = true,
                MaximumSize = new Size(380, 0),
                Font = bold ? new Font(form.Font, FontStyle.Bold) : form.Font,
                Margin = new Padding(0, 4, 0, 4),
            };
            layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(labelControl, 0, layout.RowCount - 1);
            layout.Controls.Add(valueControl, 1, layout.RowCount - 1);
        }

        AddRow("File:", relativePath, bold: true);
        AddRow("Repository:", repoPath);
        AddRow("Status:", status);

        var lastCommitHeader = new Label
        {
            Text = "Last Commit",
            Font = new Font(form.Font.FontFamily, 10f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 4),
        };
        layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(lastCommitHeader, 0, layout.RowCount - 1);
        layout.SetColumnSpan(lastCommitHeader, 2);

        if (lastCommit != null)
        {
            AddRow("Commit:", lastCommit.Hash);
            AddRow("Date:", lastCommit.Date);
            AddRow("Author:", lastCommit.Author);
            AddRow("Message:", lastCommit.Message);
        }
        else
        {
            AddRow("", "(no commits yet)");
        }

        var buttonPanel = new Panel { Dock = DockStyle.Bottom, Height = 48 };

        var closeButton = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Size = new Size(90, 28),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        var openExplorerButton = new Button
        {
            Text = "Open in Explorer",
            Size = new Size(140, 28),
            Anchor = AnchorStyles.Right | AnchorStyles.Top,
        };
        openExplorerButton.Click += (_, _) => OpenInExplorer(fullPath);

        void LayoutButtons()
        {
            closeButton.Location = new Point(buttonPanel.Width - closeButton.Width - 12, 10);
            openExplorerButton.Location = new Point(closeButton.Location.X - openExplorerButton.Width - 8, 10);
        }
        LayoutButtons();
        buttonPanel.Resize += (_, _) => LayoutButtons();

        buttonPanel.Controls.Add(closeButton);
        buttonPanel.Controls.Add(openExplorerButton);

        var scrollPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scrollPanel.Controls.Add(layout);

        form.Controls.Add(scrollPanel);
        form.Controls.Add(buttonPanel);
        form.AcceptButton = closeButton;
        form.CancelButton = closeButton;

        form.ShowDialog(new Win32Window(owner));
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
}
