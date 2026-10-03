# 并行窗口开工规格 · 第四轮:需求与交付

> **这一份比上一份松,是故意的。** 上一轮的模型在开工后被推翻了一次,原因是把「解锁」的单位
> 搞错了。这一轮的设计**还没有在游戏里验过**,所以下面把「我已经定的」和「还不确定的」分开写。
>
> **开工前先读第一节。如果那两个决定你不同意,整份规格都要重写。**

---

## 一、这一轮做出来的东西

**客户站在办公室里,头顶写着要什么;玩家把文件夹丢给他,他收下或拒收,分数动一下。**

```
客户1 站在那       头顶:合同 1 · Excel 1 · Excel 2
玩家               印齐三份 → 装进文件夹 → 走到客户1 面前 → 把文件夹丢给他
客户1              逐条核对 → 齐了就收下,不齐就退回来(文件夹留在原地)
分数               动一下
```

**这一轮不做计分板和回合结束。** 只做到「分数动一下」—— 分数记在哪、怎么显示、什么时候算赢,
是下一轮的事。理由见第六节。

---

## 二、两个我已经定的决定(不同意就说)

### ① 文件夹保持现状,代价是「多装的会一起没了」

`CONSTRAINTS.md` 里那个问题——「无限容量 + 只进不出 + 交货后整个消失,意味着一次交货会把文件夹里
所有东西一起带走」——**我的答案是:就这样。**

- 客户**只按他要的那几份判定**,多出来的不认
- **文件夹整个消失**,里面的东西一起没
- 所以玩家的正确做法是**一个订单一个文件夹**

**不做取出动词,也不做容量上限。** 上限需要取出动词才不违反「只进不出的容器必须无限」,
而取出动词需要一个 E 之外的新按键 —— 那是另一个决定,不该混在这一轮里。

**代价是玩家看不见文件夹里有几份。** 这一轮**用 `DebugHud` 顶着**(它现在会把文件夹里的东西
按名字列出来)。真正的世界内显示是单独一件,见第六节。

### ② 队伍分配提前进来,但只做最小的一份

计分要按队,而现在 `team` 只是我给的一个控制台口子。所以这一轮加上:

- **服务端在玩家生成时按连接顺序轮流分到队伍 0 / 1**
- 控制台的 `team <n>` **留着**,作为覆盖手段
- **不做**:重连保持、观战、队伍 UI、开局准备阶段

理由:没有真的分队,「两队各自推进」这条就只是纸上的,而它是这一轮之前所有设计的前提。

---

## 三、这一轮要抽出来的东西(第三次了)

**交付靠物理投喂,这是第三个进料工位。**

打印机(第一)、文件夹(第二)各自实现了「盯着一个盒子,看有没有东西落进去」。
`CONSTRAINTS.md` 写着「第三个出现时应该抽成组件」。

**但上次抽的时候发现,该抽的只有一半。** 抽出来的是:

```csharp
// 一个纯函数,不是组件。盒子多大、接不接受,仍然是每个工位自己的事。
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

**打印机和文件夹都要改成调它**(它们现在各写了一遍盒子判定)。
**不接受时的处理仍然是各自的**:打印机看索引,文件夹看 `DataId`,客户看「是不是文件夹」。

> 不在窗口里改 `Printer` / `FolderIntake` 之外的地方。**这次抽的是判定,不是流程。**

---

## 四、已冻结的接口(写窗口之前要先确认)

### `DocumentRequest` —— 一条需求

```csharp
[System.Serializable]
public struct DocumentRequest
{
    public int RequestId;      // 哪条需求
    public int DocumentId;     // 它要的一份文档
    public int Team;           // 哪一队的需求
}
```

**一条需求 = 一组文档**,而一组没有固定长度,所以铺平成「每份一行、同 `RequestId` 的算一条」。
和 `ContainerEntry` / `DocumentRecord` 同一条规矩:**只有 public 字段。**

### `RequestBoard`(NetworkBehaviour,场景里一个)

```csharp
public class RequestBoard : NetworkBehaviour
{
    public static RequestBoard Instance { get; }

    public int Count { get; }                                        // 有几条需求
    public bool TryGet(int index, out DocumentRequest request);      // 按行读

    [Server] public int ServerCreate(int team, IReadOnlyList<int> documentIds);   // 返回 requestId
    [Server] public bool ServerRemove(int requestId);

    public event Action RequestsChanged;   // 本地事件,不过网
}
```

**和 `DocumentStore` 的关系**:需求**点名**文档,所以 `ServerCreate` 之前那些文档必须已经存在
(`DocumentStore.ServerCreate`)。**需求不创建文档。**

### `Customer`(NetworkBehaviour,场景里一个,自带模型)

```csharp
public class Customer : NetworkBehaviour
{
    public int RequestId { get; }          // 它负责哪条需求

    public bool Meets(IReadOnlyList<int> documentIds);   // 这一摞够不够
}
```

**`Meets` 是纯判定,不消耗任何东西** —— 扣分、消耗文件夹、删需求都是调用方的事。
让它能单独测,是这一轮最容易出错的地方。

---

## 五、窗口(等第一节确认之后再开)

| 窗口 | 独占文件 | 做什么 |
|---|---|---|
| **P0** | `Documents/DocumentRequest.cs`、`Documents/RequestBoard.cs`、`Interaction/IntakeVolume.cs` | 数据层 + 抽出来的判定 |
| **W1** | `Npc/Customer.cs`、`Npc/RequestLabel.cs` | 客户实体 + 头顶的需求显示 |
| **W2** | `Interaction/DeliveryZone.cs`(新,挂在客户身上) | 文件夹落进来 → 核对 → 消耗 → 报告 |
| **W3** | `Player/TeamAssignment.cs`、`Stations/Printer.cs`、`Interaction/FolderIntake.cs` | 分队 + 两个工位改用 `IntakeVolume` |

**这些名字和边界都还是暂定的**,P0 之后我会把确切的文件和提示词写进这一份。

---

## 六、明确不做的(以及为什么)

| 不做 | 为什么 |
|---|---|
| **计分板 / 配额 / 回合结束** | 用户原本把它们和交付排在同一天。**那是一天的量装两天的事。** 先把「递过去 → 分数动一下」跑通,计分和胜负下一轮 |
| **文件夹的世界内存量显示** | 现在唯一的出路是动 `Object.prefab` 的 `_payloadRoot`(见 `DEVELOPMENT.md` 的坑表),那是结构改动,要单独评估回退 |
| **取出动词 / 文件夹容量** | 见第二节 ① |
| **多种客户 / 需求难度** | 先一个客户、一条需求,把链路跑通 |
| **重连保持队伍** | 见第二节 ② |

---

## 七、还不确定的(明天开工前问用户)

1. **客户怎么被触发给出需求?** 开局就有?还是玩家走过去按 E?
   —— 按 E 的话,客户必须是个 `StationBase`,而 E 在持物时走不通(那条老规矩),
   所以「走过去按 E」和「把文件夹丢给他」是两种不同的交互,要能同时存在
2. **一条需求完成之后,客户干什么?** 消失?站在原地等新需求?还是这一局就这一条?
3. **需求对两队镜像是什么意思?** 两队各有自己的客户和自己的需求,还是同一批需求两队抢?
   —— 「红队的 Excel 1 和蓝队的 Excel 1 是两份」说明是**各自的**,
   但那就意味着每个客户属于某一队,而办公室里的人是看得见的,对方能看见你的客户吗?
