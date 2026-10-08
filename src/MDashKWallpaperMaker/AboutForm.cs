using System.Drawing;
using System.Windows.Forms;

namespace MDashKWallpaperMaker
{
    internal sealed class AboutForm : Form
    {
        public AboutForm()
        {
            SuspendLayout();
            Text = "About " + AppInfo.Name;
            Font = new Font("Segoe UI", 9F);
            // Designed at 96 DPI; scaled to the system DPI (ApplicationHighDpiMode = SystemAware).
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            Icon = AppInfo.LoadIcon();
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(24, 20, 24, 16);

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 2,
                Dock = DockStyle.Fill,
            };

            var icon = new PictureBox
            {
                Image = AppInfo.LoadIconImage(),
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(64, 64),
                Anchor = AnchorStyles.None,
                Margin = new Padding(0, 0, 16, 0),
            };

            var title = new Label
            {
                Text = AppInfo.Name,
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 2),
            };
            var version = new Label
            {
                Text = "Version " + AppInfo.Version,
                AutoSize = true,
                Margin = new Padding(2, 0, 0, 0),
            };
            var text = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0),
            };
            text.Controls.Add(title);
            text.Controls.Add(version);

            var ok = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.None,
                AutoSize = true,
                MinimumSize = new Size(88, 0),
                Margin = new Padding(0, 20, 0, 0),
            };

            layout.Controls.Add(icon, 0, 0);
            layout.Controls.Add(text, 1, 0);
            layout.Controls.Add(ok, 0, 1);
            layout.SetColumnSpan(ok, 2);

            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = ok;

            ResumeLayout(false);
            PerformLayout();
        }
    }
}
