using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MDashKWallpaperMaker
{
    /// <summary>waifu2x options (model, noise reduction, device, tile size) and a GPU self test.</summary>
    internal sealed class Waifu2xForm : Form
    {
        private readonly Waifu2xOptions _options;
        private readonly ComboBox _cmbModel, _cmbNoise, _cmbDevice, _cmbTile;
        private readonly TextBox _txtTest;
        private readonly Button _btnTest;

        private static readonly int[] TileSizes = { 64, 112, 160, 256, 400 };

        // Device combo: [Auto, GPU adapters..., CPU]
        private readonly System.Collections.Generic.List<GpuAdapter> _adapters = GpuAdapters.HardwareByPreference().OrderBy(a => a.Index).ToList();

        public Waifu2xOptions Result { get; private set; }

        public Waifu2xForm(Waifu2xOptions options)
        {
            _options = options.Clone();
            SuspendLayout();
            Text = "waifu2x - " + AppInfo.Name;
            Icon = AppInfo.LoadIcon();
            Font = new Font("Segoe UI", 9F);
            // Designed at 96 DPI; scaled to the system DPI (ApplicationHighDpiMode = SystemAware).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(560, 470);
            Padding = new Padding(12);

            var intro = new Label
            {
                Dock = DockStyle.Top,
                Height = 64,
                Text = "waifu2x is never used automatically. In the Preview / Adjust window any image can be upscaled " +
                       "(required for images that are too small): choose the factor and click \"Upscale with waifu2x\". " +
                       "Upscaled images are always processed with manual framing.",
            };

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 4, 0, 4) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _cmbModel = MakeCombo("swin_unet / art  (best quality - recommended)", "cunet / art  (faster, lower quality)");
            _cmbModel.SelectedIndex = _options.Model == Waifu2xModel.CunetArt ? 1 : 0;

            _cmbNoise = MakeCombo("None", "Level 0 (light)", "Level 1", "Level 2", "Level 3 (strong)");
            _cmbNoise.SelectedIndex = Math.Clamp(_options.NoiseLevel + 1, 0, 4);

            _cmbDevice = MakeCombo(new[] { "Auto (best working GPU, otherwise CPU)" }
                .Concat(_adapters.Select(a => a.Display)).Concat(new[] { "CPU" }).ToArray());
            int di = 0;
            if (_options.Device == Waifu2xDevice.Cpu) di = _adapters.Count + 1;
            else if (_options.Device == Waifu2xDevice.Gpu && _options.GpuAdapter >= 0)
            {
                int ai = _adapters.FindIndex(a => a.Index == _options.GpuAdapter);
                di = ai >= 0 ? ai + 1 : 0;
            }
            _cmbDevice.SelectedIndex = di;

            _cmbTile = MakeCombo(TileSizes.Select(t => t.ToString()).ToArray());
            int ti = Array.IndexOf(TileSizes, _options.TileSize);
            _cmbTile.SelectedIndex = ti >= 0 ? ti : 3;

            AddRow(grid, "Model:", _cmbModel);
            AddRow(grid, "Noise reduction:", _cmbNoise);
            AddRow(grid, "Device:", _cmbDevice);
            AddRow(grid, "Tile size (px):", _cmbTile);

            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 40,
                ForeColor = Color.DimGray,
                Text = "Noise reduction also removes JPEG artifacts (use 0-1 for clean images, 2-3 for low quality JPEGs). " +
                       "Larger tiles are faster on the GPU but need more video memory.",
            };

            _btnTest = new Button { Text = "Test GPUs", AutoSize = true, MinimumSize = new Size(100, 30), Dock = DockStyle.Top };
            _btnTest.Click += async (_, _) => await RunTest();
            _txtTest = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9F) };
            _txtTest.Text = Waifu2xUpscaler.Available
                ? $"Models: {Waifu2xUpscaler.ModelsDir}"
                : "waifu2x models not found in " + Waifu2xUpscaler.ModelsDir;

            var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(90, 30), DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 30), DialogResult = DialogResult.Cancel };
            ok.Click += (_, _) => Result = ReadOptions();
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
            buttons.Controls.AddRange(new Control[] { cancel, ok });

            var testPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
            testPanel.Controls.Add(_txtTest);
            testPanel.Controls.Add(_btnTest);

            Controls.Add(testPanel);
            Controls.Add(hint);
            Controls.Add(grid);
            Controls.Add(intro);
            Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            ResumeLayout(false);
            PerformLayout();
        }

        private static ComboBox MakeCombo(params string[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 3, 3, 6) };
            c.Items.AddRange(items);
            return c;
        }

        private static void AddRow(TableLayoutPanel grid, string label, Control control)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(3, 7, 3, 3) });
            grid.Controls.Add(control);
        }

        private Waifu2xOptions ReadOptions()
        {
            int di = _cmbDevice.SelectedIndex;
            var o = new Waifu2xOptions
            {
                Model = _cmbModel.SelectedIndex == 1 ? Waifu2xModel.CunetArt : Waifu2xModel.SwinUnetArt,
                NoiseLevel = _cmbNoise.SelectedIndex - 1,
                TileSize = TileSizes[_cmbTile.SelectedIndex],
                Device = Waifu2xDevice.Auto,
                GpuAdapter = -1,
            };
            if (di == _adapters.Count + 1) o.Device = Waifu2xDevice.Cpu;
            else if (di >= 1) { o.Device = Waifu2xDevice.Gpu; o.GpuAdapter = _adapters[di - 1].Index; }
            return o;
        }

        /// <summary>
        /// Tests every GPU with the selected model: creates the DirectML session, runs a tile and compares it with
        /// the CPU result (some drivers fail to start, hang, or silently return wrong values).
        /// </summary>
        private async Task RunTest()
        {
            if (!Waifu2xUpscaler.Available) return;
            _btnTest.Enabled = false;
            UseWaitCursor = true;
            var opt = ReadOptions();
            _txtTest.Text = $"Testing the GPUs with {(opt.Model == Waifu2xModel.SwinUnetArt ? "swin_unet/art" : "cunet/art")} (2x)...";
            try
            {
                string report = await Task.Run(() =>
                {
                    var sb = new System.Text.StringBuilder();
                    string file = System.IO.Path.Combine(Waifu2xUpscaler.ModelsDir,
                        opt.Model == Waifu2xModel.SwinUnetArt ? @"swin_unet\art" : @"cunet\art", "scale2x.onnx");
                    int tile = Waifu2xUpscaler.ValidTileSize(opt.Model, 2, 64);
                    using var cpu = new Microsoft.ML.OnnxRuntime.InferenceSession(file);
                    foreach (var a in GpuAdapters.All)
                    {
                        if (a.IsSoftware) { sb.AppendLine($"{a.Display}: software adapter - skipped"); continue; }
                        var sw = Stopwatch.StartNew();
                        try
                        {
                            using var gpu = Waifu2xUpscaler.CreateGpuSession(file, a.Index);
                            bool same = Waifu2xUpscaler.GpuMatchesCpu(gpu, cpu, tile);
                            sb.AppendLine($"{a.Display}: " + (same
                                ? $"OK ({sw.ElapsedMilliseconds} ms incl. model loading)"
                                : "WRONG RESULTS - will not be used"));
                        }
                        catch (Exception ex)
                        {
                            sb.AppendLine($"{a.Display}: NOT USABLE - {Waifu2xUpscaler.ShortError(ex)}");
                        }
                    }
                    if (GpuAdapters.All.Count == 0) sb.AppendLine("No graphics adapters found.");
                    sb.AppendLine("CPU: always available.");
                    sb.AppendLine("\"Auto\" uses the first GPU marked OK (largest video memory first), otherwise the CPU.");
                    return sb.ToString();
                });
                _txtTest.Text = report;
            }
            catch (Exception ex)
            {
                _txtTest.Text = "Test failed: " + ex.Message;
            }
            finally
            {
                UseWaitCursor = false;
                _btnTest.Enabled = true;
            }
        }
    }
}
