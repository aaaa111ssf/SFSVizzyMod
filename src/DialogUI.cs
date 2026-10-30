using System.Globalization;
using SFS.Input;
using UnityEngine;

namespace SFSVizzyMod
{
    // 游戏内弹窗/输入框：黑底白框，供 dialog 积木使用
    public class DialogUI : MonoBehaviour
    {
        public static DialogUI main;

        public bool IsOpen { get; private set; }

        private string title = "";
        private string body = "";
        private bool numeric;
        private string text = "";
        private double result;
        private bool hasResult;
        private float deadline;
        private bool cursorVisibleBefore;
        private CursorLockMode lockBefore;
        private float timeScaleBefore = 1f;
        private bool timeScaleSaved;
        private DialogBlocker blocker;
        private GUIStyle placeholderStyle;

        private Texture2D darkTex;
        private Texture2D whiteTex;
        private Texture2D shadeTex;

        private GUIStyle titleStyle;
        private GUIStyle bodyStyle;
        private GUIStyle inputStyle;
        private GUIStyle buttonStyle;
        private GUIStyle hintStyle;
        private bool focusOnce;

        private void Awake()
        {
            main = this;
            darkTex = Solid(new Color(0.05f, 0.05f, 0.05f, 1f));
            whiteTex = Solid(Color.white);
            shadeTex = Solid(new Color(0f, 0f, 0f, 0.55f));
        }

        private static Texture2D Solid(Color c)
        {
            Texture2D t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        public void Open(string t, string b, bool num, float timeout)
        {
            title = t ?? "";
            body = b ?? "";
            numeric = num;
            text = "";
            hasResult = false;
            focusOnce = false;
            IsOpen = true;
            deadline = timeout > 0f ? Time.unscaledTime + timeout : 0f;

            cursorVisibleBefore = Cursor.visible;
            lockBefore = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            if (ScreenManager.main != null && ScreenManager.main.CurrentScreen != null)
            {
                GameObject go = new GameObject("SFSVizzyDialogBlocker");
                blocker = go.AddComponent<DialogBlocker>();
                ScreenManager.main.OpenScreen(() => blocker);
            }
            else
            {
                if (!timeScaleSaved)
                {
                    timeScaleBefore = Time.timeScale;
                    timeScaleSaved = true;
                }
                Time.timeScale = 0f;
            }
        }

        public void Close(bool ok)
        {
            if (!IsOpen) return;
            IsOpen = false;

            if (ok)
            {
                if (numeric)
                {
                    double v;
                    if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    {
                        result = v;
                        hasResult = true;
                    }
                }
                else hasResult = true;
            }

            Cursor.visible = cursorVisibleBefore;
            Cursor.lockState = lockBefore;

            if (blocker != null)
            {
                if (ScreenManager.main != null && ScreenManager.main.GetStackCount() > 1)
                    ScreenManager.main.CloseCurrent();
                Destroy(blocker.gameObject);
                blocker = null;
            }

            if (timeScaleSaved)
            {
                Time.timeScale = timeScaleBefore;
                timeScaleSaved = false;
            }
        }

        public bool TryTake(out double value)
        {
            value = result;
            bool ok = hasResult;
            hasResult = false;
            return ok;
        }

        private void OnDestroy()
        {
            if (blocker != null)
            {
                if (ScreenManager.main != null && ScreenManager.main.GetStackCount() > 1)
                    ScreenManager.main.CloseCurrent();
                Destroy(blocker.gameObject);
                blocker = null;
            }

            if (timeScaleSaved)
            {
                Time.timeScale = timeScaleBefore;
                timeScaleSaved = false;
            }
        }

        private void Update()
        {
            if (!IsOpen) return;
            if (deadline > 0f && Time.unscaledTime > deadline) Close(false);
        }

        private void OnGUI()
        {
            if (!IsOpen) return;

            float s = Mathf.Max(1f, Screen.height / 1080f);
            EnsureStyles(s);

            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), shadeTex);

            float w = Mathf.Min(640f * s, Screen.width * 0.72f);
            float h = Mathf.Min(360f * s, Screen.height * 0.72f);
            Rect box = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);

            GUI.DrawTexture(box, whiteTex);
            Rect inner = new Rect(box.x + 2f, box.y + 2f, box.width - 4f, box.height - 4f);
            GUI.DrawTexture(inner, darkTex);

            float pad = 22f * s;
            float y = inner.y + pad;

            GUI.Label(new Rect(inner.x + pad, y, inner.width - pad * 2f, 34f * s), title, titleStyle);
            y += 40f * s;

            GUI.Label(new Rect(inner.x + pad, y, inner.width - pad * 2f, h * 0.45f), body, bodyStyle);
            y += h * 0.42f;

            if (numeric)
            {
                Rect field = new Rect(inner.x + pad, y, inner.width - pad * 2f, 34f * s);
                GUI.SetNextControlName("vizzyInput");
                string next = GUI.TextField(field, text, 24, inputStyle);
                if (next != text) text = Filter(next);

                if (string.IsNullOrEmpty(text) && placeholderStyle != null)
                {
                    GUI.Label(new Rect(field.x + 10f * s, field.y, field.width - 20f * s, field.height),
                        "Enter a number...", placeholderStyle);
                }

                if (!focusOnce)
                {
                    GUI.FocusControl("vizzyInput");
                    focusOnce = true;
                }
                y += 48f * s;
            }

            float bw = 110f * s;
            float bh = 32f * s;
            if (GUI.Button(new Rect(inner.x + inner.width - pad - bw * 2f - 10f * s, inner.y + inner.height - pad - bh, bw, bh), "CANCEL", buttonStyle))
                Close(false);
            if (GUI.Button(new Rect(inner.x + inner.width - pad - bw, inner.y + inner.height - pad - bh, bw, bh), "OK", buttonStyle))
                Close(true);

            Event e = Event.current;
            if (e != null && (e.type == EventType.MouseDown || e.type == EventType.MouseUp
                || e.type == EventType.MouseDrag || e.type == EventType.ScrollWheel)
                && !box.Contains(e.mousePosition))
            {
                e.Use();
            }

            if (e != null && e.type == EventType.KeyDown)
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { Close(true); e.Use(); }
                else if (e.keyCode == KeyCode.Escape) { Close(false); e.Use(); }
            }
        }

        private string Filter(string v)
        {
            if (!numeric) return v;
            string outp = "";
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (char.IsDigit(c) || c == '.' || (c == '-' && outp.Length == 0)) outp += c;
            }
            return outp;
        }

        private void EnsureStyles(float s)
        {
            if (titleStyle != null && Mathf.Abs(titleStyle.fontSize - Mathf.RoundToInt(26f * s)) < 1) return;

            int big = Mathf.RoundToInt(26f * s);
            int normal = Mathf.RoundToInt(17f * s);
            int small = Mathf.RoundToInt(14f * s);

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = big,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip
            };

            bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = normal,
                wordWrap = true,
                normal = { textColor = new Color(0.86f, 0.86f, 0.86f) },
                alignment = TextAnchor.UpperLeft
            };

            hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = small,
                normal = { textColor = new Color(0.65f, 0.65f, 0.65f) },
                alignment = TextAnchor.MiddleLeft
            };

            inputStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = normal,
                padding = new RectOffset(8, 8, 5, 5),
                normal = { background = whiteTex, textColor = Color.black },
                focused = { background = whiteTex, textColor = Color.black },
                hover = { background = whiteTex, textColor = Color.black },
                active = { background = whiteTex, textColor = Color.black }
            };

            placeholderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = normal,
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.55f, 0.55f, 0.55f) }
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = normal,
                fontStyle = FontStyle.Bold,
                normal = { background = whiteTex, textColor = Color.black },
                hover = { background = whiteTex, textColor = Color.black },
                active = { background = whiteTex, textColor = Color.black }
            };
        }
    }
}
