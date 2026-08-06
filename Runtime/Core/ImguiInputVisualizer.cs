using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// 零资源的默认可视化: 十字光标复用一张 1x1 白贴图 + GUI.color 叠色画描边与填充两层;
    /// 按压圆环、圆点光标、箭头指针各自程序生成一张贴图(圆点与箭头的填充/描边色直接烤进
    /// 贴图, 不叠色)。fork 不能要求使用者提供美术资源。
    ///
    /// 只画不消费事件。标记不随会话结束清除 —— 截图是另一次独立调用, 隔了若干帧;
    /// 只有显式 Clear() 才清。用 unscaledTime 而非 Time.time (PlayMode 测试可能改 timeScale)。
    /// </summary>
    public sealed class ImguiInputVisualizer : MonoBehaviour, IStageInputVisualizer
    {
        /// <summary>
        /// 一次按压。upTime &lt; 0 表示还按着 —— held 期间圆环不衰减、跟随光标, 抬起后才淡出。
        /// 圆环因此表达"正按着"的状态, 而不是"某一帧按下过"的事件: 按住不抬 / 按住拖拽
        /// 在中途截图里读得出来。
        /// </summary>
        struct PressMarker
        {
            public int button;
            public Vector2 pos;
            public float upTime;
        }

        /// <summary>
        /// 某一刻一个按压圆环的画法。BuildRing 算出来, 绘制与查询共用同一份。
        /// radius 是**样式空间**的半径(与 pressRingHoldRadius / pressRingMaxRadius 同一单位),
        /// 不含 ContentScale —— 乘 scale 是绘制时的换算, 混进来会让这份数据依赖 GRoot 是否存在。
        /// </summary>
        public struct PressRing
        {
            public int button;
            public Vector2 pos;
            public bool held;      // 还按着: 半径固定、alpha 不掉、跟随光标
            public float radius;
            public float alpha;
        }

        // 箭头指针轮廓, 24x24 视口坐标(左上原点, y 向下), 尖端在 (4.5, 3.5)。
        static readonly Vector2[] ArrowPolygon = new Vector2[]
        {
            new Vector2(4.5f, 3.5f),
            new Vector2(11f, 20.5f),
            new Vector2(14.2f, 14.2f),
            new Vector2(20.5f, 11f)
        };
        const float ArrowViewportSize = 24f;

        InputVisualStyle _style = InputVisualStyle.Default();
        IFrameClock _clock = UnityFrameClock.instance;

        Vector2 _pointer;
        bool _hasPointer;
        readonly List<PressMarker> _presses = new List<PressMarker>();
        readonly List<UnityEngine.Touch> _touches = new List<UnityEngine.Touch>();

        Texture2D _white;
        Texture2D _ring;
        const int RingTextureSize = 64;   // 固定尺寸生成一次, 绘制时靠 Rect 缩放, 半径连续变化不重建贴图

        // Dot / Arrow 的颜色是烤进贴图的(不是靠 GUI.color 叠色), 缓存键要带上颜色与描边宽度。
        Texture2D _dot;
        int _dotSize = -1;
        Color _dotFillColor;
        Color _dotBorderColor;
        float _dotBorderWidth = -1f;

        Texture2D _arrow;
        int _arrowSize = -1;
        Color _arrowFillColor;
        Color _arrowBorderColor;
        float _arrowBorderWidth = -1f;

        // 触摸点是纯色半透明实心圆, 跟光标的 Dot 分开一份贴图缓存(语义、颜色来源都不同)。
        Texture2D _touchDot;
        int _touchDotSize = -1;
        Color _touchDotColor;

        public InputVisualStyle style
        {
            get { return _style; }
            set { _style = value; }
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
            // _white 在 Awake() 里无条件生成, EditMode 下 AddComponent 也会触发 Awake ——
            // 所以这里同 Dispose() 一样要按 Application.isPlaying 分流 Destroy/DestroyImmediate,
            // 不能假设 OnDestroy 只会在 Play 模式下跑到。
            DestroyTexture(_white);
            DestroyTexture(_ring);
            DestroyTexture(_dot);
            DestroyTexture(_arrow);
            DestroyTexture(_touchDot);
        }

        static void DestroyTexture(Texture2D tex)
        {
            if (tex == null) return;
            if (Application.isPlaying) Destroy(tex);
            else DestroyImmediate(tex);
        }

        // ---------------- IStageInputVisualizer ----------------

        // held 的圆环跟着光标走 —— 按住拖拽时圆环留在按下点会读成"点了一下又移开"。
        public void OnPointerMove(Vector2 screenPos)
        {
            _pointer = screenPos;
            _hasPointer = true;

            for (int i = 0; i < _presses.Count; i++)
            {
                if (_presses[i].upTime >= 0f) continue;   // 已抬起的钉在抬起点, 不跟随
                PressMarker m = _presses[i];
                m.pos = screenPos;
                _presses[i] = m;
            }
        }

        public void OnPointerDown(Vector2 screenPos, int button)
        {
            _pointer = screenPos;
            _hasPointer = true;
            _presses.Add(new PressMarker
            {
                button = button,
                pos = screenPos,
                upTime = -1f
            });
        }

        // 没有配对 Down 的 Up 什么都不做 —— 凭空造一个 marker 就是画一个从没按下过的环。
        public void OnPointerUp(Vector2 screenPos, int button)
        {
            _pointer = screenPos;
            _hasPointer = true;

            for (int i = _presses.Count - 1; i >= 0; i--)
            {
                if (_presses[i].button != button || _presses[i].upTime >= 0f) continue;
                PressMarker m = _presses[i];
                m.pos = screenPos;
                m.upTime = _clock.unscaledTime;
                _presses[i] = m;
                return;
            }
        }

        // 触摸落指不叠加鼠标风格的按压圆环(pressColor/pressUpFadeSeconds) ——
        // 触摸点自己的可视化(DrawTouches: 半透明圆点 + 多指连线)已经够用, 叠两套显得乱。
        public void OnTouches(IList<UnityEngine.Touch> touches)
        {
            _touches.Clear();
            if (touches == null) return;
            for (int i = 0; i < touches.Count; i++)
                _touches.Add(touches[i]);
        }

        public void Clear()
        {
            _hasPointer = false;
            _presses.Clear();
            _touches.Clear();
        }

        /// <summary>此刻还会被画出来的按压圆环数(held 的 + 还没淡完的)。</summary>
        public int CountVisiblePressMarkers()
        {
            float now = _clock.unscaledTime;
            int count = 0;
            for (int i = 0; i < _presses.Count; i++)
                if (IsVisible(_presses[i], now)) count++;
            return count;
        }

        /// <summary>
        /// 取该按键最近一个还画着的圆环, 内容与 OnGUI 这一刻画出来的一致。
        /// EditMode 测试跑不到 OnGUI, 没有这个查询, 跟随/半径/淡出只能靠截图肉眼判读。
        /// </summary>
        public bool TryGetPressRing(int button, out PressRing ring)
        {
            float now = _clock.unscaledTime;
            for (int i = _presses.Count - 1; i >= 0; i--)
            {
                if (_presses[i].button != button || !IsVisible(_presses[i], now)) continue;
                ring = BuildRing(_presses[i], now);
                return true;
            }

            ring = new PressRing();
            return false;
        }

        bool IsVisible(PressMarker m, float now)
        {
            if (m.upTime < 0f) return true;                       // 按着就一直画
            if (_style.pressUpFadeSeconds <= 0f) return false;     // 不淡出, 抬起即消失
            return now - m.upTime < _style.pressUpFadeSeconds;
        }

        /// <summary>
        /// held: 半径固定在 pressRingHoldRadius, alpha 不掉 —— 表达"正按着"。
        /// 抬起后: 在 pressUpFadeSeconds 内半径从 hold 扩到 pressRingMaxRadius、alpha 掉到 0,
        /// 也就是"松手弹开"。扩张属于抬起而非按下: 按下那一刻就扩完的话, 按住期间反而没有
        /// 稳定形态可读。
        /// </summary>
        PressRing BuildRing(PressMarker m, float now)
        {
            PressRing ring;
            ring.button = m.button;
            ring.pos = m.pos;
            ring.held = m.upTime < 0f;

            if (ring.held)
            {
                ring.radius = _style.pressRingHoldRadius;
                ring.alpha = _style.pressColor.a;
                return ring;
            }

            float t = Mathf.Clamp01((now - m.upTime) / _style.pressUpFadeSeconds);
            ring.radius = Mathf.Lerp(_style.pressRingHoldRadius, _style.pressRingMaxRadius, t);
            ring.alpha = _style.pressColor.a * (1f - t);
            return ring;
        }

        public void Dispose()
        {
            Clear();
            if (this == null || gameObject == null) return;

            // Destroy() 在 EditMode 下会抛 "may not be called from edit mode"。
            // UseDefaultVisualizer() 明确支持 EditMode 使用(懒建时按 Application.isPlaying
            // 决定要不要 DontDestroyOnLoad), Dispose() 也要对称处理两种模式。
            if (Application.isPlaying)
                Destroy(gameObject);
            else
                DestroyImmediate(gameObject);
        }

        // ---------------- 绘制 ----------------

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;

            Color saved = GUI.color;

            DrawPressMarkers();
            if (_hasPointer) DrawCursor(_pointer, _style.cursorColor);
            DrawTouches();

            GUI.color = saved;
        }

        /// <summary>
        /// 半径与 alpha 的算法见 BuildRing。ring.radius 是样式空间的, 画之前才乘 ContentScale。
        /// 淡完的顺手从列表里摘掉(倒序遍历, 边画边删安全)。
        /// </summary>
        void DrawPressMarkers()
        {
            float scale = ContentScale();
            float now = _clock.unscaledTime;
            for (int i = _presses.Count - 1; i >= 0; i--)
            {
                if (!IsVisible(_presses[i], now)) { _presses.RemoveAt(i); continue; }

                PressRing ring = BuildRing(_presses[i], now);
                Color c = _style.pressColor;
                c.a = ring.alpha;
                DrawRing(ring.pos, ring.radius * scale, c);
            }
        }

        /// <summary>
        /// UI 内容相对屏幕像素的缩放系数, 跟 GRoot 的设计分辨率走 ——
        /// 不同 Demo/不同实际分辨率下光标看起来的相对大小才能保持一致。
        /// 用内部字段 GRoot._inst 而不是 GRoot.inst, 避免在还没有 GRoot 的场合
        /// (比如纯 EditMode 测试)把它意外建出来。
        /// </summary>
        static float ContentScale()
        {
            return GRoot._inst != null ? GRoot._inst.scale.x : 1f;
        }

        /// <summary>
        /// 触摸点用手指触摸的语义画(半透明实心圆), 不借 DrawCursor 的鼠标指针形状 ——
        /// 捏合/旋转是双指以上的手势, 常见可视化习惯是触点之间连一条线, 直观看出间距/角度变化。
        /// </summary>
        void DrawTouches()
        {
            if (_touches.Count == 0) return;

            float scale = ContentScale();
            float radius = _style.touchRadius * scale;

            if (_style.showTouchConnectors && _touches.Count >= 2)
            {
                float connectorWidth = _style.touchConnectorWidth * scale;
                for (int i = 0; i < _touches.Count; i++)
                    for (int j = i + 1; j < _touches.Count; j++)
                        DrawLine(_touches[i].position, _touches[j].position, connectorWidth, _style.touchConnectorColor);
            }

            for (int i = 0; i < _touches.Count; i++)
            {
                Vector2 p = _touches[i].position;
                DrawTouchPoint(p, radius);
                GUI.color = Color.white;
                GUI.Label(new Rect(p.x + radius + 4f, ToGuiY(p.y) - 10f, 40f, 20f), _touches[i].fingerId.ToString());
            }
        }

        void DrawTouchPoint(Vector2 screenPos, float radius)
        {
            EnsureTouchDot(Mathf.Max(2, Mathf.RoundToInt(radius * 2f)), _style.touchColor);
            GUI.color = Color.white;   // 颜色已经烤进贴图
            GUI.DrawTexture(new Rect(screenPos.x - radius, ToGuiY(screenPos.y) - radius, radius * 2f, radius * 2f), _touchDot);
        }

        void DrawLine(Vector2 a, Vector2 b, float width, Color color)
        {
            Vector2 ga = new Vector2(a.x, ToGuiY(a.y));
            Vector2 gb = new Vector2(b.x, ToGuiY(b.y));
            Vector2 d = gb - ga;
            float len = d.magnitude;
            if (len < 0.01f) return;

            GUI.color = color;
            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            Matrix4x4 saved = GUI.matrix;
            GUIUtility.RotateAroundPivot(angle, ga);
            GUI.DrawTexture(new Rect(ga.x, ga.y - width * 0.5f, len, width), _white);
            GUI.matrix = saved;
        }

        void DrawCursor(Vector2 screenPos, Color color)
        {
            float scale = ContentScale();
            float s = _style.cursorSize * scale;

            Color fill = color;
            Color border = _style.cursorBorderColor;

            switch (_style.cursorShape)
            {
                case CursorShape.Dot:
                    DrawDot(screenPos, s, fill, border, scale);
                    break;

                case CursorShape.Arrow:
                    DrawArrow(screenPos, s, fill, border, scale);
                    break;

                default:   // Crosshair
                    DrawCrosshair(screenPos, s, fill, border, scale);
                    break;
            }
        }

        /// <summary>
        /// 十字光标。描边用"外层画粗一圈描边色, 内层再画正常宽度的填充色"两层叠色实现,
        /// 复用 1x1 白贴图, 不用另生成贴图。
        /// </summary>
        void DrawCrosshair(Vector2 screenPos, float s, Color fillColor, Color borderColor, float scale)
        {
            float w = _style.lineWidth * scale;
            float bw = _style.cursorBorderWidth * scale;

            if (bw > 0f)
            {
                GUI.color = borderColor;
                float ow = w + bw * 2f;
                float ol = s + bw * 2f;
                GUI.DrawTexture(new Rect(screenPos.x - ol * 0.5f, ToGuiY(screenPos.y) - ow * 0.5f, ol, ow), _white);
                GUI.DrawTexture(new Rect(screenPos.x - ow * 0.5f, ToGuiY(screenPos.y) - ol * 0.5f, ow, ol), _white);
            }

            GUI.color = fillColor;
            GUI.DrawTexture(new Rect(screenPos.x - s * 0.5f, ToGuiY(screenPos.y) - w * 0.5f, s, w), _white);
            GUI.DrawTexture(new Rect(screenPos.x - w * 0.5f, ToGuiY(screenPos.y) - s * 0.5f, w, s), _white);
        }

        /// <summary>圆点光标, 填充色 + 描边色都烤进贴图(贴图是圆对称的, 不用管上下翻转)。</summary>
        void DrawDot(Vector2 screenPos, float size, Color fillColor, Color borderColor, float scale)
        {
            EnsureDot(Mathf.Max(2, Mathf.RoundToInt(size)), fillColor, borderColor, _style.cursorBorderWidth * scale);
            GUI.color = Color.white;   // 颜色已经烤进贴图, 这里不再叠色
            GUI.DrawTexture(new Rect(screenPos.x - size * 0.5f, ToGuiY(screenPos.y) - size * 0.5f, size, size), _dot);
        }

        /// <summary>
        /// 箭头指针。热点(代表实际注入坐标的那个点)是箭头尖端, 不是贴图中心 ——
        /// 与真实鼠标光标的习惯一致, 尖端对准 screenPos。填充色 + 描边色烤进贴图。
        /// </summary>
        void DrawArrow(Vector2 screenPos, float size, Color fillColor, Color borderColor, float scale)
        {
            EnsureArrow(Mathf.Max(2, Mathf.RoundToInt(size)), fillColor, borderColor, _style.cursorBorderWidth * scale);
            GUI.color = Color.white;

            float tipX = size * (ArrowPolygon[0].x / ArrowViewportSize);
            float tipY = size * (ArrowPolygon[0].y / ArrowViewportSize);
            GUI.DrawTexture(new Rect(screenPos.x - tipX, ToGuiY(screenPos.y) - tipY, size, size), _arrow);
        }

        void DrawRing(Vector2 screenPos, float radius, Color color)
        {
            EnsureRing();
            GUI.color = color;
            GUI.DrawTexture(new Rect(screenPos.x - radius, ToGuiY(screenPos.y) - radius,
                                     radius * 2f, radius * 2f), _ring);
        }

        /// <summary>
        /// 按压圆环的半径按 Lerp(4, pressRingMaxRadius, t) 连续变化, 若贴图按半径生成,
        /// Mathf.CeilToInt(radius) 几乎每帧都变、缓存 key 跟着变, 衰减期间每帧都要
        /// Destroy + new Texture2D + 一遍 O(size^2) 像素循环。改成固定尺寸只生成一次,
        /// 缩放交给 GUI.DrawTexture 的目标 Rect —— 反正原来就是缩放绘制。
        /// </summary>
        void EnsureRing()
        {
            if (_ring != null) return;

            int size = RingTextureSize;
            _ring = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _ring.hideFlags = HideFlags.HideAndDontSave;

            var pixels = new Color32[size * size];
            float radius = size / 2f;
            float outer = radius;
            float inner = Mathf.Max(1f, radius - size * 0.08f);
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

        // 圆形轴对称, 上下翻转不影响外观, 不用像箭头那样转 texY。
        void EnsureDot(int size, Color fillColor, Color borderColor, float borderWidth)
        {
            if (_dot != null && _dotSize == size && _dotFillColor == fillColor
                && _dotBorderColor == borderColor && _dotBorderWidth == borderWidth)
                return;

            if (_dot != null) Destroy(_dot);
            _dotSize = size;
            _dotFillColor = fillColor;
            _dotBorderColor = borderColor;
            _dotBorderWidth = borderWidth;

            _dot = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _dot.hideFlags = HideFlags.HideAndDontSave;

            float radius = size / 2f;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                float dy = y - radius + 0.5f;
                for (int x = 0; x < size; x++)
                {
                    float dx = x - radius + 0.5f;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);

                    Color32 c;
                    if (d > radius) c = new Color32(0, 0, 0, 0);
                    else if (borderWidth > 0f && d >= radius - borderWidth) c = borderColor;
                    else c = fillColor;

                    pixels[y * size + x] = c;
                }
            }
            _dot.SetPixels32(pixels);
            _dot.Apply();
        }

        // 纯色实心圆, 不像光标的 Dot 那样有描边 —— 手指触摸反馈通常就是一片半透明色块。
        void EnsureTouchDot(int size, Color color)
        {
            if (_touchDot != null && _touchDotSize == size && _touchDotColor == color)
                return;

            if (_touchDot != null) Destroy(_touchDot);
            _touchDotSize = size;
            _touchDotColor = color;

            _touchDot = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _touchDot.hideFlags = HideFlags.HideAndDontSave;

            float radius = size / 2f;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                float dy = y - radius + 0.5f;
                for (int x = 0; x < size; x++)
                {
                    float dx = x - radius + 0.5f;
                    bool inside = (dx * dx + dy * dy) <= radius * radius;
                    pixels[y * size + x] = inside ? (Color32)color : new Color32(0, 0, 0, 0);
                }
            }
            _touchDot.SetPixels32(pixels);
            _touchDot.Apply();
        }

        // 只有像素边长/颜色/描边宽度变了才重生成。贴图行 0 对应 Texture2D 底部约定,
        // 视口 y=0(顶部)写到贴图顶部那一行, 靠 texY 翻转对齐 —— 这样 GUI.DrawTexture
        // 显示时箭头朝向与 SVG 源图一致(尖端在左上)。填充色/描边色直接烤进贴图,
        // 不再靠 GUI.color 叠色, 所以缓存键要带上这两个颜色。
        void EnsureArrow(int size, Color fillColor, Color borderColor, float borderWidth)
        {
            if (_arrow != null && _arrowSize == size && _arrowFillColor == fillColor
                && _arrowBorderColor == borderColor && _arrowBorderWidth == borderWidth)
                return;

            if (_arrow != null) Destroy(_arrow);
            _arrowSize = size;
            _arrowFillColor = fillColor;
            _arrowBorderColor = borderColor;
            _arrowBorderWidth = borderWidth;

            _arrow = new Texture2D(size, size, TextureFormat.RGBA32, false);
            _arrow.hideFlags = HideFlags.HideAndDontSave;

            float scale = size / ArrowViewportSize;
            var pixelPoly = new Vector2[ArrowPolygon.Length];
            for (int i = 0; i < ArrowPolygon.Length; i++)
                pixelPoly[i] = ArrowPolygon[i] * scale;

            var pixels = new Color32[size * size];
            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    Vector2 p = new Vector2(px + 0.5f, py + 0.5f);

                    Color32 c;
                    if (borderWidth > 0f && DistanceToPolygonEdge(p, pixelPoly) <= borderWidth * 0.5f)
                        c = borderColor;
                    else if (PointInPolygon(p, pixelPoly))
                        c = fillColor;
                    else
                        c = new Color32(0, 0, 0, 0);

                    int texY = size - 1 - py;
                    pixels[texY * size + px] = c;
                }
            }
            _arrow.SetPixels32(pixels);
            _arrow.Apply();
        }

        static bool PointInPolygon(Vector2 p, Vector2[] poly)
        {
            bool inside = false;
            int n = poly.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                Vector2 a = poly[i], b = poly[j];
                if ((a.y > p.y) != (b.y > p.y))
                {
                    float t = (p.y - a.y) / (b.y - a.y);
                    float xCross = a.x + t * (b.x - a.x);
                    if (p.x < xCross) inside = !inside;
                }
            }
            return inside;
        }

        static float DistanceToPolygonEdge(Vector2 p, Vector2[] poly)
        {
            float min = float.MaxValue;
            int n = poly.Length;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float d = DistancePointToSegment(p, poly[j], poly[i]);
                if (d < min) min = d;
            }
            return min;
        }

        static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Vector2.Dot(p - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude);
            t = Mathf.Clamp01(t);
            Vector2 closest = a + t * ab;
            return Vector2.Distance(p, closest);
        }

        // 注入坐标是 Unity 屏幕系(左下原点), OnGUI 是左上原点。
        static float ToGuiY(float screenY)
        {
            return Screen.height - screenY;
        }
    }
}
