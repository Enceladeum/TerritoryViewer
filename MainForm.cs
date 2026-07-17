using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TerritoryViewer
{
    public class MainForm : Form
    {
        readonly TextBox txtGame = new TextBox();
        readonly TextBox txtTerritory = new TextBox();
        readonly TextBox txtOut = new TextBox();
        readonly CheckBox chkObj = new CheckBox();
        readonly Button btnDump = new Button();
        readonly TextBox txtLog = new TextBox();

        public MainForm()
        {
            Text = "Territory Viewer";
            Width = 720; Height = 560;
            MinimumSize = new System.Drawing.Size(560, 400);

            var lblGame = new Label { Text = "sqpack folder:", Left = 12, Top = 15, Width = 100 };
            txtGame.SetBounds(115, 12, 480, 24);
            txtGame.Text = @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";
            txtGame.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var btnGame = new Button { Text = "...", Left = 600, Top = 11, Width = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnGame.Click += (s, e) => Browse(txtGame);

            var lblTerr = new Label { Text = "Territory ID:", Left = 12, Top = 47, Width = 100 };
            txtTerritory.SetBounds(115, 44, 200, 24);
            var lblHint = new Label { Text = "(TerritoryType row, e.g. 1345 — or a bg level dir)", Left = 322, Top = 47, Width = 320, ForeColor = System.Drawing.SystemColors.GrayText };

            var lblOut = new Label { Text = "Output folder:", Left = 12, Top = 79, Width = 100 };
            txtOut.SetBounds(115, 76, 480, 24);
            txtOut.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "lgb-dumps");
            txtOut.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            var btnOut = new Button { Text = "...", Left = 600, Top = 75, Width = 32, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            btnOut.Click += (s, e) => Browse(txtOut);

            chkObj.Text = "Export collision-mesh.obj (world-space, can be large)";
            chkObj.SetBounds(115, 106, 400, 24);
            chkObj.Checked = true;

            btnDump.Text = "Dump";
            btnDump.SetBounds(115, 136, 120, 30);
            btnDump.Click += async (s, e) => await RunDump();

            txtLog.Multiline = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.ReadOnly = true;
            txtLog.Font = new System.Drawing.Font("Consolas", 9f);
            txtLog.SetBounds(12, 178, 680, 330);
            txtLog.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

            Controls.AddRange(new Control[] { lblGame, txtGame, btnGame, lblTerr, txtTerritory, lblHint, lblOut, txtOut, btnOut, chkObj, btnDump, txtLog });
        }

        void Browse(TextBox target)
        {
            using var dlg = new FolderBrowserDialog();
            if (Directory.Exists(target.Text)) dlg.SelectedPath = target.Text;
            if (dlg.ShowDialog(this) == DialogResult.OK) target.Text = dlg.SelectedPath;
        }

        void Log(string msg)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), msg); return; }
            txtLog.AppendText(msg + Environment.NewLine);
        }

        async Task RunDump()
        {
            var game = txtGame.Text.Trim();
            var terr = txtTerritory.Text.Trim();
            var outRoot = txtOut.Text.Trim();
            if (!Directory.Exists(game)) { Log("!! sqpack folder not found: " + game); return; }
            if (terr.Length == 0) { Log("!! enter a territory ID"); return; }
            bool obj = chkObj.Checked;

            btnDump.Enabled = false;
            txtLog.Clear();
            try
            {
                var outDir = await Task.Run(() => DumpCore.Run(game, terr, outRoot, obj, Log));
                Log("");
                Log("Done: " + outDir);
            }
            catch (Exception ex)
            {
                Log("!! " + ex.Message);
                Log(ex.StackTrace ?? "");
            }
            finally { btnDump.Enabled = true; }
        }
    }
}
