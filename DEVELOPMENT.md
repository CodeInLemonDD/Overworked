# 开发笔记

这份文档记录**代码里看不出来的东西**——为什么这么设计、哪些值不能乱改、哪些坑踩过。
有意不重复 README 的玩法与技术栈介绍,只写会让人做错决定的部分。

最后更新:2026-09-28

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

**已完成**:联网链路、玩家移动(主机权威 + 预测回滚)、相机跟随、抓取/手持/放置/投掷/网格吸附。
**未开始**:任务系统、道具系统、Steam 真机联机。

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
- **README 与实际不符**:见下方「README 待修正」。
- **`CONTRIBUTING.md` 与 `LICENSE` 不存在**,但 README 引用了它们。

### README 待修正

1. 依赖表里 `Facepunch.Steamworks` 的来源写的是 GitHub 链接,但**官方不提供 UPM 分发**——
   本项目用的是自制的嵌入包,应说明清楚,否则别人按 README 装不上。
2. Roadmap 里 Prototype 阶段标注「Core movement, basic task system, 2-player LAN / In Progress」——
   移动与本地联机已完成,**任务系统未开始**。
3. `git clone https://github.com/yourusername/Overworked.git` 是占位地址。
4. 引用的 `CONTRIBUTING.md` / `LICENSE` 文件不存在。

### 下一步

- **基础任务系统**(README 路线图 Prototype 阶段剩余的唯一一项)
- Steam 传输真机联机(FishyFacepunch 只能走 Steam P2P,**本地无法自测**)

### 已知局限

- 投掷物砸到人时,**投掷者端与被砸者端的解算结果不同** —— 这是「投掷者客户端模拟」的固有代价,
  被砸者在自己屏幕上不会被推动(他那边物体是运动学的)。不改为服务端权威物理则无法消除。
- 恶意客户端可以用任意速度投掷。但**抓取是完全服务器校验的**(范围与朝向都在服务端复核)。
- 网格吸附要求桌子**摆在格中心**(`x,z` 为 `±0.5`、`±1.5` 这类值)。`SnapSurface` 带 `OnValidate`,
  摆放不正会在 Console 报警告。
