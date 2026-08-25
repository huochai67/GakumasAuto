using System;
using System.Collections.Generic;

namespace GakumasAuto
{
    public partial class AutoDriver
    {
        private object GiftEnter()
        {
            try
            {
                Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.PresentTop, true, false, null);
                return "opened PresentTop";
            }
            catch (Exception e)
            {
                return "gift_enter failed: " + e.Message;
            }
        }

        private void BuildGiftList()
        {
            var dto = new GiftListDto
            {
                screenOpen = false,
                total = 0,
                gifts = new List<GiftDto>(),
                error = ""
            };
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.PresentTopScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    dto.error = "present screen not open (call gift_enter first)";
                    GakumasAutoPlugin.SharedGiftList = dto;
                    return;
                }

                dto.screenOpen = true;
                var model = presenters[0]._model;
                if (model == null)
                {
                    dto.error = "present model null";
                    GakumasAutoPlugin.SharedGiftList = dto;
                    return;
                }

                dto.total = model.UserGiftTotalCount;
                var gifts = model.UserGifts;
                if (gifts != null)
                {
                    for (int i = 0; i < 200; i++)
                    {
                        Campus.Common.Proto.Client.Api.GiftListResponse.Types.UserGift g = null;
                        try { g = gifts[i]; }
                        catch { break; }
                        if (g == null) break;
                        dto.gifts.Add(MapGift(g));
                    }
                }
            }
            catch (Exception e)
            {
                dto.error = "gift list failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedGiftList = dto;
        }

        private static GiftDto MapGift(Campus.Common.Proto.Client.Api.GiftListResponse.Types.UserGift g)
        {
            var d = new GiftDto
            {
                id = g.UserGiftId ?? "",
                name = "",
                resourceId = g.ResourceId ?? "",
                resourceType = g.ResourceType.ToString(),
                quantity = g.Quantity,
                message = g.Message ?? "",
                createdAt = g.CreatedAt,
                limitTime = g.LimitTime,
                unreceivable = g.IsUnreceivable
            };
            if (d.name.Length == 0)
            {
                if (d.resourceType == "JewelTotal" || d.resourceType == "Jewel") d.name = "ジュエル";
                else if (d.resourceId == "item-produce_continue-1") d.name = "再挑戦チケット";
                else if (d.resourceId == "item-staminaregen-1") d.name = "APドリンク";
                else d.name = d.resourceId;
            }
            return d;
        }

        private object GiftReceive(string giftId)
        {
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.PresentTopScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                    return "PRESENT_NOT_OPEN: call gift_enter first";

                string[] names;
                if (string.IsNullOrEmpty(giftId))
                    names = new[] { "ReceiveAllButton", "AllReceiveButton", "ReceiveAll", "ExecuteButton" };
                else
                    names = new[] { "ReceiveButton", "ExecuteButton" };

                for (int i = 0; i < names.Length; i++)
                {
                    var hit = FindTarget(names[i]);
                    if (hit == null) continue;
                    var msg = InvokeButtonCallback(names[i]);
                    if (msg != null && msg.IndexOf("invoked", StringComparison.Ordinal) >= 0)
                        return "gift receive: " + msg;
                }

                FindMatches("Receive");
                var found = GakumasAutoPlugin.SharedFindResp;
                if (found != null && found.matches != null)
                {
                    for (int i = 0; i < found.matches.Count; i++)
                    {
                        var m = found.matches[i];
                        if (m == null || !m.active) continue;
                        var msg = InvokeButtonCallback(m.path);
                        return "gift receive: " + msg;
                    }
                }
                return "GIFT_RECEIVE_NOT_FOUND: present screen open but no receive button";
            }
            catch (Exception e)
            {
                return "gift_receive failed: " + e.Message;
            }
        }
    }
}
