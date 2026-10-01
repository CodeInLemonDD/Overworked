# 并行窗口开工规格 · 第二轮:文档数据层

每个窗口的提示词都是**整段可复制**的。复制时把该窗口那一段整个贴进去。

---

## 通用规则(每个提示词里都已经包含)

- 只改**你名下的文件**。需要动别人的文件,**停下来问**,不要自己改
- 接口不清楚就**先问**。已冻结的接口就是用来防止各写各的
- **交付前必须自己跑一遍离线编译。** 项目没有 asmdef,单程序集 —— 你写出编译错误,所有窗口都跑不起来

### 离线编译:不要等用户切焦点

```bash
T=$(mktemp -d)
dotnet build Assembly-CSharp.csproj -nologo -v:q \
  -p:BaseIntermediateOutputPath="$T/obj/" -p:BaseOutputPath="$T/bin/"
rm -rf "$T"
```

- **csproj 是 Unity 聚焦时生成的快照。** 新文件不在 `Compile Include` 里时会报「找不到类型」这种
  **假错误** —— 手动补一行再编,别去信它
- 它会**连别人的文件一起编**,所以顺带是一次全项目冒烟检查
- **`CS0114`(`Awake` 隐藏基类成员)与 `CS0649`(`[SerializeField] private` 从未赋值)是本项目基线,
  不要"修"** —— 见 `CONSTRAINTS.md` #15
- 这**不能替代**真机 MPPM 测试,只是把编译错误挡在交付之前

**每轮开工先跑一遍确认基线干净,再动自己的文件。** 本轮基线:**0 error / 3 warning**(全是 CS0114)。

### 本轮的额外一条

**FishNet 的 API 一律对着包源码写,不要凭记忆。** 包在
`Library/PackageCache/com.firstgeargames.fishnet@12ee279bcfde/`。
本轮第一次用到 `TargetRpc`,而 `CONSTRAINTS.md` 里只写了 `ServerRpc` 那条 ——
**自己去看 `Attributes.cs` 确认签名要求**。

---

## 排期与依赖

| 窗口 | 独占文件 | 依赖 | 什么时候能开 |
|---|---|---|---|
| **P0 数据层**(核心窗口) | `Documents/**`、`NetworkGrabbable.cs` | — | **立刻** |
| **W1 打印机** | `Stations/Printer.cs`、`Stations/PrinterDisplay.cs` | P0 | P0 之后 |
| **W2 电脑工位** | `Stations/Computer.cs`(新)、`UI/ComputerPanel.cs`(新)、`PlayerInteraction.cs` | P0 | P0 之后 |
| **W3 控制台** | `Dev/DevConsole.cs` | P0 | P0 之后 |
| **W4 纸箱与仪表** | `UI/ContainerGauge.cs`(新)、`Stations/PaperBox.cs` | **无** | **立刻** |

**`DebugHud` 不是窗口** —— 代码已经写完了,只差放进场景。接线清单见本文末尾。

---

## 一、这一轮在做的事(每个窗口都先读这段)

### 链条

```
电脑(选一份文档) ──→ 文档进入打印机的【任务队列】
                          │
        ┌─────────────────┴─────────────────┐
        │  纸 + 墨 + 队列里的一份文档        │  ← 打印机现在的三个条件
        └─────────────────┬─────────────────┘
                          ↓
                  产出【带编号的纸】堆在机器上
                          ↓
                     拿在手里(编号还在)
```

现在断在哪里:**打印机产出时,输出条目里没有编号**。所以堆上永远是模板那个空白。
实测发现的那个「文件上的字并没有显示」,就是这一段。

### 一份文档是什么

| 什么 | 例子 | 放在哪 | 会长吗 |
|---|---|---|---|
| **种类** | Excel / 合同 / 图片 / 文章 | **payload 索引**(外观模板) | 固定几种 |
| **队伍** | A / B —— 只差文字颜色 | 数据 | 固定两个 |
| **编号** | Excel 1、Excel 2… | 数据 | **每局都长** |
| **来源** | 后台文件 / Internet | 数据 | 固定两个 |

**编号绝不能烘进 prefab**(那会变成 `Excel 1.prefab`、`Excel 2.prefab`… 而编号没有上限)。
这条上一轮已经定死了,这一轮只是把它落到运行时的数据上。

### 关键决定:文档在容器里是 `Data` 条目,不是 `Entity`

`ContainerEntry` 上一轮就留好了口子:

```csharp
public enum ContainerEntryKind : byte { Empty = 0, Entity = 1, Data = 2 }
// DataId 字段和 ForData() 工厂都在,但至今没有一个调用点
```

**为什么不让 `Entity` 条目也带编号?** 因为那样「一份文档」就有两种说法 ——
`Entity{PayloadIndex=2, 编号=3}` 和 `Data{DataId=7}` 指的是同一件事。
**一个东西只能有一种表示**,否则两个窗口迟早会各写一种。
所以:**原料(纸、墨)= `Entity`,文档 = `Data`。** 没有例外。

### 连带的一条:实体化的文档要记得自己是谁

一张纸被拿走以后是一个 `NetworkGrabbable`,它带着 `PayloadIndex` + `VariantNumber` + `VariantTeam` ——
外观和编号都在。**但它不知道自己对应库里哪一条**(没有 `DataId`)。
于是把它放进任何一个容器(将来的文件夹)时,只能写成 `Entity` 条目,**编号就丢了**。

所以 P0 会给 `NetworkGrabbable` 加一个 `DataId`:

```csharp
public int DataId { get; }             // -1 = 不是文档(纸、墨)
public void ServerSetDataId(int id);
```

有了它,「手里的文档 → 文件夹」这条路才是无损的。文件夹这一轮不做,但**口子今天就要留对**。

### 打印机的「任务队列」就是一个普通容器

不新造队列类型。打印机已经有三个容器了(纸 / 墨 / 产出),**第四个是任务队列**,
里面装的就是 `Data` 条目。理由和上一轮一样:**容器就是容器**。

- 电脑往里面 `ServerTryAdd(ContainerEntry.ForData(docId))`
- 打印机开工时 `ServerTryRemoveFirst()` —— **FIFO,先到先打**
- 上一轮那个按玩家轮转的 `PrinterQueue` **不要复活**。它的前提是「输入条目带所有者」,
  而原材料是共享池,没有所有者可轮 —— 那个设计已经被否掉了,代码还在 git 历史里,别去挖

**一个已知的、暂时接受的问题**:队列满了以后,玩家可以合法地把机器堵住(塞满自己的任务)。
能恢复(打完就通了),不是死锁,但这是个**抢机器的玩法**,先记着,这轮不处理。

---

## 二、已冻结的接口(P0 产出)

命名空间 `Overworked.Documents`。**依赖它们的模块按这些签名写,不要另起一套。**

### `DocumentRecord`(struct)

```csharp
[System.Serializable]
public struct DocumentRecord
{
    public int PayloadIndex;   // 外观模板,指向 PayloadCatalogue
    public int Number;         // 编号,从 1 开始
    public int Team;           // 队伍,-1 = 不上色
    public int Source;         // 0 = 后台文件, 1 = Internet
}
```

**和 `ContainerEntry` 同一条规矩**:只有 public 字段,没有属性,没有基类。
FishNet 的编织器按字段生成序列化器,**私有字段会被静默跳过** —— 能编译、能跑、就是过不了网。

### `DocumentCatalogue`(ScriptableObject)

**可获取的文档规格表**,不是已存在的文档。和 `PayloadCatalogue` 同一个套路,数组顺序就是契约。

```csharp
public class DocumentCatalogue : ScriptableObject
{
    public int Count { get; }
    public bool TryGet(int index, out Spec spec);

    [System.Serializable]
    public struct Spec
    {
        public string DisplayName;   // "合同" / "Excel" / "图片" / "文章"
        public int PayloadIndex;     // 外观模板
        public int Source;           // 0 = 后台文件, 1 = Internet
        public float FetchSeconds;   // 获取耗时;后台文件填 0
    }
}
```

`FetchSeconds` 就是「数据获取」的成本 —— 后台文件立刻拿到,Internet 的要等。
**这只是个字段,这一轮不实现计时**(见 W2 的范围)。

### `DocumentStore`(NetworkBehaviour)

场景里一个 NetworkObject 上挂一个。**服务端分配,客户端只读。**

```csharp
public class DocumentStore : NetworkBehaviour
{
    public static DocumentStore Instance { get; }   // 找场景里那一个;没有则为 null

    public int Count { get; }                        // 已创建的文档数
    public bool TryGet(int id, out DocumentRecord record);

    [Server] public int ServerCreate(int payloadIndex, int team, int source);  // 返回新 id
}
```

**id 就是下标,只能追加,这一局内不删除。** 用 `SyncList<DocumentRecord>` ——
和 `ContainerBase` 用 `SyncList<ContainerEntry>` 是同一个理由,已经在跑,已知可行。

**编号在服务端算**:扫一遍已有记录,取同类里最大的 `Number` 加一。
不另存计数器 —— 少一份要在开局重置的状态,也就少一个忘记重置的 bug。

### `NetworkGrabbable` 的新增部分

```csharp
public int DataId { get; }              // -1 = 不是文档
public void ServerSetDataId(int id);
```

**必须和 `ServerSetPayload` / `ServerSetVariant` 一样在 `Spawn()` 之前设** ——
`SyncVar.OnChange` 不为初值触发,之后设会变成一次「变更」,两端会看到它先出现再纠正。

### 已有的、本轮要用的接口(别改签名)

```csharp
// ContainerBase
[Server] public bool ServerTryAdd(ContainerEntry entry);
[Server] public bool ServerTryRemoveFirst();      // 队列
[Server] public bool ServerTryRemoveLast();       // 栈
public bool TryGetEntry(int index, out ContainerEntry entry);

// NetworkGrabbable
public void ServerSetVariant(int number, int team);

// PayloadLabel(挂在 payload prefab 上)
public void SetVariant(int number, int team);

// GrabbableSpawner
public static NetworkObject SpawnGrabbable(
    int payloadIndex, Vector3 position, Quaternion rotation,
    NetworkConnection owner = null, int variantNumber = -1, int variantTeam = -1);

// StationBase
protected abstract void OnServerInteract(PlayerInteraction player, NetworkConnection conn, bool longPress);
```

---

## 三、审出来的、本轮必须知道的四件事

审 W1–W6 全程只发现这几个坑。其余部分(遍历 `Spawned` 前先快照、`asServer` 守卫、
`[Server]` 不标抽象方法、没有 `SyncList` 下标直改)都守住了,**别去动它们**。

### ① `PrinterDisplay` 认的是「外观」不是「身份」—— 本轮第一个真 bug

```csharp
// PrinterDisplay.cs:170
if (_slots[i] == null || _shown[i] == entry.PayloadIndex)
    continue;
```

槽位只在 `PayloadIndex` 变化时才重画。而 **Excel 1 和 Excel 2 是同一个 `PayloadIndex`** ——
所以堆里从「Excel 1」换成「Excel 2」时,**它什么都不会做**,编号停在旧的。
`_shownPrinting` 有同样的问题。

这条单独拎出来,是因为**不修的话后面所有验证都会得出错误结论**:你会看到编号没变,
然后去查数据层,而问题在显示层。

### ② 打印机取走一份产出时,编号和队伍没传下去

`Printer.cs:642` 现在只传了 payload。要改成从库里查记录,把 `Number` 和 `Team` 一起传给
`SpawnGrabbable` 的 `variantNumber` / `variantTeam`。

### ③ `PayloadLabel.SetVariant` 已经能用了,但 `PrinterDisplay` 从来没调过

`ReplaceContent()` 造出副本以后,要找到它身上的 `PayloadLabel` 并调 `SetVariant(number, team)`。
**没有 `PayloadLabel` 的 payload 跳过就行,不是错误。**

### ④ `TargetRpc` 本项目还没用过 —— W2 是第一个

`PlayerInteraction.cs:936` 有一个现成的例子可以照着看。**但签名要求去 `Attributes.cs` 确认**,
不要照着本文的描述写。

---

## 四、后面几轮才做、但这轮别做坏的东西

- **文件夹**:装订多份文档。它靠的就是 `NetworkGrabbable.DataId` 那条回路
- **NPC 需求与任务板**:需求指向一份文档 = `{种类, 编号}`。**所以编号是需求的核心,不能糊弄**
- **电源分区**:机器要被断电,`Computer` 和 `Printer` 都要接
- **指示灯 / 滚动条**:墨量、纸量、队列长度。W4 先做容器通用的那个

---

# P0 · 数据层地基(核心窗口写)

> 这个不派给窗口。接口一旦分叉,后面四个全废。

**产出**:`Assets/Scripts/Documents/` 三个文件 + `NetworkGrabbable` 的 `DataId`。

1. `DocumentRecord.cs` —— 上面那个 struct
2. `DocumentCatalogue.cs` —— ScriptableObject,带 `[CreateAssetMenu]`
3. `DocumentStore.cs` —— NetworkBehaviour,`SyncList<DocumentRecord>` + `Instance` + `ServerCreate`
4. `NetworkGrabbable` 加 `DataId`(照 `ServerSetVariant` 的样子写,注意**别标 `[Server]`** ——
   那个方法故意没标,原因见 `CONSTRAINTS.md` #5)
5. 跑离线编译,确认还是 0 error

**P0 完成后接口冻结,核心窗口退回策划与审核。**

---

# W1 · 打印机

```
你负责【打印机】这一块。项目是 Unity 6000.6.0f1 + FishNet 4.7.3 的 2v2 办公室 PVP 游戏。

## 先读这些(按顺序)

1. CONSTRAINTS.md —— 硬约束和已冻结接口,改代码前必读
2. WINDOWS-DATA.md 的「一、这一轮在做的事」和「二、已冻结的接口」
3. DEVELOPMENT.md —— 为什么这么设计、踩过的坑
4. 你自己的文件:Assets/Scripts/Stations/Printer.cs、PrinterDisplay.cs

## 通用规则

- 只改你名下的文件:Stations/Printer.cs、Stations/PrinterDisplay.cs。需要动别人的文件停下来问
- 接口不清楚先问,不要自己造
- 交付前必须自己跑离线编译(命令见 WINDOWS-DATA.md)

## 你的任务

打印机现在产出的是 Entity 条目,里面没有编号,所以堆上的纸永远是空白。
把它改成走文档数据层。

### 1. 加第四个容器:任务队列

- 类型 ContainerBase,挂在打印机 prefab 上(和纸 / 墨 / 产出并列)
- 里面装 Data 条目(ContainerEntry.ForData(docId))
- 默认容量建议 4~6,序列化字段,Inspector 可调
- **加 OnValidate 检查**:这个槽位必须接上,而且不能和另外三个接成同一个
  —— Printer.OnValidate 里已经有这类检查,照着加

### 2. TryBeginCraft 要凑齐三个条件

现在检查「有纸 && 有墨」,改成「有纸 && 有墨 && 队列非空」。
三个都在**动手之前**检查 —— 先扣了纸才发现队列是空的,等于白烧一张纸。

### 3. 打印中的是哪一份,要同步出去

现在是 SyncVar<int> _printingPayload。需要让客户端知道「正在打的是哪一份文档」,
因为 PrinterDisplay 要据此显示编号。加一个 _printingDocument(SyncVar<int>,DataId)。

注意:先读 Printer.cs:555-590 那段注释再动手。那里解释了为什么
「Printing N → 下一条过渡的条件是 Printing != N 而不是 == 0」——
「先写 0 再写下一条」会在同一包里合并发出,客户端采样时只看到后一个。
**同样的陷阱对 _printingDocument 也成立,别把它清成 0 再写新值。**

### 4. FinishCraft 产出 Data 条目

现在:ContainerEntry.ForEntity(_outputPayloadIndex)
改成:ContainerEntry.ForData(刚打完那份的 DataId)

### 5. 取走一份产出时,编号要跟着走

Printer.cs:642 附近。现在只传 payload,改成:
  - 先 TryGetEntry 拿到 DataId
  - 从 DocumentStore.TryGet 查到记录
  - SpawnGrabbable(record.PayloadIndex, ..., variantNumber: record.Number, variantTeam: record.Team)
  - 再 ServerSetDataId(dataId) —— 必须在 Spawn 之前设

注意 SpawnGrabbable 现在只接 payload 和 variant 两个参数,接不了 DataId。
**要么在 Printer 里拿到 nob 之后、Spawn 之前自己补一句,要么问核心窗口要不要扩签名 ——
不要自己改 GrabbableSpawner.cs,那是别人的文件。**

三个 ServerSet* 都必须在 manager.ServerManager.Spawn() 之前调用。
SyncVar.OnChange 不为初值触发,之后设会变成一次「变更」,
两端会看到物体先以错的样子出现再自己纠正。

### 6. PrinterDisplay 要认「身份」不只认「外观」 —— 本轮第一个真 bug

PrinterDisplay.cs:170:
    if (_slots[i] == null || _shown[i] == entry.PayloadIndex) continue;

Excel 1 和 Excel 2 的 PayloadIndex 相同,所以换一份文档时它什么都不做,编号停在旧的。
_shownPrinting 有同样的问题。两个都要改成认 entry.DataId。

然后在 ReplaceContent 造出副本之后,找到它身上的 PayloadLabel 调 SetVariant(number, team)。
没有 PayloadLabel 的 payload 跳过,不是错误。

### 7. 取走的路径要一起改

现在产出是 Entity,PrinterDisplay 读 entry.PayloadIndex 就能直接画。
改成 Data 之后 PayloadIndex 是 -1,**必须先 DocumentStore.TryGet(entry.DataId) 查到记录**,
再用 record.PayloadIndex 去 catalogue 取 prefab。

Store 还没同步好(或 TryGet 失败)时,**保留槽位里原有的东西,不要清空** ——
和现在「catalogue 答不出来就留着占位」是同一个处理方式。

## 交付

- 离线编译通过(0 error;3 个 CS0114 是基线,不要修)
- 一份 Markdown 交付说明:你做了什么、自己决定了什么、认为哪里可能不对
- 需要用户在编辑器里做的接线,列成清单(哪个 prefab、哪个组件、哪个字段填什么)
- **不要碰场景和 prefab 文件** —— 那是 YAML,只能有一个窗口碰。需要改就写进接线清单
```

---

# W2 · 电脑工位

```
你负责【电脑工位】这一块。项目是 Unity 6000.6.0f1 + FishNet 4.7.3 的 2v2 办公室 PVP 游戏。

## 先读这些(按顺序)

1. CONSTRAINTS.md —— 硬约束和已冻结接口,改代码前必读
2. WINDOWS-DATA.md 的「一、这一轮在做的事」和「二、已冻结的接口」
3. DEVELOPMENT.md —— 为什么这么设计、踩过的坑
4. 参考 Assets/Scripts/UI/DebugHud.cs —— 本项目「整个界面用代码搭」的样板
5. 参考 Assets/Scripts/Stations/Printer.cs —— 一个完整的工位长什么样
6. 参考 Assets/Scripts/Interaction/PlayerInteraction.cs:936 —— 本项目唯一一个 TargetRpc

## 通用规则

- 只改你名下的文件:Stations/Computer.cs(新)、UI/ComputerPanel.cs(新)、PlayerInteraction.cs。
  需要动别人的文件停下来问
- 接口不清楚先问,不要自己造
- 交付前必须自己跑离线编译(命令见 WINDOWS-DATA.md)

## 你的任务

电脑是「数据获取」的入口:玩家在电脑上挑一份文档,选一台打印机,把它送进那台机器的任务队列。

### 1. Computer.cs —— 一个 StationBase

照 Printer.cs 的结构写:
- 继承 StationBase,实现 OnServerInteract
- 需要拿到 DocumentCatalogue(序列化字段)
- 需要能列出场景里所有的 Printer(服务端 FindObjectsByType)

OnServerInteract 本身**不创建任何东西** —— 它只负责把「这台电脑的面板该打开了」告诉
按 E 的那个客户端。真正的选择在面板上做。

### 2. 打开面板是一条服务端 → 客户端的消息

**这是本项目第一次用 TargetRpc。** PlayerInteraction.cs:936 有一个现成的例子。
**签名要求去 Library/PackageCache/com.firstgeargames.fishnet@12ee279bcfde/ 里的 Attributes.cs 确认,
不要凭记忆写。** CONSTRAINTS.md 只写了 ServerRpc 那条约束,TargetRpc 没有。

ServerRpc 和 TargetRpc 是反的:
- ServerRpc(客户端 → 服务端)**默认要求拥有权** —— 所以不能声明在无主的场景工位上
- TargetRpc(服务端 → 指定客户端)**发给你指定的那条连接**,不受拥有权限制,可以声明在工位上

### 3. ComputerPanel.cs —— 面板本体

**整个界面用代码搭**,和 DebugHud.cs 一样。理由也是一样的:场景和 prefab 是 YAML,
无法合并,只能有一个窗口碰 —— 代码搭就没有这个问题,用户只要挂一个组件。

面板要有:
- 一个可获取的文档列表(来自 DocumentCatalogue),显示 DisplayName、来源是后台还是 Internet
- 一个打印机选择(场景里所有 Printer 的列表)
- 一个关闭方式

**遵守 CONSTRAINTS.md 约定 C**:自建 UI 必须 sortingOrder >= 1、**不挂 GraphicRaycaster**、
所有 graphic 的 raycastTarget = false。否则会吃掉 FishNet demo 左上角 Host/Client 按钮的点击,
而 MPPM 测试就靠那两个按钮。DebugHud.cs 开头的注释解释了这三条为什么都在。

**输入**:本项目是 activeInputHandler: 1(独占),**OnGUI 收不到键盘输入**(约定 E)。
面板如果要文本输入,字符从 Keyboard.current.onTextInput 收。鼠标点击在 Canvas 上是正常的。

### 4. 玩家选中之后的那条路

面板上点「打印到 #1」之后:
1. 客户端 → 服务端的调用。**ServerRpc 必须声明在 PlayerInteraction 上**(约定 #4:
   默认 RequireOwnership,无主场景对象上的会被直接拒收)
2. 服务端:DocumentStore.ServerCreate(spec.PayloadIndex, team, spec.Source) 拿到 id
3. 服务端:把那台打印机的任务容器 ServerTryAdd(ContainerEntry.ForData(id))

队伍这一轮**先硬编码成 0**,加个注释说明「现在所有玩家都在队伍 A,分队伍那一轮再改」。
不要自己发明队伍分配。

### 5. 这轮不做

- **FetchSeconds 的计时不实现。** DocumentCatalogue 里有这个字段,但「Internet 的要等一会儿」
  这轮先当 0 处理。写个注释说明这里将来要接计时
- **不要做队列满的提示**。ServerTryAdd 返回 false 就当没发生
  (和 PaperBox 空了的处理一样是静默的)

## 交付

- 离线编译通过(0 error;3 个 CS0114 是基线,不要修)
- 一份 Markdown 交付说明:你做了什么、自己决定了什么、认为哪里可能不对
- 需要用户在编辑器里做的接线,列成清单。**Computer 需要一个 prefab**(自带模型桌子,和其它工位一样)
- **不要碰场景和 prefab 文件**
```

---

# W3 · 控制台

```
你负责【调试控制台】这一块。项目是 Unity 6000.6.0f1 + FishNet 4.7.3 的 2v2 办公室 PVP 游戏。

## 先读这些(按顺序)

1. CONSTRAINTS.md —— 硬约束和已冻结接口,改代码前必读
2. WINDOWS-DATA.md 的「一、这一轮在做的事」和「二、已冻结的接口」
3. 你自己的文件:Assets/Scripts/Dev/DevConsole.cs

## 通用规则

- 只改你名下的文件:Dev/DevConsole.cs。需要动别人的文件停下来问
- 交付前必须自己跑离线编译(命令见 WINDOWS-DATA.md)

## 你的任务

电脑面板做完之前,验证文档链路的唯一手段就是控制台。
**这个窗口的价值全在于「让 W1 能被测」**,所以优先级是命令能用,不是命令好看。

### 要加的命令

沿用它现有的风格(坐标是格子、Tab 补全、↑↓ 历史、_commandsEnabled 门控):

- `document <spec> [队伍]` —— 从 DocumentCatalogue 造一份文档,打印出它的 id 和编号
- `queue <打印机序号> <文档id>` —— 把那份文档塞进某台打印机的任务队列
- `docs` —— 列出已经造出来的所有文档(id / 种类 / 编号 / 队伍)
- `printers` —— 列出场景里的打印机和它们各自队列里有几份

`document` + `queue` 两条加起来要能替代电脑面板的完整流程,这样 W1 不需要等 W2。

### 沿用现有约定

- **payload 用 PayloadCatalogue 索引,文档规格用 DocumentCatalogue 索引** —— 和现有的
  `spawn entity` 一样,**不要自己另立一张表**。控制台和游戏不可能对「0 是什么意思」产生分歧
- 只能在服务端跑,并且受 _commandsEnabled 门控
- 补全候选要从 DocumentCatalogue 现取,不要硬编码

## 交付

- 离线编译通过(0 error;3 个 CS0114 是基线,不要修)
- 一份 Markdown 交付说明:加了哪些命令、怎么用、自己决定了什么
- 不需要用户做任何编辑器操作(控制台已经接进场景了)
- **不要碰场景和 prefab 文件**
```

---

# W4 · 纸箱与仪表

```
你负责【纸箱与容器仪表】这一块。项目是 Unity 6000.6.0f1 + FishNet 4.7.3 的 2v2 办公室 PVP 游戏。

## 先读这些(按顺序)

1. CONSTRAINTS.md —— 硬约束和已冻结接口,改代码前必读
2. WINDOWS-DATA.md 的「一、这一轮在做的事」
3. DEVELOPMENT.md
4. 你自己的文件:Assets/Scripts/Stations/PaperBox.cs
5. 参考 Assets/Scripts/UI/DebugHud.cs —— 本项目「界面用代码搭」的样板

## 通用规则

- 只改你名下的文件:UI/ContainerGauge.cs(新)、Stations/PaperBox.cs。需要动别人的文件停下来问
- 交付前必须自己跑离线编译(命令见 WINDOWS-DATA.md)

## 这个窗口现在就能开

**不依赖 P0**,因为你要读的接口(ContainerBase.Count / Capacity / IsFull / ContentsChanged)
上一轮就已经冻结并且在跑了。别的窗口要等 P0,你不用。

## 你的任务

### 1. ContainerGauge.cs —— 一个通用的容器存量指示器

原话:

> 「看见机器里囤着的纸堆?用不着看见吧?纸塞进去就只是数据了,我们后面大可以做一些指示灯或者
> 滚动条来提示用户剩余纸量、墨量不是吗?」

所以:**纸和墨塞进去就是数据,不要画成物理纸堆**(那和「容器里的东西退出物理世界」这条模型矛盾)。
用指示灯 / 滚动条表示数量。

要求:
- 读 ContainerBase 的 Count / Capacity / IsFull
- 订阅 ContentsChanged 更新,**不要每帧轮询**
  (DebugHud 是每 0.1 秒重建一次的调试工具,这个是游戏内 UI,标准不一样)
- IsUnlimited 的容器要能正确处理(没有分母,不能显示 "3/0")
- **遵守 CONSTRAINTS.md 约定 C**:自建 UI 必须 sortingOrder >= 1、不挂 GraphicRaycaster、
  所有 graphic 的 raycastTarget = false
- 容量为 0 的处理、容器还没 spawn 时的处理,都要有个明确答案,并写进注释

**做成通用的**:打印机的纸槽、墨槽、任务队列,纸箱的库存,将来都挂这一个组件。
所以不要写死任何和打印机相关的东西。

样式你定。用 TMP 文字 + 色块是最省事的做法(项目已经有 simhei SDF 字体)。

### 2. PaperBox.cs 的复核

代码上一轮写完了但**从来没跑起来过**。读一遍,确认:
- _payloadIndex 默认 -1 这条路径是对的(印出来的是 prefab 原始样子)
- 补给逻辑和容量、和 IsUnlimited 的关系
- 和它对不上的地方,改掉并写清楚为什么

**用户需要建 PaperBox 的 prefab**(自带模型桌子,和其它工位一样)。把接线清单列出来:
根上要什么组件、碰撞体多大、ContainerBase 挂哪、容量填多少。

## 交付

- 离线编译通过(0 error;3 个 CS0114 是基线,不要修)
- 一份 Markdown 交付说明:你做了什么、自己决定了什么、认为哪里可能不对
- PaperBox 的接线清单要具体到字段
- **不要碰场景和 prefab 文件**
```

---

## 附:用户在编辑器里要做的(不属于任何窗口)

### 1. `DebugHud` 接线 —— 两分钟

DebugHud 的**整个界面是代码搭的**,所以只需要:

1. 场景里新建一个空物体(名字随意,比如 `Debug HUD`)
2. 挂上 `DebugHud` 组件
3. 填两个字段:
   - `Font` → `Assets/Font/simhei SDF.asset`(**必须填**,内置 TMP 字体没有中文字形)
   - `Catalogue` → `Assets/PayloadCatalogue.asset`(可选,不填就按索引列出条目)
4. 其余字段保持默认(`Sorting Order` 1、`Player Tag` "Player"、`Look Distance` 4)

**不需要 Canvas、不需要 EventSystem、不需要任何子物体** —— 它自己建。

### 2. `PaperBox` prefab —— 等 W4 的接线清单

### 3. `Computer` prefab —— 等 W2 的接线清单

### 4. `DocumentStore` 场景物体 —— 等 P0

需要一个挂着 `DocumentStore` 的 NetworkObject(保持激活、**不加 NetworkTransform**)。
严格照 `WIRING.md` 里已有的步骤走,`SceneId` 为 0 时不要打包。

### 5. `NetworkManager` 的定时炸弹(不紧急,但正式开发前要做)

场景里的 NetworkManager 现在引用的是包缓存里的 prefab:

```
Library/PackageCache/com.firstgeargames.fishnet@12ee279bcfde/Demos/Prefabs/NetworkManager.prefab
```

FishNet 一升级,这串路径的哈希就变,场景引用直接断。
做法:把场景里那个 NetworkManager 实例 **Unpack Completely**,或者把 prefab 拷进 `Assets/` 重建。
