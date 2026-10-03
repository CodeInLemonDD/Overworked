# 并行窗口开工规格 · 第四轮:需求、客户与交付

**截止 10.7,倒排如下。这一份按「先能玩,再好」写。**

| 日期 | 做什么 |
|---|---|
| **10-04** | 需求 + 客户 + 交付 + 计分 |
| **10-05** | 队伍 + 配额 + 胜负 + 回合结束 |
| **10-06** | 场景布置 + 多人测试 + 修剩下的 + 需求/比分显示 |
| **10-07** | 材质上色 + 收尾 + 发布 |

**不做的**:教程、多张地图、道具系统、章笔、电源分区。

> **交互(已定):** 客户按 E 是**接单**(把要求告诉你);**交付是脱手的文件夹碰到他** —— 和打印机一样。
> 之所以不能按 E 交付,是那条老规矩——**E 在持物时只走放下/投掷**,永远到不了工位。
> 所以客户同时是一个 `StationBase`(管 E)和一个进料工位(管投喂)。
>
> **「脱手」是必要条件**:拿在手上的文件夹蹭到客户**不算**交付。
> `GrabbableSpawner.CollectLooseGrabbables` 已经把「在手上」的排除了 —— 用它,不要自己再判一遍。
>
> **客户用玩家模型顶。** 玩家模型和 NPC 大差不差,先别雕人 —— 那是 10-07 的事。

---

## 通用规则(每个提示词里都已经包含)

- 只改**你名下的文件**。需要动别人的文件,**停下来问**,不要自己改
- 接口不清楚就**先问**
- **交付前必须自己跑一遍离线编译**
- **不要切分支、不要 push。** `git add` 只写你名下的具体路径,不要 `git add -A`

### 离线编译

```bash
T=$(mktemp -d)
dotnet build Assembly-CSharp.csproj -nologo -v:q \
  -p:BaseIntermediateOutputPath="$T/obj/" -p:BaseOutputPath="$T/bin/"
rm -rf "$T"
```

**本轮基线:0 error / 3 warning**(全是 CS0114)。**离线编译不跑编织器** —— `SyncType`、`[ServerRpc]`
那些错误只能在 Unity 里才看得到。

**FishNet 的 API 一律对着包源码写**,包在
`Library/PackageCache/com.firstgeargames.fishnet@12ee279bcfde/`。

---

## 一、这一轮做出来的东西

```
客户站在办公室里(先用玩家模型顶)
玩家走过去按 E          → 客户把要求告诉你:合同 1 · Excel 1
玩家印齐、装进**自己队的**文件夹
玩家把文件夹**丢向他**    → 脱手之后碰到他就算交付
                        → 服务端逐条核对 → 齐了就收下 → 那一队加分 → 客户离开,新客户进场
```

**需求是共享的,不分队。** 一个客户一条需求,**两队都看得见、都能交,谁先交谁得分**。
另一队白做 —— 那几份文档还在他们手上,但那个客户没了。

这不是「两队各自接单」,是**抢单**。所以需求上**没有队伍字段**:它说的是「合同 1」,
两队各自拿自己那份去交。

---

## 二、已定的决定

### ① 文件夹分队,只装同队的资料

**这是为了修一个真 bug**:客户要「合同 1 和 Excel 1」,而一个文件夹里装了 A 队的 Excel 1 和
B 队的合同 1 —— 那这单算谁的?

答案:**文件夹自己属于某一队**,只接受同队的文档,颜色区分。

- **哪一队**:文件夹从箱子里出来时,由按 E 的那个人决定
- **颜色**:文件夹的 payload 挂一个 `PayloadLabel`。`NetworkGrabbable` 上已经有 `_variantTeam`,
  而 `PayloadLabel` 就是拿它上色的 —— 和文档的编号走的是同一套,**不用新做视觉**
- **只装同队**:`FolderIntake` 现在只看「有没有 `DataId`」,要再加一条「队伍对不对」

### ② 文件夹保持「只进不出、无限容量、交货整个消失」

客户**只按他要的那几份判定**,多出来的不认,**文件夹整个消失**。所以正解是**一个订单一个文件夹**。

不做取出动词,也不做容量上限 —— 上限必须配取出动词才不违反「只进不出的容器必须无限」,
而取出动词需要一个 E 之外的新按键,那是另一个决定。

**代价**:玩家看不见文件夹里有几份。这一轮**用 `DebugHud` 顶着**(它会把文件夹里的东西按名字列出来)。

### ③ 队伍分配

- **服务端在玩家生成时按连接顺序轮流分到 0 / 1**
- 控制台的 `team <n>` **留着**当覆盖手段
- **不做**:重连保持、观战、队伍 UI、准备阶段

### ④ 回合:限时,分高者胜

- **场上两个客户**,各自一条需求
- **需求按顺序递增**,不是随机:第 1~3 条要「1 合同 + 1 Excel」,第 4~8 条要「1 合同 + 2 Excel」,
  第 9 条起要「2 合同 + 3 Excel + 1 文档」…… **具体数字在资源里填,这里只是形状**
- **交成一条 10 分**,先交的那队拿,**另一队白做**
- **限时**,时间到分高者胜

> **这推翻了「先到配额」那个设想。** `ScoreBoard` 里是**一个钟**,不是一个目标分 ——
> 「先到 N 分」和「时间内分最高」是两种不同的游戏,后者要有一个服务端权威的倒计时。

> **一个平衡上要留意的地方**(现在不用处理):抢单是赢家通吃,所以一路抢输的那队可能**零分**,
> 而且没有追赶机制。调参的时候再说。

---

### ⑤ 客户耐心:每队一个钟,而且「等待钟」会重启

**初值:等待钟 20 秒,每队耐心钟 60 秒**(序列化字段,别写死)。

这是把「抢单」从「谁手快」变成「谁扛得住」的那条规则。

```
客户出现
  │
  ├─ 没有任何队在做  → 「等待钟」在跑
  │                     到点 → 还没出局的队**都扣 5 分**,客户走
  │                     有队接了 → 这个钟停
  │
  └─ A 队按 E 接了  → A 自己的「耐心钟」从头跑
                       B 后来的话,B 的耐心钟**也从头跑**,不和 A 同步

  某队的钟到点 → State = Failed,扣那队 5 分,那队**再也不能交这个客户**
  所有队都 Failed → 客户走
  有队 Failed、而没有队在做 → **等待钟重启**(见下)
```

**「等待钟」重启是关键的一条。** 不做的话:A 接了、超时了,B 从没接过,于是 B 没有钟 ——
客户就一直站在那儿占着一个位子,而 B 可以无限期地慢慢来。

所以等待钟的准确说法是:**只要还有队没出局、又没有队正在做,它就在跑。**
它不是「客户出现时的钟」,是「客户还没被任何人接手时的钟」。

**接单和交付都要看状态:**

- **没接单 → 不能交。** 否则「不接单就没有钟」会被绕过:没人接就直接丢文件夹,白拿分
- **已出局 → 不能交。** 这就是那条「倒计时结束后不再接收该队任务」
- **两队都出局 → 客户走**,两个人都不交也算

**扣分是 5 分**(交成是 10)。

---

## 三、要抽出来的东西(第三次了)

**交付是第三个盯着盒子看的进料工位**(打印机、文件夹、客户)。
`CONSTRAINTS.md` 写着「第三个出现时应该抽成组件」,上次抽的时候发现**该抽的只有一半**。

抽出来的是**判定**,不是流程:

```csharp
public static class IntakeVolume
{
    /// 把散落在盒子里、且不在任何人手上的可抓物体收进 buffer。
    /// 盒子以 space 的本地坐标表示,所以它会跟着机器转。
    public static void CollectInside(
        NetworkManager manager,
        Transform space,
        Vector3 centre,
        Vector3 halfExtents,
        List<NetworkGrabbable> buffer);
}
```

**打印机和 `FolderIntake` 都要改成调它**(它们现在各写了一遍盒子判定)。
**接不接受仍然是各自的**:打印机看 payload 索引,文件夹看 `DataId` 和队伍,客户看「是不是文件夹」。

---

## 四、已冻结的接口

### `DocumentRequest` —— 一条需求里的一行

```csharp
[System.Serializable]
public struct DocumentRequest
{
    public int RequestId;      // 哪条需求
    public int SpecIndex;      // 要哪个种类
    public int Number;         // 要第几号
}
```

**没有队伍,也没有 `DocumentId`。** 需求说的是「合同 1」这个名字,而**两队各自有自己的一份**;
写 `DocumentId` 就把需求绑死在某一队的那一份上了。

**一条需求 = 一组这样的行**,铺平成「每行一条记录、同 `RequestId` 的算一条」。
只有 public 字段,同 `ContainerEntry` 的规矩。

### `RequestBoard`(NetworkBehaviour,场景里一个)

```csharp
public class RequestBoard : NetworkBehaviour
{
    public static RequestBoard Instance { get; }

    public int Count { get; }                                     // 有几行
    public int RequestCount { get; }                              // 有几条需求
    public bool TryGet(int index, out DocumentRequest request);   // 按行读
    public bool TryGetWanted(int requestId, List<DocumentRequest> buffer);   // 一条需求的全部行

    public event Action RequestsChanged;                          // 本地事件,不过网

    [Server] public int ServerCreate(IReadOnlyList<DocumentRequest> wanted);  // 调用方填 Spec/Number,返回 requestId
    [Server] public bool ServerRemove(int requestId);
}
```

**需求不创建文档。** 客户开口的时候,那些文档**必须已经存在** —— 由客户生成器调
`DocumentStore.ServerCreate` 建好,再拿它们的 `{SpecIndex, Number}` 组一条需求出来。

### `RequestCatalogue`(ScriptableObject)—— 递增的需求序列

```csharp
public class RequestCatalogue : ScriptableObject
{
    public int Count { get; }
    public bool TryGet(int tier, out RequestTier wanted);

    [System.Serializable]
    public struct RequestEntry
    {
        public int SpecIndex;   // 要哪个种类
        public int Count;       // 要几份(展开成 1..Count 号)
    }

    [System.Serializable]
    public struct RequestTier
    {
        public RequestEntry[] Wanted;
    }
}
```

**第 N 条出现的需求,取第 N 层。** 数组顺序就是契约,和另外两张表一样:**只能追加,不能重排**。

**超出表长就重复最后一层** —— 回合是限时的,需求迟早会用完,而「打到最后没有需求了」是个比
「一直要最难的」更糟的结局。

**「合同 ×2」展开成「合同 1、合同 2」**。两队各自有自己那份合同 1 和合同 2,
需求说的是名字,不是某一队的哪一份。

### `Customer`(NetworkBehaviour,场景里一个)

```csharp
public enum CustomerPhase : byte { Idle = 0, Working = 1, Failed = 2 }

public class Customer : StationBase
{
    public int RequestId { get; }

    public int TeamCount { get; }
    public CustomerPhase PhaseOf(int team);
    public float RemainingFor(int team);     // 那个队的耐心钟。没接时是 0
    public float WaitingRemaining { get; }   // 「还没被接手」的钟

    /// 这一摞文档够不够这条需求。**纯判定,不消耗任何东西。**
    public bool Meets(int team, IReadOnlyList<int> documentIds);

    [Server] public bool ServerAccept(int team);                       // 按 E 接单
    [Server] public bool ServerDeliver(int team, IReadOnlyList<int> documentIds);
}
```

**它是场上最有状态的东西** —— 每队一个 `Phase` 和一个钟,外加一个等待钟。
钟由**服务端权威**跑(和 `ScoreBoard` 同一个理由),每秒写几次,客户端显示。

**`Meets` 只回答「够不够」**:扣分、消耗文件夹、删需求、让客户离开,都是调用方的事。
**队伍判定就在它里面** —— 那个文件夹是不是这个队的、里面那几份是不是这个队的。
让它能单独测,是这一轮最容易出错的地方。

**头顶显示的是「你这个队」的钟**,不是统一的 —— 两个玩家看同一个客户,看到的倒计时不一样。

### `ScoreBoard`(NetworkBehaviour,场景里一个)

```csharp
public class ScoreBoard : NetworkBehaviour
{
    public static ScoreBoard Instance { get; }

    public int ScoreOf(int team);
    public float Remaining { get; }   // 秒。回合剩余时间
    public bool IsOver { get; }
    public int Winner { get; }        // -1 = 还没分出来,-2 = 平局

    public event Action ScoresChanged;

    [Server] public void ServerAward(int team, int points);
    [Server] public void ServerReset();       // 分数归零、钟重置

    // 序列化字段:回合时长(秒)、每个队伍的名字/颜色 —— 后者这轮可以不做
}
```

**钟是服务端权威的**,因为「什么时候结束」不能由各端自己算。**服务端每秒写几次 `Remaining`**,
客户端显示它、两次更新之间用自己的 delta 平滑 —— 一个浮点数每秒写几次,和
`Computer` 那个下载进度不是一回事(那个是「不写也能各自算对」,这个不能)。

**`ScoreBoard` 不知道回合怎么跑**,只知道分数和时间。**谁交的货、交给谁,是别人的事。**

---

## 五、窗口

| 窗口 | 独占文件 | 做什么 |
|---|---|---|
| **P0**(核心窗口) | `Documents/DocumentRequest.cs`、`Documents/RequestBoard.cs`、`Documents/RequestCatalogue.cs`、`Stations/ScoreBoard.cs`、`Interaction/IntakeVolume.cs` | 数据层 + 抽出来的判定 + 钟 |
| **W1 客户** | `Npc/Customer.cs`、`Npc/CustomerSpawner.cs`、`Npc/RequestLabel.cs` | 客户实体、生成器、头顶显示 |
| **W2 交付** | `Npc/DeliveryZone.cs`、`Interaction/FolderIntake.cs`、`Interaction/NetworkGrabbable.cs` | 收件区 + 文件夹分队 + 只装同队 |
| **W3 接线** | `Stations/Printer.cs`、`Stations/SupplyBox.cs`、`Interaction/GrabbableSpawner.cs`、`Stations/Computer.cs` | 两个工位改用 `IntakeVolume`;箱子吐出带队的文件夹 |

**P0 先做,做完接口冻结,其余三个再开。**

---

## 六、还没定的

已经定了的:

| | |
|---|---|
| 场上客户 | **两个** |
| 需求 | **按序列递增**(第 1~3 条 1 合同+1 Excel,第 4~8 条 1 合同+2 Excel,……) |
| 交成一条 | **+10** |
| 超时一条 | **−5** |
| 胜负 | **限时,分高者胜** |
| 耐心 | **每队一个钟,等待钟会重启** |

**初值已定(都是调参,开测了再改):**

| | |
|---|---|
| 回合时长 | **5 分钟**(300 秒) |
| 场景客户位 | **4 个**(两个在用、一个在走、一个在来) |
| 等待钟 | **20 秒** |
| 每队耐心钟 | **60 秒** |

**这四个数都要做成序列化字段**,不要写死在代码里 —— 它们是这一轮唯一确定会被反复调的东西。

---

## 附:用户在编辑器里要做的

### 客户**不生成、不销毁** —— 场景里摆几个位子

「客户离开、新客户进场」**不要做成 spawn/despawn**。两个理由:

- **场景 NetworkObject 一旦 `Despawn()` 就退化成 `SetActive(false)`,没有恢复路径** ——
  这条 `CONSTRAINTS.md` 里写着,是踩过的坑
- 做成 spawned prefab 就要进 `DefaultPrefabObjects`,那是编辑器里的一处手工活,这个周末不值得

所以:**场景里摆几个客户位**(各自一个 `NetworkObject`),客户在里面**复用** ——
空闲时走开或站到一边,接到需求时走回来、把要求显示出来,交完货换下一条。
**进出场先做成「挪位置」,不要做成销毁重建。**

### 要摆的东西

| 物体 | 挂什么 |
|---|---|
| `Customer`(×N) | `NetworkObject`(场景物体、**不加 NetworkTransform**)+ `Customer` + 一个碰撞体(E 要能扫到它,**没有碰撞体就按不了 E**)+ 一个挂 `RequestLabel` 的空子节点 |
| `RequestBoard` | `NetworkObject`(场景、不加 NetworkTransform)+ `RequestBoard` |
| `ScoreBoard` | `NetworkObject`(场景、不加 NetworkTransform)+ `ScoreBoard` |

**客户的模型直接拿玩家模型。** 先别雕人。
