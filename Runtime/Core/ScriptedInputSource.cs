using System;
using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// 可编程的输入源。精确复现 Legacy Input 的单帧语义:
    /// GetMouseButtonDown/Up 在设置的那一帧内多次读取结果一致, 下一帧翻假。
    /// 契约: 驱动方只在帧边界改状态。FairyGUI 一帧内会多次读同一份,
    /// 帧中途改会让 hit-test 与事件分发看到不一致的输入。
    /// </summary>
    public sealed class ScriptedInputSource : IStageInputSource
    {
        readonly IFrameClock _clock;
        readonly int[] _downFrame = new int[3] { -1, -1, -1 };
        readonly int[] _upFrame = new int[3] { -1, -1, -1 };
        // Down/Up 是单帧边沿, 读侧不需要"按住"这个状态; 记它只为 ResetAll 能给
        // visualizer 补一次 Up —— 否则会话结束后 held 圆环会一直画着谎报"还按着"。
        readonly bool[] _mouseHeld = new bool[3];
        readonly HashSet<KeyCode> _held = new HashSet<KeyCode>();
        readonly List<UnityEngine.Touch> _touches = new List<UnityEngine.Touch>();
        Vector2 _mousePos;
        string _composition = string.Empty;

        public ScriptedInputSource(IFrameClock clock)
        {
            _clock = clock != null ? clock : (IFrameClock)UnityFrameClock.instance;
        }

        public ScriptedInputSource() : this(null)
        {
        }

        public IFrameClock clock { get { return _clock; } }

        /// <summary>可视化, 为 null 时不推送。持有点在 source 上, 让 StageInputPlayer 不依赖静态门面。</summary>
        public IStageInputVisualizer visualizer { get; set; }

        // ---------------- 读侧: 帧内稳定 ----------------

        public bool touchSupported { get { return true; } }
        public Vector2 mousePosition { get { return _mousePos; } }
        public bool GetMouseButtonDown(int button) { return _downFrame[button] == _clock.frameCount; }
        public bool GetMouseButtonUp(int button) { return _upFrame[button] == _clock.frameCount; }
        public int touchCount { get { return _touches.Count; } }
        public UnityEngine.Touch GetTouch(int index) { return _touches[index]; }
        public bool GetKey(KeyCode key) { return _held.Contains(key); }
        public string compositionString { get { return _composition; } }

        // ---------------- 驱动侧: 只在帧边界调用 ----------------

        public void MoveMouse(Vector2 pos)
        {
            _mousePos = pos;
            Stage.InvalidateInputCaches();
            if (visualizer != null) visualizer.OnPointerMove(pos);
        }

        public void PressMouse(int button)
        {
            CheckButton(button);
            _downFrame[button] = _clock.frameCount;
            _mouseHeld[button] = true;
            if (visualizer != null) visualizer.OnPointerDown(_mousePos, button);
        }

        public void ReleaseMouse(int button)
        {
            CheckButton(button);
            _upFrame[button] = _clock.frameCount;
            _mouseHeld[button] = false;
            if (visualizer != null) visualizer.OnPointerUp(_mousePos, button);
        }

        public void HoldKey(KeyCode key) { _held.Add(key); }
        public void ReleaseKey(KeyCode key) { _held.Remove(key); }

        public void SetTouches(IList<UnityEngine.Touch> touches)
        {
            _touches.Clear();
            if (touches != null)
            {
                for (int i = 0; i < touches.Count; i++)
                    _touches.Add(touches[i]);
            }
            Stage.InvalidateInputCaches();
            if (visualizer != null) visualizer.OnTouches(_touches);
        }

        public void SetComposition(string s)
        {
            _composition = s != null ? s : string.Empty;
        }

        // ---------------- 中断收尾用 ----------------
        // 收尾要"释放实际持有的", 而不是猜。这三个口只给同程序集的收尾路径用。

        internal bool IsMouseHeld(int button) { return _mouseHeld[button]; }

        internal void ReleaseAllHeldKeys()
        {
            _held.Clear();
        }

        /// <summary>
        /// 把还在的手指全部置 Ended。不是直接清空 —— 清空只是让 touchCount 归零,
        /// FairyGUI 收不到 Ended 相位就不会走 touch.End(), 业务的 onTouchEnd 永远不来。
        /// </summary>
        internal void EndAllTouches()
        {
            if (_touches.Count == 0) return;

            var ended = new List<UnityEngine.Touch>(_touches.Count);
            for (int i = 0; i < _touches.Count; i++)
            {
                UnityEngine.Touch t = _touches[i];
                t.phase = TouchPhase.Ended;
                ended.Add(t);
            }
            SetTouches(ended);
        }

        /// <summary>
        /// 清空按键/触摸/组合串这些瞬时状态。异常 / 中断兜底, 会话进入与退出时各调一次。
        /// 不推 visualizer: 标记要留到截图之后, 只有显式 Clear() 才清。
        ///
        /// 鼠标位置故意不清零: 它代表"指针当前在哪", 跟按键/触摸这些"这一帧发生了什么"
        /// 不是同一类状态, 清成 (0,0) 只是武断的哨兵值, 不代表指针真的移动到了那里。
        /// 留着不清, MoveTo(target, steps) 这类"从当前位置移动"的调用在新会话开头才有意义
        /// (从上一次真实设置的位置接着滑, 不会凭空跳到原点再滑回来)。只有显式 MoveMouse()
        /// 才会改变它。
        /// </summary>
        public void ResetAll()
        {
            for (int i = 0; i < 3; i++)
            {
                _downFrame[i] = -1;
                _upFrame[i] = -1;

                // 只对还按着的键补推一次 Up: 标记不清(留到截图之后), 但 held 圆环要转入淡出,
                // 否则它会继续画着, 而输入状态其实已经释放了。没有 held 键时一次都不推。
                if (_mouseHeld[i])
                {
                    _mouseHeld[i] = false;
                    if (visualizer != null) visualizer.OnPointerUp(_mousePos, i);
                }
            }
            _held.Clear();
            _touches.Clear();
            _composition = string.Empty;
        }

        static void CheckButton(int button)
        {
            if (button < 0 || button > 2)
                throw new ArgumentOutOfRangeException("button", button, "鼠标按键只支持 0(左) / 1(右) / 2(中)");
        }
    }
}
