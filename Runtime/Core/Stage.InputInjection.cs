using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    public partial class Stage
    {
        // ---------------- 通道 A: Legacy Input 读取边界 ----------------

        static IStageInputSource _inputSource = UnityInputSource.instance;

        /// <summary>
        /// FairyGUI 读取指针 / 触摸 / 修饰键 / IME 组合串的来源。
        /// 必须是 static: InputEvent.ctrl 是属性, 可能在 Stage 尚未初始化时被访问,
        /// 若做成实例属性就得写 Stage.inst.inputSource, 而 inst 的 getter 会按需创建 Stage。
        /// 置 null 回落到 UnityInputSource.instance, 避免误设成 null 让全局输入失灵。
        /// </summary>
        public static IStageInputSource inputSource
        {
            get { return _inputSource; }
            set { _inputSource = value != null ? value : (IStageInputSource)UnityInputSource.instance; }
        }

        /// <summary>
        /// 非创建式的初始化检查。inst 的 getter 会 Instantiate(), 这里不会。
        /// </summary>
        public static bool isInitialized
        {
            get { return _inst != null; }
        }

        /// <summary>
        /// 作废两个帧缓存。改变指针 / 触摸位置后必须调, 否则同帧内早先读过
        /// touchPosition / touchTarget 的代码已经把缓存钉在注入之前的坐标上,
        /// LateUpdate 里的重算会直接 early-return。
        /// 两个都要清: LongPressGesture 的计时器在 Update 阶段调 GetTouchPosition,
        /// 那条路径是无条件 UpdateTouchPosition()。
        /// </summary>
        internal static void InvalidateInputCaches()
        {
            if (_inst == null) return;
            _inst._frameGotHitTarget = -1;
            _inst._frameGotTouchPosition = -1;
        }

        /// <summary>
        /// 直写 touchScreen 的 backing field, 绕开公开 setter 的 inst.ResetInputState() 副作用
        /// (那个副作用会在 EditMode 下凭空创建 Stage)。复位由调用方在 isInitialized 时显式做。
        /// </summary>
        internal static void SetTouchScreenRaw(bool value)
        {
            _touchScreen = value;
            _clickTestThreshold = value ? 50 : 10;
            if (value)
            {
                // 注意: _touchSupportDetected 置真后不还原, 只影响 WebGL 的自动触摸检测分支。
                _touchSupportDetected = true;
            }
            else
            {
                _keyboardInput = false;
                _keyboardOpened = false;
            }
        }

        // ---------------- 通道 B: IMGUI Event 队列 ----------------

        readonly List<Event> _queuedGUIEvents = new List<Event>();
        readonly List<Event> _drainBuffer = new List<Event>();

        /// <summary>
        /// 把一个 IMGUI 事件排进队列, 由 StageEngine.OnGUI 在本帧排空,
        /// 走的是真实事件同一个入口 HandleGUIEvents。
        /// </summary>
        public void QueueGUIEvent(Event evt)
        {
            if (evt != null)
                _queuedGUIEvents.Add(new Event(evt));   // 拷贝: 防调用方复用同一个实例
        }

        /// <summary>
        /// 排空队列。先快照再清空: HandleGUIEvents 会冒泡到业务代码, 业务代码可以再 QueueGUIEvent,
        /// 那些新事件留到下一次排空, 否则会在本轮被消费甚至死循环。
        /// </summary>
        internal void DrainQueuedGUIEvents()
        {
            if (_queuedGUIEvents.Count == 0) return;

            _drainBuffer.Clear();
            _drainBuffer.AddRange(_queuedGUIEvents);
            _queuedGUIEvents.Clear();

            for (int i = 0; i < _drainBuffer.Count; i++)
                HandleGUIEvents(_drainBuffer[i]);

            _drainBuffer.Clear();
        }
    }
}
