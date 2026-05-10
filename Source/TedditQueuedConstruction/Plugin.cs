using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace TedditQueuedConstruction
{
    [BepInPlugin("com.teddit.queuedconstruction", "Teddit Queued Construction", "0.1.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("Teddit Queued Construction v0.1.0 loaded.");
            new Harmony("com.teddit.queuedconstruction").PatchAll();
        }
    }
}
