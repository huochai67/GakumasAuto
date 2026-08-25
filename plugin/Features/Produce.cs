using System;
using System.Collections.Generic;
using Campus.Common.Proto.Client.Enums;

namespace GakumasAuto
{
    public partial class AutoDriver
    {
        private object ProduceEnter()
        {
            try
            {
                Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.ProduceTop, true, false, null);
                return "opened ProduceTop";
            }
            catch (Exception e)
            {
                return "produce_enter failed: " + e.Message;
            }
        }

        private static string ProduceStepKind(ProduceStepType t)
        {
            switch (t)
            {
                case ProduceStepType.AuditionMid1:
                case ProduceStepType.AuditionMid2:
                case ProduceStepType.AuditionFinal:
                    return "exam";
                case ProduceStepType.Shop:
                    return "shop";
                case ProduceStepType.Business:
                case ProduceStepType.EventBusiness:
                case ProduceStepType.Present:
                case ProduceStepType.FanPresent:
                case ProduceStepType.EventActivity:
                    return "outing";
                case ProduceStepType.Customize:
                    return "customize";
                case ProduceStepType.Refresh:
                    return "refresh";
                case ProduceStepType.Interval:
                    return "interval";
                default:
                    if (t.ToString().IndexOf("Lesson", StringComparison.Ordinal) >= 0) return "lesson";
                    if (t.ToString().IndexOf("Event", StringComparison.Ordinal) >= 0) return "event";
                    return t == ProduceStepType.Unknown ? "" : "other";
            }
        }

        private void BuildProduceState()
        {
            var dto = new ProduceStateDto
            {
                inProgress = false,
                inProgressStep = false,
                produceId = "",
                produceName = "",
                produceType = "",
                produceGroupId = "",
                produceGroupName = "",
                characterId = "",
                characterName = "",
                idolCardId = "",
                idolCardName = "",
                producePoint = 0,
                stamina = 0,
                maxStamina = 0,
                produceScore = 0,
                status = "",
                stepType = "",
                stepKind = "",
                stepId = "",
                stepNumber = 0,
                week = 0,
                day = 0,
                currentOptions = new List<string>(),
                error = ""
            };
            try
            {
                var p = Campus.Common.User.UserDataManager.UserProduceProgress;
                if (p == null)
                {
                    dto.error = "no produce in progress";
                    GakumasAutoPlugin.SharedProduceState = dto;
                    return;
                }

                dto.inProgress = p.IsInProgress;
                dto.inProgressStep = p.InProgressStep;
                dto.produceId = p.ProduceId ?? "";
                dto.produceGroupId = p.ProduceGroupId ?? "";
                dto.characterId = p.CharacterId ?? "";
                dto.idolCardId = p.IdolCardId ?? "";
                dto.producePoint = p.ProducePoint;
                dto.stamina = p.Stamina;
                dto.maxStamina = p.MaxStamina;
                dto.produceScore = p.ProduceScore;
                dto.status = p.Status.ToString();
                dto.stepType = p.StepType.ToString();
                dto.stepKind = ProduceStepKind(p.StepType);
                dto.stepId = p.StepId ?? "";
                dto.stepNumber = p.StepNumber;

                try
                {
                    var week = p.GetWeekData();
                    // Il2Cpp ValueTuple sometimes yields garbage; fall back to step index.
                    if (week.Item1 > 0 && week.Item1 < 20 && week.Item2 > 0 && week.Item2 < 20)
                    {
                        dto.week = week.Item1;
                        dto.day = week.Item2;
                    }
                }
                catch { }
                if (dto.week == 0 && dto.stepNumber > 0)
                {
                    dto.week = (dto.stepNumber - 1) / 6 + 1;
                    dto.day = (dto.stepNumber - 1) % 6 + 1;
                }

                try
                {
                    var produce = p.GetProduce();
                    if (produce != null)
                    {
                        dto.produceName = produce.Name ?? "";
                        dto.produceType = produce.ProduceType.ToString();
                    }
                }
                catch { }

                try
                {
                    var group = p.GetProduceGroup();
                    if (group != null)
                    {
                        dto.produceGroupName = group.Name ?? "";
                        if (dto.produceType.Length == 0) dto.produceType = group.Type.ToString();
                    }
                }
                catch { }

                try
                {
                    var ch = p.GetCharacter();
                    if (ch != null) dto.characterName = ch.Name ?? "";
                }
                catch { }

                try
                {
                    var card = p.GetIdolCard();
                    if (card != null) dto.idolCardName = card.Name ?? "";
                }
                catch { }

                try
                {
                    var cur = p.CurrentSchedule;
                    if (cur != null)
                    {
                        dto.stepNumber = cur.StepNumber;
                        var types = cur.StepTypeList;
                        if (types != null)
                        {
                            for (int i = 0; i < 16; i++)
                            {
                                ProduceStepType st;
                                try { st = types[i]; }
                                catch { break; }
                                dto.currentOptions.Add(st + "|" + ProduceStepKind(st));
                            }
                        }
                    }
                }
                catch { }
            }
            catch (Exception e)
            {
                dto.error = "produce read failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedProduceState = dto;
        }

        private void BuildProduceSchedule()
        {
            var dto = new ProduceScheduleListDto
            {
                inProgress = false,
                currentStepNumber = 0,
                days = new List<ProduceScheduleDto>(),
                error = ""
            };
            try
            {
                var p = Campus.Common.User.UserDataManager.UserProduceProgress;
                if (p == null)
                {
                    dto.error = "no produce in progress";
                    GakumasAutoPlugin.SharedProduceSchedule = dto;
                    return;
                }

                dto.inProgress = p.IsInProgress;
                dto.currentStepNumber = p.StepNumber;

                var list = Campus.Common.User.UserDataManager.UserProduceProgressScheduleList;
                if (list == null)
                {
                    dto.error = "schedule list unavailable";
                    GakumasAutoPlugin.SharedProduceSchedule = dto;
                    return;
                }

                var all = list.GetAll();
                if (all == null)
                {
                    dto.error = "schedule GetAll() null";
                    GakumasAutoPlugin.SharedProduceSchedule = dto;
                    return;
                }
                foreach (var s in all)
                {
                    if (s == null) continue;
                    var day = new ProduceScheduleDto
                    {
                        stepNumber = s.StepNumber,
                        selectedStepType = s.SelectedStepType.ToString(),
                        stepTypes = new List<string>(),
                        auditionRank = s.AuditionRank
                    };
                    try
                    {
                        var types = s.StepTypeList;
                        if (types != null)
                        {
                            for (int k = 0; k < 16; k++)
                            {
                                ProduceStepType st;
                                try { st = types[k]; }
                                catch { break; }
                                day.stepTypes.Add(st + "|" + ProduceStepKind(st));
                            }
                        }
                    }
                    catch { }
                    dto.days.Add(day);
                }
            }
            catch (Exception e)
            {
                dto.error = "produce schedule failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedProduceSchedule = dto;
        }

        private void BuildProduceShop()
        {
            var dto = new ProduceShopListDto
            {
                inProgress = false,
                shopScreenOpen = false,
                producePoint = 0,
                items = new List<ProduceShopItemDto>(),
                error = ""
            };
            try
            {
                try
                {
                    var shops = UnityEngine.Object.FindObjectsOfType<Campus.InGame.Schedule.ScheduleShopScreenPresenter>();
                    dto.shopScreenOpen = shops != null && shops.Length > 0;
                }
                catch { }

                var p = Campus.Common.User.UserDataManager.UserProduceProgress;
                if (p != null)
                {
                    dto.inProgress = p.IsInProgress;
                    dto.producePoint = p.ProducePoint;
                }

                var list = Campus.Common.User.UserDataManager.UserProduceProgressShopList;
                if (list == null)
                {
                    dto.error = dto.shopScreenOpen ? "" : "produce shop list unavailable";
                    GakumasAutoPlugin.SharedProduceShop = dto;
                    return;
                }
                var all = list.GetAll();
                if (all != null)
                {
                    foreach (var s in all)
                    {
                        if (s == null) continue;
                        var d = new ProduceShopItemDto
                        {
                            position = s.PositionNumber,
                            name = s.Name ?? "",
                            resourceId = s.ResourceId ?? "",
                            resourceType = s.ResourceType.ToString(),
                            price = 0,
                            nextPrice = s.NextPrice,
                            purchased = s.Purchased,
                            locked = s.Lock,
                            upgradeCount = s.UpgradeCount,
                            discount = false
                        };
                        try { d.price = s.GetCurrentPrice(); } catch { d.price = s.Price; }
                        try { d.discount = s.IsDiscount(); } catch { }
                        dto.items.Add(d);
                    }
                }
                if (dto.items.Count == 0 && !dto.shopScreenOpen && (p == null || !p.IsInProgress))
                    dto.error = "not in produce shop";
            }
            catch (Exception e)
            {
                dto.error = "produce shop failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedProduceShop = dto;
        }

        private void BuildProduceOuting()
        {
            var dto = new ProduceOutingDto
            {
                outingScreenOpen = false,
                options = new List<ProduceOutingOptionDto>(),
                error = ""
            };
            try
            {
                try
                {
                    var ps = UnityEngine.Object.FindObjectsOfType<Campus.InGame.ScheduleBusinessScreenPresenter>();
                    dto.outingScreenOpen = ps != null && ps.Length > 0;
                }
                catch { }

                var list = Campus.Common.User.UserDataManager.UserProduceProgressBusinessList;
                if (list == null)
                {
                    dto.error = "outing list unavailable";
                    GakumasAutoPlugin.SharedProduceOuting = dto;
                    return;
                }
                var all = list.GetAll();
                if (all != null)
                {
                    foreach (var b in all)
                    {
                        if (b == null) continue;
                        dto.options.Add(new ProduceOutingOptionDto
                        {
                            type = b.BusinessType.ToString(),
                            name = b.Name ?? "",
                            stamina = b.Stamina,
                            producePoint = b.ProducePoint,
                            fanVoteValue = b.FanVoteValue,
                            number = b.Number
                        });
                    }
                }
                if (dto.options.Count == 0 && !dto.outingScreenOpen)
                    dto.error = "no outing options (enter a produce outing step)";
            }
            catch (Exception e)
            {
                dto.error = "produce outing failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedProduceOuting = dto;
        }

        private void BuildProduceCards()
        {
            var dto = new ProduceCardListDto
            {
                customizeScreenOpen = false,
                remainingCustomizeCount = 0,
                cards = new List<ProduceCardDto>(),
                error = ""
            };
            try
            {
                try
                {
                    var ps = UnityEngine.Object.FindObjectsOfType<Campus.InGame.ScheduleCustomizeScreenPresenter>();
                    dto.customizeScreenOpen = ps != null && ps.Length > 0;
                }
                catch { }

                var list = Campus.Common.User.UserDataManager.UserProduceProgressProduceCardList;
                if (list == null)
                {
                    dto.error = "produce card list unavailable";
                    GakumasAutoPlugin.SharedProduceCards = dto;
                    return;
                }
                var all = list.GetAll();
                if (all != null)
                {
                    foreach (var c in all)
                    {
                        if (c == null) continue;
                        var d = new ProduceCardDto
                        {
                            number = c.Number,
                            produceCardId = c.ProduceCardId ?? "",
                            name = "",
                            upgradeCount = c.UpgradeCount,
                            customizing = c.Customizing,
                            deleted = c.Deleted,
                            canCustomize = false
                        };
                        try { d.canCustomize = c.CanCustomize(); } catch { }
                        try
                        {
                            var master = c.GetProduceCard();
                            if (master != null) d.name = master.Name ?? "";
                        }
                        catch { }
                        dto.cards.Add(d);
                    }
                }
                if (dto.cards.Count == 0)
                    dto.error = "no produce cards (start a produce run)";
            }
            catch (Exception e)
            {
                dto.error = "produce cards failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedProduceCards = dto;
        }
    }
}
