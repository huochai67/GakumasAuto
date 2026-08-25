// GakumasAuto v2 — unified in-process automation plugin for gakumas (BepInEx 6 / Il2CppInterop).
// Merges the former GakumasUI (UI observation/interaction + command channel) and
// GakumasAuto (ADV automation) plugins, and adds structured responses for MCP consumers.
//
// IL2CPP safety rule (hard limit): MonoBehaviour methods MUST NOT expose managed types
// (DTOs, StringBuilder, List<T>) in their signatures — Il2CppInterop substitutes
// unsupported signatures and calls break. All such state is routed through static
// fields on the plugin class instead.
//
// Hotkeys:
//   F8  — toggle ADV auto-advance (skip to next message, auto-select choices)
//   F9  — toggle ADV fast-forward
//   F10 — capture current screen layout to log
// Passive watchers (1Hz): login user, loading state, maintenance info, top screen-layer changes.
//
// Command channel: write JSON to  <BepInEx>\gakumas-ui-cmd.json  — executed within 1s,
// response written to  <BepInEx>\gakumas-ui-resp.json  and the command file renamed to *.done.json.
//   { "id": "...", "action": "layout" }                    -> resp.result = { root, count, nodes:[...] }
//   { "id": "...", "action": "state" }                     -> resp.result = { userId, name, topLayer, layersActive, loading, maintenance }
//   { "id": "...", "action": "screenshot" }                -> resp.result = { file, w, h } or { file, pending:true }
//   { "id": "...", "action": "tap_at", "x": 0, "y": 0 }    -> screen-coord pointer down/up/click
//   { "id": "...", "action": "click", "path": "<name>" }   -> button OnClicked() (gesture-gated)
//   { "id": "...", "action": "tap", "path": "<name>" }     -> full pointer down/up/click sequence
//   { "id": "...", "action": "invoke_callback", "path": "<name>" } -> fire the wired click callback directly
//   { "id": "...", "action": "debug_button", "path": "<name>" }    -> button wiring diagnostics
//   { "id": "...", "action": "find", "path": "<substring>" }       -> matching nodes of current screen
//   { "id": "...", "action": "adv_end_wait" }                      -> ADVTimelineManager.EndWait(true)
//   { "id": "...", "action": "adv_set_ff", "value": true|false }   -> ToggleFastForward(value)
//   { "id": "...", "action": "adv_select_unselected" }             -> BranchManager.SelectUnselectedChoices()
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Campus.ADV;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GakumasAuto
{
    [BepInPlugin("dev.gakumas.auto", "Gakumas Auto", "2.0.2")]
    public class GakumasAutoPlugin : BasePlugin
    {
        internal static ManualLogSource SharedLog;

        // Static routing for DTO-shaped command results (see IL2CPP safety rule above).
        internal static LayoutRespDto SharedLayoutResp;
        internal static StateDto SharedState;
        internal static ScreenshotDto SharedScreenshot;
        internal static FindRespDto SharedFindResp;
        internal static List<LayoutNodeDto> SharedNodes;
        internal static List<FindMatchDto> SharedFindMatches;

        public override void Load()
        {
            SharedLog = Log;
            Log.LogInfo("GakumasAuto v2 loaded. F8/F9 = ADV, F10 = layout capture");
            Log.LogInfo("Command channel: <BepInEx>\\gakumas-ui-cmd.json (layout|state|screenshot|tap_at|click|tap|invoke_callback|debug_button|find|adv_*)");

            try
            {
                var driver = AddComponent<AutoDriver>();
                driver.Attach();
                Log.LogInfo("AutoDriver component attached");
            }
            catch (Exception e)
            {
                Log.LogError($"failed to attach AutoDriver: {e}");
            }
        }
    }

    // ---------------- response DTOs (plain data, only touched via statics) ----------------

    internal class LayoutNodeDto
    {
        public int id { get; set; }
        public string path { get; set; }
        public string name { get; set; }
        public int sx { get; set; }      // screen coords (pixels)
        public int sy { get; set; }
        public float w { get; set; }     // rect size (canvas units)
        public float h { get; set; }
        public bool active { get; set; }
        public List<string> flags { get; set; }
        public string text { get; set; }
    }

    internal class LayoutRespDto
    {
        public string root { get; set; }
        public int count { get; set; }
        public List<LayoutNodeDto> nodes { get; set; }
    }

    internal class StateDto
    {
        public string userId { get; set; }
        public string name { get; set; }
        public string topLayer { get; set; }
        public bool layersActive { get; set; }
        public bool loading { get; set; }
        public string maintenance { get; set; }
    }

    internal class ScreenshotDto
    {
        public string file { get; set; }
        public int w { get; set; }
        public int h { get; set; }
        public bool pending { get; set; }
    }

    internal class FindMatchDto
    {
        public string path { get; set; }
        public string name { get; set; }
        public bool active { get; set; }
        public string text { get; set; }
    }

    internal class FindRespDto
    {
        public int count { get; set; }
        public List<FindMatchDto> matches { get; set; }
    }

    // ---------------- driver component ----------------

    public class AutoDriver : MonoBehaviour
    {
        private const int MaxLayoutNodes = 500;
        private const int MaxLayoutDepth = 12;
        private const int MaxFindMatches = 300;

        private static readonly string CmdPath = Path.Combine(Paths.BepInExRootPath, "gakumas-ui-cmd.json");
        private static readonly string RespPath = Path.Combine(Paths.BepInExRootPath, "gakumas-ui-resp.json");
        private static readonly string ScreenPath = Path.Combine(Paths.BepInExRootPath, "gakumas-screen.png");

        private bool _autoAdvance;
        private bool _fastForward;
        private float _nextTick;
        private float _nextHeartbeat;
        private float _nextWatch;
        private int _lastEngineCount = -1;
        private string _lastServerUserId = "";
        private bool _lastLoading;
        private bool _watchStarted;
        private string _lastTopLayer = "";
        private DateTime _lastCmdWrite = DateTime.MinValue;
        private readonly StringBuilder _sb = new StringBuilder();
        private int _walkCount;

        public void Attach()
        {
            _nextTick = Time.realtimeSinceStartup + 1f;
            _nextHeartbeat = Time.realtimeSinceStartup + 30f;
            _nextWatch = Time.realtimeSinceStartup + 5f;
        }

        public void Update()
        {
            var log = GakumasAutoPlugin.SharedLog;
            if (log == null) return;

            if (Input.GetKeyDown(KeyCode.F8))
            {
                _autoAdvance = !_autoAdvance;
                log.LogInfo($"auto-advance {(_autoAdvance ? "ON" : "OFF")}");
            }
            if (Input.GetKeyDown(KeyCode.F9))
            {
                _fastForward = !_fastForward;
                log.LogInfo($"fast-forward {(_fastForward ? "ON" : "OFF")}");
            }
            if (Input.GetKeyDown(KeyCode.F10))
            {
                log.LogInfo("F10: manual layout capture");
                CaptureLayoutToLog();
            }

            if (Time.realtimeSinceStartup >= _nextHeartbeat)
            {
                log.LogInfo("heartbeat: AutoDriver alive");
                _nextHeartbeat = Time.realtimeSinceStartup + 30f;
            }

            if (Time.realtimeSinceStartup >= _nextWatch)
            {
                _nextWatch = Time.realtimeSinceStartup + 1f;
                try
                {
                    WatchState();
                    WatchScreen();
                    ProcessCommands();
                }
                catch (Exception e) { log.LogWarning($"tick failed: {e.Message}"); }
            }

            if (!_autoAdvance && !_fastForward) return;
            if (Time.realtimeSinceStartup < _nextTick) return;
            _nextTick = Time.realtimeSinceStartup + 0.2f;

            try
            {
                var engines = UnityEngine.Object.FindObjectsOfType<ADVEngine>();
                int count = engines == null ? 0 : engines.Length;
                if (count != _lastEngineCount)
                {
                    log.LogInfo($"ADVEngine instances: {count}");
                    _lastEngineCount = count;
                }
                if (count == 0) return;

                var engine = engines[0];
                var timeline = engine.Timeline;
                if (timeline == null) return;

                if (timeline.IsChoiceLoop && engine.Branch != null && engine.Branch.ChoiceCount > 0)
                {
                    engine.Branch.SelectUnselectedChoices();
                    log.LogInfo("choice: auto-selected unselected choices");
                }
                else if (timeline.IsWaiting && _autoAdvance)
                {
                    timeline.EndWait(true);
                    log.LogInfo("advance: EndWait(skip=true)");
                }

                if (_fastForward && !timeline.IsFastForward)
                {
                    timeline.ToggleFastForward(true);
                    log.LogInfo("fast-forward engaged");
                }
            }
            catch (Exception e)
            {
                log.LogWarning($"ADV tick failed: {e.Message}");
            }
        }

        // ---------------- passive watchers ----------------

        private void WatchState()
        {
            var log = GakumasAutoPlugin.SharedLog;

            string serverUserId = "";
            string userName = "";
            try
            {
                var u = Campus.Common.User.UserDataManager.User;
                if (u != null)
                {
                    serverUserId = u.ServerUserId ?? "";
                    if (serverUserId.Length > 0)
                    {
                        var prof = Campus.Common.User.UserDataManager.UserProfile;
                        userName = prof != null ? (prof.Name ?? "") : "";
                    }
                }
            }
            catch { }

            if (serverUserId != _lastServerUserId)
            {
                _lastServerUserId = serverUserId;
                if (serverUserId.Length > 0)
                {
                    log.LogInfo($"LOGIN: ServerUserId={serverUserId}" +
                                (userName.Length > 0 ? $" Name={userName}" : ""));
                }
                else if (_watchStarted)
                {
                    log.LogInfo("logout/not-logged-in (ServerUserId empty)");
                }
            }

            bool loading = false;
            try
            {
                var lm = Campus.Common.LoadingManager.Instance;
                loading = lm != null && Campus.Common.LoadingManager.IsActive;
            }
            catch { }
            if (loading != _lastLoading || !_watchStarted)
            {
                _lastLoading = loading;
                log.LogInfo($"LOADING: {(loading ? "active" : "idle")}");
            }

            if (!_watchStarted)
            {
                try
                {
                    var mi = Campus.Common.MaintenanceInfoHolder.Instance.MaintenanceInfo;
                    log.LogInfo($"MAINTENANCE: {(mi == null ? "(null/not-fetched)" : mi.ToString())}");
                }
                catch { }
            }

            _watchStarted = true;
        }

        private void WatchScreen()
        {
            var log = GakumasAutoPlugin.SharedLog;
            string topLayer = "";
            try
            {
                var slm = Campus.Common.ScreenLayerManager.Instance;
                if (slm != null && slm.HasActive)
                {
                    var top = slm.GetTopLayer();
                    if (top != null)
                    {
                        var t = top.GetType();
                        topLayer = t != null ? t.Name : "?";
                    }
                }
            }
            catch { }

            if (topLayer != _lastTopLayer)
            {
                _lastTopLayer = topLayer;
                log.LogInfo($"SCREEN: top layer = {(topLayer.Length > 0 ? topLayer : "(none)")}");
                if (topLayer.Length > 0)
                    CaptureLayoutToLog();
            }
        }

        // ---------------- layout capture ----------------

        private void CaptureLayoutToLog()
        {
            var log = GakumasAutoPlugin.SharedLog;
            try
            {
                _sb.Clear();
                _sb.AppendLine("LAYOUT BEGIN");
                _walkCount = 0;
                GakumasAutoPlugin.SharedNodes = null;

                Transform layerRoot = GetLayerRoot();
                if (layerRoot != null)
                {
                    Walk(layerRoot, 0, "");
                }
                else
                {
                    var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                    _sb.AppendLine($"canvases: {(canvases == null ? 0 : canvases.Length)}");
                    if (canvases != null)
                    {
                        for (int i = 0; i < canvases.Length && _walkCount < MaxLayoutNodes; i++)
                        {
                            var c = canvases[i];
                            _sb.AppendLine($"[canvas {i}] {c.name} sortingOrder={c.sortingOrder} enabled={c.enabled}");
                            Walk(c.transform, 1, "");
                        }
                    }
                }
                _sb.AppendLine($"LAYOUT END ({_walkCount} nodes)");
                log.LogInfo(_sb.ToString());
            }
            catch (Exception e)
            {
                log.LogWarning($"layout capture failed: {e.Message}");
            }
        }

        private void CaptureLayoutJson()
        {
            var nodes = new List<LayoutNodeDto>();
            GakumasAutoPlugin.SharedNodes = nodes;
            _sb.Clear();
            _walkCount = 0;
            string rootName = "";

            Transform layerRoot = GetLayerRoot();
            if (layerRoot != null)
            {
                rootName = layerRoot.name;
                Walk(layerRoot, 0, "");
            }
            else
            {
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                if (canvases != null)
                {
                    for (int i = 0; i < canvases.Length && _walkCount < MaxLayoutNodes; i++)
                    {
                        var c = canvases[i];
                        if (rootName.Length == 0) rootName = $"[canvas] {c.name}";
                        Walk(c.transform, 1, "");
                    }
                }
            }

            GakumasAutoPlugin.SharedNodes = null;
            GakumasAutoPlugin.SharedLayoutResp = new LayoutRespDto { root = rootName, count = _walkCount, nodes = nodes };
        }

        private Transform GetLayerRoot()
        {
            var sb = _sb;
            try
            {
                var slm = Campus.Common.ScreenLayerManager.Instance;
                if (slm != null && slm.HasActive)
                {
                    var top = slm.GetTopLayer();
                    if (top != null)
                    {
                        var root = top.GetParent();
                        sb.AppendLine($"screen-layer root: {root.name}");
                        return root;
                    }
                }
            }
            catch (Exception e) { sb.AppendLine($"screen-layer read failed: {e.Message}"); }
            return null;
        }

        private void Walk(Transform t, int depth, string parentPath)
        {
            if (t == null || _walkCount >= MaxLayoutNodes || depth > MaxLayoutDepth) return;
            _walkCount++;

            var go = t.gameObject;
            string name = t.name ?? "?";
            if (name.Length > 48) name = name.Substring(0, 48);
            string path = parentPath.Length == 0 ? name : parentPath + "/" + name;

            string rect = "";
            int sx = 0, sy = 0;
            float rw = 0, rh = 0;
            var rt = t.GetComponent<RectTransform>();
            if (rt != null)
            {
                rect = $" pos=({rt.anchoredPosition.x:F0},{rt.anchoredPosition.y:F0}) size=({rt.rect.width:F0}x{rt.rect.height:F0})";
                rw = rt.rect.width; rh = rt.rect.height;
                if (rw <= 0f)
                {
                    try
                    {
                        var sd = rt.sizeDelta;
                        rw = sd.x; rh = sd.y;
                        rect += $" (sizeDelta {rw:F0}x{rh:F0})";
                    }
                    catch { }
                }
                try
                {
                    Camera cam = null;
                    var canvas = go.GetComponentInParent<Canvas>();
                    if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                        cam = canvas.worldCamera;
                    var sp = RectTransformUtility.WorldToScreenPoint(cam, rt.position);
                    sx = (int)sp.x; sy = (int)sp.y;
                }
                catch { }
            }

            string flags = "";
            if (go != null)
            {
                try { if (!go.activeInHierarchy) flags += " [inactive]"; } catch { }
                try { if (go.GetComponent<Campus.Common.CampusButton>() != null) flags += " [CampusButton]"; } catch { }
                try
                {
                    var ub = go.GetComponent<Button>();
                    if (ub != null)
                    {
                        flags += " [UIButton]";
                        try { if (!ub.interactable) flags += " [not-interactable]"; } catch { }
                    }
                }
                catch { }
            }
            string text = "";
            try
            {
                var txt = go != null ? go.GetComponent<TMPro.TMP_Text>() : null;
                if (txt != null)
                {
                    text = txt.text ?? "";
                    if (text.Length > 40) text = text.Substring(0, 40);
                    flags += $" [TMP:\"{text}\"]";
                }
            }
            catch { }
            try
            {
                var utxt = go != null ? go.GetComponent<UnityEngine.UI.Text>() : null;
                if (utxt != null)
                {
                    text = utxt.text ?? "";
                    if (text.Length > 40) text = text.Substring(0, 40);
                    flags += $" [Text:\"{text}\"]";
                }
            }
            catch { }

            _sb.AppendLine($"{new string(' ', depth * 2)}{name}{rect}{flags}");

            var nodes = GakumasAutoPlugin.SharedNodes;
            if (nodes != null)
            {
                try
                {
                    var dto = new LayoutNodeDto
                    {
                        id = _walkCount,
                        path = path,
                        name = name,
                        sx = sx,
                        sy = sy,
                        w = rw,
                        h = rh,
                        active = go == null || go.activeInHierarchy,
                        flags = flags.Trim().Length > 0
                            ? new List<string>(flags.Trim().Trim('[', ']').Split(new[] { "] [" }, StringSplitOptions.RemoveEmptyEntries))
                            : new List<string>(),
                        text = text
                    };
                    nodes.Add(dto);
                }
                catch (Exception e)
                {
                    GakumasAutoPlugin.SharedLog.LogWarning($"layout node {_walkCount} ({name}) DTO failed: {e.Message}");
                }
            }

            int children = 0;
            try { children = t.childCount; } catch { }
            for (int i = 0; i < children; i++)
            {
                Transform child = null;
                try { child = t.GetChild(i); } catch { }
                if (child != null) Walk(child, depth + 1, path);
            }
        }

        // ---------------- command channel ----------------

        private void ProcessCommands()
        {
            var log = GakumasAutoPlugin.SharedLog;
            if (!File.Exists(CmdPath)) return;

            DateTime wt;
            try { wt = File.GetLastWriteTimeUtc(CmdPath); }
            catch { return; }
            if (wt == _lastCmdWrite) return;
            _lastCmdWrite = wt;

            object result = "";
            bool ok = true;
            string id = "";
            try
            {
                string json = ReadCmdFile();
                if (json == null)
                {
                    ok = false;
                    result = "cmd read failed: file locked";
                }
                else
                {
                    using (var doc = JsonDocument.Parse(json))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("id", out var idEl)) id = idEl.GetString() ?? "";
                        var action = root.GetProperty("action").GetString() ?? "";
                        string path = root.TryGetProperty("path", out var pEl) ? (pEl.GetString() ?? "") : "";

                        log.LogInfo($"UI-CMD: {action} {(path.Length > 0 ? "path=" + path : "")}");

                        switch (action)
                        {
                            case "layout":
                                CaptureLayoutJson();
                                result = GakumasAutoPlugin.SharedLayoutResp;
                                break;
                            case "state":
                                BuildState();
                                result = GakumasAutoPlugin.SharedState;
                                break;
                            case "screenshot":
                                CaptureScreenshot();
                                result = GakumasAutoPlugin.SharedScreenshot;
                                break;
                            case "tap_at":
                                float tx = root.TryGetProperty("x", out var xEl) ? xEl.GetSingle() : -1f;
                                float ty = root.TryGetProperty("y", out var yEl) ? yEl.GetSingle() : -1f;
                                result = TapAt(tx, ty);
                                break;
                            case "click":
                                result = ClickByPath(path);
                                break;
                            case "tap":
                                result = TapByPath(path);
                                break;
                            case "invoke_callback":
                                result = InvokeButtonCallback(path);
                                break;
                            case "debug_button":
                                result = DebugButton(path);
                                break;
                            case "find":
                                FindMatches(path);
                                result = GakumasAutoPlugin.SharedFindResp;
                                break;
                            case "adv_end_wait":
                                result = AdvEndWait();
                                break;
                            case "adv_set_ff":
                                bool ff = root.TryGetProperty("value", out var vEl) && vEl.GetBoolean();
                                result = AdvSetFastForward(ff);
                                break;
                            case "adv_select_unselected":
                                result = AdvSelectUnselected();
                                break;
                            default:
                                ok = false;
                                result = $"unknown action: {action}";
                                break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ok = false;
                result = $"command failed: {e.Message}";
            }

            try
            {
                File.WriteAllText(RespPath, JsonSerializer.Serialize(new { id, ok, result }));
                File.Move(CmdPath, CmdPath + "." + DateTime.UtcNow.Ticks + ".done.json");
            }
            catch (Exception e)
            {
                log.LogWarning($"resp write failed: {e.Message}");
            }
        }

        private string ReadCmdFile()
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(CmdPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        return sr.ReadToEnd();
                    }
                }
                catch (IOException) { System.Threading.Thread.Sleep(50); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(50); }
            }
            return null;
        }

        private void BuildState()
        {
            var dto = new StateDto { userId = "", name = "", topLayer = "(none)", layersActive = false, loading = false, maintenance = "" };
            try
            {
                var u = Campus.Common.User.UserDataManager.User;
                if (u != null) dto.userId = u.ServerUserId ?? "";
                var prof = Campus.Common.User.UserDataManager.UserProfile;
                if (prof != null) dto.name = prof.Name ?? "";

                var slm = Campus.Common.ScreenLayerManager.Instance;
                if (slm != null)
                {
                    dto.layersActive = slm.HasActive;
                    var tl = slm.GetTopLayer();
                    if (tl != null)
                    {
                        var t = tl.GetType();
                        dto.topLayer = t != null ? t.Name : "?";
                    }
                }

                var lm = Campus.Common.LoadingManager.Instance;
                if (lm != null) dto.loading = Campus.Common.LoadingManager.IsActive;

                var mi = Campus.Common.MaintenanceInfoHolder.Instance.MaintenanceInfo;
                dto.maintenance = mi == null ? "" : mi.ToString();
            }
            catch (Exception e)
            {
                dto.maintenance = $"state read failed: {e.Message}";
            }
            GakumasAutoPlugin.SharedState = dto;
        }

        private void CaptureScreenshot()
        {
            var log = GakumasAutoPlugin.SharedLog;
            try
            {
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex != null)
                {
                    int w = tex.width, h = tex.height;
                    byte[] png = tex.EncodeToPNG();
                    File.WriteAllBytes(ScreenPath, png);
                    UnityEngine.Object.Destroy(tex);
                    log.LogInfo($"SCREENSHOT: {ScreenPath} {w}x{h} ({png.Length} bytes)");
                    GakumasAutoPlugin.SharedScreenshot = new ScreenshotDto { file = ScreenPath, w = w, h = h, pending = false };
                    return;
                }
            }
            catch (Exception e)
            {
                log.LogWarning($"sync screenshot failed: {e.Message}; falling back to async");
            }

            try
            {
                ScreenCapture.CaptureScreenshot(ScreenPath);
                log.LogInfo("SCREENSHOT: async capture scheduled");
                GakumasAutoPlugin.SharedScreenshot = new ScreenshotDto { file = ScreenPath, w = Screen.width, h = Screen.height, pending = true };
            }
            catch (Exception e)
            {
                log.LogWarning($"async screenshot failed: {e.Message}");
                GakumasAutoPlugin.SharedScreenshot = new ScreenshotDto { file = "", w = 0, h = 0, pending = false };
            }
        }

        // ---------------- target search + interaction ----------------

        private GameObject FindTarget(string path)
        {
            Transform root = FindRoot();
            if (root != null)
            {
                var t = FindByName(root, path, true);
                if (t == null) t = FindByName(root, path, false);
                if (t != null) return t;
            }
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            if (canvases != null)
            {
                foreach (var c in canvases)
                {
                    var t = FindByName(c.transform, path, true);
                    if (t == null) t = FindByName(c.transform, path, false);
                    if (t != null) return t;
                }
            }
            return null;
        }

        private void FindMatches(string pattern)
        {
            var matches = new List<FindMatchDto>();
            GakumasAutoPlugin.SharedFindMatches = matches;
            if (pattern.Length > 0)
            {
                var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
                if (canvases != null)
                {
                    foreach (var c in canvases)
                        CollectMatches(c.transform, pattern, "", 0);
                }
            }
            GakumasAutoPlugin.SharedFindMatches = null;
            GakumasAutoPlugin.SharedFindResp = new FindRespDto { count = matches.Count, matches = matches };
        }

        private Transform FindRoot()
        {
            try
            {
                var slm = Campus.Common.ScreenLayerManager.Instance;
                if (slm != null && slm.HasActive)
                {
                    var top = slm.GetTopLayer();
                    if (top != null) return top.GetParent();
                }
            }
            catch { }
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            if (canvases != null && canvases.Length > 0) return canvases[0].transform;
            return null;
        }

        private void CollectMatches(Transform t, string pattern, string parentPath, int depth)
        {
            var matches = GakumasAutoPlugin.SharedFindMatches;
            if (t == null || matches == null || matches.Count >= MaxFindMatches || depth > MaxLayoutDepth) return;
            string name = t.name ?? "?";
            string path = parentPath.Length == 0 ? name : parentPath + "/" + name;

            if (name.Contains(pattern))
            {
                bool active = true;
                try { active = t.gameObject.activeInHierarchy; } catch { }
                string text = "";
                try
                {
                    var txt = t.gameObject.GetComponent<TMPro.TMP_Text>();
                    if (txt != null) text = txt.text ?? "";
                }
                catch { }
                matches.Add(new FindMatchDto { path = path, name = name, active = active, text = text });
            }
            int children = 0;
            try { children = t.childCount; } catch { }
            for (int i = 0; i < children; i++)
            {
                Transform child = null;
                try { child = t.GetChild(i); } catch { }
                if (child != null) CollectMatches(child, pattern, path, depth + 1);
            }
        }

        private string ClickByPath(string path)
        {
            var log = GakumasAutoPlugin.SharedLog;
            if (path.Length == 0) return "empty path";
            var target = FindTarget(path);
            if (target == null) return $"not found: {path}";

            var go = target;
            Campus.Common.CampusButton cb = null;
            Button uib = null;
            for (int i = 0; i < 5 && go != null; i++)
            {
                try { cb = go.GetComponent<Campus.Common.CampusButton>(); } catch { }
                if (cb != null) break;
                try { uib = go.GetComponent<Button>(); } catch { }
                if (uib != null) break;
                go = go.transform.parent != null ? go.transform.parent.gameObject : null;
            }

            if (cb != null)
            {
                try
                {
                    var m = typeof(Qua.UI.ButtonBase).GetMethod("OnClicked",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (m == null) return "OnClicked not found on Qua.UI.ButtonBase";
                    m.Invoke(cb, null);
                    log.LogInfo($"CLICK: {target.name} via CampusButton.OnClicked()");
                    return $"clicked {target.name} via OnClicked";
                }
                catch (Exception e)
                {
                    return $"OnClicked invoke failed: {e.Message}";
                }
            }

            if (uib != null)
            {
                uib.onClick.Invoke();
                log.LogInfo($"CLICK: {target.name} via uGUI Button.onClick");
                return $"clicked {target.name} via uGUI";
            }

            return $"no button component on {target.name} or ancestors";
        }

        private string TapByPath(string path)
        {
            var log = GakumasAutoPlugin.SharedLog;
            if (path.Length == 0) return "empty path";
            var target = FindTarget(path);
            if (target == null) return $"not found: {path}";

            var es = EventSystem.current;
            if (es == null) return "EventSystem.current is null";

            Vector2 pos;
            var rt = target.GetComponent<RectTransform>();
            if (rt != null)
            {
                Camera cam = null;
                var canvas = target.GetComponentInParent<Canvas>();
                if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                    cam = canvas.worldCamera;
                pos = RectTransformUtility.WorldToScreenPoint(cam, rt.position);
            }
            else
            {
                pos = new Vector2(Screen.width / 2f, Screen.height / 2f);
            }

            var ped = new PointerEventData(es)
            {
                position = pos,
                button = PointerEventData.InputButton.Left,
                pointerId = -1,
                clickCount = 1
            };

            bool down = ExecuteEvents.Execute<IPointerDownHandler>(target, ped, ExecuteEvents.pointerDownHandler);
            bool up = ExecuteEvents.Execute<IPointerUpHandler>(target, ped, ExecuteEvents.pointerUpHandler);
            bool click = ExecuteEvents.Execute<IPointerClickHandler>(target, ped, ExecuteEvents.pointerClickHandler);

            log.LogInfo($"TAP: {target.name} pos={pos.x:F0},{pos.y:F0} down={down} up={up} click={click}");
            return $"tapped {target.name}: down={down} up={up} click={click}";
        }

        private string TapAt(float x, float y)
        {
            var log = GakumasAutoPlugin.SharedLog;
            if (x < 0 || y < 0) return "invalid coordinates";
            var es = EventSystem.current;
            if (es == null) return "EventSystem.current is null";

            var ped = new PointerEventData(es)
            {
                position = new Vector2(x, y),
                button = PointerEventData.InputButton.Left,
                pointerId = -1,
                clickCount = 1
            };

            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(ped, results);
            if (results.Count == 0)
            {
                log.LogInfo($"TAP_AT: {x:F0},{y:F0} -> no UI hit");
                return $"no UI hit at ({x:F0},{y:F0})";
            }

            var hit = results[0].gameObject;
            bool down = ExecuteEvents.ExecuteHierarchy<IPointerDownHandler>(hit, ped, ExecuteEvents.pointerDownHandler);
            bool up = ExecuteEvents.ExecuteHierarchy<IPointerUpHandler>(hit, ped, ExecuteEvents.pointerUpHandler);
            bool click = ExecuteEvents.ExecuteHierarchy<IPointerClickHandler>(hit, ped, ExecuteEvents.pointerClickHandler);

            log.LogInfo($"TAP_AT: {x:F0},{y:F0} hit={hit.name} down={down} up={up} click={click}");
            return $"tapped at ({x:F0},{y:F0}) hit={hit.name}: down={down} up={up} click={click}";
        }

        private string InvokeButtonCallback(string path)
        {
            var log = GakumasAutoPlugin.SharedLog;
            if (path.Length == 0) return "empty path";
            var target = FindTarget(path);
            if (target == null) return $"not found: {path}";

            var cb = target.GetComponent<Campus.Common.CampusButton>();
            if (cb == null)
            {
                var btn = target.GetComponent<Button>();
                if (btn != null)
                {
                    btn.onClick.Invoke();
                    return $"invoked uGUI onClick on {target.name}";
                }
                return $"no CampusButton on {target.name}";
            }

            try
            {
                var gesture = cb.GetLongTapGesture();
                if (gesture != null && gesture.onClickedCallback != null)
                {
                    gesture.onClickedCallback.Invoke();
                    log.LogInfo($"CALLBACK: {target.name} invoked LongTapGesture.onClickedCallback");
                    return $"invoked gesture callback on {target.name}";
                }
            }
            catch (Exception e) { return $"gesture callback failed: {e.Message}"; }

            try
            {
                var prop = typeof(Qua.UI.ButtonBase).GetProperty("onClickedCallback",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prop != null)
                {
                    var cbk = prop.GetValue(cb) as Il2CppSystem.Action;
                    if (cbk != null)
                    {
                        cbk.Invoke();
                        log.LogInfo($"CALLBACK: {target.name} invoked ButtonBase.onClickedCallback");
                        return $"invoked button callback on {target.name}";
                    }
                }
            }
            catch (Exception e) { return $"button callback failed: {e.Message}"; }

            return $"no wired callback found on {target.name}";
        }

        private string DebugButton(string path)
        {
            if (path.Length == 0) return "empty path";
            var target = FindTarget(path);
            if (target == null) return $"not found: {path}";

            var parts = new List<string>();
            var go = target;
            for (int i = 0; i < 3 && go != null; i++)
            {
                parts.Add(go.name);
                go = go.transform.parent != null ? go.transform.parent.gameObject : null;
            }

            var cb = target.GetComponent<Campus.Common.CampusButton>();
            if (cb == null)
            {
                var btn = target.GetComponent<Button>();
                return $"path={string.Join("/", parts)} CampusButton=NONE uGUIButton={(btn != null ? "yes" : "no")}";
            }

            string gestureCbk = "none";
            string buttonCbk = "none";
            string pressedCbk = "none";
            try
            {
                var g = cb.GetLongTapGesture();
                if (g != null)
                {
                    gestureCbk = g.onClickedCallback != null ? "SET" : "null";
                    pressedCbk = g.onPressedCallback != null ? "SET" : "null";
                }
            }
            catch { }
            try
            {
                var prop = typeof(Qua.UI.ButtonBase).GetProperty("onClickedCallback",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (prop != null)
                {
                    var cbk = prop.GetValue(cb) as Il2CppSystem.Action;
                    buttonCbk = cbk != null ? "SET" : "null";
                }
            }
            catch { }

            return $"path={string.Join("/", parts)} IsEnabled={cb.IsEnabled} IsDisabled={cb.IsDisabled} gestureCallback={gestureCbk} buttonCallback={buttonCbk} pressedCallback={pressedCbk}";
        }

        private GameObject FindByName(Transform t, string name, bool exact)
        {
            if (t == null) return null;
            if (t.name == name) return t.gameObject;
            if (!exact && t.name.Contains(name)) return t.gameObject;

            int children = 0;
            try { children = t.childCount; } catch { }
            for (int i = 0; i < children; i++)
            {
                Transform child = null;
                try { child = t.GetChild(i); } catch { }
                if (child != null)
                {
                    var hit = FindByName(child, name, exact);
                    if (hit != null) return hit;
                }
            }
            return null;
        }

        // ---------------- ADV direct commands ----------------

        private string AdvEndWait()
        {
            try
            {
                var engines = UnityEngine.Object.FindObjectsOfType<ADVEngine>();
                if (engines == null || engines.Length == 0) return "no ADVEngine";
                var timeline = engines[0].Timeline;
                if (timeline == null) return "ADVEngine.Timeline is null";
                timeline.EndWait(true);
                return $"EndWait(skip=true) sent";
            }
            catch (Exception e) { return $"adv_end_wait failed: {e.Message}"; }
        }

        private string AdvSetFastForward(bool on)
        {
            try
            {
                var engines = UnityEngine.Object.FindObjectsOfType<ADVEngine>();
                if (engines == null || engines.Length == 0) return "no ADVEngine";
                var timeline = engines[0].Timeline;
                if (timeline == null) return "ADVEngine.Timeline is null";
                timeline.ToggleFastForward(on);
                return $"ToggleFastForward({on}) sent";
            }
            catch (Exception e) { return $"adv_set_ff failed: {e.Message}"; }
        }

        private string AdvSelectUnselected()
        {
            try
            {
                var engines = UnityEngine.Object.FindObjectsOfType<ADVEngine>();
                if (engines == null || engines.Length == 0) return "no ADVEngine";
                var engine = engines[0];
                if (engine.Branch == null) return "engine.Branch is null";
                int n = engine.Branch.ChoiceCount;
                engine.Branch.SelectUnselectedChoices();
                return $"SelectUnselectedChoices sent; choiceCount={n}";
            }
            catch (Exception e) { return $"adv_select_unselected failed: {e.Message}"; }
        }
    }
}
