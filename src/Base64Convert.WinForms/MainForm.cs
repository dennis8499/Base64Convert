using Base64Convert.Core;

namespace Base64Convert.WinForms;

public sealed class MainForm : Form
{
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _sourcePath = ReadOnlyPath("來源檔案");
    private readonly TextBox _outputDirectory = ReadOnlyPath("輸出資料夾");
    private readonly NumericUpDown _partSize = new()
    {
        DecimalPlaces = 2, Increment = 1, Minimum = 0.01M, Maximum = 1_000_000,
        Value = 15, Width = 115, ThousandsSeparator = true, AccessibleName = "每個文字分片上限（MB）"
    };
    private readonly ListBox _partList = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, AccessibleName = "已選取的文字分片" };
    private readonly Label _partCount = new() { AutoSize = true, Text = "尚未選取分片" };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "準備就緒。" };
    private readonly Button _cancel = new() { Text = "取消作業", AutoSize = true, Enabled = false };
    private string[] _selectedParts = [];
    private CancellationTokenSource? _activeOperation;
    private bool _closeWhenDone;

    public MainForm()
    {
        Text = "Base64 檔案轉換工具";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(840, 770);
        MinimumSize = new Size(700, 580);
        Font = SystemFonts.MessageBoxFont!;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 4 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label
        {
            Text = "Base64 檔案轉換工具", AutoSize = true,
            Font = new Font(Font.FontFamily, 15, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12)
        }, 0, 0);

        _tabs.TabPages.Add(BuildExportPage());
        _tabs.TabPages.Add(BuildRestorePage());
        root.Controls.Add(_tabs, 0, 1);
        root.Controls.Add(_status, 0, 2);
        _status.Margin = new Padding(3, 12, 3, 7);

        var progressRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1 };
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        progressRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        progressRow.Controls.Add(_progress, 0, 0);
        progressRow.Controls.Add(_cancel, 1, 0);
        _cancel.Click += (_, _) => CancelOperation();
        root.Controls.Add(progressRow, 0, 3);
        Controls.Add(root);

        FormClosing += OnFormClosing;
    }

    private TabPage BuildExportPage()
    {
        var page = new TabPage("轉換與拆分") { Padding = new Padding(14), AutoScroll = true };
        var layout = CreatePageLayout();
        layout.Dock = DockStyle.Top;
        layout.AutoSize = true;
        layout.AutoSizeMode = AutoSizeMode.GrowAndShrink;

        AddAutoRow(layout, SectionLabel("來源檔案"));
        var chooseSource = ActionButton("選擇檔案…", (_, _) => ChooseSource());
        AddAutoRow(layout, PathRow(_sourcePath, chooseSource));
        AddAutoRow(layout, SectionLabel("輸出資料夾"));
        var chooseOutput = ActionButton("選擇資料夾…", (_, _) => ChooseOutputDirectory());
        AddAutoRow(layout, PathRow(_outputDirectory, chooseOutput));
        AddAutoRow(layout, SectionLabel("每個文字分片的大小上限"));
        var sizeRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        sizeRow.Controls.Add(_partSize);
        sizeRow.Controls.Add(new Label { Text = "MB（1 MB = 1,000,000 位元組）", AutoSize = true, Margin = new Padding(4, 6, 0, 0) });
        AddAutoRow(layout, sizeRow);
        AddAutoRow(layout, Note("Brotli 品質 11 自動壓縮，僅在輸出較小時採用。檔名與 SHA-256 內嵌，還原一律驗證。"));
        AddAutoRow(layout, Note("高品質壓縮可能需要較長時間。大小限制適用於文字檔；郵件附件編碼可能增加實際大小。"));
        var run = ActionButton("開始轉換", async (_, _) => await StartExportAsync());
        run.Margin = new Padding(3, 15, 3, 3);
        AddAutoRow(layout, LeftAligned(run));
        page.Controls.Add(layout);
        return page;
    }

    private TabPage BuildRestorePage()
    {
        var page = new TabPage("合併與還原") { Padding = new Padding(14), AutoScroll = true };
        var layout = CreatePageLayout();
        AddAutoRow(layout, SectionLabel("選取同一組全部文字分片"));
        var selectRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        selectRow.Controls.Add(ActionButton("選取文字分片…", (_, _) => ChooseParts()));
        _partCount.Margin = new Padding(8, 8, 3, 3);
        selectRow.Controls.Add(_partCount);
        AddAutoRow(layout, selectRow);
        _partList.Margin = new Padding(3, 8, 3, 3);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_partList, 0, layout.RowCount++);
        AddAutoRow(layout, Note("僅支援 v2 分片。還原時會自動辨識壓縮方式，並一律驗證原始檔案大小與 SHA-256。"));
        var run = ActionButton("選擇儲存位置並還原", async (_, _) => await StartRestoreAsync());
        run.Margin = new Padding(3, 12, 3, 3);
        AddAutoRow(layout, LeftAligned(run));
        page.Controls.Add(layout);
        return page;
    }

    private void ChooseSource()
    {
        using var dialog = new OpenFileDialog { Title = "選擇要轉換的檔案", Filter = "所有檔案 (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _sourcePath.Text = dialog.FileName;
        if (string.IsNullOrWhiteSpace(_outputDirectory.Text))
            _outputDirectory.Text = Path.GetDirectoryName(dialog.FileName) ?? "";
    }

    private void ChooseOutputDirectory()
    {
        using var dialog = new FolderBrowserDialog { Description = "選擇要儲存文字分片的資料夾", UseDescriptionForTitle = true };
        if (Directory.Exists(_outputDirectory.Text)) dialog.InitialDirectory = _outputDirectory.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) _outputDirectory.Text = dialog.SelectedPath;
    }

    private void ChooseParts()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "選取同一組全部文字分片", Filter = "文字檔 (*.txt)|*.txt|所有檔案 (*.*)|*.*",
            Multiselect = true, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _selectedParts = dialog.FileNames;
        _partList.BeginUpdate();
        try
        {
            _partList.Items.Clear();
            _partList.Items.AddRange(_selectedParts.Select(Path.GetFileName).Cast<object>().ToArray());
        }
        finally { _partList.EndUpdate(); }
        _partCount.Text = $"已選取 {_selectedParts.Length:N0} 個分片";
    }

    private async Task StartExportAsync()
    {
        if (_activeOperation is not null) return;
        string source = _sourcePath.Text, directory = _outputDirectory.Text;
        long limit = decimal.ToInt64(_partSize.Value * 1_000_000M);
        await RunOperationAsync(async (progress, token) =>
        {
            await using ExportPlan plan = await Task.Run(() =>
                ConversionService.CreateExportPlanAsync(source, directory, limit, progress, token), token);
            token.ThrowIfCancellationRequested();
            if (_closeWhenDone || IsDisposed) return null;
            string[] existing = plan.OutputPaths.Where(File.Exists).ToArray();
            if (existing.Length > 0 && MessageBox.Show(this,
                    $"有 {existing.Length:N0} 個輸出檔案已存在。確定要覆寫嗎？", "確認覆寫",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return null;
            ExportResult result = await Task.Run(() => ConversionService.ExportAsync(plan, existing, progress, token), token);
            string method = result.Compression == CompressionMethod.Brotli ? "Brotli（品質 11）" : "未壓縮（壓縮後沒有較小）";
            double savings = result.SavedBytes * 100d / result.UncompressedEncodedBytes;
            return $"轉換完成，共產生 {result.PartCount:N0} 個 v2 文字分片。\n" +
                $"處理方式：{method}\n原始大小：{FormatBytes(result.OriginalBytes)}\n" +
                $"文字總輸出：{FormatBytes(result.EncodedBytes)}\n較 v2 未壓縮輸出節省：{savings:N2}%\n" +
                $"\n檔名、大小及 SHA-256 已內嵌。\n輸出資料夾：{directory}";
        });
    }

    private async Task StartRestoreAsync()
    {
        if (_activeOperation is not null) return;
        if (_selectedParts.Length == 0)
        {
            MessageBox.Show(this, "請先選取同一組的全部文字分片。", "尚未選取分片", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string[] selectedParts = _selectedParts.ToArray();
        await RunOperationAsync(async (progress, token) =>
        {
            RestorePlan plan = await Task.Run(() => ConversionService.CreateRestorePlanAsync(selectedParts, token), token);
            token.ThrowIfCancellationRequested();
            if (_closeWhenDone || IsDisposed) return null;
            using var dialog = new SaveFileDialog
            {
                Title = $"選擇還原位置（原始大小：{FormatBytes(plan.Metadata.OriginalLength)}）", FileName = plan.SuggestedFileName,
                Filter = "所有檔案 (*.*)|*.*", AddExtension = false, OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return null;
            bool exists = File.Exists(dialog.FileName);
            if (exists && MessageBox.Show(this, $"檔案已存在：\n{dialog.FileName}\n\n確定要覆寫嗎？", "確認覆寫",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return null;
            await Task.Run(() => ConversionService.RestoreAsync(plan, dialog.FileName, exists, progress, token), token);
            return $"檔案已還原：\n{dialog.FileName}\n\n已通過大小及 SHA-256 驗證。";
        });
    }

    private async Task RunOperationAsync(Func<IProgress<ConversionProgress>, CancellationToken, Task<string?>> work)
    {
        BeginOperation("正在準備…");
        CancellationTokenSource operation = _activeOperation!;
        var progress = new Progress<ConversionProgress>(value =>
        {
            if (!ReferenceEquals(_activeOperation, operation) || IsDisposed || operation.IsCancellationRequested) return;
            int percent = value.TotalBytes == 0 ? 0 : (int)Math.Clamp(value.ProcessedBytes * 100d / value.TotalBytes, 0, 99);
            _progress.Value = percent;
            _status.Text = $"{value.Message}：{percent}%（{FormatBytes(value.ProcessedBytes)} / {FormatBytes(value.TotalBytes)}）";
        });
        string? successMessage = null;
        try
        {
            successMessage = await work(progress, operation.Token);
            if (successMessage is not null)
            {
                _progress.Value = 100;
                _status.Text = "作業完成。";
            }
            else _status.Text = "已取消。";
        }
        catch (OperationCanceledException) { _status.Text = "已取消。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { EndOperation(); }
        if (successMessage is not null && !_closeWhenDone && !IsDisposed)
            MessageBox.Show(this, successMessage, "作業完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BeginOperation(string message)
    {
        _activeOperation = new CancellationTokenSource();
        _tabs.Enabled = false;
        _cancel.Enabled = true;
        _progress.Value = 0;
        _status.Text = message;
    }

    private void EndOperation()
    {
        _activeOperation?.Dispose();
        _activeOperation = null;
        _tabs.Enabled = true;
        _cancel.Enabled = false;
        if (_closeWhenDone && !IsDisposed) BeginInvoke(Close);
    }

    private void CancelOperation()
    {
        if (_activeOperation is null) return;
        _activeOperation.Cancel();
        _cancel.Enabled = false;
        _status.Text = "正在取消並清理暫存檔案…";
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_activeOperation is null) return;
        e.Cancel = true;
        _closeWhenDone = true;
        CancelOperation();
    }

    private void ShowError(Exception ex)
    {
        _status.Text = "作業未完成。";
        MessageBox.Show(this, ex.Message, "無法完成作業", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static string FormatBytes(long bytes) => bytes < 1_000_000
        ? $"{bytes:N0} 位元組" : $"{bytes / 1_000_000d:N2} MB";

    private static TextBox ReadOnlyPath(string accessibleName) => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AccessibleName = accessibleName, Margin = new Padding(3, 5, 3, 3)
    };

    private static Label SectionLabel(string text) => new()
    {
        Text = text, AutoSize = true, Margin = new Padding(3, 13, 3, 5)
    };

    private static Label Note(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(680, 0), ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 9, 3, 3)
    };

    private static Button ActionButton(string text, EventHandler handler)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(88, 30) };
        button.Click += handler;
        return button;
    }

    private static TableLayoutPanel PathRow(TextBox textBox, params Button[] buttons)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = buttons.Length + 1, RowCount = 1 };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(textBox, 0, 0);
        for (int i = 0; i < buttons.Length; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.Controls.Add(buttons[i], i + 1, 0);
        }
        return row;
    }

    private static FlowLayoutPanel LeftAligned(Control control)
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        row.Controls.Add(control);
        return row;
    }

    private static TableLayoutPanel CreatePageLayout()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 0 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return layout;
    }

    private static void AddAutoRow(TableLayoutPanel layout, Control control)
    {
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(control, 0, layout.RowCount++);
    }

}
