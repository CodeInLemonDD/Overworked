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
└─ 输出 (子物件)          ← 已有节点
    ├─ ContainerBase      ← 输出,容量 6    【不挂 ContainerView】
    └─ 打印好的纸1..6      ← 视觉由动画 + PrinterDisplay 负责
```

**三个容器各自挂在自己的节点上,不要挂在根上。**

**三个都【不要】挂 `ContainerView`:**

- `输出` —— 你已经有 `打印好的纸1..6` 六个槽位了。动画负责开关、`PrinterDisplay` 负责填内容。
  再挂一个 `ContainerView` 会在同一位置又实例化一批 prefab,两套视觉叠在一起。
- `纸槽` / `墨槽` —— 只关心「还剩多少」,顺序没有意义,该用指示灯 / 滚动条,不该用纸堆。
  纸塞进去就是数据了,把它重新画成物理纸堆和容器模型本身是矛盾的。

**判据:容器的可视化用「堆」还是「表」,看它的顺序有没有意义。** 输出的顺序有意义
(取用是 LIFO,玩家要看见最上面那张是什么),所以用堆;纸和墨没有,所以用表。

`ContainerView` 留给将来那些**顺序有意义、但没有专属动画**的容器(货架、文件夹)。

### 引用

| 组件 | 字段 | 填 |
|---|---|---|
| 根 | `NetworkObject` | 加上(**容器要同步,必须有**) |
| 根 `Printer` | `_paper` | **`纸槽`** 的 `ContainerBase` |
| | `_ink` | `Ink` 的 `ContainerBase` |
| | `_output` | `Output` 的 `ContainerBase` |
| | `_paperPayloadIndex` | **0** |
| | `_inkPayloadIndex` | **1** |
| | `_outputPayloadIndex` | **2** |
| | `_secondsPerOutput` | `3`(盲设,要试玩调 —— 应**明显小于**「跑到原料箱再跑回来」的时间) |
| | `_printsPerCartridge` | `8` |
| | `_intakeCentre` / `_intakeHalfExtents` | 选中机器看 Gizmo 调,**绿框要盖住桌面**;上沿必须高过物体落在机器顶面后的位置 |
| 三个槽位 | (不挂 `ContainerView`,理由见上方结构说明) | — |

容器容量填在 **`ContainerBase._capacity`** 上,不是 `Printer` 上。

### ⚠️ `PlacementBlocker` 挂哪

> 必须挂在**它占据的那个碰撞体所在节点**,或该节点与 `SnapSurface` 之间的任一祖先上。

挂在没有碰撞体的纯视觉子节点上 → 无效。
挂在 `SnapSurface` **之上** → **无效,原 bug 原样复现**(物体被吸到机器顶上)。

本机**不需要** `SnapSurface` —— 机器占住那格,那格本来就不该能放东西。

启动时机器会自己检查容量是不是 6 / 1、`_printsPerCartridge` 是不是正数,不对会在 Console 报警告。

---

## 第 4 步 · `PaperBox.prefab`(新建,**自带模型桌子**)

```
PaperBox (根)
├─ 模型桌子 + 箱子模型
├─ ContainerBase          ← 容量 3~4
├─ PlacementBlocker
├─ 碰撞体
├─ PaperBox
└─ (不挂 ContainerView —— 只关心剩几张,该用指示灯 / 滚动条)
```

| 组件 | 字段 | 填 |
|---|---|---|
| 根 | `NetworkObject` | 加上 |
| 根 `PaperBox` | `_container` | 根的 `ContainerBase` |
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

## 第 7 步 · 把工位摆进场

打印机和原料箱各摆若干,**位置落在格中心**。

> 摆完之后选中场景里的 NetworkManager,跑一次
> **`Fish-Networking → Utility → Reserialize NetworkObjects → Reserialize Scenes`**。
>
> 工位 prefab 拖进场景后是「场景 NetworkObject」,靠 `SceneId` 自动生成;`SceneId` 为 0 时它不会被生成,
> 而且**打包会抛异常**。prefab 实例拖进场景时 `OnValidate` 通常会自己设好,但跑一次菜单更保险。

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

**注意第 4 条**:如果它失败,说明纸墨又回到共用一个容器了 —— 那会**永久卡死机器**
(塞满纸 → 墨进不来 → 配方永远凑不齐 → 而机器从不退还输入)。
