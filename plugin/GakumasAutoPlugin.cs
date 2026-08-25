// GakumasAuto v2 — in-process automation plugin (BepInEx 6 / Il2CppInterop).
//
// IL2CPP safety rule: MonoBehaviour methods MUST NOT expose managed types
// (DTOs, StringBuilder, List<T>) in their signatures. Results route through
// static fields on this class.
//
// Hotkeys: F8 ADV auto-advance, F9 ADV fast-forward, F10 layout dump.
// Command channel: <BepInEx>\gakumas-ui-cmd.json -> gakumas-ui-resp.json
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace GakumasAuto
{
    [BepInPlugin("dev.gakumas.auto", "Gakumas Auto", "2.2.1")]
    public class GakumasAutoPlugin : BasePlugin
    {
        internal static ManualLogSource SharedLog;

        internal static LayoutRespDto SharedLayoutResp;
        internal static Layout2Dto SharedLayout2;
        internal static StateDto SharedState;
        internal static ScreenshotDto SharedScreenshot;
        internal static FindRespDto SharedFindResp;
        internal static DailyStateDto SharedDailyState;
        internal static ShopListDto SharedShopList;
        internal static ExamStateDto SharedExamState;
        internal static ExchangeListDto SharedExchangeList;
        internal static MissionListDto SharedMissionList;
        internal static AccountStateDto SharedAccountState;
        internal static ItemListDto SharedItemList;
        internal static GiftListDto SharedGiftList;
        internal static PvpStateDto SharedPvpState;
        internal static ProduceStateDto SharedProduceState;
        internal static ProduceScheduleListDto SharedProduceSchedule;
        internal static ProduceShopListDto SharedProduceShop;
        internal static ProduceOutingDto SharedProduceOuting;
        internal static ProduceCardListDto SharedProduceCards;
        internal static List<LayoutNodeDto> SharedNodes;
        internal static List<FindMatchDto> SharedFindMatches;

        public override void Load()
        {
            SharedLog = Log;
            Log.LogInfo("GakumasAuto v2.2.1 loaded. F8/F9 = ADV, F10 = layout capture");
            Log.LogInfo("Command channel: <BepInEx>\\gakumas-ui-cmd.json");

            try
            {
                var driver = AddComponent<AutoDriver>();
                driver.Attach();
                Log.LogInfo("AutoDriver attached; overlay AUTOMATION (top-left)");
            }
            catch (Exception e)
            {
                Log.LogError($"failed to attach AutoDriver: {e}");
            }
        }
    }
}
