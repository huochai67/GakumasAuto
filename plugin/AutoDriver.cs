using System;
using System.IO;
using System.Text;
using System.Text.Json;
using BepInEx;
using Campus.ADV;
using UnityEngine;

namespace GakumasAuto
{
    public partial class AutoDriver : MonoBehaviour
    {
        private const int MaxLayoutNodes = 3000;
        private const int MaxLayoutDepth = 12;
        private const int MaxFindMatches = 300;
        private const int MaxLayout2Nodes = 8000;
        private const int MaxLayout2Depth = 32;
        private const int MaxLayout2Text = 80;
        private const int MaxLayout2Name = 96;

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
        private static GUIStyle _watermarkStyle;
        private static GUIStyle _watermarkShadowStyle;
        private static bool _watermarkFailed;
        private bool _layout2Truncated;
        private bool _layout2IncludeInactive = true;

        public void Attach()
        {
            _nextTick = Time.realtimeSinceStartup + 1f;
            _nextHeartbeat = Time.realtimeSinceStartup + 30f;
            _nextWatch = Time.realtimeSinceStartup + 5f;
        }

        public void OnGUI()
        {
            if (_watermarkFailed) return;
            try
            {
                if (_watermarkStyle == null)
                {
                    _watermarkStyle = MakeWatermarkStyle(new Color(1f, 1f, 1f, 0.6f));
                    _watermarkShadowStyle = MakeWatermarkStyle(new Color(0f, 0f, 0f, 0.45f));
                }

                const string label = "AUTOMATION";
                var rect = new Rect(10f, 8f, 400f, 32f);
                GUI.depth = -1000;
                GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), label, _watermarkShadowStyle);
                GUI.Label(rect, label, _watermarkStyle);
            }
            catch (Exception e)
            {
                _watermarkFailed = true;
                GakumasAutoPlugin.SharedLog?.LogWarning($"watermark OnGUI failed: {e.Message}");
            }
        }

        private static GUIStyle MakeWatermarkStyle(Color color)
        {
            var style = new GUIStyle();
            style.fontSize = 18;
            style.fontStyle = FontStyle.Bold;
            style.alignment = TextAnchor.UpperLeft;
            var state = style.normal;
            if (state != null)
            {
                state.textColor = color;
                style.normal = state;
            }
            return style;
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
                            case "layout2":
                                _layout2IncludeInactive = true;
                                if (root.TryGetProperty("includeInactive", out var i2El) && i2El.ValueKind == JsonValueKind.False)
                                    _layout2IncludeInactive = false;
                                CaptureLayout2();
                                result = GakumasAutoPlugin.SharedLayout2;
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
                            case "drag_at":
                                float dx1 = root.TryGetProperty("x", out var dxEl) ? dxEl.GetSingle() : -1f;
                                float dy1 = root.TryGetProperty("y", out var dyEl) ? dyEl.GetSingle() : -1f;
                                float dx2 = root.TryGetProperty("x2", out var dx2El) ? dx2El.GetSingle() : -1f;
                                float dy2 = root.TryGetProperty("y2", out var dy2El) ? dy2El.GetSingle() : -1f;
                                result = DragAt(dx1, dy1, dx2, dy2);
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
                            case "daily_state":
                                BuildDailyState();
                                result = GakumasAutoPlugin.SharedDailyState;
                                break;
                            case "shop_items":
                                BuildShopItems();
                                result = GakumasAutoPlugin.SharedShopList;
                                break;
                            case "exchange_list":
                                BuildExchangeList();
                                result = GakumasAutoPlugin.SharedExchangeList;
                                break;
                            case "exchange_enter":
                                string exType = root.TryGetProperty("type", out var etEl) ? (etEl.GetString() ?? "") : "";
                                string exId = "";
                                if (root.TryGetProperty("exchange_id", out var eidEl)) exId = eidEl.GetString() ?? "";
                                else if (root.TryGetProperty("exchangeId", out var eid2El)) exId = eid2El.GetString() ?? "";
                                result = ExchangeEnter(exType, exId);
                                break;
                            case "exam_start":
                                result = ExamStart();
                                break;
                            case "exam_state":
                                BuildExamState();
                                result = GakumasAutoPlugin.SharedExamState;
                                break;
                            case "mission_list":
                                string missionCat = "";
                                if (root.TryGetProperty("category", out var mcEl)) missionCat = mcEl.GetString() ?? "";
                                BuildMissionList(missionCat);
                                result = GakumasAutoPlugin.SharedMissionList;
                                break;
                            case "mission_receive":
                                result = MissionReceive();
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
                            case "account_state":
                                BuildAccountState();
                                result = GakumasAutoPlugin.SharedAccountState;
                                break;
                            case "item_list":
                                BuildItemList();
                                result = GakumasAutoPlugin.SharedItemList;
                                break;
                            case "screen_goto":
                                string screen = root.TryGetProperty("screen", out var scEl) ? (scEl.GetString() ?? "") : "";
                                result = ScreenGoto(screen);
                                break;
                            case "gift_list":
                                BuildGiftList();
                                result = GakumasAutoPlugin.SharedGiftList;
                                break;
                            case "gift_enter":
                                result = GiftEnter();
                                break;
                            case "gift_receive":
                                string giftId = root.TryGetProperty("gift_id", out var gidEl) ? (gidEl.GetString() ?? "") : "";
                                result = GiftReceive(giftId);
                                break;
                            case "pvp_state":
                                BuildPvpState();
                                result = GakumasAutoPlugin.SharedPvpState;
                                break;
                            case "pvp_enter":
                                result = PvpEnter();
                                break;
                            case "produce_state":
                                BuildProduceState();
                                result = GakumasAutoPlugin.SharedProduceState;
                                break;
                            case "produce_enter":
                                result = ProduceEnter();
                                break;
                            case "produce_schedule":
                                BuildProduceSchedule();
                                result = GakumasAutoPlugin.SharedProduceSchedule;
                                break;
                            case "produce_shop":
                                BuildProduceShop();
                                result = GakumasAutoPlugin.SharedProduceShop;
                                break;
                            case "produce_outing":
                                BuildProduceOuting();
                                result = GakumasAutoPlugin.SharedProduceOuting;
                                break;
                            case "produce_cards":
                                BuildProduceCards();
                                result = GakumasAutoPlugin.SharedProduceCards;
                                break;
                            case "pvp_challenge":
                                string rival = root.TryGetProperty("rival", out var rvEl) ? (rvEl.GetString() ?? "") : "";
                                bool pvpConfirm = root.TryGetProperty("confirm", out var pcEl) && pcEl.ValueKind == JsonValueKind.True;
                                result = PvpChallenge(rival, pvpConfirm);
                                break;
                            case "pvp_auto_set":
                                result = PvpAutoSet();
                                break;
                            case "club_state":
                                BuildClubState();
                                result = GakumasAutoPlugin.SharedClubState;
                                break;
                            case "club_enter":
                                result = ClubEnter();
                                break;
                            case "club_receive":
                                result = ClubReceive();
                                break;
                            case "club_request":
                                result = ClubRequest();
                                break;
                            case "club_donate":
                                result = ClubDonate();
                                break;
                            case "capsule_state":
                                BuildCapsuleState();
                                result = GakumasAutoPlugin.SharedCapsuleList;
                                break;
                            case "capsule_enter":
                                result = CapsuleEnter();
                                break;
                            case "capsule_draw":
                                string capKind = root.TryGetProperty("kind", out var ckEl) ? (ckEl.GetString() ?? "") : "";
                                bool capConfirm = root.TryGetProperty("confirm", out var ccEl) && ccEl.ValueKind == JsonValueKind.True;
                                result = CapsuleDraw(capKind, capConfirm);
                                break;
                            case "support_list":
                                BuildSupportList();
                                result = GakumasAutoPlugin.SharedSupportCards;
                                break;
                            case "support_enter":
                                result = SupportEnter();
                                break;
                            case "support_upgrade":
                                bool suConfirm = root.TryGetProperty("confirm", out var suEl) && suEl.ValueKind == JsonValueKind.True;
                                result = SupportUpgrade(suConfirm);
                                break;
                            case "exchange_items":
                                BuildExchangeItems();
                                result = GakumasAutoPlugin.SharedExchangeItems;
                                break;
                            case "exam_play":
                                int examIdx = -1;
                                if (root.TryGetProperty("index", out var eiEl) && eiEl.ValueKind == JsonValueKind.Number)
                                    examIdx = eiEl.GetInt32();
                                result = ExamPlay(examIdx);
                                break;
                            case "exam_skip_end":
                                result = ExamSkipEnd();
                                break;
                            case "loading_hide":
                                result = PresenterUtil.DismissLoading();
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

        internal static string SanitizeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var chars = s.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                char c = chars[i];
                if (c < 32 && c != '\t') chars[i] = ' ';
            }
            return new string(chars).Trim();
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
    }
}
