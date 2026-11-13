using System.Drawing.Imaging;
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
            Check(controls.OfType<CheckBox>().Single().Checked, "Verification must be enabled by default.");
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
                Check(page.ClientRectangle.Contains(actionBounds), $"The default window clips the '{actionText}' button.");
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
            Console.WriteLine("PASS WinForms initialization, tabs and default controls");
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

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
