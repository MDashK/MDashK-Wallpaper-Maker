using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MDashKWallpaperMakerMobile
{
    /// <summary>
    /// Optional preview / adjust window. Shows the wallpaper that will be produced and lets the user change zoom and
    /// position (drag with the mouse, mouse wheel to zoom). Any change is stored as a manual framing for that file;
    /// files never opened here are processed with the automatic framing.
    /// </summary>
    internal sealed class PreviewForm : Form
    {
        private const int PreviewHeight = 960;                       // preview canvas height (width follows the phone ratio)
        private readonly int PreviewW;
        private readonly int PreviewH;
        private readonly double PreviewFactor;                       // preview pixels per wallpaper pixel
        private readonly int PreviewSourceMaxWidth;                  // enough for the maximum zoom at preview resolution
        private readonly TargetSize _target;

        private readonly IList<string> _files;
        private readonly FramingSession _session;
        private readonly bool _smart;
        private readonly double _squareTolerance;
        private int _index;

        // Current image state
        private string _path;
        private int _ow, _oh;
        private RgbImage _previewSource;          // original reduced to at most PreviewSourceMaxWidth
        private double _previewSourceFactor;      // previewSource pixels per original pixel
        private ImageAnalysis _analysis;
        private FramingPlan _auto, _plan;
        private bool _plannable;
        private string _staticMessage;            // for images that cannot be framed
        private Bitmap _staticBitmap;

        // Render cache
        private RgbImage _scaledCache;
        private double _scaledCacheScale = -1;
        private Bitmap _frame;

        private CancellationTokenSource _loadCts;
        private bool _loading;

        // UI
        private readonly PreviewCanvas _canvas;
        private readonly Label _lblFile, _lblInfo, _lblZoom;
        private readonly TrackBar _zoomBar;
        private readonly Button _btnPrev, _btnNext, _btnAuto, _btnCenter, _btnCenterH, _btnAccept;
        private readonly CheckBox _chkDetections, _chkSkip, _chkAi;
        private readonly Label _lblManual;
        private bool _updatingUi;

        // waifu2x (only for images that are too small)
        private readonly Waifu2xOptions _w2xOptions;
        private readonly FlowLayoutPanel _w2xBar;
        private readonly ComboBox _cmbFactor;
        private readonly Button _btnUpscale, _btnUndoUpscale;
        private readonly Label _lblW2x;
        private bool _tooSmall, _upscaling;
        private CancellationTokenSource _upscaleCts;

        private Point _dragStart;
        private double _dragPlanX, _dragPlanY;
        private bool _dragging;

        public PreviewForm(IList<string> files, int index, FramingSession session, bool smart, double squareTolerance, TargetSize target,
                           Waifu2xOptions w2xOptions, int upscaleFactor)
        {
            _target = target;
            _w2xOptions = w2xOptions?.Clone() ?? new Waifu2xOptions();
            PreviewFactor = PreviewHeight / (double)target.Height;
            PreviewH = PreviewHeight;
            PreviewW = Math.Max(1, (int)Math.Round(target.Width * PreviewFactor));
            PreviewSourceMaxWidth = (int)Math.Ceiling(PreviewW * WallpaperProcessor.MaxManualZoom);
            SuspendLayout();
            _files = files;
            _index = Math.Clamp(index, 0, files.Count - 1);
            _session = session;
            _smart = smart;
            _squareTolerance = squareTolerance;

            Text = "Preview / Adjust - " + AppInfo.Name;
            Text += $"  [{target}]";
            Icon = AppInfo.LoadIcon();
            Font = new Font("Segoe UI", 9F);
            // Designed at 96 DPI; scaled to the system DPI (ApplicationHighDpiMode = SystemAware).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(1000, 900);
            MinimumSize = new Size(760, 560);
            KeyPreview = true;

            _lblFile = new Label { Dock = DockStyle.Top, Height = 26, Font = new Font("Segoe UI", 10.5F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0), AutoEllipsis = true };
            _lblInfo = new Label { Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0), ForeColor = Color.DimGray, AutoEllipsis = true };

            _canvas = new PreviewCanvas { Dock = DockStyle.Fill, BackColor = Color.FromArgb(40, 40, 40) };
            _canvas.Paint += OnCanvasPaint;
            _canvas.MouseDown += OnCanvasMouseDown;
            _canvas.MouseMove += OnCanvasMouseMove;
            _canvas.MouseUp += (_, _) => { _dragging = false; _canvas.Cursor = Cursors.Default; };
            _canvas.MouseWheel += OnCanvasMouseWheel;
            _canvas.MouseDoubleClick += (_, _) => ResetToAuto();
            _canvas.Resize += (_, _) => _canvas.Invalidate();

            // ---- Bottom controls
            _btnPrev = new Button { Text = "◀ Previous", AutoSize = true, MinimumSize = new Size(100, 30) };
            _btnNext = new Button { Text = "Next ▶", AutoSize = true, MinimumSize = new Size(100, 30) };
            _btnAuto = new Button { Text = "Reset to auto", AutoSize = true, MinimumSize = new Size(110, 30) };
            _btnCenter = new Button { Text = "Center ↕", AutoSize = true, MinimumSize = new Size(80, 30) };
            _btnCenterH = new Button { Text = "Center ↔", AutoSize = true, MinimumSize = new Size(80, 30) };
            _btnAccept = new Button { Text = "Use this framing", AutoSize = true, MinimumSize = new Size(120, 30) };
            var btnClose = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 30) };
            btnClose.Click += (_, _) => Close(); // not a modal dialog any more: DialogResult would not close it
            _btnPrev.Click += (_, _) => Navigate(-1);
            _btnNext.Click += (_, _) => Navigate(+1);
            _btnAuto.Click += (_, _) => ResetToAuto();
            _btnCenter.Click += (_, _) => CenterVertically();
            _btnCenterH.Click += (_, _) => CenterHorizontally();
            _btnAccept.Click += (_, _) => { if (_plan != null) { _session.SetManual(_path, _plan); UpdateInfo(); } };

            var tip = new ToolTip();
            tip.SetToolTip(_btnAccept, "Stores the current framing as manual for this file\n(needed e.g. for square images, which are otherwise skipped).");
            tip.SetToolTip(_btnCenter, "Centres the image vertically, keeping the current zoom and horizontal position.");
            tip.SetToolTip(_btnCenterH, "Centres the image horizontally (the visible part when zoomed in), keeping the current zoom and vertical position.");
            tip.SetToolTip(_btnAuto, "Discards the manual framing of this file and goes back to the automatic one (also: double-click the preview).");

            _zoomBar = new TrackBar
            {
                Minimum = 100,
                Maximum = (int)(WallpaperProcessor.MaxManualZoom * 100),
                TickFrequency = 25,
                SmallChange = 1,
                LargeChange = 10,
                Width = 260,
                AutoSize = false,
                Height = 30,
            };
            _zoomBar.ValueChanged += (_, _) =>
            {
                if (_updatingUi || _plan == null) return;
                ZoomAround(_zoomBar.Value / 100.0 * (_target.Width / (double)_ow) / _plan.Scale,
                           new PointF(_target.Width / 2f, _target.Height / 2f));
            };
            _lblZoom = new Label { AutoSize = true, Margin = new Padding(0, 8, 12, 0), Text = "Zoom" };

            _chkDetections = new CheckBox { Text = "Show detections", AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
            _chkDetections.CheckedChanged += (_, _) => _canvas.Invalidate();
            _chkSkip = new CheckBox { Text = "Skip this image", AutoSize = true, Margin = new Padding(12, 7, 3, 3) };
            _chkSkip.CheckedChanged += (_, _) => { if (!_updatingUi && _path != null) { _session.SetExcluded(_path, _chkSkip.Checked); UpdateInfo(); } };

            _chkAi = new CheckBox { Text = "AI framing", AutoSize = true, Margin = new Padding(12, 7, 3, 3), Enabled = smart };
            _chkAi.CheckedChanged += (_, _) =>
            {
                if (_updatingUi || _path == null) return;
                _session.SetAiDisabled(_path, !_chkAi.Checked);
                LoadCurrent(); // recompute the automatic framing with / without AI
            };
            tip.SetToolTip(_chkAi, smart
                ? "Use the anime detection (AI) to frame this image automatically.\nUnchecked: classic rules for this image only."
                : "Smart framing (AI) is switched off in the main window.");

            _lblManual = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 8, 0),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = Color.FromArgb(46, 125, 50),
                Text = "✔  \"Use this framing\" active - this image will be processed with this MANUAL framing (not the automatic one).",
                Visible = false,
            };

            // ---- waifu2x bar (visible only for images that are too small, or already upscaled)
            _cmbFactor = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 60, Margin = new Padding(3, 6, 3, 3) };
            _cmbFactor.Items.AddRange(Waifu2xUpscaler.Factors.Select(f => f + "x").ToArray());
            int fi = Array.IndexOf(Waifu2xUpscaler.Factors, upscaleFactor);
            _cmbFactor.SelectedIndex = fi >= 0 ? fi : 0;
            _btnUpscale = new Button { Text = "Upscale with waifu2x", AutoSize = true, MinimumSize = new Size(150, 30) };
            _btnUpscale.Click += (_, _) => UpscaleCurrent();
            _btnUndoUpscale = new Button { Text = "Undo waifu2x", AutoSize = true, MinimumSize = new Size(110, 30), Visible = false };
            _btnUndoUpscale.Click += (_, _) => { if (_path != null && !_upscaling) { _session.ClearUpscale(_path); LoadCurrent(); } };
            _lblW2x = new Label { AutoSize = true, Margin = new Padding(10, 9, 3, 3), ForeColor = Color.DimGray };
            _w2xBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(6, 4, 6, 0), Visible = false, BackColor = Color.FromArgb(232, 240, 254) };
            _w2xBar.Controls.AddRange(new Control[]
            {
                new Label { Text = "waifu2x:", AutoSize = true, Margin = new Padding(3, 9, 0, 3), Font = new Font(Font, FontStyle.Bold) },
                _cmbFactor, _btnUpscale, _btnUndoUpscale, _lblW2x,
            });
            tip.SetToolTip(_btnUpscale, $"Upscales this image with waifu2x ({_w2xOptions.Describe()}).\n" +
                                        "Options: \"waifu2x\" button in the main window.");

            var bottom1 = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(6, 4, 6, 0) };
            bottom1.Controls.AddRange(new Control[] { new Label { Text = "Zoom:", AutoSize = true, Margin = new Padding(3, 8, 0, 0) }, _zoomBar, _lblZoom, _btnCenter, _btnCenterH, _btnAuto, _btnAccept, _chkAi, _chkDetections, _chkSkip });
            var bottom2 = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(6, 2, 6, 6) };
            bottom2.Controls.AddRange(new Control[] { _btnPrev, _btnNext, new Label { AutoSize = true, Margin = new Padding(12, 8, 12, 0), ForeColor = Color.DimGray, Text = "Drag = move   |   Mouse wheel = zoom   |   Double-click = auto   |   ←/→ = previous/next" }, btnClose });

            Controls.Add(_canvas);
            Controls.Add(_lblManual);
            Controls.Add(_w2xBar);
            Controls.Add(bottom1);
            Controls.Add(bottom2);
            Controls.Add(_lblInfo);
            Controls.Add(_lblFile);
            AcceptButton = btnClose;
            CancelButton = btnClose;

            Shown += (_, _) => LoadCurrent();
            FormClosed += (_, _) => { _loadCts?.Cancel(); _upscaleCts?.Cancel(); _frame?.Dispose(); _staticBitmap?.Dispose(); };

            ResumeLayout(false);
            PerformLayout();
        }

        /// <summary>
        /// Sizes and centres the window on the working area of <paramref name="screen"/> (the screen without the taskbar),
        /// so it never goes under the taskbar whatever the resolution / Windows scaling.
        /// </summary>
        public void PlaceOn(Screen screen)
        {
            var wa = screen.WorkingArea;
            int w = Math.Max(Math.Min(MinimumSize.Width, wa.Width), (int)(wa.Width * 0.55));
            int h = Math.Max(Math.Min(MinimumSize.Height, wa.Height), (int)(wa.Height * 0.92));
            w = Math.Min(w, wa.Width);
            h = Math.Min(h, wa.Height);
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
        }

        /// <summary>←/→ change image (arrow keys never reach KeyDown when they are used for focus navigation).</summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!(ActiveControl is TrackBar))
            {
                if (keyData == Keys.Left) { Navigate(-1); return true; }
                if (keyData == Keys.Right) { Navigate(+1); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ================================================================== Loading

        private void Navigate(int delta)
        {
            int next = _index + delta;
            if (next < 0 || next >= _files.Count || _loading || _upscaling) return;
            _index = next;
            LoadCurrent();
        }

        private async void LoadCurrent()
        {
            _loadCts?.Cancel();
            var cts = _loadCts = new CancellationTokenSource();
            _loading = true;
            _path = _files[_index];
            _plan = _auto = null;
            _analysis = null;
            _previewSource = null;
            _scaledCache = null;
            _scaledCacheScale = -1;
            _staticMessage = null;
            _staticBitmap?.Dispose();
            _staticBitmap = null;
            _lblFile.Text = $"[{_index + 1}/{_files.Count}]  {Path.GetFileName(_path)}";
            bool useAi = _session.UsesAi(_path, _smart);
            _updatingUi = true;
            _chkAi.Checked = useAi;
            _updatingUi = false;
            _lblManual.Visible = _session.GetManual(_path) != null;
            _lblInfo.Text = "Loading" + (useAi ? " and analysing (anime detection)..." : "...");
            UpdateButtons();
            _canvas.Invalidate();

            try
            {
                string path = _path;
                bool smart = useAi;
                var result = await Task.Run(() => Prepare(path, smart, cts.Token), cts.Token);
                if (cts.IsCancellationRequested || IsDisposed) return;

                (_ow, _oh, _previewSource, _previewSourceFactor, _analysis, _auto, _plannable, _staticMessage, _staticBitmap) = result;
                _plan = _session.GetManual(_path)?.ConvertTo(_target, _ow, _oh, WallpaperProcessor.MaxManualZoom) ?? _auto?.Clone();
                _tooSmall = !_plannable && WallpaperProcessor.IsTooSmall(_ow, _oh, _target);
                // waifu2x images are always processed with manual framing.
                if (_plannable && _plan != null && _session.GetUpscale(_path) != null && _session.GetManual(_path) == null)
                    _session.SetManual(_path, _plan);
                if (_plannable && _analysis != null) _session.MarkViewed(_path);
                RenderFrame();
                UpdateInfo();
            }
            catch (OperationCanceledException)
            {
                // navigated away
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested) _lblInfo.Text = "Cannot load this image: " + ex.Message;
            }
            finally
            {
                if (_loadCts == cts) _loading = false;
                UpdateButtons();
                _canvas.Invalidate();
            }
        }

        private (int, int, RgbImage, double, ImageAnalysis, FramingPlan, bool, string, Bitmap) Prepare(string path, bool smart, CancellationToken token)
        {
            var original = _session.LoadSource(path); // the waifu2x version when there is one
            token.ThrowIfCancellationRequested();
            int ow = original.Width, oh = original.Height;

            string tooSmall = WallpaperProcessor.SkipReason(ow, oh, _squareTolerance, hasManualPlan: true, _target);
            if (tooSmall != null)
                return (ow, oh, null, 1, null, null, false, tooSmall, null);

            if (!WallpaperProcessor.UsesPlan(ow, oh, _target))
            {
                // Narrow and tall: fixed "mirror on the right" layout, just show the result.
                var proc = new WallpaperProcessor(null) { Target = _target };
                var r = proc.Process(original);
                if (r.Status != ProcessStatus.Processed)
                    return (ow, oh, null, 1, null, null, false, r.Reason, null);
                var small = r.Image.Resize(PreviewW, PreviewH, -0.5);
                return (ow, oh, null, 1, null, null, false, "Narrow and tall image: fixed layout (image on the left + blurred mirror on the right), no adjustments.", small.ToBitmap());
            }

            ImageAnalysis analysis = null;
            FramingPlan auto = null;
            if (smart)
            {
                var models = ModelHub.Get();
                if (models != null)
                {
                    analysis = _session.GetAnalysis(path);
                    if (analysis == null)
                    {
                        analysis = SmartFramer.Analyze(original, models);
                        _session.StoreAnalysis(path, analysis);
                    }
                    auto = SmartFramer.AutoPlan(analysis, null, _target);
                }
            }
            auto ??= WallpaperProcessor.ClassicPlan(ow, oh, _target);
            token.ThrowIfCancellationRequested();

            double f = Math.Min(1.0, PreviewSourceMaxWidth / (double)ow);
            var src = f < 1 ? original.Resize(PreviewSourceMaxWidth, Math.Max(1, (int)Math.Round(oh * f))) : original;
            f = src.Height / (double)oh;
            return (ow, oh, src, f, analysis, auto, true, null, null);
        }

        // ================================================================== waifu2x

        private async void UpscaleCurrent()
        {
            // Too small images, or images already upscaled (to redo it with another factor), always from the original.
            // Available for every image (forced upscale); the processing itself never upscales automatically.
            if (_path == null || _upscaling || _loading || _ow == 0) return;
            int factor = Waifu2xUpscaler.Factors[Math.Max(0, _cmbFactor.SelectedIndex)];
            string path = _path;
            var opt = _w2xOptions.Clone();
            var cts = _upscaleCts = new CancellationTokenSource();
            _upscaling = true;
            UpdateButtons();
            _lblW2x.Text = $"Upscaling {factor}x with waifu2x ({opt.Describe()})...";
            try
            {
                var (info, message) = await Task.Run(() =>
                {
                    var original = RgbImage.Load(path);
                    int ow = original.Width, oh = original.Height;
                    if (WallpaperProcessor.IsTooSmall(ow * factor, oh * factor, _target))
                        return ((UpscaleInfo)null, $"{factor}x is not enough: {ow * factor}x{oh * factor} would still be too small for {_target}. Choose a larger factor.");
                    var sw = Stopwatch.StartNew();
                    var up = Waifu2xUpscaler.Shared.Upscale(original, factor, opt,
                        (done, total) => BeginInvoke(new Action(() => _lblW2x.Text = $"Upscaling {factor}x with waifu2x... {done}/{total} tiles")),
                        cts.Token);
                    Directory.CreateDirectory(FramingSession.TempDir);
                    string file = Path.Combine(FramingSession.TempDir, Guid.NewGuid().ToString("N") + ".png");
                    up.SavePng(file);
                    var u = new UpscaleInfo { TempFile = file, Factor = factor, Width = up.Width, Height = up.Height, Options = opt.Describe() };
                    string warn = Waifu2xUpscaler.Shared.LastWarning;
                    return (u, $"Upscaled {ow}x{oh} -> {up.Width}x{up.Height} in {sw.Elapsed.TotalSeconds:F1} s on {Waifu2xUpscaler.Shared.LastDevice}." +
                               (warn != null ? "  Note: " + warn : ""));
                }, cts.Token);

                _lblW2x.Text = message;
                if (info == null || IsDisposed) return;
                info.Note = message;
                _session.ClearManual(path);
                _session.SetUpscale(path, info);
                if (_path == path) LoadCurrent();
            }
            catch (OperationCanceledException)
            {
                // window closed
            }
            catch (Exception ex)
            {
                if (!IsDisposed) _lblW2x.Text = "waifu2x failed: " + ex.Message;
            }
            finally
            {
                _upscaling = false;
                if (!IsDisposed) UpdateButtons();
            }
        }

        // ================================================================== Editing

        private void ResetToAuto()
        {
            if (_auto == null) return;
            _plan = _auto.Clone();
            if (_session.GetUpscale(_path) != null) _session.SetManual(_path, _plan); // waifu2x images stay manual
            else _session.ClearManual(_path);
            RenderFrame();
            UpdateInfo();
        }

        private void CenterHorizontally()
        {
            if (_plan == null || !_plannable) return;
            _plan.X = (_target.Width - _ow * _plan.Scale) / 2;
            PlanEdited();
        }

        private void CenterVertically()
        {
            if (_plan == null || !_plannable) return;
            _plan.Y = (_target.Height - _oh * _plan.Scale) / 2;
            PlanEdited();
        }

        private void PlanEdited()
        {
            _plan.Clamp(_ow, _oh, WallpaperProcessor.MaxManualZoom);
            _session.SetManual(_path, _plan);
            RenderFrame();
            UpdateInfo();
        }

        private void ZoomAround(double factor, PointF canvasPoint)
        {
            if (_plan == null || !_plannable) return;
            double ox = (canvasPoint.X - _plan.X) / _plan.Scale, oy = (canvasPoint.Y - _plan.Y) / _plan.Scale;
            double minScale = _target.Width / (double)_ow;
            double newScale = Math.Clamp(_plan.Scale * factor, minScale, minScale * WallpaperProcessor.MaxManualZoom);
            _plan.Scale = newScale;
            _plan.X = canvasPoint.X - ox * newScale;
            _plan.Y = canvasPoint.Y - oy * newScale;
            // Mirrors above / below: the image stays vertically centred when it becomes shorter than the screen.
            if (_oh * newScale < _target.Height && _oh * (newScale / factor) >= _target.Height)
                _plan.Y = (_target.Height - _oh * newScale) / 2;
            PlanEdited();
        }

        private void OnCanvasMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || _plan == null || !_plannable) return;
            _dragging = true;
            _dragStart = e.Location;
            _dragPlanX = _plan.X;
            _dragPlanY = _plan.Y;
            _canvas.Cursor = Cursors.SizeAll;
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || _plan == null) return;
            var r = DisplayRect();
            double k = _target.Width / (double)r.Width; // display px -> canvas px
            _plan.X = _dragPlanX + (e.X - _dragStart.X) * k;
            _plan.Y = _dragPlanY + (e.Y - _dragStart.Y) * k;
            PlanEdited();
            _canvas.Update();
        }

        private void OnCanvasMouseWheel(object sender, MouseEventArgs e)
        {
            if (_plan == null || !_plannable) return;
            var r = DisplayRect();
            if (r.Width <= 0) return;
            double k = _target.Width / (double)r.Width;
            var p = new PointF((float)((e.X - r.X) * k), (float)((e.Y - r.Y) * k));
            ZoomAround(e.Delta > 0 ? 1.05 : 1 / 1.05, p);
        }

        // ================================================================== Rendering

        private void RenderFrame()
        {
            if (_plan == null || _previewSource == null) return;
            double s = _plan.Scale * PreviewFactor / _previewSourceFactor; // previewSource -> preview canvas
            if (_scaledCache == null || Math.Abs(s - _scaledCacheScale) > 1e-9)
            {
                int w = Math.Max(1, (int)Math.Round(_previewSource.Width * s));
                int h = Math.Max(1, (int)Math.Round(_previewSource.Height * s));
                _scaledCache = _previewSource.Resize(w, h, -0.5);
                _scaledCacheScale = s;
            }
            var img = PlanRenderer.Render(_scaledCache, 1.0, _plan.X * PreviewFactor, _plan.Y * PreviewFactor,
                PreviewW, PreviewH, _target.BlurSigma * PreviewFactor);
            _frame?.Dispose();
            _frame = img.ToBitmap();
            _canvas.Invalidate();
        }

        private Rectangle DisplayRect()
        {
            var c = _canvas.ClientSize;
            double k = Math.Min((c.Width - 16) / (double)PreviewW, (c.Height - 16) / (double)PreviewH);
            int w = Math.Max(1, (int)(PreviewW * k)), h = Math.Max(1, (int)(PreviewH * k));
            return new Rectangle((c.Width - w) / 2, (c.Height - h) / 2, w, h);
        }

        private void OnCanvasPaint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = DisplayRect();
            Bitmap bmp = _plannable ? _frame : _staticBitmap;
            if (bmp != null && !_loading)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(bmp, r);

                if (_plannable && _plan != null)
                {
                    double k = r.Width / (double)_target.Width;
                    // Outline of the sharp (centre) image
                    var img = new RectangleF((float)(r.X + _plan.X * k), (float)(r.Y + _plan.Y * k), (float)(_ow * _plan.Scale * k), (float)(_oh * _plan.Scale * k));
                    using var pen = new Pen(Color.FromArgb(160, 255, 210, 0), 1) { DashStyle = DashStyle.Dash };
                    var clip = g.Clip;
                    g.SetClip(r);
                    g.DrawRectangle(pen, img.X, img.Y, img.Width, img.Height);

                    if (_chkDetections.Checked && _analysis != null)
                    {
                        foreach (var d in _analysis.Detections)
                        {
                            Color c = d.Label switch { "head" => Color.Red, "face" => Color.Magenta, "halfbody" => Color.LimeGreen, "text" => Color.Orange, _ => Color.Cyan };
                            using var dp = new Pen(c, 2);
                            float x = (float)(r.X + (_plan.X + d.Box.X * _plan.Scale) * k), y = (float)(r.Y + (_plan.Y + d.Box.Y * _plan.Scale) * k);
                            float w = (float)(d.Box.Width * _plan.Scale * k), h = (float)(d.Box.Height * _plan.Scale * k);
                            g.DrawRectangle(dp, x, y, w, h);
                            using var b = new SolidBrush(c);
                            g.DrawString($"{d.Label} {d.Score:F2}", Font, b, x + 2, y + 2);
                        }
                    }
                    g.Clip = clip;
                }
            }
            else
            {
                using var b = new SolidBrush(Color.Gainsboro);
                string msg = _loading ? "Loading..." : (_staticMessage ?? "");
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString(msg, new Font("Segoe UI", 11F), b, _canvas.ClientRectangle, sf);
            }

            if (_staticMessage != null && _staticBitmap != null)
            {
                using var b = new SolidBrush(Color.White);
                g.DrawString(_staticMessage, Font, b, r.X + 6, r.Bottom + 2);
            }
        }

        private void UpdateInfo()
        {
            _updatingUi = true;
            try
            {
                _chkSkip.Checked = _path != null && _session.IsExcluded(_path);
                _lblManual.Visible = _path != null && _session.GetManual(_path) != null;
                var up = _path != null ? _session.GetUpscale(_path) : null;
                _lblManual.Text = up != null
                    ? $"✔  waifu2x {up.Factor}x upscaled ({up.Width}x{up.Height}) - this image will be processed with this MANUAL framing."
                    : "✔  \"Use this framing\" active - this image will be processed with this MANUAL framing (not the automatic one).";
                _w2xBar.Visible = Waifu2xUpscaler.Available;
                _btnUndoUpscale.Visible = up != null;
                if (!_upscaling)
                    _lblW2x.Text = up != null ? $"Using the waifu2x {up.Factor}x version ({up.Options}). {up.Note}"
                        : _tooSmall ? $"Too small for {_target} - choose the factor and upscale (the result must reach {_target.Width} width or {_target.Height} height)."
                        : "Optional: upscale with waifu2x for more detail (the upscaled image is then framed manually).";
                if (_plan == null)
                {
                    _lblInfo.Text = _staticMessage ?? "";
                    return;
                }
                double zoom = _plan.Zoom(_ow);
                _zoomBar.Value = Math.Clamp((int)Math.Round(zoom * 100), _zoomBar.Minimum, _zoomBar.Maximum);
                bool upscaled = _plan.Scale > 1.0001;
                _lblZoom.Text = $"x{zoom:F2}" + (upscaled ? " (upscaled!)" : "");
                _lblZoom.ForeColor = upscaled ? Color.OrangeRed : SystemColors.ControlText;

                string status = _session.IsExcluded(_path) ? "SKIPPED" : (_session.GetManual(_path) != null ? "MANUAL framing" : "AUTO framing");
                string extra = "";
                if (_session.GetManual(_path) == null)
                {
                    if (_ow == _target.Width && _oh == _target.Height)
                        extra = $"  |  already {_target}: skipped unless you click \"Use this framing\"";
                    else if (WallpaperProcessor.IsSquare(_ow, _oh, _squareTolerance))
                        extra = "  |  square image: skipped unless you adjust it or click \"Use this framing\"";
                }
                _lblInfo.Text = $"{_ow}x{_oh}   |   {status}   |   auto: {_auto?.Notes}{extra}";
            }
            finally
            {
                _updatingUi = false;
            }
        }

        private void UpdateButtons()
        {
            _btnPrev.Enabled = _index > 0;
            _btnNext.Enabled = _index < _files.Count - 1;
            bool edit = !_loading && _plannable && _plan != null;
            _btnAuto.Enabled = edit;
            _btnCenter.Enabled = edit;
            _btnCenterH.Enabled = edit;
            _btnAccept.Enabled = edit;
            _zoomBar.Enabled = edit;
            _chkSkip.Enabled = !_loading;
            _btnUpscale.Enabled = !_loading && !_upscaling && _path != null && _ow > 0;
            _cmbFactor.Enabled = !_upscaling;
            _btnUndoUpscale.Enabled = !_loading && !_upscaling;
            _btnPrev.Enabled &= !_upscaling;
            _btnNext.Enabled &= !_upscaling;
            _chkAi.Enabled = !_loading && _smart;
        }

        /// <summary>Double-buffered panel that receives focus for the mouse wheel.</summary>
        private sealed class PreviewCanvas : Panel
        {
            public PreviewCanvas()
            {
                DoubleBuffered = true;
                ResizeRedraw = true;
                SetStyle(ControlStyles.Selectable, true);
                TabStop = true;
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                Focus();
            }
        }
    }
}
