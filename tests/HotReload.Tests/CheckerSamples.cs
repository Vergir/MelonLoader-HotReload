// IL samples for HotReloadCheck's Input-patch scan (CheckerTests reads this test assembly). Nothing here is patched or run.
using HarmonyLib;

namespace UnityEngine
{
    /// <summary>Stand-in for UnityEngine.Input: the scan matches by full type name.</summary>
    public static class Input
    {
        public static bool GetKeyDown(int key) => false;
        public static bool GetKey(int key) => false;
    }
}

namespace HotReload.Tests.CheckerSamples
{
    using UnityEngine;

    [HarmonyPatch(typeof(Input), nameof(Input.GetKeyDown))]
    internal static class AttributeInputPatch
    {
        private static bool Prefix() => true;
    }

    internal static class ManualInputPatch
    {
        public static void Apply(HarmonyLib.Harmony harmony) =>
            harmony.Patch(AccessTools.Method(typeof(Input), nameof(Input.GetKey)), prefix: new HarmonyMethod(typeof(ManualInputPatch), nameof(Block)));

        private static bool Block() => true;
    }

    internal static class ReadsInputPlainly
    {
        public static bool Pressed() => Input.GetKeyDown(1);                  // a call, not a patch
        public static string Name() => typeof(Input).Name;                     // typeof(Input) with no Harmony around
    }
}
