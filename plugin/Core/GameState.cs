using System;
using UnityEngine;

namespace GakumasAuto
{
    public partial class AutoDriver
    {
        private void WatchState()
        {
            var log = GakumasAutoPlugin.SharedLog;

            string serverUserId = "";
            string userName = "";
            try
            {
                var u = Campus.Common.User.UserDataManager.User;
                if (u != null)
                {
                    serverUserId = u.ServerUserId ?? "";
                    if (serverUserId.Length > 0)
                    {
                        var prof = Campus.Common.User.UserDataManager.UserProfile;
                        userName = prof != null ? (prof.Name ?? "") : "";
                    }
                }
            }
            catch { }

            if (serverUserId != _lastServerUserId)
            {
                _lastServerUserId = serverUserId;
                if (serverUserId.Length > 0)
                {
                    log.LogInfo($"LOGIN: ServerUserId={serverUserId}" +
                                (userName.Length > 0 ? $" Name={userName}" : ""));
                }
                else if (_watchStarted)
                {
                    log.LogInfo("logout/not-logged-in (ServerUserId empty)");
                }
            }

            bool loading = false;
            try
            {
                var lm = Campus.Common.LoadingManager.Instance;
                loading = lm != null && Campus.Common.LoadingManager.IsActive;
            }
            catch { }
            if (loading != _lastLoading || !_watchStarted)
            {
                _lastLoading = loading;
                log.LogInfo($"LOADING: {(loading ? "active" : "idle")}");
            }

            if (!_watchStarted)
            {
                try
                {
                    var mi = Campus.Common.MaintenanceInfoHolder.Instance.MaintenanceInfo;
                    log.LogInfo($"MAINTENANCE: {(mi == null ? "(null/not-fetched)" : mi.ToString())}");
                }
                catch { }
            }

            _watchStarted = true;
        }

        private void WatchScreen()
        {
            var log = GakumasAutoPlugin.SharedLog;
            string topLayer = "";
            try
            {
                var slm = Campus.Common.ScreenLayerManager.Instance;
                if (slm != null && slm.HasActive)
                {
                    var top = slm.GetTopLayer();
                    if (top != null)
                    {
                        var t = top.GetType();
                        topLayer = t != null ? t.Name : "?";
                    }
                }
            }
            catch { }

            if (topLayer != _lastTopLayer)
            {
                _lastTopLayer = topLayer;
                log.LogInfo($"SCREEN: top layer = {(topLayer.Length > 0 ? topLayer : "(none)")}");
            }
        }

        private void BuildState()
        {
            var dto = new StateDto
            {
                userId = "",
                name = "",
                topLayer = "(none)",
                layersActive = false,
                loading = false,
                maintenance = "",
                screen = ""
            };
            try
            {
                var u = Campus.Common.User.UserDataManager.User;
                if (u != null) dto.userId = u.ServerUserId ?? "";
                var prof = Campus.Common.User.UserDataManager.UserProfile;
                if (prof != null) dto.name = prof.Name ?? "";

                var slm = Campus.Common.ScreenLayerManager.Instance;
                if (slm != null)
                {
                    dto.layersActive = slm.HasActive;
                    var tl = slm.GetTopLayer();
                    if (tl != null)
                    {
                        var t = tl.GetType();
                        dto.topLayer = t != null ? t.Name : "?";
                    }
                }

                var lm = Campus.Common.LoadingManager.Instance;
                if (lm != null) dto.loading = Campus.Common.LoadingManager.IsActive;

                var mi = Campus.Common.MaintenanceInfoHolder.Instance.MaintenanceInfo;
                dto.maintenance = mi == null ? "" : mi.ToString();
                dto.screen = DetectScreen();
            }
            catch (Exception e)
            {
                dto.maintenance = $"state read failed: {e.Message}";
            }
            GakumasAutoPlugin.SharedState = dto;
        }

        private static bool AnyActive<T>() where T : UnityEngine.Object
        {
            try
            {
                var arr = UnityEngine.Object.FindObjectsOfType<T>();
                return arr != null && arr.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool GoNamedActive(string name)
        {
            try
            {
                var go = GameObject.Find(name);
                return go != null && go.activeInHierarchy;
            }
            catch
            {
                return false;
            }
        }

        private string DetectScreen()
        {
            try
            {
                if (AnyActive<Campus.InGame.Exam.ExamScreenPresenter>()) return "exam";
                if (AnyActive<Campus.InGame.Schedule.ScheduleShopScreenPresenter>()) return "produce_shop";
                if (AnyActive<Campus.InGame.ScheduleBusinessScreenPresenter>()) return "produce_outing";
                if (AnyActive<Campus.InGame.ScheduleCustomizeScreenPresenter>()) return "produce_customize";
                if (AnyActive<Campus.OutGame.PvpRateTopScreenPresenter>()) return "pvp";
                if (AnyActive<Campus.OutGame.PresentTopScreenPresenter>()) return "present";
                if (AnyActive<Campus.OutGame.MissionTopScreenPresenter>()) return "mission";
                if (AnyActive<Campus.OutGame.Shop.ShopTopScreenPresenter>()) return "shop";
                if (AnyActive<Campus.OutGame.ProduceTopScreenPresenter>()) return "produce";
                if (AnyActive<Campus.OutGame.ExchangeItemSelectScreenPresenter>()
                    || AnyActive<Campus.OutGame.ExchangeItemListScreenPresenter>()
                    || AnyActive<Campus.OutGame.ExchangeDailyListScreenPresenter>())
                    return "exchange";
                if (GoNamedActive("WorkStateList") || GoNamedActive("WorkTop")) return "work";
                if (GoNamedActive("HomeFooter"))
                {
                    if (GoNamedActive("GashaPage")) return "gasha";
                    if (GoNamedActive("PvpPage")) return "pvp";
                    if (GoNamedActive("StoryPage")) return "story";
                    if (GoNamedActive("IdolPage")) return "card";
                    return "home";
                }
            }
            catch { }
            return "";
        }
    }
}
