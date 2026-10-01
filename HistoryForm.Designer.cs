using System.Drawing;
using System.Windows.Forms;

namespace MSSCCforGIT;

partial class HistoryForm
{
    /// <summary>
    /// Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    /// Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    /// Required method for Designer support - do not modify
    /// the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        headerLabel = new Label();
        historyListView = new ListView();
        commitColumnHeader = new ColumnHeader();
        dateColumnHeader = new ColumnHeader();
        authorColumnHeader = new ColumnHeader();
        messageColumnHeader = new ColumnHeader();
        buttonPanel = new Panel();
        buttonFlowPanel = new FlowLayoutPanel();
        openExplorerButton = new Button();
        closeButton = new Button();
        buttonPanel.SuspendLayout();
        buttonFlowPanel.SuspendLayout();
        SuspendLayout();
        // 
        // headerLabel
        // 
        headerLabel.BackColor = SystemColors.Control;
        headerLabel.Dock = DockStyle.Top;
        headerLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        headerLabel.Location = new Point(0, 0);
        headerLabel.Name = "headerLabel";
        headerLabel.Padding = new Padding(10, 10, 10, 6);
        headerLabel.Size = new Size(735, 34);
        headerLabel.TabIndex = 0;
        headerLabel.Text = "testPackage14.xml";
        // 
        // historyListView
        // 
        historyListView.Columns.AddRange(new ColumnHeader[] { commitColumnHeader, dateColumnHeader, authorColumnHeader, messageColumnHeader });
        historyListView.Dock = DockStyle.Fill;
        historyListView.FullRowSelect = true;
        historyListView.GridLines = true;
        historyListView.Location = new Point(0, 34);
        historyListView.Name = "historyListView";
        historyListView.Size = new Size(735, 398);
        historyListView.TabIndex = 1;
        historyListView.UseCompatibleStateImageBehavior = false;
        historyListView.View = View.Details;
        // 
        // commitColumnHeader
        // 
        commitColumnHeader.Text = "Commit";
        commitColumnHeader.Width = 80;
        // 
        // dateColumnHeader
        // 
        dateColumnHeader.Text = "Date";
        dateColumnHeader.Width = 90;
        // 
        // authorColumnHeader
        // 
        authorColumnHeader.Text = "Author";
        authorColumnHeader.Width = 140;
        // 
        // messageColumnHeader
        // 
        messageColumnHeader.Text = "Message";
        messageColumnHeader.Width = 420;
        // 
        // buttonPanel
        // 
        buttonPanel.BackColor = SystemColors.Control;
        buttonPanel.Controls.Add(buttonFlowPanel);
        buttonPanel.Dock = DockStyle.Bottom;
        buttonPanel.Location = new Point(0, 432);
        buttonPanel.Name = "buttonPanel";
        buttonPanel.Padding = new Padding(0, 12, 20, 12);
        buttonPanel.Size = new Size(735, 48);
        buttonPanel.TabIndex = 2;
        // 
        // buttonFlowPanel
        // 
        buttonFlowPanel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        buttonFlowPanel.AutoSize = true;
        buttonFlowPanel.Controls.Add(openExplorerButton);
        buttonFlowPanel.Controls.Add(closeButton);
        buttonFlowPanel.Location = new Point(499, 12);
        buttonFlowPanel.Name = "buttonFlowPanel";
        buttonFlowPanel.Size = new Size(216, 24);
        buttonFlowPanel.TabIndex = 0;
        // 
        // openExplorerButton
        // 
        openExplorerButton.Location = new Point(0, 0);
        openExplorerButton.Margin = new Padding(0, 0, 10, 0);
        openExplorerButton.Name = "openExplorerButton";
        openExplorerButton.Size = new Size(115, 23);
        openExplorerButton.TabIndex = 0;
        openExplorerButton.Text = "Open in Explorer";
        openExplorerButton.UseVisualStyleBackColor = true;
        openExplorerButton.Click += openExplorerButton_Click;
        // 
        // closeButton
        // 
        closeButton.DialogResult = DialogResult.OK;
        closeButton.Location = new Point(125, 0);
        closeButton.Margin = new Padding(0);
        closeButton.Name = "closeButton";
        closeButton.Size = new Size(91, 23);
        closeButton.TabIndex = 1;
        closeButton.Text = "Close";
        closeButton.UseVisualStyleBackColor = true;
        // 
        // HistoryForm
        // 
        AcceptButton = closeButton;
        BackColor = SystemColors.Control;
        CancelButton = closeButton;
        ClientSize = new Size(735, 480);
        Controls.Add(historyListView);
        Controls.Add(headerLabel);
        Controls.Add(buttonPanel);
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new Size(480, 300);
        Name = "HistoryForm";
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "File History";
        buttonPanel.ResumeLayout(false);
        buttonPanel.PerformLayout();
        buttonFlowPanel.ResumeLayout(false);
        ResumeLayout(false);
    }

    #endregion

    private Label headerLabel;
    private ListView historyListView;
    private ColumnHeader commitColumnHeader;
    private ColumnHeader dateColumnHeader;
    private ColumnHeader authorColumnHeader;
    private ColumnHeader messageColumnHeader;
    private Panel buttonPanel;
    private FlowLayoutPanel buttonFlowPanel;
    private Button openExplorerButton;
    private Button closeButton;
}
