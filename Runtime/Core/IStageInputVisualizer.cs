using System.Collections.Generic;
using UnityEngine;

namespace FairyGUI
{
    /// <summary>
    /// 注入不动真实光标, Game View 截图上看不出注入点在哪。可视化把注入状态画出来,
    /// 只画不消费事件, 为 null 时整段跳过。由 ScriptedInputSource 在状态变更时推。
    /// </summary>
    public interface IStageInputVisualizer
    {
        void OnPointerMove(Vector2 screenPos);
        void OnPointerDown(Vector2 screenPos, int button);
        void OnPointerUp(Vector2 screenPos, int button);
        void OnTouches(IList<UnityEngine.Touch> touches);

        /// <summary>清除已画的标记。注意: 会话结束不会自动清, 标记要留到截图之后。</summary>
        void Clear();

        /// <summary>销毁自身持有的资源。与 StageInputPlayer.Dispose() 无关。</summary>
        void Dispose();
    }

    public enum CursorShape
    {
        Crosshair,
        Dot,
        Ring
    }

    /// <summary>
    /// 默认实现的样式。只作用于 ImguiInputVisualizer, 不进 IStageInputVisualizer 接口。
    /// 必须可调: AI 靠截图判断, 红色光标画在红色 UI 上就是看不见。
    /// </summary>
    public sealed class InputVisualStyle
    {
        public CursorShape cursorShape = CursorShape.Crosshair;
        public Color cursorColor = new Color(1f, 0.2f, 0.2f, 0.9f);
        public float cursorSize = 24f;              // 像素

        public Color pressColor = new Color(1f, 0.85f, 0.2f, 0.9f);
        public float pressRingMaxRadius = 40f;
        public float pressFadeSeconds = 2f;         // 要够跨一次截图往返

        public bool showTrail = true;
        public int trailLength = 32;
        public Color trailColor = new Color(0.2f, 0.9f, 1f, 0.7f);
        public float lineWidth = 2f;
    }
}
