# 第四轮 · 三个窗口的开工提示词

**用法**:开一个窗口,把下面**对应那一节整段**粘进去。三节可以同时开。

P0 已完成并推上 `main`(`139b1c0`、`ed51c58`)。冻结的接口在 `WINDOWS-NPC.md` 第四节,
和实际代码逐字一致 —— **以那份为准**,本文只补它没说的细节。

---

---

# ▍W1 · 客户(关键路径)

```
你是并行窗口 W1。项目 E:\UnityProject\Overworked,分支 main,Unity 6000.6.0f1 + FishNet 4.7.3。

先按顺序读:
1. WINDOWS-NPC.md —— 重点读「通用规则」「二、已定的决定」「四、已冻结的接口」(第五节里 W1 那一行)
2. CONSTRAINTS.md —— 硬约束,违反任何一条当场坏掉
3. Assets/Scripts/Documents/RequestBoard.cs、Stations/ScoreBoard.cs(注意 ScoreBoard 在 Stations 下)、Documents/DocumentStore.cs
4. Assets/Scripts/Dev/DevConsole.cs 里的 CreateRequest 方法 —— **那是客户生成器的草稿**

## 你名下的文件(只改这三个)

  Assets/Scripts/Npc/Customer.cs          (新)
  Assets/Scripts/Npc/CustomerSpawner.cs   (新)
  Assets/Scripts/Npc/RequestLabel.cs      (新)

别的文件一个字都不要动。需要改别人的,停下来问。

## 要做的东西

客户站在办公室里(模型先用玩家模型),玩家走过去按 E 接单,回来把装满的文件夹**丢向他**,
脱手后碰到他就算交付。分数动了,客户进下一相。

### Customer.cs

`Customer : StationBase`。接口照 WINDOWS-NPC.md 第四节,一个字都别改形状。

**它同时是两个东西**,这一点是这一轮最容易做错的地方:
- 一个 `StationBase` —— 管按 E(接单)
- 一个进料工位 —— 管投喂(交付),挂一个 `IntakeVolume` 盒子

`Meets(team, folder)` 是**纯判定**,不消耗任何东西。`ServerDeliver(team, folder)` 才动世界:
核对 → `_container` 里的文档抽出来 → 销毁文件夹 → `RequestBoard.ServerRemove` →
`ScoreBoard.ServerAward(team, 10)` → 进下一相。

**判定用的是名字,不是 id。** `DocumentRequest` 里没有 document id,只有 `{SpecIndex, Number}`。
交付时对每一行需求,去这个队自己的文档里找一份 `{SpecIndex, Number}` 相同的、且**在这个文件夹里**的。
`DocumentStore.Instance.TryGet(documentId, out record)` 拿 `record.SpecIndex / record.Number / record.Team`。
**一份文档只能用一次**(需求要两份就得真有两份),所以匹配时要划掉已经配过的。

### 三个钟(这是本轮最细的一块)

序列化字段,别写死:等待钟 **20 秒**、每队耐心钟 **60 秒**。

```
客户出现
  ├─ 没有任何队在做 → 「等待钟」在跑。到点 → 还没出局的队都扣 5 分,客户走
  └─ A 队按 E 接了 → A 自己的耐心钟从头跑
                     B 后来的话,B 的耐心钟也从**头**跑,不和 A 同步

某队的钟到点 → 那个队 Failed,扣 5 分,那队再也不能交这个客户
所有队 Failed → 客户走
有队 Failed、又没有队在做 → **等待钟重启**
```

**等待钟不是「客户出现时的钟」,是「客户还没被任何人接手时的钟」。** 不做重启的话:
A 接了、超时了,B 从没接过于是没有钟 —— 客户就一直站着占位子,B 可以无限期慢慢来。

钟由**服务端权威**跑。**用 `TimeManager.OnUpdate` + `Time.unscaledDeltaTime`**,
别用 `OnTick`(一帧跑两三次还可能掉 tick)。抄 `Assets/Scripts/Stations/Printer.cs` 的
`OnStartServer`/`OnStopServer` 订阅写法,再抄 `ScoreBoard` 的「精确值 + 每 0.2 秒发布一次」。

**盒子扫描要有个闸**:没有任何队处于 `Working` 时**直接不扫**。`IntakeVolume.CollectInside`
是全场可抓物体的一遍遍历,4 个客户每帧各扫一遍没必要;而且这个闸顺带把「没接单 → 不能交」
变成了结构上做不到。

### CustomerSpawner.cs

**客户不生成、不销毁。** 场景里摆 4 个客户位(场景 NetworkObject),客户在里面**复用**:
空闲时挪到一边,接到需求时挪回来、把要求显示出来,交完货换下一条。
**进出场做成「挪位置」,不要做成 spawn/despawn** —— 场景 NetworkObject 一旦 `Despawn()`
就退化成 `SetActive(false)`,没有恢复路径。

要摆几个空位、什么时候补位,做成序列化字段(场上保持 2 个)。

**建需求的形状照抄控制台的 `tier` 命令**(DevConsole.cs,`CreateRequest`):
读 `RequestBoard.Instance.RequestsMade` 当层号 → `RequestCatalogue.TryGet` →
**给每个 team 各建一份**(`DocumentStore.ServerCreate(spec, team)`)→ 核对两队拿到的编号一致
(不一致就 LogError)→ 用返回的编号组 `DocumentRequest` 行 → `RequestBoard.ServerCreate`。

**编号一定从 store 读回来,不要自己算。** 两队编号同步是**推论**(所有文档都由生成器成对建,
玩家只印不改名),所以要**核一下** —— 这个错在交付对不上之前完全看不见。

### RequestLabel.cs

世界空间文字,挂在客户头顶。显示:他要什么(名字 + 编号)、**你自己队**的钟。

**钟是「你这个队」的,不是统一的** —— 两个玩家看同一个客户,看到的倒计时不一样。
没接单的时候显示等待钟。已经 Failed 的队显示「出局」。

面板/标签上「合同 1」这种名字的取法:`DocumentStore.Instance.TryGetSpecAt(specIndex, out spec)`
→ `spec.DisplayName`。这个是 P0 刚加的。

## 规则

- 只改你名下那三个文件
- 接口不清楚就**先问**,不要自己发明
- **交付前必须自己跑离线编译**
- **不要切分支、不要 push。** `git add` 只写你名下的具体路径,不要 `git add -A`
- FishNet 的 API 一律对着包源码写,包在
  `Library/PackageCache/com.firstgeargames.fishnet@12ee279bcfde/`

## 离线编译

T=$(mktemp -d)
dotnet build Assembly-CSharp.csproj -nologo -v:q \
  -p:BaseIntermediateOutputPath="$T/obj/" -p:BaseOutputPath="$T/bin/"
rm -rf "$T"

**新文件要先加进 `Assembly-CSharp.csproj` 的 `<Compile Include>`**,否则报「找不到类型」。
那个文件是 Unity 的快照、没进 git,改坏了不影响别人。
**本轮基线:0 error / 3 warning**(全是 CS0114)。离线编译**不跑编织器** ——
`SyncType`、`[ServerRpc]` 那些错只能在 Unity 里看得到,所以要说明「哪几处需要你在编辑器里确认」。

## 交付时给我

1. 三个文件
2. 离线编译的 exit code 和 warning 数
3. **你在编辑器里没法验、需要用户在 Unity 里确认的东西**,列清楚
4. 哪里你拿不准,明说
```

---

---

# ▍W2 · 文件夹归队

```
你是并行窗口 W2。项目 E:\UnityProject\Overworked,分支 main,Unity 6000.6.0f1 + FishNet 4.7.3。

先按顺序读:
1. WINDOWS-NPC.md —— 重点读「二、已定的决定」的 ①②、第四节、第五节的 W2 那一行
2. CONSTRAINTS.md
3. Assets/Scripts/Interaction/FolderIntake.cs、NetworkGrabbable.cs、IntakeVolume.cs
4. Assets/Scripts/Documents/DocumentStore.cs、Assets/Scripts/Containers/PayloadCatalogue.cs

## 你名下的文件(只改这两个)

  Assets/Scripts/Interaction/FolderIntake.cs
  Assets/Scripts/Interaction/NetworkGrabbable.cs

别的文件一个字都不要动。**尤其不要动 `IntakeVolume.cs`** —— 它是 P0 刚冻结的,
盒子判定已经在里面了,你只加「队伍对不对」这一条判据。

## 要解决的真 bug

客户要「合同 1 和 Excel 1」。一个文件夹里装了 A 队的 Excel 1 和 B 队的合同 1 —— **这单算谁的?**

答案:**文件夹自己属于某一队,只接受同队的文档**。

## 要做的东西

### ① 文件夹带上队伍

- 文件夹从箱子里出来时,由**按 E 的那个人**的队决定它属于哪一队(W3 会调
  `GrabbableSpawner.SpawnGrabbable(..., variantTeam: ...)`,那个参数**已经存在**)
- **不用新做视觉**:`NetworkGrabbable` 上已经有 `_variantTeam`,而 `PayloadLabel`
  就是拿它上色的 —— 和文档编号走的是同一套

所以这一条**大概率不需要改代码**,你要做的是**确认它成立**:读
`NetworkGrabbable.ServerSetVariant` / `_variantTeam` / `PayloadLabel`,确认
「spawn 时传 variantTeam,文件夹上色,客户端读得到 `VariantTeam`」这条路是通的。
**如果哪里断了,那才是你要改的。**

### ② 只收同队的文档

`FolderIntake.TryFile` 现在只看「有没有 `DataId`」。再加一条:**那份文档的队伍 == 这个文件夹的队伍**。

```
文件夹的队伍   = _grabbable.VariantTeam
文档的队伍     = DocumentStore.Instance.TryGet(dataId, out record) ? record.Team : ?
```

**`TryGet` 返回 false 的时候要拒绝,不要放行。** 一个还没同步到的文档 id
(新加入的客户端可能遇到)如果按「不知道队伍就当同队」处理,就会出现一个能吞对面文档的文件夹,
而且只在个别帧上出现 —— 是最难查的那种。**拿不准就不收**,文档留在世界上,玩家还能捡回来。

### ③ 队伍未知的文件夹

`VariantTeam == -1` 是「不上色」。这种文件夹怎么办,**你决定,但要在注释里写清楚理由**。
两个选项都说得通:(a) 谁都不收,(b) 谁都能收(退化成现在的行为)。
选哪个要说明它对「测试时不设队伍也能玩」有什么影响。

## 规则

- 只改你名下那两个文件
- 接口不清楚就**先问**
- **交付前必须自己跑离线编译**
- **不要切分支、不要 push。** `git add` 只写你名下的具体路径,不要 `git add -A`
- FishNet 的 API 一律对着包源码写,包在
  `Library/PackageCache/com.firstgeargames.fishnet@12ee279bcfde/`

## 离线编译

T=$(mktemp -d)
dotnet build Assembly-CSharp.csproj -nologo -v:q \
  -p:BaseIntermediateOutputPath="$T/obj/" -p:BaseOutputPath="$T/bin/"
rm -rf "$T"

**本轮基线:0 error / 3 warning**(全是 CS0114)。离线编译**不跑编织器**。

## 交付时给我

1. 改了什么,为什么
2. `VariantTeam == -1` 你选了哪个,为什么
3. 「不需要改」的地方也要说 —— 我去核一遍,而不是猜你没看
4. 离线编译的 exit code 和 warning 数
```

---

---

# ▍W3 · 箱子与接线

```
你是并行窗口 W3。项目 E:\UnityProject\Overworked,分支 main,Unity 6000.6.0f1 + FishNet 4.7.3。

先按顺序读:
1. WINDOWS-NPC.md —— 重点读「二、已定的决定」的 ①②、第五节的 W3 那一行
2. CONSTRAINTS.md
3. Assets/Scripts/Stations/SupplyBox.cs、Assets/Scripts/Interaction/GrabbableSpawner.cs
4. Assets/Scripts/Containers/PayloadCatalogue.cs、PayloadLabel.cs
5. Assets/Scripts/Interaction/PlayerInteraction.cs —— 只读,看 `Team` 这个 SyncVar

## 你名下的文件(只改这两个)

  Assets/Scripts/Stations/SupplyBox.cs
  Assets/Scripts/Interaction/GrabbableSpawner.cs

别的文件一个字都不要动。

## 要做的东西

**箱子吐出来的文件夹,要带着「按 E 的那个人」的队伍。**

现在 `SupplyBox.OnServerInteract` 是:

    GrabbableSpawner.SpawnGrabbable(_payloadIndex, player.HandPosition, Quaternion.identity, conn);

`SpawnGrabbable` **已经有** `variantTeam` 参数了(还有 `variantNumber`、`dataId`),
顺序是 `(payloadIndex, position, rotation, owner, variantNumber, variantTeam, dataId)`。

所以这件事的形状是:**当 `_payloadIndex` 指的是一个容器(payload 目录里 `IsContainer` 为真)时,
把 `player.Team` 传进 `variantTeam`。**

## 要注意的

- **`player.Team` 是上一轮才加的 `SyncVar`**(`PlayerInteraction` 上),控制台 `team <n>` 能改它。
  读之前确认它在服务端是有效的
- **`PlayerInteraction.Team` 默认是 0**,不是 -1。也就是说「没设过队」和「A 队」在代码里长得一样。
  这是已知的,控制台 `team <n>` 是覆盖手段。**如果你发现这会掩盖什么错,说出来,别自己改设计**
- **`SupplyBox` 还有另一条给货的路**(它自己 `_container` 里的库存)。看清楚**文件夹走的是哪一条**,
  以及那条路上队伍会不会丢。**两条都要对**
- 箱子吐出来的别的 payload(纸、墨盒)不该带队伍 —— 它们是材料,不是谁的

## 不要做的

- **不要改 `Stations/Printer.cs`** —— P0 已经把它接到 `IntakeVolume` 上了
- **不要改 `Stations/Computer.cs`** —— 它不在这一轮
- **不要动 prefab 和场景**(YAML 合不了,只有用户能碰)

## 规则

- 只改你名下那两个文件
- 接口不清楚就**先问**
- **交付前必须自己跑离线编译**
- **不要切分支、不要 push。** `git add` 只写你名下的具体路径,不要 `git add -A`
- FishNet 的 API 一律对着包源码写,包在
  `Library/PackageCache/com.firstgeargames.fishnet@12ee279bcfde/`

## 离线编译

T=$(mktemp -d)
dotnet build Assembly-CSharp.csproj -nologo -v:q \
  -p:BaseIntermediateOutputPath="$T/obj/" -p:BaseOutputPath="$T/bin/"
rm -rf "$T"

**本轮基线:0 error / 3 warning**(全是 CS0114)。离线编译**不跑编织器**。

## 交付时给我

1. 改了什么,以及**文件夹走的是两条给货路里的哪一条**
2. 另一条路上队伍会不会丢,你的判断和依据
3. 离线编译的 exit code 和 warning 数
4. 哪里你拿不准,明说
```
