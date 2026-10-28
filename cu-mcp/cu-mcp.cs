// cu-mcp: computer-use MCP server (stdio) for Claude Code on Windows.
// One file, no packages: only .NET Framework 4.x, which ships with Windows 10/11.
// Build with build.cmd (uses the in-box csc.exe). C# 5 syntax on purpose.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;

class UserError : Exception { public UserError(string m) : base(m) { } }
class RpcError : Exception { public int Code; public RpcError(int c, string m) : base(m) { Code = c; } }

static class Cfg
{
    public static int MaxEdge = Env("CU_MAX_EDGE", 1280);     // screenshot long edge (tokens ~ w*h/784)
    public static int Quality = Env("CU_QUALITY", 75);        // JPEG quality
    public static bool Png = (Environment.GetEnvironmentVariable("CU_FORMAT") ?? "").ToLowerInvariant() == "png";
    public static int StepDelay = Env("CU_STEP_DELAY", 40);   // ms after every act step
    public static int Settle = Env("CU_SETTLE", 300);         // ms before shot/ui that follow input
    public static int TypeDelay = Env("CU_TYPE_DELAY", 8);    // ms between typed key events
    public static int KeyDelay = Env("CU_KEY_DELAY", 10);     // ms between events of a key combo
    public static int MaxElems = Env("CU_MAX_ELEMENTS", 150);
    public static int UiTimeout = Env("CU_UI_TIMEOUT", 8000);
    public static int OverlayHide = Env("CU_OVERLAY_HIDE", 60);  // seconds idle before the overlay hides
    public static bool Approve = Env("CU_APPROVE", 1) != 0;       // 0 = never ask the user before using the PC
    public static int GrantMinutes = Env("CU_GRANT_MINUTES", 15);  // length of an approval preselected in the dialog
    public static int IdleMinutes = Env("CU_IDLE_MINUTES", 30);    // the "until unused" option ends after this many minutes without use
    static int Env(string n, int d) { int v; return int.TryParse(Environment.GetEnvironmentVariable(n), out v) ? v : d; }
}

static class Program
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static StreamWriter Out;

    const string Instructions =
        "Controls this PC's real mouse and keyboard. Loop: look, then act. Look with `ui` (cheap text list of controls with ids) " +
        "and use `screenshot` only when visuals matter. In `act`, batch many steps and end with `shot` or `ui` to verify in the same call. " +
        "Prefer keyboard shortcuts, `launch`, `focus` and @name targets over hunting with coordinates. " +
        "The user can abort by moving the mouse to the top-left screen corner. " +
        "Access is time-limited: the user approves a length of time in a dialog, and when it ends the next call asks them again. " +
        "Never try to approve that dialog yourself; if a call is denied, ask the user in chat.";

    const string ShotDoc =
        "Screenshot of the current monitor (cursor drawn). Its pixel grid is the coordinate system for act. " +
        "Pass region to zoom in on small text; coordinates stay full-screenshot coordinates.";

    const string UiDoc =
        "List the foreground window's controls via UI Automation as text, far cheaper than a screenshot. " +
        "Line: id type \"name\" =\"value\" x,y (center, screenshot coords) then * focused, [x]/[ ] toggle, off disabled. " +
        "Use #id in act; ids reset on every listing.";

    const string ActDoc =
        "Run input steps on the real PC, one per line, in order; stops at the first error. " +
        "Target T = `X Y` (screenshot pixels), `#N` (id from ui) or `@name` (control found by name in the foreground window, e.g. @Save or @\"Save as\").\n" +
        "click [T] [mods] (also rclick dclick tclick mclick; mods e.g. ctrl or ctrl+shift)\n" +
        "move T | drag T T | mousedown [T] | mouseup [T]\n" +
        "scroll [T] up|down|left|right [N=3]\n" +
        "type TEXT (real keystrokes, rest of line, literal unicode; no Enter, use key enter)\n" +
        "paste TEXT (via clipboard; instant and exact, best for long text or editors that drop keys)\n" +
        "key K... (sequence of combos, optional *repeat: ctrl+s alt+f4 win+r tab*3 enter esc f5 pgdn up a ?)\n" +
        "keydown K | keyup K | wait MS\n" +
        "set T TEXT (set field value directly) | invoke T (press control via UIA, no mouse)\n" +
        "focus TITLE (activate window by title or process substring) | launch CMD [ARGS] (app, file or URL; waits for its window) | windows (list)\n" +
        "shot [X0 Y0 X1 Y1] | ui [FILTER] (append screenshot or control list; put last to verify)\n" +
        "say TEXT (show a message to the user on the on-screen overlay)";

    const string StatusDoc =
        "Show a message to the user on the always-on-top overlay, e.g. what you are about to do or what you are waiting for " +
        "(\"Waiting for the research agent to finish\"). It stays until replaced. Use it whenever the user might wonder what is happening.";

    [STAThread]
    static int Main(string[] args)
    {
        Native.MakeDpiAware();
        Native.timeBeginPeriod(1); // accurate short sleeps between keystrokes
        try { Disp.Select(0); } catch (Exception e) { Console.Error.WriteLine("display: " + e.Message); }
        Overlay.Start();
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        Out = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        Out.NewLine = "\n";
        Out.AutoFlush = true;
        string line;
        while ((line = input.ReadLine()) != null)
        {
            if (line.Trim().Length == 0) continue;
            Dictionary<string, object> msg;
            try { msg = Json.Deserialize<Dictionary<string, object>>(line); }
            catch { Send(Err(null, -32700, "parse error")); continue; }
            object id;
            msg.TryGetValue("id", out id);
            string method = Str(msg, "method");
            if (method == null || id == null) continue; // responses and notifications need no reply
            try { Send(Obj("jsonrpc", "2.0", "id", id, "result", Handle(method, Dict(msg, "params")))); }
            catch (RpcError e) { Send(Err(id, e.Code, e.Message)); }
            catch (Exception e) { Send(Err(id, -32603, Msg(e))); }
        }
        return 0;
    }

    static object Handle(string method, Dictionary<string, object> p)
    {
        switch (method)
        {
            case "initialize":
                return Obj("protocolVersion", Str(p, "protocolVersion") ?? "2025-06-18",
                    "capabilities", Obj("tools", Obj()),
                    "serverInfo", Obj("name", "computer", "version", "1.0.0"),
                    "instructions", Instructions);
            case "ping": return Obj();
            case "tools/list": return Obj("tools", Tools());
            case "tools/call": return CallTool(Str(p, "name"), Dict(p, "arguments"));
            default: throw new RpcError(-32601, "method not found: " + method);
        }
    }

    static object[] Tools()
    {
        return new object[] {
            Obj("name", "screenshot", "description", ShotDoc,
                "inputSchema", Obj("type", "object", "properties", Obj(
                    "region", Obj("type", "array", "items", Obj("type", "number"), "description", "[x0,y0,x1,y1] area to zoom into"),
                    "display", Obj("type", "integer", "description", "monitor index, sticky; 0 = primary"))),
                "annotations", Obj("readOnlyHint", true)),
            Obj("name", "ui", "description", UiDoc,
                "inputSchema", Obj("type", "object", "properties", Obj(
                    "window", Obj("type", "string", "description", "title/process substring or 'taskbar' instead of the foreground window"),
                    "find", Obj("type", "string", "description", "only controls whose name or value contains this"),
                    "text", Obj("type", "boolean", "description", "also list static text labels"))),
                "annotations", Obj("readOnlyHint", true)),
            Obj("name", "act", "description", ActDoc,
                "inputSchema", Obj("type", "object", "properties", Obj(
                    "script", Obj("type", "string", "description", "steps, one per line"),
                    "note", Obj("type", "string", "description", "optional: one line telling the user what this script is for, shown on the on-screen overlay")),
                    "required", new[] { "script" })),
            Obj("name", "status", "description", StatusDoc,
                "inputSchema", Obj("type", "object", "properties", Obj(
                    "message", Obj("type", "string", "description", "short plain sentence; empty clears it")),
                    "required", new[] { "message" }),
                "annotations", Obj("readOnlyHint", true)),
        };
    }

    static Dictionary<string, object> CallTool(string name, Dictionary<string, object> a)
    {
        var content = new List<object>();
        bool ok = true;
        if (name != "status") Overlay.Busy(name == "act" ? "starting actions" : name == "ui" ? "reading the window's controls" : "taking a screenshot");
        try
        {
            if (name != "status")
            {
                if (name == "act" && Overlay.Blocked) throw new UserError("the user blocked mouse and keyboard control from the tray icon; ask them to unblock it (right-click the tray icon) before trying again");
                string what = name == "act" ? (Str(a, "note") ?? Clean((Str(a, "script") ?? "").Trim().Split('\n')[0], 80))
                    : name == "ui" ? "read the controls of a window" : "take a screenshot";
                Approval.Check(what);
            }
            switch (name)
            {
                case "screenshot":
                    if (a.ContainsKey("display") && a["display"] != null) Disp.Select(Convert.ToInt32(a["display"]));
                    content.AddRange(Screenshot.Take(Nums(a.ContainsKey("region") ? a["region"] : null)));
                    break;
                case "ui":
                    content.Add(Text(Ui.List(Str(a, "window"), Str(a, "find"), a.ContainsKey("text") && a["text"] is bool && (bool)a["text"])));
                    break;
                case "status":
                    Overlay.Say(Str(a, "message"));
                    content.Add(Text("shown"));
                    break;
                case "act":
                    if (!string.IsNullOrEmpty(Str(a, "note"))) Overlay.Say(Str(a, "note"));
                    ok = Act.Run(Str(a, "script") ?? "", content);
                    break;
                default: throw new UserError("unknown tool: " + name);
            }
        }
        catch (Exception e) { ok = false; content.Add(Text(Msg(e))); }
        finally { Overlay.Idle(); if (name != "status") Approval.Touch(); }
        return Obj("content", content, "isError", !ok);
    }

    // ---- helpers ----
    static void Send(object o) { Out.WriteLine(Json.Serialize(o)); }
    static Dictionary<string, object> Err(object id, int code, string m) { return Obj("jsonrpc", "2.0", "id", id, "error", Obj("code", code, "message", m)); }
    public static Dictionary<string, object> Obj(params object[] kv)
    {
        var d = new Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
        return d;
    }
    public static Dictionary<string, object> Text(string s) { return Obj("type", "text", "text", s); }
    static string Str(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null; }
    static Dictionary<string, object> Dict(Dictionary<string, object> d, string k) { object v; return d != null && d.TryGetValue(k, out v) && v is Dictionary<string, object> ? (Dictionary<string, object>)v : new Dictionary<string, object>(); }
    static double[] Nums(object o)
    {
        var e = o as IEnumerable;
        if (o == null || o is string || e == null) return null;
        return e.Cast<object>().Select(x => Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray();
    }
    public static string Msg(Exception e)
    {
        while ((e is System.Reflection.TargetInvocationException || e is AggregateException) && e.InnerException != null) e = e.InnerException;
        return e is UserError ? e.Message : e.GetType().Name + ": " + e.Message;
    }
    public static string Clean(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Replace("\"", "'").Trim();
        return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
    }
}

// Coordinate space: the downscaled screenshot of the selected monitor.
static class Disp
{
    public static Rectangle B;
    public static double S = 1;
    public static int Index;

    public static List<Rectangle> All()
    {
        var list = new List<KeyValuePair<Rectangle, bool>>();
        Native.MonitorEnumProc cb = (IntPtr h, IntPtr dc, ref Native.RECT r, IntPtr d) =>
        {
            var mi = new Native.MONITORINFO();
            mi.cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (Native.GetMonitorInfo(h, ref mi)) list.Add(new KeyValuePair<Rectangle, bool>(mi.rcMonitor.ToRect(), (mi.dwFlags & 1) != 0));
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return list.OrderBy(p => p.Value ? 0 : 1).ThenBy(p => p.Key.X).ThenBy(p => p.Key.Y).Select(p => p.Key).ToList();
    }

    public static void Select(int i)
    {
        var all = All();
        if (all.Count == 0) throw new UserError("no display found");
        if (i < 0 || i >= all.Count) throw new UserError("display " + i + " not found; have " + all.Count);
        Index = i;
        B = all[i];
        S = Math.Min(1.0, (double)Cfg.MaxEdge / Math.Max(B.Width, B.Height));
    }

    public static void Refresh() { try { Select(Index); } catch (UserError) { Select(0); } }
    public static int W { get { return (int)Math.Round(B.Width * S); } }
    public static int H { get { return (int)Math.Round(B.Height * S); } }

    public static Point ToPhys(double x, double y)
    {
        if (x < 0 || y < 0 || x > W || y > H) throw new UserError("point " + x + "," + y + " is outside the " + W + "x" + H + " screen");
        int px = (int)Math.Round((x + 0.5) / S - 0.5), py = (int)Math.Round((y + 0.5) / S - 0.5);
        return new Point(B.X + Math.Max(0, Math.Min(B.Width - 1, px)), B.Y + Math.Max(0, Math.Min(B.Height - 1, py)));
    }

    public static string Fmt(Point p) { return (int)Math.Round((p.X - B.X) * S) + "," + (int)Math.Round((p.Y - B.Y) * S); }
}

static class Screenshot
{
    static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    public static List<object> Take(double[] reg)
    {
        Disp.Refresh();
        Rectangle src;
        int ow, oh;
        string hdr;
        if (reg == null)
        {
            src = Disp.B; ow = Disp.W; oh = Disp.H;
            hdr = "screen " + ow + "x" + oh;
        }
        else
        {
            if (reg.Length != 4) throw new UserError("region needs [x0,y0,x1,y1]");
            double x0 = Clamp(Math.Min(reg[0], reg[2]), Disp.W), y0 = Clamp(Math.Min(reg[1], reg[3]), Disp.H);
            double x1 = Clamp(Math.Max(reg[0], reg[2]), Disp.W), y1 = Clamp(Math.Max(reg[1], reg[3]), Disp.H);
            Point a = Disp.ToPhys(x0, y0), b = Disp.ToPhys(x1, y1);
            src = Rectangle.FromLTRB(a.X, a.Y, Math.Max(a.X + 8, b.X), Math.Max(a.Y + 8, b.Y));
            double k = Math.Min(4.0, (double)Cfg.MaxEdge / Math.Max(src.Width, src.Height));
            ow = Math.Max(1, (int)(src.Width * k)); oh = Math.Max(1, (int)(src.Height * k));
            hdr = "zoom " + (int)x0 + "," + (int)y0 + "-" + (int)x1 + "," + (int)y1 + " shown at " + ow + "x" + oh + " (coords stay " + Disp.W + "x" + Disp.H + " screen)";
        }

        string data;
        using (var cap = new Bitmap(src.Width, src.Height, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(cap)) g.CopyFromScreen(src.X, src.Y, 0, 0, src.Size, CopyPixelOperation.SourceCopy);
            DrawCursor(cap, src);
            if (ow == src.Width && oh == src.Height) data = Encode(cap);
            else using (var outb = new Bitmap(ow, oh, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(outb))
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetWrapMode(WrapMode.TileFlipXY); // no dark edge artifacts
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(cap, new Rectangle(0, 0, ow, oh), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
                    }
                    data = Encode(outb);
                }
        }

        Native.POINT c;
        Native.GetCursorPos(out c);
        var cp = new Point(c.X, c.Y);
        hdr += ", cursor " + (Disp.B.Contains(cp) ? Disp.Fmt(cp) : "on another display");
        hdr += ", fg \"" + Program.Clean(Win.Title(Native.GetForegroundWindow()), 60) + "\"";
        return new List<object> { Program.Text(hdr), Program.Obj("type", "image", "data", data, "mimeType", Cfg.Png ? "image/png" : "image/jpeg") };
    }

    static double Clamp(double v, int max) { return Math.Max(0, Math.Min(max, v)); }

    static string Encode(Bitmap b)
    {
        using (var ms = new MemoryStream())
        {
            if (Cfg.Png) b.Save(ms, ImageFormat.Png);
            else
            {
                var ep = new EncoderParameters(1);
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Cfg.Quality);
                b.Save(ms, Jpeg, ep);
            }
            return Convert.ToBase64String(ms.ToArray());
        }
    }

    static void DrawCursor(Bitmap bmp, Rectangle src)
    {
        var ci = new Native.CURSORINFO();
        ci.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || !src.Contains(ci.pt.X, ci.pt.Y)) return;
        Native.ICONINFO ii;
        int hx = 0, hy = 0;
        if (Native.GetIconInfo(ci.hCursor, out ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
        }
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            Native.DrawIconEx(hdc, ci.pt.X - src.X - hx, ci.pt.Y - src.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
            g.ReleaseHdc(hdc);
        }
    }
}

// Tokenizer for act arguments: whitespace separated, "quoted strings" and @"quoted names" allowed.
class Args
{
    readonly string s;
    int i;
    public Args(string s) { this.s = s ?? ""; }
    public bool End { get { return Peek(0) == null; } }
    public string Take()
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length) return null;
        string prefix = "";
        if (s[i] == '@' && i + 1 < s.Length && s[i + 1] == '"') { prefix = "@"; i++; }
        if (s[i] == '"')
        {
            int e = s.IndexOf('"', i + 1);
            if (e < 0) e = s.Length;
            string v = s.Substring(i + 1, e - i - 1);
            i = Math.Min(s.Length, e + 1);
            return prefix + v;
        }
        int st = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return s.Substring(st, i - st);
    }
    public string Peek(int k) { int save = i; string t = null; for (int j = 0; j <= k; j++) t = Take(); i = save; return t; }
    public string Rest() { if (i < s.Length && s[i] == ' ') i++; return s.Substring(Math.Min(i, s.Length)); }
}

class Tgt { public Point P; public El E; }

static class Act
{
    public static bool Run(string script, List<object> content)
    {
        var log = new StringBuilder();
        var extra = new List<object>();
        bool dirty = false;
        int n = 0;
        var lines = script.Replace("\r", "").Split('\n');
        int total = lines.Count(l => { var t = l.TrimStart(); return t.Length > 0 && !t.StartsWith("//"); });
        foreach (var line in lines)
        {
            string raw = line.TrimStart();
            if (raw.Length == 0 || raw.StartsWith("//")) continue;
            n++;
            try
            {
                Overlay.Busy("step " + n + "/" + total + ": " + Program.Clean(raw, 70));
                Failsafe();
                Approval.StillAllowed();
                Step(raw, log, extra, ref dirty);
                if (dirty && Cfg.StepDelay > 0) Thread.Sleep(Cfg.StepDelay);
            }
            catch (Exception e)
            {
                log.Append("step ").Append(n).Append(" `").Append(Program.Clean(raw, 60)).Append("` failed: ").Append(Program.Msg(e));
                content.Add(Program.Text(log.ToString()));
                content.AddRange(extra);
                return false;
            }
        }
        log.Append("ok ").Append(n).Append(n == 1 ? " step" : " steps")
           .Append("; fg \"").Append(Program.Clean(Win.Title(Native.GetForegroundWindow()), 60)).Append('"');
        content.Add(Program.Text(log.ToString()));
        content.AddRange(extra);
        return true;
    }

    // Abort when the mouse sits in the primary monitor's top-left corner and we did not put it there.
    static void Failsafe()
    {
        Native.POINT c;
        Native.GetCursorPos(out c);
        if (c.X <= 0 && c.Y <= 0 && !(Input.LastSet.X <= 0 && Input.LastSet.Y <= 0))
            throw new UserError("aborted by user (failsafe: mouse in top-left corner)");
    }

    static void Step(string raw, StringBuilder log, List<object> extra, ref bool dirty)
    {
        int sp = raw.IndexOf(' ');
        string cmd = (sp < 0 ? raw : raw.Substring(0, sp)).ToLowerInvariant();
        string rest = sp < 0 ? "" : raw.Substring(sp + 1);
        var a = new Args(rest);
        switch (cmd)
        {
            case "click": case "lclick": Click(a, 0, 1); break;
            case "rclick": Click(a, 1, 1); break;
            case "mclick": Click(a, 2, 1); break;
            case "dclick": Click(a, 0, 2); break;
            case "tclick": Click(a, 0, 3); break;
            case "move": case "hover": Input.Move(Target(a).P); break;
            case "drag": { var p1 = Target(a).P; var p2 = Target(a).P; Input.Drag(p1, p2); break; }
            case "mousedown": if (!a.End) Input.Move(Target(a).P); Input.Buttons(0, true, false); break;
            case "mouseup": if (!a.End) Input.Move(Target(a).P); Input.Buttons(0, false, true); break;
            case "scroll":
                {
                    Point? p = HasTarget(a) ? Target(a).P : (Point?)null;
                    string dir = (a.Take() ?? "down").ToLowerInvariant();
                    string amt = a.Take();
                    Input.Scroll(p, dir, amt == null ? 3 : (int)Num(amt));
                    break;
                }
            case "type": Input.Type(rest); break;
            case "paste": Input.Paste(rest); break;
            case "key":
                {
                    string k; int c = 0;
                    while ((k = a.Take()) != null) { Input.Combo(k); c++; }
                    if (c == 0) throw new UserError("key needs a key name");
                    break;
                }
            case "keydown": Input.KeyState(Need(a.Take(), "key name"), true); break;
            case "keyup": Input.KeyState(Need(a.Take(), "key name"), false); break;
            case "wait": { string t = a.Take(); Thread.Sleep(Math.Max(0, Math.Min(60000, t == null ? 500 : (int)Num(t)))); return; }
            case "set": { var t = Target(a); Ui.Set(t, a.Rest()); break; }
            case "invoke": Ui.Invoke(Target(a)); break;
            case "focus": Win.Focus(Win.Find(rest.Trim())); break;
            case "launch": log.Append(Win.Launch(rest.Trim())).Append('\n'); break;
            case "windows": log.Append(Win.List()); return;
            case "say": Overlay.Say(rest); return;
            case "shot":
                if (dirty) Thread.Sleep(Cfg.Settle);
                extra.AddRange(Screenshot.Take(a.End ? null : new[] { Num(a.Take()), Num(a.Take()), Num(a.Take()), Num(a.Take()) }));
                dirty = false;
                return;
            case "ui":
                if (dirty) Thread.Sleep(Cfg.Settle);
                log.Append(Ui.List(null, a.End ? null : rest.Trim(), false)).Append('\n');
                dirty = false;
                return;
            default: throw new UserError("unknown command '" + cmd + "'");
        }
        dirty = true;
    }

    static void Click(Args a, int btn, int count)
    {
        Point? p = HasTarget(a) ? Target(a).P : (Point?)null;
        Input.Click(p, btn, count, a.Take());
    }

    static string Need(string s, string what) { if (s == null) throw new UserError("missing " + what); return s; }

    static double Num(string s)
    {
        double d;
        if (s == null || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw new UserError("expected a number, got '" + s + "'");
        return d;
    }

    static bool IsNum(string s) { double d; return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d); }

    static bool HasTarget(Args a)
    {
        string t = a.Peek(0);
        return t != null && (t[0] == '#' || t[0] == '@' || (IsNum(t) && IsNum(a.Peek(1))));
    }

    static Tgt Target(Args a)
    {
        string t = a.Peek(0);
        if (t == null) throw new UserError("missing target (X Y, #id or @name)");
        if (t[0] == '#')
        {
            a.Take();
            int id;
            if (!int.TryParse(t.Substring(1), out id) || id < 1 || id > Ui.Last.Count) throw new UserError("unknown id " + t + " (ids come from the latest ui listing)");
            var e = Ui.Last[id - 1];
            return new Tgt { P = new Point(e.X, e.Y), E = e };
        }
        if (t[0] == '@')
        {
            a.Take();
            var e = Ui.FindByName(t.Substring(1));
            return new Tgt { P = new Point(e.X, e.Y), E = e };
        }
        if (IsNum(t) && IsNum(a.Peek(1)))
        {
            double x = Num(a.Take()), y = Num(a.Take());
            return new Tgt { P = Disp.ToPhys(x, y) };
        }
        throw new UserError("bad target '" + t + "' (X Y, #id or @name)");
    }
}

static class Input
{
    public static Point LastSet = new Point(-100, -100);
    const uint MOVE = 0x1, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000, WHEEL = 0x800, HWHEEL = 0x1000;
    const uint KEYUP = 0x2, UNICODE = 0x4, EXTENDED = 0x1;
    static readonly uint[] DownFlag = { 0x2, 0x8, 0x20 }, UpFlag = { 0x4, 0x10, 0x40 };
    static readonly HashSet<ushort> Ext = new HashSet<ushort> { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2C, 0x2D, 0x2E, 0x5B, 0x5C, 0x5D, 0x6F, 0x90, 0xA3, 0xA5 };

    static readonly Dictionary<string, ushort> Named = new Dictionary<string, ushort>
    {
        {"enter",0x0D},{"return",0x0D},{"tab",0x09},{"esc",0x1B},{"escape",0x1B},{"space",0x20},{"backspace",0x08},{"bs",0x08},
        {"delete",0x2E},{"del",0x2E},{"insert",0x2D},{"ins",0x2D},{"home",0x24},{"end",0x23},
        {"pageup",0x21},{"pgup",0x21},{"prior",0x21},{"pagedown",0x22},{"pgdn",0x22},{"next",0x22},
        {"up",0x26},{"down",0x28},{"left",0x25},{"right",0x27},
        {"ctrl",0x11},{"control",0x11},{"shift",0x10},{"alt",0x12},{"menu",0x12},{"altgr",0xA5},{"rctrl",0xA3},
        {"win",0x5B},{"super",0x5B},{"meta",0x5B},{"cmd",0x5B},{"lwin",0x5B},{"rwin",0x5C},{"apps",0x5D},{"contextmenu",0x5D},
        {"capslock",0x14},{"numlock",0x90},{"scrolllock",0x91},{"pause",0x13},{"printscreen",0x2C},{"prtsc",0x2C},{"print",0x2C},
        {"plus",0xBB},{"minus",0xBD},{"add",0x6B},{"subtract",0x6D},{"multiply",0x6A},{"divide",0x6F},
        {"volumeup",0xAF},{"volumedown",0xAE},{"mute",0xAD},{"playpause",0xB3},{"nexttrack",0xB0},{"prevtrack",0xB1},{"stop",0xB2},
        {"browserback",0xA6},{"browserforward",0xA7},{"browserrefresh",0xA8},
    };

    public static void Move(Point p)
    {
        var vs = Native.VirtualScreen();
        int dx = (int)Math.Round((p.X - vs.X) * 65535.0 / Math.Max(1, vs.Width - 1));
        int dy = (int)Math.Round((p.Y - vs.Y) * 65535.0 / Math.Max(1, vs.Height - 1));
        Send(Mouse(dx, dy, 0, MOVE | ABSOLUTE | VIRTUALDESK));
        Native.POINT c;
        Native.GetCursorPos(out c);
        if (c.X != p.X || c.Y != p.Y) Native.SetCursorPos(p.X, p.Y);
        LastSet = p;
    }

    public static void Buttons(int btn, bool down, bool up)
    {
        var l = new List<Native.INPUT>();
        if (down) l.Add(Mouse(0, 0, 0, DownFlag[btn]));
        if (up) l.Add(Mouse(0, 0, 0, UpFlag[btn]));
        Send(l.ToArray());
    }

    public static void Click(Point? p, int btn, int count, string mods)
    {
        var mv = string.IsNullOrEmpty(mods) ? new List<ushort>() : ParseCombo(mods);
        if (p.HasValue) { Move(p.Value); Thread.Sleep(20); }
        if (mv.Count > 0) { Paced(mv.Select(k => Key(k, false)).ToList(), Cfg.KeyDelay); Thread.Sleep(Cfg.KeyDelay); }
        for (int i = 0; i < count; i++)
        {
            Buttons(btn, true, true);
            if (i < count - 1) Thread.Sleep(40);
        }
        if (mv.Count > 0) { Thread.Sleep(Cfg.KeyDelay); Paced(Enumerable.Reverse(mv).Select(k => Key(k, true)).ToList(), Cfg.KeyDelay); }
    }

    public static void Drag(Point a, Point b)
    {
        Move(a);
        Thread.Sleep(30);
        Buttons(0, true, false);
        Thread.Sleep(60);
        for (int i = 1; i <= 12; i++)
        {
            Move(new Point(a.X + (b.X - a.X) * i / 12, a.Y + (b.Y - a.Y) * i / 12));
            Thread.Sleep(15);
        }
        Thread.Sleep(60);
        Buttons(0, false, true);
    }

    public static void Scroll(Point? p, string dir, int n)
    {
        if (p.HasValue) { Move(p.Value); Thread.Sleep(20); }
        bool h = dir == "left" || dir == "right";
        if (!h && dir != "up" && dir != "down") throw new UserError("scroll direction must be up, down, left or right");
        int delta = (dir == "up" || dir == "right") ? 120 : -120;
        for (int i = 0; i < Math.Max(1, Math.Min(n, 50)); i++)
        {
            Send(Mouse(0, 0, unchecked((uint)delta), h ? HWHEEL : WHEEL));
            Thread.Sleep(15);
        }
    }

    public static void Type(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            short vs = ch < 0xD800 ? Native.VkKeyScanW(ch) : (short)-1;
            var l = new List<Native.INPUT>();
            if (ch == '\t') { l.Add(Key(0x09, false)); l.Add(Key(0x09, true)); }
            else if (vs != -1 && (vs & 0x600) == 0)
            {
                // Real key strokes when the layout has the char without Ctrl/Alt, like a human typing.
                ushort vk = (ushort)(vs & 0xFF);
                bool shift = (vs & 0x100) != 0;
                if (shift) l.Add(Key(0x10, false));
                l.Add(Key(vk, false));
                l.Add(Key(vk, true));
                if (shift) l.Add(Key(0x10, true));
            }
            else if (char.IsHighSurrogate(ch) && i + 1 < text.Length)
            {
                char lo = text[++i]; // keep surrogate pairs together
                l.Add(Uni(ch, false)); l.Add(Uni(lo, false)); l.Add(Uni(ch, true)); l.Add(Uni(lo, true));
            }
            else { l.Add(Uni(ch, false)); l.Add(Uni(ch, true)); }
            // Pace every event: apps whose UI thread stalls (Win11 Notepad, WinUI) read live key state
            // when they catch up, so a burst loses chars and applies Shift to the wrong key.
            Paced(l, Cfg.TypeDelay);
            Thread.Sleep(Cfg.TypeDelay);
        }
    }

    // XAML/WinUI apps read key state when they process an event, so events sent in one burst
    // get mangled (ctrl+v arrives as "v"). A few ms between events fixes it.
    static void Paced(List<Native.INPUT> l, int gap)
    {
        if (gap <= 0) { Send(l.ToArray()); return; }
        for (int i = 0; i < l.Count; i++)
        {
            Send(l[i]);
            if (i < l.Count - 1) Thread.Sleep(gap);
        }
    }

    public static void Paste(string text)
    {
        if (text.Length == 0) return;
        for (int i = 0; ; i++)
        {
            try { System.Windows.Forms.Clipboard.SetText(text); break; }
            catch (ExternalException) { if (i >= 10) throw new UserError("clipboard busy"); Thread.Sleep(50); }
        }
        Combo("ctrl+v");
        Thread.Sleep(50);
    }

    public static void Combo(string spec)
    {
        int rep = 1;
        int star = spec.LastIndexOf('*');
        if (star > 0 && star < spec.Length - 1 && int.TryParse(spec.Substring(star + 1), out rep)) spec = spec.Substring(0, star);
        else rep = 1;
        var vks = ParseCombo(spec);
        for (int r = 0; r < Math.Max(1, Math.Min(rep, 100)); r++)
        {
            var l = new List<Native.INPUT>();
            foreach (var k in vks) l.Add(Key(k, false));
            for (int i = vks.Count - 1; i >= 0; i--) l.Add(Key(vks[i], true));
            Paced(l, Cfg.KeyDelay);
            if (rep > 1) Thread.Sleep(Cfg.KeyDelay);
        }
    }

    public static void KeyState(string name, bool down)
    {
        Paced(ParseCombo(name).Select(k => Key(k, !down)).ToList(), Cfg.KeyDelay);
    }

    public static void Tap(ushort vk) { Paced(new List<Native.INPUT> { Key(vk, false), Key(vk, true) }, Cfg.KeyDelay); }

    static List<ushort> ParseCombo(string spec)
    {
        var res = new List<ushort>();
        var parts = spec == "+" ? new[] { "+" } : spec.Split('+');
        foreach (var part in parts)
            foreach (var vk in VkFor(part.Length == 0 ? "plus" : part))
                if (!res.Contains(vk)) res.Add(vk);
        return res;
    }

    static List<ushort> VkFor(string name)
    {
        string n = name.ToLowerInvariant();
        ushort vk;
        if (Named.TryGetValue(n, out vk)) return new List<ushort> { vk };
        int f;
        if (n.Length > 1 && n[0] == 'f' && int.TryParse(n.Substring(1), out f) && f >= 1 && f <= 24) return new List<ushort> { (ushort)(0x6F + f) };
        if (name.Length == 1)
        {
            char c = name[0];
            if (c < 128 && char.IsLetterOrDigit(c)) return new List<ushort> { (ushort)char.ToUpperInvariant(c) };
            short s = Native.VkKeyScanW(c);
            if (s == -1) throw new UserError("no key for '" + name + "' on this keyboard layout; use type");
            var r = new List<ushort>();
            if ((s & 0x100) != 0) r.Add(0x10);
            if ((s & 0x200) != 0) r.Add(0x11);
            if ((s & 0x400) != 0) r.Add(0x12);
            r.Add((ushort)(s & 0xFF));
            return r;
        }
        throw new UserError("unknown key '" + name + "'");
    }

    static Native.INPUT Mouse(int dx, int dy, uint data, uint flags)
    {
        var i = new Native.INPUT { type = 0 };
        i.U.mi = new Native.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags };
        return i;
    }

    static Native.INPUT Key(ushort vk, bool up)
    {
        var i = new Native.INPUT { type = 1 };
        i.U.ki = new Native.KEYBDINPUT { wVk = vk, wScan = (ushort)Native.MapVirtualKey(vk, 0), dwFlags = (up ? KEYUP : 0) | (Ext.Contains(vk) ? EXTENDED : 0) };
        return i;
    }

    static Native.INPUT Uni(char c, bool up)
    {
        var i = new Native.INPUT { type = 1 };
        i.U.ki = new Native.KEYBDINPUT { wVk = 0, wScan = c, dwFlags = UNICODE | (up ? KEYUP : 0) };
        return i;
    }

    static void Send(params Native.INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        uint sent = Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT)));
        if (sent != inputs.Length)
            throw new UserError("input was blocked (error " + Marshal.GetLastWin32Error() + "): the target may be an elevated window, UAC prompt or locked screen");
    }
}

class El
{
    public AutomationElement A;
    public string Type, Name, Raw, Value;
    public int X, Y, Toggle = -1;
    public bool Enabled, Focus;
}

static class Ui
{
    public static List<El> Last = new List<El>();

    static readonly ControlType[] Interactive = {
        ControlType.Button, ControlType.Edit, ControlType.CheckBox, ControlType.RadioButton, ControlType.ComboBox,
        ControlType.ListItem, ControlType.MenuItem, ControlType.TabItem, ControlType.Hyperlink, ControlType.TreeItem,
        ControlType.Slider, ControlType.SplitButton, ControlType.DataItem, ControlType.Spinner, ControlType.Document,
        ControlType.HeaderItem };

    static string Abbr(ControlType t)
    {
        if (t == ControlType.Button) return "btn";
        if (t == ControlType.Edit) return "edit";
        if (t == ControlType.CheckBox) return "check";
        if (t == ControlType.RadioButton) return "radio";
        if (t == ControlType.ComboBox) return "combo";
        if (t == ControlType.ListItem) return "item";
        if (t == ControlType.MenuItem) return "menu";
        if (t == ControlType.TabItem) return "tab";
        if (t == ControlType.Hyperlink) return "link";
        if (t == ControlType.TreeItem) return "tree";
        if (t == ControlType.Slider) return "slider";
        if (t == ControlType.SplitButton) return "split";
        if (t == ControlType.DataItem) return "cell";
        if (t == ControlType.Spinner) return "spin";
        if (t == ControlType.Document) return "doc";
        if (t == ControlType.HeaderItem) return "header";
        if (t == ControlType.Text) return "text";
        return t.ProgrammaticName.Replace("ControlType.", "").ToLowerInvariant();
    }

    public static T Timed<T>(Func<T> f, int ms)
    {
        T r = default(T);
        Exception ex = null;
        var th = new Thread(() => { try { r = f(); } catch (Exception e) { ex = e; } });
        th.IsBackground = true;
        th.SetApartmentState(ApartmentState.MTA);
        th.Start();
        if (!th.Join(ms)) throw new UserError("UI Automation timed out after " + ms + "ms (app busy or huge); use screenshot instead");
        if (ex != null) throw ex;
        return r;
    }

    static List<El> Collect(IntPtr h, bool withText)
    {
        if (h == IntPtr.Zero) throw new UserError("no window");
        var types = Interactive.ToList();
        if (withText) types.Add(ControlType.Text);
        Condition cond = new AndCondition(
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false),
            new OrCondition(types.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray()));
        var cr = new CacheRequest();
        cr.Add(AutomationElement.NameProperty);
        cr.Add(AutomationElement.ControlTypeProperty);
        cr.Add(AutomationElement.BoundingRectangleProperty);
        cr.Add(AutomationElement.IsEnabledProperty);
        cr.Add(AutomationElement.HasKeyboardFocusProperty);
        cr.Add(ValuePattern.ValueProperty);
        cr.Add(TogglePattern.ToggleStateProperty);
        Native.RECT wr;
        Native.GetWindowRect(h, out wr);
        var win = wr.ToRect();
        AutomationElementCollection found;
        using (cr.Activate()) found = AutomationElement.FromHandle(h).FindAll(TreeScope.Descendants, cond);
        var res = new List<El>();
        var seen = new HashSet<string>();
        foreach (AutomationElement e in found)
        {
            try
            {
                var r = e.Cached.BoundingRectangle;
                if (r.IsEmpty || r.Width < 1 || r.Height < 1) continue;
                int cx = (int)(r.X + r.Width / 2), cy = (int)(r.Y + r.Height / 2);
                if (!win.Contains(cx, cy)) continue;
                var el = new El { A = e, Type = Abbr(e.Cached.ControlType), Raw = e.Cached.Name ?? "", X = cx, Y = cy, Enabled = e.Cached.IsEnabled, Focus = e.Cached.HasKeyboardFocus };
                el.Name = Program.Clean(el.Raw, 50);
                if (el.Type == "text" && el.Name.Length == 0) continue;
                var v = e.GetCachedPropertyValue(ValuePattern.ValueProperty, true) as string;
                if (!string.IsNullOrEmpty(v) && v != el.Raw) el.Value = Program.Clean(v, 40);
                object t = e.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true);
                if (t is ToggleState) el.Toggle = (int)(ToggleState)t;
                if (!seen.Add(el.Type + "|" + el.Raw + "|" + cx + "," + cy)) continue;
                res.Add(el);
            }
            catch (ElementNotAvailableException) { }
        }
        return res;
    }

    public static string List(string winQ, string find, bool withText)
    {
        IntPtr h = Win.Resolve(winQ);
        var els = Timed(() => Collect(h, withText), Cfg.UiTimeout);
        if (!string.IsNullOrEmpty(find))
            els = els.Where(e => Has(e.Raw, find) || Has(e.Value, find)).ToList();
        Last = els.Take(Cfg.MaxElems).ToList();
        var sb = new StringBuilder();
        sb.Append("window \"").Append(Program.Clean(Win.Title(h), 60)).Append("\" (").Append(Win.Proc(h)).Append("), ").Append(els.Count).Append(" controls");
        for (int i = 0; i < Last.Count; i++)
        {
            var e = Last[i];
            sb.Append('\n').Append(i + 1).Append(' ').Append(e.Type).Append(" \"").Append(e.Name).Append('"');
            if (e.Value != null) sb.Append(" =\"").Append(e.Value).Append('"');
            sb.Append(' ').Append(Disp.Fmt(new Point(e.X, e.Y)));
            if (e.Focus) sb.Append(" *");
            if (e.Toggle == 1) sb.Append(" [x]"); else if (e.Toggle == 0) sb.Append(" [ ]");
            if (!e.Enabled) sb.Append(" off");
        }
        if (els.Count > Last.Count) sb.Append("\n+").Append(els.Count - Last.Count).Append(" more; narrow with find");
        return sb.ToString();
    }

    static bool Has(string s, string q) { return s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0; }

    public static El FindByName(string q)
    {
        IntPtr h = Native.GetForegroundWindow();
        var els = Timed(() => Collect(h, true), Cfg.UiTimeout);
        El best = null;
        int bestScore = 0;
        foreach (var e in els)
        {
            int s;
            if (e.Raw.Equals(q, StringComparison.OrdinalIgnoreCase)) s = 30;
            else if (e.Raw.StartsWith(q, StringComparison.OrdinalIgnoreCase)) s = 20;
            else if (Has(e.Raw, q)) s = 10;
            else continue;
            if (e.Enabled) s += 2;
            if (e.Type != "text") s += 3;
            if (s > bestScore) { best = e; bestScore = s; }
        }
        if (best == null) throw new UserError("no control named '" + q + "' in \"" + Program.Clean(Win.Title(h), 40) + "\" (try ui)");
        return best;
    }

    public static void Set(Tgt t, string text)
    {
        bool done = t.E != null && Timed(() =>
        {
            object p;
            if (!t.E.A.TryGetCurrentPattern(ValuePattern.Pattern, out p) || ((ValuePattern)p).Current.IsReadOnly) return false;
            try { ((ValuePattern)p).SetValue(text); return true; } catch (InvalidOperationException) { return false; }
        }, 5000);
        if (done) return;
        Input.Click(t.P, 0, 1, null);
        Thread.Sleep(50);
        Input.Combo("ctrl+a");
        Input.Type(text);
    }

    public static void Invoke(Tgt t)
    {
        bool done = t.E != null && Timed(() =>
        {
            var a = t.E.A;
            object p;
            if (a.TryGetCurrentPattern(InvokePattern.Pattern, out p)) { ((InvokePattern)p).Invoke(); return true; }
            if (a.TryGetCurrentPattern(TogglePattern.Pattern, out p)) { ((TogglePattern)p).Toggle(); return true; }
            if (a.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p)) { ((SelectionItemPattern)p).Select(); return true; }
            if (a.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out p))
            {
                var ec = (ExpandCollapsePattern)p;
                if (ec.Current.ExpandCollapseState == ExpandCollapseState.Collapsed) ec.Expand(); else ec.Collapse();
                return true;
            }
            return false;
        }, 5000);
        if (!done) Input.Click(t.P, 0, 1, null);
    }
}

static class Win
{
    public static string Title(IntPtr h)
    {
        if (h == IntPtr.Zero) return "";
        int n = Native.GetWindowTextLength(h);
        var sb = new StringBuilder(n + 1);
        Native.GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Proc(IntPtr h)
    {
        uint pid;
        Native.GetWindowThreadProcessId(h, out pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
    }

    // Top-level windows as Alt+Tab shows them.
    public static List<IntPtr> Top()
    {
        var res = new List<IntPtr>();
        Native.EnumWindowsProc cb = (h, l) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindowTextLength(h) == 0) return true;
            int ex = Native.GetWindowLong(h, -20);
            bool tool = (ex & 0x80) != 0, app = (ex & 0x40000) != 0;
            if (tool && !app) return true;
            if (Native.GetWindow(h, 4) != IntPtr.Zero && !app) return true; // owned popup
            int cloaked;
            if (Native.DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
            res.Add(h);
            return true;
        };
        Native.EnumWindows(cb, IntPtr.Zero);
        GC.KeepAlive(cb);
        return res;
    }

    public static IntPtr Find(string q)
    {
        if (string.IsNullOrEmpty(q)) throw new UserError("missing window title");
        var wins = Top();
        var ci = StringComparison.OrdinalIgnoreCase;
        IntPtr h = wins.FirstOrDefault(w => Title(w).Equals(q, ci));
        if (h == IntPtr.Zero) h = wins.FirstOrDefault(w => Title(w).IndexOf(q, ci) >= 0);
        if (h == IntPtr.Zero) h = wins.FirstOrDefault(w => Proc(w).Equals(q, ci));
        if (h == IntPtr.Zero) h = wins.FirstOrDefault(w => Proc(w).IndexOf(q, ci) >= 0);
        if (h == IntPtr.Zero)
            throw new UserError("no window matching '" + q + "'; open: " + string.Join(", ", wins.Take(10).Select(w => "\"" + Program.Clean(Title(w), 30) + "\"")));
        return h;
    }

    public static IntPtr Resolve(string q)
    {
        if (string.IsNullOrEmpty(q)) return Native.GetForegroundWindow();
        if (q.Equals("taskbar", StringComparison.OrdinalIgnoreCase)) return Native.FindWindow("Shell_TrayWnd", null);
        return Find(q);
    }

    public static void Focus(IntPtr h)
    {
        if (Native.IsIconic(h)) Native.ShowWindow(h, 9);
        for (int attempt = 0; attempt < 3 && Native.GetForegroundWindow() != h; attempt++)
        {
            uint dummy;
            uint ft = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out dummy), me = Native.GetCurrentThreadId();
            bool att = ft != 0 && ft != me && Native.AttachThreadInput(me, ft, true);
            if (attempt > 0) Input.Tap(0x12); // an Alt tap lifts the foreground lock
            Native.BringWindowToTop(h);
            Native.SetForegroundWindow(h);
            if (att) Native.AttachThreadInput(me, ft, false);
            Thread.Sleep(80);
        }
        if (Native.GetForegroundWindow() != h) throw new UserError("could not focus \"" + Program.Clean(Title(h), 40) + "\"");
    }

    public static string Launch(string cmd)
    {
        if (cmd.Length == 0) throw new UserError("launch needs a command");
        var a = new Args(cmd);
        string file = a.Take(), args = a.Rest();
        IntPtr before = Native.GetForegroundWindow();
        var known = new HashSet<IntPtr>(Top());
        var psi = new ProcessStartInfo(file, args) { UseShellExecute = true };
        try { Process.Start(psi); }
        catch (Win32Exception e) { throw new UserError("launch failed: " + e.Message); }
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 6000)
        {
            Thread.Sleep(100);
            IntPtr fg = Native.GetForegroundWindow();
            if (fg != before && fg != IntPtr.Zero && Title(fg).Length > 0) break;
            if (sw.ElapsedMilliseconds > 800)
            {
                IntPtr nw = Top().FirstOrDefault(w => !known.Contains(w));
                if (nw != IntPtr.Zero) { try { Focus(nw); } catch (UserError) { } break; }
            }
        }
        Thread.Sleep(Cfg.Settle);
        return Native.GetForegroundWindow() != before ? "launched " + file : "launched " + file + " (no new window yet)";
    }

    public static string List()
    {
        var sb = new StringBuilder();
        IntPtr fg = Native.GetForegroundWindow();
        foreach (var h in Top())
            sb.Append(h == fg ? "* \"" : "  \"").Append(Program.Clean(Title(h), 60)).Append("\" (").Append(Proc(h)).Append(')')
              .Append(Native.IsIconic(h) ? " min" : "").Append('\n');
        return sb.ToString();
    }
}

// Always-on-top status bar at the top of the primary screen, so the user can see what Claude is doing.
// Click-through, never takes focus, and excluded from screen capture so it does not show up in screenshots.
// Nothing is visible until Claude first uses the PC; then a tray icon appears (overlay on/off, block control)
// and the bar hides itself after CU_OVERLAY_HIDE seconds (default 60) without activity.
// Runs its own message loop on a background STA thread. CU_OVERLAY=0 turns overlay and tray icon off.
static class Overlay
{
    static OverlayForm F;
    static System.Windows.Forms.NotifyIcon Tray;
    static string LastStep = "";
    static string Message = "";
    static bool IsBusy, Used;
    // Settings are flag files shared by every server process (one per Claude session), so the single tray icon
    // controls all of them.
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cu-mcp");
    static readonly string OffFlag = Path.Combine(Dir, "overlay-off");
    static readonly string BlockFlag = Path.Combine(Dir, "blocked");
    public static bool Enabled { get { return !File.Exists(OffFlag); } }
    public static bool Blocked { get { return File.Exists(BlockFlag); } }
    // Only the process holding this mutex shows the tray icon; the others retry, so one takes over when it exits.
    static Mutex TrayLock;
    static bool OwnsTray;

    static void SetFlag(string path, bool on, string text)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (on) File.WriteAllText(path, text); else File.Delete(path);
        }
        catch (Exception) { }
    }

    // Claude's own narration ("waiting for agent X to finish"), shown above the step line until replaced.
    public static void Say(string msg)
    {
        Message = Program.Clean(msg ?? "", 300);
        if (IsBusy) Busy(LastStep); else Idle();
    }

    public static void Start()
    {
        if (Environment.GetEnvironmentVariable("CU_OVERLAY") == "0") return;
        var ready = new ManualResetEvent(false);
        var t = new Thread(() =>
        {
            try
            {
                F = new OverlayForm();
                var h = F.Handle; // create the window without showing it, so BeginInvoke works
                ready.Set();
                System.Windows.Forms.Application.Run();
            }
            catch (Exception e) { Console.Error.WriteLine("overlay: " + e.Message); ready.Set(); }
        });
        t.IsBackground = true;
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.WaitOne(3000);
    }

    public static void Busy(string step)
    {
        LastStep = step;
        IsBusy = true;
        Post(true, Lead() + "●  Claude is using this PC  —  " + step + "     (to stop: move the mouse to the top-left corner, or use the tray icon)" + Access());
    }

    public static void Idle()
    {
        IsBusy = false;
        Post(false, Lead() + "Claude computer use: paused" + (LastStep.Length > 0 ? "  ·  last: " + LastStep + " at " + DateTime.Now.ToString("HH:mm:ss") : "") + Access());
    }

    static string Lead() { return Message.Length > 0 ? "\U0001F4AC  " + Message + "\n" : ""; }
    static string Access() { var s = Approval.Summary(); return s == null ? "" : "\n" + s; }

    static void Post(bool busy, string text)
    {
        var f = F;
        if (f == null || !f.IsHandleCreated) return;
        try
        {
            f.BeginInvoke((Action)(() =>
            {
                if (!Used) { Used = true; StartTrayElection(); }
                f.SetState(busy, text, Enabled);
            }));
        }
        catch (Exception) { }
    }

    // Runs on the overlay thread. Tries to become the one process that shows the tray icon, now and every 3 s.
    static void StartTrayElection()
    {
        try { TrayLock = new Mutex(false, @"Local\cu-mcp-tray"); } catch (Exception) { return; }
        TryOwnTray();
        var timer = new System.Windows.Forms.Timer { Interval = 3000 };
        timer.Tick += (s, e) => { if (!OwnsTray) TryOwnTray(); else RefreshTray(); };
        timer.Start();
    }

    static void TryOwnTray()
    {
        bool got;
        try { got = TrayLock.WaitOne(0); }
        catch (AbandonedMutexException) { got = true; } // the previous owner exited without releasing it
        if (!got) return;
        OwnsTray = true;
        MakeTray();
    }

    static System.Windows.Forms.ToolStripMenuItem ShowItem, BlockItem, CountItem, AccessItem, EndItem;

    static void MakeTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        CountItem = new System.Windows.Forms.ToolStripMenuItem("") { Enabled = false };
        AccessItem = new System.Windows.Forms.ToolStripMenuItem("") { Enabled = false };
        EndItem = new System.Windows.Forms.ToolStripMenuItem("End Claude's access now");
        EndItem.Click += (s, e) =>
        {
            try { Approval.End(); }
            catch (Exception ex) { System.Windows.Forms.MessageBox.Show("Could not end Claude's access: " + ex.Message + "\n\nUse \"Block Claude\" or \"Quit computer use\" instead.", "Claude computer use"); }
            if (!IsBusy) Idle();
            RefreshTray();
        };
        ShowItem = new System.Windows.Forms.ToolStripMenuItem("Show overlay");
        ShowItem.Click += (s, e) => { SetFlag(OffFlag, Enabled, "overlay turned off from the tray icon"); F.SetState(IsBusy, null, Enabled); RefreshTray(); };
        BlockItem = new System.Windows.Forms.ToolStripMenuItem("Block Claude from using mouse and keyboard");
        BlockItem.Click += (s, e) => { SetFlag(BlockFlag, !Blocked, "blocked from the tray icon"); RefreshTray(); };
        var quit = new System.Windows.Forms.ToolStripMenuItem("Quit computer use (close all servers)");
        quit.Click += (s, e) => QuitAll();
        menu.Items.Add(CountItem);
        menu.Items.Add(AccessItem);
        menu.Items.Add(EndItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(ShowItem);
        menu.Items.Add(BlockItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(quit);
        menu.Opening += (s, e) => RefreshTray();
        Tray = new System.Windows.Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true };
        Tray.MouseClick += (s, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowItem.PerformClick(); };
        RefreshTray();
        System.Windows.Forms.Application.ApplicationExit += (s, e) => Tray.Visible = false;
        AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { Tray.Visible = false; } catch (Exception) { } };
    }

    static Process[] Servers() { return Process.GetProcessesByName(Process.GetCurrentProcess().ProcessName); }

    static void RefreshTray()
    {
        if (Tray == null) return;
        bool blocked = Blocked;
        ShowItem.Checked = Enabled;
        BlockItem.Checked = blocked;
        int n = Servers().Length;
        CountItem.Text = n + (n == 1 ? " Claude session" : " Claude sessions") + " can use this PC";
        string access = Approval.Summary();
        AccessItem.Visible = EndItem.Visible = access != null;
        AccessItem.Text = access ?? "";
        EndItem.Enabled = Approval.IsAllowed();
        Tray.Text = blocked ? "Claude computer use: BLOCKED" : "Claude computer use (" + n + ")" + (Approval.IsAllowed() ? "" : ", needs approval");
        var icon = blocked ? SystemIcons.Error : SystemIcons.Information;
        if (Tray.Icon != icon) Tray.Icon = icon;
    }

    static void QuitAll()
    {
        var others = Servers().Where(p => p.Id != Process.GetCurrentProcess().Id).ToList();
        var answer = System.Windows.Forms.MessageBox.Show(
            "Close all " + (others.Count + 1) + " computer-use servers?\n\nClaude sessions lose mouse, keyboard and screenshot access until they restart their 'computer' server (a new session, or /mcp reconnect).",
            "Quit Claude computer use", System.Windows.Forms.MessageBoxButtons.OKCancel, System.Windows.Forms.MessageBoxIcon.Warning);
        if (answer != System.Windows.Forms.DialogResult.OK) return;
        foreach (var p in others) { try { p.Kill(); } catch (Exception) { } }
        Tray.Visible = false;
        Environment.Exit(0);
    }
}

class OverlayForm : System.Windows.Forms.Form
{
    readonly System.Windows.Forms.Label L;

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    public OverlayForm()
    {
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        Opacity = 0.88;
        L = new System.Windows.Forms.Label
        {
            AutoSize = true,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Padding = new System.Windows.Forms.Padding(12, 5, 12, 5),
        };
        Controls.Add(L);
        // Re-assert topmost now and then; other topmost windows can cover it.
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (s, e) =>
        {
            if (!Visible) return;
            if (!Busy && (DateTime.Now - LastChange).TotalSeconds > HideAfter) { Hide(); return; }
            SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // HWND_TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
        };
        timer.Start();
    }

    bool Busy;
    DateTime LastChange = DateTime.Now;
    static readonly int HideAfter = Cfg.OverlayHide;

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override System.Windows.Forms.CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x8 | 0x20 | 0x80 | 0x80000 | 0x08000000; // TOPMOST, TRANSPARENT (click-through), TOOLWINDOW, LAYERED, NOACTIVATE
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+)
    }

    // text == null keeps the current text (used when the tray toggles the overlay).
    public void SetState(bool busy, string text, bool enabled)
    {
        if (text != null) L.Text = text;
        Busy = busy;
        LastChange = DateTime.Now;
        if (!enabled) { if (Visible) Hide(); return; }
        BackColor = busy ? Color.FromArgb(200, 70, 20) : Color.FromArgb(45, 45, 48);
        var wa = System.Windows.Forms.Screen.PrimaryScreen.Bounds;
        L.MaximumSize = Size.Empty;
        var sz = L.PreferredSize;
        int w = Math.Min(sz.Width, wa.Width * 2 / 3); // long messages wrap onto more lines
        L.MaximumSize = new Size(w, 0);
        Size = new Size(w, L.PreferredSize.Height);
        Location = new Point(wa.X + (wa.Width - w) / 2, wa.Y);
        if (!Visible) base.Show();
    }
}

// Claude may use the PC only while an approval from the user runs. In a dialog the user picks how long: a fixed time
// (CU_GRANT_MINUTES preselected, default 15) that ends at a set clock time however busy Claude is, or "until unused",
// which ends once no session has used the PC for CU_IDLE_MINUTES. When it ends, the next screenshot/ui/act asks
// again and a running act script stops. The tray icon shows the time left and can end it early.
// The approval is a file shared by every server process (one per Claude session), so one approval covers sessions
// running side by side, and ending it from the tray ends it for all of them.
// To approve, the user types a code drawn as an image: it is excluded from screen capture and not exposed to
// UI Automation, so no Claude session can read it and approve on its own.
static class Approval
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cu-mcp");
    static readonly string GrantFile = Path.Combine(Dir, "grant");
    static readonly string Stamp = Path.Combine(Dir, "last-use");
    static bool Granted;

    // Mode "time" ends at Until, "idle" ends IdleMin minutes after the last use, "ended" was ended by the user at At.
    class Grant { public string Mode = ""; public DateTime At, Until; public int IdleMin; }

    public static void Check(string what)
    {
        Granted = false;
        string why;
        if (Cfg.Approve && !Allowed(out why))
        {
            // One dialog at a time: sessions that ask meanwhile wait here, then go by the user's answer.
            using (var m = new Mutex(false, @"Local\cu-mcp-approval"))
            {
                bool got;
                try { got = m.WaitOne(TimeSpan.FromMinutes(11)); } catch (AbandonedMutexException) { got = true; }
                if (!got) throw new UserError("another session's approval dialog is still open; ask the user in chat");
                try
                {
                    if (!Allowed(out why))
                    {
                        Overlay.Say("Waiting for you to approve computer use (see the dialog)");
                        var g = Ask(what, why);
                        Overlay.Say("");
                        if (g == null) throw new UserError("the user did not approve computer use in the dialog (denied or timed out). Do not retry on your own; ask the user in chat.");
                        Save(g);
                    }
                }
                finally { m.ReleaseMutex(); }
            }
        }
        Granted = true;
        Touch();
    }

    // Stops a running act script once the approved time is over or the user ended it from the tray.
    public static void StillAllowed()
    {
        string why;
        if (Cfg.Approve && !Allowed(out why))
            throw new UserError("stopped: the time the user allowed for computer use is over. " + why + " The next call asks them again; first tell them in chat why you need more time.");
    }

    public static bool IsAllowed() { string why; return !Cfg.Approve || Allowed(out why); }

    // For the overlay and tray, e.g. "Allowed until 14:35 (12 min left)"; null when approval is turned off.
    public static string Summary()
    {
        if (!Cfg.Approve) return null;
        var g = Load();
        DateTime ends;
        string why;
        if (!Allowed(g, out ends, out why)) return "Not allowed: Claude must ask you first";
        if (g.Mode == "idle") return "Allowed until unused for " + g.IdleMin + " min (now until " + Clock(ends) + ")";
        return "Allowed until " + Clock(ends) + " (" + Math.Max(1, (int)Math.Ceiling((ends - DateTime.UtcNow).TotalMinutes)) + " min left)";
    }

    // Ends the approval for every session; their next call asks again.
    public static void End() { Save(new Grant { Mode = "ended", At = DateTime.UtcNow }); }

    // Records use, but only after a call that was allowed to run. "Until unused" approvals count from it.
    public static void Touch()
    {
        if (!Granted) return;
        try
        {
            Directory.CreateDirectory(Dir);
            if (!File.Exists(Stamp)) File.WriteAllText(Stamp, "last time Claude used the PC");
            File.SetLastWriteTime(Stamp, DateTime.Now);
        }
        catch (Exception) { }
    }

    static bool Allowed(out string why) { DateTime ends; return Allowed(Load(), out ends, out why); }

    // why: one sentence for the dialog saying why the user is asked (again).
    static bool Allowed(Grant g, out DateTime ends, out string why)
    {
        DateTime now = DateTime.UtcNow;
        ends = DateTime.MinValue;
        why = null;
        if (g != null && g.Mode == "time")
        {
            ends = g.Until;
            if (now < ends) return true;
            why = "The " + Length((int)Math.Round((g.Until - g.At).TotalMinutes)) + " you allowed at " + Clock(g.At) + " ended at " + Clock(g.Until) + ".";
            return false;
        }
        if (g != null && g.Mode == "idle")
        {
            DateTime last = File.Exists(Stamp) ? File.GetLastWriteTimeUtc(Stamp) : g.At;
            if (last < g.At) last = g.At;
            ends = last.AddMinutes(g.IdleMin);
            if (now < ends) return true;
            why = "No Claude session has used this PC for " + (int)(now - last).TotalMinutes + " minutes.";
            return false;
        }
        why = g != null && g.Mode == "ended" ? "You ended its access at " + Clock(g.At) + "." : "You have not approved it yet.";
        return false;
    }

    static Grant Load()
    {
        for (int i = 0; ; i++)
        {
            try
            {
                if (!File.Exists(GrantFile)) return null;
                var g = new Grant();
                foreach (var line in File.ReadAllLines(GrantFile))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    if (k == "mode") g.Mode = v;
                    else if (k == "at") g.At = Utc(v);
                    else if (k == "until") g.Until = Utc(v);
                    else if (k == "idle") int.TryParse(v, out g.IdleMin);
                }
                return g;
            }
            catch (IOException) { if (i >= 5) return null; Thread.Sleep(20); } // another process is writing it
            catch (Exception) { return null; }
        }
    }

    static void Save(Grant g)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(GrantFile, "mode=" + g.Mode + "\nat=" + g.At.ToString("o", CultureInfo.InvariantCulture) +
            (g.Mode == "time" ? "\nuntil=" + g.Until.ToString("o", CultureInfo.InvariantCulture) : "") +
            (g.Mode == "idle" ? "\nidle=" + g.IdleMin : "") + "\n");
    }

    static DateTime Utc(string s) { return DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime(); }
    public static string Clock(DateTime utc) { return utc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture); }
    public static string Length(int min) { return min >= 60 && min % 60 == 0 ? (min / 60) + (min == 60 ? " hour" : " hours") : min + (min == 1 ? " minute" : " minutes"); }

    static Grant Ask(string what, string why)
    {
        Grant result = null;
        var t = new Thread(() =>
        {
            using (var f = new ApprovalForm(what, why))
            {
                System.Windows.Forms.Application.Run(f);
                if (!f.Approved) return;
                var now = DateTime.UtcNow; // the time runs from the moment the user approves
                result = f.Minutes > 0 ? new Grant { Mode = "time", At = now, Until = now.AddMinutes(f.Minutes) }
                                       : new Grant { Mode = "idle", At = now, IdleMin = Cfg.IdleMinutes };
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result;
    }
}

class ApprovalForm : System.Windows.Forms.Form
{
    public bool Approved;
    public int Minutes; // how long the user allows; 0 = until no session has used the PC for CU_IDLE_MINUTES
    readonly string Code = new Random().Next(1000, 10000).ToString(CultureInfo.InvariantCulture);
    readonly System.Windows.Forms.TextBox Box;
    readonly System.Windows.Forms.Button Allow;
    readonly System.Windows.Forms.Label Ends;

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    public ApprovalForm(string what, string why)
    {
        Text = "Allow Claude to use this computer?";
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        TopMost = true;
        StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;

        var msg = new System.Windows.Forms.Label
        {
            Location = new Point(20, 16), Size = new Size(480, 96),
            Text = "Claude wants to take control of your mouse and keyboard.\n" + why + "\n\nIt wants to: " + what,
        };
        var ask = new System.Windows.Forms.Label { Location = new Point(20, 116), AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "For how long may it work without asking you again?" };
        Controls.AddRange(new System.Windows.Forms.Control[] { msg, ask });
        Box = new System.Windows.Forms.TextBox { MaxLength = 4, Width = 120, Font = new Font("Consolas", 16f) };
        Ends = new System.Windows.Forms.Label { AutoSize = true, ForeColor = Color.Gray };

        // Fixed lengths first; the last choice is the old idle-based behavior, never the default unless configured.
        var choices = new List<int> { 5, 15, 30, 60, 120 };
        if (Cfg.GrantMinutes > 0 && !choices.Contains(Cfg.GrantMinutes)) { choices.Add(Cfg.GrantMinutes); choices.Sort(); }
        choices.Add(0);
        int y = 142;
        foreach (int min in choices)
        {
            int m = min;
            var rb = new System.Windows.Forms.RadioButton
            {
                Location = new Point(32, y), AutoSize = true,
                Text = m > 0 ? Approval.Length(m) : "Until no Claude session has used it for " + Cfg.IdleMinutes + " minutes (can last for hours)",
            };
            rb.CheckedChanged += (s, e) => { if (!rb.Checked) return; Minutes = m; ShowEnd(); Box.Focus(); };
            Controls.Add(rb);
            if (m == Math.Max(0, Cfg.GrantMinutes)) rb.Checked = true;
            y += 26;
        }
        Ends.Location = new Point(32, y + 4);

        var hint = new System.Windows.Forms.Label { Location = new Point(20, y + 52), AutoSize = true, Text = "To allow it, type this code:" };
        var codeImg = new System.Windows.Forms.Panel { Location = new Point(20, y + 78), Size = new Size(160, 52), BackColor = Color.FromArgb(240, 240, 240) };
        codeImg.Paint += (s, e) =>
        {
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using (var big = new Font("Consolas", 26f, FontStyle.Bold)) e.Graphics.DrawString(Code, big, Brushes.Black, 14, 4);
        };
        Box.Location = new Point(200, y + 90);
        Allow = new System.Windows.Forms.Button { Text = "Allow", Location = new Point(300, y + 154), Size = new Size(96, 36), Enabled = false };
        var deny = new System.Windows.Forms.Button { Text = "Deny", Location = new Point(404, y + 154), Size = new Size(96, 36) };
        var left = new System.Windows.Forms.Label { Location = new Point(20, y + 162), AutoSize = true, ForeColor = Color.Gray };
        ClientSize = new Size(520, y + 208);
        Box.TextChanged += (s, e) => Allow.Enabled = Box.Text == Code;
        Allow.Click += (s, e) => { if (Box.Text == Code) { Approved = true; Close(); } };
        deny.Click += (s, e) => Close();
        AcceptButton = Allow; // Enter approves, but only once the code is right (the button is disabled until then)
        CancelButton = deny;  // Esc denies
        Controls.AddRange(new System.Windows.Forms.Control[] { Ends, hint, codeImg, Box, Allow, deny, left });

        // Deny automatically if nobody answers within 10 minutes.
        var end = DateTime.Now.AddMinutes(10);
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (s, e) =>
        {
            var rest = end - DateTime.Now;
            if (rest <= TimeSpan.Zero) { Close(); return; }
            left.Text = "Denies itself in " + (int)rest.TotalMinutes + ":" + rest.Seconds.ToString("00", CultureInfo.InvariantCulture);
            ShowEnd(); // the end time moves with the clock until the user approves
        };
        timer.Start();
        // The server is started with a hidden window, and Windows applies that to the first window the process shows
        // normally, so show it again explicitly.
        // Windows' foreground lock stops a background process from simply activating its window, so use Win.Focus
        // (attach to the foreground thread, then an Alt tap if needed).
        Shown += (s, e) =>
        {
            ShowWindow(Handle, 5);
            try { Win.Focus(Handle); } catch (Exception) { }
            Activate();
            Box.Focus();
            System.Media.SystemSounds.Exclamation.Play();
        };
    }

    void ShowEnd()
    {
        Ends.Text = (Minutes > 0 ? "Ends at " + Approval.Clock(DateTime.UtcNow.AddMinutes(Minutes)) + ", even if Claude is still working."
                                 : "Ends once the PC has not been used by Claude for " + Cfg.IdleMinutes + " minutes.") +
                    "\nYou can end it sooner from the tray icon.";
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, 0x11); // WDA_EXCLUDEFROMCAPTURE: no session can screenshot the code
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int L, T, R, B;
        public Rectangle ToRect() { return Rectangle.FromLTRB(L, T, R, B); }
    }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [StructLayout(LayoutKind.Sequential)] public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)] public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public InputUnion U; }

    public delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);
    public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr h, out ICONINFO ii);
    [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern short VkKeyScanW(char c);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr h, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int v);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    public static Rectangle VirtualScreen() { return new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79)); }

    // Physical pixels everywhere, so screenshots, UIA rectangles and SendInput agree on high-DPI screens.
    public static void MakeDpiAware()
    {
        try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch (EntryPointNotFoundException) { }
        try { if (SetProcessDpiAwareness(2) == 0) return; } catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        try { SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
    }
}
