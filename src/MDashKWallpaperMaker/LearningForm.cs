using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MDashKWallpaperMaker
{
    /// <summary>Shows what the app has learned from the user's adjustments and lets them recalibrate / reset.</summary>
    internal sealed class LearningForm : Form
    {
        private readonly AppSettings _settings;
        private readonly Label _lblStats;
        private readonly TextBox _txtReport;
        private readonly Button _btnRecalibrate, _btnReset;
        private readonly CheckBox _chkAuto;

        public LearningForm(AppSettings settings)
        {
            SuspendLayout();
            _settings = settings;
            Text = "Learning - " + AppInfo.Name;
            Icon = AppInfo.LoadIcon();
            Font = new Font("Segoe UI", 9F);
            // Designed at 96 DPI; scaled to the system DPI (ApplicationHighDpiMode = SystemAware).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(720, 490);
            MinimumSize = new Size(560, 380);
            MinimizeBox = false;
            ShowInTaskbar = false;
            Padding = new Padding(10);

            var intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 76,
                Text = "The app records every image you adjust in the preview (\"manual\") and every image you looked at in the " +
                       "preview and kept as it was (\"approved\"), once it is processed. With these examples it refits the automatic " +
                       $"framing to your taste: from {Calibrator.MinSamplesForBias} examples it corrects the headroom / zoom tendency, " +
                       $"from {Calibrator.MinSamplesForFull} it recalibrates everything. A new calibration is only used when it matches your choices better.",
            };

            _lblStats = new Label { Dock = DockStyle.Top, Height = 110, Font = new Font("Segoe UI", 9.5F), Padding = new Padding(0, 6, 0, 0) };

            _chkAuto = new CheckBox
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "Learn automatically after processing (when at least 5 new examples were recorded)",
                Checked = settings.LearnAutomatically,
            };
            _chkAuto.CheckedChanged += (_, _) => _settings.LearnAutomatically = _chkAuto.Checked;

            _txtReport = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9F),
            };

            _btnRecalibrate = new Button { Text = "Recalibrate now", AutoSize = true, MinimumSize = new Size(130, 30) };
            _btnReset = new Button { Text = "Reset to original calibration", AutoSize = true, MinimumSize = new Size(190, 30) };
            var btnFolder = new Button { Text = "Open config folder", AutoSize = true, MinimumSize = new Size(140, 30) };
            var btnClose = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 30), DialogResult = DialogResult.OK };
            _btnRecalibrate.Click += async (_, _) => await Recalibrate();
            _btnReset.Click += (_, _) => ResetCalibration();
            btnFolder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", "\"" + AppPaths.ConfigDir + "\"") { UseShellExecute = true });

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
            buttons.Controls.AddRange(new Control[] { _btnRecalibrate, _btnReset, btnFolder, btnClose });

            Controls.Add(_txtReport);
            Controls.Add(_chkAuto);
            Controls.Add(_lblStats);
            Controls.Add(intro);
            Controls.Add(buttons);
            AcceptButton = btnClose;
            CancelButton = btnClose;

            Shown += async (_, _) => await RefreshStats();

            ResumeLayout(false);
            PerformLayout();
        }

        private async Task RefreshStats()
        {
            _lblStats.Text = "Reading history...";
            var (manual, approved, score) = await Task.Run(() =>
            {
                var h = LearningStore.LoadHistory();
                return (h.Count(r => r.Kind == LearningRecord.KindManual), h.Count(r => r.Kind == LearningRecord.KindApproved), Calibrator.CurrentScore());
            });
            var c = FramingCalibration.Current;
            int user = manual + approved;
            string next = user < Calibrator.MinSamplesForBias
                ? $"{Calibrator.MinSamplesForBias - user} more example(s) needed for the first tendency correction."
                : user < Calibrator.MinSamplesForFull
                    ? $"Tendency correction available. {Calibrator.MinSamplesForFull - user} more example(s) for a full recalibration."
                    : "Full recalibration available.";
            _lblStats.Text =
                $"Your examples:  {manual} adjusted (manual)  +  {approved} approved   =  {user}\n" +
                $"Reference examples (original calibration):  {LearningStore.LoadSeed().Count}\n" +
                $"Calibration in use:  {c.Source}{(c.CreatedUtc.HasValue ? $"  ({c.CreatedUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm})" : "")}\n" +
                $"    {c.Describe()}\n" +
                $"Match with all examples:  {score:P1}\n" +
                next;
            _btnReset.Enabled = !c.IsDefault;
        }

        private async Task Recalibrate()
        {
            _btnRecalibrate.Enabled = false;
            UseWaitCursor = true;
            try
            {
                var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var report = await Task.Run(() => Calibrator.Run(lines.Enqueue));
                foreach (var l in lines) AppendReport(l);
                AppendReport(report.Message);
            }
            catch (Exception ex)
            {
                AppendReport("Recalibration failed: " + ex.Message);
            }
            finally
            {
                UseWaitCursor = false;
                _btnRecalibrate.Enabled = true;
                await RefreshStats();
            }
        }

        private async void ResetCalibration()
        {
            if (MessageBox.Show(this, "Go back to the original calibration?\nThe history of your examples is kept and can be used again later.",
                    AppInfo.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            FramingCalibration.ResetToDefault();
            AppendReport("Calibration reset to the original values.");
            await RefreshStats();
        }

        private void AppendReport(string line) =>
            _txtReport.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
    }
}
