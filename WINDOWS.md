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

# W1 · 打印机

> **⚠️ 这一节已按设计变更重写。** 用户澄清了一条前提,推翻了原先的「按阵营分队列 + 交替调度」。

**名下文件**:`Assets/Scripts/Stations/Printer.cs`(修改)
　　　　　　　`Assets/Scripts/Stations/PrinterQueue.cs`(**删除**)

## 设计前提的变更

**原材料(纸、墨)和谁放的无关,是公用池。** 放置纸张不产生任何分数;分数只来自完成 NPC 的业务。

**有阵营之分的只有「打印出来的文件」** —— 红方的文件1和蓝方的文件1同名但不是同一个文件,在**数据**里区分(模型上也会做区分)。文件来自电脑,而电脑传输过来的数据已经划分好阵营;不同阵营的玩家打开电脑看到的内容不同。文件层这一轮不实现。

### 连带后果:按阵营分队列作废

原来那套设计的前提是「纸和墨有归属 → 灌自己的队列只烧自己的纸、只堵自己的活 → 无利可图」。**材料无归属,前提没了,也就没有可轮转的对象。** 公用池里 6 张纸,谁放的都是那 6 张。

而真正要防的那个漏洞(反复打印同一份废文件堵塞打印机)**本来就在文件层**。所以轮转会在**文件队列**那一步回来,那时它轮转的是**阵营**,不是放纸的人。算法本身可复用,只是今天没有可轮的对象。

### 所以这一轮打印机大幅简化了

没有队列、没有归属、没有 feeder 追踪,就是「纸够 + 有墨 + 输出没满 → 打一张」。

## 结构

| 槽位 | 类型 | 容量 | 说明 |
|---|---|---|---|
| 纸 | `ContainerBase` | **6** | 公用池 |
| 墨 | `ContainerBase` | **1 盒** | 一盒 **8 张**;槽里有盒时 `IsFull`,新盒自然放不进去,即「用完才能更换」 |
| 输出 | `ContainerBase` | **6** | 满了机器**进入等待**,不丢东西 |

打印条件:`_paper.Count > 0` 且 `_printsRemaining > 0` 且输出未满。消耗:1 张纸 + 1 点墨量。

**注意纸墨必须是两个独立容器。** 原来共用一个容器会死锁:纸塞到 6 张满 → 墨进不来 → 永远凑不齐配方 → 而机器从不退还输入,整局报废。

```
项目:E:\UnityProject\Overworked(Unity 6000.6.0f1 + FishNet 4.7.3 + URP,新输入系统,无 asmdef)

开工前必读,按顺序:
1. E:\UnityProject\Overworked\CONSTRAINTS.md —— 硬约束与已冻结接口。违反任何一条都会当场坏掉
2. E:\UnityProject\Overworked\DEVELOPMENT.md —— 项目已有的坑

你名下:
- Assets/Scripts/Stations/Printer.cs        (修改)
- Assets/Scripts/Stations/PrinterQueue.cs   (删除)

规则:
- 只改你名下的文件。需要改别人的文件,停下来告诉我
- 交付前必须让项目能编译。单程序集,你的编译错误会让所有窗口都跑不起来
- 接口不清楚先问,不要自己发明

任务:按设计变更修打印机

【设计前提变了】原材料(纸、墨)和谁放的无关,是公用池。有阵营之分的只有
【打印出来的文件】,而文件来自电脑、这一轮不实现。所以之前那版「按阵营分队列 +
交替调度」作废 —— 材料无归属,没有可轮转的对象。

新的模型:

  纸槽   _paper : ContainerBase,容量 6        —— 公用池,无归属
  墨槽   _ink   : ContainerBase,容量 1(一盒)  —— 公用,一盒 8 张
  输出   _output: ContainerBase,容量 6        —— 满了进入等待

  打印条件:_paper.Count > 0 且 _printsRemaining > 0 且 输出未满
  消耗     :1 张纸 + 1 点墨量

必要改动:

1. 把原来那个 _input 拆成 _paper 和 _ink 两个序列化字段,删掉 _input。
   各自配 ContainerView,挂在不同子物件上。

2. 墨是「一盒 8 张」的计数,不是一张一个条目:
   - 加服务端字段 _printsRemaining
   - _ink 从空变有(有人丢进一盒)→ _printsRemaining = PrintsPerCartridge(默认 8)
   - 每打出一张 → _printsRemaining--
   - 归零 → 从 _ink 移除那一盒(槽位空出,可以装新的)
   - 「用完才能更换」不需要额外代码:_ink 容量 1,槽里有盒就是 IsFull,
     新的墨盒自然放不进去

3. 【删掉 PrinterQueue.cs 整个文件,以及 Printer 里所有跟它相关的调用。】
   材料无归属,没有队列可排、没有人可轮转。
   轮转算法会在【文件队列】那一步回来 —— 那时它轮转的是阵营而不是放纸的人,
   到时用 git 历史把算法捞回来即可,不要现在留着死代码。
   这不是说你之前写的轮转逻辑有问题:它是对的,只是它要解决的问题在文件层
   而不在材料层。

4. 【删掉 _feeders 字典、UpdateIntake 里的 feeder 追踪、ResolveFeeder。】
   它们存在的唯一目的是给材料条目标 OwnerClientId,而材料现在不需要归属。
   UpdateIntake 剩下的职责只有「把落在 intake 盒里的纸/墨吞进对应容器」。

5. ContainerEntry.OwnerClientId 这个字段【保留】(接口里冻结了,文件条目将来要用),
   但材料条目一律传 -1。

6. InTake 按 payload 分派:纸索引 → _paper;墨索引 → _ink。
   各自判自己的 IsFull —— 一个满了不该拦住另一个。

7. 输出满了机器进入等待 —— 这一条现在已经是对的(IsOutputBlocked),不要动。

8. 删掉 TwoJobCapacity 那条警告(前提已不成立)。

9. OnValidate / 启动检查改成:_paper 容量不是 6、_ink 容量不是 1、
   或 PrintsPerCartridge <= 0 时警告。

10. 在类注释里留一句:「文件队列与阵营轮转在这里接入,见 PrinterQueue 的 git 历史」,
    标明将来接哪里。

11. 核心窗口已经把 ServerSetHeld 移进 ServerHandToPlayer 了,你那边多出来的那次调用
    也已经被清掉。改之前先拉一下,免得冲突。

验收:
- 纸塞到 6 张满 → 仍然可以装墨盒,机器照常工作
  (这是这次改动要修的核心问题:原来纸墨共用一个容器,纸塞满就永远凑不齐配方)
- 一盒墨打满 8 张后自动消失,槽位空出,可以装新盒
- 墨槽有盒时丢新盒进去 → 被拒绝,盒留在世界上
- 输出堆到 6 张 → 机器停下等待,不丢东西;取走一张后自动继续
- 只有一个玩家玩的时候也能正常打印(没有队列意味着不需要第二个玩家)
```

---

# W2 · 原料箱

> **一条待办的小修**:`OnServerInteract` 上方那句注释说「Which player a sheet counts for is
> decided when it is fed into a machine」—— 这句话现在不成立了。材料不再有归属,纸在机器里
> 也不记谁的分。注释要改,代码不用动(它本来就传的 -1)。

**名下文件**:`Assets/Scripts/Stations/PaperBox.cs`

**免费但慢速自补**,默认 8 秒补一份。短按 E、双手为空 → 取一份,生成实物**到手里**。

**为什么免费**:材料经济不用钱,用时间。慢速自补 = 真正的货币是「跑一趟的时间」,
而且**结构上不可能死锁**(原料总会有,只是慢)。指标是单调分数,永远不可花 ——
分数和货币不能是同一个数,否则指标清零即死锁。

```
项目:E:\UnityProject\Overworked(Unity 6000.6.0f1 + FishNet 4.7.3 + URP,新输入系统,无 asmdef)

开工前必读,按顺序:
1. E:\UnityProject\Overworked\CONSTRAINTS.md —— 硬约束与已冻结接口。违反任何一条都会当场坏掉
2. E:\UnityProject\Overworked\DEVELOPMENT.md —— 项目已有的坑

你名下(只能改这些):
- Assets/Scripts/Stations/PaperBox.cs   (新建)

规则:
- 只改你名下的文件。需要改别人的文件,停下来告诉我
- 交付前必须让项目能编译。单程序集,你的编译错误会让所有窗口都跑不起来
- 接口不清楚先问,不要自己发明

任务:原料箱

它是什么:一个 StationBase 子类,【自带模型桌子】,整件摆在场景里。

结构:
- 根:ContainerBase(容量小,比如 3)+ PlacementBlocker + 碰撞体

行为:
- 【免费但慢速自补】:每 _refillSeconds(默认 8 秒)往容器里加一份,加到上限为止
- 短按 E、双手为空 → 从容器取一份,生成实物【到手里】:
    !NetworkGrabbable.IsHeldBy(manager, conn.ClientId) 判定双手为空
    → GrabbableSpawner.SpawnGrabbable(payload, player.HandPosition, rot, conn)
    → player.ServerHandToPlayer(nob)
  两步缺一不可,见 CONSTRAINTS.md 第二节末尾
- 容器空了 → 什么都不做(不报错、不生成)
- 手上已经有东西 → 什么都不做

自补用【服务端计时】,放在 TimeManager_OnUpdate 里累加 Time.unscaledDeltaTime。
不要用协程,也不要用 InvokeRepeating —— 见 CONSTRAINTS.md 第 6 条。

为什么免费:材料经济不用钱,用时间。慢速自补 = 真正的货币是「跑一趟的时间」,
而且结构上不可能死锁。指标是单调分数,永远不可花。

验收:
- 按 E → 手里多一个物体,容器少一份
- 手上有东西时按 E → 什么都不发生
- 容器空了按 E → 什么都不发生
- 自补按设定的速率进行
- 两个玩家同时取 → 都拿到,不重复生成
```

---

# 附:P0.1 已完成

`Stations/StationBase.cs` 与 `PlayerInteraction` 的交互通道都已落地,签名见 `CONSTRAINTS.md` 第二节末尾。
W1/W2 可以直接开工。

**接下来由核心窗口负责的:**

- 数据层与 NPC(客户 / 同事 / BOSS)、文件夹、章笔、电脑面板 —— 下一轮,依赖还没定
- Steam 传输真机联机
