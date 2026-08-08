using System;
using System.Collections;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// 对 FairyGUI Stage 的输入模拟。注入点在 Stage 读取原始输入的边界
    /// (Legacy Input 与 IMGUI Event 两侧), 不经过 OS —— 不触发系统输入法,
    /// 也不经过 Editor 的焦点路由。
    ///
    /// 一次 Start() 到 Dispose() 是接管作用域, 不是一次输入; 中间可以跑任意多个序列。
    /// 独占而非引用计数: Stage.inputSource 是 static, 本来就只能有一个真正的驱动方。
    /// </summary>
    public static class StageInputSimulator
    {
        static readonly ScriptedInputSource _source = new ScriptedInputSource();

        static StageInputPlayer _current;
        static string _label;
        static IStageInputSource _prevInputSource;
        static bool _prevTouchScreen;

        static bool _running;
        static bool _cancelRequested;
        static Action<StageInputRunResult, Exception> _onComplete;

        // 每次 Run 自增。包装迭代器捕获当次的值, 在每个恢复点比对 ——
        // 不相等就说明这条序列已经被作废(会话归还 / 新的 Run), 立刻 yield break。
        // Timers.StartCoroutine 返回 void, 拿不到 Coroutine 句柄, 停不了协程, 只能这么作废。
        static int _runGeneration;

        /// <summary>internal: 绕过 player 直接写状态会破坏单帧语义。</summary>
        internal static ScriptedInputSource source { get { return _source; } }

        public static bool active { get { return _current != null; } }

        /// <summary>当前会话的标签, 未接管时为 null。</summary>
        public static string activeLabel { get { return _label; } }

        /// <summary>
        /// 接管输入并返回一个 player。拿 player 的唯一途径就是这里 ——
        /// "忘了接管" 在编译期就不存在。
        /// 已 active 时抛 InvalidOperationException, 消息带上一个会话的 label。
        ///
        /// syncMousePositionFromCurrent: ScriptedInputSource.mousePosition 跨会话延续(见
        /// ScriptedInputSource.ResetAll 的注释), 默认沿用上一次脚本会话记住的虚拟位置,
        /// 让连续多个脚本会话之间的移动序列看起来连贯, 不会凭空跳到 (0,0) 再滑回来。
        /// 但如果两次脚本会话之间夹了一段真实输入(真人真的动过鼠标), 这个"记忆"就是过时的
        /// —— 库自己没法判断中途有没有发生这种情况, 需要调用方自己知道并显式传 true:
        /// 这样会从接管前的 inputSource(通常是真实鼠标)读取当前位置来初始化, 丢弃脚本记忆。
        /// </summary>
        public static StageInputPlayer Start(StageInputMode mode = StageInputMode.Mouse, string label = null,
                                              bool syncMousePositionFromCurrent = false)
        {
            if (_current != null)
                throw new InvalidOperationException(
                    "StageInputSimulator 已被会话 '" + (_label != null ? _label : "<未命名>")
                    + "' 占用。先 Dispose() 那个 player, 或调 ForceReset() 强制归还。");

            _prevInputSource = Stage.inputSource;
            _prevTouchScreen = Stage.touchScreen;

            Vector2 syncPos = _prevInputSource.mousePosition;

            _source.ResetAll();
            if (syncMousePositionFromCurrent) _source.MoveMouse(syncPos);
            Stage.inputSource = _source;

            // 先设模式(不触发 setter 副作用), 再显式复位。次序不能反:
            // ResetInputState 读 touchScreen 决定要不要 _touches[0].touchId = 0。
            Stage.SetTouchScreenRaw(mode == StageInputMode.Touch);
            if (Stage.isInitialized) Stage.inst.ResetInputState();

            _label = label;
            _current = new StageInputPlayer(_source, StageGuiEventSink.instance, mode, EndSession);
            return _current;
        }

        static void EndSession(StageInputPlayer player)
        {
            if (_current != player) return;   // 已被 ForceReset 归还过
            Restore();
        }

        /// <summary>
        /// 强制归还。独占语义下, 上一个会话没被 Dispose 就会永久卡住后续所有 Start(),
        /// 而"没被 Dispose"是必然会发生的: 协程被 StopCoroutine(using 的 finally 不执行)、
        /// MCP 命令超时返回、测试断言抛出。
        /// </summary>
        public static void ForceReset()
        {
            if (_current == null) return;

            Debug.LogWarning("StageInputSimulator.ForceReset(): 会话 '"
                             + (_label != null ? _label : "<未命名>") + "' 未正常 Dispose, 已强制归还");

            StageInputPlayer leaked = _current;
            Restore();
            leaked.MarkDisposed();
        }

        static void Restore()
        {
            _source.ResetAll();

            // 与 Start 同序: 先设模式, 再显式复位, 最后还原 inputSource。
            Stage.SetTouchScreenRaw(_prevTouchScreen);
            if (Stage.isInitialized) Stage.inst.ResetInputState();
            Stage.inputSource = _prevInputSource;

            _running = false;
            _cancelRequested = false;
            _onComplete = null;
            _runGeneration++;

            _current = null;
            _label = null;
            _prevInputSource = null;
        }

        /// <summary>可视化。为 null 表示关(默认)。持有点在 source 上, 这里只是转写。</summary>
        public static IStageInputVisualizer visualizer
        {
            get { return _source.visualizer; }
            set { _source.visualizer = value; }
        }

        const string DefaultVisualizerName = "[FairyGUI InputVisualizer]";

        static ImguiInputVisualizer _defaultVisualizer;

        /// <summary>
        /// 启用零资源的默认可视化, 同时是设置/更新样式的入口 —— 传 style 就把它应用到
        /// 默认实现上, 哪怕默认可视化已经在用也能再调一次改样式(幂等, 不重建 GameObject)。
        /// 懒建一个 DontDestroyOnLoad 的 GameObject, 不依赖 Stage 的 GameObject 生命周期。
        /// </summary>
        public static void UseDefaultVisualizer(InputVisualStyle? style = null)
        {
            if (_defaultVisualizer == null)
            {
                var go = new GameObject(DefaultVisualizerName);
                go.hideFlags = HideFlags.HideAndDontSave;
                if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
                _defaultVisualizer = go.AddComponent<ImguiInputVisualizer>();

                DestroyStaleDefaultVisualizers(_defaultVisualizer);
            }

            if (style.HasValue) _defaultVisualizer.style = style.Value;
            _defaultVisualizer.enabled = true;
            _source.visualizer = _defaultVisualizer;
        }

        /// <summary>
        /// _defaultVisualizer 是静态字段, domain reload 会清掉它; 但它的 GameObject 是
        /// HideAndDontSave, 跨 reload 存活。所以"静态为 null"不等于"实例不存在" ——
        /// 不清理的话每次 reload 后都多一个, 旧实例继续在 OnGUI 里画自己最后已知的状态,
        /// 截图上出现多个假光标, 而截图正是调用方的判读依据。
        ///
        /// 不复用旧实例、只销毁: EditMode 建的实例跨 reload 进了 PlayMode 之后不再收
        /// OnGUI(状态还能读、就是没人画), 复用它等于可视化整体失灵。新建走的是与老代码
        /// 同一条路径(DontDestroyOnLoad), 行为可预期。
        ///
        /// 只清名字对得上的(UseDefaultVisualizer 自己建的那种): 外部换入的自定义
        /// ImguiInputVisualizer 实例归调用方所有, 不能替它做主销毁。
        /// </summary>
        static void DestroyStaleDefaultVisualizers(ImguiInputVisualizer keep)
        {
            ImguiInputVisualizer[] all = Resources.FindObjectsOfTypeAll<ImguiInputVisualizer>();
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null || all[i] == keep) continue;
                if (all[i].gameObject.name != DefaultVisualizerName) continue;

                if (Application.isPlaying) UnityEngine.Object.Destroy(all[i].gameObject);
                else UnityEngine.Object.DestroyImmediate(all[i].gameObject);
            }
        }

        /// <summary>
        /// 关掉可视化推送。只 enabled = false 不 Destroy —— 标记要在会话结束后继续显示,
        /// 截图是另一次独立调用。GameObject 在 visualizer.Dispose() 时才销毁。
        /// 只对默认实现设 enabled(外部换入的自定义 visualizer 没有这个概念, 停推送即可);
        /// 不设的话默认实现的 OnGUI 会一直照旧绘制上一次的状态, 标记永远画在 Game View 上。
        /// </summary>
        public static void DisableVisualizer()
        {
            _source.visualizer = null;
            if (_defaultVisualizer != null) _defaultVisualizer.enabled = false;
        }

        /// <summary>
        /// 清掉当前 visualizer(默认实现或外部换入的自定义实现)已画的标记, 不管开着还是关着。
        /// 兜底 _defaultVisualizer: DisableVisualizer() 之后 source.visualizer 是 null,
        /// 但默认实现的 GameObject 与其内部状态(_pointer/_presses/_touches)还活着, 若不清
        /// 下次 UseDefaultVisualizer() 重新打开时会先闪一下上一次会话的残留标记。
        /// 对两者都为 null 是 no-op —— 调用方不用自己先判空。
        /// </summary>
        public static void ClearVisualizer()
        {
            if (_source.visualizer != null) _source.visualizer.Clear();
            else if (_defaultVisualizer != null) _defaultVisualizer.Clear();
        }

        // ---------------- 序列执行 ----------------

        /// <summary>当前是否有序列在执行。Cancel 的收尾阶段仍为 true。</summary>
        public static bool isRunning { get { return _running; } }

        /// <summary>
        /// 在当前会话内执行一个序列。宿主是 FairyGUI 已有的 Timers 协程引擎 ——
        /// 恢复点在所有 Update() 之后、LateUpdate() 之前, 正是 ScriptedInputSource
        /// 单帧语义要求的那一侧(FGUI 在 StageEngine.LateUpdate 读)。
        /// 手动 pump 的消费方自己挑时点极易挑错, 而挑错是静默丢输入, 文档守不住。
        ///
        /// 注意这个"恢复点安全"的保证不覆盖第一段: StartCoroutine 会同步跑完协程体
        /// 到第一个 yield 为止, 也就是序列里第一段写状态的代码(比如 Click 的
        /// MoveMouse + PressMouse)发生在 Run() 被调用的那一刻、调用者的调用点上 ——
        /// 不是在下一次协程恢复时。所以如果消费方在某一帧的 StageEngine.LateUpdate
        /// 已经跑过之后才调 Run(), 这一段的按下会落在 LateUpdate 已读过的那一帧,
        /// 等同于手动 pump 挑错时点的那种静默丢输入。当前所有调用方都在安全侧调用
        /// (帧开始时, LateUpdate 之前), 但这个边界必须记在这里, 别让后续基于
        /// "整段都在恢复点上"这个错误模型去算帧数。
        ///
        /// onComplete 为 null 时结果被丢弃, 但异常仍会 LogError。
        /// </summary>
        public static void Run(IEnumerator sequence,
                               Action<StageInputRunResult, Exception> onComplete = null)
        {
            if (sequence == null) throw new ArgumentNullException("sequence");

            if (!Application.isPlaying)
                throw new InvalidOperationException(
                    "StageInputSimulator.Run 需要 Play 模式: EditMode 没有 player loop, 协程不会推进。"
                    + "EditMode 下请自己 MoveNext() 推进 IEnumerator。");

            if (_current == null)
                throw new InvalidOperationException(
                    "StageInputSimulator.Run 需要先 Start() 接管输入。未接管时指针注入完全无效"
                    + "而键盘照常生效 —— 半生效比全不生效更难查。");

            if (_running)
                throw new InvalidOperationException(
                    "StageInputSimulator 已有序列在执行 (会话 '"
                    + (_label != null ? _label : "<未命名>") + "')。先等它完成, 或调 Cancel()。");

            _running = true;
            _cancelRequested = false;
            _onComplete = onComplete;

            int gen = ++_runGeneration;
            // Timers.inst 的 getter 会按需 new GameObject, 所以只在过完四道门之后碰它。
            Timers.inst.StartCoroutine(RunRoutine(gen, sequence));
        }

        /// <summary>
        /// 中止当前序列并收尾。不在执行中时是 no-op。
        /// 收尾是异步的, 占一帧 —— 释放只是写 _upFrame, FairyGUI 要在 LateUpdate 读到
        /// GetMouseButtonUp 才走 touch.End()。同步返回的话那次释放没人消费, 业务的
        /// 拖拽状态机会永远停在拖拽中。收尾期间 isRunning 仍为 true。
        ///
        /// 实测(Cancel_MidDrag_DeliversTouchEndToBusiness): 一帧收尾就够, 不用再加一帧。
        /// 原因是 Cancel() 本身从外部同步调用只置标志, 真正的收尾(ReleaseHeldInput)发生在
        /// RunRoutine 下一次协程恢复 —— 那个恢复点本来就在本帧 Update() 之后、LateUpdate()
        /// 之前(Timers 协程引擎的性质), 跟 ReleaseMouse 写的 _upFrame 要被同一帧的
        /// StageEngine.LateUpdate 读到这件事天然对齐, 不需要额外等一帧。
        /// </summary>
        public static void Cancel()
        {
            if (!_running) return;
            _cancelRequested = true;
        }

        /// <summary>释放实际持有的输入。释放什么不用猜, source 自己记着。</summary>
        static void ReleaseHeldInput()
        {
            for (int b = 0; b < 3; b++)
                if (_source.IsMouseHeld(b)) _source.ReleaseMouse(b);

            _source.ReleaseAllHeldKeys();
            _source.EndAllTouches();
        }

        static IEnumerator RunRoutine(int gen, IEnumerator sequence)
        {
            Exception fault = null;

            while (true)
            {
                if (gen != _runGeneration) yield break;
                if (_cancelRequested) break;

                bool moved;
                try
                {
                    moved = sequence.MoveNext();
                }
                catch (Exception ex)
                {
                    // 不包的话协程里的异常只会被 Unity 打一条 error log 然后静默终止,
                    // 调用方永远等不到回调。异常发生时可能正按着键(比如 Drag 中途抛), 所以
                    // 不在这里直接 Finish —— 落到循环外与 Cancel 走同一套收尾。
                    fault = ex;
                    break;
                }
                if (!moved) break;

                yield return sequence.Current;
            }

            if (gen != _runGeneration) yield break;

            if (_cancelRequested || fault != null)
            {
                // 序列可能停在按下的半路(drag 拖到一半): 不释放的话 FGUI 侧那个控件
                // 永久按下, 污染之后所有点击判定。释放什么不用猜, source 自己记着。
                ReleaseHeldInput();
                yield return null;                 // 让 LateUpdate 消费掉这次释放
                if (gen != _runGeneration) yield break;

                _source.ResetAll();
                if (Stage.isInitialized) Stage.inst.ResetInputState();

                Finish(fault != null ? StageInputRunResult.Faulted : StageInputRunResult.Canceled,
                       fault);
                yield break;
            }

            // 完成前额外推一帧。序列末尾通常是"写状态 + yield", 所以 MoveNext 返回 false 时
            // 最后一次注入其实已被上一帧的 LateUpdate 消费 —— 但那依赖"每个序列末尾都有一次
            // yield"这个将来容易破的不变量。而且调用方常在回调后立刻读 Stage.inst.touchTarget,
            // 读早了拿到旧值。这一帧买断这两个风险, 代价约 16ms。
            yield return null;
            if (gen != _runGeneration) yield break;

            Finish(StageInputRunResult.Completed, null);
        }

        static void Finish(StageInputRunResult result, Exception ex)
        {
            Action<StageInputRunResult, Exception> cb = _onComplete;
            _onComplete = null;
            _running = false;
            _cancelRequested = false;

            if (ex != null && cb == null)
                Debug.LogError("StageInputSimulator.Run: 序列抛出异常且没有 onComplete 接收\n" + ex);

            if (cb == null) return;

            // 调用方回调里抛异常不能污染宿主状态。
            try { cb(result, ex); }
            catch (Exception cbEx)
            {
                Debug.LogError("StageInputSimulator.Run 的 onComplete 抛出异常\n" + cbEx);
            }
        }

        /// <summary>控件中心的屏幕坐标。</summary>
        public static Vector2 ScreenPointOf(GObject obj)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            return ScreenPointOf(obj, new Vector2(obj.width / 2f, obj.height / 2f));
        }

        /// <summary>
        /// 控件局部点的屏幕坐标。LocalToGlobal 给的是 Stage 坐标(Y 向下),
        /// 屏幕坐标 Y 向上, 所以 screenY = stageHeight - stageY。
        /// </summary>
        public static Vector2 ScreenPointOf(GObject obj, Vector2 localPoint)
        {
            if (obj == null) throw new ArgumentNullException("obj");
            Vector2 stagePos = obj.LocalToGlobal(localPoint);
            return new Vector2(stagePos.x, Stage.inst.size.y - stagePos.y);
        }
    }

    /// <summary>StageInputSimulator.Run 的完成结果。</summary>
    public enum StageInputRunResult
    {
        Completed,
        Canceled,
        Faulted
    }
}
