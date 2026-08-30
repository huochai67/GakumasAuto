using System;
using System.Collections.Generic;

namespace GakumasAuto
{
    public partial class AutoDriver
    {
        private static string GradeName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            return raw[0] == '_' ? raw.Substring(1) : raw;
        }

        private object PvpChallenge(string rivalRaw, bool confirm)
        {
            try
            {
                string key = (rivalRaw ?? "").Trim().ToLowerInvariant();
                string path;
                if (key == "high" || key == "1" || key == "上")
                    path = "Rivals/PvpRateRivalInfo";
                else if (key == "middle" || key == "mid" || key == "2" || key == "中")
                    path = "Rivals/PvpRateRivalInfo (1)";
                else if (key == "low" || key == "3" || key == "下")
                    path = "Rivals/PvpRateRivalInfo (2)";
                else
                    return "rival must be high|middle|low";

                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.PvpRateTopScreenPresenter>();
                bool onPvp = presenters != null && presenters.Length > 0;
                if (!confirm)
                    return onPvp
                        ? "dry_run: would tap " + path + " (pass confirm:true to start)"
                        : "dry_run: not on PvP (call pvp_enter first); would tap " + path;

                if (!onPvp)
                {
                    Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.PvpRateTop, true, false, null);
                    return "opened PvpRateTop; retry pvp_challenge after the screen loads";
                }

                return InvokeButtonCallback(path);
            }
            catch (Exception e)
            {
                return "pvp_challenge failed: " + e.Message;
            }
        }

        private object PvpEnter()
        {
            try
            {
                PresenterUtil.DismissLoading();
                Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.PvpRateTop, true, false, null);
                return "opened PvpRateTop";
            }
            catch (Exception e)
            {
                return "pvp_enter failed: " + e.Message;
            }
        }

        private object PvpAutoSet()
        {
            try
            {
                var edits = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.PvpRateUnitEditScreenPresenter>();
                if (edits == null || edits.Length == 0)
                {
                    Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.PvpRateUnitEdit, true, false, null);
                    return "opened PvpRateUnitEdit; retry pvp_auto_set after the screen loads";
                }
                var msg = InvokeFirstMatch(new[]
                {
                    "AutoButton", "AutoSetButton", "UnitAutoButton", "ExecuteButton"
                });
                if (msg != null) return "pvp auto_set: " + msg;
                return "PVP_AUTO_SET_NOT_FOUND: unit edit open but no auto button";
            }
            catch (Exception e)
            {
                return "pvp_auto_set failed: " + e.Message;
            }
        }

        private void BuildPvpState()
        {
            var dto = new PvpStateDto
            {
                grade = "",
                bestGrade = "",
                phaseType = "",
                rank = 0,
                rate = 0,
                remainingDailyPlayCount = 0,
                maxDailyPlayCount = 0,
                seasonStatus = "",
                seasonEndTime = 0,
                rivalMatchTime = 0,
                competitionRemaining = 0,
                challengePoint = 0,
                screenOpen = false,
                rivals = new List<PvpRivalDto>(),
                error = ""
            };
            try
            {
                var rate = Campus.Common.User.UserDataManager.UserPvpRate;
                if (rate != null)
                {
                    dto.grade = GradeName(rate.Grade.ToString());
                    dto.bestGrade = GradeName(rate.BestGrade.ToString());
                    dto.phaseType = rate.PhaseType.ToString();
                    dto.rivalMatchTime = rate.RivalMatchTime;
                }

                var season = Campus.Common.User.UserDataManager.PvpRateSeasonTop;
                if (season != null)
                {
                    dto.rank = season.Rank;
                    dto.rate = season.Rate;
                    dto.remainingDailyPlayCount = season.RemainingDailyPlayCount;
                    dto.maxDailyPlayCount = season.MaxDailyPlayCount;
                    dto.seasonStatus = season.StatusType.ToString();
                    dto.seasonEndTime = season.CurrentSeasonEndTime;
                }

                try
                {
                    var comp = Campus.Common.User.UserDataManager.UserCompetition;
                    if (comp != null) dto.competitionRemaining = (int)comp.RemainingPlayCount;
                }
                catch { }

                try
                {
                    var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.PvpRateTopScreenPresenter>();
                    dto.screenOpen = presenters != null && presenters.Length > 0;
                    if (dto.screenOpen)
                        FillPvpRivalsFromPresenter(presenters[0], dto);
                }
                catch { }
            }
            catch (Exception e)
            {
                dto.error = "pvp read failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedPvpState = dto;
        }

        private static void FillPvpRivalsFromPresenter(Campus.OutGame.PvpRateTopScreenPresenter presenter, PvpStateDto dto)
        {
            if (presenter == null) return;
            try
            {
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                object model = null;
                var modelProp = presenter.GetType().GetProperty("Model", flags) ?? presenter.GetType().GetProperty("_model", flags);
                if (modelProp != null) model = modelProp.GetValue(presenter);
                if (model == null)
                {
                    var modelField = presenter.GetType().GetField("_model", flags);
                    if (modelField != null) model = modelField.GetValue(presenter);
                }
                if (model == null) return;

                var rivalsProp = model.GetType().GetProperty("Rivals", flags)
                    ?? model.GetType().GetProperty("RivalList", flags)
                    ?? model.GetType().GetProperty("PvpRateRivals", flags);
                object rivals = rivalsProp != null ? rivalsProp.GetValue(model) : null;
                if (rivals == null) return;

                for (int i = 0; i < 8; i++)
                {
                    object raw = null;
                    try
                    {
                        var indexer = rivals.GetType().GetProperty("Item");
                        raw = indexer != null ? indexer.GetValue(rivals, new object[] { i }) : null;
                    }
                    catch { break; }
                    var rival = raw as Campus.Common.Proto.Client.Api.PvpRateRival;
                    if (rival == null) break;
                    string name = "";
                    try
                    {
                        if (rival.Profile != null) name = rival.Profile.Name ?? "";
                    }
                    catch { }
                    dto.rivals.Add(new PvpRivalDto
                    {
                        type = rival.RivalType.ToString(),
                        name = name,
                        totalPower = rival.TotalPower,
                        earnedRate = rival.EarnedRate,
                        isNpc = rival.IsNpc
                    });
                }
            }
            catch { }
        }
    }
}
