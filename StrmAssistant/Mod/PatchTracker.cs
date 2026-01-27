using System;

namespace StrmAssistant.Mod
{
    public enum PatchApproach
    {
        None,
        Injection,
        Reflection,
        Harmony
    }

    public class PatchTracker
    {
        public PatchTracker(Type patchType, PatchApproach defaultApproach, string name = null)
        {
            Name = string.IsNullOrEmpty(name) ? patchType.Name : name;
            PatchType = patchType;
            DefaultPatchApproach = defaultApproach;
            FallbackPatchApproach = defaultApproach;

            PatchManager.PatchTrackerList.Add(this);
        }

        public string Name { get; }

        public Type PatchType { get; set; }

        public PatchApproach DefaultPatchApproach { get; }

        public PatchApproach FallbackPatchApproach { get; set; }

        public bool IsSupported { get; set; } = true;
    }
}
