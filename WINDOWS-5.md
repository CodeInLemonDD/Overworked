# W5 · 体力 —— 交付说明与观点

窗口:`W5 · 体力`。名下文件:`Assets/Scripts/Player/PlayerStamina.cs`(新建)、
`Assets/Scripts/Player/PlayerMovementPrediction.cs`(修改,独占)。

**状态:代码已交付、编译已验证。运行时未验证 —— MPPM 双实例没跑过,那是用户的活。**

---

## 一、交付了什么

| 文件 | 内容 |
|---|---|
| `Player/PlayerStamina.cs` | 精力(上限)、体力(当前)、移动消耗、静止回满;公开 `Stamina` / `StaminaCap` / `SpeedMultiplier` / `Local` |
| `Player/PlayerMovementPrediction.cs` | `ReplicateData` 加 `public float SpeedMultiplier`;`PerformReplicate` 里用它;`Awake` 里 `GetComponent<PlayerStamina>()` |

**编译验证**:把新文件临时加进 Unity 生成的 `Assembly-CSharp.csproj`,跑
`dotnet build` → **0 错误**;跑完把 csproj 还原(它 gitignored、Unity 自己会重生成)。
顺手的一个发现记在第六节。

---

## 二、核心决定:倍率为什么必须坐在复制数据里

这条是整件事的技术重心,我把依据查到了源码,免得下一个人重推一遍:

- `ReplicateData` 里的字段**必须 public**。FishNet 的 weaver 取字段走
  `CodeGenerating/Helpers/Extension/TypeDefinitionExtensions.cs` 的 `FindAllPublicFields`:
  跳过 static、`[NotSerialized]`、`[ExcludeSerialization]`、**private**。
  写成 private 或属性 → **编译通过、运行静默不发包**。这正是 CONSTRAINTS #10 那一类坑,
  而且它不报错,只会表现为"两端速度不一致"。
  (入口:`PredictionProcessor.cs` 用 `HasSerializerAndDeserializer(replicateDataTd.MakeArrayType(), true)` 自动生成序列化器。)

- **采样点在 `BuildMoveData()`**,和 `Input` 同一处、同一 tick。`PerformReplicate` 里**只读 `rd.SpeedMultiplier`**,
  一次都不碰 `PlayerStamina` 的当前值。回滚重放时用的是那一 tick 存下来的 rd,倍率跟着 tick 走。

- **防御**:`ResolveSpeedMultiplier` 把 `!(v > 0f) || v > 1f` 一律当 1。
  非 owner 构造的 `default(ReplicateData)` 里这个字段就是 **0**,不挡会把角色**冻死**;
  写成 `!(v > 0f)` 而不是 `v <= 0f` 是故意的 —— NaN 对一切比较都返回 false,这样它和 0 落进同一个分支,不用单独判。

---

## 三、我按规格做了,但我认为长期该换一种

**规格明确要求**「把倍率加进 `ReplicateData`,在 `PerformReplicate` 里应用」,我照做了。
但我去翻了官方 demo(`Demos/Prediction/CharacterController/Scripts/CharacterControllerPrediction.cs`),
它做的是**另一套**,而且我认为那套更根本:

> demo 把体力**在 replicate 内部模拟**(`ModifyStamina` / `TryRemoveStamina`),
> 把 `Stamina` 放进 **`ReconcileData`**,倍率在 replicate 里从 `rd.Run` 推出来。

两套都能做到"无拉扯"。差别在**权威归属**:

| | 我实现的(规格版) | 官方 demo 版 |
|---|---|---|
| 体力真相在谁那 | **只在 owner 本地** | 服务端也模拟一份 |
| 服务端知道体力吗 | **不知道** | 知道 |
| 防作弊 | **不防**(客户端说满就满) | 防 |
| 回滚正确性 | 靠 rd 带值 | 靠 `ReconcileData` 带 `Stamina` + idle 计时 |

**我的判断**:这一轮规格版是对的 —— 体力的唯一消费者是移速,owner 本地算完随 rd 发出去,
两端用的就是同一个数,**结构上不可能分歧**,这是最省事也最稳的。但它有一个明确的失效点:

> 一旦体力要承担**服务端判定**的功能(能不能干活、能不能推、NPC 要不要理你),
> 这套就撑不住了 —— 服务端手里根本没有体力这个量。
> 那时候要换成 demo 那套:体力进 replicate 模拟,**并且 `Stamina` 和 idle 计时都要进 `ReconcileData`**,
> 否则回滚重放会从错误的体力起点开始算。

我把这条写进 `PlayerStamina` 的类注释了。**换的那天不是"加个 SyncVar"** ——
加 SyncVar 正是规格点名禁止的错法(回滚时读到另一个时刻的值)。

---

## 四、数值(全部是盲设,按第四节的老规矩,要试玩调)

| 字段 | 默认 | 说明 |
|---|---|---|
| `_staminaCap`(精力) | 100 | |
| `_drainPerSecond` | 12 | 连续跑约 **8.3 秒**见底 |
| `_recoveryDelay` | 2 | 到点**瞬间**回满,不是逐渐涨 |
| `_slowBelowRatio` | 0.35 | 体力 < 35% 才开始变慢 |
| `MinSpeedMultiplier` | 0.3(const) | 速度下限,规格要求"不低于三成" |

两个我拿不准、需要真机定夺的点:

1. **可观测的速度区间很窄** —— 只有底部 35% 的条影响速度,且只降到 0.3×。
   好处是"掉了点体力手感不变",坏处是玩家**几乎感觉不到自己在变慢**,直到突然发现走不动。
   如果试玩觉得"没压力",先动 `_slowBelowRatio`(调大 → 更早变慢)。
2. **"2 秒静止 → 瞬回满"是个硬拐点**。最优解会退化成"跑 8 秒、站定 2 秒、再跑",
   中间没有过渡。规格明确要这个(「原地不动满 2 秒 → 体力回到上限」),我没改。
   但如果试玩觉得像开关不像疲劳,把它改成"2 秒后开始按速率回升"只需改 `TimeManager_OnUpdate`
   里那一个分支 —— **注意规格写的是"回到上限",改之前先问用户**。

**判定"在移动"用的是输入,不是位移** —— 和 `PlayerMovementPrediction` 共用同一个 action、
同一个阈值(`sqrMagnitude > 0.0001f`)。顶着墙推也算消耗。理由写在代码注释里:
奖励"靠着墙不动"会把体力条变成按键谜题。

---

## 五、我超出了冻结接口的部分(需要知会,这是我的主动决定)

规格只冻结了 `Stamina` / `StaminaCap` / `SpeedMultiplier`。我另外加了两个 public 成员:

- **`public static PlayerStamina Local`** —— 本客户端**自己**那个玩家的体力组件。
- **`public const float MinSpeedMultiplier = 0.3f`** —— 生产端与消费端共用同一个下限常量。

加 `Local` 的理由很具体:一个客户端场景里**每个玩家都有一份** `PlayerStamina`。
W6 如果用 `FindObjectOfType<PlayerStamina>()` 拿体力条,返回哪个是**不确定的**,
拿错就是**远程玩家的条,永远是满的** —— 在双实例测试里这看起来像"体力系统没生效",
能查半天。**这是我替 W6 提前挡掉的坑,请转告 W6 用 `Local`。**

这是"自己发明接口"吗?我认为不算 —— 纯增量、不改冻结项、有明确的具体理由。
但**确实超出了规格字面**,所以写在这里备案,而不是埋在代码里。

---

## 六、我没做的 / 需要用户做的

1. **prefab 要手动加组件**(YAML 是约定 D,我不碰):
   `Assets/Prefabs/Player.prefab` **根节点**加 `PlayerStamina`,把
   `Assets/InputSystem_Actions.inputactions` 拖进它的 `Input actions` 字段。
   组件引用是 `GetComponent` 自动找的,`PlayerMovementPrediction` 上**不用**再连线。
   - 没加不会崩,但 Console 会出 warning「never slow down」。这是故意的:
     否则"体力不生效"和"组件忘了加"长得一模一样。

2. **运行时验证一次都没做**。验收标准(两端位置一致、无拉扯、倍率变化中不拉扯)我这边**没有证据**,
   只有结构上的论证。这是我这轮最大的未验证项。

3. **`DEVELOPMENT.md` 的「四、手感调参」表我没有加体力那几行** —— 那个文件不在我名下,
   按规则我只改自己的文件。上面第四节可以直接抄进去。

---

## 七、顺手验出来的、跟本窗口无关的事实(别的窗口可能用得上)

- **`InstanceFinder.IsServer` 在 FishNet 4.7.3 已过时**(CS0618,建议用 `IsServerStarted`,
  且原话提醒"注意 `IsServerInitialized` 与 `IsServerStarted` 的区别")。
  编译输出里 `Npc/Cleaner.cs:129` 和 `Interaction/NetworkGrabbable.cs:255` 都在报。
  **WINDOWS.md 的 W4 规格里恰恰写着让用 `InstanceFinder.IsServer`** —— 规格和 4.7.3 的现实不一致,
  W4 需要知道这件事(是警告不是错误,不阻塞)。

- **`TickNetworkBehaviour.TimeManager_OnUpdate` 跑在 tick 之前,不是之后。**
  `TimeManager.cs:158` 默认 `_updateOrder = UpdateOrder.BeforeTick`,而 `TickUpdate()` 每帧只调一次 `OnUpdate`
  (`TimeManager.cs:366` 起,两个分支互斥)。所以"每帧一次"成立、"在 tick 之后"**不成立** ——
  我原先的注释就写错了,已改。任何在 `TimeManager_OnUpdate` 里读 tick 结果的人都要注意这一条。

- **`Awake()` 的 CS0114 是本项目的既有基线,不要"修"。**
  `PlayerMovementPrediction` / `PlayerInteraction` 都声明 `private void Awake()`,
  编译报"隐藏了 `TickNetworkBehaviour.Awake()`"。这是 weaver 的工作方式:
  它把用户的 `Awake` 改名成 `Awake_UserLogic_*`,再生成一个真正的 `Awake` 串起网络初始化。
  我在 `PlayerStamina` 里**沿用了同一种写法**,明知会多一条警告 —— 一致性 > 少一条警告,
  改成 `override` 有可能改变 weaver 的处理路径,不值得为一条警告冒这个险。

- **离线验编译有一条比"直接调 csc.dll"更省事的路**:
  Unity 生成的 `Assembly-CSharp.csproj` 是新鲜的(它甚至已经包含 W3 的 `PlacementBlocker.cs`、
  W4 的 `Cleaner.cs`),直接
  `dotnet build Assembly-CSharp.csproj` 就能跑,用的是同一套 Roslyn、同一套 analyzer。
  唯一要动的:新文件不在 `Compile Include` 列表里(Unity 没聚焦就不会重生 csproj),
  临时加一行、跑完删掉即可 —— 它 gitignored,不污染仓库。
  注意这条**不能替代真机测试**,只是把编译错误挡在交付之前。

---

## 八、我认为该改、但不在我名下的东西

1. **`CONSTRAINTS.md` 第 6 条只说了 `OnTick` 的坑**,没说 `TimeManager_OnUpdate` 的**触发时机**
   (默认在 tick 之前)。这条已经咬过我一次(注释写错)。值得补进那份文档,因为它是跨模块的。

2. **`CONSTRAINTS.md` 值得加一条「复制结构里想被序列化的字段必须 public」的正面说明。**
   现在这条只在 #10 里以"SyncList 类型参数"的语境出现,写 `IReplicateData` 的人不一定会联想到。
   `SpeedMultiplier` 写错成 private 的后果是**静默的**,我觉得它够格单独一条。

3. **WINDOWS.md 里 W4 那段的 `InstanceFinder.IsServer` 建议应改成 `IsServerStarted`**
   (或至少注明已过时)。

---

## 九、一句话总结

代码这块我认为是干净的:倍率**结构上**不可能和两端位置分歧,这是这轮唯一真正的验收标准。
真正的风险不在实现,在**没测** —— 以及一个设计上的定时炸弹:**体力现在是纯客户端说的算**,
等它开始承担服务端判定,这套结构就得换成 demo 那套,并且要连 `ReconcileData` 一起改。
