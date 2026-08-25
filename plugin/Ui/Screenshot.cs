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
        private void CaptureScreenshot()
        {
            var log = GakumasAutoPlugin.SharedLog;
            try
            {
                var tex = ScreenCapture.CaptureScreenshotAsTexture();
                if (tex != null)
                {
                    int w = tex.width, h = tex.height;
                    byte[] png = tex.EncodeToPNG();
                    File.WriteAllBytes(ScreenPath, png);
                    UnityEngine.Object.Destroy(tex);
                    log.LogInfo($"SCREENSHOT: {ScreenPath} {w}x{h} ({png.Length} bytes)");
                    GakumasAutoPlugin.SharedScreenshot = new ScreenshotDto { file = ScreenPath, w = w, h = h, pending = false };
                    return;
                }
            }
            catch (Exception e)
            {
                log.LogWarning($"sync screenshot failed: {e.Message}; falling back to async");
            }

            try
            {
                ScreenCapture.CaptureScreenshot(ScreenPath);
                log.LogInfo("SCREENSHOT: async capture scheduled");
                GakumasAutoPlugin.SharedScreenshot = new ScreenshotDto { file = ScreenPath, w = Screen.width, h = Screen.height, pending = true };
            }
            catch (Exception e)
            {
                log.LogWarning($"async screenshot failed: {e.Message}");
                GakumasAutoPlugin.SharedScreenshot = new ScreenshotDto { file = "", w = 0, h = 0, pending = false };
            }
        }
    }
}
