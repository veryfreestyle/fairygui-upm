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
                var go = new GameObject("[FairyGUI InputVisualizer]");
                go.hideFlags = HideFlags.HideAndDontSave;
                if (Application.isPlaying) UnityEngine.Object.DontDestroyOnLoad(go);
                _defaultVisualizer = go.AddComponent<ImguiInputVisualizer>();
            }

            if (style.HasValue) _defaultVisualizer.style = style.Value;
            _defaultVisualizer.enabled = true;
            _source.visualizer = _defaultVisualizer;
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
        /// 但默认实现的 GameObject 与其内部状态(_pointer/_ripples/_touches)还活着, 若不清
        /// 下次 UseDefaultVisualizer() 重新打开时会先闪一下上一次会话的残留标记。
        /// 对两者都为 null 是 no-op —— 调用方不用自己先判空。
        /// </summary>
        public static void ClearVisualizer()
        {
            if (_source.visualizer != null) _source.visualizer.Clear();
            else if (_defaultVisualizer != null) _defaultVisualizer.Clear();
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
