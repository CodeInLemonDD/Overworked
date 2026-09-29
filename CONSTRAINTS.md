# 并行开发约束与接口契约

这份文档是**每个并行窗口开工前必读**的。里面两类东西:

1. **硬约束** —— 读 FishNet 4.7.3 源码核实过的,和玩法无关,换多少轮设计都不变,但违反任何一条都会当场坏掉
2. **已冻结的接口** —— P0 产出的容器与 payload 机制,依赖它的模块按这里的签名写,不要自己另起一套

最后更新:2026-09-29

---

## 零、统一模型

> **容器里的东西退出物理世界。**

| 对象 | 是什么 |
|---|---|
| 桌子 | 纯家具,零功能 |
| 机器 / 工位 | 独立 prefab,**自带模型桌子**,整件摆在场景里。功能全在机器上 |
| 原料 | 玩家**塞进机器内部**(按 E 或丢过去)—— 进去即退出网格世界 |
| 产物 | 堆在**机器自己身上**(可视化)—— 同样不在网格世界里 |

**推论:** 网格吸附、放置检测、保洁阿姨,**都看不见容器内容物**。任何新功能只要问一句「它看不看得见容器里的东西」,答案默认是「看不见」。

**配套空间规则 —— 桌子是安全区。** 上桌或入容器 = 安全;留在物理世界地面 = 会被阿姨收走。

**推论(已确认):机器顶面不算「桌」。** 物体塞不进机器(槽满了)就停在机器上 —— 而它**并不在任何容器里**,所以它仍在物理世界,阿姨照样收。

> 这条不是特例,是模型的直接推论,判据只有一句:**它在容器里吗?** 不在,就归保洁阿姨管。
> 机器不是仓库;放不下就自己拿回去。

---

## 一、硬约束

### FishNet 层面

| # | 事实 | 出处 |
|---|---|---|
| 1 | **边遍历 `Spawned` 边 `Despawn` 会抛 `InvalidOperationException`** —— 它是 `Dictionary` 的活视图,`Despawn` 同步删键。**先快照**。用 `GrabbableSpawner.CollectSpawnedGrabbables` | `ManagedObjects.cs:31-32, 97-101` |
| 2 | **`_list[i].Field++` 编译不过(CS1612)**;能编译的 `GetCollection(true)[i].Field++` **静默不同步**。正确写法:`var t = _list[i]; t.F = x; _list[i] = t;` | `SyncList.cs:687-691, 742-755` |
| 3 | **主机上每种 SyncType 回调触发两次**(`asServer: true` / `false`)。**客户端侧处理器第一行必须挡住重复的那次**,否则界面重绘两遍、副作用执行两次 | `SyncList.cs:196, 422`;`SyncTimer.cs:465-471` |
| 4 | **`ServerRpc` 默认 `RequireOwnership = true`** → 不能声明在无主的场景 NetworkObject 上。玩家交互的 RPC 一律放 `PlayerInteraction` | `Attributes.cs:45-55` |
| 5 | **`[Server]` 编译成 `if (!IsServerInitialized) return;`** —— 所以**对象 spawn 之前调不了**。要在 `GetPooledInstantiated` 与 `Spawn()` 之间调用的方法,**不能标 `[Server]`**,自己判 `InstanceFinder.IsServer` | `NetworkBehaviourHelper.cs:398-408` |
| 6 | **`OnTick` 一帧可能跑 2–3 次,且可能掉 tick**(`_allowTickDropping` 不区分服务端)。计时一律用 `TimeManager_OnUpdate` + `Time.unscaledDeltaTime` | `TimeManager.cs:710-714, 718` |
| 7 | **`SyncTimer` 不会自己走**,每个端都要调 `Update()` | `SyncTimer.cs:421-425` |
| 8 | **场景里的 `NetworkObject` 会自动生成** —— 包括**拖进场景的 prefab 实例**(`CreateSceneId` 只在 `IsPartOfPrefabAsset` 时跳过,实例不算)。客户端会先 `SetActive(false)`,收到 spawn 消息后**复用同一实例** | `ServerObjects.cs:405-493`;`ClientObjects.cs:249-252`;`NetworkObject.Serialized.cs:159-215` |
| 9 | **`NetworkObject.NetworkBehaviours: []` 是无害残留** —— 运行时按层级+组件顺序重建,发包走运行时属性。**往 prefab 加组件不需要 Editor 刷新** | `NetworkObject.cs:1002-1032`;`NetworkBehaviour.SyncTypes.cs:502` |
| 10 | **`SyncList` 类型参数可以是自定义 struct**,但**字段必须 public** —— private / `[SerializeField] private` 会被 weaver **静默跳过**(编译通过、运行不同步)。也不能有 public 属性(有 get+set 会被一起序列化) | `SyncTypeProcessor.cs:399-437`;`TypeDefinitionExtensions.cs:31-62` |
| 11 | **Scene 物件 `Despawn()` 退化成 `SetActive(false)`**,没有恢复路径。**永远不要 despawn 场景物件** | `ManagedObjects.cs:430-434` |
| 12 | **`InstanceFinder.IsServer` 是 `[Obsolete]`** —— 用 `IsServerStarted`。行为完全一样(源码里就是 `IsServer => IsServerStarted`),换掉纯粹是消警告 | CS0618 |
| 13 | **`TimeManager.OnUpdate` 跑在 tick 【之前】** —— 默认 `_updateOrder = BeforeTick`,而 `TickUpdate()` 每帧只调一次 `OnUpdate`。「每帧一次」成立,「在 tick 之后」**不成立** | `TimeManager.cs:158, 366-385` |
| 14 | **`NetworkBehaviour` 上的 `OnValidate` 必须 `override`,不能隐藏。** 基类是 `protected virtual` 且会调 `TryAddNetworkObject()`(编辑器里自动解析 NetworkObject 引用);写成 `private void OnValidate()` 会**静默停掉它**。weaver **不**接管 `OnValidate`,所以没有「它自会处理」这回事 | `NetworkBehaviour.cs:206-214` |
| 15 | **`Awake()` 的 CS0114 是本项目基线,不要"修"。** weaver 把用户的 `Awake` 改名成 `Awake_UserLogic_*` 再生成一个真正的 `Awake` 串起网络初始化 —— 所以 `TickNetworkBehaviour` 子类里就是写 `private void Awake()`,不要改成 `override` | `NetworkBehaviourHelper.cs:73` |

### 项目层面

| # | 约定 |
|---|---|
| A | `InputActionAsset` 是共享 ScriptableObject。**每个玩家对象必须 `Instantiate` 自己的一份**,否则一个玩家的 `Disable()` 会关掉另一个人的输入 |
| B | 项目**没有 asmdef**,全部代码在一个程序集 → **任何一个模块的编译错误,所有模块都跑不起来**。交付前必须确认自己这部分能编译 |
| C | 自建 HUD 必须 `sortingOrder >= 1`、**不挂 `GraphicRaycaster`**、所有 graphic `raycastTarget = false`,否则会吃掉 FishNet demo 左上角 Host/Client 按钮的点击 —— MPPM 测试就靠它们 |
| D | 场景与 prefab 是 YAML,**无法合并**。只能有一个窗口碰,其余由用户手工操作 |

---

## 二、已冻结的接口(P0 产出)

命名空间 `Overworked.Containers`。**依赖它们的模块按这些签名写,不要另起一套。**

### `ContainerEntry`(struct)

```csharp
public enum ContainerEntryKind : byte { Empty = 0, Entity = 1, Data = 2 }

[System.Serializable]
public struct ContainerEntry
{
    public byte Kind;        // ContainerEntryKind
    public int PayloadIndex; // Entity 用,指向 PayloadCatalogue;否则 -1
    public int DataId;       // Data 用;否则 -1

    public static ContainerEntry ForEntity(int payloadIndex);
    public static ContainerEntry ForData(int dataId);
    public static bool IsEmpty(in ContainerEntry entry);
}
```

无属性、只有 public 字段 —— 见约束 #10。

### `PayloadCatalogue`(ScriptableObject)

```csharp
public class PayloadCatalogue : ScriptableObject
{
    public int Count { get; }
    public GameObject Get(int index);           // 越界返回 null
    public bool TryGet(int index, out GameObject prefab);
}
```

**数组顺序就是契约**:索引在任何地方含义相同。**只能追加,不能重排**。

**payload prefab 不能自带 Rigidbody** —— 外壳上已经有一个,第二个会让物体穿过自己的碰撞体。

### `ContainerBase`(抽象 NetworkBehaviour)

```csharp
public abstract class ContainerBase : NetworkBehaviour
{
    public int Capacity { get; }        // <= 0 表示无限
    public int Count { get; }
    public bool IsUnlimited { get; }
    public bool IsFull { get; }
    public event Action ContentsChanged; // 每个端、每次变更各触发一次(已去重)

    public bool TryGetEntry(int index, out ContainerEntry entry);

    [Server] public bool ServerTryAdd(ContainerEntry entry);        // 满了返回 false
    [Server] public bool ServerReplaceAt(int index, ContainerEntry entry);
    [Server] public bool ServerTryRemoveAt(int index);
    [Server] public bool ServerTryRemoveFirst();                     // 队列用
    [Server] public bool ServerTryRemoveLast();                      // 栈用
    [Server] public void ServerClear();
}
```

- **顺序有含义**:队列从头取,栈从尾取。用 `First`/`Last` 而不是裸索引,让意图在调用点可读
- **不含任何触发逻辑** —— 什么时候算「料齐」、产出什么、耗时多久,都属于拥有它的工位
- 服务器要读旧值就先 `TryGetEntry` 再删/改 —— **故意没有带 `out` 参数的重载**,因为 `[Server]` 守卫会提前 return,`out` 就没人赋值了

### `ContainerView`(MonoBehaviour)

把容器内容画成**本地可视化的堆叠**。不联网、**无碰撞、不可抓**。

- 依赖 `ContainerBase` + `PayloadCatalogue`
- 订阅 `ContentsChanged` 重建
- 取走一个 → 列表变短 → 自动重摆
- `_showData` 默认关(数据还没有视觉表示,画出来会在堆里留洞)

### `NetworkGrabbable` 的 payload 部分

```csharp
public int PayloadIndex { get; }              // -1 = 用 prefab 原始样子
public void ServerSetPayload(int index);      // 见约束 #5 —— 故意没标 [Server]
public void RefreshGeometry();                // 换装后重测碰撞体与几何
```

**索引 -1 表示「prefab 原始样子」** —— 此时 `ApplyPayload` 什么也不做,所以默认路径零成本、**不需要改 prefab 结构**。只有传了真实索引才会清掉原有子节点。

### `GrabbableSpawner` 的静态工具

```csharp
public static NetworkObject SpawnGrabbable(int payloadIndex, Vector3 position,
                                           Quaternion rotation, NetworkConnection owner = null);
public static void CollectSpawnedGrabbables(NetworkManager manager, List<NetworkGrabbable> buffer);
public static void DespawnAllGrabbables(NetworkManager manager, List<NetworkGrabbable> buffer);
```

**任何需要造一个可抓物体的地方都用 `SpawnGrabbable`** —— 它是唯一入口,免得各模块对「怎么造物体」各有一套。payload 在 spawn **之前**设好,这样它随 spawn 消息一起到达。

`CollectSpawnedGrabbables` / `DespawnAllGrabbables` 是约束 #1 的唯一正确实现,不要在别处重写。

### `StationBase`(抽象 NetworkBehaviour)—— P0.1

```csharp
public abstract class StationBase : NetworkBehaviour
{
    public float InteractReach { get; }

    [Server] public void ServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress);
    protected abstract void OnServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress);
}
```

**工位继承它,覆写 `OnServerInteract`。**

**不要给 `OnServerInteract` 加 `[Server]`。** weaver 是靠**改写方法体**插入守卫的,而抽象方法没有方法体 —— 标上去会让 weaver 空引用。守卫在具体的 `ServerInteract` 上,覆写者只能经由它被调用。

工位靠**自己的碰撞体**被玩家的扇区检测找到。`InteractReach` 只是服务端的粗校验,用来防止改过的客户端隔着地图操作机器 —— 客户端找到工位时已经判定过一次距离了。

### 玩家侧(已冻结)

```csharp
// PlayerInteraction
public Vector3 HandPosition { get; }                         // 服务端读:生成物体时的落点
[Server] public void ServerHandToPlayer(NetworkObject nob);   // 让玩家开始持有刚生成给他的物体

// NetworkGrabbable
public static bool IsHeldBy(NetworkManager manager, int clientId);   // 服务端:「双手为空」的判定
```

**「双手为空」一律用 `!NetworkGrabbable.IsHeldBy(manager, conn.ClientId)`。** 服务端没有现成的携带标志,这个静态是唯一正确实现 —— 不要在别处重写一份。

**往玩家手里生成物体:两步,顺序固定。**

1. `GrabbableSpawner.SpawnGrabbable(payload, player.HandPosition, rot, conn)` —— 生成,并把所有权给该玩家
2. `player.ServerHandToPlayer(nob)` —— **标记为持有** + 让客户端开始持有

只做第一步物体掉在地上;只做第二步没有物体。

**第 2 步里已经包含「标记为持有」,调用者不要再自己调 `ServerSetHeld`。**

这一条原本写在调用方,结果打印机和原料箱两个窗口各自踩了一遍才补上。原因是:物体生成出来是 `Idle` 且**无持有者**,
而**所有服务端规则读的是这份状态**,不是客户端那份。漏掉的后果是三条同时发生 ——

- 「双手为空」(`IsHeldBy`)永远为真 → 能重复领第二份
- 手上的物体碰撞体没关 → 会被网格吸附到格子上
- `CmdDropObject` 与 `CmdNotifyThrown` 都检查 `Held` → 全被拒 → **东西永远卡在手里**

**交互的动词是 `bool longPress`**(按住 ≥ 0.3 秒为 true),在**松手时**发出。不关心长短按的工位忽略它即可。玩家按 E 时**抓取优先**,抓不到才轮到工位 —— 所以工位摆在桌子旁边不会让桌上的东西变得捡不起来。

### `PlacementBlocker`(空标记 MonoBehaviour)—— W3

**用途**:给「站在某一格里的固定物件」挂上,那一格不再接受放置。机器(打印机、原料箱)也算固定物件。

**为什么需要它**:探测射线从上往下打,命中物若是桌子的子节点,`GetComponentInParent<SnapSurface>()` 会**顺着往上找到桌子的 `SnapSurface`** —— 于是物体顶面被当成桌面高度,物体被吸到它上面。命中本身分不清「这是桌面」和「这是站在桌面上的东西」,所以由占位者自己声明,拒在 `SnapSurface.IsBlocked()` 里。

**布置规则(硬性,违反会静默失效)**:

> **`PlacementBlocker` 必须挂在它所占据的那个碰撞体所在节点,或该节点与 `SnapSurface` 之间的任一祖先上。**
> 挂在没有碰撞体的纯视觉子节点上 → 无效。
> 挂在 `SnapSurface` **之上**(标记挂机器根、`SnapSurface` 挂子节点「桌面」) → **无效,原 bug 原样复现**。

走查从命中碰撞体往上走,**走到 `SnapSurface` 所在节点就停**。这是为了保住「大桌子 + 机器只占一格」这种摆法(机器挂 blocker、桌子挂 `SnapSurface`、机器是桌子的子节点)。

### Payload 索引契约 —— 必须定死

`PayloadCatalogue` 的**数组顺序就是契约**,索引在任何地方含义相同。**只能追加,不能重排** —— 重排会改变世界里所有已存在物体和所有已配置字段的含义。

**所有需要索引的地方必须填同一个数**:

| 位置 | 字段 |
|---|---|
| `PaperBox` | `_payloadIndex`(盒子吐出的东西) |
| `Printer` | 纸索引 / 墨索引 / 产物索引 |
| `Object.prefab` | `NetworkGrabbable._catalogue` |
| 各 `ContainerView` | `_catalogue` |
| `DebugHud` | `_catalogue`(可选,不指就显示 `payload N`) |

**没有任何代码层的地方定义「0 是纸」。** 填错的表现是「箱子吐出墨盒」或「打印机不认纸」,而且**要跑起来才发现**,编译期零提示。HUD 是最便宜的验证工具:台面上显示 `纸 x3` 说明索引通了,显示 `payload 0 x3` 说明没通。

### 进料型工位没有主动动词 —— 只能靠物理投喂

**这是接口层面的事实,不是某个工位的实现细节**:

- `PlayerInteraction` 里 E 在**持物时**只走放下/投掷;
- 工位目标只在**没抓到东西**时才会被设置。

所以「拿着纸走到机器前按 E 递给它」**在现有接口下不可能发生**。进料型工位(打印机、将来的「插 U 盘」「放文件」)必须自己盯一个区域,靠物体**落进那个区域**触发。

**推论**:每个进料工位都要自己实现「区域判定 + 拒绝时把东西留在世界上」。第三个出现时应该抽成组件,别各写一遍。

### 只进不出的容器:容量必须无限,否则必须给取回动词 —— W1

输入容器(塞进去办正事的那些)**没有任何取回途径**。所以它一旦容量有限,玩家就能用一个**合法操作**把机器永久卡死:塞满同一种原料 → 缺的那一种进不来 → 配方永远凑不齐 → 而机器从不退还输入。

打印机为此把纸和墨拆成了**两个独立容器**(纸 6 / 墨 1 盒),因为**生命周期不同的资源不该共用一个容器**:纸是「一张一用」,墨是「一盒 8 张」,塞进同一个槽位模型就长出了「纸满了装不进墨」这种荒谬的失败模式。

**这条对将来的文件夹、章笔座、任何「收进去办事」的容器同样成立。**

---

## 三、拆分与文件所有权

| 窗口 | 独占文件 | 依赖 | 状态 |
|---|---|---|---|
| P0 | `Containers/*`、`NetworkGrabbable` 换装、两处硬性修复 | — | **完成** |
| P0.1 | `Stations/StationBase.cs`、`PlayerInteraction` 交互通道 | P0 | **完成** |
| W1 打印机 | `Stations/Printer.cs`、`Stations/PrinterQueue.cs` | P0 + P0.1 | **可开工** |
| W2 原料箱 | `Stations/PaperBox.cs` | P0 + P0.1 | **可开工** |
| W3 吸附阻挡 | `Interaction/PlacementBlocker.cs`、`Interaction/SnapSurface.cs` | 无 | 完成 |
| W4 保洁阿姨 | `Npc/Cleaner.cs` | 无 | 完成 |
| W5 体力 | `Player/PlayerStamina.cs` + `PlayerMovementPrediction.cs`(独占) | 无 | 完成 |
| W6 调试 HUD | `UI/DebugHud.cs` | P0 | 完成 |

**文件所有权是硬性的。** 两个窗口碰同一个文件必然冲突。需要改别人名下的文件,先找核心窗口。
