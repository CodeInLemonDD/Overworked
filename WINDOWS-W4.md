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
