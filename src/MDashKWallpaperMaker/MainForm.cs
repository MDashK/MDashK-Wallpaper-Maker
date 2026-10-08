using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MDashKWallpaperMaker
{
    internal sealed class MainForm : Form
    {
        private static readonly string[] SupportedExtensions =
        {
            ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif",
        };

        private sealed class SourceFile
        {
            public string FullPath;
            public string RootFolder; // output goes to RootFolder\processed_output\<relative sub-folder>
        }

        private sealed class FailedItem
        {
            public SourceFile Source;
            public string File;
            public string Reason;
            public string MovedTo;
        }

        private readonly AppSettings _settings = AppSettings.Load();

        // What the user selected; the file list is rebuilt from these (e.g. when the sub-folder option changes).
        private readonly List<string> _selectedFolders = new();
        private readonly List<string> _selectedFiles = new();
        private readonly List<SourceFile> _files = new();

        private readonly FramingSession _framing = new();

        private CancellationTokenSource _cts;

        // The preview is a separate (not owned) window, so the main window can be minimized while it is open.
        private PreviewForm _preview;

        private Button _btnOpenFolder, _btnOpenFile, _btnPreview, _btnProcess, _btnLearning, _btnAbout;
        private CheckBox _chkSubfolders, _chkSmart;
        private NumericUpDown _numSquare;
        private ComboBox _cmbSize, _cmbUpscale;
        private Button _btnWaifu2x;
        private TargetSize _target = TargetSize.FullHd;

        private static readonly string[] SizePresets =
        {
            "1920x1080 (Full HD)", "1280x720 (HD)", "1366x768", "1600x900", "2560x1440 (QHD)", "3840x2160 (4K UHD)",
            "1920x1200 (16:10)", "2560x1600 (16:10)", "2560x1080 (21:9)", "3440x1440 (21:9)",
        };
        private ListView _list;
        private GroupBox _grpFiles;
        private RichTextBox _log;
        private ProgressBar _progress;
        private Label _status;
        private ContextMenuStrip _listMenu;

        public MainForm(string[] startupPaths = null)
        {
            BuildUi();
            Load += (_, _) =>
            {
                Log($"{AppInfo.Name} v{AppInfo.Version} ready. Select a folder or files, or drag & drop them into the list.", Color.Gray);
                // Files/folders passed on the command line (e.g. dropped onto the .exe) are added like a drag & drop.
                if (startupPaths != null && startupPaths.Length > 0) AddPaths(startupPaths);
            };
            FormClosing += OnFormClosing;
            FramingSession.CleanupStaleTemp();
            _framing.Changed += path => UI(() => RefreshStatusColumn(path));
        }

        // ================================================================== UI

        private void BuildUi()
        {
            SuspendLayout();
            Text = $"{AppInfo.Name} v{AppInfo.Version}";
            Icon = AppInfo.LoadIcon();
            Font = new Font("Segoe UI", 9F);
            // Designed at 96 DPI; scaled to the system DPI (ApplicationHighDpiMode = SystemAware).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1200, 720);
            Load += (_, _) => FitToWorkingArea();
            MinimumSize = new Size(640, 480);
            Padding = new Padding(8);

            // ---- Toolbar
            _btnOpenFolder = MakeButton("Open folder", (_, _) => OpenFolder());
            _btnOpenFile = MakeButton("Open file", (_, _) => OpenFiles());
            _btnPreview = MakeButton("Preview / Adjust", (_, _) => OpenPreview());
            _btnProcess = MakeButton("Process", (_, _) => ProcessOrStop());
            _btnProcess.Font = new Font(Font, FontStyle.Bold);
            _btnLearning = MakeButton("Learning", (_, _) => { using var f = new LearningForm(_settings); f.ShowDialog(this); });
            _btnAbout = MakeButton("About", (_, _) => { using var f = new AboutForm(); f.ShowDialog(this); });

            _chkSubfolders = new CheckBox
            {
                Text = "Include sub-folders",
                AutoSize = true,
                Checked = _settings.IncludeSubfolders,
                Margin = new Padding(16, 8, 3, 3),
            };
            _chkSubfolders.CheckedChanged += (_, _) =>
            {
                _settings.IncludeSubfolders = _chkSubfolders.Checked;
                if (_selectedFolders.Count > 0) RebuildFileList();
            };

            var lblSquare = new Label { Text = "Square tolerance (%):", AutoSize = true, Margin = new Padding(16, 9, 0, 3) };
            _numSquare = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 50,
                DecimalPlaces = 0,
                Value = Math.Clamp(_settings.SquareTolerancePercent, 0, 50),
                Width = 50,
                Margin = new Padding(3, 6, 3, 3),
            };
            new ToolTip().SetToolTip(_numSquare,
                "Images whose width and height differ by up to this percentage are treated as square\nand left for manual processing.");
            _numSquare.ValueChanged += (_, _) => _settings.SquareTolerancePercent = _numSquare.Value;

            _chkSmart = new CheckBox
            {
                Text = "Smart framing (AI)",
                AutoSize = true,
                Checked = _settings.SmartFraming,
                Margin = new Padding(16, 8, 3, 3),
            };
            new ToolTip().SetToolTip(_chkSmart,
                "Detects the anime character (head / half-body) and text near the borders to choose zoom and crop automatically.\n" +
                "When off, the classic fixed rules are used.");
            _chkSmart.CheckedChanged += (_, _) =>
            {
                _settings.SmartFraming = _chkSmart.Checked;
                RefreshAllStatus();
            };
            if (!Directory.Exists(ModelHub.ModelsDir))
            {
                _chkSmart.Checked = false;
                _chkSmart.Enabled = false;
                _chkSmart.Text += " - models missing";
            }

            // ---- Wallpaper size (limits such as "too small" follow the chosen size)
            var lblSize = new Label { Text = "Wallpaper size:", AutoSize = true, Margin = new Padding(3, 9, 0, 3), Font = new Font(Font, FontStyle.Bold) };
            _cmbSize = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 170, Margin = new Padding(3, 5, 3, 3) };
            _cmbSize.Items.AddRange(SizePresets);
            if (!TargetSize.TryParse(_settings.TargetResolution, out _target)) _target = TargetSize.FullHd;
            _cmbSize.Text = SizePresets.FirstOrDefault(x => TargetSize.TryParse(x, out var t) && t == _target) ?? _target.ToString();
            _cmbSize.SelectionChangeCommitted += (_, _) => BeginInvoke(new Action(ApplySizeText));
            _cmbSize.Leave += (_, _) => ApplySizeText();
            _cmbSize.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ApplySizeText(); e.SuppressKeyPress = true; } };
            new ToolTip().SetToolTip(_cmbSize,
                "Resolution of the wallpapers to create. Choose a preset or type e.g. 1366x768.\n" +
                "Images smaller than this size in both dimensions are not processed; the mirror blur scales with the size.");

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Padding = new Padding(0, 0, 0, 2),
            };
            toolbar.Controls.AddRange(new Control[] { _btnOpenFolder, _btnOpenFile, _btnPreview, _btnProcess, _btnLearning, _btnAbout });

            var optionsBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Padding = new Padding(0, 0, 0, 4),
            };
            _chkSubfolders.Margin = new Padding(16, 8, 3, 3);
            // ---- waifu2x: factor used in the preview for images that are too small + options window
            var lblUpscale = new Label { Text = "Upscale:", AutoSize = true, Margin = new Padding(16, 9, 0, 3) };
            _cmbUpscale = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 58, Margin = new Padding(3, 5, 3, 3) };
            _cmbUpscale.Items.AddRange(Waifu2xUpscaler.Factors.Select(f => f + "x").ToArray());
            int ui = Array.IndexOf(Waifu2xUpscaler.Factors, _settings.UpscaleFactor);
            _cmbUpscale.SelectedIndex = ui >= 0 ? ui : 0;
            _cmbUpscale.SelectedIndexChanged += (_, _) => _settings.UpscaleFactor = Waifu2xUpscaler.Factors[_cmbUpscale.SelectedIndex];
            _btnWaifu2x = new Button { Text = "waifu2x...", AutoSize = true, MinimumSize = new Size(90, 27), Margin = new Padding(3, 3, 3, 3) };
            _btnWaifu2x.Click += (_, _) =>
            {
                using var f = new Waifu2xForm(_settings.Waifu2x ?? new Waifu2xOptions());
                if (f.ShowDialog(this) == DialogResult.OK && f.Result != null)
                {
                    _settings.Waifu2x = f.Result;
                    Log("waifu2x options: " + f.Result.Describe(), Color.DeepSkyBlue);
                }
            };
            var upscaleTip = new ToolTip();
            upscaleTip.SetToolTip(_cmbUpscale, "Default waifu2x factor in Preview / Adjust (any image can be upscaled there;\n" +
                                               "images that are too small need it). Upscaled images are processed with manual framing.");
            upscaleTip.SetToolTip(_btnWaifu2x, "waifu2x options: model, noise reduction, GPU / CPU, tile size, GPU test.");
            if (!Waifu2xUpscaler.Available)
            {
                _cmbUpscale.Enabled = false;
                _btnWaifu2x.Enabled = false;
                lblUpscale.Text = "Upscale (waifu2x models missing):";
            }

            optionsBar.Controls.AddRange(new Control[] { lblSize, _cmbSize, lblUpscale, _cmbUpscale, _btnWaifu2x, _chkSubfolders, _chkSmart, lblSquare, _numSquare });

            // ---- File list
            _list = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                AllowDrop = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                ShowItemToolTips = true,
                CheckBoxes = true,
            };
            _list.ItemChecked += (_, e) =>
            {
                if (_suppressItemChecked || e.Item.Tag is not SourceFile sf) return;
                _framing.SetAiDisabled(sf.FullPath, !e.Item.Checked);
            };
            // A double-click opens the preview; it must not also toggle the AI checkbox.
            bool doubleClick = false;
            _list.MouseDown += (_, e) => doubleClick = e.Clicks > 1;
            _list.ItemCheck += (_, e) =>
            {
                if (!doubleClick) return;
                e.NewValue = e.CurrentValue;
                doubleClick = false;
            };
            _list.Columns.Add("AI  |  File name", LogicalToDeviceUnits(350));
            _list.Columns.Add("Framing", LogicalToDeviceUnits(100));
            _list.Columns.Add("Folder", LogicalToDeviceUnits(500));
            _list.ItemActivate += (_, _) => OpenPreview();
            _list.Resize += (_, _) => FitFolderColumn();
            _list.DragEnter += OnDragEnter;
            _list.DragDrop += OnDragDrop;
            _list.KeyDown += (_, e) =>
            {
                if (e.KeyCode == Keys.Delete) RemoveSelected();
                else if (e.Control && e.KeyCode == Keys.A) foreach (ListViewItem i in _list.Items) i.Selected = true;
            };

            _listMenu = new ContextMenuStrip();
            _listMenu.Items.Add("Preview / Adjust...", null, (_, _) => OpenPreview());
            _listMenu.Items.Add("Reset to automatic framing", null, (_, _) => ResetSelectedFraming());
            _listMenu.Items.Add(new ToolStripSeparator());
            _listMenu.Items.Add("Remove selected", null, (_, _) => RemoveSelected());
            _listMenu.Items.Add("Clear list", null, (_, _) => ClearSelection());
            _listMenu.Items.Add(new ToolStripSeparator());
            _listMenu.Items.Add("Open output folder", null, (_, _) => OpenOutputFolder());
            _list.ContextMenuStrip = _listMenu;

            _grpFiles = new GroupBox { Text = "Files to process (drag && drop files or folders here)", Dock = DockStyle.Fill, Padding = new Padding(6) };
            _grpFiles.Controls.Add(_list);

            // ---- Log
            _log = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                Font = new Font("Consolas", 9F),
                BorderStyle = BorderStyle.None,
                DetectUrls = false,
                WordWrap = false,
                HideSelection = false,
            };
            var logMenu = new ContextMenuStrip();
            logMenu.Items.Add("Copy", null, (_, _) => { if (_log.SelectionLength > 0) _log.Copy(); });
            logMenu.Items.Add("Select all", null, (_, _) => _log.SelectAll());
            logMenu.Items.Add("Clear log", null, (_, _) => _log.Clear());
            _log.ContextMenuStrip = logMenu;

            var grpLog = new GroupBox { Text = "Log", Dock = DockStyle.Fill, Padding = new Padding(6) };
            grpLog.Controls.Add(_log);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 6,
            };
            split.Panel1.Controls.Add(_grpFiles);
            split.Panel2.Controls.Add(grpLog);

            // ---- Status bar
            _progress = new ProgressBar { Dock = DockStyle.Right, Width = 260, Style = ProgressBarStyle.Continuous };
            _status = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Text = "No files selected." };
            var statusPanel = new Panel { Dock = DockStyle.Bottom, Height = 26, Padding = new Padding(0, 4, 0, 0) };
            statusPanel.Controls.Add(_status);
            statusPanel.Controls.Add(_progress);

            Controls.Add(split);
            Controls.Add(statusPanel);
            Controls.Add(optionsBar);
            Controls.Add(toolbar);

            // Allow dropping anywhere on the window as well.
            AllowDrop = true;
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;

            Shown += (_, _) =>
            {
                split.SplitterDistance = (int)(split.Height * 0.42);
                FitFolderColumn();
            };

            ResumeLayout(false);
            PerformLayout();
        }

        private void FitFolderColumn()
        {
            if (_list.Columns.Count < 3) return;
            _list.Columns[2].Width = Math.Max(LogicalToDeviceUnits(150), _list.ClientSize.Width - _list.Columns[0].Width - _list.Columns[1].Width - 4);
        }

        /// <summary>Keeps the main window inside the screen's working area (e.g. 1920x1080 with 125% scaling).</summary>
        private void FitToWorkingArea()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            int w = Math.Min(Width, wa.Width), h = Math.Min(Height, wa.Height);
            if (w == Width && h == Height) return;
            Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
        }

        private Button MakeButton(string text, EventHandler click)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(100, 30), Margin = new Padding(3) };
            b.Click += click;
            return b;
        }

        // ================================================================== Selection

        private void OpenFolder()
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select the folder with the images to process",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false,
            };
            if (!string.IsNullOrEmpty(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
                dlg.SelectedPath = _settings.LastFolder;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _settings.LastFolder = dlg.SelectedPath;
            _selectedFolders.Clear();
            _selectedFiles.Clear();
            _selectedFolders.Add(dlg.SelectedPath);
            RebuildFileList();
            Log($"Folder selected: {dlg.SelectedPath} ({(_chkSubfolders.Checked ? "including" : "excluding")} sub-folders) - {_files.Count} image(s).");
        }

        private void OpenFiles()
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select the image(s) to process",
                Multiselect = true,
                Filter = "Images|" + string.Join(";", SupportedExtensions.Select(e => "*" + e)) + "|All files|*.*",
            };
            if (!string.IsNullOrEmpty(_settings.LastFolder) && Directory.Exists(_settings.LastFolder))
                dlg.InitialDirectory = _settings.LastFolder;
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            _settings.LastFolder = Path.GetDirectoryName(dlg.FileNames[0]);
            _selectedFolders.Clear();
            _selectedFiles.Clear();
            _selectedFiles.AddRange(dlg.FileNames);
            RebuildFileList();
            Log($"{dlg.FileNames.Length} file(s) selected.");
        }

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            bool ok = !IsBusy && _preview == null && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            if (IsBusy || _preview != null || e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;
            AddPaths(paths);
        }

        private void AddPaths(string[] paths)
        {
            int folders = 0, files = 0, ignored = 0;
            foreach (var p in paths)
            {
                if (Directory.Exists(p))
                {
                    if (!_selectedFolders.Contains(p, StringComparer.OrdinalIgnoreCase)) _selectedFolders.Add(p);
                    folders++;
                }
                else if (File.Exists(p))
                {
                    if (!IsSupported(p))
                    {
                        Log($"Ignored (not a supported image): {p}", Color.Goldenrod);
                        ignored++;
                        continue;
                    }
                    if (!_selectedFiles.Contains(p, StringComparer.OrdinalIgnoreCase)) _selectedFiles.Add(p);
                    files++;
                }
            }
            RebuildFileList();
            Log($"Added: {files} file(s), {folders} folder(s){(ignored > 0 ? $", {ignored} ignored" : "")}. Total in list: {_files.Count}.");
        }

        private void RemoveSelected()
        {
            if (IsBusy || _list.SelectedItems.Count == 0) return;
            RemoveFiles(_list.SelectedItems.Cast<ListViewItem>().Select(i => (SourceFile)i.Tag));
        }

        private void RemoveFiles(IEnumerable<SourceFile> files, bool forceRebuild = false)
        {
            var remove = new HashSet<SourceFile>(files);
            if (remove.Count == 0 && !forceRebuild) return;
            // Turn the remaining entries into an explicit file list so removed ones don't come back on rebuild.
            var keep = _files.Where(f => !remove.Contains(f)).ToList();
            _files.Clear();
            _files.AddRange(keep);
            _selectedFolders.Clear();
            _selectedFiles.Clear();
            _explicitRoots.Clear();
            foreach (var f in keep)
            {
                _selectedFiles.Add(f.FullPath);
                _explicitRoots[f.FullPath] = f.RootFolder;
            }
            RefreshListView();
        }

        private void ClearSelection()
        {
            if (IsBusy) return;
            _selectedFolders.Clear();
            _selectedFiles.Clear();
            _explicitRoots.Clear();
            RebuildFileList();
        }

        // Keeps the output root of files that originally came from a folder selection (after "Remove selected").
        private readonly Dictionary<string, string> _explicitRoots = new(StringComparer.OrdinalIgnoreCase);

        private void RebuildFileList()
        {
            _files.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in _selectedFolders)
            {
                foreach (var f in EnumerateImages(folder, _chkSubfolders.Checked))
                    if (seen.Add(f)) _files.Add(new SourceFile { FullPath = f, RootFolder = folder });
            }
            foreach (var f in _selectedFiles)
            {
                if (!seen.Add(f)) continue;
                string root = _explicitRoots.TryGetValue(f, out var r) ? r : Path.GetDirectoryName(f);
                _files.Add(new SourceFile { FullPath = f, RootFolder = root });
            }
            RefreshListView();
        }

        private void RefreshListView()
        {
            // Adding items raises ItemChecked; those events must not be taken as the user switching AI off.
            _suppressItemChecked = true;
            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                foreach (var f in _files)
                {
                    var item = new ListViewItem(Path.GetFileName(f.FullPath))
                    {
                        Tag = f,
                        ToolTipText = f.FullPath,
                        Checked = !_framing.IsAiDisabled(f.FullPath),
                    };
                    item.SubItems.Add("");
                    item.SubItems.Add(Path.GetDirectoryName(f.FullPath));
                    item.UseItemStyleForSubItems = true;
                    _list.Items.Add(item);
                    UpdateItemState(item);
                }
            }
            finally
            {
                _list.EndUpdate();
                _suppressItemChecked = false;
            }
            _grpFiles.Text = $"Files to process: {_files.Count}  (drag && drop files or folders here  |  checkbox = AI framing for that image)";
            _status.Text = _files.Count == 0 ? "No files selected." : $"{_files.Count} file(s) ready.";
        }

        private static bool IsSupported(string path) =>
            SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        private IEnumerable<string> EnumerateImages(string folder, bool recursive)
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.System };
            var stack = new Stack<string>();
            stack.Push(folder);
            var result = new List<string>();
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                try
                {
                    result.AddRange(Directory.EnumerateFiles(dir, "*", opts)
                        .Where(IsSupported)
                        .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase));
                    if (recursive)
                    {
                        foreach (var sub in Directory.EnumerateDirectories(dir, "*", opts).OrderByDescending(d => d, StringComparer.CurrentCultureIgnoreCase))
                        {
                            // Never re-process our own output.
                            string subName = Path.GetFileName(sub);
                            if (AppInfo.IsOutputFolder(subName)) continue;
                            if (string.Equals(subName, AppInfo.NotProcessedFolderName, StringComparison.OrdinalIgnoreCase)) continue;
                            stack.Push(sub);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Cannot read folder {dir}: {ex.Message}", Color.Goldenrod);
                }
            }
            return result;
        }

        private void OpenOutputFolder()
        {
            var src = _list.SelectedItems.Count > 0 ? (SourceFile)_list.SelectedItems[0].Tag : _files.FirstOrDefault();
            if (src == null) return;
            string dir = Path.Combine(src.RootFolder, AppInfo.OutputFolderFor(_target));
            if (!Directory.Exists(dir))
            {
                MessageBox.Show(this, "The output folder does not exist yet:\n" + dir, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }

        // ================================================================== Wallpaper size

        /// <summary>Validates the size typed / chosen in the combo box; invalid text goes back to the current size.</summary>
        private void ApplySizeText()
        {
            if (TargetSize.TryParse(_cmbSize.Text, out var t))
            {
                if (t != _target)
                {
                    _target = t;
                    _settings.TargetResolution = t.ToString();
                    Log($"Wallpaper size: {t} (images smaller than {t.Width}x{t.Height} in both dimensions are not processed).", Color.DeepSkyBlue);
                }
            }
            else
            {
                Log($"Invalid wallpaper size \"{_cmbSize.Text}\" - use e.g. 1366x768 ({TargetSize.MinSide}-{TargetSize.MaxSide} px).", Color.Goldenrod);
                _cmbSize.Text = SizePresets.FirstOrDefault(x => TargetSize.TryParse(x, out var p) && p == _target) ?? _target.ToString();
            }
        }

        // ================================================================== Preview

        private void OpenPreview()
        {
            if (IsBusy) return;
            if (_files.Count == 0)
            {
                MessageBox.Show(this, "There are no files in the list.", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_preview != null && !_preview.IsDisposed)
            {
                if (_preview.WindowState == FormWindowState.Minimized) _preview.WindowState = FormWindowState.Normal;
                _preview.Activate();
                return;
            }
            int index = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : 0;
            var paths = _files.Select(f => f.FullPath).ToList();
            ApplySizeText();
            var form = new PreviewForm(paths, index, _framing, _chkSmart.Checked, (double)_numSquare.Value / 100.0, _target,
                                       _settings.Waifu2x, _settings.UpscaleFactor);
            form.PlaceOn(Screen.FromControl(this));
            form.FormClosed += (_, _) =>
            {
                _preview = null;
                SetPreviewOpen(false);
                form.Dispose();
            };
            _preview = form;
            SetPreviewOpen(true);
            form.Show(); // no owner: the main window can be minimized independently
        }

        private void ResetSelectedFraming()
        {
            if (IsBusy) return;
            foreach (ListViewItem item in _list.SelectedItems)
            {
                var f = (SourceFile)item.Tag;
                _framing.ClearManual(f.FullPath);
                _framing.SetExcluded(f.FullPath, false);
            }
        }

        private bool _suppressItemChecked;

        private void RefreshStatusColumn(string path)
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (string.Equals(((SourceFile)item.Tag).FullPath, path, StringComparison.OrdinalIgnoreCase))
                    UpdateItemState(item);
            }
        }

        private void RefreshAllStatus()
        {
            foreach (ListViewItem item in _list.Items) UpdateItemState(item);
        }

        /// <summary>Checkbox = AI framing for this image; "Framing" column = Auto (AI) / Auto (classic) / Manual / Skip.</summary>
        private void UpdateItemState(ListViewItem item)
        {
            var f = (SourceFile)item.Tag;
            bool wasSuppressed = _suppressItemChecked;
            _suppressItemChecked = true;
            try
            {
                item.Checked = !_framing.IsAiDisabled(f.FullPath);
                item.SubItems[1].Text = _framing.StatusText(f.FullPath, _chkSmart.Checked);
                item.ForeColor = item.SubItems[1].Text == "Skip" ? Color.Gray : SystemColors.WindowText;
            }
            finally
            {
                _suppressItemChecked = wasSuppressed;
            }
        }

        // ================================================================== Processing

        private bool IsBusy => _cts != null;

        private void ProcessOrStop()
        {
            if (IsBusy)
            {
                _cts.Cancel();
                _btnProcess.Enabled = false;
                Log("Stopping after the current image...", Color.Goldenrod);
                return;
            }
            if (_files.Count == 0)
            {
                MessageBox.Show(this, "There are no files to process.\nUse \"Open folder\", \"Open file\" or drag & drop images into the list.",
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _ = RunAsync();
        }

        private async Task RunAsync()
        {
            var files = _files.ToList();
            double tolerance = (double)_numSquare.Value / 100.0;
            ApplySizeText();
            var target = _target;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            SetBusy(true);
            _progress.Value = 0;
            _progress.Maximum = files.Count;

            Log("");
            Log($"===== Processing {files.Count} image(s) at {target} - {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====", Color.DeepSkyBlue);

            var failed = new List<FailedItem>();
            var finished = new List<SourceFile>(); // processed, or nothing to do -> removed from the list afterwards
            int ok = 0;
            int learned = 0;
            var sw = Stopwatch.StartNew();
            var usedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            bool smart = _chkSmart.Checked;
            await Task.Run(() =>
            {
                var processor = new WallpaperProcessor(msg => Log(msg, Color.DarkGray)) { SquareTolerance = tolerance, Target = target };
                if (smart)
                {
                    Log("Smart framing (AI) on - loading anime detection models...", Color.DarkGray);
                    processor.Models = ModelHub.Get();
                    if (processor.Models == null)
                        Log("Smart framing unavailable (" + ModelHub.LoadError + ") - using the classic rules.", Color.Goldenrod);
                }
                else
                {
                    Log("Smart framing is off - using the classic rules.", Color.DarkGray);
                }
                for (int i = 0; i < files.Count; i++)
                {
                    if (token.IsCancellationRequested) break;
                    var f = files[i];
                    string name = Path.GetFileName(f.FullPath);
                    int index = i + 1;
                    UI(() =>
                    {
                        _status.Text = $"[{index}/{files.Count}] {name}";
                        SelectInList(f);
                    });

                    try
                    {
                        if (!File.Exists(f.FullPath)) throw new FileNotFoundException("File no longer exists.");
                        if (_framing.IsExcluded(f.FullPath))
                        {
                            Log($"[{index}/{files.Count}] {name}", Color.White);
                            failed.Add(new FailedItem { Source = f, File = f.FullPath, Reason = "Marked as \"Skip this image\" in the preview." });
                            Log("    NOT PROCESSED: skipped in the preview", Color.Orange);
                            UI(() => _progress.Value = index);
                            continue;
                        }
                        var upscaled = _framing.GetUpscale(f.FullPath);
                        var img = _framing.LoadSource(f.FullPath);
                        Log($"[{index}/{files.Count}] {name}  ({img.Width}x{img.Height})", Color.White);
                        if (upscaled != null)
                            Log($"    Using the waifu2x {upscaled.Factor}x version made in the preview ({upscaled.Options})", Color.DarkGray);

                        var manual = _framing.GetManual(f.FullPath);
                        ImageAnalysis analysis = processor.Models != null ? _framing.GetAnalysis(f.FullPath) : null;
                        bool useAi = !_framing.IsAiDisabled(f.FullPath);
                        if (!useAi && manual == null && processor.Models != null) Log("    AI framing disabled for this image - classic rules", Color.DarkGray);
                        var result = processor.Process(img, manual, analysis, useAi);
                        bool nothingToDo = manual == null && img.Width == target.Width && img.Height == target.Height;
                        if (result.Status == ProcessStatus.Processed)
                        {
                            string outPath = GetOutputPath(f, usedOutputs, target);
                            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                            result.Image.SavePng(outPath);
                            ok++;
                            finished.Add(f);
                            Log($"    OK -> {outPath}", Color.LightGreen);
                            if (RecordLearningExample(f.FullPath, img, result, manual, analysis, useAi, processor.Models)) learned++;
                        }
                        else
                        {
                            failed.Add(new FailedItem { Source = f, File = f.FullPath, Reason = result.Reason });
                            if (nothingToDo) finished.Add(f);
                            Log("    NOT PROCESSED: " + result.Reason, Color.Orange);
                        }
                        img = null;
                    }
                    catch (Exception ex)
                    {
                        string reason = ex is OutOfMemoryException ? "Not enough memory to process this image." : ex.Message;
                        failed.Add(new FailedItem { Source = f, File = f.FullPath, Reason = "Error: " + reason });
                        Log($"[{index}/{files.Count}] {name}", Color.White);
                        Log("    ERROR: " + reason, Color.IndianRed);
                    }

                    UI(() => _progress.Value = index);
                }
            });

            sw.Stop();
            bool cancelled = token.IsCancellationRequested;
            int notStarted = files.Count - ok - failed.Count;

            Log("");
            Log($"===== {(cancelled ? "Stopped" : "Finished")} in {sw.Elapsed:mm\\:ss} - processed: {ok}, not processed: {failed.Count}" +
                (notStarted > 0 ? $", not started: {notStarted}" : "") + " =====", Color.DeepSkyBlue);

            int moved = MoveNotProcessed(failed);

            if (failed.Count > 0)
            {
                Log("");
                Log($"Images NOT processed ({failed.Count}):", Color.Orange);
                foreach (var item in failed)
                {
                    Log($"  - {Path.GetFileName(item.File)}", Color.Orange);
                    Log($"      Folder: {Path.GetDirectoryName(item.File)}", Color.DarkGray);
                    Log($"      Reason: {item.Reason}", Color.Gainsboro);
                    if (item.MovedTo != null) Log($"      Moved to: {Path.GetDirectoryName(item.MovedTo)}", Color.DarkGray);
                }
                if (moved > 0)
                    Log($"{moved} original(s) moved to the \"{AppInfo.NotProcessedFolderName}\" folder.", Color.DeepSkyBlue);
            }
            else if (!cancelled)
            {
                Log("All images were processed.", Color.LightGreen);
            }

            // Keep in the list only the images that still need attention (to be adjusted / processed by hand).
            RemoveFiles(finished, forceRebuild: moved > 0);
            if (finished.Count > 0)
            {
                Log("");
                Log(_files.Count == 0
                    ? $"Removed {finished.Count} finished image(s) from the list - nothing left to do."
                    : $"Removed {finished.Count} finished image(s) from the list. {_files.Count} image(s) left that need manual processing.",
                    Color.DeepSkyBlue);
            }

            if (learned > 0) await LearnAfterProcessing(learned);

            _status.Text = cancelled ? "Stopped." : $"Done: {ok} processed, {failed.Count} not processed.";
            _cts.Dispose();
            _cts = null;
            SetBusy(false);
            GC.Collect();
        }

        // ================================================================== Learning

        /// <summary>
        /// Stores the user's choice for this image in config\history.jsonl: a manual framing, or an automatic framing the
        /// user looked at in the preview and kept. Images never seen in the preview say nothing about the user's taste.
        /// </summary>
        private bool RecordLearningExample(string path, RgbImage img, ProcessResult result, FramingPlan manual,
                                           ImageAnalysis cachedAnalysis, bool useAi, ModelHub models)
        {
            if (models == null || result.Plan == null) return false;
            string kind = manual != null ? LearningRecord.KindManual
                : useAi && _framing.WasViewed(path) ? LearningRecord.KindApproved
                : null;
            if (kind == null) return false;
            try
            {
                var analysis = result.Analysis ?? cachedAnalysis ?? _framing.GetAnalysis(path) ?? SmartFramer.Analyze(img, models);
                _framing.StoreAnalysis(path, analysis);
                var auto = SmartFramer.AutoPlan(analysis, null, result.Plan.Target);
                LearningStore.Append(LearningRecord.Create(kind, Path.GetFileName(path), analysis, auto, result.Plan));
                Log($"    Learning: recorded as {(kind == LearningRecord.KindManual ? "manual adjustment" : "approved automatic framing")}", Color.DarkGray);
                return true;
            }
            catch (Exception ex)
            {
                Log("    Learning: could not record this example (" + ex.Message + ")", Color.Goldenrod);
                return false;
            }
        }

        private async Task LearnAfterProcessing(int recorded)
        {
            try
            {
                int total = await Task.Run(Calibrator.UserSampleCount);
                Log("");
                Log($"Learning: {recorded} new example(s) recorded - {total} in the history.", Color.DeepSkyBlue);

                if (total < Calibrator.MinSamplesForBias)
                {
                    Log($"Learning: {Calibrator.MinSamplesForBias - total} more example(s) needed before the framing adapts to your choices.", Color.DarkGray);
                    return;
                }
                if (!_settings.LearnAutomatically)
                {
                    Log("Learning: automatic learning is off (see the Learning window).", Color.DarkGray);
                    return;
                }
                int sinceLast = total - FramingCalibration.Current.UserSamples;
                if (sinceLast < 5)
                {
                    Log($"Learning: will recalibrate after {5 - sinceLast} more new example(s).", Color.DarkGray);
                    return;
                }

                var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
                var report = await Task.Run(() => Calibrator.Run(lines.Enqueue));
                foreach (var l in lines) Log(l, Color.DarkGray);
                Log("Learning: " + report.Message, report.Adopted ? Color.LightGreen : Color.Gainsboro);
            }
            catch (Exception ex)
            {
                Log("Learning failed: " + ex.Message, Color.Goldenrod);
            }
        }

        /// <summary>
        /// Sub-folder of the file relative to the selected folder, ignoring a leading "not_processed" (files moved there
        /// keep their original place in processed_output / not_processed).
        /// </summary>
        private static string RelativeSubDir(SourceFile f)
        {
            string rel = Path.GetRelativePath(f.RootFolder, Path.GetDirectoryName(f.FullPath));
            if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return "";
            var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToList();
            if (parts.Count > 0 && string.Equals(parts[0], AppInfo.NotProcessedFolderName, StringComparison.OrdinalIgnoreCase))
                parts.RemoveAt(0);
            return parts.Count == 0 ? "" : Path.Combine(parts.ToArray());
        }

        /// <summary>
        /// Moves the originals that were not processed to &lt;selected folder&gt;\not_processed\&lt;sub-folder&gt;, so they
        /// are easy to find and handle by hand. Choices made in the preview follow the file.
        /// </summary>
        private int MoveNotProcessed(List<FailedItem> failed)
        {
            int moved = 0;
            foreach (var item in failed)
            {
                var f = item.Source;
                if (f == null || !File.Exists(f.FullPath)) continue;
                string dir = Path.Combine(f.RootFolder, AppInfo.NotProcessedFolderName, RelativeSubDir(f));
                string dest = Path.Combine(dir, Path.GetFileName(f.FullPath));
                if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(f.FullPath), StringComparison.OrdinalIgnoreCase)) continue; // already there
                try
                {
                    Directory.CreateDirectory(dir);
                    string name = Path.GetFileNameWithoutExtension(f.FullPath), ext = Path.GetExtension(f.FullPath);
                    for (int n = 2; File.Exists(dest); n++)
                        dest = Path.Combine(dir, $"{name} ({n}){ext}");
                    var analysis = _framing.GetAnalysis(f.FullPath);
                    File.Move(f.FullPath, dest);
                    _framing.RenamePath(f.FullPath, dest, analysis);
                    f.FullPath = dest;
                    item.MovedTo = dest;
                    moved++;
                }
                catch (Exception ex)
                {
                    Log($"Could not move {Path.GetFileName(f.FullPath)} to \"{AppInfo.NotProcessedFolderName}\": {ex.Message}", Color.Goldenrod);
                }
            }
            return moved;
        }

        private static string GetOutputPath(SourceFile f, HashSet<string> used, TargetSize target)
        {
            string outDir = Path.Combine(f.RootFolder, AppInfo.OutputFolderFor(target), RelativeSubDir(f));

            string baseName = Path.GetFileNameWithoutExtension(f.FullPath);
            string path = Path.Combine(outDir, baseName + ".png");
            lock (used)
            {
                // Two sources with the same name (e.g. "a.jpg" and "a.png") in the same run must not overwrite each other.
                int n = 2;
                while (!used.Add(path))
                    path = Path.Combine(outDir, $"{baseName} ({n++}).png");
            }
            return path;
        }

        private void SelectInList(SourceFile f)
        {
            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag == f)
                {
                    _list.SelectedItems.Clear();
                    item.Selected = true;
                    item.EnsureVisible();
                    break;
                }
            }
        }

        /// <summary>While the preview is open, everything that changes the list or the options is disabled.</summary>
        private void SetPreviewOpen(bool open)
        {
            _btnOpenFolder.Enabled = !open;
            _btnOpenFile.Enabled = !open;
            _btnProcess.Enabled = !open;
            _btnLearning.Enabled = !open;
            _chkSubfolders.Enabled = !open;
            _numSquare.Enabled = !open;
            _cmbSize.Enabled = !open;
            _cmbUpscale.Enabled = !open && Waifu2xUpscaler.Available;
            _btnWaifu2x.Enabled = !open && Waifu2xUpscaler.Available;
            _chkSmart.Enabled = !open && Directory.Exists(ModelHub.ModelsDir);
            _list.Enabled = !open;
            _btnPreview.Text = open ? "Show preview" : "Preview / Adjust";
            if (!open) RefreshAllStatus();
        }

        private void SetBusy(bool busy)
        {
            _btnOpenFolder.Enabled = !busy;
            _btnOpenFile.Enabled = !busy;
            _chkSubfolders.Enabled = !busy;
            _numSquare.Enabled = !busy;
            _cmbSize.Enabled = !busy;
            _cmbUpscale.Enabled = !busy && Waifu2xUpscaler.Available;
            _btnWaifu2x.Enabled = !busy && Waifu2xUpscaler.Available;
            _chkSmart.Enabled = !busy && Directory.Exists(ModelHub.ModelsDir);
            _btnPreview.Enabled = !busy;
            _btnLearning.Enabled = !busy;
            _listMenu.Enabled = !busy;
            _btnProcess.Text = busy ? "Stop" : "Process";
            _btnProcess.Enabled = true;
            UseWaitCursor = false;
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (IsBusy)
            {
                var r = MessageBox.Show(this, "Images are still being processed. Stop and exit?", AppInfo.Name,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                _cts.Cancel();
            }
            if (_preview != null && !_preview.IsDisposed) _preview.Close();
            _settings.Save();
            FramingSession.CleanupTemp();
        }

        // ================================================================== Log

        private void UI(Action a)
        {
            if (IsDisposed) return;
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }

        private void Log(string message) => Log(message, Color.Gainsboro);

        private void Log(string message, Color color)
        {
            UI(() =>
            {
                if (_log.IsDisposed) return;
                string line = message.Length == 0 ? "" : $"{DateTime.Now:HH:mm:ss}  {message}";
                _log.SelectionStart = _log.TextLength;
                _log.SelectionLength = 0;
                _log.SelectionColor = color;
                _log.AppendText(line + Environment.NewLine);
                _log.ScrollToCaret();
            });
        }
    }
}
