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
        private void BuildDailyState()
        {
            var dto = new DailyStateDto
            {
                works = new List<WorkStatusDto>(),
                moneyUnreceived = 0,
                moneyUnreceivedElapsedSeconds = 0,
                moneyLastReceivedTime = 0,
                jewelFree = 0,
                jewelPaid = 0,
                jewelTotal = 0,
                error = ""
            };
            try
            {
                var money = Campus.Common.User.UserDataManager.UserMoney;
                if (money != null)
                {
                    dto.moneyUnreceived = money.UnreceivedTotalQuantity;
                    dto.moneyUnreceivedElapsedSeconds = money.UnreceivedTotalElapsedTimeSeconds;
                    dto.moneyLastReceivedTime = money.LastReceivedTime;
                    try { dto.moneyReceivable = money.IsReceivable(); } catch { }
                }

                var balance = Campus.Common.User.UserDataManager.UserBalance;
                if (balance != null)
                {
                    dto.jewelFree = balance.FreeBalance;
                    dto.jewelPaid = balance.PaidBalance;
                    dto.jewelTotal = balance.TotalBalance;
                }

                var works = Campus.Common.User.UserDataManager.UserWorkList;
                if (works != null)
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var types = new[]
                    {
                        Campus.Common.Proto.Client.Enums.WorkType.MiniLive,
                        Campus.Common.Proto.Client.Enums.WorkType.LiveStreaming
                    };
                    foreach (var wt in types)
                    {
                        var w = new WorkStatusDto
                        {
                            type = wt.ToString(),
                            name = "",
                            state = "Unknown",
                            characterId = "",
                            characterName = "",
                            level = 0,
                            durationMinutes = 0,
                            startedTime = 0,
                            remainingSeconds = 0,
                            skipCount = 0,
                            totalFinishCount = 0,
                            fixedIsExcellent = false
                        };
                        try
                        {
                            var uw = works.FindById(wt);
                            if (uw == null)
                            {
                                w.state = "Acceptable";
                            }
                            else
                            {
                                w.state = works.IsWorking(wt) ? "Working" : "Completed";
                                w.characterId = uw.CharacterId ?? "";
                                w.level = uw.Level;
                                w.durationMinutes = uw.DurationMinutes;
                                w.startedTime = uw.StartedTime;
                                w.skipCount = uw.SkipCount;
                                w.totalFinishCount = uw.TotalFinishCount;
                                w.fixedIsExcellent = uw.FixedIsExcellent;
                                long finishAt = 0;
                                try { finishAt = uw.GetFinishTime(); } catch { }
                                if (finishAt <= 0)
                                    finishAt = uw.StartedTime + uw.DurationMinutes * 60L;
                                w.remainingSeconds = finishAt > now ? finishAt - now : 0;
                                var master = uw.Work;
                                if (master != null) w.name = master.Name ?? "";
                                var ch = uw.GetCharacter();
                                if (ch != null) w.characterName = ch.Name ?? "";
                            }
                        }
                        catch { }
                        dto.works.Add(w);
                    }
                }
                else
                {
                    dto.error = "UserWorkList unavailable";
                }
            }
            catch (Exception e)
            {
                dto.error = "daily read failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedDailyState = dto;
        }
    }
}
