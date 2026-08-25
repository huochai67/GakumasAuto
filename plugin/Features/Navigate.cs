using System;
using System.Collections.Generic;
using System.Reflection;

namespace GakumasAuto
{
    public partial class AutoDriver
    {
        private static readonly Dictionary<string, string> ScreenAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "home", "HomeTop" },
            { "shop", "ShopTop" },
            { "jewel_shop", "ShopJewel" },
            { "jewel", "ShopJewel" },
            { "exchange", "ExchangeItemSelect" },
            { "exchange_item", "ExchangeItemSelect" },
            { "exchange_daily", "ExchangeDailyList" },
            { "present", "PresentTop" },
            { "gift", "PresentTop" },
            { "mission", "MissionTop" },
            { "work", "WorkTop" },
            { "produce", "ProduceTop" },
            { "pvp", "PvpRateTop" },
            { "contest", "PvpRateTop" },
            { "arena", "PvpRateTop" },
            { "gasha", "GashaAnimation" },
            { "item", "ItemTop" },
            { "profile", "ProfileTop" },
            { "story", "StoryMainPart" },
        };

        private object ScreenGoto(string raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "empty screen (use ScreenState name or alias: home/shop/produce/pvp/present/mission/exchange)";

            string name = raw.Trim();
            string mapped;
            if (ScreenAliases.TryGetValue(name, out mapped)) name = mapped;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase;
                var prop = typeof(Campus.ScreenState).GetProperty(name, flags);
                if (prop == null)
                    return "unknown screen: " + raw + " (alias or Campus.ScreenState name, e.g. HomeTop, ShopTop, ProduceTop, PvpRateTop, PresentTop)";

                var state = prop.GetValue(null) as Campus.ScreenState;
                if (state == null) return "ScreenState." + name + " is null";

                Campus.OutGame.OutGameTransitionUtility.To(state, true, false, null);
                return "opened " + name;
            }
            catch (Exception e)
            {
                return "screen_goto failed: " + e.Message;
            }
        }
    }
}
