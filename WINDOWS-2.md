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
