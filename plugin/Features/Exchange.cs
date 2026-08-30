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
        private static Campus.Common.Proto.Client.Enums.ExchangeType ParseExchangeType(string raw)
        {
            if (raw == null) return Campus.Common.Proto.Client.Enums.ExchangeType.Item;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "1":
                case "daily":
                    return Campus.Common.Proto.Client.Enums.ExchangeType.Daily;
                case "3":
                case "event":
                    return Campus.Common.Proto.Client.Enums.ExchangeType.Event;
                default:
                    return Campus.Common.Proto.Client.Enums.ExchangeType.Item;
            }
        }

        private static void AddExchangeShop(List<ExchangeShopDto> dst, string id, string name, string type, bool isNew, bool isLocked, bool isMaintenance)
        {
            if (string.IsNullOrEmpty(id) && string.IsNullOrEmpty(name)) return;
            for (int i = 0; i < dst.Count; i++)
            {
                if (dst[i].id == id && dst[i].name == name) return;
            }
            dst.Add(new ExchangeShopDto
            {
                id = id ?? "",
                name = name ?? "",
                type = type ?? "",
                isNew = isNew,
                isLocked = isLocked,
                isMaintenance = isMaintenance
            });
        }

        private void BuildExchangeList()
        {
            var dto = new ExchangeListDto
            {
                selectScreenOpen = false,
                listScreenOpen = false,
                currentId = "",
                exchanges = new List<ExchangeShopDto>(),
                error = ""
            };
            try
            {
                var select = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.ExchangeItemSelectScreenPresenter>();
                if (select != null && select.Length > 0)
                {
                    dto.selectScreenOpen = true;
                    var list = select[0]._list;
                    if (list != null)
                    {
                        for (int i = 0; i < 80; i++)
                        {
                            Campus.Common.IMixListItemModel raw = null;
                            try { raw = list.GetItem(i); }
                            catch { break; }
                            if (raw == null) continue;
                            var shop = raw.TryCast<Campus.OutGame.ExchangeSelectListExchangeItemModel>();
                            if (shop == null) continue;
                            AddExchangeShop(dto.exchanges, shop.ExchangeId, shop.Name, shop.ExchangeType.ToString(), shop.IsNew, shop.IsLocked, shop.IsMaintenance);
                        }
                    }
                }

                var itemList = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.ExchangeItemListScreenPresenter>();
                if (itemList != null && itemList.Length > 0 && itemList[0]._model != null)
                {
                    dto.listScreenOpen = true;
                    var model = itemList[0]._model;
                    dto.currentId = model.CurrentExchangeId ?? "";
                    var infos = model.ExchangeInfoList;
                    if (infos != null)
                    {
                        for (int i = 0; i < 80; i++)
                        {
                            Campus.Common.Proto.Client.Api.ExchangeInfo info = null;
                            try { info = infos[i]; }
                            catch { break; }
                            if (info == null) break;
                            AddExchangeShop(dto.exchanges, info.Id, info.Name, info.ExchangeType.ToString(), false, !info.Unlocked, false);
                        }
                    }
                }

                var dailyList = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.ExchangeDailyListScreenPresenter>();
                if (dailyList != null && dailyList.Length > 0)
                {
                    dto.listScreenOpen = true;
                    try
                    {
                        var model = dailyList[0]._model;
                        if (model != null)
                        {
                            dto.currentId = model.CurrentExchangeId ?? "";
                            var infos = model.ExchangeInfoList;
                            if (infos != null)
                            {
                                for (int i = 0; i < 80; i++)
                                {
                                    Campus.Common.Proto.Client.Api.ExchangeInfo info = null;
                                    try { info = infos[i]; }
                                    catch { break; }
                                    if (info == null) break;
                                    AddExchangeShop(dto.exchanges, info.Id, info.Name, info.ExchangeType.ToString(), false, !info.Unlocked, false);
                                }
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        if (dto.error.Length == 0) dto.error = "daily list read failed: " + e.Message;
                    }
                }

                if (!dto.selectScreenOpen && !dto.listScreenOpen)
                    dto.error = "exchange screen not open";
            }
            catch (Exception e)
            {
                dto.error = "exchange list failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedExchangeList = dto;
        }

        private object ExchangeEnter(string typeRaw, string exchangeId)
        {
            try
            {
                if (string.IsNullOrEmpty(exchangeId))
                {
                    Campus.ScreenState target = Campus.ScreenState.ExchangeItemSelect;
                    var parsed = ParseExchangeType(typeRaw);
                    if (parsed == Campus.Common.Proto.Client.Enums.ExchangeType.Daily)
                        target = Campus.ScreenState.ExchangeDailyList;
                    Campus.OutGame.OutGameTransitionUtility.To(target, true, false, null);
                    return "opened " + (target != null ? target.ToString() : "?");
                }

                var et = ParseExchangeType(typeRaw);
                var link = new Campus.OutGame.ExchangeListLinkData(et, exchangeId);
                Campus.OutGame.OutGameTransitionUtility.To(link);
                return "entered " + et + " " + exchangeId;
            }
            catch (Exception e)
            {
                return "exchange_enter failed: " + e.Message;
            }
        }

        private void FillExchangeProducts(Campus.Common.Proto.Client.Api.ExchangeInfo info, ExchangeProductListDto dto)
        {
            if (info == null) return;
            dto.currentId = info.Id ?? dto.currentId;
            dto.currentName = info.Name ?? "";
            dto.manualResettable = info.ManualResettable;
            dto.resetCount = info.ResetCount;
            var items = info.Items;
            if (items == null) return;
            for (int i = 0; i < 200; i++)
            {
                Campus.Common.Proto.Client.Api.ExchangeItem it = null;
                try { it = items[i]; } catch { break; }
                if (it == null) break;
                var d = new ExchangeProductDto
                {
                    id = it.Id ?? "",
                    name = it.Name ?? "",
                    recommend = it.IsShowRecommendLabel,
                    unlocked = it.Unlocked,
                    exchangeLimit = it.ExchangeLimit,
                    exchangedCount = it.ExchangedCount,
                    price = 0,
                    consumptionResource = "",
                    rewardType = "",
                    rewardId = "",
                    rewardQuantity = 0,
                    order = it.Order
                };
                try
                {
                    d.price = it.GetCurrentConsumptionResourceQuantity();
                    d.consumptionResource = info.ConsumptionResourceType + ":" + (info.ConsumptionResourceId ?? "");
                }
                catch { }
                try
                {
                    d.rewardType = it.ResourceType.ToString();
                    d.rewardId = it.ResourceId ?? "";
                    d.rewardQuantity = it.Quantity;
                }
                catch { }
                dto.items.Add(d);
            }
        }

        private void BuildExchangeItems()
        {
            var dto = new ExchangeProductListDto
            {
                listScreenOpen = false,
                currentId = "",
                currentName = "",
                manualResettable = false,
                resetCount = 0,
                items = new List<ExchangeProductDto>(),
                error = ""
            };
            try
            {
                var daily = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.ExchangeDailyListScreenPresenter>();
                if (daily != null && daily.Length > 0 && daily[0]._model != null)
                {
                    dto.listScreenOpen = true;
                    FillExchangeProducts(daily[0]._model.CurrentExchangeInfo, dto);
                }

                var itemList = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.ExchangeItemListScreenPresenter>();
                if (itemList != null && itemList.Length > 0 && itemList[0]._model != null)
                {
                    dto.listScreenOpen = true;
                    FillExchangeProducts(itemList[0]._model.CurrentExchangeInfo, dto);
                }

                if (!dto.listScreenOpen)
                    dto.error = "exchange list screen not open (call exchange_enter first)";
                else if (dto.items.Count == 0)
                    dto.error = "no items on current exchange tab";
            }
            catch (Exception e)
            {
                dto.error = "exchange items failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedExchangeItems = dto;
        }
    }
}
