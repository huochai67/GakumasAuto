using System;
using System.Collections.Generic;

namespace GakumasAuto
{
    public partial class AutoDriver
    {
        private static string CapsuleKind(string name, string type, int index)
        {
            string n = name ?? "";
            if (n.IndexOf("フレンド", StringComparison.Ordinal) >= 0 || n.IndexOf("Friend", StringComparison.OrdinalIgnoreCase) >= 0)
                return "friend";
            if (n.IndexOf("センス", StringComparison.Ordinal) >= 0 || n.IndexOf("Sense", StringComparison.OrdinalIgnoreCase) >= 0)
                return "sense";
            if (n.IndexOf("ロジック", StringComparison.Ordinal) >= 0 || n.IndexOf("Logic", StringComparison.OrdinalIgnoreCase) >= 0)
                return "logic";
            if (n.IndexOf("アノマリー", StringComparison.Ordinal) >= 0 || n.IndexOf("Anomaly", StringComparison.OrdinalIgnoreCase) >= 0)
                return "anomaly";
            if (index == 0) return "friend";
            if (index == 1) return "sense";
            if (index == 2) return "logic";
            if (index == 3) return "anomaly";
            return (type ?? "").ToLowerInvariant();
        }

        private static string PropStr(object o, string name)
        {
            if (o == null) return "";
            try
            {
                var p = o.GetType().GetProperty(name);
                if (p == null) return "";
                var v = p.GetValue(o, null);
                return v != null ? v.ToString() : "";
            }
            catch { return ""; }
        }

        private static int PropInt(object o, string name)
        {
            if (o == null) return 0;
            try
            {
                var p = o.GetType().GetProperty(name);
                if (p == null) return 0;
                var v = p.GetValue(o, null);
                if (v == null) return 0;
                return Convert.ToInt32(v);
            }
            catch { return 0; }
        }

        private object CapsuleEnter()
        {
            try
            {
                Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.CoinGashaTop, true, false, null);
                return "opened CoinGashaTop";
            }
            catch (Exception e)
            {
                return "capsule_enter failed: " + e.Message;
            }
        }

        private void BuildCapsuleState()
        {
            var dto = new CapsuleListDto
            {
                screenOpen = false,
                gashas = new List<CapsuleDto>(),
                error = ""
            };
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.CoinGashaTopScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    dto.error = "coin gasha screen not open (call capsule_enter first)";
                    GakumasAutoPlugin.SharedCapsuleList = dto;
                    return;
                }

                dto.screenOpen = true;
                Campus.OutGame.CoinGashaTopScreenModel model = null;
                for (int i = 0; i < presenters.Length; i++)
                {
                    model = PresenterUtil.ReadModel<Campus.OutGame.CoinGashaTopScreenModel>(presenters[i]);
                    if (model != null) break;
                }
                if (model == null)
                {
                    dto.error = "coin gasha model null (screen still loading?)";
                    GakumasAutoPlugin.SharedCapsuleList = dto;
                    return;
                }

                var items = model.ItemModels;
                if (items != null)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        Campus.OutGame.CoinGashaListItemModel it = null;
                        try { it = items[i]; } catch { break; }
                        if (it == null) break;
                        var d = MapCapsuleItem(it, i);
                        dto.gashas.Add(d);
                    }
                }
            }
            catch (Exception e)
            {
                dto.error = "capsule read failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedCapsuleList = dto;
        }

        private static CapsuleDto MapCapsuleItem(Campus.OutGame.CoinGashaListItemModel it, int index)
        {
            var d = new CapsuleDto
            {
                id = "",
                name = "",
                type = "",
                kind = "",
                locked = it.IsLock,
                noti = it.IsNoti,
                consumption = "",
                consumptionQuantity = 0,
                maxDraw = 0,
                totalDrawCount = 0
            };
            try
            {
                var info = it.GashaInfo;
                if (info != null)
                {
                    d.type = info.Type.ToString();
                    d.id = PropStr(info, "Id");
                    if (d.id.Length == 0) d.id = info.CoinGashaButtonId ?? "";
                    d.name = PropStr(info, "Name");
                    try
                    {
                        var btn = Campus.Common.Data.ICoinGashaInfoExtensions.GetCoinGashaButton(info);
                        if (btn != null)
                        {
                            if (d.id.Length == 0) d.id = btn.Id ?? d.id;
                            if (d.name.Length == 0) d.name = btn.Name ?? "";
                            if (d.consumption.Length == 0) d.consumption = btn.ResourceType.ToString();
                            if (d.consumptionQuantity <= 0) d.consumptionQuantity = btn.Quantity;
                            if (d.maxDraw <= 0) d.maxDraw = btn.MaxDrawCount;
                        }
                    }
                    catch { }
                    try
                    {
                        var bi = it.GashaButtonInfo;
                        if (bi != null && d.name.Length == 0)
                            d.name = PropStr(bi, "Name");
                    }
                    catch { }
                    d.totalDrawCount = PropInt(info, "TotalDrawCount");
                    d.maxDraw = info.MaxGuaranteedDrawCount;
                    if (d.maxDraw <= 0) d.maxDraw = PropInt(info, "MaxDrawCount");
                }
            }
            catch { }
            try
            {
                var c = it.Consumption;
                if (c != null)
                {
                    d.consumption = c.ResourceType.ToString();
                    try
                    {
                        var qn = PropInt(c, "Quantity");
                        if (qn > 0) d.consumptionQuantity = qn;
                    }
                    catch { }
                }
            }
            catch { }
            d.kind = CapsuleKind(d.name, d.type, index);
            return d;
        }

        private object CapsuleDraw(string kindRaw, bool confirm)
        {
            try
            {
                string kind = (kindRaw ?? "").Trim().ToLowerInvariant();
                if (kind == "friend" || kind == "0" || kind == "フレンド") kind = "friend";
                else if (kind == "sense" || kind == "1" || kind == "センス" || kind == "感性") kind = "sense";
                else if (kind == "logic" || kind == "2" || kind == "ロジック" || kind == "理性") kind = "logic";
                else if (kind == "anomaly" || kind == "3" || kind == "アノマリー" || kind == "非凡") kind = "anomaly";
                else return "kind must be friend|sense|logic|anomaly";

                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.CoinGashaTopScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    if (!confirm) return "dry_run: CoinGashaTop not open; would draw " + kind;
                    Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.CoinGashaTop, true, false, null);
                    return "opened CoinGashaTop; retry capsule_draw after the screen loads";
                }

                var model = PresenterUtil.ReadModel<Campus.OutGame.CoinGashaTopScreenModel>(presenters[0]);
                if (model == null) return "coin gasha model null";
                var items = model.ItemModels;
                if (items == null) return "no coin gasha items";

                Campus.OutGame.CoinGashaListItemModel hit = null;
                string hitName = "";
                for (int i = 0; i < 16; i++)
                {
                    Campus.OutGame.CoinGashaListItemModel it = null;
                    try { it = items[i]; } catch { break; }
                    if (it == null) break;
                    var mapped = MapCapsuleItem(it, i);
                    if (mapped.kind == kind)
                    {
                        hit = it;
                        hitName = mapped.name;
                        break;
                    }
                }
                if (hit == null) return "capsule not found: " + kind;
                if (hit.IsLock) return "LOCKED: " + kind + " " + hitName;
                if (!confirm) return "dry_run: would draw " + kind + " " + hitName + " (pass confirm:true)";

                presenters[0].OnDrawButtonClicked(hit);
                return "opened draw sheet for " + kind + " " + hitName + "; set count in UI then confirm ExecuteButton";
            }
            catch (Exception e)
            {
                return "capsule_draw failed: " + e.Message;
            }
        }
    }
}
