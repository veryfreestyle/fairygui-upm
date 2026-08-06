using System;
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

        /// <summary>internal: 绕过 player 直接写状态会破坏单帧语义。</summary>
        internal static ScriptedInputSource source { get { return _source; } }

        public static bool active { get { return _current != null; } }

        /// <summary>当前会话的标签, 未接管时为 null。</summary>
        public static string activeLabel { get { return _label; } }

        /// <summary>
        /// 接管输入并返回一个 player。拿 player 的唯一途径就是这里 ——
        /// "忘了接管" 在编译期就不存在。
        /// 已 active 时抛 InvalidOperationException, 消息带上一个会话的 label。
        /// </summary>
        public static StageInputPlayer Start(StageInputMode mode = StageInputMode.Mouse, string label = null)
        {
            if (_current != null)
                throw new InvalidOperationException(
                    "StageInputSimulator 已被会话 '" + (_label != null ? _label : "<未命名>")
                    + "' 占用。先 Dispose() 那个 player, 或调 ForceReset() 强制归还。");

            _prevInputSource = Stage.inputSource;
            _prevTouchScreen = Stage.touchScreen;

            _source.ResetAll();
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
}
