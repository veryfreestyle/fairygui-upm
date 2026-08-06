using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>指针模式。决定 Stage.touchScreen 走哪条分支, 也决定这个会话能用哪半套 API。</summary>
    public enum StageInputMode
    {
        Mouse,
        Touch
    }

    /// <summary>
    /// 输入序列编排。纯 IEnumerator, 不继承 MonoBehaviour ——
    /// [UnityTest] 直接 yield return, 游戏 StartCoroutine, MCP 自己 MoveNext。
    /// 所有序列帧数固定, 不含任何等待条件。
    /// </summary>
    public sealed partial class StageInputPlayer : IDisposable
    {
        readonly ScriptedInputSource _source;
        readonly IGuiEventSink _sink;
        readonly StageInputMode _mode;
        readonly Action<StageInputPlayer> _onSessionEnd;
        bool _disposed;

        public StageInputPlayer(ScriptedInputSource source, IGuiEventSink sink,
                                StageInputMode mode = StageInputMode.Mouse)
            : this(source, sink, mode, null)
        {
        }

        internal StageInputPlayer(ScriptedInputSource source, IGuiEventSink sink,
                                  StageInputMode mode, Action<StageInputPlayer> onSessionEnd)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (sink == null) throw new ArgumentNullException("sink");

            _source = source;
            _sink = sink;
            _mode = mode;
            _onSessionEnd = onSessionEnd;
        }

        public StageInputMode mode { get { return _mode; } }
        public bool disposed { get { return _disposed; } }

        /// <summary>归还会话。幂等; 直接 new 出来的 player 没有会话, 只标记自身已释放。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_onSessionEnd != null) _onSessionEnd(this);
        }

        /// <summary>供 StageInputSimulator.ForceReset() 作废泄漏的 player, 不回调归还。</summary>
        internal void MarkDisposed()
        {
            _disposed = true;
        }

        // ---------------- 校验 ----------------
        // 注意: IEnumerator 方法体里抛出的异常要到第一次 MoveNext() 才浮现。
        // 所以全部校验都放在非迭代器的外层方法里, 让参数错误立即失败。

        void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("StageInputPlayer",
                    "会话已结束; 从 StageInputSimulator.Start() 取一个新 player");
        }

        void RequireMouse(string method)
        {
            if (_mode != StageInputMode.Mouse)
                throw new InvalidOperationException(
                    method + " 只在 Mouse 模式下可用 (当前 " + _mode + ")。触摸模式下 GetMouseButtonDown/Up 没人读。");
        }

        void RequireTouch(string method)
        {
            if (_mode != StageInputMode.Touch)
                throw new InvalidOperationException(
                    method + " 只在 Touch 模式下可用 (当前 " + _mode + ")。鼠标模式下 touchCount 恒为 0。");
        }

        static void CheckButton(int button)
        {
            if (button < 0 || button > 2)
                throw new ArgumentOutOfRangeException("button", button, "鼠标按键只支持 0(左) / 1(右) / 2(中)");
        }

        static void CheckFrames(int frames, string name)
        {
            if (frames < 0)
                throw new ArgumentOutOfRangeException(name, frames, name + " 不能为负");
        }

        /// <summary>
        /// 每帧位移必须 ≥ 1 像素。SwipeGesture.__touchMove 的 snapping 默认为 true,
        /// delta 取整后为 (0,0) 就整帧 return —— 200 像素分 300 帧走, 位移一点都累加不进去。
        /// 总位移为 0(按住不动)是合法的, 跳过校验。
        /// </summary>
        internal static void CheckStepDisplacement(Vector2 from, Vector2 to, int steps, string method)
        {
            if (steps < 1)
                throw new ArgumentOutOfRangeException("steps", steps, method + " 的 steps 至少为 1");

            float total = (to - from).magnitude;
            if (total == 0f) return;

            float perFrame = total / steps;
            if (perFrame < 1f)
                throw new ArgumentException(
                    method + ": 每帧位移 " + perFrame.ToString("F3") + "px < 1px, "
                    + "SwipeGesture 的 snapping 会把 delta 取整为 0, 整段位移静默丢失。"
                    + "总位移 " + total.ToString("F1") + "px, steps 最多取 " + Mathf.FloorToInt(total),
                    "steps");
        }

        // ---------------- 通用 ----------------

        /// <summary>只推帧不改状态。帧数 = frames。</summary>
        public IEnumerator Step(int frames = 1)
        {
            ThrowIfDisposed();
            CheckFrames(frames, "frames");
            return StepRoutine(frames);
        }

        static IEnumerator StepRoutine(int frames)
        {
            for (int i = 0; i < frames; i++)
                yield return null;
        }

        // ---------------- 鼠标 ----------------

        /// <summary>移到指定位置。等价 MoveTo(pos, 1) —— 只要终点效果, 不测途经行为。帧数 1。</summary>
        public IEnumerator Hover(Vector2 screenPos)
        {
            ThrowIfDisposed();
            RequireMouse("Hover");
            return MoveToRoutine(_source.mousePosition, screenPos, 1);
        }

        /// <summary>从当前位置插值移动到 to。帧数 = steps。</summary>
        public IEnumerator MoveTo(Vector2 to, int steps = 1)
        {
            ThrowIfDisposed();
            RequireMouse("MoveTo");
            Vector2 from = _source.mousePosition;
            CheckStepDisplacement(from, to, steps, "MoveTo");
            return MoveToRoutine(from, to, steps);
        }

        /// <summary>显式起点的插值移动。帧数 = steps。</summary>
        public IEnumerator MoveTo(Vector2 from, Vector2 to, int steps)
        {
            ThrowIfDisposed();
            RequireMouse("MoveTo");
            CheckStepDisplacement(from, to, steps, "MoveTo");
            return MoveToRoutine(from, to, steps);
        }

        IEnumerator MoveToRoutine(Vector2 from, Vector2 to, int steps)
        {
            for (int i = 1; i <= steps; i++)
            {
                _source.MoveMouse(Vector2.Lerp(from, to, (float)i / steps));
                yield return null;
            }
        }

        /// <summary>
        /// 按下 - 保持 - 抬起。帧数 3。
        /// 中间那帧不可省: TouchInfo.End() 在 frameCount - downFrame == 1 时把 holdTime
        /// 算成 1f / Application.targetFrameRate, 而它默认是 -1。
        /// 内部是瞬移不插值; 要测途经行为(rollover 链)先 MoveTo(target, steps) 再 Click。
        /// </summary>
        public IEnumerator Click(Vector2 screenPos, int button = 0)
        {
            ThrowIfDisposed();
            RequireMouse("Click");
            CheckButton(button);
            return ClickRoutine(screenPos, button);
        }

        IEnumerator ClickRoutine(Vector2 screenPos, int button)
        {
            _source.MoveMouse(screenPos);
            _source.PressMouse(button);
            yield return null;

            yield return null;          // holdTime: 让 frameCount - downFrame == 2

            _source.ReleaseMouse(button);
            yield return null;
        }

        /// <summary>两次 Click。帧数 6。双击窗口 0.35 秒由 TouchInfo.End() 用真实时间判定。</summary>
        public IEnumerator DoubleClick(Vector2 screenPos)
        {
            ThrowIfDisposed();
            RequireMouse("DoubleClick");
            return DoubleClickRoutine(screenPos);
        }

        IEnumerator DoubleClickRoutine(Vector2 screenPos)
        {
            IEnumerator first = ClickRoutine(screenPos, 0);
            while (first.MoveNext()) yield return first.Current;

            IEnumerator second = ClickRoutine(screenPos, 0);
            while (second.MoveNext()) yield return second.Current;
        }

        /// <summary>按下不抬。帧数 1。与 Release 连用时中间至少插一次 Step(1), 否则 holdTime 是 -1。</summary>
        public IEnumerator Press(Vector2 screenPos, int button = 0)
        {
            ThrowIfDisposed();
            RequireMouse("Press");
            CheckButton(button);
            return PressRoutine(screenPos, button);
        }

        IEnumerator PressRoutine(Vector2 screenPos, int button)
        {
            _source.MoveMouse(screenPos);
            _source.PressMouse(button);
            yield return null;
        }

        /// <summary>抬起。帧数 1。</summary>
        public IEnumerator Release(Vector2 screenPos, int button = 0)
        {
            ThrowIfDisposed();
            RequireMouse("Release");
            CheckButton(button);
            return ReleaseRoutine(screenPos, button);
        }

        IEnumerator ReleaseRoutine(Vector2 screenPos, int button)
        {
            _source.MoveMouse(screenPos);
            _source.ReleaseMouse(button);
            yield return null;
        }

        /// <summary>按下 - 保持 - 插值移动 - 抬起。帧数 = 1 + holdFrames + steps + 1。</summary>
        public IEnumerator Drag(Vector2 from, Vector2 to, int steps, int holdFrames = 0)
        {
            ThrowIfDisposed();
            RequireMouse("Drag");
            CheckFrames(holdFrames, "holdFrames");
            CheckStepDisplacement(from, to, steps, "Drag");
            return DragRoutine(from, to, steps, holdFrames);
        }

        IEnumerator DragRoutine(Vector2 from, Vector2 to, int steps, int holdFrames)
        {
            _source.MoveMouse(from);
            _source.PressMouse(0);
            yield return null;

            for (int i = 0; i < holdFrames; i++)
                yield return null;

            for (int i = 1; i <= steps; i++)
            {
                _source.MoveMouse(Vector2.Lerp(from, to, (float)i / steps));
                yield return null;
            }

            _source.ReleaseMouse(0);
            yield return null;
        }

        /// <summary>显式轨迹的拖拽。帧数 = 1 + holdFrames + path.Count + 1。</summary>
        public IEnumerator Drag(Vector2 from, IList<Vector2> path, int holdFrames = 0)
        {
            ThrowIfDisposed();
            RequireMouse("Drag");
            CheckFrames(holdFrames, "holdFrames");
            if (path == null || path.Count == 0)
                throw new ArgumentException("path 不能为空", "path");

            Vector2 prev = from;
            for (int i = 0; i < path.Count; i++)
            {
                CheckStepDisplacement(prev, path[i], 1, "Drag(path) 第 " + i + " 段");
                prev = path[i];
            }

            var copy = new List<Vector2>(path);
            return DragPathRoutine(from, copy, holdFrames);
        }

        IEnumerator DragPathRoutine(Vector2 from, List<Vector2> path, int holdFrames)
        {
            _source.MoveMouse(from);
            _source.PressMouse(0);
            yield return null;

            for (int i = 0; i < holdFrames; i++)
                yield return null;

            for (int i = 0; i < path.Count; i++)
            {
                _source.MoveMouse(path[i]);
                yield return null;
            }

            _source.ReleaseMouse(0);
            yield return null;
        }
    }
}
