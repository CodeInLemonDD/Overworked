# 并行窗口开工规格

每个窗口的提示词都是**整段可复制**的。复制时把该窗口那一段整个贴进去。

---

## 通用规则(每个提示词里都已经包含)

- 只改**你名下的文件**。需要动别人的文件,停下来问,不要自己改
- 交付前必须**让项目能编译**。项目没有 asmdef,单程序集 —— 你写出编译错误,所有窗口都跑不起来
- 接口不清楚就**先问**。已冻结的接口就是用来防止各写各的
- Unity 编辑器一次只开一个,**编译验证由用户统一切焦点触发**,不要指望自己验证

---

## 排期

| 窗口 | 依赖 | 现在能开吗 |
|---|---|---|
| **W3 吸附阻挡** | 无 | ✅ 立刻 |
| **W4 保洁阿姨** | 无 | ✅ 立刻 |
| **W5 体力** | 无 | ✅ 立刻 |
| **W6 调试 HUD** | P0 | ✅ 立刻 |
| **W1 打印机** | P0 + **P0.1** | ⏸ 等 P0.1 |
| **W2 原料箱** | P0 + **P0.1** | ⏸ 等 P0.1 |

**P0.1(工位交互接缝)** 由核心窗口写,内容:`Stations/StationBase.cs` + `PlayerInteraction` 里的一个通用交互 RPC。没有它,W1/W2 会各自发明一套转发,必然分叉。

---

# W3 · 吸附阻挡

**名下文件**:`Assets/Scripts/Interaction/PlacementBlocker.cs`(新建)、`Assets/Scripts/Interaction/SnapSurface.cs`(修改)

**要修的 bug**:桌上固定物件如果是**桌子的子节点**,射线命中的是那个物件,而 `GetComponentInParent<SnapSurface>()` 会**顺着往上找到桌子的 `SnapSurface`** → `surfaceY` 取到**固定物件顶面的高度** → 物体被强行吸到它上面。

**做**:
1. 新建 `PlacementBlocker`:空标记 MonoBehaviour,`[DisallowMultipleComponent]`,写清楚它为什么存在
2. `SnapSurface` 加 `bool _acceptsObjects = true`(整桌停用)
3. `TrySample` 与 `TryFind` 命中后:若该 `SnapSurface` 不接受物件,或命中碰撞体与它之间隔着 `PlacementBlocker`,判该格不可用
4. **保留**现有的 `TryRaycastDown` 自身排除逻辑与 `OnValidate` 网格居中警告

**验收**:被 `PlacementBlocker` 挡住的格子,物体**不会**被吸上去;`_acceptsObjects` 关掉后整桌拒绝;干净桌子的行为**一字不变**;没挂在桌子下的固定物件(场景同级)本来就被拒,不能回归。**必须在 `TryFind` 内部拦**,因为工位选候选格也走它。

```
项目:E:\UnityProject\Overworked(Unity 6000.6.0f1 + FishNet 4.7.3 + URP,新输入系统,无 asmdef)

开工前必读,按顺序:
1. E:\UnityProject\Overworked\CONSTRAINTS.md —— 硬约束与已冻结接口。违反任何一条都会当场坏掉
2. E:\UnityProject\Overworked\DEVELOPMENT.md —— 项目已有的坑

你名下(只能改这些):
- Assets/Scripts/Interaction/PlacementBlocker.cs   (新建)
- Assets/Scripts/Interaction/SnapSurface.cs         (修改)

规则:
- 只改你名下的文件,一个字都不要碰别人的。需要改别人的文件,停下来告诉我
- 交付前必须让项目能编译。这是单程序集,你的编译错误会让所有窗口都跑不起来
- 接口不清楚先问,不要自己发明

任务:修「物体被强行吸附到桌上固定物件上」的 bug

根因:探测射线从上往下打,打到的是桌上那个固定物件,而 SnapSurface.TryFind / TrySample
里用 hit.collider.GetComponentInParent<SnapSurface>() 会顺着父级往上找到【桌子的】SnapSurface,
于是 surfaceY 取到的是【固定物件顶面】的高度 —— 物体就被吸到打印机顶上了。

要做:
1. 新建 Assets/Scripts/Interaction/PlacementBlocker.cs —— 空标记 MonoBehaviour,
   [DisallowMultipleComponent],XML 注释写清楚它为什么存在(给固定物件挂,表示「这一格不能被吸附」)
2. SnapSurface 加一个 bool _acceptsObjects = true(Inspector 带 Tooltip),用于整张桌子停用
3. TrySample 和 TryFind 在命中之后判定:若该 SnapSurface 不接受物件,或者命中碰撞体向上走到
   SnapSurface 的路径上隔着 PlacementBlocker,就判该格不可用
4. 保留现有的 TryRaycastDown 自身排除逻辑,也保留 OnValidate 的网格居中警告

注意:必须在 TryFind 内部拦,不能只在调用点拦 —— 工位系统选候选格也走 TryFind。

验收(用户会用 MPPM 双实例测):
- 桌上放一个挂了 PlacementBlocker 的子物件,往那一格放方块 → 不被吸上去
- 该桌 _acceptsObjects 取消勾选 → 整桌拒绝放置
- 干净桌子的行为一字不变
- 没挂在桌子下的固定物件(场景同级)本来就被拒,不能出现回归
```

---

# W4 · 保洁阿姨

**名下文件**:`Assets/Scripts/Npc/Cleaner.cs`(新建)

**规则(已定)**:
- 以她为中心的 **3×3 格**范围内收走
- 收:**留在地面上**的原料与散落文件
- **不收**:在桌子上的;在容器里的(文件夹、打印机)—— 后者自动成立,容器内容物根本不在世界里

**关键实现提示**:别用射线判断「在不在桌上」。服务端有现成的精确信号 ——
`NetworkGrabbable.State == Idle && PlacedCell == null` 就是「静止在地面上」;
`PlacedCell.HasValue` 就是「在桌上」。比射线干净得多。

**做**:
- 普通 MonoBehaviour,**不要** NetworkObject(巡逻是确定性的,各端自己跑)
- 巡逻沿一串授权点循环,匀速;**巡逻在各端都跑**(为了看得见),**清扫只在服务端跑**(`InstanceFinder.IsServer`)
- 清扫时用 `GrabbableSpawner.CollectSpawnedGrabbables` 拿快照,**绝不直接遍历 `Spawned`**(硬约束 #1)
- 跳过:被持有的、移动中的、已上桌的

**验收**:她走过去 → 地上的东西没了,桌上的还在,机器里的还在,别人手上拿着的还在,半空中飞着的还在。

```
项目:E:\UnityProject\Overworked(Unity 6000.6.0f1 + FishNet 4.7.3 + URP,新输入系统,无 asmdef)

开工前必读,按顺序:
1. E:\UnityProject\Overworked\CONSTRAINTS.md —— 硬约束与已冻结接口。违反任何一条都会当场坏掉
2. E:\UnityProject\Overworked\DEVELOPMENT.md —— 项目已有的坑

你名下(只能改这些):
- Assets/Scripts/Npc/Cleaner.cs   (新建,目录也要你建)

规则:
- 只改你名下的文件。需要改别人的文件,停下来告诉我
- 交付前必须让项目能编译。单程序集,你的编译错误会让所有窗口都跑不起来
- 接口不清楚先问,不要自己发明

任务:保洁阿姨

行为规则(已和用户确认,不要改):
- 她沿着固定巡逻路线走,以她为中心的 3x3 格范围内收走东西
- 收:留在地面上、没人拿的原料和散落文件
- 不收:在桌子上的;在容器里的(文件夹里、打印机里)
- 她的巡逻路线必须是【可预测的】—— 固定点循环、匀速。随机游走会让玩家觉得是倒霉而不是压力

实现要点:
- 普通 MonoBehaviour,不要 NetworkObject。巡逻是确定性的,各端自己跑就行
- 巡逻(移动)在每个端都跑,为了看得见;清扫只在服务端跑(InstanceFinder.IsServer)
- 判断「在不在桌上」不要用射线。服务端有精确信号:
    NetworkGrabbable.State == Idle && PlacedCell == null   → 静止在地面
    NetworkGrabbable.PlacedCell.HasValue                    → 在桌上
- 清扫必须先快照:用 GrabbableSpawner.CollectSpawnedGrabbables(manager, buffer) 拿到列表,
  再遍历那个列表去 despawn。绝对不要边遍历 ServerManager.Objects.Spawned 边 despawn ——
  它是 Dictionary 的活视图,会抛 InvalidOperationException(见 CONSTRAINTS.md 第 1 条)
- 用 DespawnType.Destroy(显式传,不要用 prefab 默认)
- 跳过:被持有的、正在移动的(速度超过一个小阈值)、已上桌的

验收(用户会用 MPPM 双实例测):
- 地上丢一个方块在她路线上 → 她走过之后没了
- 桌上的方块 → 还在
- 塞进打印机里的原料 → 还在
- 别人手上拿着的 → 还在
- 半空中飞着的 → 还在
```

---

# W5 · 体力

**名下文件**:`Assets/Scripts/Player/PlayerStamina.cs`(新建)、`Assets/Scripts/Player/PlayerMovementPrediction.cs`(修改,**独占,别人不许碰**)

**三个必须知道的前提**:
1. **「体力不足→移速下降」的倍率必须是复制状态的一部分,并且必须经由 `[Replicate]` 的 `ReplicateData` 结构传进去。** 不能在里面直接读 `SyncVar` —— 回滚重放时会用「另一个时刻」的值,两端算出不同的位置,表现为疯狂拉扯。
2. **体力见底只是变慢,不是不能动。** 速度下限别低于正常的三成,否则体感上等于卡死。
3. 恢复条件:**原地不动 2 秒**回满。

**做**:`PlayerStamina`(精力=上限,体力=当前,移动消耗,静止回满),改动 `PlayerMovementPrediction` 把倍率放进复制数据。公开 `Stamina` / `StaminaCap` / `SpeedMultiplier` 供 W6 显示。

**验收**:**两个实例看到的移动位置一致、无拉扯** —— 这是唯一真正的验收标准,其他都是次要的。

```
项目:E:\UnityProject\Overworked(Unity 6000.6.0f1 + FishNet 4.7.3 + URP,新输入系统,无 asmdef)

开工前必读,按顺序:
1. E:\UnityProject\Overworked\CONSTRAINTS.md —— 硬约束与已冻结接口。违反任何一条都会当场坏掉
2. E:\UnityProject\Overworked\DEVELOPMENT.md —— 项目已有的坑

你名下(只能改这些):
- Assets/Scripts/Player/PlayerStamina.cs            (新建)
- Assets/Scripts/Player/PlayerMovementPrediction.cs (修改 —— 这个文件你独占,别人不会碰)

规则:
- 只改你名下的文件。需要改别人的文件,停下来告诉我
- 交付前必须让项目能编译。单程序集,你的编译错误会让所有窗口都跑不起来
- 接口不清楚先问,不要自己发明

任务:体力系统

设计(已和用户确认):
- 两层:精力决定体力上限;体力决定当前能干什么
- 原地不动满 2 秒 → 体力回到上限
- 移动持续消耗体力
- 体力低 → 移动变慢,但【只是变慢,不是不能动】。速度下限不要低于正常的三成

最重要的技术约束 —— 移速倍率必须走预测管线的复制数据:
PlayerMovementPrediction 是主机权威 + 预测回滚(见 DEVELOPMENT.md)。速度倍率如果是本地算的、
或者在 [Replicate] 方法里直接读 SyncVar,回滚重放时会用「另一个时刻」的倍率值,两端算出的位置
不一致 → 表现为疯狂拉扯。正确做法是把倍率作为字段加进 ReplicateData 结构一起复制,
在 PerformReplicate 里用它。

要做的:
- PlayerStamina:精力(上限)、体力(当前)、移动消耗、静止回满。自己 Instantiate 一份
  InputActionAsset(项目约定:共享资产会导致一个玩家 Disable 掉另一个玩家的输入,见 CONSTRAINTS.md 约定 A)
- 改 PlayerMovementPrediction:把速度倍率加进 ReplicateData,在 PerformReplicate 里应用
- 公开 Stamina / StaminaCap / SpeedMultiplier,给调试 HUD 用

验收(用户会用 MPPM 双实例测):
- 两个实例看到的移动位置一致,没有拉扯感 —— 这是唯一真正的验收标准
- 倍率在移动中变化时不产生拉扯
- 连续走 → 体力下降、速度变慢;停下 2 秒 → 回满
```

---

# W6 · 调试 HUD

**名下文件**:`Assets/Scripts/UI/DebugHud.cs`(新建)

**做**:运行时用代码建 Canvas + TMP 文字。显示:场上各容器的内容(数量与条目类型);本地玩家的体力(如果 W5 已合入)。字体用 `Assets/Font/simhei SDF.asset`,通过 Inspector 上的 `TMP_FontAsset` 字段指过去。

**三条硬性 UI 约束(否则会吃掉 FishNet demo 左上角 Host/Client 按钮的点击,而 MPPM 测试就靠它们)**:
- Canvas `sortingOrder >= 1`
- **不挂 `GraphicRaycaster`**
- 所有 graphic `raycastTarget = false`

**不做**:任务板、Tab 总览 —— 那要等 NPC 那一轮(「任务」的定义已经变成 NPC 需求了)。

```
项目:E:\UnityProject\Overworked(Unity 6000.6.0f1 + FishNet 4.7.3 + URP,新输入系统,无 asmdef)

开工前必读,按顺序:
1. E:\UnityProject\Overworked\CONSTRAINTS.md —— 硬约束与已冻结接口。违反任何一条都会当场坏掉
2. E:\UnityProject\Overworked\DEVELOPMENT.md —— 项目已有的坑

你名下(只能改这些):
- Assets/Scripts/UI/DebugHud.cs   (新建,目录也要你建)

规则:
- 只改你名下的文件。需要改别人的文件,停下来告诉我
- 交付前必须让项目能编译。单程序集,你的编译错误会让所有窗口都跑不起来
- 接口不清楚先问,不要自己发明

任务:调试用 HUD

内容(这一轮只要这些,不要做任务板、不要做 Tab 总览 —— 「任务」的定义已经变成 NPC 需求,那部分和 NPC 一起做):
- 列出场上各容器(ContainerBase)的内容:条目数量与类型
- 本地玩家的体力/精力(如果 PlayerStamina 已经合入;没有就跳过,不要为了它改别人的文件)
- 玩家正看着哪个工位(可选)

做法:
- 运行时用代码建 Canvas,用 TextMeshProUGUI
- 字体指 Assets/Font/simhei SDF.asset —— 通过 Inspector 上的 TMP_FontAsset 字段,不要写死路径
- 中文字要能正常显示

三条硬性 UI 约束(违反会直接毁掉测试流程):
- Canvas 的 sortingOrder >= 1
- 【不要】挂 GraphicRaycaster
- 所有 graphic 的 raycastTarget = false
原因:FishNet 自带的 demo 界面(左上角 Logo + Host/Client 两个按钮)是 ScreenSpaceOverlay、
sortingOrder 0。我们一个全屏 RectTransform 盖上去会吃掉那两个按钮的点击,而 MPPM 双实例测试
就是靠它们连主机的。见 CONSTRAINTS.md 约定 C。

验收:
- 中文字正常显示
- 左上角 Host / Client 按钮仍然点得动(这是最重要的一条)
- 数值实时更新
```

---

# W1 · 打印机(等 P0.1)

**名下文件**:`Assets/Scripts/Stations/Printer.cs`、`Assets/Scripts/Stations/PrinterQueue.cs`

**做**:`StationBase` 的子类。
- 根上:`ContainerBase`(输入:纸 + 墨)、`PlacementBlocker`(机器占住那格)、碰撞体
- 子物件 `Output`:`ContainerBase` + `ContainerView`(产物堆在机器身上,可视化)
- 输入齐了**自动**开工,消耗输入、把产物追加进 Output
- **按阵营分队列**:用 `ContainerEntry.OwnerClientId`。调度规则 —— 只有一方有活时给它**全部**产能,双方都有活时**交替**
- 电源这一轮**恒为开**,只留一个 `IsPowered` 接口

**为什么这么分队列**:这样「往队列里灌自己的活」只烧自己的纸、只堵自己,而「让打印机一直有活干」就变成了压制对手的手段 —— **靠干活压制,不靠捣乱**。不需要任何反骚扰机制。

**这一轮不做数据**:`电脑 → 文件数据` 那条链没有,所以打印条件先用「纸 + 墨」。

---

# W2 · 原料箱(等 P0.1)

**名下文件**:`Assets/Scripts/Stations/PaperBox.cs`

**做**:`StationBase` 的子类。一个 `ContainerBase`,**免费但慢速自补**(默认 8 秒补一份)。短按 E、双手为空 → 从容器取一份,生成真实物体**到手里**。

**为什么免费**:材料经济不用钱,用时间。原料箱慢速自补 = 真正的货币是「跑一趟的时间」,而且**结构上不可能死锁**。指标是单调分数,永远不可花 —— 分数和货币不能是同一个数,否则指标清零即死锁。

---

# 附:核心窗口接下来写什么

**P0.1 · 工位交互接缝** —— W1/W2 的前置:

- `Assets/Scripts/Stations/StationBase.cs`(新建):抽象 `NetworkBehaviour`,一个 `ServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress)`
- `Assets/Scripts/Interaction/PlayerInteraction.cs`(修改,归核心):加一个通用交互 RPC,以及 E 键在「抓取没命中」时回退到工位

**为什么必须由核心写**:`ServerRpc` 不能声明在无主的场景物件上(硬约束 #4),所以从玩家到工位的转发**只能**落在 `PlayerInteraction`。这个文件如果让 W1 和 W2 各改一次,必然冲突;而且两个窗口会各自发明一套转发。
