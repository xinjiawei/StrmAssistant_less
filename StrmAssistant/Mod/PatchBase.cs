using System;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public abstract class PatchBase<T> where T : PatchBase<T>
    {
        public PatchTracker PatchTracker;

        public static T Instance { get; private set; }

        protected PatchBase()
        {
            Instance = (T)this;
            PatchTracker = new PatchTracker(typeof(T), PatchApproach.Harmony);
        }

        protected void Initialize()
        {
            try
            {
                OnInitialize();
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Info(e.Message);
                    Plugin.Instance.Logger.Info(e.StackTrace);
                }

                Plugin.Instance.Logger.Warn($"{PatchTracker.PatchType.Name} Init Failed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }

            if (PatchTracker.FallbackPatchApproach == PatchApproach.None) return;

            if (HarmonyMod is null) PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
        }

        protected abstract void OnInitialize();

        protected abstract void Prepare(bool apply);

        public void Patch() => Prepare(true);

        public void Unpatch() => Prepare(false);
    }
}
