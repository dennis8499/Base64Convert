using System.Drawing.Imaging;
using System.Diagnostics;
using System.Reflection;
using Base64Convert.Core;
using Base64Convert.WinForms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using var form = new MainForm();
            // Realize child handles without showing a test window on the desktop.
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-32000, -32000);
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Show();
            Application.DoEvents();
            _ = form.Handle;
            form.PerformLayout();
            var controls = Descendants(form).ToArray();
            var tabs = controls.OfType<TabControl>().Single();
            Check(tabs.TabPages.Count == 2, "Expected export and restore tabs.");
            Check(tabs.TabPages[0].Text == "轉換與拆分" && tabs.TabPages[1].Text == "合併與還原", "Unexpected tabs.");
            var partSize = controls.OfType<NumericUpDown>().Single();
            Check(partSize.Value == 15 && partSize.DecimalPlaces == 2, "The default split size is incorrect.");
            Check(!controls.OfType<CheckBox>().Any(), "Optional verification controls must be removed.");
            Check(controls.OfType<Label>().Any(c => c.Text.Contains("品質 11", StringComparison.Ordinal)), "High-quality automatic compression hint is missing.");
            Check(controls.OfType<Label>().Any(c => c.Text.Contains("一律", StringComparison.Ordinal) && c.Text.Contains("SHA-256", StringComparison.Ordinal)), "Mandatory integrity hint is missing.");
            Check(!controls.OfType<TextBox>().Any(c => c.AccessibleName == "驗證資訊檔"), "External manifest picker must be removed.");
            Check(controls.OfType<Button>().Single(b => b.Text == "取消作業").Enabled == false, "Cancel should initially be disabled.");
            Check(controls.OfType<ListBox>().Single().Items.Count == 0, "The initial part selection should be empty.");
            Check(controls.OfType<Button>().Any(b => b.Text == "開始轉換"), "Export button is missing.");
            Check(controls.OfType<Button>().Any(b => b.Text == "選擇儲存位置並還原"), "Restore button is missing.");

            for (int i = 0; i < tabs.TabPages.Count; i++)
            {
                tabs.SelectedIndex = i;
                Application.DoEvents();
                var page = tabs.TabPages[i];
                string actionText = i == 0 ? "開始轉換" : "選擇儲存位置並還原";
                var action = Descendants(page).OfType<Button>().Single(b => b.Text == actionText);
                Rectangle actionBounds = page.RectangleToClient(action.RectangleToScreen(action.ClientRectangle));
                Check(page.ClientRectangle.Contains(actionBounds), $"The default window clips the '{actionText}' button: page={page.ClientSize}, button={actionBounds}.");
            }

            if (args.Length > 0)
            {
                Directory.CreateDirectory(args[0]);
                for (int i = 0; i < tabs.TabPages.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    form.PerformLayout();
                    Application.DoEvents();
                    using var image = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                    image.Save(Path.Combine(args[0], i == 0 ? "export.png" : "restore.png"), ImageFormat.Png);
                }
            }
            form.Size = form.MinimumSize;
            form.PerformLayout();
            Application.DoEvents();
            for (int i = 0; i < tabs.TabPages.Count; i++)
            {
                tabs.SelectedIndex = i;
                Application.DoEvents();
                var page = tabs.TabPages[i];
                string actionText = i == 0 ? "開始轉換" : "選擇儲存位置並還原";
                var action = Descendants(page).OfType<Button>().Single(b => b.Text == actionText);
                page.ScrollControlIntoView(action);
                Application.DoEvents();
                Rectangle actionBounds = page.RectangleToClient(action.RectangleToScreen(action.ClientRectangle));
                Check(page.ClientRectangle.Contains(actionBounds), $"The minimum-size window cannot reach the '{actionText}' button.");
            }
            Console.WriteLine("PASS V2DefaultsAndLayout");
            OperationLifecycle(form, close: false);
            Console.WriteLine("PASS BackgroundCancellationLifecycle");
            using var closingForm = new MainForm
            {
                StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000),
                ShowInTaskbar = false, Opacity = 0
            };
            closingForm.Show();
            OperationLifecycle(closingForm, close: true);
            Console.WriteLine("PASS CloseWaitsForCleanup");
            Console.WriteLine("Passed: 3, Failed: 0, Skipped: 0");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAIL WinForms smoke test: {ex}");
            return 1;
        }
    }

    private static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static void OperationLifecycle(MainForm form, bool close)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IProgress<ConversionProgress>? capturedProgress = null;
        Func<IProgress<ConversionProgress>, CancellationToken, Task<string?>> work = async (progress, token) =>
        {
            capturedProgress = progress;
            await Task.Run(async () =>
            {
                started.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cleaning.SetResult(); await release.Task; }
            }, token);
            return null;
        };
        var method = typeof(MainForm).GetMethod("RunOperationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task operation = (Task)method.Invoke(form, [work])!;
        try
        {
            PumpUntil(() => started.Task.IsCompleted);
            var controls = Descendants(form).ToArray();
            var tabs = controls.OfType<TabControl>().Single();
            var cancel = controls.OfType<Button>().Single(b => b.Text == "取消作業");
            Check(!tabs.Enabled && cancel.Enabled, "Operation must lock inputs and allow cancellation.");
            bool heartbeat = false;
            form.BeginInvoke(() => heartbeat = true);
            PumpUntil(() => heartbeat);
            Check(!operation.IsCompleted, "Background work did not remain independently active.");
            if (close) form.Close();
            else cancel.PerformClick();
            PumpUntil(() => cleaning.Task.IsCompleted);
            Check(!form.IsDisposed && !tabs.Enabled && !operation.IsCompleted, "UI completed or closed before cleanup finished.");
            Check(!cancel.Enabled, "Cancel must disable after cancellation is requested.");
            release.SetResult();
            PumpUntil(() => operation.IsCompleted && (!close || form.IsDisposed));
            operation.GetAwaiter().GetResult();
            if (!close)
            {
                Check(tabs.Enabled && !cancel.Enabled, "Inputs did not recover after cleanup.");
                string before = string.Join("|", Descendants(form).OfType<Label>().Select(label => label.Text));
                capturedProgress!.Report(new ConversionProgress("stale progress", 1, 1));
                Application.DoEvents();
                Check(string.Join("|", Descendants(form).OfType<Label>().Select(label => label.Text)) == before,
                    "A completed operation changed the UI through a stale progress callback.");
                Check(Descendants(form).OfType<Label>().Any(label => label.Text == "已取消。"), "Cancelled state was not displayed.");
            }
        }
        finally { release.TrySetResult(); }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("WinForms operation did not complete.");
            Application.DoEvents();
            Thread.Yield();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
