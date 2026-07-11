using Godot.Bridge;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace InfiniteUpgradeSystem;

[ModInitializer(nameof(Init))]
public static class Entry
{
    public const string ModId = "InfiniteUpgradeSystem";

    public static void Init()
    {
        var harmony = new Harmony(ModId);
        harmony.PatchAll(typeof(Entry).Assembly);
        ScriptManagerBridge.LookupScriptsInAssembly(typeof(Entry).Assembly);

        // Create the UI overlay and attach it to the scene tree
        UpgradeUIHandler.CreateInstance();

        Log.Info("InfiniteUpgradeSystem mod initialized.");
    }
}
