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
    private readonly CheckBox _createInfo = new()
    {
        Text = "產生驗證資訊檔（記錄原始檔名、大小及 SHA-256）", AutoSize = true, Checked = true
    };
    private readonly ListBox _partList = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true, AccessibleName = "已選取的文字分片" };
    private readonly Label _partCount = new() { AutoSize = true, Text = "尚未選取分片" };
    private readonly TextBox _infoPath = ReadOnlyPath("驗證資訊檔");
    private readonly Label _infoHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
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

        UpdateInfoHint();
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
        _createInfo.Margin = new Padding(3, 12, 3, 3);
        AddAutoRow(layout, _createInfo);
        AddAutoRow(layout, Note("此大小限制適用於產生的文字檔；郵件附件編碼及信件內容可能增加實際郵件大小。"));
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
        AddAutoRow(layout, SectionLabel("驗證資訊檔（選用）"));
        AddAutoRow(layout, PathRow(_infoPath,
            ActionButton("選擇…", (_, _) => ChooseInfo()),
            ActionButton("清除", (_, _) => { _infoPath.Clear(); UpdateInfoHint(); })));
        _infoHint.Margin = new Padding(3, 5, 3, 3);
        AddAutoRow(layout, _infoHint);
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
        _infoPath.Text = ConversionService.FindVerificationInfo(_selectedParts[0]) ?? "";
        UpdateInfoHint();
    }

    private void ChooseInfo()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "選擇驗證資訊檔", Filter = "驗證資訊檔 (*.json)|*.json|所有檔案 (*.*)|*.*", CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _infoPath.Text = dialog.FileName;
        UpdateInfoHint();
    }

    private void UpdateInfoHint()
    {
        _infoHint.Text = string.IsNullOrWhiteSpace(_infoPath.Text)
            ? "未使用資訊檔：無法驗證內容是否損毀；請確認還原檔名。"
            : "還原時會檢查原始檔大小及 SHA-256。";
    }

    private async Task StartExportAsync()
    {
        if (_activeOperation is not null) return;
        ExportPlan plan;
        try
        {
            long limit = decimal.ToInt64(_partSize.Value * 1_000_000M);
            plan = ConversionService.CreateExportPlan(_sourcePath.Text, _outputDirectory.Text, limit, _createInfo.Checked);
        }
        catch (Exception ex) { ShowError(ex); return; }

        string[] existing = plan.OutputPaths.Where(File.Exists).ToArray();
        if (existing.Length > 0 && MessageBox.Show(this,
                $"有 {existing.Length:N0} 個輸出檔案已存在。確定要覆寫嗎？", "確認覆寫",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;

        await RunOperationAsync(
            (progress, token) => ConversionService.ExportAsync(plan, existing, progress, token),
            $"轉換完成，共產生 {plan.PartPaths.Count:N0} 個文字分片。" +
            (plan.ManifestPath is null ? "" : "\n已產生驗證資訊檔。") + $"\n\n輸出資料夾：{plan.DestinationDirectory}");
    }

    private async Task StartRestoreAsync()
    {
        if (_activeOperation is not null) return;
        if (_selectedParts.Length == 0)
        {
            MessageBox.Show(this, "請先選取同一組的全部文字分片。", "尚未選取分片", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        RestorePlan? plan = null;
        string[] selectedParts = _selectedParts.ToArray();
        string? info = string.IsNullOrWhiteSpace(_infoPath.Text) ? null : _infoPath.Text;
        BeginOperation("正在檢查分片…");
        try
        {
            CancellationToken token = _activeOperation!.Token;
            plan = await Task.Run(() => ConversionService.CreateRestorePlanAsync(selectedParts, info, token), token);
        }
        catch (OperationCanceledException) { _status.Text = "已取消。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { EndOperation(); }
        if (plan is null || _closeWhenDone || IsDisposed) return;

        using var dialog = new SaveFileDialog
        {
            Title = "選擇還原檔案的儲存位置", FileName = plan.SuggestedFileName,
            Filter = "所有檔案 (*.*)|*.*", AddExtension = false, OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        bool exists = File.Exists(dialog.FileName);
        if (exists && MessageBox.Show(this, $"檔案已存在：\n{dialog.FileName}\n\n確定要覆寫嗎？", "確認覆寫",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            return;
        await RunOperationAsync(
            (progress, token) => ConversionService.RestoreAsync(plan, dialog.FileName, exists, progress, token),
            $"檔案已還原：\n{dialog.FileName}" + (plan.Verification is null ? "\n\n未使用驗證資訊檔。" : "\n\n已通過大小及 SHA-256 驗證。"));
    }

    private async Task RunOperationAsync(Func<IProgress<ConversionProgress>, CancellationToken, Task> work, string successMessage)
    {
        BeginOperation("正在準備…");
        var progress = new Progress<ConversionProgress>(value =>
        {
            if (_activeOperation is null || IsDisposed) return;
            int percent = value.TotalBytes == 0 ? 0 : (int)Math.Clamp(value.ProcessedBytes * 100d / value.TotalBytes, 0, 100);
            _progress.Value = percent;
            _status.Text = $"{value.Message}：{percent}%（{FormatBytes(value.ProcessedBytes)} / {FormatBytes(value.TotalBytes)}）";
        });
        bool succeeded = false;
        try
        {
            CancellationToken token = _activeOperation!.Token;
            await Task.Run(() => work(progress, token), token);
            succeeded = true;
            _progress.Value = 100;
            _status.Text = "作業完成。";
        }
        catch (OperationCanceledException) { _status.Text = "已取消。"; }
        catch (Exception ex) { ShowError(ex); }
        finally { EndOperation(); }
        if (succeeded && !_closeWhenDone && !IsDisposed)
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
