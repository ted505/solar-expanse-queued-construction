using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.IO;

namespace TedditQueuedConstruction
{
    [BepInPlugin("com.teddit.queuedconstruction", "Teddit Queued Construction", "0.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> AdvancedQueuedRuns;
        private static ConfigFile PluginConfig;

        private void Awake()
        {
            Log = Logger;
            string pluginConfigPath = Path.Combine(Paths.PluginPath, "TedditQueuedConstruction", "TedditQueuedConstruction.cfg");
            PluginConfig = new ConfigFile(pluginConfigPath, saveOnInit: true);
            AdvancedQueuedRuns = PluginConfig.Bind(
                "Queue",
                "AdvancedQueuedRuns",
                false,
                "When enabled, queued facilities are grouped and started by contiguous queue runs instead of merging every facility of the same type together.");
            PluginConfig.Save();
            Log.LogInfo("Teddit Queued Construction v0.2.0 loaded. Config: " + pluginConfigPath);
            new Harmony("com.teddit.queuedconstruction").PatchAll();
        }
    }
}
