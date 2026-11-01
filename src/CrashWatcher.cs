using System;
using System.Reflection;
using HarmonyLib;
using SFS.World;
using UnityEngine;

namespace SFSVizzyMod
{
    // 坠毁事件：钩 RocketManager.DestroyRocket，覆盖地形/水面/对撞；补丁失败不影响模组
    public static class CrashWatcher
    {
        public static void Install()
        {
            try
            {
                MethodInfo method = typeof(RocketManager).GetMethod("DestroyRocket",
                    BindingFlags.Static | BindingFlags.Public, null,
                    new[] { typeof(Rocket), typeof(DestructionReason) }, null);

                if (method == null)
                {
                    Debug.LogWarning("[SFSVizzy] crash hook: DestroyRocket not found, ON CRASH disabled");
                    return;
                }

                Harmony harmony = new Harmony("sfsvizzy.crash");
                harmony.Patch(method, postfix: new HarmonyMethod(
                    typeof(CrashWatcher).GetMethod("PostfixDestroy", BindingFlags.Static | BindingFlags.NonPublic)));
                Debug.Log("[SFSVizzy] crash hook installed on RocketManager.DestroyRocket");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SFSVizzy] crash hook failed: " + e);
            }
        }

        private static void PostfixDestroy(Rocket rocket, DestructionReason reason)
        {
            if (reason == DestructionReason.Intentional) return;
            if (ProgramRunner.main != null) ProgramRunner.main.NotifyCrash(rocket, reason.ToString());
        }
    }
}
