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

        private void CaptureLayout2()
        {
            _sb.Clear();
            _walkCount = 0;
            _layout2Truncated = false;

            var canvases = UnityEngine.Object.FindObjectsOfType<Canvas>();
            if (canvases != null)
            {
                for (int i = 0; i < canvases.Length; i++)
                {
                    var c = canvases[i];
                    if (c == null) continue;
                    Camera cam = null;
                    try
                    {
                        if (c.renderMode != RenderMode.ScreenSpaceOverlay)
                            cam = c.worldCamera;
                    }
                    catch { }
                    WalkLayout2(c.transform, 0, cam);
                }
            }

            GakumasAutoPlugin.SharedLayout2 = new Layout2Dto
            {
                w = Screen.width,
                h = Screen.height,
                count = _walkCount,
                truncated = _layout2Truncated,
                tree = _sb.ToString()
            };
        }

        private void WalkLayout2(Transform t, int depth, Camera cam)
        {
            if (t == null) return;
            if (_walkCount >= MaxLayout2Nodes || depth > MaxLayout2Depth)
            {
                _layout2Truncated = true;
                return;
            }

            var go = t.gameObject;
            bool active = true;
            try { active = go == null || go.activeInHierarchy; } catch { }
            if (!_layout2IncludeInactive && !active) return;

            _walkCount++;

            string name = t.name ?? "?";
            if (name.Length > MaxLayout2Name) name = name.Substring(0, MaxLayout2Name);
            name = name.Replace('\n', ' ').Replace('\r', ' ');

            Camera nodeCam = cam;
            try
            {
                var nested = go != null ? go.GetComponent<Canvas>() : null;
                if (nested != null)
                    nodeCam = nested.renderMode != RenderMode.ScreenSpaceOverlay ? nested.worldCamera : null;
            }
            catch { }

            int sx = 0, sy = 0;
            float rw = 0, rh = 0;
            bool hasRect = false;
            try
            {
                var rt = t.GetComponent<RectTransform>();
                if (rt != null)
                {
                    hasRect = true;
                    rw = rt.rect.width;
                    rh = rt.rect.height;
                    if (rw <= 0f)
                    {
                        try
                        {
                            var sd = rt.sizeDelta;
                            rw = sd.x;
                            rh = sd.y;
                        }
                        catch { }
                    }
                    try
                    {
                        var sp = RectTransformUtility.WorldToScreenPoint(nodeCam, rt.position);
                        sx = (int)sp.x;
                        sy = (int)sp.y;
                    }
                    catch { }
                }
            }
            catch { }

            bool campusBtn = false, uiBtn = false, interactable = true;
            if (go != null)
            {
                try { campusBtn = go.GetComponent<Campus.Common.CampusButton>() != null; } catch { }
                try
                {
                    var ub = go.GetComponent<Button>();
                    if (ub != null)
                    {
                        uiBtn = true;
                        try { interactable = ub.interactable; } catch { }
                    }
                }
                catch { }
            }

            string text = "";
            try
            {
                var txt = go != null ? go.GetComponent<TMPro.TMP_Text>() : null;
                if (txt != null) text = txt.text ?? "";
            }
            catch { }
            if (text.Length == 0)
            {
                try
                {
                    var utxt = go != null ? go.GetComponent<UnityEngine.UI.Text>() : null;
                    if (utxt != null) text = utxt.text ?? "";
                }
                catch { }
            }
            if (text.Length > 0)
            {
                text = text.Replace('\n', ' ').Replace('\r', ' ').Replace('"', '\'');
                if (text.Length > MaxLayout2Text) text = text.Substring(0, MaxLayout2Text);
            }

            for (int i = 0; i < depth; i++) _sb.Append("  ");
            _sb.Append(active ? '+' : '-');
            _sb.Append(' ');
            _sb.Append(name);
            if (campusBtn) _sb.Append(" #B");
            if (uiBtn) _sb.Append(" #U");
            if ((campusBtn || uiBtn) && !interactable) _sb.Append(" #X");
            if (hasRect && (rw > 0f || rh > 0f || campusBtn || uiBtn))
            {
                _sb.Append(' ');
                _sb.Append(sx);
                _sb.Append(',');
                _sb.Append(sy);
                _sb.Append(' ');
                _sb.Append((int)rw);
                _sb.Append('x');
                _sb.Append((int)rh);
            }
            if (text.Length > 0)
            {
                _sb.Append(" \"");
                _sb.Append(text);
                _sb.Append('"');
            }
            _sb.Append('\n');

            int children = 0;
            try { children = t.childCount; } catch { }
            for (int i = 0; i < children; i++)
            {
                Transform child = null;
                try { child = t.GetChild(i); } catch { }
                if (child != null) WalkLayout2(child, depth + 1, nodeCam);
            }
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
    }
}
