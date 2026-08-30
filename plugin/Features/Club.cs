using System;
using System.Collections.Generic;

namespace GakumasAuto
{
    public partial class AutoDriver
    {
        private object ClubEnter()
        {
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.GuildTopScreenPresenter>();
                if (presenters != null && presenters.Length > 0)
                {
                    var existing = PresenterUtil.ReadModel<Campus.OutGame.GuildTopScreenModel>(presenters[0]);
                    if (existing != null) return "already GuildTop";
                    try { Campus.Common.LoadingManager.HideImmediate(); } catch { }
                    return "GuildTop visible; dismissed loading overlay";
                }

                var link = Campus.Common.MenuButtonTypeExtensions.GetLinkData(Campus.Common.MenuButtonType.Guild);
                if (link != null)
                {
                    Campus.OutGame.OutGameTransitionUtility.To(link, true);
                    return "opened guild via menu link";
                }

                Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.GuildTop, true, false, null);
                return "opened GuildTop";
            }
            catch (Exception e)
            {
                return "club_enter failed: " + e.Message;
            }
        }

        private static string ReadReactiveEnum(object rp)
        {
            var v = PresenterUtil.ReactiveValue(rp);
            return v != null ? (v.ToString() ?? "") : "";
        }


        private void BuildClubState()
        {
            var dto = new ClubStateDto
            {
                screenOpen = false,
                inGuild = false,
                guildId = "",
                guildName = "",
                requestState = "",
                canRequest = false,
                canReceive = false,
                requestedItemId = "",
                requestedItemName = "",
                remainDonationCount = 0,
                maxDonationCount = 0,
                donationState = "",
                memberCount = 0,
                members = new List<string>(),
                error = ""
            };
            try
            {
                try
                {
                    var g = Campus.Common.User.UserDataManager.UserGuild;
                    dto.inGuild = g != null;
                }
                catch { }

                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.GuildTopScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    dto.error = dto.inGuild ? "guild screen not open (call club_enter first)" : "not in a guild / GuildTop not open";
                    GakumasAutoPlugin.SharedClubState = dto;
                    return;
                }
                dto.screenOpen = true;
                Campus.OutGame.GuildTopScreenModel model = null;
                for (int i = 0; i < presenters.Length; i++)
                {
                    model = PresenterUtil.ReadModel<Campus.OutGame.GuildTopScreenModel>(presenters[i]);
                    if (model != null) break;
                }

                if (model != null)
                {
                    dto.inGuild = true;
                    dto.guildId = model.GuildId ?? "";
                    dto.guildName = model.GuildName ?? "";
                    dto.requestState = ReadReactiveEnum(PresenterUtil.Member(model, "UserDonationRequestState"));
                    dto.canRequest = dto.requestState == "CanRequest";
                    dto.canReceive = dto.requestState == "RequestingAndCanReceive";
                    try
                    {
                        if (model.ReceivedDonationInfo != null) dto.canReceive = true;
                    }
                    catch { }

                    try
                    {
                        var req = model.UserDonationRequest;
                        if (req != null)
                            dto.requestedItemId = req.GuildDonationItemId ?? "";
                    }
                    catch { }

                    var members = model.Members;
                    if (members != null)
                    {
                        for (int i = 0; i < 40; i++)
                        {
                            Campus.Common.Proto.Client.Api.GuildMemberInfo m = null;
                            try { m = members[i]; } catch { break; }
                            if (m == null) break;
                            dto.memberCount++;
                            string name = "";
                            try { if (m.Profile != null) name = m.Profile.Name ?? ""; } catch { }
                            if (name.Length > 0) dto.members.Add(name);
                        }
                    }
                }

                try
                {
                    var dps = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.GuildTopScreenDonationPresenter>();
                    if (dps != null)
                    {
                        for (int di = 0; di < dps.Length; di++)
                        {
                            var dm = PresenterUtil.ReadModel<Campus.OutGame.GuildTopScreenDonationModel>(dps[di]);
                            if (dm == null) continue;
                            var cur = PresenterUtil.ReactiveValue(PresenterUtil.Member(dm, "CurrentUserDonation"));
                            var u = cur as Campus.OutGame.GuildTopScreenDonationModel.UserDonationModel;
                            if (u != null)
                            {
                                dto.remainDonationCount = u.RemainDonationCount;
                                dto.maxDonationCount = u.MaxDonationCount;
                                dto.donationState = u.State.ToString();
                            }
                            var don = PresenterUtil.ReactiveValue(PresenterUtil.Member(dm, "CurrentDonation"));
                            if (don != null && dto.requestedItemId.Length == 0)
                            {
                                try
                                {
                                    var dr = PresenterUtil.Member(don, "DonationRequest") as Campus.Common.Proto.Client.Api.DonationRequest;
                                    if (dr != null) dto.requestedItemId = dr.GuildDonationItemId ?? "";
                                }
                                catch { }
                            }
                            break;
                        }
                    }
                }
                catch { }

                if (dto.requestedItemId.Length > 0 && dto.requestedItemName.Length == 0)
                    dto.requestedItemName = dto.requestedItemId;
                if (model == null && dto.remainDonationCount == 0 && dto.memberCount == 0)
                    dto.error = "guild model null (screen still loading?)";
            }
            catch (Exception e)
            {
                dto.error = "club read failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedClubState = dto;
        }

        private object ClubReceive()
        {
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.GuildTopScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.GuildTop, true, false, null);
                    return "opened GuildTop; retry club_receive after the screen loads";
                }

                var msg = InvokeFirstMatch(new[]
                {
                    "ReceiveButton", "ReceiveDonationButton", "DonationReceiveButton",
                    "ExecuteButton", "CloseButton"
                });
                if (msg != null) return "club receive: " + msg;
                return "CLUB_RECEIVE_NOT_FOUND: no receive button (maybe nothing to claim)";
            }
            catch (Exception e)
            {
                return "club_receive failed: " + e.Message;
            }
        }

        private object ClubRequest()
        {
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.GuildTopScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.GuildTop, true, false, null);
                    return "opened GuildTop; retry club_request after the screen loads";
                }

                var msg = InvokeFirstMatch(new[]
                {
                    "RequestButton", "DonationRequestButton", "NoteRequestButton",
                    "RequestDonationButton", "ExecuteButton"
                });
                if (msg != null) return "club request: " + msg;
                return "CLUB_REQUEST_NOT_FOUND: no request button (state may be Requesting/Restricted)";
            }
            catch (Exception e)
            {
                return "club_request failed: " + e.Message;
            }
        }

        private object ClubDonate()
        {
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.GuildTopScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    Campus.OutGame.OutGameTransitionUtility.To(Campus.ScreenState.GuildTop, true, false, null);
                    return "opened GuildTop; retry club_donate after the screen loads";
                }

                var msg = InvokeFirstMatch(new[]
                {
                    "DonateButton", "GiftButton", "SendGiftButton", "DonationButton",
                    "ExecuteButton"
                });
                string next = null;
                try
                {
                    var dps = UnityEngine.Object.FindObjectsOfType<Campus.OutGame.GuildTopScreenDonationPresenter>();
                    if (dps != null)
                    {
                        for (int di = 0; di < dps.Length; di++)
                        {
                            var dm = PresenterUtil.ReadModel<Campus.OutGame.GuildTopScreenDonationModel>(dps[di]);
                            if (dm == null) continue;
                            dm.MoveNextRequest();
                            next = "moved next";
                            break;
                        }
                    }
                }
                catch { }
                if (next == null)
                    next = InvokeFirstMatch(new[] { "NextButton", "ArrowRightButton", "RightButton" });

                if (msg != null) return "club donate: " + msg + (next != null ? "; " + next : "");
                if (next != null) return "club donate skipped (no donate button); " + next;
                return "CLUB_DONATE_NOT_FOUND: no donate/next button";
            }
            catch (Exception e)
            {
                return "club_donate failed: " + e.Message;
            }
        }
    }
}
