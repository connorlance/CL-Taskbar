using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CLTaskbar
{
    internal partial class SettingsForm
    {
        // A window for picking Windows tools, settings and open apps to add.
        // Replaces the very long sub-menus: search box, categories on the left, and a list
        // that wraps into columns. Pick several with Ctrl/Shift, or double-click one.
        class PickerDialog : Form
        {
            class Entry
            {
                public string Category, Name, IconKey;
                public Func<Image> LoadIcon;      // returns null while the icon is still loading
                public Func<LaunchItem> Make;
            }

            public const string AllCat = "All", OpenCat = "Apps that are open";

            readonly SettingsForm f;
            readonly List<Entry> entries = new List<Entry>();
            readonly TextBox search;
            readonly ListBox cats;
            readonly ListView list;
            readonly ImageList images;
            readonly Button add;
            readonly Timer iconTimer = new Timer { Interval = 400 };
            readonly HashSet<string> haveIcon = new HashSet<string>();
            public readonly List<LaunchItem> Picked = new List<LaunchItem>();

            public PickerDialog(SettingsForm owner, string startCategory)
            {
                f = owner;
                Text = "Add Windows tools, settings and apps";
                FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; MinimizeBox = false; ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.None;
                BackColor = f.cSurface; ForeColor = f.cText; Font = f.fBody;
                ClientSize = new Size(f.P(760), f.P(480));
                MinimumSize = new Size(f.P(520), f.P(340));
                KeyPreview = true;

                // ----- what can be picked -----
                foreach (var p in Presets.All)
                {
                    var pp = p;
                    entries.Add(new Entry { Category = p.Category, Name = p.Name, IconKey = "p|" + p.Target, LoadIcon = () => f.PresetIcon(pp.Target), Make = pp.Clone });
                }
                try
                {
                    foreach (var w in f.bar.OpenAppsForPicker().OrderBy(x => Icons.FriendlyName(x)))
                    {
                        var ww = w;
                        entries.Add(new Entry
                        {
                            Category = OpenCat, Name = Icons.FriendlyName(w), IconKey = "w|" + w.GroupKey,
                            LoadIcon = () => Icons.ForWindow(ww, f.P(16)),
                            Make = () => f.ItemFromApp(new AppNode { Key = ww.GroupKey, Sample = ww })
                        });
                    }
                }
                catch { }

                // ----- controls -----
                int m = f.P(14);
                search = new TextBox { BorderStyle = BorderStyle.FixedSingle, BackColor = f.cInput, ForeColor = f.cText, Location = new Point(m, m), Width = ClientSize.Width - m * 2, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
                search.HandleCreated += (s2, e2) => { try { SendMessage(search.Handle, 0x1501, (IntPtr)1, "Search, for example: sound, printers, device manager"); } catch { } };
                search.TextChanged += (s2, e2) => Fill();
                search.KeyDown += (s2, e2) =>
                {
                    // arrow down jumps into the results
                    if (e2.KeyCode == Keys.Down && list.Items.Count > 0) { list.Focus(); list.Items[0].Selected = true; list.Items[0].Focused = true; e2.Handled = true; }
                };
                Controls.Add(search);

                int top = search.Bottom + f.P(10);
                int bottomBar = f.P(52);

                cats = new ListBox
                {
                    BorderStyle = BorderStyle.None, BackColor = f.cSurface, ForeColor = f.cText, IntegralHeight = false,
                    DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = f.P(30),
                    Location = new Point(m, top), Size = new Size(f.P(190), ClientSize.Height - top - bottomBar),
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left
                };
                cats.Items.Add(AllCat);
                foreach (var c in entries.Select(e => e.Category).Distinct()) if (c != OpenCat) cats.Items.Add(c);
                cats.Items.Add(OpenCat);
                cats.DrawItem += DrawCategory;
                cats.SelectedIndexChanged += (s2, e2) =>
                {
                    if (cats.SelectedIndex < 0 && cats.Items.Count > 0) { cats.SelectedIndex = 0; return; }   // never "nothing selected"
                    Fill();
                };
                Controls.Add(cats);

                images = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(f.P(16), f.P(16)) };
                images.Images.Add("", new Bitmap(f.P(16), f.P(16)));   // blank placeholder while icons load

                list = new ListView
                {
                    View = View.List, MultiSelect = true, HideSelection = false, BorderStyle = BorderStyle.FixedSingle,
                    BackColor = f.cInput, ForeColor = f.cText, SmallImageList = images, LabelWrap = false,
                    Location = new Point(cats.Right + f.P(10), top),
                    Size = new Size(ClientSize.Width - cats.Right - f.P(10) - m, ClientSize.Height - top - bottomBar),
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
                };
                list.HandleCreated += (s2, e2) => { try { SetWindowTheme(list.Handle, f.dark ? "DarkMode_Explorer" : "Explorer", null); } catch { } };
                list.SelectedIndexChanged += (s2, e2) => UpdateAddButton();
                list.ItemActivate += (s2, e2) => Finish();   // double-click or Enter adds what's selected
                Controls.Add(list);

                var hint = new Label
                {
                    Text = "Double-click to add one. Hold Ctrl or Shift to pick several.", AutoSize = true, ForeColor = f.cDim,
                    Location = new Point(m, ClientSize.Height - bottomBar + f.P(16)), Anchor = AnchorStyles.Bottom | AnchorStyles.Left
                };
                Controls.Add(hint);

                var cancel = f.MakeButton("Cancel");
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                cancel.AutoSize = false;
                cancel.Size = new Size(Math.Max(f.P(90), cancel.PreferredSize.Width), Math.Max(f.P(30), cancel.PreferredSize.Height));
                cancel.Location = new Point(ClientSize.Width - m - cancel.Width, ClientSize.Height - bottomBar + f.P(12));
                Controls.Add(cancel);
                add = f.MakeButton("Add", true);
                add.AutoSize = false;
                add.Size = new Size(f.P(110), cancel.Height);
                add.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                add.Location = new Point(cancel.Left - f.P(8) - add.Width, cancel.Top);
                add.Click += (s2, e2) => Finish();
                Controls.Add(add);
                CancelButton = cancel;

                int start = cats.Items.IndexOf(startCategory ?? AllCat);
                cats.SelectedIndex = start < 0 ? 0 : start;
                Fill();

                iconTimer.Tick += (s2, e2) => LoadIcons();
                Shown += (s2, e2) =>
                {
                    // select the starting category again once the window is really on screen: Windows can
                    // drop a selection made before the list exists, or a stray click can land on the list
                    // as the window opens
                    int want = cats.Items.IndexOf(startCategory ?? AllCat);
                    if (want < 0) want = 0;
                    if (cats.SelectedIndex != want) cats.SelectedIndex = want; else Fill();
                    cats.TopIndex = 0;
                    search.Focus(); LoadIcons(); iconTimer.Start();
                };
                FormClosed += (s2, e2) => { iconTimer.Stop(); iconTimer.Dispose(); };
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                try { if (f.dark) { int on = 1; if (Native.DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) Native.DwmSetWindowAttribute(Handle, 19, ref on, 4); } } catch { }
            }

            void DrawCategory(object sender, DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                var r = e.Bounds;
                using (var b = new SolidBrush(f.cSurface)) e.Graphics.FillRectangle(b, r);
                if (sel)
                {
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(f.cHover)) using (var p = BarForm.RoundRect(Rectangle.Inflate(r, -f.P(2), -f.P(2)), f.P(4))) e.Graphics.FillPath(b, p);
                    using (var b = new SolidBrush(f.cAccent)) using (var p = BarForm.RoundRect(new Rectangle(r.X + f.P(3), r.Y + r.Height / 2 - f.P(8), f.P(3), f.P(16)), f.P(1))) e.Graphics.FillPath(b, p);
                }
                string name = cats.Items[e.Index].ToString();
                int count = name == AllCat ? entries.Count : entries.Count(x => x.Category == name);
                var tr = new Rectangle(r.X + f.P(12), r.Y, r.Width - f.P(16), r.Height);
                TextRenderer.DrawText(e.Graphics, name, Font, tr, f.cText, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(e.Graphics, count.ToString(), Font, tr, f.cDim, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }

            IEnumerable<Entry> Current()
            {
                string cat = cats.SelectedItem as string ?? AllCat;
                string q = search.Text.Trim();
                var src = cat == AllCat ? entries : entries.Where(e => e.Category == cat);
                if (q.Length == 0) return src;
                var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                return src.Where(e => words.All(w => e.Name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0
                                                  || e.Category.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));
            }

            void Fill()
            {
                list.BeginUpdate();
                list.Items.Clear();
                foreach (var e in Current())
                {
                    var it = new ListViewItem(e.Name) { Tag = e, ImageKey = haveIcon.Contains(e.IconKey) ? e.IconKey : "" };
                    if (cats.SelectedItem as string == AllCat) it.ToolTipText = e.Category;
                    list.Items.Add(it);
                }
                if (list.Items.Count == 0)
                    list.Items.Add(new ListViewItem(cats.SelectedItem as string == OpenCat && search.Text.Length == 0 ? "No apps are open" : "Nothing matches") { ForeColor = f.cDim });
                list.EndUpdate();
                UpdateAddButton();
            }

            // Icons arrive in the background; drop them in as they're ready
            void LoadIcons()
            {
                bool pending = false;
                foreach (ListViewItem it in list.Items)
                {
                    if (!(it.Tag is Entry e) || haveIcon.Contains(e.IconKey)) continue;
                    Image img = null;
                    try { img = e.LoadIcon(); } catch { }
                    if (img == null) { pending = true; continue; }
                    images.Images.Add(e.IconKey, img);
                    haveIcon.Add(e.IconKey);
                    it.ImageKey = e.IconKey;
                }
                if (!pending) iconTimer.Interval = 1500;   // everything visible is done; just check now and then
            }

            IEnumerable<Entry> Selected() => list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as Entry).Where(e => e != null);

            void UpdateAddButton()
            {
                int n = Selected().Count();
                add.Enabled = n > 0;
                add.Text = n > 1 ? "Add " + n : "Add";
            }

            void Finish()
            {
                foreach (var e in Selected())
                {
                    LaunchItem it = null;
                    try { it = e.Make(); } catch { }
                    if (it != null) Picked.Add(it);
                }
                if (Picked.Count == 0) return;
                DialogResult = DialogResult.OK;
                Close();
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                // typing anywhere goes to the search box
                if (!search.Focused && !e.Control && !e.Alt && ((e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z) || (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9)))
                {
                    search.Focus();
                    search.SelectionStart = search.TextLength;
                }
                base.OnKeyDown(e);
            }
        }

        void ShowPicker(object ctx, string category)
        {
            WarmPresetIcons();
            using (var dlg = new PickerDialog(this, category))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AddMany(dlg.Picked, ctx);
            }
        }
    }
}
