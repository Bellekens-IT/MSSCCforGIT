using System.Drawing;
using System.Windows.Forms;

namespace MSSCCforGIT;

partial class PropertiesForm
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
        contentTableLayoutPanel = new TableLayoutPanel();
        fileIconPictureBox = new PictureBox();
        fileNameLabel = new Label();
        topSeparator = new Panel();
        repositoryLabel = new Label();
        repositoryValueLabel = new Label();
        statusLabel = new Label();
        statusValueLabel = new Label();
        detailsSeparator = new Panel();
        commitLabel = new Label();
        commitValueLabel = new Label();
        dateLabel = new Label();
        dateValueLabel = new Label();
        authorLabel = new Label();
        authorValueLabel = new Label();
        messageLabel = new Label();
        messageValueLabel = new Label();
        buttonPanel = new Panel();
        buttonFlowPanel = new FlowLayoutPanel();
        openExplorerButton = new Button();
        closeButton = new Button();
        contentTableLayoutPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)fileIconPictureBox).BeginInit();
        buttonPanel.SuspendLayout();
        buttonFlowPanel.SuspendLayout();
        SuspendLayout();
        // 
        // contentTableLayoutPanel
        // 
        contentTableLayoutPanel.BackColor = SystemColors.Control;
        contentTableLayoutPanel.ColumnCount = 2;
        contentTableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));
        contentTableLayoutPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        contentTableLayoutPanel.Controls.Add(fileIconPictureBox, 0, 0);
        contentTableLayoutPanel.Controls.Add(topSeparator, 0, 1);
        contentTableLayoutPanel.Controls.Add(repositoryLabel, 0, 2);
        contentTableLayoutPanel.Controls.Add(repositoryValueLabel, 1, 2);
        contentTableLayoutPanel.Controls.Add(statusLabel, 0, 3);
        contentTableLayoutPanel.Controls.Add(statusValueLabel, 1, 3);
        contentTableLayoutPanel.Controls.Add(detailsSeparator, 0, 4);
        contentTableLayoutPanel.Controls.Add(commitLabel, 0, 5);
        contentTableLayoutPanel.Controls.Add(commitValueLabel, 1, 5);
        contentTableLayoutPanel.Controls.Add(dateLabel, 0, 6);
        contentTableLayoutPanel.Controls.Add(dateValueLabel, 1, 6);
        contentTableLayoutPanel.Controls.Add(authorLabel, 0, 7);
        contentTableLayoutPanel.Controls.Add(authorValueLabel, 1, 7);
        contentTableLayoutPanel.Controls.Add(messageLabel, 0, 8);
        contentTableLayoutPanel.Controls.Add(messageValueLabel, 1, 8);
        contentTableLayoutPanel.Controls.Add(fileNameLabel, 1, 0);
        contentTableLayoutPanel.Dock = DockStyle.Fill;
        contentTableLayoutPanel.Location = new Point(0, 0);
        contentTableLayoutPanel.Name = "contentTableLayoutPanel";
        contentTableLayoutPanel.Padding = new Padding(20, 18, 20, 18);
        contentTableLayoutPanel.RowCount = 9;
        contentTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
        contentTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 21F));
        contentTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        contentTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        contentTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 21F));
        contentTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        contentTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        contentTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        contentTableLayoutPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
        contentTableLayoutPanel.Size = new Size(444, 372);
        contentTableLayoutPanel.TabIndex = 0;
        // 
        // fileIconPictureBox
        // 
        fileIconPictureBox.Anchor = AnchorStyles.Left;
        fileIconPictureBox.Location = new Point(20, 22);
        fileIconPictureBox.Margin = new Padding(0, 4, 10, 4);
        fileIconPictureBox.Name = "fileIconPictureBox";
        fileIconPictureBox.Size = new Size(32, 32);
        fileIconPictureBox.SizeMode = PictureBoxSizeMode.CenterImage;
        fileIconPictureBox.TabIndex = 0;
        fileIconPictureBox.TabStop = false;
        // 
        // fileNameLabel
        // 
        fileNameLabel.Anchor = AnchorStyles.Left;
        fileNameLabel.AutoEllipsis = true;
        fileNameLabel.AutoSize = true;
        fileNameLabel.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
        fileNameLabel.Location = new Point(120, 30);
        fileNameLabel.Margin = new Padding(0, 4, 0, 4);
        fileNameLabel.Name = "fileNameLabel";
        fileNameLabel.Size = new Size(113, 15);
        fileNameLabel.TabIndex = 1;
        fileNameLabel.Text = "testPackage14.xml";
        // 
        // topSeparator
        // 
        topSeparator.BackColor = SystemColors.ControlLight;
        contentTableLayoutPanel.SetColumnSpan(topSeparator, 2);
        topSeparator.Dock = DockStyle.Fill;
        topSeparator.Location = new Point(20, 67);
        topSeparator.Margin = new Padding(0, 9, 0, 9);
        topSeparator.Name = "topSeparator";
        topSeparator.Size = new Size(404, 3);
        topSeparator.TabIndex = 2;
        // 
        // repositoryLabel
        // 
        repositoryLabel.Anchor = AnchorStyles.Left;
        repositoryLabel.AutoSize = true;
        repositoryLabel.Location = new Point(20, 83);
        repositoryLabel.Margin = new Padding(0, 4, 0, 4);
        repositoryLabel.Name = "repositoryLabel";
        repositoryLabel.Size = new Size(66, 15);
        repositoryLabel.TabIndex = 3;
        repositoryLabel.Text = "Repository:";
        // 
        // repositoryValueLabel
        // 
        repositoryValueLabel.Anchor = AnchorStyles.Left;
        repositoryValueLabel.AutoEllipsis = true;
        repositoryValueLabel.AutoSize = true;
        repositoryValueLabel.Location = new Point(120, 83);
        repositoryValueLabel.Margin = new Padding(0, 4, 0, 4);
        repositoryValueLabel.MaximumSize = new Size(410, 0);
        repositoryValueLabel.Name = "repositoryValueLabel";
        repositoryValueLabel.Size = new Size(269, 15);
        repositoryValueLabel.TabIndex = 4;
        repositoryValueLabel.Text = "C:\\Users\\geert\\Documents\\GitHub\\MSSCCforGIT";
        // 
        // statusLabel
        // 
        statusLabel.Anchor = AnchorStyles.Left;
        statusLabel.AutoSize = true;
        statusLabel.Location = new Point(20, 107);
        statusLabel.Margin = new Padding(0, 4, 0, 4);
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(42, 15);
        statusLabel.TabIndex = 5;
        statusLabel.Text = "Status:";
        // 
        // statusValueLabel
        // 
        statusValueLabel.Anchor = AnchorStyles.Left;
        statusValueLabel.AutoSize = true;
        statusValueLabel.Location = new Point(120, 107);
        statusValueLabel.Margin = new Padding(0, 4, 0, 4);
        statusValueLabel.MaximumSize = new Size(410, 0);
        statusValueLabel.Name = "statusValueLabel";
        statusValueLabel.Size = new Size(58, 15);
        statusValueLabel.TabIndex = 6;
        statusValueLabel.Text = "Unaltered";
        // 
        // detailsSeparator
        // 
        detailsSeparator.BackColor = SystemColors.ControlLight;
        contentTableLayoutPanel.SetColumnSpan(detailsSeparator, 2);
        detailsSeparator.Dock = DockStyle.Fill;
        detailsSeparator.Location = new Point(20, 136);
        detailsSeparator.Margin = new Padding(0, 9, 0, 9);
        detailsSeparator.Name = "detailsSeparator";
        detailsSeparator.Size = new Size(404, 3);
        detailsSeparator.TabIndex = 7;
        // 
        // commitLabel
        // 
        commitLabel.Anchor = AnchorStyles.Left;
        commitLabel.AutoSize = true;
        commitLabel.Location = new Point(20, 152);
        commitLabel.Margin = new Padding(0, 4, 0, 4);
        commitLabel.Name = "commitLabel";
        commitLabel.Size = new Size(54, 15);
        commitLabel.TabIndex = 8;
        commitLabel.Text = "Commit:";
        // 
        // commitValueLabel
        // 
        commitValueLabel.Anchor = AnchorStyles.Left;
        commitValueLabel.AutoSize = true;
        commitValueLabel.Location = new Point(120, 152);
        commitValueLabel.Margin = new Padding(0, 4, 0, 4);
        commitValueLabel.MaximumSize = new Size(410, 0);
        commitValueLabel.Name = "commitValueLabel";
        commitValueLabel.Size = new Size(50, 15);
        commitValueLabel.TabIndex = 9;
        commitValueLabel.Text = "5b110a8";
        // 
        // dateLabel
        // 
        dateLabel.Anchor = AnchorStyles.Left;
        dateLabel.AutoSize = true;
        dateLabel.Location = new Point(20, 176);
        dateLabel.Margin = new Padding(0, 4, 0, 4);
        dateLabel.Name = "dateLabel";
        dateLabel.Size = new Size(34, 15);
        dateLabel.TabIndex = 10;
        dateLabel.Text = "Date:";
        // 
        // dateValueLabel
        // 
        dateValueLabel.Anchor = AnchorStyles.Left;
        dateValueLabel.AutoSize = true;
        dateValueLabel.Location = new Point(120, 176);
        dateValueLabel.Margin = new Padding(0, 4, 0, 4);
        dateValueLabel.MaximumSize = new Size(410, 0);
        dateValueLabel.Name = "dateValueLabel";
        dateValueLabel.Size = new Size(65, 15);
        dateValueLabel.TabIndex = 11;
        dateValueLabel.Text = "2026-09-17";
        // 
        // authorLabel
        // 
        authorLabel.Anchor = AnchorStyles.Left;
        authorLabel.AutoSize = true;
        authorLabel.Location = new Point(20, 200);
        authorLabel.Margin = new Padding(0, 4, 0, 4);
        authorLabel.Name = "authorLabel";
        authorLabel.Size = new Size(47, 15);
        authorLabel.TabIndex = 12;
        authorLabel.Text = "Author:";
        // 
        // authorValueLabel
        // 
        authorValueLabel.Anchor = AnchorStyles.Left;
        authorValueLabel.AutoSize = true;
        authorValueLabel.Location = new Point(120, 200);
        authorValueLabel.Margin = new Padding(0, 4, 0, 4);
        authorValueLabel.MaximumSize = new Size(410, 0);
        authorValueLabel.Name = "authorValueLabel";
        authorValueLabel.Size = new Size(87, 15);
        authorValueLabel.TabIndex = 13;
        authorValueLabel.Text = "Geert Bellekens";
        // 
        // messageLabel
        // 
        messageLabel.AutoSize = true;
        messageLabel.Location = new Point(20, 224);
        messageLabel.Margin = new Padding(0, 4, 0, 4);
        messageLabel.Name = "messageLabel";
        messageLabel.Size = new Size(56, 15);
        messageLabel.TabIndex = 14;
        messageLabel.Text = "Message:";
        // 
        // messageValueLabel
        // 
        messageValueLabel.AutoSize = true;
        messageValueLabel.Location = new Point(120, 224);
        messageValueLabel.Margin = new Padding(0, 4, 0, 4);
        messageValueLabel.MaximumSize = new Size(410, 0);
        messageValueLabel.Name = "messageValueLabel";
        messageValueLabel.Size = new Size(224, 15);
        messageValueLabel.TabIndex = 15;
        messageValueLabel.Text = "EA Package addition: 17/09/2026 19:06:55";
        // 
        // buttonPanel
        // 
        buttonPanel.BackColor = SystemColors.Control;
        buttonPanel.Controls.Add(buttonFlowPanel);
        buttonPanel.Dock = DockStyle.Bottom;
        buttonPanel.Location = new Point(0, 372);
        buttonPanel.Name = "buttonPanel";
        buttonPanel.Padding = new Padding(0, 12, 20, 12);
        buttonPanel.Size = new Size(444, 48);
        buttonPanel.TabIndex = 1;
        // 
        // buttonFlowPanel
        // 
        buttonFlowPanel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        buttonFlowPanel.AutoSize = true;
        buttonFlowPanel.Controls.Add(openExplorerButton);
        buttonFlowPanel.Controls.Add(closeButton);
        buttonFlowPanel.Location = new Point(208, 12);
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
        // PropertiesForm
        // 
        AcceptButton = closeButton;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = SystemColors.Control;
        CancelButton = closeButton;
        ClientSize = new Size(444, 420);
        Controls.Add(contentTableLayoutPanel);
        Controls.Add(buttonPanel);
        Font = new Font("Segoe UI", 9F);
        MaximizeBox = false;
        MinimizeBox = false;
        MinimumSize = new Size(460, 320);
        Name = "PropertiesForm";
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "File Properties";
        contentTableLayoutPanel.ResumeLayout(false);
        contentTableLayoutPanel.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)fileIconPictureBox).EndInit();
        buttonPanel.ResumeLayout(false);
        buttonPanel.PerformLayout();
        buttonFlowPanel.ResumeLayout(false);
        ResumeLayout(false);
    }

    #endregion

    private TableLayoutPanel contentTableLayoutPanel;
    private PictureBox fileIconPictureBox;
    private Label fileNameLabel;
    private Panel topSeparator;
    private Label repositoryLabel;
    private Label repositoryValueLabel;
    private Label statusLabel;
    private Label statusValueLabel;
    private Panel detailsSeparator;
    private Label commitLabel;
    private Label commitValueLabel;
    private Label dateLabel;
    private Label dateValueLabel;
    private Label authorLabel;
    private Label authorValueLabel;
    private Label messageLabel;
    private Label messageValueLabel;
    private Panel buttonPanel;
    private FlowLayoutPanel buttonFlowPanel;
    private Button openExplorerButton;
    private Button closeButton;
}
