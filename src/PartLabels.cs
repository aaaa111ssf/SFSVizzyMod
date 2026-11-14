using System;
using System.Collections.Generic;
using SFS.Input;
using SFS.Parts;
using SFS.Parts.Modules;
using SFS.World;
using UnityEngine;

namespace SFSVizzyMod
{
    // 游戏内部件编号：H 开关，发动机/分级/降落伞/轮子/RCS/分离器/折叠件旁画编号，右上角列清单
    public class PartLabels : MonoBehaviour
    {
        public static PartLabels main;

        private bool visible;
        private GUIStyle numStyle;
        private GUIStyle headStyle;
        private GUIStyle lineStyle;
        private Texture2D blackTex;
        private float lineH;

        private class Badge
        {
            public string Text;
            public Vector3 Pos;
            public Color Color;
            public float Width;
        }

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
            Bridge.main.Log("PARTS: stages=" + NonEmptyStages(r).Count
                + " engines=" + r.partHolder.GetModules<EngineModule>().Length
                + " chutes=" + r.partHolder.GetModules<ParachuteModule>().Length
                + " wheels=" + r.partHolder.GetModules<WheelModule>().Length
                + " rcs=" + r.partHolder.GetModules<RcsModule>().Length
                + " decouplers=" + r.partHolder.GetModules<DetachModule>().Length
                + " folding=" + r.partHolder.GetModules<ToggleModule>().Length
                + " rockets=" + (GameManager.main != null && GameManager.main.rockets != null
                    ? GameManager.main.rockets.Count : 0));
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

            List<Badge> badges = new List<Badge>();
            List<KeyValuePair<string, List<string>>> cats = new List<KeyValuePair<string, List<string>>>();

            // 火箭编号：和 select rocket #N 用的是同一套序号，H 打开就能对上号
            AddRockets(r, badges, cats);

            // 分级：画在每级第一个零件上
            List<Stage> stages = NonEmptyStages(r);
            for (int i = 0; i < stages.Count; i++)
            {
                if (stages[i].parts.Count == 0) continue;
                badges.Add(new Badge { Text = "S" + (i + 1), Pos = stages[i].parts[0].transform.position, Color = new Color(1f, 0.85f, 0.3f), Width = 40f });
            }
            List<string> stageLines = new List<string>();
            for (int i = 0; i < stages.Count; i++)
                stageLines.Add((i + 1) + "  " + stages[i].PartCount + " parts" + (i == 0 ? "  <next>" : ""));
            cats.Add(new KeyValuePair<string, List<string>>("Stages (" + stages.Count + ")", stageLines));

            // 发动机
            EngineModule[] engines = r.partHolder.GetModules<EngineModule>();
            int[] engineOrder = ProgramRunner.OrderedModuleIndices(r, engines);
            List<string> engLines = new List<string>();
            for (int k = 0; k < engineOrder.Length; k++)
            {
                EngineModule eng = engines[engineOrder[k]];
                bool on = false;
                try { on = eng.engineOn.Value; } catch (Exception) { }
                badges.Add(new Badge { Text = "E" + (k + 1), Pos = eng.transform.position, Color = on ? Color.white : new Color(0.55f, 0.55f, 0.55f), Width = 40f });
                engLines.Add("E" + (k + 1) + "  " + (on ? "on" : "off") + "  " + PartName(eng));
            }
            cats.Add(new KeyValuePair<string, List<string>>("Engines (" + engines.Length + ")", engLines));

            // 降落伞
            ParachuteModule[] chutes = r.partHolder.GetModules<ParachuteModule>();
            int[] chuteOrder = ProgramRunner.OrderedModuleIndices(r, chutes);
            List<string> chuteLines = new List<string>();
            for (int k = 0; k < chuteOrder.Length; k++)
            {
                ParachuteModule ch = chutes[chuteOrder[k]];
                float st = 0f;
                try { st = ch.state.Value; } catch (Exception) { }
                string state = st >= 3f ? "cut" : st >= 2f ? "full" : st >= 1f ? "half" : "stowed";
                badges.Add(new Badge { Text = "P" + (k + 1), Pos = ch.transform.position, Color = st > 0f ? Color.white : new Color(0.55f, 0.55f, 0.55f), Width = 40f });
                chuteLines.Add("P" + (k + 1) + "  " + state + "  " + PartName(ch));
            }
            cats.Add(new KeyValuePair<string, List<string>>("Parachutes (" + chutes.Length + ")", chuteLines));

            // 轮子
            WheelModule[] wheels = r.partHolder.GetModules<WheelModule>();
            int[] wheelOrder = ProgramRunner.OrderedModuleIndices(r, wheels);
            List<string> wheelLines = new List<string>();
            for (int k = 0; k < wheelOrder.Length; k++)
            {
                WheelModule w = wheels[wheelOrder[k]];
                bool on = false;
                try { on = w.on.Value; } catch (Exception) { }
                badges.Add(new Badge { Text = "W" + (k + 1), Pos = w.transform.position, Color = on ? Color.white : new Color(0.55f, 0.55f, 0.55f), Width = 40f });
                wheelLines.Add("W" + (k + 1) + "  " + (on ? "on" : "off") + "  " + PartName(w));
            }
            cats.Add(new KeyValuePair<string, List<string>>("Wheels (" + wheels.Length + ")", wheelLines));

            // RCS
            RcsModule[] rcs = r.partHolder.GetModules<RcsModule>();
            int[] rcsOrder = ProgramRunner.OrderedModuleIndices(r, rcs);
            List<string> rcsLines = new List<string>();
            for (int k = 0; k < rcsOrder.Length; k++)
            {
                badges.Add(new Badge { Text = "R" + (k + 1), Pos = rcs[rcsOrder[k]].transform.position, Color = Color.white, Width = 40f });
                rcsLines.Add("R" + (k + 1) + "  " + PartName(rcs[rcsOrder[k]]));
            }
            cats.Add(new KeyValuePair<string, List<string>>("Rcs (" + rcs.Length + ")", rcsLines));

            // 分离器
            DetachModule[] detach = r.partHolder.GetModules<DetachModule>();
            int[] detachOrder = ProgramRunner.OrderedModuleIndices(r, detach);
            List<string> detachLines = new List<string>();
            for (int k = 0; k < detachOrder.Length; k++)
            {
                badges.Add(new Badge { Text = "D" + (k + 1), Pos = detach[detachOrder[k]].transform.position, Color = Color.white, Width = 40f });
                detachLines.Add("D" + (k + 1) + "  " + PartName(detach[detachOrder[k]]));
            }
            cats.Add(new KeyValuePair<string, List<string>>("Decouplers (" + detach.Length + ")", detachLines));

            // 折叠件：按部件名区分着陆腿 / 太阳能板 / 其它
            ToggleModule[] toggles = r.partHolder.GetModules<ToggleModule>();
            int[] toggleOrder = ProgramRunner.OrderedModuleIndices(r, toggles);
            List<string> legLines = new List<string>();
            List<string> solarLines = new List<string>();
            List<string> otherLines = new List<string>();
            int legNo = 0, solarNo = 0, otherNo = 0;
            for (int k = 0; k < toggleOrder.Length; k++)
            {
                ToggleModule t = toggles[toggleOrder[k]];
                Part part = t.GetComponent<Part>();
                string pname = part != null ? part.Name : "";
                bool deployed = false;
                try { deployed = t.state != null && t.state.targetTime.Value > 0f; } catch (Exception) { }

                if (pname.IndexOf("leg", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    legNo++;
                    badges.Add(new Badge { Text = "L" + legNo, Pos = t.transform.position, Color = deployed ? Color.white : new Color(0.55f, 0.55f, 0.55f), Width = 40f });
                    legLines.Add("L" + legNo + "  " + (deployed ? "down" : "up") + "  " + pname);
                }
                else if (pname.IndexOf("solar", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    solarNo++;
                    badges.Add(new Badge { Text = "A" + solarNo, Pos = t.transform.position, Color = deployed ? Color.white : new Color(0.55f, 0.55f, 0.55f), Width = 40f });
                    solarLines.Add("A" + solarNo + "  " + (deployed ? "open" : "closed") + "  " + pname);
                }
                else
                {
                    otherNo++;
                    badges.Add(new Badge { Text = "T" + otherNo, Pos = t.transform.position, Color = deployed ? Color.white : new Color(0.55f, 0.55f, 0.55f), Width = 40f });
                    otherLines.Add("T" + otherNo + "  " + (deployed ? "open" : "closed") + "  " + pname);
                }
            }
            cats.Add(new KeyValuePair<string, List<string>>("Legs (" + legLines.Count + ")", legLines));
            cats.Add(new KeyValuePair<string, List<string>>("Solar (" + solarLines.Count + ")", solarLines));
            if (otherLines.Count > 0)
                cats.Add(new KeyValuePair<string, List<string>>("Other folding (" + otherLines.Count + ")", otherLines));

            for (int i = 0; i < badges.Count; i++)
                DrawBadge(badges[i].Pos, badges[i].Text, badges[i].Color, badges[i].Width);

            DrawPanel(cats);
        }

        // 场上的火箭各画一个 #N，右上角顺便列出来（含高度 / 是否在跑物理）
        private void AddRockets(Rocket player, List<Badge> badges, List<KeyValuePair<string, List<string>>> cats)
        {
            List<Rocket> all = null;
            try { all = GameManager.main != null ? GameManager.main.rockets : null; }
            catch (Exception) { }
            if (all == null || all.Count == 0) return;

            List<string> lines = new List<string>();
            for (int i = 0; i < all.Count; i++)
            {
                Rocket r = all[i];
                if (r == null) continue;

                bool isPlayer = r == player;
                string name = string.IsNullOrEmpty(r.rocketName) ? "rocket" : r.rocketName;
                string alt = "?";
                bool simulated = false;
                Vector3 pos = Vector3.zero;
                bool hasPos = false;
                try
                {
                    alt = Short(r.location.Value.Height);
                    simulated = r.physics != null && r.physics.PhysicsMode;
                    pos = r.rb2d.transform.position;
                    hasPos = true;
                }
                catch (Exception) { }

                // 玩家那枚用黄色，别的用白色；在轨不模拟的调暗，免得以为是卡了
                Color col = isPlayer ? new Color(1f, 0.85f, 0.3f)
                                     : (simulated ? Color.white : new Color(0.62f, 0.72f, 0.85f));
                if (hasPos) badges.Add(new Badge { Text = "#" + (i + 1), Pos = pos, Color = col, Width = 40f });

                lines.Add("#" + (i + 1) + " " + name + "  " + alt
                    + (isPlayer ? "  you" : (simulated ? "" : "  rail")));
            }

            if (lines.Count > 0)
                cats.Add(new KeyValuePair<string, List<string>>("Rockets (" + lines.Count + ")", lines));
        }

        // 面板只有 236 宽，零件名截短一点；模组零件名字一般也够看
        private static string PartName(Component c)
        {
            if (c == null) return "";
            Part p = c.GetComponent<Part>();
            if (p == null || string.IsNullOrEmpty(p.Name)) return "";
            return p.Name.Length > 14 ? p.Name.Substring(0, 14) : p.Name;
        }

        private static string Short(double m)
        {
            if (double.IsNaN(m) || double.IsInfinity(m)) return "?";
            return Math.Abs(m) >= 1000.0
                ? (m / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "km"
                : m.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "m";
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

        private void DrawPanel(List<KeyValuePair<string, List<string>>> cats)
        {
            float w = 236f;
            float x = Screen.width - w - 14f;
            float y = 64f;

            int lines = 1;
            for (int i = 0; i < cats.Count; i++) lines += 1 + cats[i].Value.Count;

            float total = lineH * (lines + 2) + 14f;
            float maxH = Screen.height - y - 10f;
            if (total > maxH) total = maxH;

            GUI.DrawTexture(new Rect(x, y, w, total), blackTex);

            float cy = y + 7f;
            GUI.Label(new Rect(x + 10f, cy, w - 20f, lineH * 1.2f), "Part numbers (H)", headStyle);
            cy += lineH * 1.35f;

            for (int i = 0; i < cats.Count; i++)
            {
                if (cy + lineH > y + total) break;
                GUI.Label(new Rect(x + 10f, cy, w - 20f, lineH), cats[i].Key, headStyle);
                cy += lineH;

                List<string> items = cats[i].Value;
                for (int k = 0; k < items.Count && cy + lineH <= y + total; k++)
                {
                    GUI.Label(new Rect(x + 14f, cy, w - 24f, lineH), items[k], lineStyle);
                    cy += lineH;
                }
                cy += 2f;
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
