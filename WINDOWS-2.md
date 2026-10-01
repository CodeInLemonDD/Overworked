# W2 · 原料箱 —— 交付说明与观点

窗口:`W2 · 原料箱`。名下文件:`Assets/Scripts/Stations/PaperBox.cs`(新建)。

**状态:代码已交付、编译已验证(离线 Roslyn + Unity 真机编译各一遍)。
运行时未验证 —— MPPM 双实例没跑过,那是用户的活。**

---

## 一、交付了什么

**`PaperBox : StationBase`,`[DisallowMultipleComponent]`。** 本轮只碰了这一个文件。

- **自补**:服务端计时。`StationBase` 不是 `TickNetworkBehaviour`,拿不到那个模板,
  所以在 `OnStartNetwork` 里**直接订阅 `TimeManager.OnUpdate`**(三行,和模板做的事一样),
  累加 `Time.unscaledDeltaTime`,每 `_refillSeconds` 往容器加一份,到上限为止。
  无协程、无 `InvokeRepeating`。订在 `OnStopNetwork` 里退,`TimeManager` 字段缓存着退订
  (照 `TickNetworkBehaviour` 的做法 —— 它也是缓存而不是在 stop 时重读)。
- **取**:`OnServerInteract` → `IsHeldBy` 判空手 → 容器有货 → `SpawnGrabbable` → `ServerHandToPlayer`。
- **货只在 spawn 成功之后才扣**,spawn 失败不白扣一份。

另外顺手做掉了新版 `WINDOWS.md` 里点名的那条待办:`OnServerInteract` 上方那句
「Which player a sheet counts for is decided when it is fed into a machine」已经不成立 ——
材料不再有归属。已改成「纸是公用池,放进去不记分;归属是完成品文件的性质,不是材料的」。
**纯注释,代码没动**(它本来就传 `-1`)。

**编译验证**:
- Unity 自带 Roslyn 离线编了一遍(`csc.dll`,19 个源文件,排除 W1 正在写的 `Printer*.cs`),
  `exit=0`,只有 `PaperBox` 的两条 `CS0649` —— 本项目 `[SerializeField] private` 的基线警告。
- `Library/ScriptAssemblies/Assembly-CSharp.dll`(11:34 那次切焦点)里查得到 `PaperBox` 类型,
  说明**真机的 Unity 编译也过了**。

---

## 二、我替你做的五个判断

都不是规格写死的,是我按「最不容易被误读成 bug」选的。

| # | 判断 | 为什么 |
|---|---|---|
| 1 | **长按也取** | 这箱子只有一个动词。按住不放却什么都不发生,会被读成「机器坏了」而不是「我按错了」 |
| 2 | **开局满箱** | 否则头 8 秒走过去按 E 没反应 —— 而那正好是玩家第一次试它的时候 |
| 3 | **满箱时计时器归零** | 见下,这条最要紧 |
| 4 | **取栈顶**(`ServerTryRemoveLast`) | `ContainerView` 从下标 0 往上摆。取栈顶,堆叠只在顶上增减;取队首会让整摞凭空往下掉一格 |
| 5 | **`_payloadIndex` 默认 `-1`** | `-1` 在冻结契约里的含义是「prefab 原始样子」。**项目里现在根本没有 `PayloadCatalogue` 资产**,默认 `-1` 让箱子今天就能用(见第三节) |

### 第 3 条展开:这一行才是「慢」的实现

满箱时计时器**归零**,而不是**继续累加**。差别很实际:

满箱还继续累加的话,箱子摆着不动就永远处于「已就绪」状态 —— 拿走一张,**下一帧立刻补上**。
等于没有自补时间,箱子跟得上站在它旁边的玩家,「跑一趟」这个成本当场蒸发。

归零之后,拿走一张起的 8 秒才真正开始走。

**所以这个箱子的手感要调,调的是这条语义,不是那个 8。** 8 只是价格的刻度。
容量也是价格的一部分(见第七节,那个数不在我文件里)。

---

## 三、最该被写进契约的一件事:纸的 payload 索引

**现在有三个互不相干的 Inspector 数字必须一致,而没有任何地方定义它们:**

| 谁 | 字段 |
|---|---|
| W2 原料箱 | `PaperBox._payloadIndex` |
| W1 打印机 | 「纸索引 → `_paper`;墨索引 → `_ink`」的那两个 |
| 资产层 | `Object.prefab` 上的 `NetworkGrabbable._catalogue`、各 `ContainerView._catalogue` |

**没有任何地方写着「0 是纸」。** 这正是 P0.1 想防的那类分叉 —— 只不过它落在了**资产层**
而不是代码层,所以排期表没覆盖到。

填错的表现是「箱子吐出一个墨盒」或者「打印机不认纸」,而且**要跑起来才发现**,编译期零提示。

建议核心窗口在 `CONSTRAINTS.md` 的 `PayloadCatalogue` 一节里定死索引含义,
并把「先建 `PayloadCatalogue` 资产」列成 W1/W2 的开工前置 —— **它现在不存在**(我 grep 过 GUID,
`Assets/` 下没有任何资产引用 `PayloadCatalogue.cs`)。

---

## 四、几条设计意见

- **箱子和打印机之间故意没有耦合,请不要加。** 箱子产出实物、打印机吞实物,中间那一段路
  (拿起来 → 走过去 → 放进 intake)本身就是玩法。任何「直接从箱子送进打印机」的便利都会把这段路删掉,
  而这段路是「货币是跑一趟的时间」这句话唯一被兑现的地方。
- **别给它加「一次拿一把」。** 一次一张,是让容量和 8 秒成为约束的前提。
- **「什么都不发生」是设计,不是缺失。** 空箱按 E、满手按 E,我都做成**静默**。
  这两种都是正常状态,不是错误;用报错或音效说「你不能拿」,等于把正常状态说成失败。
- **箱子也是工位,占一格,靠 `PlacementBlocker` 挡**(W3 已落地,我确认过 `SnapSurface.IsBlocked`
  在 `TryFind`/`TrySample` 两条路径上都走查)。别在它身上再挂 `SnapSurface`。

---

## 五、我看到的、在我文件之外的问题

### 1. 并行窗口 + 无 asmdef:任何人的编译错误会淹掉所有人

`WINDOWS.md` 通用规则写「编译验证由用户统一切焦点触发,**不要指望自己验证**」—— 这句不准确,
而且代价不小。W5 那份文档里已经给了一条路(`Assembly-CSharp.csproj` + `dotnet build`);
我走的是更土的那条:**直接调 Unity 自带的 `csc.dll` 编源码**,不依赖 csproj —— 因为 csproj 可能是旧的
(它只在 Unity 聚焦时才重生成)。

**但真正值得写进通用规则的是这个习惯,不是工具**:

> 我干活期间 W1 正在同一个目录里改 `Printer.cs`。单程序集下,**他一个编译错误会让所有窗口都跑不起来**;
> 而如果大家都只在切焦点时才发现,就分不清是谁弄坏的,也不知道自己那份是不是干净的。

**建议:每轮开工先跑一遍全量离线编译,确认基线是干净的,再动自己的文件。** 这样切焦点时看到的任何
新错误都必然是自己刚写的那几行。这条比"不要指望自己验证"有用得多。

### 2. P0.1 落地后从没在 Unity 里跑过 —— W1/W2 是它的第一批用户

我开工时 `Assets/Scripts/Stations/` 下只有 `StationBase.cs`:没有 `Stations.meta`,
没有 `StationBase.cs.meta`,而 `Assembly-CSharp.dll`(10:47)比 `StationBase.cs`(10:54)还旧。

不是问题,是个提醒:**那个「接缝」当时一次都没被 Unity 加载过**,它的第一批用户就是 W1 和 W2。
(现在 meta 都齐了,11:34 那次切焦点生成的,再看到它们出现不用当新问题。)

### 3. `ServerSetHeld` 那一课:接口该由一处负责

我严格按 `CONSTRAINTS.md` 的「两步」写完,发现服务端那份物体仍是 `State = Idle`、`HolderId = -1` ——
`ServerHandToPlayer` 只发一条 `TargetRpc` 让客户端**本地**开始持有,完全不碰复制状态。
后果是双手为空判定失效、被抱着的物体碰撞体仍启用、放下和投掷全被服务端拒。
我在自己文件里补了 `ServerSetHeld`。

**核心窗口的后续处理是对的**:把那一行挪进了 `ServerHandToPlayer` 内部,两个窗口的局部补丁都删了,
`CONSTRAINTS.md` 也改成了「两步,第 2 步里已包含标记为持有,调用者不要再自己调 `ServerSetHeld`」。

**教训在顺序上**:我当时应该先停下来问,而不是在调用点补一行。补一行能让 W2 单独跑通,
但 W1 会各自补一行 —— 而补两次就是分叉。这正是 P0.1 存在的理由,我在它身上又犯了一次。

---

## 六、我没验证的(运行时结论全部是推断)

| 项 | 状态 |
|---|---|
| 开局满箱依赖「`OnStartServer` 里写 SyncList 会随 spawn 消息一起到达」 | 框架注释这么写,我没跑过 |
| host 上 `TimeManager.OnUpdate` 只触发一次(不会双倍自补) | 源码 `_onStartNetworkCalled` 有闸,我读过,没跑过 |
| 8 秒的实际手感 | 数值盲设 |
| 两个玩家同帧各取一份 | 服务端单线程顺序处理,逻辑上必然成立,没实测 |
| 箱子不吃掉旁边物体的射线 / 不挡桌子的吸附 | 靠 `PlacementBlocker`,没实测 |

**我这轮没有引入任何过时 API**:`InstanceFinder.IsServer` 会报 CS0618(见 W5 的发现),
我用的是 `IsServerInitialized`(per-object 的那个,没过时),`NetworkManager` 取自 `InstanceFinder`。
`PaperBox` 的编译输出里除了两条 CS0649 什么都没有。

---

## 七、需要用户手工做的

1. **建 `PayloadCatalogue` 资产** —— 现在不存在。`Object.prefab`、`ContainerView`、W1 的打印机都要它。
2. **接线配方**:根上 `NetworkObject` + `ContainerBase` + `PlacementBlocker` + 碰撞体 + `PaperBox`
   + 自带模型桌子,整件摆场景里;想看见纸堆再加 `ContainerView`(它要 `_container` 和 `_catalogue`)。
3. **容量在 `ContainerBase` 自己的 `_capacity` 上填**(规格说「比如 3」)—— **那个字段不在我文件里**,
   我这边任何地方都没有硬编码容量,也只是按 `Capacity` 判满。
4. **定纸的 payload 索引**,填进 `_payloadIndex`(现在是 `-1`,即「原始 prefab 样子」)。

---

## 八、如果还要我继续做 W2

这一轮**故意没做**的,按我认为值不值得做排:

1. **箱子的视觉反馈**:现在拿取没有任何表现。`ContainerView` 能显示纸堆,但接线是第七节的事。
   要不要做取决于你希望箱子多显眼。**我认为这一条最值得先做** —— 「慢速自补」现在完全不可见,
   玩家没法知道还剩几张、下一张还要等多久。
2. **「箱子还有多久补上」的提示**:自补现在是纯后台的。做成箱体上的一个小指示(
   比如 `ContainerView` 空位处一个半透明幽灵)能让"等"变成可预期的,而不是以为坏了。
3. **不许往箱子里放东西**:现在 `PaperBox` 完全不处理「放进来」。有人把纸丢在箱子上,那只是地上的一件东西。
   做成「能退还」是设计问题,不是遗漏 —— **我倾向于不做**:箱子免费,没有退还的动机。
4. **多个箱子**:每个箱子的计时器互相独立,这应该是对的,但没写过第二个箱子。

---

## 九、一句话总结

代码这块是干净的,行为边界(空箱 / 满手 / spawn 失败)都显式处理了,没有静默扣货的路径。
**真正的风险不在实现,在没测**;而在设计上,最该被补上的是一个契约而不是一行代码 ——
**「哪个 payload 索引是纸」至今没有任何地方定义,而 W1 和 W2 必须填同一个数。**

---

# 第二轮 · 电脑工位

**状态:完成,离线编译 0 error / 3 warning(3 条全是 CS0114 基线)。**
第三节那个接口缺口已按你选定的方案补上。

**文件**:`Assets/Scripts/Stations/Computer.cs`(新)、`Assets/Scripts/UI/ComputerPanel.cs`(新)、
`Assets/Scripts/Interaction/PlayerInteraction.cs`(加了两对 RPC + 一个常量)。

---

## 一、做了什么

**`Computer : StationBase`。** `OnServerInteract` 不创建任何东西,只把「打开你那份面板」发给按 E 的客户端。
面板开着的时候玩家是**模态**的:移动、体力、交互三份输入一起关掉,关面板时还回去。

**`ComputerPanel`。** 整个界面用代码搭(和 `DebugHud` 同一套路),两列:左文档、右打印机。
选中一份文档 → 点「打印到 #1」→ 面板关闭、请求发出。

**两对 RPC 加在 `PlayerInteraction` 上**:

```
Computer.OnServerInteract  →  player.ServerOpenComputerPanel(computer)   [Server]
                           →  RpcOpenComputerPanel(conn, computerObject) [TargetRpc]
ComputerPanel 点打印机      →  interaction.RequestDocument(computer, printer, specIndex)
                           →  CmdRequestDocument(...)                    [ServerRpc]
```

服务端那一步:`TryGet(specIndex)` → 范围校验 → **判队列满** → `DocumentStore.ServerCreate` → `queue.ServerTryAdd(ForData(id))`。

---

## 二、我自己决定的事(都不在提示词里)

1. **面板挂在电脑 prefab 上**,不单独往场景里放一个 UI 物体。`Computer.Panel` 懒查找,`RpcOpenComputerPanel`
   拿到电脑对象后直接在它身上找面板。好处是**没有新的接线字段** —— 一个 prefab 自包含,
   和 `PrinterDisplay` 挂在打印机上是一回事。

2. **面板自己找打印机,服务端不传列表。** 客户端本地 `FindObjectsByType<Printer>` 列表给它看,
   点中的那台**以 `NetworkObject` 本体回传**,不是回传「第 3 台」。
   这样即使两端的列表短暂不一致,最坏结果是「服务端没有这台机器」被拒 —— **永远不会选错机器**。
   传下标就会(两端的 `FindObjectsByType` 顺序不保证一致)。

3. **范围校验是回到电脑,不是回到打印机。** 这是设计核心:计算机存在的意义就是让你不必站在打印机旁,
   而「跑一趟」正是游戏对一份文档收的费。拿打印机做范围校验会把这座工位唯一的功能禁掉。

4. **发送前先判队列满,满了直接 return,不创建文档。** 否则会先 `ServerCreate` 出一个
   存在库里、拿着一个编号、谁都够不到的文档 —— 而**编号没有上限、不可回收**,烧掉一个是永久的。

5. **发送之后面板直接关闭。** 一次按键一份文档;成功与否玩家从机器上看。
   服务端对「队列满」是静默的(规格这么要求),所以窗口上**画任何确认都可能是谎话**。
   同理**不显示队列长度** —— 那是机器上的仪表(ContainerGauge)的事,在这儿再显示一份就是第二个真相。

6. **打开面板会锁移动**(和 DevConsole 同一套做法,逐个告诉三个输入组件)。
   `PlayerInteraction` 的输入也一起关,**顺带把 E 也关掉了** —— 否则玩家能在窗口背后再按一次 E 开出第二个面板。
   关闭方式:关闭按钮 或 Esc。`OnDisable` 兜底还输入,不会把人冻住。

7. **默认选中第一份文档。** 否则第一次点打印机会什么都不发生,读起来像窗口坏了。

8. **面板挂 `GraphicRaycaster`**(按你的决定)。面板锚在**右侧垂直居中**,并且**不做全屏遮罩** ——
   所以左上角 FishNet 那两个按钮**即使面板开着也照点**。约定 C 的理由完整保住,只有字面被违反,
   类注释里写清楚了这件事,免得下一个人照搬这个例外。

9. **队伍硬编码成 `TeamThisRound = 0`**,是个命名常量而不是裸 0,方便将来 grep 掉。

---

## 三、跨窗口的接口缺口:`Printer` 原本没有公开的队列访问口(已解决)

W2 的规格第 4 节写着「服务端:把那台打印机的**任务容器** `ServerTryAdd(ContainerEntry.ForData(id))`」,
但**没说 W2 怎么够到它**。我去看了 W1 已经落地的 `Printer.cs`:

- `_queue` 字段**有**(第 144 行,private)
- `ValidateConfiguration` / `TryBeginCraft` 都在用它
- 但**公开面里没有它** —— `public` 只有 `IsPowered` / `PrintingSlot` / `PrintingDocument` /
  `PrintsRemaining` / `Output` / `ServerSetPowered`。**`Output` 有,`Queue` 没有。**

而 W1 自己的交付说明写着:「**W1 现在没法和 W2/W3 分开测。** 队列里没有别的途径能进文档 ——
要么等 W2 的电脑面板」。也就是说这条通道本来就必须由我建,而**口子没开**。

`Printer.cs` 是 W1 名下的文件,按规矩我没有自己动手,先停下来问了。**你选的是加属性**,已在
`Printer.cs` 里补上(紧跟 `Output` 之后):

```csharp
/// <summary>
/// The job queue: documents waiting to be printed, oldest at the front.
/// </summary>
public ContainerBase Queue => _queue;
```

---

## 四、我认为还可能不对的地方

1. **列表不滚动。** 一列大约装得下 9 行,再多会画到提示行上。文档种类和打印机数量一多就会撞上。
   现在没做,是因为这两个数字都还很小 —— 但它是个迟早会来的问题。

2. **站在电脑前看不到哪台打印机忙。** 我特意没在面板上显示队列长度(理由见第二节第 5 条),
   但代价是玩家可能把文档送进一台已经排满的机器,然后**什么反馈都没有**。
   W4 的 `ContainerGauge` 挂在机器上,人得走过去才知道。要不要在面板上补一列「队列 2/5」是个设计选择 ——
   我倾向**不要**(那就变成第二个真相),但如果你是照「玩家不该白跑一趟」来权衡的,就该要。

3. **`FetchSeconds` 完全没读。** 按规格「这轮先当 0 处理」。所以 Internet 和后台文件在这一轮
   **只差一行标签**,行为一模一样。将来接计时的时候,位置在 `CmdRequestDocument` 里
   `ServerCreate` 之前 —— 那是个服务端延迟,不是客户端动画。

4. **打开面板会吞掉一次正在进行的投掷蓄力。** 锁输入是逐个组件 `SetInputEnabled(false)`,
   如果玩家按 E 的时候正好在持物蓄力,那次投掷会被吃掉。E 在持物时走的是放下/投掷、不会设工位目标,
   所以理论上到不了这条路径 —— 但那是**两个模块的联合行为**,我没有实测。

5. **`TeamThisRound = 0` 意味着所有文档都是 0 队颜色。** 如果 `PayloadLabel._teamColours`
   没配,什么都不发生;配了,就**全是 A 队色**。分队伍那一轮这个常量要变成「按玩家查」。

6. **面板是纯客户端对象,没有任何服务端校验「这个客户端有没有资格开这个面板」。**
   现在的不可达性来自「只有按了 E 才会有 TargetRpc 发出去」,不是来自任何检查。
   和 DevConsole 的 `_commandsEnabled` 是同一类问题:安全性来自它在哪,不来自它允许什么。

7. **`Computer` 的 `OnStartServer` 会在没有 `DocumentCatalogue` 或没有 `ComputerPanel` 时报错。**
   这是故意的(否则「机器坏了」和「还没轮到用它」长得一模一样),但会有人在 Console 里看到红字。

---

## 五、接线清单

### `Computer.prefab`(新建,自带模型桌子)

```
Computer (根)
├─ 模型桌子 + 电脑模型
├─ NetworkObject
├─ PlacementBlocker
├─ 碰撞体(要覆盖机器正面 —— 工位靠它被玩家的扇区检测找到)
├─ Computer
└─ ComputerPanel        ← 挂在根上,或者任何「永远不会被关掉」的子物件上
```

| 组件 | 字段 | 填 |
|---|---|---|
| 根 `Computer` | `_catalogue` | `DocumentCatalogue` 资产 |
| `ComputerPanel` | `_font` | `Assets/Font/simhei SDF.asset`(**必须填**,内置 TMP 字体没有中文字形) |
| | `_sortingOrder` | `10`(默认,保持 ≥ 1 即可) |

- **`ComputerPanel` 不能挂在会被关掉的子物件上** —— 面板的 canvas 建在它自己下面,
  父物件不激活的话画出来没人看得见。代码里会报错说这件事。
- **不要给它挂 `SnapSurface`。** 机器占住那格,靠 `PlacementBlocker` 挡。
- `PlacementBlocker` 的挂法和打印机一样:**必须挂在它占据的那个碰撞体所在节点,
  或该节点与 `SnapSurface` 之间的任一祖先上**,否则静默失效。

### 场景

- `DocumentStore` 场景物体(P0 的活,一个挂着 `DocumentStore` 的 NetworkObject,**不加 NetworkTransform**)。
- `DocumentCatalogue` 资产(没有的话先 Create → Overworked → Document Catalogue)。
- 电脑摆进场后,选中 NetworkManager 跑一次
  **`Fish-Networking → Utility → Reserialize NetworkObjects → Reserialize Scenes`**。

---

## 六、建议的验收步骤(和「错了会看到什么」)

| # | 做 | 错了会看到 |
|---|---|---|
| 1 | 走到电脑前按 E | 什么都没出 → `ComputerPanel` 不在激活的物体上 / `_catalogue` 没接 |
| 2 | 看面板 | 一片方块 → `_font` 没填 |
| 3 | **面板开着时点左上角 Host 按钮** | 点不动 → 面板跑到左上角去了,或者有人给它加了全屏遮罩 |
| 4 | 选一份文档,点「打印到 #1」 | 面板不关 → 点击没进到按钮 |
| 5 | 走到 1 号打印机,看队列 | 空的 → 就是第三节那个 `Queue` 接口 |
| 6 | 队列塞满 5 份后再点一次 | 报错 → 违反了「静默」 |
| 7 | 面板开着按 Esc | 关不掉 / 关了但人动不了 → `SetGameplayInputEnabled` 没还回去 |
| 8 | 面板开着按 E | 又开出一个 → 交互输入没跟着关 |

第 5 条是这轮唯一真正的端到端验证,也是第三节那个接口缺口存在的全部理由 ——
口子补上之前它必然失败,补上之后它才是这条链路的证明。

---

## 七、一句话总结

面板、RPC、范围校验、静默语义都写完了,离线编译 0 error。
唯一的拦路石是**一个没人写进冻结接口的访问口** —— `Printer` 有 `Output` 却没有 `Queue`,
而 W2 的规格默认它存在。补上那一行之后这一轮就能测了。

**留给下一个人的教训**:工位与工位之间的调用(电脑 → 打印机)和工位与玩家之间的调用一样,
需要一个**冻结的访问口**。P0.1 只解决了后者(`StationBase` + `PlayerInteraction` 的交互通道),
前者没有对应的东西 —— 每出现一个「A 机器要操作 B 机器」,都要重新发现一遍。
