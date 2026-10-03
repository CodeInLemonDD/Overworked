# 并行窗口开工规格 · 第三轮:数据解锁

每个窗口的提示词都是**整段可复制**的。复制时把该窗口那一段整个贴进去。

**这一轮的验收只有一条**:

> 控制台解锁一条 Internet 文档 → 电脑面板上它亮起来 → 点它 → 等几秒 → **打印机队列里出现**

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

**每轮开工先跑一遍确认基线干净。** 本轮基线:**0 error / 3 warning**(全是 CS0114)。

### 本轮的额外一条

**FishNet 的 API 一律对着包源码写,不要凭记忆。** 包在
`Library/PackageCache/com.firstgeargames.fishnet@12ee279bcfde/`。

**这条今天已经被违反过一次**:有人凭记忆写了 `using FishNet.Managing;` 去用 `TimeManager`,
而它其实在 `FishNet.Managing.Timing`。来回两轮编译才修好。
**任何你没在项目里见过用法的类型/方法,先 grep 一遍包源码。**

---

## 排期与依赖

| 窗口 | 独占文件 | 依赖 | 什么时候能开 |
|---|---|---|---|
| **P0 解锁数据层**(核心窗口) | `Documents/DocumentUnlocks.cs`(新)、`Documents/DocumentCatalogue.cs` | — | **立刻** |
| **W1 控制台** | `Dev/DevConsole.cs` | P0 | P0 之后 |
| **W2 面板与工位** | `UI/ComputerPanel.cs`、`Stations/Computer.cs` | P0 | P0 之后 |

**内容填写不是窗口** —— `DocumentCatalogue.asset` 的 spec 表由用户在编辑器里填,见本文末尾。

---

## 一、这一轮在做的事(每个窗口都先读这段)

### 现在缺的是什么

链条已经通了:电脑下单 → 打印机印出带编号的纸 → 丢进文件夹。

但**电脑面板上列出的每一条文档,玩家一开局就能拿**。所以现在没有「获取」这件事 —— 只有「选择」。

这一轮加的是**解锁**:有些文档一开始就能拿(公司自己的文件柜),有些要先「找到」。

### 解锁是什么

**一条 spec 只有两个状态:已解锁 / 未解锁。** 服务端记一张**只会变长的表**,同步给所有端。

- **未解锁的 spec**:面板上仍然列出来,但是灰的、点不动
- **已解锁的 spec**:和现在完全一样

> **为什么灰掉而不是藏起来**:玩家得知道「还有东西没找到」。藏起来的话,这轮做的东西
> 在面板上完全看不见,连验收都做不了。

### 谁来触发解锁

**这一轮只接控制台。** NPC 还没做,所以解锁必须先是一个**谁都能调的动作**:

```csharp
[Server] bool ServerUnlock(int specIndex)
```

后天做了 NPC,「和客户交互」就是在这个方法上加一行调用 —— **不动面板、不动数据层**。

> 昨天排期时发现的那个循环依赖(「解锁要靠 NPC,而 NPC 排在交付那条线上」)就是靠这个解开的。

### 一开局哪些是解锁的

`Spec` 上多一个 `UnlockedAtStart`。

**不要用 `Source` 推。** 看起来「后台文件 = 本来就有,Internet = 要去拿」很顺,但
**合同是后台文件,而它恰恰是要跟客户谈出来的那一种**。来源说的是「从哪拿」,解锁说的是
「现在能不能拿」,两件事。

`DocumentUnlocks` 在自己启动时按这张表**播种**一次。

### 未解锁的 spec 服务端要拒绝

不是只把面板上的行点灰。**服务端要拒** —— 面板是客户端的,改过的客户端可以直接发 spec 索引过来。

判断点只有一个:`Computer.ServerBeginFetch`。**「怎么取得一份文档」这件事已经全部在它里面了**
(来源、等待、队列位置),解锁检查是同一类规则,放一起。

---

## 二、已冻结的接口(P0 产出)

命名空间 `Overworked.Documents`。**依赖它们的模块按这些签名写,不要另起一套。**

### `DocumentCatalogue.Spec` 的新增部分

```csharp
[System.Serializable]
public struct Spec
{
    public string DisplayName;
    public int PayloadIndex;
    public int Source;
    public float FetchSeconds;

    public bool UnlockedAtStart;   // ← 新增
}
```

**加在最后。** 这个 struct 不会过网(它是资源里的行),但字段顺序变了以后**资源文件里已有的行
会按新顺序重读** —— 所以只能往后加,不能插在中间。

### `DocumentUnlocks`(NetworkBehaviour,新文件)

场景里一个 NetworkObject 上挂一个,**和 `DocumentStore` 挂在同一个物体上**。服务端写,所有端读。

```csharp
public class DocumentUnlocks : NetworkBehaviour
{
    public static DocumentUnlocks Instance { get; }   // 找场景里那一个;没有则为 null

    public int UnlockedCount { get; }
    public bool TryGetUnlocked(int index, out int specIndex);
    public bool IsUnlocked(int specIndex);

    public event Action UnlockedChanged;   // 本地事件,不过网。同 ContainerBase.ContentsChanged

    [Server] public bool ServerUnlock(int specIndex);   // 已解锁或索引不存在时返回 false
    [Server] public void ServerResetUnlocks();          // 清空后按 UnlockedAtStart 重新播种
}
```

**内部用 `SyncList<int>`**,存已解锁的 spec 索引。理由和 `DocumentStore` 一样:
`ContainerBase` 已经在跑这个类型,已知可行。**这张表只增不减**(`ServerResetUnlocks` 是给测试用的)。

> **`Instance` 为 null 时,一律当作「没有锁」。** 调用点这么写:
>
> ```csharp
> DocumentUnlocks unlocks = DocumentUnlocks.Instance;
> if (unlocks != null && !unlocks.IsUnlocked(specIndex))
>     return;   // 或这一行画成灰的
> ```
>
> 两种失败的代价不对称:场景里**没有这个组件**时,「锁不生效」的表现是**面板上没有灰行** ——
> 验收第 2 条一眼就看得出来;反过来全部拒绝的表现是「电脑坏了」,和一个没接线的工位长得一模一样,
> 要花久得多才能找到。
>
> **但「组件在、`_catalogue` 没填」是反过来的** —— 全部锁上,而且启动时报错。
> 那是「东西在但填错了」,不是「场景比这个功能还老」,两种情况不该给同一个答案。

**`Instance` 的写法和 `DocumentStore` 一模一样** —— 在 `OnStartNetwork` 里赋值、发现第二个时报错,
在 `OnStopNetwork` 里清空。照着抄 `DocumentStore.cs`,不要另发明一套。

**`OnStartServer` 里要播种,`OnStopServer` 里要清空。** 两个理由不一样:

- **播种**,因为 `UnlockedAtStart` 是资源里的静态事实,运行时的表是它的增量
- **清空**,因为**场景 NetworkObject 的字段不随会话重置**(`SyncList` 不会自己清 —— 这一点
  `ContainerBase` 从来没处理过,是本项目的一个已知欠账)。不清理的话,第二次进 Play 会带着
  上一局的解锁状态

**`DocumentUnlocks` 需要一个 `DocumentCatalogue` 引用**(序列化字段,用户来填)。
没填时在 `OnStartServer` 里报错并**当作全部未解锁** —— 沉默地全解锁比报错糟得多。

### `Computer` 的新增行为(不是新接口,是 W2 要改的)

`ServerBeginFetch` 在最前面加一条:**这条 spec 没解锁就直接 return false。**

顺序上放在「catalogue 和索引校验之后、打印机解析之前」都行,**唯一的要求是它在动手之前**。

---

## 三、审出来的、本轮必须知道的

### ① `SyncList` 不会自己清 —— 场景物体会带着上一局的状态进来

这条昨天在 `Computer._fetching` 上踩过,处理方式是在 `OnStopServer` 里显式 `Clear()`。

**`DocumentUnlocks` 必须做同样的事。** 不做的表现是:**第一次 Play 正常,第二次 Play 一开局
东西全是解锁的**,而且你会以为是播种写错了。

### ② `UnlockedChanged` 在主机上会触发两次

同一件事的第 N 次:`SyncList.OnChange` 在主机上跑两遍(`asServer: true` 和 `false`)。
**第一行必须是 `if (asServer && IsClientStarted) return;`** —— 照抄 `ContainerBase.OnContentsChanged`。

不守这条的表现是:面板重建两次(看不出来),但如果将来有人在这里加了动画或音效,就会**播两遍**。

### ③ 面板重建会丢掉选中状态

`ComputerPanel` 现在靠 `Rebuild()` 重建所有行,而**重建会连高亮一起丢掉**。
`OnFetchingChanged` 里已经处理过一次(重建之后重新 `Choose`)。**解锁变化也要走同一条路。**

不处理的表现在验收里直接可见:解锁一条文档,面板刷了,但**选中的那一条不高亮了**,
下一次点打印机什么都不会发生。

### ④ 面板分组以后,行号不再是 spec 索引

`BuildDocumentRows` 现在按 `i` 从 0 数到 `Count`,所以「第几行」正好等于「第几个 spec」。
**分组之后不成立了** —— 第一组的行号和第二组连不上。

`ComputerPanel` 里已经有一个 `SpecRow` 小类专门记着 `SpecIndex`(是为了文件夹那一轮加的)。
**用它,不要再用行号当索引。** 谁要是把 `_documentRows[i]` 当成 spec `i`,表现是
「点第二组的文档,印出来的是第一组的」。

---

## 四、后面几轮才做、但这轮别做坏的东西

- **NPC 与需求**:需求指向一份文档 = `{种类, 编号}`。**解锁是「能不能拿」,需求是「要不要」**,两回事
- **交付与计分**:把文件夹递给客户,服务端查编号
- **队伍**:分数按队算,而 `PlayerInteraction.TeamThisRound` 现在还是 `const int = 0`

---

# P0 · 解锁数据层(核心窗口写)

> 这个不派给窗口。接口一旦分叉,后面两个全废。

**产出**:`Assets/Scripts/Documents/DocumentUnlocks.cs`(新)+ `DocumentCatalogue.Spec` 加一个字段。

1. `DocumentCatalogue.Spec` 末尾加 `public bool UnlockedAtStart;`,带 `[Tooltip]`
2. `DocumentUnlocks.cs` —— 照 `DocumentStore.cs` 的骨架写:`Instance`、`SyncList<int>`、
   `OnStartNetwork`/`OnStopNetwork`、`OnStartServer`(校验 + 播种)/`OnStopServer`(清空)
3. 跑离线编译,确认 0 error

**`Computer.cs` 里的那条拒绝是 W2 的活,不是 P0 的** —— 那个文件归 W2。
P0 只定义「谁有权取得一份文档」这个接口,不碰调用点。

**P0 完成后接口冻结,核心窗口退回策划与审核。**

---

# W1 · 控制台

```
你负责【调试控制台】这一块。项目是 Unity 6000.6.0f1 + FishNet 4.7.3 的 2v2 办公室 PVP 游戏。

## 先读这些(按顺序)

1. CONSTRAINTS.md —— 硬约束和已冻结接口,改代码前必读
2. WINDOWS-UNLOCK.md 的「一、这一轮在做的事」和「二、已冻结的接口」
3. 你自己唯一的文件:Assets/Scripts/Dev/DevConsole.cs

## 通用规则

- 只改你名下的文件:Dev/DevConsole.cs。需要动别人的文件停下来问
- 交付前必须自己跑离线编译(命令见 WINDOWS-UNLOCK.md)

## 你的任务

这一轮加入「解锁」以后,验证它的唯一手段就是控制台 —— NPC 还没做。
**这个窗口的价值全在于「让 W2 能被测」**,所以优先级是命令能用,不是命令好看。

### 要加的命令

沿用它现有的风格(坐标是格子、Tab 补全、↑↓ 历史、_commandsEnabled 门控):

- `unlock <spec>` —— 解锁一条文档。打印出它叫什么、现在是第几条解锁的
- `unlocks` —— 列出所有 spec 和解锁状态(用 ✓ / ·,不要用会乱码的字符)
- `unlock reset` —— 调 ServerResetUnlocks(),回到 UnlockedAtStart 的状态

**`unlock` 的补全候选要从 DocumentCatalogue 现取**,不要硬编码 —— 和现有的
`document` 命令同一个套路。已经解锁的也要能补出来(重复解锁返回 false,打印一句就行)。

### 不要做的事

- **不要给 `document` 命令加解锁检查。** 它是调试工具,绕过解锁是它的职责;
  要测「锁着的时候拿不到」,走面板,不要走控制台

## 交付

- 离线编译通过(0 error;3 个 CS0114 是基线,不要修)
- 一份 Markdown 交付说明:加了哪些命令、怎么用、自己决定了什么
- 不需要用户做任何编辑器操作
- **不要碰场景和 prefab 文件**
```

---

# W2 · 面板与工位

```
你负责【电脑面板与工位】这一块。项目是 Unity 6000.6.0f1 + FishNet 4.7.3 的 2v2 办公室 PVP 游戏。

## 先读这些(按顺序)

1. CONSTRAINTS.md —— 硬约束和已冻结接口,改代码前必读
2. WINDOWS-UNLOCK.md 的「一、这一轮在做的事」、「二、已冻结的接口」和「三、审出来的」
3. 你自己的两个文件:Assets/Scripts/UI/ComputerPanel.cs、Assets/Scripts/Stations/Computer.cs

## 通用规则

- 只改你名下的文件:UI/ComputerPanel.cs、Stations/Computer.cs。需要动别人的文件停下来问
- 交付前必须自己跑离线编译(命令见 WINDOWS-UNLOCK.md)

## 你的任务

### 1. Computer.cs:拒绝未解锁的 spec

`ServerBeginFetch` 里加一条:没解锁就直接 return false。

位置在「catalogue 和索引校验之后」都行,**唯一要求是它在动手之前**。

**要做的检查是服务端的,不是面板的。** 面板把行点灰只是提示 —— 改过的客户端可以直接
把 spec 索引发过来,所以真正的门开在这个方法里。

### 2. 面板:左栏分成两组

**布局不动**(还是两栏、还是那么大),只把**左栏**的行按来源分成两组:

```
文档
  后台文件
    [合同]                      ← 已解锁,和现在一样可点
    [报表]                      ← 已解锁
   Internet
    [图片]  锁定                 ← 灰的,点不动
```

规则:

- 组标题只在**这一组有 spec 时**出现
- 未解锁的行:名字照常显示,底色用比 `RowColour` 更暗的一档,**不可点**
- 组顺序固定:**后台文件在前,Internet 在后**

> 为什么灰掉而不是藏起来:玩家得知道「还有东西没找到」,而且藏起来的话这一轮做的东西
> 在面板上根本看不见,连验收都做不了。

### 3. 解锁变化要刷新面板

订阅 `DocumentUnlocks.Instance.UnlockedChanged`,变化时 `Rebuild()`。

**注意「三、审出来的」第 ③ 条**:`Rebuild()` 会把选中高亮一起丢掉,而 `OnFetchingChanged`
里已经处理过一次(重建之后重新 `Choose`)。**解锁这条走同一条路,不要另写一套。**

订阅和解订阅放在和 `FetchingChanged` 同一个地方(`Show` / `Hide`)—— 两个事件的生命周期一样。

### 4. 不要做的事

- **不要动右栏(打印机列表)**
- **不要动 `SendTo` 的关窗逻辑** —— Internet 的等待那一套是上一轮做好的,别碰
- **`FetchSeconds` 和解锁是两件事**,一个已解锁的 Internet 文档**仍然要等**
- 不要 `[RequireComponent]`,不要往 prefab 加组件

## 交付

- 离线编译通过(0 error;3 个 CS0114 是基线,不要修)
- 一份 Markdown 交付说明:你做了什么、自己决定了什么、认为哪里可能不对
- 需要用户在编辑器里做的接线,列成清单(应该只有一条:把 DocumentUnlocks 挂上去)
- **不要碰场景和 prefab 文件**
```

---

## 附:用户在编辑器里要做的

### 1. `DocumentCatalogue.asset` 填内容

**现在整张表只有一条 spec,而且 `Display Name` 字面量就是字符串 `"0"`。**

1. 把现有那条的 `Display Name` 改成真名,**`Unlocked At Start` 勾上**
2. 再加几条(合同 / 报表 / 文章 / 图片 …)

**`Payload Index` 可以都填 2(那张纸)。** `DocumentCatalogue` 的注释里写着这件事:

> several specs may share one payload (a contract and a report could both be a sheet of paper)

**所以这一轮不需要为新种类建模。** 靠 `Display Name` 和印出来的编号区分就够了 ——
要上外观是材质那天的事,别让它挡在数据前面。

参考填法:

| Display Name | Payload | Source | Fetch | Unlocked At Start |
|---|---|---|---|---|
| 合同 | 2 | 0 后台 | 0 | ✅ |
| 报表 | 2 | 0 后台 | 0 | ✅ |
| 文章 | 2 | 1 Internet | 4 | ⬜ |
| 图片 | 2 | 1 Internet | 5 | ⬜ |

### 2. 场景里那个 `DocumentStore` 的物体上

**挂一个 `DocumentUnlocks`**,把 `_catalogue` 指向同一个 `DocumentCatalogue.asset`。

> **和 `DocumentStore` 挂在同一个 GameObject 上。** 两个都要是场景 NetworkObject
> (保持激活、**不加 NetworkTransform**)。

---

## 验收(接完之后跑)

| # | 测 | 错了会看到 |
|---|---|---|
| 1 | `unlocks` | 只有勾了 `Unlocked At Start` 的那几条是 ✓ |
| 2 | 打开电脑面板 | 未解锁的行是灰的、点不动;组标题「后台文件」「Internet」都在 |
| 3 | 点一个**灰**的行 | 高亮跳过去了 ← 应该点不动 |
| 4 | `unlock 2`,再看面板 | 那条亮了、能点了 → **面板没有订阅 UnlockedChanged** |
| 5 | 第 4 步之后 | **选中的那条不高亮了** → 重建后没重新 Choose(第 ③ 条) |
| 6 | 解锁一条 Internet → 点它 → 选打印机 | 面板立刻关了 → 那条 spec 的 `Fetch Seconds` 是 0,或者解锁检查把已解锁的也拒了 |
| 7 | 第 6 步之后等几秒 | 打印机队列没多一条 |
| 8 | **没解锁就直接 `document 2`(控制台)** | 应该**照常产出** —— 调试命令绕过解锁 |
| 9 | `unlock reset` | 回到第 1 步的状态 |
| 10 | 退出 Play 再进一次 | 解锁状态**没有**带过来 ← 带过来就是 `OnStopServer` 没清(第 ① 条) |

> **第 10 条单独拎出来。** 不做的话第一次测试全过,第二次进 Play 所有东西都是解锁的 ——
> 而你会以为是播种写错了,去查数据层,而问题在生命周期。
