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
        private static Campus.Common.Proto.Client.Enums.MissionCategory ParseMissionCategory(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Campus.Common.Proto.Client.Enums.MissionCategory.Unknown;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "daily": return Campus.Common.Proto.Client.Enums.MissionCategory.Daily;
                case "weekly": return Campus.Common.Proto.Client.Enums.MissionCategory.Weekly;
                case "normal": return Campus.Common.Proto.Client.Enums.MissionCategory.Normal;
                case "special": return Campus.Common.Proto.Client.Enums.MissionCategory.Special;
                case "event": return Campus.Common.Proto.Client.Enums.MissionCategory.Event;
                case "achievement": return Campus.Common.Proto.Client.Enums.MissionCategory.Achievement;
                case "maintask":
                case "main_task":
                case "main": return Campus.Common.Proto.Client.Enums.MissionCategory.MainTask;
                default:
                    try
                    {
                        return (Campus.Common.Proto.Client.Enums.MissionCategory)System.Enum.Parse(
                            typeof(Campus.Common.Proto.Client.Enums.MissionCategory), raw, true);
                    }
                    catch
                    {
                        return Campus.Common.Proto.Client.Enums.MissionCategory.Unknown;
                    }
            }
        }

        private static string MissionStateOf(
            Campus.Common.Proto.Client.Master.Mission m,
            Campus.Common.Proto.Client.Transaction.UserMission um)
        {
            try
            {
                if (m != null && Campus.Common.User.UserDataManager.IsMaxPointMission(m)) return "MaxPoint";
            }
            catch { }

            bool unlocked = um != null && um.IsUnlock;
            if (um == null)
            {
                try { if (m != null && m.IsReceivable()) return "Receivable"; } catch { }
                return "InProgress";
            }
            if (!unlocked) return "Lock";

            try { if (m != null && m.IsAllClearAndReceived()) return "Cleared"; } catch { }
            try { if (m != null && (m.IsReceivable() || m.IsAllClear())) return "Receivable"; } catch { }
            return "InProgress";
        }

        private static void BumpMissionSummary(Dictionary<string, MissionCategorySummaryDto> map, string category, string state)
        {
            MissionCategorySummaryDto s;
            if (!map.TryGetValue(category, out s))
            {
                s = new MissionCategorySummaryDto
                {
                    category = category,
                    total = 0,
                    receivable = 0,
                    inProgress = 0,
                    locked = 0,
                    cleared = 0,
                    maxPoint = 0
                };
                map[category] = s;
            }
            s.total++;
            if (state == "Receivable") s.receivable++;
            else if (state == "InProgress") s.inProgress++;
            else if (state == "Lock") s.locked++;
            else if (state == "Cleared") s.cleared++;
            else if (state == "MaxPoint") s.maxPoint++;
        }

        private void BuildMissionList(string categoryFilter)
        {
            var dto = new MissionListDto
            {
                filter = categoryFilter ?? "",
                summary = new List<MissionCategorySummaryDto>(),
                missions = new List<MissionDto>(),
                error = ""
            };
            try
            {
                var filterCat = ParseMissionCategory(categoryFilter);
                if (!string.IsNullOrEmpty(categoryFilter) && filterCat == Campus.Common.Proto.Client.Enums.MissionCategory.Unknown)
                {
                    dto.error = "unknown category: " + categoryFilter + " (Daily|Weekly|Normal|Special|Event|Achievement|MainTask)";
                    GakumasAutoPlugin.SharedMissionList = dto;
                    return;
                }

                var master = Campus.Common.Master.MasterManager.MissionMaster;
                if (master == null)
                {
                    dto.error = "MissionMaster unavailable";
                    GakumasAutoPlugin.SharedMissionList = dto;
                    return;
                }

                var all = master.GetAllWithSortByKey(Campus.Common.Master.MissionSortType.Id_Asc);
                if (all == null)
                {
                    dto.error = "MissionMaster empty";
                    GakumasAutoPlugin.SharedMissionList = dto;
                    return;
                }

                var users = Campus.Common.User.UserDataManager.UserMissionList;

                var summaryMap = new Dictionary<string, MissionCategorySummaryDto>();
                const int maxMissions = 800;
                int n = all.Count;
                for (int i = 0; i < n && dto.missions.Count < maxMissions; i++)
                {
                    Campus.Common.Proto.Client.Master.Mission m = null;
                    try { m = all[i]; } catch { break; }
                    if (m == null) continue;

                    try { if (!m.IsValid()) continue; } catch { }
                    try { if (Campus.Common.User.UserDataManager.IsNotReleaseDailyReleaseMission(m)) continue; } catch { }

                    var cat = m.Category;
                    if (filterCat != Campus.Common.Proto.Client.Enums.MissionCategory.Unknown && cat != filterCat)
                        continue;

                    string catName = cat.ToString();
                    Campus.Common.Proto.Client.Transaction.UserMission um = null;
                    try { if (users != null) um = users.FindById(m.Id); } catch { }

                    int threshold = 0;
                    try { threshold = m.GetMaxProgressThreshold(); } catch { }
                    if (threshold <= 0) threshold = m.TargetValue;

                    string name = "";
                    try { name = m.GetName(threshold) ?? ""; } catch { }
                    if (string.IsNullOrEmpty(name)) name = m.Name ?? "";

                    long progress = 0;
                    if (um != null) progress = um.Progress;
                    else
                    {
                        try { progress = m.GetProgressCount(); } catch { }
                    }

                    bool receivable = false;
                    int receivableCount = 0;
                    bool allClear = false;
                    bool received = false;
                    try { receivable = m.IsReceivable(); } catch { }
                    try { receivableCount = m.GetReceivableCount(); } catch { }
                    try { allClear = m.IsAllClear(); } catch { }
                    try { received = m.IsAllClearAndReceived(); } catch { }

                    string groupName = "";
                    try
                    {
                        var g = m.GetMissionGroup();
                        if (g != null) groupName = g.Name ?? "";
                    }
                    catch { }

                    string state = MissionStateOf(m, um);
                    BumpMissionSummary(summaryMap, catName, state);

                    dto.missions.Add(new MissionDto
                    {
                        id = m.Id ?? "",
                        name = name,
                        category = catName,
                        type = m.Type.ToString(),
                        groupId = m.MissionGroupId ?? "",
                        groupName = groupName,
                        progress = progress,
                        threshold = threshold,
                        state = state,
                        receivable = receivable,
                        receivableCount = receivableCount,
                        allClear = allClear,
                        received = received,
                        unlocked = um != null && um.IsUnlock,
                        isEvent = m.IsEventMission,
                        order = m.Order
                    });
                }

                if (dto.missions.Count >= maxMissions)
                    dto.error = "truncated at " + maxMissions;

                var cats = new[] { "Daily", "Weekly", "Normal", "Special", "Event", "Achievement", "MainTask", "Unknown" };
                for (int c = 0; c < cats.Length; c++)
                {
                    MissionCategorySummaryDto s;
                    if (summaryMap.TryGetValue(cats[c], out s)) dto.summary.Add(s);
                }
                foreach (var kv in summaryMap)
                {
                    bool known = false;
                    for (int c = 0; c < cats.Length; c++) if (kv.Key == cats[c]) { known = true; break; }
                    if (!known) dto.summary.Add(kv.Value);
                }
            }
            catch (Exception e)
            {
                dto.error = "mission list failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedMissionList = dto;
        }

        private object MissionReceive()
        {
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.MissionTopScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.MissionTop, true, false, null);
                    return "opened MissionTop; retry mission_receive after the screen loads";
                }

                string[] names = { "ReceiveAllButton", "AllReceiveButton", "ReceiveButton", "ExecuteButton" };
                for (int i = 0; i < names.Length; i++)
                {
                    var hit = FindTarget(names[i]);
                    if (hit == null) continue;
                    var msg = InvokeButtonCallback(names[i]);
                    if (msg != null && msg.IndexOf("invoked", StringComparison.Ordinal) >= 0)
                        return "mission receive: " + msg;
                }

                FindMatches("Receive");
                var found = GakumasAutoPlugin.SharedFindResp;
                if (found != null && found.matches != null)
                {
                    for (int i = 0; i < found.matches.Count; i++)
                    {
                        var m = found.matches[i];
                        if (m == null || !m.active) continue;
                        if (m.name.IndexOf("Receive", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var msg = InvokeButtonCallback(m.path);
                        return "mission receive: " + msg;
                    }
                }
                return "MISSION_RECEIVE_NOT_FOUND: opened mission screen but no receive button";
            }
            catch (Exception e)
            {
                return "mission_receive failed: " + e.Message;
            }
        }
    }
}
