using System;
using System.Collections.Generic;

namespace GakumasAuto
{
    public partial class AutoDriver
    {
        private object SupportEnter()
        {
            try
            {
                Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.CardSupportCardList, true, false, null);
                return "opened CardSupportCardList";
            }
            catch (Exception e)
            {
                return "support_enter failed: " + e.Message;
            }
        }

        private static SupportCardDto MapSupportCard(Campus.Common.Proto.Client.Transaction.UserSupportCard c)
        {
            var d = new SupportCardDto
            {
                id = c.SupportCardId ?? "",
                name = "",
                level = c.Level,
                levelLimit = c.LevelLimit,
                stock = c.StockQuantity,
                planType = "",
                rarity = c.RarityInt
            };
            try { d.planType = c.PlanType.ToString(); } catch { }
            try
            {
                var master = c.SupportCardMaster;
                if (master == null) master = c.GetSupportCard();
                if (master != null) d.name = master.Name ?? "";
            }
            catch { }
            if (d.name.Length == 0) d.name = d.id;
            return d;
        }

        private void BuildSupportList()
        {
            var dto = new SupportCardListDto
            {
                count = 0,
                cards = new List<SupportCardDto>(),
                error = ""
            };
            try
            {
                var list = Campus.Common.User.UserDataManager.UserSupportCardList;
                if (list == null)
                {
                    dto.error = "UserSupportCardList unavailable";
                    GakumasAutoPlugin.SharedSupportCards = dto;
                    return;
                }
                var all = list.GetAll();
                if (all == null)
                {
                    dto.error = "UserSupportCardList.GetAll() null";
                    GakumasAutoPlugin.SharedSupportCards = dto;
                    return;
                }
                const int max = 400;
                foreach (var c in all)
                {
                    if (c == null) continue;
                    if (dto.cards.Count >= max) break;
                    try { dto.cards.Add(MapSupportCard(c)); }
                    catch { }
                }
                dto.count = dto.cards.Count;
                if (dto.cards.Count >= max) dto.error = "truncated at " + max;
            }
            catch (Exception e)
            {
                dto.error = "support list failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedSupportCards = dto;
        }

        private object SupportUpgrade(bool confirm)
        {
            try
            {
                var list = Campus.Common.User.UserDataManager.UserSupportCardList;
                if (list == null) return "UserSupportCardList unavailable";
                var all = list.GetAll();
                if (all == null) return "no support cards";

                Campus.Common.Proto.Client.Transaction.UserSupportCard best = null;
                int bestLevel = int.MaxValue;
                foreach (var c in all)
                {
                    if (c == null) continue;
                    int lv = c.Level;
                    int lim = c.LevelLimit;
                    if (lim > 0 && lv >= lim) continue;
                    if (lv < bestLevel)
                    {
                        bestLevel = lv;
                        best = c;
                    }
                }
                if (best == null) return "nothing_to_upgrade: all support cards at level limit";

                var mapped = MapSupportCard(best);
                if (!confirm)
                    return "dry_run: would upgrade " + mapped.id + " " + mapped.name + " lv" + mapped.level + " (pass confirm:true)";

                var details = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.CardSupportCardDetailScreenPresenter>();
                bool onDetail = details != null && details.Length > 0;
                if (onDetail)
                {
                    var msg = InvokeFirstMatch(new[]
                    {
                        "EnhanceButton", "UpgradeButton", "LevelUpButton",
                        "ExecuteButton", "ActionButton"
                    });
                    if (msg != null) return "support upgrade: " + mapped.name + " lv" + mapped.level + " " + msg;
                    return "SUPPORT_ENHANCE_NOT_FOUND: detail open for " + mapped.name + " but no enhance button";
                }

                var lists = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.CardSupportCardListScreenPresenter>();
                if (lists == null || lists.Length == 0)
                {
                    Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.CardSupportCardList, true, false, null);
                    return "opened CardSupportCardList; retry support_upgrade after the screen loads (target " + mapped.name + " lv" + mapped.level + ")";
                }

                lists[0].MoveCardDetailScreen(mapped.id);
                return "opened detail for " + mapped.name + " lv" + mapped.level + "; retry support_upgrade to press enhance";
            }
            catch (Exception e)
            {
                return "support_upgrade failed: " + e.Message;
            }
        }
    }
}
