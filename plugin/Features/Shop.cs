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
        private void BuildShopItems()
        {
            var dto = new ShopListDto { shopScreenOpen = false, shops = new List<string>(), items = new List<ShopItemDto>(), error = "" };
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.Shop.ShopTopScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    dto.error = "shop top screen not open";
                }
                else
                {
                    dto.shopScreenOpen = true;
                    var model = presenters[0]._model;
                    if (model == null)
                    {
                        dto.error = "shop model null";
                    }
                    else
                    {
                        var shops = model.Shops;
                        if (shops != null)
                        {
                            for (int i = 0; i < 60; i++)
                            {
                                Campus.Common.Data.IShop s = null;
                                try { s = shops[i]; } catch { break; }
                                if (s == null) break;
                                try
                                {
                                    dto.shops.Add(s.Id + "|" + s.Name + "|" + s.ShopType + "|" + s.EndTime + "|" + s.NextResetTime);
                                }
                                catch { }
                            }
                        }

                        var items = model.ShopItems;
                        if (items != null)
                        {
                            for (int i = 0; i < 300; i++)
                            {
                                Campus.Common.Proto.Client.Api.ShopItem api = null;
                                try { api = items[i]; } catch { break; }
                                if (api == null) break;
                                try
                                {
                                    var d = new ShopItemDto
                                    {
                                        id = api.Id ?? "",
                                        shopId = api.ShopId ?? "",
                                        name = "",
                                        assetId = "",
                                        price = 0,
                                        totalJewelQuantity = 0,
                                        paidOnlyJewelQuantity = 0,
                                        purchaseLimit = 0,
                                        purchasedCount = api.PurchasedCount,
                                        purchasedOrder = api.PurchasedOrder,
                                        unlocked = api.Unlocked,
                                        isSoldOut = api.IsSoldOut,
                                        noti = api.Noti,
                                        isFree = false,
                                        endTime = api.EndTime,
                                        nextResetTime = api.NextResetTime,
                                        order = api.Order,
                                        consumptionResource = "",
                                        consumptionQuantity = 0,
                                        labelTypes = new List<string>()
                                    };
                                    var master = api.Master;
                                    if (master != null)
                                    {
                                        d.name = master.Name ?? "";
                                        d.assetId = master.AssetId ?? "";
                                        d.price = master.Price;
                                        d.totalJewelQuantity = master.TotalJewelQuantity;
                                        d.paidOnlyJewelQuantity = master.PaidOnlyJewelQuantity;
                                        d.purchaseLimit = master.PurchaseLimit;
                                        d.isFree = master.IsFree;
                                        d.consumptionResource = master.ConsumptionResourceType + ":" + (master.ConsumptionResourceId ?? "");
                                        d.consumptionQuantity = master.ConsumptionResourceQuantity;
                                    }
                                    var labels = api.LabelTypes;
                                    if (labels != null)
                                    {
                                        for (int k = 0; k < 20; k++)
                                        {
                                            Campus.Common.Proto.Client.Enums.ShopItemLabelType lbl;
                                            try { lbl = labels[k]; } catch { break; }
                                            d.labelTypes.Add(lbl.ToString());
                                        }
                                    }
                                    dto.items.Add(d);
                                }
                                catch { }
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                dto.error = "shop read failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedShopList = dto;
        }
    }
}
