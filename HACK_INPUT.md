# hack-input：程序驱动 FairyGUI 输入（调用方使用指南）

适用：本 fork 的 `hack-input` 分支，`com.veryfreestyle.unity.fairygui` **5.2.4** 起（`unity: 2021.3+`）。
这些 API 是本 fork 独有的，上游原版 FairyGUI 没有——装了上游包，本文提到的类型全部不存在（引用即编译不过）。

本文只讲**怎么用它驱动 FGUI 输入**（写 PlayMode 测试、写工具、写引导演示）。fork 内部改了哪些读点、为什么这么改、升版怎么打 patch，属维护/移植视角，不在本文范围。

---

## 1. 这是什么

一套让你**用代码驱动 FairyGUI 界面**的输入注入面：点击、拖拽、多指手势、键盘、文本、滚轮，都能程序触发，且 **Editor 与真机走同一条代码路径**（注入点落在 FGUI 读原始输入的边界，不经 OS）。

命名空间 `FairyGUI`，程序集 `FairyGUI`。核心三个类型：

| 类型 | 角色 | 你会怎么碰它 |
|---|---|---|
| `StageInputSimulator` | 静态门面：接管 / 归还会话、执行序列、坐标换算、可视化 | 入口，几乎所有调用从这里起手 |
| `StageInputPlayer` | 一次会话内的全部手势与序列方法 | 从 `Start()` 拿到它，调它的 `Click` / `Drag` / `SendKey`… |
| `ScriptedInputSource` | 可编程输入源，被门面持有 | 一般不直接碰；只在 EditMode 单测里用假实现替它 |

能力矩阵（√ = 直接有方法）：

| 能力 | Mouse 模式 | Touch 模式 |
|---|---|---|
| 移动 / 悬停 | `MoveTo` `Hover` `MoveAtSpeed` | —（触摸没有悬停） |
| 点击 / 双击 | `Click` `DoubleClick` | `Tap` |
| 按下 / 抬起（跨命令保持） | `Press` `Release` | `PressFinger` `ReleaseFinger` |
| 拖拽 | `Drag` `DragAtSpeed` | `TouchDrag` |
| 多指缩放 / 旋转 | — | `TwoFingerTransform` |
| 键盘按键（带 keyCode） | `SendKey` | `SendKey` |
| 文本录入（含中文，逐 char，BMP 内） | `TypeText` `TypeTextAtRate` | `TypeText` `TypeTextAtRate` |
| 修饰键（Ctrl/Shift/Alt/Cmd） | `HoldModifiers` `SendKey` | `HoldModifiers` `SendKey` |
| 滚轮 | `Scroll` | —（真机无滚轮） |
| 只推帧不改状态 | `Step` `StepMs` | `Step` `StepMs` |

**方法按模式分组，跨模式调用会抛 `InvalidOperationException`**——那些状态在另一种模式下物理上不存在（详见 §5）。`SendKey` / `TypeText` / `HoldModifiers` / `Step` 两种模式都能用。

---

## 2. 最短上手

一个 PlayMode 测试，点一个按钮并断言 `onClick` 触发：

```csharp
using FairyGUI;
using NUnit.Framework;
using UnityEngine.TestTools;

[UnityTest]
public IEnumerator ClickButton_FiresOnClick()
{
    // ... 已加载 UIPackage、拿到按钮 GObject btn ...
    using (var player = StageInputSimulator.Start(label: nameof(ClickButton_FiresOnClick)))
    {
        bool fired = false;
        btn.onClick.Add(() => fired = true);

        yield return player.Click(StageInputSimulator.ScreenPointOf(btn));

        Assert.IsTrue(fired);
    }
}
```

三件事就够上手：
- `StageInputSimulator.Start(...)` 接管输入，返回一个 `StageInputPlayer`，`using` 结束时自动归还。
- `StageInputSimulator.ScreenPointOf(gobject)` 把控件换算成屏幕坐标。
- `player.Click(pos)` 返回 `IEnumerator`，`yield return` 它就跑完了这次点击（固定 3 帧）。

---

## 3. 三类调用方怎么接入

`StageInputPlayer` 的方法返回纯 `IEnumerator`，谁来推帧决定了接入写法：

### 3.1 PlayMode 测试 / 正式代码（有 Unity 协程驱动）

直接 `yield return`（`[UnityTest]`）或 `StartCoroutine`：

```csharp
yield return player.MoveTo(pos, 20);   // 20 帧插值移动
yield return player.Click(pos);

// 时间驱动（不吃帧率假设）
yield return player.MoveAtSpeed(pos, 500f);          // 500 像素/秒
yield return player.StepMs(500f);                    // 停 500 毫秒
yield return player.DragAtSpeed(from, to, 500f, holdBeforeMs: 200f, holdAfterMs: 0f);
```

正式代码里若要中途能停，配 `Dispose()` 兜底（协程被 `StopCoroutine` 时 `using` 的 finally **不执行**）：

```csharp
IEnumerator Demo()
{
    using (_player)                      // 正常跑完 / 内部异常都会归还
    {
        yield return _player.MoveAtSpeed(pos, 1000f);
        yield return _player.Click(pos);
    }
}
void OnDisable()
{
    if (_running != null) { StopCoroutine(_running); _running = null; }
    _player?.Dispose();                  // 幂等，兜住 StopCoroutine 不走 finally 的情况
}
```

### 3.2 手动 pump 的调用方（如 UnityMCP 命令层）

没有 Unity 协程宿主、要自己驱动的调用方，**不要自己 `MoveNext`**——用 `StageInputSimulator.Run`，它把序列挂到 FairyGUI 自带的 `Timers` 协程引擎上跑，恢复点天然落在正确时点（`Update` 之后、`LateUpdate` 之前），避开「挑错时点导致输入静默丢失」这个坑：

```csharp
StageInputSimulator.Run(player.Click(pos), (result, ex) =>
{
    if (ex != null)                                    { /* Faulted，序列内抛异常 */ }
    else if (result == StageInputRunResult.Completed)  { /* 正常跑完 */ }
    else                                               { /* Canceled */ }
});
```

桥成 `await`（fork 不引 UniTask/Task，桥接由你做）：

```csharp
var tcs = new UniTaskCompletionSource<StageInputRunResult>();
StageInputSimulator.Run(player.Click(pos),
    (r, ex) => { if (ex != null) tcs.TrySetException(ex); else tcs.TrySetResult(r); });
var result = await tcs.Task;
```

超时中止：`StageInputSimulator.Cancel();`——收尾占一帧、走真实抬起路径（业务能收到 `onTouchEnd`），回调以 `Canceled` 触发。

> **`Run` 的首段在调用点同步执行。** `StartCoroutine` 会同步跑到第一个 `yield` 为止，所以 `Run(player.Click(pos))` 里的 `MoveMouse` + `PressMouse` 发生在你**调 `Run()` 的那一刻**，不是下一次协程恢复。因此手动 pump 的调用方必须在该帧 `LateUpdate` **之前**调 `Run()`（帧刚开始、`Update` 阶段），否则这次按下会落在已经读过的那一帧、下一帧再读就错过——静默丢输入。

---

## 4. API 形状

### 4.1 `StageInputSimulator`（静态门面）

```csharp
public static class StageInputSimulator
{
    // —— 会话 ——
    public static StageInputPlayer Start(StageInputMode mode = StageInputMode.Mouse,
                                         string label = null,
                                         bool syncMousePositionFromCurrent = false);
    public static bool   active { get; }        // 是否已接管
    public static string activeLabel { get; }   // 当前会话 label；未接管为 null
    public static void   ForceReset();          // 强制归还（没被 Dispose 时的逃生口）

    // —— 序列执行（给手动 pump 的调用方）——
    public static bool isRunning { get; }        // 有序列在跑；Cancel 收尾阶段仍为 true
    public static void Run(IEnumerator sequence,
                           Action<StageInputRunResult, Exception> onComplete = null);
    public static void Cancel();                 // 中止当前序列并收尾；没在跑时 no-op

    // —— 坐标 ——
    public static Vector2 ScreenPointOf(GObject obj);
    public static Vector2 ScreenPointOf(GObject obj, Vector2 localPoint);
    public static Vector2 mousePosition { get; } // 当前虚拟指针位置（脚本记的，跨会话延续）

    // —— 可视化（默认关，见 §8）——
    public static IStageInputVisualizer visualizer { get; set; }
    public static void UseDefaultVisualizer(InputVisualStyle? style = null);
    public static void DisableVisualizer();
    public static void ClearVisualizer();
}

public enum StageInputMode { Mouse, Touch }
public enum StageInputRunResult { Completed, Canceled, Faulted }
```

**player 只能从 `Start()` 拿。** `StageInputPlayer` 有一个 `public` 构造函数，但它是给 EditMode 单测用假 `ScriptedInputSource` / `IGuiEventSink` 直接测的——**正常运行代码不要 `new`**：不经 `Start()` 就调 `player.Click(pos)` 能编译能跑，但 `Stage.inputSource` 还没被接管，指针操作**完全无效**，而键盘/滚轮（走另一条通道）却照常生效。半生效比全不生效更难查。

### 4.2 `StageInputPlayer`（会话内的全部方法）

```csharp
public sealed partial class StageInputPlayer : IDisposable
{
    public StageInputMode mode { get; }
    public bool disposed { get; }
    public int  activeFingerCount { get; }        // 当前落着的手指数，Mouse 恒 0
    public void Dispose();                         // 归还会话，幂等
    public static KeyEventStyle keyEventStyle;     // 默认 Split（见 §6）

    // —— 通用 / 时间驱动 ——
    public IEnumerator Step(int frames = 1);
    public IEnumerator StepMs(float ms);
    public IEnumerator ReleaseHeld();              // 释放本会话当前持有的全部输入，帧数 1

    // —— Mouse 模式 ——
    public IEnumerator Hover(Vector2 screenPos);
    public IEnumerator MoveTo(Vector2 to, int steps = 1);
    public IEnumerator MoveTo(Vector2 from, Vector2 to, int steps);
    public IEnumerator MoveAtSpeed(Vector2 to, float pixelsPerSecond);
    public IEnumerator MoveAtSpeed(Vector2 from, Vector2 to, float pixelsPerSecond);
    public IEnumerator Click(Vector2 screenPos, int button = 0);
    public IEnumerator DoubleClick(Vector2 screenPos);
    public IEnumerator Press(Vector2 screenPos, int button = 0);
    public IEnumerator Release(Vector2 screenPos, int button = 0);
    public IEnumerator Drag(Vector2 from, Vector2 to, int steps, int holdFrames = 0);
    public IEnumerator Drag(Vector2 from, Vector2 to, int steps,
                            int holdBeforeFrames, int holdAfterFrames, int button = 0);
    public IEnumerator Drag(Vector2 from, IList<Vector2> path, int holdFrames = 0);
    public IEnumerator Drag(Vector2 from, IList<Vector2> path,
                            int holdBeforeFrames, int holdAfterFrames, int button = 0);
    public IEnumerator DragAtSpeed(Vector2 from, Vector2 to, float pixelsPerSecond,
                                   float holdBeforeMs, float holdAfterMs, int button = 0);
    public IEnumerator Scroll(Vector2 screenPos, float delta, EventModifiers mods = 0);

    // —— 键盘 / 文本 / 修饰键（两种模式都可用）——
    public IEnumerator SendKey(KeyCode key, EventModifiers modifiers = 0);
    public IEnumerator TypeText(string text, int framesPerChar = 1);
    public IEnumerator TypeTextAtRate(string text, float msPerChar);
    public IDisposable HoldModifiers(EventModifiers mods);   // using 作用域内按住
    public void SetComposition(string text);                 // IME 组合中态，仅 Mouse 会话有效

    // —— Touch 模式 ——
    public void PressFinger(int fingerId, Vector2 screenPos);   // 超 5 指抛异常
    public void MoveFinger(int fingerId, Vector2 screenPos);
    public void ReleaseFinger(int fingerId, bool canceled = false);
    public IEnumerator Tap(Vector2 screenPos, int fingerId = 0);
    public IEnumerator TouchDrag(Vector2 from, Vector2 to, int steps,
                                 int fingerId = 0, int holdFrames = 0);
    public IEnumerator TwoFingerTransform(Vector2 center,
                                          float fromDistance, float toDistance,
                                          float fromAngle, float toAngle,
                                          int steps = 10, int fingerId0 = 0, int fingerId1 = 1);
}

public enum KeyEventStyle { Combined, Split }
```

**坐标一律屏幕系**（Unity 左下原点）。`ScreenPointOf(obj)` 给控件中心，`ScreenPointOf(obj, localPoint)` 给控件内某个局部点。所有 `Vector2 screenPos` / `center` 参数、`TwoFingerTransform` 的角度都在屏幕系。

---

## 5. 会话与模式规则

### 5.1 一次 `Start()` 到 `Dispose()` 是「接管作用域」，不是一次输入

中间可以跑任意多个序列。粒度建议：

| 场景 | 推荐粒度 |
|---|---|
| PlayMode 测试 | 一个测试方法一对，或 `[SetUp]` / `[TearDown]` |
| 引导演示 | 一次演示一对 |
| 手动 pump（MCP 每命令独立 RPC） | **每个命令一对**，并让每个动作自包含 |

**独占，不是引用计数**：已 `active` 时再 `Start()` 直接抛 `InvalidOperationException`（消息带上一个会话的 `label`，方便定位是谁没释放）。两个 player 写同一个输入源会互相覆盖状态，所以不允许嵌套。

**残留会卡死后续所有 `Start()`。** 协程被 `StopCoroutine`、命令超时返回、断言抛出，都可能让会话没走 `Dispose`。对策：
- 测试里 `[TearDown]` 无条件 `StageInputSimulator.ForceReset();`。
- 手动 pump 超时后按两态收尾：**有序列在跑**（`isRunning`）→ `Cancel()` 并 await 回调；**没序列在跑但有输入挂着**（如 `Run(player.Press(pos))` 跑完后）→ `Run(player.ReleaseHeld())` 并 await 回调（此时 `Cancel()` 是 no-op，不会有回调）。

### 5.2 `mode` 决定这个会话能用哪半套 API

`Start(mode)` 不只切内部触摸标志，还决定 API 可用集：Mouse 会话调 `Tap` / `PressFinger` 抛异常，Touch 会话调 `Click` / `Scroll` 抛异常。选错模式在编译期查不出，运行期才抛。

**真机上 `Start(Mouse)` 必须显式走这个门面，不能自己拼输入源**：桌面 `touchScreen` 初值恒 `false`，真机初值是 `true`；`Start(Mouse)` 会把它置回 `false`，否则 FGUI 仍在触摸分支、注入的鼠标按键没人读——指针注入静默失效。这层门面已替你处理，前提是你从 `Start()` 拿 player。

### 5.3 `Press` / `Release` 与跨命令保持

`Press(pos)` 之后控件停在按下态，活过一次 `Run` 的边界，供后续 `Move` / `Release` 接着用（这是设计内用法，不是泄漏）。但**别忘了配对 `Release`**：只 `Press` 不 `Release` 就归还会话，`Dispose()` 只清标志、不补写抬起帧，业务永远收不到 `onTouchEnd`。要在中途安全清干净，用 `ReleaseHeld()`。

---

## 6. 帧数与时间驱动

**帧驱动重载的帧数是文档化的确定值**（可直接断言，Editor 失焦节流下只变慢不失败）：

| 序列 | 帧数 |
|---|---|
| `Hover` | 1 |
| `MoveTo(to, steps)` | `steps` |
| `Click` | 3 |
| `DoubleClick` | 6 |
| `Press` / `Release` / `ReleaseHeld` | 各 1 |
| `Drag(from, to, steps, holdFrames)` | `1 + holdFrames + steps + 1` |
| `Drag(from, to, steps, holdBeforeFrames, holdAfterFrames, button)` | `1 + holdBeforeFrames + steps + holdAfterFrames + 1` |
| `Scroll` | 2 |
| `SendKey` | 3 |
| `TypeText(text, framesPerChar)` | `text.Length * framesPerChar` |
| `Tap` | 3 |
| `TouchDrag(from, to, steps, _, holdFrames)` | `1 + holdFrames + steps + 1` |
| `TwoFingerTransform(…, steps)` | `1 + steps + 1` |
| `Step(frames)` | `frames` |

**时间驱动重载**（`MoveAtSpeed` / `DragAtSpeed` / `TypeTextAtRate` / `StepMs`）的帧数取决于运行时帧率，**不保证**——它们按墙钟绝对定位插值，帧多帧少都精确到达终点、时长恒定。要断言帧数用帧驱动版，要断言时长语义（`LongPressGesture` 触发、`holdTime` 的值、双击窗口）用时间驱动版。

> **命名坑：帧驱动与时间驱动分开命名，别混。** `MoveTo(pos, 500)` 是 500 **帧**，`MoveAtSpeed(pos, 500f)` 才是 500 **像素/秒**。少写个 `f` 不会报错，行为差 16 倍。

**用 `Run` 时每个序列多算一帧**：`Run` 跑完会额外推一帧再回调（确保最后一次注入被消费、`Stage.inst.touchTarget` 已更新）。所以 `Run(player.Click(pos))` 实际是 3 + 1 帧。

---

## 7. 必须知道的坑

- **想测「途经行为」必须先插值移动。** `Click` / `Press` / `Scroll` 内部都是把指针一步放到目标（不逐帧插值），不产生途经的 rollover。要测 `onRollOver`/`onRollOut` 链、`ScrollPane` 惯性，先 `MoveTo(target, steps)` 再点。`Hover` 就是 `steps = 1` 的快捷方式——用它即表示「只要终点效果」。
- **相邻按下抬起会让 `holdTime` 退化成 -1。** `Application.targetFrameRate` 默认 -1，down 与 up 相邻一帧时 `evt.holdTime == -1f`。`Click` / `Tap` 内部已各插一帧（所以是 3 帧）；但你自己 `Press` 后紧接 `Release`，中间要至少 `Step(1)`。帧驱动五参 `Drag` 的 `holdBeforeFrames` 传 0 会抛 `ArgumentOutOfRangeException`（防止第一次 `onTouchMove` 的 `holdTime` 退化），至少给 1。
- **`Command` 修饰键只在 macOS 有效。** 非 OSX 平台 `evt.command` 无条件为 `false`（上游行为），Windows 上注入 `Command` 只有 `evt.modifiers` 生效。涉及 `Command` 的断言只在 macOS 写。
- **慢速移动（每帧 <1px）合法、不再抛异常**（老版本曾误加过这条护栏，已删）。但要**越过手势识别器门槛**（`SwipeGesture` / `PinchGesture` 的 `touchDragSensitivity` 默认 10）需要单帧位移够大——帧数给太多反而可能永远触发不了 `onAction`。`TwoFingerTransform` 触发 `RotationGesture` 还有个方向坑：屏幕系 `toAngle` 必须**小于** `fromAngle`（递减）才让局部系旋转量为正，`fromAngle:0 → toAngle:90` 一次都不会触发。
- **`ReleaseFinger(canceled: true)` 与 `false` 的唯一差别是不派发 `onClick`**，其余（`onTouchEnd`、槽位释放）都照常。用来验「业务被系统打断时不误触发」。
- **文本只支持 BMP 内、逐 `char`**；代理对 / emoji 不做。中文能录入（`TypeText("小创AB9客")` 这类可行）。

---

## 8. 可视化（截图判读用）

注入不动真实光标，Game View 截图上看不出注入点在哪。开默认可视化后，会在 Game View 上画出光标与按压圆环，**能被 `screenshot-game-view` 拍到**（AI 靠截图判断「点对了没」时用）：

```csharp
StageInputSimulator.UseDefaultVisualizer();                 // 默认样式（箭头光标）
StageInputSimulator.UseDefaultVisualizer(myStyle);          // 自定义样式，改样式也走这个（幂等）
StageInputSimulator.DisableVisualizer();                    // 关
StageInputSimulator.ClearVisualizer();                      // 清掉已画的标记
```

- 默认**关**（`visualizer == null`）。
- 样式用 `InputVisualStyle`，因保 2021.3 兼容不能用无参构造：改样式从 `InputVisualStyle.Default()` 起手改字段，**别用 `new InputVisualStyle { ... }` 对象初始化器**（没写到的字段会是全透明黑、尺寸 0）。红光标画在红 UI 上看不见，所以颜色务必按背景调。
- **标记不随 `Dispose()` / 会话结束清除**——截图是另一次独立调用、隔了若干帧，标记要留到截图之后。要清就显式 `ClearVisualizer()`。

---

## 9. 边界与已知限制

**注入点在 FGUI 读原始输入的边界，不经 OS。** 两条独立通道各管一列：指针位置 / 鼠标键 / 触摸 / 修饰键 / IME 组合串走 Legacy `Input` 一侧，按键 `keyCode` / 字符 / 滚轮走 IMGUI `Event` 一侧。由此带来的边界：

- **不触发系统输入法**，也**不经 Editor 焦点路由**——Editor 失焦也能注入（对自动化是优点）。
- **不模拟 OS 级事件**：注入只对 FairyGUI 生效，非 FairyGUI 的 uGUI / 原生 IMGUI 不受影响。
- **通道 B（键盘/滚轮）的事件在 `OnGUI` 排空。** 前台运行时实测每帧至少排空一次；理论上失焦、Game View 不重绘的帧里 `OnGUI` 可能不被调、事件滞后到下一帧——无人值守跑 PlayMode 建议保持 Editor 前台。
- **双击 0.35 秒窗口、`LongPressGesture` 触发时间读真实墙钟**（不受注入的假时钟控制）。失焦节流下这两者时序可能被拉长；`LongPressGesture.trigger` 是 public 字段，测试里可调小到几帧。
- **真机 `Start(Mouse)` 会把 `keyboardInput` 置 false**，`InputTextField` 获焦从「打开系统软键盘」切回 IMGUI 文本路径——对 `TypeText` 是好事，但改变了被测对象在真机上的默认行为，属测试失真。
- **hover 到设了 `cursor` 的控件（如 `InputTextField`）会真的换系统光标贴图**，且残留到下次 rollover。上游行为，本 fork 不改。

---

## 10. 下游消费方

UnityMCP 的 `fgui-input` 工具就是这套能力的一个消费方：装了带 hack-input 的 fork 时，它注册 13 个 action（`move`/`click`/`double-click`/`press`/`release`/`drag`/`wheel`/`send-key`/`type-text`/`step`/`begin-session`/`end-session`/`visualize`）；装上游原版包时探测不到这些类型，降级为 4 个 legacy action，Console 留一条 `compatibility mode: missing member: ...` warning。要驱动 FGUI 输入而看到的 action 只有 4 个，先查那条 warning——那是「宿主装的 FairyGUI 没有 hack-input」，不是工具坏了。

设计与推导（为什么两条通道、为什么门面独占、每处坑的源码依据）在 UnityMCP 仓库的 spec 里：`docs/superpowers/specs/2026-08-06-fairygui-input-injection-design.md`（P22）、`docs/superpowers/specs/2026-08-07-p22-1-stage-input-execution-design.md`（P22.1）。本文只讲用法，那两份讲为什么。
