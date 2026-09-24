// Shim logging. Must not depend on BepInEx (it runs before BepInEx is up),
// so it appends to a file next to the BepInEx root and mirrors to stdout.
using System;
using System.IO;
using System.Text;

namespace GakumasDoorstopShim
{
    internal static class ShimLog
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static bool _toConsole = true;

        internal static string Path => _path;

        internal static void Init(string path, bool toConsole)
        {
            _toConsole = toConsole;
            _path = path;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, "\r\n=== GakumasDoorstopShim " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===\r\n", Encoding.UTF8);
            }
            catch
            {
                _path = null;
            }
        }

        internal static void Write(string message)
        {
            var line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + message;
            if (_toConsole)
            {
                try { Console.WriteLine(message); } catch { }
            }
            if (_path == null) return;
            lock (Gate)
            {
                try { File.AppendAllText(_path, line + "\r\n", Encoding.UTF8); } catch { }
            }
        }
    }
}
