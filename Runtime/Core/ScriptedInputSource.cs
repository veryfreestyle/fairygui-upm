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
            if (visualizer != null) visualizer.OnPointerDown(_mousePos, button);
        }

        public void ReleaseMouse(int button)
        {
            CheckButton(button);
            _upFrame[button] = _clock.frameCount;
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

        /// <summary>
        /// 全部清空。异常 / 中断兜底, 会话进入与退出时各调一次。
        /// 不推 visualizer: 标记要留到截图之后, 只有显式 Clear() 才清。
        /// </summary>
        public void ResetAll()
        {
            for (int i = 0; i < 3; i++)
            {
                _downFrame[i] = -1;
                _upFrame[i] = -1;
            }
            _held.Clear();
            _touches.Clear();
            _mousePos = Vector2.zero;
            _composition = string.Empty;
        }

        static void CheckButton(int button)
        {
            if (button < 0 || button > 2)
                throw new ArgumentOutOfRangeException("button", button, "鼠标按键只支持 0(左) / 1(右) / 2(中)");
        }
    }
}
