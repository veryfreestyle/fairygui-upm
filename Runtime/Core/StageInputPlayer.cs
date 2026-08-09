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
    /// [UnityTest] 直接 yield return, 游戏里用 StartCoroutine。
    /// 手动 pump(自己反复调 MoveNext 推进)的消费方改用 StageInputSimulator.Run ——
    /// 手动 pump 必须自己挑对推进的时点, 挑错就静默丢一次输入, 这条约束写在文档里守不住,
    /// 所以收进 Run 内建校验; Run 自身的时点约束见它的 XML 注释。
    /// 帧数契约分两半: 帧驱动的重载(Click / Drag / Tap 等不带 AtSpeed/Ms/Rate 后缀的)
    /// 帧数是文档化的确定值, 写在各方法的 summary 里; 时间驱动的重载
    /// (StepMs / MoveAtSpeed / DragAtSpeed / TypeTextAtRate)帧数取决于运行时帧率, 不保证。
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
            _fingers.Clear();
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

        internal static void CheckSteps(int steps, string method)
        {
            if (steps < 1)
                throw new ArgumentOutOfRangeException("steps", steps, method + " 的 steps 至少为 1");
        }

        // ---------------- 通用 ----------------

        /// <summary>只推帧不改状态。帧数 = frames。</summary>
        public IEnumerator Step(int frames = 1)
        {
            ThrowIfDisposed();
            CheckFrames(frames, "frames");
            return StepRoutine(frames);
        }

        IEnumerator StepRoutine(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return null;
                if (_mode == StageInputMode.Touch) AdvanceTouchPhases();
            }
        }

        /// <summary>
        /// 按毫秒停顿。至少推一帧 —— 序列末尾少一次 yield, 最后一次注入就没人消费。
        /// 帧数取决于运行时帧率, 不是确定值; 要断言帧数用 Step(int)。
        /// </summary>
        public IEnumerator StepMs(float ms)
        {
            ThrowIfDisposed();
            CheckMs(ms, "ms");
            return WaitMsRoutine(ms, 1);
        }

        static void CheckMs(float ms, string name)
        {
            if (ms < 0f)
                throw new ArgumentOutOfRangeException(name, ms, name + " 不能为负");
        }

        /// <summary>
        /// 推帧直到墙钟走够 ms, 且至少 minFrames 帧。minFrames = 0 且 ms = 0 时一帧都不推。
        /// 读 _source.clock 而非 Time: UnityFrameClock 本来就是 Time 的透传,
        /// 走 clock 才能在 EditMode 用假时钟验。
        ///
        /// 循环体里推 Touch 相位, 而通道 B 的另外两条既有等待循环
        /// (TypeTextRoutine 的逐帧 yield、SendKeyRoutine 的三帧 yield) 都没有这句 ——
        /// 于是 Touch 模式下按着手指不放同时打字, TypeText 与 TypeTextAtRate 的副作用面并不一致。
        /// 这不是本方法要对齐旧行为: 不推才是缺口。AdvanceTouchPhases 的注释写得很清楚,
        /// Began 只能停留一帧, 多停一帧 FairyGUI 会把同一根手指当新手指再分配一次槽位。
        /// TypeTextRoutine / SendKeyRoutine 那两处不推是既有序列方法冻结范围内的既有缺口,
        /// 不在本 task 里改; 本方法保持推相位是对的一侧。
        /// </summary>
        IEnumerator WaitMsRoutine(float ms, int minFrames)
        {
            float seconds = ms / 1000f;
            float start = _source.clock.unscaledTime;
            int frames = 0;
            while (frames < minFrames || _source.clock.unscaledTime - start < seconds)
            {
                yield return null;
                frames++;
                if (_mode == StageInputMode.Touch) AdvanceTouchPhases();
            }
        }

        /// <summary>
        /// 释放本会话当前持有的全部输入。帧数 1。
        ///
        /// 存在的理由: Cancel() 面向"有序列在跑"这一态, 对"没有序列在跑但状态还挂着"
        /// (比如 Run(player.Press(pos)) 跑完之后 _running 已是 false)是彻底的 no-op;
        /// Dispose() 走 Restore() -> ResetAll(), 只把标志位清掉、把 _upFrame 清成 -1
        /// (抹掉抬起帧, 不是补写一个当前帧), FairyGUI 永远读不到 GetMouseButtonUp,
        /// 业务在 onTouchBegin 里置的状态永远等不到 onTouchEnd。而 "press 之后不 release
        /// 就 end-session" 是设计内的用法(会话要让按下态活过命令边界), 所以需要一条
        /// 优雅收尾路径。
        ///
        /// 释放什么不用猜, 按实际持有的释放: 鼠标读 _source.IsMouseHeld 逐个 ReleaseMouse;
        /// 修饰键走 _source.ReleaseAllHeldKeys() —— 注意这是无条件释放全部修饰键, 若在
        /// HoldModifiers(...) 的 using 作用域内调用, 会连同作用域按住的修饰键一起放掉:
        /// 作用域对象持有的 _keys 与 source 就此脱节, 作用域内之后的 SendKey 不再带那个
        /// 修饰键, 作用域 Dispose() 也变成静默 no-op; 触摸用本 player 自己的 ReleaseFinger
        /// 而非 _source.EndAllTouches() —— 直接操作 source 会让 _fingers 与 _source 的
        /// touch 列表脱节, 下一帧 AdvanceTouchPhases() 就没法正常释放槽位。
        ///
        /// 必须占一帧: ReleaseMouse(b) 只是写 _upFrame[b], FairyGUI 要在 LateUpdate 读到
        /// GetMouseButtonUp 才走 touch.End(), 同步返回的话这次释放没人消费。什么都没持有时
        /// 也占一帧(与 Step(1) 一致, 帧数保持确定), 不因为无事可做就抛异常或跳过。
        ///
        /// 不限模式: 两种模式下都要正确工作, 是通用收尾, 不加 RequireMouse / RequireTouch。
        /// </summary>
        public IEnumerator ReleaseHeld()
        {
            ThrowIfDisposed();
            return ReleaseHeldRoutine();
        }

        IEnumerator ReleaseHeldRoutine()
        {
            for (int b = 0; b < 3; b++)
                if (_source.IsMouseHeld(b)) _source.ReleaseMouse(b);

            _source.ReleaseAllHeldKeys();

            for (int i = 0; i < _fingers.Count; i++)
                ReleaseFinger(_fingers[i].id);

            yield return null;
            if (_mode == StageInputMode.Touch) AdvanceTouchPhases();
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
            CheckSteps(steps, "MoveTo");
            return MoveToRoutine(from, to, steps);
        }

        /// <summary>显式起点的插值移动。帧数 = steps。</summary>
        public IEnumerator MoveTo(Vector2 from, Vector2 to, int steps)
        {
            ThrowIfDisposed();
            RequireMouse("MoveTo");
            CheckSteps(steps, "MoveTo");
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

        /// <summary>从当前位置按速度移动到 to。帧数取决于运行时帧率, 不是确定值。</summary>
        public IEnumerator MoveAtSpeed(Vector2 to, float pixelsPerSecond)
        {
            ThrowIfDisposed();
            RequireMouse("MoveAtSpeed");
            CheckSpeed(pixelsPerSecond, "MoveAtSpeed");
            return MoveAtSpeedRoutine(_source.mousePosition, to, pixelsPerSecond);
        }

        /// <summary>显式起点的按速度移动。</summary>
        public IEnumerator MoveAtSpeed(Vector2 from, Vector2 to, float pixelsPerSecond)
        {
            ThrowIfDisposed();
            RequireMouse("MoveAtSpeed");
            CheckSpeed(pixelsPerSecond, "MoveAtSpeed");
            return MoveAtSpeedRoutine(from, to, pixelsPerSecond);
        }

        static void CheckSpeed(float pixelsPerSecond, string method)
        {
            if (pixelsPerSecond <= 0f)
                throw new ArgumentOutOfRangeException("pixelsPerSecond", pixelsPerSecond,
                    method + " 的速度必须为正 (像素每秒)");
        }

        /// <summary>插值移动至少占这么多帧。低于 2 会撞 TouchInfo 的
        /// frameCount - downFrame == 1 分支, holdTime 退化成 1f / targetFrameRate(默认 -1)。</summary>
        const int MinInterpolationFrames = 2;

        /// <summary>
        /// 按墙钟插值。必须是绝对定位 Lerp 而非增量累加 (pos += v * deltaTime):
        /// 绝对式在掉帧时只跳过中间点, 终点仍精确落在 to; 增量式的浮点误差会累积成过冲或差一点,
        /// 且位移随 deltaTime 波动, 观感上就是速度抖动。
        /// </summary>
        IEnumerator MoveAtSpeedRoutine(Vector2 from, Vector2 to, float pixelsPerSecond)
        {
            float duration = Vector2.Distance(from, to) / pixelsPerSecond;
            float start = _source.clock.unscaledTime;
            int frames = 0;

            while (true)
            {
                float t = duration > 0f
                    ? Mathf.Clamp01((_source.clock.unscaledTime - start) / duration)
                    : 1f;
                _source.MoveMouse(Vector2.Lerp(from, to, t));
                yield return null;
                frames++;

                if (t >= 1f && frames >= MinInterpolationFrames) yield break;
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

        /// <summary>
        /// 按下 - 保持 - 插值移动 - 抬起。帧数 = 1 + holdFrames + steps + 1。左键固定;
        /// 要按右键/中键或要控制抬起前的停顿, 用下面六参重载的 button。
        /// </summary>
        public IEnumerator Drag(Vector2 from, Vector2 to, int steps, int holdFrames = 0)
        {
            ThrowIfDisposed();
            RequireMouse("Drag");
            CheckFrames(holdFrames, "holdFrames");
            CheckSteps(steps, "Drag");
            return DragRoutine(from, to, steps, holdFrames, 0, 0);
        }

        /// <summary>
        /// 按下 - 停 holdBeforeFrames - 插值移动 - 停 holdAfterFrames - 抬起。
        /// 帧数 = 1 + holdBeforeFrames + steps + holdAfterFrames + 1。
        ///
        /// holdAfterFrames 决定抬起前停不停, 而那决定 ScrollPane 有没有甩动惯性:
        /// 停一下再松手无惯性(停在原地), 移动中直接松手带惯性(继续滑)。两种都是真实行为。
        /// holdBeforeFrames 至少为 1 —— down 与第一个 move 相邻会让第一次 onTouchMove 的
        /// holdTime 退化成 -1。这里抛而不是静默抬升: 帧驱动重载的帧数是文档化的确定值。
        ///
        /// button 只加在这个最宽重载上: 四参便捷重载(holdFrames = 0)若同样加 button = 0,
        /// Drag(a, b, 5, 3, 0) 会同时匹配"四参 + button"与"本重载", 编译器报歧义。
        /// </summary>
        public IEnumerator Drag(Vector2 from, Vector2 to, int steps,
                                int holdBeforeFrames, int holdAfterFrames, int button = 0)
        {
            ThrowIfDisposed();
            RequireMouse("Drag");
            CheckButton(button);
            CheckHoldBeforeFrames(holdBeforeFrames);
            CheckFrames(holdAfterFrames, "holdAfterFrames");
            CheckSteps(steps, "Drag");
            return DragRoutine(from, to, steps, holdBeforeFrames, holdAfterFrames, button);
        }

        static void CheckHoldBeforeFrames(int holdBeforeFrames)
        {
            if (holdBeforeFrames < 1)
                throw new ArgumentOutOfRangeException("holdBeforeFrames", holdBeforeFrames,
                    "holdBeforeFrames 至少为 1: down 与第一个 move 相邻会让第一次 onTouchMove 的 "
                    + "holdTime 退化成 1f / Application.targetFrameRate (默认 -1)");
        }

        IEnumerator DragRoutine(Vector2 from, Vector2 to, int steps,
                                int holdBeforeFrames, int holdAfterFrames, int button)
        {
            _source.MoveMouse(from);
            _source.PressMouse(button);
            yield return null;

            for (int i = 0; i < holdBeforeFrames; i++)
                yield return null;

            for (int i = 1; i <= steps; i++)
            {
                _source.MoveMouse(Vector2.Lerp(from, to, (float)i / steps));
                yield return null;
            }

            for (int i = 0; i < holdAfterFrames; i++)
                yield return null;

            _source.ReleaseMouse(button);
            yield return null;
        }

        /// <summary>显式轨迹的拖拽。帧数 = 1 + holdFrames + path.Count + 1。</summary>
        public IEnumerator Drag(Vector2 from, IList<Vector2> path, int holdFrames = 0)
        {
            ThrowIfDisposed();
            RequireMouse("Drag");
            CheckFrames(holdFrames, "holdFrames");
            CheckPath(path);
            return DragPathRoutine(from, new List<Vector2>(path), holdFrames, 0, 0);
        }

        /// <summary>
        /// 显式轨迹 + 抬起前停顿。帧数 = 1 + holdBeforeFrames + path.Count + holdAfterFrames + 1。
        /// button 只加在这个最宽重载上, 理由同五参 Drag: 四参便捷重载若同样加 button = 0 会与
        /// 本重载在调用点产生歧义。
        /// </summary>
        public IEnumerator Drag(Vector2 from, IList<Vector2> path,
                                int holdBeforeFrames, int holdAfterFrames, int button = 0)
        {
            ThrowIfDisposed();
            RequireMouse("Drag");
            CheckButton(button);
            CheckHoldBeforeFrames(holdBeforeFrames);
            CheckFrames(holdAfterFrames, "holdAfterFrames");
            CheckPath(path);
            return DragPathRoutine(from, new List<Vector2>(path), holdBeforeFrames, holdAfterFrames, button);
        }

        static void CheckPath(IList<Vector2> path)
        {
            if (path == null || path.Count == 0)
                throw new ArgumentException("path 不能为空", "path");
        }

        IEnumerator DragPathRoutine(Vector2 from, List<Vector2> path,
                                    int holdBeforeFrames, int holdAfterFrames, int button)
        {
            _source.MoveMouse(from);
            _source.PressMouse(button);
            yield return null;

            for (int i = 0; i < holdBeforeFrames; i++)
                yield return null;

            for (int i = 0; i < path.Count; i++)
            {
                _source.MoveMouse(path[i]);
                yield return null;
            }

            for (int i = 0; i < holdAfterFrames; i++)
                yield return null;

            _source.ReleaseMouse(button);
            yield return null;
        }

        /// <summary>
        /// 按速度拖拽。帧数取决于运行时帧率, 不是确定值。
        /// holdBeforeMs 至少占一帧(理由同五参 Drag); holdAfterMs 无下限, 0 表示移动中直接松手,
        /// ScrollPane 会带甩动惯性继续滑。
        /// DragAtSpeed 只有这一个重载(没有与之撞歧义的便捷版本), button = 0 直接加在这里。
        /// </summary>
        public IEnumerator DragAtSpeed(Vector2 from, Vector2 to, float pixelsPerSecond,
                                       float holdBeforeMs, float holdAfterMs, int button = 0)
        {
            ThrowIfDisposed();
            RequireMouse("DragAtSpeed");
            CheckButton(button);
            CheckSpeed(pixelsPerSecond, "DragAtSpeed");
            CheckMs(holdBeforeMs, "holdBeforeMs");
            CheckMs(holdAfterMs, "holdAfterMs");
            return DragAtSpeedRoutine(from, to, pixelsPerSecond, holdBeforeMs, holdAfterMs, button);
        }

        IEnumerator DragAtSpeedRoutine(Vector2 from, Vector2 to, float pixelsPerSecond,
                                       float holdBeforeMs, float holdAfterMs, int button)
        {
            _source.MoveMouse(from);
            _source.PressMouse(button);
            yield return null;

            IEnumerator before = WaitMsRoutine(holdBeforeMs, 1);
            while (before.MoveNext()) yield return before.Current;

            IEnumerator move = MoveAtSpeedRoutine(from, to, pixelsPerSecond);
            while (move.MoveNext()) yield return move.Current;

            IEnumerator after = WaitMsRoutine(holdAfterMs, 0);
            while (after.MoveNext()) yield return after.Current;

            _source.ReleaseMouse(button);
            yield return null;
        }

        // ---------------- 通道 B: IMGUI 事件 ----------------

        /// <summary>
        /// 一次真实按键在 IMGUI 里是一个还是两个 Event, 由 spec §8.2 #1 实测决定。
        /// Combined: 一个 KeyDown 同时带 keyCode 与 character。
        /// Split:    两个 KeyDown, 一个只带 keyCode, 一个只带 character, 同帧入队。
        /// 两种都不改帧数。
        /// </summary>
        public static KeyEventStyle keyEventStyle = KeyEventStyle.Split;

        /// <summary>
        /// 按下 - 抬起 - 释放修饰键。帧数 3。
        /// 两侧都喂: source.HoldKey 让 evt.ctrl 为真(InputEvent 读 GetKey),
        /// Event.modifiers 让 evt.modifiers 为真(Stage.cs:954 抄自事件)。
        /// 缺一个就是 "Ctrl+A 被当成没按修饰键的 A" 那个行为错误。
        /// 释放修饰键必须比 KeyUp 晚一帧: InputEvent.ctrl 是惰性属性, 在派发那一刻才读 GetKey,
        /// 而队列要到该帧 OnGUI 才排空。
        /// </summary>
        public IEnumerator SendKey(KeyCode key, EventModifiers modifiers = 0)
        {
            ThrowIfDisposed();
            return SendKeyRoutine(key, modifiers);
        }

        IEnumerator SendKeyRoutine(KeyCode key, EventModifiers modifiers)
        {
            // 只按住/释放这次调用自己按下的键: 若外层已有 HoldModifiers(Shift) 作用域,
            // 这里再传 EventModifiers.Shift 时 Shift 已经是 true, 不该在本方法结束时被释放——
            // 否则会在 using 作用域结束前把外层按住的修饰键提前放掉, 静默打断外层作用域。
            List<KeyCode> held = ModifierKeys(modifiers);
            List<KeyCode> pressedHere = new List<KeyCode>(held.Count);
            for (int i = 0; i < held.Count; i++)
            {
                if (_source.GetKey(held[i])) continue;
                _source.HoldKey(held[i]);
                pressedHere.Add(held[i]);
            }

            EventModifiers mods = CurrentModifiers(modifiers);
            QueueKeyEvents(EventType.KeyDown, key, mods);
            yield return null;

            QueueKeyEvents(EventType.KeyUp, key, mods);
            yield return null;

            for (int i = 0; i < pressedHere.Count; i++)
                _source.ReleaseKey(pressedHere[i]);
            yield return null;
        }

        void QueueKeyEvents(EventType type, KeyCode key, EventModifiers mods)
        {
            char ch = DeriveCharacter(key, mods);

            if (keyEventStyle == KeyEventStyle.Combined)
            {
                _sink.Queue(MakeKeyEvent(type, key, ch, mods));
                return;
            }

            _sink.Queue(MakeKeyEvent(type, key, '\0', mods));
            if (ch != '\0')
                _sink.Queue(MakeKeyEvent(type, KeyCode.None, ch, mods));
        }

        static Event MakeKeyEvent(EventType type, KeyCode key, char character, EventModifiers mods)
        {
            Event e = new Event();
            e.type = type;
            e.keyCode = key;
            e.character = character;
            e.modifiers = mods;
            return e;
        }

        /// <summary>
        /// 逐字符投递。帧数 = text.Length * framesPerChar。
        /// 只发 character(keyCode = None) —— 文本录入走的是 InputTextField.HandleTextInput,
        /// 它读的是 evt.character。BMP 内的中文逐 char 发即可; 代理对与 emoji 本阶段不支持。
        /// </summary>
        public IEnumerator TypeText(string text, int framesPerChar = 1)
        {
            ThrowIfDisposed();
            if (text == null) throw new ArgumentNullException("text");
            if (framesPerChar < 1)
                throw new ArgumentOutOfRangeException("framesPerChar", framesPerChar, "framesPerChar 至少为 1");
            return TypeTextRoutine(text, framesPerChar);
        }

        IEnumerator TypeTextRoutine(string text, int framesPerChar)
        {
            EventModifiers mods = CurrentModifiers(0);
            for (int i = 0; i < text.Length; i++)
            {
                _sink.Queue(MakeKeyEvent(EventType.KeyDown, KeyCode.None, text[i], mods));
                for (int f = 0; f < framesPerChar; f++)
                    yield return null;
            }
        }

        /// <summary>
        /// 按毫秒节奏逐字符投递。每个字符至少占一帧(同 TypeText 的 framesPerChar 下限)。
        /// 帧数取决于运行时帧率, 不是确定值。
        /// </summary>
        public IEnumerator TypeTextAtRate(string text, float msPerChar)
        {
            ThrowIfDisposed();
            if (text == null) throw new ArgumentNullException("text");
            CheckMs(msPerChar, "msPerChar");
            return TypeTextAtRateRoutine(text, msPerChar);
        }

        IEnumerator TypeTextAtRateRoutine(string text, float msPerChar)
        {
            EventModifiers mods = CurrentModifiers(0);
            for (int i = 0; i < text.Length; i++)
            {
                _sink.Queue(MakeKeyEvent(EventType.KeyDown, KeyCode.None, text[i], mods));
                IEnumerator wait = WaitMsRoutine(msPerChar, 1);
                while (wait.MoveNext()) yield return wait.Current;
            }
        }

        /// <summary>
        /// 滚轮。帧数 2: 第一帧把指针放到目标处让 LateUpdate 算出 _touchTarget,
        /// 第二帧投事件, OnGUI 排空时 _touchTarget 仍然正确。
        /// Touch 模式下必然无效(没手指时 _touchTarget 恒为 null, 事件被静默丢弃), 故限 Mouse。
        /// </summary>
        public IEnumerator Scroll(Vector2 screenPos, float delta, EventModifiers mods = 0)
        {
            ThrowIfDisposed();
            RequireMouse("Scroll");
            return ScrollRoutine(screenPos, delta, mods);
        }

        IEnumerator ScrollRoutine(Vector2 screenPos, float delta, EventModifiers mods)
        {
            _source.MoveMouse(screenPos);
            yield return null;

            Event e = new Event();
            e.type = EventType.ScrollWheel;     // 必须先设 type: Event 内部 delta 与 mousePosition 共用存储
            e.delta = new Vector2(0f, delta);
            e.modifiers = CurrentModifiers(mods);
            _sink.Queue(e);
            yield return null;
        }

        /// <summary>
        /// 在 using 作用域内按住修饰键。做成 IDisposable 而非 IEnumerator ——
        /// _held 里残留一个 Ctrl 会让 evt.ctrl 永久为真, 是极难查的污染,
        /// 同步作用域的 using 是第一道防线。
        /// </summary>
        public IDisposable HoldModifiers(EventModifiers mods)
        {
            ThrowIfDisposed();
            List<KeyCode> keys = ModifierKeys(mods);
            for (int i = 0; i < keys.Count; i++)
                _source.HoldKey(keys[i]);
            return new ModifierScope(_source, keys);
        }

        /// <summary>
        /// 设置 IME 组合中态。FairyGUI 读 Stage.inputSource.compositionString。
        /// 注意仅 Mouse 会话有效: InputTextField.compositionString 第一句是
        /// if (Stage.keyboardInput) return String.Empty, 而真机 Touch 会话会把它置真。
        /// </summary>
        public void SetComposition(string text)
        {
            ThrowIfDisposed();
            _source.SetComposition(text);
        }

        // ---------------- 修饰键映射 ----------------

        static List<KeyCode> ModifierKeys(EventModifiers mods)
        {
            var keys = new List<KeyCode>(4);
            if ((mods & EventModifiers.Control) != 0) keys.Add(KeyCode.LeftControl);
            if ((mods & EventModifiers.Shift) != 0) keys.Add(KeyCode.LeftShift);
            if ((mods & EventModifiers.Alt) != 0) keys.Add(KeyCode.LeftAlt);
            if ((mods & EventModifiers.Command) != 0) keys.Add(KeyCode.LeftCommand);
            return keys;
        }

        /// <summary>
        /// 显式参数与当前按住状态的并集。不取并集的话, HoldModifiers(Shift) 期间调 SendKey(A)
        /// 会产生镜像故障: evt.shift 为真(读 GetKey)而 evt.modifiers 为 0(抄自事件)。
        /// </summary>
        EventModifiers CurrentModifiers(EventModifiers explicitMods)
        {
            EventModifiers m = explicitMods;
            if (_source.GetKey(KeyCode.LeftControl) || _source.GetKey(KeyCode.RightControl)) m |= EventModifiers.Control;
            if (_source.GetKey(KeyCode.LeftShift) || _source.GetKey(KeyCode.RightShift)) m |= EventModifiers.Shift;
            if (_source.GetKey(KeyCode.LeftAlt) || _source.GetKey(KeyCode.RightAlt)) m |= EventModifiers.Alt;
            if (_source.GetKey(KeyCode.LeftCommand) || _source.GetKey(KeyCode.RightCommand)) m |= EventModifiers.Command;
            return m;
        }

        /// <summary>
        /// KeyCode 到字符的映射。按住 Control / Command 时返回 '\0' ——
        /// 那些是命令键组合, 不应该往输入框塞字符。
        ///
        /// Backspace 也返回 '\0', 尽管 '\b' 才是它的 ASCII 字符: InputTextField.OnKeyDown
        /// 先按 keyCode 走 case KeyCode.Backspace 的 ReplaceSelection(null) 删掉选区, 然后
        /// 无条件往下走到 `char c = evt.character; if (c != 0) HandleTextInput(c);` ——
        /// HandleTextInput 只滤掉 27 / 单行下的 '\n' 与 '\t', 不滤 '\b', 于是同一次按键
        /// 既删了字符又把一个字面 '\b' 插进文本。实测 "abc" 上按一次 Backspace 得到
        /// "ab\b"。KeyCode.Delete 没有这个问题, 因为它本来就落到下面的 return '\0'。
        /// </summary>
        static char DeriveCharacter(KeyCode key, EventModifiers mods)
        {
            if ((mods & EventModifiers.Control) != 0 || (mods & EventModifiers.Command) != 0)
                return '\0';

            bool shift = (mods & EventModifiers.Shift) != 0;

            if (key >= KeyCode.A && key <= KeyCode.Z)
            {
                char lower = (char)('a' + (key - KeyCode.A));
                return shift ? char.ToUpperInvariant(lower) : lower;
            }
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9 && !shift)
                return (char)('0' + (key - KeyCode.Alpha0));

            if (key == KeyCode.Space) return ' ';
            if (key == KeyCode.Return || key == KeyCode.KeypadEnter) return '\n';
            if (key == KeyCode.Tab) return '\t';

            return '\0';
        }

        // ---------------- 触摸 ----------------

        sealed class Finger
        {
            public int id;
            public Vector2 pos;
            public TouchPhase phase;
        }

        readonly List<Finger> _fingers = new List<Finger>();
        readonly List<UnityEngine.Touch> _touchBuffer = new List<UnityEngine.Touch>();

        public int activeFingerCount { get { return _fingers.Count; } }

        /// <summary>落指。相位置 Began —— FairyGUI 只在这个相位分配触摸槽位。超 5 指抛异常。</summary>
        public void PressFinger(int fingerId, Vector2 screenPos)
        {
            ThrowIfDisposed();
            RequireTouch("PressFinger");

            if (_fingers.Count >= 5)
                throw new InvalidOperationException("FairyGUI 只有 5 个触摸槽位, 第 6 根手指会被静默丢弃");
            if (FindFinger(fingerId) != null)
                throw new InvalidOperationException("fingerId " + fingerId + " 已经落指, 先 ReleaseFinger");

            _fingers.Add(new Finger { id = fingerId, pos = screenPos, phase = TouchPhase.Began });
            SyncTouches();
        }

        /// <summary>移动已落下的手指。未落指抛异常 —— 直接发 Moved 会被 FairyGUI 丢弃。</summary>
        public void MoveFinger(int fingerId, Vector2 screenPos)
        {
            ThrowIfDisposed();
            RequireTouch("MoveFinger");

            Finger f = FindFinger(fingerId);
            if (f == null)
                throw new InvalidOperationException("fingerId " + fingerId + " 尚未落指, 先 PressFinger");

            f.pos = screenPos;
            // 本帧刚落指的手指保持 Began: 一根手指不可能同一帧既 Began 又 Moved。
            if (f.phase != TouchPhase.Began)
                f.phase = TouchPhase.Moved;
            SyncTouches();
        }

        /// <summary>
        /// 抬指。canceled: true 时发 Canceled 而非 Ended, 唯一差别是 FairyGUI 不派发 onClick
        /// (onTouchEnd / rollOut / 槽位释放照常)。对应真机的系统打断: 来电、系统手势接管、
        /// 超出平台触点上限。Ended / Canceled 这个二分在 iOS UITouch.Phase、Android
        /// ACTION_UP / ACTION_CANCEL、W3C touchend / touchcancel 上一致。
        /// </summary>
        public void ReleaseFinger(int fingerId, bool canceled = false)
        {
            ThrowIfDisposed();
            RequireTouch("ReleaseFinger");

            Finger f = FindFinger(fingerId);
            if (f == null)
                throw new InvalidOperationException("fingerId " + fingerId + " 尚未落指");

            f.phase = canceled ? TouchPhase.Canceled : TouchPhase.Ended;
            SyncTouches();
        }

        Finger FindFinger(int fingerId)
        {
            for (int i = 0; i < _fingers.Count; i++)
                if (_fingers[i].id == fingerId) return _fingers[i];
            return null;
        }

        void SyncTouches()
        {
            _touchBuffer.Clear();
            for (int i = 0; i < _fingers.Count; i++)
            {
                Finger f = _fingers[i];
                _touchBuffer.Add(new UnityEngine.Touch
                {
                    fingerId = f.id,
                    position = f.pos,
                    phase = f.phase,
                    tapCount = 1
                });
            }
            _source.SetTouches(_touchBuffer);
        }

        /// <summary>
        /// 帧末推进相位: Began / Moved 降级为 Stationary, Ended / Canceled 释放槽位。
        /// Began 只能出现一帧, 否则 FairyGUI 会认为是新手指再走一次分配并重复 touch.Begin()。
        /// </summary>
        void AdvanceTouchPhases()
        {
            if (_fingers.Count == 0) return;

            bool changed = false;
            for (int i = _fingers.Count - 1; i >= 0; i--)
            {
                Finger f = _fingers[i];
                if (f.phase == TouchPhase.Ended || f.phase == TouchPhase.Canceled)
                {
                    _fingers.RemoveAt(i);
                    changed = true;
                }
                else if (f.phase != TouchPhase.Stationary)
                {
                    f.phase = TouchPhase.Stationary;
                    changed = true;
                }
            }
            if (changed) SyncTouches();
        }

        /// <summary>落指 - 保持 - 抬指。帧数 3。中间那帧同 Click, 为了 holdTime 不退化成 -1。</summary>
        public IEnumerator Tap(Vector2 screenPos, int fingerId = 0)
        {
            ThrowIfDisposed();
            RequireTouch("Tap");
            return TapRoutine(screenPos, fingerId);
        }

        IEnumerator TapRoutine(Vector2 screenPos, int fingerId)
        {
            PressFinger(fingerId, screenPos);
            yield return null;
            AdvanceTouchPhases();

            yield return null;
            AdvanceTouchPhases();

            ReleaseFinger(fingerId);
            yield return null;
            AdvanceTouchPhases();
        }

        /// <summary>单指拖拽。帧数 = 1 + holdFrames + steps + 1。</summary>
        public IEnumerator TouchDrag(Vector2 from, Vector2 to, int steps,
                                     int fingerId = 0, int holdFrames = 0)
        {
            ThrowIfDisposed();
            RequireTouch("TouchDrag");
            CheckFrames(holdFrames, "holdFrames");
            CheckSteps(steps, "TouchDrag");
            return TouchDragRoutine(from, to, steps, fingerId, holdFrames);
        }

        IEnumerator TouchDragRoutine(Vector2 from, Vector2 to, int steps, int fingerId, int holdFrames)
        {
            PressFinger(fingerId, from);
            yield return null;
            AdvanceTouchPhases();

            for (int i = 0; i < holdFrames; i++)
            {
                yield return null;
                AdvanceTouchPhases();
            }

            for (int i = 1; i <= steps; i++)
            {
                MoveFinger(fingerId, Vector2.Lerp(from, to, (float)i / steps));
                yield return null;
                AdvanceTouchPhases();
            }

            ReleaseFinger(fingerId);
            yield return null;
            AdvanceTouchPhases();
        }

        /// <summary>
        /// 双指缩放 + 旋转。一个方法覆盖两者 —— 拆成 Pinch 和 Rotate 表达不了
        /// "同时缩放加旋转", 而那正是真人双指操作的样子。帧数 = 1 + steps + 1。
        ///
        /// center 与 angle 都是屏幕坐标系。每帧:
        ///   d = Lerp(fromDistance, toDistance, t);  a = Lerp(fromAngle, toAngle, t)
        ///   off = (cos a, sin a) * (d / 2)
        ///   finger0 = center - off;  finger1 = center + off
        ///
        /// 要触发 RotationGesture, toAngle 必须小于 fromAngle: stage 坐标 Y 向下,
        /// 屏幕系角度递增到识别器的局部系里会变成负 rot, 而它的
        /// if (!_started && rot > 5) 没有 Mathf.Abs。
        /// 两端全相等(两指按住不动)合法, 跳过每帧位移校验。
        /// </summary>
        public IEnumerator TwoFingerTransform(Vector2 center,
                                              float fromDistance, float toDistance,
                                              float fromAngle, float toAngle,
                                              int steps = 10,
                                              int fingerId0 = 0, int fingerId1 = 1)
        {
            ThrowIfDisposed();
            RequireTouch("TwoFingerTransform");

            if (steps < 1)
                throw new ArgumentOutOfRangeException("steps", steps, "TwoFingerTransform 的 steps 至少为 1");
            if (fingerId0 == fingerId1)
                throw new ArgumentException("两个 fingerId 不能相同", "fingerId1");
            if (FindFinger(fingerId0) != null || FindFinger(fingerId1) != null)
                throw new InvalidOperationException("fingerId " + fingerId0 + " 或 " + fingerId1 + " 已被占用");
            if (_fingers.Count > 3)
                throw new InvalidOperationException("触摸槽位不足以再落两根手指, 当前已占 " + _fingers.Count + " 个");

            return TwoFingerTransformRoutine(center, fromDistance, toDistance,
                                             fromAngle, toAngle, steps, fingerId0, fingerId1);
        }

        IEnumerator TwoFingerTransformRoutine(Vector2 center,
                                              float fromDistance, float toDistance,
                                              float fromAngle, float toAngle,
                                              int steps, int fingerId0, int fingerId1)
        {
            Vector2 off0 = OffsetAt(fromDistance, fromAngle);
            PressFinger(fingerId0, center - off0);
            PressFinger(fingerId1, center + off0);
            yield return null;
            AdvanceTouchPhases();

            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector2 off = OffsetAt(Mathf.Lerp(fromDistance, toDistance, t),
                                       Mathf.Lerp(fromAngle, toAngle, t));
                MoveFinger(fingerId0, center - off);
                MoveFinger(fingerId1, center + off);
                yield return null;
                AdvanceTouchPhases();
            }

            ReleaseFinger(fingerId0);
            ReleaseFinger(fingerId1);
            yield return null;
            AdvanceTouchPhases();
        }

        static Vector2 OffsetAt(float distance, float angleDegrees)
        {
            float rad = angleDegrees * Mathf.Deg2Rad;
            return new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * (distance / 2f);
        }

        sealed class ModifierScope : IDisposable
        {
            readonly ScriptedInputSource _source;
            readonly List<KeyCode> _keys;
            bool _released;

            public ModifierScope(ScriptedInputSource source, List<KeyCode> keys)
            {
                _source = source;
                _keys = keys;
            }

            public void Dispose()
            {
                if (_released) return;
                _released = true;
                for (int i = 0; i < _keys.Count; i++)
                    _source.ReleaseKey(_keys[i]);
            }
        }
    }

    /// <summary>见 StageInputPlayer.keyEventStyle。</summary>
    public enum KeyEventStyle
    {
        Combined,
        Split
    }
}
