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
        private string AdvEndWait()
        {
            try
            {
                var engines = UnityEngine.Object.FindObjectsOfType<ADVEngine>();
                if (engines == null || engines.Length == 0) return "no ADVEngine";
                var timeline = engines[0].Timeline;
                if (timeline == null) return "ADVEngine.Timeline is null";
                timeline.EndWait(true);
                return $"EndWait(skip=true) sent";
            }
            catch (Exception e) { return $"adv_end_wait failed: {e.Message}"; }
        }

        private string AdvSetFastForward(bool on)
        {
            try
            {
                var engines = UnityEngine.Object.FindObjectsOfType<ADVEngine>();
                if (engines == null || engines.Length == 0) return "no ADVEngine";
                var timeline = engines[0].Timeline;
                if (timeline == null) return "ADVEngine.Timeline is null";
                timeline.ToggleFastForward(on);
                return $"ToggleFastForward({on}) sent";
            }
            catch (Exception e) { return $"adv_set_ff failed: {e.Message}"; }
        }

        private string AdvSelectUnselected()
        {
            try
            {
                var engines = UnityEngine.Object.FindObjectsOfType<ADVEngine>();
                if (engines == null || engines.Length == 0) return "no ADVEngine";
                var engine = engines[0];
                if (engine.Branch == null) return "engine.Branch is null";
                int n = engine.Branch.ChoiceCount;
                engine.Branch.SelectUnselectedChoices();
                return $"SelectUnselectedChoices sent; choiceCount={n}";
            }
            catch (Exception e) { return $"adv_select_unselected failed: {e.Message}"; }
        }
    }
}
