using System;
using ModLoader;
using SFS.Input;
using UnityEngine;

namespace SFSVizzyMod
{
    // 可在游戏「设置 → 按键」里改的快捷键。注册失败（按键系统尚未就绪）时，Bridge 会退回写死的 F8 / R / P。
    internal class Keybindings : ModKeybindings
    {
        internal static Keybindings main;

        public KeybindingsPC.Key toggleEditor = KeyCode.F8;

        public KeybindingsPC.Key runProgram = KeyCode.R;

        public KeybindingsPC.Key stopProgram = KeyCode.P;

        public KeybindingsPC.Key togglePartLabels = KeyCode.H;

        internal static bool IsReady { get { return main != null; } }

        internal static void Setup()
        {
            if (main != null) return;
            try
            {
                if (KeybindingsPC.main == null)
                {
                    Debug.Log("[SFSVizzy] keybindings: KeybindingsPC not ready, will retry");
                    return;
                }
                main = SetupKeybindings<Keybindings>(VizzyMod.main);
                Debug.Log("[SFSVizzy] keybindings registered (native Controls settings)");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SFSVizzy] keybinding setup failed, will retry: " + e);
                main = null;
            }
        }

        public override void CreateUI()
        {
            CreateUI_Text("SFS Vizzy");
            CreateUI_Keybinding(toggleEditor, (KeybindingsPC.Key)KeyCode.F8, "Open the block editor in your browser");
            CreateUI_Keybinding(runProgram, (KeybindingsPC.Key)KeyCode.R, "Run the program open in the browser");
            CreateUI_Keybinding(stopProgram, (KeybindingsPC.Key)KeyCode.P, "Stop the running program");
            CreateUI_Keybinding(togglePartLabels, (KeybindingsPC.Key)KeyCode.H, "Show part numbers for blocks");
        }
    }
}
