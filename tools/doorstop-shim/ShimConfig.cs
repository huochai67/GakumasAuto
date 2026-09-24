// Minimal key=value configuration for the shim (BepInEx\core\GakumasDoorstopShim.cfg).
// No dependency on BepInEx.Configuration: the shim runs before BepInEx is loaded.
using System;
using System.Collections.Generic;
using System.IO;

namespace GakumasDoorstopShim
{
    internal sealed class ShimConfig
    {
        internal string ImagePath;
        internal string CachePath;
        internal string RuntimePath;
        internal bool Verbose = true;

        internal static ShimConfig Load(string path)
        {
            var cfg = new ShimConfig();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                ShimLog.Write("[cfg] no config file at " + (path ?? "<null>") + "; using defaults");
                return cfg;
            }

            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var value = line.Substring(eq + 1).Trim();
                int comment = value.IndexOf('#');
                if (comment >= 0) value = value.Substring(0, comment).Trim();

                switch (key.ToLowerInvariant())
                {
                    case "imagepath": cfg.ImagePath = value; break;
                    case "cachepath": cfg.CachePath = value; break;
                    case "runtimepath": cfg.RuntimePath = value; break;
                    case "verbose": cfg.Verbose = value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase); break;
                }
            }

            ShimLog.Write("[cfg] image=" + (cfg.ImagePath ?? "<auto>") + " cache=" + (cfg.CachePath ?? "<auto>"));
            return cfg;
        }
    }
}
