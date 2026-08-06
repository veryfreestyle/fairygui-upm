using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// 零资源的默认可视化: 一张 1x1 白贴图 + GUI.color 着色画十字与轨迹线,
    /// 圆环另生成一张 ring 贴图。fork 不能要求使用者提供美术资源。
    ///
    /// 只画不消费事件。标记不随会话结束清除 —— 截图是另一次独立调用, 隔了若干帧;
    /// 只有显式 Clear() 才清。用 unscaledTime 而非 Time.time (PlayMode 测试可能改 timeScale)。
    /// </summary>
    public sealed class ImguiInputVisualizer : MonoBehaviour, IStageInputVisualizer
    {
        struct Ripple
        {
            public Vector2 pos;
            public float startTime;
        }

        InputVisualStyle _style = new InputVisualStyle();
        IFrameClock _clock = UnityFrameClock.instance;

        Vector2 _pointer;
        bool _hasPointer;
        readonly List<Vector2> _trail = new List<Vector2>();
        readonly List<Ripple> _ripples = new List<Ripple>();
        readonly List<UnityEngine.Touch> _touches = new List<UnityEngine.Touch>();

        Texture2D _white;
        Texture2D _ring;
        int _ringRadius = -1;

        public InputVisualStyle style
        {
            get { return _style; }
            set { _style = value != null ? value : new InputVisualStyle(); }
        }

        /// <summary>时基。默认 UnityFrameClock; 测试可以换成可控时钟验衰减。</summary>
        public IFrameClock clock
        {
            get { return _clock; }
            set { _clock = value != null ? value : (IFrameClock)UnityFrameClock.instance; }
        }

        void Awake()
        {
            useGUILayout = false;
            _white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            _white.SetPixel(0, 0, Color.white);
            _white.Apply();
            _white.hideFlags = HideFlags.HideAndDontSave;
        }

        void OnDestroy()
        {
            if (_white != null) Destroy(_white);
            if (_ring != null) Destroy(_ring);
        }

        // ---------------- IStageInputVisualizer ----------------

        public void OnPointerMove(Vector2 screenPos)
        {
            _pointer = screenPos;
            _hasPointer = true;

            if (_style.showTrail)
            {
                _trail.Add(screenPos);
                while (_trail.Count > _style.trailLength)
                    _trail.RemoveAt(0);
            }
        }

        public void OnPointerDown(Vector2 screenPos, int button)
        {
            _pointer = screenPos;
            _hasPointer = true;
            _ripples.Add(new Ripple { pos = screenPos, startTime = _clock.unscaledTime });
        }

        public void OnPointerUp(Vector2 screenPos, int button)
        {
            _pointer = screenPos;
            _hasPointer = true;
        }

        public void OnTouches(IList<UnityEngine.Touch> touches)
        {
            _touches.Clear();
            if (touches == null) return;
            for (int i = 0; i < touches.Count; i++)
            {
                _touches.Add(touches[i]);
                if (touches[i].phase == TouchPhase.Began)
                    _ripples.Add(new Ripple { pos = touches[i].position, startTime = _clock.unscaledTime });
            }
        }

        public void Clear()
        {
            _hasPointer = false;
            _trail.Clear();
            _ripples.Clear();
            _touches.Clear();
        }

        public void Dispose()
        {
            Clear();
            if (this != null && gameObject != null)
                Destroy(gameObject);
        }

        // ---------------- 绘制 ----------------

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;

            Color saved = GUI.color;

            DrawRipples();
            if (_style.showTrail) DrawTrail();
            if (_hasPointer) DrawCursor(_pointer, _style.cursorColor);
            DrawTouches();

            GUI.color = saved;
        }

        void DrawRipples()
        {
            float now = _clock.unscaledTime;
            for (int i = _ripples.Count - 1; i >= 0; i--)
            {
                float age = now - _ripples[i].startTime;
                if (age > _style.pressFadeSeconds) { _ripples.RemoveAt(i); continue; }

                float t = age / _style.pressFadeSeconds;
                float radius = Mathf.Lerp(4f, _style.pressRingMaxRadius, t);
                Color c = _style.pressColor;
                c.a *= 1f - t;
                DrawRing(_ripples[i].pos, radius, c);
            }
        }

        void DrawTrail()
        {
            GUI.color = _style.trailColor;
            for (int i = 1; i < _trail.Count; i++)
                DrawLine(_trail[i - 1], _trail[i], _style.lineWidth);
        }

        void DrawTouches()
        {
            for (int i = 0; i < _touches.Count; i++)
            {
                Vector2 p = _touches[i].position;
                DrawCursor(p, _style.cursorColor);
                GUI.color = Color.white;
                GUI.Label(new Rect(p.x + 8f, ToGuiY(p.y) - 8f, 40f, 20f), _touches[i].fingerId.ToString());
            }
        }

        void DrawCursor(Vector2 screenPos, Color color)
        {
            GUI.color = color;
            float s = _style.cursorSize;

            switch (_style.cursorShape)
            {
                case CursorShape.Dot:
                    GUI.DrawTexture(new Rect(screenPos.x - s * 0.25f, ToGuiY(screenPos.y) - s * 0.25f,
                                             s * 0.5f, s * 0.5f), _white);
                    break;

                case CursorShape.Ring:
                    DrawRing(screenPos, s * 0.5f, color);
                    break;

                default:   // Crosshair
                    float w = _style.lineWidth;
                    GUI.DrawTexture(new Rect(screenPos.x - s * 0.5f, ToGuiY(screenPos.y) - w * 0.5f, s, w), _white);
                    GUI.DrawTexture(new Rect(screenPos.x - w * 0.5f, ToGuiY(screenPos.y) - s * 0.5f, w, s), _white);
                    break;
            }
        }

        void DrawRing(Vector2 screenPos, float radius, Color color)
        {
            EnsureRing(Mathf.Max(2, Mathf.CeilToInt(radius)));
            GUI.color = color;
            GUI.DrawTexture(new Rect(screenPos.x - radius, ToGuiY(screenPos.y) - radius,
                                     radius * 2f, radius * 2f), _ring);
        }

        // 只有半径分辨率变了才重生成。
        void EnsureRing(int radius)
        {
            if (_ring != null && _ringRadius == radius) return;

            if (_ring != null) Destroy(_ring);
            _ringRadius = radius;

            int size = radius * 2;
            _ring = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _ring.hideFlags = HideFlags.HideAndDontSave;

            var pixels = new Color32[size * size];
            float outer = radius;
            float inner = Mathf.Max(1f, radius - 2f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - radius + 0.5f;
                    float dy = y - radius + 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    bool on = d <= outer && d >= inner;
                    pixels[y * size + x] = on ? new Color32(255, 255, 255, 255) : new Color32(255, 255, 255, 0);
                }
            }
            _ring.SetPixels32(pixels);
            _ring.Apply();
        }

        void DrawLine(Vector2 a, Vector2 b, float width)
        {
            Vector2 ga = new Vector2(a.x, ToGuiY(a.y));
            Vector2 gb = new Vector2(b.x, ToGuiY(b.y));
            Vector2 d = gb - ga;
            float len = d.magnitude;
            if (len < 0.01f) return;

            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, ga);
            GUI.DrawTexture(new Rect(ga.x, ga.y - width * 0.5f, len, width), _white);
            GUI.matrix = saved;
        }

        // 注入坐标是 Unity 屏幕系(左下原点), OnGUI 是左上原点。
        static float ToGuiY(float screenY)
        {
            return Screen.height - screenY;
        }
    }
}
