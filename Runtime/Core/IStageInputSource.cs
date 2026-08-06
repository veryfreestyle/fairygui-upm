using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// FairyGUI 读取 Legacy 原始输入的边界。默认实现直接转调 UnityEngine.Input;
    /// 把 Stage.inputSource 换成别的实现即可驱动 FairyGUI, 且走的是 Player 同样的分支。
    /// </summary>
    public interface IStageInputSource
    {
        bool touchSupported { get; }
        Vector2 mousePosition { get; }
        bool GetMouseButtonDown(int button);
        bool GetMouseButtonUp(int button);
        int touchCount { get; }

        // 全限定: Stage.cs 在 FAIRYGUI_INPUT_SYSTEM 下会把 Touch 别名到 Input System 的类型。
        UnityEngine.Touch GetTouch(int index);

        bool GetKey(KeyCode key);
        string compositionString { get; }
    }

    /// <summary>
    /// 帧计数与非缩放时间。存在的理由是可测性: 单帧语义在 EditMode 里没法用真的 Time.frameCount 验。
    /// </summary>
    public interface IFrameClock
    {
        int frameCount { get; }
        float unscaledTime { get; }
    }

    /// <summary>
    /// IMGUI 事件的投递口。默认实现转调 Stage.inst.QueueGUIEvent。
    /// </summary>
    public interface IGuiEventSink
    {
        void Queue(Event evt);
    }

    /// <summary>
    /// 默认输入源: 逐个透传到 UnityEngine.Input。
    /// </summary>
    public sealed class UnityInputSource : IStageInputSource
    {
        public static readonly UnityInputSource instance = new UnityInputSource();

        public bool touchSupported { get { return Input.touchSupported; } }
        public Vector2 mousePosition { get { return Input.mousePosition; } }
        public bool GetMouseButtonDown(int button) { return Input.GetMouseButtonDown(button); }
        public bool GetMouseButtonUp(int button) { return Input.GetMouseButtonUp(button); }
        public int touchCount { get { return Input.touchCount; } }
        public UnityEngine.Touch GetTouch(int index) { return Input.GetTouch(index); }
        public bool GetKey(KeyCode key) { return Input.GetKey(key); }
        public string compositionString { get { return Input.compositionString; } }
    }

    /// <summary>
    /// 默认时钟: 透传 UnityEngine.Time。
    /// </summary>
    public sealed class UnityFrameClock : IFrameClock
    {
        public static readonly UnityFrameClock instance = new UnityFrameClock();

        public int frameCount { get { return Time.frameCount; } }
        public float unscaledTime { get { return Time.unscaledTime; } }
    }

    /// <summary>
    /// 默认投递口: 进 Stage 自有的 GUI 事件队列, 由 StageEngine.OnGUI 排空。
    /// </summary>
    public sealed class StageGuiEventSink : IGuiEventSink
    {
        public static readonly StageGuiEventSink instance = new StageGuiEventSink();

        public void Queue(Event evt)
        {
            Stage.inst.QueueGUIEvent(evt);
        }
    }
}
