# CLAUDE.md

## Computer-use MCP server (`cu-mcp/`)

Lets Claude drive this Windows PC's real mouse and keyboard. It is one C# file built with the in-box
.NET Framework compiler, so it needs no installs. It speaks MCP over stdio.

| File | Purpose |
|---|---|
| `cu-mcp/cu-mcp.cs` | The whole server (C# 5 syntax only: the in-box `csc.exe` supports nothing newer) |
| `cu-mcp/build.cmd` | Builds `cu-mcp.exe` with `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` |
| `cu-mcp/tools/client.js` | Minimal MCP test client: `node client.js <exe> <outdir> <calls.json>`, where calls = `[["tool",{args}],…]`. Each run starts a new server, so `#id`s don't carry over between runs |
| `cu-mcp/tools/img.ps1` | System.Drawing helper: `sheet` (numbered contact sheet), `view` (10% grid overlay), `fix` (rotate+crop by %) |

### Registration
Registered at **user scope only** as `computer` (private to the user; never put it in a shared/project `.mcp.json`):
```
claude mcp add --scope user computer -- "<abs path>\cu-mcp\cu-mcp.exe"
```
To test a change without touching running sessions, drive a separately built exe through `tools/client.js`.
`build.cmd` moves a running (locked) `cu-mcp.exe` aside as `cu-mcp.old-N.exe`; running sessions keep the old code
until their server restarts (new session or `/mcp` reconnect). Old copies can be deleted once no server uses them.

### Tools
- `screenshot {region?:[x0,y0,x1,y1], display?}`: JPEG, long edge 1280 (~1.2k tokens). Its pixel grid is THE
  coordinate system for everything. `region` zooms in; coordinates stay full-screen.
- `ui {window?, find?, text?}`: UI Automation list of the foreground window's controls as text
  (`id type "name" ="value" x,y`, `*` focused, `[x]` toggled). Much cheaper than screenshots. Works in Firefox/Chrome
  web content, file dialogs and the taskbar (`window:"taskbar"`).
- `act {script}`: one step per line; stops at the first error. Targets: `X Y`, `#id` (from last ui), `@name` / `@"two words"`.
  `click/rclick/dclick/tclick/mclick [T] [mods]`, `move`, `drag T T`, `scroll [T] up|down N`, `type TEXT`,
  `paste TEXT`, `key ctrl+s tab*3 …`, `keydown/keyup`, `wait MS`, `set T TEXT`, `invoke T`, `focus TITLE`,
  `launch CMD`, `windows`, and end with `shot [region]` or `ui [filter]` to verify in the same call.
- Env tuning: `CU_MAX_EDGE`, `CU_QUALITY`, `CU_FORMAT=png`, `CU_TYPE_DELAY` (8ms), `CU_KEY_DELAY` (10ms),
  `CU_STEP_DELAY`, `CU_SETTLE`, `CU_MAX_ELEMENTS`, `CU_UI_TIMEOUT`.
- Failsafe: the user moving the mouse to the top-left corner aborts a script.
- Overlay: a status bar at the top center of the primary screen shows what Claude is doing (orange = acting, with
  the current step; grey = paused, with the last step). It is click-through, never takes focus, is excluded from
  screen capture (so screenshots never show it) and is hidden from `windows`/`focus`. Nothing shows until the first
  tool call; it hides after `CU_OVERLAY_HIDE` seconds idle (default 60). `CU_OVERLAY=0` turns overlay and tray off.
- Time-limited approval: screenshot/ui/act only run while an approval is active. Otherwise the call blocks on a
  dialog where the user picks how long Claude may work without asking again (5 min, **15 min (default,
  `CU_GRANT_MINUTES`)**, 30 min, 1 h, 2 h, or "until no session has used it for `CU_IDLE_MINUTES`" = the old idle
  behavior, default 30) and types a 4-digit code drawn as an image (excluded from capture, not exposed to UI
  Automation). A fixed length ends at a set clock time however busy Claude is; when it ends, a running `act` script
  stops at its next step and the next call asks again. Deny, Esc or 10 minutes without an answer fails the call.
  The approval is shared by all sessions (`%LOCALAPPDATA%\cu-mcp\grant`, plain text; `last-use` stamps the idle
  option); one dialog shows at a time and waiting sessions use its answer. `CU_APPROVE=0` turns approval off.
  Never try to approve it or edit the grant file yourself; ask the user in chat.
- The server is started with a hidden window, and Windows applies that to the first window the process shows
  normally: call `ShowWindow(h, SW_SHOW)` on any new normal window, or it stays invisible.
- Tray icon (appears on first use): shows how long access lasts ("Allowed until 14:35 (12 min left)") and
  "End Claude's access now"; left-click or "Show overlay" toggles the overlay (persisted via
  `%LOCALAPPDATA%\cu-mcp\overlay-off`); "Block Claude from using mouse and keyboard" makes `act` fail until unchecked.
  The overlay also shows the access line.
  Above the step line it shows Claude's own message, set by the `status {message}` tool, `act`'s optional `note`,
  or a `say TEXT` step. **Use these to narrate** (e.g. "Waiting for the research agent to finish"), so the user
  knows whether it is safe to use the PC.

### Lessons learned (follow these)
- **Start every script with `focus <window title>`** and re-focus before typing. The user works on the same desktop;
  focus once moved to the Claude window mid-script and keystrokes went into the user's chat.
  (TODO in server: abort typing if the foreground window changed.)
- Prefer `paste` over `type` for text. Win11 Notepad and other WinUI apps drop or reorder synthetic keystrokes;
  paste is exact. Note that paste overwrites the clipboard, and another app can briefly lock it ("clipboard busy").
- Prefer `@name` / `ui` over guessing coordinates. Web UIs move: e.g. buttons shift as content is added.
- Windows file dialogs reject forward slashes. Type a backslash path into the dialog (`key alt+n`, `paste C:\…`).
  When passing scripts through bash/JSON, write `\u005c` for a backslash, or generate JSON with Node.
- `windows` lists only top-level app windows, not owned dialogs (file pickers); use `ui` or `shot` to see those.
- Don't use the MCP for everything: for data, look for an API first (e.g. call a site's REST API from its devtools console).

