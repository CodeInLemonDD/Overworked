# W3 · 吸附阻挡 —— 窗口笔记

2026-09-29 · 状态:**代码完成,离线编译已过,等真机验收**

这份只写提示词里没有的东西:我做的决定、我认为提示词说得不够准的地方、以及给 W1/W2 的布置规则。
任务本身(改了什么、机制是什么)在提示词和代码注释里已经写全了,不重复。

---

## 交付

| 文件 | 改动 |
|---|---|
| `Interaction/PlacementBlocker.cs` | 新建。空标记 `MonoBehaviour` + `[DisallowMultipleComponent]`,无成员 |
| `Interaction/SnapSurface.cs` | +56 行:`_acceptsObjects` 字段 / `AcceptsObjects` 属性 / 私有 `IsBlocked()` / `TrySample` 与 `TryFind` 各一处拦截 |

`TryRaycastDown` 的自身排除逻辑、`OnValidate` 的网格居中警告 —— 一行未动,`git diff` 可确认。
`TryFind` 里拒掉时把 `surface` 置 null,和原有的法线拒绝分支保持一致。

---

## 一、走查在哪一层停,以及它的静默失效(已用警告兜住)

`IsBlocked` 从命中碰撞体往上走,**走到 `SnapSurface` 所在节点就停**(该节点自己也查)。于是:

| 标记的位置 | 结果 |
|---|---|
| 与 `SnapSurface` 同层,或在其下层 | 拦得住 |
| 在 `SnapSurface` 的**上层**(标记挂机器 prefab 根、`SnapSurface` 挂子节点「桌面」) | **拦不住**,那一格照旧可放 —— 原始 bug 原样复现 |

语义保留(不改成「一路走到场景根」:那会废掉「大桌子 + 机器只占一格」这种摆法)。但**失效不再静默** —— `PlacementBlocker.OnValidate` 现在双向检查,挂错层当场在 Console 报警并给出改法。

### 为什么是这两条,而不是提示词里写的「往下找」

提示词要的是「从自己的节点往下(含自身子树)找不到 `SnapSurface` 就报警」。这条规则和它自己的五条验收冲突:

| 验收 | 字面规则(往下找) | 实际要求 |
|---|---|---|
| 标记与 `SnapSurface` 同层 | 含自身 → 找得到 → 不报 ✓(不含自身则报 ✗) | 不报 |
| 标记在 `SnapSurface` **之下**(机器挂标记、桌子挂 SnapSurface、机器是桌子子节点) | 子树里**没有** `SnapSurface` → 报 ✗ | **不报** |
| 标记在 `SnapSurface` **之上** | 子树里**有** `SnapSurface` → 不报 ✗ | **报** |

**方向是反的**。该问的不是「我下面有没有 `SnapSurface`」,而是「探针走得到我吗」。`IsBlocked` 从命中碰撞体**往上**走,于是只有两种形态会让标记失效:

> 报警 = (**自己及身下没有任何可命中的碰撞体**) **或** (**自己下方有接受物件的 `SnapSurface`**)

- 第一条:走查从命中的碰撞体起步。身下没有碰撞体,就没有任何一条走查会经过我
- 第二条:我下方那层桌面更近,走查在那里就停了 —— 命中那层桌面的射线绕过我

### 机器的桌子是装饰品 —— 这条澄清换掉了我的第一版判据

用户澄清:机器**不自带桌面**,机器的桌子只是装饰品,和容器桌子(挂 `SnapSurface` 的那些)是两码事。两个后果:

1. **「上方有没有 `SnapSurface`」这条判据被删掉了**(我第一版的第一条)。机器独立站在格子里,上方什么都没有 —— 按那条写,**每一台机器都会报警**。而那种形态下什么都不漏:没有桌面可解析,探针本来就会拒掉那一格,不需要标记出手。警告必须只对「真的会漏」的形态发声,否则它会被当成噪音关掉。
2. **一个真阳性风险要盯着**:如果机器的装饰桌子用了 `Table.prefab`(或任何一个装饰节点带了 `SnapSurface`),机器顶上就变成可放的了 —— 而标记挂在更上面,**盖不住它**,第二条检查会当场报警。修法:那层关掉 `AcceptsObjects`(警告随之消失),或者干脆别让装饰件带 `SnapSurface`。

### 布置规则(给 W1/W2)

> `PlacementBlocker` 必须挂在**它所占据的那个碰撞体所在节点**,或该节点与 `SnapSurface` 之间的任一祖先上。
> 挂在没有碰撞体的纯视觉子节点上无效;挂在 `SnapSurface` 之上也无效(**现在会报警告**)。

W1 的打印机按提示词是「根上:ContainerBase + PlacementBlocker + 碰撞体」——**如果机器的 `SnapSurface` 不在根上,这条就要重新摆**。

---

## 二、判据是命中级,不是格级(提示词写的是格级)

提示词写「判该**格**不可用」,而描述出来的机制(「命中碰撞体与它之间隔着 PlacementBlocker」)是**命中级**的。我实现的是机制。两者的差别:

一次成功的放置要过两道探测 —— `TryFind`(在物体所在 XZ)和随后的 `TrySample`**在格中心**复探。所以最终效果是:

> **一格是否被拒,取决于那个固定物件是否盖住了格中心。**

- 物件盖住格中心 → 两道都拦下来 → 整格拒绝 ✓
- 物件只盖住一格的一角 → 物体照样落在这一格**裸露的桌面上**(落在桌面,不是落在物件顶上 —— 原 bug 不会出现)

我认为这个结果是可接受的,而且比「整格」更贴近直觉(那一格还有地方放)。但**它不是提示词字面写的东西**,所以写在这里备案:试玩时如果看到「某些格子有时能放有时不能放」,那不是随机,是物件盖没盖住格中心。

如果真想要整格语义,做法是查格的足迹而不是查命中,成本高一个量级,不建议为了这个场景加。

---

## 三、考虑过、没采用的方案

**让探测自己认出「这是站在桌面上的东西」,完全不布置标记**:射线一次拿到的是一串命中,若某个命中的**下方还有一个属于同一 `SnapSurface` 的命中**,就说明命中的东西是站在桌面上的。

- 优点:零布置负担,忘不了,没有「层级搭错就静默失效」这个坑
- 为什么没做:
  1. **多级桌面会误判**。带一圈抬高边沿的桌子,每一格都会读成「边沿是站在桌面上的东西」,而那道边沿的顶面其实是合法可放的
  2. 提示词已经定了标记方案,接口是冻结的
  3. 它终究是启发式,标记是确定性的

留在这里只是说明设计空间看过了,不是建议改。**但请注意它揭示的风险是真实的:标记方案的失效方式是静默的**(漏拦 = 原 bug 复现),而启发式方案的失效方式是显眼的(多级桌面被拒)。这是我对这次改动的最大保留意见。

---

## 四、我请求核心窗口改的文档(不是我的文件,我没动)

1. **`CONSTRAINTS.md`**:`PlacementBlocker` 现在是一个被 W1 依赖的冻结接口,值得和 `ContainerBase` 它们并列写一行 —— 签名 + 上面那条布置规则。否则 W1 只会看到「挂一个空脚本」,不知道自己可能挂错层。
2. **`CONSTRAINTS.md` 零节的措辞**:「机器/工位:独立 prefab,**自带模型桌子**」很容易被读成「机器自带一张可放置的桌子」。实际那只是装饰,可放置的桌面是另一回事(容器桌子)。W1 会踩:装饰件若带上了 `SnapSurface`,机器顶上就变成可放的了。
3. **`DEVELOPMENT.md` 的「踩过的坑」表**:值得加一行 —— *物体被吸到桌上固定物件顶上 / 探测射线命中的是子物件,`GetComponentInParent` 顺着找到了桌子的 `SnapSurface` / 固定物件必须挂 `PlacementBlocker`*。现在的表里已经有一行「桌子吸附完全失效」是同一族问题(都是「射线打到的不是你以为的那个碰撞体」),两行挨着放更好查。

---

## 五、未验证的,和试玩时该留意的

我一次都没运行过(编辑器归你),四条验收全是推理得来,不是实测:

- **「干净桌子行为一字不变」** 我能给的保证是:干净桌子上没有任何 `PlacementBlocker`,`_acceptsObjects` 默认 `true`,`IsBlocked` 恒返回 false,`TryFind`/`TrySample` 的路径与改动前完全一致。唯一新增开销是每次探测多几次 `GetComponent` 走查(链长 2–3 层,无 GC 分配,可忽略)。
- **「场景同级固定物件本来就被拒」不回归**:那种物件的 `GetComponentInParent<SnapSurface>` 返回 null,在 `IsBlocked` **之前**就返回了,新代码根本走不到。这条我比较有把握。
- **新增的警告我也没法实测**(`OnValidate` 只在编辑器里跑),五条验收是手推的:碰撞体扫描管第一条,子树里的 `SnapSurface` 扫描管第二条;孤立的标记(身下没有碰撞体)在第一条就报警,不崩。你在 Console 里摆一遍就能确认。
- **一个未验证的猜测**(与本次改动无关,是既有行为):探测射线用 `~0` 打所有层,玩家自己的 `CharacterController` 也是碰撞体。物体掉在自己脚下那一格时射线可能受它影响。如果试玩出现「站着的格子上放不了」,先怀疑这里,别怀疑 `PlacementBlocker`。我没有验证过,只是个怀疑。

---

## 六、离线编译验证(不切焦点、不开编辑器)

WINDOWS.md 说「编译验证由用户统一切焦点触发,不要指望自己验证」—— 至少在编译这一层,不必等。`dotnet build` 走 Unity 自己生成的 csproj 就行:

```bash
dotnet build Assembly-CSharp.csproj -p:BaseIntermediateOutputPath="$TEMP/obj/" -p:BaseOutputPath="$TEMP/bin/"
```

引用集是 csproj 里写好的(Unity 安装目录下的 `UnityEngine/*.dll` 等),不用自己拼。两个坑:

1. **生成的 csproj 是过期的**。它在 Unity 上次切焦点时生成,之后新增的文件不在里面 —— 我直接 build 会报 `CS0234 Overworked.Containers 不存在` / `CS0246 PayloadCatalogue`,**都是假错误**,根因是 P0 的 `Containers/*.cs` 还没进列表。补进临时 csproj 再 build 就干净了(0 错误)。
2. `dotnet build` 会**连别的窗口的文件一起编**,所以它顺带是个全项目冒烟检查。这次跑出来 9 条既有警告(`InstanceFinder.IsServer` 过时、几个 `Awake()` 的 CS0114),不是我的,我没动。

跑完记得删掉临时的 csproj 和 `Temp/bin`、`Temp/obj`,别留在工作区。

---

## 七、一句话

这个 bug 的根因是 `SnapSurface` 用「命中碰撞体的父级」来认桌面,而「父级」这个关系分不清**桌面**和**站在桌面上的东西** —— 两者在层级上完全一样。标记方案是在这个前提下的正解(确定性、不加足迹数据、不动多格桌子和堆叠桌子的既有设计),**但它把正确性的一部分交给了布置的人** —— 所以 `OnValidate` 现在把挂错层当场报出来,而不是等试玩时看到 bug 复现。这一点值得写进 CONSTRAINTS。
---

# 第二轮 · 控制台

**状态**:代码完成,离线编译 **0 error / 3 warning**(3 条全是 CS0114 基线)。
**运行时未验证** —— MPPM 没跑过,那是你的活。
**文件**:`Assets/Scripts/Dev/DevConsole.cs`(+358 行)。场景和 prefab 一个字没碰,**编辑器里不需要你做任何事**。

## 四条命令

| 命令 | 作用 |
|---|---|
| `document <spec> [团队]` | 从 `DocumentCatalogue` 造一份文档。打印 id / 种类 / 编号 / 队伍 / 来源 |
| `docs` | 列出已造出来的所有文档:`#id payload N number N team N Filing/Internet` |
| `queue <打印机> <文档>` | 把那份文档塞进某台机器的任务队列 |
| `printers` | 列打印机:`#0 Printer cell (3, 1) queue 2/5 printing document 4` |

`document` 不带参数时先把规格表列出来(索引 / 名字 / payload / 来源);Tab 补全的候选从
`DocumentCatalogue.Count` 现取,`queue` 的第一个参数补打印机序号、第二个补文档 id。

**一条完整链路**(W1 不用等 W2 的电脑面板):

```
document 1      → created document 0: spec 1 'Excel', payload 2, number 1, team 0, source Filing.
printers        → #0  Printer  cell (3, 1)  queue 0/5
queue 0 0       → queued document 0 (#1) on 'Printer'; queue 1/5.
```

之后机器在纸和墨都够的时候自己开工,产出堆到机器上,拿在手里编号还在 —— 那是 W1 那部分。
(名字印的是你在 asset 里填的 `DisplayName`,上面这个只是样子。)

## 我决定的事(都不在提示词里)

**① 打印机序号按名字排序,不按 `FindObjectsByType` 给的顺序。**
`queue` 用序号指机器,`printers` 把序号印给人看,两个命令隔着人的眼睛。`FindObjectsByType` 的顺序
没有任何承诺 —— 这次是个样、下次加载可能是另一个样;按 `string.CompareOrdinal` 排则**两端一致、每次一致**。
名字重复是布置错误,不是这个命令该处理的。

**② `docs` 和 `printers` 不要求服务端。** 它们只读,不造不删 —— 类注释那句「Server only」管的是
造和删的命令,`pos` 也是这个先例。这样它们正好能在**客户端**上敲,用来验「复制到没到」——
那是有价值的问法,而服务端专用的命令给不了。

**③ `document` 的队伍默认 0,不是 -1。** 本轮所有玩家都在队伍 A(W2 那轮同样硬编码),
默认给 0 才是「和现在一致」;要不上色就显式写 `-1`。

**④ `docs` 的「种类」列是 payload 索引,不是 DisplayName。** 记录里**故意不存**它来自哪个 spec
(多个 spec 可以共用一个 payload),从记录反推名字只能猜。宁可不猜:决定外观的就是 payload 索引,
它和将来 NPC 需求要核对的那个「种类」是同一个东西。

**⑤ 队列满了会说出来,不像电脑那样静默。** 游戏里满容器一律静默(W2 也照做了),但控制台前面
站着一个人刚敲完一行字,他该拿到答复 —— 而且报的是 `2/5` 这种具体数字,不是一句「失败了」。

**⑥ `document` 只造记录,不造物体。** 手上那张纸是工位发出来的 `NetworkGrabbable`;控制台要顶替的是
**电脑**,所以它走到「库里多了一条」为止。要造带编号的物体,得先给 `SpawnGrabbable` 接上 `dataId`
(W1 交接里提过),那是 `GrabbableSpawner.cs`,不是我的文件。

## 我认为可能不对的

- **`document` 造出来的记录没有物体、不进任何容器,`clear entities` 也清不掉它。** `DocumentStore`
  这一局只增不减(P0 定的),所以敲几次就留下几条谁也用不到的记录。测试时无害(编号本来就会跳),
  但**别拿它当「造物体」的替代品**。
- **`Catalogue` 的编辑器兜底是 `AssetDatabase.FindAssets`,现在只有一个 `DocumentCatalogue`。**
  出现第二个时它会拿找到的第一个,不报警。字段是正路,兜底只是让控制台不接线也能用。
- **`printers` 每敲一次都 `FindObjectsByType` + 排序。** 几十台机器、人手敲命令的速度,不值得缓存;
  真看到它卡,根因在这里。
- **`queue` 会往队列里塞任意存在的 id**,包括已经被打掉、已经堆在别处的那些。控制台是开发工具,
  不替使用者记「这份已经用过了」。

## 曾经阻塞在这里(已解决)

`Printer._queue` 是 private,公开面里只有 `Output`。W2 的 `PlayerInteraction.cs:1220` 和我的 `queue`
都要摸它,所以**当时整棵树是红的,而且只有一个错误**。W1 补了 `public ContainerBase Queue => _queue;`,
并把我建议的那条契约(只能塞 `ContainerEntry.ForData`)写进了属性的 XML 注释里。

留一笔,因为这不是「W1 漏了一行」:**一个窗口加了「别的窗口要往里喂东西的容器」时,喂的口子属于它的交付范围。**
W1 的交接里写着「队列里没有别的途径能进文档 …… 建议优先 W3」—— 它知道机器不可测,
但没意识到那等于接口还没写完。`WINDOWS-DATA.md` 的「已冻结接口」那张表可以加一行 `Printer.Queue`,
下一轮就不会有人再问第二遍。
---

# 第三轮 · 收尾

**状态**:三件事都做完,离线编译 **0 error / 3 warning**(CS0114 基线)。
**运行时未验证** —— MPPM 没跑过,那是你的活。
**文件**:`UI/DebugHud.cs`、`Containers/ContainerEntry.cs`、`Stations/Printer.cs`(一行注释)、
`Interaction/PlayerInteraction.cs`、`Dev/DevConsole.cs`。场景和 prefab 一个字没碰,**编辑器里不需要你做任何事**。

## 1. DebugHud:文档报身份,不再报个数

`数据 x2` → `Excel 1 x1, Excel 2 x1`。

- 按**文档 id** 分组(新加一条 `_documentTally`,和原来那条按 payload 的分开):
  同一份文档塞两次仍显示 `x2`,但**同种类的两份不同文档不会被并成一个数** ——
  需求点名的是「某一份」,把它们并起来等于没报。
- 名字走 `DocumentStore.TryGetSpec`。拿不到时报 `种类 {SpecIndex} #{Number}`,连记录都拿不到时报 `文档 {id}`。
  两级兜底,因为有两种「知道得比全部少」:种类被从资源里删了(编号还在),和 store 还没同步(什么都没有)。
  哪一种都不该让这一条消失 —— 少报一件比标签难看糟得多。

**我没做的事,先说清楚:队伍没显示。** 提示词给的格式是 `Excel 1 ×1`,我照做了。
代价是两队各有一份 Excel 1 进了同一台机器时,那一行会是 `Excel 1 x1, Excel 1 x1` ——
看着像渲染错了,其实是两份不同的文档。要加队伍标签(`Excel 1 A x1`)说一声,三行的事。

## 2. `ContainerEntry.OwnerClientId` 删掉了

- 字段、`ForEntity` / `ForData` 上的 `ownerClientId` 参数一起删(确认过没有任何调用点传它)
- `Printer.cs` 里那句注释改写,留下它真正在说的事实:纸和墨是共享池,谁喂进来的不影响任何事;
  「哪一边要印」属于**队列里的那份文档**,不属于原料
- **顺带改了一处注释**(超出提示词的字面范围):`ContainerEntryKind.Data` 的摘要还写着
  「Not implemented yet; the id will point at the document store once that exists」——
  这句话现在是假的,而这一轮做的正是它。留着它,下一个读的人会以为 Data 条目还没接线。
  你要是希望这轮严格只动字段,这处可以回退。

顺带一提:`CONSTRAINTS.md` 第二节里冻结的 `ContainerEntry` **本来就没有这个字段** ——
所以这次删除是让代码对上了那份接口表,不是改接口。

## 3. `team <n>`

- `PlayerInteraction`:`private const int TeamThisRound = 0` → `readonly SyncVar<int> _team = new(0)`,
  `public int Team => _team.Value`(名字和类型都没动,W2 的面板在读它),
  新增 `[Server] public void ServerSetTeam(int team)`
- `ServerBeginFetch(..., Team)` 现在传复制值,不是常量
- 控制台加 `team <n>`:服务端限定,对本机玩家调用,然后打印确认

三处我自己定的:

1. **`ServerSetTeam` 标了 `[Server]`**,而 `NetworkGrabbable` 那几个 `ServerSet*` 故意没标。
   两者不矛盾,原因不同:那几个要在「实例化之后、Spawn 之前」被调用,而 `[Server]` 编译成
   `IsServerInitialized` 检查,那个窗口里它是 false;玩家对象不一样,有人问它队伍的时候它早就 spawn 了。
2. **确认信息里写了下一步该敲什么**:`team 1. The panel filters on this; pass it to 'document' to name one for this side.`
   因为**队伍不会自己传播** —— 面板读它,而 `document` 收的是参数。
3. **`document` 的默认队伍我没改,仍是写死的 0。** 于是 `team 1` 之后敲 `document 0`,造出来的还是队伍 0 的文档。
   这不是 bug,但反直觉,而且 W1 那句注释(「和电脑面板一样先硬编码 0」)现在只对了一半 ——
   面板走的是玩家队伍,**是我这轮让两者不一样的**。要不要把 `document` 的默认也改成玩家队伍,你定;
   这一轮提示词只给了三件事,我没有自己扩。

## 我认为可能不对的

- **`_team` 真的会过网这件事,离线编译证明不了。** 提示词自己也写了:`dotnet build` **不跑编织器**,
  `SyncVar` 的序列化是 Unity 里生成的。这条要等你切焦点编一次才算数。
- **只有主机能改队伍。** 控制台是服务端专属,客户端敲会被拒 —— 所以 MPPM 里客户端那个玩家
  永远停在队伍 0,除非以后加一条 RPC。验收第 9 条(自己变队伍 1、面板翻过来)主机上能做,
  「让**对手**变队伍」这轮做不到。
- **`team` 没有任何范围检查**,`team -5` 也收,面板会按 -5 去比。控制台是开发工具,我没替使用者挡;
  真把队伍当玩法的时候才需要。

## 关于验收第 14 条(`unlocks` 里的 `✓`)—— 我先查了,不用等你看见方框

两件事,一件是查出来的,一件是查不出来的:

1. **`simhei.ttf` 里没有 U+2713(✓)。** 我解了 `Assets/Font/simhei.ttf` 的 cmap 表核实过:
   `A`、`·`、`×`、`√`(U+221A)都有,`✓`(U+2713)、`✔`(U+2714)、`•`(U+2022)都是 0 号字形 —— 缺字。
2. **但控制台根本不用 simhei。** 它是 IMGUI 画的(`OnGUI`),用的是 Unity 内置的那套 GUI 字体;
   simhei 是 TMP 字体,归 DebugHud 和电脑面板。所以**「simhei 没有」并不能推出「控制台会出方框」**,
   我没有条件离线验证内置字体有没有这个字形(它在编辑器里不落地成 ttf)。**这条要你跑一次才知道。**

如果跑出来确实是方框,我建议用 **`√`(U+221A)而不是提示词写的 `[x]`**:`√` 单字符宽,
而 `unlocks` 的标记是每行**第一个字符**,换成三字符的 `[x]` 会把整个列表推歪两格;
`√` 是中文里惯用的对勾,而且我已经确认它在 simhei 里有字形。改一行,说一声就改。
---

# 第四轮 · 箱子与接线

**状态**:代码完成。离线编译 **exit code 0 / 0 error / 3 warning**(3 条全是 CS0114 基线)。
**文件**:`Stations/SupplyBox.cs`(改)。`Interaction/GrabbableSpawner.cs` **一个字没改** —— 理由见下。

## 1. 改了什么

```csharp
int team = HandsOutContainers ? player.Team : -1;

NetworkObject nob = GrabbableSpawner.SpawnGrabbable(
    _payloadIndex, player.HandPosition, Quaternion.identity, conn, variantTeam: team);
```

`HandsOutContainers` = `_catalogue != null && _catalogue.IsContainer(_payloadIndex)` —— 问目录不问物体,
因为物体还不存在,而索引是唯一能说明「马上造出来的是什么」的东西。

材料那条路**零变化**:`variantTeam` 仍是 -1,于是 `SpawnGrabbable` 里 `variantNumber >= 0 || variantTeam >= 0`
不成立,`ServerSetVariant` 根本不会被调用,prefab 保持原样。

## 2. 文件夹走的是哪一条 —— 我查了,只有一条

提示词说箱子里有两条给货路。按代码查下来,**对象只有一条路能出来**:

| 谁 | 造物体的地方 | variantTeam |
|---|---|---|
| **箱子 E 键** | `SupplyBox.OnServerInteract` → `SpawnGrabbable` | **本次改**:容器类用 `player.Team`,材料 -1 |
| 打印机取走产出 | `Printer` → `SpawnGrabbable` | `document.Team`(第三轮就有) |
| 控制台 `spawn entity` / `give` | `DevConsole` → `SpawnGrabbable` | 命令行显式给 |
| 开局撒的那批 | `GrabbableSpawner.SpawnAll` → `SpawnGrabbable` | 无(-1) |

全项目 `SpawnGrabbable(` 一共 5 个调用点,就是上面这些(第 5 个是它自己的定义)。

**`_container`(箱子的库存)不是第二条给货路 —— 它从来不变成物体。** 它是「还剩几件」的计数器:
`OnServerInteract` 造出物体之后 `ServerTryRemoveLast()` 把它减一;库存里的条目是 `ForEntity(_payloadIndex)`,
而**造物体时读的是字段 `_payloadIndex`,不是条目的 `PayloadIndex`** —— 条目连「造什么」都不参与决定,
更不可能带上队伍。

**另一条路上队伍会不会丢?** 那条路不产生物体,所以没有「丢」这回事。而且它**结构上带不了队伍**:
`ContainerEntry` 没有队伍字段,一个箱子两队共用一份库存,写进条目的队伍只会是「上一次是谁来补的货」。
所以队伍只能在造出来那一刻贴 —— 也就是现在这样。

## 3. 场景里那个箱子确实配的是文件夹(核实过,不是推测)

`PayloadCatalogue.asset` 里**只有 index 3 是 `IsContainer: 1`**,而 `SampleScene` 里两个 `Box.prefab` 实例
分别把 `_payloadIndex` 覆盖成 **3**(文件夹)和 **1**(材料)。这条改动落在真实配置上,不是空转。

## 4. 我拿不准的 / 要提醒的

- **箱子没法自检「我本来该吐文件夹」。** 如果那个箱子的 `_catalogue` 没接,或者索引没被标成
  `IsContainer`,文件夹会**没有颜色**地出来(team -1);而 `FolderIntake` 按队伍比对,
  于是这个文件夹什么都装不进 —— 箱子和文件夹看起来都完全正常。**我没有加启动检查**:
  「目录没接」在材料箱上是**合法配置**(`_payloadIndex = -1` 就是「按 prefab 原样」),
  箱子分不出「我本该是文件夹箱」和「我是材料箱」。这条只能靠注释,写在 `HandsOutContainers` 上了。
- **`Team` 默认 0 确实会掩盖一件事。** 决策 ③ 说服务端生成玩家时轮流分 0/1,那 0 和 1 都是真队伍,没问题。
  但如果**那段分配代码没生效**,所有文件夹都会是 A 队 —— 而那是**看起来完全正常**的结果,
  唯一症状是「B 队文件夹永远不出现」。你让我留意的那个掩盖,我确认它会以这个形状出现。
- **控制台 `team -1` 会造出无色的文件夹。** team < 0 时 `PayloadLabel` 不上色,`FolderIntake` 拿 -1 去比,
  于是它同样什么都装不进。这是测试口子的边界(第三轮那条命令没做范围检查),不是这轮改的东西。
- **开局撒的那批(`_spawnCount: 4`)用的是 `_payloadIndex: -1`**,现在不会撒出文件夹。
  哪天把它配成文件夹 payload,那批文件夹**没有玩家可问**,会是永久无色的。要不要加个警告,你说 ——
  我没有自己加,那是「谁来决定它的队伍」的设计问题,不是接线问题。
