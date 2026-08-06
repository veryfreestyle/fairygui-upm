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
        Arrow
    }

    /// <summary>
    /// 默认实现的样式。只作用于 ImguiInputVisualizer, 不进 IStageInputVisualizer 接口。
    /// 必须可调: AI 靠截图判断, 红色光标画在红色 UI 上就是看不见。
    ///
    /// struct 不带字段初始化器 —— 要保持 Unity 2021.3 兼容, 不能用 C# 10 才支持的
    /// struct 无参构造。默认值都在 Default() 里给; 想部分覆盖默认值, 从 Default()
    /// 起手改字段, 不要用 new InputVisualStyle { ... } 对象初始化器,
    /// 否则没写到的字段会是类型默认值(颜色是全透明黑, 尺寸是 0)。
    /// </summary>
    public struct InputVisualStyle
    {
        public CursorShape cursorShape;
        public Color cursorColor;
        public Color cursorBorderColor;
        public float cursorSize;            // 像素
        public float cursorBorderWidth;     // 像素, Crosshair / Dot / Arrow 通用

        public Color pressColor;
        public float pressRingMaxRadius;
        public float pressFadeSeconds;      // 要够跨一次截图往返

        public float lineWidth;             // Crosshair 十字本身的线宽(不含描边)

        // 触摸点跟鼠标光标是两套语义(手指按压 vs 鼠标指针), 独立配置, 不复用 cursorShape。
        public Color touchColor;              // 触摸点(半透明实心圆)填充色
        public float touchRadius;             // 触摸点半径, 像素
        public bool showTouchConnectors;      // 多指(≥2)时要不要在触点之间连线, 常见捏合/旋转手势的可视化习惯
        public Color touchConnectorColor;
        public float touchConnectorWidth;

        public static InputVisualStyle Default()
        {
            return new InputVisualStyle
            {
                cursorShape = CursorShape.Arrow,
                cursorColor = Color.white,
                cursorBorderColor = Color.black,
                cursorSize = 32f,
                cursorBorderWidth = 2f,

                pressColor = new Color(1f, 0.85f, 0.2f, 1f),
                pressRingMaxRadius = 40f,
                pressFadeSeconds = 1f,

                lineWidth = 2f,

                touchColor = new Color(0.3f, 0.7f, 1f, 0.8f),
                touchRadius = 12f,
                showTouchConnectors = true,
                touchConnectorColor = new Color(0.3f, 0.7f, 1f, 0.6f),
                touchConnectorWidth = 3f
            };
        }
    }
}
