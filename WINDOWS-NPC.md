# 并行窗口开工规格 · 第四轮:需求、客户与交付

**截止 10.7,倒排如下。这一份按「先能玩,再好」写。**

| 日期 | 做什么 |
|---|---|
| **10-04** | 需求 + 客户 + 交付 + 计分 |
| **10-05** | 队伍 + 配额 + 胜负 + 回合结束 |
| **10-06** | 场景布置 + 多人测试 + 修剩下的 + 需求/比分显示 |
| **10-07** | 材质上色 + 收尾 + 发布 |

**不做的**:教程、多张地图、道具系统、章笔、电源分区。

> **一条待确认(开工前问):** 客户按 E 是**接单**(把要求告诉你),交付是把文件夹**丢进他身边的收件区**。
> 之所以不能按 E 交付,是那条老规矩——**E 在持物时只走放下/投掷**,永远到不了工位。
> 所以客户同时是一个 `StationBase`(管 E)和一个进料工位(管投喂),和打印机一样。

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
客户站在办公室里
玩家走过去按 E          → 客户把要求告诉你:合同 1 · Excel 1
玩家印齐、装进**自己队的**文件夹
玩家把文件夹丢进客户身边的收件区
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

### `Customer`(NetworkBehaviour,场景里一个)

```csharp
public class Customer : StationBase
{
    public int RequestId { get; }

    /// 这一摞文档够不够这条需求。**纯判定,不消耗任何东西。**
    public bool Meets(IReadOnlyList<int> documentIds);
}
```

`Meets` 只回答「够不够」:**扣分、消耗文件夹、删需求、让客户离开,都是调用方的事。**
让它能单独测,是这一轮最容易出错的地方 —— 队伍判定就在它里面。

### `ScoreBoard`(NetworkBehaviour,场景里一个)

```csharp
public class ScoreBoard : NetworkBehaviour
{
    public static ScoreBoard Instance { get; }

    public int ScoreOf(int team);
    public int Quota { get; }
    public bool IsOver { get; }
    public int Winner { get; }        // -1 = 还没分出来,-2 = 平局

    public event Action ScoresChanged;

    [Server] public void ServerAward(int team, int points);
    [Server] public void ServerReset();
}
```

---

## 五、窗口

| 窗口 | 独占文件 | 做什么 |
|---|---|---|
| **P0**(核心窗口) | `Documents/DocumentRequest.cs`、`Documents/RequestBoard.cs`、`Stations/ScoreBoard.cs`、`Interaction/IntakeVolume.cs` | 数据层 + 抽出来的判定 |
| **W1 客户** | `Npc/Customer.cs`、`Npc/CustomerSpawner.cs`、`Npc/RequestLabel.cs` | 客户实体、生成器、头顶显示 |
| **W2 交付** | `Npc/DeliveryZone.cs`、`Interaction/FolderIntake.cs`、`Interaction/NetworkGrabbable.cs` | 收件区 + 文件夹分队 + 只装同队 |
| **W3 接线** | `Stations/Printer.cs`、`Stations/SupplyBox.cs`、`Interaction/GrabbableSpawner.cs`、`Stations/Computer.cs` | 两个工位改用 `IntakeVolume`;箱子吐出带队的文件夹 |

**P0 先做,做完接口冻结,其余三个再开。**

---

## 六、还没定的(明天开工前问用户)

1. **客户生成器从哪来?** 场景里摆几个位置轮流用?还是随机?一次场上几个客户?
2. **一条需求几份文档?** 固定 2 份还是 1~4 随机?
3. **配额多少?** 「先到 N 分」的 N。
4. **客户离开了还会回来吗?** 还是源源不断换新的直到回合结束?
