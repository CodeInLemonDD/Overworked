# 开发笔记

这份文档记录**代码里看不出来的东西**——为什么这么设计、哪些值不能乱改、哪些坑踩过。
有意不重复 README 的玩法与技术栈介绍,只写会让人做错决定的部分。

最后更新:2026-09-29

> 另外两份文档:
> - **`CONSTRAINTS.md`** —— FishNet 硬约束与**已冻结的接口**(容器 / payload)。改代码前必读
> - **`WINDOWS.md`** —— 并行窗口的开工规格与文件所有权划分

---

## 当前状态

| 项 | 版本 / 状态 |
|---|---|
| Unity | `6000.6.0f1` |
| FishNet | `4.7.3`(UPM git 依赖) |
| FishyFacepunch | `4.1.0`(`Assets/FishNet/Plugins/`) |
| Facepunch.Steamworks | `2.5.2`(**自制嵌入包** `Packages/com.facepunch.steamworks/`) |
| 输入系统 | 新输入系统**独占**(`activeInputHandler: 1`) |
| 传输层 | Tugboat(运行时自动挂载,端口 7770)。**Steam 传输尚未启用** |

**已完成**:联网链路、玩家移动(主机权威 + 预测回滚)、相机跟随、抓取/手持/放置/投掷/网格吸附;
容器与 payload 地基、工位交互接缝、吸附阻挡、保洁阿姨、体力、调试 HUD、打印机、箱子;
**文档数据层**(一份文档 = 一条 `Data` 条目)、**电脑工位与面板**、**容器存量仪表**。

**链条现在是通的**:电脑上挑一份文档 → 送进某台打印机的任务队列 → 机器凑齐纸、墨、文档后开印
→ 产出带编号与队伍颜色的纸 → 拿在手里,尺寸和堆里、打印头上的完全一致。

**未开始**:NPC(客户 / 同事 / BOSS、需求、任务板)、文件夹与章笔、电源分区、道具系统、
Steam 真机联机。**「Internet 数据获取」只做了一半** —— `DocumentCatalogue.Spec.FetchSeconds`
是个字段,但**至今没有任何代码读它**,所以 Internet 来源和后台文件目前只差一行标签。

> **接线已经不再是瓶颈** —— 这一轮全部模块都接进了场景,控制台、HUD、仪表都能用。
> 剩下的欠账是**手感与数值**:见第四节的盲设值,以及 `WINDOWS-DATA.md` 各窗口自报的未验证项。

> **README 里的「基础任务系统」已经被重新设计掉了。** 任务不再是「把方块送到格子」,而是
> **NPC 提出的需求**(客户要合同、同事要报表)。原来那套「老板派单 → 送方块 → 计分」的主循环
> **作废**,不要照它开工。

---

## 一、最容易做错的几件事

### 1. Player prefab 上不能有 `NetworkTransform`

移动用的是 FishNet 的 **state forwarding** 架构(与官方 `Demos/Prediction/CharacterController/` 一致)。
`ConfigureForPrediction()` 被 `!_enableStateForwarding` 门控(`NetworkObject.Prediction.cs:252`),
而 Player 是 `_enableStateForwarding: 1`,所以它**永不执行**,不会自动中和 `NetworkTransform`。
而 `NetworkTransform.SendToServer` 仅在 `!_clientAuthoritative || !IsOwner` 时早退 ——
挂上去的话拥有者每 tick 都发包,**与权威 CC 模拟抢同一个 transform**。

### 2. 交互系统的 RPC 声明在**玩家**身上,不在物体上

`[ServerRpc]` 默认 `RequireOwnership = true`。玩家的所有权永远归自己的客户端,所以 RPC 永远通过;
而物体的所有权会在持有者之间转移、放下后还给服务器,声明在物体上会失效。

**也不用 FishNet 自带的 `PredictedOwner`** —— 它先 `SetLocalOwnership` 本地改所有权再发 RPC,
服务端一旦否决**没有任何回滚机制**,客户端会卡在「本地是 owner、服务端不是」的死结。

### 3. 手持物体不挂父子,每帧代码驱动

`NetworkTransform` 写的是 `localPosition`,挂父节点会变成复制局部偏移,而远端玩家姿态永远不同步;
且 `Graphical` 根本不是 NetworkObject,`SetParent` 的前置条件不满足。

### 4. 抛物线预览不能用 `LineRenderer`

`LineAlignment.View` 需要**逐段根据相机算四边形朝向**,线段方向接近相机视线时计算退化、线段塌缩消失。
相机 yaw 为 0 时表现为「玩家朝 ±Z 看不到、朝 ±X 正常」。
现用**一串小球**(点阵),从任何角度看都可见,不需要朝向计算。

---

### 5. 一个 payload 的尺寸,只写在它自己的 prefab 里

**槽位节点只说「放哪」,不说「多大」。** 推论:**功能性的子节点不要挂在「为了造型而压扁」的美术节点下面。**

打印机的堆就是这么坏掉的:六个槽位挂在 `Table`(0.8, 0.5, 0.8)里面,再挂在 `输出`(0.5, 0.01, 0.6)里面,于是每张纸的尺寸被乘了两遍。手工抵消的话 —— **有几个压扁的祖先就要补几个数**,而没有任何东西检查那组数是否完整。补了 `输出`、漏了 `Table` 的那一版**看起来是对的**(堆里的纸只比手里窄 20%,不刺眼),它被当成「修好了」用了一轮。

正解是**把槽位挪到根的直系子节点下面** —— 和 `Table`、`输出` 平级。挪完之后每个槽位的累计缩放就是 `(1,1,1)`,补偿的数字全部消失,而且**不需要任何人记住任何规则**。模型自己的缩放(纸是拍扁的)留在**带网格的那个子节点**上。

同一个坑在手里的物体上也出现过:prefab 的根带缩放,重构成「根 1 倍、模型带缩放」之后才对。

**判据:一个节点要么是位置标记(空 transform、缩放 1),要么是美术(带网格、自己带缩放)。两件事一起干,迟早会有一边被另一边改掉。**

---

## 二、不能乱改的值

| 位置 | 值 | 为什么 |
|---|---|---|
| Player → CharacterController → **Center** | **`(0, 0.85, 0)`** | 用 `(0, 0.75, 0)` 时**角色脚会悬空不着地**。角色身上已无视觉碰撞体,此值只影响 CC 自身 |
| Object → NetworkTransform → **Packing → Position** | **`Unpacked`** | 默认 `Packed` 会把位置**量化到 1 厘米**,网格吸附会失真(0.5375 传到对端变 0.54) |
| Object → NetworkObject → **Prevent Despawn On Disconnect** | **勾选** | 投掷者长期持有所有权,不勾的话他一断线会带走**他扔过的所有物体** |
| Player → NetworkObject → **_enablePrediction** | **`1`** | 唯一的移动行为开关 |
| `Assets/DefaultPrefabObjects.asset` | Player=0, Object=1 | 只能在 Editor 里改,手改会让两个 prefab 的 `PrefabId` 冲突 |

**`PrefabId` 序列化值是 `65535` 也不用管** —— `NetworkManager.Awake()` 会按列表顺序重新分配。

---

## 三、踩过的坑

| 现象 | 根因 |
|---|---|
| 两个玩家都动不了 | `InputActionAsset` 是共享的 ScriptableObject,一个玩家 `Disable()` 会关掉另一个人的输入。**每个玩家对象必须 `Instantiate` 自己的一份** |
| 物体怎么都捡不起来 | 拾取判定原点在**手点**(玩家前方 0.6 米),比它更近的物体被判成「在身后」。改为以玩家根节点为原点 |
| 玩家能踩物体叠高越过桌子 | 物体高 0.125 而 CC `StepOffset` 是 0.3,自动踏上去。**调 StepOffset 到 0.1 也无效**。正解是 `Physics.IgnoreCollision` 让玩家与物体**完全不参与彼此物理** |
| 桌子吸附完全失效 | 探测射线**打到了物体自己**——手持时碰撞体禁用所以没事,落定后启用,从上方打下来的射线第一个命中就是它自己,而它没有 `SnapSurface`。改用 `RaycastNonAlloc` + 排除自身 |
| 物体被吸到桌上固定物件的顶上 | 探测射线命中的是**桌上那个子物件**,而 `GetComponentInParent<SnapSurface>()` 顺着父级找到了**桌子的** `SnapSurface` —— 于是 `surfaceY` 取到的是物件顶面的高度。命中本身分不清「这是桌面」和「这是站在桌面上的东西」,所以由占位者挂 `PlacementBlocker` 声明。**和上一行是同一族问题:射线打到的不是你以为的那个碰撞体**。布置规则见 `CONSTRAINTS.md` |
| 印出来的纸、徽标、手里的东西**全是 1×1×1 的方块** | 三处 `localScale = Vector3.one` 把 payload prefab 自己的缩放丢掉了。**那个缩放就是它的外观**(纸是拍扁的、墨盒是薄片),不是「摆在哪」。三份都活着,而且**只要所有 prefab 都是 1 倍就一份都看不出来** —— 谁做出第一个非 1 倍的 prefab,三处同时发作 |
| 堆里的纸比手里的小一半、薄 25 倍 | 手工抵消祖先缩放时**漏了一层**(见第一节第 5 条) |
| Box 上凭空多出一个 `SupplyBox`,纸的补给速度翻倍 | `[RequireComponent(typeof(SupplyBox))]` 在往子节点挂 `BoxBadge` 时**自动补了一个** `SupplyBox`。两个箱组件共用同一个容器、各跑一套计时器。**`RequireComponent` 只会补,不会问** —— 想要「必须有」的语义就自己在 `Awake`/`Start` 里查并报错 |
| 手里的东西不跟着转身 | `UpdateCarry` 每帧写 `Quaternion.identity`。注释给的理由有一半是错的:「和吸附后一致」一直是**捕捉**在保证的(放下时写 identity)。改成只跟 yaw |
| 投掷物落在地上捡不起来 | 状态卡在 `Free`,而 `Free` 不在拾取白名单。**吸附与松手是两件事**:吸附看高度(即时),松手看速度(停稳后交还所有权) |

---

## 四、手感调参

这些值是**盲设的,从未验证过**,要按实际试玩调。全部在 Inspector 里。

| 组件 | 字段 | 当前 | 作用 |
|---|---|---|---|
| `PlayerInteraction` | `Kick Force` | `12` | 推开物体的力 |
| | `Kick Radius` | `0.7` | 多近才推 |
| | `Kick Vertical Range` | `0.45` | 高度差超过就不推(防止扫掉桌上的东西) |
| | `Throw Angle Degrees` | `10` | 发射仰角(负值可往下扔) |
| | `Throw Min/Max Speed` | `6` / `14` | 蓄力范围 |
| | `Tap Max Seconds` | `0.25` | 超过这个时长算蓄力投掷,以内算放下 |
| | `Pickup Radius` / `Half Angle` | `1.1` / `60°` | 面前扇区抓取范围 |
| `NetworkGrabbable` | `Capture Height` | `0.5` | 离桌面多低才自动吸附 |
| | `Capture Depth` | `0.25` | 下界,防止桌子底下的物体被吸上来 |
| | `Settle Speed` / `Seconds` | `0.15` / `0.35` | 停稳判定,决定何时交还所有权 |
| `ThrowTrajectoryPreview` | `Dot Count` / `Dot Size` | `18` / `0.055` | 点阵密度与粗细 |

---

## 五、待办

### 该尽快处理的

- **场景里的 NetworkManager 是 FishNet 包内的 prefab**(`Library/PackageCache/.../Demos/Prefabs/NetworkManager.prefab`)。
  一旦 FishNet 升级导致包缓存哈希变化(`@12ee279bcfde` → 别的),**场景引用会直接断掉**。
  正式开发前应把它拷进 `Assets/` 重建。
- **接线是当前真正的瓶颈。** 20 个脚本里只有 7 个接进了场景 / prefab(W6 按 GUID 扫过),
  而那 7 个全在地基之前 —— 八个模块的代码都在仓库里,但没有一个能在编辑器里跑起来。
  每份交付说明末尾都有该模块的接线清单,汇总见 `WINDOWS-6.md`。

### 下一步

- **接线与首轮实测** —— 在这个做完之前,任何新代码都只是堆量
- 数据层与 NPC(文件数据、电脑面板、客户 / 同事 / BOSS、文件夹、章笔)
- 电源分区与道具
- Steam 传输真机联机(FishyFacepunch 只能走 Steam P2P,**本地无法自测**)

### 已知局限

- **打印头那张纸会闪一下。** 队列较长时,机器打印到当前这份的尾声,打印头上会**短暂出现下一份的内容**,
  再变回来,最终结果是对的。**已定位方向但没修**:`Printing N → Finish N` 那条过渡有 **0.25 秒的混合**,
  而下一份任务在上一份完工的下一帧就开工了(`_printingDocument` 立刻变成 N+1),
  于是混合期间头上那份已经被换成下一份。**用户已确认当前判断,并决定先不修 —— 不影响游玩。**
  修的方向是让 `SyncPrintingSheet` 的重画等过渡走完,而不是改动画。
- **`FetchSeconds` 至今没有任何代码读它。** Internet 来源与后台文件在行为上没有区别,只差面板上一行标签。
- **只出不进的容器容量有限时,队列可以被合法堵死**(塞满自己的任务)。能恢复(打完就通),不是死锁,
  但这是个「抢机器」的玩法。打印机队列容量 5 是这条的分界线。

- 投掷物砸到人时,**投掷者端与被砸者端的解算结果不同** —— 这是「投掷者客户端模拟」的固有代价,
  被砸者在自己屏幕上不会被推动(他那边物体是运动学的)。不改为服务端权威物理则无法消除。
- 恶意客户端可以用任意速度投掷。但**抓取是完全服务器校验的**(范围与朝向都在服务端复核)。
- **体力由拥有者自己模拟,并随移动数据上报移速倍率**,所以改过的客户端可以永远上报 1.0 保持全速。
  与上一条同类:不把体力改成服务端模拟就消除不掉。
- **`PlayerStamina.Local` 是静态字段**,只在「MPPM 的虚拟玩家是独立进程」这个前提下成立 ——
  目前成立(否则两个玩家不可能各控各的)。将来若变成同进程多玩家,它会被最后写入者覆盖。
- 网格吸附要求桌子**摆在格中心**(`x,z` 为 `±0.5`、`±1.5` 这类值)。`SnapSurface` 带 `OnValidate`,
  摆放不正会在 Console 报警告。
