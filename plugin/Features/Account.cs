using System;
using System.Collections.Generic;

namespace GakumasAuto
{
    public partial class AutoDriver
    {
        private void BuildAccountState()
        {
            var dto = new AccountStateDto
            {
                userId = "",
                name = "",
                producerLevel = 0,
                exp = 0,
                expToNext = 0,
                expProgress = 0f,
                levelMax = false,
                totalFan = 0,
                money = 0,
                jewelFree = 0,
                jewelPaid = 0,
                jewelTotal = 0,
                actionPoint = 0,
                actionPointMax = 0,
                challengePoint = 0,
                competitionRemaining = 0,
                error = ""
            };
            try
            {
                var u = Campus.Common.User.UserDataManager.User;
                if (u != null) dto.userId = u.ServerUserId ?? "";

                var prof = Campus.Common.User.UserDataManager.UserProfile;
                if (prof != null)
                {
                    dto.name = prof.Name ?? "";
                    dto.exp = prof.Exp;
                    dto.totalFan = prof.TotalFanCount;
                }

                try
                {
                    var lvl = Campus.Common.User.UserDataManager.UserProducerLevelData;
                    if (lvl != null)
                    {
                        dto.producerLevel = lvl.Level;
                        try { dto.expToNext = lvl.GetExpToNextLevel(); } catch { }
                        try { dto.expProgress = lvl.GetExpProgress(); } catch { }
                        try { dto.levelMax = lvl.IsMaxLevel(); } catch { }
                    }
                }
                catch { }

                var balance = Campus.Common.User.UserDataManager.UserBalance;
                if (balance != null)
                {
                    dto.jewelFree = balance.FreeBalance;
                    dto.jewelPaid = balance.PaidBalance;
                    dto.jewelTotal = balance.TotalBalance;
                }

                try
                {
                    var items = Campus.Common.User.UserDataManager.UserItemList;
                    if (items != null) dto.money = items.GetMoneyQuantity();
                }
                catch { }

                try
                {
                    var ap = Campus.Common.User.UserDataManager.UserActionPoint;
                    if (ap != null)
                    {
                        dto.actionPoint = ap.Quantity;
                        dto.actionPointMax = ap.IsMax ? ap.Quantity : 0;
                    }
                }
                catch { }

                try
                {
                    var comp = Campus.Common.User.UserDataManager.UserCompetition;
                    if (comp != null) dto.competitionRemaining = (int)comp.RemainingPlayCount;
                }
                catch { }
            }
            catch (Exception e)
            {
                dto.error = "account read failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedAccountState = dto;
        }

        private void BuildItemList()
        {
            var dto = new ItemListDto
            {
                count = 0,
                money = 0,
                items = new List<ItemDto>(),
                error = ""
            };
            try
            {
                var list = Campus.Common.User.UserDataManager.UserItemList;
                if (list == null)
                {
                    dto.error = "UserItemList unavailable";
                    GakumasAutoPlugin.SharedItemList = dto;
                    return;
                }

                try { dto.money = list.GetMoneyQuantity(); } catch { }

                const int max = 400;
                var all = list.GetAll();
                if (all == null)
                {
                    dto.error = "UserItemList.GetAll() null";
                    GakumasAutoPlugin.SharedItemList = dto;
                    return;
                }
                foreach (var it in all)
                {
                    if (it == null) continue;
                    if (dto.items.Count >= max) break;
                    try
                    {
                        if (!it.IsValid()) continue;
                    }
                    catch { }

                    var d = new ItemDto
                    {
                        id = it.ItemId ?? "",
                        name = "",
                        type = "",
                        quantity = it.Quantity,
                        expiryTime = it.ExpiryTime,
                        rarity = ""
                    };
                    try
                    {
                        var master = it.Item ?? it.GetItem();
                        if (master != null)
                        {
                            d.name = SanitizeText(master.Name);
                            d.type = master.Type.ToString();
                            d.rarity = master.Rarity.ToString();
                        }
                    }
                    catch { }
                    dto.items.Add(d);
                }
                dto.count = dto.items.Count;
                if (dto.items.Count >= max) dto.error = "truncated at " + max;
            }
            catch (Exception e)
            {
                dto.error = "item list failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedItemList = dto;
        }
    }
}
