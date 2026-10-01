# W4 · 保洁阿姨 —— 交付说明与想法

窗口:W4 · 保洁阿姨
名下文件:`Assets/Scripts/Npc/Cleaner.cs`(新建,321 行)
状态:**代码完成,离线编译验证通过(0 error / 0 warning);运行时未验证,等 MPPM 双实例**

---

## 一、我做了什么

一个 `Cleaner` 组件:沿 `Transform[] Patrol Points` 循环匀速走,以她为中心的 3×3 格内收走地上的散落物。

- 普通 `MonoBehaviour`,无 `NetworkObject`。巡逻各端各跑,清扫只看 `InstanceFinder.IsServerStarted`。
- **没有碰撞体,是刻意的**:她不能把自己正要收的东西推开,也不该把玩家顶着走。
- 判定全部用 `NetworkGrabbable` 自己的状态,零射线:
  `State == Idle` + `PlacedCell == null` + 在自己所在格的 3×3 内。
- 清扫每 0.5s 一次,先 `GrabbableSpawner.CollectSpawnedGrabbables` 拿快照再遍历 `Despawn(DespawnType.Destroy)`。

**比规格多做的两条防御**(都在 `IsCollectable`,已注释):

| 判定 | 为什么 |
|---|---|
| `body.isKinematic` → 跳过 | 客户端持有的物体在服务端是 kinematic 的,`linearVelocity` 恒为 0。只看速度会把「别人正拿着/正飞着」读成「静止在地上」 |
| `nob.IsSceneObject` → 跳过 | 场景物件 `Despawn()` 退化成 `SetActive(false)`,无恢复路径(约束 #11)。不该有人这么摆,但失败是静默且永久的 |

失败方向是刻意选的:**宁可漏收(显眼的麻烦),不可错收(玩家丢了东西)**。

---

## 二、需要你在场景里手工做的两件事

场景/prefab 是 YAML,按约束 D 归你:

1. 建 GameObject 挂 `Cleaner`。**脚本不生成任何视觉**——不挂模型/图元她就是隐形的,而「各端都跑巡逻」的全部意义就是看得见。
2. 建几个空物体当巡逻点,按顺序拖进 `Patrol Points`。**路线是循环的**,最后一点连回第一点。

忘了挂巡逻点的话 `Start()` 会报一条警告——不挂的话她站着不动,从游戏里看和脚本坏了一模一样。

选中她时 Gizmo 画出巡逻环线 + 当前会清扫的 3×3 方块。

---

## 三、我认为你在 MPPM 里最可能不满意的地方

按「最可能出问题」排序。

### 1. 「以她为中心的 3×3 格」实际不是 1.5 米,而是 3 米宽的格锁窗口

这是我最想请你实测的一条。格子是 `FloorToInt`,所以她的地块是 `[c-1, c+2)` 这**固定 3 米**——她在地块内走动时窗口**不动**,跨过格线时**整格跳 1 米**。

后果:她到窗口边缘的距离在 **1.0 米到 2.0 米之间来回摆**。也就是说她其实在吸一条 3 米宽的走廊,而不是「身边一圈」。

- 手感上会「一顿一顿」:她走着走着威胁范围突然往外跳一格。
- 也意味着她会收走**明显不在她旁边**的东西(最远 2 米)。

我按规格做成了格子(它同时也是放置系统的格子,对测试和解释都更干净),并且把窗口画进 Gizmo 让你能直接看见。**如果实机觉得别扭,正确的修法是改成半径判定,而不是把格子调大**——调大只会让跳变更粗。

### 2. 收走是瞬间的,没有任何预告

验收标准写的是「她走过去 → 没了」,所以我做成了到点即消失,无动画、无延迟、无音效。但这意味着**玩家只要没在看,东西就凭空少了**,而且他永远不会知道是她拿的。这是整个模块里我判断手感风险最大的一点。

真要修,加一个 0.3~0.5 秒的「她伸手/物品缩小」再 despawn 就够,不需要联网改动(服务端延迟 despawn 即可)。我**没有**擅自加,因为进度不站在我这边,而且它会影响你验收时「走过去就没了」的判定。

### 3. 玩家对她完全没有反制手段

她不停、不累、收不掉、赶不走,唯一的防御是「别放在地上」。如果设计意图是「压力」,那目前玩家能做的只有**预防**,没有任何**应对**。这可能正是你要的,但也可能是漏了一环(比如「被收走的东西进某个回收点可以拿回」)。我只是标出来,没有改。

### 4. 她不是物理存在的

没碰撞体 → 玩家可以站进她身体里,她也穿墙(巡逻点之间走直线,没有寻路)。代价是「卡在阿姨身上」这类 bug 一个都不会有;如果你要她挡路,那就得给她加碰撞体,并接受她会推物体。

---

## 四、我发现的两件可能影响别人的事

### A. `InstanceFinder.IsServer` 在 FishNet 4.7.3 是 `[Obsolete]`

WINDOWS.md 给我的规格里写的是 `InstanceFinder.IsServer`,但它已被标记过时:

```
warning CS0618: 'InstanceFinder.IsServer' 已过时:
"Use IsServerStarted. Note the difference between IsServerInitialized and IsServerStarted."
```

它在源码里就是 `IsServer => IsServerStarted`,**行为完全一样**,换掉纯粹是消警告。我已改成 `InstanceFinder.IsServerStarted`。

**已有的存量**:`NetworkGrabbable.cs:255`(`ServerSetPayload`)也有同一条警告。W5 的规格里同样写着 `InstanceFinder.IsServer`。建议核心窗口统一清一遍——不是 bug,但每次编译都刷警告会淹没真问题。

### B. 「静止在地面上」有 0.35 秒延迟

`Idle` 不等于「刚放下」。放下时 `CmdDropObject` 走的是 `ServerSetFree()`,要等 `UpdateResting` 判定停稳(`_settleSeconds = 0.35s`)才转到 `Idle`。所以任何「这东西在地上吗」的逻辑都要经过这半秒,不能期待放下即静止。我的 0.5s 清扫间隔正好把这个盖住了。

### C. ——这条最关键:我的正确性依赖别人调用 `ServerSetHeld`

`ServerHandToPlayer` 早期的版本只发 `TargetRpc` 让客户端本地 `_isCarrying = true`,**不碰服务端复制状态**。那样的话服务端那份物体仍是 `State = Idle`、`PlacedCell = null`——在我的判定里这就等于「静止在地面上」,**我会把玩家手里的东西收走**。

W2 撞上后核心窗口已把 `ServerSetHeld` 挪进了 `ServerHandToPlayer` 内部,现在是对的。但这说明:任何**将来**新增的「把物体交到玩家手里」的路径,只要漏了 `ServerSetHeld`,表现就是「东西在手里凭空消失」,而且**不会报任何错**。写新工位的人需要知道这一条。

---

## 五、我刻意没做的(免得被当成疏漏)

- **不做寻路**:巡逻点之间走直线,跨墙就走穿墙。要绕障碍得换 NavMesh,那是另一个工作量级。
- **不做动画/视觉**:见第二节,视觉归你。
- **不做「玩家正看着就不收」**:那会让威胁变成可以靠转头规避,和「压力」的设计目标是反的。
- **不收容器内容物**:它们根本不在世界里(统一模型),所以这条是自动成立的,不需要代码。

---

## 六、验收清单

**编译**:已离线验证(方法见第七节),`Assets/Scripts/**` 全量 0 error,`Cleaner.cs` 0 warning。**运行时全部未验证**。

| 验收项 | 预期 | 我的判定路径 |
|---|---|---|
| 地上丢个方块在她路线上 | 走过之后没了 | `Idle` + `PlacedCell == null` + 速度够小 + 在她 3×3 内 |
| 桌上的方块 | 还在 | `PlacedCell.HasValue` → 跳 |
| 塞进打印机里的原料 | 还在 | 容器内容物不在世界里 |
| 别人手上拿着的 | 还在 | `State == Held` → 跳(依赖第四节 C) |
| 半空中飞着的 | 还在 | `State == Free` → 跳;刚 spawn 下落的靠速度阈值挡 |

两个实例都要开,重点看**客户端上她走过的位置和服务端是否一致**(她不是 NetworkObject,两边各跑各的,靠确定性对齐)。

---

## 七、离线编译验证的命令(留给后面的窗口)

不用切 Unity 焦点也能验编译。Unity 自带 Roslyn,`Library/ScriptAssemblies/*.dll` 直接当引用:

```bash
U="/e/unity/6000.6.0f1/Editor/Data"
"$U/NetCoreRuntime/dotnet.exe" exec "$U/DotNetSdk/sdk/8.0.318/Roslyn/bincore/csc.dll" \
  -nologo -nostdlib+ -target:library -langversion:9.0 -out:/tmp/check.dll -nowarn:0649 \
  <UnityEngine/*.dll> <netstandard ref+shims> \
  <Library/ScriptAssemblies/{FishNet.Runtime,Unity.TextMeshPro,Unity.InputSystem,UnityEngine.UI}.dll> \
  $(find Assets/Scripts -name '*.cs')
```

- `-nostdlib+` 必须加,否则和 netstandard 引用打架
- `-nowarn:0649`(`[SerializeField] private` 从未赋值)是本项目基线,每个文件都有,别去"修"
- 全量编 `Assets/Scripts/**` 比单编自己的文件更有用:**能顺带确认没有撞到别的窗口的改动**
- 这**不能**替代你的 MPPM 真机测试,只是把编译错误挡在交付之前

我这次就是靠它抓到 `InstanceFinder.IsServer` 那条过时警告的——我原先只做了纯语法检查(只报缺引用类错误),什么也没发现。

---

## 八、我的一句话判断

机制是干净的,服务端信号判定比射线判定可靠得多,这部分我有把握。**真正的不确定性在手感,而且集中在「3 米格锁窗口」这一条上**——它比「3×3 格」这个词听起来要宽得多,也跳得多。建议第一次测试就专门盯着这个:找个东西放在她侧前方 1.8 米左右,看她是不是隔空就收走了。如果是,那我们要谈的是改半径,不是调参数。

---

# 第二轮 · W4 · 纸箱与容器仪表

窗口:W4 · 容器仪表
名下文件:`Assets/Scripts/UI/ContainerGauge.cs`(新建)、`Assets/Scripts/Stations/PaperBox.cs`(修改)
状态:**离线编译通过(0 error / 3 warning,全是 CS0114 基线);运行时未验证**

> 上一轮(保洁阿姨)的内容在本文件下半部分,仍然有效。

---

## 一、`ContainerGauge` 做了什么

一个通用的容器存量指示器:**TMP 文字 + 色块条**,挂在任何 `ContainerBase` 旁边就能用。
它不认识打印机、不认识纸箱,也不该认识 —— 打印机的纸槽 / 墨槽 / 任务队列、原料箱的库存,
将来都挂这一个组件。

- 读 `Count` / `Capacity` / `IsUnlimited`,订阅 `ContentsChanged` 更新,**没有每帧轮询**
- 世界空间 Canvas,自动转向本地相机(关掉就是固定朝向,给墙上的表面板用)
- 遵守约定 C:`sortingOrder` 钳到 ≥1、**不挂 `GraphicRaycaster`**、每个 graphic 都 `raycastTarget = false`
- 整个层级用代码搭,挂一个组件就是全部安装工作

### 三个边界情况,以及我给的答案

| 情况 | 答案 | 为什么 |
|---|---|---|
| **无限容器** | 画 `3 ∞`,**整条色块隐藏** | 没有分母就没有分数。画任何一条都得先编一个上限,而**一条错的条比没有条更糟**。把两个 Image 都 `SetActive(false)` 而不是留着上一帧的分数 —— 否则屏幕上会冻住一个陈旧的存量 |
| **容量 0** | 就是上面那条 | `ContainerBase.IsUnlimited => _capacity <= 0`,所以「容量 0」和「无限」在本项目里**是同一件事**,不是两个 case。分开处理只会写出两套说法 |
| **容器还没 spawn** | 按空的画(`0/6`) | 本地那份列表**确实**是空的,指示器说的是实话;同步到达时 `ContentsChanged` 会纠正它,通常一两帧内。**故意不做「加载中」状态** —— 一个说「未知」的指示器比一个短暂说「空」的更糟 |

第二条我核对过源码:`SyncList.Read()` 对初始同步进来的每一条都会 `InvokeOnChange(..., false)`
(`SyncList.cs:418`),而且早于 start 回调的那些会被缓存、之后补发(`SyncList.cs:429-443`)。
所以**中途加入的客户端也会收到回调**,不会永远停在 0。

### 两个我踩到的 Unity 坑(都写进注释了)

1. **`Image.fillAmount` 在没有 sprite 时什么都不做。** `Image.OnPopulateMesh` 第一行就是
   `if (activeSprite == null) { base.OnPopulateMesh(...); return; }` —— 它**完全忽略 `type`**。
   所以 `type = Filled` + `fillAmount` 会编译、会运行、**一点效果都没有**。
   我改用**直接写锚点**(`anchorMax.x = fraction`),不需要 sprite,而且是精确的。
2. **设完锚点再调「拉满」的辅助函数会把锚点冲掉。** 第一版里 `Stretch(rect)` 在
   `anchorMin/anchorMax` 之后调用,把整条色带的锚点重置成了 (0,0)-(1,1)。
   现在合并成一个 `Place(rect, anchorMin, anchorMax, offsetMin, offsetMax)`。

### 我做的、你可能不同意的决定

- **世界空间 Canvas,不是屏幕 HUD。** 理由:机器上的指示器要**绕着机器走的时候**读得到。
  屏幕 HUD 会让每个仪表都变成全屏 overlay 的一部分,而 DebugHud 已经在做那件事了。
- **默认转向相机**(`_faceCamera` 默认开)。一台机器从四面都会被看到,固定朝向总有一面是反的。
- **没有字号字段。** 字号从仪表高度按比例算出来(0.32×)。两个都能控制字大小的数字迟早会打架,
  而 Inspector 里那个是更难发现的一个。
- **`Count/Capacity`,不读 `IsFull`。** 规格里写了三个都要读,但 `IsFull` 对有限容器
  **恰好等于** `Count >= Capacity`,对无限容器恒为 false —— 读它是把同一件事说第三遍。
  更实际的理由:**「满」对不同容器含义相反** —— 纸槽满了是好事,输出堆满了是机器停了。
  一个通用组件没法知道该把哪个染红,所以「满」不做视觉。
- **空的时候才变色。** 那是唯一一个「机器因此不工作」的状态,而且从机器外面看不出来
  —— 这就是你要的那个「指示灯」。

---

## 二、`PaperBox.cs` 复核结果

上一轮写完但**从没跑起来过**。逐条查完,**逻辑是对的**,但有两个静默失败的口子。

### 查过、确认没问题的

- **`_payloadIndex = -1` 这条路是对的。** `GrabbableSpawner.SpawnGrabbable` 里有
  `if (payloadIndex >= 0)` 的守卫,所以 -1 时**根本不碰 payload**,prefab 保持原始样子;
  `ContainerEntry.ForEntity(-1)` 也是一个合法条目(`Kind = Entity`),只是没有任何东西能解析它。
- **补给计时的语义是对的。** 满了就 `_refillTimer = 0` 并返回(所以「放着自己涨」不会发生);
  成功入账才清零。「失败就不清零、下一帧再试」那段实际到不了(能过 `IsFull` 检查就一定能加进去),
  但它是防御性的,留着无害。
- **`OnServerInteract` 的顺序是对的。** 先生成、`nob == null` 就直接返回、**再**扣库存
  —— 生成失败不会白扣一张纸。`IsHeldBy` 用对了(约束里指定的那个唯一实现)。
- **`ServerTryRemoveLast()`(栈)而不是 `First()`(队列)。** 箱子里全是同一种纸,顺序无意义,
  两种取法等价。不改。

### 改掉的两处

**① 设了 payload 索引、却没连 catalogue —— 原来**完全没人检查**。**

原来的校验是 `_payloadIndex >= 0 && _catalogue != null && !TryGet(...)`。所以
**`_catalogue` 为空时整个条件短路,一个警告都不报**,而箱子会安安静静地吐出没有外观的方块。
这正是本项目最忌讳的那类失败:Inspector 里看着配好了,运行时是错的,编译期零提示。

拆成两条,让更可能犯的那个错误有自己的消息,并且告诉你怎么改。

**② 无限容器的警告不可操作,而且没说后果。**

原来只说「restock rate is the only limit」。现在说清楚:没有缓冲可补 → **开局是空的**
(而这正是 `FillStock` 存在的意义:开局满的,第一张纸不用等),以及**列表会无上限地涨**,
每个中途加入的客户端都要收全量。

**为什么是 warning 不是 error**(我改主意了,理由在下面):我先写了 LogError,然后去看打印机
—— W1 已经把约定定死了:**缺引用 = LogError,容量不对/无限 = LogWarning**。
打印机的队列无限也只是 warning,理由是「机器还能用,丢的是平衡不是功能」。
纸箱同理,所以跟它保持一致,而不是发明我自己的严重级别。

### 发现但**没有**改的两条 —— 需要你拍板

**A. `longPress` 被完全忽略。** 上一轮规格写的是「**短按** E、双手为空 → 取一份」,
现在的代码对长按短按一视同仁,并且注释里给了理由(这个箱子只有一个动词,长按不是另一个请求,
而长按没反应会让玩家以为机器坏了)。

两种都说得通,我不想在没人要求的情况下改掉上一个窗口**写明了理由**的决定。**但规格和代码确实对不上**,
所以摆出来:要么接受代码(我倾向于接受),要么把规格当准,我可以改。

**B. `_refillSeconds` 只有 `[Min(0.01f)]` 这一道防线。** `[Min]` 只在 Inspector 里钳,
不改变已经序列化进 prefab 的值,也不在运行时生效。填成 0 或 0.01 → **每帧一张纸**,
整条「走路才是价格」的设计当场消失,而且没有任何提示。
我**没有**加运行时钳制,因为那会让 Inspector 显示的值和实际生效的值悄悄不一致 —— 那是另一种坑。
要不要加一道启动检查(比如 < 1 秒就报 warning),你定。

---

## 三、PaperBox prefab 接线清单

```
PaperBox (根)
├─ 模型桌子 + 箱子模型
├─ NetworkObject
├─ ContainerBase          ← 容量填 3~4(见下)
├─ PlacementBlocker
├─ 碰撞体(要覆盖机器正面 —— 工位靠它被玩家的扇区检测找到)
├─ PaperBox
└─ ContainerGauge         ← 本轮新增,这就是「指示灯」
```

| 组件 | 字段 | 填 |
|---|---|---|
| 根 | `NetworkObject` | 加上(**容器要同步,必须有**) |
| 根 `ContainerBase` | `_capacity` | **3~4**。⚠️ **默认值是 0 = 无限**,不填就是这个 |
| 根 `PaperBox` | `_container` | 根上那个 `ContainerBase` |
| | `_payloadIndex` | **0** ← **必须和打印机的 `_paperPayloadIndex` 是同一个数**。留 -1 就是「原始 prefab 样子」 |
| | `_catalogue` | `PayloadCatalogue`。**只要 `_payloadIndex` 不是 -1 就必须填**(现在不填会报 LogError) |
| | `_refillSeconds` | `8`(盲设;这是「一张纸的价格」,用走路付) |
| 根 `ContainerGauge` | `_container` | 根上那个 `ContainerBase` |
| | `_font` | `Assets/Font/simhei SDF.asset`(**必须**,不填中文画不出来) |
| | `_label` | 留空 = 用节点名。想要「纸」就在这填 |
| | `_localOffset` | 默认 `(0, 0.35, 0)`,在箱子顶上。选中看 Gizmo 调 |
| | `_size` / `_pixelsPerMetre` | 默认 `0.7 × 0.22` 米 / `400`。字发虚就调大后者 |

**不要给它挂 `ContainerView`** —— 条目全是同一种纸,顺序没有意义,该用表不该用堆(判据见 `WIRING.md` 第 3 步)。
**不要给它挂 `SnapSurface`** —— 它是工位,占一格,靠 `PlacementBlocker` 挡。

### 打印机的三个槽位 + 队列,也各挂一个 `ContainerGauge`

| 挂在哪 | `_container` | `_label` 建议 |
|---|---|---|
| `纸槽` 节点 | 纸槽的 `ContainerBase` | 纸 |
| `墨槽` 节点 | 墨槽的 `ContainerBase` | 墨 |
| `输出` 节点 | 输出的 `ContainerBase` | 输出 |
| `队列` 节点(W1 本轮新增) | 队列的 `ContainerBase` | 队列 |

`_font` 每个都要填一次 —— 这是这个组件目前最烦的一点,见下。

---

## 四、我认为可能不对的地方

1. **`_font` 要在每一个 gauge 上填一遍。** 一台打印机 4 个 + 纸箱 1 个 × 几台,
   就是十几次拖拽。DebugHud 也是这么做的(单例,无所谓),但仪表是复数的,这个模式开始变贵。
   **没有做自动查找**:`Resources.Load` 要求资产在 `Resources/` 下,而它不是;
   用一个静态字段互相兜底则是魔法 —— 谁先 Awake 谁说了算,顺序一变行为就变。
   如果你觉得烦,我建议的修法是**把字体挪进 `Resources/`**,而不是加静态缓存。
2. **每个 gauge 一个独立 Canvas。** 一台打印机 4 个,加上箱子,场景里几十个世界空间 Canvas。
   这个量级没问题,但如果将来仪表铺满整个办公室,应该改成**每个工位一个 Canvas、多个仪表共用**。
   现在不做,因为「一个组件挂上去就能用」值这个代价。
3. **`∞` 这个字形我没在运行时验证过。** simhei 是 CJK 字体,大概率有;DebugHud 也已经在用它了。
   万一显示成方框,把它换成 `--` 或直接隐藏分母即可。
4. **仪表不区分「满」和「空」以外的状态。** 打印机的输出堆满 = 机器停了,但仪表只会显示
   一个满条,不会变红。要判断「满是不是坏事」得知道容器是干什么用的,而通用组件不该知道。
   如果实测发现玩家看不出「机器不动了」,那应该给打印机加**它自己的**告警,而不是把这个组件变聪明。

---

## 五、验证

- **离线编译**:全项目 `Assembly-CSharp.csproj`,`0 error / 3 warning`(全是 CS0114 基线)。
  过程中 W1 的 `Printer.cs` / `PrinterDisplay.cs` 一度有 6 个 error(他们正在改),现已恢复干净
  —— 顺带证明了这条命令确实是一次全项目冒烟检查。
- ⚠️ **新文件不在 `Assembly-CSharp.csproj` 里。** csproj 是 Unity 聚焦时生成的快照,
  我这个新文件一开始**根本没被编到** —— 全项目构建报 0 error,而单独编我的文件才发现
  一个真的编译错误(`SetText` 的重载歧义:int 参数在 `(string,float,float)` 和
  `(ReadOnlySpan<char>,int,int)` 之间二义)。**只信全项目构建会漏掉你自己的新文件。**
  我往 csproj 里补了一行(它被 `.gitignore` 忽略,Unity 下次聚焦会重新生成)。
- **运行时:完全没验证。** 需要你切焦点。

### 实测时优先看这两条

1. **纸箱按 E → 手上多一张纸,仪表从 4 掉到 3。** 如果仪表不动 → `ContentsChanged` 没接上
   或 `_container` 指错了节点。
2. **把纸槽塞满 → 仪表满格;取走一张 → 掉一格;等 8 秒 → 自己涨回来。**
   这一条同时验了仪表和上一轮的补给计时。

## 六、一句话判断

仪表本身我有把握,它读的是已经冻结、已经在跑的接口。**真正的不确定性在接线**:
`_payloadIndex` 必须和打印机的 `_paperPayloadIndex` 是同一个数,`ContainerBase` 的容量默认是
**0 = 无限**(不是 3),而这两个错了都不会崩 —— 只会表现成「箱子吐出没外观的方块」
和「箱子开局是空的」。上面那张表里带 ⚠️ 的两行是这轮最容易接错的。
