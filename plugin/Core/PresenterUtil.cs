using System;
using System.Reflection;

namespace GakumasAuto
{
    internal static class PresenterUtil
    {
        private const BindingFlags Flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        internal static object ReadModel(object presenter)
        {
            if (presenter == null) return null;
            var t = presenter.GetType();
            foreach (var name in new[] { "Model", "_model" })
            {
                try
                {
                    var p = t.GetProperty(name, Flags);
                    if (p != null)
                    {
                        var v = p.GetValue(presenter, null);
                        if (v != null) return v;
                    }
                }
                catch { }
                try
                {
                    var f = t.GetField(name, Flags);
                    if (f != null)
                    {
                        var v = f.GetValue(presenter);
                        if (v != null) return v;
                    }
                }
                catch { }
            }
            return null;
        }

        internal static T ReadModel<T>(object presenter) where T : class
        {
            return ReadModel(presenter) as T;
        }

        internal static object Member(object o, string name)
        {
            if (o == null || string.IsNullOrEmpty(name)) return null;
            var t = o.GetType();
            try
            {
                var p = t.GetProperty(name, Flags);
                if (p != null) return p.GetValue(o, null);
            }
            catch { }
            try
            {
                var f = t.GetField(name, Flags);
                if (f != null) return f.GetValue(o);
            }
            catch { }
            return null;
        }

        internal static object ReactiveValue(object rp)
        {
            if (rp == null) return null;
            var v = Member(rp, "Value");
            if (v != null) return v;
            return rp;
        }

        internal static string DismissLoading()
        {
            try
            {
                bool active = Campus.Common.LoadingManager.IsActive;
                if (active)
                    Campus.Common.LoadingManager.HideImmediate();
                return active ? "hid loading overlay" : "loading already idle";
            }
            catch (Exception e)
            {
                return "loading_hide failed: " + e.Message;
            }
        }
    }
}
