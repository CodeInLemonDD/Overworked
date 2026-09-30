# W6 · 调试 HUD —— 交付说明与观点

**窗口**:W6 · 调试 HUD。名下文件:`Assets/Scripts/UI/DebugHud.cs`(564 行,已提交于 `8a89cda`)。
中途你把手写的反射改成了 `PlayerStamina.Local` 硬引用,现在的版本是你那版(第四节复盘这件事)。

**状态:代码已交付、编译已验证。运行时未验证。** 而且更要紧的是 —— **它现在挂上去也几乎看不到东西**,
第二节解释为什么,那节是我这份文档里最该看的部分。

---

## 一、交付了什么

运行时用代码建 Canvas + TMP 文字,右上角一块面板:

```
容器 (3)
  Printer/Input   1/4   纸 x1
  Printer/Output  2/∞   文件 x2
  Shelf/Folder    0/6   (空)

体力  62 / 100  [██████░░░░]  速度 x0.78

注视  Printer
```

三条硬性 UI 约束:

| 约束 | 做法 |
|---|---|
| `sortingOrder >= 1` | `Mathf.Max(1, _sortingOrder)` —— **夹住而不是信任**。配错了的表现是「看不见但仍然吃点击」,这种最难查 |
| 不挂 `GraphicRaycaster` | 整个层级一个都不加,`EventSystem` 也没碰 |
| `raycastTarget = false` | 每个 graphic 逐个显式设 |

两个我自己加的、规格里没写的决定:

- **容器按 label 排序后再渲染**。`FindObjectsByType` 的返回顺序和屏幕上的东西没关系,不排的话每 0.1 秒行都在跳,一个会自己重排的调试面板基本没法读。
- **「注视」从玩家沿正前方打射线,不是从相机打**。相机在玩家后上方俯视,相机射线永远先穿过玩家自己。用玩家前向还顺带对上了拾取/投掷的方向,所以这一行读作「按 E 会选中谁」。

---

## 二、接线总表:代码到了,接线没到

这一节是我认为最该被看见的东西。我按 GUID 扫了场景和三个 prefab(方法用已知接线的脚本做过对照):

| 已接线(7) | 位置 |
|---|---|
| `PlayerCameraFollow`、`GrabbableSpawner` | SampleScene |
| `PlayerInteraction`、`PlayerMovementPrediction`、`HoldPoint` | Player.prefab |
| `NetworkGrabbable` | Object.prefab |
| `SnapSurface` | Table.prefab |

**其余 14 个脚本全部未接线。** 其中两个是误报不用管(`ThrowTrajectoryPreview` 是代码里 `Create()` 的,`WorldGrid`/`ContainerEntry` 本来就没有组件形态)。真缺口按影响排序:

1. **`PlayerStamina` 不在 `Player.prefab` 上。** W5 的验收「两个实例看到的移动位置一致」现在一步都走不了 —— 组件根本没跑。HUD 的体力行也会一直是暗的。
2. **`DebugHud` 不在场景里。** 这是我这边的收尾动作。
3. **`ContainerBase` 一个都没接线**,所以容器那段现在只会显示 `容器 (0) / (无)`。
4. `Printer` / `PaperBox` / `Cleaner` / `PlacementBlocker` 都还没接 —— 而这些的宿主 prefab(机器)**目前根本不存在**,`Assets/Prefabs/` 里只有 Object / Player / Table 三个。

**也就是说:20 个脚本里 7 个接线了,而那 7 个全是 P0 之前的。W1–W6 六个窗口的代码全在仓库里,但没有一个能在编辑器里跑起来。**

每个窗口都写了自己的「需要你手工接的」清单(W1 §7、W2 §7、W4 §2),那些清单本身都是对的、也都够细。缺的不是细节,是**全局视图** —— 单看任何一份都不知道「现在的完成度是 7/21」。约束 D(场景与 prefab 是 YAML、不能合并、只能一个窗口碰)本身没错,但它的代价是**所有集成工作被推迟到同一个人身上,并且堆在最后**。

建议下一轮把「接线」当成一份正式交付物:每个窗口交代码时附一节机器可读的 wiring 清单(哪个 prefab、哪个字段、填什么),最后合成一张总表。现在的做法是靠每个人自觉写散文,合起来就散。

---

## 三、流程意见:通用规则第 4 条应该改

> 「Unity 编辑器一次只开一个,**编译验证由用户统一切焦点触发**,不要指望自己验证」

**这一条是错的。** 而且不是我一个人的发现 —— **六个窗口里五个(W2、W3、W4、W5、W6)各自独立撞上它,并各自在文档里写了一遍。** 五个人重复发现同一件事、又各自发明一套命令,本身就是这条规则在漏水的证据。

已经有三套写法在文档里了(`dotnet build Assembly-CSharp.csproj` / 直接调 `csc.dll`),不用再抄第四遍。我的补充只有两点:

**1. 为什么这条在这个项目里特别要紧。** 没有 asmdef、单程序集 —— 一个编译错误是全窗口停摆,风险全项目最高;但按现在的流程,唯一的编译检查是**一轮人肉往返**,而这个分工里你的注意力是最稀缺的资源。**风险最高的东西配了最贵的检查方式**,这个组合是反的。

**2. 两个方法各有各的坑,该定一个。** csproj 那条路的好处是顺带做全项目冒烟检查(`dotnet build` 会连别人的文件一起编),坏处是 **csproj 是过期快照** —— Unity 没聚焦就不会重生,新文件不在 `Compile Include` 里,于是报出来的是 `CS0234 找不到 Overworked.Containers` 这种**假错误**(W3 §6 踩过)。直接调 `csc.dll` 没有过期问题,但引用集要自己拼。

我的建议:**以 csproj 为主,因为「单程序集」这个前提让全项目检查的价值高于单文件检查**;新文件不在列表里时手动补 `Compile Include`,别去信它报的「找不到类型」。

顺带一句:`CS0649`(字段从未赋值)是本项目基线 —— 每个 `[SerializeField] private` 都报,`ContainerView.cs` 一个文件就 4 条。别去「修」它。

---

## 四、复盘:体力那一行,反射 → 硬引用

你看到的初版是反射(`Assembly.GetType("Overworked.Player.PlayerStamina")` + `GetProperty`),你换成了 `PlayerStamina.Local`。**换得对,而且我认为那正是应有的终态。**

我当时为什么那么写:W5 和 W6 并行,在 W5 落盘前直接写类型名就是编译错误,而单程序集里一个编译错误拖垮所有窗口。**反射是为了买过渡期的解耦。**

想把规则提炼清楚,因为下次还会遇到:

- **并行期间**:按名字找,或者干脆不做这块。不跨窗口硬引用。
- **依赖落盘之后**:立刻换成硬引用。

理由是:**HUD 的职责是发现问题,所以它自己悄悄坏掉是最糟的失败模式。** 反射版的属性名一旦被 W5 改掉,表现是「体力行不声不响地消失」;硬引用版会直接编译不过,当场抓住。**对调试工具而言,响亮地失败比优雅地降级好。**

(反射版我加了 `LogWarning`,所以不是完全静默 —— 但那也只是从「静默」变成「日志里一行」,信息量和编译错误差得远。)

---

## 五、观察者窗口看到的

W6 的位置有点特殊:六个窗口里只有我的职责就是「看」。所以「什么东西看不出来」正好构成一张盲区地图。

### 5.1 下一轮 HUD 最该补的一项:网络质量读数

W5 的验收标准是「两个实例看到的移动位置一致、没有拉扯」。这条**肉眼判不准** —— 尤其在 MPPM 两个并排的小窗口里,而且「拉扯」这种感觉在三十秒内会自我怀疑三次。

而现在整个项目**没有任何一个数字**能回答「拉扯了多少」。

建议加:回滚次数、平均修正距离、RTT、tick 率。把这几个放进 HUD,W5 那条主观验收就变成客观的。这是我认为下一轮 HUD 性价比最高的一件事 —— 比加任何新的世界状态都值。

### 5.2 关于 payload 索引:W2 §3 说对了,我只加一个用法

W2 已经指出「现在有三个互不相干的 Inspector 数字必须一致,而没有任何地方定义它们」,并且 grep 过 `PayloadCatalogue` 资产**不存在**。我不重复那条,只加一个下游视角:

**HUD 可以直接当那个索引契约的验证工具。** 它是 `PayloadCatalogue` 的消费方之一 —— catalogue 没配时,条目显示成 `payload 0`;配对了就显示成 `纸`。所以 W2 说的「要跑起来才发现」,有了 HUD 之后变成「瞟一眼就知道」:如果打印机里堆着的东西显示成 `payload 0 x3` 而不是 `纸 x3`,就是索引或 catalogue 没接上,不用去猜。

前提是 `DebugHud` 的 `Catalogue` 字段也要指过去(和 `Object.prefab`、各 `ContainerView` 同一个资产)。

### 5.3 现在这版 HUD 的信息架构,不要为下一轮留伏笔

「任务」的定义已经变成 NPC 需求,所以下一轮那版 HUD 是「谁要什么、还差什么」,和现在这版「世界状态转储」是两种东西。

我的意见是:**别现在就抽一层「面板系统」出来。** 564 行里真正可复用的只有建 Canvas 那 60 行,为它做抽象是提前下注,等 NPC 那一轮整个重写更划算。

---

## 六、我读到、但没验过的(都在别人名下)

- `PlayerInteraction.KickNearby` 每个 `FixedUpdate` 遍历 `ServerManager/ClientManager.Objects.Spawned`。**只读,所以不违反约束 #1**(那条讲的是边遍历边 despawn)。但它是 O(全部已生成物体) × 玩家数 × 50Hz,随着 W1/W2 开始造物件会持续长。CONSTRAINTS 里现成的 `CollectSpawnedGrabbables` **不能**直接替换 —— 它要求 `IsServerStarted` 且只收 grabbable,而 `KickNearby` 要在 host/client 两条路径上都跑。**不是 bug,只是提一句。**
- `ContainerBase.ContentsChanged` 的去重逻辑(`if (asServer && IsClientStarted) return;`)是对的,host 上正好挡掉重复的那次。我写 HUD 时对着它确认过一遍。

---

## 七、我需要你做的

1. SampleScene 建一个空物体,挂 `DebugHud`
2. `Font` 字段 → `Assets/Font/simhei SDF.asset`(不指会报一条 LogError,中文会画不出来)
3. `Catalogue` 字段 → 同一个 `PayloadCatalogue` 资产(可选,不指就显示 `payload N`)
4. 确认左上角 Host / Client 按钮**仍然点得动** —— 这是我最在意的一条验收,也是三条 UI 约束存在的全部理由

## 八、一句话

HUD 本身是好的,但它现在照不到任何东西 —— **这一轮的瓶颈已经不是代码了,是接线。**
