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
        private ExamCardDto MapExamCard(Campus.InGame.Card.ExamCardData c)
        {
            return new ExamCardDto
            {
                id = c.Id ?? "",
                guid = c.Guid ?? "",
                name = c.Name ?? "",
                rarity = c.Rarity.ToString(),
                upgradeCount = c.UpgradeCount,
                customizeCount = c.CustomizeCount,
                evaluation = c.Evaluation,
                staminaCost = c.StaminaCostRaw,
                category = c.Category.ToString(),
                planType = c.PlanType.ToString()
            };
        }

        private void FillExamCards(Il2CppSystem.Collections.Generic.IReadOnlyList<Campus.InGame.Card.ExamCardData> src, List<ExamCardDto> dst, int max)
        {
            if (src == null || dst == null) return;
            for (int i = 0; i < max; i++)
            {
                Campus.InGame.Card.ExamCardData c = null;
                try { c = src[i]; } catch { break; }
                if (c == null) break;
                try { dst.Add(MapExamCard(c)); }
                catch { }
            }
        }

        private void BuildExamState()
        {
            var dto = new ExamStateDto
            {
                active = false,
                isInProgress = false,
                isCommandPlaying = false,
                isPauseTurnEnd = false,
                isShowTurnEndButton = false,
                isBusy = false,
                isInitialized = false,
                selectCardIndex = -1,
                currentPlayerSequenceIndex = -1,
                statusType = "",
                playingCard = null,
                hand = new List<ExamCardDto>(),
                deck = new List<ExamCardDto>(),
                grave = new List<ExamCardDto>(),
                lost = new List<ExamCardDto>(),
                hold = new List<ExamCardDto>(),
                futureDeck = new List<List<ExamCardDto>>(),
                error = ""
            };
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.InGame.Exam.ExamScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    dto.error = "no exam screen active";
                }
                else
                {
                    dto.active = true;
                    var p = presenters[0];

                    var model = p.Model;
                    if (model != null)
                    {
                        dto.isShowTurnEndButton = model.IsShowTurnEndButton;
                        dto.selectCardIndex = model.SelectCardIndex;
                        dto.currentPlayerSequenceIndex = model.CurrentPlayerSequenceIndex;
                        dto.isBusy = model.IsBusy;
                        dto.isInitialized = model.IsInitialized;
                        dto.statusType = model.StatusType.ToString();
                    }

                    var store = p.DataStore;
                    if (store != null)
                    {
                        FillExamCards(store.HandList, dto.hand, 20);
                        FillExamCards(store.DeckList, dto.deck, 60);
                        FillExamCards(store.GraveList, dto.grave, 60);
                        FillExamCards(store.LostList, dto.lost, 60);
                        FillExamCards(store.HoldList, dto.hold, 20);
                    }

                    var seq = p.Sequence;
                    if (seq != null)
                    {
                        dto.isInProgress = seq.IsInProgress;
                        dto.isCommandPlaying = seq.IsCommandPlaying;
                        dto.isPauseTurnEnd = seq.IsPauseTurnEnd;
                        var playing = seq.PlayingCard;
                        if (playing != null) dto.playingCard = MapExamCard(playing);
                        var future = seq.FutureDeckList;
                        if (future != null)
                        {
                            for (int i = 0; i < 10; i++)
                            {
                                Il2CppSystem.Collections.Generic.IReadOnlyList<Campus.InGame.Card.ExamCardData> layerSrc = null;
                                try { layerSrc = future[i]; } catch { break; }
                                if (layerSrc == null) break;
                                var layer = new List<ExamCardDto>();
                                FillExamCards(layerSrc, layer, 20);
                                dto.futureDeck.Add(layer);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                dto.error = "exam read failed: " + e.Message;
            }
            GakumasAutoPlugin.SharedExamState = dto;
        }

        private object ExamStart()
        {
            try
            {
                var presenters = UnityEngine.Object.FindObjectsOfType<Campus.InGame.Exam.ExamScreenPresenter>();
                if (presenters == null || presenters.Length == 0)
                {
                    return "EXAM_NOT_ACTIVE: no exam screen; enter a produce exam or contest rehearsal first";
                }
                presenters[0].OnSkipExamTransition();
                return "exam transition skipped";
            }
            catch (Exception e)
            {
                return "exam start failed: " + e.Message;
            }
        }
    }
}
