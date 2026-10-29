// Display Tuner: screen-wide color filters (OLED-style movie look, vivid, warm, night) with a Windows 11 style GUI and tray icon.
// Color, contrast, brightness and warmth use the Windows Magnification color matrix; black level uses the display gamma ramp.
// Presets are tuned automatically from the display's EDID (name, color gamut, OLED or not).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MAGCOLOREFFECT { [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)] public float[] transform; }
    [DllImport("Magnification.dll")] public static extern bool MagInitialize();
    [DllImport("Magnification.dll")] public static extern bool MagUninitialize();
    [DllImport("Magnification.dll")] public static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT effect);
    [DllImport("gdi32.dll")] public static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Windows 11: dark title bar and rounded window corners
    public static void ModernWindow(IntPtr hwnd)
    {
        int dark = 1, round = 2;
        DwmSetWindowAttribute(hwnd, 20, ref dark, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);  // DWMWA_WINDOW_CORNER_PREFERENCE = round
    }
}

// Brightness -50..50, contrast/saturation 50..200 (%), black level 0..100 (strength of the near-black deepening), warmth -50..50
class Look
{
    public int Brightness, Contrast = 100, Saturation = 100, BlackLevel, Warmth;
    public Look Clone() { return (Look)MemberwiseClone(); }
    public bool Same(Look o) { return o.Brightness == Brightness && o.Contrast == Contrast && o.Saturation == Saturation && o.BlackLevel == BlackLevel && o.Warmth == Warmth; }
    public static Look Make(int b, int c, int s, int k, int w) { return new Look { Brightness = b, Contrast = c, Saturation = s, BlackLevel = k, Warmth = w }; }
}

static class Filter
{
    static bool magReady;

    // Row-vector 5x5 matrix: [r g b a 1] * M
    static float[] Matrix(Look l)
    {
        float s = l.Saturation / 100f, c = l.Contrast / 100f, br = l.Brightness / 200f;
        const float lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
        float[,] sat = {
            { lr*(1-s)+s, lr*(1-s),   lr*(1-s)   },
            { lg*(1-s),   lg*(1-s)+s, lg*(1-s)   },
            { lb*(1-s),   lb*(1-s),   lb*(1-s)+s } };
        // warmth: positive keeps red and trims blue (and a little green); negative trims red. Never boosts above 1, so no clipping.
        float w = l.Warmth / 100f;
        float[] gain = w >= 0 ? new[] { 1f, 1 - 0.10f * w, 1 - 0.45f * w } : new[] { 1 + 0.35f * w, 1 + 0.08f * w, 1f };
        var m = new float[25];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                m[i * 5 + j] = sat[i, j] * c * gain[j];
        m[3 * 5 + 3] = 1;
        // contrast pivots around mid grey, then brightness shifts everything
        for (int j = 0; j < 3; j++) m[4 * 5 + j] = (0.5f - 0.5f * c + br) * gain[j];
        m[4 * 5 + 4] = 1;
        return m;
    }

    // Deepens only the darkest tones (bottom ~22%); mid-tones and highlights stay untouched, so the picture doesn't get darker overall.
    // Above 60% the curve gets stronger and slightly wider for a true-black look.
    static void SetBlackLevel(int strength)
    {
        double s = strength / 100.0, extra = Math.Max(0, strength - 60) / 40.0;
        double k = Math.Min(0.97, s * 0.7 + extra * 0.25), t = 0.22 + extra * 0.08;
        var ramp = new ushort[256 * 3];
        for (int i = 0; i < 256; i++)
        {
            double x = i / 255.0, y = x;
            if (x < t) { double d = 1 - x / t; y = x * (1 - k * d * d); }
            ushort u = (ushort)Math.Round(Math.Max(0, Math.Min(1, y)) * 65535);
            ramp[i] = ramp[256 + i] = ramp[512 + i] = u;
        }
        IntPtr dc = Native.GetDC(IntPtr.Zero);
        Native.SetDeviceGammaRamp(dc, ramp);
        Native.ReleaseDC(IntPtr.Zero, dc);
    }

    public static bool Apply(Look l)
    {
        if (!magReady) magReady = Native.MagInitialize();
        var e = new Native.MAGCOLOREFFECT { transform = Matrix(l) };
        bool ok = magReady && Native.MagSetFullscreenColorEffect(ref e);
        SetBlackLevel(l.BlackLevel);
        return ok;
    }

    public static void Clear()
    {
        if (magReady) { var e = new Native.MAGCOLOREFFECT { transform = Matrix(new Look()) }; Native.MagSetFullscreenColorEffect(ref e); }
        SetBlackLevel(0);
    }

    public static void Shutdown()
    {
        Clear();
        if (magReady) { Native.MagUninitialize(); magReady = false; }
    }
}

// ---------- display detection ----------
// Reads the main display's EDID (the data every screen reports about itself) to get its name and color gamut.
class PanelInfo
{
    public string Name = "Unknown display";
    public double GamutVsSrgb = 1.0;   // area of the panel's color triangle relative to sRGB (1.0 = full sRGB area)
    public bool Detected, IsOled;

    static readonly Dictionary<string, string> Makers = new Dictionary<string, string> {
        { "CMN", "Innolux" }, { "BOE", "BOE" }, { "AUO", "AU Optronics" }, { "LGD", "LG Display" }, { "SDC", "Samsung Display" },
        { "SHP", "Sharp" }, { "IVO", "InfoVision" }, { "CSO", "CSOT" }, { "LEN", "Lenovo" }, { "DEL", "Dell" }, { "SAM", "Samsung" },
        { "GSM", "LG" }, { "ACI", "ASUS" }, { "AUS", "ASUS" }, { "ACR", "Acer" }, { "HWP", "HP" }, { "MSI", "MSI" }, { "AOC", "AOC" } };

    public string Summary()
    {
        if (!Detected) return "Display not detected  •  using standard presets";
        string kind = IsOled ? "OLED" : GamutVsSrgb < 0.75 ? "narrow-gamut panel" : GamutVsSrgb < 0.95 ? "mid-gamut panel" : GamutVsSrgb < 1.2 ? "sRGB panel" : "wide-gamut panel";
        return string.Format(CultureInfo.InvariantCulture, "{0}  •  {1}  •  {2:0}% sRGB area", Name, kind, GamutVsSrgb * 100);
    }

    public static PanelInfo Detect()
    {
        var info = new PanelInfo();
        try
        {
            byte[] edid = null;
            // Active monitors, from WMI; their EDID lives under the same instance path in the registry
            using (var s = new System.Management.ManagementObjectSearcher(@"root\wmi", "SELECT InstanceName, Active FROM WmiMonitorID"))
                foreach (System.Management.ManagementObject m in s.Get())
                {
                    if (!(bool)m["Active"]) continue;
                    string inst = ((string)m["InstanceName"]);
                    int us = inst.LastIndexOf('_'); if (us > 0) inst = inst.Substring(0, us);
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + inst + @"\Device Parameters"))
                        if (k != null) edid = k.GetValue("EDID") as byte[];
                    if (edid != null) break;
                }
            if (edid == null || edid.Length < 128) return info;
            info.Parse(edid); info.Detected = true;
        }
        catch { }
        return info;
    }

    void Parse(byte[] e)
    {
        // manufacturer: three 5-bit letters
        int id = (e[8] << 8) | e[9];
        string pnp = "" + (char)('A' - 1 + ((id >> 10) & 31)) + (char)('A' - 1 + ((id >> 5) & 31)) + (char)('A' - 1 + (id & 31));
        string maker; if (!Makers.TryGetValue(pnp, out maker)) maker = pnp;
        // model text from the descriptor blocks: 0xFC = monitor name, 0xFE = text (laptop panels put the model here)
        string fc = null, fe = null;
        for (int o = 54; o <= 108; o += 18)
        {
            if (e[o] != 0 || e[o + 1] != 0) continue;
            string txt = System.Text.Encoding.ASCII.GetString(e, o + 5, 13).Split('\n')[0].Trim();
            if (e[o + 3] == 0xFC && txt.Length > 0) fc = txt;
            if (e[o + 3] == 0xFE && txt.Length > 0) fe = txt;   // last one wins: usually the panel model
        }
        string model = fc ?? fe ?? ("0x" + ((e[11] << 8) | e[10]).ToString("X4"));
        Name = model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : maker + " " + model;
        // color primaries (CIE xy, 10-bit) -> triangle area
        Func<int, int, int, double> c = (hi, lo, sh) => ((hi << 2) | ((lo >> sh) & 3)) / 1024.0;
        double rx = c(e[27], e[25], 6), ry = c(e[28], e[25], 4), gx = c(e[29], e[25], 2), gy = c(e[30], e[25], 0);
        double bx = c(e[31], e[26], 6), by = c(e[32], e[26], 4);
        double area = Math.Abs(rx * (gy - by) + gx * (by - ry) + bx * (ry - gy)) / 2;
        if (area > 0.02) GamutVsSrgb = area / 0.1121;
        // OLED: model text says so, or a Samsung Display ATNA* laptop OLED panel
        IsOled = (fc + " " + fe).IndexOf("OLED", StringComparison.OrdinalIgnoreCase) >= 0 || (pnp == "SDC" && (fe ?? "").StartsWith("ATNA"));
    }
}

static class Tuning
{
    // Narrow-gamut panels look washed out, so they get the most extra color; wide-gamut panels get almost none.
    // LCD/IPS panels get their darkest tones deepened; OLED already has true black, so black level stays off.
    public static List<KeyValuePair<string, Look>> Build(PanelInfo p)
    {
        double r = p.GamutVsSrgb;
        int boost = r < 0.75 ? 15 : r < 0.95 ? 8 : r < 1.2 ? 4 : 0;
        int vivid = r < 0.75 ? 13 : r < 0.95 ? 10 : r < 1.2 ? 8 : 5;
        int k = p.IsOled ? 0 : 35;
        Func<int, int> blk = v => p.IsOled ? 0 : v;   // OLED: no black deepening needed
        return new List<KeyValuePair<string, Look>> {
            P("Natural",      new Look()),
            P("OLED Movie",   Look.Make(0, 104, 100 + boost, k, 0)),
            P("True Black",   Look.Make(0, 107, 100 + boost, blk(85), 0)),
            P("AMOLED Punch", Look.Make(0, 108, 100 + boost + vivid + 5, blk(70), -2)),
            P("Deep Black",   Look.Make(0, 105, Math.Max(100, 100 + boost - 5), blk(60), 0)),
            P("Dark Room",    Look.Make(-4, 104, 100 + boost, blk(70), 6)),
            P("OLED Vivid",   Look.Make(0, 106, 100 + boost + vivid, Math.Max(0, k - 5), -3)),
            P("Cinema",       Look.Make(0, 103, Math.Max(100, 100 + boost - 7), Math.Max(0, k - 5), 12)),
            P("Anime",        Look.Make(0, 103, 100 + boost + vivid + 8, blk(25), -4)),
            P("Sports",       Look.Make(0, 106, 100 + boost + 8, blk(20), -6)),
            P("Gaming",       Look.Make(0, 105, 100 + boost + 6, blk(30), 0)),
            P("Noir",         Look.Make(0, 110, 0, blk(60), 0)),
            P("Reading",      Look.Make(0, 98, 90, 0, 20)),
            P("Night",        Look.Make(-6, 100, 95, blk(15), 40)),
        };
    }
    static KeyValuePair<string, Look> P(string name, Look look) { return new KeyValuePair<string, Look>(name, look); }
}

// ---------- Windows 11 style controls ----------
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(32, 32, 32);
    public static readonly Color Card = Color.FromArgb(43, 43, 43);
    public static readonly Color CardHover = Color.FromArgb(50, 50, 50);
    public static readonly Color Stroke = Color.FromArgb(58, 58, 58);
    public static readonly Color Text = Color.FromArgb(242, 242, 242);
    public static readonly Color SubText = Color.FromArgb(160, 160, 160);
    public static readonly Color Accent = Color.FromArgb(96, 205, 255);
    public static readonly Color OnAccent = Color.FromArgb(20, 20, 20);
    public static readonly Font Body = new Font("Segoe UI Variable Text", 10f);
    public static readonly Font Small = new Font("Segoe UI Variable Text", 9f);
    public static readonly Font Title = new Font("Segoe UI Variable Display", 18f, FontStyle.Bold);
    public static readonly Font Section = new Font("Segoe UI Variable Text", 10.5f, FontStyle.Bold);

    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        if (d > r.Height) d = r.Height; if (d > r.Width) d = r.Width;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    public static void Smooth(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; g.PixelOffsetMode = PixelOffsetMode.HighQuality; }
}

class Card : Panel
{
    public Card() { DoubleBuffered = true; SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true); BackColor = Color.Transparent; Padding = new Padding(16); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Smooth(e.Graphics);
        using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8))
        {
            using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillPath(b, p);
            using (var pen = new Pen(Theme.Stroke)) e.Graphics.DrawPath(pen, p);
        }
    }
}

class PillButton : Control
{
    public bool Selected, Primary;
    bool hover;
    public PillButton() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; Font = Theme.Body; Cursor = Cursors.Hand; Height = 34; }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Smooth(g);
        bool filled = Selected || Primary;
        Color fill = filled ? (hover ? ControlPaint.Light(Theme.Accent, 0.15f) : Theme.Accent) : (hover ? Color.FromArgb(62, 62, 62) : Color.FromArgb(55, 55, 55));
        using (var p = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), Primary ? 6 : Height / 2f))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, p);
            if (!filled) using (var pen = new Pen(Color.FromArgb(70, 70, 70))) g.DrawPath(pen, p);
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, filled ? Theme.OnAccent : Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

class ToggleSwitch : Control
{
    bool on;
    public event EventHandler Toggled;
    public bool On { get { return on; } set { if (on != value) { on = value; Invalidate(); } } }
    public ToggleSwitch() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; Size = new Size(44, 22); Cursor = Cursors.Hand; }
    protected override void OnClick(EventArgs e) { On = !On; if (Toggled != null) Toggled(this, e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Smooth(g);
        var r = new RectangleF(1, 1, Width - 2, Height - 2);
        using (var p = Theme.Round(r, r.Height / 2))
        {
            if (on) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
            else using (var pen = new Pen(Theme.SubText, 1.2f)) g.DrawPath(pen, p);
        }
        float d = on ? r.Height - 8 : r.Height - 10, x = on ? r.Right - d - 4 : r.X + 5;
        using (var b = new SolidBrush(on ? Theme.OnAccent : Theme.SubText)) g.FillEllipse(b, x, r.Y + (r.Height - d) / 2, d, d);
    }
}

class ModernSlider : Control
{
    int min, max, val; bool drag, hover;
    public event EventHandler ValueChanged;
    public int Minimum { get { return min; } set { min = value; Invalidate(); } }
    public int Maximum { get { return max; } set { max = value; Invalidate(); } }
    public int Value { get { return val; } set { int v = Math.Max(min, Math.Min(max, value)); if (v != val) { val = v; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); } } }
    public ModernSlider() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true); BackColor = Color.Transparent; Height = 26; Cursor = Cursors.Hand; TabStop = true; }
    float Track0 { get { return 10; } }
    float TrackW { get { return Width - 20; } }
    void SetFromX(int x) { Value = min + (int)Math.Round((x - Track0) / TrackW * (max - min)); }
    protected override void OnMouseDown(MouseEventArgs e) { drag = true; Focus(); SetFromX(e.X); }
    protected override void OnMouseMove(MouseEventArgs e) { if (drag) SetFromX(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { drag = false; }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { Value += e.Delta > 0 ? 1 : -1; }
    protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Left) Value--; if (e.KeyCode == Keys.Right) Value++; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Theme.Smooth(g);
        float cy = Height / 2f, frac = max == min ? 0 : (val - min) / (float)(max - min), x = Track0 + frac * TrackW;
        using (var p = Theme.Round(new RectangleF(Track0, cy - 2, TrackW, 4), 2)) using (var b = new SolidBrush(Color.FromArgb(90, 90, 90))) g.FillPath(b, p);
        if (x - Track0 > 1) using (var p = Theme.Round(new RectangleF(Track0, cy - 2, x - Track0, 4), 2)) using (var b = new SolidBrush(Theme.Accent)) g.FillPath(b, p);
        using (var b = new SolidBrush(Color.FromArgb(69, 69, 69))) g.FillEllipse(b, x - 10, cy - 10, 20, 20);
        using (var pen = new Pen(Color.FromArgb(80, 80, 80))) g.DrawEllipse(pen, x - 10, cy - 10, 20, 20);
        float inner = (drag ? 8 : hover ? 14 : 11);
        using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, x - inner / 2, cy - inner / 2, inner, inner);
    }
}

// ---------- main window ----------
class MainForm : Form
{
    public const string AppName = "KevinB's Display Tuner";
    static readonly string Dir = Path.GetDirectoryName(Application.ExecutablePath);
    static readonly string SettingsFile = Path.Combine(Dir, "settings.ini");
    static readonly string StartupLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), AppName + ".lnk");

    // Presets are built from the detected panel (see Tuning.Build)
    static readonly PanelInfo Panel = PanelInfo.Detect();
    static readonly List<KeyValuePair<string, Look>> Presets = Tuning.Build(Panel);

    Look current = new Look();
    bool enabled, loading, reallyExit;
    readonly ToggleSwitch swOn = new ToggleSwitch(), swStartup = new ToggleSwitch();
    readonly Label lblOn = new Label(), status = new Label();
    readonly List<PillButton> chips = new List<PillButton>();
    readonly Dictionary<string, ModernSlider> sliders = new Dictionary<string, ModernSlider>();
    readonly Dictionary<string, Label> valueLabels = new Dictionary<string, Label>();
    readonly Dictionary<string, Func<int, string>> formats = new Dictionary<string, Func<int, string>>();
    readonly NotifyIcon tray = new NotifyIcon();
    Card top, pc, sc, bc;
    System.Windows.Forms.Panel content;
    Label lblTitle, lblSummary;
    PillButton save;
    const int HotkeyId = 0x4454;

    public MainForm(bool startHidden)
    {
        Text = AppName; Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.Sizable; MaximizeBox = true; AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Body; DoubleBuffered = true;
        content = new System.Windows.Forms.Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Bg };
        Controls.Add(content);
        ClientSize = new Size(480, 682); StartPosition = FormStartPosition.CenterScreen;

        lblTitle = new Label { Text = AppName, Font = Theme.Title, AutoSize = false, AutoEllipsis = true, Location = new Point(22, 16), Size = new Size(440, 40), BackColor = Color.Transparent };
        content.Controls.Add(lblTitle);
        lblSummary = new Label { Text = Panel.Summary(), Font = Theme.Small, ForeColor = Theme.SubText, AutoSize = false, AutoEllipsis = true, Location = new Point(24, 56), Size = new Size(440, 20), BackColor = Color.Transparent };
        content.Controls.Add(lblSummary);

        // card: on/off
        top = new Card { Location = new Point(18, 86), Size = new Size(444, 60) };
        top.Controls.Add(new Label { Text = "Color filter", Font = Theme.Section, AutoSize = true, Location = new Point(16, 10), BackColor = Color.Transparent });
        top.Controls.Add(new Label { Text = "Ctrl + Alt + F toggles it anywhere", Font = Theme.Small, ForeColor = Theme.SubText, AutoSize = true, Location = new Point(16, 32), BackColor = Color.Transparent });
        lblOn.AutoSize = true; lblOn.BackColor = Color.Transparent; lblOn.Location = new Point(338, 20);
        swOn.Location = new Point(380, 19); swOn.BackColor = Color.Transparent;
        swOn.Toggled += (s, e) => { enabled = swOn.On; ApplyNow(); };
        lblOn.Anchor = swOn.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        top.Controls.Add(lblOn); top.Controls.Add(swOn);
        content.Controls.Add(top);

        // card: presets
        pc = new Card { Location = new Point(18, 156), Size = new Size(444, 128) };
        pc.Controls.Add(new Label { Text = "Presets", Font = Theme.Section, AutoSize = true, Location = new Point(16, 10), BackColor = Color.Transparent });
        int cx = 16, cy = 40;
        foreach (var kv in Presets)
        {
            string name = kv.Key;
            var chip = new PillButton { Text = name, BackColor = Color.Transparent };
            chip.Width = TextRenderer.MeasureText(name, Theme.Body).Width + 30;
            if (cx + chip.Width > 428) { cx = 16; cy += 42; }
            chip.Location = new Point(cx, cy); cx += chip.Width + 8;
            chip.Click += (s, e) => { current = Find(name).Clone(); enabled = name != "Natural"; SyncControls(); ApplyNow(); };
            pc.Controls.Add(chip); chips.Add(chip);
        }
        pc.Height = cy + 34 + 14;
        content.Controls.Add(pc);

        // card: sliders (cards below follow the presets card's height)
        sc = new Card { Location = new Point(18, pc.Bottom + 10), Size = new Size(444, 262) };
        sc.Controls.Add(new Label { Text = "Fine tune", Font = Theme.Section, AutoSize = true, Location = new Point(16, 10), BackColor = Color.Transparent });
        int y = 40;
        AddSlider(sc, "Brightness", -50, 50, ref y, v => current.Brightness = v, v => v == 0 ? "0" : (v > 0 ? "+" : "") + v);
        AddSlider(sc, "Contrast", 50, 200, ref y, v => current.Contrast = v, v => v + "%");
        AddSlider(sc, "Color", 0, 200, ref y, v => current.Saturation = v, v => v + "%");
        AddSlider(sc, "Black level", 0, 100, ref y, v => current.BlackLevel = v, v => v == 0 ? "off" : v + "%");
        AddSlider(sc, "Warmth", -50, 50, ref y, v => current.Warmth = v, v => v == 0 ? "neutral" : v > 0 ? "warm +" + v : "cool " + v);
        content.Controls.Add(sc);

        // card: startup + actions
        bc = new Card { Location = new Point(18, sc.Bottom + 10), Size = new Size(444, 52) };
        bc.Controls.Add(new Label { Text = "Start with Windows", AutoSize = true, Location = new Point(16, 15), BackColor = Color.Transparent });
        swStartup.Location = new Point(380, 15); swStartup.BackColor = Color.Transparent; swStartup.On = File.Exists(StartupLink);
        swStartup.Toggled += (s, e) => SetStartup(swStartup.On);
        swStartup.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        bc.Controls.Add(swStartup);
        content.Controls.Add(bc);

        save = new PillButton { Text = "Save as default", Primary = true, Location = new Point(18, bc.Bottom + 8), Size = new Size(160, 32), BackColor = Color.Transparent };
        save.Click += (s, e) => { SaveSettings(); SetStatus("Saved as default"); };
        content.Controls.Add(save);
        status.ForeColor = Theme.SubText; status.Font = Theme.Small; status.BackColor = Color.Transparent; status.Location = new Point(190, bc.Bottom + 16); status.Size = new Size(275, 20);
        content.Controls.Add(status);
        ClientSize = new Size(480, save.Bottom + 24);   // room below the button
        MinimumSize = new Size(380, 300);   // can shrink freely; the content scrolls when the window is small
        // Open at the normal size, centred, and never taller than the screen's working area
        var work = Screen.PrimaryScreen.WorkingArea;
        Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height - 20));
        startSize = Size;
        content.Resize += (s, e) => Relayout();

        // tray
        tray.Icon = Icon; tray.Text = AppName; tray.Visible = true;
        var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkMenuColors()), ForeColor = Theme.Text, BackColor = Color.Transparent, ShowImageMargin = false };
        menu.Items.Add("Open " + AppName, null, (s, e) => ShowWindow());
        menu.Items.Add("Filter on / off   (Ctrl+Alt+F)", null, (s, e) => Toggle());
        menu.Items.Add(new ToolStripSeparator());
        foreach (var kv in Presets) { string key = kv.Key; menu.Items.Add(key, null, (s, e) => { current = Find(key).Clone(); enabled = key != "Natural"; SyncControls(); ApplyNow(); }); }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit (normal colors)", null, (s, e) => { reallyExit = true; Close(); });
        foreach (ToolStripItem it in menu.Items) it.ForeColor = Theme.Text;
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (s, e) => ShowWindow();

        LoadSettings();
        if (startHidden) { ShowInTaskbar = false; WindowState = FormWindowState.Minimized; Load += (s, e) => Hide(); }
    }

    class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color MenuItemSelected { get { return Color.FromArgb(60, 60, 60); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(60, 60, 60); } }
        public override Color MenuBorder { get { return Theme.Stroke; } }
        public override Color SeparatorDark { get { return Theme.Stroke; } }
        public override Color SeparatorLight { get { return Theme.Stroke; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
    }

    // Stretches the cards to the window width, re-wraps the preset buttons and stacks the cards again
    void Relayout()
    {
        if (pc == null || content == null || WindowState == FormWindowState.Minimized) return;
        content.SuspendLayout();
        // content.ClientSize excludes the scrollbar; when scrolled, positions are offset by the scroll position
        int w = Math.Max(300, content.ClientSize.Width - 36), oy = content.AutoScrollPosition.Y;
        top.Width = w; pc.Width = w; sc.Width = w; bc.Width = w;
        lblTitle.Width = w; lblSummary.Width = w - 4;
        lblOn.Visible = w >= 400;   // the On/Off word would overlap the hint text in a narrow window
        pc.Top = top.Bottom + 10;
        int cx = 16, cy = 40;
        foreach (var chip in chips)
        {
            if (cx + chip.Width > w - 16) { cx = 16; cy += 42; }
            chip.Location = new Point(cx, cy); cx += chip.Width + 8;
        }
        pc.Height = cy + 34 + 14;
        sc.Top = pc.Bottom + 10; bc.Top = sc.Bottom + 10;
        save.Top = bc.Bottom + 8; status.Top = bc.Bottom + 16; status.Width = Math.Max(80, w - 172);
        content.AutoScrollMinSize = new Size(0, save.Bottom - oy + 24);
        content.ResumeLayout();
        content.Invalidate(true);
    }

    static Look Find(string name) { foreach (var kv in Presets) if (kv.Key == name) return kv.Value; return new Look(); }

    void AddSlider(Control parent, string name, int min, int max, ref int y, Action<int> set, Func<int, string> fmt)
    {
        parent.Controls.Add(new Label { Text = name, AutoSize = true, Location = new Point(16, y), BackColor = Color.Transparent });
        var val = new Label { TextAlign = ContentAlignment.TopRight, Location = new Point(300, y), Size = new Size(128, 20), ForeColor = Theme.SubText, BackColor = Color.Transparent };
        val.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        parent.Controls.Add(val); valueLabels[name] = val; formats[name] = fmt;
        var sl = new ModernSlider { Minimum = min, Maximum = max, Location = new Point(10, y + 18), Width = 424, BackColor = Color.Transparent };
        sl.ValueChanged += (s, e) => {
            val.Text = fmt(sl.Value);
            if (loading) return;
            set(sl.Value); enabled = true; HighlightPreset(); ApplyNow();
        };
        sl.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        parent.Controls.Add(sl); sliders[name] = sl;
        y += 44;
    }

    void HighlightPreset()
    {
        foreach (var chip in chips) { chip.Selected = enabled && Find(chip.Text).Same(current); chip.Invalidate(); }
    }

    void SyncControls()
    {
        loading = true;
        Set("Brightness", current.Brightness); Set("Contrast", current.Contrast); Set("Color", current.Saturation);
        Set("Black level", current.BlackLevel); Set("Warmth", current.Warmth);
        loading = false;
        HighlightPreset();
    }
    void Set(string name, int v) { sliders[name].Value = v; valueLabels[name].Text = formats[name](sliders[name].Value); }

    void ApplyNow()
    {
        swOn.On = enabled; lblOn.Text = enabled ? "On" : "Off";
        if (enabled)
        {
            bool ok = Filter.Apply(current);
            string name = "Custom"; foreach (var kv in Presets) if (kv.Value.Same(current)) name = kv.Key;
            SetStatus(ok ? "Applied: " + name : "Couldn't apply the filter on this PC");
        }
        else { Filter.Clear(); SetStatus("Normal colors"); }
        HighlightPreset();
        tray.Text = AppName + " - " + (enabled ? "on" : "off");
    }

    void Toggle() { enabled = !enabled; ApplyNow(); }
    void SetStatus(string s) { status.Text = s; }
    void ShowWindow() { Show(); ShowInTaskbar = true; WindowState = FormWindowState.Normal; Activate(); }

    void LoadSettings()
    {
        if (File.Exists(SettingsFile))
        {
            var d = new Dictionary<string, int>();
            foreach (var line in File.ReadAllLines(SettingsFile))
            {
                var parts = line.Split('='); int v;
                if (parts.Length == 2 && int.TryParse(parts[1].Trim(), out v)) d[parts[0].Trim()] = v;
            }
            Func<string, int, int> g = (k, def) => d.ContainsKey(k) ? d[k] : def;
            current = Look.Make(g("Brightness", 0), g("Contrast", 100), g("Saturation", 100), g("BlackLevel", 0), g("Warmth", 0));
            enabled = g("Enabled", 0) == 1;
        }
        SyncControls();
        ApplyNow();
    }

    void SaveSettings()
    {
        File.WriteAllLines(SettingsFile, new[] {
            "Enabled=" + (enabled ? 1 : 0), "Brightness=" + current.Brightness, "Contrast=" + current.Contrast,
            "Saturation=" + current.Saturation, "BlackLevel=" + current.BlackLevel, "Warmth=" + current.Warmth });
    }

    void SetStartup(bool on)
    {
        try
        {
            if (on)
            {
                var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                dynamic lnk = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { StartupLink });
                lnk.TargetPath = Application.ExecutablePath; lnk.Arguments = "--tray"; lnk.WorkingDirectory = Dir; lnk.Save();
                SetStatus("Starts with Windows");
            }
            else { if (File.Exists(StartupLink)) File.Delete(StartupLink); SetStatus("Removed from startup"); }
        }
        catch (Exception ex) { SetStatus("Couldn't change startup: " + ex.Message); }
    }

    // Something on this system shows resizable windows maximized at first; open at the normal size instead
    Size startSize;
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (WindowState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Normal;
            Size = startSize;
            CenterToScreen();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.ModernWindow(Handle);
        Native.RegisterHotKey(Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000, (uint)Keys.F); // Ctrl+Alt+F, no repeat
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId) Toggle();
        base.WndProc(ref m);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Closing the window keeps the filter running in the tray; Exit from the tray menu really quits
        if (!reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; Hide(); ShowInTaskbar = false;
            tray.ShowBalloonTip(2000, AppName, "Still running in the tray. Right-click the icon to exit.", ToolTipIcon.Info);
            return;
        }
        Native.UnregisterHotKey(Handle, HotkeyId);
        tray.Visible = false;
        Filter.Shutdown();
        base.OnFormClosing(e);
    }
}

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    [STAThread]
    static void Main(string[] args)
    {
        bool created;
        using (new System.Threading.Mutex(true, "DisplayTuner.SingleInstance", out created))
        {
            if (!created) return;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Filter.Shutdown();
            Application.Run(new MainForm(Array.IndexOf(args, "--tray") >= 0));
        }
    }
}
