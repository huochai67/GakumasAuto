using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using BepInEx;
using Campus.ADV;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GakumasAuto
{
    public partial class AutoDriver
    {
        private GameObject FindTarget(string path)
        {
            Transform root = FindRoot();
            if (root != null)
            {
                var t = FindByName(root, path, true, "");
                if (t == null) t = FindByName(root, path, false, "");
                if (t != null) return t;
            }
            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            if (canvases != null)
            {
                foreach (var c in canvases)
                {
                    var t = FindByName(c.transform, path, true, "");
                    if (t == null) t = FindByName(c.transform, path, false, "");
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

        private string DragAt(float x, float y, float x2, float y2)
        {
            var log = GakumasAutoPlugin.SharedLog;
            if (x < 0 || y < 0 || x2 < 0 || y2 < 0) return "invalid coordinates";
            var es = EventSystem.current;
            if (es == null) return "EventSystem.current is null";

            var ped = new PointerEventData(es)
            {
                position = new Vector2(x, y),
                button = PointerEventData.InputButton.Left,
                pointerId = -1,
                pressPosition = new Vector2(x, y)
            };
            ped.delta = Vector2.zero;

            var results = new Il2CppSystem.Collections.Generic.List<RaycastResult>();
            es.RaycastAll(ped, results);
            if (results.Count == 0) return $"no UI hit at ({x:F0},{y:F0})";
            var hit = results[0].gameObject;

            ExecuteEvents.ExecuteHierarchy<IPointerDownHandler>(hit, ped, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.ExecuteHierarchy<IBeginDragHandler>(hit, ped, ExecuteEvents.beginDragHandler);
            ped.position = new Vector2(x2, y2);
            ped.delta = new Vector2(x2 - x, y2 - y);
            ExecuteEvents.ExecuteHierarchy<IDragHandler>(hit, ped, ExecuteEvents.dragHandler);
            ExecuteEvents.ExecuteHierarchy<IDropHandler>(hit, ped, ExecuteEvents.dropHandler);
            ExecuteEvents.ExecuteHierarchy<IPointerUpHandler>(hit, ped, ExecuteEvents.pointerUpHandler);
            ExecuteEvents.ExecuteHierarchy<IEndDragHandler>(hit, ped, ExecuteEvents.endDragHandler);

            log.LogInfo($"DRAG: {x:F0},{y:F0} -> {x2:F0},{y2:F0} hit={hit.name}");
            return $"dragged ({x:F0},{y:F0})->({x2:F0},{y2:F0}) hit={hit.name}";
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

        private string InvokeFirstMatch(string[] names)
        {
            if (names == null) return null;
            for (int i = 0; i < names.Length; i++)
            {
                if (string.IsNullOrEmpty(names[i])) continue;
                if (FindTarget(names[i]) == null) continue;
                var msg = InvokeButtonCallback(names[i]);
                if (msg != null && msg.IndexOf("invoked", StringComparison.Ordinal) >= 0)
                    return msg;
            }
            return null;
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

        // Path-aware node search. A query containing '/' matches the full node path
        // (exact or suffix, e.g. "HomeFooter/UIContentArea/FrontRoot/ButtonRoot/Home");
        // a bare name keeps the old exact-then-substring name matching.
        private GameObject FindByName(Transform t, string query, bool exact, string parentPath)
        {
            if (t == null) return null;
            string name = t.name ?? "?";
            string path = parentPath.Length == 0 ? name : parentPath + "/" + name;
            if (query.IndexOf('/') >= 0)
            {
                if (path == query || path.EndsWith("/" + query)) return t.gameObject;
            }
            else
            {
                if (name == query) return t.gameObject;
                if (!exact && name.Contains(query)) return t.gameObject;
            }

            int children = 0;
            try { children = t.childCount; } catch { }
            for (int i = 0; i < children; i++)
            {
                Transform child = null;
                try { child = t.GetChild(i); } catch { }
                if (child != null)
                {
                    var hit = FindByName(child, query, exact, path);
                    if (hit != null) return hit;
                }
            }
            return null;
        }
    }
}
