# 并行窗口开工规格 · 第三轮:文档与解锁

> **这一份取代了本文件之前的版本。** 早先那一版把「解锁」当成了「解锁一个**种类**」,
> 而游戏里玩家拿到的是**某一份文件**。两个窗口的第一版交付因此作废,数据层已经重做
> (见「零、已经做完的」)。
>
> 每个窗口的提示词都是**整段可复制**的。复制时把该窗口那一段整个贴进去。

---

## 通用规则(每个提示词里都已经包含)

- 只改**你名下的文件**。需要动别人的文件,**停下来问**,不要自己改
- 接口不清楚就**先问**。已冻结的接口就是用来防止各写各的
- **交付前必须自己跑一遍离线编译。** 项目没有 asmdef,单程序集 —— 你写出编译错误,所有窗口都跑不起来
- **不要切分支、不要 push。** `git add` 只写你名下的具体路径,不要 `git add -A`。理由见第五节

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
  不要"修"**
- **离线编译不跑编织器。** `SyncType`、`[ServerRpc]` 这些是 Unity 里的编织器处理的,`dotnet build`
  看不见 —— 它只能挡住语法和类型错误

**每轮开工先跑一遍确认基线干净。** 本轮基线:**0 error / 3 warning**(全是 CS0114)。

### 本轮的额外一条

**FishNet 的 API 一律对着包源码写,不要凭记忆。** 包在
`Library/PackageCache/com.firstgeargames.fishnet@12ee279bcfde/`。
本轮已经有人凭记忆写错过一次 `TimeManager` 的命名空间,来回两轮才修好。

---

## 排期与依赖

| 窗口 | 独占文件 | 什么时候能开 |
|---|---|---|
| **W2 面板** | `UI/ComputerPanel.cs` | **立刻** |
| **W3 收尾** | `UI/DebugHud.cs`、`Containers/ContainerEntry.cs`、`Stations/Printer.cs`(只改一行注释)、`Interaction/PlayerInteraction.cs`、`Dev/DevConsole.cs` | **立刻** |

两个窗口文件零重叠,可以同时开。**数据层已经做完,不需要等任何东西。**

---

## 零、已经做完的(不要在窗口里重做)

**P0-v2**:整个文档数据层。`DocumentRecord`、`DocumentStore`、`DocumentUnlocks`、`DocumentFetch`、
`Computer`、`Printer`、`PrinterDisplay` 都已经按新模型改完并提交。

**W1**:控制台。`document <spec> [team]`、`docs`、`unlock <document>`、`unlock reset`、`unlocks`
都已经按新模型改完。**控制台的活没有了。**

---

## 一、这一轮在做的事(每个窗口都先读这段)

### 一份文档是什么

| | 是什么 | 在哪 |
|---|---|---|
| **种类** | 合同 / Excel / 图片 / 文档 | `DocumentCatalogue.asset` 里的一行 |
| **文档** | 某个种类下、某个编号、属于某个队伍的一份**具体文件** | 运行时由 `DocumentStore.ServerCreate` 创建 |

**编号不是烘在资源里的。** 「Excel 3」是这个回合里第三份被点名的 Excel —— 它没有上限,
所以不可能是一条资源记录。

### 「点名」和「拿到」是两件事

```
客户1 开口:交一个文件夹,里面要有【合同1、Excel1、Excel2】
          → 三条记录立刻存在(DocumentStore)
          → 但只有已经拿到手的才能印(DocumentUnlocks)

同事1 同意给 Excel1,但要你先给他【Image1、Document1】
          → 这一刻 Image1 / Document1 的下载权限开放
          → 打印、打包、交给他
          → 他把 Excel1 交给你(ServerUnlock)
```

**这个差就是面板上那些灰行的意义**:任务点名了三份,你拿到一份,面板上三行、两行是灰的。
玩家看得到这份工作要什么,而不是只能撞上去。

### 两个队伍各自推进

**红队的 Excel 1 和蓝队的 Excel 1 是两份不同的文档。** 所以:

- 编号按 **(种类, 队伍)** 各自递增 —— 两队各自有 Excel 1、Excel 2
- 一份文档只属于一个队伍,**另一队印不了**,服务端在 `Computer.ServerBeginFetch` 里拒
- 面板上**两队的都列出来**,右边标 `A` / `B`,**对方的整行是灰的**

### 面板要长这样

```
文档
  合同      后台
    合同 1        A
  Excel     后台
    Excel 1       A
    Excel 1       B      灰
    Excel 2       A
  图片      Internet
    图片 1        A
```

- **种类行**:种类名 + 来源标签(`后台` / `Internet`)。**不可点**
- **文档行**:种类名 + 编号 + 队伍标签。**可以点**(自己队的、已拿到的、未在下载中的)
- 一个种类只有**在它下面有文档时**才出行

> **来源不再单独分组了。** 早先那一版把左栏分成「后台文件 / Internet」两组;
> 现在来源是种类自己的属性,做在种类行右边的一个标签上,省一层缩进。

---

## 二、已冻结的接口(不要另起一套)

命名空间 `Overworked.Documents`。

```csharp
public struct DocumentRecord          // 只有三个字段
{
    public int SpecIndex;             // 哪个种类
    public int Number;                // 编号,按 (种类, 队伍) 各自从 1 开始
    public int Team;                  // 哪个队伍,-1 = 不上色
}

public struct DocumentCatalogue.Spec  // 一个种类
{
    public string DisplayName;
    public int PayloadIndex;
    public int Source;                // 0 = 后台, 1 = Internet
    public float FetchSeconds;
}

public class DocumentStore : NetworkBehaviour
{
    public static DocumentStore Instance { get; }   // 没同步好时为 null
    public int Count { get; }
    public bool TryGet(int id, out DocumentRecord record);
    public bool TryGetSpec(int id, out DocumentCatalogue.Spec spec);   // ← 一个 id 查出全部信息
    [Server] public int ServerCreate(int specIndex, int team);
}

public class DocumentUnlocks : NetworkBehaviour
{
    public static DocumentUnlocks Instance { get; }  // null = 什么都不锁
    public int UnlockedCount { get; }
    public bool TryGetUnlocked(int index, out int documentId);
    public bool IsUnlocked(int documentId);
    public event Action UnlockedChanged;             // 本地事件,不过网
    [Server] public bool ServerUnlock(int documentId);
    [Server] public void ServerResetUnlocks();
}

public class Computer : StationBase
{
    public int FetchingCount { get; }
    public bool TryGetFetch(int index, out DocumentFetch fetch);
    public bool IsFetching(int documentId);          // ← 收的是文档 id
    public event Action FetchingChanged;
    [Server] public bool ServerBeginFetch(int documentId, Printer printer, int team);
}

public struct DocumentFetch
{
    public int DocumentId;
    public int PrinterObjectId;
    public float ServerReadyAt;      // 服务端的钟,客户端不要读
}

public class PlayerInteraction : TickNetworkBehaviour
{
    public int Team { get; }
    public void RequestDocument(NetworkObject computer, NetworkObject printer, int documentId);
}
```

### 三条调用规矩

**① `DocumentStore.TryGetSpec` 是拿「外观 / 名字 / 来源 / 耗时」的唯一途径。**
`DocumentRecord` 里没有这些,因为它们属于**种类**,拷进记录就是同一个事实的第二份拷贝。

**② `Instance` 为 null 一律当作「不上锁 / 没同步好」。**
`DocumentUnlocks.Instance == null` → 什么都不锁(场景里没这个组件,应当表现得和加它之前一样)。
`DocumentStore.Instance == null` → 这一帧什么都画不出来,**保留槽位里原有的东西,不要清空**。

**③ 队伍要拿 `record.Team` 比,不要相信客户端说的。**
服务端已经在 `Computer.ServerBeginFetch` 里这样做了。面板上的过滤只是显示。

---

## 三、审出来的、本轮必须知道的

### ① `SyncList` 不会自己清 —— 场景物体会带着上一局的状态进来

这条在这一轮已经踩过两次(`Computer._fetching` 和 `DocumentUnlocks`),两处都在
`OnStartServer` 里显式清了。**不要再加一个忘了清的。**

### ② `SyncList.OnChange` 在主机上触发两次

一次 `asServer: true`(服务端自己的写),一次 `false`(回显的读)。
**处理器第一行必须是 `if (asServer && IsClientStarted) return;`** ——
照抄 `ContainerBase.OnContentsChanged` 或 `DocumentUnlocks.OnUnlockedChanged`。

不守这条的表现,现在看不出来;等有人在这里加了动画或音效,就会**播两遍**。

### ③ 面板重建会丢掉选中状态

`Rebuild()` 把所有行销毁重建,高亮跟着没。`OnOfferChanged` 里已经处理过:
**重建之后重新 `Choose(_chosenDocument)`**。这次改布局,别把那一行弄丢。

弄丢的表现很具体:解锁一份文档 → 面板刷了 → **选中的那条不高亮了** → 下一次点打印机什么都不会发生。

### ④ 行号不再是文档 id

`DocumentRow` 小类里那个 `DocumentId` 就是为这件事存在的。
**任何地方都不要用 `_documentRows[i]` 的下标当文档 id** —— 现在种类行和文档行混在一个列表里,
下标和 id 完全对不上。用错的表现是「点第二行的文档,印出来的是第一行」。

### ⑤ `TryGetSpec` 会失败,失败时要保留现场

三种原因:store 还没同步、catalogue 没接、那份文档的种类被从资源里删了。
三种的答案一样:**那一行/那个槽位保留原有的东西,不要清空。**

`PrinterDisplay.ReplaceContent` 就是这么做的,照它的样子写。
清空的表现是机器上的纸变成空白,而你会去查数据层。

### ⑥ 种类行和文档行不能共用一个"能不能点"的字段

种类行永远不可点,文档行看情况。而两边的底色规则也不一样
(种类行是暗色标签,文档行有三种底色)。别为了省一个类把两种行塞进同一个 `DocumentRow`。

---

## 四、后面几轮才做、但这轮别做坏的东西

- **NPC 与需求**:需求指向一份文档 = `{种类, 编号, 队伍}`。它是**点名**的一方,不是「拿到」的一方
- **交付与计分**:把文件夹递给客户,服务端查编号对不对
- **队伍分配**:`PlayerInteraction.Team` 现在由一个控制台命令临时设,**没有真正的分队**

---

## 五、git:这一轮一个分支,不要在窗口里切

**这一轮用的是 `prototype/unlocks`。两个窗口都提交到它,不要各自开分支。**

理由和分支本身无关,是这个项目的**工作目录只有一份**:

- 两个窗口跑在**同一个目录** `E:\UnityProject\Overworked` 里,共用同一批文件和同一个 git index
- 一个窗口执行 `git switch` / `git checkout`,**另一个窗口正在编辑的文件会当场被抽走**
- Unity 也会跟着重新导入整个 `Assets`,而它正开着

| 可以做 | 不要做 |
|---|---|
| `git add <你名下的具体路径>` | `git add -A` / `git add .`(会把别人的半成品也提交进去) |
| `git commit` | `git push`(由核心窗口统一推) |
| `git status` / `git diff` / `git log` | `git switch` / `git checkout` / `git stash` / `git rebase` / `git reset` |

---

# W2 · 电脑面板

```
你负责【电脑面板】这一块。项目是 Unity 6000.6.0f1 + FishNet 4.7.3 的 2v2 办公室 PVP 游戏。

## 先读这些(按顺序)

1. CONSTRAINTS.md —— 硬约束和已冻结接口,改代码前必读
2. WINDOWS-UNLOCK.md 的「一、这一轮在做的事」、「二、已冻结的接口」、「三、审出来的」
3. 你自己唯一的文件:Assets/Scripts/UI/ComputerPanel.cs
   —— 它现在能用,但列的**全是种类**,没有编号、没有队伍。你要把左栏改成下面那个样子

## 通用规则

- 只改你名下的文件:UI/ComputerPanel.cs。需要动别人的文件停下来问
- 接口不清楚先问,不要自己造
- 交付前必须自己跑离线编译(命令见 WINDOWS-UNLOCK.md)
- 不要切分支、不要 push;`git add` 只写你自己那个路径,不要 `git add -A`

## 你的任务

### 1. 左栏改成两级

```
文档
  合同      后台          ← 种类行:名字 + 来源标签。不可点
    合同 1        A       ← 文档行:名字 + 编号 + 队伍标签。可点
  Excel     后台
    Excel 1       A
    Excel 1       B       ← 对方队伍的,灰的,点不动
    Excel 2       A
  图片      Internet
    图片 1        A
```

**迭代顺序**:按 **catalogue 的顺序**走种类;每个种类下按 **队伍、再按编号** 走文档。

**种类行只在它下面有文档时才出现。** 一个没有任何文档的种类是空的,不该占一行。

**种类不在 catalogue 里的文档不能消失。** 记录里的 `SpecIndex` 可能在资源里找不到
(有人删了那一行)。那份文档仍然是真实的、可能正被人拿在手里 —— **照样出一行**,
种类名用 `种类 N` 之类的兜底写法。**静默丢一份文档是这里最糟的失败方式。**

### 2. 队伍标签

- `record.Team == 0` → `A`;`== 1` → `B`
- 其他值(包括 `-1`)→ 直接显示那个数字。**不要猜**,不要把它当成 A
- **只有 `record.Team == _team` 的行才能点。** 其余的一律灰掉、点不动

`_team` 已经在 `Show()` 里缓存好了(`interaction.Team`),**不要每次重建去读**。

**灰掉用的是 `RowLockedColour`** 那一档。注意 `Choose()` 会把每一行重涂,
所以每一行都要记住自己"没被选中时该是什么颜色" —— `DocumentRow.BaseColour` 就是干这个的,
**别把它丢了**。

### 3. 保留现在对的东西

这几件事现在的代码是对的,**照原样留着,不要顺手重写**:

- 下载中的行显示倒计时(`FetchLabel` / `BeginTracking` / `RefreshFetchLabels`),
  而且**等待中的行保持全亮** —— 「正在下载」不该读成「被拒绝」
- `Rebuild()` 之后重新 `Choose(_chosenDocument)`(见「三、审出来的」第 ③ 条)
- `FetchingChanged` 和 `UnlockedChanged` 走同一个处理器
- 订阅和解订阅都在 `Show` / `Hide`

### 4. 不要做的事

- **不要动右栏(打印机列表)**
- **不要动 `SendTo` 的关窗逻辑** —— Internet 的等待那一套是对的
- **不要动 `Computer.cs`** —— 服务端的检查和它无关
- 不要 `[RequireComponent]`,不要往 prefab 加组件
- 不要把种类行和文档行塞进同一个类(见「三、审出来的」第 ⑥ 条)

## 交付

- 离线编译通过(0 error;3 个 CS0114 是基线,不要修)
- 一份 Markdown 交付说明:你做了什么、自己决定了什么、认为哪里可能不对
- 不需要用户做任何编辑器操作
- **不要碰场景和 prefab 文件**
```

---

# W3 · 收尾

```
你负责【收尾】这一块。项目是 Unity 6000.6.0f1 + FishNet 4.7.3 的 2v2 办公室 PVP 游戏。

## 先读这些(按顺序)

1. CONSTRAINTS.md —— 硬约束和已冻结接口,改代码前必读
2. WINDOWS-UNLOCK.md 的「一、这一轮在做的事」和「二、已冻结的接口」
3. 你自己的文件:
   - Assets/Scripts/UI/DebugHud.cs
   - Assets/Scripts/Containers/ContainerEntry.cs
   - Assets/Scripts/Stations/Printer.cs   ← 只改一行注释
   - Assets/Scripts/Interaction/PlayerInteraction.cs
   - Assets/Scripts/Dev/DevConsole.cs

## 通用规则

- 只改上面这五个文件。需要动别人的文件停下来问
- 接口不清楚先问,不要自己造
- 交付前必须自己跑离线编译(命令见 WINDOWS-UNLOCK.md)
- 不要切分支、不要 push;`git add` 只写你自己那五个路径,不要 `git add -A`

## 你的任务

三件互相没关系的小事。**按这个顺序做,每件做完跑一次编译。**

### 1. DebugHud 要报出「是哪几份文档」

现在容器里的文档条目只被数了个数,显示成 `数据 x2`。而一份文档现在的身份是
**`{种类, 编号, 队伍}`** —— 只报个数等于没报。

改成把每一份都报出来,比如 `Excel 1 ×1, Excel 2 ×1`。
用 `DocumentStore.TryGetSpec` 拿种类名,拿不到就用 `种类 N` 兜底(见 W2 那条同样的理由:
**静静丢掉一份文档是最糟的失败方式**)。

`DebugHud` 里有现成的 `_tally` / `Tally` / `PayloadName` 那套模式,照着它的风格写。
**它是每 0.1 秒重建一次的工具,不联网、不改状态。**

### 2. 删掉 `ContainerEntry.OwnerClientId`

这个字段没有任何人读。**它自己的注释就写着**:

> If the document store ends up carrying that itself, delete this field rather than leave one
> that nothing explains.

`DocumentStore` 现在自己带队伍了,所以:**删掉它**。

- 删字段
- 删 `ForEntity` / `ForData` 上的 `ownerClientId` 参数(现在**没有任何调用点传它**)
- `Printer.cs:559` 附近有一句注释提到 `OwnerClientId`,一并改掉

**`ContainerEntry` 是会过网的 struct**,所以才只有 public 字段。删字段会改变线上格式 —— 这是
允许的(所有端一起换),但**不要顺手动别的字段**。

### 3. 控制台加一条 `team <n>`,并且让队伍真的能设

现在 `PlayerInteraction.TeamThisRound` 是个 `const int = 0`,全服一个队。
这意味着**按队伍计数、按队伍拒绝这两件事根本测不了**。

- `PlayerInteraction`:`const` 改成 `readonly SyncVar<int> _team`(初值 **0**),
  `public int Team => _team.Value`,`[Server] public void ServerSetTeam(int team)`
  —— 形状照 `NetworkGrabbable` 里那几个 `ServerSet*` 写
- `DevConsole`:`team <n>` 命令,**服务端限定**,对自己的玩家对象调用 `ServerSetTeam(n)`,
  然后打印一句确认

> **这是个测试口子,不是玩法。** 真正的分队是后面那一轮的事 —— 写的时候在注释里说清楚,
> 别让它看起来像正式的分队机制。

**注意 `Team` 已经是 `PlayerInteraction` 上的公开属性**,W2 的面板在读它。
**不要改它的名字或类型**(类型仍是 `int`)。

## 交付

- 离线编译通过(0 error;3 个 CS0114 是基线,不要修)
- 一份 Markdown 交付说明:你做了什么、自己决定了什么、认为哪里可能不对
- 不需要用户做任何编辑器操作
- **不要碰场景和 prefab 文件**
```

---

## 附:用户在编辑器里要做的

### 1. `Assets/DocumentCatalogue.asset` 填成**种类**

**现在里面是「一份文件一条」**(`Image 1`、`Image 2`、`Document 1`),那是作废的模型。
一条记录现在指的是**种类**,编号是运行时给的。

| Display Name | Payload | Source | Fetch Seconds |
|---|---|---|---|
| 合同 | 2 | 0 后台 | 0 |
| Excel | 2 | 0 后台 | 0 |
| 图片 | 2 | **1 Internet** | 4 |
| 文档 | 2 | **1 Internet** | 5 |

**`Payload Index` 全填 2 就行** —— 这一轮不需要为新种类建模,靠名字和编号区分。
要上外观是材质那天的事。

> `UnlockedAtStart` 这个字段已经从代码里删了,Unity 下次保存会把它丢掉。不用管。

### 2. 场景里 `DocumentStore` 那个物体

- **`DocumentStore` 自己多了一个 `_catalogue` 字段要填**(catalogue 从 `Computer` 挪到它身上了,
  因为 `Computer` 上那一份已经没有任何人读)
- **挂一个 `DocumentUnlocks`**

> 两个都在同一个 GameObject 上。都要是场景 NetworkObject(保持激活、**不加 NetworkTransform**)。
>
> **`Computer` 上的 catalogue 字段已经删了** —— 那是预期的,不是丢了。

---

## 验收

| # | 测 | 错了会看到 |
|---|---|---|
| 1 | `document 0` | 打印出「named document 0: kind 0 '合同', number 1」→ 编号是按种类算的 |
| 2 | `document 0` 再来一次 | number **2** |
| 3 | `document 0 1`(队伍 1) | number **1** ← **不是 3**。两队各自从 1 开始 |
| 4 | `docs` | 三行,种类名 + 编号 + 队伍 |
| 5 | `unlocks` | 三份都是 `·`(开局什么都没有) |
| 6 | `unlock 0`,看面板 | 那一行亮了、能点 | 
| 7 | 面板长什么样 | **种类行 + 编号行两级,右边有 A/B**(见第一节那张图) |
| 8 | 面板上队伍 1 的那一份 | **灰的、点不动** |
| 9 | `team 1`,重开面板 | A 变 B;原先那一份变灰,队伍 1 的亮起来 |
| 10 | 面板上点一份已解锁的 Internet 文档 → 选打印机 | 面板不立刻关,那一行开始倒数 |
| 11 | 等几秒 | 打印机队列多一份;`printers` 能看见 |
| 12 | DebugHud 的容器行 | 打印机的队列显示 `图片 1` 之类的**身份**,不是 `数据 x1` |
| 13 | 退出 Play 再进 | 解锁状态**没有**带过来 |
| 14 | `unlocks` 里的 `✓` | 出方框就说明 simhei SDF 没有这个字形,换成 `[x]` |

> **第 3 条单独拎出来。** 不做按队伍计数的话,两队同时开工时编号会交替往下走 ——
> 蓝队永远做不出自己的 Excel 1。这条是这一轮修掉的一个真 bug。
>
> **第 9 条需要 W3 的控制台命令**才能测。没有它 `Team` 永远是 0,第 8 条也看不到灰行。
