# 接线总表

代码全部就位(离线编译 0 error),但**接线没做**。这份表是把六个窗口各自的清单合成一张,按**操作顺序**排 ——
先建被所有人引用的资产,再逐个对象接线,最后摆进场。

> **为什么按这个顺序**:`PayloadCatalogue` 被 5 个地方引用。先建它,后面每接一处都能立刻填上;
> 放在最后就要回头补一遍。

---

## 现状

W6 按 GUID 扫过全场景:

| | |
|---|---|
| 已接线 | **7 个** —— `PlayerCameraFollow`、`GrabbableSpawner`(场景)、`PlayerInteraction`、`PlayerMovementPrediction`、`HoldPoint`(Player.prefab)、`NetworkGrabbable`(Object.prefab)、`SnapSurface`(Table.prefab) |
| 未接线 | **其余全部** —— 而那 7 个**全在地基之前** |

也就是说:**八个模块的代码都在仓库里,但没有一个能在编辑器里跑起来。**
在接线做完之前,再写新代码都只是堆量。

---

## 第 0 步 · 建 PayloadCatalogue 资产

`Assets/` 下右键 → **Create → Overworked → Payload Catalogue**,命名比如 `PayloadCatalogue`。

**索引契约(填任何地方都必须用同一套)**:

| 索引 | 是什么 |
|---|---|
| **0** | 纸 |
| **1** | 墨盒 |
| **2** | 打印件 |

> **只能追加,不能重排。** 重排会改变世界里所有已存在物体和所有已配置字段的含义。
> 现在这个资产**不存在** —— 不建的话打印机会打出一堆没外观的方块,而且**编译期零提示**。

---

## 第 1 步 · `Object.prefab`(现有的可抓物体)

| 字段 | 填 |
|---|---|
| `NetworkGrabbable._catalogue` | 上面的 `PayloadCatalogue` |

**`_payloadRoot` 留空** —— 留空时回退到根节点,而根节点现在只有一个 `Cube` 子节点,正好是「payload 索引 -1 = 用 prefab 原始样子」那条默认路径需要的形状。**不用改 prefab 结构。**

---

## 第 2 步 · `Player.prefab`

| 位置 | 做什么 |
|---|---|
| 根节点 | 加 `PlayerStamina` |
| `PlayerStamina._inputActions` | → `Assets/InputSystem_Actions.inputactions` |

**`PlayerMovementPrediction` 上不用连线** —— 它用 `GetComponent` 自己找同物体上的 `PlayerStamina`。
不加组件不会崩,但 Console 会警告「never slow down」(故意的:否则「体力不生效」和「组件忘了加」长得一模一样)。

---

## 第 3 步 · `Printer.prefab`(新建,**自带模型桌子**)

### 结构

```
Printer (根)
├─ 模型桌子 + 机器模型
├─ NetworkObject
├─ PlacementBlocker
├─ 碰撞体(要覆盖机器正面 —— 工位靠它被玩家的扇区检测找到)
├─ Printer
│
├─ 纸槽 (子物件)          ← 已有节点
│   └─ ContainerBase      ← 纸,容量 6      【不挂 ContainerView】
│
├─ 墨槽 (子物件)          ← 已有节点
│   └─ ContainerBase      ← 墨,容量 1      【不挂 ContainerView】
│
├─ 队列 (子物件,新建)     ← 任务队列,文档数据层那一轮加的
│   └─ ContainerBase      ← 待打印的文档,容量 5  【不挂 ContainerView】
│
└─ 输出 (子物件)          ← 已有节点
    ├─ ContainerBase      ← 输出,容量 6    【不挂 ContainerView】
    └─ 打印好的纸1..6      ← 视觉由动画 + PrinterDisplay 负责
```

**四个容器各自挂在自己的节点上,不要挂在根上。**

**四个都【不要】挂 `ContainerView`:**

- `输出` —— 你已经有 `打印好的纸1..6` 六个槽位了。动画负责开关、`PrinterDisplay` 负责填内容。
  再挂一个 `ContainerView` 会在同一位置又实例化一批 prefab,两套视觉叠在一起。
- `纸槽` / `墨槽` / `队列` —— 只关心「还剩多少」,顺序没有意义,该用指示灯 / 滚动条,不该用纸堆。
  纸塞进去就是数据了,把它重新画成物理纸堆和容器模型本身是矛盾的。

**判据:容器的可视化用「堆」还是「表」,看它的顺序有没有意义。** 输出的顺序有意义
(取用是 LIFO,玩家要看见最上面那张是什么),所以用堆;纸和墨没有,所以用表。

`ContainerView` 留给将来那些**顺序有意义、但没有专属动画**的容器(货架、文件夹)。

### 引用

| 组件 | 字段 | 填 |
|---|---|---|
| 根 | `NetworkObject` | 加上(**容器要同步,必须有**) |
| 根 `Printer` | `_paper` | **`纸槽`** 的 `ContainerBase` |
| | `_ink` | `墨槽` 的 `ContainerBase` |
| | `_output` | `输出` 的 `ContainerBase` |
| | `_queue` | `队列` 的 `ContainerBase` |
| | `_paperPayloadIndex` | **0** |
| | `_inkPayloadIndex` | **1** |
| | `_secondsPerOutput` | `2`(**必须**和 `Printing` 片段的 2.000 秒一致,否则动画放完机器会干等) |
| | `_printsPerCartridge` | `8` |
| | `_intakeCentre` / `_intakeHalfExtents` | 选中机器看 Gizmo 调,**绿框要盖住桌面**;上沿必须高过物体落在机器顶面后的位置 |
| 四个容器 | (都不挂 `ContainerView`,理由见上方结构说明) | — |

容器容量填在 **`ContainerBase._capacity`** 上,不是 `Printer` 上。

### ⚠️ `PlacementBlocker` 挂哪

> 必须挂在**它占据的那个碰撞体所在节点**,或该节点与 `SnapSurface` 之间的任一祖先上。

挂在没有碰撞体的纯视觉子节点上 → 无效。
挂在 `SnapSurface` **之上** → **无效,原 bug 原样复现**(物体被吸到机器顶上)。

本机**不需要** `SnapSurface` —— 机器占住那格,那格本来就不该能放东西。

启动时机器会自己检查:四个容器**都接上了没**、有没有**接重**、纸和墨的容量是不是 6 / 1、
输出是不是 6、队列是不是**无限容量**(是就警告),以及 `_printsPerCartridge` 是不是正数。

> **打印机依赖场景里的 `DocumentStore`。** 没有它,堆上会保留美术摆的占位纸、打印头什么都不显示 ——
> 那是设计好的降级(不是崩溃),但机器**一份也打不出来**,因为没有文档能进队列。

---

## 第 4 步 · `PaperBox.prefab`(新建,**自带模型桌子**)

```
PaperBox (根)
├─ 模型桌子 + 箱子模型
├─ ContainerBase          ← 容量 3~4
├─ PlacementBlocker
├─ 碰撞体
├─ SupplyBox
└─ (不挂 ContainerView —— 只关心剩几张,该用指示灯 / 滚动条)
```

| 组件 | 字段 | 填 |
|---|---|---|
| 根 | `NetworkObject` | 加上 |
| 根 `SupplyBox` | `_container` | 根的 `ContainerBase` |
| | `_payloadIndex` | **0** ← **必须和打印机的 `_paperPayloadIndex` 是同一个数** |
| | `_catalogue` | `PayloadCatalogue`(只用来在启动时校验索引) |
| | `_refillSeconds` | `8`(盲设;这是「一张纸的价格」,用走路付) |
| — | 不挂 `ContainerView` | 条目全是同一种纸,顺序没有意义 —— 用指示灯表示剩余量 |

**不要给它挂 `SnapSurface`** —— 它是工位,占一格,靠 `PlacementBlocker` 挡。

---

## 第 5 步 · 场景 · `Cleaner`

| 做什么 | 说明 |
|---|---|
| 一个 GameObject + `Cleaner` | |
| 建几个空物体当巡逻点 | 按顺序拖进 `_patrolPoints`。**路线循环** —— 最后一点连回第一点 |
| **给她一个模型** | ⚠️ 脚本**不生成任何视觉**。不挂模型她就是**隐形的**,而「各端都跑巡逻」的全部意义就是看得见 |

忘了挂巡逻点 `Start()` 会警告 —— 不挂的话她站着不动,从游戏里看和脚本坏了一模一样。
选中她会画出巡逻环线 + 当前会清扫的 3×3 方块。

---

## 第 6 步 · 场景 · `DebugHud`

| 字段 | 填 |
|---|---|
| `_font` | `Assets/Font/simhei SDF.asset`(不指会报 LogError,中文画不出来) |
| `_catalogue` | `PayloadCatalogue`(可选;不指就显示 `payload 0` 而不是「纸」) |

**验收**:中文字正常显示,**且左上角 Host / Client 按钮仍然点得动** —— 后者是三条 UI 约束存在的全部理由。

---

## 第 7 步 · 场景 · `DocumentStore`

**没有它,打印机一份也打不出来。** 队列里进不去文档,堆上会保留美术摆的占位纸 —— 那是设计好的降级,不是崩溃。

| 做什么 | 填 |
|---|---|
| 场景新建空物体(名字随意,如 `Document Store`) | **保持激活** |
| 挂 `NetworkObject` | `Is Networked` ✅、`Is Global` ⬜ |
| | **不要加 `NetworkTransform`** —— 它不动 |
| 挂 `DocumentStore` | 没有字段 |

建完选中一次让它跑 `OnValidate`,然后跑一次
**`Fish-Networking → Utility → Reserialize NetworkObjects → Reserialize Scenes`**,确认 `SceneId` 非 0。
**`SceneId` 为 0 时不要打包**(构建期抛异常)。

**顺带:建 `DocumentCatalogue` 资产**(Create → Overworked → Document Catalogue),
按 `PayloadCatalogue` 的顺序填 `PayloadIndex`。`FetchSeconds` 至今没有任何代码读它 —— 填 0 就行。

**验收**:控制台敲 `printers`,不报错、能列出机器。

---

## 第 8 步 · `Computer.prefab`(新建,**自带模型桌子**)

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
| | `_interactReach` | 默认 `2.5` |
| `ComputerPanel` | `_font` | `Assets/Font/simhei SDF.asset`(**必须**,不填就是一窗方块) |
| | `_sortingOrder` | `10`(默认。**必须 ≥ 1**) |

**两个坑:**

- **`ComputerPanel` 不能挂在会被关掉的子物件上** —— 面板的 canvas 建在它自己下面,父物件不激活就没人看得见。代码会报错说这件事
- **它是本项目唯一带 `GraphicRaycaster` 的 UI**,这是故意的(面板要能点)。前提是它锚在**屏幕右侧**、不做全屏遮罩 —— 别把它挪到左上角,那里有 MPPM 要用的 Host / Client 按钮

本机**不需要** `SnapSurface`,也**不要**挂 `ContainerView`。

---

## 第 9 步 · 把工位摆进场

打印机和原料箱各摆若干,**位置落在格中心**。

> 摆完之后选中场景里的 NetworkManager,跑一次
> **`Fish-Networking → Utility → Reserialize NetworkObjects → Reserialize Scenes`**。
>
> 工位 prefab 拖进场景后是「场景 NetworkObject」,靠 `SceneId` 自动生成;`SceneId` 为 0 时它不会被生成,
> 而且**打包会抛异常**。prefab 实例拖进场景时 `OnValidate` 通常会自己设好,但跑一次菜单更保险。

---

## 第 10 步 · `DocumentCatalogue.asset` 填内容

**现在整张表只有一条 spec,而且 `Display Name` 字面量就是字符串 `"0"`** —— 面板上会显示一个
「0」,没人知道那是什么。

1. 把现有那条的 `Display Name` 改成真名(「合同」「报表」…随你)
2. **再加几条 Internet 的**,`Source` 填 1,`Fetch Seconds` 填 3~5

> 不加 Internet 的条目,`FetchSeconds` 那条路走不到 —— 代码已经在了,但没有任何一条 spec 会触发它。

---

## 第 11 步 · 文件夹

### 1. `PayloadCatalogue.asset` 加一条

| 字段 | 填什么 |
|---|---|
| `Payload` | 文件夹模型 prefab(和纸、墨一样,根缩放随意,**尺寸写在这个 prefab 里**) |
| `Badge` | 拍扁的文件夹 logo,给**文件夹箱**用 |
| `Is Container` | ✅ **勾上** |

> 这一条勾错了不会报错,只会让文件夹装不进东西 —— 和「盒子没接线」长得一样。

### 2. `Object.prefab` 的根上挂两个组件

**都在根节点上,和 `NetworkGrabbable` 同一个 GameObject。**

| 组件 | 设置 |
|---|---|
| `ContainerBase` | `Capacity` 留 **0**(= 无限)。见 `CONSTRAINTS.md`:只进不出的容器必须无限 |
| `FolderIntake` | `Centre` / `Half Extents` 先留默认,按文件夹模型实际大小再调 |

> **`Half Extents` 就是这个玩法的准星。** 它是玩家把文件丢进去时要砸中的那个盒子,
> 太小会变成「打靶」。默认值 `(0.28, 0.22, 0.32)` 大约是 0.56 × 0.44 × 0.64 米。
>
> `Centre` 是相对**文件夹自身**的偏移,所以盒子跟着文件夹转,不是地上固定的一块。

### 3. 建一个 `FolderBox.prefab`

复制 `PaperBox`,把 `_payloadIndex` 改成文件夹那一条的索引,模型换成文件夹箱。

---

## 接线之后的第一次实测

按风险排序,每条都给出「错了会看到什么」。

| # | 测 | 错了会看到 |
|---|---|---|
| 1 | 纸箱按 E | 手里没东西 / 东西掉在地上(W5 的 `ServerHandToPlayer` 链路) |
| 2 | 手上有东西时再按 E | 手里被塞了第二份 |
| 3 | 把纸**丢**向打印机 | 没被吞掉(intake 盒子位置/大小) |
| 4 | 纸塞到 **6 张满** → 再装墨盒 | **装不进去** ← 这是这轮改动要修的核心问题 |
| 5 | 一盒墨打满 8 张 | 墨盒没消失 / 消失了但装不进新的 |
| 6 | 输出堆到 6 | 机器丢东西了(应该停下等待) |
| 7 | 按 E 取产物 | 生成不到手里 / 卡在手里放不下 |
| 8 | 连续走 → 体力变化,**两个实例位置是否一致** | 拉扯感 ← W5 唯一的真验收标准 |
| 9 | RTT 下 HUD 的容器行 | 显示 `payload 0` 而不是「纸」→ catalogue 或索引没接上 |
| 10 | 左上角 Host 按钮 | 点不动 → HUD 挂了 `GraphicRaycaster` |

### 文档链路(第二轮)—— 用控制台铺路,不必等电脑面板

`document 1` → `queue 0 0` → 等机器打完 → 取走。这几条是这轮唯一的端到端证明。

| # | 测 | 错了会看到 |
|---|---|---|
| 11 | `document 1` 然后 `queue 0 0`,等机器打完 | 堆上还是空白纸 → `PrinterDisplay` 没接 `DocumentStore`,或 `PayloadLabel._texts` 没填 |
| 12 | **再 `document 1`(会拿到编号 2)然后 `queue`,`#1` 还在堆上时产出 `#2`** | 编号停在旧的 ← **`PrinterDisplay` 认的是外观不是身份**,那是这轮第一个真 bug |
| 13 | 取一份产出的纸拿在手里 | 手里那张没有编号 / 队伍颜色 |
| 14 | 打开电脑面板,点「打印到 #1」 | 面板不关 / 打印机队列没变 → `Printer.Queue` 链路 |
| 15 | **面板开着**点左上角 Host 按钮 | 点不动 → 面板挪到左上角去了,或被人加了全屏遮罩 |
| 16 | 面板开着按 Esc | 关不掉,或关了但人动不了 → `SetInputEnabled` 没还回去 |

> **第 12 条单独拎出来。** 不修 `PrinterDisplay` 的话,11 是**过得去**的 —— 第一份文档编号对,
> 你会以为通了;到 12 才发现编号不跟着换。这是最容易得出错误结论的一条。

**注意第 4 条**:如果它失败,说明纸墨又回到共用一个容器了 —— 那会**永久卡死机器**
(塞满纸 → 墨进不来 → 配方永远凑不齐 → 而机器从不退还输入)。

---

### 文件夹 + Internet 获取(第三轮)

| # | 测 | 错了会看到 |
|---|---|---|
| 17 | 面板上点一条 **Internet** 文档 →「打印到 #1」 | 面板立刻关了 → 那条 spec 的 `Fetch Seconds` 还是 0(见第 10 步) |
| 18 | 点完那一眼 | 那行没变灰、不倒数 → `MarkWaiting` 或 `FetchingChanged` 没通 |
| 19 | 倒数到 0 | 打印机队列没多一条 → `Computer.UpdateFetches` 没在跑 |
| 20 | **队列满时**等它倒数完 | 行停在「下载完成,等待打印机…」,**腾出位置后自己进去** ← 这是对的 |
| 21 | 从文件夹箱按 E | 拿到的是纸 → `FolderBox` 的 `_payloadIndex` 填错了 |
| 22 | 文件夹**放在桌上**,把手里的文件丢上去 | 文件没被吞掉 → `FolderIntake` 的盒子太小 / 位置不对,或 `Is Container` 没勾 |
| 23 | 文件夹**拿在手上**走过地上的文件 | 文件被吸进去了 ← **不应该**。`Update` 里的 `Held` 判断没了 |
| 24 | DebugHud 的容器列表 | 多出一堆 `Object 0/∞ (空)` → `ShouldReport` 的过滤没了 |
| 25 | 文件夹里有 2 份文件时看 DebugHud | 应该有一行 `文件夹 2/∞ 数据 x2` |
| 26 | 把**文件夹**丢向打印机 | 被吃了 ← 不应该(文件夹的 payload 不是纸也不是墨) |
| 27 | 把**一张白纸**丢向文件夹 | 被吃了 ← 不应该(白纸没有 `DataId`) |
| 28 | 文件夹里塞 3 份文件,拿在手里走到打印机 | 机器从你手上把文件夹拿走 ← 不应该 |

> **第 25 条是这一轮唯一的验收手段。** 文件夹里的东西没有世界内显示 ——
> 容器挂在根节点上,而 `ApplyPayload` 会清掉根节点的所有子物体,`ContainerGauge` 挂上去会在
> spawn 时被抹掉。见 `DEVELOPMENT.md` 的坑表。
