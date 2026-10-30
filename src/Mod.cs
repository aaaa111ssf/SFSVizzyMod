using System;
using ModLoader;
using UnityEngine;

namespace SFSVizzyMod
{
    // 模组入口。ModLoader 在 Mods/<文件夹名>/<文件夹名>.dll 里找 Mod 派生类。
    public class VizzyMod : Mod
    {
        internal static VizzyMod main;

        public override string ModNameID => "SFSVizzyMod";

        public override string DisplayName => "SFS Vizzy";

        public override string Author => "A Future star";

        public override string MinimumGameVersionNecessary => "1.6.0.0";

        public override string ModVersion => "1.0.0";

        public override string Description =>
            "Vizzy-style visual block programming in your browser. Press F8 to open the local editor, " +
            "build a program from blocks (wait, set throttle, log, if/else, while, fail) and run it on your " +
            "rocket in flight with live telemetry.";

        public override Action LoadKeybindings => Keybindings.Setup;

        public override void Load()
        {
            main = this;
            try
            {
                GameObject go = new GameObject("SFSVizzyMod.Manager");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<ProgramRunner>();
                go.AddComponent<Bridge>();
                go.AddComponent<DialogUI>();
                go.AddComponent<PartLabels>();
                Debug.Log("[SFSVizzy] Loaded");
            }
            catch (Exception e)
            {
                Debug.LogError("[SFSVizzy] Load failed: " + e);
            }
        }
    }
}
