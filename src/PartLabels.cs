using System;
using System.Collections.Generic;
using SFS.Input;
using SFS.Parts.Modules;
using SFS.World;
using UnityEngine;

namespace SFSVizzyMod
{
    // 游戏内部件编号：H 开关，发动机/降落伞/分级旁画编号，右上角列清单
    public class PartLabels : MonoBehaviour
    {
        public static PartLabels main;

        private bool visible;
        private GUIStyle numStyle;
        private GUIStyle headStyle;
        private GUIStyle lineStyle;
        private Texture2D blackTex;
        private float lineH;

        private void Awake()
        {
            main = this;
            blackTex = new Texture2D(1, 1);
            blackTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.85f));
            blackTex.Apply();
        }

        private void Update()
        {
            if (DialogUI.main != null && DialogUI.main.IsOpen) return;
            if (TogglePressed())
            {
                visible = !visible;
                if (visible) LogCounts();
            }
        }

        private void LogCounts()
        {
            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null || Bridge.main == null) return;
            int engines = r.partHolder.GetModules<EngineModule>().Length;
            int chutes = r.partHolder.GetModules<ParachuteModule>().Length;
            int stages = NonEmptyStages(r).Count;
            Bridge.main.Log("PARTS: stages=" + stages + " engines=" + engines + " chutes=" + chutes);
        }

        private static bool TogglePressed()
        {
            if (Keybindings.main != null)
            {
                try { return ((I_Key)Keybindings.main.togglePartLabels).IsKeyDown(); }
                catch (Exception) { }
            }
            return Input.GetKeyDown(KeyCode.H);
        }

        private void OnGUI()
        {
            if (!visible) return;

            Rocket r = WorldVars.GetPlayerRocket();
            if (r == null) return;

            EnsureStyles();

            EngineModule[] engines = r.partHolder.GetModules<EngineModule>();
            ParachuteModule[] chutes = r.partHolder.GetModules<ParachuteModule>();
            int[] engineOrder = ProgramRunner.OrderedModuleIndices(r, engines);
            int[] chuteOrder = ProgramRunner.OrderedModuleIndices(r, chutes);
            List<Stage> stages = NonEmptyStages(r);

            // 分级编号：画在每级第一个零件上
            for (int i = 0; i < stages.Count; i++)
            {
                if (stages[i].parts.Count == 0) continue;
                DrawBadge(stages[i].parts[0].transform.position, "S" + (i + 1), new Color(1f, 0.85f, 0.3f), 40f);
            }

            for (int k = 0; k < engineOrder.Length; k++)
            {
                EngineModule eng = engines[engineOrder[k]];
                bool on = false;
                try { on = eng.engineOn.Value; } catch (Exception) { }
                DrawBadge(eng.transform.position, (k + 1).ToString(),
                    on ? Color.white : new Color(0.55f, 0.55f, 0.55f), 32f);
            }

            for (int k = 0; k < chuteOrder.Length; k++)
            {
                ParachuteModule ch = chutes[chuteOrder[k]];
                float st = 0f;
                try { st = ch.state.Value; } catch (Exception) { }
                DrawBadge(ch.transform.position, "P" + (k + 1),
                    st > 0f ? Color.white : new Color(0.55f, 0.55f, 0.55f), 40f);
            }

            DrawPanel(stages, engines, chutes, engineOrder, chuteOrder);
        }

        private static List<Stage> NonEmptyStages(Rocket r)
        {
            List<Stage> list = new List<Stage>();
            foreach (Stage s in r.staging.stages)
                if (s != null && s.PartCount > 0) list.Add(s);
            return list;
        }

        private void DrawBadge(Vector3 world, string text, Color color, float width)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) return;

            float x = sp.x;
            float y = Screen.height - sp.y;
            Rect box = new Rect(x - width * 0.5f, y - 22f, width, 18f);

            GUI.DrawTexture(box, blackTex);
            Color old = GUI.color;
            GUI.color = color;
            GUI.Label(box, text, numStyle);
            GUI.color = old;
        }

        private void DrawPanel(List<Stage> stages, EngineModule[] engines, ParachuteModule[] chutes,
            int[] engineOrder, int[] chuteOrder)
        {
            float w = 236f;
            float x = Screen.width - w - 14f;
            float y = 64f;

            float total = lineH * (4 + stages.Count + engines.Length + chutes.Length) + 14f;
            float maxH = Screen.height - y - 10f;
            if (total > maxH) total = maxH;

            GUI.DrawTexture(new Rect(x, y, w, total), blackTex);

            float cy = y + 7f;
            GUI.Label(new Rect(x + 10f, cy, w - 20f, lineH * 1.2f), "PART NUMBERS (H)", headStyle);
            cy += lineH * 1.35f;

            GUI.Label(new Rect(x + 10f, cy, w - 20f, lineH), "STAGES (" + stages.Count + ")", headStyle);
            cy += lineH;
            for (int i = 0; i < stages.Count && cy + lineH <= y + total; i++)
            {
                string tag = i == 0 ? "  <next>" : "";
                GUI.Label(new Rect(x + 14f, cy, w - 24f, lineH),
                    (i + 1) + "  " + stages[i].PartCount + " parts" + tag, lineStyle);
                cy += lineH;
            }

            cy += 2f;
            if (cy + lineH <= y + total)
                GUI.Label(new Rect(x + 10f, cy, w - 20f, lineH), "ENGINES (" + engines.Length + ")", headStyle);
            cy += lineH;
            for (int k = 0; k < engineOrder.Length && cy + lineH <= y + total; k++)
            {
                bool on = false;
                try { on = engines[engineOrder[k]].engineOn.Value; } catch (Exception) { }
                GUI.Label(new Rect(x + 14f, cy, w - 24f, lineH),
                    (k + 1) + "  " + (on ? "on" : "off"), lineStyle);
                cy += lineH;
            }

            cy += 2f;
            if (cy + lineH <= y + total)
                GUI.Label(new Rect(x + 10f, cy, w - 20f, lineH), "PARACHUTES (" + chutes.Length + ")", headStyle);
            cy += lineH;
            for (int k = 0; k < chuteOrder.Length && cy + lineH <= y + total; k++)
            {
                float st = 0f;
                try { st = chutes[chuteOrder[k]].state.Value; } catch (Exception) { }
                string state = st >= 3f ? "cut" : st >= 2f ? "full" : st >= 1f ? "half" : "stowed";
                GUI.Label(new Rect(x + 14f, cy, w - 24f, lineH),
                    "P" + (k + 1) + "  " + state, lineStyle);
                cy += lineH;
            }
        }

        private void EnsureStyles()
        {
            if (numStyle != null) return;

            int font = Mathf.Clamp(Mathf.RoundToInt(Screen.height * 0.015f), 11, 18);
            lineH = font * 1.45f;

            numStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = font,
                alignment = TextAnchor.MiddleCenter,
                clipping = TextClipping.Clip,
                normal = { textColor = Color.white }
            };
            headStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = font,
                fontStyle = FontStyle.Bold,
                clipping = TextClipping.Clip,
                normal = { textColor = Color.white }
            };
            lineStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = font,
                clipping = TextClipping.Clip,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
            };
        }
    }
}
