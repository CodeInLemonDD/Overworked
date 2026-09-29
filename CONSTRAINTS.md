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
